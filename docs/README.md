# Documentation

The docs are sorted by reader. Start with the [README](../README.md) for the facts, the install and the first sign-in.

## Using Whiskers — [guide/](guide/)

| Document | What |
|---|---|
| [guide/governance.md](guide/governance.md) | Governance model: comparison with a plain Docker/K8s MCP server, the hero workflow, how the policy layer applies per caller |
| [guide/features.md](guide/features.md) | Features by area, including the compose exposure checks (contract 0006) |
| [guide/mcp.md](guide/mcp.md) | MCP server: API keys and scopes, tool groups, prompts, resources |
| [guide/mcp-tool-catalog.md](guide/mcp-tool-catalog.md) | **Generated** — every MCP tool this build serves, with its permission level and owning module. Regenerated from the code; `McpToolCatalogSnapshotTests` fails the build if it drifts. Do not edit by hand. |
| [guide/agent.md](guide/agent.md) | The optional in-process AI agent and its safety model |
| [guide/modules/](guide/modules/) | One page per optional module: what it adds, its toggle, its dependencies |
| [guide/screenshots.md](guide/screenshots.md) | Screenshots of the governance screens and the dashboard |
| [guide/roadmap.md](guide/roadmap.md) | Planned, not yet implemented |

## Running Whiskers — [operations/](operations/README.md)

| Document | What |
|---|---|
| [operations/installation.md](operations/installation.md) | Every install path: installer, `docker run`, Compose, first sign-in, MCP clients, reverse proxy, Tailscale, profiles, Helm, from source |
| [operations/configuration.md](operations/configuration.md) | All environment variables, runtime details, tech stack |
| [operations/container-hardening.md](operations/container-hardening.md) | Full vs. locked-down container profiles, per-mode privilege matrix, socket-proxy, distroless roadmap |
| [operations/README.md](operations/README.md) | **Operator guides**: production checklist (with the accepted limits of 1.0), upgrade and rollback, backup and recovery, troubleshooting, observability, and one guide per feature that needs host-side setup |
| [../deploy/telemetry/README.md](../deploy/telemetry/README.md) | Mesh/mTLS telemetry deploy templates (node_exporter, VictoriaMetrics, Tailscale ACL) |

## Contributing — [architecture/](architecture/)

| Document | What |
|---|---|
| [architecture/ARCHITECTURE.md](architecture/ARCHITECTURE.md) | The SSH-key-free design, mesh + mTLS, the three planes, PKI, onboarding |
| [architecture/security-architecture.md](architecture/security-architecture.md) | The three planes per host, security summary, supply chain |
| [architecture/CODEBASE_MAP.md](architecture/CODEBASE_MAP.md) | Orientation in the code: projects and areas with their sizes, how a call flows to the host, where state lives, where to start a change, hotspots |
| [architecture/project-structure.md](architecture/project-structure.md) | Source layout and code conventions |
| [architecture/adr/](architecture/adr/) | Architecture decision records |
| [contracts/](contracts/README.md) | Accepted contracts: every change is specified and accepted here before it is built |

## Checking the promises — [v1/](v1/README.md)

| Document | What |
|---|---|
| [v1/PRODUCT_CONTRACT.md](v1/PRODUCT_CONTRACT.md) | What Whiskers 1.0 promises, its decisions and the path matrix |
| [v1/SECURITY_INVARIANTS.md](v1/SECURITY_INVARIANTS.md) | Every security invariant with the tests that prove it |
| [v1/README.md](v1/README.md) | The release gate, the claim register and the other process artefacts the tests read |

## Product and reference

| Document | What |
|---|---|
| [product/README.md](product/README.md) | **Product strategy** — canonical positioning, the one-liner and pitch, target audience, the three product pillars, and the hero workflow ([product/POSITIONING.md](product/POSITIONING.md)) |
| [releases/](releases/) | Release notes |
| [reference/history/](reference/history/README.md) | Historical internal documents: the security-fix history, the 2026-08-26 log-monitor incident, the SSH hardening rollout, the CodeQL triage before 1.0, an early implementation plan |

## Code documentation

Every source folder under [`../src/Whiskers/`](../src/Whiskers/) carries its own `README.md` describing the files within. Start points:

- [src/Whiskers/README.md](../src/Whiskers/README.md): application project layout
- [Services/README.md](../src/Whiskers/Services/README.md): a guided tour of every service
- [Mcp/README.md](../src/Whiskers/Mcp/README.md): the MCP server and its tools

## Conventions

- **Interface-first**: services live behind an `IFoo` interface; consumers depend on the interface.
- **English** in-code comments and XML docs throughout; the user-facing UI is localized (English default, German available) via `IStringLocalizer` / `.resx`.
