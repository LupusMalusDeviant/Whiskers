# Mcp/Resources

Read-only MCP resources a client can attach as context. They describe the MCP surface itself and hold no secrets
and no infrastructure data; that stays behind the scoped, guardrailed tools.

| File | What |
|---|---|
| `WhiskersResources.cs` | `whiskers://tools/catalog` (the same information as [`docs/mcp-tool-catalog.md`](../../../../docs/mcp-tool-catalog.md)) and `whiskers://me/permissions` (what the calling key or user may do). |
