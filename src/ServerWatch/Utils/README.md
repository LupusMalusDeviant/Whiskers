# Utils

Small, dependency-free helpers used across the app.

## Files

| File | Purpose |
|---|---|
| `SecretRedactor.cs` | Redacts secrets (tokens, keys, passwords) from command output and logs before they are displayed or stored. |
| `ShellUtils.cs` | Shell helpers, safe quoting/escaping of arguments for host command execution. |

## Related

- Host command execution: [`../Services/Server/HostCommandExecutor.cs`](../Services/Server/HostCommandExecutor.cs)
