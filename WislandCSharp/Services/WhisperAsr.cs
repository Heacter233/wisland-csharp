using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Wisland.Services;

/// <summary>下载进度（字节数来自实际写入量，便于 UI 显示"已缓存可续传"）。</summary>
public readonly record struct DownloadProgress(double Ratio, long Bytes, long Total, string Text);

/// <summary>
/// 离线 ASR（Whisper.net / whisper.cpp），负责两件事：
///   1. 模型管理：按名称解析本地路径，缺失时从镜像下载（带进度）；
///   2. 识别：把 16kHz 单声道 float 采样转成文本。
///
/// 模型来自 ggerganov/whisper.cpp 的 GGML 文件。
/// huggingface.co 在部分网络下不可达，所以优先用 hf-mirror.com，失败再回退官方源。
/// </summary>
public sealed class WhisperAsr : IDisposable
{
    /// <summary>可选的模型（名称 → 大小提示，MB）。</summary>
    public static readonly (string Name, int SizeMb, string Note)[] Models =
    {
        ("tiny",  75,  "最快，准确度较低（适合先试）"),
        ("base",  142, "推荐：速度与准确度平衡"),
        ("small", 466, "更准，占用更高（日语效果更好）"),
    };

    private static readonly HttpClient Http = CreateHttp();

    private readonly string _modelsDir;
    private readonly object _lock = new();
    private WhisperFactory? _factory;
    private string _loadedModel = "";

    public WhisperAsr(string modelsDir)
    {
        _modelsDir = modelsDir;
        Directory.CreateDirectory(_modelsDir);
    }

    private static HttpClient CreateHttp()
    {
        // 超时不用 HttpClient.Timeout（它覆盖整个响应体读取，大模型下必然被砍断），
        // 改由下载循环里的"停滞看门狗"控制：连续 StallMs 没收到数据才算超时。
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Wisland/0.2 (Windows desktop widget; +https://github.com/Heacter233/wisland-csharp)");
        }
        catch { }
        return http;
    }

    public static string FileName(string model) => $"ggml-{model}.bin";

    /// <summary>
    /// 解析模型名："auto" 按 CPU 核心数选择 —— 8 核以上用 base（更准），
    /// 否则用 tiny（实测 4 核：tiny 6s 片段约 2s，base 约 4s，字幕延迟差别明显）。
    /// </summary>
    public static string ResolveModel(string model)
    {
        if (!string.IsNullOrWhiteSpace(model) && !model.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return model;
        return Environment.ProcessorCount >= 8 ? "base" : "tiny";
    }

    public string PathOf(string model) => Path.Combine(_modelsDir, FileName(model));

    public bool Exists(string model) => File.Exists(PathOf(model)) && new FileInfo(PathOf(model)).Length > 10 * 1024 * 1024;

    /// <summary>连续多久收不到数据视为断线（毫秒）。有数据就重置。</summary>
    private const int StallMs = 45_000;

    /// <summary>
    /// 下载模型。支持：
    ///   - 断点续传：已存在的 <c>ggml-x.bin.part</c> 用 HTTP Range 从断点继续；
    ///   - 取消：ct 触发后保留 .part，下次接着下；
    ///   - 断线保护：连续 45s 无数据判为超时（保留 .part 供续传）；
    ///   - 校验：文件大小 + GGML 魔数，脏文件自动丢弃重下一次。
    /// </summary>
    public async Task DownloadAsync(string model, Action<DownloadProgress>? progress, CancellationToken ct = default)
    {
        var target = PathOf(model);
        if (Exists(model))
        {
            var len = new FileInfo(target).Length;
            progress?.Invoke(new DownloadProgress(1, len, len, "模型已存在"));
            return;
        }

        Directory.CreateDirectory(_modelsDir);
        var tmp = target + ".part";
        var totalHint = ReadTotalHint(tmp);

        var urls = new[]
        {
            $"https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/{FileName(model)}",
            $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{FileName(model)}",
        };

        Exception? last = null;
        var cleaned = false;

        while (true)
        {
            var needClean = false;
            foreach (var url in urls)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await DownloadFromAsync(url, tmp, progress, totalHint, ct);

                    var size = new FileInfo(tmp).Length;
                    Validate(tmp);

                    if (File.Exists(target)) File.Delete(target);
                    File.Move(tmp, target, overwrite: true);
                    TryDelete(TotalHintPath(tmp));
                    progress?.Invoke(new DownloadProgress(1, size, size, "模型下载完成"));
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;                       // 用户取消：保留 .part
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (ex is InvalidDataException) needClean = true;   // 内容脏：只能重下
                    // 其他错误（网络/超时）保留 .part，下次续传
                }
            }

            if (needClean && !cleaned)
            {
                // 只清一次：清完再完整试一轮，避免死循环
                cleaned = true;
                TryDelete(tmp);
                TryDelete(TotalHintPath(tmp));
                totalHint = 0;
                continue;
            }
            break;
        }

        throw new InvalidOperationException(
            "模型下载失败（镜像与官方源都不可用）：" + (last?.Message ?? "未知错误") +
            "\n可手动下载 " + FileName(model) + " 放到 " + _modelsDir, last);
    }

    /// <summary>从单个源下载（若 .part 存在则续传）。</summary>
    private async Task DownloadFromAsync(string url, string tmp, Action<DownloadProgress>? progress,
        long totalHint, CancellationToken ct)
    {
        long offset = 0;
        if (File.Exists(tmp))
        {
            try { offset = new FileInfo(tmp).Length; } catch { offset = 0; }
        }
        // .part 比已知总大小还大：内容已错乱，丢弃重下
        if (totalHint > 0 && offset >= totalHint) { TryDelete(tmp); offset = 0; }
        if (offset > 1024L * 1024 * 1024) { TryDelete(tmp); offset = 0; }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (offset > 0) req.Headers.Range = new RangeHeaderValue(offset, null);

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            throw new InvalidDataException("续传位置无效（服务器返回 416），需要重新下载");

        resp.EnsureSuccessStatusCode();

        // 206 = 服务器接受了 Range（真续传）；200 = 不支持 Range，只能从头写
        var partial = resp.StatusCode == HttpStatusCode.PartialContent;
        if (!partial) offset = 0;

        var restLen = resp.Content.Headers.ContentLength ?? 0;
        var total = restLen > 0 ? offset + restLen : totalHint;
        if (total > 0) WriteTotalHint(tmp, total);

        progress?.Invoke(new DownloadProgress(
            total > 0 ? Math.Min(1.0, (double)offset / total) : 0,
            offset, total,
            offset > 0 ? $"从 {Mb(offset)} 处续传" : "开始下载"));

        // 停滞看门狗：ReadAsync 用它的 token，收到数据就往后推
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallMs);

        await using var input = await resp.Content.ReadAsStreamAsync(stall.Token);
        await using var output = new FileStream(tmp,
            offset > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long done = offset;
        var lastReport = DateTime.MinValue;

        while (true)
        {
            int read;
            try
            {
                read = await input.ReadAsync(buffer, 0, buffer.Length, stall.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException($"网络停滞超过 {StallMs / 1000} 秒（已保存 {Mb(done)}，可继续下载）");
            }
            if (read <= 0) break;

            // 落盘不接 ct：保证每个 80KB 块完整写入，否则续传点会落在半个块上
            await output.WriteAsync(buffer, 0, read, CancellationToken.None);
            done += read;
            stall.CancelAfter(StallMs);

            if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 200)
            {
                lastReport = DateTime.UtcNow;
                progress?.Invoke(new DownloadProgress(
                    total > 0 ? Math.Min(1.0, (double)done / total) : 0,
                    done, total,
                    $"{Mb(done)} / {(total > 0 ? Mb(total) : "?")} MB"));
            }
        }

        await output.FlushAsync(CancellationToken.None);

        if (total > 0 && done < total)
            throw new IOException($"连接提前结束（{Mb(done)} / {Mb(total)} MB），已保存，可继续下载");
        if (total > 0 && done > total)
            throw new InvalidDataException("下载字节数超过预期（文件可能损坏），需要重新下载");
    }

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0");

    private static string TotalHintPath(string tmp) => tmp + ".total";

    private static long ReadTotalHint(string tmp)
    {
        try
        {
            var p = TotalHintPath(tmp);
            if (!File.Exists(p)) return 0;
            return long.TryParse(File.ReadAllText(p).Trim(), out var v) ? v : 0;
        }
        catch { return 0; }
    }

    private static void WriteTotalHint(string tmp, long total)
    {
        try
        {
            var p = TotalHintPath(tmp);
            var s = total.ToString();
            if (!File.Exists(p) || File.ReadAllText(p) != s) File.WriteAllText(p, s);
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>完整性校验：体积下限 + GGML 魔数 "ggml"（0x6C 0x6D 0x67 0x67）。</summary>
    private static void Validate(string file)
    {
        var len = new FileInfo(file).Length;
        if (len < 10 * 1024 * 1024)
            throw new InvalidDataException($"文件过小（{Mb(len)} MB），不是有效的 GGML 模型");

        var magic = new byte[4];
        using (var fs = File.OpenRead(file)) fs.ReadExactly(magic, 0, 4);
        if (magic[0] != 0x6C || magic[1] != 0x6D || magic[2] != 0x67 || magic[3] != 0x67)
            throw new InvalidDataException("下载内容不是 GGML 模型（魔数不匹配）");
    }

    /// <summary>未完成的临时文件大小（0 表示没有缓存）。</summary>
    public long PartialBytes(string model)
    {
        var tmp = PathOf(model) + ".part";
        try { return File.Exists(tmp) ? new FileInfo(tmp).Length : 0; }
        catch { return 0; }
    }

    /// <summary>丢弃未完成的下载缓存。</summary>
    public void ClearPartial(string model)
    {
        var tmp = PathOf(model) + ".part";
        TryDelete(tmp);
        TryDelete(TotalHintPath(tmp));
    }

    /// <summary>识别一段 16k 单声道采样，language 为 whisper 语言码（auto/ja/en/zh）。</summary>
    public async Task<string> TranscribeAsync(float[] samples, string language, CancellationToken ct = default)
    {
        if (_disposed) return "";
        if (samples.Length < TargetRate / 4) return "";   // 少于 250ms 不值得识别

        samples = NormalizeForWhisper(samples);
        var factory = EnsureFactory(modelName: null);     // 复用已加载模型
        if (factory == null) return "";

        // 线程数：默认用满物理核心（留 1 个给 UI），可用 live.asr.threads 覆盖
        var threads = _threadsOverride > 0
            ? _threadsOverride
            : Math.Max(2, Environment.ProcessorCount - 1);

        var builder = factory.CreateBuilder()
            .WithThreads(threads)
            // 关闭温度回退：字幕场景不需要多轮重采样，能显著降低延迟
            .WithTemperature(0f);

        builder = string.IsNullOrWhiteSpace(language) || language == "auto"
            ? builder.WithLanguage("auto")
            : builder.WithLanguage(language);

        using var processor = builder.Build();

        // Whisper.net 需要 WAV/PCM16 流：这里自己封一个 16k 单声道 16bit WAV
        using var wav = BuildWav(samples);

        var sb = new System.Text.StringBuilder();
        await foreach (var result in processor.ProcessAsync(wav, ct))
        {
            var text = result.Text?.Trim() ?? "";
            if (text.Length == 0) continue;
            // 逐段清理：模型可能把一整句切成多段，每段都可能带句首 S 标记
            if (StripAnimePrefix &&
                string.Equals(_currentModel, "anime", StringComparison.OrdinalIgnoreCase))
                text = StripAnimeSpeakerMarker(text);
            sb.Append(text);
        }

        var text0 = sb.ToString().Trim();
        if (StripAnimePrefix &&
            string.Equals(_currentModel, "anime", StringComparison.OrdinalIgnoreCase))
            text0 = StripAnimeSpeakerMarker(text0);

        return text0;
    }

    /// <summary>
    /// anime-whisper 的训练语料（Galgame 台本）带有说话人/台本前缀标记，
    /// 模型经常会在句首多输出一个 "S"（如 "S魔物であることが…"）。
    /// 这里只在该模型下、且 "S" 后面紧跟日文（汉字/平假名）或引号时去掉，
    /// 保留 "Sランク" / "SS" / "S級" 这类合法的字母开头的词。
    /// </summary>
    private static string StripAnimeSpeakerMarker(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2) return text;
        var t = text.TrimStart();
        if (t[0] != 'S') return text;

        // 兼容 "S 魔物…" 这种带空格的变体（trim 掉 S 之后的空白）
        var body = t[1..].TrimStart();
        if (body.Length == 0) return text;

        var next = body[0];
        var japanese = (next >= '\u3040' && next <= '\u30ff') ||
                       (next >= '\u4e00' && next <= '\u9fff') || next == '々';
        var quote = next == '「' || next == '『' || next == '"' || next == '“';
        if (!japanese && !quote) return text;

        // 片假名开头（Sランク / Sクラス）大概率是合法词，保留
        if (next >= '\u30a0' && next <= '\u30ff') return text;

        // 常见合法复合词：S級 / Sランク / Sクラス / Sサイズ …（汉字开头也要保留）
        var rest = body;
        foreach (var legit in AnimeLegitSPrefixes)
        {
            if (rest.StartsWith(legit, StringComparison.Ordinal)) return text;
        }

        // 引号前是对话开始，去掉 S 更安全
        if (quote) return rest;
        // 其余汉字/平假名开头：判定为语料标记
        return rest;
    }

    /// <summary>合法的 "S+词" 前缀（等级/排名类），这些不当作语料标记清理。</summary>
    private static readonly string[] AnimeLegitSPrefixes =
    {
        "級", "ランク", "クラス", "サイズ", "カップ", "ランキング",
        "タイプ", "チーム", "シリーズ", "システム",
    };

    /// <summary>是否清理 anime-whisper 的句首 S 标记（默认开，可在设置里关）。</summary>
    public bool StripAnimePrefix { get; set; } = true;

    /// <summary>确保模型已加载；返回工厂实例。</summary>
    private WhisperFactory? EnsureFactory(string? modelName)
    {
        if (_disposed) return null;
        var model = modelName ?? _currentModel;
        if (string.IsNullOrWhiteSpace(model)) return null;

        lock (_lock)
        {
            if (_factory != null && _loadedModel == model) return _factory;
            try
            {
                _factory?.Dispose();
                var options = new WhisperFactoryOptions
                {
                    UseGpu = _useGpu,
                    GpuDevice = 0,
                };
                _factory = WhisperFactory.FromPath(PathOf(model), options);
                _loadedModel = model;
                try { App.DebugNote($"[Whisper] 已加载 {model}，运行库={WhisperFactory.GetRuntimeInfo()}"); }
                catch { }
                return _factory;
            }
            catch
            {
                _factory = null;
                _loadedModel = "";
                return null;
            }
        }
    }

    private string _currentModel = "base";
    private int _threadsOverride;

    private static bool _useGpu = true;

    private volatile bool _disposed;

    /// <summary>
    ///     // 选择 whisper.cpp 运行库后端：
    ///   auto   → Vulkan（AMD/Intel/NVIDIA 都支持）优先，失败自动回退 CPU
    ///   vulkan → 强制 Vulkan 优先
    ///   cuda   → CUDA12/CUDA 优先（需自备 CUDA 运行库；未安装时自动退 Vulkan/CPU）
    ///   off    → 只用 CPU
    /// 必须在创建 WhisperFactory 之前调用。
    /// </summary>
    public static void ConfigureRuntime(string mode)
    {
        var order = new List<RuntimeLibrary>();
        switch ((mode ?? "auto").Trim().ToLowerInvariant())
        {
            case "off":
            case "cpu":
                order.Add(RuntimeLibrary.Cpu);
                _useGpu = false;
                break;
            case "cuda":
                // CUDA 运行库没有随包（+400MB）；不存在时退到 Vulkan，避免选了 CUDA 直接加载失败
                if (RuntimeDirExists("cuda12") || RuntimeDirExists("cuda"))
                {
                    order.Add(RuntimeLibrary.Cuda12);
                    order.Add(RuntimeLibrary.Cuda);
                }
                order.Add(RuntimeLibrary.Vulkan);
                order.Add(RuntimeLibrary.Cpu);
                _useGpu = true;
                break;
            case "vulkan":
                if (RuntimeDirExists("vulkan")) order.Add(RuntimeLibrary.Vulkan);
                order.Add(RuntimeLibrary.Cpu);
                _useGpu = order.Count > 1;   // 只有 CPU 时不启用 GPU 标志
                break;
            default: // auto
                if (RuntimeDirExists("vulkan")) order.Add(RuntimeLibrary.Vulkan);
                order.Add(RuntimeLibrary.Cpu);
                _useGpu = order.Count > 1;
                break;
        }
        RuntimeOptions.RuntimeLibraryOrder = order;
        Debug.WriteLine($"[Whisper] 运行库顺序: {string.Join(",", order)}  UseGpu={_useGpu}");
    }

    /// <summary>输出目录里是否存在某个后端运行库目录（如 runtimes/vulkan/win-x64）。</summary>
    private static bool RuntimeDirExists(string backend)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "runtimes", backend, "win-x64");
            return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*whisper*.dll").Any();
        }
        catch { return false; }
    }

    /// <summary>识别线程数（0 = 自动：核心数 - 1）。</summary>
    public void SetThreads(int threads) => _threadsOverride = Math.Max(0, threads);

    /// <summary>切换模型（下次识别生效）。</summary>
    public void UseModel(string model)
    {
        lock (_lock)
        {
            if (_loadedModel == model) { _currentModel = model; return; }
            _currentModel = model;
        }
    }

    /// <summary>峰值归一化：AGC 之后再做一层保险，极安静的片段也能被模型识别。</summary>
    private static float[] NormalizeForWhisper(float[] samples)
    {
        if (samples.Length == 0) return samples;
        var peak = 0f;
        foreach (var s in samples) peak = Math.Max(peak, Math.Abs(s));
        if (peak < 1e-4f) return samples;
        var gain = Math.Min(0.9f / peak, 8f);
        if (gain <= 1.01f) return samples;
        var outp = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++) outp[i] = Math.Clamp(samples[i] * gain, -1f, 1f);
        return outp;
    }

    private static MemoryStream BuildWav(float[] samples)
    {
        var dataLen = samples.Length * 2;
        var ms = new MemoryStream(44 + dataLen);
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + dataLen);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);
            w.Write((short)1);          // PCM
            w.Write((short)1);          // 单声道
            w.Write(TargetRate);
            w.Write(TargetRate * 2);    // 字节率
            w.Write((short)2);          // 块对齐
            w.Write((short)16);         // 位深
            w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            w.Write(dataLen);
            foreach (var s in samples)
            {
                var v = (short)Math.Clamp((int)(s * 32767f), short.MinValue, short.MaxValue);
                w.Write(v);
            }
        }
        ms.Position = 0;
        return ms;
    }

    private const int TargetRate = AudioCapture.TargetRate;

    public void Dispose()
    {
        _disposed = true;
        lock (_lock)
        {
            try { _factory?.Dispose(); } catch { }
            _factory = null;
            _loadedModel = "";
        }
    }
}
