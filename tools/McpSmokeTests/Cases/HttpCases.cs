using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ScriptDebugEngine.Mcp;

namespace McpSmokeTests
{
    /// <summary>A 组：HTTP 报文层的畸形与边界输入。</summary>
    internal static class HttpCases
    {
        public static void Register(Runner runner)
        {
            int port = runner.Env.Port;

            runner.Case("A-01", "随机二进制垃圾（无 CRLFCRLF）", () =>
            {
                string response = RawClient.Send(port, RawClient.RandomBytes(512), 8000);
                int status = RawClient.StatusCode(response);
                runner.Check(status == 0 || status == 400 || status == 408,
                    "随机垃圾应被关闭或回 400/408，实际 status=" + status + " -> " + Runner.Describe(response));
                runner.Note("status=" + status + "（无分隔符时只能等头部时限）");
            });

            runner.Case("A-02", "只有 CRLFCRLF 的空请求", () =>
            {
                string response = RawClient.SendText(port, "\r\n\r\n", 8000);
                runner.ExpectStatus("空请求行", response, 400);
            });

            runner.Case("A-03", "头部超过 8KB", () =>
            {
                var request = new StringBuilder();
                request.Append("POST /mcp HTTP/1.1\r\nHost: 127.0.0.1\r\n");
                request.Append("X-Pad: ").Append(new string('a', 9000)).Append("\r\n\r\n");
                string response = RawClient.SendText(port, request.ToString(), 8000);
                runner.ExpectStatus("超长头部", response, 431);
            });

            runner.Case("A-04", "查询串（4KB，总头部仍 < 8KB）命中 /mcp", () =>
            {
                string request = "POST /mcp?" + new string('q', 4000) + " HTTP/1.1\r\nHost: x\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                string response = RawClient.SendText(port, request, 8000);
                runner.ExpectStatus("带查询串", response, 200);
                runner.Check(Runner.ErrorCode(response) == -32700, "空 body 应为 -32700，实际: " + Runner.ResultJson(response));
            });

            runner.Case("A-05", "请求行缺版本", () =>
            {
                string response = RawClient.SendText(port, "POST /mcp\r\nHost: x\r\nContent-Length: 0\r\n\r\n", 8000);
                runner.ExpectStatus("缺版本", response, 200);
                runner.Check(Runner.ErrorCode(response) == -32700, "应为 -32700，实际: " + Runner.ResultJson(response));
            });

            runner.Case("A-06", "未知 HTTP 方法 / HEAD", () =>
            {
                runner.ExpectStatus("PUT", RawClient.SendText(port, "PUT /mcp HTTP/1.1\r\nHost: x\r\nContent-Length: 0\r\n\r\n", 8000), 404);
                runner.ExpectStatus("OPTIONS", RawClient.SendText(port, "OPTIONS /mcp HTTP/1.1\r\nHost: x\r\n\r\n", 8000), 404);
                runner.ExpectStatus("HEAD", RawClient.SendText(port, "HEAD /mcp HTTP/1.1\r\nHost: x\r\n\r\n", 8000), 404);
                runner.Note("HEAD 目前仍带 body（已知小瑕疵，见方案 §5-P2）");
            });

            runner.Case("A-07", "GET /mcp → 405", () =>
            {
                string response = RawClient.Get(port, "/mcp");
                runner.ExpectStatus("GET /mcp", response, 405);
                runner.ExpectContains("Content-Type", response, "text/plain");
            });

            runner.Case("A-08", "HTTP/1.0 且无 Host", () =>
            {
                string response = RawClient.SendText(port, "POST /mcp HTTP/1.0\r\nContent-Length: 0\r\n\r\n", 8000);
                runner.ExpectStatus("HTTP/1.0", response, 200);
                runner.Check(Runner.ErrorCode(response) == -32700, "应为 -32700，实际: " + Runner.ResultJson(response));
            });

            runner.Case("A-09", "重复 Content-Length → 400", () =>
            {
                string response = RawClient.SendText(port, "POST /mcp HTTP/1.1\r\nHost: x\r\nContent-Length: 2\r\nContent-Length: 3\r\n\r\n{}", 8000);
                runner.ExpectStatus("重复 CL", response, 400);
            });

            runner.Case("A-10", "Content-Length + Transfer-Encoding 并存 → 400", () =>
            {
                string response = RawClient.SendText(port, "POST /mcp HTTP/1.1\r\nHost: x\r\nContent-Length: 2\r\nTransfer-Encoding: chunked\r\n\r\n{}", 8000);
                runner.ExpectStatus("CL+TE", response, 400);
            });

            runner.Case("A-11", "仅 chunked → 411", () =>
            {
                string response = RawClient.SendText(port, "POST /mcp HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n2\r\n{}\r\n0\r\n\r\n", 8000);
                runner.ExpectStatus("chunked", response, 411);
            });

            runner.Case("A-12", "Content-Length 大于实发 → 408（不挂死）", () =>
            {
                var watch = Stopwatch.StartNew();
                string response = RawClient.SendText(port, "POST /mcp HTTP/1.1\r\nHost: x\r\nContent-Length: 100\r\n\r\n{}", 20000);
                watch.Stop();
                runner.ExpectStatus("声明 100 只发 2", response, 408);
                runner.Check(watch.ElapsedMilliseconds <= 13000, "应在正文时限(10s)附近结束，实际 " + watch.ElapsedMilliseconds + "ms");
                runner.Note("耗时 " + watch.ElapsedMilliseconds + "ms");
            });

            runner.Case("A-13", "Content-Length 小于实发 → 只读声明部分", () =>
            {
                string response = RawClient.SendText(port, "POST /mcp HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\nConnection: close\r\n\r\n{\"a\":1}EXTRA", 8000);
                runner.ExpectStatus("CL 偏小", response, 200);
                runner.Check(Runner.ErrorCode(response) == -32700, "截断 JSON 应为 -32700，实际: " + Runner.ResultJson(response));
            });

            runner.Case("A-14", "声明 100MB 正文 → 413（丢弃不挂死）", () =>
            {
                var watch = Stopwatch.StartNew();
                string response = RawClient.SendText(port, "POST /mcp HTTP/1.1\r\nHost: x\r\nContent-Length: 104857600\r\n\r\n", 20000);
                watch.Stop();
                runner.ExpectStatus("100MB 声明", response, 413);
                runner.Check(watch.ElapsedMilliseconds <= 6000, "丢弃时限 2s，实际 " + watch.ElapsedMilliseconds + "ms");
                runner.Note("耗时 " + watch.ElapsedMilliseconds + "ms");
            });

            runner.Case("A-15", "正文上限边界 65535 / 65536 / 65537", () =>
            {
                foreach (int size in new[] { 65535, 65536, 65537 })
                {
                    string response = RawClient.Post(port, BuildPaddedJson(size), 15000);
                    if (size <= 65536)
                    {
                        runner.ExpectStatus("size=" + size, response, 200);
                        runner.Check(Runner.ErrorCode(response) == -32601, "size=" + size + " 应正常解析为 -32601，实际: " + Runner.ResultJson(response));
                    }
                    else
                    {
                        runner.ExpectStatus("size=" + size, response, 413);
                    }
                }
            });

            runner.Case("A-16", "同一连接流水线两个请求", () =>
            {
                const string one = "POST /mcp HTTP/1.1\r\nHost: x\r\nContent-Length: 40\r\n\r\n{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}";
                string response = RawClient.SendText(port, one + one, 8000);
                runner.ExpectStatus("第一个请求", response, 200);
                runner.Note("pipelining：只处理第一个，随后 Connection: close（预期行为）");
            });

            runner.Case("A-17", "客户端收响应前 RST（反复 5 次）", () =>
            {
                byte[] request = Encoding.ASCII.GetBytes("POST /mcp HTTP/1.1\r\nHost: x\r\nContent-Length: 2\r\n\r\n{}");
                for (int i = 0; i < 5; i++) RawClient.SendAbort(port, request);
                runner.Note("已发 5 次 RST 断开，本用例只断言服务器仍活着（见探活）");
            });

            runner.Case("A-18", "半关闭（shutdown send）", () =>
            {
                string response = RawClient.SendText(port, "GET /health HTTP/1.1\r\nHost: x\r\n\r\n", 8000, true);
                runner.ExpectStatus("半关闭 GET", response, 200);
            });

            runner.Case("A-19", "50 个慢速连接 + 正常请求仍及时", () =>
            {
                var slow = new List<TcpClient>();
                try
                {
                    for (int i = 0; i < 50; i++) slow.Add(RawClient.OpenSlowConnection(port, 8));

                    var watch = Stopwatch.StartNew();
                    string response = RawClient.Get(port, "/health", 6000);
                    watch.Stop();

                    runner.ExpectStatus("慢速连接下的正常请求", response, 200);
                    runner.Check(watch.ElapsedMilliseconds < 3000, "正常请求应很快完成，实际 " + watch.ElapsedMilliseconds + "ms");
                    runner.Note("50 个半开连接，正常请求耗时 " + watch.ElapsedMilliseconds + "ms");
                }
                finally
                {
                    foreach (TcpClient client in slow)
                    {
                        try { client.Close(); }
                        catch (Exception) { }
                    }
                }
            });

            runner.Case("A-20", "IPv6 回环不可用（只绑 IPv4，预期）", () =>
            {
                try
                {
                    using (var client = new TcpClient(AddressFamily.InterNetworkV6))
                    {
                        client.Connect(IPAddress.IPv6Loopback, port);
                        runner.Check(false, "IPv6 竟然连上了（预期应被拒绝）");
                    }
                }
                catch (SocketException)
                {
                    runner.Note("IPv6 回环被拒（符合预期）");
                }
            });

            runner.Case("A-21", "端口被占用时的启动行为", () =>
            {
                int busyPort = RawClient.FreePort();
                var blocker = new TcpListener(IPAddress.Loopback, busyPort);
                blocker.Start();
                try
                {
                    McpServer server = null;
                    try
                    {
                        server = runner.StartServer(busyPort);
                        runner.Note("占用端口上仍启动成功（Windows SO_REUSEADDR 语义，已记录；探活确认主服务器未受影响）");
                    }
                    catch (SocketException)
                    {
                        runner.Note("启动失败并抛 SocketException（符合预期）");
                    }
                    finally
                    {
                        if (server != null) server.Stop();
                    }
                }
                finally
                {
                    blocker.Stop();
                }
            });

            runner.Case("A-22", "非法端口值 0 / -1 / 70000", () =>
            {
                foreach (int bad in new[] { 0, -1, 70000 })
                {
                    try
                    {
                        McpServer server = runner.StartServer(bad);
                        server.Stop();
                        runner.Check(false, "端口 " + bad + " 不应启动成功");
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        runner.Note("端口 " + bad + " 被拒绝（预期）");
                    }
                    catch (SocketException)
                    {
                        runner.Note("端口 " + bad + " 抛 SocketException");
                    }
                }
            });
        }

        /// <summary>构造恰好 size 字节的合法 JSON（用于探测正文上限）。</summary>
        private static string BuildPaddedJson(int size)
        {
            const string prefix = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"x\",\"pad\":\"";
            const string suffix = "\"}";
            int pad = size - prefix.Length - suffix.Length;
            return prefix + new string('a', pad) + suffix;
        }
    }
}
