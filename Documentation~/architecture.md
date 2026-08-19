# MCP Bridge — internals

Reference material for people who already have the package running, or who want to work on it.
Install, first run and daily use are in the [README](../README.md).

- [Request flow](#request-flow)
- [Components](#components)
- [How the bridge finds your Editor](#how-the-bridge-finds-your-editor)
- [The `.mcp.json` file Unity writes for you](#the-mcpjson-file-unity-writes-for-you)
- [Write gate and retry rules](#write-gate-and-retry-rules)
- [Repo layout](#repo-layout)
- [Running the tests](#running-the-tests)

---

## Request flow

```
Claude Code / MCP client ──stdio──► Server~/index.js ──HTTP POST /path──► MCPServer (TcpListener)
                                          ▲                                      │
                                   commands.json                          MCPHandlers.Dispatch
                                  (single source)                     rate limit → write gate → route
                                          ▼                                      │
In-editor chat (F12) ──────────────────────────────────────────────────► main-thread execution
```

## Components

- **`Editor/MCPServer.cs`** — a small `TcpListener` HTTP server on a background thread, bound to
  `IPAddress.Loopback`, port **23457** (ParrelSync clones take 23458 and up; the search range is
  23457–23466). `TcpListener` rather than `HttpListener` on purpose: if Unity is force-quit, the kernel
  does not hold the port hostage until reboot.
- **`Editor/MCPHandlers*.cs`** — the dispatcher and the handlers, split by pack (core, assist, edit,
  offline). Work that touches Unity APIs is marshalled to the main thread.
- **`Server~/commands.json`** — the single source of truth for tool name, route, description and
  parameter schema. The Node bridge turns it into MCP tools with Zod schemas; the C# side reads it to
  map command names to routes. Add a tool in one place and both sides see it.
- **`Server~/registry.js`** — Editor discovery (below).
- **The in-editor chat** calls `MCPHandlers.Dispatch` directly, in-process — it never goes over HTTP.

External tool names are the command names prefixed with `unity_` (`read_console` →
`unity_read_console`). Three are spelled differently: `set_terrain_heights` →
`unity_terrain_set_heights`, `diagnose_deep` → `unity_deep_analysis`, `get_exceptions` →
`unity_exceptions`.

## How the bridge finds your Editor

Every open Editor writes a presence file (`<pid>.json`: project path, port, server on/off) to two
registries — `<UnityProject>/Library/DeltaMCP/instances/` and the machine-wide
`~/.mcpbridge/instances/`. The bridge reads both and merges them by PID.

That machine-wide registry is what makes discovery work no matter where the package sits on disk: Node
has no Package Manager API to ask, so with a `file:` reference or a separate bridge clone there is no
way to derive the Unity project from `index.js`'s own location.

When more than one Editor is running, the bridge prefers one belonging to *your* project — from
`UNITY_PROJECT_PATH` if set, else the Unity project that contains the bridge, else the project that
contains the client's working directory — then `Main` over ParrelSync clones, then the lowest port.
`unity_list_instances` / `unity_select_instance` let an agent inspect and change that choice, and
`unity_start_instance` can switch on an Editor whose server is off. Entries left behind by a crashed
Editor are ignored (dead PID) and swept by the next Editor that runs.

Two optional environment variables override all of that:

| Variable | Effect |
|---|---|
| `UNITY_PROJECT_PATH` | Pin discovery to one Unity project when several are open at once |
| `UNITY_MCP_PORT` | Skip discovery entirely and talk to a fixed port (e.g. `23457`) |

## The `.mcp.json` file Unity writes for you

For editable installs (local `file:` reference or an embedded copy in `Packages/`), Unity writes
`<UnityProject>/.mcp.json` on load, with `args` already pointing at this package's real
`Server~/index.js`. The path comes from the Package Manager, so it is project-relative when the package
sits inside the project, and absolute when it does not.
[`Documentation~/.mcp.json.template`](.mcp.json.template) shows the shape if you would rather write it
by hand.

The file is only written when it is **missing**, or when it contains **nothing but this package's own
entry** (which is how a stale path from an older version gets repaired). A `.mcp.json` you have
customised — other MCP servers, extra keys — is never overwritten; if its `unity` entry points at a
file that does not exist, you get one Console warning per session naming the path to use instead.
Git-URL installs are left alone entirely.

With a local `file:` reference that absolute path is machine-specific, so gitignore `.mcp.json` if your
teammates keep their clone somewhere else — otherwise each of them will re-point it and commit the
churn.

## Write gate and retry rules

- Mutating routes are listed explicitly in `MCPHandlers.WritePaths` and are refused with an
  explanatory error until **MCP Bridge → Allow Write Commands** is on. The gate applies to the
  in-editor chat and to external MCP clients alike — including every sub-command of `run_batch`.
- The dispatcher is rate limited to **25 commands per second**.
- `delete_asset` moves files to the OS trash and refuses folders, third-party paths and anything
  outside `Assets/`.
- `run_csharp`, `edit_script`, `run_batch`, `delete_asset`, `set_import_settings`, `build_player` and
  `run_tests` are flagged `noRetry`, so a timeout cannot silently re-run side effects.

## Repo layout

| Path | What it is |
|---|---|
| `Editor/` | The `MCPBridge.Editor` assembly — chat window, server, handlers, profiler readers, code/prefab indexers, refactor audit, runtime watch, exception tracker |
| `Editor/TestRunner/` | Optional assembly; only compiles when `com.unity.test-framework` is present |
| `Editor/Fonts/` | Bundled IBM Plex Sans Thai Looped (OFL), so the UI renders the same everywhere |
| `Server~/` | Node stdio MCP bridge (`index.js`), Editor discovery (`registry.js`) and the `commands.json` manifest |
| `Tests/Editor/` | EditMode tests (batch parser, edit-script primitives, `.mcp.json` path rules), gated by `UNITY_INCLUDE_TESTS` |
| `Documentation~/` | This file, the `.mcp.json` template, and a Thai guide to the Play Mode inspection tools |

## Running the tests

Add `"com.mcpbridge"` to `testables` in the consuming project's `Packages/manifest.json`, then use
**Window → General → Test Runner** (or the `run_tests` command).
