using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;

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
        if (samples.Length < TargetRate / 4) return "";   // 少于 250ms 不值得识别

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
            if (text.Length > 0) sb.Append(text);
        }
        return sb.ToString().Trim();
    }

    /// <summary>确保模型已加载；返回工厂实例。</summary>
    private WhisperFactory? EnsureFactory(string? modelName)
    {
        var model = modelName ?? _currentModel;
        if (string.IsNullOrWhiteSpace(model)) return null;

        lock (_lock)
        {
            if (_factory != null && _loadedModel == model) return _factory;
            try
            {
                _factory?.Dispose();
                _factory = WhisperFactory.FromPath(PathOf(model));
                _loadedModel = model;
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
        lock (_lock)
        {
            try { _factory?.Dispose(); } catch { }
            _factory = null;
        }
    }
}
