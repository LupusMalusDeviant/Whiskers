using System.Text.Json;
using System.Text.Json.Serialization;
using Whiskers.Configuration;

namespace Whiskers.Services.Mutations;

/// <summary>Contract 0020: <c>Handover</c> is written by <see cref="IAutomationHandover"/>; <c>Hold</c> by the
/// <c>account.create</c> hold (D11 c), where From == To and nobody received anything.</summary>
public enum HandoverEntryKind { Handover, Hold }

/// <summary>Contract 0020: an item handed over (or held) and not yet run since.</summary>
public sealed record HandoverEntry(HandoverEntryKind Kind, string HandoverOperationId, string FromOwnerId,
    string ToOwnerId, string HandedOverBy, DateTime HandedOverAtUtc, string? ResumedBy, string? ResumeOperationId);

/// <summary>The ledger file cannot be read or written: a handover is refused, a run only loses the note.</summary>
public sealed class HandoverLedgerUnavailableException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

/// <summary>
/// Contract 0020: items handed over and not yet run since. Whiskers' own bookkeeping, like recording a run: not a
/// mutation, no gateway decision of its own; written only inside the handover, hold and resume decisions, and by a
/// run that the gateway executed.
/// </summary>
public interface IHandoverLedger
{
    /// <summary>Written <b>before</b> any item is changed, with every item as <c>Pending</c>. Throws
    /// <see cref="HandoverLedgerUnavailableException"/> when the file cannot be read or written: the handover is then
    /// refused and nothing is changed. <paramref name="kind"/> is <see cref="HandoverEntryKind.Hold"/> for the
    /// <c>account.create</c> hold.</summary>
    void RecordHandover(string operationId, string fromOwnerId, string toOwnerId, string actingAdminId,
        IReadOnlyList<AutomationItemRef> items, HandoverEntryKind kind = HandoverEntryKind.Handover);

    /// <summary>After each item write: its status in that entry. Only <c>HandedOver</c> items stay in the ledger. A
    /// failed write is logged; the stale <c>Pending</c> row reads as "not handed over" and is dropped by the first
    /// handover write or pruning once it is an hour old.</summary>
    void Reconcile(string operationId, AutomationItemRef item, HandoverItemStatus status);

    /// <summary>Called inside a resume decision for an item in the ledger; remembers who resumed it and in which
    /// operation. Nothing for an item that is not in the ledger.</summary>
    void RecordResume(AutomationItemRef item, string resumedBy, string operationId);

    /// <summary>For a runner's run of a ledger item: the entry, without removing it. Null otherwise. Throws
    /// <see cref="HandoverLedgerUnavailableException"/> when the ledger cannot be read; the run goes on.</summary>
    HandoverEntry? PeekFirstRun(AutomationItemRef item);

    /// <summary>Whether <paramref name="item"/> has an entry (also a <c>Pending</c> one, whose handover may still be
    /// running) that nobody resumed yet: a run by hand of such an item is refused as <c>AutomationHeld</c>. Throws
    /// <see cref="HandoverLedgerUnavailableException"/> when the ledger cannot be read; the caller then refuses.</summary>
    bool AwaitsResume(AutomationItemRef item);

    /// <summary>Removes the entry, called only after a run of the item that the gateway <b>executed</b>.</summary>
    void ConsumeAfterExecuted(AutomationItemRef item, string runOperationId);

    /// <summary>Removes the entry of an item that was deleted (or taken over the way it is today).</summary>
    void Forget(AutomationItemRef item);

    /// <summary>Removes entries that were not resumed for <paramref name="maxAge"/> (D14) and every stale
    /// <c>Pending</c> row. Logged once per pruning, naming the items; the handover event stays in the audit.</summary>
    int Prune(TimeSpan maxAge, DateTime nowUtc);

    /// <summary>Whether the file can be read now (a handover is refused when not).</summary>
    bool IsReadable { get; }
}

/// <summary>The one place a run names its handover (the first-run note).</summary>
public static class HandoverNotes
{
    /// <summary>D14: an entry that was not resumed ages out after 30 days, counted from the handover.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    /// <summary>The note a run's event carries for <paramref name="entry"/>.</summary>
    public static string Of(HandoverEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Kind == HandoverEntryKind.Hold
            ? $"hold = {{ operationId: {entry.HandoverOperationId}, owner: {entry.FromOwnerId}, heldBy: {entry.HandedOverBy}, " +
              $"resumedBy: {entry.ResumedBy ?? "-"}, resumeOperationId: {entry.ResumeOperationId ?? "-"} }}"
            : $"handover = {{ operationId: {entry.HandoverOperationId}, from: {entry.FromOwnerId}, to: {entry.ToOwnerId}, " +
              $"handedOverBy: {entry.HandedOverBy}, resumedBy: {entry.ResumedBy ?? "-"}, " +
              $"resumeOperationId: {entry.ResumeOperationId ?? "-"} }}";
    }

    /// <summary>The note of a run while the ledger cannot be read.</summary>
    public const string Unknown = "handover = unknown (ledger unreadable)";

    /// <summary>The ledger key of a runner id (<c>task:&lt;id&gt;</c>, <c>webhook:&lt;id&gt;</c>, …), or <c>null</c> for a
    /// run that is no ledger item.</summary>
    public static AutomationItemRef? ItemOfRunner(string? runnerId)
    {
        if (string.IsNullOrWhiteSpace(runnerId)) return null;
        static string? Rest(string id, string prefix) =>
            id.StartsWith(prefix, StringComparison.Ordinal) && id.Length > prefix.Length ? id[prefix.Length..] : null;
        if (Rest(runnerId, PrincipalIds.TaskPrefix) is { } task) return new(AutomationKind.ScheduledTask, task);
        if (Rest(runnerId, PrincipalIds.WebhookPrefix) is { } hook) return new(AutomationKind.Webhook, hook);
        if (Rest(runnerId, PrincipalIds.GitDeployPrefix) is { } app) return new(AutomationKind.GitDeployApp, app);
        if (Rest(runnerId, PrincipalIds.CheckPrefix) is { } check) return new(AutomationKind.EndpointCheck, check);
        return runnerId switch
        {
            PrincipalIds.AutoPatch => new(AutomationKind.FleetFeature, IAutomationOwnerStore.AutoPatch),
            PrincipalIds.AutoUpdate => new(AutomationKind.FleetFeature, IAutomationOwnerStore.AutoUpdate),
            PrincipalIds.VpnBootstrap => new(AutomationKind.FleetFeature, IAutomationOwnerStore.VpnBootstrap),
            _ => null,
        };
    }

    /// <summary>Records a resume of <paramref name="item"/> in the ledger, as the open decision's principal and operation.
    /// Never fails the resume: a ledger that cannot be written only loses the resumer on the first run's note, which the
    /// resume's own event still names.</summary>
    public static void RecordResumeInScope(IHandoverLedger? ledger, AutomationItemRef item, ILogger? logger = null)
    {
        if (ledger is null || MutationScope.Current is not { } scope) return;
        var resumer = scope.Principal?.OnBehalfOf?.Id ?? scope.Principal?.Id;
        if (string.IsNullOrWhiteSpace(resumer)) return;
        try
        {
            ledger.RecordResume(item, resumer, scope.OperationId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogError(ex, "The resume of {Kind} {Id} could not be recorded in the handover ledger", item.Kind, item.Id);
            MutationScope.Note("handover ledger: the resume could not be recorded", success: true);
        }
    }

    /// <summary>Forgets <paramref name="item"/> (deleted, or taken over as today); never fails the caller.</summary>
    public static void ForgetQuietly(IHandoverLedger? ledger, AutomationItemRef item, ILogger? logger = null)
    {
        if (ledger is null) return;
        try
        {
            ledger.Forget(item);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogError(ex, "The handover ledger entry of {Kind} {Id} could not be removed", item.Kind, item.Id);
        }
    }
}

/// <summary><c>automation-handovers.json</c> in the data directory, written atomically and flushed (R1-35).</summary>
public sealed class HandoverLedger : IHandoverLedger
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>One persisted row: an item of one handover, <c>Pending</c> until its write is reconciled.</summary>
    internal sealed record Row(AutomationKind Kind, string Id, bool Pending, HandoverEntry Entry);

    private readonly string _path;
    private readonly ILogger<HandoverLedger> _logger;
    private readonly object _gate = new();

    public HandoverLedger(DataPathOptions paths, ILogger<HandoverLedger> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = paths.AutomationHandoversJson;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsReadable
    {
        get
        {
            lock (_gate)
            {
                try { Read(); return true; }
                catch (HandoverLedgerUnavailableException) { return false; }
            }
        }
    }

    public void RecordHandover(string operationId, string fromOwnerId, string toOwnerId, string actingAdminId,
        IReadOnlyList<AutomationItemRef> items, HandoverEntryKind kind = HandoverEntryKind.Handover)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fromOwnerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toOwnerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actingAdminId);
        ArgumentNullException.ThrowIfNull(items);
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            // Stale Pending rows (a reconcile that could not be written, an hour ago or more) are read as "not handed
            // over" and dropped here. A younger one may belong to a handover or hold that is still running: kept.
            var rows = Read().Where(r => !IsStalePending(r, now)).ToList();
            foreach (var item in items)
            {
                rows.RemoveAll(r => r.Kind == item.Kind && r.Id == item.Id);
                rows.Add(new Row(item.Kind, item.Id, true,
                    new HandoverEntry(kind, operationId, fromOwnerId, toOwnerId, actingAdminId, now, null, null)));
            }
            Write(rows);
        }
    }

    public void Reconcile(string operationId, AutomationItemRef item, HandoverItemStatus status)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            var rows = Read();
            var index = rows.FindIndex(r => r.Kind == item.Kind && r.Id == item.Id
                                            && r.Entry.HandoverOperationId == operationId);
            if (index < 0) return;
            if (status == HandoverItemStatus.HandedOver) rows[index] = rows[index] with { Pending = false };
            else rows.RemoveAt(index);
            Write(rows);
        }
    }

    public void RecordResume(AutomationItemRef item, string resumedBy, string operationId)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(resumedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        lock (_gate)
        {
            var rows = Read();
            var index = rows.FindIndex(r => !r.Pending && r.Kind == item.Kind && r.Id == item.Id);
            if (index < 0) return;
            rows[index] = rows[index] with
            {
                Entry = rows[index].Entry with { ResumedBy = resumedBy, ResumeOperationId = operationId },
            };
            Write(rows);
        }
    }

    public HandoverEntry? PeekFirstRun(AutomationItemRef item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
            return Read().FirstOrDefault(r => !r.Pending && r.Kind == item.Kind && r.Id == item.Id)?.Entry;
    }

    public bool AwaitsResume(AutomationItemRef item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var now = DateTime.UtcNow;
        lock (_gate)
            return Read().Any(r => r.Kind == item.Kind && r.Id == item.Id && r.Entry.ResumedBy is null
                                   && !IsStalePending(r, now));
    }

    public void ConsumeAfterExecuted(AutomationItemRef item, string runOperationId)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            var rows = Read();
            if (rows.RemoveAll(r => r.Kind == item.Kind && r.Id == item.Id) == 0) return;
            Write(rows);
        }
        _logger.LogInformation("Handover ledger: {Kind} {Id} ran (operation {Operation}); its entry is consumed",
            item.Kind, item.Id, runOperationId);
    }

    public void Forget(AutomationItemRef item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            var rows = Read();
            if (rows.RemoveAll(r => r.Kind == item.Kind && r.Id == item.Id) == 0) return;
            Write(rows);
        }
    }

    private static readonly TimeSpan StalePending = TimeSpan.FromHours(1);

    /// <summary>A Pending row is stale once its handover is long over (an hour); a younger one may belong to a handover
    /// or hold in progress.</summary>
    private static bool IsStalePending(Row row, DateTime nowUtc) =>
        row.Pending && nowUtc - row.Entry.HandedOverAtUtc > StalePending;

    public int Prune(TimeSpan maxAge, DateTime nowUtc)
    {
        List<Row> gone;
        lock (_gate)
        {
            var rows = Read();
            // A scheduled task's unresumed entry does not age out: it is what keeps "Run now" of the task refused until
            // someone switches it on (amendment point 15). It goes when the task is switched on and runs, taken over
            // or deleted.
            gone = rows.Where(r => IsStalePending(r, nowUtc)
                                   || (r.Entry.ResumedBy is null && r.Kind != AutomationKind.ScheduledTask
                                       && nowUtc - r.Entry.HandedOverAtUtc > maxAge))
                .ToList();
            if (gone.Count == 0) return 0;
            Write(rows.Except(gone).ToList());
        }
        _logger.LogInformation("Handover ledger: {Count} entries aged out or were stale: {Items}", gone.Count,
            string.Join(", ", gone.Select(r => $"{r.Kind}:{r.Id}")));
        return gone.Count;
    }

    /// <summary>The rows; a missing file is an empty ledger. Call under <see cref="_gate"/>.</summary>
    private List<Row> Read()
    {
        try
        {
            if (Directory.Exists(_path)) throw new IOException("The handover ledger path is a directory.");
            string text;
            try { text = File.ReadAllText(_path); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return []; }
            var rows = JsonSerializer.Deserialize<List<Row>>(text, Json)
                       ?? throw new JsonException("The handover ledger is empty.");
            if (rows.Any(r => r is null || r.Entry is null || string.IsNullOrWhiteSpace(r.Id)))
                throw new JsonException("The handover ledger holds an invalid row.");
            return rows;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogError(ex, "The handover ledger {Path} cannot be read", _path);
            throw new HandoverLedgerUnavailableException("The handover ledger cannot be read.", ex);
        }
    }

    /// <summary>Writes the rows atomically. Call under <see cref="_gate"/>.</summary>
    private void Write(List<Row> rows)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            Whiskers.Services.Persistence.DurableFile.WriteAllText(_path, JsonSerializer.Serialize(rows, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogError(ex, "The handover ledger {Path} cannot be written", _path);
            throw new HandoverLedgerUnavailableException("The handover ledger cannot be written.", ex);
        }
    }
}
