# Utils

Small, dependency-free helpers used across the app.

## Files

| File | Purpose |
|---|---|
| `SecretRedactor.cs` | Redacts secrets (tokens, keys, passwords) from command output and logs before they are displayed or stored. |
| `ShellUtils.cs` | Shell helpers, safe quoting/escaping of arguments for host command execution. |
| `MarkdownSanitizer.cs` | Post-filters rendered (LLM) markdown HTML, rewriting any non-`http(s)`/`mailto`/`#` href to `#` so a model-supplied `javascript:` link can't become a live one-click XSS. |
| `EnvMasking.cs` | Decides whether a container env-var value should be masked in the UI — a sensitive key OR a value with inline credentials (`://user:pass@`). |

## Related

- Host command execution: [`../Services/Server/HostCommandExecutor.cs`](../Services/Server/HostCommandExecutor.cs)
