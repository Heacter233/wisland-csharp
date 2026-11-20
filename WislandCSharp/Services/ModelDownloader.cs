using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Wisland.Services;

/// <summary>
/// 通用大文件下载：HTTP Range 续传 + 停滞看门狗 + 进度回调。
/// 供 AsrModelStore 下载 GGML / ONNX / tokens 等多文件模型包使用。
/// </summary>
internal static class ModelDownloader
{
    private const int StallMs = 45_000;
    private const int BufferBytes = 81920;

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        // 连接/TLS 握手单独计时：之前用无超时的 HttpClient，遇到代理/镜像握手卡住会一直挂着，
        // 表现为"下载 0 字节但永远不结束"。响应体的大文件耗时交给停滞看门狗。
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Wisland/0.2 (Windows desktop widget; +https://github.com/Heacter233/wisland-csharp)");
        }
        catch { }
        return http;
    }

    /// <summary>
    /// 下载单个文件到 target（.part 续传），成功后自动改名为正式文件。
    /// progress 的 Bytes/Total 是"本文件"的进度，Ratio 已按本文件大小归一化。
    /// </summary>
    public static async Task DownloadFileAsync(string[] urls, string target, long expectedBytes,
        Action<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? ".");

        if (IsValidFile(target, expectedBytes, tokenFile: false))
        {
            progress?.Invoke(new DownloadProgress(1, expectedBytes, expectedBytes, "已存在"));
            return;
        }

        var tmp = target + ".part";
        var totalHint = ReadTotalHint(tmp);
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

                    if (!IsValidFile(tmp, expectedBytes, tokenFile: false))
                        throw new InvalidDataException("文件大小与预期不符，可能下载不完整");

                    if (File.Exists(target)) File.Delete(target);
                    File.Move(tmp, target, overwrite: true);
                    TryDelete(TotalHintPath(tmp));
                    progress?.Invoke(new DownloadProgress(1, expectedBytes, expectedBytes, "下载完成"));
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;                     // 用户取消：保留 .part 供续传
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (ex is InvalidDataException) needClean = true;
                }
            }

            if (needClean && !cleaned)
            {
                cleaned = true;
                TryDelete(tmp);
                TryDelete(TotalHintPath(tmp));
                totalHint = 0;
                continue;
            }
            break;
        }

        throw new InvalidOperationException(
            "下载失败（镜像与官方源都不可用）：" + (last?.Message ?? "未知错误"), last);
    }

    /// <summary>文件是否完整可用（大小阈值；token 文件只要求非空）。</summary>
    public static bool IsValidFile(string path, long expectedBytes, bool tokenFile)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var len = new FileInfo(path).Length;
            if (tokenFile) return len > 0;
            if (expectedBytes > 0) return len >= (long)(expectedBytes * 0.95);
            return len > 4 * 1024 * 1024;
        }
        catch { return false; }
    }

    private static async Task DownloadFromAsync(string url, string tmp, Action<DownloadProgress>? progress,
        long totalHint, CancellationToken ct)
    {
        long offset = 0;
        if (File.Exists(tmp))
        {
            try { offset = new FileInfo(tmp).Length; } catch { offset = 0; }
        }
        if (totalHint > 0 && offset >= totalHint) { TryDelete(tmp); offset = 0; }
        if (offset > 2L * 1024 * 1024 * 1024) { TryDelete(tmp); offset = 0; }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (offset > 0) req.Headers.Range = new RangeHeaderValue(offset, null);

        // 只给"等响应头"阶段加超时（60s），拿到响应后由停滞看门狗接管
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerTimeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            throw new InvalidDataException("续传位置无效（416），需要重新下载");

        resp.EnsureSuccessStatusCode();

        var partial = resp.StatusCode == HttpStatusCode.PartialContent;
        if (!partial) offset = 0;

        var restLen = resp.Content.Headers.ContentLength ?? 0;
        var total = restLen > 0 ? offset + restLen : totalHint;
        if (total > 0) WriteTotalHint(tmp, total);

        progress?.Invoke(new DownloadProgress(
            total > 0 ? Math.Min(1.0, (double)offset / total) : 0,
            offset, total,
            offset > 0 ? $"从 {Mb(offset)} MB 处续传" : "开始下载"));

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallMs);

        await using var input = await resp.Content.ReadAsStreamAsync(stall.Token);
        await using var output = new FileStream(tmp,
            offset > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, BufferBytes, useAsync: true);

        var buffer = new byte[BufferBytes];
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
                throw new IOException($"网络停滞超过 {StallMs / 1000} 秒（已保存 {Mb(done)} MB，可继续下载）");
            }
            if (read <= 0) break;

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
}
