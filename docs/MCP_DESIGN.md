# ScriptDebugEngine · MCP 热加载执行服务 设计文档

- 状态：**已实现、已部署、已验证**（离线 134 条用例 121 PASS / 0 FAIL；实机 13 项完成 12 项，仅 E-17 故意不做）
- 本版取代此前那版复杂设计：砍掉了参数编组、异步任务、方法列举、日志缓冲、状态字段等一切非必要内容
- 适用工程：`ScriptDebugEngine/`（程序集 `ScriptDebugEngine`，GUID `com.github.wukong2468.scriptdebugengine`）；产物部署到 `BepInEx\plugins`
- 目标运行环境：`D:\ProgramPortable\Custom Order Maid\COM3D2_5`（BepInEx 5.4.23，Unity 2022.3.62f2，Windows）
- 语言约定：代码注释用简体中文，日志/异常信息用英文（遵循 `AGENTS.md`）
- 相关文档：`README.md`（英文）/ `README.zh-CN.md`、`docs/MCP_SMOKE_TEST.md`、`tools/McpSmokeTests/MANUAL_L3_L4.md`

---

## 1. 一句话目标

在 `ScriptDebugEngine` 里常驻一个小型 MCP HTTP 服务器；AI agent 调用一个工具，传入 **DLL 路径 + 类型名 + 方法名**，服务器**热加载该 DLL、执行这个函数、把执行结果原样返回**。

## 2. 范围

### 2.1 做什么

- 一个 MCP 工具：`invoke_method(dllPath, typeName, methodName)`。
- 每次调用都**重新加载 DLL 的新副本**（这就是"热加载"：改完代码重新构建，下一次调用立刻生效，不用重启游戏）。
- 在 Unity 主线程执行目标函数，等它返回后把结果回给 agent。
- 引擎**只做 MCP 宿主**：原有的脚本插件装载（目录扫描、F6 重载、`FileSystemWatcher` 自动重载、`DumpAssemblies`）已按需求删除。

### 2.2 明确不做（此前方案里的多余部分全部砍掉）

| 砍掉的内容 | 原因 |
|---|---|
| 函数参数传入 / 参数编组 | 目标函数约定为**无参** |
| `dryRun`、`parameterTypes`、`reload` 等参数 | 只有 3 个参数就够了 |
| `reload_dll`、`list_scripts`、`list_methods`、`get_status`、`get_recent_logs` 工具 | 只要"执行 + 返回结果" |
| 返回值里的 `ok/exception/logs/elapsedMs/...` 状态字段 | 返回的文本就是执行结果本身 |
| 调用期间日志采集（`LogRingBuffer`） | 需要看日志时直接看 `BepInEx\LogOutput.log` |
| 异步任务 / job 轮询 / 协程 | 同步等待即可 |
| SSE 帧、会话 ID、鉴权 Token、CORS | 只回 JSON、无状态、仅本机回环 |
| 实例方法 / 构造函数调用 | 只支持 `public static` |
| 独立 scripts 目录、net472/Unity 2022 引用升级 | 本期不动工程定位 |
| 原有的**脚本插件装载**能力（扫描 `scripts`、F6 重载、`FileSystemWatcher` 自动重载、`DumpAssemblies`） | 已按需求从本插件删除；现在它只"按请求加载并执行一个函数"，不再把 `scripts` 里的 DLL 注册成 BepInEx 插件。若还需要脚本插件装载，保留原来的 `ScriptEngine.dll` |

### 2.3 目标函数的约定

- 必须是 `public static`，且**无参数**；
- 返回 `string` 或基础类型时结果最直观（其它类型走 `ToString()`）；
- 必须是同步方法（不支持 `IEnumerator`/`Task`）；
- 建议放在 `BepInEx\scripts` 下的 DLL 里（`plugins` 下的也可以，但热加载语义以 `scripts` 为主）。

---

## 3. 交互形态

### 3.1 工具定义（`tools/list` 里唯一的一项）

> 面向调用方的文本（工具描述、参数描述、错误信息）**一律用英文**；仅代码注释用中文。

```json
{
  "name": "invoke_method",
  "description": "Hot-loads the given DLL and runs one of its public static parameterless methods, then returns the execution result. If you are not sure about the method name, pass any name: the error message lists the available parameterless static methods of that type.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "dllPath":    { "type": "string", "description": "Path to the DLL: an absolute path, or a path relative to the BepInEx root (for example scripts\\Foo.dll)." },
      "typeName":   { "type": "string", "description": "Type name, for example Foo.Bar. Either the full name or a suffix match is accepted." },
      "methodName": { "type": "string", "description": "Name of a public static method that takes no parameters." }
    },
    "required": ["dllPath", "typeName", "methodName"]
  }
}
```

### 3.2 `tools/call` 请求 / 响应

请求：

```json
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{
  "name":"invoke_method",
  "arguments":{"dllPath":"scripts\\Probe.dll","typeName":"Probe.Main","methodName":"Ping"}}}
```

成功响应（`text` 就是函数返回值）：

```json
{"jsonrpc":"2.0","id":3,"result":{"content":[{"type":"text","text":"pong"}],"isError":false}}
```

失败响应（`text` 是异常信息，方便 agent 定位）：

```json
{"jsonrpc":"2.0","id":3,"result":{"content":[{"type":"text",
  "text":"System.InvalidOperationException: boom from Probe\n  at Probe.Main.Boom () ..."}],
  "isError":true}}
```

**结果规则（只有这几条）**

| 情况 | `text` |
|---|---|
| 返回 `string` | 原样 |
| 返回其它类型 | `ToString()`（数值用 InvariantCulture） |
| 返回 `null` | `null` |
| `void` | `OK` |
| 抛异常 | `异常类型: 消息` + 换行 + 堆栈，`isError: true` |
| DLL / 类型 / 方法找不到 | 明确提示；**方法找不到时附带该类型可用的无参静态方法名列表**（用错误提示代替"列函数"工具） |

---

## 4. 架构与时序

```
AI Agent ──MCP Streamable HTTP (JSON-RPC)──▶ ScriptDebugEngine.dll (BepInEx\plugins)
                                                     │
                                    ┌────────────────┴─────────────────┐
                                    │ McpServer：TcpListener 极简 HTTP │
                                    │ initialize / ping / tools/list   │
                                    │ tools/call → 入队并等待结果      │
                                    └────────────────┬─────────────────┘
                                                     │ lock + Queue（net35 无 ConcurrentQueue）
                                                     ▼
                                    ┌──────────────────────────────────┐
                                    │ 插件 Update()：主线程取出任务     │
                                    │ Invoker：Cecil 改名加载 → 反射    │
                                    │ 找 public static 无参方法 → 调用  │
                                    │ → 结果/异常 → Monitor.Pulse 唤醒  │
                                    └──────────────────────────────────┘
                                                     │
                                                     ▼
                                        BepInEx\scripts\*.dll（每次调用加载新副本）
```

一次调用的流程：

1. HTTP 后台线程收到 `tools/call`，解析出 3 个字符串参数。
2. 解析 `dllPath`（相对路径按 `BepInEx` 根目录拼），校验在白名单范围内。
3. 任务入队；HTTP 线程 `Monitor.Wait(gate, 超时)` 等待。
4. 主线程 `Update()` 取任务 → 加载 DLL 新副本 → 定位类型与方法 → 调用 → 记下结果或异常 → `Monitor.Pulse`。
5. HTTP 线程醒来，把结果文本按 MCP `content` 格式回给 agent（超时则回 `isError` + 超时提示）。

---

## 5. 关键实现要点

| 环节 | 做法 |
|---|---|
| HTTP 实现 | 自研 `TcpListener`（`127.0.0.1`）+ 极简 HTTP/1.1：读头部到 `\r\n\r\n` → 按 `Content-Length` 读体 → 处理 → 回 `Content-Length` + `Connection: close` 后关连接 |
| 读请求限时 | 头部 5 s / 正文 10 s / 超限正文丢弃 2 s，**每次 Read 前按剩余时限设 `ReceiveTimeout`**；超时分别回 `408`，头部超 8 KB 回 `431`，请求行畸形回 `400`（畸形客户端不再被静默断连） |
| **`Origin` 校验（规范 MUST）** | `IsAllowedOrigin`：`Origin` **缺席放行**（非浏览器 MCP 客户端本来就不带）；一旦出现，主机名必须是 `127.0.0.1` / `localhost` / `::1`，否则回 `403`。`Origin: null`（`file://`、沙箱 iframe）与 `file://` 一律拒 —— 沙箱同样能被攻击者利用。这是 DNS rebinding 防护：仅绑回环挡不住"恶意网页把自己的域名解析到 127.0.0.1"。校验对所有路径生效（含 `/health`） |
| **`MCP-Protocol-Version` 校验（2025-06-18 起 MUST）** | 读取该头；不在 `SupportedProtocolVersions`（`2025-03-26` / `2025-06-18`）内 → `400`，错误文本列出受支持版本。**缺头不报错**（按 2025-03-26 兼容处理）。`2024-11-05` 已从支持列表移除：它走旧的 HTTP+SSE 传输（GET 打开 SSE），与本 Streamable HTTP 端不兼容，声明支持属于误导性协商 |
| HTTP 方法边界 | `GET /mcp` → `405`；**`DELETE /mcp` → `405`**（本服务器无协议级 session，规范允许以此表示"不允许客户端主动终止 session"）；其余方法/路径 → `404` |
| JSON-RPC 响应输入 | 无 `method` 但带 `result`/`error` 的报文被识别为 JSON-RPC **响应**：本服务器从不向客户端发起请求，无法接受它 → 回 **HTTP `400`** + `-32600`（规范要求此类输入用 HTTP 错误状态码，而不是 200）。既无 `method` 也无 `result`/`error` 的仍按"非法请求"回 `200` + `-32600` |
| **坑：`Expect: 100-continue`** | 收到该头必须先回 `HTTP/1.1 100 Continue\r\n\r\n` 再读体，否则部分 .NET 客户端会延迟/卡住（已实现，PowerShell 5.1 实测走的就是这条路径） |
| `Transfer-Encoding: chunked` | 不支持，回 `411 Length Required`；与 `Content-Length` 并存、或 `Content-Length` 重复，都回 `400` |
| 请求体上限 | 64 KB；超限时**先把正文丢弃读完再回 `413`**，否则客户端可能因连接被重置而看不到这个错误 |
| JSON | `Mcp/Json.cs`：自研精简实现（不引第三方库），解析 + 生成都在里面；正确转义 `\\`、`\uXXXX`，整数按 `long` 处理不失真；**嵌套深度上限 64**（递归下降遇到几万层嵌套会 `StackOverflow`，该异常无法捕获、会直接杀掉游戏进程）；**孤立代理（未配对 surrogate）转义为 `\uXXXX`**，否则 UTF-8 编码会把它替换成 U+FFFD（静默改数据） |
| 执行线程 | **主线程**（函数可能碰 Unity API）；net35 没有 `ConcurrentQueue`/`Task`/`async`，用 `lock + Queue<Action>` + `Monitor.Wait/Pulse` |
| **坑：游戏的全局 `Monitor`** | `Assembly-CSharp` 里有一个全局命名空间的 `Monitor` 类型，会遮蔽 `System.Threading.Monitor`，所以代码里必须写全名（编译器报 `CS0117`） |
| 并发 | 同时只允许一个调用，忙时返回 `isError`（引擎忙）；8 个并发 HTTP 连接实测全部正常 |
| 超时 | 默认 30 秒，只结束 HTTP 等待（不中止已在跑的函数），返回超时提示 |
| 错误日志克制 | 客户端中途断开/写响应失败（`IOException`/`SocketException`）不记错误日志，避免畸形客户端刷屏；但**工具执行失败会记一条 error 日志**（否则 agent 只看到一句错误、日志里毫无线索） |
| **坑：Mono 的 `Exception.ToString()`** | 实机实测：从 `Assembly.Load(byte[])` 加载的程序集里抛出的异常，**`ToString()` 返回空串**（同一对象的 `Message` 与 `StackTrace` 都正常）。因此错误文本必须自己拼「类型 + 消息 + InnerException 链 + 最外层堆栈」（见 `Invoker.DescribeException`），不能依赖 `ToString()`，否则 agent 会收到空错误文本 |
| 配置触发方式 | `Enabled`/`Port` 每帧与配置比对，但**只有真正改变 `ConfigEntry.Value` 的操作才会触发**（ConfigurationManager 里勾选，或代码调用 `Config.Reload()`）。实测：**直接编辑 `.cfg` 文件不会生效**（BepInEx 5.4 没有配置文件监视器）；且把服务器关掉之后，远程无法再打开（调用通道就在这个服务器上），需 ConfigurationManager 或重启 |
| DLL 加载 | `Mcp/ScriptLoader.cs`：Cecil 读程序集 → 程序集名加 `-{Ticks}` 后缀 → `Assembly.Load(byte[])`；**不读符号文件**（`ReadSymbols=false`，Cecil 找不到 .pdb/.mdb 时会抛 `SymbolsNotFoundException`，代价是堆栈无行号）；**不注册为 BepInEx 插件**；引用解析只用 DLL 自己所在目录 |
| 定位方法 | `typeName` 支持全名/后缀匹配；只接受 `public static` 且参数个数为 0 的方法；排除泛型方法定义（`ContainsGenericParameters`，否则 `Invoke` 会直接抛异常），候选列表把泛型标成 `Name<>` |
| 服务器生命周期 | 挂在**插件自身** GameObject 上；`OnDestroy()` 里停监听、释放端口；`SO_REUSEADDR`；`Enabled`/`Port` 变化时自动停旧起新；端口 0/负数/>65535 启动即抛异常；日志打**真实绑定端口** |
| 引擎代码改动 | MCP 自身在 `plugins` 里，**改它必须重启游戏**；脚本 DLL 才是每次调用自动重载 |
| 依赖 | 不新增任何 NuGet 引用 |
| 客户端编码要求 | 请求体必须是 **UTF-8** 的 JSON（MCP 客户端都满足）。注意 Windows PowerShell 5.1 的 `Invoke-WebRequest` 会把字符串 body 按 ANSI 发送，含中文的绝对路径会被打乱并报 `Illegal characters in path` —— 手工测试请用 `curl` 或显式传 `[Text.Encoding]::UTF8.GetBytes($json)` |
| 冒烟测试 | 离线极端场景用例见 `docs/MCP_SMOKE_TEST.md`，一键跑：`tools\run-smoke.ps1`（当前 **134 条用例：121 PASS / 0 FAIL / 13 SKIP**）；实机探针 `tools/ProbeDll/` + 手工清单 `tools/McpSmokeTests/MANUAL_L3_L4.md` |

---

## 6. 配置（`[Mcp]` 段，只有 3 项）

配置文件：`BepInEx\config\com.github.wukong2468.scriptdebugengine.cfg`（插件 GUID 决定文件名，首次启动自动生成）。

| 键 | 默认 | 说明 |
|---|---|---|
| `Enabled` | `true` | 是否启动 MCP 服务器 |
| `Port` | `8765` | 监听端口（已确认本机空闲） |
| `TimeoutSeconds` | `30` | 单次调用的等待上限 |
| `AllowAnyPath` | `false` | **危险开关**：为 `true` 时**不做任何路径校验**，`dllPath` 可以是机器上任意位置（默认关闭）。见 §7 |

其余全部硬编码：只绑 `127.0.0.1`、无 Token、只允许 `public static` 无参方法。

**这 4 项的运行时生效范围**（重要，实机验证过）：

- `Enabled` / `Port`：插件每帧把当前配置与"已应用的状态"比对，一旦变化就立刻停掉旧服务器并按新配置启停/换端口；配置没变时不做任何事（启动失败也不会每帧重试，改配置才会再试）
- `TimeoutSeconds`：每次调用时读取，立即生效
- `AllowAnyPath`：每帧刷新进 `InvokeContext`，**不需要重启服务器**即生效
- **触发方式**：只有真正改变 `ConfigEntry.Value` 的操作才算（ConfigurationManager 里勾选 → 用它自己的 Reload；或代码调用 `Config.Reload()`）。**手改 `.cfg` 文件不会生效**（BepInEx 5.4 无配置文件监视器）；`Enabled=false` 之后无法远程再打开，需 ConfigurationManager 或重启

## 7. 安全

1. 只绑回环 `127.0.0.1`，外部网络无法访问。
2. **`Origin` 校验（DNS rebinding 防护，规范 MUST）**：`Origin` 缺席放行（非浏览器 MCP 客户端不带）；一旦出现，主机名必须是 `127.0.0.1` / `localhost` / `::1`，否则回 `403`；`Origin: null` 与 `file://` 一律拒。**没有这一条，只绑回环是不够的** —— 浏览器里的恶意网页可以把自己的域名解析到 `127.0.0.1`，从而跨源驱动 `invoke_method` 加载执行 DLL。
3. `dllPath` 规范化后必须位于 `BepInEx` 根目录之下（即 `scripts`、`plugins` 等），防止被诱导加载系统 DLL；相对路径也以该根为基准。
4. **逃生开关 `[Mcp] AllowAnyPath`（默认 false）**：打开后跳过第 3 条的全部校验（前缀比较 + 符号链接检查），任意路径的 DLL 都能被加载执行 —— 等于把"任意代码执行"面重新打开，此时只剩"仅回环绑定 + Origin 校验"兜底。打开时越权错误里也不再出现 `Access denied`，只保留"文件必须存在"的检查（那不是安全校验，是为了给出明确错误）。用途：需要直接调 BepInEx 目录之外的构建产物。配置项说明里已标 `DANGEROUS`。

---

## 8. 改动清单

| 文件 | 改动 |
|---|---|
| `Mcp/McpServer.cs` | 新增：TcpListener + 极简 HTTP + JSON-RPC 分发 + `invoke_method` 工具（只依赖 BCL） |
| `Mcp/Invoker.cs` | 新增：路径白名单校验、加载 DLL、定位并调用 `public static` 无参方法、结果/异常文本 |
| `Mcp/ScriptLoader.cs` | 新增：Cecil 热加载（读入内存 → 程序集名加时间戳 → `Assembly.Load(byte[])`），不读符号、不注册插件 |
| `Mcp/Json.cs` | 新增：自研精简 JSON（解析 + 生成），零第三方依赖 |
| `ScriptDebugEngine.cs` | 改造：`Awake` 启动服务器 + `[Mcp]` 三个配置；`Update` 泵主线程任务队列；`OnDestroy` 停服务器；嵌套类 `MainThreadInvocationHost`。原有的插件装载代码（`ReloadPlugins`/`LoadDLL`/`StartFileSystemWatcher`/`GetTypesSafe`/`DelayAction` 及其配置）已删除 |
| `ScriptDebugEngine.csproj` | 不变（零新增引用） |
| `tools/McpSmokeTests/`（net48） | 新增：离线冒烟测试宿主（链接 `Mcp/*.cs`）+ 测试 DLL 资产（`Good`/`GoodV2`/`Weird`/`Evil`/`MissingRef`/`Helper`） |
| `tools/ProbeDll/`（net35） | 新增：实机探针（部署到 `BepInEx\scripts\Probe.dll`），覆盖主线程/Unity/超时/异常/配置重载等实机用例 |
| `tools/run-smoke.ps1` | 新增：一键构建 + 跑全部离线用例 + 生成报告 |
| `docs/`、`README.md`、`README.zh-CN.md`、`AGENTS.md` | 新增：设计文档、冒烟方案与结果、使用说明、AI 协作规则 |

---

## 9. 实施与验证结果

| 阶段 | 内容 | 状态 |
|---|---|---|
| **P1** 执行内核 | `ScriptLoader` + `Invoker` + 主线程任务泵 | ✅ 已实现并验证（离线 E 组 + 实机 E-15/E-16/F-04/F-05） |
| **P2** 协议与 HTTP | `McpServer`（HTTP + JSON-RPC + 唯一工具 `invoke_method`） | ✅ 已实现并验证（离线 A/B/C/D/J 组） |
| **P3** 部署与接入 | 部署到 `BepInEx\plugins`、重启游戏、DSH 连接器端到端 | ✅ 已完成（`Ping → pong`、连续 10 次、重启后重连） |

### 9.1 离线验证（`tools\run-smoke.ps1`）

134 条用例 **121 PASS / 0 FAIL / 13 SKIP**，约 26 秒。宿主（net48）直接链接 `ScriptDebugEngine/Mcp/*.cs`，被调用目标是专门构造的测试 DLL（`Good`/`GoodV2`/`Weird`/`Evil`/`MissingRef`/`Helper`）。

- **协议**：`/health`；`initialize`；`notifications/initialized` → 202 空体；`ping`；`tools/list`；错误码 `-32700/-32600/-32601/-32602`
- **传输安全与版本协商（C-21~C-27）**：`Origin` 缺席/回环放行、非回环与 `null`/`file://` → `403`（含 `/health`）；`MCP-Protocol-Version` 支持版本放行、不支持 → `400`、缺头兼容；`DELETE /mcp` → `405`；POST 携带 JSON-RPC 响应 → `400`
- **HTTP**：`GET /mcp` 405；畸形/超长头部 400/431；无整体超时防护 → 408；超大正文 413；CL 重复 / CL+TE 400；`Expect: 100-continue`；8 并发；50 个慢速连接下正常请求仍 0ms
- **执行**：相对/绝对/后缀类型名/多类型；`string`/`int`/`void`/`null`；异常文本；带参方法与方法名错误的候选列表；泛型方法不误命中
- **安全**：越权路径/`..` 穿越/同前缀兄弟目录/UNC/`\\?\`/符号链接全部被拒
- **序列化（J 组 15 条）**：全部 0x00–0x1F 控制字符、引号反斜杠、Unicode 空白/行分隔符/BOM、emoji、非字符、NUL、空串、像 JSON 的字符串、1MB/4MB 返回值；用**严格 UTF-8 解码 + `Content-Length` 字节数校验 + 独立解析器（Newtonsoft）+ 与直接反射调用逐字符比对**验证，不用被测自己的 JSON 类
- **热加载**：覆盖 DLL 后同一路径返回新结果；加载过的文件不被占用
- **生命周期与资源**：20 次启停、换端口、端口释放、500 次调用后句柄零增长、日志增量受控、P95 延迟

### 9.2 实机验证（经 DSH 的 `bepinex-mcp` 连接器）

逐项结果见 `docs/MCP_SMOKE_TEST.md` §5.3 与 `tools/McpSmokeTests/MANUAL_L3_L4.md`。摘要：主线程 Unity API ✅、超时+恢复 ✅、busy 拒绝 ✅、超时后重试 ✅、热重载 ✅、动态启停/换端口 ✅、退出释放端口 ✅、重启后重连 ✅、异常文本（含 `ToString()` 为空串的异常）✅；仅 E-17（死循环/爆栈）故意未做。

### 9.3 手工自测命令

> Windows PowerShell 下**不要把 JSON 直接写在 `-d '...'` 里**（引号会被吞，表现为 `-32700 Parse error ... position 1`）。用 `--data-binary @文件`：

```powershell
$tmp = "$env:TEMP\mcp-check"; New-Item -ItemType Directory -Force $tmp | Out-Null
'{ "jsonrpc":"2.0","id":1,"method":"tools/list","params":{} }' | Set-Content "$tmp\tools.json" -Encoding ASCII -NoNewline
'{ "jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"invoke_method","arguments":{"dllPath":"scripts\\Probe.dll","typeName":"Probe.Main","methodName":"Ping"}} }' | Set-Content "$tmp\invoke.json" -Encoding ASCII -NoNewline

curl.exe -s http://127.0.0.1:8765/health
curl.exe -s -X POST http://127.0.0.1:8765/mcp -H "Content-Type: application/json" --data-binary "@$tmp\tools.json"
curl.exe -s -X POST http://127.0.0.1:8765/mcp -H "Content-Type: application/json" --data-binary "@$tmp\invoke.json"
```

**客户端接入**：`{"mcpServers":{"script-debug-engine":{"type":"http","url":"http://127.0.0.1:8765/mcp"}}}`

---

## 10. 约定与已知取舍

1. **每次调用都重载 DLL 新副本**：这是热加载语义（改完即生效）；副作用是函数看到的是**新的静态状态**，不是运行中插件实例的状态，且多次重载会累积内存（重载次数多了重启一次游戏即可）。
2. **方法必须无参**：需要传数据时，让函数自己读文件/静态字段，或后续再评估是否加参数支持。
3. **只回 JSON，不做 SSE**：MCP 规范要求客户端同时接受 `application/json` 与 `text/event-stream`，回 JSON 是合规的。若接入某个客户端时发现它只吃 SSE，再补约 15 行帧封装。
4. **不支持异步/协程**：目标函数必须同步返回（返回 `Task`/`IEnumerator` 不会被等待，只 `ToString()`）。
5. **方法名未知时**：错误提示会列出该类型全部可用的无参静态方法名（泛型标成 `Name<>`），够 agent 自行纠正；如果实际用起来仍不便，再考虑加一个"列函数"工具（约 40 行）。
6. **不读 .pdb**（`ReadSymbols = false`）：这样 .pdb 缺失或与 DLL 不匹配都不会导致调用失败，代价是异常堆栈里没有行号。
7. **请求体必须是 UTF-8 JSON**：这是 JSON/MCP 的默认约定，MCP 客户端都满足；只有 Windows PowerShell 5.1 手工测试时会踩坑（见 §5 与 §9.3）。
8. **单次调用串行**：同时只跑一个调用（引擎忙时直接返回 `isError`），这既符合 Unity 主线程的现实，也避免重入。
9. **超时后 `busy` 立即释放，慢调用仍在跑**：此时重试会被接受并排队、随后依次执行（实机实测：超时后重发 5 次全部成功，#1 等 3056 ms）。若被重试的方法本身很慢，会叠加卡帧 —— 建议目标函数幂等、客户端超时后不要盲目重试；待执行队列目前**无上限**（见 §11）。
10. **返回值长度无上限**：4 MB 实测正常，但整段进内存后原样回传；大返回值请由目标函数自己控制体量。
11. **白名单只检查目标文件本身是否为链接**：中间目录是 junction/符号链接时仍可指向根外（已知限制，见 §11）。
12. **无鉴权、仅 IPv4 回环、不支持 chunked/batch**：设计取舍，详见 `docs/MCP_SMOKE_TEST.md` §6。
13. **引擎 DLL 运行期间无法替换**：BepInEx 把 `plugins` 下的程序集内存映射进进程（实测 `user-mapped section open`），改引擎必须"关游戏 → 覆盖 → 再启动"；`scripts` 下的脚本 DLL 不受影响。
14. **配置文件手改不生效**：BepInEx 5.4 没有配置文件监视器，只有 ConfigurationManager 或代码调用 `Config.Reload()` 才会触发；且 `Enabled=false` 后无法远程再打开（调用通道就在这个服务器上）。

## 11. 尚未修复（已记录，待决策）

| 项 | 说明 | 建议 |
|---|---|---|
| 待执行队列无上限 | 超时/重试会把任务堆进主线程队列，`PumpMainThreadQueue` 一帧内全部执行 | 加上限（超额回 busy）约 10 行 |
| 返回值无长度上限 | 见 §10.10 | 加 1 MB 截断 + `truncated` 标记约 10 行 |
| 中间目录 junction 可绕过白名单 | 见 §10.11；彻底堵需要 P/Invoke 解析真实路径，且会误伤"把 scripts 挂到别的盘"的正常用法 | 视需要 |
| 无幂等 | 客户端重试 = 重复执行 | 文档提醒 + 目标函数幂等 |
| `HEAD /mcp` 响应带 body | HTTP 小瑕疵 | 给 `WriteResponse` 加 `suppressBody` |
| 超时/停止时在途连接的 `IOException` 被吞 | 客户端表现为连接被重置而非明确错误 | 可选改进 |
