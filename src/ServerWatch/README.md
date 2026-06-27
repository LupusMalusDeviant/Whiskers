# ServerWatch (application project)

The ASP.NET Core / Blazor Server application. For project overview, setup and deployment see the **[repository README](../../README.md)**; for the SSH-key-free design see **[docs/ARCHITECTURE.md](../../docs/ARCHITECTURE.md)**.

## Layout

| Folder | What |
|---|---|
| [`Components/`](Components/) | Blazor UI — [`Pages/`](Components/Pages/), [`Layout/`](Components/Layout/), [`Shared/`](Components/Shared/) |
| [`Services/`](Services/) | All business logic — see the [services tour](Services/README.md) |
| [`Mcp/`](Mcp/) | MCP server: auth pipeline + [`Tools/`](Mcp/Tools/) |
| [`Models/`](Models/) | Data models (+ Agent, Cloud, Cve, Hetzner, Hostinger) |
| [`Configuration/`](Configuration/) | Strongly-typed settings classes |
| [`Hubs/`](Hubs/) | SignalR hubs (container + terminal streams) |
| [`Utils/`](Utils/) | Small helpers |
| [`wwwroot/`](wwwroot/) | Static assets + JS interop |
| [`Properties/`](Properties/) | Local run profiles |
| `Program.cs` | Composition root — DI registrations, middleware, auth, MCP mapping, startup init |

## Build & test

```bash
dotnet build ServerWatch.csproj          # 0 warnings expected
dotnet test ../ServerWatch.Tests/        # xUnit suite
dotnet run --project ServerWatch.csproj  # listens on :8080
```

## Conventions

- **Interface-first** — every service behind an `IFoo`; consumers depend on the interface.
- **English** in-code comments and XML docs; user-facing strings are German.
