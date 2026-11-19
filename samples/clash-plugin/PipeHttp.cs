using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClashPlugin;

/// <summary>
/// 通过 Windows 命名管道发送 HTTP 请求（最小实现）。
///
/// 背景：Clash Verge / mihomo 除了 TCP 端口，还可以只开命名管道：
///   external-controller-pipe: \\.\pipe\verge-mihomo
/// 该管道上的协议仍然是 HTTP/1.1，所以这里手写请求/解析响应即可，
/// 不依赖任何第三方库，也不需要管理员权限。
///
/// 约定：一律带 Connection: close，然后读到管道关闭为止
/// （mihomo 会按 close 语义结束响应）。
/// </summary>
internal static class PipeHttp
{
    /// <summary>一次管道 HTTP 调用。</summary>
    public sealed class Result
    {
        public int StatusCode;
        public string Reason = "";
        public string Body = "";
        public bool Ok => StatusCode is >= 200 and < 300;
    }

    public static async Task<Result> SendAsync(
        string pipeName,
        string method,
        string path,
        string? bearer,
        string? jsonBody,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        await using var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(1500, cts.Token).ConfigureAwait(false);

        var bodyBytes = jsonBody == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(jsonBody);

        var sb = new StringBuilder();
        sb.Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\n");
        sb.Append("Host: localhost\r\n");
        sb.Append("Accept: application/json\r\n");
        sb.Append("Connection: close\r\n");
        if (!string.IsNullOrEmpty(bearer))
            sb.Append("Authorization: Bearer ").Append(bearer).Append("\r\n");
        if (jsonBody != null)
        {
            sb.Append("Content-Type: application/json\r\n");
            sb.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
        }
        sb.Append("\r\n");

        var headBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await pipe.WriteAsync(headBytes, 0, headBytes.Length, cts.Token).ConfigureAwait(false);
        if (bodyBytes.Length > 0)
            await pipe.WriteAsync(bodyBytes, 0, bodyBytes.Length, cts.Token).ConfigureAwait(false);
        await pipe.FlushAsync(cts.Token).ConfigureAwait(false);

        // 读到管道关闭
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        while (true)
        {
            var n = await pipe.ReadAsync(buf, 0, buf.Length, cts.Token).ConfigureAwait(false);
            if (n <= 0) break;
            ms.Write(buf, 0, n);
        }

        return Parse(ms.ToArray());
    }

    private static Result Parse(byte[] raw)
    {
        var text = Encoding.UTF8.GetString(raw);
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        string head, body;
        if (split >= 0)
        {
            head = text[..split];
            body = text[(split + 4)..];
        }
        else
        {
            head = text;
            body = "";
        }

        var result = new Result { Body = body };
        var lines = head.Split("\r\n");
        if (lines.Length == 0) return result;

        // 状态行：HTTP/1.1 200 OK
        var statusLine = lines[0].Split(' ', 3);
        if (statusLine.Length >= 2 && int.TryParse(statusLine[1], out var code)) result.StatusCode = code;
        if (statusLine.Length >= 3) result.Reason = statusLine[2];

        // 处理 chunked
        var chunked = head.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase);
        if (chunked)
        {
            try { result.Body = Dechunk(body); } catch { }
        }
        return result;
    }

    private static string Dechunk(string body)
    {
        var sb = new StringBuilder();
        var idx = 0;
        while (idx < body.Length)
        {
            var nl = body.IndexOf("\r\n", idx, StringComparison.Ordinal);
            if (nl < 0) break;
            var sizeHex = body[idx..nl].Trim();
            if (!int.TryParse(sizeHex, System.Globalization.NumberStyles.HexNumber, null, out var size) || size == 0) break;
            var start = nl + 2;
            if (start + size > body.Length) size = body.Length - start;
            sb.Append(body.Substring(start, size));
            idx = start + size + 2;
        }
        return sb.ToString();
    }
}