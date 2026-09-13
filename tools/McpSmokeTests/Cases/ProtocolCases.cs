using System;
using System.Collections.Generic;
using ScriptDebugEngine.Mcp;

namespace McpSmokeTests
{
    /// <summary>C 组：JSON-RPC / MCP 协议层。</summary>
    internal static class ProtocolCases
    {
        public static void Register(Runner runner)
        {
            int port = runner.Env.Port;

            runner.Case("C-01", "initialize 已知协议版本回显", () =>
            {
                foreach (string version in new[] { "2024-11-05", "2025-03-26", "2025-06-18" })
                {
                    string response = runner.Rpc(Initialize(1, version));
                    runner.Expect("回显 " + version, ResultString(response, "protocolVersion"), version);
                    runner.Check(ResultObject(response, "capabilities") != null, "应带 capabilities: " + Runner.ResultJson(response));
                    runner.Check(ResultObject(response, "serverInfo") != null, "应带 serverInfo: " + Runner.ResultJson(response));
                }
            });

            runner.Case("C-02", "initialize 未知版本 / 缺 params / 空 params", () =>
            {
                runner.Expect("未知版本回退", ResultString(runner.Rpc(Initialize(2, "1999-01-01")), "protocolVersion"), "2025-03-26");
                runner.Expect("缺 params 回退", ResultString(runner.Rpc("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"initialize\"}"), "protocolVersion"), "2025-03-26");
                runner.Expect("空 params 回退", ResultString(runner.Rpc("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"initialize\",\"params\":{}}"), "protocolVersion"), "2025-03-26");
            });

            runner.Case("C-03", "initialize 作为通知（无 id）→ 202 空体", () =>
            {
                string response = RawClient.Post(port, "{\"jsonrpc\":\"2.0\",\"method\":\"initialize\",\"params\":{}}");
                runner.ExpectStatus("通知型 initialize", response, 202);
                runner.Check(string.IsNullOrEmpty(RawClient.Body(response)), "202 应无响应体，实际: " + Runner.Describe(RawClient.Body(response)));
            });

            runner.Case("C-04", "notifications/initialized → 202 空体", () =>
            {
                string response = RawClient.Post(port, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
                runner.ExpectStatus("initialized 通知", response, 202);
                runner.Check(string.IsNullOrEmpty(RawClient.Body(response)), "202 应无响应体，实际: " + Runner.Describe(RawClient.Body(response)));
            });

            runner.Case("C-05", "未知通知（无 id）→ 202 空体", () =>
            {
                string response = RawClient.Post(port, "{\"jsonrpc\":\"2.0\",\"method\":\"foo/bar\"}");
                runner.ExpectStatus("未知通知", response, 202);
            });

            runner.Case("C-06", "notifications/initialized 带 id（错误用法）", () =>
            {
                string response = RawClient.Post(port, "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"notifications/initialized\"}");
                runner.Check(Runner.ErrorCode(response) == -32601, "应回 -32601（不当作通知），实际: " + Runner.ResultJson(response));
            });

            runner.Case("C-07", "未知方法 → -32601", () =>
            {
                string response = runner.Rpc("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"some/unknown\"}");
                runner.Check(Runner.ErrorCode(response) == -32601, "应为 -32601，实际: " + Runner.ResultJson(response));
            });

            runner.Case("C-08", "缺 method → -32600", () =>
            {
                string response = runner.Rpc("{\"jsonrpc\":\"2.0\",\"id\":1}");
                runner.Check(Runner.ErrorCode(response) == -32600, "应为 -32600，实际: " + Runner.ResultJson(response));
            });

            runner.Case("C-09", "缺 jsonrpc 字段（宽松处理）", () =>
            {
                string response = runner.Rpc("{\"id\":1,\"method\":\"x\"}");
                runner.Check(Runner.ErrorCode(response) == -32601, "应被当作普通请求处理（-32601），实际: " + Runner.ResultJson(response));
                runner.Note("当前不校验 jsonrpc 字段（MCP 客户端都会带）");
            });

            runner.Case("C-10", "顶层 batch 数组 → -32600", () =>
            {
                string response = runner.Rpc("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}]");
                runner.Check(Runner.ErrorCode(response) == -32600, "batch 应为 -32600，实际: " + Runner.ResultJson(response));
                runner.Note("MCP 2025-03-26 已移除 batch，属预期");
            });

            runner.Case("C-11", "ping", () =>
            {
                string response = runner.Rpc("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}");
                runner.Check(ResultObject(response, null) != null, "ping 应返回（空）对象结果，实际: " + Runner.ResultJson(response));
            });

            runner.Case("C-12", "tools/list 结构与英文描述", () =>
            {
                string response = runner.Rpc("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}");
                IDictionary<string, object> tool = FirstTool(response);
                runner.Check(tool != null, "应返回 1 个工具，实际: " + Runner.ResultJson(response));
                if (tool == null) return;

                runner.Expect("工具名", Json.GetString(tool, "name"), "invoke_method");
                string description = Json.GetString(tool, "description");
                runner.Check(description != null && description.IndexOf("Hot-loads", StringComparison.Ordinal) >= 0, "描述应为英文: " + description);
                runner.Check(!HasChinese(description), "描述不应含中文: " + description);

                IDictionary<string, object> schema = Json.GetObject(tool, "inputSchema");
                IDictionary<string, object> properties = Json.GetObject(schema, "properties");
                runner.Check(properties != null && properties.Count == 3, "应有 3 个参数: " + Runner.ResultJson(response));
                var required = schema != null && schema.ContainsKey("required") ? schema["required"] as List<object> : null;
                runner.Check(required != null && required.Count == 3, "required 应为 3 项: " + Runner.ResultJson(response));
            });

            runner.Case("C-13", "未知工具名 / 缺 name → -32602", () =>
            {
                runner.Check(Runner.ErrorCode(runner.Rpc("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"nope\",\"arguments\":{}}}")) == -32602,
                    "未知工具应为 -32602");
                runner.Check(Runner.ErrorCode(runner.Rpc("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{}}")) == -32602,
                    "缺 name 应为 -32602");
            });

            runner.Case("C-14", "tools/call 缺 arguments / 缺参数 → isError", () =>
            {
                string noArguments = runner.Rpc("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"invoke_method\"}}");
                runner.Check(Runner.IsError(noArguments), "缺 arguments 应 isError: " + Runner.ResultJson(noArguments));
                runner.ExpectContains("提示", Runner.Text(noArguments), "are all required");

                string missingOne = runner.Rpc("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"invoke_method\",\"arguments\":{\"dllPath\":\"scripts\\\\Good.dll\"}}}");
                runner.Check(Runner.IsError(missingOne), "缺 typeName/methodName 应 isError: " + Runner.ResultJson(missingOne));
            });

            runner.Case("C-15", "arguments 传非字符串类型", () =>
            {
                string response = runner.Rpc("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"invoke_method\",\"arguments\":{\"dllPath\":123,\"typeName\":\"T\",\"methodName\":\"M\"}}}");
                runner.Check(Runner.IsError(response), "非字符串 dllPath 应 isError: " + Runner.ResultJson(response));
            });

            runner.Case("C-16", "id 为 null / 字符串 / 浮点原样回显", () =>
            {
                runner.ExpectContains("null id", RawClient.Body(RawClient.Post(port, "{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"ping\"}")), "\"id\":null");
                runner.ExpectContains("字符串 id", RawClient.Body(RawClient.Post(port, "{\"jsonrpc\":\"2.0\",\"id\":\"abc\",\"method\":\"ping\"}")), "\"id\":\"abc\"");
                runner.ExpectContains("浮点 id", RawClient.Body(RawClient.Post(port, "{\"jsonrpc\":\"2.0\",\"id\":2.5,\"method\":\"ping\"}")), "\"id\":2.5");
            });

            runner.Case("C-17", "Accept 只接受 text/event-stream", () =>
            {
                string response = RawClient.SendText(port,
                    RawPost("Accept: text/event-stream\r\n", "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}"), 8000);
                runner.ExpectStatus("只吃 SSE 的客户端", response, 200);
                runner.ExpectContains("仍回 JSON", response, "application/json");
                runner.Note("不实现 SSE（MCP 规范要求客户端两种都接受），已记录为已知取舍");
            });

            runner.Case("C-18", "Content-Type 非 application/json（宽松）", () =>
            {
                string response = RawClient.SendText(port,
                    RawPost("Content-Type: text/plain\r\n", "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}"), 8000);
                runner.ExpectStatus("text/plain body", response, 200);
            });

            runner.Case("C-19", "/health", () =>
            {
                string response = RawClient.Get(port, "/health");
                runner.ExpectStatus("health", response, 200);
                runner.ExpectContains("status", response, "\"status\":\"ok\"");
                runner.ExpectContains("server", response, "script-debug-engine");
            });

            runner.Case("C-20", "未知路径 → 404", () =>
            {
                runner.ExpectStatus("GET /nope", RawClient.Get(port, "/nope"), 404);
                runner.ExpectStatus("POST /nope", RawClient.SendText(port, RawPost(string.Empty, "{}", "/nope"), 8000), 404);
            });
        }

        /// <summary>拼一个长度正确的原始 POST 请求。</summary>
        private static string RawPost(string extraHeaders, string body, string path = "/mcp")
        {
            int length = System.Text.Encoding.UTF8.GetByteCount(body);
            return "POST " + path + " HTTP/1.1\r\nHost: x\r\n" + extraHeaders + "Content-Length: " + length + "\r\nConnection: close\r\n\r\n" + body;
        }

        private static string Initialize(int id, string protocolVersion)
        {
            return "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"initialize\",\"params\":{\"protocolVersion\":"
                + Json.Serialize(protocolVersion) + ",\"capabilities\":{},\"clientInfo\":{\"name\":\"smoke\",\"version\":\"1\"}}}";
        }

        private static IDictionary<string, object> Result(string response)
        {
            return Json.GetObject(Runner.ParseResponse(response), "result");
        }

        private static IDictionary<string, object> ResultObject(string response, string key)
        {
            IDictionary<string, object> result = Result(response);
            return key == null ? result : Json.GetObject(result, key);
        }

        private static string ResultString(string response, string key)
        {
            return Json.GetString(Result(response), key);
        }

        private static IDictionary<string, object> FirstTool(string response)
        {
            IDictionary<string, object> result = Result(response);
            var tools = result != null && result.ContainsKey("tools") ? result["tools"] as List<object> : null;
            return tools != null && tools.Count > 0 ? tools[0] as IDictionary<string, object> : null;
        }

        private static bool HasChinese(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
            {
                if (c >= '\u4e00' && c <= '\u9fff') return true;
            }
            return false;
        }
    }
}
