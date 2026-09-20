# AGENTS.md · AI 协作规则

> 本文件规定在本仓库工作的 AI（以及人类协作者）必须遵守的规则。改动任何代码/文档前先读完本文件。
> 项目背景与用法见 `README.md`（英文）/ `README.zh-CN.md`；设计与测试细节见 `docs/`。

---

## 1. 语言规范（不可违反）

| 内容 | 语言 |
|---|---|
| 代码注释、XML/结构化文档注释 | 简体中文 |
| 日志、异常消息、错误信息、运行时诊断信息 | **英文** |
| 变量名、函数名、类型名等一切代码标识符 | 英文 |
| 面向最终用户的 UI 提示/按钮/标签 | 简体中文 |
| 面向调用方的接口文本（MCP 工具描述、参数描述、`isError` 文本） | **英文** |
| `README.md` | 英文 |
| `README.zh-CN.md`、`docs/*.md`、`AGENTS.md` | 简体中文 |

例外说明：MCP 的 `tools/list` 是给 AI agent 读的"接口文本"，一律英文；如果将来加了游戏内 UI，那一部分才用中文。

## 2. 技术选型

- 需要选型时，**先给足选项 + 说明原因与代价，再向用户提问**；给出推荐项，不要替用户静默决定。
- 用户已明确"极简/不要多余功能"时，不要再引入可选项、开关和抽象层。

## 3. 目录与职责

| 路径 | 说明 |
|---|---|
| `ScriptDebugEngine/` | 插件本体（BepInEx 插件，`net35`）。产物 `bin/<Config>/net35/ScriptDebugEngine.dll` → 部署到 `BepInEx\plugins\` |
| `ScriptDebugEngine/Mcp/` | MCP 层：**刻意不依赖 UnityEngine / BepInEx**，因此可离线测试；不要在这一层引入 Unity 类型 |
| `tools/McpSmokeTests/` | 离线冒烟测试（net48，直接链接 `Mcp/*.cs`）+ 测试 DLL 资产 |
| `tools/ProbeDll/` | 实机探针（net35），部署到 `BepInEx\scripts\Probe.dll`，供实机用例调用 |
| `tools/run-smoke.ps1` | 一键：构建 → 准备测试目录 → 跑离线用例 → 生成 `smoke-report.json`（退出码 = 失败数） |
| `docs/MCP_DESIGN.md` | 设计文档（实现要点、配置、约定、已知取舍、未修项） |
| `docs/MCP_SMOKE_TEST.md` | 冒烟方案 + 结果（离线/实机） |
| `tools/McpSmokeTests/MANUAL_L3_L4.md` | 实机与集成用例的逐项结果与剩余项 |
| `reference/` | **外部参考项目快照（只读）**：上游 BepInEx.Debug（LGPL-3.0）。禁止修改、移动或删除，也不要让它参与任何构建（SDK 式工程的 `**/*.cs` 通配符会误包含它 —— 所以本工程放在子目录里） |

## 4. 改动流程（必须遵守）

1. **改 `ScriptDebugEngine/Mcp/**` 之后必须跑 `tools\run-smoke.ps1`，要求 0 FAIL**（当前基线：134 条用例 121 PASS / 0 FAIL / 13 SKIP）。
2. **改了行为就补用例**：优先补离线用例（`tools/McpSmokeTests/Cases/`）；离线覆盖不到的主线程/超时/配置类行为补到实机清单或探针里。
3. **引擎（`plugins`）改动 → 必须"关游戏 → 覆盖 DLL → 重启游戏"**：运行中的实例会把 DLL 内存映射锁定（覆盖会报 `user-mapped section open`）。脚本 DLL（`scripts`）可以直接覆盖，下一次调用即生效。
4. **文档与代码同步**：改行为 → 更新 `docs/MCP_DESIGN.md`；改测试 → 更新 `docs/MCP_SMOKE_TEST.md` 与 `MANUAL_L3_L4.md`；改用法/配置 → 更新两份 README。
5. **构建门槛**：`dotnet build ScriptDebugEngine/ScriptDebugEngine.csproj -c Release` 必须是 0 warning / 0 error（离线环境下的 `NU1900` 漏洞审计警告可忽略，或用 `-p:NuGetAudit=false` 关掉）。
6. **依赖克制**：`Mcp/` 层零第三方依赖是硬约束（JSON 解析器是自研的）。未经用户同意不要新增 `PackageReference`；测试工程里可以用 `Mono.Cecil` / `Newtonsoft.Json`。
7. **不要动用户的个人环境**：不修改游戏配置以外的文件；需要写游戏目录（部署 DLL、改 cfg）时先确认状态（游戏是否在运行），并在完成后说明做了什么。

## 5. 必须知道的坑（都踩过，别再踩）

**net35 / Unity / BepInEx**
- net35 编译面没有 `ConcurrentQueue`/`Task`/`async-await`/`ManualResetEventSlim`；线程间通信用 `lock + Queue<T>` + `Monitor.Wait/Pulse`。
- 游戏的 `Assembly-CSharp` 里有一个**全局命名空间的 `Monitor` 类型**，会遮蔽 `System.Threading.Monitor` → 必须写全名，否则 `CS0117`。
- Unity 运行时是 **2022.3.62f2**，而编译引用是 `UnityEngine.Modules 5.6.4`；新增代码只用两代都有的 API。
- 引擎与脚本必须区分清楚：`plugins` 里的引擎改动要重启游戏，`scripts` 里的脚本 DLL 每次调用都重新加载。

**Mono / Cecil / 反射**
- **Mono 上 `Exception.ToString()` 对"从 `Assembly.Load(byte[])` 加载的程序集抛出的异常"会返回空串**（`Message`/`StackTrace` 正常）→ 错误文本必须自己拼「类型: 消息 + InnerException 链 + 堆栈」（`Invoker.DescribeException`）。
- `AssemblyDefinition.ReadAssembly(..., ReadSymbols = true)` 在找不到 `.pdb`/`.mdb` 时会抛 `SymbolsNotFoundException` → 调用路径一律 `ReadSymbols = false`。
- 热加载靠"改写程序集名 + `Assembly.Load(byte[])`"；Cecil 定义必须及时 `Dispose`，否则 DLL 文件被占用、无法重新构建覆盖。

**HTTP / JSON（自研实现）**
- `Expect: 100-continue` 必须先回 `100 Continue` 再读正文，否则部分 .NET 客户端会卡住。
- 每次 `Read` 之前按剩余时限设 `ReceiveTimeout`（头部 5s / 正文 10s / 丢弃 2s），否则慢客户端能长期占线程。
- 拒绝超大正文前要先把正文读完（否则客户端看不到 413）。
- **JSON 解析必须有嵌套深度上限**：递归下降遇到几万层嵌套会 `StackOverflow`，该异常**不可捕获、直接杀进程**（已实测：退出码 `-1073741571` = `0xC00000FD` = `STATUS_STACK_OVERFLOW`）。
- **孤立代理（unpaired surrogate）必须转义成 `\uXXXX`**，否则 UTF-8 编码会把它替换成 U+FFFD（静默改数据）。
- 响应体不要用被测自己的 JSON 类去验证；用**严格 UTF-8 解码 + `Content-Length` 字节数校验 + 独立解析器**。

**Windows / PowerShell**
- Windows PowerShell 5.1：`curl.exe -d '{...}'` 的引号会被吞（表现为 `-32700 ... position 1`）→ 用 `--data-binary "@文件"`；`Invoke-WebRequest` 的字符串 body 会按 ANSI 发送 → 显式传 UTF-8 字节。
- 含中文的 `.ps1` 必须存成 **UTF-8 with BOM**，否则 PS 5.1 会按 ANSI 解析报语法错。
- 删除含 junction/符号链接的目录前，先用 `cmd /c rmdir` / `del` 删链接本身，避免 `Remove-Item -Recurse` 跟随链接。

## 6. 验证与诚实性要求

- **没有证据不算通过**："实现了/通过了/修好了"必须给出可复现证据：用例 id、原始响应片段、日志行、命令输出。
- **断言不要依赖本地化文本**（中文/英文异常消息在不同运行时不同）→ 只断言类型名、结构、错误码。
- **发现缺陷先写复现用例，再改代码**；修完后必须重跑整套离线冒烟，并检查"没有把别的用例搞红"（本轮就发生过：修异常文本时漏了内层异常链，被 E-07 当场抓住）。
- 已知限制、未修项、未测面要**明确写进文档**，不要含糊过去或假装不存在。
- 不要把"没测过"说成"没问题"。

## 7. 危险操作（需用户明确同意）

- 让目标函数死循环/递归爆栈（会真的卡死或崩溃游戏进程）。
- 在游戏运行中触发长时间阻塞的调用（会让游戏主线程冻结数十秒）。
- 覆盖/删除游戏目录里的文件；删除 `reference/` 下的任何内容。
- `git commit` / `push`：本仓库目前**没有任何提交**，提交前先与用户确认。
- **擅自把 `[Mcp] AllowAnyPath` 默认值改成 `true`**，或在没有用户明确要求时开启它：那会让 `invoke_method` 能加载执行机器上任意 DLL（等于重开任意代码执行面）。它存在的意义是"用户主动打开的逃生开关"，不是默认行为。
