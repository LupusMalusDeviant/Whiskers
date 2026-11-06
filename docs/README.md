# Documentation

| Document | What |
|---|---|
| [../README.md](../README.md) | Facts, install, first sign-in, MCP client, image verification, key settings, accepted limits |
| [governance.md](governance.md) | Governance model: comparison with a plain Docker/K8s MCP server, the hero workflow, how the policy layer applies per caller |
| [features.md](features.md) | Features by area, including the compose exposure checks (contract 0006) |
| [installation.md](installation.md) | Every install path: installer, `docker run`, Compose, first sign-in, MCP clients, reverse proxy, Tailscale, profiles, Helm, from source |
| [configuration.md](configuration.md) | All environment variables, runtime details, tech stack |
| [mcp.md](mcp.md) | MCP server: API keys and scopes, tool groups, prompts, resources |
| [agent.md](agent.md) | The optional in-process AI agent and its safety model |
| [security-architecture.md](security-architecture.md) | The three planes per host, security summary, supply chain |
| [screenshots.md](screenshots.md) | Screenshots of the governance screens and the dashboard |
| [roadmap.md](roadmap.md) | Planned, not yet implemented |
| [project-structure.md](project-structure.md) | Source layout and code conventions |
| [product/README.md](product/README.md) | **Product strategy** — canonical positioning, the one-liner and pitch, target audience, the three product pillars, and the hero workflow ([product/POSITIONING.md](product/POSITIONING.md)) |
| [ARCHITECTURE.md](ARCHITECTURE.md) | The SSH-key-free design, mesh + mTLS, the three planes, PKI, onboarding |
| [container-hardening.md](container-hardening.md) | Full vs. locked-down container profiles, per-mode privilege matrix, socket-proxy, distroless roadmap |
| [operations/](operations/README.md) | **Operator guides**: production checklist (with the accepted limits of 1.0), upgrade and rollback, backup and recovery, troubleshooting, observability, and one guide per feature that needs host-side setup |
| [contracts/](contracts/) | Accepted interface contracts per work package (and proposed ones awaiting the maintainer) |
| [v1/](v1/) | The 1.0 product contract, security invariants, technical audit and work packages |
| [adr/](adr/) | Architecture decision records |
| [modules/](modules/) | One page per optional module: what it adds, its toggle, its dependencies |
| [reviews/](reviews/) | The security-fix history and incident reports, including [the 2026-08-26 log-monitor incident](reviews/2026-08-26-logmonitor-dockerd-cpu-incident.md) that drove the self-protection work |
| [mcp-tool-catalog.md](mcp-tool-catalog.md) | **Generated** — every MCP tool this build serves, with its permission level and owning module. Regenerated from the code; `McpToolCatalogSnapshotTests` fails the build if it drifts. Do not edit by hand. |
| [../deploy/telemetry/README.md](../deploy/telemetry/README.md) | Mesh/mTLS telemetry deploy templates (node_exporter, VictoriaMetrics, Tailscale ACL) |

## Code documentation

Every source folder under [`../src/Whiskers/`](../src/Whiskers/) carries its own `README.md` describing the files within. Start points:

- [src/Whiskers/README.md](../src/Whiskers/README.md): application project layout
- [Services/README.md](../src/Whiskers/Services/README.md): a guided tour of every service
- [Mcp/README.md](../src/Whiskers/Mcp/README.md): the MCP server and its tools

## Conventions

- **Interface-first**: services live behind an `IFoo` interface; consumers depend on the interface.
- **English** in-code comments and XML docs throughout; the user-facing UI is localized (English default, German available) via `IStringLocalizer` / `.resx`.
