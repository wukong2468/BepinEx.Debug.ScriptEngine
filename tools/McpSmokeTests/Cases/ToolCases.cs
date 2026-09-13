using System;
using System.Diagnostics;
using System.IO;

namespace McpSmokeTests
{
    /// <summary>D 组：工具参数（dllPath）与安全边界。</summary>
    internal static class ToolCases
    {
        public static void Register(Runner runner)
        {
            string scripts = runner.Env.Scripts;
            string root = runner.Env.Root;

            runner.Case("D-01", "相对路径", () =>
            {
                runner.Expect("相对路径调用", Runner.Text(runner.Invoke("scripts\\Good.dll", "Good.Probe", "Ping")), "pong");
            });

            runner.Case("D-02", "绝对路径（含中文目录）", () =>
            {
                string absolute = Path.Combine(scripts, "Good.dll");
                runner.Expect("绝对路径调用", Runner.Text(runner.Invoke(absolute, "Good.Probe", "Ping")), "pong");
            });

            runner.Case("D-03", "点斜杠前缀 / 正斜杠", () =>
            {
                runner.Expect("点斜杠", Runner.Text(runner.Invoke(".\\scripts\\Good.dll", "Good.Probe", "Ping")), "pong");
                runner.Expect("正斜杠", Runner.Text(runner.Invoke("scripts/Good.dll", "Good.Probe", "Ping")), "pong");
            });

            runner.Case("D-04", "越权路径被拒", () =>
            {
                string response = runner.Invoke("C:\\Windows\\System32\\advapi32.dll", "X", "Y");
                runner.Check(Runner.IsError(response), "应 isError: " + Runner.ResultJson(response));
                runner.ExpectContains("错误信息", Runner.Text(response), "Access denied");
            });

            runner.Case("D-05", ".. 穿越被拒", () =>
            {
                string response = runner.Invoke("scripts\\..\\..\\..\\..\\Good.dll", "Good.Probe", "Ping");
                runner.Check(Runner.IsError(response), "穿越路径应被拒: " + Runner.ResultJson(response));
                runner.ExpectContains("错误信息", Runner.Text(response), "Access denied");
            });

            runner.Case("D-06", "同前缀兄弟目录被拒（fakeroot2）", () =>
            {
                string sibling = root + "2";
                string siblingScripts = Path.Combine(sibling, "scripts");
                Directory.CreateDirectory(siblingScripts);
                File.Copy(Path.Combine(scripts, "Good.dll"), Path.Combine(siblingScripts, "Good.dll"), true);

                string response = runner.Invoke("..\\" + Path.GetFileName(sibling) + "\\scripts\\Good.dll", "Good.Probe", "Ping");
                runner.Check(Runner.IsError(response), "同前缀兄弟目录应被拒: " + Runner.ResultJson(response));
                runner.ExpectContains("错误信息", Runner.Text(response), "Access denied");
            });

            runner.Case("D-07", "UNC 路径被拒", () =>
            {
                string response = runner.Invoke("\\\\localhost\\c$\\Windows\\System32\\advapi32.dll", "X", "Y");
                runner.Check(Runner.IsError(response), "UNC 应被拒: " + Runner.ResultJson(response));
            });

            runner.Case("D-08", "\\\\?\\ 长路径前缀（fail-closed）", () =>
            {
                string absolute = Path.Combine(scripts, "Good.dll");
                string response = runner.Invoke("\\\\?\\" + absolute, "Good.Probe", "Ping");
                runner.Check(Runner.IsError(response), "\\\\?\\ 前缀应被拒: " + Runner.ResultJson(response));
                runner.Note("不可归一化的长路径前缀被 fail-closed 拒绝（可接受）");
            });

            runner.Case("D-09", "符号链接指向白名单之外", () =>
            {
                string outsideDir = Path.Combine(Path.GetDirectoryName(root), Path.GetFileName(root) + "-outside");
                string target = Path.Combine(outsideDir, "Good.dll");
                string link = Path.Combine(scripts, "link-out.dll");

                if (!TryCreateFileSymlink(link, target))
                {
                    runner.Note("当前环境无法创建文件符号链接（需要管理员或开发者模式）→ 记录为未验证");
                    return;
                }

                string response = runner.Invoke("scripts\\link-out.dll", "Good.Probe", "Ping");
                runner.Check(Runner.IsError(response), "符号链接应被拒绝: " + Runner.ResultJson(response));
                runner.ExpectContains("错误信息", Runner.Text(response), "symbolic link");
                runner.Note("符号链接（指向根外）被拒绝 ✔");
            });

            runner.Case("D-09b", "根内目录 junction 指向根外（已知限制）", () =>
            {
                string outsideDir = Path.Combine(Path.GetDirectoryName(root), Path.GetFileName(root) + "-outside");
                string junction = Path.Combine(root, "scripts-junction");

                if (!TryCreateDirectoryJunction(junction, outsideDir))
                {
                    runner.Note("无法创建目录 junction → 记录为未验证");
                    return;
                }

                string response = runner.Invoke("scripts-junction\\Good.dll", "Good.Probe", "Ping");
                bool allowed = Runner.Text(response) == "pong";
                runner.Check(allowed, "junction 目录下的 DLL 当前可被加载（已知限制），实际: " + Runner.ResultJson(response));
                runner.Note("中间目录为 junction 时可绕过白名单：已知限制，已写进方案 §6");
            });

            runner.Case("D-10", "目录 / 不存在 / 大小写变体", () =>
            {
                string directory = runner.Invoke("scripts", "Good.Probe", "Ping");
                runner.Check(Runner.IsError(directory), "目录应报错: " + Runner.ResultJson(directory));
                runner.ExpectContains("目录提示", Runner.Text(directory), "DLL not found");

                string missing = runner.Invoke("scripts\\NoSuch.dll", "X", "Y");
                runner.ExpectContains("不存在提示", Runner.Text(missing), "DLL not found");

                runner.Expect("大小写变体", Runner.Text(runner.Invoke("SCRIPTS\\GOOD.DLL", "Good.Probe", "Ping")), "pong");
            });

            runner.Case("D-11", "路径含空格与特殊字符", () =>
            {
                string name = "Good (copy)#1%&'.dll";
                File.Copy(Path.Combine(scripts, "Good.dll"), Path.Combine(scripts, name), true);
                runner.Expect("特殊字符路径", Runner.Text(runner.Invoke("scripts\\" + name, "Good.Probe", "Ping")), "pong");
            });

            runner.Case("D-12", "超长路径（>260 字符）", () =>
            {
                string longPath = "scripts\\" + new string('d', 300) + ".dll";
                string response = runner.Invoke(longPath, "Good.Probe", "Ping");
                runner.Check(Runner.IsError(response), "超长路径应给出明确错误: " + Runner.ResultJson(response));
                runner.Note("异常: " + FirstLine(Runner.Text(response)));
            });

            runner.Case("D-13", "空 / 空白 / null 参数", () =>
            {
                runner.Check(Runner.IsError(runner.Invoke(string.Empty, "T", "M")), "空 dllPath 应 isError");
                runner.Check(Runner.IsError(runner.Invoke("   ", "T", "M")), "空白 dllPath 应 isError");

                const string json = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"invoke_method\",\"arguments\":{\"dllPath\":null,\"typeName\":\"T\",\"methodName\":\"M\"}}}";
                runner.Check(Runner.IsError(runner.Rpc(json)), "null dllPath 应 isError");
            });

            runner.Case("D-14", "AllowAnyPath 默认必须为 false，且越权提示要给出开关提示", () =>
            {
                var fresh = new ScriptDebugEngine.Mcp.InvokeContext();
                runner.Check(!fresh.AllowAnyPath, "InvokeContext.AllowAnyPath 的默认值必须是 false");
                runner.Check(!runner.Env.Context.AllowAnyPath, "运行中的上下文默认也必须是 false");

                string response = runner.Invoke("C:\\Windows\\System32\\advapi32.dll", "X", "Y");
                runner.Check(Runner.IsError(response), "默认应拒绝越权路径: " + Runner.ResultJson(response));
                runner.ExpectContains("提示可开开关", Runner.Text(response), "AllowAnyPath");
            });

            runner.Case("D-15", "AllowAnyPath=true：允许加载 BepInEx 根目录之外的 DLL", () =>
            {
                // fakeroot-outside 是驱动脚本准备的"根目录之外"的目录（含一份 Good.dll）
                string outside = Path.GetFullPath(Path.Combine(runner.Env.Root, "..", Path.GetFileName(runner.Env.Root) + "-outside", "Good.dll"));
                try
                {
                    runner.Env.Context.AllowAnyPath = true;

                    string response = runner.Invoke(outside, "Good.Probe", "Ping");
                    runner.Check(!Runner.IsError(response), "开启后应能加载根外 DLL: " + Runner.ResultJson(response));
                    runner.Expect("根外 DLL 的返回值", Runner.Text(response), "pong");

                    // 相对路径依旧以 BepInEx 根目录为基准（这不是安全校验）
                    runner.Expect("相对路径基准不变", Runner.Text(runner.Invoke("scripts\\Good.dll", "Good.Probe", "Ping")), "pong");
                }
                finally
                {
                    runner.Env.Context.AllowAnyPath = false;
                }
            });

            runner.Case("D-16", "AllowAnyPath=true：仍保留“文件必须存在”的检查", () =>
            {
                try
                {
                    runner.Env.Context.AllowAnyPath = true;
                    string response = runner.Invoke("C:\\definitely\\not\\here\\Nope.dll", "X", "Y");
                    runner.Check(Runner.IsError(response), "不存在的文件应仍报错: " + Runner.ResultJson(response));
                    runner.ExpectContains("明确提示", Runner.Text(response), "DLL not found");
                }
                finally
                {
                    runner.Env.Context.AllowAnyPath = false;
                }
            });

            runner.Case("D-17", "AllowAnyPath=true：白名单与链接检查都不再生效（语义确认）", () =>
            {
                string absolute = Path.Combine(scripts, "Good.dll");
                try
                {
                    runner.Env.Context.AllowAnyPath = true;

                    // \\?\ 长路径前缀在受限模式下会被 fail-closed 拒绝（D-08），开启后应当可用
                    string response = runner.Invoke("\\\\?\\" + absolute, "Good.Probe", "Ping");
                    runner.Expect("\\\\?\\ 前缀不再被拦", Runner.Text(response), "pong");

                    // 越权路径也不再有前缀检查（这里指向一个真实存在的系统 DLL，会走到"解析失败"而不是"越权拒绝"）
                    string system = runner.Invoke("C:\\Windows\\System32\\advapi32.dll", "X", "Y");
                    runner.Check(Runner.IsError(system), "原生 DLL 仍会失败（只是原因不同）: " + Runner.ResultJson(system));
                    runner.Check(Runner.Text(system) != null && Runner.Text(system).IndexOf("Access denied", StringComparison.Ordinal) < 0,
                        "开启后不应再出现 Access denied，实际: " + Runner.Text(system));
                }
                finally
                {
                    runner.Env.Context.AllowAnyPath = false;
                }

                runner.Check(!runner.Env.Context.AllowAnyPath, "用例结束必须把开关还原为 false，避免影响后续用例");
            });
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "(empty)";
            int index = text.IndexOf('\n');
            return index < 0 ? text : text.Substring(0, index);
        }

        private static bool TryCreateFileSymlink(string linkPath, string targetPath)
        {
            return RunMklink("/c mklink \"" + linkPath + "\" \"" + targetPath + "\"", linkPath);
        }

        private static bool TryCreateDirectoryJunction(string junctionPath, string targetPath)
        {
            return RunMklink("/c mklink /J \"" + junctionPath + "\" \"" + targetPath + "\"", junctionPath);
        }

        private static bool RunMklink(string arguments, string expectedPath)
        {
            try
            {
                if (File.Exists(expectedPath)) File.Delete(expectedPath);
                else if (Directory.Exists(expectedPath)) Directory.Delete(expectedPath, true);

                var startInfo = new ProcessStartInfo("cmd.exe", arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (Process process = Process.Start(startInfo))
                {
                    process.WaitForExit(5000);
                    return process.ExitCode == 0 && (File.Exists(expectedPath) || Directory.Exists(expectedPath));
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
