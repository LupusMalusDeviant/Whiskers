using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Vault;

/// <summary>Why a rotation cannot run (or go on): the start stops with this reason, nothing is changed by it.</summary>
public sealed class VaultKeyRotationException(string message) : InvalidOperationException(message);

/// <summary>What a rotation changed — counts, never values (the journal, the log and the audit row carry these).</summary>
public sealed record VaultKeyRotationCounts(int VaultEntries, int FieldValues, int KeyRingFiles, int SealedKeyFiles,
    int AuditLinks, int AuditCheckpoints);

/// <summary>The rotation journal <c>vault-rotation.json</c> in the data directory: phase and counts, no secret.</summary>
public sealed record VaultKeyRotationJournal
{
    public const string Started = "started";
    public const string Prepared = "prepared";
    public const string Committed = "committed";
    public const string Done = "done";

    public string Phase { get; init; } = Started;
    public DateTime StartedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public string? SafetyBackupId { get; init; }
    public string? FreshBackupId { get; init; }
    public VaultKeyRotationCounts? Counts { get; init; }

    /// <summary>The <c>vault.key_rotated</c> audit row is written (at the first start after the switch).</summary>
    public bool Audited { get; init; }
}

/// <summary>
/// Contract 0015 Part B (INV-52): replaces <c>VAULT_KEY</c> in place — every consumer of <see cref="VaultKeyConsumers"/>
/// sealed or signed with the current key is re-sealed or re-signed with the new one, all or nothing, with a journal that
/// lets a crash at any point either leave the old state complete (before the database commit) or finish the new one
/// (after it). No crypto of its own: vault entries and protected fields go through <see cref="VaultService"/>, signatures
/// through <see cref="AuditChain"/>.
/// </summary>
public sealed class VaultKeyRotation
{
    public const string JournalFileName = "vault-rotation.json";
    public const string RotatingSuffix = ".rotating";
    public const string SafetyBackupLabel = "pre-rotation";
    public const string FreshBackupLabel = "post-rotation";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly DataPathOptions _paths;
    private readonly IServiceProvider _db;
    private readonly string _oldKey;
    private readonly string _newKey;
    private readonly ILogger _logger;
    private readonly Func<string, string, CancellationToken, Task<string?>>? _backup;

    /// <param name="db">Where a <see cref="MetricsDbContext"/> is resolved from (one scope per step).</param>
    /// <param name="backup">Takes a self-backup labelled (first argument) and sealed with the key (second); returns its id.</param>
    public VaultKeyRotation(DataPathOptions paths, IServiceProvider db, string oldKey, string newKey, ILogger? logger = null,
        Func<string, string, CancellationToken, Task<string?>>? backup = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(oldKey);
        ArgumentException.ThrowIfNullOrEmpty(newKey);
        _paths = paths;
        _db = db;
        _oldKey = oldKey;
        _newKey = newKey;
        _logger = logger ?? NullLogger.Instance;
        _backup = backup;
    }

    /// <summary>Tests only: called after each named step ("checked", "safety-copy", "prepared", "before-commit",
    /// "committed", "switched"); throw from it to model a crash there.</summary>
    internal Action<string>? FaultAt { get; init; }

    public static string JournalPath(DataPathOptions paths) => Path.Combine(paths.RootDir, JournalFileName);

    public static VaultKeyRotationJournal? ReadJournal(DataPathOptions paths)
    {
        var path = JournalPath(paths);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<VaultKeyRotationJournal>(File.ReadAllText(path)); }
        catch (JsonException) { throw new VaultKeyRotationException($"The rotation journal {JournalFileName} cannot be read; it is left as it is."); }
    }

    private void WriteJournal(VaultKeyRotationJournal journal) =>
        DurableFile.WriteAllText(JournalPath(_paths), JsonSerializer.Serialize(journal, Json));

    /// <summary>Runs (or resumes) the rotation. Returns the journal as it stands at the end (phase <c>done</c>).</summary>
    public async Task<VaultKeyRotationJournal> RunAsync(CancellationToken ct = default)
    {
        var journal = ReadJournal(_paths);
        if (string.Equals(_oldKey, _newKey, StringComparison.Ordinal))
        {
            // The key file was already replaced and VAULT_KEY_NEW_FILE is still set: nothing to do once the rotation is done.
            if (journal?.Phase == VaultKeyRotationJournal.Done && await OpensVaultAsync(_newKey)) return journal;
            throw new VaultKeyRotationException("VAULT_KEY_NEW is the current VAULT_KEY: nothing to rotate. Remove VAULT_KEY_NEW_FILE.");
        }

        if (journal?.Phase == VaultKeyRotationJournal.Done && await OpensVaultAsync(_newKey))
            return journal;   // already rotated: a later start with both keys set does nothing

        if (journal?.Phase == VaultKeyRotationJournal.Committed
            || (journal is not null && journal.Phase != VaultKeyRotationJournal.Done && await OpensVaultAsync(_newKey)))
        {
            _logger.LogWarning("Resuming the VAULT_KEY rotation after its database commit: switching the prepared files");
            return await FinishAsync(journal with { Phase = VaultKeyRotationJournal.Committed }, ct);
        }
        if (journal?.Phase == VaultKeyRotationJournal.Prepared)
        {
            if (!await DatabaseUnderOldKeyAsync(ct) && AllPrepared())
            {
                _logger.LogWarning("Resuming the VAULT_KEY rotation: the database was committed under the new key");
                return await FinishAsync(journal with { Phase = VaultKeyRotationJournal.Committed }, ct);
            }
            _logger.LogWarning("An interrupted VAULT_KEY rotation did not reach its commit: its prepared files are discarded and it starts over");
        }
        DiscardPrepared();

        // 1) Check: nothing changes unless all of this holds.
        using var oldVault = await OpenVaultAsync(_oldKey, _paths.VaultJson);
        if (oldVault.KeyState != VaultKeyState.Verified)
            throw new VaultKeyRotationException(await OpensVaultAsync(_newKey)
                ? "The vault is already under VAULT_KEY_NEW, but the rotation journal is missing: replace VAULT_KEY_FILE with the new key and remove VAULT_KEY_NEW_FILE."
                : "The current VAULT_KEY does not open the vault: a rotation needs the current key. Nothing was changed.");
        await CheckDatabaseAsync(ct);
        FaultAt?.Invoke("checked");

        // 2) Safety copy, sealed with the current key.
        var safety = _backup is null ? null : await _backup(SafetyBackupLabel, _oldKey, ct);
        journal = new VaultKeyRotationJournal { Phase = VaultKeyRotationJournal.Started, StartedAtUtc = DateTime.UtcNow, SafetyBackupId = safety };
        WriteJournal(journal);
        FaultAt?.Invoke("safety-copy");

        // 3) Every file consumer re-sealed beside its original, read back with the new key.
        using var newVault = await PrepareVaultAsync(oldVault, ct);
        var keyRing = PrepareKeyRing(oldVault, newVault);
        var sealedKeys = PrepareSealedKeys(oldVault, newVault);
        PrepareCheckpointFile();
        journal = journal with
        {
            Phase = VaultKeyRotationJournal.Prepared,
            Counts = new VaultKeyRotationCounts(oldVault.ListSecrets().Count, 0, keyRing, sealedKeys, 0, 0),
        };
        WriteJournal(journal);
        FaultAt?.Invoke("prepared");

        // 4) The database in one transaction.
        var (fields, links, checkpoints) = await RewriteDatabaseAsync(oldVault, newVault, ct);
        journal = journal with
        {
            Phase = VaultKeyRotationJournal.Committed,
            Counts = journal.Counts! with { FieldValues = fields, AuditLinks = links, AuditCheckpoints = checkpoints },
        };
        WriteJournal(journal);
        FaultAt?.Invoke("committed");

        return await FinishAsync(journal, ct);
    }

    // ---- 5) and 6) switch and done ----------------------------------------------------------------------------

    private async Task<VaultKeyRotationJournal> FinishAsync(VaultKeyRotationJournal journal, CancellationToken ct)
    {
        foreach (var rotating in PreparedFiles())
            File.Move(rotating, rotating[..^RotatingSuffix.Length], overwrite: true);
        RemovePreviousGenerations();
        FaultAt?.Invoke("switched");

        if (!await OpensVaultAsync(_newKey))
            throw new VaultKeyRotationException("After the switch the vault does not open with VAULT_KEY_NEW; the start stops. Restore the pre-rotation backup.");
        journal = journal with { Phase = VaultKeyRotationJournal.Done, CompletedAtUtc = journal.CompletedAtUtc ?? DateTime.UtcNow };
        WriteJournal(journal);

        // D9: a fresh self-backup under the new key right after the rotation.
        if (_backup is not null && journal.FreshBackupId is null)
        {
            try
            {
                journal = journal with { FreshBackupId = await _backup(FreshBackupLabel, _newKey, ct) };
                WriteJournal(journal);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "The self-backup after the VAULT_KEY rotation failed; take one by hand");
            }
        }
        _logger.LogWarning("VAULT_KEY rotated: {Entries} vault entries, {Fields} protected values, {KeyRing} key-ring file(s), "
                           + "{Keys} sealed key file(s), {Links} audit links and {Checkpoints} checkpoints now under the new key. "
                           + "Replace VAULT_KEY_FILE with the new key and remove VAULT_KEY_NEW_FILE.",
            journal.Counts?.VaultEntries, journal.Counts?.FieldValues, journal.Counts?.KeyRingFiles, journal.Counts?.SealedKeyFiles,
            journal.Counts?.AuditLinks, journal.Counts?.AuditCheckpoints);
        return journal;
    }

    private IEnumerable<string> PreparedFiles()
    {
        var found = new List<string>();
        void Scan(string dir, SearchOption option)
        {
            if (Directory.Exists(dir)) found.AddRange(Directory.EnumerateFiles(dir, "*" + RotatingSuffix, option));
        }
        Scan(_paths.RootDir, SearchOption.TopDirectoryOnly);
        Scan(_paths.KeysDir, SearchOption.TopDirectoryOnly);
        Scan(_paths.SshKeysDir, SearchOption.AllDirectories);
        Scan(_paths.MtlsDir, SearchOption.AllDirectories);
        return found.Distinct(StringComparer.Ordinal).ToList();
    }

    private bool AllPrepared() => File.Exists(_paths.VaultJson + RotatingSuffix);

    private void DiscardPrepared()
    {
        foreach (var file in PreparedFiles()) File.Delete(file);
        foreach (var leftover in new[] { _paths.VaultJson + RotatingSuffix + DurableFile.BackupSuffix })
            if (File.Exists(leftover)) File.Delete(leftover);
    }

    /// <summary>Previous generations are never left sealed with the old key.</summary>
    private void RemovePreviousGenerations()
    {
        var candidates = new List<string>
        {
            _paths.VaultJson + DurableFile.BackupSuffix,
            _paths.VaultJson + RotatingSuffix + DurableFile.BackupSuffix,
            _paths.AuditCheckpointJson + DurableFile.BackupSuffix,
        };
        var dir = Path.GetDirectoryName(_paths.VaultJson)!;
        if (Directory.Exists(dir))
            candidates.AddRange(Directory.EnumerateFiles(dir, Path.GetFileName(_paths.VaultJson) + ".legacy-cbc*"));
        foreach (var file in candidates.Where(File.Exists)) File.Delete(file);
    }

    // ---- 1) check --------------------------------------------------------------------------------------------

    private async Task CheckDatabaseAsync(CancellationToken ct)
    {
        using var scope = _db.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count > 0)
            throw new VaultKeyRotationException($"The database schema is not current ({pending.Count} migration(s) pending): start "
                                                + "once with the current VAULT_KEY only, then rotate. Nothing was changed.");
        // D8: the whole trail must verify with the current key, or nothing is re-signed.
        var report = await new AuditChain(_oldKey, _paths.AuditCheckpointJson).VerifyAsync(db, ct);
        if (!report.Intact || !report.Complete)
            throw new VaultKeyRotationException("The audit trail does not verify completely with the current VAULT_KEY ("
                                                + (report.Problems.FirstOrDefault() ?? "incomplete check")
                                                + "): a rotation would re-sign what cannot be proven. Nothing was changed.");
    }

    /// <summary>Whether anything in the database still verifies or opens only under the old key.</summary>
    private async Task<bool> DatabaseUnderOldKeyAsync(CancellationToken ct)
    {
        using var scope = _db.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var oldChain = new AuditChain(_oldKey, null);
        var checkpoint = await db.AuditCheckpoints.AsNoTracking().Where(c => c.Mac != null).OrderBy(c => c.Id).FirstOrDefaultAsync(ct);
        if (checkpoint is not null) return oldChain.CheckpointSignatureValid(checkpoint);
        var link = await db.AuditChain.AsNoTracking().Where(l => l.Mac != null).OrderBy(l => l.Seq).FirstOrDefaultAsync(ct);
        if (link is not null) return oldChain.LinkSignatureValid(link);
        var webhook = await db.Webhooks.AsNoTracking().Where(w => w.Secret.StartsWith(ProtectedFields.Prefix)).FirstOrDefaultAsync(ct);
        var snapshot = await db.UpdateRollbacks.AsNoTracking().Where(r => r.ConfigJson.StartsWith(ProtectedFields.Prefix)).FirstOrDefaultAsync(ct);
        if (webhook is null && snapshot is null) return false;   // nothing keyed in the database: either side is the same
        using var old = await OpenVaultAsync(_oldKey, _paths.VaultJson, readOnly: true);
        return webhook is not null
            ? old.TryUnprotectField(ProtectedFields.WebhookSecret(webhook.WebhookId), webhook.Secret, out _)
            : old.TryUnprotectField(ProtectedFields.RollbackSnapshot(snapshot!.ServerId, snapshot.ContainerName), snapshot.ConfigJson, out _);
    }

    // ---- 3) files --------------------------------------------------------------------------------------------

    /// <summary>vault.json with a new salt, a new key check and every entry, beside the original.</summary>
    private async Task<DisposableVault> PrepareVaultAsync(DisposableVault oldVault, CancellationToken ct)
    {
        var rotating = _paths.VaultJson + RotatingSuffix;
        var entries = (await new JsonFileStore<VaultData>(_paths.VaultJson).LoadAsync()).Entries;
        var plain = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
            plain[entry.Key] = oldVault.GetSecret(entry.Key)
                               ?? throw new VaultKeyRotationException($"The vault entry {entry.Key} does not open with the current key. Nothing was changed.");

        using (var fresh = await OpenVaultAsync(_newKey, rotating))
        {
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                await fresh.SetSecretAsync(entry.Key, plain[entry.Key], entry.ContainerId, entry.ContainerName, entry.RotateAfterDays);
            }
        }
        // The entries keep their dates (rotation reminders count from them).
        var store = new JsonFileStore<VaultData>(rotating);
        var data = await store.LoadAsync();
        foreach (var entry in data.Entries)
            if (entries.FirstOrDefault(e => e.Key == entry.Key) is { } before)
                (entry.CreatedAt, entry.UpdatedAt) = (before.CreatedAt, before.UpdatedAt);
        await store.SaveAsync(data);
        if (File.Exists(rotating + DurableFile.BackupSuffix)) File.Delete(rotating + DurableFile.BackupSuffix);

        var reopened = await OpenVaultAsync(_newKey, rotating);
        foreach (var (key, value) in plain)
            if (reopened.GetSecret(key) != value)
                throw new VaultKeyRotationException($"The re-sealed vault entry {key} did not read back. Nothing was changed.");
        return reopened;
    }

    private int PrepareKeyRing(DisposableVault oldVault, DisposableVault newVault)
    {
        if (!Directory.Exists(_paths.KeysDir)) return 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(_paths.KeysDir, "*.xml"))
        {
            var root = XElement.Load(file);
            var sealedValues = root.Descendants(VaultKeyRing.VaultProtected).ToList();
            if (sealedValues.Count == 0) continue;
            foreach (var element in sealedValues)
                element.Value = Reseal(oldVault, newVault, ProtectedFields.DataProtectionKey, element.Value, Path.GetFileName(file));
            using (var stream = new FileStream(file + RotatingSuffix, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                root.Save(stream);
                stream.Flush(flushToDisk: true);
            }
            count++;
        }
        return count;
    }

    private int PrepareSealedKeys(DisposableVault oldVault, DisposableVault newVault)
    {
        var oldKeys = new SealedKeyFiles(oldVault, _paths);
        var newKeys = new SealedKeyFiles(newVault, _paths);
        var count = 0;
        foreach (var (path, purpose) in KeyMaterialMigration.Candidates(_paths).Where(c => SealedKeyFiles.IsSealedFile(c.Path)))
        {
            SealedKeyMaterial original;
            try { original = oldKeys.Read(path, purpose); }
            catch (SealedKeyException ex)
            {
                throw new VaultKeyRotationException($"The sealed key {Path.GetRelativePath(_paths.RootDir, path)} does not open with the current key ({ex.Message}). Nothing was changed.");
            }
            using (original)
            {
                newKeys.Write(path + RotatingSuffix, purpose, original.Pem);
                using var back = newKeys.Read(path + RotatingSuffix, purpose);
                if (!back.Pem.SequenceEqual(original.Pem))
                    throw new VaultKeyRotationException("A re-sealed key did not read back. Nothing was changed.");
            }
            count++;
        }
        return count;
    }

    private void PrepareCheckpointFile()
    {
        if (!File.Exists(_paths.AuditCheckpointJson)) return;
        var checkpoint = JsonSerializer.Deserialize<AuditCheckpointEntity>(File.ReadAllText(_paths.AuditCheckpointJson))
                         ?? throw new VaultKeyRotationException("The audit checkpoint file is empty. Nothing was changed.");
        if (checkpoint.Mac is null) return;
        var newChain = new AuditChain(_newKey, null);
        checkpoint.Mac = newChain.CheckpointSignature(checkpoint);
        DurableFile.WriteAllText(_paths.AuditCheckpointJson + RotatingSuffix, JsonSerializer.Serialize(checkpoint));
    }

    private static string Reseal(DisposableVault oldVault, DisposableVault newVault, string purpose, string stored, string what)
    {
        if (!oldVault.TryUnprotectField(purpose, stored, out var plain))
            throw new VaultKeyRotationException($"A protected value ({what}) does not open with the current key. Nothing was changed.");
        var resealed = newVault.ProtectField(purpose, plain) ?? throw new VaultKeyRotationException("The new vault is not usable.");
        if (!newVault.TryUnprotectField(purpose, resealed, out var back) || back != plain)
            throw new VaultKeyRotationException($"A re-sealed value ({what}) did not read back. Nothing was changed.");
        return resealed;
    }

    // ---- 4) database -----------------------------------------------------------------------------------------

    private async Task<(int Fields, int Links, int Checkpoints)> RewriteDatabaseAsync(DisposableVault oldVault, DisposableVault newVault,
        CancellationToken ct)
    {
        using var scope = _db.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var newChain = new AuditChain(_newKey, null);
        var result = (Fields: 0, Links: 0, Checkpoints: 0);

        await ProtectedFieldMigration.WithoutRemnantsAsync(db, async () =>
        {
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var fields = 0;
                foreach (var webhook in await db.Webhooks.Where(w => w.Secret.StartsWith(ProtectedFields.Prefix)).ToListAsync(ct))
                {
                    webhook.Secret = Reseal(oldVault, newVault, ProtectedFields.WebhookSecret(webhook.WebhookId), webhook.Secret, "webhook secret");
                    fields++;
                }
                foreach (var snapshot in await db.UpdateRollbacks.Where(r => r.ConfigJson.StartsWith(ProtectedFields.Prefix)).ToListAsync(ct))
                {
                    snapshot.ConfigJson = Reseal(oldVault, newVault,
                        ProtectedFields.RollbackSnapshot(snapshot.ServerId, snapshot.ContainerName), snapshot.ConfigJson, "rollback snapshot");
                    fields++;
                }
                var links = await db.AuditChain.Where(l => l.Mac != null).ToListAsync(ct);
                foreach (var link in links) link.Mac = newChain.LinkSignature(link.Hash);
                var checkpoints = await db.AuditCheckpoints.Where(c => c.Mac != null).ToListAsync(ct);
                foreach (var checkpoint in checkpoints) checkpoint.Mac = newChain.CheckpointSignature(checkpoint);
                await db.SaveChangesAsync(ct);
                FaultAt?.Invoke("before-commit");
                await tx.CommitAsync(ct);
                result = (fields, links.Count, checkpoints.Count);
            });
        }, _logger, ct);
        return result;
    }

    // ---- vaults ----------------------------------------------------------------------------------------------

    private async Task<bool> OpensVaultAsync(string key)
    {
        if (!File.Exists(_paths.VaultJson)) return false;
        using var vault = await OpenVaultAsync(key, _paths.VaultJson, readOnly: true);
        return vault.KeyState == VaultKeyState.Verified;
    }

    /// <summary>A vault over <paramref name="path"/> opened with <paramref name="key"/> only — the environment's
    /// VAULT_KEY_FILE is shadowed, so the key is exactly this one. <paramref name="readOnly"/>: on a copy, so opening never
    /// writes the file (a key check, a legacy conversion).</summary>
    private static async Task<DisposableVault> OpenVaultAsync(string key, string path, bool readOnly = false)
    {
        string? copyDir = null;
        if (readOnly && File.Exists(path))
        {
            copyDir = Path.Combine(Path.GetTempPath(), "whiskers-rotation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(copyDir);
            var copy = Path.Combine(copyDir, "vault.json");
            File.Copy(path, copy);
            path = copy;
        }
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["VAULT_KEY_FILE"] = "", ["VAULT_KEY"] = key })
            .Build();
        var vault = new DisposableVault(config, path, copyDir);
        await vault.InitializeAsync();
        return vault;
    }

    /// <summary>A vault opened for the rotation; a read-only copy is removed on dispose.</summary>
    private sealed class DisposableVault(IConfiguration config, string path, string? copyDir)
        : VaultService(config, NullLogger<VaultService>.Instance, path), IDisposable
    {
        public void Dispose()
        {
            if (copyDir is null) return;
            try { Directory.Delete(copyDir, recursive: true); } catch (IOException) { }
        }
    }
}
