using System;
using System.Collections.Generic;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ScriptDebugEngine.Mcp;

namespace ScriptDebugEngine
{
    [BepInPlugin("com.github.wukong2468.scriptdebugengine", "Script Debug Engine", "1.0")]
    public class ScriptDebugEngine : BaseUnityPlugin
    {
        private ConfigEntry<bool> McpEnabled { get; set; }
        private ConfigEntry<int> McpPort { get; set; }
        private ConfigEntry<int> McpTimeoutSeconds { get; set; }
        private ConfigEntry<bool> McpAllowAnyPath { get; set; }

        // 等待在 Unity 主线程上执行的 MCP 调用（net35 没有 ConcurrentQueue，用锁 + Queue 代替）
        private readonly Queue<Action> mainThreadQueue = new Queue<Action>();
        private readonly object mainThreadQueueLock = new object();
        private int invocationBusy;

        private InvokeContext invokeContext;
        private McpServer mcpServer;

        // 已经应用过的 MCP 配置：用于侦测运行时的 Enabled / Port 变化
        private bool mcpApplied;
        private bool mcpAppliedEnabled;
        private int mcpAppliedPort;

        private void Awake()
        {
            McpEnabled = Config.Bind("Mcp", "Enabled", true, new ConfigDescription("Start the MCP HTTP server that lets an AI agent hot-load a DLL and execute a parameterless public static method."));
            McpPort = Config.Bind("Mcp", "Port", 8765, new ConfigDescription("TCP port for the MCP server. The server only binds to 127.0.0.1."));
            McpTimeoutSeconds = Config.Bind("Mcp", "TimeoutSeconds", 30, new ConfigDescription("How long an MCP request waits for the invocation to finish on the Unity main thread."));
            McpAllowAnyPath = Config.Bind("Mcp", "AllowAnyPath", false, new ConfigDescription("DANGEROUS: when enabled, invoke_method may load a DLL from ANY path, not just from under the BepInEx root. That turns this tool into arbitrary code execution for any local process. Leave it off unless you really need it."));

            ApplyMcpConfiguration();
        }

        private void Update()
        {
            // Enabled / Port 允许在运行时修改（ConfigurationManager 或配置文件重载），这里负责启停与换端口
            ApplyMcpConfiguration();

            // AllowAnyPath 也允许运行时改动，而且不需要重启服务器：每次比对前刷新一次上下文
            if (invokeContext != null)
                invokeContext.AllowAnyPath = McpAllowAnyPath.Value;

            PumpMainThreadQueue();
        }

        private void OnDestroy()
        {
            StopMcpServer();
        }

        /// <summary>让服务器状态与当前配置保持一致；配置没变时什么都不做。</summary>
        private void ApplyMcpConfiguration()
        {
            bool enabled = McpEnabled.Value;
            int port = McpPort.Value;

            if (mcpApplied && mcpAppliedEnabled == enabled && mcpAppliedPort == port) return;

            mcpApplied = true;
            mcpAppliedEnabled = enabled;
            mcpAppliedPort = port;

            StopMcpServer();

            if (!enabled)
            {
                Logger.Log(LogLevel.Info, "MCP server is disabled by config");
                return;
            }

            StartMcpServer(port);
        }

        private void StartMcpServer(int port)
        {
            try
            {
                invokeContext = new InvokeContext
                {
                    BepInExRoot = Paths.BepInExRootPath,
                    AllowAnyPath = McpAllowAnyPath.Value,
                    LogInfo = message => Logger.Log(LogLevel.Info, message)
                };

                mcpServer = new McpServer(port, "1.0", new MainThreadInvocationHost(this),
                    message => Logger.Log(LogLevel.Info, message),
                    (message, exception) => Logger.LogError(exception == null ? message : $"{message}: {exception}"));

                mcpServer.Start();
            }
            catch (Exception e)
            {
                mcpServer = null;
                // 注意：启动失败不会每帧重试，改了 Enabled/Port 才会再试一次
                Logger.LogError($"Failed to start the MCP server on port {port}: {e}");
            }
        }

        private void StopMcpServer()
        {
            if (mcpServer == null) return;

            mcpServer.Stop();
            mcpServer = null;
            Logger.Log(LogLevel.Info, "MCP server stopped");
        }

        private void EnqueueMainThreadAction(Action action)
        {
            lock (mainThreadQueueLock)
            {
                mainThreadQueue.Enqueue(action);
            }
        }

        private void PumpMainThreadQueue()
        {
            while (true)
            {
                Action action;
                lock (mainThreadQueueLock)
                {
                    if (mainThreadQueue.Count == 0) return;
                    action = mainThreadQueue.Dequeue();
                }

                action();
            }
        }

        /// <summary>把 MCP 请求排到 Unity 主线程执行并等待结果（目标函数可能访问 Unity API，必须在主线程调用）。</summary>
        private sealed class MainThreadInvocationHost : IInvocationHost
        {
            private readonly ScriptDebugEngine plugin;

            public MainThreadInvocationHost(ScriptDebugEngine plugin)
            {
                this.plugin = plugin;
            }

            public string Invoke(string dllPath, string typeName, string methodName)
            {
                if (Interlocked.CompareExchange(ref plugin.invocationBusy, 1, 0) != 0)
                    throw new InvalidOperationException("The engine is busy executing another invocation.");

                try
                {
                    object gate = new object();
                    bool finished = false;
                    string result = null;
                    Exception failure = null;

                    plugin.EnqueueMainThreadAction(() =>
                    {
                        try
                        {
                            result = Invoker.Invoke(plugin.invokeContext, dllPath, typeName, methodName);
                        }
                        catch (Exception e)
                        {
                            failure = e;
                        }
                        finally
                        {
                            lock (gate)
                            {
                                finished = true;
                                Monitor.Pulse(gate);
                            }
                        }
                    });

                    int timeoutMs = Math.Max(1, plugin.McpTimeoutSeconds.Value) * 1000;
                    lock (gate)
                    {
                        while (!finished)
                        {
                            if (!Monitor.Wait(gate, timeoutMs))
                                throw new TimeoutException($"Invocation did not finish within {timeoutMs} ms.");
                        }
                    }

                    if (failure != null) throw failure;
                    return result;
                }
                finally
                {
                    Interlocked.Exchange(ref plugin.invocationBusy, 0);
                }
            }
        }
    }
}
