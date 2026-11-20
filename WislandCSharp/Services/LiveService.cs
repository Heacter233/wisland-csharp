using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wisland.Services;

/// <summary>
/// 实时字幕服务：进程内完成 音频捕获 → VAD 切句 → 离线 ASR → 翻译。
///
/// 与旧实现的区别：不再启动 Python sidecar（仓库里从来没有对应的 asr_pipe.py），
/// 改为纯 C#：
///   - 音频：<see cref="AudioCapture"/>（WASAPI 回环 / 麦克风，统一 16k 单声道）
///   - 识别：<see cref="WhisperAsr"/>（Whisper.net 离线模型，首次使用自动下载）
///   - 翻译：DeepSeek（有 key 时）→ Google 非官方接口兜底（实测可用、免 key）
///
/// 对外事件与旧版保持一致，翻译页与收起态字幕无需改动。
/// </summary>
public class LiveService : ILiveService, IDisposable
{
    // 采样参数
    private const int SampleRate = AudioCapture.TargetRate;
    private const int FrameMs = 30;
    private const int FrameSize = SampleRate * FrameMs / 1000;   // 480

    private AudioCapture? _capture;
    private AudioEnhancer? _enhancer;
    private readonly object _enhancerLock = new();
    private AsrService? _asr;
    private CancellationTokenSource? _cts;
    private Task? _vadLoop;
    private readonly SemaphoreSlim _asrGate = new(1, 1);

    // VAD 状态
    private readonly List<float> _buffer = new();
    private int _framePos;
    private int _speechFrom = -1;
    private DateTime _lastVoiceAt = DateTime.MinValue;
    private DateTime _speechStartAt = DateTime.MinValue;
    private int _segmentId;

    // 配置
    private string _modelsDir = "models";
    private string _audioSource = "loopback";
    private string _audioDevice = "";
    private int _blockMs = 80;

    private bool _running;
    private readonly object _runningLock = new();

    // 翻译统一交给 ServiceHub.Translate（多引擎 + 回退链），本服务不再自己发翻译请求

    public event EventHandler<string>? Status;
    public event EventHandler<string>? Error;
    public event EventHandler<string>? Partial;
    public event EventHandler<(string sid, string jp)>? Segment;
    public event EventHandler<(string sid, string zh)>? LiveZh;
    public event EventHandler<(string sid, string zh)>? FinalZh;
    public event EventHandler<float>? Level;
    public event EventHandler<bool>? RunningChanged;
    public event EventHandler<bool>? DownloadingChanged;

    public bool Running
    {
        get { lock (_runningLock) return _running; }
    }

    public void Initialize(string modelsDir = "models", string audioSource = "loopback",
        string audioDevice = "", int blockMs = 80)
    {
        _modelsDir = modelsDir;
        _audioSource = audioSource;
        _audioDevice = audioDevice;
        _blockMs = blockMs;
    }

    /// <summary>配置读取（ServiceHub.Config 在启动后可用；缺失时用默认值）。</summary>
    private static T Cfg<T>(string key, T fallback)
    {
        try
        {
            var cfg = ServiceHub.Config;
            return cfg == null ? fallback : cfg.Get(key, fallback);
        }
        catch { return fallback; }
    }

    /// <summary>ModelManager 不可用时的兜底：live.models_dir（相对路径 → data/）。</summary>
    private string FallbackModelsDir()
    {
        var dir = Cfg("live.models_dir", _modelsDir);
        if (string.IsNullOrWhiteSpace(dir)) dir = "models";
        if (Path.IsPathRooted(dir)) return dir;

        var dataDir = Cfg("app.data_dir", "");
        var baseDir = string.IsNullOrWhiteSpace(dataDir)
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : dataDir;
        return Path.Combine(baseDir, dir);
    }

    /// <summary>
    ///     // ---------------- 生命周期 ----------------

    public async Task StartAsync()
    {
        lock (_runningLock)
        {
            if (_running) return;
            _running = true;
        }

        try
        {
            RunningChanged?.Invoke(this, true);

            // 1) 模型目录：统一由 ModelManager 解析（live.models_dir，相对路径放 data/ 下），
            //    保证"这里判断存在与否"和"模型页下载到哪"永远是同一个目录。
            var modelsDir = ServiceHub.Models?.Dir ?? FallbackModelsDir();
            Directory.CreateDirectory(modelsDir);

            // 引擎选择：auto → SenseVoice 模型在场就优先 SenseVoice（更快），否则用 Whisper
            var store = new AsrModelStore(modelsDir);
            var cfg = ServiceHub.Config;
            var chosen = AsrService.ResolveModelKey(cfg, store);
            var info = AsrModels.Find(chosen);

            if (!store.Exists(chosen))
            {
                // 首次使用：应用内引导下载（带进度，可取消；.part 保留可续传）
                Status?.Invoke(this,
                    $"首次使用：正在下载语音模型 {(info?.DisplayName ?? chosen)}（{info?.SizeMb ?? 0} MB）…");
                DownloadingChanged?.Invoke(this, true);
                bool ok;
                try
                {
                    ok = await ServiceHub.Models.EnsureAsync(chosen, p =>
                        Status?.Invoke(this, $"下载模型 {p.Ratio:P0}  {p.Text}"));
                }
                finally
                {
                    DownloadingChanged?.Invoke(this, false);
                }

                if (!ok || !store.Exists(chosen))
                {
                    // 用户取消或下载失败：干净地停在这里，不启动音频捕获
                    Status?.Invoke(this, "已取消模型下载");
                    lock (_runningLock) _running = false;
                    RunningChanged?.Invoke(this, false);
                    await SafeStopAsync();
                    return;
                }
            }

            _asr = new AsrService(cfg, store);
            if (!_asr.Load(chosen))
            {
                var msg = "语音模型加载失败（可在翻译页 →「模型」检查或重新下载）";
                Error?.Invoke(this, msg);
                Status?.Invoke(this, msg);
                lock (_runningLock) _running = false;
                RunningChanged?.Invoke(this, false);
                await SafeStopAsync();
                return;
            }

            // 2) 人声处理（原始音频 → 增强 → VAD → ASR）
            //    dpdfnet/gtcrn 降噪保留所有人声；voxlock 只保留锁定的说话人。
            var enhanceMode = (Cfg("live.enhance.mode", "dpdfnet") ?? "off").Trim().ToLowerInvariant();
            if (enhanceMode != AudioEnhancer.ModeOff)
            {
                var enhKey = AudioEnhancer.ModelKeyForMode(enhanceMode);
                if (enhKey.Length > 0 && !store.Exists(enhKey))
                {
                    Status?.Invoke(this, $"首次使用：正在下载人声处理模型（{AsrModels.Find(enhKey)?.DisplayName ?? enhKey}）…");
                    DownloadingChanged?.Invoke(this, true);
                    bool enhOk;
                    try
                    {
                        enhOk = await ServiceHub.Models.EnsureAsync(enhKey, p =>
                            Status?.Invoke(this, $"下载人声模型 {p.Ratio:P0}  {p.Text}"));
                    }
                    finally { DownloadingChanged?.Invoke(this, false); }

                    if (!enhOk)
                    {
                        Status?.Invoke(this, "人声处理模型未下载，已回退为「不处理」");
                        enhanceMode = AudioEnhancer.ModeOff;
                    }
                }

                if (enhanceMode != AudioEnhancer.ModeOff)
                {
                    _enhancer = new AudioEnhancer();
                    var okEnh = _enhancer.Load(
                        enhanceMode, modelsDir,
                        Cfg("live.enhance.threads", 2),
                        Cfg("live.enhance.voice_sample", ""),
                        Cfg("live.enhance.normalize", true));
                    if (!okEnh)
                    {
                        Status?.Invoke(this, $"人声处理加载失败（{_enhancer.Error}），已回退为「不处理」");
                        _enhancer = null;
                    }
                }
            }

            // 3) 启动音频捕获
            _capture = new AudioCapture();
            _capture.Level += (_, v) => Level?.Invoke(this, v);
            _capture.Samples += OnSamples;
            _capture.AgcEnabled = Cfg("live.capture.agc", true);
            _capture.ManualGain = (float)Math.Clamp(Cfg("live.capture.gain", 1.0), 0.1, 16.0);

            var source = Cfg("live.audio.source", _audioSource);
            var deviceIdx = -1;
            var devStr = Cfg("live.audio.device", _audioDevice);
            if (int.TryParse(devStr, out var di)) deviceIdx = di;
            _capture.Start(source, deviceIdx);

            // 3) VAD 循环
            _cts = new CancellationTokenSource();
            ResetVad();
            _vadLoop = Task.Run(() => VadLoopAsync(_cts.Token));

            var engineLabel = _asr?.EngineLabel ?? "离线识别";
            if (_enhancer?.Active == true) engineLabel += " + " + AudioEnhancer.DisplayName(_enhancer.Mode);
            Status?.Invoke(this, source == "mic"
                ? $"实时字幕已启动（麦克风 · {engineLabel}）"
                : $"实时字幕已启动（系统音频 · {engineLabel}）");
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex.Message);
            lock (_runningLock) _running = false;
            RunningChanged?.Invoke(this, false);
            await SafeStopAsync();
        }
    }

    public async Task StopAsync(bool wait = true)
    {
        lock (_runningLock)
        {
            if (!_running) return;
            _running = false;
        }

        try
        {
            await SafeStopAsync();
            Status?.Invoke(this, "实时字幕已停止");
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex.Message);
        }
    }

    private async Task SafeStopAsync()
    {
        try { _cts?.Cancel(); } catch { }

        if (_capture != null)
        {
            _capture.Samples -= OnSamples;
            try { _capture.Stop(); } catch { }
            _capture.Dispose();
            _capture = null;
        }

        // 等 VAD 线程退出：它可能正在 HandleSegmentAsync 里跑 ASR，
        // 这时释放 WhisperFactory / sherpa recognizer 属于原生 use-after-free，会直接崩进程。
        if (_vadLoop != null)
        {
            if (await Task.WhenAny(_vadLoop, Task.Delay(8000)) != _vadLoop)
                Debug.WriteLine("[LiveService] 警告：VAD 线程 8s 内未退出");
            _vadLoop = null;
        }

        _cts?.Dispose();
        _cts = null;

        // 增强器：与音频回调互斥，避免音频线程还在 Process 时释放原生模型
        lock (_enhancerLock)
        {
            try { _enhancer?.Dispose(); } catch { }
            _enhancer = null;
        }

        // ASR：拿住识别闸门 = 保证没有进行中的转写；拿不到就跳过释放（宁可泄漏一次也别崩）
        if (_asr != null)
        {
            var gotGate = await _asrGate.WaitAsync(TimeSpan.FromSeconds(8));
            try
            {
                if (gotGate)
                {
                    try { _asr.Dispose(); } catch { }
                    _asr = null;
                }
                else
                {
                    Debug.WriteLine("[LiveService] 警告：识别仍在进行，跳过释放（等待 GC 回收）");
                    _asr = null;
                }
            }
            finally
            {
                if (gotGate) _asrGate.Release();
            }
        }

        RunningChanged?.Invoke(this, false);
    }

    public Task SendCommandAsync(string command, object? parameters = null)
    {
        // 目前只有开关类命令，配置改动走 config + 重启聆听
        return Task.CompletedTask;
    }

    // ---------------- VAD + 切句 ----------------

    private void ResetVad()
    {
        lock (_buffer)
        {
            _buffer.Clear();
            _framePos = 0;
            _speechFrom = -1;
            _lastVoiceAt = DateTime.MinValue;
        }
    }

    private void OnSamples(object? sender, float[] samples)
    {
        if (samples.Length == 0) return;

        // 人声处理（流式，内部按帧缓存；可能返回空数组，表示还在攒帧）
        // 与 StopAsync 的 Dispose 共用一把锁：避免音频线程用到已释放的原生模型（会直接 fast-fail 崩进程）
        lock (_enhancerLock)
        {
            if (_enhancer != null && _enhancer.Active)
            {
                try { samples = _enhancer.Process(samples); }
                catch (Exception ex) { Debug.WriteLine("[LiveService] enhancer: " + ex.Message); }
            }
        }
        if (samples.Length == 0) return;

        lock (_buffer) _buffer.AddRange(samples);
    }

    private async Task VadLoopAsync(CancellationToken ct)
    {
        var threshold = Math.Max(0.001, Cfg("live.vad.threshold", 0.006));
        var adaptive = Cfg("live.vad.adaptive", true);
        var noiseFloor = 0.002;
        var silenceMs = Math.Max(150, Cfg("live.vad.silence_ms", 450));
        var minSpeechMs = Math.Max(120, Cfg("live.vad.min_speech_ms", 300));
        var maxSegmentMs = Math.Max(2000, Cfg("live.vad.max_segment_ms", 6000));
        // VAD 总是比人声晚一点触发，加一段"预录"把句首补回来，避免识别结果缺头
        var prerollMs = Math.Max(0, Math.Min(800, Cfg("live.vad.preroll_ms", 300)));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cutStart = -1;
                var cutEnd = -1;
                float[]? segment = null;
                double cutMs = 0;

                lock (_buffer)
                {
                    while (_framePos + FrameSize <= _buffer.Count)
                    {
                        var rms = Rms(_buffer, _framePos, FrameSize);
                        // 自适应阈值：跟着环境噪声底走，安静环境不会因为阈值太高而漏句
                        var thr = adaptive ? Math.Max(threshold, noiseFloor * 2.5) : threshold;
                        var voice = rms >= thr;
                        if (!voice)
                        {
                            noiseFloor = noiseFloor * 0.995 + rms * 0.005;
                            noiseFloor = Math.Min(noiseFloor, threshold * 0.8);
                        }
                        var now = DateTime.UtcNow;

                        if (voice)
                        {
                            if (_speechFrom < 0)
                            {
                                var prerollFrames = (int)(SampleRate * prerollMs / 1000.0);
                                _speechFrom = Math.Max(0, _framePos - prerollFrames);
                                _speechStartAt = now;
                                App.DebugNote($"[VAD] 语音开始 rms={rms:0.0000} thr={thr:0.0000} 噪声底={noiseFloor:0.0000}");
                                Partial?.Invoke(this, "…");
                            }
                            _lastVoiceAt = now;
                        }
                        else if (_speechFrom >= 0 && (now - _lastVoiceAt).TotalMilliseconds > silenceMs)
                        {
                            cutStart = _speechFrom;
                            cutEnd = _framePos;
                            _speechFrom = -1;
                        }

                        _framePos += FrameSize;

                        if (cutStart >= 0) break;

                        // 超长片段：强制切
                        if (_speechFrom >= 0 && (now - _speechStartAt).TotalMilliseconds > maxSegmentMs)
                        {
                            cutStart = _speechFrom;
                            cutEnd = _framePos;
                            _speechFrom = -1;
                            break;
                        }
                    }

                    // 关键：必须先用「旧索引」把片段取出来，再裁剪缓冲。
                    // 之前先 RemoveRange 再按旧索引切片，导致每句只剩不到一帧（40ms）。
                    if (cutStart >= 0 && cutEnd > cutStart)
                    {
                        var from = Math.Max(0, Math.Min(cutStart, _buffer.Count));
                        var to = Math.Max(from, Math.Min(cutEnd, _buffer.Count));
                        if (to > from)
                        {
                            segment = _buffer.GetRange(from, to - from).ToArray();
                            cutMs = segment.Length * 1000.0 / SampleRate;
                        }

                        var drop = Math.Min(cutEnd, _buffer.Count);
                        if (drop > 0)
                        {
                            _buffer.RemoveRange(0, drop);
                            _framePos = Math.Max(0, _framePos - drop);
                        }
                    }
                    // 空闲时裁掉已处理数据，避免缓冲无限增长
                    else if (_speechFrom < 0 && _framePos > SampleRate * 2)
                    {
                        _buffer.RemoveRange(0, _framePos);
                        _framePos = 0;
                    }
                }

                if (segment != null)
                {
                    App.DebugNote($"[VAD] 切句 {cutMs:0}ms → 识别");
                    if (cutMs >= minSpeechMs) await HandleSegmentAsync(segment, ct);
                    else Partial?.Invoke(this, "");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[LiveService] VAD: " + ex.Message);
            }

            try { await Task.Delay(50, ct); } catch { break; }
        }
    }

    private static float Rms(List<float> buf, int offset, int count)
    {
        var sum = 0.0;
        for (var i = 0; i < count; i++)
        {
            var s = buf[offset + i];
            sum += s * s;
        }
        return (float)Math.Sqrt(sum / count);
    }

    private async Task HandleSegmentAsync(float[] samples, CancellationToken ct)
    {
        var asr = _asr;
        if (asr == null) return;
        var sid = (++_segmentId).ToString();

        // 同一时刻只跑一次识别，避免 CPU 打满
        if (!await _asrGate.WaitAsync(0, ct)) return;

        string text;
        try
        {
            var language = Cfg("live.asr.language", "auto");
            text = Clean(await asr.TranscribeAsync(samples, language, ct));
            App.DebugNote($"[ASR] {samples.Length * 1000 / SampleRate}ms → \"{text}\"");
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[LiveService] ASR: " + ex.Message);
            Error?.Invoke(this, "识别失败：" + ex.Message);
            return;
        }
        finally
        {
            // 识别一结束就释放闸门：翻译走网络（最长 60s），不能让它挡住后续切句/识别
            _asrGate.Release();
        }

        if (text.Length == 0) { Partial?.Invoke(this, ""); return; }

        Segment?.Invoke(this, (sid, text));
        Partial?.Invoke(this, "");

        // 翻译异步执行，不阻塞 VAD 循环（否则网络慢时音频缓冲会一直涨）
        _ = TranslateSegmentAsync(sid, text, ct);
    }

    /// <summary>字幕翻译（独立任务）：失败只记日志，不影响识别链路。</summary>
    private async Task TranslateSegmentAsync(string sid, string text, CancellationToken ct)
    {
        try
        {
            var zh = await TranslateAsync(text, ct);
            if (zh.Length == 0) return;

            LiveZh?.Invoke(this, (sid, zh));
            FinalZh?.Invoke(this, (sid, zh));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine("[LiveService] 翻译: " + ex.Message);
        }
    }

    /// <summary>过滤 whisper 的非语音标记（[音乐]、♪、(silence) 等）。</summary>
    private static string Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = text.Trim();
        if (t.StartsWith("[") && t.EndsWith("]")) return "";
        if (t.StartsWith("(") && t.EndsWith(")")) return "";
        if (t.Trim('♪', ' ', '。', '.', '、').Length == 0) return "";

        // Whisper 在纯音乐/静音上的常见幻觉：网址、同一句反复、单字重复
        if (t.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("https://", StringComparison.OrdinalIgnoreCase))
            return "";

        var words = t.Split(new[] { ' ', '\u3000' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 3 && words.Distinct().Count() == 1) return "";
        if (t.Length >= 30 && t.Distinct().Count() <= 3) return "";

        return t;
    }

    // ---------------- 字幕翻译：统一走 ServiceHub.Translate 的引擎链 ----------------

    private async Task<string> TranslateAsync(string text, CancellationToken ct)
    {
        if (!Cfg("live.translate.enabled", true)) return "";
        if (text.Length == 0) return "";

        var target = Cfg("live.translate.target", "zh-CN");
        try
        {
            // 引擎与回退链在 TranslationEngines 里统一配置（默认 Index-Translate 免 Key）
            return await ServiceHub.Translate.TranslateOnceAsync(text, "auto", target, ct);
        }
        catch (OperationCanceledException) { return ""; }
        catch (Exception ex)
        {
            Debug.WriteLine("[LiveService] 翻译失败: " + ex.Message);
            return "";
        }
    }

    public void Dispose()
    {
        if (Running)
        {
            try { StopAsync(wait: true).Wait(3000); } catch { }
        }

        try { _asrGate.Dispose(); } catch { }
    }
}

/// <summary>实时字幕服务接口。</summary>
public interface ILiveService
{
    bool Running { get; }

    event EventHandler<string>? Status;
    event EventHandler<string>? Error;
    event EventHandler<string>? Partial;
    event EventHandler<(string sid, string jp)>? Segment;
    event EventHandler<(string sid, string zh)>? LiveZh;
    event EventHandler<(string sid, string zh)>? FinalZh;
    event EventHandler<float>? Level;
    event EventHandler<bool>? RunningChanged;
    event EventHandler<bool>? DownloadingChanged;

    void Initialize(string modelsDir = "models", string audioSource = "loopback",
        string audioDevice = "", int blockMs = 80);

    Task StartAsync();
    Task StopAsync(bool wait = true);
    Task SendCommandAsync(string command, object? parameters = null);
}
