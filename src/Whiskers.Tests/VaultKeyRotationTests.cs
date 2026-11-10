using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Persistence;
using Whiskers.Services.Vault;

namespace Whiskers.Tests;

// Contract 0015 Part B (INV-52): VAULT_KEY is replaced in place — every consumer opens with the new key and no longer
// with the old one; a crash at any step leaves the old state complete (before the commit) or finishes the new one.
public sealed class VaultKeyRotationTests : IDisposable
{
    private const string OldKey = "rotation-old-passphrase";
    private const string NewKey = "rotation-new-passphrase";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-tests", "rotate-" + Guid.NewGuid().ToString("N"));
    private readonly DataPathOptions _paths;
    private readonly ServiceProvider _db;
    private readonly string _pem = SealedKeyFilesTests.NewPem();
    private string _webhookSecret = "";
    private string _keyRingSecret = "";

    public VaultKeyRotationTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new DataPathOptions(_root);
        var services = new ServiceCollection();
        services.AddWhiskersDbContexts("sqlite", null, _paths);
        services.AddSingleton<IAuditChain>(sp => new AuditChain(OldKey, _paths.AuditCheckpointJson, scopes: sp.GetRequiredService<IServiceScopeFactory>()));
        _db = services.BuildServiceProvider();
        using var scope = _db.CreateScope();
        scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.Migrate();
    }

    public void Dispose()
    {
        _db.Dispose();
        TestSqlite.ClearPools(_root);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private async Task<VaultService> Vault(string key)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["VAULT_KEY_FILE"] = "", ["VAULT_KEY"] = key }).Build();
        var vault = new VaultService(config, NullLogger<VaultService>.Instance, dataPaths: _paths);
        await vault.InitializeAsync();
        return vault;
    }

    private string SshKeyPath => Path.Combine(_paths.SshKeysDir, "edge", "id_ecdsa");
    private string KeyRingFile => Path.Combine(_paths.KeysDir, "key-1.xml");

    /// <summary>A data directory with every consumer under the old key.</summary>
    private async Task SeedAsync()
    {
        var vault = await Vault(OldKey);
        await vault.SetSecretAsync("cloud:edge", "cloud-api-key-value", rotateAfterDays: 90);

        Directory.CreateDirectory(Path.GetDirectoryName(SshKeyPath)!);
        new SealedKeyFiles(vault, _paths).Write(SshKeyPath, SealedKeyPurposes.SshKey("edge", "id_ecdsa"), _pem);

        Directory.CreateDirectory(_paths.KeysDir);
        _keyRingSecret = "<masterKey>" + Guid.NewGuid().ToString("N") + "</masterKey>";
        var sealedSecret = vault.ProtectField(ProtectedFields.DataProtectionKey, _keyRingSecret)!;
        new XElement("key", new XAttribute("id", "1"), new XElement("descriptor",
            VaultKeyRing.Placeholder(typeof(VaultXmlDecryptor), new XElement(VaultKeyRing.VaultProtected, sealedSecret)))).Save(KeyRingFile);

        _webhookSecret = "whsec-" + Guid.NewGuid().ToString("N");
        using (var scope = _db.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var webhook = new WebhookEntity { Name = "deploy", TargetId = "web" };
            webhook.Secret = vault.ProtectField(ProtectedFields.WebhookSecret(webhook.WebhookId), _webhookSecret)!;
            db.Webhooks.Add(webhook);
            db.UpdateRollbacks.Add(new UpdateRollbackEntity
            {
                ContainerName = "web", ServerId = "edge", CapturedAt = DateTime.UtcNow,
                ConfigJson = vault.ProtectField(ProtectedFields.RollbackSnapshot("edge", "web"), "{\"Env\":[\"X=1\"]}")!,
            });
            await db.SaveChangesAsync();
        }

        var audit = new AuditLogService(_db.GetRequiredService<IServiceScopeFactory>(), NullLogger<AuditLogService>.Instance);
        for (var i = 0; i < 3; i++)
            Assert.True(await audit.TryLogAsync("admin@local.test", "web", "container.restart", "container", $"c{i}", $"c{i}"));
        await _db.GetRequiredService<IAuditChain>().CheckpointAsync();
    }

    private VaultKeyRotation Rotation(Action<string>? fault = null, List<string>? backups = null) =>
        new(_paths, _db, OldKey, NewKey, NullLogger.Instance,
            backups is null ? null : (label, key, _) =>
            {
                backups.Add($"{label}:{(key == OldKey ? "old" : key == NewKey ? "new" : "?")}");
                return Task.FromResult<string?>(label);
            })
        { FaultAt = fault };

    /// <summary>Every consumer opens with <paramref name="key"/>.</summary>
    private async Task AssertEverythingOpensWith(string key)
    {
        var vault = await Vault(key);
        Assert.Equal(VaultKeyState.Verified, vault.KeyState);
        Assert.Equal("cloud-api-key-value", vault.GetSecret("cloud:edge"));
        Assert.Equal(90, vault.ListSecrets().Single().RotateAfterDays);

        using (var material = new SealedKeyFiles(vault, _paths).Read(SshKeyPath, SealedKeyPurposes.SshKey("edge", "id_ecdsa")))
            Assert.Equal(_pem, material.Pem.ToString());

        var stored = XElement.Load(KeyRingFile).Descendants(VaultKeyRing.VaultProtected).Single().Value;
        Assert.True(vault.TryUnprotectField(ProtectedFields.DataProtectionKey, stored, out var ring));
        Assert.Equal(_keyRingSecret, ring);

        using (var scope = _db.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var webhook = await db.Webhooks.AsNoTracking().SingleAsync();
            Assert.Equal(_webhookSecret, ProtectedFields.Read(vault, ProtectedFields.WebhookSecret(webhook.WebhookId), webhook.Secret));
            var snapshot = await db.UpdateRollbacks.AsNoTracking().SingleAsync();
            Assert.NotNull(ProtectedFields.Read(vault, ProtectedFields.RollbackSnapshot("edge", "web"), snapshot.ConfigJson));

            var report = await new AuditChain(key, _paths.AuditCheckpointJson).VerifyAsync(db, default);
            Assert.True(report.Intact, string.Join("; ", report.Problems));
            Assert.True(report.CheckpointsSigned);
        }
        Assert.Empty(Directory.EnumerateFiles(_root, "*" + VaultKeyRotation.RotatingSuffix, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Every_consumer_opens_with_the_new_key_and_no_longer_with_the_old_one()
    {
        await SeedAsync();
        var backups = new List<string>();

        var journal = await Rotation(backups: backups).RunAsync();

        Assert.Equal(VaultKeyRotationJournal.Done, journal.Phase);
        Assert.Equal(new VaultKeyRotationCounts(1, 2, 1, 1, 3, journal.Counts!.AuditCheckpoints), journal.Counts);
        Assert.True(journal.Counts.AuditCheckpoints >= 1);
        Assert.Equal(["pre-rotation:old", "post-rotation:new"], backups);
        await AssertEverythingOpensWith(NewKey);

        // The old key alone: the vault is Wrong (INV-54) and the trail's signatures do not verify under it.
        Assert.Equal(VaultKeyState.Wrong, (await Vault(OldKey)).KeyState);
        using var scope = _db.CreateScope();
        var old = await new AuditChain(OldKey, _paths.AuditCheckpointJson).VerifyAsync(scope.ServiceProvider.GetRequiredService<MetricsDbContext>(), default);
        Assert.False(old.Intact && old.CheckpointsSigned);
        // Previous generations are not left under the old key.
        Assert.False(File.Exists(_paths.VaultJson + DurableFile.BackupSuffix));
        Assert.False(File.Exists(_paths.AuditCheckpointJson + DurableFile.BackupSuffix));
    }

    [Theory]
    [InlineData("checked")]
    [InlineData("safety-copy")]
    [InlineData("prepared")]
    [InlineData("before-commit")]
    public async Task A_crash_before_the_commit_leaves_the_old_state_complete_and_the_next_start_rotates(string step)
    {
        await SeedAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Rotation(s => { if (s == step) throw new InvalidOperationException("crash at " + step); }).RunAsync());

        // Nothing switched: the old key opens everything (the prepared files aside are ignored by the app).
        foreach (var rotating in Directory.EnumerateFiles(_root, "*" + VaultKeyRotation.RotatingSuffix, SearchOption.AllDirectories).ToList())
            File.Move(rotating, rotating + ".aside");
        await AssertEverythingOpensWith(OldKey);
        foreach (var aside in Directory.EnumerateFiles(_root, "*.aside", SearchOption.AllDirectories).ToList())
            File.Move(aside, aside[..^".aside".Length]);

        Assert.Equal(VaultKeyRotationJournal.Done, (await Rotation().RunAsync()).Phase);
        await AssertEverythingOpensWith(NewKey);
    }

    [Theory]
    [InlineData("committed")]
    [InlineData("switched")]
    public async Task A_crash_after_the_commit_resumes_to_the_new_state(string step)
    {
        await SeedAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Rotation(s => { if (s == step) throw new InvalidOperationException("crash at " + step); }).RunAsync());

        Assert.Equal(VaultKeyRotationJournal.Done, (await Rotation().RunAsync()).Phase);
        await AssertEverythingOpensWith(NewKey);
    }

    [Fact]
    public async Task A_crash_between_the_commit_and_its_journal_entry_resumes_to_the_new_state()
    {
        await SeedAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Rotation(s => { if (s == "committed") throw new InvalidOperationException("crash"); }).RunAsync());
        // The journal write after the commit never happened.
        var journal = VaultKeyRotation.ReadJournal(_paths)! with { Phase = VaultKeyRotationJournal.Prepared };
        File.WriteAllText(VaultKeyRotation.JournalPath(_paths), System.Text.Json.JsonSerializer.Serialize(journal));

        Assert.Equal(VaultKeyRotationJournal.Done, (await Rotation().RunAsync()).Phase);
        await AssertEverythingOpensWith(NewKey);
    }

    [Fact]
    public async Task The_boot_handler_rotates_with_real_self_backups_marks_the_older_one_and_records_the_rotation()
    {
        await SeedAsync();
        var keys = Path.Combine(_root, "..", "keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(keys);
        File.WriteAllText(Path.Combine(keys, "old"), OldKey + "\n");
        File.WriteAllText(Path.Combine(keys, "new"), NewKey + "\n");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["VAULT_KEY_FILE"] = Path.Combine(keys, "old"),
            ["VAULT_KEY_NEW_FILE"] = Path.Combine(keys, "new"),
        }).Build();
        var log = new StringWriter();

        var result = VaultKeyRotationBootHandler.Apply(_paths, config, log);

        Assert.True(result.Rotated);
        Assert.Equal(Path.Combine(keys, "new"), result.ConfigurationOverride["VAULT_KEY_FILE"]);
        Assert.DoesNotContain(OldKey, log.ToString());
        Assert.DoesNotContain(NewKey, log.ToString());
        Assert.Contains("Replace VAULT_KEY_FILE", log.ToString());
        await AssertEverythingOpensWith(NewKey);

        // D9: the safety copy is sealed with the old key and marked; the fresh backup after the rotation is not.
        var running = new ConfigurationBuilder().AddConfiguration(config).AddInMemoryCollection(result.ConfigurationOverride).Build();
        var backups = await new Whiskers.Services.Backup.BackupService(_db.GetRequiredService<IServiceScopeFactory>(),
            new Whiskers.Services.Maintenance.MaintenanceStateService(), new NoLifetime(), running,
            NullLogger<Whiskers.Services.Backup.BackupService>.Instance, _paths).ListBackupsAsync();
        Assert.Equal(2, backups.Count);
        Assert.True(backups.Single(b => b.Label.StartsWith(VaultKeyRotation.SafetyBackupLabel, StringComparison.Ordinal)).SealedWithEarlierKey);
        Assert.False(backups.Single(b => b.Label.StartsWith(VaultKeyRotation.FreshBackupLabel, StringComparison.Ordinal)).SealedWithEarlierKey);

        // The readiness check asks to replace the key file while VAULT_KEY_NEW_FILE is set.
        var readiness = new Whiskers.Services.Setup.ProductionReadinessService(running, await Vault(NewKey),
            _db.GetRequiredService<IServiceScopeFactory>(), dataPaths: _paths, text: ReadinessTexts.English);
        Assert.Contains("Replace VAULT_KEY_FILE with the new key", readiness.RotationCheck()!.Detail);

        // The audit row, once, on the re-signed chain.
        var audit = new AuditLogService(_db.GetRequiredService<IServiceScopeFactory>(), NullLogger<AuditLogService>.Instance);
        await VaultKeyRotationBootHandler.RecordRotatedAsync(_paths, audit, NullLogger.Instance);
        await VaultKeyRotationBootHandler.RecordRotatedAsync(_paths, audit, NullLogger.Instance);
        using var scope = _db.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking().SingleAsync(a => a.Action == "vault.key_rotated");
        Assert.Equal("system:vault-rotation", row.PrincipalId);
        Assert.Equal("system", row.ActorType);
        Assert.DoesNotContain(NewKey, row.Details ?? "");
        Assert.True(VaultKeyRotation.ReadJournal(_paths)!.Audited);
    }

    [Fact]
    public async Task Without_the_new_key_a_rotation_stopped_after_its_commit_refuses_the_start()
    {
        await SeedAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Rotation(s => { if (s == "committed") throw new InvalidOperationException("crash"); }).RunAsync());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["VAULT_KEY"] = OldKey }).Build();

        var refused = Assert.Throws<VaultKeyRotationException>(() => VaultKeyRotationBootHandler.Apply(_paths, config, TextWriter.Null));
        Assert.Contains("VAULT_KEY_NEW_FILE", refused.Message);
    }

    private sealed class NoLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    [Fact]
    public async Task A_trail_that_does_not_verify_refuses_and_changes_nothing()
    {
        await SeedAsync();
        using (var scope = _db.CreateScope())
        {
#pragma warning disable EF1002 // test-only literal SQL
            await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database
                .ExecuteSqlRawAsync("UPDATE AuditLog SET Details = 'rewritten' WHERE Id = (SELECT MIN(Id) FROM AuditLog)");
#pragma warning restore EF1002
        }
        var vaultBefore = await File.ReadAllBytesAsync(_paths.VaultJson);
        var keyBefore = await File.ReadAllBytesAsync(SshKeyPath);

        var refused = await Assert.ThrowsAsync<VaultKeyRotationException>(() => Rotation().RunAsync());

        Assert.Contains("audit trail", refused.Message);
        Assert.Equal(vaultBefore, await File.ReadAllBytesAsync(_paths.VaultJson));
        Assert.Equal(keyBefore, await File.ReadAllBytesAsync(SshKeyPath));
        Assert.Null(VaultKeyRotation.ReadJournal(_paths));
        Assert.Equal(VaultKeyState.Verified, (await Vault(OldKey)).KeyState);
    }

    [Fact]
    public async Task A_later_start_with_both_keys_set_does_nothing_and_a_wrong_current_key_is_refused()
    {
        await SeedAsync();
        await Rotation().RunAsync();
        var vaultAfter = await File.ReadAllBytesAsync(_paths.VaultJson);

        Assert.Equal(VaultKeyRotationJournal.Done, (await Rotation().RunAsync()).Phase);
        Assert.Equal(vaultAfter, await File.ReadAllBytesAsync(_paths.VaultJson));
        // The key file was replaced, VAULT_KEY_NEW_FILE is still set: nothing to do either.
        Assert.Equal(VaultKeyRotationJournal.Done, (await new VaultKeyRotation(_paths, _db, NewKey, NewKey).RunAsync()).Phase);

        // A rotation with a current key that does not open the vault changes nothing.
        File.Delete(VaultKeyRotation.JournalPath(_paths));
        await Assert.ThrowsAsync<VaultKeyRotationException>(() => new VaultKeyRotation(_paths, _db, "not-the-key", "yet-another").RunAsync());
        Assert.Equal(vaultAfter, await File.ReadAllBytesAsync(_paths.VaultJson));
    }

    /// <summary>Contract 0015 A8: the rotation runs after a pending restore and before the host is built, so the
    /// guardrail store's start (contract 0010, in RunWhiskersStartupAsync) and every other reader see the new key; a host
    /// CLI next to the live instance — the guardrail reset included — never rotates.</summary>
    [Fact]
    public void The_rotation_runs_before_the_host_and_the_guardrail_start_and_never_for_a_host_cli()
    {
        var program = File.ReadAllText(Path.Combine(AutomationActorSchemeTests.SourceRoot(), "src", "Whiskers", "Program.cs"));
        var restore = program.IndexOf("RestoreBootHandler.ApplyPendingRestore(", StringComparison.Ordinal);
        var rotate = program.IndexOf("VaultKeyRotationBootHandler.Apply(", StringComparison.Ordinal);
        var build = program.IndexOf("builder.Build()", StringComparison.Ordinal);
        var startup = program.IndexOf("RunWhiskersStartupAsync()", StringComparison.Ordinal);
        Assert.True(restore >= 0 && rotate > restore && build > rotate && startup > build,
            $"restore {restore}, rotation {rotate}, build {build}, startup {startup}");

        var rotationLine = program[program.LastIndexOf('\n', rotate)..program.IndexOf('\n', rotate)];
        Assert.Contains("!hostCli", rotationLine);
        Assert.Matches(@"var hostCli = restoreCli \|\| recoveryCli \|\| bindingCli \|\| guardrailResetCli;", program);
    }

    /// <summary>The rotation runs before the Mutation Gateway exists, like S-09; its two unmarked write paths (the
    /// re-sealed vault entry, the rotation backups) have exactly their one start-up caller each.</summary>
    [Fact]
    public void The_start_up_rotation_write_paths_have_exactly_their_one_caller_each()
    {
        static IReadOnlyList<string> CallersOf(Type type, string method) => MutationCallScanner
            .Callers(typeof(Program).Assembly, m => m.DeclaringType == type && m.Name == method)
            .Select(c => c.Owner.FullName!)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Whiskers.Services.Vault.VaultKeyRotation"],
            CallersOf(typeof(VaultService), nameof(VaultService.ResealForRotationAsync)));
        Assert.Equal(["Whiskers.Services.Vault.VaultKeyRotationBootHandler"],
            CallersOf(typeof(Whiskers.Services.Backup.BackupService), nameof(Whiskers.Services.Backup.BackupService.CreateVaultRotationBackupAsync)));
    }
}
