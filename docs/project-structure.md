# Project structure

Each source folder carries its own `README.md` describing the files within.

```
Whiskers/
├── src/
│   ├── Whiskers/               # the application
│   │   ├── Components/         # Blazor UI (Pages, Layout, Shared)
│   │   ├── Configuration/      # strongly-typed settings classes
│   │   ├── Hubs/               # SignalR hubs (container + terminal streams)
│   │   ├── Mcp/                # MCP server tools + permission layer
│   │   ├── Models/             # data models (Agent, Cloud, Cve, Hetzner, Hostinger)
│   │   ├── Services/           # all business logic (see Services/README.md)
│   │   ├── Utils/              # small helpers (secret redaction, shell quoting)
│   │   ├── wwwroot/            # static assets
│   │   └── Program.cs          # composition root (DI, middleware, MCP, auth)
│   └── Whiskers.Tests/         # xUnit test suite
├── deploy/                     # installer, compose overlays, Helm chart
│   └── telemetry/              # mesh/mTLS deploy templates (node_exporter, VictoriaMetrics, Tailscale ACL)
├── docs/                       # documentation (index: docs/README.md)
│   ├── ARCHITECTURE.md         # architecture, security-critical areas & change control
│   └── product/                # product positioning (see docs/product/POSITIONING.md)
├── Dockerfile
├── docker-compose.yml
└── README.md
```

`Services/` is the core of the app — [src/Whiskers/Services/README.md](../src/Whiskers/Services/README.md) walks
through it.

## Code conventions

- **Interface-first**: services are defined behind an `IFoo` interface and registered in DI; consumers depend on the
  interface.
- **English** in-code comments and XML docs throughout; the user-facing UI is localized (English default, German).
