# MCP Bridge

**Let an AI assistant look at your Unity Editor — and, when you allow it, click the buttons for you.**

MCP Bridge is an Editor-only package for **Unity 6**. It gives you two things:

- **A chat window inside Unity** (`MCP Bridge → Chat`, or press `F12`). You ask a question; the window
  collects the real Editor data first and answers from that, not from guesswork.
- **An MCP server** (a small Node program in `Server~/`). It lets an AI agent running *outside* Unity —
  Claude Code in your terminal, or any other MCP client — inspect the same Editor and run the same
  commands.

Nothing from this package ships in your game build: all of it lives in an Editor-only assembly.

> [!NOTE]
> **What is MCP?** The Model Context Protocol is a standard way for an AI assistant to use tools on
> your computer. A *server* publishes a list of actions it can perform; a *client* (the AI) reads that
> list and calls the ones it needs. MCP Bridge is a server whose actions are Unity Editor operations —
> read the Console, open a scene, run a performance audit, take a screenshot, and so on.

---

## Contents

- [Why it exists](#why-it-exists)
- [What you can do with it](#what-you-can-do-with-it)
- [Requirements](#requirements)
- [Install](#install)
- [First run](#first-run)
- [Using the in-editor chat](#using-the-in-editor-chat)
- [Connecting an external MCP client](#connecting-an-external-mcp-client)
- [Command catalogue](#command-catalogue)
- [Safety model](#safety-model)
- [How it works](#how-it-works)
- [Known rough edges](#known-rough-edges)
- [Acknowledgments](#acknowledgments)
- [License](#license)

---

## Why it exists

When you ask an AI for help with a Unity problem today, **you** are the one carrying the data back and
forth:

| Without MCP Bridge | With MCP Bridge |
|---|---|
| You copy Console errors and paste them into a chat | It reads the Console itself |
| You describe the scene, or screenshot the Hierarchy | It reads the hierarchy itself |
| You read the Profiler and summarise it in your own words | It runs an audit and reads the numbers |
| You take the answer back and click through the Inspector | It can make the change — if you allow writes |

Every one of those hops is manual, and every one of them loses detail. MCP Bridge exposes the Editor
as a set of typed commands the assistant can call directly, so the loop closes without you in the
middle.

Both entry points — the chat window inside Unity and the external agent — go through the same
dispatcher in C#. One command list, one read-only default, one write gate.

This package was pulled out of a production Unity project and made standalone, so it is opinionated in
places. Read [Known rough edges](#known-rough-edges) before judging it as a general-purpose tool.

---

## What you can do with it

Things people actually use it for, day to day:

| You want to… | You do this | What happens |
|---|---|---|
| Find out why the game stutters | press `F12`, type `fps why does this scene stutter?` | the word `fps` runs a real performance audit *first*, so the answer is based on those numbers |
| See what is spamming the Console | type `errors` | it reads the Console and answers from the actual messages |
| Watch a variable while the game runs | select the object, type `watch health`, press Play | the value appears live in the Watch panel with a trend arrow and a sparkline |
| Know why an enemy will not walk to its target | ask for a `navmesh_path` between the two points | you get path status, corner list and distance instead of a guess |
| Let Claude Code fix a bug without you describing the scene | register the bridge with `claude mcp add …` | your terminal agent reads the live Editor, and with writes on, changes it |
| See what an external agent just did in your project | open the **Claude In** tab | a log of every MCP command that hit this Editor: path, body, response, duration |
| Catch an error that scrolled past during Play | set a `console_alert` pattern, then read the count | matching messages are counted even after the Console has moved on |

The full list is in the [command catalogue](#command-catalogue): **69 Editor commands**, plus 3 tools
for picking between several open Editors.

---

## Requirements

| What | Needed for | Notes |
|---|---|---|
| **Unity 6** | everything | `package.json` declares `6000.0`; developed and tested on `6000.0.75f1` |
| **An Anthropic API key** *or* **the Claude Code CLI** | the in-editor chat | CLI route: `npm i -g @anthropic-ai/claude-code`, then `claude login` |
| **Node.js 18+** | the external MCP bridge only | the bridge uses ESM and global `fetch` |
| `com.unity.test-framework` *(optional)* | `run_tests` | without it that one assembly is skipped and the two test commands return a clear error; nothing else changes |
| Photon Fusion 2 *(optional)* | `fusion_stats` | network readers are reflection-based and report zero when Fusion is absent, so single-player projects are unaffected |

---

## Install

Three ways in. Pick by what you plan to do with the package:

| Option | Installs from | The package is | Pick it when |
|---|---|---|---|
| **A** | Git URL | read-only, in `Library/PackageCache` | you just want to use MCP Bridge — simplest path |
| **B** | Local `file:` reference | editable, stays in the folder you cloned | you edit the package source, or you want the external MCP bridge |
| **C** | A copy inside `Packages/` | editable, versioned with that project | you want the package to travel with one project |

### Option A — Git URL (easiest)

**Window → Package Manager → `+` → Install package from git URL…** (in Unity versions before 6 the
same entry is called *Add package from git URL…*), then paste:

```
https://github.com/Smile-Codes/mcpbridge.git
```

`package.json` sits at the repository root, so no `?path=` suffix is needed.

Package Manager shells out to your Git client, so Git 2.14+ must be installed and reachable on `PATH`
(on Windows: the Git executable folder has to be in the `PATH` environment variable). If it is not,
the install fails with a *"no 'git' executable was found"* error rather than anything about this
package.

**Pinning a version.** Append `#<revision>` to lock the install to a tag, a branch, or a full
40-character commit SHA:

```
https://github.com/Smile-Codes/mcpbridge.git#v1.0.0
https://github.com/Smile-Codes/mcpbridge.git#main
```

No release tags are published yet, so a bare URL tracks the default branch: Unity resolves it once,
writes the resolved commit into `Packages/packages-lock.json`, and the whole team stays on that commit
until someone presses **Update** in the Package Manager. Once a release is tagged, pin to the tag for
anything shared.

> [!WARNING]
> **If you also want the external MCP bridge, use Option B or C instead.** A git-URL package is
> immutable: it lands in `Library/PackageCache/com.mcpbridge@<revision>` and `Server~/` comes with it,
> but `Server~/node_modules/` is not in the repo — so you would have to `npm install` inside a folder
> Unity treats as read-only and re-resolves under a new path on every update. The in-editor chat needs
> no Node at all, so this only matters when an external agent drives the Editor.

### Option B — Local `file:` reference (for working on the package)

Add this to the consuming project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.mcpbridge": "file:../../com.mcpbridge"
  }
}
```

Adjust the relative path to wherever this folder lives. An absolute path works too:
`"file:C:/Work/git/com.mcpbridge"`.

The package stays editable at its source folder and several projects can share one clone, so this is
the setup for contributing to the package — and `Server~/index.js` keeps a stable path for the
external bridge.

### Option C — Embedded package

Copy this whole folder into the project's `Packages/` directory. Also editable, but the copy belongs
to that one project.

---

## First run

**1. Open the chat window.** Open Unity, wait for the compile to finish, then open
**MCP Bridge → Chat** (or press `F12`). The window opening at all means the package installed
correctly.

**2. Start the server.** In the chat window, go to the **Claude In** tab and press **▶ Start** (the
menu item **MCP Bridge → Server → Start** does the same thing). The status pill in the header turns
green / *online*.

> [!IMPORTANT]
> The server is required **even for the in-editor chat** — the chat refuses to send while it is off.
> It binds `127.0.0.1` and takes port **23457** (ParrelSync clones take 23458 and up; the search range
> is 23457–23466). It restarts itself after a script recompile, but it does **not** come back on its
> own when you reopen Unity, so expect to press Start once per session.

**3. Pick a backend.** The gear icon opens Settings:

| Backend | What it uses | Trade-off |
|---|---|---|
| **API Chat** | an Anthropic API key, stored in `EditorPrefs` — never in the project, so it cannot end up in git | pay per token; model is selectable (Sonnet by default) |
| **Subscription** | the Claude Code CLI in print mode, so it runs on your Claude subscription | model and effort are selectable; slower per message, because every message pays the CLI cold start |

**4. Sanity check.** Type `test` in the chat. It answers locally, with no model call, listing the
server status, the write mode, and every available command.

**5. Turn on writes when you need them.** **MCP Bridge → Allow Write Commands** is **off by default**.
Until you switch it on, every command that touches the scene, assets, Play Mode or the build is
blocked. Read-only commands work either way.

---

## Using the in-editor chat

### Walkthrough: chasing down a stutter

1. Press `F12` to open the chat, and make sure the header says *online* (step 2 of
   [First run](#first-run)).
2. Type a question with a keyword in it, for example `fps why does this scene stutter?`
3. Before the model is asked anything, the window sees `fps` and runs `perf_audit` on your live
   Editor. Your question and the audit result are sent together.
4. The answer comes back in two versions — a **Dev** section and an **Art** section, so the same
   finding reads as "fix this in C#" or "fix this in the asset". Click the role chip in the header to
   switch between them.
5. If the model needs more data, it answers with a command block. The window runs that command through
   the same dispatcher (and the same write gate), feeds the result back, and you get a plain-language
   summary instead of raw JSON. Screenshots are re-attached as images, so the model can actually look
   at them.

### Walkthrough: watching a value during Play Mode

1. Select the object in the Hierarchy.
2. Type `watch health`. Only the field name is required — the component is auto-detected, and the
   object defaults to your current selection.
3. Enter Play Mode.
4. Open the **👁 Watch** panel: current value, trend arrow, a sparkline of recent history, and an alert
   badge. The panel also has quick-add for the selected object, and per-item delete.
5. Type `wv` to print current values into the chat, or `watchclear` to remove all watches.

> [!TIP]
> `watch_add` / `watch_get` / `watch_clear` and the event probe are **read-only** — they sample values,
> they do not change your scene — so they work without turning on Allow Write Commands. Entering Play
> Mode from chat (`play_control`) *does* need it.
>
> There is a Thai-language guide to the Play Mode inspection tools in
> [`Documentation~/runtime-inspection-th.md`](Documentation~/runtime-inspection-th.md).

### Keyword shortcuts

Some words make the window fetch real data *before* asking the model. Type them anywhere in your
sentence. Repeating keywords that map to the same command collapses into a single call.

| Type any of these | It runs first | Good for |
|---|---|---|
| `fps` `perf` `audit` `spike` `กระตุก` `เฟรมตก` | `perf_audit` | frame stats, heavy objects, batching |
| `gc` | `perf_audit` | allocation pressure |
| `mem` `memory` `แรม` | `memory_snapshot` | Mono heap, native, graphics memory |
| `console` `errors` `err` `เออเรอ` | `read_console` | recent errors and warnings |
| `exceptions` `exc` | `get_exceptions` | runtime exceptions with stack traces |
| `log` | `read_logfile` | full stack traces from `Editor.log` |
| `hier` `hierarchy` | `scene_hierarchy` | the object tree of the open scene |
| `sel` `selection` | `get_selection` | what is selected right now |
| `state` | `capture_state` | isPlaying / timeScale / frameCount |
| `wv` `watches` | `watch_get` | current values of everything you watch |
| `draw` `batches` `setpass` `overdraw` `lod` `shadow` `light` | `perf_audit` | rendering cost (art side) |
| `fusion` | `fusion_stats` | Photon Fusion 2 stats (Play Mode only) |

The **🔑 Keys** button in the toolbar lists the main ones inside Unity.

> [!NOTE]
> A few heavy scans — `refactor`, `tex`, `unused`, `deep` — deliberately do **not** auto-run. The model
> calls them on purpose when they are warranted, so a stray word cannot freeze your Editor.

### Other things the chat window does

- **Attach context inline.** `@` autocompletes project scripts, `#` autocompletes prefabs, `/`
  autocompletes locally installed Claude skills and slash commands (Subscription mode). `Ctrl+V` pastes
  an image from the clipboard, and **+ Image** opens a file picker.
- **Monitor panel.** Toggles a background health watcher that logs memory spikes and Editor stalls to
  `Library/DeltaMCP/monitor.log`.
- **Claude In tab.** A log of every MCP command that hit this Editor — path, body, response, duration
  and error flag — persisted to `Library/DeltaMCP/mcp_log.json`. This is where you look when an
  external agent is driving and you want to see what it did.

---

## Connecting an external MCP client

This is the part that lets a Claude Code session in your terminal inspect and drive the running Editor.

**1. Install the bridge dependencies once.**

```bash
cd Server~
npm install
```

**2. Register the server with your MCP client.** With the Claude Code CLI:

```bash
claude mcp add unity -- node "C:/Work/git/com.mcpbridge/Server~/index.js"
```

Point it at wherever the package actually lives: the folder you cloned (**Option B**), the copy inside
`Packages/` (**Option C**), or — for a git-URL install (**Option A**) — a separate clone of the repo,
since the copy under `Library/PackageCache` is read-only and gets a new path on every update. No
environment variables are needed; the bridge finds the Editor by itself.

**3. Make sure the Unity server is on** (step 2 of [First run](#first-run)), then ask the agent to call
`unity_ping`.

> [!TIP]
> External tool names are the command names prefixed with `unity_` — `read_console` becomes
> `unity_read_console`. Three are spelled a little differently: `set_terrain_heights` →
> `unity_terrain_set_heights`, `diagnose_deep` → `unity_deep_analysis`, `get_exceptions` →
> `unity_exceptions`.

### The `.mcp.json` file Unity writes for you

For **Options B and C**, Unity writes `<UnityProject>/.mcp.json` on load, with `args` already pointing
at this package's real `Server~/index.js`. The path comes from the Package Manager, so it is
project-relative when the package sits inside the project, and absolute when it does not.
`Documentation~/.mcp.json.template` shows the shape if you would rather write it by hand.

The file is only written when it is **missing**, or when it contains **nothing but this package's own
entry** (which is how a stale path from an older version gets repaired).

> [!IMPORTANT]
> A `.mcp.json` you have customised — other MCP servers, extra keys — is **never overwritten**. If its
> `unity` entry points at a file that does not exist, you get one Console warning per session naming
> the path to use instead. Git-URL installs are left alone entirely.
>
> With Option B that absolute path is machine-specific, so gitignore `.mcp.json` if your teammates keep
> their clone somewhere else — otherwise each of them will re-point it and commit the churn.

### How the bridge finds your Editor

Every open Editor writes a presence file (`<pid>.json`: project path, port, server on/off) to two
registries — `<UnityProject>/Library/DeltaMCP/instances/` and the machine-wide
`~/.mcpbridge/instances/`. The bridge reads both and merges them by PID.

That machine-wide registry is what makes discovery work no matter where the package sits on disk: Node
has no Package Manager API to ask, so with a `file:` reference or a separate bridge clone there is no
way to derive the Unity project from `index.js`'s own location.

When more than one Editor is running, the bridge prefers one belonging to *your* project — from
`UNITY_PROJECT_PATH` if set, else the Unity project that contains the bridge, else the project that
contains the client's working directory — then `Main` over ParrelSync clones, then the lowest port.
`unity_list_instances` / `unity_select_instance` let an agent inspect and change that choice. Entries
left behind by a crashed Editor are ignored (dead PID) and swept by the next Editor that runs.

Two optional environment variables override all of that:

| Variable | Effect |
|---|---|
| `UNITY_PROJECT_PATH` | Pin discovery to one Unity project when several are open at once |
| `UNITY_MCP_PORT` | Skip discovery entirely and talk to a fixed port (e.g. `23457`) |

---

## Command catalogue

69 commands are defined in `Server~/commands.json` — the single source shared by the Node bridge and
the C# dispatcher — plus 3 instance-management tools that live in the bridge itself.

Commands that *change* something (create, delete, set, edit, Play Mode control, batch, build, tests)
are blocked until **Allow Write Commands** is on; see [Safety model](#safety-model). Everything else
reads.

### Scene and objects

| Commands | What they do |
|---|---|
| `scene_list` `open_scene` `save_scene` | List the project's scenes, open one, save the open one |
| `scene_hierarchy` | The object tree of the open scene |
| `count_components` | Component census, split into active vs. pooled/inactive |
| `inspect_object` | Serialized values of an object — or everything, via reflection, with `deep=true` |
| `create_gameobject` `delete_gameobject` `set_transform` | Create, delete and move objects |
| `add_component` `set_property` | Add a component; set a primitive value on one |
| `assign_reference` | Assign an object or asset reference (`set_property` cannot do this) |
| `get_selection` `set_selection` | Read or change the Hierarchy selection |

### Assets

| Commands | What they do |
|---|---|
| `find_asset` | Locate assets in the project |
| `create_prefab` `place_prefab` | Turn an object into a prefab; drop a prefab into the scene |
| `create_material` `create_sprite_atlas` | Create a material; create a sprite atlas |
| `create_ui` | Create UI objects |
| `create_terrain` `set_terrain_heights` | Create terrain; set heights from Perlin noise or a raw heightmap |
| `read_scriptableobject` `edit_scriptableobject` | Read and tune config / balance data on a ScriptableObject asset |
| `set_import_settings` | Texture importer settings |
| `delete_asset` | Move an asset to the OS trash — refuses folders and anything outside `Assets/` |

### Diagnostics

| Commands | What they do |
|---|---|
| `read_console` `clear_console` | Read the Console; clear it |
| `read_logfile` | Tail of `Editor.log`, with full stack traces |
| `get_exceptions` | Deduplicated rolling buffer of runtime exceptions |
| `console_alert` `console_alert_get` `console_alert_clear` | Count log messages matching a pattern, so errors that scroll away during Play are still caught |
| `capture_state` | `isPlaying`, `timeScale`, `frameCount` — call it twice to see whether frames are advancing |

### Performance

| Commands | What they do |
|---|---|
| `perf_audit` | Scene census: renderers, skinned meshes, particles, realtime lights, animators, mesh colliders, heavy object groups, heuristic warnings, captured frame spikes |
| `perf_worst` | The worst captured spike on its own |
| `diagnose_deep` *(`unity_deep_analysis`)* | Top GC allocators and CPU self-time, with the offending source lines |
| `memory_snapshot` | Mono heap, Unity native, graphics driver memory, GC collection counts |
| `audit_textures` | Textures worth optimising: oversized, uncompressed, read/write enabled |
| `audit_unused` `audit_empty_folders` | Assets that *may* be unused (report only — Addressables/Resources are not detected) and empty folders under `Assets/` |
| `optimize_ui` | Turn off `raycastTarget` on non-interactive Image/Text, turn off `pixelPerfect` on Canvas, warn about heavy LayoutGroups |

### Play Mode inspection

No changes to your game code required. Everything here only reads live state — except `play_control`,
which drives Play Mode itself and therefore needs writes on.

| Commands | What they do |
|---|---|
| `play_control` | Enter / exit / pause / resume / step, plus `timescale` for slow motion |
| `watch_add` `watch_get` `watch_clear` | Sample a field every 0.5 s and track its trend; only the field name is required |
| `watch_alert` | Rising-edge condition (`lt` / `gt` / `eq` / `changed`) — logs a warning and counts hits |
| `watch_animator` | Current animator state, or one parameter |
| `event_log` `event_log_get` `event_log_clear` | Temporary probe that records OnCollision / OnTrigger events; detaches on Stop |
| `raycast` `overlap` | Physics ray; colliders inside a sphere |
| `navmesh_path` | Path status, corners and distance — for AI that cannot reach its target |

### Code and project

| Commands | What they do |
|---|---|
| `read_script` | Source with line numbers, optionally just one method |
| `edit_script` | Targeted find/replace, not a whole-file rewrite |
| `run_csharp` | Escape hatch: compile and run C# against the live Editor via Roslyn |
| `refactor_audit` | Large classes, long/complex methods, fan-in/fan-out coupling, deep inheritance, public fields, magic numbers, TODO debt |
| `compile` `compile_status` | Trigger a compile; poll its result |
| `run_tests` `get_test_results` | Unity Test Runner (EditMode or PlayMode) with async polling |
| `build_player` `git_status` | Build the player; read git status |

### Other

| Commands | What they do |
|---|---|
| `capture_screenshot` | Game or Scene view. The bridge reads the PNG off disk and returns it as a real image block, so the model *sees* the result instead of a file path |
| `run_batch` | Up to 50 commands in one round trip |
| `ping` `server_stop` | Liveness check; stop the server |
| `fusion_stats` | Photon Fusion 2: tick, RTT, bandwidth, packet loss, resimulation count |

### Multi-Editor (bridge-side)

`unity_list_instances` · `unity_select_instance` · `unity_start_instance`

Every open Editor registers itself, so an agent can list them (ParrelSync Main/Clone setups included),
pick one, and even switch a stopped one on.

---

## Safety model

- **Read-only by default.** Mutating routes are listed explicitly in `MCPHandlers.WritePaths` and are
  refused with an explanatory error until **Allow Write Commands** is on. The gate applies to the
  in-editor chat and to external MCP clients alike — including every sub-command of `run_batch`.
- **Rate limited** to 25 commands per second, so a runaway agent loop cannot hammer the Editor.
- **Deletes are recoverable.** `delete_asset` moves files to the OS trash and refuses folders,
  third-party paths and anything outside `Assets/`.
- **Non-idempotent commands are never retried.** `run_csharp`, `edit_script`, `run_batch`,
  `delete_asset`, `set_import_settings`, `build_player` and `run_tests` are flagged `noRetry`, so a
  timeout cannot silently re-run side effects.

> [!WARNING]
> **Local, but not authenticated.** The listener binds `IPAddress.Loopback`, so it is unreachable from
> the network — but there is no authentication. Any process on your machine that can reach the port can
> drive the Editor while the server is on. Turn it off when you are not using it.

---

## How it works

```
Claude Code / MCP client ──stdio──► Server~/index.js ──HTTP POST /path──► MCPServer (TcpListener)
                                          ▲                                      │
                                   commands.json                          MCPHandlers.Dispatch
                                  (single source)                     rate limit → write gate → route
                                          ▼                                      │
In-editor chat (F12) ──────────────────────────────────────────────────► main-thread execution
```

- `Editor/MCPServer.cs` runs a small `TcpListener` HTTP server on a background thread. `TcpListener`
  rather than `HttpListener` on purpose: if Unity is force-quit, the kernel does not hold the port
  hostage until reboot.
- `Editor/MCPHandlers*.cs` is the dispatcher and the handlers, split by pack (core, assist, edit,
  offline). Work that touches Unity APIs is marshalled to the main thread.
- `Server~/commands.json` is the single source of truth for tool name, route, description and
  parameter schema. The Node bridge turns it into MCP tools with Zod schemas; the C# side reads it to
  map command names to routes. Add a tool in one place and both sides see it.
- Every open Editor writes a presence file (`<pid>.json`) to two registries — its own
  `Library/DeltaMCP/instances/` and the machine-wide `~/.mcpbridge/instances/`. `Server~/registry.js`
  merges both by PID, which is what makes discovery work for any install layout, and what makes
  listing and switching between Main/Clone editors possible.
- The in-editor chat calls `MCPHandlers.Dispatch` directly, in-process — it never goes over HTTP.

### Repo layout

| Path | What it is |
|---|---|
| `Editor/` | The `MCPBridge.Editor` assembly — chat window, server, handlers, profiler readers, code/prefab indexers, refactor audit, runtime watch, exception tracker |
| `Editor/TestRunner/` | Optional assembly; only compiles when `com.unity.test-framework` is present |
| `Editor/Fonts/` | Bundled IBM Plex Sans Thai Looped (OFL), so the UI renders the same everywhere |
| `Server~/` | Node stdio MCP bridge (`index.js`), Editor discovery (`registry.js`) and the `commands.json` manifest |
| `Tests/Editor/` | EditMode tests (batch parser, edit-script primitives, `.mcp.json` path rules), gated by `UNITY_INCLUDE_TESTS` |
| `Documentation~/` | `.mcp.json` template and a Thai guide to the Play Mode inspection tools |

To run the tests from a consuming project, add `"com.mcpbridge"` to `testables` in
`Packages/manifest.json`, then use **Window → General → Test Runner** (or the `run_tests` command).

Release history is in [`CHANGELOG.md`](CHANGELOG.md).

---

## Known rough edges

Stated up front so nothing surprises you after install.

- **The UI is in Thai.** This came out of a Thai-speaking team's project. Settings labels, tooltips,
  several error strings, most code comments and `Documentation~/runtime-inspection-th.md` are Thai, and
  the chat's built-in prompt asks the model to answer in Thai using the Dev/Art section format. The MCP
  tool descriptions that external agents read are in English.
- **Live profiler recorders are switched off** by a constant (`ProfilerReader.ENABLED = false`), so
  Play Mode carries zero profiling overhead. The cost: the GC / Deep / Live buttons are hidden in the
  chat toolbar, and live FPS and draw-call numbers are unavailable. Scene census, memory snapshots and
  frame-spike capture still work; naming the exact method behind a spike needs Unity's Profiler window
  to be recording. Flip the constant to get the full set back.
- **Unity maintains `<project>/.mcp.json` for editable installs** (Options B and C). It writes the file
  when it is missing and repairs the path when the package moves — convenient, but it means the project
  root gains a file you did not create. It backs off from anything you have customised, and never
  touches it for git-URL installs.
- **The Node bridge ships without its dependencies.** `Server~/node_modules/` is not in the repo, so an
  external MCP client needs one `npm install` in `Server~/` — and a read-only git-URL install has
  nowhere durable to put it (use Option B or C, or a separate clone). The in-editor chat needs no Node
  at all.
- **A few paths still reference the original project** (a sibling `Delta-Project` repo, used by the
  skills picker and by an external copy of the analysis playbook). All of them fail soft and fall back
  to embedded defaults.
- **Version 1.0.0, single-developer project.** There is no CI, no registry publication and no tagged
  release yet, so a bare git URL install tracks the default branch (Option A explains how to pin a
  revision once tags exist).

---

## Acknowledgments

Built with **Claude Code** (Anthropic) used as an AI pair-programmer throughout: the package is
maintained by a single developer, with Claude models writing and reviewing large parts of the code
alongside them. It also targets Claude on both backends — the Anthropic API and the Claude Code CLI.

Implemented on top of the [Model Context Protocol](https://modelcontextprotocol.io) and the official
`@modelcontextprotocol/sdk`.

> [!NOTE]
> Not affiliated with, sponsored by, or endorsed by Anthropic.

## License

Fonts under `Editor/Fonts/` (IBM Plex Sans Thai Looped) are licensed under the SIL Open Font License —
see `OFL.txt`. No license file is published for the package source itself yet.
