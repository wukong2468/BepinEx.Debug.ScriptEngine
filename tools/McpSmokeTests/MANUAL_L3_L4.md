# MCP 服务器冒烟测试 · 实机与集成清单（L3 / L4）

离线部分（L1/L2，A~H 组共 84 条）由 `tools/run-smoke.ps1` 自动执行；下面这些用例依赖 Unity / BepInEx / 真实 MCP 客户端，只能人工执行。每条都遵守同一判定规则：**做完动作后必须还能正常调用（`GET /health` + 一次 `invoke_method`）**。

## 0. 前置

1. 复制 `ScriptDebugEngine\bin\Release\net35\ScriptDebugEngine.dll` 到 `D:\ProgramPortable\Custom Order Maid\COM3D2_5\BepInEx\plugins\`
2. 重启游戏，确认 `BepInEx\LogOutput.log` 出现：`MCP server listening on http://127.0.0.1:8765/mcp`
3. 在 `BepInEx\scripts\` 放一个带探针方法的 DLL，例如：

```csharp
namespace Probe
{
    public static class Main
    {
        public static string Ping() { return "pong"; }              // 基础探活
        public static int Frame() { return UnityEngine.Time.frameCount; }   // 主线程验证
        public static string Make()                                  // 主线程 + Unity API 验证
        {
            var go = UnityEngine.GameObject.Find("SybarisLoader");
            return go == null ? "no-object" : go.name;
        }
        public static string Slow() { System.Threading.Thread.Sleep(60000); return "slow-done"; }
    }
}
```

（`Slow()` 用于超时用例：把 `TimeoutSeconds` 配成 5，避免等太久。）

## L3 游戏内清单

| ID | 操作 | 期望观察 |
|---|---|---|
| E-15 | 调 `Probe.Main.Frame()` 与 `Probe.Main.Make()` | 都成功返回（证明在 Unity 主线程执行，能访问 Unity API）；游戏画面在执行瞬间有极短卡顿 |
| E-16 | 把 `TimeoutSeconds` 改成 5，调 `Probe.Main.Slow()` | HTTP 侧约 5 秒后返回 `isError:true`、文本含 `did not finish within 5000 ms`；**游戏在这 60 秒内卡住**；`Slow()` 跑完后再次调 `Ping()` 必须成功（证明 busy 标志已释放、队列正常） |
| E-17 | （危险，可选）让目标方法死循环或无限递归 | 死循环：游戏卡死，只能杀进程；无限递归：`StackOverflow` **直接杀掉游戏进程**。用于确认"超时只解 HTTP 等待、不中止主线程"这一已知限制 |
| F-04 | 用两个客户端（如两个终端 curl）几乎同时调 `Ping()` 与 `Slow()`（`Slow` 先发） | 第二个请求立刻返回 `isError:true`、文本含 `The engine is busy`；`Slow` 结束后再调 `Ping()` 成功 |
| F-05 | 在 E-16 超时后立刻连发 5 次 `Ping()` | 全部正常返回（或短暂 busy），不崩、日志无异常堆栈 |
| G-01 | 用 ConfigurationManager 把 `[Mcp] Enabled` 取消勾选，再勾回来 | 取消后：`curl http://127.0.0.1:8765/health` 连接失败，日志出现 `MCP server stopped`；勾回后：立刻又能访问，日志出现 `MCP server listening on ...`（无需重启游戏） |
| G-05 | 直接编辑 `BepInEx\config\com.github.wukong2468.scriptdebugengine.cfg` 改 `Port`，然后在 ConfigurationManager 里点 Reload（或重启游戏） | 旧端口不再响应、新端口可用。**实测：只改文件、不 Reload 是不生效的**（BepInEx 5.4 无配置文件监视器） |
| G-06 | 退出游戏 | 端口被释放：退出后立刻用 `netstat -ano | findstr 8765` 看不到监听；再次启动游戏能正常绑定 |

## L4 集成清单（DSH / 其它 MCP 客户端）

| ID | 操作 | 期望观察 |
|---|---|---|
| I-01 | 在 DSH 连接器管理里登记 `streamable-http` + `http://127.0.0.1:8765/mcp` | 连接成功；`tools/list` 能看到唯一的 `invoke_method`，描述为英文 |
| I-02 | 让 agent 调 `invoke_method`（指向真实脚本 DLL 的探针方法） | 返回结果文本；`BepInEx\LogOutput.log` 出现 `Hot loading '...' for ...` |
| I-03 | 重新构建脚本 DLL → 让 agent 再调 10 次 | 每次都拿到最新构建的结果（验证热加载闭环），无 GUID 冲突报错 |
| I-04 | 重启游戏后再让 agent 调一次 | 客户端重连成功（无需改配置） |

## 已执行的实机结果（2026-09-13，游戏运行中，经 DSH 的 `bepinex-mcp` 连接器）

> 第二轮（重启游戏加载修好的引擎后）已复测：E-06 / E-18 ✅；I-04 ✅；F-05 ✅；G-06 ✅。除 E-17 外全部完成。

| ID | 结果 |
|---|---|
| I-01 连接与工具发现 | ✅ DSH 连接器可见唯一工具 `invoke_method`（英文描述） |
| I-02 端到端调用 | ✅ `Ping → pong`；`Loud → logged`（日志出现 `[Probe] Loud was called on the main thread`） |
| I-03 连续调用 + 热重载 | ✅ 连续 10 次全部成功；重建探针后同一路径 `Mark → PROBE-V2`（无需重启游戏） |
| I-04 重启后重连 | ✅ 重启游戏后日志 `MCP server listening ...`，`health`/`Ping` 正常 |
| E-15 主线程 Unity API | ✅ `unity=2022.3.62f2 platform=WindowsPlayer frame=11269`（真实运行时的 Unity，不是编译引用 5.6.4）；`Find → scene=SceneTitle roots=5 loaded=True` |
| E-06 目标方法抛异常 | ✅ **修复后复测通过**：`System.InvalidOperationException: boom from Probe` + 完整堆栈（修复前是空文本） |
| E-18 `ToString()` 为空的异常 | ✅ **修复后复测通过**：`Probe.Main+EmptyToStringException: empty-tostring-boom` + 堆栈 |
| 失败留痕 | ✅ 日志出现 `invoke_method failed for ... -> ...`（本次修复新增） |
| E-16 超时 | ✅ `Sleep35` 在 ~30s 后 `TimeoutException: Invocation did not finish within 30000 ms.`；其间游戏主线程冻结（预期） |
| E-16b 超时后恢复 | ✅ 慢调用结束后 `Ping → pong` |
| F-04 busy 拒绝 | ✅ 并发第二个请求 **10ms** 返回 `The engine is busy executing another invocation.` |
| F-05 超时后立即重发 5 次 | ✅ 全部成功（#1 等 3056ms 排在慢调用之后，#2~#5 16~19ms）；**实测确认"重试会被接受并排队、随后依次执行"** |
| G-01 运行时启停 | ✅ `Enabled=false` + Reload → 端口关闭 + 日志 `MCP server is disabled by config`（**注意**：关闭后无远程手段再打开，需 ConfigurationManager 或重启） |
| G-03/G-05 运行时换端口 | ✅ 改 `Port` + `Config.Reload()`：旧端口立即关闭、新端口开始服务（日志 `stopped` → `listening`） |
| G-06 退出释放端口 | ✅ 退出游戏后 `netstat` 里 8765 已无监听 |
| 目标 DLL 发现 | 记录：`COM3D2.ExpandCheatMenu.ExpandCheatMenu` 存在，但**没有 public static 无参方法**，所以目前无法用 `invoke_method` 驱动它 |

## 剩余（仅 E-17，属"故意不做"）

| ID | 说明 |
|---|---|
| E-17 死循环 / 递归爆栈 | 会真的卡死或崩溃游戏进程（`StackOverflow` 不可捕获），仅在明确接受风险时手工做；这是"超时只解 HTTP 等待、不中止主线程"这一已知限制的直接体现 |

## 记录要求

- 每条用例记录：时间、动作、**原始响应片段**、`LogOutput.log` 相关行、是否符合期望。
- 任何"游戏卡死/崩溃"都要单独记一条，并注明是目标函数本身的问题还是服务器的问题。
- 实机结果附在 `tools/McpSmokeTests/smoke-report.json` 之外单独记录（该文件只保存离线用例结果）。
