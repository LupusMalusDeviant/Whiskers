# Project structure

Each source folder carries its own `README.md` describing the files within. For sizes, how a call flows from an MCP
client, the agent or a page to the host, where state lives and where to start a change, see the
**[codebase map](CODEBASE_MAP.md)**.

```
Whiskers/
├── src/
│   ├── Whiskers/                    # the application
│   │   ├── Components/              # Blazor UI (Pages, Layout, Shared)
│   │   ├── Configuration/           # strongly-typed settings classes
│   │   ├── HealthChecks/            # /healthz and /readyz probes
│   │   ├── Hubs/                    # SignalR hub (live container streams; the terminal uses no hub)
│   │   ├── Mcp/                     # MCP server: auth, guardrail filter, Tools/, Prompts/, Resources/
│   │   ├── Models/                  # data models
│   │   ├── Modules/                 # optional feature modules + module framework
│   │   ├── Resources/               # localized UI strings (English, German)
│   │   ├── Services/                # all business logic (see Services/README.md)
│   │   ├── Startup/                 # composition root extensions (DI, auth, middleware pipeline)
│   │   ├── Utils/                   # small helpers (secret redaction, shell quoting)
│   │   ├── wwwroot/                 # static assets
│   │   └── Program.cs               # composition root (calls the Startup/ extensions)
│   ├── Whiskers.Data/               # EF Core entities + DbContexts
│   ├── Whiskers.Migrations.Sqlite/  # generated migrations, SQLite
│   ├── Whiskers.Migrations.Postgres/# generated migrations, PostgreSQL
│   └── Whiskers.Tests/              # xUnit + bUnit test suite
├── deploy/                          # installer, compose overlays, Helm chart, telemetry and backup templates
├── docs/                            # documentation (index: docs/README.md): architecture, codebase map, ADRs,
│                                    # contracts, operations, product
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
