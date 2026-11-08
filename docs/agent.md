# AI agent (optional module)

Besides the MCP server (which serves *external* agents), Whiskers has an **in-process acting agent**: you describe an
operations task in natural language and it plans and executes using Whiskers' own tools.

- **Providers:** OpenAI, OpenRouter, Ollama, Gemini, Anthropic and Claude Code, selectable in the UI.
- **Advisor chat:** an optional read-only chat that explains and suggests but runs nothing.

The safety model is enforced in code, not in the prompt:

- **Guardrails** (a separate, admin-only `guardrails.json`) are a deterministic policy-enforcement layer:
  Allow/Confirm/Deny rules evaluated at the tool boundary, "most-restrictive wins", identical whether the agent was
  started from the web UI or via MCP.
- The agent **inherits the rights of whoever triggered it** (web user or MCP key) and can never exceed them.
- **Hybrid autonomy**: reads run autonomously; writes/admin actions require confirmation.

Configuration: `AGENT_*` and `AICHAT_*` in [configuration.md](configuration.md). Implementation:
[src/Whiskers/Services/Agent/](../src/Whiskers/Services/Agent/).
