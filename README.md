# MCP Bridge

In-editor AI assistant + MCP bridge for Unity. Portable, standalone — drop into any Unity 6 project.

Open the chat window via **MCP Bridge → Chat** or press **F12**.

## What it does

- **Create / edit:** GameObject, Prefab, UI, Material, Terrain, Script — from chat
- **Inspect:** scene hierarchy, object components, prefab contents, asset search
- **Perf audit:** FPS / GC / draw calls / triangles / lights / **GPU instancing candidates** / spikes
- **Pinpoint:** 📍 GC (alloc → file:line) · 🔬 Deep (CPU method+line, 5s capture)
- **Debug:** read console, exceptions, Editor.log, runtime watch live variables
- **Code quality:** refactor audit (complexity, coupling, structural issues)

Two backends: **API Key** (`console.anthropic.com`, pay per token) or **Subscription** (Claude Code CLI, Max plan).

## Install (any project)

### Option A — Local package reference (recommended for shared use)

Add to the consuming project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.delta.unity-mcp": "file:../../com.delta.unity-mcp"
  }
}
```

Adjust the relative path to wherever this folder lives (e.g. `C:/Work/git/com.delta.unity-mcp`).
You can also use an absolute path: `"file:C:/Work/git/com.delta.unity-mcp"`.

### Option B — Embedded package

Copy this whole folder into the project's `Packages/` directory.

## After install — 3 setup steps

1. **Open Unity** → wait for compile → **MCP Bridge → Chat** (or F12). Window opens = installed.

2. **Pick a backend** (⚙ Settings in the chat window):
   - **💬 API Chat** — paste an Anthropic API key (stored in EditorPrefs, never in git)
   - **♾ Subscription** — `npm i -g @anthropic-ai/claude-code` then `claude login`

3. **(Optional) Node bridge** — to let an external Claude Code CLI drive Unity:
   - The Node server lives in `Server~/` inside this package.
   - From that folder run `npm install` once.
   - Copy `Documentation~/.mcp.json.template` to your project root as `.mcp.json`, then set the
     `args` path to this package's `Server~/index.js`:
     - **Embedded package (Option B):** `./Packages/com.delta.unity-mcp/Server~/index.js`
     - **Local `file:` reference (Option A):** the package stays at its source folder, so use an
       absolute path, e.g. `C:/Work/git/com.delta.unity-mcp/Server~/index.js`
   - In Unity: **MCP Bridge → Server** must be 🟢 ON (auto-starts on Unity open).

## Notes

- **Allow Write Commands** (MCP Bridge → Allow Write Commands) must be ON for any command that
  modifies the scene/assets. Read-only by default.
- **Unity version:** developed/tested on Unity 6 (6000.0.75f1).
- **Fusion / networking:** network monitoring (`net`, `rtt`, `bandwidth`) uses reflection and
  returns 0 when Fusion is not present — harmless in single-player projects.

## License

Fonts under `Editor/Fonts/` (IBM Plex Sans Thai Looped) — SIL Open Font License, see `OFL.txt`.
