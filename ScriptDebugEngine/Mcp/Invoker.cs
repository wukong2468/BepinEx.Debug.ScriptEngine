using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ScriptDebugEngine.Mcp
{
    /// <summary>调用上下文：白名单根目录、引用解析目录、日志回调。</summary>
    internal sealed class InvokeContext
    {
        /// <summary>BepInEx 根目录：相对路径的基准，同时限定可加载的 DLL 范围。</summary>
        public string BepInExRoot;

        /// <summary>可选的日志回调（可为 null）。</summary>
        public Action<string> LogInfo;
    }

    /// <summary>目标函数执行时抛出了异常；Message 内含原始异常类型、消息与堆栈。</summary>
    internal sealed class InvocationFailedException : Exception
    {
        public InvocationFailedException(string message) : base(message) { }
    }

    /// <summary>热加载 DLL 并执行其中的 public static 无参方法。</summary>
    internal static class Invoker
    {
        /// <summary>加载并执行目标方法；成功返回结果文本，失败抛异常。</summary>
        public static string Invoke(InvokeContext context, string dllPath, string typeName, string methodName)
        {
            if (context == null) throw new ArgumentNullException("context");
            if (string.IsNullOrEmpty(context.BepInExRoot)) throw new InvalidOperationException("BepInEx root is not configured.");
            if (string.IsNullOrEmpty(dllPath)) throw new ArgumentException("dllPath is required.", "dllPath");
            if (string.IsNullOrEmpty(typeName)) throw new ArgumentException("typeName is required.", "typeName");
            if (string.IsNullOrEmpty(methodName)) throw new ArgumentException("methodName is required.", "methodName");

            string fullPath = ResolveDllPath(context, dllPath);
            if (context.LogInfo != null) context.LogInfo(string.Format("Hot loading '{0}' for {1}.{2}", fullPath, typeName, methodName));

            using (LoadedScript script = ScriptLoader.Load(fullPath))
            {
                var loaderErrors = new StringBuilder();
                Type targetType = FindType(ScriptLoader.GetTypesSafe(script.Assembly, loaderErrors), typeName);
                if (targetType == null)
                    throw new MissingMemberException(string.Format("Type '{0}' was not found in '{1}'.{2}", typeName, Path.GetFileName(fullPath), loaderErrors));

                return InvokeMethod(FindMethod(targetType, methodName));
            }
        }

        private static string ResolveDllPath(InvokeContext context, string dllPath)
        {
            string root = Path.GetFullPath(context.BepInExRoot);
            string fullPath = Path.GetFullPath(Path.IsPathRooted(dllPath) ? dllPath : Path.Combine(root, dllPath));

            string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException(string.Format("Access denied: '{0}' is outside of '{1}'.", fullPath, root));

            if (!File.Exists(fullPath)) throw new FileNotFoundException("DLL not found: " + fullPath, fullPath);

            // 拒绝符号链接/junction：Path.GetFullPath 不解析链接，否则可以拿一个链接指向白名单之外来绕过限制
            FileAttributes attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                throw new UnauthorizedAccessException(string.Format("Access denied: '{0}' is a symbolic link or junction.", fullPath));

            return fullPath;
        }

        private static Type FindType(IEnumerable<Type> types, string typeName)
        {
            Type[] all = types.ToArray();

            Type[] matches = all.Where(type => string.Equals(type.FullName, typeName, StringComparison.Ordinal)).ToArray();
            if (matches.Length == 0) matches = all.Where(type => string.Equals(type.Name, typeName, StringComparison.Ordinal)).ToArray();
            if (matches.Length == 0) matches = all.Where(type => type.FullName != null && type.FullName.EndsWith("." + typeName, StringComparison.Ordinal)).ToArray();

            if (matches.Length == 1) return matches[0];
            if (matches.Length > 1)
                throw new AmbiguousMatchException(string.Format("Type name '{0}' matches multiple types: {1}", typeName, string.Join(", ", matches.Select(type => type.FullName).ToArray())));
            return null;
        }

        private static MethodInfo FindMethod(Type type, string methodName)
        {
            MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);

            MethodInfo[] matches = methods
                .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal)
                    && method.GetParameters().Length == 0
                    && !method.ContainsGenericParameters)
                .ToArray();
            if (matches.Length > 0) return matches[0];

            // 找不到时把该类型可用的无参静态方法列出来，便于调用方自行纠正（泛型方法标为 Name<>，它无法直接调用）
            string[] available = methods
                .Where(method => method.GetParameters().Length == 0)
                .Select(method => method.ContainsGenericParameters ? method.Name + "<>" : method.Name)
                .Distinct()
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            string hint = available.Length == 0
                ? "This type has no public static parameterless methods."
                : "Available public static parameterless methods: " + string.Join(", ", available);

            throw new MissingMethodException(string.Format("No public static parameterless method named '{0}' on type '{1}'. {2}", methodName, type.FullName, hint));
        }

        /// <summary>
        /// 拼装异常文本：**不能用 <c>Exception.ToString()</c>**。
        /// 实机（Mono）实测：从 <c>Assembly.Load(byte[])</c> 加载的程序集里抛出的异常，
        /// 其 <c>ToString()</c> 返回**空串**（同一对象的 Message 与 StackTrace 都正常），
        /// 直接用它会导致 agent 收到空的错误文本、完全无法定位。
        /// 这里自己拼"类型: 消息"，并把 InnerException 链一起带上（最多 5 层），最后附最外层的堆栈。
        /// </summary>
        private static string DescribeException(Exception exception)
        {
            var builder = new StringBuilder();
            Exception current = exception;

            for (int depth = 0; current != null && depth < 5; depth++)
            {
                if (depth > 0) builder.Append("\n ---> ");
                builder.Append(TryGet(() => current.GetType().FullName) ?? "Exception")
                       .Append(": ")
                       .Append(TryGet(() => current.Message) ?? string.Empty);

                Exception next = null;
                try { next = current.InnerException; }
                catch (Exception) { }
                current = next;
            }

            string stackTrace = TryGet(() => exception.StackTrace);
            if (!string.IsNullOrEmpty(stackTrace)) builder.Append('\n').Append(stackTrace);
            return builder.ToString();
        }

        private static string TryGet(Func<string> getter)
        {
            try
            {
                return getter();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string InvokeMethod(MethodInfo method)
        {
            object result;
            try
            {
                result = method.Invoke(null, null);
            }
            catch (TargetInvocationException exception)
            {
                Exception inner = exception.InnerException ?? exception;
                throw new InvocationFailedException(DescribeException(inner));
            }

            if (method.ReturnType == typeof(void)) return "OK";
            if (result == null) return "null";

            var text = result as string;
            if (text != null) return text;

            var formattable = result as IFormattable;
            if (formattable != null) return formattable.ToString(null, CultureInfo.InvariantCulture);

            return result.ToString();
        }
    }
}
