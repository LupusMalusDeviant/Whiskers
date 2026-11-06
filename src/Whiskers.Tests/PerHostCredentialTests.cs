using System.ComponentModel;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.Certificates;
using Whiskers.Services.Docker;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Onboarding;
using Whiskers.Services.Server;
using Whiskers.Services.Setup;

namespace Whiskers.Tests;

/// <summary>
/// INV-58 / R1-44 (contract 0007 with amendments 1–10): every managed host gets its own client identity
/// <c>whiskers-client-{slug}-g{n}</c>; its proxy admits only that name after migration; a credential taken from one host
/// is refused at every other host, an older generation is refused after a rotation and the shared credential after the
/// narrowing. Every host step is guarded, confirmed through the new credential only, and resumed from what ghostunnel
/// runs with.
/// </summary>
public sealed class PerHostCredentialTests
{
    private const string SharedCn = MtlsCertificateService.SharedClientCommonName;

    // ---- isolation: the property INV-58 states ----

    [Fact]
    public async Task After_migration_a_hosts_credential_is_refused_at_every_other_host_and_the_shared_one_everywhere()
    {
        using var w = new CredentialWorld(true, ("rabenhof", "100.64.0.9"), ("zirkuswagen", "100.64.0.10"));
        Assert.True((await w.Service.MigrateClientAsync("rabenhof", null, default)).Ok);
        Assert.True((await w.Service.MigrateClientAsync("zirkuswagen", null, default)).Ok);

        var a = w.ConfigPaths("rabenhof");
        var b = w.ConfigPaths("zirkuswagen");
        Assert.Equal(MtlsProbeOutcome.Admitted, w.Host("rabenhof").Probe(w.With("rabenhof", a)));
        Assert.Equal(MtlsProbeOutcome.Admitted, w.Host("zirkuswagen").Probe(w.With("zirkuswagen", b)));
        // A's key at B and B's key at A: refused.
        Assert.Equal(MtlsProbeOutcome.Refused, w.Host("zirkuswagen").Probe(w.With("zirkuswagen", a)));
        Assert.Equal(MtlsProbeOutcome.Refused, w.Host("rabenhof").Probe(w.With("rabenhof", b)));
        // The shared credential: refused at both.
        Assert.Equal(MtlsProbeOutcome.Refused, w.Host("rabenhof").Probe(w.With("rabenhof", w.Store.SharedPaths)));
        Assert.Equal(MtlsProbeOutcome.Refused, w.Host("zirkuswagen").Probe(w.With("zirkuswagen", w.Store.SharedPaths)));

        Assert.Equal(["whiskers-client-rabenhof-g1"], w.Host("rabenhof").RunningAllow);
        Assert.Equal(["whiskers-client-zirkuswagen-g1"], w.Host("zirkuswagen").RunningAllow);
    }

    [Fact]
    public async Task A_migration_widens_switches_narrows_and_records_each_step_confirmed_through_the_new_credential()
    {
        using var w = new CredentialWorld();
        var result = await w.Service.MigrateClientAsync("rabenhof", null, default);

        Assert.True(result.Ok, result.Error);
        var host = w.Host("rabenhof");
        Assert.Equal(
            ["read", $"apply:r1:{SharedCn},whiskers-client-rabenhof-g1", "read", "confirm:r1", "read", "cleanup:r1",
             "read", "apply:r2:whiskers-client-rabenhof-g1", "read", "confirm:r2", "read", "cleanup:r2"],
            host.Log);
        Assert.True(w.Store.IsManaged(w.Server("rabenhof")));
        var journal = w.Store.ReadJournal("rabenhof")!;
        Assert.Equal(5, journal.Phase);
        Assert.True(journal.NegativeCheckPassed);
        Assert.True(journal.IsNarrowed);
        // The phase-record step removed the markers on the host.
        Assert.Empty(host.Markers);
        // Every host command travelled over a credential the host admitted: Whiskers never locked itself out.
        Assert.Empty(w.Fleet.LockedOut);
    }

    [Fact]
    public async Task Without_a_shared_credential_the_running_allow_list_is_the_proof()
    {
        using var w = new CredentialWorld(withSharedCredential: false);
        // A record on an operator-free managed slot is needed first: issue and point the record at it.
        var paths = await w.Store.IssueAsync(w.Server("rabenhof"), 1, default);
        var record = w.With("rabenhof", paths);
        await w.Servers.UpdateServerAsync(record);
        w.Host("rabenhof").RunningAllow = [SharedCn, "whiskers-client-rabenhof-g1"];
        w.Host("rabenhof").FileAllow = [SharedCn, "whiskers-client-rabenhof-g1"];

        var result = await w.Service.MigrateClientAsync("rabenhof", null, default);

        Assert.True(result.Ok, result.Error);
        var journal = w.Store.ReadJournal("rabenhof")!;
        Assert.True(journal.SharedCredentialAbsent);
        Assert.False(journal.NegativeCheckPassed);
        Assert.True(journal.IsNarrowed);
        Assert.Equal(["whiskers-client-rabenhof-g1"], w.Host("rabenhof").RunningAllow);
    }

    // ---- fail closed: nothing is confirmed without proof ----

    [Fact]
    public async Task A_shared_credential_that_gets_no_tls_alert_is_not_proven_refused_and_nothing_is_confirmed()
    {
        using var w = new CredentialWorld();
        w.Host("rabenhof").NotCheckedFor = SharedCn;

        var result = await w.Service.MigrateClientAsync("rabenhof", null, default);

        Assert.False(result.Ok);
        Assert.Equal("narrow", result.Phase);
        Assert.Contains("no TLS alert", result.Error);
        Assert.DoesNotContain(w.Host("rabenhof").Log, l => l.StartsWith("confirm:r2", StringComparison.Ordinal));
        Assert.Equal(3, w.Store.ReadJournal("rabenhof")!.Phase);
        // The guard on the host restores the widened list by itself.
        w.Host("rabenhof").FireGuard();
        Assert.Equal([SharedCn, "whiskers-client-rabenhof-g1"], w.Host("rabenhof").RunningAllow);
    }

    [Fact]
    public async Task A_guard_that_fires_between_the_checks_and_the_marker_leaves_no_phase_recorded()
    {
        using var w = new CredentialWorld();
        var host = w.Host("rabenhof");
        host.BeforeConfirm = () => { if (host.RunningAllow.Count == 1) host.FireGuard(); };

        var result = await w.Service.MigrateClientAsync("rabenhof", null, default);

        Assert.False(result.Ok);
        Assert.Equal(3, w.Store.ReadJournal("rabenhof")!.Phase);
        Assert.Empty(host.Markers);
        Assert.Equal([SharedCn, "whiskers-client-rabenhof-g1"], host.RunningAllow);
    }

    [Fact]
    public async Task A_host_that_shows_the_old_list_after_the_confirmation_is_not_recorded()
    {
        using var w = new CredentialWorld();
        var host = w.Host("rabenhof");
        host.AfterConfirm = () => { if (host.RunningAllow.Count == 1) host.RunningAllow = [SharedCn, "whiskers-client-rabenhof-g1"]; };

        var result = await w.Service.MigrateClientAsync("rabenhof", null, default);

        Assert.False(result.Ok);
        Assert.Contains("nothing was recorded", result.Error);
        Assert.Equal(3, w.Store.ReadJournal("rabenhof")!.Phase);
    }

    [Fact]
    public async Task A_confirmation_killed_inside_the_lock_writes_no_marker_and_the_guard_rolls_back()
    {
        using var w = new CredentialWorld();
        var host = w.Host("rabenhof");
        host.HangInConfirm = true;

        var result = await w.Service.MigrateClientAsync("rabenhof", null, default);

        Assert.False(result.Ok);
        Assert.Equal("widen", result.Phase);
        Assert.Empty(host.Markers);
        host.FireGuard();
        Assert.Equal([SharedCn], host.RunningAllow);
        Assert.Equal([SharedCn], host.FileAllow);
    }

    [Fact]
    public async Task A_confirmation_that_fails_after_stopping_the_timer_reports_an_unknown_state_and_no_rollback()
    {
        using var w = new CredentialWorld();
        var host = w.Host("rabenhof");
        host.ConfirmFailsAfterStop = true;

        var result = await w.Service.MigrateClientAsync("rabenhof", null, default);

        Assert.False(result.Ok);
        Assert.Equal("widen", result.Phase);
        Assert.StartsWith("State unknown", result.Error);
        Assert.Contains("the marker could not be written", result.Error);
        Assert.DoesNotContain("restores its previous allow-list on its own", result.Error);
        Assert.Single(host.Log, l => l.StartsWith("confirm:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_host_briefly_unreachable_right_after_the_confirmation_is_an_unknown_state_and_never_retried()
    {
        // Review r3 MINOR 1a: the confirmation exited 0 (guard stopped, marker written); the recheck's read is lost.
        using var w = new CredentialWorld();
        var host = w.Host("rabenhof");
        host.AfterConfirm = () => host.UnreachableNextCommands = 1;

        var result = await w.Service.MigrateClientAsync("rabenhof", null, default);

        Assert.False(result.Ok);
        Assert.Equal("widen", result.Phase);
        Assert.StartsWith("State unknown", result.Error);
        Assert.Contains("could not be checked again", result.Error);
        Assert.DoesNotContain("restores its previous allow-list on its own", result.Error);
        var confirm = Assert.Single(host.Log, l => l.StartsWith("confirm:", StringComparison.Ordinal));   // no new attempt
        Assert.Contains("unreachable", host.Log);
        // The confirmed run's marker and lock are cleaned up, and the widened list stays.
        Assert.Contains("cleanup:" + confirm["confirm:".Length..], host.Log);
        Assert.Empty(host.Markers);
        Assert.Equal([SharedCn, "whiskers-client-rabenhof-g1"], host.RunningAllow);
    }

    [Fact]
    public async Task An_unusable_host_state_right_after_the_apply_is_a_failed_attempt_and_retried()
    {
        // Review r2 C: ghostunnel half-recreated reads as unparseable once; the next attempt confirms.
        using var w = new CredentialWorld();
        var host = w.Host("rabenhof");
        host.UnusableReadsAfterApply = 1;

        var result = await w.Service.MigrateClientAsync("rabenhof", null, default);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(2, host.Log.Count(l => l == "read-unusable"));   // once after the widening, once after the narrowing
        Assert.Equal(["whiskers-client-rabenhof-g1"], host.RunningAllow);
    }

    [Fact]
    public async Task A_host_state_that_stays_unusable_after_the_apply_ends_with_the_rollback_time_not_nothing_changed()
    {
        using var w = new CredentialWorld();
        var host = w.Host("rabenhof");
        host.UnusableReadsAfterApply = 1000;

        var result = await w.Service.MigrateClientAsync("rabenhof", null, default);

        Assert.False(result.Ok);
        Assert.Equal("widen", result.Phase);
        Assert.Contains("restores its previous allow-list on its own by about", result.Error);
        Assert.Contains("the proxy's state was not usable", result.Error);
        Assert.DoesNotContain("Nothing was changed", result.Error);
        Assert.True(host.Log.Count(l => l == "read-unusable") > 1);   // retried, not given up at the first parse problem
        Assert.DoesNotContain(host.Log, l => l.StartsWith("confirm:", StringComparison.Ordinal));
        host.FireGuard();
        Assert.Equal([SharedCn], host.RunningAllow);
    }

    [Fact]
    public async Task A_resume_after_the_marker_but_before_phase_five_repeats_both_checks()
    {
        using var w = new CredentialWorld();
        Assert.True((await w.Service.MigrateClientAsync("rabenhof", null, default)).Ok);
        // As if Whiskers stopped after the narrowing was confirmed but before anything was recorded.
        var journal = w.Store.ReadJournal("rabenhof")!;
        await w.Store.WriteJournalAsync("rabenhof", journal with { Phase = 3, NegativeCheckPassed = false }, default);
        var host = w.Host("rabenhof");
        host.Log.Clear();

        // The negative check cannot be repeated: the resume must not record phase 5.
        host.NotCheckedFor = SharedCn;
        var failed = await w.Service.MigrateClientAsync("rabenhof", null, default);
        Assert.False(failed.Ok);
        Assert.False(w.Store.ReadJournal("rabenhof")!.IsNarrowed);
        Assert.DoesNotContain(host.Log, l => l.StartsWith("apply:", StringComparison.Ordinal));

        host.NotCheckedFor = null;
        var resumed = await w.Service.MigrateClientAsync("rabenhof", null, default);
        Assert.True(resumed.Ok, resumed.Error);
        Assert.True(w.Store.ReadJournal("rabenhof")!.IsNarrowed);
        Assert.DoesNotContain(host.Log, l => l.StartsWith("apply:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_record_that_does_not_name_the_new_credential_is_never_narrowed()
    {
        using var w = new CredentialWorld();
        var sharedRecord = w.Server("rabenhof").Clone();
        var calls = 0;
        w.Service.VerifyConnection = async (id, _) =>
        {
            calls++;
            // The switch is confirmed, then someone puts the shared credential back into the record.
            await w.Servers.UpdateServerAsync(sharedRecord);
            return true;
        };

        var result = await w.Service.MigrateClientAsync("rabenhof", null, default);

        Assert.False(result.Ok);
        Assert.Contains("not narrowing", result.Error);
        Assert.Equal(1, calls);
        Assert.DoesNotContain(w.Host("rabenhof").Log, l => l.StartsWith("apply:r2", StringComparison.Ordinal));
    }

    // ---- preflight ----

    [Fact]
    public async Task A_host_whose_compose_file_and_running_container_differ_is_refused_before_any_change()
    {
        using var w = new CredentialWorld();
        w.Host("rabenhof").ComposeDiffers = true;

        var result = await w.Service.MigrateClientAsync("rabenhof", null, default);

        Assert.False(result.Ok);
        Assert.Contains("differ", result.Error);
        Assert.DoesNotContain(w.Host("rabenhof").Log, l => l.StartsWith("apply:", StringComparison.Ordinal));
        Assert.Equal(0, w.Fleet.Signed);
    }

    [Fact]
    public async Task A_host_that_admits_a_foreign_name_is_refused()
    {
        using var w = new CredentialWorld();
        w.Host("rabenhof").RunningAllow = [SharedCn, "whiskers-client-zirkuswagen-g1"];
        w.Host("rabenhof").FileAllow = [SharedCn, "whiskers-client-zirkuswagen-g1"];

        var result = await w.Service.MigrateClientAsync("rabenhof", null, default);

        Assert.False(result.Ok);
        Assert.Contains("neither the shared client nor this host's own", result.Error);
    }

    [Fact]
    public async Task A_compose_file_that_admits_a_foreign_name_is_refused_before_a_resume_widens_onto_it()
    {
        // Review r2 B: the running list is fine, but the file drifted by hand to a foreign name. The widen step of a
        // resume builds on the file's list, so the preflight refuses the host before anything is applied.
        using var w = new CredentialWorld();
        Assert.True((await w.Service.MigrateClientAsync("rabenhof", null, default)).Ok);
        var host = w.Host("rabenhof");
        host.RunningAllow = [SharedCn, "whiskers-client-rabenhof-g1"];
        host.FileAllow = [SharedCn, "whiskers-client-rabenhof-g1", "whiskers-client-zirkuswagen-g1"];
        await w.Store.WriteJournalAsync("rabenhof", w.Store.ReadJournal("rabenhof")! with { Phase = 2, NegativeCheckPassed = false }, default);
        host.Log.Clear();

        var resumed = await w.Service.MigrateClientAsync("rabenhof", null, default);

        Assert.False(resumed.Ok);
        Assert.Contains("admits 'whiskers-client-zirkuswagen-g1'", resumed.Error);
        Assert.DoesNotContain(host.Log, l => l.StartsWith("apply:", StringComparison.Ordinal));
        Assert.DoesNotContain("whiskers-client-zirkuswagen-g1", host.RunningAllow);
    }

    [Fact]
    public async Task Another_runs_active_guard_refuses_the_step()
    {
        using var w = new CredentialWorld();
        w.Host("rabenhof").ForeignUnits.Add("whiskers-allow-guard-20261106x.timer");

        var result = await w.Service.MigrateClientAsync("rabenhof", null, default);

        Assert.False(result.Ok);
        Assert.Contains("guard timer is armed", result.Error);
        Assert.DoesNotContain(w.Host("rabenhof").Log, l => l.StartsWith("apply:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_operator_supplied_credential_is_never_touched()
    {
        using var w = new CredentialWorld();
        var own = Path.Combine(w.Root, "operator");
        Directory.CreateDirectory(own);
        File.Copy(Path.Combine(w.MtlsDir, "client.crt"), Path.Combine(own, "client.crt"));
        File.Copy(Path.Combine(w.MtlsDir, "client.key"), Path.Combine(own, "client.key"));
        var record = w.Server("rabenhof").Clone();
        record.TcpClientCertPath = Path.Combine(own, "client.crt");
        record.TcpClientKeyPath = Path.Combine(own, "client.key");
        await w.Servers.UpdateServerAsync(record);
        var before = File.ReadAllBytes(record.TcpClientKeyPath);

        var result = await w.Service.MigrateClientAsync("rabenhof", null, default);

        Assert.False(result.Ok);
        Assert.Contains("operator-supplied", result.Error);
        Assert.Equal(before, File.ReadAllBytes(record.TcpClientKeyPath));
        Assert.Equal(MtlsClientCredentialKind.OperatorSupplied, w.Store.StatusOf(w.Server("rabenhof")).Kind);
        Assert.Empty(w.Fleet.Commands);
    }

    // ---- lock shared with the server-certificate renewal ----

    [Fact]
    public async Task The_rollout_and_the_server_certificate_renewal_share_one_lock_per_host()
    {
        using var w = new CredentialWorld();
        Assert.True((await w.Service.MigrateClientAsync("rabenhof", null, default)).Ok);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var host = w.Host("rabenhof");
        host.BeforeConfirm = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(30)); };

        // A rotation holds the host's lock (blocked inside its confirmation) — every other step reaches the lock and
        // is refused there, server removal included (review r1 MINOR 7, 8).
        var rotation = Task.Run(() => w.Service.RenewClientAsync("rabenhof", rotate: true, default));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));

        var renewal = await w.Service.RenewAsync("rabenhof", default);
        var clientRenewal = await w.Service.RenewClientAsync("rabenhof", rotate: false, default);
        var second = await w.Service.MigrateClientAsync("rabenhof", null, default);
        var removal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Whiskers.Services.ServerConfig.ServerRemoval.RemoveAsync(w.Servers,
                ComponentTests.ServersKubeconfigSaveTests.NullProxy<Whiskers.Services.Vault.IVaultService>.Create(), w.Server("rabenhof"), w.Store));
        release.Set();

        Assert.False(renewal.Success);
        Assert.Contains("already running", renewal.Message);
        Assert.False(clientRenewal.Ok);
        Assert.Contains("already running", clientRenewal.Error);
        Assert.False(second.Ok);
        Assert.Contains("already running", second.Error);
        Assert.Contains("is running", removal.Message);
        Assert.NotNull(w.Servers.GetServer("rabenhof"));
        Assert.True(Directory.Exists(Path.Combine(w.MtlsDir, "hosts", "rabenhof")));
        Assert.True((await rotation).Ok);
        Assert.Empty(w.Fleet.LockedOut);
    }

    // ---- review r1: resume from the file, pending until verified, unknown confirmations, cancellation ----

    [Fact]
    public async Task A_running_list_narrowed_while_the_file_still_admits_the_shared_name_is_applied_again_before_phase_five()
    {
        using var w = new CredentialWorld();
        Assert.True((await w.Service.MigrateClientAsync("rabenhof", null, default)).Ok);
        // The guard restored the file but both of its recreates failed: the container runs [cn], the file says [shared, cn].
        var host = w.Host("rabenhof");
        host.FileAllow = [SharedCn, "whiskers-client-rabenhof-g1"];
        var journal = w.Store.ReadJournal("rabenhof")!;
        await w.Store.WriteJournalAsync("rabenhof", journal with { Phase = 3, NegativeCheckPassed = false }, default);
        host.Log.Clear();

        var resumed = await w.Service.MigrateClientAsync("rabenhof", null, default);

        Assert.True(resumed.Ok, resumed.Error);
        var applies = host.Log.Where(l => l.StartsWith("apply:", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, applies.Count);
        Assert.EndsWith(":" + SharedCn + ",whiskers-client-rabenhof-g1", applies[0]);
        Assert.EndsWith(":whiskers-client-rabenhof-g1", applies[1]);
        Assert.Equal(["whiskers-client-rabenhof-g1"], host.FileAllow);
        Assert.True(w.Store.ReadJournal("rabenhof")!.IsNarrowed);
    }

    [Fact]
    public async Task A_file_that_still_admits_the_shared_name_is_never_recorded_without_a_confirmed_narrowing()
    {
        using var w = new CredentialWorld();
        Assert.True((await w.Service.MigrateClientAsync("rabenhof", null, default)).Ok);
        var host = w.Host("rabenhof");
        host.FileAllow = [SharedCn, "whiskers-client-rabenhof-g1"];
        await w.Store.WriteJournalAsync("rabenhof", w.Store.ReadJournal("rabenhof")! with { Phase = 3, NegativeCheckPassed = false }, default);
        host.NotCheckedFor = SharedCn;   // the negative check of the new narrowing cannot be made

        var resumed = await w.Service.MigrateClientAsync("rabenhof", null, default);

        Assert.False(resumed.Ok);
        Assert.False(w.Store.ReadJournal("rabenhof")!.IsNarrowed);
    }

    [Fact]
    public async Task An_issued_slot_stays_pending_until_the_switch_is_verified_and_leftover_slots_go_after_phase_five()
    {
        using var w = new CredentialWorld();
        var issued = await w.Store.IssueAsync(w.Server("rabenhof"), 1, default);
        Assert.Equal(issued, w.Store.PendingFor("rabenhof"));
        Assert.Null(w.Store.PreviousFor("rabenhof"));
        Assert.Equal("whiskers-client-rabenhof-g1", w.Store.IdentityOf("rabenhof")!.CommonName);

        // A first run whose switch cannot be verified: the record goes back, the slot stays pending.
        w.Service.VerifyConnection = (_, _) => Task.FromResult(false);
        Assert.False((await w.Service.MigrateClientAsync("rabenhof", null, default)).Ok);
        Assert.True(w.Store.IsShared(w.Server("rabenhof")));
        Assert.NotNull(w.Store.PendingFor("rabenhof"));
        // A stray slot of an interrupted run.
        var stray = Path.Combine(w.MtlsDir, "hosts", "rabenhof", "g1-s9");
        Directory.CreateDirectory(stray);
        File.WriteAllText(Path.Combine(stray, "client.key"), "x");

        w.Service.VerifyConnection = (id, _) => Task.FromResult(w.Host(id).Probe(w.Server(id)) == MtlsProbeOutcome.Admitted);
        Assert.True((await w.Service.MigrateClientAsync("rabenhof", null, default)).Ok);

        Assert.Null(w.Store.PendingFor("rabenhof"));
        var slot = Assert.Single(Directory.GetDirectories(Path.Combine(w.MtlsDir, "hosts", "rabenhof")));
        Assert.True(MtlsClientCredentialStore.SamePath(w.Server("rabenhof").TcpClientCertPath, Path.Combine(slot, "client.crt")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_confirmation_whose_outcome_is_unknown_promises_no_rollback(bool throws)
    {
        using var w = new CredentialWorld();
        var confirm = w.Service.RunConfirmation;
        w.Service.RunConfirmation = async (server, command, ct) =>
        {
            await confirm(server, command, ct);   // the script ran on the host …
            if (throws) throw new IOException("connection reset after the script ran");
            return ("", "", 124);                 // … or was stopped by its time limit
        };

        var result = await w.Service.MigrateClientAsync("rabenhof", null, default);

        Assert.False(result.Ok);
        Assert.StartsWith("State unknown", result.Error);
        Assert.DoesNotContain("restores its previous allow-list on its own", result.Error);
        Assert.Equal(1, w.Store.ReadJournal("rabenhof")!.Phase);
    }

    [Fact]
    public async Task A_caller_cancelled_after_the_record_save_still_verifies_and_activates_the_switch()
    {
        using var w = new CredentialWorld();
        using var cts = new CancellationTokenSource();
        var tokens = new List<CancellationToken>();
        var verify = w.Service.VerifyConnection;
        w.Service.VerifyConnection = (id, ct) => { tokens.Add(ct); return verify(id, ct); };
        w.Servers.BeforeSave = s => { if (w.Store.IsManaged(s)) cts.Cancel(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w.Service.MigrateClientAsync("rabenhof", null, cts.Token));

        Assert.NotEmpty(tokens);
        Assert.All(tokens, t => Assert.False(t.CanBeCanceled));
        Assert.True(w.Store.IsManaged(w.Server("rabenhof")));
        Assert.Null(w.Store.PendingFor("rabenhof"));   // verified, then made active
        Assert.Equal(3, w.Store.ReadJournal("rabenhof")!.Phase);
    }

    // ---- renewal and rotation: slots, never overwriting the live credential ----

    [Fact]
    public async Task A_renewal_writes_a_new_slot_switches_the_record_and_leaves_the_host_unchanged()
    {
        using var w = new CredentialWorld();
        Assert.True((await w.Service.MigrateClientAsync("rabenhof", null, default)).Ok);
        var before = w.ConfigPaths("rabenhof");
        var beforeKey = File.ReadAllBytes(before.Key);
        w.Host("rabenhof").Log.Clear();

        var result = await w.Service.RenewClientAsync("rabenhof", rotate: false, default);

        Assert.True(result.Ok, result.Error);
        var after = w.ConfigPaths("rabenhof");
        Assert.NotEqual(before.Cert, after.Cert);
        Assert.Equal("whiskers-client-rabenhof-g1", MtlsCertificateService.CommonNameOf(after.Cert));
        Assert.NotEqual(beforeKey, File.ReadAllBytes(after.Key));
        Assert.DoesNotContain(w.Host("rabenhof").Log, l => l.StartsWith("apply:", StringComparison.Ordinal));
        Assert.False(File.Exists(before.Key));   // the old slot goes once the new connection is confirmed
        Assert.Equal(MtlsProbeOutcome.Admitted, w.Host("rabenhof").Probe(w.Server("rabenhof")));
    }

    [Fact]
    public async Task A_renewal_whose_connection_cannot_be_confirmed_keeps_the_live_credential_untouched()
    {
        using var w = new CredentialWorld();
        Assert.True((await w.Service.MigrateClientAsync("rabenhof", null, default)).Ok);
        var before = w.ConfigPaths("rabenhof");
        var beforeCert = File.ReadAllText(before.Cert);
        w.Service.VerifyConnection = (_, _) => Task.FromResult(false);

        var result = await w.Service.RenewClientAsync("rabenhof", rotate: false, default);

        Assert.False(result.Ok);
        Assert.Equal(before.Cert, w.Server("rabenhof").TcpClientCertPath);
        Assert.Equal(beforeCert, File.ReadAllText(before.Cert));
        Assert.Null(w.Store.PendingFor("rabenhof"));
    }

    [Fact]
    public async Task A_rotation_moves_to_the_next_generation_and_the_old_one_is_refused_at_the_host()
    {
        using var w = new CredentialWorld();
        Assert.True((await w.Service.MigrateClientAsync("rabenhof", null, default)).Ok);
        var g1 = w.ConfigPaths("rabenhof");
        var g1Cert = File.ReadAllText(g1.Cert);
        var g1Record = w.With("rabenhof", g1);
        var g1Copy = Path.Combine(w.Root, "g1");
        Directory.CreateDirectory(g1Copy);
        File.Copy(g1.Cert, Path.Combine(g1Copy, "client.crt"));
        File.Copy(g1.Key, Path.Combine(g1Copy, "client.key"));

        var result = await w.Service.RenewClientAsync("rabenhof", rotate: true, default);

        Assert.True(result.Ok, result.Error);
        var g2 = w.ConfigPaths("rabenhof");
        Assert.Equal("whiskers-client-rabenhof-g2", MtlsCertificateService.CommonNameOf(g2.Cert));
        Assert.Equal(["whiskers-client-rabenhof-g2"], w.Host("rabenhof").RunningAllow);
        // The g1 credential (kept aside, as an attacker would) is refused; the slot itself is gone.
        g1Record.TcpClientCertPath = Path.Combine(g1Copy, "client.crt");
        g1Record.TcpClientKeyPath = Path.Combine(g1Copy, "client.key");
        Assert.Equal(MtlsProbeOutcome.Refused, w.Host("rabenhof").Probe(g1Record));
        Assert.False(File.Exists(g1.Key));
        Assert.NotEqual(g1Cert, File.ReadAllText(g2.Cert));
        var journal = w.Store.ReadJournal("rabenhof")!;
        Assert.Equal((MtlsRolloutJournal.Rotate, 2, 5, true), (journal.Kind, journal.Generation, journal.Phase, journal.NegativeCheckPassed));
        Assert.Empty(w.Fleet.LockedOut);
    }

    [Fact]
    public async Task A_crash_between_the_record_save_and_the_store_update_resumes_from_the_record()
    {
        using var w = new CredentialWorld();
        Assert.True((await w.Service.MigrateClientAsync("rabenhof", null, default)).Ok);
        var verify = w.Service.VerifyConnection;
        var crashed = false;
        w.Service.VerifyConnection = (id, ct) =>
        {
            if (!crashed) { crashed = true; throw new OperationCanceledException("crash after the record was saved"); }
            return verify(id, ct);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w.Service.RenewClientAsync("rabenhof", rotate: true, default));
        // The record names the g2 slot, the store still has it pending: nothing references the g1 slot but the store.
        Assert.Equal("whiskers-client-rabenhof-g2", MtlsCertificateService.CommonNameOf(w.Server("rabenhof").TcpClientCertPath!));
        Assert.Equal(1, w.Store.IdentityOf("rabenhof")!.Generation);

        var resumed = await w.Service.RenewClientAsync("rabenhof", rotate: true, default);

        Assert.True(resumed.Ok, resumed.Error);
        Assert.Equal(2, w.Store.IdentityOf("rabenhof")!.Generation);
        Assert.Equal(["whiskers-client-rabenhof-g2"], w.Host("rabenhof").RunningAllow);
        Assert.Equal(2, w.Store.StatusOf(w.Server("rabenhof")).Generation);
    }

    [Fact]
    public async Task A_slot_a_server_record_references_is_never_deleted()
    {
        using var w = new CredentialWorld(true, ("rabenhof", "100.64.0.9"), ("zirkuswagen", "100.64.0.10"));
        Assert.True((await w.Service.MigrateClientAsync("rabenhof", null, default)).Ok);
        var g1 = w.ConfigPaths("rabenhof");
        // Another record (a hand edit) points at the rabenhof slot.
        var other = w.Server("zirkuswagen").Clone();
        other.TcpClientCertPath = g1.Cert;
        other.TcpClientKeyPath = g1.Key;
        await w.Servers.UpdateServerAsync(other);

        Assert.True((await w.Service.RenewClientAsync("rabenhof", rotate: false, default)).Ok);
        await w.Store.DiscardPreviousAsync("rabenhof", default);

        Assert.True(File.Exists(g1.Key));
    }

    // ---- retire (UI only) ----

    [Fact]
    public async Task Retiring_is_refused_while_a_host_still_uses_the_shared_credential()
    {
        using var w = new CredentialWorld(true, ("rabenhof", "100.64.0.9"), ("zirkuswagen", "100.64.0.10"));
        Assert.True((await w.Service.MigrateClientAsync("rabenhof", null, default)).Ok);

        var result = await w.Service.RetireSharedClientAsync(default);

        Assert.False(result.Success);
        Assert.Contains("Zirkuswagen (still presents the shared certificate)", result.Message);
        Assert.True(File.Exists(w.Store.SharedPaths.Key));
    }

    [Fact]
    public async Task Retiring_reads_the_live_allow_list_and_ignores_operator_supplied_servers()
    {
        using var w = new CredentialWorld(true, ("rabenhof", "100.64.0.9"), ("zirkuswagen", "100.64.0.10"));
        Assert.True((await w.Service.MigrateClientAsync("rabenhof", null, default)).Ok);
        var foreign = w.Server("zirkuswagen").Clone();
        foreign.TcpClientCertPath = Path.Combine(w.Root, "elsewhere.crt");
        foreign.TcpClientKeyPath = Path.Combine(w.Root, "elsewhere.key");
        await w.Servers.UpdateServerAsync(foreign);

        // The journal says narrowed, the running proxy disagrees: blocked.
        w.Host("rabenhof").RunningAllow = [SharedCn, "whiskers-client-rabenhof-g1"];
        var blocked = await w.Service.RetireSharedClientAsync(default);
        Assert.False(blocked.Success);
        Assert.Contains("its proxy admits", blocked.Message);
        Assert.Contains(w.Host("rabenhof").Log, l => l == "args");

        w.Host("rabenhof").RunningAllow = ["whiskers-client-rabenhof-g1"];
        var retired = await w.Service.RetireSharedClientAsync(default);
        Assert.True(retired.Success, retired.Message);
        Assert.Contains("Zirkuswagen", retired.Message);
        Assert.False(File.Exists(w.Store.SharedPaths.Key));
        Assert.False(File.Exists(w.Store.SharedPaths.Cert));
        Assert.True(File.Exists(w.Store.CaPath));
    }

    [Fact]
    public async Task Retiring_is_refused_when_a_hosts_running_allow_list_cannot_be_read()
    {
        using var w = new CredentialWorld();
        Assert.True((await w.Service.MigrateClientAsync("rabenhof", null, default)).Ok);
        w.Host("rabenhof").Unreachable = true;

        var result = await w.Service.RetireSharedClientAsync(default);

        Assert.False(result.Success);
        Assert.Contains("could not be read", result.Message);
        Assert.True(File.Exists(w.Store.SharedPaths.Key));
    }

    // ---- onboarding: the new host never admits the shared name ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Onboarding_writes_the_issued_paths_the_per_host_allow_cn_and_a_phase_five_record(bool sharedExists)
    {
        using var w = new CredentialWorld(sharedExists, ("neuhof", "100.64.0.20"));
        var record = w.Server("neuhof").Clone();
        record.ConnectionType = ConnectionType.SSH;
        record.TcpUseTls = false;
        record.TcpClientCertPath = record.TcpClientKeyPath = record.TcpCaCertPath = null;
        await w.Servers.UpdateServerAsync(record);
        var exec = new OnboardingExecutor(w.Fleet, "100.64.0.20");
        var onboarding = new OnboardingService(exec, w.Servers, ReachableDocker.Create(),
            ComponentTests.ServersKubeconfigSaveTests.NullProxy<IMtlsCertificateService>.Create(), w.Store,
            NullLogger<OnboardingService>.Instance)
        {
            ProbeCredential = (server, _) => Task.FromResult(MtlsCertificateService.CommonNameOf(server.TcpClientCertPath!) == SharedCn
                ? MtlsProbeOutcome.Refused
                : MtlsProbeOutcome.NotChecked),
        };

        var result = await onboarding.OnboardServerAsync("neuhof", new Progress<string>(), default);

        Assert.True(result.Success, result.Error);
        var saved = w.Server("neuhof");
        // Exactly the slot the one issue wrote (no second proposal), in the store's managed layout.
        var slots = Directory.GetDirectories(Path.Combine(w.MtlsDir, "hosts", "neuhof"));
        var slot = Assert.Single(slots);
        Assert.True(MtlsClientCredentialStore.SamePath(saved.TcpClientCertPath, Path.Combine(slot, "client.crt")), saved.TcpClientCertPath);
        Assert.True(MtlsClientCredentialStore.SamePath(saved.TcpClientKeyPath, Path.Combine(slot, "client.key")), saved.TcpClientKeyPath);
        Assert.Equal(w.Store.CaPath, saved.TcpCaCertPath);
        Assert.True(w.Store.IsManaged(saved));
        var compose = OnboardingCommands.DockerProxyCompose("100.64.0.20", "whiskers-client-neuhof-g1");
        Assert.Contains(exec.Scripts, s => s.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(compose)), StringComparison.Ordinal));
        Assert.DoesNotContain(exec.Scripts, s => s.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("--allow-cn=" + SharedCn)), StringComparison.Ordinal));

        Assert.Null(w.Store.PendingFor("neuhof"));   // verified over mTLS, then made active
        var journal = w.Store.ReadJournal("neuhof")!;
        Assert.Equal((MtlsRolloutJournal.Onboard, 5, true), (journal.Kind, journal.Phase, journal.IsNarrowed));
        Assert.Equal(sharedExists, journal.NegativeCheckPassed);
        Assert.Equal(!sharedExists, journal.SharedCredentialAbsent);
    }

    [Fact]
    public async Task While_onboarding_holds_the_host_lock_removal_and_migration_are_refused()
    {
        // Review r3 MAJOR 1: the break-glass re-onboarding of an existing mTLS host holds the store's per-host lock from
        // the server certificate to the journal; the probe in step 9 runs inside it.
        using var w = new CredentialWorld();
        var exec = new OnboardingExecutor(w.Fleet, "100.64.0.9");
        Exception? removal = null;
        MtlsClientMigrationResult? migration = null;
        MtlsClientMigrationResult? renewal = null;
        var onboarding = new OnboardingService(exec, w.Servers, ReachableDocker.Create(),
            ComponentTests.ServersKubeconfigSaveTests.NullProxy<IMtlsCertificateService>.Create(), w.Store,
            NullLogger<OnboardingService>.Instance)
        {
            ProbeCredential = async (server, _) =>
            {
                Assert.Equal(0, w.Store.HostLock("rabenhof").CurrentCount);
                migration = await w.Service.MigrateClientAsync("rabenhof", null, default);
                renewal = await w.Service.RenewClientAsync("rabenhof", rotate: true, default);
                removal = await Record.ExceptionAsync(() => Whiskers.Services.ServerConfig.ServerRemoval.RemoveAsync(w.Servers,
                    ComponentTests.ServersKubeconfigSaveTests.NullProxy<Whiskers.Services.Vault.IVaultService>.Create(), w.Server("rabenhof"), w.Store));
                return MtlsProbeOutcome.Refused;
            },
        };

        var result = await onboarding.OnboardServerAsync("rabenhof", new Progress<string>(), default);

        Assert.True(result.Success, result.Error);
        Assert.False(migration!.Ok);
        Assert.Contains("already running", migration.Error);
        Assert.False(renewal!.Ok);
        Assert.Contains("already running", renewal.Error);
        Assert.IsType<InvalidOperationException>(removal);
        Assert.Contains("is running", removal!.Message);
        Assert.NotNull(w.Servers.GetServer("rabenhof"));
        Assert.Empty(w.Host("rabenhof").Log);   // the refused steps never reached the host's proxy
        Assert.True(Directory.Exists(Path.Combine(w.MtlsDir, "hosts", "rabenhof")));   // removal deleted nothing
        Assert.Equal(1, w.Store.HostLock("rabenhof").CurrentCount);   // released after the run
    }

    [Fact]
    public async Task Onboarding_is_refused_while_another_certificate_step_holds_the_host_lock()
    {
        using var w = new CredentialWorld(true, ("neuhof", "100.64.0.20"));
        var record = w.Server("neuhof").Clone();
        record.ConnectionType = ConnectionType.SSH;
        record.TcpUseTls = false;
        record.TcpClientCertPath = record.TcpClientKeyPath = record.TcpCaCertPath = null;
        await w.Servers.UpdateServerAsync(record);
        var onboarding = new OnboardingService(new OnboardingExecutor(w.Fleet, "100.64.0.20"), w.Servers, ReachableDocker.Create(),
            ComponentTests.ServersKubeconfigSaveTests.NullProxy<IMtlsCertificateService>.Create(), w.Store,
            NullLogger<OnboardingService>.Instance);

        var gate = w.Store.HostLock("neuhof");
        Assert.True(await gate.WaitAsync(0));   // e.g. a removal or a migration of the same id
        OnboardingResult result;
        try { result = await onboarding.OnboardServerAsync("neuhof", new Progress<string>(), default); }
        finally { gate.Release(); }

        Assert.False(result.Success);
        Assert.Equal(OnboardingStep.Certificate, result.FailedStep);
        Assert.Contains("Zertifikatsschritt", result.Error);
        Assert.False(Directory.Exists(Path.Combine(w.MtlsDir, "hosts", "neuhof")));   // no key issued
        Assert.Equal(ConnectionType.SSH, w.Server("neuhof").ConnectionType);
    }

    private static async Task<ServerConfig> SshRecordAsync(CredentialWorld w, string id)
    {
        var record = w.Server(id).Clone();
        record.ConnectionType = ConnectionType.SSH;
        record.TcpUseTls = false;
        record.TcpClientCertPath = record.TcpClientKeyPath = record.TcpCaCertPath = null;
        await w.Servers.UpdateServerAsync(record);
        return record;
    }

    [Fact]
    public async Task Onboarding_with_the_real_certificate_service_issues_the_server_certificate_under_the_host_lock()
    {
        // Review r4 NIT 2: the lock check of IssueForNewHostAsync together with OnboardingService.
        using var w = new CredentialWorld(true, ("neuhof", "100.64.0.20"));
        await SshRecordAsync(w, "neuhof");
        var exec = new OnboardingExecutor(w.Fleet, "100.64.0.20", w.Ca);
        var certificates = new MtlsCertificateService(exec, w.Servers, NullLogger<MtlsCertificateService>.Instance, w.Store);
        var onboarding = new OnboardingService(exec, w.Servers, ReachableDocker.Create(), certificates, w.Store,
            NullLogger<OnboardingService>.Instance)
        {
            ProbeCredential = (server, _) => Task.FromResult(MtlsProbeOutcome.Refused),
        };

        var result = await onboarding.OnboardServerAsync("neuhof", new Progress<string>(), default);

        Assert.True(result.Success, result.Error);
        Assert.True(exec.ServerCertificateActivated);
        Assert.Equal(1, w.Store.HostLock("neuhof").CurrentCount);
    }

    [Fact]
    public async Task Onboarding_continues_with_the_record_as_it_is_when_it_takes_the_host_lock()
    {
        // Review r4 MINOR 2: a change finished during steps 0–4 (before the lock) is not overwritten at the switchover.
        using var w = new CredentialWorld(true, ("neuhof", "100.64.0.20"));
        await SshRecordAsync(w, "neuhof");
        var exec = new OnboardingExecutor(w.Fleet, "100.64.0.20");
        var changed = false;
        exec.OnCommand = command =>
        {
            if (changed || !command.Contains("BackendState", StringComparison.Ordinal)) return;
            changed = true;
            var edited = w.Server("neuhof").Clone();
            edited.Group = "Produktion";
            w.Servers.UpdateServerAsync(edited).GetAwaiter().GetResult();
        };
        var onboarding = new OnboardingService(exec, w.Servers, ReachableDocker.Create(),
            ComponentTests.ServersKubeconfigSaveTests.NullProxy<IMtlsCertificateService>.Create(), w.Store,
            NullLogger<OnboardingService>.Instance)
        {
            ProbeCredential = (server, _) => Task.FromResult(MtlsProbeOutcome.Refused),
        };

        var result = await onboarding.OnboardServerAsync("neuhof", new Progress<string>(), default);

        Assert.True(result.Success, result.Error);
        Assert.True(changed);
        var saved = w.Server("neuhof");
        Assert.Equal("Produktion", saved.Group);
        Assert.True(saved.TcpUseTls);
        Assert.Equal(ConnectionType.TCP, saved.ConnectionType);
    }

    [Fact]
    public async Task Retiring_is_blocked_while_a_migration_holds_a_hosts_lock()
    {
        // Review r4 MINOR 1 (contract 0007 amendment 10): retirement takes the per-host lock of every managed host.
        using var w = new CredentialWorld();
        Assert.True((await w.Service.MigrateClientAsync("rabenhof", null, default)).Ok);
        // A resumed migration (the file still admits the shared name) blocks inside its confirmation, holding the lock.
        var host = w.Host("rabenhof");
        host.FileAllow = [SharedCn, "whiskers-client-rabenhof-g1"];
        await w.Store.WriteJournalAsync("rabenhof", w.Store.ReadJournal("rabenhof")! with { Phase = 3, NegativeCheckPassed = false }, default);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        host.BeforeConfirm = () => { if (!entered.IsSet) { entered.Set(); release.Wait(TimeSpan.FromSeconds(30)); } };
        var migration = Task.Run(() => w.Service.MigrateClientAsync("rabenhof", null, default));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));

        var retired = await w.Service.RetireSharedClientAsync(default);
        release.Set();

        Assert.False(retired.Success);
        Assert.Contains("Rabenhof (a certificate step is running)", retired.Message);
        Assert.True(File.Exists(w.Store.SharedPaths.Key));
        Assert.True((await migration).Ok);
        // Once the migration is done, the retirement goes through and leaves no lock behind.
        Assert.True((await w.Service.RetireSharedClientAsync(default)).Success);
        Assert.Equal(1, w.Store.HostLock("rabenhof").CurrentCount);
    }

    [Fact]
    public async Task The_server_certificate_of_a_new_host_is_issued_only_under_the_host_lock()
    {
        using var w = new CredentialWorld();
        var identity = MtlsIdentity.ForNewHost("rabenhof", "100.64.0.9");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => w.Service.IssueForNewHostAsync("rabenhof", identity, default));

        Assert.Contains("only under the host's certificate lock", refused.Message);
        Assert.Empty(w.Fleet.Commands);
    }

    // ---- the store ----

    [Fact]
    public async Task Issue_writes_exactly_the_proposed_slot_and_a_proposal_creates_nothing()
    {
        using var w = new CredentialWorld();
        var proposed = w.Store.ProposeNewSlot("rabenhof", 1);
        Assert.False(Directory.Exists(Path.GetDirectoryName(proposed.Cert)));

        var issued = await w.Store.IssueAsync(w.Server("rabenhof"), 1, default);

        Assert.Equal(proposed, issued);
        Assert.Equal(w.Store.CaPath, issued.Ca);
        Assert.NotEqual(issued.Cert, w.Store.ProposeNewSlot("rabenhof", 1).Cert);
        Assert.EndsWith(Path.Combine("hosts", "rabenhof", "g1-s1", "client.crt"), issued.Cert);
    }

    [SkippableFact]
    public async Task Keys_are_0600_and_host_and_slot_directories_0700_owned_by_whiskers()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix file modes");
        using var w = new CredentialWorld();
        var paths = await w.Store.IssueAsync(w.Server("rabenhof"), 1, default);
        var slotDir = Path.GetDirectoryName(paths.Key)!;
        var hostDir = Path.GetDirectoryName(slotDir)!;

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(paths.Key));
        var dirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Assert.Equal(dirMode, File.GetUnixFileMode(slotDir));
        Assert.Equal(dirMode, File.GetUnixFileMode(hostDir));
        Assert.Equal(dirMode, File.GetUnixFileMode(Path.Combine(w.MtlsDir, "hosts")));
        var (_, uid, _) = DeploymentScripts.Run("id", ["-u"]);
        var (_, owner, _) = DeploymentScripts.Run("stat", ["-c", "%u", paths.Key]);
        Assert.Equal(uid.Trim(), owner.Trim());
        Assert.False(ProductionReadinessService.KeyIsExposed(paths.Key));
        File.SetUnixFileMode(paths.Key, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        Assert.True(ProductionReadinessService.KeyIsExposed(paths.Key));
    }

    [SkippableFact]
    public void On_windows_a_key_readable_by_everyone_is_exposed()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows ACLs");
        using var w = new CredentialWorld();
        var key = Path.Combine(w.Root, "probe.key");
        File.WriteAllText(key, "x");
        Assert.False(ProductionReadinessService.KeyIsExposed(key));
#pragma warning disable CA1416
        var info = new FileInfo(key);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null),
            System.Security.AccessControl.FileSystemRights.Read, System.Security.AccessControl.AccessControlType.Allow));
        info.SetAccessControl(acl);
#pragma warning restore CA1416
        Assert.True(ProductionReadinessService.KeyIsExposed(key));
    }

    [Fact]
    public async Task No_key_material_reaches_the_log()
    {
        using var w = new CredentialWorld();
        var log = new CapturingLogger<MtlsClientCredentialStore>();
        var store = new MtlsClientCredentialStore(w.Fleet, log, w.Paths, w.Servers);
        var paths = await store.IssueAsync(w.Server("rabenhof"), 1, default);
        var key = File.ReadAllText(paths.Key);
        var body = string.Concat(key.Split('\n').Where(l => !l.StartsWith("-----", StringComparison.Ordinal))).Trim();

        Assert.NotEmpty(log.Lines);
        Assert.All(log.Lines, l =>
        {
            Assert.DoesNotContain("PRIVATE KEY", l);
            Assert.DoesNotContain(body[..16], l);
        });
        // Only the CSR travels to step-ca, never the key.
        Assert.DoesNotContain(w.Fleet.Commands, c => c.Script.Contains(body[..16], StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_servers_never_share_a_name_and_an_overlong_id_gets_the_hash_rule()
    {
        using var w = new CredentialWorld(true, ("rabe-hof", "100.64.0.9"), ("rabe_hof", "100.64.0.10"));
        await w.Store.IssueAsync(w.Server("rabe-hof"), 1, default);
        await w.Store.IssueAsync(w.Server("rabe_hof"), 1, default);

        var a = w.Store.IdentityOf("rabe-hof")!.CommonName;
        var b = w.Store.IdentityOf("rabe_hof")!.CommonName;
        Assert.Equal("whiskers-client-rabe-hof-g1", a);
        Assert.NotEqual(a, b);
        Assert.Matches("^whiskers-client-rabe-hof-[0-9a-f]{6}-g1$", b);

        var longId = new string('x', 80);
        var slug = MtlsClientIdentity.SlugFor(longId, _ => false);
        Assert.True(MtlsClientIdentity.IsValidCommonName(new MtlsClientIdentity(longId, slug, 9999).CommonName));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData(".hidden")]
    public void A_server_id_that_cannot_be_a_directory_name_is_refused(string id)
    {
        Assert.False(MtlsClientCredentialStore.IsValidServerId(id));
    }

    // ---- validation of the server certificate after each kind of switch ----

    [Fact]
    public async Task After_migration_renewal_rotation_and_onboarding_the_proxy_certificate_is_still_checked()
    {
        using var w = new CredentialWorld();
        Assert.True((await w.Service.MigrateClientAsync("rabenhof", null, default)).Ok);
        AssertServerCheck(w, "rabenhof", "100.64.0.9");
        Assert.True((await w.Service.RenewClientAsync("rabenhof", rotate: false, default)).Ok);
        AssertServerCheck(w, "rabenhof", "100.64.0.9");
        Assert.True((await w.Service.RenewClientAsync("rabenhof", rotate: true, default)).Ok);
        AssertServerCheck(w, "rabenhof", "100.64.0.9");
    }

    private static void AssertServerCheck(CredentialWorld w, string id, string ip)
    {
        var record = w.Server(id);
        var credentials = DockerConnectionManager.BuildMtlsCredentials(record);
        var callback = credentials.ServerCertificateValidationCallback!;
        using var right = w.Ca.IssueServer(ip);
        using var wrongIp = w.Ca.IssueServer("100.64.0.99");
        using var foreignCa = new TestCa();
        using var foreign = foreignCa.IssueServer(ip);
        using var chain = new X509Chain();
        chain.ChainPolicy.ExtraStore.Add(w.Ca.Intermediate);

        Assert.True(callback(record, right, chain, System.Net.Security.SslPolicyErrors.None));
        Assert.False(callback(record, wrongIp, chain, System.Net.Security.SslPolicyErrors.None));
        Assert.False(callback(record, foreign, null, System.Net.Security.SslPolicyErrors.None));

        var anchors = new X509Certificate2Collection();
        anchors.ImportFromPemFile(record.TcpCaCertPath!);
        Assert.True(MtlsClientProbe.ValidateServer(right, chain, anchors, record.TcpHost));
        Assert.False(MtlsClientProbe.ValidateServer(wrongIp, chain, anchors, record.TcpHost));
        Assert.False(MtlsClientProbe.ValidateServer(foreign, null, anchors, record.TcpHost));
    }

    // ---- what "refused" means ----

    [Fact]
    public void Only_a_tls_alert_counts_as_refused()
    {
        // OpenSSL: the alert reason in the inner error, during the handshake (TLS 1.2) or on the first read (TLS 1.3).
        Assert.Equal(MtlsProbeOutcome.Refused, MtlsClientProbe.Classify(new AuthenticationException("Authentication failed, see inner exception.",
            new Exception("SSL Handshake failed with OpenSSL error - SSL_ERROR_SSL.", new Exception("error:0A000412:SSL routines::sslv3 alert bad certificate")))));
        Assert.Equal(MtlsProbeOutcome.Refused, MtlsClientProbe.Classify(new IOException("Unable to read data from the transport connection.",
            new Exception("error:0A00045C:SSL routines::tlsv13 alert certificate required"))));
        Assert.Equal(MtlsProbeOutcome.Refused, MtlsClientProbe.Classify(new Exception("error:0A000418:SSL routines::tlsv1 alert unknown ca")));
        // SChannel: the statuses a received alert maps to.
        Assert.Equal(MtlsProbeOutcome.Refused, MtlsClientProbe.Classify(new IOException("read failed", new Win32Exception(unchecked((int)0x80090327)))));
        Assert.Equal(MtlsProbeOutcome.Refused, MtlsClientProbe.Classify(new AuthenticationException("failed", new Win32Exception(unchecked((int)0x80090326)))));
        Assert.Equal(MtlsProbeOutcome.Refused, MtlsClientProbe.Classify(new IOException("Received an alert: bad certificate")));

        // A bare TLS failure, an OpenSSL error that is no alert, an end of stream and other SChannel errors prove nothing.
        Assert.Equal(MtlsProbeOutcome.NotChecked, MtlsClientProbe.Classify(new AuthenticationException("handshake failed")));
        Assert.Equal(MtlsProbeOutcome.NotChecked, MtlsClientProbe.Classify(new AuthenticationException("Authentication failed, see inner exception.",
            new Exception("SSL Handshake failed with OpenSSL error - SSL_ERROR_SSL.", new Exception("error:0A000126:SSL routines::unexpected eof while reading")))));
        Assert.Equal(MtlsProbeOutcome.NotChecked, MtlsClientProbe.Classify(new IOException("x",
            new Exception("SSL Handshake failed with OpenSSL error - SSL_ERROR_SYSCALL."))));
        Assert.Equal(MtlsProbeOutcome.NotChecked, MtlsClientProbe.Classify(new IOException("read failed", new Win32Exception(unchecked((int)0x80090304)))));
        Assert.Equal(MtlsProbeOutcome.NotChecked, MtlsClientProbe.Classify(new IOException("Authentication failed because the remote party has closed the transport stream.")));

        Assert.Equal(MtlsProbeOutcome.NotChecked, MtlsClientProbe.Classify(new IOException("reset", new SocketException((int)SocketError.ConnectionReset))));
        Assert.Equal(MtlsProbeOutcome.NotChecked, MtlsClientProbe.Classify(new TimeoutException()));
        Assert.Equal(MtlsProbeOutcome.NotChecked, MtlsClientProbe.Classify(new OperationCanceledException()));
        Assert.Equal(MtlsProbeOutcome.NotChecked, MtlsClientProbe.Classify(new IOException("Received an unexpected EOF or 0 bytes from the transport stream.")));
    }

    // ---- readiness ----

    [Fact]
    public async Task Readiness_names_shared_hosts_and_a_shared_key_left_behind()
    {
        using var w = new CredentialWorld(true, ("rabenhof", "100.64.0.9"), ("zirkuswagen", "100.64.0.10"));
        Assert.True((await w.Service.MigrateClientAsync("rabenhof", null, default)).Ok);
        IReadOnlyList<ReadinessCheck> Checks() =>
            new ProductionReadinessService(null!, null!, null!, w.Servers, clientCredentials: w.Store).MtlsClientCredentialChecks();

        var checks = Checks();
        Assert.True(checks.Single(c => c.Title.EndsWith("Rabenhof", StringComparison.Ordinal)).Ok);
        Assert.False(checks.Single(c => c.Title.EndsWith("Zirkuswagen", StringComparison.Ordinal)).Ok);
        Assert.False(checks.Single(c => c.Title == "Gemeinsamer mTLS-Client-Schlüssel").Ok);
        Assert.Contains(checks, c => c.Title == "mTLS-Client-Schlüssel geschützt");
    }

    [Fact]
    public async Task Readiness_names_hosts_whose_ghostunnel_is_stopped_or_unreadable_and_narrowed_hosts_running_another_list()
    {
        using var w = new CredentialWorld(true, ("rabenhof", "100.64.0.9"), ("zirkuswagen", "100.64.0.10"), ("burgcloud", "100.64.0.11"));
        Assert.True((await w.Service.MigrateClientAsync("rabenhof", null, default)).Ok);
        var readiness = new ProductionReadinessService(null!, null!, null!, w.Servers, clientCredentials: w.Store);

        // Everything runs; the two hosts not yet migrated still admit the shared name and are named for it.
        var before = await readiness.LiveAllowListChecksAsync(w.Service.ReadLiveAllowListAsync);
        Assert.True(before.Single(c => c.Title == "mTLS-Proxy (ghostunnel) läuft").Ok);
        Assert.True(before.Single(c => c.Title == "Laufende --allow-cn-Liste").Ok);
        var shared = before.Single(c => c.Title == "Gemeinsame Client-Identität im laufenden ghostunnel");
        Assert.False(shared.Ok);
        Assert.Contains("Zirkuswagen, Burgcloud", shared.Detail);
        Assert.DoesNotContain("Rabenhof", shared.Detail);

        w.Host("zirkuswagen").GhostunnelStopped = true;
        w.Host("burgcloud").Unreachable = true;
        w.Host("rabenhof").RunningAllow = [SharedCn, "whiskers-client-rabenhof-g1"];
        var checks = await readiness.LiveAllowListChecksAsync(w.Service.ReadLiveAllowListAsync);

        var down = checks.Single(c => c.Title == "mTLS-Proxy (ghostunnel) läuft");
        Assert.False(down.Ok);
        Assert.Contains("Zirkuswagen (ghostunnel läuft nicht)", down.Detail);
        Assert.Contains("Burgcloud (Argumente nicht lesbar)", down.Detail);
        Assert.Contains("Re-Onboarding per SSH", down.Detail);
        Assert.DoesNotContain("Rabenhof", down.Detail);
        var list = checks.Single(c => c.Title == "Laufende --allow-cn-Liste");
        Assert.False(list.Ok);
        Assert.Contains("Rabenhof", list.Detail);
    }

    [Fact]
    public async Task Readiness_names_every_host_whose_running_ghostunnel_admits_the_shared_name_operator_supplied_included()
    {
        using var w = new CredentialWorld(true, ("rabenhof", "100.64.0.9"), ("handmade", "100.64.0.12"));
        Assert.True((await w.Service.MigrateClientAsync("rabenhof", null, default)).Ok);
        // A hand-made host on operator paths whose ghostunnel admits the shared name besides the operator's own.
        var own = Path.Combine(w.Root, "operator");
        Directory.CreateDirectory(own);
        File.Copy(w.Store.SharedPaths.Cert, Path.Combine(own, "client.crt"));
        File.Copy(w.Store.SharedPaths.Key, Path.Combine(own, "client.key"));
        var record = w.Server("handmade").Clone();
        record.TcpClientCertPath = Path.Combine(own, "client.crt");
        record.TcpClientKeyPath = Path.Combine(own, "client.key");
        await w.Servers.UpdateServerAsync(record);
        var readiness = new ProductionReadinessService(null!, null!, null!, w.Servers, clientCredentials: w.Store);

        Assert.Equal(MtlsClientCredentialKind.OperatorSupplied, w.Store.StatusOf(w.Server("handmade")).Kind);
        var line = (await readiness.LiveAllowListChecksAsync(w.Service.ReadLiveAllowListAsync))
            .Single(c => c.Title == "Gemeinsame Client-Identität im laufenden ghostunnel");
        Assert.False(line.Ok);
        Assert.Contains("Handmade", line.Detail);
        Assert.DoesNotContain("Rabenhof", line.Detail);

        // Narrowed by hand: the line turns green.
        w.Host("handmade").RunningAllow = ["operator-client"];
        line = (await readiness.LiveAllowListChecksAsync(w.Service.ReadLiveAllowListAsync))
            .Single(c => c.Title == "Gemeinsame Client-Identität im laufenden ghostunnel");
        Assert.True(line.Ok, line.Detail);
    }

    [Fact]
    public async Task Readiness_points_a_host_behind_a_restored_older_backup_to_the_break_glass_re_onboarding()
    {
        // A real restore brings back the whole mtls/ directory (journals included) together with the records
        // (review r2 A), so only the live read shows that the host no longer admits what the record names.
        using var w = new CredentialWorld(true, ("rabenhof", "100.64.0.9"), ("zirkuswagen", "100.64.0.10"));
        var readiness = new ProductionReadinessService(null!, null!, null!, w.Servers, clientCredentials: w.Store);
        ReadinessCheck Line(IReadOnlyList<ReadinessCheck> checks, string name) =>
            checks.Single(c => c.Title == $"mTLS-Client-Identität: {name}");

        // 1) A backup taken before the migration: shared record, no hosts/ directory, no journal.
        var beforeMigration = Snapshot(w.MtlsDir, Path.Combine(w.Root, "backup-1"));
        var sharedRecord = w.Server("rabenhof").Clone();
        Assert.True((await w.Service.MigrateClientAsync("rabenhof", null, default)).Ok);
        Assert.True(Line(await readiness.MtlsChecksAsync(w.Service.ReadLiveAllowListAsync), "Rabenhof").Ok);

        Restore(beforeMigration, w.MtlsDir);
        await w.Servers.UpdateServerAsync(sharedRecord);
        Assert.Null(w.Store.ReadJournal("rabenhof"));

        var checks = await readiness.MtlsChecksAsync(w.Service.ReadLiveAllowListAsync);
        var line = Line(checks, "Rabenhof");
        Assert.False(line.Ok);
        Assert.Contains("Notfall-Re-Onboarding per SSH", line.Detail);
        Assert.Contains("„Migrieren“ erreicht ihn dann nicht", line.Detail);
        Assert.Contains("nur gerade nicht erreichbar", line.Detail);
        Assert.Contains("rabenhof", w.Fleet.LockedOut);
        Assert.Contains("Rabenhof (Argumente nicht lesbar)", checks.Single(c => c.Title == "mTLS-Proxy (ghostunnel) läuft").Detail);
        // The reachable host still on the shared credential keeps the plain „Migrieren“ advice.
        var zirkuswagen = Line(checks, "Zirkuswagen");
        Assert.False(zirkuswagen.Ok);
        Assert.Contains("Auf der Server-Seite „Migrieren“", zirkuswagen.Detail);
        Assert.DoesNotContain("Notfall-Re-Onboarding", zirkuswagen.Detail);
    }

    [Fact]
    public async Task Readiness_points_a_host_behind_a_backup_restored_after_a_rotation_to_the_break_glass_re_onboarding()
    {
        using var w = new CredentialWorld();
        var readiness = new ProductionReadinessService(null!, null!, null!, w.Servers, clientCredentials: w.Store);
        Assert.True((await w.Service.MigrateClientAsync("rabenhof", null, default)).Ok);

        // 2) A backup taken after the migration (g1, narrowed), restored after a rotation to g2.
        var afterMigration = Snapshot(w.MtlsDir, Path.Combine(w.Root, "backup-g1"));
        var g1Record = w.Server("rabenhof").Clone();
        Assert.True((await w.Service.RenewClientAsync("rabenhof", rotate: true, default)).Ok);

        Restore(afterMigration, w.MtlsDir);
        await w.Servers.UpdateServerAsync(g1Record);
        Assert.Equal(1, w.Store.ReadJournal("rabenhof")!.Generation);

        var line = (await readiness.MtlsChecksAsync(w.Service.ReadLiveAllowListAsync))
            .Single(c => c.Title == "mTLS-Client-Identität: Rabenhof");
        Assert.False(line.Ok);
        Assert.Contains("whiskers-client-rabenhof-g1", line.Detail);
        Assert.Contains("Notfall-Re-Onboarding per SSH", line.Detail);
    }

    private static string Snapshot(string source, string target)
    {
        CopyTree(source, target);
        return target;
    }

    private static void Restore(string snapshot, string target)
    {
        Directory.Delete(target, recursive: true);
        CopyTree(snapshot, target);
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source))
            CopyTree(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    // ---- registration and documentation ----

    [Fact]
    public void The_client_credential_tools_and_capabilities_are_part_of_the_protected_control_plane()
    {
        foreach (var name in new[]
                 {
                     "mtls.client_migrate", "migrate_mtls_client_credential", "mtls.client_renew", "renew_mtls_client_credential",
                     "mtls.shared_client_retire",
                 })
            Assert.Contains(name, ControlPlaneResources.Capabilities);

        // Both tools are Admin; retiring is UI only: no MCP tool.
        var tools = McpPermissionLevels.DefaultToolLevels;
        Assert.Equal(McpPermissionLevels.Admin, tools["migrate_mtls_client_credential"]);
        Assert.Equal(McpPermissionLevels.Admin, tools["renew_mtls_client_credential"]);
        Assert.DoesNotContain(tools.Keys, t => t.Contains("retire", StringComparison.Ordinal));
    }

    [Fact]
    public void The_architecture_and_the_invariant_document_the_blast_radius_per_host_after_migration()
    {
        var architecture = DeploymentScripts.ReadRepoFile("docs", "ARCHITECTURE.md");
        Assert.Contains("## Blast radius", architecture);
        Assert.Contains("whiskers-client-", architecture);

        var invariants = DeploymentScripts.ReadRepoFile("docs", "v1", "SECURITY_INVARIANTS.md");
        var row = invariants.Split('\n').Single(l => l.StartsWith("| INV-58 |", StringComparison.Ordinal));
        Assert.Contains("✅", row);
        Assert.Contains("per host after migration", row);
        Assert.Contains("`PerHostCredentialTests`", row);
        Assert.DoesNotContain("planned", row);
    }

    // ---- doubles ----

    /// <summary>The onboarding's bootstrap and host commands: tailnet up, everything else succeeds; step-ca via the fleet.</summary>
    private sealed class OnboardingExecutor(SimulatedFleet fleet, string ip, TestCa? ca = null) : IHostCommandExecutor
    {
        public List<string> Scripts { get; } = new();
        /// <summary>Runs before each command (e.g. a record change while onboarding is in its first steps).</summary>
        public Action<string>? OnCommand { get; set; }
        /// <summary>Whether the host was asked to activate a freshly staged server certificate.</summary>
        public bool ServerCertificateActivated { get; private set; }

        public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null)
        {
            Scripts.Add(command);
            OnCommand?.Invoke(command);
            if (ca is not null && serverId != "local")
            {
                // The server-certificate steps of the real MtlsCertificateService on the new host (key and CSR made there).
                var script = Plain(command);
                if (script.Contains("openssl req -new", StringComparison.Ordinal))
                {
                    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                    var identity = MtlsIdentity.ForNewHost(OnboardingCommands.Slug(serverId), ip);
                    var csr = MtlsCertificateRenewalTests.Csr(key, identity.CommonName, [.. identity.DnsNames], [.. identity.IpAddresses]);
                    return Task.FromResult(new CommandResult { ExitCode = 0, Output = SimulatedProxyHost.B64(csr) });
                }
                if (script.Contains("echo staged", StringComparison.Ordinal))
                    return Task.FromResult(new CommandResult { ExitCode = 0, Output = "staged" });
            }
            if (ca is not null && serverId == "local"
                && Plain(command).StartsWith("cat /opt/step-ca/certs/root_ca.crt", StringComparison.Ordinal))
                return Task.FromResult(new CommandResult { ExitCode = 0, Output = SimulatedProxyHost.B64(ca.BundlePem) });
            if (ca is not null && command == MtlsCertificateCommands.ActivateFresh())
                ServerCertificateActivated = true;
            if (command.Contains("BackendState", StringComparison.Ordinal))
                return Task.FromResult(new CommandResult { ExitCode = 0, Output = "\"BackendState\": \"Running\"," });
            if (command.Contains("tailscale ip -4", StringComparison.Ordinal))
                return Task.FromResult(new CommandResult { ExitCode = 0, Output = serverId == "local" ? "100.64.0.1" : ip });
            if (serverId == "local" && MtlsCertificateRenewalTests.Decode(command).Contains("step certificate sign", StringComparison.Ordinal))
                return fleet.ExecuteAsync(serverId, command, timeout, ct, maxOutputChars);
            return Task.FromResult(new CommandResult { ExitCode = 0, Output = "" });
        }

        /// <summary>The script inside an <c>echo b64 | base64 -d | sh</c> wrapper; any other command as it is.</summary>
        private static string Plain(string command)
        {
            try { return MtlsCertificateRenewalTests.Decode(command); }
            catch (FormatException) { return command; }
        }
    }

    /// <summary>Docker that reports every host reachable over mTLS.</summary>
    public class ReachableDocker : DispatchProxy
    {
        public static IDockerService Create() => DispatchProxy.Create<IDockerService, ReachableDocker>();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IDockerService.GetServerSystemInfoAsync))
                return Task.FromResult(new ServerSystemInfo { IsReachable = true, OperatingSystem = "Linux" });
            throw new NotSupportedException(method.Name);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception) + " " + exception);
    }
}
