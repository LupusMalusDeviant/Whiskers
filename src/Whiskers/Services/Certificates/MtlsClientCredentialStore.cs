using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Whiskers.Configuration;
using Whiskers.Services.Server;

namespace Whiskers.Services.Certificates;

/// <summary>
/// File-backed <see cref="IMtlsClientCredentialStore"/> under <c>{MtlsDir}/hosts/</c> (R1-44, contract 0007 with amendment
/// 3). Every issued certificate gets its own slot <c>g{generation}-s{serial}/</c> (0700) with <c>client.key</c> (0600) and
/// <c>client.crt</c>; <c>identity</c> records the client-name stem and the active, pending and previous slot. Keys are P-256
/// from .NET <see cref="ECDsa"/>, created with their mode at open time (never chmod-ed afterwards); every file is written
/// as <c>*.tmp</c> and renamed into place. Nothing here logs key material, PEM text or any part of it.
/// </summary>
public sealed partial class MtlsClientCredentialStore : IMtlsClientCredentialStore
{
    public const string HostsDirName = "hosts";
    public const string KeyFile = "client.key";
    public const string CertFile = "client.crt";
    public const string IdentityFile = "identity";
    public const string JournalFile = "rollout.json";

    private static readonly TimeSpan SignTimeout = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly IHostCommandExecutor _exec;
    private readonly ILogger<MtlsClientCredentialStore> _logger;
    private readonly string _mtlsDir;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _hostLocks = new(StringComparer.Ordinal);

    public SemaphoreSlim HostLock(string serverId) => _hostLocks.GetOrAdd(serverId, _ => new SemaphoreSlim(1, 1));

    private readonly Whiskers.Services.ServerConfig.IServerConfigService? _servers;

    // Contract 0015 [mTLS layer]: every key this store makes is written sealed by the vault (D4: refused without one).
    private readonly Whiskers.Services.Vault.ISealedKeyFiles? _sealedKeys;
    private readonly DataPathOptions _paths;

    public MtlsClientCredentialStore(IHostCommandExecutor exec, ILogger<MtlsClientCredentialStore> logger, DataPathOptions? dataPaths = null,
        Whiskers.Services.ServerConfig.IServerConfigService? servers = null, Whiskers.Services.Vault.ISealedKeyFiles? sealedKeys = null)
    {
        _exec = exec;
        _logger = logger;
        _servers = servers;
        _paths = dataPaths ?? DataPathOptions.Default;
        _mtlsDir = Path.GetFullPath(_paths.MtlsDir);
        _sealedKeys = sealedKeys;
    }

    private string HostsDir => Path.Combine(_mtlsDir, HostsDirName);

    /// <summary>The fleet CA bundle every connection trusts; the per-host credentials use it too.</summary>
    public string CaPath => Path.Combine(_mtlsDir, "ca.crt");

    public MtlsClientCredentialPaths SharedPaths =>
        new(Path.Combine(_mtlsDir, CertFile), Path.Combine(_mtlsDir, KeyFile), CaPath, 0);

    /// <summary>True for a server id that may name a directory: letters, digits, '.', '_' and '-', starting with a
    /// letter or digit, no "..". A hand-edited servers.json can hold anything, so every path goes through this.</summary>
    public static bool IsValidServerId(string? serverId) =>
        serverId is not null && ServerIdRegex().IsMatch(serverId) && !serverId.Contains("..", StringComparison.Ordinal);

    private string HostDir(string serverId)
    {
        if (!IsValidServerId(serverId))
            throw new ArgumentException($"The server id '{serverId}' cannot name a credential directory.", nameof(serverId));
        var dir = Path.GetFullPath(Path.Combine(HostsDir, serverId));
        if (!dir.StartsWith(HostsDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException($"The server id '{serverId}' leaves the credential directory.", nameof(serverId));
        return dir;
    }

    private MtlsClientCredentialPaths SlotPaths(string serverId, string slot)
    {
        var dir = Path.Combine(HostDir(serverId), slot);
        return new(Path.Combine(dir, CertFile), Path.Combine(dir, KeyFile), CaPath, GenerationOf(slot));
    }

    public MtlsClientCredentialPaths ProposeNewSlot(string serverId, int generation)
    {
        var dir = HostDir(serverId);
        return SlotPaths(serverId, Directory.Exists(dir) ? NextSlot(dir, generation) : $"g{generation}-s1");
    }

    public bool IsManaged(Models.ServerConfig server)
    {
        if (!IsValidServerId(server.Id) || string.IsNullOrWhiteSpace(server.TcpClientCertPath)
            || string.IsNullOrWhiteSpace(server.TcpClientKeyPath))
            return false;
        try
        {
            var cert = Path.GetFullPath(server.TcpClientCertPath);
            var certDir = Path.GetDirectoryName(cert);
            if (certDir is null || Path.GetFileName(cert) != CertFile) return false;
            var slot = Path.GetFileName(certDir);
            if (!SlotRegex().IsMatch(slot)) return false;
            var expected = SlotPaths(server.Id, slot);
            return SamePath(cert, expected.Cert) && SamePath(server.TcpClientKeyPath, expected.Key);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public bool IsShared(Models.ServerConfig server) =>
        SamePath(server.TcpClientCertPath, SharedPaths.Cert) && SamePath(server.TcpClientKeyPath, SharedPaths.Key);

    internal static bool SamePath(string? configured, string expected)
    {
        if (string.IsNullOrWhiteSpace(configured)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(configured), Path.GetFullPath(expected),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>The active identity; before the first activation, the identity of the pending slot (the name the host is
    /// being moved to). Null when the store holds no slot for the server.</summary>
    public MtlsClientIdentity? IdentityOf(string serverId) =>
        ReadState(serverId) is { } s && (s.ActiveSlot ?? s.PendingSlot) is { } slot
            ? MtlsClientIdentity.FromStem(serverId, s.Stem, GenerationOf(slot))
            : null;

    public MtlsClientIdentity? PendingIdentityOf(string serverId) =>
        ReadState(serverId) is { PendingSlot: { } p } s ? MtlsClientIdentity.FromStem(serverId, s.Stem, GenerationOf(p)) : null;

    public MtlsClientCredentialPaths? PendingFor(string serverId) =>
        ReadState(serverId) is { PendingSlot: { } p } ? SlotPaths(serverId, p) : null;

    public MtlsClientCredentialPaths? PreviousFor(string serverId) =>
        ReadState(serverId) is { PreviousSlot: { } p } && SlotPaths(serverId, p) is var paths && File.Exists(paths.Cert) && File.Exists(paths.Key)
            ? paths
            : null;

    public Task<MtlsClientCredentialPaths> IssueAsync(Models.ServerConfig server, int generation, CancellationToken ct) =>
        IssueCoreAsync(server, generation, pending: false, ct);

    public Task<MtlsClientCredentialPaths> IssuePendingAsync(Models.ServerConfig server, int generation, CancellationToken ct) =>
        IssueCoreAsync(server, generation, pending: true, ct);

    private async Task<MtlsClientCredentialPaths> IssueCoreAsync(Models.ServerConfig server, int generation, bool pending, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        var dir = HostDir(server.Id);
        var state = ReadState(server.Id);
        if (pending && state?.ActiveSlot is null)
            throw new InvalidOperationException($"'{server.Id}' has no active client credential yet; renew or rotate needs one.");
        // The stem is fixed at the first issue and only ever read back afterwards (amendments 1 and 3).
        var identity = state is null
            ? MtlsClientIdentity.For(server, generation, SlugTakenByAnother(server.Id))
            : MtlsClientIdentity.FromStem(server.Id, state.Stem, generation);
        if (state is not null && SlugTakenByAnother(server.Id)(identity.Slug))
            throw new InvalidOperationException($"Another server already holds the client name stem {identity.Stem}.");

        if (!File.Exists(CaPath))
            throw new InvalidOperationException($"The fleet CA bundle {CaPath} is missing; a client certificate cannot be checked against it.");
        // Contract 0015 D4: a key is only made when it can be stored sealed — refused before anything is signed or written.
        var sealedKeys = _sealedKeys ?? throw new InvalidOperationException("Client keys are stored sealed by the vault, and no vault is wired.");
        if (sealedKeys.RefusalReason is { } refused) throw new Whiskers.Services.Vault.SealedKeyException(refused);
        var caPem = await File.ReadAllTextAsync(CaPath, ct);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        // No SAN: a client identity is its common name.
        var request = new CertificateRequest($"CN={identity.CommonName}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid(MtlsCertificateValidator.ClientAuthOid) }, false));
        var csrB64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(request.CreateSigningRequestPem()));

        var bundlePem = await SignAsync(csrB64, identity.CommonName, ct);
        using var leaf = MtlsCertificateValidator.ValidateIssuedClient(bundlePem, caPem, key.ExportSubjectPublicKeyInfo(), identity.CommonName);

        string slot;
        await _write.WaitAsync(ct);
        try
        {
            EnsureDirectory(HostsDir);
            EnsureDirectory(dir);
            // The same proposal callers see (amendment 6); taken under the write lock so two issues never share a slot.
            var proposed = ProposeNewSlot(server.Id, generation);
            var slotDir = Path.GetDirectoryName(proposed.Cert)!;
            slot = Path.GetFileName(slotDir);
            EnsureDirectory(slotDir);
            WriteSealedKey(sealedKeys, Path.Combine(slotDir, KeyFile), key);
            WriteAtomic(Path.Combine(slotDir, CertFile), bundlePem, secret: false);

            // Every issue is pending until the server record names it and a connection through it is confirmed
            // (amendment 4, 0007-B; review r1 MINOR 1): only then does ActivatePendingAsync make it active. A pending
            // slot replaced by a newer one is no longer needed (never deleted while a record names it).
            var current = ReadState(server.Id);
            if (current?.PendingSlot is { } stale && stale != current.ActiveSlot) DeleteSlot(dir, stale);
            var next = current is null
                ? new StoredState(server.Id, identity.Stem, null, slot, null)
                : current with { PendingSlot = slot };
            WriteState(dir, next);
        }
        finally
        {
            _write.Release();
        }

        _logger.LogInformation("Issued mTLS client credential {CommonName} for server {ServerId} in slot {Slot} (valid until {NotAfter:yyyy-MM-dd} UTC)",
            identity.CommonName, server.Id, slot, leaf.NotAfter.ToUniversalTime());
        return SlotPaths(server.Id, slot);
    }

    /// <summary>Whether a slug is already held by another server's directory: two hosts must never admit the same name.</summary>
    private Func<string, bool> SlugTakenByAnother(string serverId)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(HostsDir))
            foreach (var other in Directory.EnumerateDirectories(HostsDir))
            {
                var name = Path.GetFileName(other);
                if (string.Equals(name, serverId, StringComparison.Ordinal)) continue;
                if (ReadStateFile(Path.Combine(other, IdentityFile)) is { } s && s.Stem.StartsWith(MtlsClientIdentity.Prefix, StringComparison.Ordinal))
                    taken.Add(s.Stem[MtlsClientIdentity.Prefix.Length..]);
            }
        return taken.Contains;
    }

    private static string NextSlot(string hostDir, int generation)
    {
        var serial = Directory.EnumerateDirectories(hostDir)
            .Select(Path.GetFileName)
            .Select(n => SlotRegex().Match(n ?? ""))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[2].Value))
            .DefaultIfEmpty(0)
            .Max() + 1;
        return $"g{generation}-s{serial}";
    }

    private static int GenerationOf(string slot)
    {
        var m = SlotRegex().Match(slot);
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    /// <summary>step-ca on Whiskers' own host signs the CSR (one year, leaf + intermediate) — the same call that signs
    /// the proxies' server certificates. Only the CSR goes out; the key stays here.</summary>
    private async Task<string> SignAsync(string csrB64, string commonName, CancellationToken ct)
    {
        var r = await _exec.ExecuteAsync("local", MtlsCertificateCommands.SignCsr(csrB64, commonName), SignTimeout, ct);
        if (!r.Success)
            throw new InvalidOperationException($"step-ca did not sign the client certificate (exit {r.ExitCode}).");
        try { return Encoding.ASCII.GetString(Convert.FromBase64String(r.Output.Trim())); }
        catch (FormatException) { throw new InvalidOperationException("The signed client certificate did not come back as base64."); }
    }

    public Task ActivatePendingAsync(string serverId, CancellationToken ct) => UpdateStateAsync(serverId, (_, s) =>
        s.PendingSlot is { } p
            ? s with { ActiveSlot = p, PendingSlot = null, PreviousSlot = s.ActiveSlot }
            : throw new InvalidOperationException($"'{serverId}' has no pending client credential."), ct);

    public Task DiscardPendingAsync(string serverId, CancellationToken ct) => UpdateStateAsync(serverId, (dir, s) =>
    {
        if (s.PendingSlot is { } p && p != s.ActiveSlot) DeleteSlot(dir, p);
        return s with { PendingSlot = null };
    }, ct);

    public Task DiscardPreviousAsync(string serverId, CancellationToken ct) => UpdateStateAsync(serverId, (dir, s) =>
    {
        // Every slot that is neither active nor pending goes: the previous one and any left by an interrupted run.
        foreach (var slotDir in Directory.EnumerateDirectories(dir))
        {
            var slot = Path.GetFileName(slotDir);
            if (SlotRegex().IsMatch(slot) && slot != s.ActiveSlot && slot != s.PendingSlot) DeleteSlot(dir, slot);
        }
        return s with { PreviousSlot = null };
    }, ct);

    private async Task UpdateStateAsync(string serverId, Func<string, StoredState, StoredState> change, CancellationToken ct)
    {
        var dir = HostDir(serverId);
        await _write.WaitAsync(ct);
        try
        {
            var state = ReadState(serverId) ?? throw new InvalidOperationException($"'{serverId}' has no client credential.");
            WriteState(dir, change(dir, state));
        }
        finally
        {
            _write.Release();
        }
    }

    public async Task RemoveAsync(string hostId, CancellationToken ct)
    {
        var dir = HostDir(hostId);
        await _write.WaitAsync(ct);
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        finally
        {
            _write.Release();
        }
    }

    public MtlsRolloutJournal? ReadJournal(string serverId)
    {
        var path = Path.Combine(HostDir(serverId), JournalFile);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<MtlsRolloutJournal>(File.ReadAllText(path)); }
        catch (JsonException ex)
        {
            _logger.LogWarning("The rollout journal of {ServerId} is unreadable: {Error}", serverId, ex.Message);
            return null;
        }
    }

    public async Task WriteJournalAsync(string serverId, MtlsRolloutJournal journal, CancellationToken ct)
    {
        var dir = HostDir(serverId);
        await _write.WaitAsync(ct);
        try
        {
            EnsureDirectory(HostsDir);
            EnsureDirectory(dir);
            WriteAtomic(Path.Combine(dir, JournalFile), JsonSerializer.Serialize(journal, Json), secret: false);
        }
        finally
        {
            _write.Release();
        }
    }

    public MtlsClientCredentialStatus StatusOf(Models.ServerConfig server)
    {
        if (!server.TcpUseTls || string.IsNullOrWhiteSpace(server.TcpClientCertPath))
            return new(MtlsClientCredentialKind.None, 0, null, 0, null);
        var notAfter = NotAfterOf(server.TcpClientCertPath);
        if (IsShared(server)) return new(MtlsClientCredentialKind.Shared, 0, notAfter, 0, null);
        if (!IsManaged(server)) return new(MtlsClientCredentialKind.OperatorSupplied, 0, notAfter, 0, null);
        // The record is authoritative for the slot in use (amendment 6): generation and name come from the certificate it
        // names, not from the identity file.
        var generation = GenerationOf(Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(server.TcpClientCertPath))!));
        var name = MtlsCertificateService.CommonNameOf(server.TcpClientCertPath);
        return new(MtlsClientCredentialKind.Own, generation, notAfter, ReadJournal(server.Id)?.Phase ?? 0, name);
    }

    public IReadOnlyList<string> KeyFiles()
    {
        var keys = new List<string>();
        if (File.Exists(SharedPaths.Key)) keys.Add(SharedPaths.Key);
        if (Directory.Exists(HostsDir))
            foreach (var host in Directory.EnumerateDirectories(HostsDir).Order(StringComparer.Ordinal))
                foreach (var slot in Directory.EnumerateDirectories(host).Order(StringComparer.Ordinal))
                    if (File.Exists(Path.Combine(slot, KeyFile))) keys.Add(Path.Combine(slot, KeyFile));
        return keys;
    }

    private static DateTime? NotAfterOf(string path)
    {
        try
        {
            using var cert = X509Certificate2.CreateFromPem(File.ReadAllText(path));
            return cert.NotAfter.ToUniversalTime();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    // ---- identity file ----------------------------------------------------------------------------------------------

    private StoredState? ReadState(string serverId) => ReadStateFile(Path.Combine(HostDir(serverId), IdentityFile));

    private static StoredState? ReadStateFile(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var s = JsonSerializer.Deserialize<StoredState>(File.ReadAllText(path));
            // The name is read from the store, never re-derived — but what is read must still be a valid name and slots.
            if (s is null) return null;
            if (s.ActiveSlot is not null && !SlotRegex().IsMatch(s.ActiveSlot)) return null;
            if (s.PendingSlot is not null && !SlotRegex().IsMatch(s.PendingSlot)) return null;
            if (s.PreviousSlot is not null && !SlotRegex().IsMatch(s.PreviousSlot)) return null;
            _ = MtlsClientIdentity.FromStem(s.ServerId, s.Stem, 1);
            return s;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or IOException)
        {
            return null;
        }
    }

    private static void WriteState(string hostDir, StoredState state) =>
        WriteAtomic(Path.Combine(hostDir, IdentityFile), JsonSerializer.Serialize(state, Json), secret: false);

    /// <summary>Deletes a slot — never one a server record still names (the record is authoritative, amendment 4).</summary>
    private void DeleteSlot(string hostDir, string slot)
    {
        var dir = Path.Combine(hostDir, slot);
        if (!SlotRegex().IsMatch(slot) || !Directory.Exists(dir)) return;
        var key = Path.Combine(dir, KeyFile);
        var cert = Path.Combine(dir, CertFile);
        var referenced = _servers?.GetServers().Any(s => SamePath(s.TcpClientCertPath, cert) || SamePath(s.TcpClientKeyPath, key)) ?? false;
        if (referenced)
        {
            _logger.LogWarning("Kept client credential slot {Slot}: a server record still names it", slot);
            return;
        }
        Directory.Delete(dir, recursive: true);
    }

    private static void EnsureDirectory(string dir)
    {
        if (Directory.Exists(dir)) return;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Contract 0015: the key's PEM only in a buffer that is zeroed afterwards, written sealed (tmp + rename, 0600).</summary>
    private void WriteSealedKey(Whiskers.Services.Vault.ISealedKeyFiles sealedKeys, string path, ECDsa key)
    {
        var buffer = new char[4096];
        try
        {
            if (!key.TryExportPkcs8PrivateKeyPem(buffer, out var written))
                throw new InvalidOperationException("The client key could not be exported.");
            sealedKeys.Write(path, Whiskers.Services.Vault.SealedKeyPurposes.MtlsClientKey(_paths, path), buffer.AsSpan(0, written));
        }
        finally
        {
            Array.Clear(buffer);
        }
    }

    /// <summary>Writes <paramref name="content"/> to <c>{path}.tmp</c>, created with its final mode (0600 for a key,
    /// 0644 otherwise; Unix only), and renames it over <paramref name="path"/>.</summary>
    internal static void WriteAtomic(string path, string content, bool secret)
    {
        var tmp = path + ".tmp";
        File.Delete(tmp);   // a leftover of a crash: the create mode applies only to a file this call creates
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = secret
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        var bytes = new UTF8Encoding(false).GetBytes(content);
        try
        {
            using var stream = new FileStream(tmp, options);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>The <c>identity</c> file: the client-name stem and which slot is active, pending and previous.</summary>
    private sealed record StoredState(string ServerId, string Stem, string? ActiveSlot, string? PendingSlot, string? PreviousSlot);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex ServerIdRegex();

    [GeneratedRegex("^g([1-9][0-9]{0,3})-s([1-9][0-9]{0,6})$")]
    private static partial Regex SlotRegex();
}
