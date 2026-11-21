using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wisland.Services;

/// <summary>
/// 模型文件哈希：用于下载后完整性校验（防镜像投毒 / 断点拼接出"旧头新尾"）。
///
/// 哈希来源：HuggingFace 仓库 API 的 LFS 元数据（<c>lfs.oid</c> 就是文件的 SHA256）。
/// 取不到时（离线、镜像不支持 API、GitHub Release 资产）返回 null —— 这时只做体积校验，
/// 并在日志里注明"未校验哈希"，而不是拒绝下载（否则国内网络几乎下不了模型）。
/// </summary>
internal static class ModelHashes
{
    private static readonly HttpClient Http = CreateHttp();

    /// <summary>URL → SHA256（null = 查过但没有）。</summary>
    private static readonly ConcurrentDictionary<string, string?> Cache = new();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Wisland/0.2 (Windows desktop widget; +https://github.com/Heacter233/wisland-csharp)");
        }
        catch { }
        return http;
    }

    /// <summary>解析某个下载 URL 对应文件的 SHA256（十六进制小写）；取不到返回 null。</summary>
    public static async Task<string?> ResolveAsync(string url, CancellationToken ct)
    {
        if (!TryParseHfUrl(url, out var host, out var repo, out var revision, out var file)) return null;

        var cacheKey = $"{repo}|{revision}|{file}";
        if (Cache.TryGetValue(cacheKey, out var cached)) return cached;

        // hf-mirror 也代理了 API；失败再回官方（官方在国内常被判超时）
        var apiHosts = host.Contains("hf-mirror", StringComparison.OrdinalIgnoreCase)
            ? new[] { host, "huggingface.co" }
            : new[] { host };

        foreach (var apiHost in apiHosts)
        {
            try
            {
                var api = $"https://{apiHost}/api/models/{repo}/tree/{revision}?recursive=true";
                using var resp = await Http.GetAsync(api, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;

                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;

                foreach (var entry in doc.RootElement.EnumerateArray())
                {
                    if (!entry.TryGetProperty("path", out var p)) continue;
                    var path = p.GetString() ?? "";
                    if (!string.Equals(path, file, StringComparison.Ordinal)) continue;
                    if (!entry.TryGetProperty("lfs", out var lfs)) continue;
                    if (!lfs.TryGetProperty("oid", out var oid)) continue;
                    var hash = (oid.GetString() ?? "").Trim().ToLowerInvariant();
                    if (hash.Length == 64) { Cache[cacheKey] = hash; return hash; }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                App.DebugNote($"model-hash: 查询 {apiHost} 失败：{ex.Message}");
            }
        }

        Cache[cacheKey] = null;      // 记负缓存，避免每次下载都重试
        return null;
    }

    private static bool TryParseHfUrl(string url, out string host, out string repo, out string revision, out string file)
    {
        host = repo = revision = file = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!uri.Host.Contains("huggingface", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Contains("hf-mirror", StringComparison.OrdinalIgnoreCase)) return false;

        // 形如 /{owner}/{repo}/resolve/{revision}/{path...}
        var seg = uri.AbsolutePath.Trim('/').Split('/');
        if (seg.Length < 5) return false;
        var idx = Array.FindIndex(seg, s => string.Equals(s, "resolve", StringComparison.Ordinal));
        if (idx != 2) return false;

        host = uri.Host;
        repo = seg[0] + "/" + seg[1];
        revision = seg[idx + 1];
        file = Uri.UnescapeDataString(string.Join("/", seg.Skip(idx + 2)));
        return true;
    }

    /// <summary>计算文件 SHA256（十六进制小写）。</summary>
    public static string ComputeFileSha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
