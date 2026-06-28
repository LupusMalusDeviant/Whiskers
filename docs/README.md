# Documentation

| Document | What |
|---|---|
| [../README.md](../README.md) | Project overview, features, quick start, configuration, MCP, deployment |
| [ARCHITECTURE.md](ARCHITECTURE.md) | The SSH-key-free design, mesh + mTLS, the three planes, PKI, onboarding |
| [container-hardening.md](container-hardening.md) | Full vs. locked-down container profiles, per-mode privilege matrix, socket-proxy, distroless roadmap |
| [../deploy/telemetry/README.md](../deploy/telemetry/README.md) | Mesh/mTLS telemetry deploy templates (node_exporter, VictoriaMetrics, Tailscale ACL) |

## Code documentation

Every source folder under [`../src/ServerWatch/`](../src/ServerWatch/) carries its own `README.md` describing the files within. Start points:

- [src/ServerWatch/README.md](../src/ServerWatch/README.md): application project layout
- [Services/README.md](../src/ServerWatch/Services/README.md): a guided tour of every service
- [Mcp/README.md](../src/ServerWatch/Mcp/README.md): the MCP server and its tools

## Conventions

- **Interface-first**: services live behind an `IFoo` interface; consumers depend on the interface.
- **English** in-code comments and XML docs throughout; user-facing UI strings are German.
