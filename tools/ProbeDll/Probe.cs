using System;
using System.Threading;
using UnityEngine;

namespace Probe
{
    /// <summary>
    /// 实机冒烟用探针：部署到 BepInEx\scripts\Probe.dll，由 agent 通过 invoke_method 调用。
    /// 全部方法是 public static 且无参（符合 invoke_method 的约束）。
    /// </summary>
    public static class Main
    {
        /// <summary>基础探活。</summary>
        public static string Ping()
        {
            return "pong";
        }

        /// <summary>热重载验证用：重新构建成 V2 后应返回 PROBE-V2。</summary>
        public static string Mark()
        {
            return "PROBE-V2";
        }

        /// <summary>访问当前场景（主线程专有 API，用于验证 Find 类调用）。</summary>
        public static string Find()
        {
            UnityEngine.SceneManagement.Scene scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            return "scene=" + scene.name + " roots=" + scene.rootCount + " loaded=" + scene.isLoaded;
        }

        /// <summary>
        /// 诊断用：在方法内部用反射调用 Boom()，把异常在 Mono 上的真实形态原样返回
        /// （类型 / 内层 / Message / ToString 内容与长度 / StackTrace），用于定位"错误文本为空"的问题。
        /// </summary>
        public static string BoomDetail()
        {
            try
            {
                typeof(Main).GetMethod("Boom").Invoke(null, null);
                return "no-throw";
            }
            catch (Exception e)
            {
                string toString;
                string toStringError = null;
                try
                {
                    toString = e.ToString();
                }
                catch (Exception inner)
                {
                    toString = null;
                    toStringError = inner.GetType().FullName + ": " + inner.Message;
                }

                string message;
                try
                {
                    message = e.Message;
                }
                catch (Exception inner)
                {
                    message = "<Message threw " + inner.GetType().Name + ">";
                }

                return "type=" + e.GetType().FullName
                    + " | inner=" + (e.InnerException == null ? "null" : e.InnerException.GetType().FullName)
                    + " | message=[" + message + "]"
                    + " | toStringLen=" + (toString == null ? -1 : toString.Length)
                    + " | toString=[" + toString + "]"
                    + " | toStringError=" + (toStringError ?? "none")
                    + " | stack=[" + (e.StackTrace ?? "null") + "]";
            }
        }

        /// <summary>诊断用：直接抛一个普通异常（不经过反射包装）。</summary>
        public static string ThrowPlain()
        {
            throw new Exception("plain-boom");
        }

        /// <summary>ToString() 返回空串的异常：确定性复现"错误文本为空"的问题。</summary>
        public sealed class EmptyToStringException : Exception
        {
            public EmptyToStringException() : base("empty-tostring-boom")
            {
            }

            public override string ToString()
            {
                return string.Empty;
            }
        }

        /// <summary>抛出 ToString() 为空的异常（修复前服务端会回空文本；修复后应回"类型: 消息 + 堆栈"）。</summary>
        public static string ThrowEmptyToString()
        {
            throw new EmptyToStringException();
        }

        /// <summary>诊断用：打印内层异常各项属性的真实可用性（Message / ToString / StackTrace）。</summary>
        public static string BoomDetail2()
        {
            try
            {
                typeof(Main).GetMethod("Boom").Invoke(null, null);
                return "no-throw";
            }
            catch (Exception e)
            {
                Exception inner = e.InnerException ?? e;
                string toString = null;
                string toStringError = null;
                try
                {
                    toString = inner.ToString();
                }
                catch (Exception failed)
                {
                    toStringError = failed.GetType().Name;
                }

                return "innerType=" + inner.GetType().FullName
                    + " | innerMessage=[" + inner.Message + "]"
                    + " | innerToString=[" + toString + "]"
                    + " | innerToStringLen=" + (toString == null ? -1 : toString.Length)
                    + " | innerToStringError=" + (toStringError ?? "none")
                    + " | innerStack=[" + (inner.StackTrace ?? "null") + "]";
            }
        }

        /// <summary>证明在 Unity 主线程执行：能访问 Unity API，并返回真实运行时信息。</summary>
        public static string Unity()
        {
            return "unity=" + Application.unityVersion + " platform=" + Application.platform + " frame=" + Time.frameCount;
        }

        /// <summary>访问场景对象（主线程专有 API）。</summary>
        public static string FindObject()
        {
            GameObject found = GameObject.Find("SybarisLoader");
            return found == null ? "not-found" : found.name;
        }

        /// <summary>写一条 Unity 日志（验证日志进 BepInEx 日志文件）。</summary>
        public static string Loud()
        {
            Debug.Log("[Probe] Loud was called on the main thread");
            return "logged";
        }

        /// <summary>抛异常（验证异常类型/消息/堆栈回传）。</summary>
        public static string Boom()
        {
            throw new InvalidOperationException("boom from Probe");
        }

        /// <summary>在主线程睡 35 秒：用于超时用例（默认 TimeoutSeconds=30）。</summary>
        public static string Sleep35()
        {
            Thread.Sleep(35000);
            return "slept-35s";
        }

        /// <summary>带参数的方法：用于验证"只接受无参方法"与候选列表。</summary>
        public static string WithArg(int value)
        {
            return "value=" + value;
        }

        /// <summary>
        /// 触发宿主（ScriptDebugEngine）的配置重载：BepInEx 5.4 不会自动重载 .cfg 文件，
        /// 只有真的调用 Config.Reload()（或 ConfigurationManager）才会让 ConfigEntry.Value 变化，
        /// 从而让插件的每帧比对逻辑启停/换端口。
        /// </summary>
        /// <summary>列出所有已加载插件（诊断用：GUID = 插件名）。</summary>
        public static string ListEnginePlugins()
        {
            var builder = new System.Text.StringBuilder();
            foreach (System.Collections.Generic.KeyValuePair<string, BepInEx.PluginInfo> pair in BepInEx.Bootstrap.Chainloader.PluginInfos)
                builder.Append(pair.Key).Append('=').Append(pair.Value.Metadata.Name).Append("; ");
            return builder.Length == 0 ? "(no plugins)" : builder.ToString();
        }

        /// <summary>
        /// 触发宿主（ScriptDebugEngine）的配置重载：BepInEx 5.4 不会自动重载 .cfg 文件，
        /// 只有真的调用 Config.Reload()（或 ConfigurationManager）才会让 ConfigEntry.Value 变化，
        /// 从而让插件的每帧比对逻辑启停/换端口。按类型名查找，避免 GUID 变化导致失效。
        /// </summary>
        public static string ReloadEngineConfig()
        {
            foreach (System.Collections.Generic.KeyValuePair<string, BepInEx.PluginInfo> pair in BepInEx.Bootstrap.Chainloader.PluginInfos)
            {
                var plugin = pair.Value.Instance as BepInEx.BaseUnityPlugin;
                if (plugin == null) continue;
                if (plugin.GetType().FullName != "ScriptDebugEngine.ScriptDebugEngine") continue;

                plugin.Config.Reload();
                return "config-reloaded via " + pair.Key;
            }

            return "engine-plugin-not-found: " + ListEnginePlugins();
        }
    }
}
