using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;

namespace Wisland.Services;

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
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
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

    /// <summary>下载模型（进度 0~1 回调）。已存在则直接返回。</summary>
    public async Task DownloadAsync(string model, Action<double, string>? progress, CancellationToken ct = default)
    {
        var target = PathOf(model);
        if (Exists(model)) { progress?.Invoke(1, "模型已存在"); return; }

        Directory.CreateDirectory(_modelsDir);
        var tmp = target + ".part";
        var urls = new[]
        {
            $"https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/{FileName(model)}",
            $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{FileName(model)}",
        };

        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? 0;

                await using (var input = await resp.Content.ReadAsStreamAsync(ct))
                await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920];
                    long done = 0;
                    var lastReport = DateTime.MinValue;
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer, 0, buffer.Length, ct);
                        if (read <= 0) break;
                        await output.WriteAsync(buffer, 0, read, ct);
                        done += read;

                        if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 200)
                        {
                            lastReport = DateTime.UtcNow;
                            var ratio = total > 0 ? (double)done / total : 0;
                            progress?.Invoke(ratio, $"{done / 1048576.0:0.0} / {(total > 0 ? (total / 1048576.0).ToString("0.0") : "?")} MB");
                        }
                    }
                }

                // 校验：GGML 魔数 "ggml"（小端）
                var magic = new byte[4];
                using (var fs = File.OpenRead(tmp)) fs.Read(magic, 0, 4);
                if (magic[0] != 0x6C || magic[1] != 0x6D || magic[2] != 0x67 || magic[3] != 0x67)
                    throw new InvalidDataException("下载内容不是 GGML 模型（魔数不匹配）");

                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
                progress?.Invoke(1, "模型下载完成");
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        throw new InvalidOperationException(
            "模型下载失败（镜像与官方源都不可用）：" + (last?.Message ?? "未知错误") +
            "\n可手动下载 " + FileName(model) + " 放到 " + _modelsDir, last);
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
