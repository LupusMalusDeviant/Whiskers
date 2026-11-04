# Whiskers (application project)

The ASP.NET Core / Blazor Server application. For project overview, setup and deployment see the **[repository README](../../README.md)**; for the SSH-key-free design see **[docs/ARCHITECTURE.md](../../docs/ARCHITECTURE.md)**; for a map of the whole code base (areas, sizes, entry points, how a call flows) see **[docs/CODEBASE_MAP.md](../../docs/CODEBASE_MAP.md)**.

## Layout

| Folder | What |
|---|---|
| [`Components/`](Components/) | Blazor UI, [`Pages/`](Components/Pages/), [`Layout/`](Components/Layout/), [`Shared/`](Components/Shared/) |
| [`Services/`](Services/) | All business logic, see the [services tour](Services/README.md) |
| [`Mcp/`](Mcp/) | MCP server: authentication and the guardrail filter, [`Tools/`](Mcp/Tools/), [`Prompts/`](Mcp/Prompts/), [`Resources/`](Mcp/Resources/) |
| [`Modules/`](Modules/) | The optional feature modules and the module framework (`IWhiskersModule`, `ModuleCatalog`, `ModuleRegistry`) |
| [`Startup/`](Startup/) | The composition root's extension methods: hosting and DI, authentication, the middleware pipeline, logging, the mutation gateway |
| [`Models/`](Models/) | Data models (+ Agent, Cloud, Cve, Dns, Help, Hetzner, Hostinger) |
| [`Configuration/`](Configuration/) | Strongly-typed settings classes and their startup validation |
| [`HealthChecks/`](HealthChecks/) | Liveness and readiness probes behind `/healthz` and `/readyz` (database, server config, maintenance, audit trail) |
| [`Hubs/`](Hubs/) | SignalR hubs (container + terminal streams) |
| [`Resources/`](Resources/) | Localized UI strings (`SharedResource.resx` English default, `.de.resx` German) |
| [`Utils/`](Utils/) | Small helpers (secret redaction, shell quoting, command operands) |
| [`wwwroot/`](wwwroot/) | Static assets + JS interop |
| [`Properties/`](Properties/) | Local run profiles |
| `Program.cs` | Composition root: calls the `Startup/` extensions in order and runs the app |

## Build & test

```bash
dotnet build ../../Whiskers.slnx -c Release   # what CI runs
dotnet test ../../Whiskers.slnx               # xUnit suite (Docker/PostgreSQL smoke tests skip without Docker)
dotnet run --project Whiskers.csproj          # listens on :8080
```

## Conventions

- **Interface-first**: every service behind an `IFoo`; consumers depend on the interface.
- **English** in-code comments and XML docs; the UI is localized (English default, German) via `IStringLocalizer<SharedResource>`.
- Every folder carries a `README.md` describing its files.
