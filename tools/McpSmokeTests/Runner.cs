using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using ScriptDebugEngine.Mcp;

namespace McpSmokeTests
{
    internal sealed class CaseResult
    {
        public string Id;
        public string Name;
        public string Status = "PASS";
        public int ElapsedMs;
        public string Evidence = string.Empty;
    }

    /// <summary>测试运行环境。</summary>
    internal sealed class Env
    {
        public int Port;
        public string Root;      // 伪造的 BepInEx 根目录（白名单根）
        public string Scripts;   // Root\scripts，放测试 DLL
        public McpServer Server; // 当前服务器实例（生命周期用例会替换它）
        public InvokeContext Context;
    }

    /// <summary>
    /// 用例运行器：每个用例执行后自动做两次探活（/health + 正常 invoke），
    /// 用来验证"这个极端场景没有把服务器打死"。进程若被杀掉，由驱动脚本判定为 CRASHED。
    /// </summary>
    internal sealed class Runner
    {
        private readonly DateTime startedAt = DateTime.Now;
        private readonly List<CaseResult> results = new List<CaseResult>();
        private readonly StringBuilder evidence = new StringBuilder();
        private CaseResult current;
        private int currentFailures;

        public Env Env { get; set; }

        /// <summary>按端口创建服务器实例（生命周期用例需要重启服务器时使用）。</summary>
        public Func<int, McpServer> ServerFactory { get; set; }

        public int Passed { get; private set; }
        public int Failures { get; private set; }
        public int Skipped { get; private set; }

        /// <summary>服务器自身打出的日志行数（用于验证畸形输入不会刷屏）。</summary>
        public int LogLines { get; private set; }

        public void CountLog()
        {
            LogLines++;
        }

        // ---------------- 用例注册与执行 ----------------

        public void Case(string id, string name, Action body, bool probe = true, bool skip = false, string skipReason = null)
        {
            if (skip)
            {
                results.Add(new CaseResult { Id = id, Name = name, Status = "SKIP", Evidence = skipReason ?? string.Empty });
                Skipped++;
                Console.WriteLine(string.Format("{0,-5} {1,-6} {2}  ({3})", "SKIP", id, name, skipReason));
                return;
            }

            current = new CaseResult { Id = id, Name = name };
            currentFailures = 0;
            evidence.Length = 0;

            var watch = Stopwatch.StartNew();
            try
            {
                body();
            }
            catch (Exception exception)
            {
                Check(false, "unhandled " + exception.GetType().Name + ": " + exception.Message);
            }

            if (probe && currentFailures == 0) Probe();
            current.ElapsedMs = (int)watch.ElapsedMilliseconds;

            if (currentFailures > 0)
            {
                current.Status = "FAIL";
                Failures++;
            }
            else
            {
                Passed++;
            }

            current.Evidence = evidence.ToString();
            results.Add(current);
            Console.WriteLine(string.Format("{0,-5} {1,-6} {2}  {3}ms", current.Status, current.Id, current.Name, current.ElapsedMs));
            if (currentFailures > 0) Console.WriteLine("      -> " + current.Evidence);
            current = null;
        }

        private void Probe()
        {
            try
            {
                string health = RawClient.Get(Env.Port, "/health");
                if (RawClient.StatusCode(health) != 200 || health.IndexOf("\"status\":\"ok\"", StringComparison.Ordinal) < 0)
                {
                    Check(false, "探活失败 /health -> " + Describe(health));
                    return;
                }

                string response = Invoke("scripts\\Good.dll", "Good.Probe", "Ping");
                if (Text(response) != "pong") Check(false, "探活失败 invoke Good.Probe.Ping -> " + ResultJson(response));
            }
            catch (Exception exception)
            {
                Check(false, "探活异常 " + exception.GetType().Name + ": " + exception.Message + "（服务器可能已不可用）");
            }
        }

        // ---------------- 断言 ----------------

        public void Check(bool ok, string detail)
        {
            if (ok) return;
            currentFailures++;
            if (evidence.Length > 0) evidence.Append(" ; ");
            evidence.Append(detail);
        }

        public void Expect(string label, string actual, string expected)
        {
            if (actual == expected) { Note(label + "=OK"); return; }
            Check(false, label + ": expected [" + expected + "] got [" + Describe(actual) + "]");
        }

        public void ExpectStatus(string label, string response, int expected)
        {
            int actual = RawClient.StatusCode(response);
            if (actual == expected) Note(label + "=HTTP " + actual);
            else Check(false, label + ": expected HTTP " + expected + " got " + actual + " -> " + Describe(response));
        }

        public void ExpectContains(string label, string actual, string expected)
        {
            if (actual != null && actual.IndexOf(expected, StringComparison.Ordinal) >= 0) { Note(label + "=OK"); return; }
            Check(false, label + ": expected to contain [" + expected + "] got [" + Describe(actual) + "]");
        }

        /// <summary>断言 JSON-RPC 错误码。</summary>
        public void ExpectCode(string label, string response, long expected)
        {
            long actual = ErrorCode(response);
            if (actual == expected) { Note(label + "=code " + actual); return; }
            Check(false, label + ": expected code " + expected + " got " + actual + " -> " + ResultJson(response));
        }

        public void Note(string text)
        {
            if (evidence.Length > 0) evidence.Append(" ; ");
            evidence.Append(text);
        }

        // ---------------- 服务器控制 ----------------

        public McpServer StartServer(int port)
        {
            McpServer server = ServerFactory(port);
            server.Start();
            return server;
        }

        public void RestartPrimaryServer()
        {
            try
            {
                if (Env.Server != null) Env.Server.Stop();
            }
            catch (Exception)
            {
                // 用例里可能已经停过
            }

            Env.Server = StartServer(Env.Port);
        }

        // ---------------- 请求构造 ----------------

        /// <summary>构造 tools/call 请求（返回 JSON 文本，便于需要原始字节的用例复用）。</summary>
        public string InvokeJson(string dllPath, string typeName, string methodName)
        {
            return "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"invoke_method\",\"arguments\":{"
                + "\"dllPath\":" + Json.Serialize(dllPath)
                + ",\"typeName\":" + Json.Serialize(typeName)
                + ",\"methodName\":" + Json.Serialize(methodName) + "}}}";
        }

        public string Invoke(string dllPath, string typeName, string methodName)
        {
            return RawClient.Post(Env.Port, InvokeJson(dllPath, typeName, methodName));
        }

        public string Rpc(string json)
        {
            return RawClient.Post(Env.Port, json);
        }

        // ---------------- 响应解析 ----------------

        public static IDictionary<string, object> ParseResponse(string response)
        {
            string body = RawClient.Body(response);
            if (string.IsNullOrEmpty(body)) return null;
            try
            {
                return Json.Parse(body) as IDictionary<string, object>;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string Text(string response)
        {
            IDictionary<string, object> root = ParseResponse(response);
            IDictionary<string, object> result = Json.GetObject(root, "result");
            var content = result != null && result.ContainsKey("content") ? result["content"] as List<object> : null;
            if (content == null || content.Count == 0) return null;
            var first = content[0] as IDictionary<string, object>;
            return first == null ? null : Json.GetString(first, "text");
        }

        public static bool IsError(string response)
        {
            IDictionary<string, object> root = ParseResponse(response);
            IDictionary<string, object> result = Json.GetObject(root, "result");
            if (result == null || !result.ContainsKey("isError")) return false;
            return result["isError"] is bool && (bool)result["isError"];
        }

        public static long ErrorCode(string response)
        {
            IDictionary<string, object> root = ParseResponse(response);
            IDictionary<string, object> error = Json.GetObject(root, "error");
            if (error == null || !error.ContainsKey("code")) return 0;
            object code = error["code"];
            return code is long ? (long)code : 0;
        }

        /// <summary>把响应里的 result（或 error）重新序列化成一行，作为断言证据。</summary>
        public static string ResultJson(string response)
        {
            IDictionary<string, object> root = ParseResponse(response);
            if (root == null) return Describe(response);

            object payload = null;
            if (root.ContainsKey("result")) payload = root["result"];
            else if (root.ContainsKey("error")) payload = root["error"];

            return payload == null ? "(no result/error)" : Json.Serialize(payload);
        }

        public static string Describe(string value)
        {
            if (value == null) return "(null)";
            string text = value.Replace("\r", string.Empty).Replace("\n", " | ");
            return text.Length > 240 ? text.Substring(0, 240) + "…" : text;
        }

        // ---------------- 报告 ----------------

        public void WriteReport(string path)
        {
            var cases = new List<object>();
            foreach (CaseResult result in results)
            {
                cases.Add(new Dictionary<string, object>
                {
                    { "id", result.Id },
                    { "name", result.Name },
                    { "result", result.Status },
                    { "elapsedMs", (long)result.ElapsedMs },
                    { "evidence", result.Evidence }
                });
            }

            var report = new Dictionary<string, object>
            {
                { "startedAt", startedAt.ToString("s", CultureInfo.InvariantCulture) },
                { "finishedAt", DateTime.Now.ToString("s", CultureInfo.InvariantCulture) },
                { "target", "ScriptDebugEngine MCP (offline L1+L2)" },
                { "cases", cases },
                { "summary", new Dictionary<string, object>
                    {
                        { "pass", (long)Passed },
                        { "fail", (long)Failures },
                        { "skip", (long)Skipped }
                    }
                }
            };

            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, Json.Serialize(report), new UTF8Encoding(false));
        }
    }
}
