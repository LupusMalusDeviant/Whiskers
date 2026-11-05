using System.Net;
using System.Text.Json;
using Whiskers.Configuration;
using Whiskers.Models;

namespace Whiskers.Services.EndpointChecks;

/// <summary>R1-57 (contract 0004): what an Admin hands over to create or change a check. <see cref="Id"/> null creates
/// one. The owner, the suspension and the timestamps are the store's — whatever a caller would send for them is not
/// part of the draft.</summary>
public sealed record EndpointCheckDraft(
    string? Id,
    string ServerId,
    string Name,
    EndpointCheckKind Kind,
    string Target,
    int? Port,
    int? ExpectedStatus,
    int? TlsWarnDays,
    int IntervalSeconds,
    int TimeoutSeconds,
    IReadOnlyList<OutboundExceptionEntry> Exceptions,
    bool Enabled);

/// <summary>R1-57: one stored check, as the store hands it out (a snapshot; the entity stays inside the store).</summary>
public sealed record EndpointCheckDefinition(
    string Id,
    string ServerId,
    string Name,
    EndpointCheckKind Kind,
    string Target,
    int? Port,
    int? ExpectedStatus,
    int? TlsWarnDays,
    int IntervalSeconds,
    int TimeoutSeconds,
    IReadOnlyList<OutboundExceptionEntry> Exceptions,
    bool Enabled,
    string? SuspendedReason,
    string OwnerId,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    DateTime? LastRunUtc,
    EndpointCheckOutcome? LastOutcome)
{
    /// <summary>The default number of days a certificate must still be valid for.</summary>
    public const int DefaultTlsWarnDays = 14;

    /// <summary>The draft this definition was saved from (for re-validation before every run).</summary>
    public EndpointCheckDraft ToDraft() => new(Id, ServerId, Name, Kind, Target, Port, ExpectedStatus, TlsWarnDays,
        IntervalSeconds, TimeoutSeconds, Exceptions, Enabled);

    internal static EndpointCheckDefinition From(EndpointCheckEntity e) => new(
        e.Id, e.ServerId, e.Name, e.Kind, e.Target, e.Port, e.ExpectedStatus, e.TlsWarnDays, e.IntervalSeconds,
        e.TimeoutSeconds, ReadExceptions(e.ExceptionsJson), e.Enabled, e.SuspendedReason, e.OwnerId, e.CreatedUtc,
        e.UpdatedUtc, e.LastRunUtc, e.LastOutcome);

    /// <summary>The stored exceptions; an unreadable list excepts nothing (fail closed).</summary>
    internal static IReadOnlyList<OutboundExceptionEntry> ReadExceptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<OutboundExceptionEntry>>(json) ?? []).Where(e => e is not null).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static string WriteExceptions(IReadOnlyList<OutboundExceptionEntry> entries) =>
        JsonSerializer.Serialize(entries.Select(e => new OutboundExceptionEntry
        {
            Host = e.Host, Port = e.Port, Addresses = (e.Addresses ?? []).ToList(),
        }).ToList());
}

/// <summary>R1-57: one address as the host answered it (<see cref="Answered"/>, what a probe contacts and the result
/// records) and as it is judged (<see cref="Canonical"/>: an IPv4-mapped, NAT64 or 6to4 form is its IPv4 address).
/// Review m4: an IPv6-only host behind DNS64 must be sent to the address it was given.</summary>
public sealed record ResolvedAddress(IPAddress Answered, IPAddress Canonical);

/// <summary>R1-57: what <c>getent ahosts</c> answered: the addresses unique by their canonical form, in answer order, or
/// why it is unusable.</summary>
public sealed record ResolveAnswer(IReadOnlyList<ResolvedAddress> Entries, string? Unreadable)
{
    /// <summary>The canonical forms, as they are judged.</summary>
    public IReadOnlyList<IPAddress> Addresses => Entries.Select(e => e.Canonical).ToList();

    public bool IsUsable => Unreadable is null && Entries.Count > 0;
}

/// <summary>R1-57: one probe's reading. <see cref="Passed"/> only on positive evidence.</summary>
public sealed record ProbeResult(
    bool Passed,
    string Detail,
    long? LatencyMs = null,
    int? HttpStatus = null,
    DateTime? TlsNotAfterUtc = null,
    bool Answered = false);

/// <summary>R1-57: one run of one check, as it is recorded.</summary>
public sealed record EndpointCheckRun(
    string CheckId,
    DateTime AtUtc,
    EndpointCheckOutcome Outcome,
    string Detail,
    string? OperationId = null,
    long? LatencyMs = null,
    int? HttpStatus = null,
    DateTime? TlsNotAfterUtc = null,
    string? AnsweredAddress = null,
    int AddressesTried = 0,
    int AddressesNotTried = 0);
