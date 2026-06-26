# Telemetry stack (mesh-only)

Push/scrape-based host metrics instead of SSH `/proc`-exec. See `../../docs/ARCHITECTURE.md`.

**Stack:** `node_exporter` on every host → VictoriaMetrics on the controller, scraped only over the
Tailscale mesh.

## Hard rule: nothing public

Every port (`9100`, `8428`) listens **only** on the host's Tailscale IP. `TS_IP` is required in the
compose files for exactly this reason — no `0.0.0.0`.

**DoD check per host:**
```sh
ss -tlnp | grep -E ':(9100|8428)'   # MUST show the Tailscale IP, never 0.0.0.0
# from the public internet (a host NOT on the tailnet):
curl --max-time 5 http://<public-ip>:9100/metrics   # MUST time out / be refused
```

## Order

1. Tailscale on the controller + each host; tag the nodes and apply `tailscale-acl.json`.
2. Per host: set `TS_IP` to its Tailscale address, then deploy `host/`.
3. On the controller: fill `scrape.yml` with the host targets, deploy `victoriametrics/`.
4. Verify: `curl http://<controller-mesh-ip>:8428/api/v1/query?query=up` lists every target as `1`.

> ServerWatch's onboarding wizard automates all of the above for a newly-added server.

## Persistence

VictoriaMetrics storage lives under `/opt/serverwatch-telemetry/vm-storage` (bind-mount, survives
rebuilds).

## Metric contract (what ServerWatch queries)

Each scrape target carries `server="<id>"` (= ServerWatch `ServerConfig.Id`, a stable id, NOT the
display name). `PrometheusMetricsSource` filters on it. Only HOST metrics (node_exporter) —
container stats come from the Docker API.

| Metric | PromQL (simplified) |
|---|---|
| Host CPU % | `100 - (avg(rate(node_cpu_seconds_total{mode="idle",server="$id"}[2m]))*100)` |
| Host RAM used | `node_memory_MemTotal_bytes{server="$id"} - node_memory_MemAvailable_bytes{server="$id"}` |
| Host RAM total | `node_memory_MemTotal_bytes{server="$id"}` |
| CPU count | `count(count by(cpu)(node_cpu_seconds_total{mode="idle",server="$id"}))` |
