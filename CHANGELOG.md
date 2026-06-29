# Changelog

All notable changes to this package are documented here.

## [Unreleased]

### Added
- **Apply/Edit Pack** — 8 tools that let the AI act, not just diagnose (new file
  `Editor/MCPHandlers.Edit.cs`):
  - `unity_edit_script` (`/script/edit`) — targeted find/replace on an existing `.cs` file (the
    fix/refactor primitive; no whole-file rewrite). Write-gated, `noRetry`.
  - `unity_assign_reference` (`/object/assign-reference`) — assign an object/asset reference into a
    component's serialized field (what `set_property` can't do). Picks the matching component on the
    target by field type. Write-gated.
  - `unity_run_batch` (`/batch`) — run up to 50 commands in one round-trip; each sub-command still
    passes the write-gate but skips the rate limit (counts as one user action). Write-gated.
  - `unity_delete_asset` (`/asset/delete`) — move an asset file to the OS trash (recoverable);
    refuses folders, third-party paths, and anything outside `Assets/`. Write-gated.
  - `unity_set_import_settings` (`/asset/import-settings`) — apply texture importer changes
    (maxSize/compression/readable/mipmaps/crunch); the fix that pairs with `audit_textures`.
    Write-gated.
  - `unity_capture_screenshot` (`/view/screenshot`) — capture the Game/Scene view to a PNG; the
    bridge reads it off disk and returns it as an actual **image** block (Claude sees the result, not
    just a path). The in-Unity chat (F12) also auto-attaches the PNG to a round-2 request so the
    embedded AI analyzes the image, not the file path. Supports `overlay=true` (Play-only: real
    backbuffer incl. Screen-Space-Overlay UI), a custom `path`, and `base64` embedding (capped ~3MB).
    Read-only.
  - `unity_build_player` (`/build/player`) — build a standalone/mobile player via `BuildPipeline`
    (blocking; switches active target if needed). Write-gated, long `timeoutMs`, `noRetry`.
  - `unity_git_status` (`/git/status`) — branch + porcelain working-tree changes before suggesting a
    commit. Read-only.
- **Test Runner integration** — `unity_run_tests` (`/tests/run`) + `unity_get_test_results`
  (`/tests/results`) drive the Unity Test Runner (EditMode/PlayMode, optional name filter). Results
  are async, so `run_tests` starts a run and `get_test_results` polls until `status:done`; progress is
  persisted in `SessionState` so it survives the PlayMode domain reload. Lives in a **separate optional
  assembly** `MCPBridge.Editor.TestRunner` (`Editor/TestRunner/`) that references
  `UnityEditor.TestRunner`; if `com.unity.test-framework` is absent only that assembly is skipped (the
  main 54 tools are unaffected) and the routes return a helpful error. The main assembly wires in via
  nullable `MCPHandlers.RunTestsHandler` / `GetTestResultsHandler` delegates set at load.
- EditMode tests for the `run_batch` JSON parser and `edit_script` primitives
  (`Tests/Editor/`, assembly `MCPBridge.Editor.Tests`, gated by `UNITY_INCLUDE_TESTS`). Internal
  parser helpers are exposed via `InternalsVisibleTo` (`Editor/AssemblyInfo.cs`). To run them in a
  consuming project, add `"com.mcpbridge"` to `testables` in `Packages/manifest.json`, then open
  Window → General → Test Runner (or call `unity_run_tests`).
- `Dispatch` gained a `rateLimited` flag so batch sub-commands bypass the per-second cap.
- Node bridge `toZodShape` supports an `object[]` param type (for `run_batch`'s `commands` array).
- `unity_run_csharp` tool (`/code/run`) — escape hatch that compiles and runs arbitrary C# against
  the live Editor/scene via the existing `RuntimeCompiler` (Roslyn). Lets the AI do anything Unity
  exposes when no dedicated tool fits (build prefabs from imported models, batch-edit assets, drive
  the importer, prototype gameplay live). Logic goes in `public static string Run()` or a
  MonoBehaviour; write-gated.
- Node bridge supports per-command `timeoutMs` and `noRetry` in `commands.json`. `run_csharp` uses
  `timeoutMs: 60000, noRetry: true` so a slow Roslyn compile isn't retried (which would re-run side
  effects).

## [1.0.0] - 2026-06-28

### Added
- Initial extraction of the MCP Bridge system from the `delta-unity` project into a
  standalone, reusable UPM package.
- Editor assembly `MCPBridge.Editor` (27 scripts) — chat window, MCP server, handlers, profiler
  readers, code/prefab indexers, refactor audit, runtime watch, exception tracker.
- Node bridge under `Server~/` for external Claude Code CLI integration.
- `.mcp.json` template under `Documentation~/`.

### Notes
- No code changes from the source — fully portable as-is (`asmdef` has no external references;
  paths resolve via `Application.dataPath` at runtime).
- Fusion network features are reflection-based and inert without Fusion installed.
