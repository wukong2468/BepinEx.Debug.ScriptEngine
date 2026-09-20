using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ScriptDebugEngine.Mcp
{
    /// <summary>执行调用的宿主：插件里由 Unity 主线程的任务泵实现，测试时可同步直接调用。</summary>
    internal interface IInvocationHost
    {
        /// <summary>执行并返回结果文本；失败时抛异常。</summary>
        string Invoke(string dllPath, string typeName, string methodName);
    }

    /// <summary>带 JSON-RPC 错误码的异常。</summary>
    internal sealed class RpcException : Exception
    {
        public RpcException(int code, string message) : base(message)
        {
            Code = code;
        }

        public int Code { get; private set; }
    }

    /// <summary>
    /// 极简 MCP（Streamable HTTP）服务器：只暴露 invoke_method 一个工具。
    /// 只绑 127.0.0.1，响应后关闭连接（Connection: close）；不依赖 UnityEngine / BepInEx。
    /// </summary>
    internal sealed class McpServer
    {
        private const int MaxHeaderBytes = 8192;
        private const int MaxBodyBytes = 65536;

        // 读请求的时限：头部/正文分别限时，避免慢速客户端长期占用连接线程（只有单次 Read 超时是不够的）
        private const int HeaderReadTimeoutMs = 5000;
        private const int BodyReadTimeoutMs = 10000;
        private const int DrainTimeoutMs = 2000;

        private const string DefaultProtocolVersion = "2025-03-26";
        private const string InvokeMethodToolName = "invoke_method";

        /// <summary>
        /// 只声明真正支持的协议版本。2024-11-05 走的是旧的 HTTP+SSE 传输（GET 打开 SSE 流），
        /// 与本服务器的 Streamable HTTP 端点不兼容，故不再声明 —— 否则属于误导性协商。
        /// </summary>
        private static readonly string[] SupportedProtocolVersions = { "2025-03-26", "2025-06-18" };

        /// <summary>
        /// DNS rebinding 防护允许的 Origin 主机名。浏览器发起的请求一定带 Origin，
        /// 且必须是回环来源；非浏览器的 MCP 客户端不发 Origin（缺席视为放行）。
        /// </summary>
        private static readonly string[] AllowedOriginHosts = { "127.0.0.1", "localhost", "::1", "[::1]" };

        private readonly int port;
        private readonly string serverVersion;
        private readonly IInvocationHost host;
        private readonly Action<string> logInfo;
        private readonly Action<string, Exception> logError;

        private TcpListener listener;
        private Thread acceptThread;
        private volatile bool running;

        public McpServer(int port, string serverVersion, IInvocationHost host, Action<string> logInfo, Action<string, Exception> logError)
        {
            this.port = port;
            this.serverVersion = serverVersion ?? "0";
            this.host = host;
            this.logInfo = logInfo ?? delegate { };
            this.logError = logError ?? delegate { };
        }

        public void Start()
        {
            if (port <= 0 || port > 65535)
                throw new ArgumentOutOfRangeException("port", port, "Port must be between 1 and 65535.");

            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Start();

            running = true;
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "ScriptDebugEngine.Mcp" };
            acceptThread.Start();

            // 用真实绑定端口打日志，避免配置值与实际不一致时误导排查
            logInfo(string.Format("MCP server listening on http://127.0.0.1:{0}/mcp", ((IPEndPoint)listener.LocalEndpoint).Port));
        }

        public void Stop()
        {
            running = false;

            TcpListener current = listener;
            listener = null;
            if (current != null)
            {
                try { current.Stop(); }
                catch (Exception exception) { logError("Failed to stop the MCP listener", exception); }
            }

            Thread thread = acceptThread;
            acceptThread = null;
            if (thread != null && !thread.Join(1000)) logInfo("MCP accept thread did not exit within 1 second");
        }

        // ---------------- 接受连接 ----------------

        private void AcceptLoop()
        {
            while (running)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (SocketException) { if (!running) return; continue; }
                catch (ObjectDisposedException) { return; }
                catch (InvalidOperationException) { return; }

                TcpClient captured = client;
                ThreadPool.QueueUserWorkItem(HandleConnectionSafe, captured);
            }
        }

        private void HandleConnectionSafe(object state)
        {
            var client = (TcpClient)state;
            try
            {
                HandleConnection(client);
            }
            catch (IOException)
            {
                // 客户端中途断开/写响应失败：属于正常现象，不打错误日志（否则畸形客户端就能刷屏）
            }
            catch (SocketException)
            {
                // 同上
            }
            catch (ObjectDisposedException)
            {
                // 服务器正在停止时在途连接被关闭
            }
            catch (Exception exception)
            {
                logError("Failed to handle an MCP connection", exception);
            }
            finally
            {
                try { client.Close(); }
                catch (Exception) { }
            }
        }

        private void HandleConnection(TcpClient client)
        {
            // 每次 Read 之前会按剩余时限重设 ReceiveTimeout（见 TryReadHeader / ReadBody / DrainBody）
            client.ReceiveTimeout = HeaderReadTimeoutMs;
            client.SendTimeout = HeaderReadTimeoutMs;

            using (NetworkStream stream = client.GetStream())
            {
                string header;
                byte[] leftover;
                int leftoverLength;
                HeaderReadResult headerResult = TryReadHeader(client, stream, out header, out leftover, out leftoverLength);
                if (headerResult == HeaderReadResult.Empty) return; // 客户端什么都没发就断开
                if (headerResult == HeaderReadResult.TooLarge)
                {
                    WriteText(stream, 431, "Request Header Fields Too Large", "Request headers are too large.");
                    return;
                }
                if (headerResult == HeaderReadResult.TimedOut)
                {
                    WriteText(stream, 408, "Request Timeout", "Request headers were not received in time.");
                    return;
                }
                if (headerResult != HeaderReadResult.Ok)
                {
                    WriteText(stream, 400, "Bad Request", "Malformed request.");
                    return;
                }

                string[] lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
                string[] requestLine = lines[0].Split(' ');
                if (requestLine.Length < 2)
                {
                    WriteText(stream, 400, "Bad Request", "Malformed request line.");
                    return;
                }

                string httpMethod = requestLine[0];
                string path = requestLine[1];
                int queryIndex = path.IndexOf('?');
                if (queryIndex >= 0) path = path.Substring(0, queryIndex);

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++)
                {
                    if (lines[i].Length == 0) continue;
                    int separator = lines[i].IndexOf(':');
                    if (separator <= 0) continue;

                    string name = lines[i].Substring(0, separator).Trim();
                    string value = lines[i].Substring(separator + 1).Trim();

                    // 影响请求体语义的头部必须唯一：重复的 Content-Length / Transfer-Encoding 是典型的请求走私手法
                    bool ambiguous = string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase);
                    if (ambiguous && headers.ContainsKey(name))
                    {
                        WriteText(stream, 400, "Bad Request", "Duplicate " + name + " header is not allowed.");
                        return;
                    }

                    headers[name] = value;
                }

                if (headers.ContainsKey("Content-Length") && headers.ContainsKey("Transfer-Encoding"))
                {
                    WriteText(stream, 400, "Bad Request", "Both Content-Length and Transfer-Encoding are present.");
                    return;
                }

                // 规范要求服务端必须校验 Origin：仅绑回环挡不住 DNS rebinding
                // （恶意网页把自己的域名解析到 127.0.0.1，浏览器就会带上它自己的 Origin 来访问本机服务）
                string origin;
                if (headers.TryGetValue("Origin", out origin) && !IsAllowedOrigin(origin))
                {
                    WriteText(stream, 403, "Forbidden", "Cross-origin requests are not allowed.");
                    return;
                }

                // 部分客户端（例如 .NET 的 HttpWebRequest）会先声明 Expect: 100-continue 并等应答，必须先回 100 再读正文
                string expect;
                if (headers.TryGetValue("Expect", out expect) && expect.IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    byte[] continueBytes = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                    stream.Write(continueBytes, 0, continueBytes.Length);
                    stream.Flush();
                }

                if (string.Equals(httpMethod, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(path, "/health", StringComparison.Ordinal))
                    {
                        WriteJson(stream, 200, Json.Serialize(new Dictionary<string, object>
                        {
                            { "status", "ok" },
                            { "server", "script-debug-engine" },
                            { "version", serverVersion }
                        }));
                    }
                    else if (string.Equals(path, "/mcp", StringComparison.Ordinal))
                    {
                        WriteText(stream, 405, "Method Not Allowed", "Use POST for the MCP endpoint.");
                    }
                    else
                    {
                        WriteText(stream, 404, "Not Found", "Not found.");
                    }
                    return;
                }

                if (string.Equals(httpMethod, "DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    // 本服务器不做协议级 session，规范允许用 405 表示"不允许客户端主动终止 session"
                    if (string.Equals(path, "/mcp", StringComparison.Ordinal))
                        WriteText(stream, 405, "Method Not Allowed", "This server has no protocol sessions, so DELETE is not allowed.");
                    else
                        WriteText(stream, 404, "Not Found", "Not found.");
                    return;
                }

                if (!string.Equals(httpMethod, "POST", StringComparison.OrdinalIgnoreCase) || !string.Equals(path, "/mcp", StringComparison.Ordinal))
                {
                    WriteText(stream, 404, "Not Found", "Not found.");
                    return;
                }

                // MCP-Protocol-Version：2025-06-18 起客户端必须携带，服务器收到不支持/无效的版本必须回 400。
                // 缺头时不报错（按 2025-03-26 兼容处理），这样只有真正声明了不支持版本的客户端才会被拒。
                string declaredVersion;
                if (headers.TryGetValue("MCP-Protocol-Version", out declaredVersion)
                    && Array.IndexOf(SupportedProtocolVersions, declaredVersion) < 0)
                {
                    WriteText(stream, 400, "Bad Request", "Unsupported MCP-Protocol-Version: " + declaredVersion
                        + ". Supported versions: " + string.Join(", ", SupportedProtocolVersions) + ".");
                    return;
                }

                int contentLength;
                string headerValue;
                if (headers.TryGetValue("Content-Length", out headerValue))
                {
                    if (!int.TryParse(headerValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength) || contentLength < 0)
                    {
                        WriteText(stream, 400, "Bad Request", "Invalid Content-Length.");
                        return;
                    }
                }
                else if (headers.ContainsKey("Transfer-Encoding"))
                {
                    WriteText(stream, 411, "Length Required", "Chunked request bodies are not supported.");
                    return;
                }
                else
                {
                    contentLength = 0;
                }

                if (contentLength > MaxBodyBytes)
                {
                    // 先把正文读完再回错误：否则客户端可能因为连接被重置而看不到这个 413
                    DrainBody(client, stream, contentLength);
                    WriteText(stream, 413, "Payload Too Large", "Request body is too large.");
                    return;
                }

                string body;
                if (contentLength == 0)
                {
                    body = string.Empty;
                }
                else
                {
                    try
                    {
                        client.ReceiveTimeout = BodyReadTimeoutMs;
                        body = Encoding.UTF8.GetString(ReadBody(stream, leftover, leftoverLength, contentLength));
                    }
                    catch (IOException)
                    {
                        // 正文没在时限内读完（或客户端提前断开）：回 408，不当作服务器内部错误
                        WriteText(stream, 408, "Request Timeout", "Request body was not received in time.");
                        return;
                    }
                }

                int statusCode;
                string responseBody = HandleRpc(body, out statusCode);
                if (responseBody == null) WriteEmpty(stream, statusCode);
                else WriteJson(stream, statusCode, responseBody);
            }
        }

        // ---------------- HTTP 读写 ----------------

        private enum HeaderReadResult
        {
            Ok,
            Empty,
            Malformed,
            TooLarge,
            TimedOut
        }

        private static HeaderReadResult TryReadHeader(TcpClient client, NetworkStream stream, out string header, out byte[] leftover, out int leftoverLength)
        {
            header = null;
            leftover = new byte[MaxHeaderBytes];
            leftoverLength = 0;

            var elapsed = Stopwatch.StartNew();
            var accumulated = new MemoryStream();
            var buffer = new byte[1024];
            bool receivedAny = false;

            while (true)
            {
                int remaining = HeaderReadTimeoutMs - (int)elapsed.ElapsedMilliseconds;
                if (remaining <= 0) return receivedAny ? HeaderReadResult.TimedOut : HeaderReadResult.Empty;

                client.ReceiveTimeout = remaining;

                int read;
                try
                {
                    read = stream.Read(buffer, 0, buffer.Length);
                }
                catch (IOException)
                {
                    return receivedAny ? HeaderReadResult.TimedOut : HeaderReadResult.Empty;
                }

                if (read <= 0) return receivedAny ? HeaderReadResult.Malformed : HeaderReadResult.Empty;
                receivedAny = true;

                accumulated.Write(buffer, 0, read);
                byte[] data = accumulated.GetBuffer();
                int length = (int)accumulated.Length;

                int end = IndexOfHeaderEnd(data, length);
                if (end >= 0)
                {
                    header = Encoding.ASCII.GetString(data, 0, end);

                    // 头部与正文通常同批到达，剩下的字节要留给正文
                    int bodyStart = end + 4;
                    int rest = length - bodyStart;
                    if (rest > 0)
                    {
                        if (rest > leftover.Length) rest = leftover.Length;
                        Buffer.BlockCopy(data, bodyStart, leftover, 0, rest);
                        leftoverLength = rest;
                    }
                    return HeaderReadResult.Ok;
                }

                if (length >= MaxHeaderBytes) return HeaderReadResult.TooLarge;
            }
        }

        private static int IndexOfHeaderEnd(byte[] data, int length)
        {
            for (int i = 0; i + 3 < length; i++)
            {
                if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10) return i;
            }
            return -1;
        }

        /// <summary>丢弃超限的请求正文（有上限、有时限、忽略错误），以便客户端能读到我们回的 413。</summary>
        private static void DrainBody(TcpClient client, NetworkStream stream, int contentLength)
        {
            const int drainLimit = 1024 * 1024;
            var scratch = new byte[8192];
            int remaining = Math.Min(contentLength, drainLimit);
            var elapsed = Stopwatch.StartNew();

            try
            {
                while (remaining > 0)
                {
                    int timeout = DrainTimeoutMs - (int)elapsed.ElapsedMilliseconds;
                    if (timeout <= 0) return;
                    client.ReceiveTimeout = timeout;

                    int read = stream.Read(scratch, 0, Math.Min(scratch.Length, remaining));
                    if (read <= 0) return;
                    remaining -= read;
                }
            }
            catch (Exception)
            {
                // 正文本身不重要，读失败就直接回错误
            }
        }

        private static byte[] ReadBody(NetworkStream stream, byte[] leftover, int leftoverLength, int contentLength)
        {
            var body = new byte[contentLength];

            int copied = Math.Min(leftoverLength, contentLength);
            if (copied > 0) Buffer.BlockCopy(leftover, 0, body, 0, copied);

            int offset = copied;
            while (offset < contentLength)
            {
                int read = stream.Read(body, offset, contentLength - offset);
                if (read <= 0) throw new IOException("Unexpected end of the request body.");
                offset += read;
            }

            return body;
        }

        private static void WriteJson(NetworkStream stream, int statusCode, string json)
        {
            WriteResponse(stream, statusCode, ReasonPhrase(statusCode), "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
        }

        private static void WriteText(NetworkStream stream, int statusCode, string reason, string text)
        {
            WriteResponse(stream, statusCode, reason, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));
        }

        private static void WriteEmpty(NetworkStream stream, int statusCode)
        {
            WriteResponse(stream, statusCode, ReasonPhrase(statusCode), null, null);
        }

        private static void WriteResponse(NetworkStream stream, int statusCode, string reason, string contentType, byte[] body)
        {
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(statusCode).Append(' ').Append(reason).Append("\r\n");
            if (contentType != null) head.Append("Content-Type: ").Append(contentType).Append("\r\n");
            head.Append("Content-Length: ").Append(body == null ? 0 : body.Length).Append("\r\n");
            head.Append("Connection: close\r\n\r\n");

            byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            if (body != null && body.Length > 0) stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static string ReasonPhrase(int statusCode)
        {
            switch (statusCode)
            {
                case 200: return "OK";
                case 202: return "Accepted";
                case 400: return "Bad Request";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 408: return "Request Timeout";
                case 411: return "Length Required";
                case 413: return "Payload Too Large";
                case 431: return "Request Header Fields Too Large";
                default: return "OK";
            }
        }

        /// <summary>
        /// 判断 Origin 头是否来自本机回环。缺席（null/空）视为放行：非浏览器的 MCP 客户端本来就不发这个头。
        /// "null" 这个字面量（file:// 或沙箱 iframe 的来源）**不放行** —— 沙箱同样能被攻击者利用。
        /// </summary>
        private static bool IsAllowedOrigin(string origin)
        {
            if (string.IsNullOrEmpty(origin)) return true;

            int schemeSeparator = origin.IndexOf("://", StringComparison.Ordinal);
            if (schemeSeparator < 0) return false;

            string host = origin.Substring(schemeSeparator + 3);
            int pathStart = host.IndexOf('/');
            if (pathStart >= 0) host = host.Substring(0, pathStart);

            // 去掉端口：IPv6 字面量形如 [::1]:8765，要找最后一个 ']' 之后的冒号
            int bracket = host.LastIndexOf(']');
            int colon = host.LastIndexOf(':');
            if (colon > bracket) host = host.Substring(0, colon);

            host = host.Trim();
            return Array.IndexOf(AllowedOriginHosts, host.ToLowerInvariant()) >= 0;
        }

        // ---------------- JSON-RPC ----------------

        private string HandleRpc(string body, out int statusCode)
        {
            statusCode = 200;
            object id = null;

            try
            {
                object parsed;
                try
                {
                    parsed = Json.Parse(body);
                }
                catch (Exception exception)
                {
                    return ErrorResponse(null, -32700, "Parse error: " + exception.Message);
                }

                IDictionary<string, object> request = parsed as IDictionary<string, object>;
                if (request == null) return ErrorResponse(null, -32600, "Invalid Request: payload must be a JSON object.");

                bool hasId = request.ContainsKey("id");
                if (hasId) id = request["id"];

                if (!hasId)
                {
                    // JSON-RPC 通知（例如 notifications/initialized）：只回 202，无响应体
                    statusCode = 202;
                    return null;
                }

                string method = Json.GetString(request, "method");
                if (string.IsNullOrEmpty(method))
                {
                    // 没有 method 却带 result/error：这是一条 JSON-RPC **响应**。本服务器从不向客户端发起请求，
                    // 因此无法接受响应 —— 规范要求这类输入回 HTTP 错误状态码，而不是 200。
                    if (request.ContainsKey("result") || request.ContainsKey("error"))
                    {
                        statusCode = 400;
                        return ErrorResponse(id, -32600, "Invalid Request: this endpoint only accepts JSON-RPC requests and notifications.");
                    }

                    return ErrorResponse(id, -32600, "Invalid Request: 'method' is required.");
                }

                switch (method)
                {
                    case "initialize": return ResultResponse(id, Initialize(request));
                    case "ping": return ResultResponse(id, new Dictionary<string, object>());
                    case "tools/list": return ResultResponse(id, ToolsList());
                    case "tools/call": return ResultResponse(id, ToolsCall(request));
                    default: return ErrorResponse(id, -32601, "Method not found: " + method);
                }
            }
            catch (RpcException exception)
            {
                return ErrorResponse(id, exception.Code, exception.Message);
            }
            catch (Exception exception)
            {
                logError("Failed to handle an MCP request", exception);
                return ErrorResponse(id, -32603, "Internal error: " + exception.Message);
            }
        }

        private IDictionary<string, object> Initialize(IDictionary<string, object> request)
        {
            IDictionary<string, object> parameters = Json.GetObject(request, "params");
            string requested = Json.GetString(parameters, "protocolVersion");

            string protocolVersion = DefaultProtocolVersion;
            if (!string.IsNullOrEmpty(requested) && Array.IndexOf(SupportedProtocolVersions, requested) >= 0) protocolVersion = requested;

            return new Dictionary<string, object>
            {
                { "protocolVersion", protocolVersion },
                { "capabilities", new Dictionary<string, object> { { "tools", new Dictionary<string, object>() } } },
                { "serverInfo", new Dictionary<string, object> { { "name", "script-debug-engine" }, { "version", serverVersion } } }
            };
        }

        private static IDictionary<string, object> ToolsList()
        {
            var properties = new Dictionary<string, object>
            {
                { "dllPath", Property("string", "Path to the DLL: an absolute path, or a path relative to the BepInEx root (for example scripts\\Foo.dll).") },
                { "typeName", Property("string", "Type name, for example Foo.Bar. Either the full name or a suffix match is accepted.") },
                { "methodName", Property("string", "Name of a public static method that takes no parameters.") }
            };

            var tool = new Dictionary<string, object>
            {
                { "name", InvokeMethodToolName },
                { "description", "Hot-loads the given DLL and runs one of its public static parameterless methods, then returns the execution result. If you are not sure about the method name, pass any name: the error message lists the available parameterless static methods of that type." },
                { "inputSchema", new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", properties },
                        { "required", new List<object> { "dllPath", "typeName", "methodName" } }
                    }
                }
            };

            return new Dictionary<string, object> { { "tools", new List<object> { tool } } };
        }

        private static IDictionary<string, object> Property(string type, string description)
        {
            return new Dictionary<string, object> { { "type", type }, { "description", description } };
        }

        private IDictionary<string, object> ToolsCall(IDictionary<string, object> request)
        {
            IDictionary<string, object> parameters = Json.GetObject(request, "params");
            string toolName = Json.GetString(parameters, "name");
            if (!string.Equals(toolName, InvokeMethodToolName, StringComparison.Ordinal))
                throw new RpcException(-32602, "Unknown tool: " + (toolName ?? "(missing)"));

            IDictionary<string, object> arguments = Json.GetObject(parameters, "arguments");
            string dllPath = Json.GetString(arguments, "dllPath");
            string typeName = Json.GetString(arguments, "typeName");
            string methodName = Json.GetString(arguments, "methodName");

            if (string.IsNullOrEmpty(dllPath) || string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(methodName))
                return ToolResult("Missing required argument: dllPath, typeName and methodName are all required.", true);

            try
            {
                string result = host.Invoke(dllPath, typeName, methodName);
                return ToolResult(result ?? "null", false);
            }
            catch (Exception exception)
            {
                string text = FormatError(exception);
                // 失败也要留痕：否则 agent 侧只看到一句错误、日志里什么都没有，无从排查
                logError("invoke_method failed for " + dllPath + " / " + typeName + "." + methodName + " -> " + text, null);
                return ToolResult(text, true);
            }
        }

        private static IDictionary<string, object> ToolResult(string text, bool isError)
        {
            return new Dictionary<string, object>
            {
                { "content", new List<object> { new Dictionary<string, object> { { "type", "text" }, { "text", text } } } },
                { "isError", isError }
            };
        }

        private static string FormatError(Exception exception)
        {
            if (exception == null) return "Unknown error.";

            // InvocationFailedException 的 Message 已经是拼好的"类型: 消息 + 堆栈"
            string text = exception is InvocationFailedException
                ? exception.Message
                : exception.GetType().Name + ": " + exception.Message;

            // 兜底：任何情况下都不要把空文本回给 agent
            return string.IsNullOrEmpty(text) ? exception.GetType().FullName : text;
        }

        private static string ResultResponse(object id, object result)
        {
            return Json.Serialize(new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "result", result }
            });
        }

        private static string ErrorResponse(object id, int code, string message)
        {
            return Json.Serialize(new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "error", new Dictionary<string, object> { { "code", (long)code }, { "message", message } } }
            });
        }
    }
}
