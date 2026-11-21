# Modules/Dns

DNS zone records at an external provider (Infomaniak today): the `list_dns_records` / `set_dns_record` /
`delete_dns_record` MCP tools plus the *Settings → DNS (Infomaniak)* panel. No page, no nav entry — the
workflow is "tell the agent".

- `DnsModule.cs` — `Id = "dns"`, enabled by default but inert until a token is configured. `ConfigureServices`
  binds `DnsSettings` (`Dns` section), registers the Infomaniak client as `IDnsProviderClient`
  (typed `HttpClient`, rotating primary handler like the cloud clients) and `IDnsRecordService`.
  MCP tools: `DnsTools` (dedicated).

**Toggle:** `Features:dns:Enabled` (`Features__dns__Enabled=false`), restart-only. When off, the tools drop
off the MCP surface and the agent, and the Settings panel is hidden (`ModuleRegistry.IsEnabled("dns")`).

**Clean module (no no-ops).** Nothing in Core consumes the DNS services; the Settings panel injects only
`IOptionsMonitor<DnsSettings>` + `IAppSettingsStore`, both Core.

See [`docs/guide/modules/dns.md`](../../../../docs/guide/modules/dns.md) and [`../../Services/Dns/`](../../Services/Dns/).
