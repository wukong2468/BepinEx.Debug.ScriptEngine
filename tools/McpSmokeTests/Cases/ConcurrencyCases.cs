using System;
using System.Net.Sockets;
using System.Threading;

namespace McpSmokeTests
{
    /// <summary>F 组：并发与时序。</summary>
    internal static class ConcurrencyCases
    {
        public static void Register(Runner runner)
        {
            runner.Case("F-01", "8 个并发 tools/call", () =>
            {
                const int count = 8;
                var results = new string[count];
                var workers = new Thread[count];

                for (int i = 0; i < count; i++)
                {
                    int index = i;
                    workers[i] = new Thread(() => { results[index] = Runner.Text(runner.Invoke("scripts\\Good.dll", "Good.Probe", "Ping")); })
                    {
                        IsBackground = true
                    };
                }

                foreach (Thread worker in workers) worker.Start();
                foreach (Thread worker in workers) worker.Join(20000);

                int ok = 0;
                foreach (string result in results) if (result == "pong") ok++;
                runner.Check(ok == count, "8 个并发应全部成功，实际 " + ok + "/" + count);
            });

            runner.Case("F-02", "16 个混合并发（invoke + /health）", () =>
            {
                const int count = 16;
                var results = new string[count];
                var workers = new Thread[count];

                for (int i = 0; i < count; i++)
                {
                    int index = i;
                    workers[i] = new Thread(() =>
                    {
                        results[index] = index % 2 == 0
                            ? Runner.Text(runner.Invoke("scripts\\Good.dll", "Good.Probe", "Number"))
                            : RawClient.StatusCode(RawClient.Get(runner.Env.Port, "/health")).ToString();
                    })
                    {
                        IsBackground = true
                    };
                }

                foreach (Thread worker in workers) worker.Start();
                foreach (Thread worker in workers) worker.Join(30000);

                int ok = 0;
                for (int i = 0; i < count; i++)
                {
                    string expected = i % 2 == 0 ? "42" : "200";
                    if (results[i] == expected) ok++;
                }
                runner.Check(ok == count, "16 个混合并发应全部成功，实际 " + ok + "/" + count);
            });

            runner.Case("F-06", "在途请求存在时 Stop()", () =>
            {
                TcpClient slow = RawClient.OpenSlowConnection(runner.Env.Port, 8);
                try
                {
                    runner.Env.Server.Stop();
                    runner.Note("Stop() 在途连接存在时正常返回");
                }
                finally
                {
                    try { slow.Close(); }
                    catch (Exception) { }

                    runner.RestartPrimaryServer();
                    runner.Note("已重启主服务器（探活验证）");
                }
            });

            runner.Case("F-03", "客户端重试会重复执行（语义）", () => { }, skip: true, skipReason: "已知限制：无幂等去重，见方案 §5-P2");
            runner.Case("F-04", "两个 agent 同时调用（实机 busy）", () => { }, skip: true, skipReason: "需要实机：验证 engine is busy 与恢复");
            runner.Case("F-05", "超时后立即重发 5 次（实机）", () => { }, skip: true, skipReason: "需要实机：验证超时后 busy 释放、队列不涨");
        }
    }
}
