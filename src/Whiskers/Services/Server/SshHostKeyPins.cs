using System.Collections.Concurrent;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Mutations;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Services.Server;

/// <summary>Contract 0018 part C: the pinned host key of a server's SSH endpoint (host, port). <paramref name="PinnedAtUtc"/>
/// is null for a pin backfilled from known_hosts ("pinned before 0018").</summary>
public sealed record SshHostKeyPin(string KeyType, string FingerprintSha256, SshHostKeySource Source,
    DateTime? PinnedAtUtc, string? OperationId);

/// <summary>A host key that differs from the pinned one: both fingerprints, never replaced automatically.</summary>
public sealed class SshHostKeyMismatchException(string endpoint, string keyType, string pinnedFingerprint, string servedFingerprint)
    : InvalidOperationException(
        $"The SSH host key of {endpoint} ({keyType}) changed: pinned {pinnedFingerprint}, served now {servedFingerprint}. " +
        "This happens after a rebuild, with an IP that belonged to another server, or in an attack. Nothing was replaced; " +
        "an Admin can replace the pin after comparing both fingerprints (Servers → Replace pin…).")
{
    public string Endpoint { get; } = endpoint;
    public string KeyType { get; } = keyType;
    public string PinnedFingerprint { get; } = pinnedFingerprint;
    public string ServedFingerprint { get; } = servedFingerprint;
}

/// <summary>
/// Contract 0018 part C (ADR-0002 amendment, HOCH-11, D3): pins, records, audits and replaces SSH host keys. The
/// known_hosts file and <see cref="SshHostKeyPolicy.Options"/> stay as they are (<c>accept-new</c>); onboarding pins
/// explicitly before its first SSH command, so every later SSH call meets a present entry and a changed key fails.
/// </summary>
public interface ISshHostKeyPins
{
    /// <summary>The pin recorded for the server's SSH endpoint, or null.</summary>
    SshHostKeyPin? PinOf(Models.ServerConfig server);

    /// <summary>Read-only backfill for a record without a pin: the endpoint's line in known_hosts (<c>ssh-keygen -F</c>) as
    /// a <see cref="SshHostKeySource.FirstContact"/> pin with no time. No network access, no audit event, nothing written
    /// (kept in memory for display). Null when known_hosts holds no line for the endpoint.</summary>
    Task<SshHostKeyPin?> BackfillAsync(Models.ServerConfig server, CancellationToken ct);

    /// <summary>Step <c>HostKey</c> with a recorded pin: reads the endpoint's keys with ssh-keyscan and compares them per
    /// type with known_hosts and the record. Throws <see cref="SshHostKeyMismatchException"/> on a different key.</summary>
    Task VerifyAsync(Models.ServerConfig server, CancellationToken ct);

    /// <summary>The fingerprint of the key the endpoint serves now (the record's type, else the preferred one), for the
    /// replace dialog. Read only.</summary>
    Task<(string KeyType, string Fingerprint)?> ServedAsync(Models.ServerConfig server, CancellationToken ct);

    /// <summary>Reads the endpoint's keys with ssh-keyscan, refuses when known_hosts already holds a DIFFERENT key for
    /// this endpoint, otherwise writes the line and records the pin (FirstContact). Runs only inside "server.onboard".</summary>
    [MutatingOperation("server.onboard")]
    Task<SshHostKeyPin> PinFirstContactAsync(Models.ServerConfig server, CancellationToken ct);

    /// <summary>Pins keys read from the host's own disk (rescue path, part D) BEFORE the first contact with the real
    /// system. Same refusal on a different existing key.</summary>
    [MutatingOperation("server.onboard")]
    Task<SshHostKeyPin> PinFromDiskAsync(Models.ServerConfig server, IReadOnlyList<string> publicKeyLines, CancellationToken ct);

    /// <summary>Replaces a pin. Capability "server.hostkey_replace": Admin, interactive only, with an Admin approval
    /// exactly as INV-59. Both fingerprints must match what the person saw; otherwise refused.</summary>
    [MutatingOperation("server.hostkey_replace")]
    Task<SshHostKeyPin> ReplaceAsync([MutationTarget] string serverId, string expectedOldFingerprint, string expectedNewFingerprint,
        CancellationToken ct);
}

/// <inheritdoc cref="ISshHostKeyPins" />
public sealed class SshHostKeyPins : ISshHostKeyPins
{
    /// <summary>One writer of the known_hosts file at a time (Whiskers' own lines; ssh appends its own).</summary>
    private static readonly SemaphoreSlim FileGate = new(1, 1);

    private readonly ISshToolchain _ssh;
    private readonly IServerConfigService _servers;
    private readonly ILogger<SshHostKeyPins> _logger;
    private readonly string _knownHosts;
    private readonly ConcurrentDictionary<string, SshHostKeyPin> _backfilled = new(StringComparer.Ordinal);

    public SshHostKeyPins(ISshToolchain ssh, IServerConfigService servers, ILogger<SshHostKeyPins> logger,
        DataPathOptions? paths = null)
    {
        _ssh = ssh;
        _servers = servers;
        _logger = logger;
        _knownHosts = SshHostKeyPolicy.KnownHostsFile(paths);
    }

    public SshHostKeyPin? PinOf(Models.ServerConfig server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (server.SshHostKeyType is { } type && server.SshHostKeyFingerprint is { } fp && server.SshHostKeySource is { } source)
            return new SshHostKeyPin(type, fp, source, server.SshHostKeyPinnedAtUtc, null);
        return Endpoint(server) is { } e && _backfilled.TryGetValue(e.Name, out var b) ? b : null;
    }

    public async Task<SshHostKeyPin?> BackfillAsync(Models.ServerConfig server, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (server.SshHostKeyFingerprint is not null) return PinOf(server);
        if (Endpoint(server) is not { } e) return null;
        if (_backfilled.TryGetValue(e.Name, out var known)) return known;
        var lines = await _ssh.KnownHostsLookupAsync(e.Name, _knownHosts, ct);
        var preferred = Preferred(lines);
        if (preferred is null) return null;
        var pin = new SshHostKeyPin(preferred.Value.Type, await _ssh.FingerprintAsync(preferred.Value.Line, ct),
            SshHostKeySource.FirstContact, null, null);
        _backfilled[e.Name] = pin;
        return pin;
    }

    public async Task VerifyAsync(Models.ServerConfig server, CancellationToken ct)
    {
        var e = Endpoint(server) ?? throw new InvalidOperationException($"{server.Name} has no SSH endpoint.");
        var scanned = await ScanAsync(e, ct);
        var known = await _ssh.KnownHostsLookupAsync(e.Name, _knownHosts, ct);
        await CompareAsync(e.Name, known, scanned, ct);
        if (PinOf(server) is { } pin)
        {
            // The record's type must be among what known_hosts and the host agree on, with the recorded fingerprint.
            var line = scanned.FirstOrDefault(l => SshHostKeys.Parse(l)?.Type == pin.KeyType);
            if (line is null || !known.Any(k => SshHostKeys.Parse(k)?.Type == pin.KeyType))
                throw new SshHostKeyMismatchException(e.Name, pin.KeyType, pin.FingerprintSha256, "(no such key served or pinned)");
            var served = await _ssh.FingerprintAsync(line, ct);
            if (!string.Equals(served, pin.FingerprintSha256, StringComparison.Ordinal))
                throw new SshHostKeyMismatchException(e.Name, pin.KeyType, pin.FingerprintSha256, served);
        }
    }

    public async Task<(string KeyType, string Fingerprint)?> ServedAsync(Models.ServerConfig server, CancellationToken ct)
    {
        if (Endpoint(server) is not { } e) return null;
        var scanned = await _ssh.KeyscanAsync(e.Host, e.Port, ct);
        var line = server.SshHostKeyType is { } t ? scanned.FirstOrDefault(l => SshHostKeys.Parse(l)?.Type == t) : null;
        var pick = line is not null ? (Type: server.SshHostKeyType!, Line: line) : Preferred(scanned);
        return pick is { } p ? (p.Type, await _ssh.FingerprintAsync(p.Line, ct)) : null;
    }

    public Task<SshHostKeyPin> PinFirstContactAsync(Models.ServerConfig server, CancellationToken ct) =>
        PinAsync(server, null, SshHostKeySource.FirstContact, ct);

    public Task<SshHostKeyPin> PinFromDiskAsync(Models.ServerConfig server, IReadOnlyList<string> publicKeyLines, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(publicKeyLines);
        return PinAsync(server, publicKeyLines, SshHostKeySource.RescueDisk, ct);
    }

    private async Task<SshHostKeyPin> PinAsync(Models.ServerConfig server, IReadOnlyList<string>? fromDisk,
        SshHostKeySource source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        var e = Endpoint(server) ?? throw new InvalidOperationException($"{server.Name} has no SSH endpoint.");
        var keys = fromDisk is null
            ? await ScanAsync(e, ct)
            : Normalise(fromDisk) is { Count: > 0 } disk ? disk
            : throw new InvalidOperationException("No host key was read from the host's disk; nothing is pinned.");
        var firstLines = false;
        await FileGate.WaitAsync(ct);
        try
        {
            var known = await _ssh.KnownHostsLookupAsync(e.Name, _knownHosts, ct);
            await CompareAsync(e.Name, known, keys, ct);
            var missing = keys.Where(k => !known.Any(n => SshHostKeys.Parse(n)?.Type == SshHostKeys.Parse(k)?.Type)).ToList();
            await AppendAsync(e.Name, missing, ct);
            firstLines = known.Count == 0 && missing.Count > 0;
        }
        finally
        {
            FileGate.Release();
        }
        try
        {
            return await RecordAsync(server.Id, e.Name, keys, source, "hostkey.pin", null, ct);
        }
        catch (Exception) when (firstLines)
        {
            // The record could not take the pin: the endpoint's lines this call wrote first go again, so known_hosts and
            // the record agree and a later resume is not refused as "not fresh" (D2). The failure itself still stands.
            await RollBackAsync(e.Name);
            throw;
        }
    }

    /// <summary>Removes the endpoint's known_hosts lines after a pin that could not be recorded. A failing roll-back is
    /// logged and leaves the lines (a later rescue resume is then refused, fail closed, until an Admin removes them).</summary>
    private async Task RollBackAsync(string endpoint)
    {
        await FileGate.WaitAsync(CancellationToken.None);
        try
        {
            await _ssh.KnownHostsRemoveAsync(endpoint, _knownHosts, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("known_hosts lines of {Endpoint} could not be rolled back after a failed pin: {Error}", endpoint, ex.GetType().Name);
        }
        finally
        {
            FileGate.Release();
        }
    }

    public async Task<SshHostKeyPin> ReplaceAsync(string serverId, string expectedOldFingerprint, string expectedNewFingerprint,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOldFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedNewFingerprint);
        var server = Exact(serverId) ?? throw new InvalidOperationException($"Unknown server '{serverId}'.");
        var e = Endpoint(server) ?? throw new InvalidOperationException($"{server.Name} has no SSH endpoint.");
        var scanned = await ScanAsync(e, ct);
        await FileGate.WaitAsync(ct);
        try
        {
            // What is pinned now: the record's fingerprint, or any line known_hosts holds for the endpoint.
            var known = await _ssh.KnownHostsLookupAsync(e.Name, _knownHosts, ct);
            var oldFingerprints = new HashSet<string>(StringComparer.Ordinal);
            if (server.SshHostKeyFingerprint is { } recorded) oldFingerprints.Add(recorded);
            foreach (var line in known) oldFingerprints.Add(await _ssh.FingerprintAsync(line, ct));
            if (!oldFingerprints.Contains(expectedOldFingerprint.Trim()))
                throw new InvalidOperationException("The pinned fingerprint is not the one you confirmed; nothing was replaced.");
            var preferred = Preferred(scanned) ?? throw new InvalidOperationException("The host served no key.");
            var newFingerprint = await _ssh.FingerprintAsync(preferred.Line, ct);
            if (!string.Equals(newFingerprint, expectedNewFingerprint.Trim(), StringComparison.Ordinal))
                throw new InvalidOperationException("The host serves another key than the one you confirmed; nothing was replaced.");
            await _ssh.KnownHostsRemoveAsync(e.Name, _knownHosts, ct);
            await AppendAsync(e.Name, scanned, ct);
        }
        finally
        {
            FileGate.Release();
        }
        _backfilled.TryRemove(e.Name, out _);
        return await RecordAsync(server.Id, e.Name, scanned, SshHostKeySource.Operator, "hostkey.replace",
            expectedOldFingerprint.Trim(), ct);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private sealed record EndpointRef(string Host, int Port, string Name);

    private static EndpointRef? Endpoint(Models.ServerConfig server) =>
        string.IsNullOrWhiteSpace(server.SshHost) ? null
            : new EndpointRef(server.SshHost.Trim(), server.SshPort, SshHostKeys.EndpointName(server.SshHost.Trim(), server.SshPort));

    private Models.ServerConfig? Exact(string serverId) =>
        _servers.GetServer(serverId) is { } s && string.Equals(s.Id, serverId, StringComparison.Ordinal) ? s : null;

    private async Task<IReadOnlyList<string>> ScanAsync(EndpointRef e, CancellationToken ct)
    {
        var keys = Normalise(await _ssh.KeyscanAsync(e.Host, e.Port, ct));
        return keys.Count > 0 ? keys
            : throw new InvalidOperationException($"{e.Name} served no SSH host key (ssh-keyscan); nothing is pinned.");
    }

    private static List<string> Normalise(IEnumerable<string> lines) =>
        lines.Select(SshHostKeys.Parse).Where(p => p is not null).Select(p => $"{p!.Value.Type} {p.Value.Key}")
            .Distinct(StringComparer.Ordinal).ToList();

    private static (string Type, string Line)? Preferred(IEnumerable<string> lines) =>
        lines.Select(l => (Parsed: SshHostKeys.Parse(l), Line: l)).Where(x => x.Parsed is not null)
            .OrderBy(x => SshHostKeys.Preference(x.Parsed!.Value.Type)).Select(x => ((string, string)?)(x.Parsed!.Value.Type, x.Line))
            .FirstOrDefault();

    /// <summary>Per type: every type known_hosts holds must be served with the same key. A type no longer offered is no
    /// mismatch; a different key of any type is.</summary>
    private async Task CompareAsync(string endpoint, IReadOnlyList<string> known, IReadOnlyList<string> served, CancellationToken ct)
    {
        foreach (var line in known)
        {
            if (SshHostKeys.Parse(line) is not { } k) continue;
            var other = served.FirstOrDefault(s => SshHostKeys.Parse(s)?.Type == k.Type);
            if (other is null || SshHostKeys.Parse(other)!.Value.Key == k.Key) continue;
            throw new SshHostKeyMismatchException(endpoint, k.Type, await _ssh.FingerprintAsync(line, ct),
                await _ssh.FingerprintAsync(other, ct));
        }
    }

    private async Task AppendAsync(string name, IEnumerable<string> keys, CancellationToken ct)
    {
        var lines = keys.Select(k => $"{name} {k}\n").ToList();
        if (lines.Count == 0) return;
        var dir = Path.GetDirectoryName(_knownHosts);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // Plain lines (contract 0018): Whiskers' own lines are never hashed.
        await File.AppendAllTextAsync(_knownHosts, string.Concat(lines), ct);
    }

    /// <summary>Mirrors the pin into the record (a store write of a clone of the current record) and audits it.</summary>
    private async Task<SshHostKeyPin> RecordAsync(string serverId, string endpoint, IReadOnlyList<string> keys,
        SshHostKeySource source, string action, string? oldFingerprint, CancellationToken ct)
    {
        var preferred = Preferred(keys) ?? throw new InvalidOperationException("No host key to record.");
        var fingerprint = await _ssh.FingerprintAsync(preferred.Line, ct);
        var current = Exact(serverId)?.Clone() ?? throw new InvalidOperationException($"Server '{serverId}' was removed meanwhile.");
        var pin = new SshHostKeyPin(preferred.Type, fingerprint, source, DateTime.UtcNow, MutationScope.Current?.OperationId);
        current.SshHostKeyType = pin.KeyType;
        current.SshHostKeyFingerprint = pin.FingerprintSha256;
        current.SshHostKeySource = pin.Source;
        current.SshHostKeyPinnedAtUtc = pin.PinnedAtUtc;
        await _servers.UpdateServerAsync(current);
        _backfilled.TryRemove(endpoint, out _);
        var details = oldFingerprint is null
            ? $"SSH host key of {endpoint} pinned: {pin.KeyType} {pin.FingerprintSha256} (source {pin.Source})"
            : $"SSH host key of {endpoint} replaced: old {oldFingerprint}, new {pin.KeyType} {pin.FingerprintSha256}";
        MutationScope.Record(new MutationDomainResult(action, "server", current.Id, current.Name, details, current.Id, true));
        _logger.LogInformation("{Details}", details);
        return pin;
    }
}
