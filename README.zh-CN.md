# ScriptDebugEngine

一个 BepInEx 5.4 插件：在 Unity 游戏进程内常驻一个**小型 MCP（Model Context Protocol）HTTP 服务器**，让 AI agent 可以**热加载一个 DLL、在游戏里执行它的无参静态 C# 方法、并拿到返回值**。

面向 Unity 2022.3.62f2 + BepInEx 5.4.23 Windows 开发并验证，编译目标为 **.NET Framework 3.5**。

> English version: [README.md](README.md)。

---

## 功能

- 唯一工具：`invoke_method(dllPath, typeName, methodName)`（**不向目标函数传参**，目标方法必须无参）
- **每次调用都加载新副本**（程序集名加时间戳、从内存加载）→ 改完代码立即生效，不用重启游戏
- 在 **Unity 主线程**执行 → 目标方法可以访问 Unity API
- 成功时返回纯文本结果；失败时返回 `异常类型: 消息` + 内层异常链 + 堆栈
- **方法名写错也没关系**：错误信息会列出该类型所有可用的无参静态方法（内置的"发现"机制，因此不需要额外的列函数工具）
- 只绑回环 `127.0.0.1`；MCP Streamable HTTP + JSON-RPC 2.0；另有 `GET /health`
- MCP 层（`ScriptDebugEngine/Mcp/`）**零第三方依赖**：HTTP 服务器与 JSON 解析器都是自研精简实现；这一层刻意不引用 UnityEngine / BepInEx，因此可以脱离游戏做离线测试
- 四个配置项支持运行时调整：`Enabled`、`Port`、`TimeoutSeconds`、`AllowAnyPath`

## 环境要求

- Windows + .NET SDK（仅编译需要；开发时用 SDK 9）
- BepInEx 5.4.x（按 5.4.21 引用程序集编译，实机验证于 5.4.23）
- .NET Framework 3.5 引用程序集（由 `Microsoft.NETFramework.ReferenceAssemblies` 自动还原）
- NuGet 源：`nuget.org` + BepInEx 源 + Samboy 源（见 `NuGet.config`；`COM3D2.GameLibs` 不在 nuget.org 上）

## 编译与部署

```powershell
dotnet build ScriptDebugEngine\ScriptDebugEngine.csproj -c Release
# 产物：ScriptDebugEngine\bin\Release\net35\ScriptDebugEngine.dll
```

部署步骤：

1. **先关闭游戏**。运行中的实例会把插件 DLL 内存映射锁定，游戏开着时覆盖会报 `user-mapped section open`；
2. 把 `ScriptDebugEngine.dll` 复制到 `BepInEx\plugins\`；
3. 启动游戏，`BepInEx\LogOutput.log` 里应出现：

```
[Info   :Script Debug Engine] MCP server listening on http://127.0.0.1:8765/mcp
```

> **改引擎**（`plugins` 里的 DLL）永远需要重启游戏；**改脚本 DLL**（`scripts` 里的）不用——覆盖后下一次调用就是新代码。

## 配置

配置文件：`BepInEx\config\com.github.wukong2468.scriptdebugengine.cfg`（文件名由插件 GUID 决定，首次启动自动生成）。

| 键 | 默认 | 说明 |
|---|---|---|
| `Enabled` | `true` | 是否启动 MCP 服务器 |
| `Port` | `8765` | 监听端口（仅回环） |
| `TimeoutSeconds` | `30` | 单次请求等待主线程执行的上限 |
| `AllowAnyPath` | `false` | **危险**：允许 `invoke_method` 从**任意路径**加载 DLL，而不限于 BepInEx 根目录之下。详见"已知限制" |

运行时行为：插件每帧把当前配置与"已应用状态"比对，`Enabled`/`Port` 一变就立刻停旧起新/换端口；`TimeoutSeconds` 与 `AllowAnyPath` 都是实时读取（切换 `AllowAnyPath` 不需要重启服务器）。

**触发方式（重要）**：只有真正改变 `ConfigEntry.Value` 的操作才算——即游戏内 ConfigurationManager 勾选/输入，或代码调用 `Config.Reload()`。**手改 `.cfg` 文件不会生效**（BepInEx 5.4 没有配置文件监视器）。另外：一旦把服务器 `Enabled=false`，就**没有远程手段再打开**（调用通道本身就是这个服务器），需要用 ConfigurationManager 或重启游戏。

## 使用

在 MCP 客户端里登记：

```json
{ "mcpServers": { "script-debug-engine": { "type": "http", "url": "http://127.0.0.1:8765/mcp" } } }
```

用 curl 手工验证（Windows PowerShell 下要注意：JSON 直接写在 `-d '{...}'` 里引号会被吞，要写成文件再用 `--data-binary @文件`）：

```powershell
$tmp = "$env:TEMP\mcp-check"; New-Item -ItemType Directory -Force $tmp | Out-Null
'{ "jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"invoke_method","arguments":{"dllPath":"scripts\\MyScript.dll","typeName":"MyScript.Commands","methodName":"Ping"}} }' |
  Set-Content "$tmp\invoke.json" -Encoding ASCII -NoNewline

curl.exe -s http://127.0.0.1:8765/health
curl.exe -s -X POST http://127.0.0.1:8765/mcp -H "Content-Type: application/json" --data-binary "@$tmp\invoke.json"
```

成功：

```json
{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"pong"}],"isError":false}}
```

失败（`isError:true`，文本是异常类型 + 消息 + 堆栈）：

```json
{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text",
  "text":"System.InvalidOperationException: boom\n  at MyScript.Commands.Ping () ..."}],"isError":true}}
```

## 工具契约

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `dllPath` | string | 是 | 绝对路径，或相对 BepInEx 根目录的路径（如 `scripts\MyScript.dll`）。解析后**必须位于 BepInEx 根目录之下**（除非打开了 `[Mcp] AllowAnyPath`） |
| `typeName` | string | 是 | 类型全名、短名或点号后缀（如 `MyScript.Commands`、`Commands`） |
| `methodName` | string | 是 | **public static 且无参数**的方法名 |

结果规则：

| 目标方法返回 | `text` |
|---|---|
| `string` | 原样 |
| 其它类型 | `ToString()`（数值用 InvariantCulture） |
| `null` | `null` |
| `void` | `OK` |
| 抛异常 | `异常类型: 消息` + 换行 + 堆栈（`isError: true`） |
| DLL/类型/方法找不到 | 明确提示；方法名写错时附带该类型可用的无参静态方法列表 |

目标方法要求：`public static`、无参、同步（返回 `Task`/`IEnumerator` **不会被等待**，只会被 `ToString()`）、并且**建议幂等**（客户端重试会真的再执行一次）。

## 实现结构

```
AI agent ──MCP Streamable HTTP (JSON-RPC 2.0)──▶ ScriptDebugEngine.dll (BepInEx\plugins)
                                                      │
                                     McpServer（TcpListener + 极简 HTTP/1.1）
                                     initialize / ping / tools/list / tools/call
                                                      │  lock + Queue<Action>
                                                      ▼
                                     插件 Update() 泵 → 主线程
                                     Invoker：改名 + Assembly.Load(byte[]) → 反射 → 调用
                                                      │
                                                      ▼
                                     BepInEx\scripts\*.dll（每次调用加载新副本）
```

| 文件 | 职责 |
|---|---|
| `ScriptDebugEngine/Mcp/McpServer.cs` | TcpListener + 极简 HTTP/1.1 + JSON-RPC 分发 + 唯一工具 |
| `ScriptDebugEngine/Mcp/Invoker.cs` | 路径白名单、类型/方法定位、调用、结果/异常文本 |
| `ScriptDebugEngine/Mcp/ScriptLoader.cs` | Cecil 加载：改名 → `Assembly.Load(byte[])`；不读符号、不注册插件 |
| `ScriptDebugEngine/Mcp/Json.cs` | 自研精简 JSON 解析/生成（含深度上限与代理对转义） |
| `ScriptDebugEngine/ScriptDebugEngine.cs` | 插件入口：配置、服务器启停、主线程任务队列泵 |

## 测试

| 层次 | 怎么跑 | 状态 |
|---|---|---|
| 离线（127 条用例） | `tools\run-smoke.ps1`：构建引擎与测试 DLL、起离线宿主（链接 `Mcp/*.cs`）、跑 A–J 组、生成 `smoke-report.json` | **114 PASS / 0 FAIL / 13 SKIP**，约 26 秒 |
| 实机（13 项） | 把 `tools/ProbeDll/` 部署成 `BepInEx\scripts\Probe.dll`，用真实 MCP 客户端驱动 | 已完成 12 项（主线程、超时、busy、重试、热重载、动态配置、端口释放、重启重连） |
| 手工清单 | `tools/McpSmokeTests/MANUAL_L3_L4.md` | 剩 1 项：E-17（故意让目标函数死循环/爆栈——会真的卡死或崩溃游戏） |

逐条证据与方案细节见 [docs/MCP_SMOKE_TEST.md](docs/MCP_SMOKE_TEST.md)、[docs/MCP_DESIGN.md](docs/MCP_DESIGN.md)。

## 仓库结构

```
ScriptDebugEngine/          插件源码（net35）
  Mcp/                      MCP 层：HTTP + JSON + JSON-RPC + 调用器（不依赖 Unity/BepInEx）
docs/                       设计文档、冒烟测试方案与结果
tools/run-smoke.ps1          一键离线冒烟
tools/McpSmokeTests/         离线测试宿主、用例、测试 DLL 资产、实机清单
tools/ProbeDll/              实机探针（部署为 BepInEx\scripts\Probe.dll）
reference/BepInEx.Debug/     上游只读快照
README.md / README.zh-CN.md  英文版 / 中文版说明
AGENTS.md                    AI 协作规则
```

## 已知限制

1. 仅绑定 IPv4 回环 `127.0.0.1`；无鉴权 Token、无 TLS。本机任意进程都能调用。
2. 不做 SSE（只回 JSON）；不支持 `Transfer-Encoding: chunked`；不支持 JSON-RPC batch；请求体上限 64 KB。
3. 同一时刻只允许一个调用——并发请求会收到 `The engine is busy executing another invocation.`
4. 超时只结束 HTTP 等待，**不会中止**正在跑的主线程方法（游戏会一直卡到它返回）。目标方法死循环/爆栈会卡死或崩溃游戏。
5. 超时后 busy 标志立即释放，但慢调用仍在跑；此时重试会被接受并排队、随后依次执行（实测：超时后重发 5 次全部成功，第 1 次等了 3056ms）。待执行队列**没有上限**。
6. 返回值长度无上限（4MB 字符串实测可正确往返，更大的会整段驻留内存）。
7. 不读 `.pdb` → 异常堆栈没有行号。
8. 每次调用都会加载新程序集副本 → 反复调用会累积内存（用得多了重启一次游戏）。
9. 白名单只检查目标文件本身是否为符号链接；**中间目录是 junction/符号链接时仍可指向根目录之外**。
10. `HEAD /mcp` 的响应会带 body（无害的 HTTP 小瑕疵）。
11. 路径白名单可以通过 `[Mcp] AllowAnyPath` 关掉（默认 `false`）。开启后 `invoke_method` 能加载并执行**机器上任意 DLL**——本机任何进程都能借此获得任意代码执行能力，AI agent 也可能被注入内容诱导去做这件事；此时"仅回环绑定"是剩下的唯一边界。只有当你确实需要调用 BepInEx 目录之外的 DLL（例如直接调构建产物）时才打开它。
