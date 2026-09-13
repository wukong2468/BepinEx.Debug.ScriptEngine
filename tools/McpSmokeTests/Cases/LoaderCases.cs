using System;
using System.Diagnostics;
using System.IO;

namespace McpSmokeTests
{
    /// <summary>E 组：执行内核（Cecil 热加载、反射定位、热重载、坏 DLL）。</summary>
    internal static class LoaderCases
    {
        public static void Register(Runner runner)
        {
            string scripts = runner.Env.Scripts;

            runner.Case("E-01", "返回值各种形态", () =>
            {
                runner.Expect("string", Runner.Text(runner.Invoke("scripts\\Good.dll", "Good.Probe", "Ping")), "pong");
                runner.Expect("int", Runner.Text(runner.Invoke("scripts\\Good.dll", "Good.Probe", "Number")), "42");
                runner.Expect("void", Runner.Text(runner.Invoke("scripts\\Good.dll", "Good.Probe", "Nothing")), "OK");
                runner.Expect("null", Runner.Text(runner.Invoke("scripts\\Good.dll", "Good.Probe", "NullResult")), "null");
            });

            runner.Case("E-02", "类型名匹配：全名 / 短名 / 后缀 / 歧义", () =>
            {
                runner.Expect("全名", Runner.Text(runner.Invoke("scripts\\Good.dll", "Good.Second", "Who")), "second");
                runner.Expect("短名", Runner.Text(runner.Invoke("scripts\\Good.dll", "Probe", "Ping")), "pong");
                runner.Expect("后缀匹配", Runner.Text(runner.Invoke("scripts\\Good.dll", "Alpha.Dup", "M")), "dup-alpha");

                string ambiguous = runner.Invoke("scripts\\Good.dll", "Dup", "M");
                runner.Check(Runner.IsError(ambiguous), "同名短名类型应报歧义: " + Runner.ResultJson(ambiguous));
                runner.ExpectContains("歧义提示", Runner.Text(ambiguous), "matches multiple types");
            });

            runner.Case("E-03", "方法不存在 / 带参数 / 带默认值参数", () =>
            {
                string missing = runner.Invoke("scripts\\Good.dll", "Good.Probe", "Nope");
                runner.Check(Runner.IsError(missing), "不存在的方法应 isError: " + Runner.ResultJson(missing));
                runner.ExpectContains("候选列表", Runner.Text(missing), "Available public static parameterless methods");

                string withArgs = runner.Invoke("scripts\\Good.dll", "Good.Probe", "WithArg");
                runner.ExpectContains("带参数被拒", Runner.Text(withArgs), "No public static parameterless method");

                string defaultArg = runner.Invoke("scripts\\Weird.dll", "Weird.Methods", "DefaultArg");
                runner.ExpectContains("默认参数不被当作无参", Runner.Text(defaultArg), "No public static parameterless method");
            });

            runner.Case("E-04", "泛型方法 / 属性不被误命中", () =>
            {
                string generic = runner.Invoke("scripts\\Weird.dll", "Weird.Methods", "Generic");
                runner.Check(Runner.IsError(generic), "泛型方法不应被直接调用: " + Runner.ResultJson(generic));
                runner.ExpectContains("候选列表标注泛型", Runner.Text(generic), "Generic<>");
                runner.Note("泛型无参方法被排除在匹配之外，候选列表标为 Generic<> ✔");
            });

            runner.Case("E-05", "返回 IEnumerator / 自定义对象（不等待、走 ToString）", () =>
            {
                string enumerator = runner.Invoke("scripts\\Weird.dll", "Weird.Methods", "Iterate");
                runner.Check(!Runner.IsError(enumerator), "迭代器方法应能返回对象文本: " + Runner.ResultJson(enumerator));
                runner.Note("IEnumerator 文本=" + Runner.Text(enumerator));

                string custom = runner.Invoke("scripts\\Weird.dll", "Weird.Methods", "CustomObject");
                runner.Expect("自定义对象走 ToString", Runner.Text(custom), "custom-object");
            });

            runner.Case("E-06", "目标方法抛异常 → 类型/消息/堆栈", () =>
            {
                string response = runner.Invoke("scripts\\Good.dll", "Good.Probe", "Boom");
                runner.Check(Runner.IsError(response), "应 isError: " + Runner.ResultJson(response));
                string text = Runner.Text(response);
                runner.ExpectContains("异常类型", text, "System.InvalidOperationException");
                runner.ExpectContains("异常消息", text, "boom from Good");
                runner.ExpectContains("堆栈含方法名", text, "Good.Probe.Boom");
            });

            runner.Case("E-07", "反射再抛异常（TargetInvocationException 解包）", () =>
            {
                string response = runner.Invoke("scripts\\Weird.dll", "Weird.Methods", "RethrowViaReflection");
                runner.Check(Runner.IsError(response), "应 isError: " + Runner.ResultJson(response));
                runner.ExpectContains("内层异常可见", Runner.Text(response), "inner-boom");
            });

            runner.Case("E-08", "引用缺失程序集", () =>
            {
                string ok = runner.Invoke("scripts\\MissingRef.dll", "MissingRef.Entry", "Ok");
                runner.Expect("可加载的类型仍可调用", Runner.Text(ok), "ok");

                string broken = runner.Invoke("scripts\\MissingRef.dll", "MissingRef.Derived", "M");
                runner.Check(Runner.IsError(broken), "依赖缺失的类型应报错: " + Runner.ResultJson(broken));
                runner.ExpectContains("类型不存在", Runner.Text(broken), "was not found");
                runner.ExpectContains("带 LoaderExceptions", Runner.Text(broken), "LoaderExceptions");
                runner.Note("依赖缺失时同程序集内其它类型仍可用，错误里带 LoaderExceptions ✔");
            });

            runner.Case("E-09", "零字节 / 原生 DLL / 文本文件", () =>
            {
                foreach (string name in new[] { "zero.dll", "native.dll", "text.dll" })
                {
                    string response = runner.Invoke("scripts\\" + name, "X", "Y");
                    runner.Check(Runner.IsError(response), name + " 应报错: " + Runner.ResultJson(response));
                    runner.Note(name + " -> " + FirstLine(Runner.Text(response)));
                }
            });

            runner.Case("E-10", "只读 DLL", () =>
            {
                string readonlyPath = Path.Combine(scripts, "readonly.dll");
                File.Copy(Path.Combine(scripts, "Good.dll"), readonlyPath, true);
                File.SetAttributes(readonlyPath, FileAttributes.ReadOnly);
                runner.Expect("只读 DLL 可调用", Runner.Text(runner.Invoke("scripts\\readonly.dll", "Good.Probe", "Ping")), "pong");
            });

            runner.Case("E-11", "热重载：覆盖 DLL 后同一路径返回新结果", () =>
            {
                string reloadPath = Path.Combine(scripts, "reload.dll");
                File.Copy(Path.Combine(scripts, "Good.dll"), reloadPath, true);
                runner.Expect("第一次", Runner.Text(runner.Invoke("scripts\\reload.dll", "Good.Probe", "Ping")), "pong");

                // 直接覆盖已被加载过的文件：若文件被占用会在这里失败
                File.Copy(Path.Combine(scripts, "GoodV2.dll"), reloadPath, true);
                runner.Expect("覆盖后", Runner.Text(runner.Invoke("scripts\\reload.dll", "Good.Probe", "Ping")), "pong-v2");
            });

            runner.Case("E-12", "调用前删除 DLL", () =>
            {
                string tempPath = Path.Combine(scripts, "delete-me.dll");
                File.Copy(Path.Combine(scripts, "Good.dll"), tempPath, true);
                File.Delete(tempPath);
                string response = runner.Invoke("scripts\\delete-me.dll", "Good.Probe", "Ping");
                runner.ExpectContains("已删除", Runner.Text(response), "DLL not found");
            });

            runner.Case("E-13", "连续 500 次调用（资源与文件占用）", () =>
            {
                Process process = Process.GetCurrentProcess();
                process.Refresh();
                int handlesBefore = process.HandleCount;
                long memoryBefore = GC.GetTotalMemory(true);

                int failures = 0;
                for (int i = 0; i < 500; i++)
                {
                    if (Runner.Text(runner.Invoke("scripts\\Good.dll", "Good.Probe", "Ping")) != "pong") failures++;
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                process.Refresh();
                int handlesAfter = process.HandleCount;
                long memoryAfter = GC.GetTotalMemory(true);

                long growthMb = (memoryAfter - memoryBefore) / (1024 * 1024);
                runner.Check(failures == 0, "500 次调用应全部成功，失败 " + failures + " 次");
                runner.Check(handlesAfter - handlesBefore < 200, "句柄不应暴涨：" + handlesBefore + " -> " + handlesAfter);
                runner.Check(growthMb < 60, "托管堆增长应可控：" + growthMb + "MB");
                runner.Note("句柄 " + handlesBefore + "->" + handlesAfter + "，堆增长 " + growthMb + "MB（每次调用都会加载新副本，属预期累积）");

                // 500 次加载之后，再验证"文件仍可覆盖 → 覆盖后拿到新结果"
                string overwritePath = Path.Combine(scripts, "overwrite-check.dll");
                File.Copy(Path.Combine(scripts, "Good.dll"), overwritePath, true);
                runner.Expect("覆盖前", Runner.Text(runner.Invoke("scripts\\overwrite-check.dll", "Good.Probe", "Ping")), "pong");
                File.Copy(Path.Combine(scripts, "GoodV2.dll"), overwritePath, true);
                runner.Expect("覆盖后", Runner.Text(runner.Invoke("scripts\\overwrite-check.dll", "Good.Probe", "Ping")), "pong-v2");
                runner.Note("源文件未被占用（可直接覆盖重建）");
            });

            runner.Case("E-14", "50MB 垃圾文件（大小边界）", () =>
            {
                string bigPath = Path.Combine(scripts, "big.dll");
                if (!File.Exists(bigPath) || new FileInfo(bigPath).Length < 50L * 1024 * 1024)
                {
                    using (var stream = new FileStream(bigPath, FileMode.Create, FileAccess.Write))
                    {
                        stream.SetLength(50L * 1024 * 1024);
                    }
                }

                var watch = Stopwatch.StartNew();
                string response = runner.Invoke("scripts\\big.dll", "X", "Y");
                watch.Stop();

                runner.Check(Runner.IsError(response), "50MB 垃圾文件应报错: " + Runner.ResultJson(response));
                runner.Check(watch.ElapsedMilliseconds < 5000, "应在 5s 内失败，实际 " + watch.ElapsedMilliseconds + "ms");
                runner.Note("耗时 " + watch.ElapsedMilliseconds + "ms，异常 " + FirstLine(Runner.Text(response)));
            });

            runner.Case("E-18", "目标异常 ToString() 为空串时，错误文本仍须是 类型+消息+堆栈", () =>
            {
                string response = runner.Invoke("scripts\\Weird.dll", "Weird.Methods", "EmptyToString");
                runner.Check(Runner.IsError(response), "应 isError: " + Runner.ResultJson(response));

                string text = Runner.Text(response);
                runner.Check(!string.IsNullOrEmpty(text), "错误文本不能为空串（实机 Mono 上曾因依赖 Exception.ToString() 而变空）");
                runner.ExpectContains("异常类型", text, "Weird.EmptyToStringException");
                runner.ExpectContains("异常消息", text, "empty-tostring-boom");
                runner.ExpectContains("堆栈", text, "Weird.Methods.EmptyToString");
            });

            runner.Case("E-15", "目标方法访问 Unity API（实机）", () => { }, skip: true, skipReason: "需要实机：验证在 Unity 主线程执行");
            runner.Case("E-16", "目标方法 Sleep 超过 TimeoutSeconds（实机）", () => { }, skip: true, skipReason: "需要实机：验证超时返回 + 卡帧后恢复");
            runner.Case("E-17", "死循环 / 递归爆栈（实机，手工）", () => { }, skip: true, skipReason: "需要实机且会真卡死/崩溃：仅人工，见 MANUAL_L3_L4.md");
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "(empty)";
            int index = text.IndexOf('\n');
            return index < 0 ? text : text.Substring(0, index);
        }
    }
}
