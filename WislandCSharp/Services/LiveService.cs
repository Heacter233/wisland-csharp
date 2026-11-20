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
    private WhisperAsr? _asr;
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

            _asr = new WhisperAsr(modelsDir);
            var model = WhisperAsr.ResolveModel(Cfg("live.asr.model", "auto"));
            if (!_asr.Exists(model))
            {
                // 首次使用：应用内引导下载（带进度，可取消；.part 保留可续传）
                Status?.Invoke(this, $"首次使用：正在下载语音模型 {model}（{WhisperAsr.FileName(model)}）…");
                DownloadingChanged?.Invoke(this, true);
                bool ok;
                try
                {
                    ok = await ServiceHub.Models.EnsureAsync(model, p =>
                        Status?.Invoke(this, $"下载模型 {p.Ratio:P0}  {p.Text}"));
                }
                finally
                {
                    DownloadingChanged?.Invoke(this, false);
                }

                if (!ok || !_asr.Exists(model))
                {
                    // 用户取消或下载失败：干净地停在这里，不启动音频捕获
                    Status?.Invoke(this, "已取消模型下载");
                    lock (_runningLock) _running = false;
                    RunningChanged?.Invoke(this, false);
                    await SafeStopAsync();
                    return;
                }
            }
            _asr.UseModel(model);

            // 2) 启动音频捕获
            _capture = new AudioCapture();
            _capture.Level += (_, v) => Level?.Invoke(this, v);
            _capture.Samples += OnSamples;

            var source = Cfg("live.audio.source", _audioSource);
            var deviceIdx = -1;
            var devStr = Cfg("live.audio.device", _audioDevice);
            if (int.TryParse(devStr, out var di)) deviceIdx = di;
            _capture.Start(source, deviceIdx);

            // 3) VAD 循环
            _cts = new CancellationTokenSource();
            ResetVad();
            _vadLoop = Task.Run(() => VadLoopAsync(_cts.Token));

            Status?.Invoke(this, source == "mic"
                ? "实时字幕已启动（麦克风）"
                : "实时字幕已启动（系统音频）");
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

        if (_vadLoop != null && await Task.WhenAny(_vadLoop, Task.Delay(1500)) != _vadLoop)
        {
            // VAD 未及时退出：不阻塞关闭
        }
        _vadLoop = null;

        _cts?.Dispose();
        _cts = null;

        try { _asr?.Dispose(); } catch { }
        _asr = null;

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
        lock (_buffer) _buffer.AddRange(samples);
    }

    private async Task VadLoopAsync(CancellationToken ct)
    {
        var threshold = Math.Max(0.001, Cfg("live.vad.threshold", 0.012));
        var silenceMs = Math.Max(150, Cfg("live.vad.silence_ms", 450));
        var minSpeechMs = Math.Max(120, Cfg("live.vad.min_speech_ms", 300));
        var maxSegmentMs = Math.Max(2000, Cfg("live.vad.max_segment_ms", 12000));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cutStart = -1;
                var cutEnd = -1;

                lock (_buffer)
                {
                    while (_framePos + FrameSize <= _buffer.Count)
                    {
                        var rms = Rms(_buffer, _framePos, FrameSize);
                        var voice = rms >= threshold;
                        var now = DateTime.UtcNow;

                        if (voice)
                        {
                            if (_speechFrom < 0)
                            {
                                _speechFrom = _framePos;
                                _speechStartAt = now;
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

                    // 空闲时裁掉已处理数据，避免缓冲无限增长
                    if (_speechFrom < 0 && _framePos > SampleRate * 2)
                    {
                        _buffer.RemoveRange(0, _framePos);
                        _framePos = 0;
                    }
                }

                if (cutStart >= 0 && cutEnd > cutStart)
                {
                    float[] segment;
                    lock (_buffer)
                    {
                        var from = Math.Max(0, cutStart);
                        var to = Math.Min(_buffer.Count, cutEnd);
                        segment = _buffer.GetRange(from, to - from).ToArray();
                    }

                    var ms = segment.Length * 1000.0 / SampleRate;
                    if (ms >= minSpeechMs) await HandleSegmentAsync(segment, ct);
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
        if (_asr == null) return;
        var sid = (++_segmentId).ToString();

        // 同一时刻只跑一次识别，避免 CPU 打满
        if (!await _asrGate.WaitAsync(0, ct)) return;
        try
        {
            var language = Cfg("live.asr.language", "auto");
            var text = await _asr.TranscribeAsync(samples, language, ct);
            text = Clean(text);
            if (text.Length == 0) { Partial?.Invoke(this, ""); return; }

            Segment?.Invoke(this, (sid, text));
            Partial?.Invoke(this, "");

            var zh = await TranslateAsync(text, ct);
            if (zh.Length > 0)
            {
                LiveZh?.Invoke(this, (sid, zh));
                FinalZh?.Invoke(this, (sid, zh));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[LiveService] ASR: " + ex.Message);
            Error?.Invoke(this, "识别失败：" + ex.Message);
        }
        finally
        {
            _asrGate.Release();
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
