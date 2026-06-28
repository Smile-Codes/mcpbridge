# Changelog

All notable changes to this package are documented here.

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
