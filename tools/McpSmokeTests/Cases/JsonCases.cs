using System;
using System.Collections.Generic;
using System.Globalization;

namespace McpSmokeTests
{
    /// <summary>B 组：JSON 解析层的畸形与边界输入。</summary>
    internal static class JsonCases
    {
        public static void Register(Runner runner)
        {
            int port = runner.Env.Port;

            runner.Case("B-01", "深层嵌套 JSON（P0：曾会 StackOverflow 杀进程）", () =>
            {
                string body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"x\",\"pad\":" + new string('[', 60000);
                string response = RawClient.Post(port, body, 15000);
                runner.ExpectStatus("深嵌套", response, 200);
                runner.ExpectCode("深嵌套", response, -32700);
                runner.ExpectContains("错误信息", Runner.ResultJson(response), "nesting is too deep");
            });

            runner.Case("B-02", "空 body", () =>
            {
                string response = RawClient.Post(port, string.Empty);
                runner.ExpectStatus("空 body", response, 200);
                runner.Check(Runner.ErrorCode(response) == -32700, "应为 -32700，实际: " + Runner.ResultJson(response));
            });

            runner.Case("B-03", "截断 JSON", () =>
            {
                string response = RawClient.Post(port, "{\"jsonrpc\":\"2.0\"");
                runner.Check(Runner.ErrorCode(response) == -32700, "应为 -32700，实际: " + Runner.ResultJson(response));
            });

            runner.Case("B-04", "尾部多余内容", () =>
            {
                string response = RawClient.Post(port, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"x\"} garbage");
                runner.Check(Runner.ErrorCode(response) == -32700, "应为 -32700，实际: " + Runner.ResultJson(response));
            });

            runner.Case("B-05", "顶层不是对象", () =>
            {
                foreach (string body in new[] { "[]", "\"x\"", "1", "null", "true" })
                {
                    string response = RawClient.Post(port, body);
                    runner.Check(Runner.ErrorCode(response) == -32600, "body=" + body + " 应为 -32600，实际: " + Runner.ResultJson(response));
                }
            });

            runner.Case("B-06", "非法 \\u 转义", () =>
            {
                string response = RawClient.Post(port, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"x\",\"v\":\"\\uZZZZ\"}");
                runner.Check(Runner.ErrorCode(response) == -32700, "应为 -32700，实际: " + Runner.ResultJson(response));
            });

            runner.Case("B-07", "孤立代理对（\\uD800）", () =>
            {
                string inValue = RawClient.Post(port, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"x\",\"v\":\"\\uD800\"}");
                runner.Check(Runner.ErrorCode(inValue) == -32601 || Runner.ErrorCode(inValue) == -32700,
                    "应被接受(-32601)或拒绝(-32700)，实际: " + Runner.ResultJson(inValue));
                runner.Note("值里的孤立代理对: code=" + Runner.ErrorCode(inValue));

                // id 里的孤立代理对会被原样回显，验证回显阶段不会崩
                string inId = RawClient.Post(port, "{\"jsonrpc\":\"2.0\",\"id\":\"\\uD800\",\"method\":\"x\"}");
                runner.Check(RawClient.StatusCode(inId) == 200, "id 含孤立代理对时应仍能回响应: " + Runner.Describe(inId));
                runner.Note("id 里的孤立代理对: status=" + RawClient.StatusCode(inId));
            });

            runner.Case("B-08", "裸控制字符（未转义）", () =>
            {
                string response = RawClient.Post(port, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"x\",\"v\":\"a\u0001b\"}");
                long code = Runner.ErrorCode(response);
                runner.Check(code == -32601 || code == -32700, "应被接受(-32601)或拒绝(-32700)，实际: " + Runner.ResultJson(response));
                runner.Note("裸控制字符: code=" + code + "（当前解析器宽松，未强制校验）");
            });

            runner.Case("B-09", "63KB 超长字符串值", () =>
            {
                string body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"x\",\"pad\":\"" + new string('a', 63000) + "\"}";
                string response = RawClient.Post(port, body, 15000);
                runner.ExpectStatus("63KB 字符串", response, 200);
                runner.Check(Runner.ErrorCode(response) == -32601, "应正常解析为 -32601，实际: " + Runner.ResultJson(response));
            });

            runner.Case("B-10", "重复键取后者", () =>
            {
                string response = RawClient.Post(port, "{\"jsonrpc\":\"2.0\",\"id\":1,\"id\":2,\"method\":\"x\"}");
                runner.Expect("重复 id 回显", IdText(response), "2");
            });

            runner.Case("B-11", "数字边界 id", () =>
            {
                runner.Expect("long id 不失真", IdText(RawClient.Post(port, "{\"jsonrpc\":\"2.0\",\"id\":9007199254740993,\"method\":\"x\"}")), "9007199254740993");
                runner.Expect("浮点 id", IdText(RawClient.Post(port, "{\"jsonrpc\":\"2.0\",\"id\":1.5,\"method\":\"x\"}")), "1.5");
                string zero = IdText(RawClient.Post(port, "{\"jsonrpc\":\"2.0\",\"id\":-0,\"method\":\"x\"}"));
                runner.Check(zero == "0" || zero == "-0", "-0 行为记录: " + zero);
                runner.Note("-0 回显为 " + zero);
            });

            runner.Case("B-12", "中文 / emoji 往返", () =>
            {
                string unicode = runner.Invoke("scripts\\Good.dll", "Good.Probe", "Unicode");
                runner.Expect("中文+转义往返", Runner.Text(unicode), "中文与\\反斜杠 \"引号\" 换行\n第二行");

                string emoji = char.ConvertFromUtf32(0x1F600);
                string response = RawClient.Post(port, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"x\\uD83D\\uDE00\"}");
                runner.ExpectContains("emoji 转义解析", Runner.ResultJson(response), "x" + emoji);
            });

            runner.Case("B-13", "非 UTF-8 字节序列（GBK）", () =>
            {
                // "中文" 的 GBK 字节：D6 D0 CE C4
                byte[] gbk = { 0xD6, 0xD0, 0xCE, 0xC4 };
                var request = new List<byte>();
                request.AddRange(System.Text.Encoding.ASCII.GetBytes(
                    "POST /mcp HTTP/1.1\r\nHost: x\r\nContent-Type: application/json\r\nContent-Length: " + gbk.Length + "\r\nConnection: close\r\n\r\n"));
                request.AddRange(gbk);

                string response = RawClient.Send(port, request.ToArray(), 8000);
                runner.ExpectStatus("GBK body", response, 200);
                runner.Check(Runner.ErrorCode(response) == -32700, "非 UTF-8 应解析失败 -32700，实际: " + Runner.ResultJson(response));
            });
        }

        private static string IdText(string response)
        {
            IDictionary<string, object> root = Runner.ParseResponse(response);
            object id = root != null && root.ContainsKey("id") ? root["id"] : null;
            return id == null ? "(null)" : Convert.ToString(id, CultureInfo.InvariantCulture);
        }
    }
}
