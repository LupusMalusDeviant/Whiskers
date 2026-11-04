# Services/Dns

DNS zone records at an external provider, in two layers: a **provider client** (the HTTP layer, one per
provider, token passed per call like the cloud clients) and a **record service** on top that is provider-neutral
and makes `set` idempotent.

Credentials are **global** (one provider account, many zones) — unlike cloud credentials, which are per server.
They live in `DnsSettings` (`Dns` section of `app-settings.json`, edited under *Settings → DNS*), never in code,
never in a tool answer, never in a log line.

## Files

| File | Purpose |
|---|---|
| `DnsVerifiers.cs` | R1-37 (INV-43): the zone is listed again at the provider — a set record must hold the written value (normalised like the service writes it), a deleted one must be gone; a provider error on that read fails the outcome with the state unknown. |
| `IDnsProviderClient.cs` | Provider contract: list/create/update/delete records of a zone; names zone-relative (`@` = apex). Also `DnsProviderException` (HTTP status + the API's own message). R1-48: create/update are marked `dns.record_set`, delete `dns.record_delete` (a set composes the deletes it makes); every registered client is guarded. |
| `InfomaniakDnsClient.cs` | Infomaniak implementation (`/2/zones/{zone}/records`, Bearer token, `result`/`data`/`error` envelope — an error can arrive with HTTP 200). Maps the apex spelling (`"."` on the wire) to `@`. |
| `DnsSettingsSection.cs` | R1-29: the `Dns` section as a Settings save writes it — provider, zones, TTL and the token's vault key (`ApiTokenRef`), never the token; reads the stored section as it is in the file, so an empty `ApiToken` that hides a token set by the environment stays. |
| `IDnsRecordService.cs` / `DnsRecordService.cs` | Resolves provider + token from `DnsSettings` (the token from the vault under `ApiTokenRef` since R1-29; a plaintext token only when not migrated yet or set by the environment; a token in a disabled or locked vault is an error), enforces `AllowedZones`, normalises zone/name/type/value (IPv4/IPv6/hostname/TXT validation), refuses CNAME coexistence, and implements the idempotent `SetAsync` (create / update / unchanged, duplicates collapsed) and `DeleteAsync`. |

## Behaviour notes

- **Only A/AAAA/CNAME/TXT.** NS/SOA are the zone's skeleton, MX/SRV/CAA carry structured extras the provider
  models differently — both are refused at `NormalizeType`, not at the provider.
- **Idempotent set:** one listing, then create / update-in-place / nothing. Value comparison is
  provider-tolerant (IPs by parsed value, hostnames case- and dot-insensitive, TXT with or without the
  surrounding quotes Infomaniak adds).
- **Adding a provider:** implement `IDnsProviderClient` with a new `ProviderId`, register it in `DnsModule`
  (`AddHttpClient<IDnsProviderClient, X>()` — multi-registration), and offer the id in the Settings panel.

## Related

- Module: [`../../Modules/Dns/`](../../Modules/Dns/) · docs: [`docs/modules/dns.md`](../../../../docs/modules/dns.md)
- MCP tools: [`../../Mcp/Tools/DnsTools.cs`](../../Mcp/Tools/DnsTools.cs) — `list_dns_records`, `set_dns_record`, `delete_dns_record`
- Settings: [`../../Configuration/DnsSettings.cs`](../../Configuration/DnsSettings.cs)
