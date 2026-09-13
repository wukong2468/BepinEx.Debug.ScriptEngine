using System;
using System.IO;
using ScriptDebugEngine.Mcp;

namespace McpSmokeTests
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string root = args.Length > 0 ? args[0] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fakeroot");
            string scripts = args.Length > 1 ? args[1] : Path.Combine(root, "scripts");
            int port = args.Length > 2 ? int.Parse(args[2]) : 8915;
            string reportPath = args.Length > 3 ? args[3] : "smoke-report.json";
            bool verboseEngineLog = args.Length > 4 && args[4] == "verbose";

            var env = new Env
            {
                Root = Path.GetFullPath(root),
                Scripts = Path.GetFullPath(scripts),
                Port = port,
                Context = new InvokeContext
                {
                    BepInExRoot = Path.GetFullPath(root),
                    // 每次调用都会打一行 "Hot loading ..."，默认不打印（避免几百行噪声）；加参数 verbose 打开
                    LogInfo = verboseEngineLog ? message => Console.WriteLine("  [engine] " + message) : (Action<string>)null
                }
            };

            Action<string> logInfo = null;
            Action<string, Exception> logError = null;

            var runner = new Runner { Env = env };
            logInfo = message =>
            {
                runner.CountLog();
                Console.WriteLine("  [server] " + message);
            };
            logError = (message, exception) =>
            {
                runner.CountLog();
                Console.WriteLine("  [error] " + message + (exception == null ? string.Empty : " :: " + exception));
            };

            runner.ServerFactory = p => new McpServer(p, "smoke", new DirectHost(env), logInfo, logError);
            env.Server = runner.StartServer(port);

            Console.WriteLine("=== A/B 组：HTTP 报文 + JSON 解析 ===");
            HttpCases.Register(runner);
            JsonCases.Register(runner);

            Console.WriteLine("=== C/D 组：JSON-RPC 协议 + 工具参数与安全 ===");
            ProtocolCases.Register(runner);
            ToolCases.Register(runner);

            Console.WriteLine("=== E 组：执行内核（加载 / 反射 / 热重载）===");
            LoaderCases.Register(runner);

            Console.WriteLine("=== F/G/H 组：并发 / 生命周期 / 资源 ===");
            ConcurrencyCases.Register(runner);
            LifecycleCases.Register(runner);

            Console.WriteLine("=== J 组：返回值序列化（邪恶字符串）===");
            SerializationCases.Register(runner);

            try
            {
                env.Server.Stop();
            }
            catch (Exception)
            {
                // 生命周期最后一个用例可能已经把服务器停掉了
            }

            runner.WriteReport(reportPath);

            Console.WriteLine();
            Console.WriteLine(string.Format("结果: PASS={0} FAIL={1} SKIP={2}", runner.Passed, runner.Failures, runner.Skipped));
            Console.WriteLine("报告: " + Path.GetFullPath(reportPath));
            return runner.Failures;
        }

        private sealed class DirectHost : IInvocationHost
        {
            private readonly Env env;

            public DirectHost(Env env)
            {
                this.env = env;
            }

            public string Invoke(string dllPath, string typeName, string methodName)
            {
                return Invoker.Invoke(env.Context, dllPath, typeName, methodName);
            }
        }
    }
}
