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
    /// <summary>Runs <paramref name="write"/> — which adds or changes audit rows and returns their ids — and appends
    /// each row's new version to the chain, in one transaction under the chain lock. Nothing is written when either
    /// fails.</summary>
    Task<T> WriteAsync<T>(MetricsDbContext db,
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

/// <summary>What <see cref="IAuditChain.VerifyAsync"/> found.</summary>
public sealed record AuditChainReport(
    bool Intact,
    IReadOnlyList<string> Problems,
    long RowsChecked,
    long LinksChecked,
    long HeadSeq,
    long? TrustedCheckpointSeq,
    bool CheckpointsSigned);

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

    private static readonly PropertyInfo[] RowProperties = typeof(AuditLogEntity)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead)
        .OrderBy(p => p.Name, StringComparer.Ordinal)
        .ToArray();

    /// <summary>A chain for code that runs without the app's registration (tests, tools): it cannot sign.</summary>
    public static readonly AuditChain Shared = new(vaultKey: null, checkpointFile: null);

    private readonly SemaphoreSlim _lock = new(1, 1);
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
        _key = string.IsNullOrEmpty(vaultKey)
            ? null
            : HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(vaultKey), 32,
                salt: Encoding.UTF8.GetBytes("whiskers-audit-checkpoint"), info: Encoding.UTF8.GetBytes("v1"));
        _checkpointFile = checkpointFile;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The app's chain from its configuration: VAULT_KEY and the data directory.</summary>
    public static AuditChain FromConfiguration(IConfiguration configuration, DataPathOptions paths, ILogger<AuditChain> logger,
        IServiceScopeFactory scopes) =>
        new(configuration["VAULT_KEY"] ?? Environment.GetEnvironmentVariable("VAULT_KEY"), paths.AuditCheckpointJson, logger,
            scopes: scopes);

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
        return Sha256(canonical.ToJsonString());
    }

    /// <summary>The hash of a link: its predecessor, its position, the row and the row's version.</summary>
    public static string LinkHash(string prevHash, long seq, long auditId, string rowHash) =>
        Sha256(string.Create(CultureInfo.InvariantCulture, $"{prevHash}|{seq}|{auditId}|{rowHash}"));

    private static long Micros(DateTime time) =>
        (time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : time).Ticks / 10;

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private string? Sign(AuditCheckpointEntity checkpoint) =>
        _key is null ? null : Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(Signed(checkpoint))));

    private static string Signed(AuditCheckpointEntity c) =>
        string.Create(CultureInfo.InvariantCulture, $"{c.Kind}|{c.Seq}|{c.Hash}|{c.PrunedThroughId}|{Micros(c.At)}");

    /// <summary>Whether a checkpoint's signature is right; without a key nothing is trusted as signed.</summary>
    private bool Valid(AuditCheckpointEntity checkpoint) =>
        _key is not null && checkpoint.Mac is { Length: 64 } mac
        && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(mac), Convert.FromHexString(Sign(checkpoint)!));

    // ---------------------------------------------------------------------------------------------- writing

    public async Task<T> WriteAsync<T>(MetricsDbContext db,
        Func<CancellationToken, Task<(T Result, IReadOnlyCollection<long> AuditIds)>> write, CancellationToken ct = default)
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
                var (result, ids) = await write(ct);
                await AppendAsync(db, ids, ct);
                await tx.CommitAsync(ct);
                return result;
            });
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Appends the current version of each row. Runs inside the caller's transaction, under the lock.</summary>
    private async Task AppendAsync(MetricsDbContext db, IReadOnlyCollection<long> auditIds, CancellationToken ct)
    {
        if (auditIds.Count == 0) return;
        var head = await db.AuditChain.AsNoTracking().OrderByDescending(c => c.Seq)
            .Select(c => new { c.Seq, c.Hash }).FirstOrDefaultAsync(ct);
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
            seq++;
            var link = new AuditChainEntry
            {
                Seq = seq, AuditId = id, RowHash = rowHash, PrevHash = hash,
                Hash = LinkHash(hash, seq, id, rowHash), At = _time.GetUtcNow().UtcDateTime,
            };
            db.AuditChain.Add(link);
            hash = link.Hash;
        }
        await db.SaveChangesAsync(ct);
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
        if (await db.AuditChain.AnyAsync(ct) || await db.AuditCheckpoints.AnyAsync(ct)) return;
        // A chain that was here once is not started again: emptied chain tables beside a checkpoint file are a
        // tampered trail, and sealing its rows now would hide that. Verification reports it.
        if (ReadFile() is not null)
        {
            _logger?.LogError("AUDIT CHAIN MISSING: the checkpoint file {Path} exists but the database has no chain — " +
                              "not starting a new one; verify the audit trail", _checkpointFile);
            return;
        }
        var ids = await db.AuditLog.AsNoTracking().OrderBy(a => a.Id).Select(a => a.Id).ToListAsync(ct);
        await WriteAsync(db, _ => Task.FromResult((0, (IReadOnlyCollection<long>)ids)), ct);
        await CheckpointAsync(db, GenesisKind, ct);
        _logger?.LogInformation("Audit chain started: {Count} existing audit rows sealed (genesis)", ids.Count);
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
            if (kind == HeadKind && newest is not null && newest.Seq == seq && newest.Hash == hash) return;   // nothing new
            var written = new AuditCheckpointEntity
            {
                Kind = kind, Seq = seq, Hash = hash, At = Truncate(_time.GetUtcNow().UtcDateTime),
                PrunedThroughId = newest?.PrunedThroughId ?? 0,
            };
            written.Mac = Sign(written);
            db.AuditCheckpoints.Add(written);
            await db.SaveChangesAsync(ct);
            // Under the lock: a slower, older checkpoint never overwrites a newer one in the file.
            WriteFile(written);
        }
        finally
        {
            _lock.Release();
        }
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
        if (_checkpointFile is null || !File.Exists(_checkpointFile)) return null;
        try
        {
            return JsonSerializer.Deserialize<AuditCheckpointEntity>(File.ReadAllText(_checkpointFile));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Audit checkpoint file unreadable: {Path}", _checkpointFile);
            return new AuditCheckpointEntity { Kind = "unreadable", Seq = long.MaxValue };
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
        var count = 0;
        void Problem(string text)
        {
            if (count++ < MaxProblems) problems.Add(text);
        }

        // Checkpoints: only a valid signature is trusted. The file holds the newest one outside the database.
        var checkpoints = await db.AuditCheckpoints.AsNoTracking().OrderBy(c => c.Seq).ThenBy(c => c.Id).ToListAsync(ct);
        if (file is not null)
        {
            if (file.Kind == "unreadable") Problem("The checkpoint file cannot be read.");
            else checkpoints.Add(file);
        }
        var trusted = new List<AuditCheckpointEntity>();
        foreach (var checkpoint in checkpoints)
        {
            if (_key is null) continue;
            if (Valid(checkpoint)) trusted.Add(checkpoint);
            else if (checkpoint.Kind != "unreadable")
                Problem($"Checkpoint at link {checkpoint.Seq} ({checkpoint.Kind}) has an invalid signature.");
        }

        // The anchor: the newest signed retention checkpoint (unsigned ones are taken only when nothing can be signed).
        var retention = (_key is null ? checkpoints : trusted).Where(c => c.Kind == RetentionKind)
            .OrderByDescending(c => c.Seq).ThenByDescending(c => c.PrunedThroughId).FirstOrDefault();
        var (anchorSeq, running) = retention is null ? (0L, Genesis) : (retention.Seq, retention.Hash);
        var prunedThrough = retention?.PrunedThroughId ?? 0;

        // Links after the anchor, in order: each continues the one before.
        var latest = new Dictionary<long, string>();
        var hashAt = new Dictionary<long, string> { [anchorSeq] = running };
        long links = 0, head = anchorSeq;
        await foreach (var link in db.AuditChain.AsNoTracking().Where(c => c.Seq > anchorSeq).OrderBy(c => c.Seq)
                           .AsAsyncEnumerable().WithCancellation(ct))
        {
            links++;
            if (link.PrevHash != running)
                Problem($"Link {link.Seq} does not continue the link before it (a link was removed or changed).");
            if (LinkHash(link.PrevHash, link.Seq, link.AuditId, link.RowHash) != link.Hash)
                Problem($"Link {link.Seq} was changed.");
            running = link.Hash;
            head = link.Seq;
            hashAt[link.Seq] = link.Hash;
            latest[link.AuditId] = link.RowHash;
        }

        // The chain must reach every trusted checkpoint, with the same hash there.
        var newest = trusted.Where(c => c.Seq >= anchorSeq).OrderByDescending(c => c.Seq).FirstOrDefault();
        foreach (var checkpoint in trusted.Where(c => c.Seq >= anchorSeq))
        {
            if (checkpoint.Seq > head)
                Problem($"The trail ends at link {head}, before the checkpoint at link {checkpoint.Seq}: its end was cut off.");
            else if (!hashAt.TryGetValue(checkpoint.Seq, out var at) || at != checkpoint.Hash)
                Problem($"The trail does not match the checkpoint at link {checkpoint.Seq}.");
        }

        // Every row against its newest link; every link's row must still be there unless retention removed it.
        long rows = 0;
        await foreach (var row in db.AuditLog.AsNoTracking().OrderBy(a => a.Id).AsAsyncEnumerable().WithCancellation(ct))
        {
            rows++;
            if (!latest.Remove(row.Id, out var expected))
                Problem($"Audit row {row.Id} has no link: it was added outside the trail.");
            else if (expected != RowHash(row))
                Problem($"Audit row {row.Id} was changed after it was written.");
        }
        foreach (var missing in latest.Keys.Where(id => id > prunedThrough).Order())
            Problem($"Audit row {missing} was deleted.");

        if (count > MaxProblems) problems.Add($"… and {count - MaxProblems} more.");
        return new AuditChainReport(count == 0, problems, rows, links, head, newest?.Seq, _key is not null);
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
            await AuditExport.WriteAsync(db, report, output, exportedUtc, ct);
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
        AuditCheckpointEntity? written = null;
        var removed = await WriteAsync(db, async token =>
        {
            // Up to the first row that is still young: a row is never removed before its time, also where ids and
            // timestamps are not in the same order.
            var firstYoung = await db.AuditLog.Where(a => a.Timestamp >= cutoffUtc).OrderBy(a => a.Id)
                .Select(a => (long?)a.Id).FirstOrDefaultAsync(token);
            var through = firstYoung is { } young
                ? young - 1
                : await db.AuditLog.MaxAsync(a => (long?)a.Id, token) ?? 0;
            var count = await db.AuditLog.CountAsync(a => a.Id <= through, token);
            if (count == 0) return (0, Array.Empty<long>());

            // The removal is an event of its own, sealed before anything goes.
            var record = new AuditLogEntity
            {
                Timestamp = _time.GetUtcNow().UtcDateTime, Actor = "system", ActorType = "system",
                Action = RetentionAction, TargetType = "audit", TargetId = "audit-log", TargetName = "Audit log",
                Details = string.Create(CultureInfo.InvariantCulture,
                    $"Removed {count} audit row(s) through id {through}, older than {cutoffUtc:O} (retention {retentionDays} days)."),
                Success = true,
            };
            db.AuditLog.Add(record);
            await db.SaveChangesAsync(token);
            await AppendAsync(db, [record.Id], token);
            db.ChangeTracker.Clear();

            await db.AuditLog.Where(a => a.Id <= through).ExecuteDeleteAsync(token);
            // The new anchor: the end of the chain prefix that names only removed rows.
            var firstKept = await db.AuditChain.Where(c => c.AuditId > through).MinAsync(c => (long?)c.Seq, token);
            var anchor = await db.AuditChain.AsNoTracking().Where(c => firstKept == null || c.Seq < firstKept)
                .OrderByDescending(c => c.Seq).FirstOrDefaultAsync(token);
            var (seq, hash) = anchor is null ? await AnchorAsync(db, token) : (anchor.Seq, anchor.Hash);
            if (anchor is not null) await db.AuditChain.Where(c => c.Seq <= anchor.Seq).ExecuteDeleteAsync(token);
            written = new AuditCheckpointEntity
            {
                Kind = RetentionKind, Seq = seq, Hash = hash, PrunedThroughId = through,
                At = Truncate(_time.GetUtcNow().UtcDateTime),
            };
            written.Mac = Sign(written);
            db.AuditCheckpoints.Add(written);
            await db.SaveChangesAsync(token);
            return (count, Array.Empty<long>());
        }, ct);
        if (written is not null)
        {
            WriteFile(written);
            await CheckpointAsync(db, HeadKind, ct);
        }
        return removed;
    }
}
