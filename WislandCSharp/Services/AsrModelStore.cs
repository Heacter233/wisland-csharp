using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Wisland.Services;

/// <summary>
/// 多文件 ASR 模型包存储层：把 AsrModels 里的模型描述映射成
/// 「models 目录下的若干文件」，并负责存在性判断、体积统计、下载、删除。
///
/// Whisper 是单文件 ggml-*.bin；SenseVoice 是 sensevoice/model.int8.onnx + tokens.txt。
/// </summary>
public sealed class AsrModelStore
{
    public AsrModelStore(string modelsDir)
    {
        Dir = modelsDir;
        Directory.CreateDirectory(Dir);
    }

    /// <summary>模型根目录（绝对路径）。</summary>
    public string Dir { get; }

    public static AsrModelInfo? Info(string model) => AsrModels.Find(model);

    public string FullPath(AsrModelFile file) => Path.Combine(Dir, file.LocalPath);

    /// <summary>主文件路径（UI 显示 / 判断用）。</summary>
    public string PrimaryPath(string model)
    {
        var info = Info(model);
        return info == null ? Path.Combine(Dir, model) : FullPath(info.Files[0]);
    }

    public IEnumerable<string> AllPaths(string model)
    {
        var info = Info(model);
        return info == null ? Array.Empty<string>() : info.Files.Select(FullPath);
    }

    private bool FileValid(AsrModelFile file)
    {
        var path = FullPath(file);
        // ggml 文件顺带校验魔数，避免把 HTML 错误页当模型
        if (!file.IsTokenFile && file.LocalPath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            if (!ModelDownloader.IsValidFile(path, file.SizeBytes, false)) return false;
            try
            {
                // GGML 魔数是 uint32 0x67676D6C，小端落盘后字节为 'l','m','g','g'
                using var fs = File.OpenRead(path);
                var buf = new byte[4];
                var n = fs.Read(buf, 0, 4);
                return n == 4 && buf[0] == (byte)'l' && buf[1] == (byte)'m'
                            && buf[2] == (byte)'g' && buf[3] == (byte)'g';
            }
            catch { return false; }
        }
        return ModelDownloader.IsValidFile(path, file.SizeBytes, file.IsTokenFile);
    }

    /// <summary>模型包是否完整（所有文件都在）。</summary>
    public bool Exists(string model)
    {
        var info = Info(model);
        if (info == null) return false;
        return info.Files.All(FileValid);
    }

    /// <summary>已下载正式文件的总字节数。</summary>
    public long SizeBytes(string model)
    {
        var info = Info(model);
        if (info == null) return 0;
        long sum = 0;
        foreach (var f in info.Files)
        {
            try { var p = FullPath(f); if (File.Exists(p)) sum += new FileInfo(p).Length; }
            catch { }
        }
        return sum;
    }

    /// <summary>未下完的 .part 字节数合计。</summary>
    public long PartialBytes(string model)
    {
        var info = Info(model);
        if (info == null) return 0;
        long sum = 0;
        foreach (var f in info.Files)
        {
            try { var p = FullPath(f) + ".part"; if (File.Exists(p)) sum += new FileInfo(p).Length; }
            catch { }
        }
        return sum;
    }

    /// <summary>下载时记录的总大小（.part 旁的 .total，用于续传先画出进度）。</summary>
    public long TotalHintBytes(string model)
    {
        var info = Info(model);
        if (info == null) return 0;
        long sum = 0;
        foreach (var f in info.Files)
        {
            var p = FullPath(f) + ".part.total";
            try { if (File.Exists(p) && long.TryParse(File.ReadAllText(p).Trim(), out var v)) sum += v; }
            catch { }
        }
        return sum > 0 ? sum : info.TotalBytes;
    }

    /// <summary>删除模型包（不动 .part）。</summary>
    public void Delete(string model)
    {
        foreach (var path in AllPaths(model))
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    /// <summary>丢弃未完成缓存。</summary>
    public void ClearPartial(string model)
    {
        foreach (var path in AllPaths(model))
        {
            TryDelete(path + ".part");
            TryDelete(path + ".part.total");
        }
    }

    /// <summary>
    /// 下载整个模型包（按文件顺序，逐文件续传）。
    /// progress.Ratio 是整包进度；Text 带文件名与序号。
    /// </summary>
    public async Task DownloadAsync(string model, Action<DownloadProgress>? progress, CancellationToken ct)
    {
        var info = Info(model)
                   ?? throw new InvalidOperationException($"未知模型：{model}");

        var totalBytes = Math.Max(1, info.TotalBytes);
        var doneBefore = 0L;

        for (var i = 0; i < info.Files.Length; i++)
        {
            var file = info.Files[i];
            var path = FullPath(file);
            var index = i;

            Action<DownloadProgress> onFile = p =>
            {
                var done = doneBefore + p.Bytes;
                progress?.Invoke(new DownloadProgress(
                    Math.Min(1.0, (double)done / totalBytes),
                    done, totalBytes,
                    info.Files.Length > 1
                        ? $"{info.DisplayName} {index + 1}/{info.Files.Length} · {p.Text}"
                        : $"{info.DisplayName} · {p.Text}"));
            };

            if (ModelDownloader.IsValidFile(path, file.SizeBytes, file.IsTokenFile))
            {
                doneBefore += file.SizeBytes;
                continue;
            }

            await ModelDownloader.DownloadFileAsync(file.Urls, path, file.SizeBytes, onFile, ct);
            doneBefore += file.SizeBytes;
        }

        if (!Exists(model))
            throw new InvalidDataException("模型下载完成但校验未通过，请重试或清缓存后重新下载");

        progress?.Invoke(new DownloadProgress(1, totalBytes, totalBytes, "模型下载完成"));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
