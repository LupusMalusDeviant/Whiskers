using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.AuditLog;

/// <summary>The tamper-evident audit trail (R1-23, ADR-0006): every written version of an audit row is a link of one
/// SHA-256 chain, appended in the same transaction as the write; signed checkpoints, kept in the database and in a
/// file beside it, pin the chain's progress so a cut-off end is noticed too.</summary>
public interface IAuditChain
{
    /// <summary>Runs <paramref name="write"/> — which adds audit rows and returns their ids — and appends each row to
    /// the chain, in one transaction under the chain lock. Nothing is written when either fails. A changed existing row
    /// is refused here: that is <see cref="UpdateAsync{T}"/>.</summary>
    Task<T> WriteAsync<T>(MetricsDbContext db,
        Func<CancellationToken, Task<(T Result, IReadOnlyCollection<long> AuditIds)>> write, CancellationToken ct = default);

    /// <summary>Like <see cref="WriteAsync{T}"/>, for a write that changes the existing rows <paramref name="rowIds"/>
    /// (an intent turned into its outcome). Before <paramref name="write"/> runs, each of them is locked and checked
    /// against its newest link — the same row hash, a valid link signature — and the write is refused (throws) when it
    /// does not match: a row changed outside Whiskers is never turned into a new, authenticated version.</summary>
    Task<T> UpdateAsync<T>(MetricsDbContext db, IReadOnlyCollection<long> rowIds,
        Func<CancellationToken, Task<(T Result, IReadOnlyCollection<long> AuditIds)>> write, CancellationToken ct = default);

    /// <summary>On the first start with the chain: seals the rows written before it, in id order, and marks that
    /// point with a <c>genesis</c> checkpoint. Does nothing once the chain has an entry or a checkpoint.</summary>
    Task SealExistingAsync(CancellationToken ct = default);

    /// <summary>Signs the chain's current end (<c>head</c> checkpoint), in the database and in the checkpoint file.</summary>
    Task CheckpointAsync(CancellationToken ct = default);

    /// <summary>Checks every link, every row against its newest link and the chain's end against the newest trusted
    /// checkpoint.</summary>
    Task<AuditChainReport> VerifyAsync(CancellationToken ct = default);

    /// <summary>Removes the audit rows older than <paramref name="cutoffUtc"/> — recorded as an
    /// <see cref="AuditChain.RetentionAction"/> event first — and the chain prefix that only names them, and signs the
    /// new anchor (<c>retention</c> checkpoint). Returns the number of rows removed.</summary>
    Task<int> PruneAsync(DateTime cutoffUtc, int retentionDays, CancellationToken ct = default);

    /// <summary>Writes the whole trail — verification result, checkpoints, chain, rows — from one consistent snapshot
    /// to <paramref name="output"/>, which must be seekable: a retried read starts the document over.</summary>
    Task<AuditChainReport> ExportAsync(Stream output, CancellationToken ct = default);
}

/// <summary>What <see cref="IAuditChain.VerifyAsync"/> found. <see cref="Complete"/> is false when something needed
/// for the check could not be read (the checkpoint file) or key adoption is pending: no finding, but no clean result
/// either. <see cref="CheckpointsSigned"/>: at least one checkpoint was authenticated under the key;
/// <see cref="KeyConfigured"/>: VAULT_KEY is set. <see cref="Problems"/> are English sentences (logs, export);
/// <see cref="Findings"/> carry the same as codes and arguments for the UI's languages.</summary>
public sealed record AuditChainReport(
    bool Intact,
    IReadOnlyList<string> Problems,
    long RowsChecked,
    long LinksChecked,
    long HeadSeq,
    long? TrustedCheckpointSeq,
    bool CheckpointsSigned,
    bool Complete = true,
    bool KeyConfigured = false,
    IReadOnlyList<AuditFinding>? Findings = null,
    string? HeadHash = null,
    int FindingCount = 0);

/// <summary>One verification result for display: <see cref="Code"/> names the resource <c>AuditFinding_{Code}</c>.</summary>
public sealed record AuditFinding(string Code, object[] Args);

public sealed class AuditChain : IAuditChain
{
    /// <summary>The event a retention run writes before it removes anything.</summary>
    public const string RetentionAction = "audit.retention";

    /// <summary>The hash before the first link.</summary>
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    public const string HeadKind = "head";
    public const string GenesisKind = "genesis";
    public const string RetentionKind = "retention";

    /// <summary>At most this many problems are listed (the count is exact).</summary>
    public const int MaxProblems = 50;

    /// <summary>Head checkpoints kept in the database (the newest ones); older ones add nothing: a rewritten middle
    /// changes every later hash, so the newest checkpoint already catches it.</summary>
    public const int KeptHeadCheckpoints = 60;

    /// <summary>Retention checkpoints kept in the database (the newest ones); verification starts at the newest
    /// trusted one.</summary>
    public const int KeptRetentionCheckpoints = 60;

    /// <summary>At most this many rows are removed per retention run, so the chain lock is never held for long; the
    /// next daily run continues.</summary>
    public const int MaxPruneRows = 10_000;

    /// <summary>Rows sealed per round trip on the first start.</summary>
    private const int SealPage = 1_000;

    private static readonly PropertyInfo[] RowProperties = typeof(AuditLogEntity)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead)
        .OrderBy(p => p.Name, StringComparer.Ordinal)
        .ToArray();

    /// <summary>A chain for code that runs without the app's registration (tests, tools): it cannot sign.</summary>
    public static readonly AuditChain Shared = new(vaultKey: null, checkpointFile: null);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _adoptionPending;
    private readonly byte[]? _key;
    private readonly string? _checkpointFile;
    private readonly ILogger<AuditChain>? _logger;
    private readonly TimeProvider _time;
    private readonly IServiceScopeFactory? _scopes;

    /// <param name="vaultKey">The VAULT_KEY passphrase; the checkpoint key is derived from it with HKDF. Without it
    /// checkpoints are written unsigned.</param>
    /// <param name="checkpointFile">Where the newest checkpoint is kept outside the database.</param>
    /// <param name="scopes">Where the calls that bring no database of their own open one.</param>
    public AuditChain(string? vaultKey, string? checkpointFile, ILogger<AuditChain>? logger = null, TimeProvider? time = null,
        IServiceScopeFactory? scopes = null)
    {
        _scopes = scopes;
        _key = string.IsNullOrEmpty(vaultKey) ? null : DerivedKeys.GetOrAdd(vaultKey, DeriveKey);
        _checkpointFile = checkpointFile;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The app's chain from its configuration: VAULT_KEY and the data directory.</summary>
    public static AuditChain FromConfiguration(IConfiguration configuration, DataPathOptions paths, ILogger<AuditChain> logger,
        IServiceScopeFactory scopes) =>
        new(configuration["VAULT_KEY"] ?? Environment.GetEnvironmentVariable("VAULT_KEY"), paths.AuditCheckpointJson, logger,
            scopes: scopes);

    /// <summary>The checkpoint key: the passphrase stretched like the vault's (PBKDF2-SHA-256, 600,000 rounds) and
    /// then separated for this use with HKDF. The stretch matters because the signed checkpoints sit in the database:
    /// without it a leaked database would test VAULT_KEY guesses at HMAC speed.</summary>
    private static byte[] DeriveKey(string vaultKey)
    {
        var stretched = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(vaultKey),
            Encoding.UTF8.GetBytes("whiskers-audit-checkpoint-kdf"), KdfIterations, HashAlgorithmName.SHA256, 32);
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, stretched, 32,
                salt: Encoding.UTF8.GetBytes("whiskers-audit-checkpoint"), info: Encoding.UTF8.GetBytes("v1"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(stretched);
        }
    }

    /// <summary>PBKDF2 rounds of the checkpoint key (as the vault).</summary>
    public const int KdfIterations = 600_000;

    /// <summary>One stretch per passphrase and process.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> DerivedKeys = new(StringComparer.Ordinal);

    private IServiceScope Scope() =>
        (_scopes ?? throw new InvalidOperationException("This audit chain has no database of its own.")).CreateScope();

    /// <summary>The registered chain, or <see cref="Shared"/> where none is registered.</summary>
    public static IAuditChain Of(IServiceProvider services) => services.GetService<IAuditChain>() ?? Shared;

    // ---------------------------------------------------------------------------------------------- hashing

    /// <summary>SHA-256 of a row's canonical form: every column by name, nulls left out (a column added later does not
    /// change the hash of rows that never had it), timestamps in whole microseconds (what both providers keep).</summary>
    public static string RowHash(AuditLogEntity row)
    {
        var canonical = new JsonObject();
        foreach (var property in RowProperties)
        {
            var value = property.GetValue(row);
            canonical[property.Name] = value switch
            {
                null => null,
                DateTime time => JsonValue.Create(Micros(time)),
                string text => JsonValue.Create(text),
                bool flag => JsonValue.Create(flag),
                long number => JsonValue.Create(number),
                int number => JsonValue.Create(number),
                _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
            };
            if (value is null) canonical.Remove(property.Name);
        }
        return Sha256(canonical.ToJsonString(CanonicalJson));
    }

    /// <summary>Pinned, so a change of the runtime's default cannot change every row's hash: System.Text.Json's default
    /// escaping (non-ASCII and HTML-sensitive characters as upper-case <c>\uXXXX</c>), no indentation.</summary>
    private static readonly JsonSerializerOptions CanonicalJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default, WriteIndented = false,
    };

    /// <summary>The hash of a link: its predecessor, its position, the row and the row's version.</summary>
    public static string LinkHash(string prevHash, long seq, long auditId, string rowHash) =>
        Sha256(string.Create(CultureInfo.InvariantCulture, $"{prevHash}|{seq}|{auditId}|{rowHash}"));

    private static long Micros(DateTime time) =>
        (time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : time).Ticks / 10;

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The signature of a link (R1-23, round 7): HMAC-SHA-256 over <c>link|{hash}</c> under the checkpoint key —
    /// a database writer can compute a link's SHA-256 but not this, so a link added outside Whiskers is found even
    /// before the next checkpoint. Null without a key.</summary>
    private string? SignLink(string hash) =>
        _key is null ? null : Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes("link|" + hash)));

    private bool ValidLink(AuditChainEntry link) => SameMac(link.Mac, SignLink(link.Hash));

    /// <summary>A link whose fields still give its hash and whose signature (when it has one, under the key) is valid —
    /// so its <see cref="AuditChainEntry.RowHash"/> is the one that was written, not one changed in the database.</summary>
    private bool LinkHolds(AuditChainEntry link) =>
        LinkHash(link.PrevHash, link.Seq, link.AuditId, link.RowHash) == link.Hash
        && (link.Mac is null || _key is null || ValidLink(link));


    private static bool SameMac(string? given, string? expected)
    {
        if (expected is null || given is not { Length: 64 }) return false;
        var bytes = new byte[32];
        return Convert.FromHexString(given, bytes, out _, out var written) == System.Buffers.OperationStatus.Done
               && written == 32
               && CryptographicOperations.FixedTimeEquals(bytes, Convert.FromHexString(expected));
    }

    private string? Sign(AuditCheckpointEntity checkpoint) =>
        _key is null ? null : Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(Signed(checkpoint))));

    private static string Signed(AuditCheckpointEntity c) =>
        string.Create(CultureInfo.InvariantCulture, $"{c.Kind}|{c.Seq}|{c.Hash}|{c.PrunedThroughId}|{Micros(c.At)}");

    /// <summary>Whether a checkpoint's signature is right; without a key nothing is trusted as signed. A signature
    /// that is not 64 hex digits is wrong, never an error.</summary>
    private bool Valid(AuditCheckpointEntity checkpoint) => _key is not null && SameMac(checkpoint.Mac, Sign(checkpoint));

    // ---------------------------------------------------------------------------------------------- writing

    public Task<T> WriteAsync<T>(MetricsDbContext db,
        Func<CancellationToken, Task<(T Result, IReadOnlyCollection<long> AuditIds)>> write, CancellationToken ct = default) =>
        WriteAsync(db, [], write, ct);

    public Task<T> UpdateAsync<T>(MetricsDbContext db, IReadOnlyCollection<long> rowIds,
        Func<CancellationToken, Task<(T Result, IReadOnlyCollection<long> AuditIds)>> write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rowIds);
        return WriteAsync(db, rowIds, write, ct);
    }

    private async Task<T> WriteAsync<T>(MetricsDbContext db, IReadOnlyCollection<long> updates,
        Func<CancellationToken, Task<(T Result, IReadOnlyCollection<long> AuditIds)>> write, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(write);
        await _lock.WaitAsync(ct);
        try
        {
            var attempt = 0;
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                // A retry starts over: the transaction before it was rolled back.
                if (attempt++ > 0) db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var checkedRows = await CheckBeforeUpdateAsync(db, updates, ct);
                var (result, ids) = await write(ct);
                await AppendAsync(db, ids, ct, checkedRows);
                await tx.CommitAsync(ct);
                return result;
            });
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Locks each row about to be changed and checks it against its newest link: a row whose current state is
    /// not what the chain last authenticated was changed outside Whiskers, and its next version would launder that.
    /// SQLite: the write transaction already holds the database's write lock; PostgreSQL: <c>FOR UPDATE</c>.</summary>
    private async Task<HashSet<long>> CheckBeforeUpdateAsync(MetricsDbContext db, IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        var checkedRows = new HashSet<long>();
        foreach (var id in ids.Distinct().Order())
        {
            if (!db.Database.IsSqlite())
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"AuditLog\" WHERE \"Id\" = {id} FOR UPDATE", ct);
            var row = await db.AuditLog.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
            if (row is null) continue;   // nothing to change: the write will match no row
            var link = await db.AuditChain.AsNoTracking().Where(c => c.AuditId == id).OrderByDescending(c => c.Seq)
                .FirstOrDefaultAsync(ct);
            if (_adoptionPending && link is not null && link.Mac is null && LinkHolds(link) && link.RowHash == RowHash(row))
            {
                _logger?.LogWarning("Audit row {AuditId} not changed: VAULT_KEY adoption is pending, its link is not signed yet", id);
                throw new InvalidOperationException(
                    $"Audit row {id} cannot be changed while VAULT_KEY adoption is pending (its link is not signed yet).");
            }
            if (link is null || !LinkHolds(link) || link.RowHash != RowHash(row) || (_key is not null && link.Mac is null))
            {
                _logger?.LogError("AUDIT ROW CHANGED OUTSIDE WHISKERS: audit row {AuditId} does not match its newest " +
                                  "link — not writing its next version; verify the audit trail", id);
                throw new InvalidOperationException(
                    $"Audit row {id} does not match its newest link: it was changed outside Whiskers. Its next version is not written.");
            }
            checkedRows.Add(id);
        }
        return checkedRows;
    }

    /// <summary>Appends the current version of each row. Runs inside the caller's transaction, under the lock.</summary>
    private async Task AppendAsync(MetricsDbContext db, IReadOnlyCollection<long> auditIds, CancellationToken ct,
        IReadOnlySet<long>? checkedRows = null)
    {
        if (auditIds.Count == 0) return;
        await RefuseUnsignedOnSignedTrailAsync(db, ct);
        var head = await db.AuditChain.AsNoTracking().OrderByDescending(c => c.Seq)
            .Select(c => new { c.Seq, c.Hash }).FirstOrDefaultAsync(ct);
        if (head is null && !await db.AuditCheckpoints.AnyAsync(ct) && ReadFile() is { Kind: "unreadable" })
            // No chain yet and no telling whether one was here: neither seal the rows nor start beside them (fail
            // closed) — the next start with a readable file decides.
            throw new InvalidOperationException(
                "The audit checkpoint file cannot be read and the database has no chain yet: audit writes are refused until it can.");
        if (head is null && !await db.AuditCheckpoints.AnyAsync(ct) && ReadFile() is null)
        {
            // The chain's first link and no trace of an earlier chain: rows written before it (an audit write that ran
            // ahead of the start-up sealing) are sealed first, so none of them is reported as added outside it.
            await SealAllAsync(db, ct);
            head = await db.AuditChain.AsNoTracking().OrderByDescending(c => c.Seq)
                .Select(c => new { c.Seq, c.Hash }).FirstOrDefaultAsync(ct);
        }
        var anchor = head is null ? await AnchorAsync(db, ct) : (head.Seq, head.Hash);
        var (seq, hash) = anchor;
        foreach (var id in auditIds.Distinct().Order())
        {
            var row = await db.AuditLog.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
            if (row is null) continue;   // nothing was written (a conditional update that matched no row)
            var rowHash = RowHash(row);
            // A write that left the row as it was (a retried insert that found its own row) adds no link.
            var latest = await db.AuditChain.AsNoTracking().Where(c => c.AuditId == id)
                .OrderByDescending(c => c.Seq).Select(c => c.RowHash).FirstOrDefaultAsync(ct);
            if (latest == rowHash) continue;
            // A new version of an existing row only through UpdateAsync, which checked the row before it was changed.
            if (latest is not null && checkedRows?.Contains(id) != true)
                throw new InvalidOperationException(
                    $"Audit row {id} already has a link: a change of an existing row goes through UpdateAsync.");
            seq++;
            var link = new AuditChainEntry
            {
                Seq = seq, AuditId = id, RowHash = rowHash, PrevHash = hash,
                Hash = LinkHash(hash, seq, id, rowHash), At = _time.GetUtcNow().UtcDateTime,
            };
            link.Mac = SignLink(link.Hash);
            db.AuditChain.Add(link);
            hash = link.Hash;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A trail that was signed is not continued without the key (fail closed): an unsigned link there is
    /// indistinguishable from one added outside Whiskers. The audit write fails, so the gateway refuses the mutation
    /// until VAULT_KEY is set again. Not cached: the checkpoint table is small, and the answer belongs to the database
    /// at hand.</summary>
    private async Task RefuseUnsignedOnSignedTrailAsync(MetricsDbContext db, CancellationToken ct)
    {
        if (_key is not null) return;
        if (ReadFile()?.Mac is not null || await db.AuditCheckpoints.AnyAsync(c => c.Mac != null, ct))
            throw new InvalidOperationException("The audit trail is signed but VAULT_KEY is not set: audit writes are refused until it is.");
    }

    /// <summary>Where the chain starts when it has no link: the newest retention anchor, else the genesis hash.</summary>
    private static async Task<(long Seq, string Hash)> AnchorAsync(MetricsDbContext db, CancellationToken ct)
    {
        var anchor = await db.AuditCheckpoints.AsNoTracking().Where(c => c.Kind == RetentionKind)
            .OrderByDescending(c => c.Seq).ThenByDescending(c => c.Id).FirstOrDefaultAsync(ct);
        return anchor is null ? (0, Genesis) : (anchor.Seq, anchor.Hash);
    }

    public async Task SealExistingAsync(CancellationToken ct = default)
    {
        using var scope = Scope();
        await SealExistingAsync(scope.ServiceProvider.GetRequiredService<MetricsDbContext>(), ct);
    }

    internal async Task SealExistingAsync(MetricsDbContext db, CancellationToken ct)
    {
        if (await db.AuditChain.AnyAsync(ct) || await db.AuditCheckpoints.AnyAsync(ct))
        {
            await AdoptKeyAsync(db, ct);
            return;
        }
        // A chain that was here once is not started again: emptied chain tables beside a checkpoint file are a
        // tampered trail, and sealing its rows now would hide that. Verification reports it.
        if (ReadFile() is { } present)
        {
            if (present.Kind == "unreadable")
                _logger?.LogError("AUDIT CHAIN NOT STARTED: the checkpoint file {Path} cannot be read — audit writes are " +
                                  "refused until a start can read it; check the file's permissions", _checkpointFile);
            else
                _logger?.LogError("AUDIT CHAIN MISSING: the checkpoint file {Path} exists but the database has no chain — " +
                                  "not starting a new one; verify the audit trail", _checkpointFile);
            return;
        }
        var sealedRows = await WriteAsync(db, async token => (await SealAllAsync(db, token), Array.Empty<long>()), ct);
        await CheckpointAsync(db, GenesisKind, ct);
        _logger?.LogInformation("Audit chain started: {Count} existing audit rows sealed (genesis)", sealedRows);
    }

    /// <summary>Seals every row of a database without a chain, in id order, a page at a time. Inside the caller's
    /// transaction, under the lock.</summary>
    private async Task<int> SealAllAsync(MetricsDbContext db, CancellationToken ct)
    {
        await RefuseUnsignedOnSignedTrailAsync(db, ct);
        var (seq, hash) = await AnchorAsync(db, ct);
        long after = 0;
        var count = 0;
        while (true)
        {
            var page = await db.AuditLog.AsNoTracking().Where(a => a.Id > after).OrderBy(a => a.Id).Take(SealPage)
                .ToListAsync(ct);
            if (page.Count == 0) return count;
            foreach (var row in page)
            {
                seq++;
                var rowHash = RowHash(row);
                var link = new AuditChainEntry
                {
                    Seq = seq, AuditId = row.Id, RowHash = rowHash, PrevHash = hash,
                    Hash = LinkHash(hash, seq, row.Id, rowHash), At = _time.GetUtcNow().UtcDateTime,
                };
                link.Mac = SignLink(link.Hash);
                db.AuditChain.Add(link);
                hash = link.Hash;
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            after = page[^1].Id;
            count += page.Count;
        }
    }

    /// <summary>The first start with VAULT_KEY on a trail that never had one: nothing anywhere carries a signature, so
    /// the newest (unsigned) retention anchor is signed once — the same moment of trust as a genesis. Without it a
    /// trail pruned before the key was set would no longer reach its start. Never when any checkpoint, in the database
    /// or in the file, carries a signature: then the trail was signed before, and only those are trusted.</summary>
    private async Task AdoptKeyAsync(MetricsDbContext db, CancellationToken ct)
    {
        if (_key is null) return;
        var file = ReadFile();
        if (file is not null && file.Mac is not null) return;
        if (await db.AuditCheckpoints.AnyAsync(c => c.Mac != null, ct)) return;
        if (file is { Kind: "unreadable" })
        {
            // Cannot tell whether the trail was signed before. Sign nothing until a start that can: a checkpoint
            // signed now would make adoption impossible for good.
            _adoptionPending = true;
            _logger?.LogError("AUDIT KEY ADOPTION PENDING: {Path} cannot be read — no checkpoint is signed until it can",
                _checkpointFile);
            return;
        }
        // The signed anchor and the genesis boundary in one transaction: a start that stopped between them would leave
        // a signed trail without its boundary — adoption skipped for good, the older unsigned links reported.
        AuditCheckpointEntity? genesis = null;
        await _lock.WaitAsync(ct);
        try
        {
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var anchor = await db.AuditCheckpoints.AsNoTracking().Where(c => c.Kind == RetentionKind)
                    .OrderByDescending(c => c.Seq).ThenByDescending(c => c.Id).FirstOrDefaultAsync(ct);
                if (anchor is not null)
                {
                    var signed = new AuditCheckpointEntity
                    {
                        Kind = RetentionKind, Seq = anchor.Seq, Hash = anchor.Hash, PrunedThroughId = anchor.PrunedThroughId,
                        At = Truncate(_time.GetUtcNow().UtcDateTime),
                    };
                    signed.Mac = Sign(signed);
                    db.AuditCheckpoints.Add(signed);
                    await db.SaveChangesAsync(ct);
                }
                // The links written before the key are signed now, a page at a time — the same moment of trust as the
                // genesis below: from here on every link of the trail carries a signature, so none of them can be
                // recomputed in the database unnoticed.
                var from = anchor?.Seq ?? 0;
                while (true)
                {
                    var page = await db.AuditChain.Where(c => c.Seq > from && c.Mac == null).OrderBy(c => c.Seq)
                        .Take(SealPage).ToListAsync(ct);
                    if (page.Count == 0) break;
                    foreach (var link in page) link.Mac = SignLink(link.Hash);
                    await db.SaveChangesAsync(ct);
                    db.ChangeTracker.Clear();
                    from = page[^1].Seq;
                }
                // A genesis checkpoint (never thinned): the trail's end at the moment of trust.
                var head = await db.AuditChain.AsNoTracking().OrderByDescending(c => c.Seq)
                    .Select(c => new { c.Seq, c.Hash }).FirstOrDefaultAsync(ct);
                var (seq, hash) = head is null ? await AnchorAsync(db, ct) : (head.Seq, head.Hash);
                genesis = new AuditCheckpointEntity
                {
                    Kind = GenesisKind, Seq = seq, Hash = hash, At = Truncate(_time.GetUtcNow().UtcDateTime),
                    PrunedThroughId = await TrustedPrunedThroughAsync(db, ct),
                };
                genesis.Mac = Sign(genesis);
                db.AuditCheckpoints.Add(genesis);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            });
            if (genesis is not null && await FileMayBeReplacedAsync(db, ct)) WriteFile(genesis);
        }
        finally
        {
            _lock.Release();
        }
        _logger?.LogInformation("Audit chain: VAULT_KEY adopted, checkpoints and links are signed from now on");
    }

    public async Task CheckpointAsync(CancellationToken ct = default)
    {
        using var scope = Scope();
        await CheckpointAsync(scope.ServiceProvider.GetRequiredService<MetricsDbContext>(), HeadKind, ct);
    }

    internal async Task CheckpointAsync(MetricsDbContext db, string kind, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var head = await db.AuditChain.AsNoTracking().OrderByDescending(c => c.Seq)
                .Select(c => new { c.Seq, c.Hash }).FirstOrDefaultAsync(ct);
            var (seq, hash) = head is null ? await AnchorAsync(db, ct) : (head.Seq, head.Hash);
            var newest = await db.AuditCheckpoints.AsNoTracking().OrderByDescending(c => c.Seq)
                .ThenByDescending(c => c.Id).FirstOrDefaultAsync(ct);
            if (_adoptionPending)
            {
                _logger?.LogWarning("Audit checkpoint not signed: VAULT_KEY adoption is pending (checkpoint file unreadable at start)");
                return;
            }
            if (kind == HeadKind && newest is not null && newest.Seq == seq && newest.Hash == hash
                && (_key is null || Valid(newest)))
            {
                // Nothing new — but the file must name it too (a retention checkpoint is signed in the database first).
                if ((ReadFile() is not { } file || file.Seq != newest.Seq || file.Hash != newest.Hash)
                    && await FileMayBeReplacedAsync(db, ct))
                    WriteFile(newest);
                return;
            }
            var written = new AuditCheckpointEntity
            {
                Kind = kind, Seq = seq, Hash = hash, At = Truncate(_time.GetUtcNow().UtcDateTime),
                PrunedThroughId = await TrustedPrunedThroughAsync(db, ct),
            };
            written.Mac = Sign(written);
            db.AuditCheckpoints.Add(written);
            await db.SaveChangesAsync(ct);
            if (kind == HeadKind)
            {
                // Without the key only unsigned heads are thinned: a signed one is evidence this process cannot renew.
                var keyless = _key is null;
                var cut = await db.AuditCheckpoints.Where(c => c.Kind == HeadKind && (!keyless || c.Mac == null))
                    .OrderByDescending(c => c.Id).Skip(KeptHeadCheckpoints).Select(c => (long?)c.Id).FirstOrDefaultAsync(ct);
                if (cut is { } id)
                    await db.AuditCheckpoints.Where(c => c.Kind == HeadKind && (!keyless || c.Mac == null) && c.Id <= id)
                        .ExecuteDeleteAsync(ct);
            }
            // Under the lock: a slower, older checkpoint never overwrites a newer one in the file.
            if (await FileMayBeReplacedAsync(db, ct)) WriteFile(written);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>How far retention removed rows, from the newest retention checkpoint this chain trusts (signed, or any
    /// without a key) — never copied from an unchecked row.</summary>
    private async Task<long> TrustedPrunedThroughAsync(MetricsDbContext db, CancellationToken ct)
    {
        var keyed = _key is not null;
        var starts = await db.AuditCheckpoints.AsNoTracking().Where(c => c.Kind == RetentionKind && (!keyed || c.Mac != null))
            .OrderByDescending(c => c.Seq).ThenByDescending(c => c.Id).Take(KeptRetentionCheckpoints).ToListAsync(ct);
        return starts.FirstOrDefault(c => !keyed || Valid(c))?.PrunedThroughId ?? 0;
    }

    /// <summary>Whether the checkpoint file may be replaced: only while the database trail still reaches what the file
    /// names — the same hash at its link, or a trusted retention start at or past it. A database cut short or rebuilt
    /// below the file must never be signed over it: the file is what shows it. Under the lock.</summary>
    private async Task<bool> FileMayBeReplacedAsync(MetricsDbContext db, CancellationToken ct)
    {
        var file = ReadFile();
        if (file is null) return true;
        string? reason = null;
        if (file.Kind == "unreadable") reason = "it cannot be read";
        else if (_key is null && file.Mac is not null) reason = "it is signed and VAULT_KEY is not set";
        else if (_key is not null && file.Mac is not null && !Valid(file)) reason = "its signature does not match VAULT_KEY";
        else if (file.Seq == 0 && file.Hash == Genesis) return true;
        else if (await db.AuditChain.AsNoTracking().AnyAsync(c => c.Seq == file.Seq && c.Hash == file.Hash, ct)) return true;
        else
        {
            var starts = await db.AuditCheckpoints.AsNoTracking().Where(c => c.Kind == RetentionKind && c.Seq >= file.Seq)
                .ToListAsync(ct);
            if (starts.Any(c => (_key is null || Valid(c)) && (c.Seq > file.Seq || c.Hash == file.Hash))) return true;
            reason = string.Create(CultureInfo.InvariantCulture,
                $"the database trail no longer reaches its checkpoint at link {file.Seq}");
        }
        _logger?.LogError("AUDIT CHECKPOINT FILE KEPT: {Path} is not replaced because {Reason} — verify the audit trail",
            _checkpointFile, reason);
        return false;
    }

    private static DateTime Truncate(DateTime time) => new(Micros(time) * 10, DateTimeKind.Utc);

    /// <summary>Keeps the newest checkpoint beside the database: a database-only edit cannot remove it.</summary>
    private void WriteFile(AuditCheckpointEntity checkpoint)
    {
        if (_checkpointFile is null) return;
        try
        {
            var dir = Path.GetDirectoryName(_checkpointFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _checkpointFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(checkpoint));
            File.Move(tmp, _checkpointFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogError(ex, "AUDIT CHECKPOINT FILE NOT WRITTEN: {Path}", _checkpointFile);
        }
    }

    private AuditCheckpointEntity? ReadFile()
    {
        if (_checkpointFile is null) return null;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return JsonSerializer.Deserialize<AuditCheckpointEntity>(File.ReadAllText(_checkpointFile))
                       ?? throw new JsonException("empty checkpoint file");
            }
            // Only a confirmed absence is "no file"; a denied access or any other failure is "cannot be read" — never
            // a reason to verify without it (File.Exists answers false for both).
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(50);   // the file is being replaced (a checkpoint is written right now)
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "Audit checkpoint file unreadable: {Path}", _checkpointFile);
                return new AuditCheckpointEntity { Kind = "unreadable", Seq = long.MaxValue };
            }
        }
    }

    // ---------------------------------------------------------------------------------------------- verifying

    public async Task<AuditChainReport> VerifyAsync(CancellationToken ct = default)
    {
        using var scope = Scope();
        return await VerifyAsync(scope.ServiceProvider.GetRequiredService<MetricsDbContext>(), ct);
    }

    internal async Task<AuditChainReport> VerifyAsync(MetricsDbContext db, CancellationToken ct)
    {
        // The file first: it is written after its checkpoint is committed, so the snapshot read next reaches it.
        var file = ReadFile();
        return await SnapshotAsync(db, () => VerifyAsync(db, file, ct), ct);
    }

    /// <summary>Runs <paramref name="read"/> in one read snapshot: a write that commits meanwhile is either wholly in
    /// it or wholly out, so a row is never seen without its link. SQLite: a deferred transaction (an immediate one
    /// would hold off every writer while the read runs).</summary>
    private static async Task<T> SnapshotAsync<T>(MetricsDbContext db, Func<Task<T>> read, CancellationToken ct) =>
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            if (!db.Database.IsSqlite())
            {
                await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
                return await read();
            }
            await db.Database.OpenConnectionAsync(ct);
            try
            {
                var connection = (Microsoft.Data.Sqlite.SqliteConnection)db.Database.GetDbConnection();
                await using var tx = connection.BeginTransaction(deferred: true);
                await db.Database.UseTransactionAsync(tx, ct);
                try
                {
                    return await read();
                }
                finally
                {
                    await db.Database.UseTransactionAsync(null, ct);
                }
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        });

    private async Task<AuditChainReport> VerifyAsync(MetricsDbContext db, AuditCheckpointEntity? file, CancellationToken ct)
    {
        var problems = new List<string>();
        var findings = new List<AuditFinding>();
        var count = 0;
        void Problem(string code, string text, params object[] args)
        {
            if (count++ >= MaxProblems) return;
            problems.Add(text);
            findings.Add(new AuditFinding(code, args));
        }

        // Checkpoints: only a valid signature is trusted. The file holds the newest one outside the database.
        var checkpoints = await db.AuditCheckpoints.AsNoTracking().OrderBy(c => c.Seq).ThenBy(c => c.Id).ToListAsync(ct);
        var complete = true;
        if (file is not null)
        {
            if (file.Kind == "unreadable")
            {
                complete = false;
                problems.Add("The checkpoint file cannot be read: the end of the trail could not be checked against it.");
                findings.Add(new AuditFinding("FileUnreadable", []));
            }
            else checkpoints.Add(file);
        }
        if (_adoptionPending)
        {
            complete = false;
            problems.Add("VAULT_KEY adoption is pending: nothing is signed until the checkpoint file can be read at a start.");
            findings.Add(new AuditFinding("AdoptionPending", []));
        }
        var trusted = new List<AuditCheckpointEntity>();
        foreach (var checkpoint in checkpoints)
        {
            if (_key is null) continue;
            if (Valid(checkpoint)) trusted.Add(checkpoint);
            // An unsigned checkpoint was written while VAULT_KEY was not set: not trusted, but no sign of tampering —
            // nulling a signature gains nothing, the file keeps the newest signed checkpoint.
            else if (checkpoint.Kind != "unreadable" && checkpoint.Mac is not null)
                Problem("CheckpointSignature", $"Checkpoint at link {checkpoint.Seq} ({checkpoint.Kind}) has an invalid signature.",
                    checkpoint.Seq, checkpoint.Kind);
        }

        // Once the trail is signed, an unsigned retention checkpoint newer than every trusted one was not written by
        // Whiskers: a process without the key never prunes a signed trail.
        if (trusted.Count > 0)
        {
            var newestTrusted = trusted.Max(c => c.Seq);
            foreach (var planted in checkpoints.Where(c => c.Kind == RetentionKind && c.Mac is null && c.Seq > newestTrusted))
                Problem("RetentionUnsigned", $"Retention checkpoint at link {planted.Seq} is not signed: it was added outside Whiskers.",
                    planted.Seq);
        }

        // The anchor: the newest signed retention checkpoint (unsigned ones are taken only when nothing can be signed).
        var retention = (_key is null ? checkpoints : trusted).Where(c => c.Kind == RetentionKind)
            .OrderByDescending(c => c.Seq).ThenByDescending(c => c.PrunedThroughId).FirstOrDefault();
        var (anchorSeq, running) = retention is null ? (0L, Genesis) : (retention.Seq, retention.Hash);
        var prunedThrough = retention?.PrunedThroughId ?? 0;

        // Links after the anchor, in order: each continues the one before. Once the trail is signed (a trusted
        // checkpoint exists), every link carries a valid signature — the links from before the key were signed when it
        // was adopted.
        var signedFrom = trusted.Count == 0 ? long.MaxValue : anchorSeq;
        var latest = new Dictionary<long, string>();
        var checkedAt = trusted.Select(c => c.Seq).ToHashSet();
        var hashAt = new Dictionary<long, string> { [anchorSeq] = running };
        long links = 0, head = anchorSeq;
        await foreach (var link in db.AuditChain.AsNoTracking().Where(c => c.Seq > anchorSeq).OrderBy(c => c.Seq)
                           .AsAsyncEnumerable().WithCancellation(ct))
        {
            links++;
            if (link.PrevHash != running)
                Problem("LinkBroken", $"Link {link.Seq} does not continue the link before it (a link was removed or changed).", link.Seq);
            if (LinkHash(link.PrevHash, link.Seq, link.AuditId, link.RowHash) != link.Hash)
                Problem("LinkChanged", $"Link {link.Seq} was changed.", link.Seq);
            if (link.Mac is not null && _key is not null && !ValidLink(link))
                Problem("LinkSignature", $"Link {link.Seq} has an invalid signature.", link.Seq);
            else if (link.Mac is null && link.Seq > signedFrom)
                Problem("LinkUnsigned", $"Link {link.Seq} is not signed: it was added outside Whiskers.", link.Seq);
            running = link.Hash;
            head = link.Seq;
            if (checkedAt.Contains(link.Seq)) hashAt[link.Seq] = link.Hash;
            latest[link.AuditId] = link.RowHash;
        }

        // The chain must reach every trusted checkpoint, with the same hash there.
        var newest = trusted.Where(c => c.Seq >= anchorSeq).OrderByDescending(c => c.Seq).FirstOrDefault();
        foreach (var checkpoint in trusted.Where(c => c.Seq >= anchorSeq))
        {
            if (checkpoint.Seq > head)
                Problem("CutOff", $"The trail ends at link {head}, before the checkpoint at link {checkpoint.Seq}: its end was cut off.",
                    head, checkpoint.Seq);
            else if (!hashAt.TryGetValue(checkpoint.Seq, out var at) || at != checkpoint.Hash)
                Problem("CheckpointMismatch", $"The trail does not match the checkpoint at link {checkpoint.Seq}.", checkpoint.Seq);
        }

        // Every row against its newest link; every link's row must still be there unless retention removed it.
        long rows = 0;
        await foreach (var row in db.AuditLog.AsNoTracking().OrderBy(a => a.Id).AsAsyncEnumerable().WithCancellation(ct))
        {
            rows++;
            if (!latest.Remove(row.Id, out var expected))
                Problem("RowUnlinked", $"Audit row {row.Id} has no link: it was added outside the trail.", row.Id);
            else if (expected != RowHash(row))
                Problem("RowChanged", $"Audit row {row.Id} was changed after it was written.", row.Id);
        }
        foreach (var missing in latest.Keys.Where(id => id > prunedThrough).Order())
            Problem("RowDeleted", $"Audit row {missing} was deleted.", missing);

        if (count > MaxProblems)
        {
            problems.Add($"… and {count - MaxProblems} more.");
            findings.Add(new AuditFinding("More", [count - MaxProblems]));
        }
        return new AuditChainReport(count == 0, problems, rows, links, head, newest?.Seq, newest is not null, complete,
            _key is not null, findings, running, count);
    }

    // ---------------------------------------------------------------------------------------------- export

    public async Task<AuditChainReport> ExportAsync(Stream output, CancellationToken ct = default)
    {
        using var scope = Scope();
        return await ExportAsync(scope.ServiceProvider.GetRequiredService<MetricsDbContext>(), output, ct);
    }

    internal async Task<AuditChainReport> ExportAsync(MetricsDbContext db, Stream output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanSeek || !output.CanWrite) throw new ArgumentException("The export needs a seekable stream.", nameof(output));
        var exportedUtc = _time.GetUtcNow().UtcDateTime;
        var file = ReadFile();
        return await SnapshotAsync(db, async () =>
        {
            output.SetLength(0);   // a retried read starts the document over
            var report = await VerifyAsync(db, file, ct);
            await AuditExport.WriteAsync(db, report, file, output, exportedUtc, ct);
            return report;
        }, ct);
    }

    // ---------------------------------------------------------------------------------------------- retention

    public async Task<int> PruneAsync(DateTime cutoffUtc, int retentionDays, CancellationToken ct = default)
    {
        using var scope = Scope();
        return await PruneAsync(scope.ServiceProvider.GetRequiredService<MetricsDbContext>(), cutoffUtc, retentionDays, ct);
    }

    internal async Task<int> PruneAsync(MetricsDbContext db, DateTime cutoffUtc, int retentionDays, CancellationToken ct)
    {
        // A trail that was signed is not cut without the key: its new start could not be signed, and the trail would
        // no longer verify once the key is back.
        if (_key is null && (ReadFile()?.Mac is not null || await db.AuditCheckpoints.AnyAsync(c => c.Mac != null, ct)))
        {
            _logger?.LogError("AUDIT RETENTION SKIPPED: the trail is signed but VAULT_KEY is not set");
            return 0;
        }
        if (_adoptionPending)
        {
            _logger?.LogError("AUDIT RETENTION SKIPPED: VAULT_KEY adoption is pending");
            return 0;
        }
        // Anything due at all? Then the whole trail is verified first — outside the chain lock, in one snapshot, so
        // audit writers (and with them every mutation) are not held up for a scan of the whole trail.
        if (!await db.AuditLog.AnyAsync(a => a.Timestamp < cutoffUtc, ct)
            && !await db.McpToolCalls.AnyAsync(e => e.Timestamp < cutoffUtc, ct))
            return 0;
        // Nothing is removed from, nor signed over, a trail that does not verify: retention would otherwise sign a
        // tampered prefix end as the new start, and drop rows whose timestamps were backdated.
        var verified = await VerifyAsync(db, ct);
        if (!verified.Intact || !verified.Complete)
        {
            _logger?.LogError("AUDIT RETENTION SKIPPED: the trail does not verify ({Problems}) — nothing removed",
                string.Join("; ", verified.Problems.Take(5)));
            return 0;
        }

        AuditCheckpointEntity? written = null;
        var removed = await WriteAsync(db, async token =>
        {
            db.ChangeTracker.Clear();
            // Up to the first row that is still young: a row is never removed before its time, also where ids and
            // timestamps are not in the same order.
            var firstYoung = await db.AuditLog.Where(a => a.Timestamp >= cutoffUtc).OrderBy(a => a.Id)
                .Select(a => (long?)a.Id).FirstOrDefaultAsync(token);
            var through = firstYoung is { } young
                ? young - 1
                : await db.AuditLog.MaxAsync(a => (long?)a.Id, token) ?? 0;
            if (await db.AuditLog.Where(a => a.Id <= through).OrderBy(a => a.Id).Skip(MaxPruneRows - 1)
                    .Select(a => (long?)a.Id).FirstOrDefaultAsync(token) is { } batchEnd)
                through = batchEnd;
            var count = await db.AuditLog.CountAsync(a => a.Id <= through, token);
            // The MCP call history in batches too, by id.
            var historyThrough = await db.McpToolCalls.Where(e => e.Timestamp < cutoffUtc).OrderBy(e => e.Id)
                .Skip(MaxPruneRows - 1).Select(e => (long?)e.Id).FirstOrDefaultAsync(token) ?? long.MaxValue;
            var history = await db.McpToolCalls.CountAsync(e => e.Timestamp < cutoffUtc && e.Id <= historyThrough, token);
            if (count == 0 && history == 0) return (0, Array.Empty<long>());

            // What may have changed since the verification, checked again under the lock: bounded by this batch and
            // the links written meanwhile.
            if (!await UnchangedSinceAsync(db, verified, through, token))
            {
                _logger?.LogError("AUDIT RETENTION SKIPPED: the trail changed outside Whiskers since it was verified — nothing removed");
                return (0, Array.Empty<long>());
            }

            // The removal is an event of its own, sealed before anything goes.
            var record = new AuditLogEntity
            {
                Timestamp = _time.GetUtcNow().UtcDateTime, Actor = "system", ActorType = "system",
                Action = RetentionAction, TargetType = "audit", TargetId = "audit-log", TargetName = "Audit log",
                Details = string.Create(CultureInfo.InvariantCulture,
                    $"Removed {count} audit row(s) through id {through} and {history} MCP call history row(s), older than {cutoffUtc:O} (retention {retentionDays} days)."),
                Success = true,
            };
            db.AuditLog.Add(record);
            await db.SaveChangesAsync(token);
            await AppendAsync(db, [record.Id], token);
            db.ChangeTracker.Clear();

            await db.McpToolCalls.Where(e => e.Timestamp < cutoffUtc && e.Id <= historyThrough).ExecuteDeleteAsync(token);
            if (count == 0) return (0, Array.Empty<long>());   // only history went: the trail keeps its start
            await db.AuditLog.Where(a => a.Id <= through).ExecuteDeleteAsync(token);
            // The new anchor: the end of the chain prefix that names only removed rows.
            var firstKept = await db.AuditChain.Where(c => c.AuditId > through).MinAsync(c => (long?)c.Seq, token);
            var anchor = await db.AuditChain.AsNoTracking().Where(c => firstKept == null || c.Seq < firstKept)
                .OrderByDescending(c => c.Seq).FirstOrDefaultAsync(token);
            var (seq, hash) = anchor is null ? await AnchorAsync(db, token) : (anchor.Seq, anchor.Hash);
            if (anchor is not null) await db.AuditChain.Where(c => c.Seq <= anchor.Seq).ExecuteDeleteAsync(token);
            // Head checkpoints before the new start name links that are gone; genesis and the newest retention
            // checkpoints stay.
            await db.AuditCheckpoints.Where(c => c.Kind == HeadKind && c.Seq < seq).ExecuteDeleteAsync(token);
            written = new AuditCheckpointEntity
            {
                Kind = RetentionKind, Seq = seq, Hash = hash, PrunedThroughId = through,
                At = Truncate(_time.GetUtcNow().UtcDateTime),
            };
            written.Mac = Sign(written);
            db.AuditCheckpoints.Add(written);
            await db.SaveChangesAsync(token);
            var oldest = await db.AuditCheckpoints.Where(c => c.Kind == RetentionKind).OrderByDescending(c => c.Id)
                .Skip(KeptRetentionCheckpoints).Select(c => (long?)c.Id).FirstOrDefaultAsync(token);
            if (oldest is { } cut)
                await db.AuditCheckpoints.Where(c => c.Kind == RetentionKind && c.Id <= cut).ExecuteDeleteAsync(token);
            return (count, Array.Empty<long>());
        }, ct);
        // Under the lock: brings the file up to the new start (and anything written since).
        if (written is not null) await CheckpointAsync(db, HeadKind, ct);
        return removed;
    }

    /// <summary>Under the lock, after a verification outside it: everything retention is about to remove or sign over
    /// still holds. The links written since continue the verified head; the chain prefix from the trusted start up to
    /// the batch's end is continuous, each link holding and — under the key — signed; every row of the batch matches
    /// its newest link; and no linked row of the batch's range is missing (a row deleted meanwhile is not excused by
    /// this retention). Bounded by the batch and the links written meanwhile.</summary>
    internal async Task<bool> UnchangedSinceAsync(MetricsDbContext db, AuditChainReport verified, long through, CancellationToken ct)
    {
        bool Holds(AuditChainEntry link) => LinkHolds(link) && (_key is null || link.Mac is not null);

        var running = verified.HeadHash;
        var atHead = await db.AuditChain.AsNoTracking().Where(c => c.Seq == verified.HeadSeq).Select(c => c.Hash)
            .FirstOrDefaultAsync(ct) ?? (await AnchorAsync(db, ct)) switch
            {
                var (seq, hash) when seq == verified.HeadSeq => hash,
                _ => null,
            };
        if (running is null || atHead != running) return false;
        await foreach (var link in db.AuditChain.AsNoTracking().Where(c => c.Seq > verified.HeadSeq).OrderBy(c => c.Seq)
                           .AsAsyncEnumerable().WithCancellation(ct))
        {
            if (link.PrevHash != running || !Holds(link)) return false;
            running = link.Hash;
        }

        // The prefix from the trusted start, up to the first link naming a row that stays.
        var keyed = _key is not null;
        var start = await db.AuditCheckpoints.AsNoTracking().Where(c => c.Kind == RetentionKind && (!keyed || c.Mac != null))
            .OrderByDescending(c => c.Seq).ThenByDescending(c => c.Id).Take(KeptRetentionCheckpoints).ToListAsync(ct);
        var trustedStart = start.FirstOrDefault(c => _key is null || Valid(c));
        var (anchorSeq, prev) = trustedStart is null ? (0L, Genesis) : (trustedStart.Seq, trustedStart.Hash);
        var prunedBefore = trustedStart?.PrunedThroughId ?? 0;
        await foreach (var link in db.AuditChain.AsNoTracking().Where(c => c.Seq > anchorSeq).OrderBy(c => c.Seq)
                           .AsAsyncEnumerable().WithCancellation(ct))
        {
            if (link.AuditId > through) break;
            if (link.PrevHash != prev || !Holds(link)) return false;
            prev = link.Hash;
        }

        // Every row of the batch against its newest link; every linked row of the range still there.
        var latest = new Dictionary<long, AuditChainEntry>();
        await foreach (var link in db.AuditChain.AsNoTracking().Where(c => c.AuditId <= through && c.AuditId > prunedBefore)
                           .OrderBy(c => c.Seq).AsAsyncEnumerable().WithCancellation(ct))
        {
            if (!Holds(link)) return false;
            latest[link.AuditId] = link;
        }
        var present = new HashSet<long>();
        await foreach (var row in db.AuditLog.AsNoTracking().Where(a => a.Id <= through).AsAsyncEnumerable().WithCancellation(ct))
        {
            if (!latest.TryGetValue(row.Id, out var link) || link.RowHash != RowHash(row)) return false;
            present.Add(row.Id);
        }
        return latest.Keys.All(present.Contains);
    }
}
