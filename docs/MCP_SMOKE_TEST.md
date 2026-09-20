# ScriptDebugEngine · MCP 服务器冒烟测试方案

- 状态：**已实现并跑通** —— 离线 L1+L2 共 134 条用例：**121 PASS / 0 FAIL / 13 SKIP**，耗时约 26s；13 条实机/集成用例见 `tools/McpSmokeTests/MANUAL_L3_L4.md`
- 被测对象：`ScriptDebugEngine/Mcp/`（TcpListener 极简 HTTP + 自研 JSON + JSON-RPC 分发 + `invoke_method`）与 `ScriptDebugEngine.cs`（主线程泵、启停、配置）
- 目标：用一批**极端/畸形/边界**输入，验证服务器在各种"不该崩"的情况下**都还活着**，并且失效时给出**明确英文错误**而不是卡死或崩溃
- 相关文档：`docs/MCP_DESIGN.md`

**怎么跑**

```powershell
tools\run-smoke.ps1                      # 构建 + 准备测试目录 + 跑全部离线用例，退出码 = 失败数
tools\run-smoke.ps1 -Offline             # 无网络时用本地 NuGet 缓存还原
tools\run-smoke.ps1 -SkipBuild           # 跳过构建，直接跑（改了测试代码后要先构建）
```

结果写入 `tools/McpSmokeTests/smoke-report.json`（每条用例的 id / 名称 / 结果 / 耗时 / 观察证据），进程若被杀死（没有报告）驱动脚本会打印 `CRASHED` 并以退出码 99 结束。

---

## 1. 判定规则（最重要的一条）

单看某个用例"返回了错误"不算通过，冒烟测试的核心是**服务器没被打死**。因此每个用例执行后都要紧跟一次探活：

```
用例动作  →  记录响应/异常
          →  探活 1：GET /health 必须 200 且 body 含 "status":"ok"
          →  探活 2：tools/call invoke_method 调 SmokeAssets/Good.dll 的 Ping() 必须返回 "pong" 且 isError=false
          →  两个探活都过 = PASS；动作本身结果不符 = FAIL；进程没了/端口不通 = CRASHED（立即中止并保留现场）
```

另外四条判定约定：

1. **断言只看异常类型名，不看异常消息文本**（.NET Framework 与 Mono 的异常消息本地化不同，如"路径中具有非法字符。"）。
2. **已知且接受的限制**（见 §6）在期望里写成"预期行为"，不算失败。
3. 每次运行结束，`BepInEx\LogOutput.log`（游戏内）或宿主 stdout（离线）**不应出现日志风暴**：单用例新增日志 ≤ 3 行。
4. **响应体不许用被测自己的 `Json` 类来验证**（那只是自证自洽）。J 组一律：对原始字节做**严格 UTF-8 解码**（非法序列直接抛）→ 校验 `Content-Length` **等于正文字节数** → 用**独立解析器 Newtonsoft.Json** 解析 → 与"直接反射调用同一个方法"的原始字符串**逐字符比较**。

## 2. 测试分层与运行方式

| 层 | 名称 | 运行环境 | 覆盖 | 耗时 |
|---|---|---|---|---|
| **L1** | 协议与 HTTP 冒烟 | 离线宿主（net48 控制台，直接链接 `Mcp/*.cs`，端口 8801-8810） | HTTP 报文、JSON、JSON-RPC、工具参数、安全校验、并发、启停 | ~60 s |
| **L2** | 执行内核冒烟 | 同上（+ 预生成的一组测试 DLL） | Cecil 热加载、反射定位、热重载、文件占用、坏 DLL | ~40 s |
| **L3** | 游戏内冒烟 | `COM3D2_5` 实机 | 主线程亲和性、卡帧、超时恢复、Unity API 可见性、生命周期 | 手工 ~10 min |
| **L4** | 集成冒烟 | DSH 连接器 + agent | 真实客户端握手、工具发现、端到端调用、日志 | 手工 ~5 min |

L1/L2 之所以能覆盖真正危险的代码：宿主链接的是**同一份** `Mcp/*.cs` 源文件（`Mcp` 层刻意不依赖 UnityEngine/BepInEx），所以 HTTP 解析、JSON、JSON-RPC、加载器这些最容易出极端问题的部分都能离线自动化回归，只有 Unity 胶水层需要实机。

**建议落地的目录结构**（待批准后实现）：

```
tools/
├─ run-smoke.ps1                     # 驱动脚本：构建 → 起宿主 → 跑用例 → 生成报告 → 退出码=失败数
└─ McpSmokeTests/
   ├─ McpSmokeTests.csproj           # net48，<Compile Include="..\..\ScriptDebugEngine\Mcp\*.cs" />
   ├─ RawClient.cs                   # 裸 TcpClient 客户端：故意发畸形报文/半包/慢速/RST
   ├─ Runner.cs                      # 用例注册、探活、报告输出
   ├─ Cases/HttpCases.cs             # A 组
   ├─ Cases/JsonCases.cs             # B 组
   ├─ Cases/ProtocolCases.cs         # C 组
   ├─ Cases/ToolCases.cs             # D 组
   ├─ Cases/LoaderCases.cs           # E 组
   ├─ Cases/ConcurrencyCases.cs      # F 组
   ├─ Cases/LifecycleCases.cs        # G、H 组
   └─ Assets/SmokeAssets.csproj      # 生成下面这些测试 DLL（net35）
      ├─ Good.dll          Ping()/Echo()/Boom()/Sleep(ms)/UseUnity()/Unicode()
      ├─ MissingRef.dll    引用一个不存在的程序集
      ├─ Weird.dll         泛型方法、带默认参数的方法、返回 Task 的方法、返回 IEnumerator 的方法、字段属性方法
      └─ （零字节 DLL、原生 DLL 由脚本现场生成/拷贝）
```

报告格式（`smoke-report.json`，便于回归比对）：

```json
{"startedAt":"2026-09-14T21:00:00+08:00","target":"ScriptDebugEngine 1.0",
 "cases":[{"id":"A-03","name":"header>8KB","result":"PASS","elapsedMs":11,"evidence":"..."}],
 "summary":{"pass":61,"fail":2,"crashed":0,"skipped":3}}
```

## 3. 用例矩阵

> 类型：**A**=L1 自动化、**B**=L2 自动化、**G**=L3 实机、**I**=L4 集成。每条用例都隐含 §1 的两次探活。

### A. HTTP 报文层（畸形与边界）

| ID | 场景 | 构造方式 | 期望 | 类型 |
|---|---|---|---|---|
| A-01 | 随机二进制垃圾 | 裸 socket 发 512 字节随机字节（无 `\r\n\r\n`） | 不崩；最好回 400（见 §5-P1）；探活通过 | A |
| A-02 | 只有 `\r\n\r\n` 的空请求 | 只发分隔符 | 不崩、连接关闭（或 400） | A |
| A-03 | 头部超 8KB | 发 9000 字节超长 header 行 | 不崩；应回 400/431（见 §5-P1） | A |
| A-04 | 查询串 | `POST /mcp?x=1`（4 KB 查询串，总头部仍 < 8 KB）→ 正常命中 `/mcp`；查询串 8 KB+ → 触发头部上限（见 A-03） | A |
| A-05 | 请求行缺版本 | `POST /mcp\r\n...` | 长度校验只要求 ≥2 段 → 会被当作 `POST /mcp` 正常处理（空 body → -32700）；不崩 | A |
| A-06 | 未知 HTTP 方法 | `PUT/DELETE/OPTIONS /mcp` → 404；`HEAD` → 404 但当前实现仍会写 body（小瑕疵，见 §5-P2） | A |
| A-07 | `GET /mcp` | 普通 GET | 405 + `text/plain` | A |
| A-08 | HTTP/1.0 且无 Host | 手写 `POST /mcp HTTP/1.0` | 正常处理并响应 | A |
| A-09 | 重复 `Content-Length` | 两个不同值的 `Content-Length` | 不崩；应显式拒绝（见 §5-P1） | A |
| A-10 | `Content-Length` + `Transfer-Encoding: chunked` | 同时给两个头 | 411 或 400，**不得**把 chunked 正文当 JSON 解析出错误结果 | A |
| A-11 | 仅 chunked、无 Content-Length | `Transfer-Encoding: chunked` | 411 | A |
| A-12 | Content-Length 声明大于实发 | 声明 100、只发 10、然后静默 | 该连接最多占用 10 s 后关闭；**其它请求不受影响** | A |
| A-13 | Content-Length 声明小于实发 | 声明 5、实发 200 | 只读 5 字节 → -32700；剩余字节被丢弃（连接会关） | A |
| A-14 | 声明 100 MB 正文 | `Content-Length: 104857600` 但不发送 | 413（丢弃上限 1 MB + 10 s 超时保护）；不挂死 | A |
| A-15 | 正文上限边界 65535 / 65536 / 65537 | 精确构造三档 body | 前两档按协议处理，65537 → 413 | A |
| A-16 | 同一连接流水线两个请求 | 两个完整 POST 连发 | 只处理第一个，`Connection: close` 后关闭；不崩 | A |
| A-17 | 客户端收到响应前 RST | 发完请求立刻 `LingerState(0)` 关闭 | 服务器写响应失败被吞掉，进程不崩 | A |
| A-18 | 半关闭（shutdown send） | 发完请求后 shutdown send，不读响应 | 不崩 | A |
| A-19 | 50 个慢速连接 + 1 个正常请求 | 50 个 socket 每 5 s 发 1 字节（slowloris） | 正常请求仍在 2 s 内成功；不崩（单次 Read 10 s 超时是唯一保护，见 §5-P2） | A |
| A-20 | IPv6 回环 | `curl -g http://[::1]:PORT/health` | 连接被拒（只绑 IPv4 回环，属预期） | A |
| A-21 | 端口被占用后启动 | 先占住端口再 `McpServer.Start()` | 抛异常被捕获、记 1 条错误、**不每帧重试**、插件其余功能正常 | A |
| A-22 | `Port=0` / 负数 / 70000 | 依次设置后启动 | 0 → 实际绑定随机端口（日志应打真实端口，见 §5-P1）；负/超范围 → 明确报错且不崩 | A |

### B. JSON 解析层

| ID | 场景 | 构造方式 | 期望 | 类型 |
|---|---|---|---|---|
| B-01 | **深层嵌套（P0）** | body = 60000 个 `[`（64 KB 内） | **当前会 StackOverflow 直接杀死游戏进程**（见 §5-P0，必须先修再测） | A |
| B-02 | 空 body | `-d ''` | -32700 | A |
| B-03 | 截断 JSON | `{"jsonrpc":"2.0"` | -32700 | A |
| B-04 | 尾部多余内容 | `{"id":1} garbage` | -32700 | A |
| B-05 | 顶层是数组/字符串/数字/null | 四种 body | -32600 | A |
| B-06 | 非法 `\u` 转义 | `"\uZZZZ"` | -32700 | A |
| B-07 | 孤立代理对 | `"\uD800"` | 不崩；能回错误或替换字符（记录实际行为） | A |
| B-08 | 裸控制字符 | body 内嵌 0x01 | -32700，不崩 | A |
| B-09 | 63 KB 超长字符串值 | 单字段 63 KB | 正常处理（方法不存在 → -32601） | A |
| B-10 | 重复键 | `{"id":1,"id":2,...}` | 取后者（记录行为） | A |
| B-11 | 数字边界 | `id` 为 `9007199254740993`（>2^53）、`1.5`、`-0` | `id` 原样回显（long 不失真） | A |
| B-12 | 中文/emoji 往返 | 请求与响应含中文、`\uD83D\uDE00` | 双向正确、无乱码 | A |
| B-13 | 非 UTF-8 字节序列 | body 里塞 GBK 字节 | -32700 或不崩（不得回出半截乱码结果） | A |

### C. JSON-RPC / MCP 协议层

| ID | 场景 | 期望 | 类型 |
|---|---|---|---|
| C-01 | `initialize` 已知版本（2025-03-26/2025-06-18） | 原样回显该版本；`2024-11-05`（旧 HTTP+SSE 传输，不声明支持）→ 回退 `2025-03-26` | A |
| C-02 | `initialize` 未知版本 / 缺 params / params 为空 | 回退 `2025-03-26`，不报错 | A |
| C-03 | `initialize` 缺 id（通知） | 202 空体 | A |
| C-04 | `notifications/initialized` | 202 空体 | A |
| C-05 | 未知通知 `foo/bar`（无 id） | 202 空体（通知不回错） | A |
| C-06 | `notifications/initialized` **带 id**（错误用法） | -32601（记录行为） | A |
| C-07 | 未知方法（带 id） | -32601 | A |
| C-08 | 缺 `method` | -32600 | A |
| C-09 | 缺 `jsonrpc` 字段 | 宽松处理（记录行为）——按 MCP 客户端都会带 | A |
| C-10 | 顶层 JSON-RPC batch 数组 | -32600（MCP 2025-03-26 已移除 batch，属预期） | A |
| C-11 | `ping` | `{}` | A |
| C-12 | `tools/list` | 恰好 1 个工具 `invoke_method`；3 个必填参数；描述全英文（无中文字符） | A |
| C-13 | 未知工具名 / 缺 `name` | -32602 | A |
| C-14 | `tools/call` 缺 `arguments` / 三个参数缺任一 | `isError:true` + "…are all required." | A |
| C-15 | `arguments` 传非字符串类型（如 `dllPath: 123`） | isError（缺失判定），不崩 | A |
| C-16 | `id` 为 null / 字符串 / 浮点 | 原样回显（包含 `"id":null`） | A |
| C-17 | `Accept: text/event-stream`（只接受 SSE） | 回 `application/json`（规范要求客户端两者都接受）；记录：不实现 SSE | A |
| C-18 | `Content-Type: text/plain` 带正确 JSON | 正常处理（宽松，记录行为） | A |
| C-19 | 请求 `GET /health` | 200 + `{"status":"ok","server":...,"version":...}` | A |
| C-20 | 未知路径 `GET /nope` | 404 | A |
| C-21 | 无 `Origin` 头（非浏览器 MCP 客户端） | 放行（200） | A |
| C-22 | 回环 `Origin`（`http://127.0.0.1[:port]` / `https://localhost:8765` / `http://[::1]:8765`） | 放行（200） | A |
| C-23 | **非回环 `Origin`（DNS rebinding 防护）**：`http://evil.example:8765` / `null` / `file://` / `http://127.0.0.1.evil.com` | **403**（含 `GET /health` 跨源） | A |
| C-24 | `MCP-Protocol-Version` 受支持版本（2025-03-26 / 2025-06-18）或缺头 | 正常处理（200，缺头按 2025-03-26 兼容） | A |
| C-25 | **`MCP-Protocol-Version` 不支持/无效版本**（1999-01-01 / 2024-11-05 / 2026-07-28 / garbage） | **400** | A |
| C-26 | `DELETE /mcp`（本服务器无协议级 session） | 405；未知路径 DELETE → 404 | A |
| C-27 | POST 携带 JSON-RPC **响应**（带 `result`/`error`、无 `method`） | **400** + `-32600`；两者都没有时仍按"非法请求"回 200 + `-32600` | A |

### D. 工具参数与安全（dllPath）

| ID | 场景 | 期望 | 类型 |
|---|---|---|---|
| D-01 | 相对路径 `scripts\Good.dll` | 成功 | B |
| D-02 | 绝对路径（含中文目录） | 成功 | B |
| D-03 | `.\scripts\Good.dll` / 正斜杠 `scripts/Good.dll` | 规范化后成功 | B |
| D-04 | 越权 `C:\Windows\System32\advapi32.dll` | `Access denied` | B |
| D-05 | `..` 穿越 `scripts\..\..\..\x.dll` | 归一化后仍在根内/被拒（断言最终路径不出根） | B |
| D-06 | 前缀混淆 `..\<root>2\x.dll` | 被拒（实现用 `root + \` 前缀比较） | B |
| D-07 | UNC `\\localhost\c$\...` | 被拒 | B |
| D-08 | `\\?\` 长路径前缀（指向根内合法文件） | 被拒（`GetFullPath` 不归一化该前缀 → 前缀比较不通过）；记录为可接受的 fail-closed | B |
| D-09 | **符号链接/junction 指向根外（P1 安全）** | **当前能绕过白名单**（见 §5-P1） | B |
| D-10 | 路径是目录 / 不存在 / 大小写变体 | `DLL not found` 或成功（大小写按 Windows 语义） | B |
| D-11 | 路径含空格、`#`、`%`、`&`、`'` | 正确处理（JSON 转义） | B |
| D-12 | 超长路径（>260 字符） | 不崩，明确错误 | B |
| D-13 | 空字符串 / 空白 / null | `isError`，明确提示 | B |
| D-14 | `AllowAnyPath` 默认值 + 越权错误里给出开关提示 | 默认 `false`；越权错误文本含 `AllowAnyPath`（方便排查） | B |
| D-15 | `AllowAnyPath=true` + 根目录之外的 DLL | 能加载并调用成功；相对路径仍以 BepInEx 根为基准 | B |
| D-16 | `AllowAnyPath=true` + 不存在的文件 | 仍回 `DLL not found`（存在性检查不是安全校验，两种模式都保留） | B |
| D-17 | `AllowAnyPath=true` + `\\?\` 前缀 / 越权系统 DLL | 不再出现 `Access denied`（语义确认：白名单与链接检查都被跳过） | B |

### E. 执行内核（加载 / 反射 / 热重载）

| ID | 场景 | 期望 | 类型 |
|---|---|---|---|
| E-01 | 正常无参静态方法（string/int/void/null 返回） | 结果文本分别正确（`OK`/`null`/invariant 数字） | B |
| E-02 | 类型名全名 / 短名 / 后缀 / 歧义（两个同名短名类型） | 前三种成功；歧义给候选列表 | B |
| E-03 | 方法不存在 / 带参数 / 带默认值参数 | 拒绝 + `Available public static parameterless methods: ...` | B |
| E-04 | 泛型方法、属性 getter、`Main` 等 | 只按 `public static` 且 0 参数匹配；不误命中 | B |
| E-05 | 返回 `Task` / `IEnumerator` 的方法 | 不等待，返回 `ToString()`（**已知限制**，断言为预期行为） | B |
| E-06 | 目标方法抛异常 | `isError:true` + 类型/消息/堆栈（含方法名） | B |
| E-07 | 目标方法抛 `TargetInvocationException` | 解包到内层原始异常 | B |
| E-08 | 目标 DLL 引用缺失程序集 | 不崩；`GetTypesSafe` 过滤 + 错误里带 LoaderExceptions | B |
| E-09 | 零字节 DLL / 原生 DLL / 文本文件改名 .dll | `BadImageFormatException` 或同类明确错误；**只看类型名** | B |
| E-10 | 只读 DLL | 正常调用 | B |
| E-11 | 调用成功后立刻覆盖 DLL（重编译） | 覆盖成功（文件未被占用）→ 再调用得到**新结果** | B |
| E-12 | 调用前删除 DLL | `DLL not found` | B |
| E-13 | 同一个 DLL 连续调用 1000 次 | 全部成功；文件始终可覆盖；托管内存/句柄无单调暴涨（记录 GC 前后 + 进程句柄数） | B |
| E-14 | 50 MB 大 DLL | 不崩；记录加载+调用耗时（建议阈值 < 5 s） | B |
| E-15 | 目标方法访问 Unity API（`Time.frameCount`、`GameObject.Find`） | 实机成功（证明在主编线程） | G |
| E-16 | 目标方法 `Sleep` 超过 `TimeoutSeconds` | HTTP 返回超时错误；游戏卡住该时长后恢复；随后正常调用成功（busy 标志已释放） | G |
| E-17 | 目标方法死循环 / 递归爆栈 | **不纳入自动冒烟**：会真死/崩游戏（见 §5-P2 风险） | G(手工,可选) |

### F. 并发与时序

| ID | 场景 | 期望 | 类型 |
|---|---|---|---|
| F-01 | 8 个并发 `tools/call`（Good.Ping） | 全部成功（无状态、无共享可变状态） | A |
| F-02 | 8 个并发 + 1 个慢请求混合 | 正常请求延迟 < 2 s；无 5xx/无崩 | A |
| F-03 | 客户端超时后重试（同 id 重发） | 会**重复执行**目标函数（已知限制，见 §5-P2） | G |
| F-04 | 实机：两个 agent 同时调用 | 一个执行、另一个收到 `engine is busy` 的 isError；随后恢复正常 | G |
| F-05 | 实机：超时后立即重发 5 次 | 不崩；队列不无限增长；恢复后正常 | G |
| F-06 | 请求处理中途 `Stop()`（关服务器） | 在途请求要么完成要么失败，进程不崩、端口释放 | A |

### G. 配置与生命周期

| ID | 场景 | 期望 | 类型 |
|---|---|---|---|
| G-01 | `Enabled` 关 → 开（运行时） | 停掉后端口不再响应，开启后立即恢复（无需重启游戏） | G |
| G-02 | 连续 20 次 开/关（离线可做） | 每次都能重绑同一端口；线程数稳定；无日志风暴 | A |
| G-03 | `Port` 运行时改到空闲端口 | 旧端口关闭、新端口可用 | A |
| G-04 | `Port` 改到被占用端口 | 1 条错误日志、不崩、不每帧重试；改回空闲端口恢复 | A |
| G-05 | 改配置文件后 `Config.Reload()` | 与 G-01/G-03 同样生效 | G |
| G-06 | `OnDestroy`（游戏退出 / 插件卸载） | 端口释放、accept 线程退出（`IsBackground`） | G |
| G-07 | 连续启停 20 次后端口仍可被其它进程绑定 | 无端口泄漏 | A |

### H. 资源与稳定性

| ID | 场景 | 期望 | 类型 |
|---|---|---|---|
| H-01 | 1000 次 invoke 后：托管堆、线程数、句柄数 | 无单调增长（阈值：GC 后堆增长 < 20 MB、线程数不变、句柄 +< 200） | B |
| H-02 | 1000 次请求后：TCP 连接状态 | 无大量 CLOSE_WAIT/TIME_WAIT 堆积（客户端连接都被关闭） | A |
| H-03 | 单用例日志增量 | ≤ 3 行（无风暴） | 全部 |
| H-04 | 服务器首字节延迟（P50/P95） | P95 < 50 ms（本机回环） | A |

### I. 集成（L4）

| ID | 场景 | 期望 |
|---|---|---|
| I-01 | DSH 连接器登记 `http://127.0.0.1:8765/mcp` | 连接成功、`tools/list` 可见 `invoke_method` |
| I-02 | agent 调 `invoke_method` 指向真实脚本 DLL 的探针方法 | 返回结果文本；BepInEx 日志出现 `MCP server listening on ...` |
| I-03 | agent 连续 10 次调用（模拟迭代） | 全部成功；每次都能拿到最新构建的 DLL 结果 |
| I-04 | 游戏重启后再连 | 客户端重连成功（无需改配置） |

### J. 返回值序列化（"奇怪字符"，用独立解析器验证）

被测资产：`Assets/Evil`（`Evil.Strings`），期望值由测试进程**直接反射调用同一方法**得到，再与响应里的 `text` 逐字符比较。

| ID | 返回值内容 | 期望 |
|---|---|---|
| J-01 | 全部 0x00–0x1F 控制字符 | 逐个转义为 `\uXXXX`，往返完全一致 |
| J-02 | `"` `\` `/` `'` `` ` `` `{}[]:,` | 正确转义，外层结构不被破坏 |
| J-03 | `\t \r \n \b \f \v`、U+00A0、U+2028、U+2029、U+FEFF(BOM) | 往返完全一致 |
| J-04 | **孤立高代理 U+D800** | **wire 上必须保留为 `\ud800` 转义**（不得被替换成 U+FFFD） |
| J-05 | **孤立低代理 U+DFFF** | 同上（`\udfff`） |
| J-06 | 合法代理对（😀 U+1F600） | 4 字节 UTF-8，往返一致 |
| J-07 | U+FFFD / U+FFFE / U+FFFF | 往返一致 |
| J-08 | 夹杂 NUL（`a\0b`） | 转义为 `\u0000`，往返一致 |
| J-09 | 空字符串 | 保持空串（不得变成 `null` 字面量） |
| J-10 | 看起来就是一段 JSON-RPC 的字符串 | 不二次转义、不破坏外层结构 |
| J-11 | `C:\Program Files\a"b"\\c\nd.dll` | 往返一致 |
| J-12 | 中文 / emoji / 制表 / 引号 / 反斜杠 / 换行混合 | 往返一致 |
| J-13 | 1 MB 字符串 | 成功返回；`Content-Length` 正确；严格 UTF-8 合法；可被独立解析器解析 |
| J-14 | 4 MB 字符串 | 同上（**当前无输出长度上限**，已记录） |
| J-15 | initialize / tools/list / ping / 错误响应 / `/health` / 非法 JSON | 每个响应体都能被独立解析器解析成 JSON 对象 |

---

## 4. 执行频率与门槛

| 触发条件 | 必须跑 | 门槛 |
|---|---|---|
| 改动 `Mcp/` 任一行 | L1 + L2 | 0 FAIL / 0 CRASHED |
| 改动插件生命周期、配置、并发、超时 | L1 + L2 + G 组 + F 组（实机） | 同上 |
| 部署到游戏后 | L3 精简（E-15/E-16、F-04、G-01/G-03/G-06） | 全部符合预期 |
| 发版前 | L1 + L2 + L3 + L4 | 0 FAIL / 0 CRASHED，报告归档 |

**最小冒烟集（改动后 5 分钟内跑完）**：A-01/A-03/A-09/A-14、B-01、C-07/C-12/C-14、D-04/D-09、E-02/E-06/E-11/E-13、F-01、G-02/G-04、H-01。

## 5. 加固项与处理结果

### 5.1 已修复（本轮实现）

| 优先级 | 问题 | 修法 | 验证用例 |
|---|---|---|---|
| **P0** | **JSON 递归无深度上限** → `StackOverflow` 不可捕获、直接杀进程 | `Json.Parse` 加深度计数（上限 64），超限抛 `FormatException` → 回 -32700 | B-01 ✔（返回 `nesting is too deep`）<br>另用独立程序验证：60000 层递归确实被杀（exit `-1073741571` = STATUS_STACK_OVERFLOW） |
| **P1** | 畸形/超长头部静默断连 | 头部异常回 `400`；超 8 KB 回 `431`；有数据但超时回 `408` | A-01/A-02/A-03 ✔ |
| **P1** | `Content-Length` 重复 / 与 `Transfer-Encoding` 并存 | 两种情况都显式回 `400` | A-09/A-10 ✔ |
| **P1** | 无整体请求超时（slowloris） | 头部 5 s、正文 10 s、超限正文丢弃 2 s 的分别限时 | A-12（10.0s 回 408）/A-14（2.0s 回 413）/A-19（50 个半开连接下正常请求 0ms）✔ |
| **P1** | 白名单可被符号链接绕过 | 目标 DLL 若带 `FileAttributes.ReparsePoint` 直接拒绝 | D-09 ✔（无管理员权限时记录为未验证） |
| **P1** | `Port=0` 日志与实际不符 | 0/负数/>65535 启动即抛 `ArgumentOutOfRangeException`；日志改用真实绑定端口 | A-22 ✔ |
| P2 | 畸形客户端会刷错误日志 | 客户端断开/写响应失败（`IOException`/`SocketException`）不再记错误日志 | H-03 ✔（日志增量 ≤3 行） |
| P2 | 泛型无参方法会被误命中（`Invoke` 直接抛 `InvalidOperationException`） | 匹配时排除 `ContainsGenericParameters`；候选列表把泛型标成 `Name<>` | E-04 ✔ |
| P2 | **返回值里的孤立代理对被静默替换成 U+FFFD（改数据）** | `Json.WriteString` 用 `WriteSurrogate`：合法代理对原样输出，孤立代理转义为 `\uXXXX`（与 Newtonsoft 写出行为一致） | J-04/J-05 ✔（修复前 FAIL：wire 上出现 U+FFFD；修复后 wire 上是 `\ud800`/`\udfff`） |
| **P1（实机才发现）** | **目标函数抛异常时错误文本变成空串** | 实机 Mono 上，从 `Assembly.Load(byte[])` 加载的程序集里抛出的异常其 `ToString()` 返回**空串**（`Message`/`StackTrace` 正常）；改为自己拼「类型: 消息 + InnerException 链 + 堆栈」，并在 `FormatError` 加"空文本兜底"；工具失败也记 error 日志 | 离线 E-06/E-07/E-18 ✔（E-18 用覆盖 `ToString()==""` 的异常确定性复现）；实机 E-06 需重启后复测 |
| **P0（MCP 规范符合性复核）** | **不校验 `Origin` 头**（规范 MUST）→ DNS rebinding 可绕过"仅回环"边界，恶意网页能驱动 `invoke_method` 执行 DLL | `IsAllowedOrigin`：`Origin` 缺席放行（非浏览器客户端）；出现时主机名必须是 `127.0.0.1` / `localhost` / `::1`，否则回 `403`（`null`、`file://` 一律拒） | C-21/C-22/C-23 ✔（含 `GET /health` 跨源 403） |
| **P1（MCP 规范符合性复核）** | 不校验 `MCP-Protocol-Version` 头（2025-06-18 起 MUST 对不支持版本回 `400`） | 读取该头，不在支持列表（2025-03-26 / 2025-06-18）内即回 `400` 并列出支持版本；缺头按 2025-03-26 兼容 | C-24/C-25 ✔ |
| P2（MCP 规范符合性复核） | `DELETE /mcp` 回 `404`（2026-07-28 修订要求 `405`） | 新增 DELETE 分支：`/mcp` → `405`，其余路径仍 `404` | C-26 ✔ |
| P2（MCP 规范符合性复核） | POST 携带 JSON-RPC **响应**时回 `200 + -32600`（规范要求 HTTP 错误状态） | 无 `method` 但带 `result`/`error` → `400` + `-32600`；两者都没有时保持既有 `200 + -32600` | C-27 ✔ |
| P2（MCP 规范符合性复核） | 声明支持 `2024-11-05`，但该版本走旧 HTTP+SSE 传输，实际无法握手 | 从 `SupportedProtocolVersions` 移除该版本，协商时回退到 `2025-03-26` | C-01 ✔ |

### 5.2 未修（已知取舍 / 记录在案）

| 优先级 | 问题 | 说明 |
|---|---|---|
| **P2** | 客户端重试会重复执行目标函数 | 无幂等去重；靠文档提醒"目标函数应幂等" |
| **P2（已实测放大）** | 超时后 `busy` 已释放，但慢调用仍在主线程跑；**重试会被接受并排队**，随后依次执行 | 实机实测（F-05）：超时后立即重发 5 次，**5 次都被接受**，其中 #1 等 3056ms 才执行、其余 16~19ms。若被重试的方法本身很慢，游戏会被阻塞 N × 时长；建议给待执行队列加上限，或让客户端超时后不要盲目重试 |
| **P2** | 死循环 / 递归爆栈的目标方法不可救 | 超时只解 HTTP 等待；这是主线程执行的固有代价 |
| **P2** | `HEAD /mcp` 响应仍带 body | HTTP 小瑕疵，无实际客户端受影响（A-06 记录） |
| — | 根内目录 junction 指向根外仍可绕过白名单 | 只检查文件本身是否为 reparse point；**已由 D-09b 记录为已知限制**（若连目录也检查，会误伤"把 scripts 目录 junction 到别的盘"这种正常用法） |

### 5.3 实机（L3/L4）执行结果

2026-09-13 在游戏运行中经 DSH 的 `bepinex-mcp` 连接器执行（探针 `tools/ProbeDll/` 部署到 `BepInEx\scripts\Probe.dll`；逐项明细见 `tools/McpSmokeTests/MANUAL_L3_L4.md`）：

| 项目 | 结果 |
|---|---|
| 服务器健康（`/health`、`initialize`、`tools/list`、通知 202、错误路径、越权拒绝） | ✅ 全部符合预期 |
| E-15 主线程 Unity API | ✅ `unity=2022.3.62f2 platform=WindowsPlayer frame=11269`；`scene=SceneTitle roots=5 loaded=True` |
| I-02/I-03 端到端 + 热重载 | ✅ MCP 客户端调用成功；连续 10 次全成功；重建 DLL 后同一路径 `Mark → PROBE-V2`（无需重启游戏） |
| I-04 重启后重连 | ✅ 重启游戏后服务器自动起来，`health`/`Ping` 正常 |
| E-06 / E-18 异常文本 | ✅ 修复后复测通过（`System.InvalidOperationException: boom from Probe` + 堆栈；`ToString()` 为空的异常也回「类型: 消息 + 堆栈」）；失败日志留痕也正常 |
| E-16 超时 + 恢复、F-04 busy | ✅ ~30s 超时错误、恢复后正常；并发第二个请求 **10ms** 内 busy 拒绝 |
| F-05 超时后立即重发 5 次 | ✅ 5 次全部成功；#1 等 3056ms（排在慢调用之后）、#2~#5 16~19ms |
| G-01/G-03/G-05 动态配置 | ✅ 改 `Port`/`Enabled` 后 `Config.Reload()` 立即换端口/启停（日志可见 `stopped`/`listening`/`disabled by config`） |
| G-06 退出释放端口 | ✅ 退出游戏后 8765 无监听 |
| E-17 死循环 / 递归爆栈 | ⏳ 故意不做：会真卡死/崩溃游戏进程，仅在明确接受风险时手工验证 |

## 6. 已知且接受的限制（断言为"预期行为"，不算失败）

1. 只绑 IPv4 回环 `127.0.0.1`；IPv6 `::1` 连不上（A-20 验证被拒）。
2. 不做 SSE：一律回 `application/json`（规范允许；只吃 SSE 的客户端会不兼容，C-17 记录）。
3. 不支持 `Transfer-Encoding: chunked`（411，A-11 验证）。
4. 不支持 JSON-RPC batch（顶层数组 → -32600；MCP 2025-03-26 已移除 batch，C-10 验证）。
5. 无鉴权 Token；本机任意进程可调用（仅回环 + 路径白名单兜底）。
6. 单次只允许一个调用，忙时返回 `engine is busy` 的 isError（实机 F-04 验证）。
7. 不读 .pdb：异常堆栈无行号。
8. 不支持异步/协程：返回 `Task`/`IEnumerator` 的方法不会被等待（E-05 记录实际返回值）。
9. 目标方法运行在主线程：会卡帧，超时不会中止它。
10. 每次调用都重新加载 DLL 新副本：静态状态是新的，且反复加载会累积内存（E-13/H-01 监控：句柄零增长）。
11. 服务端主动 `Connection: close`，因此关闭的连接会留在 `TIME_WAIT`（H-02：50 次请求后 Established=0、TIME_WAIT 数百，属正常）。
12. 中间目录为 junction 时可绕过白名单（见 §5.2）。
13. **返回值长度无上限**：4 MB 字符串实测可正常回传（`Content-Length`、严格 UTF-8、独立解析全部正确）；大返回值会整段进内存，建议目标函数自己控制体量。
14. 孤立代理在 wire 上是 `\uXXXX` 转义：Node/Python 会原样还原该字符；**C# 的 Newtonsoft 读取时会归一化成 U+FFFD**（这是客户端行为，不是服务端丢数据 —— J-04/J-05 因此断言 wire 形式而不是解析结果）。
15. **手改 `.cfg` 文件不会运行时生效**（BepInEx 5.4 没有配置文件监视器）；只有 ConfigurationManager 勾选或代码调用 `Config.Reload()` 才会让 `ConfigEntry.Value` 变化、从而触发每帧比对逻辑。另：把服务器 `Enabled=false` 之后，**没有远程手段再打开**（调用通道就在这个服务器上），需 ConfigurationManager 或重启游戏。
16. **引擎 DLL 在游戏运行期间无法替换**（实测：`user-mapped section open`——BepInEx 把它映射进了进程），改引擎必须"关游戏 → 替换 → 再启动"；脚本 DLL（`scripts` 下）不受影响，可随时覆盖。
17. `[Mcp] AllowAnyPath=true` 时**不做任何路径校验**：`invoke_method` 可以执行机器上任意位置的 DLL（默认 false）。此时"仅回环绑定 + Origin 校验"是剩下的边界。
18. **`Origin` 校验只认回环来源**：浏览器发起的请求必须来自 `127.0.0.1` / `localhost` / `::1`，否则 `403`（规范 MUST 的 DNS rebinding 防护）。非浏览器 MCP 客户端不带 `Origin`，不受影响；但在浏览器里从**非回环页面**（含 `file://`、沙箱 iframe 的 `Origin: null`）调用本服务的用法被明确禁止。
19. **只声明支持 `2025-03-26` / `2025-06-18`**：客户端若声明别的 `MCP-Protocol-Version` 会收到 `400`（错误文本里列出受支持版本）。规范客户端应使用 `initialize` 协商出的版本，因此不受影响；2026-07-28 修订还要求 `Mcp-Method` / `Mcp-Name` 等镜像头与头-体一致性校验，本服务器**不支持该版本**。

## 7. 后续可做

1. 把 L1/L2 接入发布前流程（例如在 `run-smoke.ps1` 上加 `-Minimal` 开关只跑"最小冒烟集"，或接入 git hook / CI）。
2. 实机项半自动化：把 L3 的探针 DLL 也纳入 `tools/`，用 DSH agent 驱动并自动比对返回值，减少手工步骤。
3. 若要彻底堵住 junction 绕过：需要解析真实路径（P/Invoke `GetFinalPathNameByHandle`），并先确认"scripts 目录本身是 junction"的合法用法怎么办。
4. 若将来加"异步 job"（长时间任务），需要为它补一组新的极端用例（并发 job、结果过期、进程重启后 job 表清理）。
