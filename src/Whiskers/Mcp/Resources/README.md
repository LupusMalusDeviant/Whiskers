# Mcp/Resources

Read-only MCP resources a client can attach as context. They describe the MCP surface itself and hold no secrets
and no infrastructure data; that stays behind the scoped, guardrailed tools.

| File | What |
|---|---|
| `WhiskersResources.cs` | `whiskers://tools/catalog` (the same information as [`docs/mcp-tool-catalog.md`](../../../../docs/mcp-tool-catalog.md)) and `whiskers://me/permissions` (what the calling key or user may do: only the served tools a call would pass — the key's tool list, the tool switch and the level a call is held to, the capability level for a mutation, raised by a stricter stored level; a tool list never widens the key's level, INV-16. A level that a call's arguments raise further, such as a scheduled custom command, and the guardrails can still refuse a listed tool). |
