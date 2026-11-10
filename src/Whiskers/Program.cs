using Whiskers.Configuration;
using Whiskers.Services.Backup;
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

// F3 self-restore: apply a pending restore BEFORE anything opens the DB or reads the DataProtection key ring.
// A normal boot has no marker → this is a cheap no-op. After a restore it swaps the staged files into place;
// startup below then migrates the restored DB and rebuilds every cache from the restored files. It throws on
// an unrecoverable swap (→ process exit → restart retries) rather than booting on half-swapped state.
// The restore CLI (R1-34) applies nothing: a restore already staged waits for a real start (the CLI refuses a second).
// The account-recovery CLI (R1-49) runs next to the live instance (`docker compose exec`): it must never apply one.
var restoreCli = RestoreCli.Requested(args);
var recoveryCli = Whiskers.Services.Auth.LocalAccountRecoveryCli.Requested(args);
// The identity-binding CLI (R1-27) runs the same way next to the live instance.
var bindingCli = Whiskers.Services.Auth.IdentityBindingCli.Requested(args);
// The guardrail reset CLI (contract 0010) too.
var guardrailResetCli = Whiskers.Services.Guardrails.GuardrailResetCli.Requested(args);
var hostCli = restoreCli || recoveryCli || bindingCli || guardrailResetCli;
if (!hostCli) RestoreBootHandler.ApplyPendingRestore(dataPaths, builder.Configuration);
// Contract 0015 (INV-52): a VAULT_KEY rotation (VAULT_KEY_NEW_FILE) runs here, before the host is built and before
// anything reads the key (the guardrail store's start comes later, in RunWhiskersStartupAsync); the rest of the
// process then reads the new one. A host CLI next to the live instance never rotates.
if (!hostCli && Whiskers.Services.Vault.VaultKeyRotationBootHandler.Apply(dataPaths, builder.Configuration, Console.Out) is { Rotated: true } rotation)
    builder.Configuration.AddInMemoryCollection(rotation.ConfigurationOverride);

builder.AddWhiskersConfiguration(dataPaths);   // UI-writable config layers + data-protection keys
// R1-43: the configuration has read the environment; VPN keys leave it now, so no child process inherits them.
Whiskers.Utils.SecretEnvironment.RemoveFromProcess();
builder.AddWhiskersModules();                  // Core no-op defaults + module pipeline + MCP tools + nav registry
builder.AddWhiskersCoreServices(dataPaths);    // Docker, health, metrics, VPN, database, audit, startup initializers
builder.AddWhiskersAuthentication();           // cookie session + Google/OIDC providers, or LAN bypass (Off-Limits zone)
builder.AddWhiskersUi();                       // localization, MudBlazor, Blazor server components, SignalR
builder.AddWhiskersMutationGateway();         // ADR-0005: gateway + scope guard around mutating services
builder.AddWhiskersLogRedaction();            // R1-25: every log provider writes redacted entries only (last)

var app = builder.Build();

// R1-34 recovery CLI: `dotnet Whiskers.dll --restore-backup <id|latest-pre-upgrade> [--for-previous-release]` stages
// an on-box self-backup and exits without starting the web host or migrating anything; the next start applies it.
if (restoreCli)
{
    return await RestoreCli.RunAsync(app.Services.GetRequiredService<IBackupService>(),
        app.Services.GetService<Whiskers.Services.Vault.IVaultService>(), dataPaths, RestoreCli.Arguments(args), Console.Out);
}

// R1-49 host-level recovery: `dotnet Whiskers.dll --recover-local-account <email>` issues a one-time recovery code for a
// local account, prints it to this terminal only and exits without starting the web host.
if (recoveryCli)
{
    return await Whiskers.Services.Auth.LocalAccountRecoveryCli.RunAsync(app.Services,
        Whiskers.Services.Auth.LocalAccountRecoveryCli.Arguments(args), Console.Out);
}

// R1-27 host-level confirmation: `dotnet Whiskers.dll --confirm-identity-binding <email> [--issuer <issuer>]` binds the
// newest pending Google/OIDC sign-in request of a configured address and exits without starting the web host.
if (bindingCli)
{
    return await Whiskers.Services.Auth.IdentityBindingCli.RunAsync(app.Services,
        Whiskers.Services.Auth.IdentityBindingCli.Arguments(args), Console.Out);
}

// Contract 0010 D6: `dotnet Whiskers.dll --reset-guardrails` writes the safe default guardrail policy with the kill switch
// on, when guardrails.json and guardrails.json.bak are both missing, and exits without starting the web host.
if (guardrailResetCli)
{
    return await Whiskers.Services.Guardrails.GuardrailResetCli.RunAsync(app.Services,
        Whiskers.Services.Guardrails.GuardrailResetCli.Arguments(args), Console.Out);
}

app.ConfigureWhiskersHttpPipeline();           // forwarded headers → fixed auth middleware chain → endpoints
await app.RunWhiskersStartupAsync();           // IInitializable warm-ups (in Order) + metrics-DB migration

app.Run();

// Normal boot returns 0 on shutdown; the --migrate-to-postgres branch above returns its own exit code.
return 0;

// Exposed so the test project's WebApplicationFactory<Program> can boot the app in-process for the module
// boot-matrix test (RoadToSAP §6 DoD). With top-level statements the generated Program class is otherwise
// internal; this partial declaration makes it public without changing any runtime behaviour.
public partial class Program { }
