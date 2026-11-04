# Mcp/Prompts

User-invokable MCP prompts: ready-made operations workflows a client offers its user. A prompt only returns text that
tells the client's model which Whiskers tools to call, in which order, and where to stop for a human. It runs nothing
and grants nothing; every tool it names still passes the caller's scope and the guardrails.

| File | What |
|---|---|
| `OperationsPrompts.cs` | `triage_unhealthy_containers`, `security_review_server`, `plan_safe_update` (chains `get_blast_radius` and `assess_update_risk`) and `investigate_host_load`. Tools of a disabled module are absent, so each prompt says to skip what is not available. |
