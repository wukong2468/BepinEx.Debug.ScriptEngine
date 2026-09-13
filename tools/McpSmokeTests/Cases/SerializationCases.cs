using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;

namespace McpSmokeTests
{
    /// <summary>
    /// J 组：返回值序列化 —— 目标函数返回的各种"奇怪字符"能否变成**合法且无损**的 JSON。
    ///
    /// 关键点：这里刻意**不用被测自己的 `Json` 类**去验证，而是
    ///   1) 对响应字节做**严格 UTF-8 解码**（非法序列直接抛异常）;
    ///   2) 校验 `Content-Length` 等于正文字节数（多字节字符最容易在这里出错）;
    ///   3) 用**独立解析器 Newtonsoft.Json** 解析成 JSON 对象;
    ///   4) 与"直接反射调用同一个方法"拿到的原始字符串**逐字符比较**。
    /// </summary>
    internal static class SerializationCases
    {
        private const string EvilDll = "scripts\\Evil.dll";
        private const string EvilType = "Evil.Strings";

        private static Assembly evilAssembly;

        public static void Register(Runner runner)
        {
            RoundTrip(runner, "J-01", "全部 0x00-0x1F 控制字符", "Controls");
            RoundTrip(runner, "J-02", "引号 / 反斜杠 / 斜杠 / 括号", "QuotesAndSlashes");
            RoundTrip(runner, "J-03", "Unicode 空白 / 行分隔符 / BOM", "Whitespace");
            // 孤立代理无法用 UTF-8 表示，只能断言 wire 上的转义形式（见 WireEscape 里的客户端差异说明）
            WireEscape(runner, "J-04", "孤立高代理 \\uD800（wire 保留转义）", "LoneHighSurrogate", "\\ud800");
            WireEscape(runner, "J-05", "孤立低代理 \\uDFFF（wire 保留转义）", "LoneLowSurrogate", "\\udfff");
            RoundTrip(runner, "J-06", "合法代理对（emoji）", "SurrogatePair");
            RoundTrip(runner, "J-07", "非字符 U+FFFD/U+FFFE/U+FFFF", "NonCharacters");
            RoundTrip(runner, "J-08", "夹杂 NUL", "NullChar");
            RoundTrip(runner, "J-09", "空字符串（必须是空串而不是 null）", "Empty", expectEmpty: true);
            RoundTrip(runner, "J-10", "看起来像 JSON 的字符串", "JsonLooking");
            RoundTrip(runner, "J-11", "Windows 路径风格串", "PathLike");
            RoundTrip(runner, "J-12", "中文 / emoji / 代理混合", "UnicodeMix");

            runner.Case("J-13", "1MB 字符串返回值", () =>
            {
                byte[] raw = RawClient.PostBytes(runner.Env.Port, runner.InvokeJson(EvilDll, EvilType, "OneMegabyte"), 120000);
                CheckLength(runner, raw, 1024 * 1024);
            });

            runner.Case("J-14", "4MB 字符串返回值（当前无输出上限）", () =>
            {
                byte[] raw = RawClient.PostBytes(runner.Env.Port, runner.InvokeJson(EvilDll, EvilType, "FourMegabytes"), 180000);
                CheckLength(runner, raw, 4 * 1024 * 1024);
                runner.Note("无结果长度上限：大返回值整段进内存并原样回传（建议目标函数自己控制返回体量）");
            });

            runner.Case("J-15", "所有协议响应的响应体都能被独立解析器解析", () =>
            {
                string[] requests =
                {
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\"}}",
                    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}",
                    "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"ping\"}",
                    "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"nope\"}",
                    "{ bad json",
                    "[]"
                };

                foreach (string request in requests)
                {
                    byte[] raw = RawClient.PostBytes(runner.Env.Port, request);
                    string body = StrictBody(runner, raw);
                    if (body != null && ParseBody(runner, body) != null) runner.Note("独立解析 OK");
                }

                byte[] health = RawClient.SendBytes(runner.Env.Port,
                    Encoding.ASCII.GetBytes("GET /health HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n"));
                string healthBody = StrictBody(runner, health);
                if (healthBody != null) ParseBody(runner, healthBody);
            });
        }

        private static void RoundTrip(Runner runner, string id, string name, string method, bool expectEmpty = false)
        {
            runner.Case(id, name, () =>
            {
                byte[] raw = RawClient.PostBytes(runner.Env.Port, runner.InvokeJson(EvilDll, EvilType, method));
                string body = StrictBody(runner, raw);
                if (body == null) return;

                JObject parsed = ParseBody(runner, body);
                if (parsed == null) return;

                JToken result = parsed["result"];
                runner.Check(result != null, "响应里没有 result 字段");
                if (result == null) return;

                JToken isError = result["isError"];
                runner.Check(isError == null || isError.Value<bool>() == false,
                    "不应是 isError: " + Runner.Describe(parsed.ToString(Newtonsoft.Json.Formatting.None)));

                string text = (string)result["content"][0]["text"];
                string expected = Expected(runner, method);

                runner.Check(text != null, "content[0].text 字段缺失");
                runner.Expect("往返逐字符一致", text, expected);
                if (expectEmpty) runner.Check(text != null && text.Length == 0, "空字符串必须保持为空串");
                runner.Note("长度 " + (text == null ? -1 : text.Length) + "（期望 " + expected.Length + "）");
            });
        }

        /// <summary>
        /// 孤立代理（未配对）的断言：不能看"解析后的字符串"，因为不同客户端处理不同
        /// （Node/Python 原样保留，C# 的 Newtonsoft 读书时归一化成 U+FFFD）。
        /// 这里直接断言 **wire 上的形式**：必须是 \uXXXX 转义，而不是被替换成 U+FFFD 原始字符。
        /// </summary>
        private static void WireEscape(Runner runner, string id, string name, string method, string expectedEscape)
        {
            runner.Case(id, name, () =>
            {
                byte[] raw = RawClient.PostBytes(runner.Env.Port, runner.InvokeJson(EvilDll, EvilType, method));
                string body = StrictBody(runner, raw);
                if (body == null) return;

                runner.Check(body.IndexOf(expectedEscape, StringComparison.OrdinalIgnoreCase) >= 0,
                    "wire 上应保留转义 " + expectedEscape + "（不能被替换掉），body=" + Runner.Describe(body));
                runner.Check(body.IndexOf('\uFFFD') < 0, "wire 上不应出现 U+FFFD 原始字符（说明数据被改写）");
                runner.Check(body.IndexOf("\\ufffd", StringComparison.OrdinalIgnoreCase) < 0, "wire 上不应出现 U+FFFD 转义");

                JObject parsed = ParseBody(runner, body);
                runner.Check(parsed != null, "独立解析器应能解析该响应（转义形式仍是合法 JSON）");
                runner.Note("客户端差异：JS/Python 原样保留该字符；Newtonsoft 读取时归一化为 U+FFFD（客户端行为，非服务端丢数据）");
            });
        }

        private static void CheckLength(Runner runner, byte[] raw, int expectedLength)
        {
            string body = StrictBody(runner, raw);
            if (body == null) return;

            JObject parsed = ParseBody(runner, body);
            if (parsed == null) return;

            string text = (string)parsed["result"]["content"][0]["text"];
            runner.Check(text != null && text.Length == expectedLength,
                "文本长度应为 " + expectedLength + "，实际 " + (text == null ? -1 : text.Length));
            runner.Note("响应总字节数 " + raw.Length);
        }

        /// <summary>严格 UTF-8 解码 + Content-Length 校验，返回响应正文文本（失败返回 null）。</summary>
        private static string StrictBody(Runner runner, byte[] raw)
        {
            if (raw == null || raw.Length == 0)
            {
                runner.Check(false, "没有收到响应字节");
                return null;
            }

            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(raw);
            }
            catch (DecoderFallbackException exception)
            {
                runner.Check(false, "响应字节不是合法 UTF-8: " + exception.Message);
                return null;
            }

            int headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            runner.Check(headerEnd > 0, "响应缺少头部结束标记");
            if (headerEnd <= 0) return null;

            string head = text.Substring(0, headerEnd);
            string body = text.Substring(headerEnd + 4);

            int declared = DeclaredContentLength(head);
            int actual = Encoding.UTF8.GetByteCount(body);
            runner.Check(declared == actual, "Content-Length 应等于正文字节数：声明 " + declared + "，实际 " + actual);
            return body;
        }

        /// <summary>用独立解析器解析正文。</summary>
        private static JObject ParseBody(Runner runner, string body)
        {
            try
            {
                return JObject.Parse(body);
            }
            catch (Exception exception)
            {
                runner.Check(false, "独立解析器解析失败: " + exception.Message + " body=" + Runner.Describe(body));
                return null;
            }
        }

        private static int DeclaredContentLength(string head)
        {
            foreach (string line in head.Split(new[] { "\r\n" }, StringSplitOptions.None))
            {
                if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) continue;
                int value;
                if (int.TryParse(line.Substring("Content-Length:".Length).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return value;
            }
            return -1;
        }

        /// <summary>直接反射调用同一个方法，拿到"标准答案"。</summary>
        private static string Expected(Runner runner, string method)
        {
            if (evilAssembly == null)
                evilAssembly = Assembly.LoadFrom(Path.Combine(runner.Env.Scripts, "Evil.dll"));

            MethodInfo info = evilAssembly.GetType(EvilType).GetMethod(method);
            return (string)info.Invoke(null, null);
        }
    }
}
