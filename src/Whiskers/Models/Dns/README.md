# Models/Dns

Provider-neutral DNS models used by the DNS module ([`Services/Dns`](../../Services/Dns/)) and its MCP tools.

| File | What |
|---|---|
| `DnsRecord.cs` | `DnsRecord`, one record of a zone (id, name relative to the zone, type, value, TTL); `DnsSetResult`, what `set_dns_record` did (its `DnsSetAction` and the record before and after). |
