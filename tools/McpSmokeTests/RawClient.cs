using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace McpSmokeTests
{
    /// <summary>
    /// 裸 socket 客户端：用于发送畸形/超限/半包/慢速请求，并读回原始响应。
    /// 全部同步实现，方便断言"服务器是否还能正常响应"。
    /// </summary>
    internal static class RawClient
    {
        /// <summary>发送原始字节并读到连接关闭；返回原始响应文本（连接被拒/超时/被重置时返回已读到的部分）。</summary>
        public static string Send(int port, byte[] request, int readTimeoutMs = 8000, bool shutdownSend = false, int lingerSeconds = -1)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    client.Connect(IPAddress.Loopback, port);
                    client.ReceiveTimeout = readTimeoutMs;
                    client.SendTimeout = readTimeoutMs;
                    if (lingerSeconds >= 0) client.LingerState = new LingerOption(true, lingerSeconds);

                    using (NetworkStream stream = client.GetStream())
                    {
                        stream.Write(request, 0, request.Length);
                        stream.Flush();
                        if (shutdownSend) client.Client.Shutdown(SocketShutdown.Send);

                        var accumulated = new MemoryStream();
                        var buffer = new byte[4096];
                        try
                        {
                            while (true)
                            {
                                int read = stream.Read(buffer, 0, buffer.Length);
                                if (read <= 0) break;
                                accumulated.Write(buffer, 0, read);
                            }
                        }
                        catch (IOException)
                        {
                            // 读超时或连接被重置：返回已读到的部分，交给断言判断
                        }
                        catch (SocketException)
                        {
                        }

                        return Encoding.UTF8.GetString(accumulated.ToArray());
                    }
                }
            }
            catch (SocketException)
            {
                // 端口没有监听（例如服务器已停止）：返回空字符串，由 StatusCode() == 0 判定
                return string.Empty;
            }
        }

        public static string SendText(int port, string request, int readTimeoutMs = 8000, bool shutdownSend = false)
        {
            return Send(port, Encoding.ASCII.GetBytes(request), readTimeoutMs, shutdownSend);
        }

        /// <summary>发完请求立刻用 RST 断开（不读响应），用于"客户端中途消失"的场景。</summary>
        public static void SendAbort(int port, byte[] request)
        {
            using (var client = new TcpClient())
            {
                client.Connect(IPAddress.Loopback, port);
                client.LingerState = new LingerOption(true, 0);
                using (NetworkStream stream = client.GetStream())
                {
                    stream.Write(request, 0, request.Length);
                    stream.Flush();
                }
            }
        }

        /// <summary>打开一个连接但不发完整请求（用于慢速攻击场景）；由调用方负责关闭。</summary>
        public static TcpClient OpenSlowConnection(int port, int bytesToSend, int readTimeoutMs = 1000)
        {
            var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            client.ReceiveTimeout = readTimeoutMs;
            using (NetworkStream stream = client.GetStream())
            {
                stream.Write(new byte[bytesToSend], 0, bytesToSend);
                stream.Flush();
            }
            return client;
        }

        public static string Get(int port, string path, int readTimeoutMs = 8000)
        {
            return SendText(port, "GET " + path + " HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n", readTimeoutMs, true);
        }

        public static string Post(int port, string json, int readTimeoutMs = 15000)
        {
            return Send(port, BuildPost(json), readTimeoutMs);
        }

        /// <summary>构造原始 POST 请求字节。</summary>
        public static byte[] BuildPost(string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            var head = new StringBuilder();
            head.Append("POST /mcp HTTP/1.1\r\n");
            head.Append("Host: 127.0.0.1\r\n");
            head.Append("Content-Type: application/json\r\n");
            head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            head.Append("Connection: close\r\n\r\n");

            byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
            var request = new byte[headBytes.Length + body.Length];
            Buffer.BlockCopy(headBytes, 0, request, 0, headBytes.Length);
            Buffer.BlockCopy(body, 0, request, headBytes.Length, body.Length);
            return request;
        }

        /// <summary>发送请求并返回**原始响应字节**（用于严格 UTF-8 校验与独立解析器解析）。</summary>
        public static byte[] PostBytes(int port, string json, int readTimeoutMs = 60000)
        {
            return SendBytes(port, BuildPost(json), readTimeoutMs);
        }

        public static byte[] SendBytes(int port, byte[] request, int readTimeoutMs = 15000)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    client.Connect(IPAddress.Loopback, port);
                    client.ReceiveTimeout = readTimeoutMs;
                    client.SendTimeout = readTimeoutMs;

                    using (NetworkStream stream = client.GetStream())
                    {
                        stream.Write(request, 0, request.Length);
                        stream.Flush();

                        var accumulated = new MemoryStream();
                        var buffer = new byte[8192];
                        try
                        {
                            while (true)
                            {
                                int read = stream.Read(buffer, 0, buffer.Length);
                                if (read <= 0) break;
                                accumulated.Write(buffer, 0, read);
                            }
                        }
                        catch (IOException)
                        {
                        }
                        catch (SocketException)
                        {
                        }

                        return accumulated.ToArray();
                    }
                }
            }
            catch (SocketException)
            {
                return new byte[0];
            }
        }

        public static int StatusCode(string response)
        {
            if (string.IsNullOrEmpty(response)) return 0;
            int space = response.IndexOf(' ');
            if (space < 0 || space + 4 > response.Length) return 0;
            int code;
            return int.TryParse(response.Substring(space + 1, 3), out code) ? code : 0;
        }

        public static string Body(string response)
        {
            if (string.IsNullOrEmpty(response)) return string.Empty;
            int index = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            return index < 0 ? string.Empty : response.Substring(index + 4);
        }

        public static byte[] RandomBytes(int count)
        {
            var random = new Random(20140913);
            var bytes = new byte[count];
            random.NextBytes(bytes);
            // 确保不含 CR/LF，避免偶然被当成合法头部分隔
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == 13 || bytes[i] == 10) bytes[i] = 0x41;
            }
            return bytes;
        }

        /// <summary>向系统要一个当前空闲的回环端口。</summary>
        public static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
