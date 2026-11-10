using System.Net;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using Whiskers.Services.Server;
using Whiskers.Services.ServerConfig;
using Whiskers.Utils;

namespace Whiskers.Services.EndpointChecks;

/// <summary>R1-57 (contract 0004): one run of one check, through the gateway as the check's owner.</summary>
public interface IEndpointCheckRunner
{
    /// <summary>Runs <paramref name="check"/> once and returns what to record. Never throws for a failing target, a
    /// refusal or a stalled host — only when <paramref name="ct"/> is cancelled.</summary>
    Task<EndpointCheckRun> RunAsync(EndpointCheckDefinition check, CancellationToken ct);

    /// <summary>"Run now": the run of the check <paramref name="checkId"/> as it is stored <b>now</b> (review M1: never a
    /// copy a page loaded earlier), inside an <c>endpointcheck.run</c> decision the caller already holds (the UI's, for
    /// the signed-in Admin) — no decision of its own, and the result carries that decision's operation id and is marked
    /// <see cref="EndpointCheckRun.OnDemand"/>. A check that is gone, switched off or no longer validates comes back
    /// <c>Refused</c> without running anything; for one that is gone or off the answer is also marked
    /// <see cref="EndpointCheckRun.NothingRan"/> and is not recorded. Outside such a decision the scope guard refuses the
    /// host command.</summary>
    Task<EndpointCheckRun> RunInDecisionAsync(string checkId, CancellationToken ct);
}

/// <summary>
/// R1-57: resolves and probes on the host, inside one <c>endpointcheck.run</c> decision (one audit event, one operation
/// id). Whiskers never connects to the target; it decides which addresses the host may contact. Bounded: at most 1 + 4
/// executor calls, each limited by the check's timeout plus <see cref="AttemptGrace"/>, the whole run by
/// <see cref="EndpointCheckValidator.RunDeadline"/>.
/// </summary>
public sealed class EndpointCheckRunner : IEndpointCheckRunner
{
    /// <summary>The most addresses one run tries.</summary>
    public const int MaxAddresses = 4;

    /// <summary>The longest detail line kept.</summary>
    public const int MaxDetail = 300;

    /// <summary>Review n1: what an executor call gets on top of the check's timeout, so a tool's own time limit
    /// (<c>curl --max-time</c>, <c>nc -w</c>) ends first and its clearer answer is kept. Five calls of timeout + 2 s are
    /// exactly the run deadline, timeout × 5 + 10 s.</summary>
    public static readonly TimeSpan AttemptGrace = TimeSpan.FromSeconds(2);

    private readonly IAutomationMutationRunner _automation;
    private readonly IHostCommandExecutor _executor;
    private readonly IServerConfigService _servers;
    private readonly IEndpointCheckStore _store;
    private readonly TimeProvider _time;

    public EndpointCheckRunner(IAutomationMutationRunner automation, IHostCommandExecutor executor,
        IServerConfigService servers, IEndpointCheckStore store, TimeProvider? time = null)
    {
        _automation = automation;
        _executor = executor;
        _servers = servers;
        _store = store;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Who a check runs as: its owner, re-checked by the gateway on every run (INV-04). The owner is also its
    /// provenance: only an Admin creates or takes over a check, in the UI (there is no MCP write tool). Without it the
    /// gateway would treat the check as agent-created and refuse every unattended run as needing a confirmation.</summary>
    public static AutomationActor ActorOf(EndpointCheckDefinition check)
    {
        var owner = string.IsNullOrWhiteSpace(check.OwnerId) ? null : check.OwnerId;
        return AutomationActor.ForCheck(check.Id, check.Name, owner);
    }

    /// <summary>The executor timeout of one attempt.</summary>
    public static TimeSpan AttemptTimeout(int timeoutSeconds) => TimeSpan.FromSeconds(timeoutSeconds) + AttemptGrace;

    public async Task<EndpointCheckRun> RunAsync(EndpointCheckDefinition check, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(check);
        var at = _time.GetUtcNow().UtcDateTime;
        var arguments = new { serverId = check.ServerId, checkId = check.Id };

        // Review m5: no owner, no run — also during the owner grace period, which would otherwise lend the item the
        // capability's own level (Admin, for a root-equivalent command). Recorded at read level, nothing runs.
        if (string.IsNullOrWhiteSpace(check.OwnerId))
        {
            const string reason = "The check has no owner; an Admin must save it again before it runs.";
            var refused = await _automation.RefuseAsync(ActorOf(check), "endpointcheck.run", arguments, reason, ct);
            return new EndpointCheckRun(check.Id, at, Models.EndpointCheckOutcome.Refused, reason, NullIfEmpty(refused.OperationId));
        }

        // 1. The definition against the server as it is now: a check saved for a server that lost its shell transport,
        // or one whose stored values no longer pass, runs nothing — recorded as the gateway's refusal.
        if (Invalid(check, out var target) is { } invalid)
        {
            var refused = await _automation.RefuseAsync(ActorOf(check), "endpointcheck.run", arguments, invalid, ct);
            return new EndpointCheckRun(check.Id, at, Models.EndpointCheckOutcome.Refused, Bounded(invalid),
                NullIfEmpty(refused.OperationId));
        }

        var outcome = await _automation.RunAsync(ActorOf(check), "endpointcheck.run", arguments,
            (ResolvedRecord _, CancellationToken innerCt) => ProbeAsync(check, target, at, innerCt), ct);

        return outcome.Status switch
        {
            MutationStatus.Executed => outcome.Value! with { OperationId = NullIfEmpty(outcome.OperationId) },
            MutationStatus.Failed => new EndpointCheckRun(check.Id, at, Models.EndpointCheckOutcome.Failed,
                Bounded(outcome.Error?.Message ?? outcome.Reason ?? "The run failed."), NullIfEmpty(outcome.OperationId)),
            _ => new EndpointCheckRun(check.Id, at, Models.EndpointCheckOutcome.Refused,
                Bounded($"Refused: {outcome.Reason}"), NullIfEmpty(outcome.OperationId)),
        };
    }

    public async Task<EndpointCheckRun> RunInDecisionAsync(string checkId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(checkId);
        var operationId = NullIfEmpty(MutationScope.Current?.OperationId);
        var check = _store.Get(checkId);
        // Review n1: a check that is gone or off (suspended included) runs nothing and leaves no result behind — the
        // answer only tells the page why.
        if (check is null || !check.Enabled)
            return new EndpointCheckRun(checkId, _time.GetUtcNow().UtcDateTime, Models.EndpointCheckOutcome.Refused,
                check is null ? "The check no longer exists." : "The check is switched off.", operationId)
            { OnDemand = true, NothingRan = true };
        var run = await ProbeDefinitionAsync(check, ct);
        // Review n2: recorded, but the monitor's schedule stays where it is.
        return run with { OperationId = operationId, OnDemand = true };
    }

    /// <summary>Validates <paramref name="check"/> against the server as it is now and probes it, in the decision the
    /// caller holds. The probe logic of both paths; tests drive it directly.</summary>
    internal Task<EndpointCheckRun> ProbeDefinitionAsync(EndpointCheckDefinition check, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(check);
        var at = _time.GetUtcNow().UtcDateTime;
        return Invalid(check, out var target) is { } invalid
            ? Task.FromResult(new EndpointCheckRun(check.Id, at, Models.EndpointCheckOutcome.Refused, Bounded(invalid)))
            : ProbeAsync(check, target, at, ct);
    }

    /// <summary>Why <paramref name="check"/> cannot run on its server as it is now; null when it can.</summary>
    private string? Invalid(EndpointCheckDefinition check, out EndpointCheckValidator.Target target)
    {
        var errors = EndpointCheckValidator.Validate(check.ToDraft(), _servers.GetServer(check.ServerId));
        if (!EndpointCheckValidator.TryTarget(check.ToDraft(), out target, out _))
            return errors.Count > 0 ? string.Join(" ", errors) : "The target is not valid.";
        return errors.Count > 0 ? string.Join(" ", errors) : null;
    }

    private async Task<EndpointCheckRun> ProbeAsync(EndpointCheckDefinition check, EndpointCheckValidator.Target target,
        DateTime at, CancellationToken ct)
    {
        var runDeadline = EndpointCheckValidator.RunDeadline(check.TimeoutSeconds);
        var runEnd = _time.GetUtcNow() + runDeadline;
        using var timer = new CancellationTokenSource(runDeadline, _time);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        var attempt = AttemptTimeout(check.TimeoutSeconds);
        try
        {
            // 2.–3. The addresses: a literal is its own, probed as written (it is canonical text already); a name is
            // resolved on the host, completely, or not at all. Each is judged by its canonical form and contacted as the
            // host answered it (review m4).
            IReadOnlyList<ResolvedAddress> addresses;
            if (target.Literal is { } literal)
            {
                addresses = [new ResolvedAddress(literal, OutboundAddressPolicy.Canonicalize(literal))];
            }
            else
            {
                var resolved = await _executor.ExecuteMutationAsync(check.ServerId, EndpointCheckCommands.Resolve(target.Host),
                    attempt, deadline.Token, EndpointCheckCommands.OutputCap + 64);
                var answer = resolved.OutputTruncated
                    ? new ResolveAnswer([], "the answer was cut off")
                    : EndpointCheckParser.ParseResolve(resolved.Output);
                if (!answer.IsUsable)
                    return Run(check, at, Models.EndpointCheckOutcome.Failed,
                        $"Resolving {target.Host} on the host: {answer.Unreadable ?? "no address"}{TransportNote(resolved)}.");
                addresses = answer.Entries;
            }

            // Every address the host would use must be public or excepted for this host and port — not only the ones
            // tried: a name that also answers an internal address is refused as a whole.
            var exceptions = OutboundExceptionList.From(check.Exceptions, 0);
            foreach (var address in addresses)
            {
                if (OutboundAddressPolicy.DeniedRange(address.Canonical) is { } range
                    && !exceptions.Allows(target.Host, target.Port, address.Canonical))
                    return Run(check, at, Models.EndpointCheckOutcome.Refused,
                        $"{Describe(address)} ({range}) is an internal address; an Admin can allow it with an exception for " +
                        $"{target.Host}:{target.Port}.");
            }

            // 4. At most four, in answer order, until one answers.
            var tried = addresses.Take(MaxAddresses).ToList();
            ProbeResult? last = null;
            IPAddress? lastAddress = null;
            for (var i = 0; i < tried.Count; i++)
            {
                // Review n3: an attempt starts only when it can run to its own end inside the run deadline; otherwise
                // the run stops here and says so, rather than letting the deadline cut a probe off mid-flight.
                if (runEnd - _time.GetUtcNow() < attempt)
                    return Run(check, at, Models.EndpointCheckOutcome.Failed,
                        $"Run deadline reached after {i} address{(i == 1 ? "" : "es")}; no time was left for another attempt" +
                        (last is null ? "." : $" ({lastAddress}: {last.Detail})."),
                        last, null, i, addresses.Count - i);

                var address = tried[i].Answered;
                var result = await _executor.ExecuteMutationAsync(check.ServerId, CommandFor(check, target, address),
                    attempt, deadline.Token, EndpointCheckCommands.OutputCap + 64);
                var probe = result.OutputTruncated
                    ? new ProbeResult(false, "the answer was cut off")
                    : Parse(check, result.Output, _time.GetUtcNow().UtcDateTime);
                if (!probe.Passed && result.Output.Length == 0 && TransportNote(result) is { Length: > 0 } note)
                    probe = probe with { Detail = probe.Detail + note };
                last = probe;
                lastAddress = address;
                if (probe.Passed || probe.Answered)
                    return Run(check, at, probe.Passed ? Models.EndpointCheckOutcome.Passed : Models.EndpointCheckOutcome.Failed,
                        $"{address}: {probe.Detail}", probe, address, i + 1, addresses.Count - (i + 1));
            }

            return Run(check, at, Models.EndpointCheckOutcome.Failed,
                $"No address answered ({lastAddress}: {last?.Detail ?? "not tried"}).", last, null, tried.Count,
                addresses.Count - tried.Count);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Run(check, at, Models.EndpointCheckOutcome.Failed,
                $"The run did not finish within {EndpointCheckValidator.RunDeadline(check.TimeoutSeconds).TotalSeconds:0} s.");
        }
    }

    private static string Describe(ResolvedAddress address) =>
        address.Answered.Equals(address.Canonical) ? address.Canonical.ToString() : $"{address.Answered} (= {address.Canonical})";

    private static string CommandFor(EndpointCheckDefinition check, EndpointCheckValidator.Target target, IPAddress address) =>
        check.Kind switch
        {
            Models.EndpointCheckKind.Http => EndpointCheckCommands.Http(target.Url!, target.Host, address, target.Port, check.TimeoutSeconds),
            Models.EndpointCheckKind.Tcp => EndpointCheckCommands.Tcp(address, target.Port, check.TimeoutSeconds),
            Models.EndpointCheckKind.TlsExpiry => EndpointCheckCommands.Tls(target.Host, address, target.Port),
            _ => throw new InvalidOperationException("Unknown check kind."),
        };

    private static ProbeResult Parse(EndpointCheckDefinition check, string output, DateTime nowUtc) => check.Kind switch
    {
        Models.EndpointCheckKind.Http => EndpointCheckParser.ParseHttp(output, check.ExpectedStatus),
        Models.EndpointCheckKind.Tcp => EndpointCheckParser.ParseTcp(output),
        _ => EndpointCheckParser.ParseTls(output, nowUtc, check.TlsWarnDays ?? EndpointCheckDefinition.DefaultTlsWarnDays),
    };

    /// <summary>Why the host produced nothing (no transport, a refused connection), when the executor said so.</summary>
    private static string TransportNote(CommandResult result) =>
        result.Output.Length == 0 && !string.IsNullOrWhiteSpace(result.Error)
            ? $" (the host could not run it: {result.Error.Trim()})"
            : "";

    private static EndpointCheckRun Run(EndpointCheckDefinition check, DateTime at, Models.EndpointCheckOutcome outcome,
        string detail, ProbeResult? probe = null, IPAddress? answered = null, int tried = 0, int notTried = 0) =>
        new(check.Id, at, outcome, Bounded(detail), null, probe?.LatencyMs, probe?.HttpStatus, probe?.TlsNotAfterUtc,
            answered?.ToString(), tried, Math.Max(0, notTried));

    /// <summary>One line, at most <see cref="MaxDetail"/> characters, secrets redacted.</summary>
    internal static string Bounded(string detail)
    {
        var line = SecretRedactor.Redact(detail).ReplaceLineEndings(" ").Trim();
        return line.Length <= MaxDetail ? line : line[..(MaxDetail - 1)] + "…";
    }

    /// <summary>What an internal address is shown as to a reader below Admin.</summary>
    public const string MaskedAddress = "[internal address]";

    // An IPv6-looking token (it holds a colon; an embedded IPv4 part included), and a dotted quad that is not part of a
    // longer dotted number. A dotted quad followed by ":<port>" is still found by the second.
    private static readonly System.Text.RegularExpressions.Regex V6Like = new(
        @"(?<![\w.:])[0-9A-Fa-f:.]*:[0-9A-Fa-f:.]*(?!\w)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    private static readonly System.Text.RegularExpressions.Regex V4Like = new(
        @"(?<![\w.])\d{1,3}(?:\.\d{1,3}){3}(?!\w|\.\d)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>Review n4: <paramref name="text"/> with every address in a default-denied range (R1-54's
    /// <see cref="OutboundAddressPolicy.DeniedRange"/>, judged canonically) replaced by <see cref="MaskedAddress"/>, for a
    /// reader below Admin — a run's detail names the addresses the host resolved, and for an excepted target those are
    /// internal ones. Public addresses stay. Only an exact dotted quad or a valid IPv6 form counts as an address, so a
    /// latency or a version number is left alone.</summary>
    public static string MaskInternalAddresses(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        try
        {
            var masked = V6Like.Replace(text, match =>
            {
                // A trailing ':' or '.' may be the sentence's ("fd00::1: …"), not the address's: try the token first,
                // then shorter by one character at a time.
                var token = match.Value;
                for (var length = token.Length; length > 0; length--)
                {
                    var candidate = token[..length];
                    if (IPAddress.TryParse(candidate, out var address)
                        && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                        return OutboundAddressPolicy.DeniedRange(address) is null ? token : MaskedAddress + token[length..];
                    if (candidate[^1] is not (':' or '.')) break;
                }
                return token;
            });
            return V4Like.Replace(masked, match =>
                IPAddress.TryParse(match.Value, out var address) && OutboundAddressPolicy.DeniedRange(address) is not null
                    ? MaskedAddress
                    : match.Value);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return MaskedAddress;   // fail closed: show nothing rather than an unmasked line
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
