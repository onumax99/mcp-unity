# Changelog

## [0.1.0] - 2026-11-19

### Added
- MCP server running inside the Unity Editor over Streamable HTTP (stateless, `http://127.0.0.1:8890/mcp`).
- Automatic restart after domain reloads; in-flight requests are answered before the reload.
- 26 tools: editor state, play mode, menu items, selection, undo, asset refresh with compiler results,
  console logs, hierarchy, GameObject create/modify/duplicate/delete, scenes, components, serialized
  properties, asset search, materials, prefabs, screenshots, static method invocation and batching.
- Window > MCP for Unity: status, port, auto start, Claude Code connection helpers, recent calls.
- Extensibility: subclass `McpUnity.McpTool` to add project-specific tools.
