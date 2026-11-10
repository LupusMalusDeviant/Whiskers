using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Services.Backup;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Vault;

/// <summary>What the start runs with after <see cref="VaultKeyRotationBootHandler.Apply"/>.</summary>
/// <param name="Rotated">A rotation is complete (now or earlier) and the new key is the one to run with.</param>
/// <param name="ConfigurationOverride">Settings that make <see cref="SecretSource.VaultKey"/> return the new key for the rest
/// of this process (empty when nothing changes). Never logged.</param>
public sealed record VaultKeyRotationResult(bool Rotated, IReadOnlyDictionary<string, string?> ConfigurationOverride)
{
    public static readonly VaultKeyRotationResult None = new(false, new Dictionary<string, string?>());
}

/// <summary>
/// Contract 0015 Part B, D7: the rotation runs at start, before the host is built (after a pending restore), when
/// <c>VAULT_KEY_NEW_FILE</c> (or <c>VAULT_KEY_NEW</c>) is set next to the current key — one process by construction,
/// nothing else runs yet. Without it a rotation that stopped after its commit refuses the start (it needs the new key to
/// finish); one that stopped before is discarded at the next rotation and changes nothing meanwhile.
/// </summary>
public static class VaultKeyRotationBootHandler
{
    public const string NewKeyName = "VAULT_KEY_NEW";

    public static VaultKeyRotationResult Apply(DataPathOptions paths, IConfiguration configuration, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(configuration);
        var newKey = SecretSource.Read(configuration, NewKeyName);
        var journal = VaultKeyRotation.ReadJournal(paths);
        if (newKey is null)
        {
            if (journal?.Phase == VaultKeyRotationJournal.Committed)
                throw new VaultKeyRotationException("A VAULT_KEY rotation stopped after its database commit and needs the new key to "
                                                   + "finish: set VAULT_KEY_NEW_FILE to the new key again and restart.");
            return VaultKeyRotationResult.None;
        }
        var oldKey = SecretSource.VaultKey(configuration)
                     ?? throw new VaultKeyRotationException("VAULT_KEY_NEW_FILE is set, but no current VAULT_KEY: a rotation needs both.");

        var (provider, connectionString) = DatabaseRegistration.Resolve(configuration);
        var services = new ServiceCollection();
        services.AddWhiskersDbContexts(provider, connectionString, paths);
        using var provider2 = services.BuildServiceProvider();
        var logger = new TextLogger(log);
        var rotation = new VaultKeyRotation(paths, provider2, oldKey, newKey, logger,
            (label, key, ct) => BackupAsync(provider2, paths, configuration, label, key, ct));
        rotation.RunAsync().GetAwaiter().GetResult();
        return new VaultKeyRotationResult(true, Override(configuration));
    }

    /// <summary>The audit row <c>vault.key_rotated</c> (principal <c>system:startup</c>, counts only), written once on the
    /// re-signed chain at the first start after a completed rotation; retried at the next start when it fails.</summary>
    public static async Task RecordRotatedAsync(DataPathOptions paths, Whiskers.Services.AuditLog.IAuditLogService audit, ILogger logger)
    {
        VaultKeyRotationJournal? journal;
        try { journal = VaultKeyRotation.ReadJournal(paths); }
        catch (VaultKeyRotationException ex) { logger.LogError("{Reason}", ex.Message); return; }
        if (journal is not { Phase: VaultKeyRotationJournal.Done, Audited: false }) return;
        var details = System.Text.Json.JsonSerializer.Serialize(new
        {
            startedAt = journal.StartedAtUtc,
            completedAt = journal.CompletedAtUtc,
            safetyBackup = journal.SafetyBackupId,
            freshBackup = journal.FreshBackupId,
            counts = journal.Counts,
        });
        if (!await audit.TryLogAsync("system:startup", "system", "vault.key_rotated", "vault", "vault", "VAULT_KEY", details))
        {
            logger.LogError("The VAULT_KEY rotation could not be recorded in the audit trail; it is retried at the next start");
            return;
        }
        DurableFile.WriteAllText(VaultKeyRotation.JournalPath(paths),
            System.Text.Json.JsonSerializer.Serialize(journal with { Audited = true }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>The settings that make the rest of the process read the new key: its file, else its value.</summary>
    private static Dictionary<string, string?> Override(IConfiguration configuration)
    {
        var file = configuration[NewKeyName + "_FILE"] ?? Environment.GetEnvironmentVariable(NewKeyName + "_FILE");
        if (!string.IsNullOrWhiteSpace(file)) return new() { ["VAULT_KEY_FILE"] = file };
        return new() { ["VAULT_KEY_FILE"] = "", ["VAULT_KEY"] = SecretSource.Read(configuration, NewKeyName) };
    }

    /// <summary>A self-backup sealed with <paramref name="key"/> through the app's own backup service.</summary>
    private static async Task<string?> BackupAsync(IServiceProvider db, DataPathOptions paths, IConfiguration configuration,
        string label, string key, CancellationToken ct)
    {
        var withKey = new ConfigurationBuilder()
            .AddConfiguration(configuration)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["VAULT_KEY_FILE"] = "", ["VAULT_KEY"] = key })
            .Build();
        var backup = new BackupService(db.GetRequiredService<IServiceScopeFactory>(), new Whiskers.Services.Maintenance.MaintenanceStateService(),
            new NoLifetime(), withKey, NullLogger<BackupService>.Instance, paths);
        var info = await backup.CreateVaultRotationBackupAsync(label, ct);
        return info.Id;
    }

    private sealed class NoLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    /// <summary>The start log before the host's logging exists: message templates rendered with their values (counts
    /// and phases only — the rotation never logs a key).</summary>
    private sealed class TextLogger(TextWriter writer) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            writer.WriteLine($"[vault-rotation] {logLevel}: {formatter(state, exception)}"
                             + (exception is null ? "" : $" ({exception.GetType().Name}: {exception.Message})"));
        }
    }
}
