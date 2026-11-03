using Microsoft.Extensions.Configuration;
using Whiskers.Configuration;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Notifications;

/// <summary>
/// R1-54 (INV-60): the exception list, stored in the <c>OutboundGuard</c> section of <c>app-settings.json</c> and held in
/// memory as the live list. Read once at start (an entry that does not validate is left out and logged — it excepts
/// nothing); from then on only <see cref="SaveAsync"/> changes it, which the Settings page runs as a gated, audited
/// <c>config.save</c> (R1-11, R1-22): the gateway authorizes and writes its audit intent before this class runs, the
/// new list is published only after the file is written, and the diff is part of the gateway's event.
/// </summary>
public sealed class OutboundExceptionService : IOutboundExceptionService
{
    private readonly IAppSettingsStore _store;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile OutboundExceptionList _current;

    public OutboundExceptionService(IConfiguration configuration, IAppSettingsStore store,
        ILogger<OutboundExceptionService> logger)
    {
        _store = store;
        var stored = configuration.GetSection(OutboundGuardSettings.SectionName).Get<OutboundGuardSettings>()
                     ?? new OutboundGuardSettings();
        var errors = new List<string>();
        _current = OutboundExceptionList.From(stored.Exceptions, version: 1, errors);
        foreach (var error in errors)
            logger.LogWarning("An outbound guard exception was ignored, it is not valid: {Reason}", error);
    }

    public OutboundExceptionList Current => _current;

    public async Task SaveAsync(IReadOnlyList<OutboundExceptionEntry> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > OutboundException.MaxEntries)
            throw new ArgumentException($"At most {OutboundException.MaxEntries} exceptions can be kept.");
        // Everything is checked before anything is written.
        var parsed = entries.Select(OutboundException.Parse).ToList();
        var twice = parsed.GroupBy(e => (e.Host, e.Port)).FirstOrDefault(g => g.Count() > 1);
        if (twice is not null)
            throw new ArgumentException($"{OutboundException.Bracketed(twice.Key.Host)}:{twice.Key.Port} is listed twice.");

        await _lock.WaitAsync(ct);
        try
        {
            var before = _current;
            var settings = new OutboundGuardSettings { Exceptions = parsed.Select(e => e.ToEntry()).ToList() };
            // A failed write throws here and leaves the live list as it was.
            await _store.SaveSectionAsync(OutboundGuardSettings.SectionName, settings);
            _current = new OutboundExceptionList(parsed, before.Version + 1);
            // The versioned, redacted diff of the section is recorded by the store itself (R1-22), once.
        }
        finally { _lock.Release(); }
    }
}
