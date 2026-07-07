# Utils

Small, dependency-free helpers used across the app.

## Files

| File | Purpose |
|---|---|
| `SecretRedactor.cs` | Redacts secrets (tokens, keys, passwords) from command output and logs before they are displayed or stored. |
| `ShellUtils.cs` | Shell helpers, safe quoting/escaping of arguments for host command execution. |
| `ForwardedHeadersConfig.cs` | Resolves the trusted proxy networks for the ForwardedHeaders middleware; falls back to safe defaults (loopback / RFC1918 / CGNAT) so an empty or invalid list can never collapse into trust-all. |
| `MetricsScrapeAuth.cs` | Constant-time bearer-token gate for the Prometheus `/metrics` endpoint; disabled (opt-in) when no scrape token is configured. |

## Related

- Host command execution: [`../Services/Server/HostCommandExecutor.cs`](../Services/Server/HostCommandExecutor.cs)
