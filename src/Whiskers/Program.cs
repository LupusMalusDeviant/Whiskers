using Whiskers.Configuration;
using Whiskers.Services.Persistence;
using Whiskers.Startup;

// Composition root (RoadToSAP §6 DoD): a thin orchestrator. The service registrations, the security-sensitive
// authentication wiring and the HTTP pipeline all live in Whiskers.Startup extension methods — every block was
// moved VERBATIM (same services, same middleware order). Read those files for the detail; this is the sequence.
var builder = WebApplication.CreateBuilder(args);

// Central data-directory resolver (WHISKERS_DATA_DIR, default /app/data). Built here at bootstrap because the
// config layers, DataProtection keys and the DbContext connection string all need it before the DI container.
var dataPaths = DataPathOptions.FromConfiguration(builder.Configuration);

// One-time data migration CLI: `dotnet Whiskers.dll --migrate-to-postgres "<npgsql-conn>"`. Copies the SQLite
// data into a fresh PostgreSQL database and exits WITHOUT booting the web host. The source is never modified;
// the target must be empty (see SqliteToPostgresMigrator). Guarded so a normal boot is untouched.
if (args is ["--migrate-to-postgres", ..])
{
    var targetConn = args.Length > 1 ? args[1] : "";
    return await SqliteToPostgresMigrator.RunAsync(dataPaths, targetConn, Console.Out);
}

builder.AddWhiskersConfiguration(dataPaths);   // UI-writable config layers + data-protection keys
builder.AddWhiskersModules();                  // Core no-op defaults + module pipeline + MCP tools + nav registry
builder.AddWhiskersCoreServices(dataPaths);    // Docker, health, metrics, VPN, database, audit, startup initializers
builder.AddWhiskersAuthentication();           // cookie session + Google/OIDC providers, or LAN bypass (Off-Limits zone)
builder.AddWhiskersUi();                       // localization, MudBlazor, Blazor server components, SignalR

var app = builder.Build();

app.ConfigureWhiskersHttpPipeline();           // forwarded headers → fixed auth middleware chain → endpoints
await app.RunWhiskersStartupAsync();           // IInitializable warm-ups (in Order) + metrics-DB migration

app.Run();

// Normal boot returns 0 on shutdown; the --migrate-to-postgres branch above returns its own exit code.
return 0;

// Exposed so the test project's WebApplicationFactory<Program> can boot the app in-process for the module
// boot-matrix test (RoadToSAP §6 DoD). With top-level statements the generated Program class is otherwise
// internal; this partial declaration makes it public without changing any runtime behaviour.
public partial class Program { }
