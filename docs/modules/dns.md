# Module: dns

DNS zone records at an external provider — Infomaniak today — through three MCP tools, so the operator can
say "point holler.app.lupusmalus.dev at 1.2.3.4" instead of clicking through the provider's Manager. No page,
no nav entry; the only UI is the *Settings → DNS (Infomaniak)* panel that holds the token.

| | |
|---|---|
| **Id** | `dns` |
| **Enabled by default** | yes — but inert until a token is configured |
| **Toggle** | `Features:dns:Enabled` (env `Features__dns__Enabled=false`) — restart required |
| **Depends on** | — |
| **Nav** | — |
| **MCP tools** | `list_dns_records` (read), `set_dns_record` (write), `delete_dns_record` (write) — `DnsTools` |
| **Services** | `IDnsRecordService` (provider-neutral, idempotent set), `IDnsProviderClient` → `InfomaniakDnsClient` |
| **Settings** | section `Dns` in `app-settings.json`: `Provider`, `ApiTokenRef` (the vault key of the token, R1-29), `AllowedZones[]`, `DefaultTtl` |

## Configuring the token

*Settings → DNS (Infomaniak)*: paste an Infomaniak API token (Manager → Developer → API tokens, scope
**domain**), optionally restrict *Erlaubte Zonen* to the zones the tools may touch, save. The token is stored
encrypted in the vault (R1-29; saving one needs a usable `VAULT_KEY`); the `Dns` section of
`/app/data/app-settings.json` only names its vault key (`ApiTokenRef`). It applies live via `IOptionsMonitor`, and
is never shown again — the panel only reports "hinterlegt (…last4)". A plaintext token from before R1-29 is moved into the vault at the
first start with a usable vault. Equivalent environment configuration (not stored by Whiskers): `Dns__ApiToken`, `Dns__AllowedZones__0=lupusmalus.dev`, `Dns__DefaultTtl=300`.

Without a token every tool answers a one-line hint and calls nothing.

## Tool behaviour

- **Names are zone-relative.** `holler.app` in zone `lupusmalus.dev` is `holler.app.lupusmalus.dev`; `@`, an
  empty name, or the zone itself all mean the apex. An FQDN ending in the zone is accepted and trimmed.
- **Types:** A (IPv4), AAAA (IPv6), CNAME (hostname), TXT. Values are validated per type before any call.
  MX/SRV/CAA (structured extras) and NS/SOA (the zone's skeleton) are deliberately out of reach.
- **`set_dns_record` is idempotent:** an existing record of the same name + type is updated in place, an
  identical value + TTL answers "Unverändert" without writing, several records of that name + type (round-robin)
  are collapsed onto one. A CNAME next to other data at the same name — or the reverse — is refused (RFC 1034).
- **`delete_dns_record`** removes every record of that name + type; nothing there is a no-op. It is *write*,
  not *admin*: it undoes what `set_dns_record` can do and reaches the same four types only.
- **Zone fence:** with `AllowedZones` set, any other zone is refused before the provider is contacted.
- Every create/update/delete is audit-logged (`dns.record_created|updated|deleted`, target = zone); the token
  never appears in answers, audit entries, or logs — provider errors are relayed as HTTP status + the API's
  own description.

## Provider notes (Infomaniak)

`https://api.infomaniak.com/2/zones/{zone}/records` (GET list, POST create, PUT `/records/{id}` update,
DELETE `/records/{id}`), Bearer token, envelope `{"result":"success","data":…}` /
`{"result":"error","error":{"code","description"}}` — an error may arrive with HTTP 200, the client checks both.
Wire fields: `id`, `source` (relative label; the API spells the apex `"."`, the Manager leaves it blank),
`type`, `target`, `ttl` (60–86400). The client maps every apex spelling to `@` on the way in and writes `"."`
on the way out. Adding another provider = one more `IDnsProviderClient` registration; `DnsRecordService` picks
the one matching `Dns:Provider`.

Code: [`src/Whiskers/Modules/Dns/`](../../src/Whiskers/Modules/Dns/) · services in
[`Services/Dns`](../../src/Whiskers/Services/Dns/) · tools in
[`Mcp/Tools/DnsTools.cs`](../../src/Whiskers/Mcp/Tools/DnsTools.cs) · tests `DnsModuleTests`,
`DnsRecordServiceTests`, `InfomaniakDnsClientTests`.
