# Services/AiChat

The **read-only advisor chat** in the UI, an assistant that answers questions and gives guidance about your infrastructure but takes **no actions**. (For an agent that *acts*, see [`../Agent/`](../Agent/).)

## Files

| File | Purpose |
|---|---|
| `IAiChatService.cs` / `AiChatService.cs` | Read-only advisor chat (guidance only, no actions); calls a configured LLM endpoint. The system prompt is German (user-facing). |
| `IChatHistoryStore.cs` | Per-user persistence of the advisor chat history. |

**Notes:** history is saved atomically (tmp + rename); the seed sent to the provider drops leading assistant turns and keeps only user/assistant messages, so Anthropic (which requires a user-first, alternating transcript) doesn't 400 on a truncated window.

## Related

- Config: `AICHAT_*` in [`../../../.env.example`](../../../.env.example)
- UI: [`../../Components/Shared/AiChat.razor`](../../Components/Shared/AiChat.razor)
- The acting agent (distinct): [`../Agent/`](../Agent/)
