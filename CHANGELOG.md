# Changelog

All notable changes to this package are documented here.

## [Unreleased]

### Added
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
- Initial extraction of the Delta AI Unity MCP system from the `delta-unity` project into a
  standalone, reusable UPM package.
- Editor assembly `UnityMCP.Editor` (27 scripts) — chat window, MCP server, handlers, profiler
  readers, code/prefab indexers, refactor audit, runtime watch, exception tracker.
- Node bridge under `Server~/` for external Claude Code CLI integration.
- `.mcp.json` template under `Documentation~/`.

### Notes
- No code changes from the source — fully portable as-is (`asmdef` has no external references;
  paths resolve via `Application.dataPath` at runtime).
- Fusion network features are reflection-based and inert without Fusion installed.
