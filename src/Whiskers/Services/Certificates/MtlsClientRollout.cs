using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Whiskers.Services.Docker;

namespace Whiskers.Services.Certificates;

/// <summary>
/// R1-44 (contract 0007, amendments 1–3): the per-host client credential rollout of <see cref="MtlsCertificateService"/>.
///
/// <list type="number">
/// <item><b>Issue</b> — the host's own credential in a new store slot (nothing on the host changes).</item>
/// <item><b>Widen</b> — the host admits the old and the new name; guarded, confirmed by a marker created through a one-off
/// connection with the new credential.</item>
/// <item><b>Switch</b> — the server record takes the new slot's paths (one save), the cached Docker client is rebuilt and
/// the connection checked; on failure the record is put back (the host still admits both).</item>
/// <item><b>Narrow</b> — the host admits only the new name; confirmed only once the new credential is admitted and the
/// previous one refused with a TLS alert (or, with no shared credential left, by the running allow-list alone).</item>
/// <item><b>Record</b> — the journal records phase 5.</item>
/// </list>
///
/// <para>Host state is read from the running containers (<c>docker inspect</c>), and the preflight refuses a host whose
/// compose file and containers differ, that admits a foreign name, or on which another run's guard is armed. Every phase
/// is skipped when the host, the record and the store already show it done, so a second call resumes where a failed one
/// stopped — re-running the checks of the phase it resumes in. One lock per host, shared with the server-certificate
/// renewal. A renewal or rotation never overwrites the live credential (store slots).</para>
/// </summary>
public sealed partial class MtlsCertificateService
{
    /// <summary>The common name of the hand-made shared credential, used when its certificate cannot be read.</summary>
    public const string SharedClientCommonName = "serverwatch-client";

    private readonly IMtlsClientCredentialStore? _clientCredentials;
    private readonly IDockerConnectionManager? _connections;

    // Seams for tests: the probe and the marker over a one-off connection built from the given record's credential, the
    // check of the live connection after a switch, and the run suffix of the guard units and markers.
    internal Func<Models.ServerConfig, CancellationToken, Task<MtlsProbeOutcome>> ProbeCredential { get; set; }
    internal Func<Models.ServerConfig, string, CancellationToken, Task<(string Output, string Error, int ExitCode)>> RunConfirmation { get; set; }
    internal Func<string, CancellationToken, Task<bool>> VerifyConnection { get; set; }
    internal Func<string> UnitSuffix { get; set; } = () => DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");

    private sealed class RolloutFailure(string message) : InvalidOperationException(message);

    private sealed class RolloutRun(IProgress<string>? progress)
    {
        public string Phase { get; private set; } = "preflight";
        public List<string> OwnRuns { get; } = new();
        /// <summary>Confirmed runs whose marker and lock are removed once the phase is recorded.</summary>
        public List<string> Confirmed { get; } = new();
        public void Enter(string phase, string message)
        {
            Phase = phase;
            progress?.Report(message);
        }
    }

    public async Task<MtlsClientMigrationResult> MigrateClientAsync(string serverId, IProgress<string>? progress, CancellationToken ct)
    {
        var run = new RolloutRun(progress);
        if (_clientCredentials is not { } store) return Failed(run, "Per-host client credentials are not available in this installation.");
        var server = _servers.GetServer(serverId)?.Clone();
        if (PreflightProblem(server) is { } problem) return Failed(run, problem);

        if (!store.IsShared(server!) && !store.IsManaged(server!))
            return Failed(run, $"{server!.Name} uses an operator-supplied client certificate ({server.TcpClientCertPath}); Whiskers does not touch it.");
        if (!MtlsClientCredentialStore.SamePath(server!.TcpCaCertPath, store.SharedPaths.Ca))
            return Failed(run, $"{server.Name} trusts an operator-supplied CA bundle ({server.TcpCaCertPath}); Whiskers does not touch it.");

        var gate = HostGate(server.Id);
        if (!await gate.WaitAsync(0, ct))
            return Failed(run, $"A certificate operation for {server.Name} is already running.");
        try
        {
            return await MigrateLockedAsync(store, server, run, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Client credential migration of {Server} stopped in phase {Phase}: {Error}", server.Name, run.Phase, ex.Message);
            return Failed(run, ex.Message, SafeIdentity(store, server.Id));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<MtlsClientMigrationResult> MigrateLockedAsync(IMtlsClientCredentialStore store, Models.ServerConfig server,
        RolloutRun run, CancellationToken ct)
    {
        var id = server.Id;
        var shared = store.SharedPaths;
        var sharedCn = CommonNameOf(shared.Cert) ?? SharedClientCommonName;

        run.Enter("preflight", $"Reading the state of {server.Name}'s proxy…");
        var host = await ReadHostAsync(server, store, sharedCn, run, requireNoForeignGuards: true, ct);
        // The state comes from what ghostunnel runs with (amendment 7), not from the last phase record: a done migration
        // re-runs its checks (both probes) and records phase 5 again; more than one own name means a rotation is under way.
        if (store.IdentityOf(id) is { } known
            && host.AllowedCommonNames.Count(n => OwnGeneration(known.Stem, n) is not null) > 1)
            throw new RolloutFailure($"A rotation of {server.Name}'s credential is in progress; finish it with a rotating renewal.");
        MtlsRolloutJournal journal;

        // 1 Issue ------------------------------------------------------------------------------------------------------
        run.Enter("issue", "① Issuing the host's own client credential…");
        var identity = store.IdentityOf(id);
        MtlsClientCredentialPaths paths;
        if (store.IsManaged(server))
        {
            // A resumed migration: the server record is authoritative for the slot Whiskers connects with.
            var generation = store.StatusOf(server).Generation;
            if (identity is null || ConfigPaths(server, store, generation) is not { } configured)
                throw new RolloutFailure($"{server.Name} is set to its own credential, but the store has no complete credential for it.");
            paths = configured;
            identity = MtlsClientIdentity.FromStem(id, identity.Stem, generation);
        }
        else
        {
            // Not switched yet: a fresh slot. Re-issuing on a resume is harmless, the host admits the name, not a key.
            paths = await store.IssueAsync(server, identity?.Generation ?? 1, ct);
            identity = store.IdentityOf(id) ?? throw new RolloutFailure("The issued identity could not be read back.");
        }
        var cn = identity.CommonName;
        journal = await AdvanceAsync(store, id, new MtlsRolloutJournal(MtlsRolloutJournal.Migrate, cn, identity.Generation,
            0, sharedCn, false, DateTime.UtcNow), 1, ct);

        var target = WithCredential(server, paths);
        var previous = File.Exists(shared.Cert) && File.Exists(shared.Key) ? WithCredential(server, shared with { Ca = paths.Ca }) : null;

        // 2 Widen ------------------------------------------------------------------------------------------------------
        run.Enter("widen", $"② The proxy admits {cn} besides the current name…");
        await WidenAsync(server, store, sharedCn, run, host, cn, target);
        journal = await AdvanceAsync(store, id, journal, 2, ct);
        await CleanupConfirmedAsync(id, run);

        // 3 Switch -----------------------------------------------------------------------------------------------------
        run.Enter("switch", "③ Switching Whiskers to the new credential…");
        var current = _servers.GetServer(id)?.Clone() ?? throw new RolloutFailure($"{server.Name} was removed meanwhile.");
        if (!store.IsManaged(current))
            await SwitchRecordAsync(current, paths, onFailure: null, ct);
        await ActivateIfRecordNamesPendingAsync(store, id, verified: !store.IsManaged(current));
        journal = await AdvanceAsync(store, id, journal, 3, CancellationToken.None);

        // 4 Narrow -----------------------------------------------------------------------------------------------------
        run.Enter("narrow", $"④ The proxy admits only {cn}…");
        current = _servers.GetServer(id)?.Clone() ?? throw new RolloutFailure($"{server.Name} was removed meanwhile.");
        if (!MtlsClientCredentialStore.SamePath(current.TcpClientCertPath, paths.Cert))
            throw new RolloutFailure($"Whiskers does not connect to {server.Name} with the new credential yet (the server record names another one); not narrowing.");
        await NarrowAsync(current, store, sharedCn, run, cn, target, previous, ct);
        journal = await AdvanceAsync(store, id, journal with { NegativeCheckPassed = previous is not null, SharedCredentialAbsent = previous is null }, 4, ct);
        await CleanupConfirmedAsync(id, run);

        // 5 Record -----------------------------------------------------------------------------------------------------
        run.Enter("record", "⑤ Recording the result…");
        await AdvanceAsync(store, id, journal, 5, ct);
        // Slots of earlier attempts (a re-issue on a resume) hold keys the host would admit: gone once phase 5 is recorded.
        await store.DiscardPreviousAsync(id, CancellationToken.None);
        _logger.LogInformation("{Server} now admits only its own client credential {CommonName} (INV-58)", server.Name, cn);
        run.Enter("done", previous is null
            ? $"✅ {server.Name} admits only {cn} (there is no shared credential left to try)."
            : $"✅ {server.Name} admits only {cn}; the shared credential is refused there.");
        return new(true, "done", null, identity);
    }

    public async Task<MtlsClientMigrationResult> RenewClientAsync(string serverId, bool rotate, CancellationToken ct)
    {
        var run = new RolloutRun(null);
        if (_clientCredentials is not { } store) return Failed(run, "Per-host client credentials are not available in this installation.");
        var server = _servers.GetServer(serverId)?.Clone();
        if (PreflightProblem(server) is { } problem) return Failed(run, problem);
        if (!store.IsManaged(server!))
            return Failed(run, $"{server!.Name} does not use its own client credential yet; migrate it first.");
        if (store.IdentityOf(server!.Id) is null)
            return Failed(run, $"The store holds no identity for {server.Name}; migrate it again.");

        var gate = HostGate(server.Id);
        if (!await gate.WaitAsync(0, ct))
            return Failed(run, $"A certificate operation for {server.Name} is already running.");
        try
        {
            return rotate
                ? await RotateLockedAsync(store, server, run, ct)
                : await RenewLockedAsync(store, server, run, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Client credential {Kind} of {Server} stopped in phase {Phase}: {Error}",
                rotate ? "rotation" : "renewal", server.Name, run.Phase, ex.Message);
            return Failed(run, ex.Message, SafeIdentity(store, server.Id));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Same generation: a new key and certificate under the same name in a new slot. The host is unchanged
    /// (ghostunnel checks the name); the old slot stays until the new connection is confirmed.</summary>
    private async Task<MtlsClientMigrationResult> RenewLockedAsync(IMtlsClientCredentialStore store, Models.ServerConfig server,
        RolloutRun run, CancellationToken ct)
    {
        var id = server.Id;
        // State comes from the record and the store, never from the last phase record (amendment 7).
        var generation = store.StatusOf(server).Generation;
        if (store.PendingFor(id) is { } stale)
        {
            if (MtlsClientCredentialStore.SamePath(server.TcpClientCertPath, stale.Cert))
            {
                // A crash between the record save and the store update: the record wins once the connection is confirmed.
                if (!await VerifyConnection(id, ct))
                    throw new RolloutFailure($"The server record of {server.Name} names the new certificate, but no connection through it could be confirmed.");
                await store.ActivatePendingAsync(id, CancellationToken.None);
            }
            else if (stale.Generation != generation)
            {
                throw new RolloutFailure($"A rotation of {server.Name}'s credential is in progress; finish it with a rotating renewal.");
            }
            else
            {
                await store.DiscardPendingAsync(id, ct);
            }
        }
        var identity = MtlsClientIdentity.FromStem(id, store.IdentityOf(id)!.Stem, generation);

        run.Enter("issue", "Issuing a new certificate under the same name…");
        await store.DiscardPreviousAsync(id, ct);
        var pending = await store.IssuePendingAsync(server, identity.Generation, ct);   // exactly these paths go into the record

        run.Enter("switch", "Switching to the new certificate…");
        await SwitchRecordAsync(server, pending, onFailure: () => store.DiscardPendingAsync(id, CancellationToken.None), ct);
        await store.ActivatePendingAsync(id, CancellationToken.None);
        await store.DiscardPreviousAsync(id, CancellationToken.None);
        run.Enter("done", $"Client certificate of {server.Name} renewed ({identity.CommonName}).");
        return new(true, "done", null, identity);
    }

    /// <summary>Next generation: phases 1–5 again for g(n) → g(n+1). The g(n) slot is deleted only after the host refused
    /// it (D2).</summary>
    private async Task<MtlsClientMigrationResult> RotateLockedAsync(IMtlsClientCredentialStore store, Models.ServerConfig server,
        RolloutRun run, CancellationToken ct)
    {
        var id = server.Id;
        var stem = store.IdentityOf(id)!.Stem;
        var sharedCn = CommonNameOf(store.SharedPaths.Cert) ?? SharedClientCommonName;

        run.Enter("preflight", $"Reading the state of {server.Name}'s proxy…");
        var host = await ReadHostAsync(server, store, sharedCn, run, requireNoForeignGuards: true, ct);

        // Where the run stands is derived from what ghostunnel runs with, the server record and the store — never from the
        // last phase record (amendment 7): a crash between stopping a guard and writing its marker leaves a host whose
        // state the record does not show.
        if (host.AllowedCommonNames.Contains(sharedCn, StringComparer.Ordinal))
            throw new RolloutFailure($"The proxy of {server.Name} still admits the shared credential; finish the migration first.");
        var recordGeneration = store.StatusOf(server).Generation;
        var pending = store.PendingFor(id);
        var liveGenerations = host.AllowedCommonNames.Select(n => OwnGeneration(stem, n)).OfType<int>().ToList();
        var highest = liveGenerations.Append(pending?.Generation ?? 0).Append(recordGeneration).Max();
        var finished = highest == recordGeneration && host.AllowedCommonNames.Count == 1 && liveGenerations.SingleOrDefault() == recordGeneration;
        var next = MtlsClientIdentity.FromStem(id, stem, finished ? recordGeneration + 1 : highest);
        var previousCn = MtlsClientIdentity.FromStem(id, stem, Math.Max(1, next.Generation - 1)).CommonName;

        // 1 Issue ------------------------------------------------------------------------------------------------------
        run.Enter("issue", "① Issuing the next generation…");
        if (pending is not null && pending.Generation != next.Generation)
        {
            await store.DiscardPendingAsync(id, ct);   // left by another attempt; never a slot a record references
            pending = null;
        }
        if (finished) await store.DiscardPreviousAsync(id, ct);
        var journal = await AdvanceAsync(store, id, new MtlsRolloutJournal(MtlsRolloutJournal.Rotate, next.CommonName, next.Generation,
            0, previousCn, false, DateTime.UtcNow), 1, ct);

        // Switched or not: the record is authoritative. A record that names the pending slot while the store was not
        // updated (a crash in between) makes it active only once a connection through it is confirmed (amendment 4).
        if (pending is not null && MtlsClientCredentialStore.SamePath(server.TcpClientCertPath, pending.Cert))
        {
            if (!await VerifyConnection(id, ct))
                throw new RolloutFailure($"The server record of {server.Name} names the new generation, but no connection through it could be confirmed.");
            await store.ActivatePendingAsync(id, CancellationToken.None);
            pending = null;
        }
        var switched = recordGeneration == next.Generation;
        if (!switched && pending is null)
            pending = await store.IssuePendingAsync(server, next.Generation, ct);   // the host admits the name, not a key
        // The server record is authoritative for the slot in use (amendment 6): before the switch it names the previous
        // generation, after it the new one.
        var inUse = ConfigPaths(server, store, recordGeneration)
            ?? throw new RolloutFailure($"The credential files {server.Name}'s record names are missing.");
        var targetPaths = switched ? inUse : pending!;
        var previousPaths = switched ? store.PreviousFor(id) : inUse;
        var target = WithCredential(server, targetPaths);

        // 2 Widen ------------------------------------------------------------------------------------------------------
        run.Enter("widen", $"② The proxy admits {next.CommonName} besides the current name…");
        await WidenAsync(server, store, sharedCn, run, host, next.CommonName, target);
        journal = await AdvanceAsync(store, id, journal, 2, ct);
        await CleanupConfirmedAsync(id, run);

        // 3 Switch -----------------------------------------------------------------------------------------------------
        run.Enter("switch", "③ Switching Whiskers to the new generation…");
        if (!switched)
        {
            await SwitchRecordAsync(server, targetPaths, onFailure: () => store.DiscardPendingAsync(id, CancellationToken.None), ct);
            await store.ActivatePendingAsync(id, CancellationToken.None);
            previousPaths = store.PreviousFor(id);
        }
        journal = await AdvanceAsync(store, id, journal, 3, CancellationToken.None);

        // 4 Narrow -----------------------------------------------------------------------------------------------------
        run.Enter("narrow", $"④ The proxy admits only {next.CommonName}…");
        if (previousPaths is null)
            throw new RolloutFailure($"The previous credential of {server.Name} is gone, so its refusal cannot be proven; rotate again.");
        var current = _servers.GetServer(id)?.Clone() ?? throw new RolloutFailure($"{server.Name} was removed meanwhile.");
        if (!MtlsClientCredentialStore.SamePath(current.TcpClientCertPath, targetPaths.Cert))
            throw new RolloutFailure($"Whiskers does not connect to {server.Name} with the new generation yet (the server record names another one); not narrowing.");
        await NarrowAsync(current, store, sharedCn, run, next.CommonName, WithCredential(current, targetPaths),
            WithCredential(current, previousPaths), ct);
        journal = await AdvanceAsync(store, id, journal with { NegativeCheckPassed = true }, 4, ct);
        await CleanupConfirmedAsync(id, run);
        await store.DiscardPreviousAsync(id, ct);

        // 5 Record -----------------------------------------------------------------------------------------------------
        run.Enter("record", "⑤ Recording the result…");
        await AdvanceAsync(store, id, journal, 5, ct);
        _logger.LogInformation("{Server} rotated to client credential {CommonName}; generation {Old} is refused there",
            server.Name, next.CommonName, next.Generation - 1);
        run.Enter("done", $"{server.Name} admits only {next.CommonName}.");
        return new(true, "done", null, next);
    }

    public async Task<MtlsRenewalResult> RetireSharedClientAsync(CancellationToken ct)
    {
        if (_clientCredentials is not { } store) return new(false, "Per-host client credentials are not available in this installation.");
        var shared = store.SharedPaths;
        var gate = _locks.GetOrAdd("\0shared-client", _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) return new(false, "The shared client credential is already being retired.");
        try
        {
            if (!File.Exists(shared.Key) && !File.Exists(shared.Cert))
                return new(true, "The shared client credential is already gone.");
            var sharedThumbprint = ThumbprintOf(shared.Cert);
            var sharedCn = CommonNameOf(shared.Cert) ?? SharedClientCommonName;

            // Considered: servers whose client certificate is the shared one (by thumbprint or name) and servers with a
            // managed identity. Anything else (operator-supplied) is listed but does not block.
            var blocked = new List<string>();
            var ignored = new List<string>();
            foreach (var s in _servers.GetServers().Where(s => s.TcpUseTls && !string.IsNullOrWhiteSpace(s.TcpClientCertPath)))
            {
                var thumbprint = ThumbprintOf(s.TcpClientCertPath!);
                var name = CommonNameOf(s.TcpClientCertPath!);
                var usesShared = (sharedThumbprint is not null && thumbprint == sharedThumbprint) || name == sharedCn || store.IsShared(s);
                var managed = MtlsClientCredentialStore.IsValidServerId(s.Id) && (store.IsManaged(s) || store.IdentityOf(s.Id) is not null);
                if (usesShared) { blocked.Add($"{s.Name} (still presents the shared certificate)"); continue; }
                if (!managed) { ignored.Add(s.Name); continue; }
                var journal = store.ReadJournal(s.Id);
                if (journal is not { IsNarrowed: true }) { blocked.Add($"{s.Name} (not narrowed to its own credential)"); continue; }
                if (name is null || name != journal.CommonName) { blocked.Add($"{s.Name} (presents another name than its journal records)"); continue; }
                // The record alone is not enough (a restored backup, a hand edit): the running proxy must admit exactly
                // this host's own name (amendment 4).
                var live = await ReadLiveAllowListAsync(s.Id, ct);
                if (!live.Readable) { blocked.Add($"{s.Name} (its running allow-list could not be read)"); continue; }
                if (!live.Running) { blocked.Add($"{s.Name} (its ghostunnel is not running)"); continue; }
                if (live.AllowedCommonNames.Count != 1 || live.AllowedCommonNames[0] != name)
                    blocked.Add($"{s.Name} (its proxy admits {string.Join(", ", live.AllowedCommonNames)})");
            }
            if (blocked.Count > 0)
                return new(false, "The shared client credential is still needed or not proven unused: " + string.Join(", ", blocked) + ".");

            File.Delete(shared.Key);
            File.Delete(shared.Cert);
            _logger.LogInformation("Retired the shared mTLS client credential; every managed mTLS host admits only its own identity");
            return new(true, "The shared client key and certificate are deleted; the CA bundle stays." +
                             (ignored.Count > 0 ? " Not considered (operator-supplied credentials): " + string.Join(", ", ignored) + "." : ""));
        }
        finally
        {
            gate.Release();
        }
    }

    // ---- phases ---------------------------------------------------------------------------------------------------

    private async Task WidenAsync(Models.ServerConfig server, IMtlsClientCredentialStore store, string sharedCn, RolloutRun run,
        MtlsHostState host, string cn, Models.ServerConfig target)
    {
        if (!host.AllowedCommonNames.Contains(cn, StringComparer.Ordinal))
        {
            var widened = host.AllowedCommonNames.Append(cn).ToList();
            await ApplyGuardedAsync(server, store, sharedCn, run, host.Compose!, widened, target, mustFail: null);
        }
        else if (!FileMatchesRunning(host))
        {
            // Running and file disagree (a guard restored the file but its recreate failed): nothing counts as done until
            // the file says the same, so the widened list — what the file admits plus the new name — goes through a
            // guarded apply again; the narrowing then removes the rest with its negative check (review r1 M2).
            var widened = host.FileAllowedCommonNames.Union(host.AllowedCommonNames, StringComparer.Ordinal)
                .Append(cn).Distinct(StringComparer.Ordinal).ToList();
            await ApplyGuardedAsync(server, store, sharedCn, run, host.Compose!, widened, target, mustFail: null);
        }
        else if (await ProbeCredential(target, CancellationToken.None) != MtlsProbeOutcome.Admitted)
        {
            throw new RolloutFailure($"The proxy of {server.Name} lists {cn} but does not admit the new credential.");
        }
    }

    private async Task NarrowAsync(Models.ServerConfig current, IMtlsClientCredentialStore store, string sharedCn, RolloutRun run,
        string cn, Models.ServerConfig target, Models.ServerConfig? previous, CancellationToken ct)
    {
        var host = await ReadHostAsync(current, store, sharedCn, run, requireNoForeignGuards: true, ct);
        if (host.AllowedCommonNames.Count != 1 || host.AllowedCommonNames[0] != cn || !FileMatchesRunning(host))
        {
            // Also when only the file still admits more (review r1 M2): the next `docker compose up` would bring it back.
            await ApplyGuardedAsync(current, store, sharedCn, run, host.Compose!, [cn], target, previous);
            return;
        }
        // Already narrowed in the container and the file (a resumed run): both checks are made again before phase 4.
        if (await ProbeCredential(target, ct) != MtlsProbeOutcome.Admitted)
            throw new RolloutFailure($"The proxy of {current.Name} admits only {cn} but does not admit that credential.");
        if (previous is not null)
        {
            var negative = await ProbeCredential(previous, ct);
            if (negative != MtlsProbeOutcome.Refused)
                throw new RolloutFailure(negative == MtlsProbeOutcome.Admitted
                    ? $"The proxy of {current.Name} still admits the previous credential."
                    : $"Whether the proxy of {current.Name} refuses the previous credential could not be checked (no TLS alert); try again.");
        }
    }

    /// <summary>A confirmation whose outcome is unknown (review r1 MINOR 4): it may have stopped the guard, so no rollback
    /// is promised; the next run reads the state from ghostunnel's live arguments (amendment 7).</summary>
    private static RolloutFailure StateUnknown(Models.ServerConfig server, string detail) =>
        new($"State unknown: whether {server.Name} took the confirmation could not be told ({Trim(detail)}). The host may keep " +
            "the new allow-list with no guard left, or restore the previous one. Run the step again; it reads the state from " +
            "ghostunnel's live arguments. Nothing was recorded.");

    private static bool FileMatchesRunning(MtlsHostState host) =>
        host.FileAllowedCommonNames.SequenceEqual(host.AllowedCommonNames, StringComparer.Ordinal);

    /// <summary>Record switch: one save of the server record to <paramref name="paths"/>, then the cached Docker client is
    /// rebuilt and the connection checked; a failed check puts the record back.</summary>
    private async Task SwitchRecordAsync(Models.ServerConfig server, MtlsClientCredentialPaths paths, Func<Task>? onFailure, CancellationToken ct)
    {
        var current = _servers.GetServer(server.Id)?.Clone() ?? throw new RolloutFailure($"{server.Name} was removed meanwhile.");
        var before = current.Clone();
        current.TcpClientCertPath = paths.Cert;
        current.TcpClientKeyPath = paths.Key;
        current.TcpCaCertPath = paths.Ca;
        await _servers.UpdateServerAsync(current);
        // From the save onward the caller's token no longer applies (review r1 MINOR 5): a cancelled check would leave
        // the record switched but unverified and never put back.
        if (await VerifyConnection(server.Id, CancellationToken.None)) return;

        await _servers.UpdateServerAsync(before);
        _connections?.InvalidateClient(server.Id);
        if (onFailure is not null) await onFailure();
        throw new RolloutFailure($"The connection to {server.Name} with the new credential could not be verified; the previous " +
                                 "credential is back in place (the proxy still admits it).");
    }

    /// <summary>The record is authoritative (amendment 4): once it names the pending slot and a connection through it was
    /// confirmed (<paramref name="verified"/>, or checked here), the store makes that slot active. Nothing to do when the
    /// record names another slot.</summary>
    private async Task ActivateIfRecordNamesPendingAsync(IMtlsClientCredentialStore store, string id, bool verified)
    {
        var record = _servers.GetServer(id);
        if (record is null || store.PendingFor(id) is not { } pending
            || !MtlsClientCredentialStore.SamePath(record.TcpClientCertPath, pending.Cert))
            return;
        if (!verified && !await VerifyConnection(id, CancellationToken.None))
            throw new RolloutFailure($"The server record of {record.Name} names the new credential, but no connection through it could be confirmed.");
        await store.ActivatePendingAsync(id, CancellationToken.None);
    }

    /// <summary>Applies an allow-list under the guard and confirms it — one script through a one-off connection with the
    /// new credential that stops its own still-pending guard and writes the marker — only when the running ghostunnel has
    /// exactly that list, the new credential is admitted and, for a narrowing, the previous one was refused with a TLS
    /// alert. Otherwise nothing is confirmed and the guard on the host restores the previous file by itself. After the
    /// confirmation the host is read and checked again.</summary>
    private async Task ApplyGuardedAsync(Models.ServerConfig server, IMtlsClientCredentialStore store, string sharedCn, RolloutRun run,
        MtlsAllowListCommands.ComposeTarget compose, IReadOnlyList<string> allow, Models.ServerConfig target, Models.ServerConfig? mustFail)
    {
        var suffix = UnitSuffix();
        run.OwnRuns.Add(suffix);
        await RunAsync(server.Id, MtlsAllowListCommands.Apply(compose, allow, suffix), CancellationToken.None);

        // From here on a cancelled caller must not strand the host: the guard rolls back on its own, and a confirmation
        // that still gets through is the better outcome.
        await Task.Delay(RestartGrace);
        string? stop = null;
        for (var attempt = 0; attempt < ConfirmAttempts && stop is null; attempt++)
        {
            try
            {
                var state = await ReadHostAsync(server, store, sharedCn, run, requireNoForeignGuards: false, CancellationToken.None);
                if (state.AllowedCommonNames.SequenceEqual(allow, StringComparer.Ordinal)
                    && await ProbeCredential(target, CancellationToken.None) == MtlsProbeOutcome.Admitted)
                {
                    if (mustFail is not null)
                    {
                        var negative = await ProbeCredential(mustFail, CancellationToken.None);
                        if (negative == MtlsProbeOutcome.Admitted) { stop = "still admits the previous credential"; continue; }
                        if (negative == MtlsProbeOutcome.NotChecked) { stop = "gave no TLS alert for the previous credential (not checked)"; continue; }
                    }
                    // One script through the NEW credential: own guard still pending → stop it → marker.
                    (string Output, string Error, int ExitCode) confirmed;
                    try
                    {
                        confirmed = await RunConfirmation(target, MtlsAllowListCommands.Confirm(compose, suffix), CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        // The script may have run (timer stopped, perhaps the marker written) before the answer was lost.
                        throw StateUnknown(server, ex.Message);
                    }
                    if (confirmed.ExitCode is 124 or 137)
                        throw StateUnknown(server, "the confirmation was stopped by its 20-second limit inside the lock");
                    if (confirmed.ExitCode != 0)
                    {
                        stop = "did not take the confirmation (" + Trim(confirmed.Error.Trim()) + ")";
                        continue;
                    }
                    // The guard may have fired between the checks and the marker: read the host again and repeat the
                    // checks before anything is recorded (amendment 4, 0007-A).
                    await Task.Delay(RestartGrace);
                    await RecheckAsync(server, store, sharedCn, run, allow, target, mustFail);
                    run.Confirmed.Add(suffix);
                    return;
                }
            }
            catch (RolloutFailure)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug("Allow-list confirmation attempt {Attempt} on {Server} failed: {Error}", attempt + 1, server.Name, ex.Message);
            }
            await Task.Delay(ConfirmInterval);
        }

        var rollbackAt = DateTime.UtcNow + MtlsAllowListCommands.GuardDelay;
        throw new RolloutFailure(stop is not null
            ? $"The proxy of {server.Name} {stop}, so the change was not confirmed. The host restores its previous allow-list " +
              $"on its own by about {rollbackAt:HH:mm} UTC."
            : $"The new allow-list of {server.Name} could not be confirmed through the new credential. The host restores its " +
              $"previous allow-list on its own by about {rollbackAt:HH:mm} UTC; run the step again after that.");
    }

    /// <summary>After a confirmation: the running ghostunnel and the file must still hold exactly <paramref name="allow"/>,
    /// the new credential must be admitted and, for a narrowing, the previous one refused again.</summary>
    private async Task RecheckAsync(Models.ServerConfig server, IMtlsClientCredentialStore store, string sharedCn, RolloutRun run,
        IReadOnlyList<string> allow, Models.ServerConfig target, Models.ServerConfig? mustFail)
    {
        var state = await ReadHostAsync(server, store, sharedCn, run, requireNoForeignGuards: false, CancellationToken.None);
        if (!state.AllowedCommonNames.SequenceEqual(allow, StringComparer.Ordinal)
            || !state.FileAllowedCommonNames.SequenceEqual(allow, StringComparer.Ordinal))
            throw new RolloutFailure($"The guard on {server.Name} restored the previous allow-list after all; nothing was recorded. Run the step again.");
        if (await ProbeCredential(target, CancellationToken.None) != MtlsProbeOutcome.Admitted)
            throw new RolloutFailure($"After the confirmation {server.Name} no longer admits the new credential; nothing was recorded.");
        if (mustFail is not null && await ProbeCredential(mustFail, CancellationToken.None) != MtlsProbeOutcome.Refused)
            throw new RolloutFailure($"After the confirmation the refusal of the previous credential at {server.Name} could not be shown again; nothing was recorded.");
    }

    private async Task<MtlsHostState> ReadHostAsync(Models.ServerConfig server, IMtlsClientCredentialStore store, string sharedCn,
        RolloutRun run, bool requireNoForeignGuards, CancellationToken ct)
    {
        var own = new[] { store.IdentityOf(server.Id)?.Stem, store.PendingIdentityOf(server.Id)?.Stem }
            .Where(s => s is not null).Select(s => s + "-g").ToList();
        bool Permitted(string name) =>
            name == sharedCn
            || own.Any(stem => name.StartsWith(stem, StringComparison.Ordinal) && name[stem.Length..].All(char.IsAsciiDigit) && name.Length > stem.Length);

        var state = MtlsHostState.Parse((await RunAsync(server.Id, MtlsAllowListCommands.ReadHostState(), ct)).Output, Permitted, run.OwnRuns);
        if (state.Problem is not null)
            throw new RolloutFailure($"{server.Name}: {state.Problem} Nothing was changed.");
        if (requireNoForeignGuards && state.ForeignGuardUnits.Count > 0)
            throw new RolloutFailure($"A Whiskers guard timer is armed on {server.Name} ({string.Join(", ", state.ForeignGuardUnits)}): " +
                                     "another renewal or allow-list change awaits confirmation. Try again once it has fired, at most 3 minutes from its start.");
        return state;
    }

    /// <summary>After a phase was recorded: removes the markers and locks of its confirmed runs (best effort; a leftover
    /// marker of a stopped guard harms nothing and the next change removes it).</summary>
    private async Task CleanupConfirmedAsync(string serverId, RolloutRun run)
    {
        foreach (var suffix in run.Confirmed.ToList())
        {
            try { await RunAsync(serverId, MtlsAllowListCommands.Cleanup(suffix), CancellationToken.None); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogDebug("Cleaning up run {Run} on {ServerId} failed: {Error}", suffix, serverId, ex.Message); }
            run.Confirmed.Remove(suffix);
        }
    }

    public async Task<MtlsLiveAllowList> ReadLiveAllowListAsync(string serverId, CancellationToken ct)
    {
        try
        {
            var output = (await RunAsync(serverId, MtlsAllowListCommands.ReadArgs(), ct)).Output.Trim();
            var space = output.IndexOf(' ');
            if (space < 0) return new(false, false, [], "unexpected output of docker inspect");
            var running = output[..space] == "true";
            var args = System.Text.Json.JsonSerializer.Deserialize<string[]>(output[(space + 1)..]) ?? [];
            return new(true, running, args.Where(a => a.StartsWith(MtlsAllowListCommands.AllowCnFlag, StringComparison.Ordinal))
                .Select(a => a[MtlsAllowListCommands.AllowCnFlag.Length..]).ToList(), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unreachable host, a refused command or unreadable output: not proven, so retiring is refused (fail closed).
            _logger.LogDebug("Reading the running allow-list of {ServerId} failed: {Error}", serverId, ex.Message);
            return new(false, false, [], ex.Message);
        }
    }

    private async Task<bool> DefaultVerifyConnectionAsync(string serverId, CancellationToken ct)
    {
        _connections?.InvalidateClient(serverId);
        try
        {
            var echo = await RunAsync(serverId, "echo ok", ct);
            if (echo.Output.Trim() != "ok") return false;
            if (_connections is not null)
                await _connections.ExecuteGuardedAsync(serverId, async client =>
                {
                    await client.System.PingAsync(ct);
                    return true;
                }, callerToken: ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("The connection to {ServerId} with the new client credential failed: {Error}", serverId, ex.Message);
            return false;
        }
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private static string? PreflightProblem(Models.ServerConfig? server)
    {
        if (server is null) return "Server not found.";
        if (!server.TcpUseTls || string.IsNullOrWhiteSpace(server.TcpHost) || string.IsNullOrWhiteSpace(server.TcpClientCertPath))
            return $"{server.Name} is not connected over mTLS with a client certificate.";
        if (!MtlsClientCredentialStore.IsValidServerId(server.Id))
            return $"The id of {server.Name} ('{server.Id}') cannot name a credential directory.";
        return null;
    }

    /// <summary>The credential the server record names, when it is a managed slot whose files exist (the record is
    /// authoritative for the active slot; amendment 6). Null otherwise.</summary>
    private static MtlsClientCredentialPaths? ConfigPaths(Models.ServerConfig server, IMtlsClientCredentialStore store, int generation) =>
        store.IsManaged(server) && File.Exists(server.TcpClientCertPath) && File.Exists(server.TcpClientKeyPath)
            ? new MtlsClientCredentialPaths(server.TcpClientCertPath!, server.TcpClientKeyPath!, store.CaPath, generation)
            : null;

    /// <summary>The generation of <paramref name="name"/> when it is this host's own name (<c>{stem}-g{n}</c>), else null.</summary>
    internal static int? OwnGeneration(string stem, string name) =>
        name.StartsWith(stem + "-g", StringComparison.Ordinal)
        && int.TryParse(name.AsSpan(stem.Length + 2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var g)
        && g >= 1
            ? g
            : null;

    private static Models.ServerConfig WithCredential(Models.ServerConfig server, MtlsClientCredentialPaths paths)
    {
        var copy = server.Clone();
        copy.TcpClientCertPath = paths.Cert;
        copy.TcpClientKeyPath = paths.Key;
        copy.TcpCaCertPath = paths.Ca;
        return copy;
    }

    private static async Task<MtlsRolloutJournal> AdvanceAsync(IMtlsClientCredentialStore store, string serverId,
        MtlsRolloutJournal journal, int phase, CancellationToken ct)
    {
        var next = journal with { Phase = Math.Max(journal.Phase, phase), UpdatedUtc = DateTime.UtcNow };
        await store.WriteJournalAsync(serverId, next, ct);
        return next;
    }

    private static MtlsClientIdentity? SafeIdentity(IMtlsClientCredentialStore store, string serverId)
    {
        try { return store.IdentityOf(serverId); }
        catch (ArgumentException) { return null; }
    }

    private static MtlsClientMigrationResult Failed(RolloutRun run, string error, MtlsClientIdentity? identity = null) =>
        new(false, run.Phase, error, identity);

    internal static string? CommonNameOf(string certPath)
    {
        try
        {
            using var cert = X509Certificate2.CreateFromPem(File.ReadAllText(certPath));
            return cert.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    private static string? ThumbprintOf(string certPath)
    {
        try
        {
            using var cert = X509Certificate2.CreateFromPem(File.ReadAllText(certPath));
            return cert.Thumbprint;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }
}
