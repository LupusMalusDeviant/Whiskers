# Services/Agent/Providers

Provider-agnostic LLM access with tool calling. One implementation per **wire format**; each translates Whiskers's provider-neutral types ([`../../../Models/Agent/AgentDtos.cs`](../../../Models/Agent/AgentDtos.cs)) into a provider's request/stream shape and back, so the idiosyncrasies never leak out of the implementation.

All providers stream (SSE) and surface the same `AgentStreamDelta` sequence, text deltas and/or tool calls, finished by a delta with `Final` set.

> Claude Code is deliberately **not** a provider, it brings its own agentic loop and lives in [`../IClaudeCodeRuntime.cs`](../IClaudeCodeRuntime.cs).

## Files

| File | Purpose |
|---|---|
| `IAgentLlmProvider.cs` | `IAgentLlmProvider` (stable id + `StreamAsync`) and `IAgentProviderFactory` (resolve the impl from settings). |
| `AgentProviderFactory.cs` | Selects the matching provider and configures base URL + key. `openai`/`openrouter`/`ollama` share the OpenAI-compatible client; `gemini`/`anthropic` get their own. |
| `OpenAiCompatibleProvider.cs` | OpenAI Chat Completions client (covers openai, openrouter, ollama, they differ only in base URL and whether a bearer key is needed). |
| `OpenAiWire.cs` | Request mapper + SSE stream accumulator for the OpenAI Chat Completions wire format (assembles fragmented `tool_calls`). |
| `GeminiProvider.cs` | Google Gemini `generateContent` client (model in the URL; API key in the `x-goog-api-key` header, kept out of logs). |
| `GeminiWire.cs` | Request mapper + stream accumulator for Gemini (roles user/model, `functionDeclarations`, pairs tool responses by function name). |
| `AnthropicProvider.cs` | Anthropic Messages client (`x-api-key` + `anthropic-version`; default model `claude-opus-4-8`). |
| `AnthropicWire.cs` | Request mapper + stream accumulator for Anthropic (top-level system, `tool_use`/`tool_result` blocks; no temperature, since 4.x models reject sampling params). |
| `ProviderError.cs` | Shared helper that pulls a provider error body's `error.message` so a failed call surfaces the real reason instead of a bare status code. Never leaks the API key (it lives in the request headers, not the body). |

All three mappers support an optional **image** on a user turn (`AgentMessage.ImageBase64`): OpenAI `image_url` data-URI, Anthropic `image` base64 source, Gemini `inline_data`. Used by the page-aware agent widget to send a page screenshot to a vision-capable model.

## Related

- Tests: `OpenAiWireTests`, `GeminiWireTests`, `AnthropicWireTests`, `MultimodalWireTests` in [`../../../../Whiskers.Tests/`](../../../../Whiskers.Tests/)
