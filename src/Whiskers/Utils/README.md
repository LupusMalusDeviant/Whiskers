# Utils

Small, dependency-free helpers used across the app.

## Files

| File | Purpose |
|---|---|
| `SecretRedactor.cs` | Redacts secrets (tokens, keys, passwords, SQL password literals such as `PASSWORD '…'` / `IDENTIFIED BY …`) from command output and logs before they are displayed or stored; `RedactJson` redacts a JSON document value by value (decoded), as approval cards store it. Capability URLs (Telegram bot token, Discord/Slack/Mattermost webhook keys) and every value in `KnownSecrets` become `***` (R1-25). A pattern that times out withholds the whole text (fail closed). |
| `KnownSecrets.cs` | The secrets Whiskers holds for outbound channels (bot tokens, webhook and ntfy topic URLs, access tokens, SMTP password, signing secret), registered when their settings are bound; `SecretRedactor` replaces each occurrence, longest first (R1-25). A URL is also registered in its normalized form, the ntfy topic on its own. Values under 8 characters are ignored; at most 256 are kept. |
| `RedactingLoggerFactory.cs` | Stands in front of the logger factory (R1-25, INV-51): the message, each structured value, the template, the scopes and the exception text (`RedactedException`) reach the providers redacted; an entry that cannot be redacted is written as `<log entry withheld: redaction failed>`, never raw. Every enabled entry runs the full pattern set, so a pattern timeout on a loaded host shows that text as `***`. The message is rebuilt from its template and the redacted values (never formatted from the raw state); a list or dictionary value reaches the providers as one redacted string, its pairs as `key=value`, and the exception they see is a `RedactedException` (the original type name stays in its text; inner exception, `HResult` and `Data` are not passed on). Wired by `Startup/WhiskersLoggingExtensions`. |
| `SinkRedaction.cs` | What every persistent sink stores (R1-24, INV-51): `Text` (URL user info removed, then `SecretRedactor`), `Json` (value by value), `Arguments` (tool/mutation arguments: free-text payloads — `IsPayload`, matched on every spelling of the name — keep only their length, a secret-named key is `***` at any depth). Idempotent; never a prefix of a secret. |
| `ShellUtils.cs` | Shell helpers, safe quoting/escaping of arguments for host command execution. |
| `MarkdownSanitizer.cs` | Post-filters rendered (LLM) markdown HTML, rewriting any non-`http(s)`/`mailto`/`#` href to `#` so a model-supplied `javascript:` link can't become a live one-click XSS. |
| `EnvMasking.cs` | Decides whether a container env-var value should be masked in the UI — a sensitive key OR a value with inline credentials (`://user:pass@`). |
| `ForwardedHeadersConfig.cs` | Resolves the trusted proxy networks for the ForwardedHeaders middleware; falls back to safe defaults (loopback / RFC1918 / CGNAT) so an empty or invalid list can never collapse into trust-all. |
| `MetricsScrapeAuth.cs` | Constant-time bearer-token gate for the Prometheus `/metrics` endpoint; disabled (opt-in) when no scrape token is configured. |

## Related

- Host command execution: [`../Services/Server/HostCommandExecutor.cs`](../Services/Server/HostCommandExecutor.cs)
