using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using ScriptDebugEngine.Mcp;

namespace McpSmokeTests
{
    /// <summary>G/H 组：配置生命周期与资源稳定性（离线可验证的部分）。</summary>
    internal static class LifecycleCases
    {
        public static void Register(Runner runner)
        {
            runner.Case("G-02", "同一端口连续启停 20 次", () =>
            {
                int port = RawClient.FreePort();
                int failures = 0;

                for (int i = 0; i < 20; i++)
                {
                    McpServer server = runner.StartServer(port);
                    if (RawClient.StatusCode(RawClient.Get(port, "/health", 4000)) != 200) failures++;
                    server.Stop();
                }

                runner.Check(failures == 0, "20 次启停应全部成功响应，失败 " + failures + " 次");
            });

            runner.Case("G-03", "运行时换端口：旧端口关闭、新端口可用", () =>
            {
                int oldPort = runner.Env.Port;
                int newPort = RawClient.FreePort();

                runner.Env.Server.Stop();
                runner.Env.Server = runner.StartServer(newPort);
                runner.Env.Port = newPort;

                runner.ExpectStatus("新端口", RawClient.Get(newPort, "/health"), 200);
                string atOld = RawClient.Get(oldPort, "/health", 2000);
                runner.Check(RawClient.StatusCode(atOld) == 0, "旧端口应已关闭，实际: " + Runner.Describe(atOld));

                // 恢复到原端口，避免影响后续用例
                runner.Env.Server.Stop();
                runner.Env.Port = oldPort;
                runner.RestartPrimaryServer();
                runner.Note("换端口（等价于配置改 Port）后旧端口释放、新端口可用");
            });

            runner.Case("G-04", "目标端口被占用时运行中的服务器不受影响", () =>
            {
                int busyPort = RawClient.FreePort();
                var blocker = new TcpListener(IPAddress.Loopback, busyPort);
                blocker.Start();
                try
                {
                    try
                    {
                        McpServer server = runner.StartServer(busyPort);
                        server.Stop();
                        runner.Note("占用端口上仍启动成功（Windows SO_REUSEADDR 语义，已记录）");
                    }
                    catch (SocketException)
                    {
                        runner.Note("启动被拒绝（符合预期）");
                    }

                    runner.ExpectStatus("主服务器不受影响", RawClient.Get(runner.Env.Port, "/health"), 200);
                }
                finally
                {
                    blocker.Stop();
                }
            });

            runner.Case("G-07", "20 次启停后端口可被回收", () =>
            {
                int port = RawClient.FreePort();
                for (int i = 0; i < 20; i++)
                {
                    McpServer server = runner.StartServer(port);
                    server.Stop();
                }

                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                runner.Note("停止后端口可被其它 listener 绑定（无端口泄漏）");
            });

            runner.Case("H-01", "长期运行资源占用（200 轮请求 + 加载）", () =>
            {
                Process process = Process.GetCurrentProcess();
                process.Refresh();
                int handlesBefore = process.HandleCount;
                int threadsBefore = process.Threads.Count;
                long memoryBefore = GC.GetTotalMemory(true);

                for (int i = 0; i < 200; i++)
                {
                    RawClient.Get(runner.Env.Port, "/health");
                    runner.Invoke("scripts\\Good.dll", "Good.Probe", "Ping");
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                process.Refresh();

                int handleGrowth = process.HandleCount - handlesBefore;
                int threadGrowth = process.Threads.Count - threadsBefore;
                long memoryGrowthMb = (GC.GetTotalMemory(true) - memoryBefore) / (1024 * 1024);

                runner.Check(handleGrowth < 200, "句柄增长过大：" + handleGrowth);
                runner.Check(threadGrowth < 50, "线程增长过大：" + threadGrowth);
                runner.Check(memoryGrowthMb < 80, "托管堆增长过大：" + memoryGrowthMb + "MB");
                runner.Note("句柄 +" + handleGrowth + "，线程 +" + threadGrowth + "，堆 +" + memoryGrowthMb + "MB");
            });

            runner.Case("H-02", "连接不堆积（只看 Established）", () =>
            {
                for (int i = 0; i < 50; i++) RawClient.Get(runner.Env.Port, "/health");
                Thread.Sleep(500);

                int established = 0;
                int timeWait = 0;
                foreach (TcpConnectionInformation connection in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections())
                {
                    if (connection.LocalEndPoint.Port != runner.Env.Port) continue;
                    if (connection.State == TcpState.Established) established++;
                    else if (connection.State == TcpState.TimeWait) timeWait++;
                }

                runner.Check(established < 10, "仍处于 Established 的套接字过多：" + established);
                runner.Note("50 次请求后 Established=" + established + "，TIME_WAIT=" + timeWait + "（服务端主动关闭，TIME_WAIT 属正常）");
            });

            runner.Case("H-03", "日志增量受控", () =>
            {
                int before = runner.LogLines;

                RawClient.Get(runner.Env.Port, "/health");
                runner.Invoke("scripts\\Good.dll", "Good.Probe", "Ping");
                RawClient.SendText(runner.Env.Port, "POST /mcp HTTP/1.1\r\nHost: x\r\nContent-Length: 2\r\nContent-Length: 3\r\n\r\n{}", 8000);
                RawClient.SendAbort(runner.Env.Port, Encoding.ASCII.GetBytes("POST /mcp HTTP/1.1\r\nHost: x\r\nContent-Length: 2\r\n\r\n{}"));
                Thread.Sleep(300);

                int after = runner.LogLines;
                runner.Check(after - before <= 3, "服务器日志增量应 ≤3 行，实际 " + (after - before) + " 行");
                runner.Note("服务器日志增量 " + (after - before) + " 行");
            });

            runner.Case("H-04", "回环延迟（50 次 /health）", () =>
            {
                var samples = new List<long>();
                for (int i = 0; i < 50; i++)
                {
                    var watch = Stopwatch.StartNew();
                    RawClient.Get(runner.Env.Port, "/health");
                    watch.Stop();
                    samples.Add(watch.ElapsedMilliseconds);
                }

                samples.Sort();
                long p95 = samples[(int)(samples.Count * 0.95) - 1];
                runner.Check(p95 < 200, "P95 延迟应 < 200ms，实际 " + p95 + "ms");
                runner.Note("P95=" + p95 + "ms，max=" + samples[samples.Count - 1] + "ms");
            });

            runner.Case("G-01", "运行时改 Enabled（实机 ConfigurationManager）", () => { }, skip: true, skipReason: "需要实机：勾选后自动启停");
            runner.Case("G-05", "配置文件重载生效（实机）", () => { }, skip: true, skipReason: "需要实机：Config.Reload() 后生效");
            runner.Case("G-06", "OnDestroy 释放端口与线程（实机）", () => { }, skip: true, skipReason: "需要实机：退出游戏后端口释放");
            runner.Case("I-01", "DSH 连接器连接与工具发现（集成）", () => { }, skip: true, skipReason: "见 MANUAL_L3_L4.md");
            runner.Case("I-02", "agent 端到端调用真实脚本 DLL（集成）", () => { }, skip: true, skipReason: "见 MANUAL_L3_L4.md");
            runner.Case("I-03", "agent 连续 10 次迭代调用（集成）", () => { }, skip: true, skipReason: "见 MANUAL_L3_L4.md");
            runner.Case("I-04", "游戏重启后重连（集成）", () => { }, skip: true, skipReason: "见 MANUAL_L3_L4.md");
        }
    }
}
