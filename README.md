# ScriptDebugEngine

A BepInEx 5.4 plugin that hosts a small **MCP (Model Context Protocol) HTTP server inside a Unity game**, so an AI agent can *hot-load a DLL and execute a parameterless static C# method in-game, then read the result*.

Built for and verified on Unity 2022.3.62f2 + BepInEx 5.4.23 Windows, compiled against **.NET Framework 3.5**.

> 中文说明见 [README.zh-CN.md](README.zh-CN.md)。

---

## Features

- One MCP tool: `invoke_method(dllPath, typeName, methodName)` — no arguments are passed to the target method.
- **Fresh DLL copy per call** (assembly renamed with a timestamp, loaded from memory) → edits take effect immediately, no game restart.
- Runs **on the Unity main thread**, so target methods may use the Unity API.
- Returns plain result text; failures return `exception type: message` + inner-exception chain + stack trace.
- Wrong method name? The error lists the type's available parameterless static methods — a built-in discovery mechanism instead of extra tools.
- **Loopback only** (`127.0.0.1`), loopback-bound MCP Streamable HTTP, JSON-RPC 2.0, `/health` endpoint.
- **No third-party dependencies** in the MCP layer: the HTTP server and the JSON parser are minimal, self-contained implementations (`ScriptDebugEngine/Mcp/` does not reference UnityEngine or BepInEx, which is what makes offline testing possible).
- Runtime-configurable: `Enabled`, `Port`, `TimeoutSeconds`.

## Requirements

- Windows + .NET SDK (build only; developed with SDK 9)
- BepInEx 5.4.x (built against 5.4.21 reference assemblies, verified on 5.4.23)
- .NET Framework 3.5 reference assemblies (restored automatically via `Microsoft.NETFramework.ReferenceAssemblies`)
- NuGet feeds: `nuget.org` plus the BepInEx and Samboy feeds (see `NuGet.config`; `COM3D2.GameLibs` is not on nuget.org)

## Build and deploy

```powershell
dotnet build ScriptDebugEngine\ScriptDebugEngine.csproj -c Release
# -> ScriptDebugEngine\bin\Release\net35\ScriptDebugEngine.dll
```

Deployment:

1. **Close the game first.** The running instance memory-maps the plugin DLL, so it cannot be overwritten while the game is running (`user-mapped section open`).
2. Copy `ScriptDebugEngine.dll` into `BepInEx\plugins\`.
3. Start the game. `BepInEx\LogOutput.log` should show:

```
[Info   :Script Debug Engine] MCP server listening on http://127.0.0.1:8765/mcp
```

> Changing the *engine* always needs a restart. Script DLLs in `BepInEx\scripts` are different: overwrite them and the next `invoke_method` call picks up the new build.

## Configuration

File: `BepInEx\config\com.github.wukong2468.scriptdebugengine.cfg` (named after the plugin GUID, created on first run).

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Start the MCP server |
| `Port` | `8765` | TCP port (loopback only) |
| `TimeoutSeconds` | `30` | How long a request waits for the main-thread invocation |

Runtime behaviour: the plugin compares the current config against the applied state every frame and stops/starts or rebinds the server when `Enabled`/`Port` change; `TimeoutSeconds` is read per call.

**Trigger caveat:** only an actual change of `ConfigEntry.Value` counts — i.e. ConfigurationManager in-game, or code calling `Config.Reload()`. Editing the `.cfg` file by hand has no effect (BepInEx 5.4 has no config-file watcher). Also note that once the server is disabled, there is no remote way to re-enable it (the call channel *is* this server) — use ConfigurationManager or restart the game.

## Usage

Register the MCP server in your client:

```json
{ "mcpServers": { "script-debug-engine": { "type": "http", "url": "http://127.0.0.1:8765/mcp" } } }
```

Manual check with curl (Windows PowerShell: write the JSON to a file — passing `-d '{...}'` mangles the quotes):

```powershell
$tmp = "$env:TEMP\mcp-check"; New-Item -ItemType Directory -Force $tmp | Out-Null
'{ "jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"invoke_method","arguments":{"dllPath":"scripts\\MyScript.dll","typeName":"MyScript.Commands","methodName":"Ping"}} }' |
  Set-Content "$tmp\invoke.json" -Encoding ASCII -NoNewline

curl.exe -s http://127.0.0.1:8765/health
curl.exe -s -X POST http://127.0.0.1:8765/mcp -H "Content-Type: application/json" --data-binary "@$tmp\invoke.json"
```

Success:

```json
{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"pong"}],"isError":false}}
```

Failure (`isError:true`, text = exception type + message + stack):

```json
{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text",
  "text":"System.InvalidOperationException: boom\n  at MyScript.Commands.Ping () ..."}],"isError":true}}
```

## Tool contract

| Parameter | Type | Required | Meaning |
|---|---|---|---|
| `dllPath` | string | yes | Absolute path, or a path relative to the BepInEx root (e.g. `scripts\MyScript.dll`). Must resolve **under the BepInEx root**. |
| `typeName` | string | yes | Full name, short name, or a dotted suffix (e.g. `MyScript.Commands`, `Commands`, `Commands.Ping`-style suffixes are matched). |
| `methodName` | string | yes | Name of a **public static method with zero parameters**. |

Result rules:

| Target returns | `text` |
|---|---|
| `string` | as-is |
| any other type | `ToString()` (numbers use invariant culture) |
| `null` | `null` |
| `void` | `OK` |
| throws | `exception type: message` + newline + stack trace (`isError: true`) |
| DLL/type/method not found | explicit message; for a wrong method name, the list of available parameterless static methods |

Target method requirements: `public static`, parameterless, synchronous (a returned `Task`/`IEnumerator` is **not** awaited — it is just `ToString()`-ed), and it should be idempotent (a client retry re-executes it).

## How it works

```
AI agent ──MCP Streamable HTTP (JSON-RPC 2.0)──▶ ScriptDebugEngine.dll (BepInEx\plugins)
                                                        │
                                       McpServer (TcpListener, minimal HTTP/1.1)
                                       initialize / ping / tools/list / tools/call
                                                        │  lock + Queue<Action>
                                                        ▼
                                       Plugin Update() pump → main thread
                                       Invoker: rename + Assembly.Load(byte[]) → reflect → invoke
                                                        │
                                                        ▼
                                       BepInEx\scripts\*.dll (fresh copy each call)
```

| File | Role |
|---|---|
| `ScriptDebugEngine/Mcp/McpServer.cs` | TcpListener + minimal HTTP/1.1 + JSON-RPC dispatch + the single tool |
| `ScriptDebugEngine/Mcp/Invoker.cs` | Path whitelist, type/method lookup, invocation, result/exception text |
| `ScriptDebugEngine/Mcp/ScriptLoader.cs` | Cecil load: rename assembly → `Assembly.Load(byte[])`, no symbols, no plugin registration |
| `ScriptDebugEngine/Mcp/Json.cs` | Minimal JSON parser/writer (depth limit, surrogate escaping) |
| `ScriptDebugEngine/ScriptDebugEngine.cs` | Plugin entry: config, server lifecycle, main-thread queue pump |

## Testing

| Layer | How | Status |
|---|---|---|
| Offline (123 cases) | `tools\run-smoke.ps1` — builds the engine + test DLLs, starts an offline host that links `Mcp/*.cs`, runs A–J groups, writes `smoke-report.json` | **110 PASS / 0 FAIL / 13 SKIP**, ~26 s |
| In-game (13 items) | `tools/ProbeDll/` deployed as `BepInEx\scripts\Probe.dll`, driven through a real MCP client | 12 of 13 done (main thread, timeout, busy, retry, hot reload, config, port release, restart reconnect) |
| Manual checklist | `tools/McpSmokeTests/MANUAL_L3_L4.md` | remaining item: E-17 (deliberate infinite loop / stack overflow — would really hang or kill the game) |

Details and per-case evidence: [docs/MCP_SMOKE_TEST.md](docs/MCP_SMOKE_TEST.md), [docs/MCP_DESIGN.md](docs/MCP_DESIGN.md).

## Repository layout

```
ScriptDebugEngine/          plugin source (net35)
  Mcp/                      MCP layer: HTTP + JSON + JSON-RPC + invoker (no Unity/BepInEx deps)
docs/                       design + smoke-test documents
tools/run-smoke.ps1         one-shot offline smoke run
tools/McpSmokeTests/        offline test host, cases, test DLL assets, manual checklist
tools/ProbeDll/             in-game probe (deployed to BepInEx\scripts\Probe.dll)
reference/BepInEx.Debug/    read-only upstream snapshot
README.md / README.zh-CN.md  this file / Chinese version
AGENTS.md                   rules for AI collaborators
```

## Known limitations

1. Loopback IPv4 only (`127.0.0.1`); no auth token; no TLS. Any local process can call the tool.
2. No SSE (JSON only); no `Transfer-Encoding: chunked`; no JSON-RPC batching; request body ≤ 64 KB.
3. One invocation at a time — concurrent requests get `The engine is busy executing another invocation.`
4. Timeout only ends the HTTP wait; it does **not** abort a running main-thread method (the game stays frozen until it returns). A runaway method (infinite loop / stack overflow) will hang or kill the game.
5. After a timeout the busy flag is released but the slow call keeps running; retries are accepted and queued, then executed in order (measured: 5 retries after a timeout all succeeded, the first waiting 3056 ms). The pending queue has no upper bound.
6. No result-size cap (a 4 MB string was verified to round-trip correctly; larger payloads are held in memory).
7. `.pdb` files are not read → stack traces have no line numbers.
8. Each call loads a new assembly copy → repeated calls accumulate memory (restart the game after heavy use).
9. The whitelist checks the target file itself for reparse points; a *junction/symlink directory* can still point outside the BepInEx root.
10. `HEAD /mcp` responses include a body (harmless HTTP nit).
