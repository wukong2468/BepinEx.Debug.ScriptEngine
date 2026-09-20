# MCP 实现规范符合性分析报告

- 分析对象：`ScriptDebugEngine/Mcp/`（`McpServer.cs` / `Invoker.cs` / `Json.cs` / `ScriptLoader.cs`）+ `ScriptDebugEngine.cs`
- 对照标准：MCP 官方规范 Streamable HTTP 传输（2025-03-26 / 2025-06-18 / 2025-11-25）与最新修订 2026-07-28
- 分析日期：2026-09-20
- **修复状态：本报告列出的 4 项修复点已全部实现并通过离线冒烟验证（见 §5）**

---

## 1. 结论

协议主体（JSON-RPC 生命周期、工具定义与调用、结果/错误语义、状态码）本来就是合规的，能被标准 MCP 客户端正常接入；但作为对外暴露的 Streamable HTTP 端点，此前缺少规范强制的 `Origin` 校验（DNS rebinding 防护）与 `MCP-Protocol-Version` 校验。**这两项及另外两处轻微偏差现已修复。**

---

## 2. 修复前问题清单

| 级别 | 问题 | 规范依据 | 状态 |
|---|---|---|---|
| **P0** | 完全不校验 `Origin` 头 → 恶意网页可经 DNS rebinding 驱动 `invoke_method` 加载执行 DLL | 2025-06-18 起 **MUST**：无效 `Origin` 必须回 `403` | ✅ 已修复 |
| **P1** | 不校验 `MCP-Protocol-Version` 头 | 2025-06-18 起 **MUST**：不支持/无效版本必须回 `400` | ✅ 已修复 |
| P2 | `DELETE /mcp` 回 `404`（应为 `405`） | 2026-07-28 修订 | ✅ 已修复 |
| P2 | POST 携带 JSON-RPC **响应**时回 `200 + -32600`（应为 HTTP 错误状态） | Streamable HTTP：不接受此类输入时回 HTTP 错误状态码 | ✅ 已修复 |
| P2 | 声明支持 `2024-11-05`，但该版本走旧 HTTP+SSE 传输，实际无法握手 | 协商诚实性 | ✅ 已修复 |

**未修复/未声称**：2026-07-28 修订要求 `Mcp-Method` / `Mcp-Name` 等镜像头与头-体一致性校验；本服务器不声明支持该版本（客户端声明 `2026-07-28` 会收到 `400` 并被告知受支持版本）。这是有意的范围界定，不是遗漏。

---

## 3. 修复前已符合规范的部分（保持不变）

| 规范项 | 实现位置 |
|---|---|
| JSON-RPC 2.0 消息格式、错误码 `-32700/-32600/-32601/-32602/-32603` | `McpServer.cs` `HandleRpc` |
| `initialize` 返回 `protocolVersion` / `capabilities` / `serverInfo`；`ping`；`tools/list` | `McpServer.cs` |
| `tools/call` 返回 `{content:[{type:"text",text}],isError}` | `McpServer.cs` `ToolsCall` / `ToolResult` |
| **协议错误 vs 工具执行错误区分正确**（未知工具 → `-32602`；目标函数抛异常 → `isError:true`） | `McpServer.cs` |
| 通知（无 `id`）→ `202 Accepted` 且无响应体 | `McpServer.cs` |
| GET 到 MCP 端点 → `405 Method Not Allowed`（不提供 SSE 时规范允许） | `McpServer.cs` |
| 只绑 `127.0.0.1` 回环（规范 SHOULD） | `McpServer.cs` `Start` |
| 不支持 JSON-RPC batch（2025-06-18 起已从协议移除） | `McpServer.cs` |
| 大正文/慢客户端防护（431/408/413/411、`Expect: 100-continue`） | `McpServer.cs` |

---

## 4. 修复内容

### 4.1 `Origin` 校验（P0）

- 新增 `McpServer.IsAllowedOrigin`：`Origin` **缺席放行**（非浏览器 MCP 客户端不带该头）；一旦出现，主机名必须是 `127.0.0.1` / `localhost` / `::1`，否则回 **`403 Forbidden`**。
- `Origin: null`（`file://`、沙箱 iframe）与 `file://` **一律拒**：沙箱同样能被攻击者利用，不能放行。
- 校验位置在头部解析之后、路由之前，**对所有路径生效**（含 `/health`）。
- 同时给 `ReasonPhrase` 补上 `403`。

### 4.2 `MCP-Protocol-Version` 校验（P1）

- POST `/mcp` 读取该头；不在 `SupportedProtocolVersions` 内 → **`400`**，错误文本列出受支持版本。
- **缺头不报错**（按 `2025-03-26` 兼容处理），符合规范的向后兼容语义。

### 4.3 `DELETE /mcp` → `405`（P2）

- 新增 DELETE 分支：`/mcp` → `405`（本服务器无协议级 session）；其它路径仍 `404`。

### 4.4 JSON-RPC 响应输入 → `400`（P2）

- 无 `method` 但带 `result`/`error` 的报文识别为 JSON-RPC **响应**：本服务器从不向客户端发起请求，无法接受 → **HTTP `400`** + `-32600`。
- 既无 `method` 也无 `result`/`error` 的仍按"非法请求"回 `200` + `-32600`（保持既有行为，C-08 不受影响）。

### 4.5 协议版本支持列表（P2）

- `SupportedProtocolVersions`：`{2024-11-05, 2025-03-26, 2025-06-18}` → **`{2025-03-26, 2025-06-18}`**。
- `2024-11-05` 客户端协商时回退到 `2025-03-26`（服务器选择自己支持的版本，符合规范）。

---

## 5. 验证证据

命令：`powershell -File tools\run-smoke.ps1 -Offline`
结果：构建 **0 warning / 0 error**；离线冒烟 **134 条用例：121 PASS / 0 FAIL / 13 SKIP**（修复前为 127 条 / 114 PASS）。

新增用例的原始结果（摘自 `tools/McpSmokeTests/smoke-report.json`）：

| 用例 | 结果 | 证据 |
|---|---|---|
| C-01 | PASS | `2024-11-05` 协商回退到 `2025-03-26` |
| C-21 | PASS | 无 `Origin` → HTTP 200 |
| C-22 | PASS | 回环 `Origin`（`127.0.0.1` / `localhost` / `[::1]`）→ HTTP 200 |
| C-23 | PASS | `http://evil.example:8765` / `null` / `file://` / `http://127.0.0.1.evil.com` → **HTTP 403**；`GET /health` 跨源 → 403 |
| C-24 | PASS | `2025-03-26` / `2025-06-18` / 缺头 → HTTP 200 |
| C-25 | PASS | `1999-01-01` / `2024-11-05` / `2026-07-28` / `garbage` → **HTTP 400** |
| C-26 | PASS | `DELETE /mcp` → 405；`DELETE /nope` → 404 |
| C-27 | PASS | 带 `result` / 带 `error` → HTTP 400；两者都没有 → HTTP 200 |

回归确认：A~J 全部原有用例保持 PASS（无新增 FAIL）。

---

## 6. 修复后评级

| 维度 | 评价 |
|---|---|
| MCP 生命周期 / 工具语义 | 合规 |
| JSON-RPC / 错误码 | 合规 |
| Streamable HTTP 传输 | 合规（状态码、202 空体、405、DELETE 均正确） |
| 传输层安全（`Origin`） | **合规**（规范 MUST 已实现） |
| 协议版本头校验 | **合规**（2025-06-18 起 MUST 已实现） |
| 最新修订（2026-07-28） | 明确不支持，客户端声明该版本会收到 `400`（有意界定） |

**一句话**：修复后本实现符合其所声明的协议版本（2025-03-26 / 2025-06-18）的 Streamable HTTP 要求；`Origin` 校验补齐后，DNS rebinding 不再能绕过"仅回环"边界。

---

## 7. 相关文档

- 设计与实现要点：`docs/MCP_DESIGN.md`（§5 关键实现要点、§7 安全）
- 冒烟方案与结果：`docs/MCP_SMOKE_TEST.md`（§5.1 已修复项、C 组用例、§6 已知限制）
