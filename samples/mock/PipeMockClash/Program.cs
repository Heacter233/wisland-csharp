using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;

// 极简 mock：在命名管道上跑 HTTP，用来验证 ClashPlugin 的管道通道。
// 用法： dotnet run --project PipeMockClash -- verge-mihomo-test
internal static class Program
{
    private static readonly string Json =
        "{\"proxies\":{\"GLOBAL\":{\"type\":\"Selector\",\"now\":\"DIRECT\",\"all\":[\"DIRECT\",\"REJECT\"]}," +
        "\"节点选择\":{\"type\":\"Selector\",\"now\":\"节点A\",\"all\":[\"节点A\",\"节点B\",\"DIRECT\"]}}}";

    private static async Task Main(string[] args)
    {
        var name = args.Length > 0 ? args[0] : "verge-mihomo-test";
        Console.WriteLine($"pipe mock listening on \\\\.\\pipe\\{name}");
        while (true)
        {
            await using var server = new NamedPipeServerStream(
                name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync();

            var ms = new MemoryStream();
            var buf = new byte[4096];
            // 读到请求头结束
            while (true)
            {
                var n = await server.ReadAsync(buf, 0, buf.Length);
                if (n <= 0) break;
                ms.Write(buf, 0, n);
                var text = Encoding.ASCII.GetString(ms.ToArray());
                if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var head = text[..text.IndexOf("\r\n\r\n", StringComparison.Ordinal)];
                    var cl = 0;
                    foreach (var line in head.Split("\r\n"))
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            int.TryParse(line[15..].Trim(), out cl);
                    var headerLen = Encoding.ASCII.GetByteCount(head) + 4;
                    if (ms.Length >= headerLen + cl) break;
                }
            }

            var req = Encoding.UTF8.GetString(ms.ToArray());
            var first = req.Split("\r\n")[0];
            var path = first.Split(' ').Length > 1 ? first.Split(' ')[1] : "/";
            Console.WriteLine($"[pipe mock] {first}");

            string body;
            string status = "200 OK";
            if (path.StartsWith("/version")) body = "{\"version\":\"v1.18.0-pipemock\"}";
            else if (path.StartsWith("/configs")) body = "{\"mode\":\"rule\"}";
            else if (path.StartsWith("/proxies")) body = Json;
            else { status = "404 Not Found"; body = "{\"error\":\"not found\"}"; }

            var bytes = Encoding.UTF8.GetBytes(body);
            var resp = $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            var headBytes = Encoding.ASCII.GetBytes(resp);
            await server.WriteAsync(headBytes);
            await server.WriteAsync(bytes);
            await server.FlushAsync();
            server.Disconnect();
        }
    }
}
