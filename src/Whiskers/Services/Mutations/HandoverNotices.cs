using System.Text.Json;
using Whiskers.Configuration;

namespace Whiskers.Services.Mutations;

/// <summary>Contract 0020 (D13): one personal notice per handover, for its new owner, until acknowledged. Counts only:
/// no item names, no arguments.</summary>
public sealed record HandoverNotice(string Id, string OperationId, string ToOwnerId, string FromOwnerId,
    string HandedOverBy, DateTime AtUtc, int Count);

/// <summary>
/// Contract 0020 (D13): the new owner's personal in-app notice of a handover. Whiskers has no per-person delivery; the
/// notice is a banner shown to its addressee only, until they acknowledge it. No message goes to any address.
/// </summary>
public interface IHandoverNotices
{
    /// <summary>The open notices of <paramref name="ownerId"/> (<c>user:&lt;email&gt;</c>); empty when the file cannot be
    /// read (the banner is context, never a gate).</summary>
    IReadOnlyList<HandoverNotice> For(string ownerId);

    /// <summary>One notice by id, or <c>null</c> (also when the file cannot be read).</summary>
    HandoverNotice? Find(string noticeId);

    /// <summary>Whether the file can be read and written now (a handover is refused when not).</summary>
    bool IsReadable { get; }

    /// <summary>Records the notice of an executed handover. Bookkeeping inside the <c>automation.handover</c> decision
    /// only; throws when the file cannot be written.</summary>
    void RecordNotice(HandoverNotice notice);

    /// <summary>The addressee acknowledges their own notice (<c>automation.handover_acknowledge</c>, read level). The
    /// caller is the open decision's principal: a notice of anyone else is refused here, past the page and the
    /// gateway, like <c>account.password_change</c>.</summary>
    [MutatingOperation("automation.handover_acknowledge")]
    Task AcknowledgeAsync([MutationTarget] string noticeId, CancellationToken ct = default);
}

/// <summary><c>automation-handover-notices.json</c> in the data directory, written atomically and flushed (R1-35).</summary>
public sealed class HandoverNotices : IHandoverNotices
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<HandoverNotices> _logger;
    private readonly object _gate = new();

    public HandoverNotices(DataPathOptions paths, ILogger<HandoverNotices> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = paths.AutomationHandoverNoticesJson;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsReadable
    {
        get
        {
            lock (_gate)
            {
                try { Read(); return true; }
                catch (InvalidOperationException) { return false; }
            }
        }
    }

    public IReadOnlyList<HandoverNotice> For(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) return [];
        lock (_gate)
        {
            try
            {
                return Read().Where(n => string.Equals(n.ToOwnerId, ownerId, StringComparison.Ordinal))
                    .OrderBy(n => n.AtUtc).ToList();
            }
            catch (InvalidOperationException) { return []; }
        }
    }

    public HandoverNotice? Find(string noticeId)
    {
        if (string.IsNullOrWhiteSpace(noticeId)) return null;
        lock (_gate)
        {
            try { return Read().FirstOrDefault(n => n.Id == noticeId); }
            catch (InvalidOperationException) { return null; }
        }
    }

    public void RecordNotice(HandoverNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        lock (_gate)
        {
            var notices = Read();
            notices.RemoveAll(n => n.Id == notice.Id);
            notices.Add(notice);
            Write(notices);
        }
    }

    public Task AcknowledgeAsync(string noticeId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noticeId);
        ct.ThrowIfCancellationRequested();
        // The caller as the gateway decided it; outside a decision there is nobody to acknowledge for.
        var caller = MutationScope.Current?.Principal is { } principal ? principal.OnBehalfOf?.Id ?? principal.Id : null;
        if (PrincipalIds.EmailOf(caller) is null)
            throw new UnauthorizedAccessException("Only a signed-in person can acknowledge a handover notice.");
        lock (_gate)
        {
            var notices = Read();
            var notice = notices.FirstOrDefault(n => n.Id == noticeId)
                         ?? throw new KeyNotFoundException("The handover notice no longer exists.");
            // Own notices only: whoever else asks is refused here, whatever the page or the gateway let through.
            if (!string.Equals(notice.ToOwnerId, caller, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("A handover notice can only be acknowledged by the person it was given to.");
            notices.Remove(notice);
            Write(notices);
        }
        MutationScope.Note($"notice = {{ operationId: {noticeId}, acknowledgedBy: {caller} }}");
        return Task.CompletedTask;
    }

    private List<HandoverNotice> Read()
    {
        try
        {
            if (Directory.Exists(_path)) throw new IOException("The handover notices path is a directory.");
            string text;
            try { text = File.ReadAllText(_path); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return []; }
            var notices = JsonSerializer.Deserialize<List<HandoverNotice>>(text, Json)
                          ?? throw new JsonException("The handover notices file is empty.");
            if (notices.Any(n => n is null || string.IsNullOrWhiteSpace(n.Id) || string.IsNullOrWhiteSpace(n.ToOwnerId)))
                throw new JsonException("The handover notices file holds an invalid entry.");
            return notices;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogError(ex, "The handover notices {Path} cannot be read", _path);
            throw new InvalidOperationException("The handover notices cannot be read.", ex);
        }
    }

    private void Write(List<HandoverNotice> notices)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            Whiskers.Services.Persistence.DurableFile.WriteAllText(_path, JsonSerializer.Serialize(notices, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogError(ex, "The handover notices {Path} cannot be written", _path);
            throw new InvalidOperationException("The handover notices cannot be written.", ex);
        }
    }
}
