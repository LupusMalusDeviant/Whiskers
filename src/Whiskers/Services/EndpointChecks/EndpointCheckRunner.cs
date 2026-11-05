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

    /// <summary>"Run now": the same run, inside an <c>endpointcheck.run</c> decision the caller already holds (the UI's,
    /// for the signed-in Admin) — no decision of its own. Outside such a decision the scope guard refuses the host
    /// command. A definition that no longer validates comes back <c>Refused</c> without running anything.</summary>
    Task<EndpointCheckRun> RunInDecisionAsync(EndpointCheckDefinition check, CancellationToken ct);
}

/// <summary>
/// R1-57: resolves and probes on the host, inside one <c>endpointcheck.run</c> decision (one audit event, one operation
/// id). Whiskers never connects to the target; it decides which addresses the host may contact. Bounded: at most 1 + 4
/// executor calls, each limited by the check's timeout, the whole run by <see cref="EndpointCheckValidator.RunDeadline"/>.
/// </summary>
public sealed class EndpointCheckRunner : IEndpointCheckRunner
{
    /// <summary>The most addresses one run tries.</summary>
    public const int MaxAddresses = 4;

    /// <summary>The longest detail line kept.</summary>
    public const int MaxDetail = 300;

    private readonly IAutomationMutationRunner _automation;
    private readonly IHostCommandExecutor _executor;
    private readonly IServerConfigService _servers;
    private readonly TimeProvider _time;

    public EndpointCheckRunner(IAutomationMutationRunner automation, IHostCommandExecutor executor,
        IServerConfigService servers, TimeProvider? time = null)
    {
        _automation = automation;
        _executor = executor;
        _servers = servers;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Who a check runs as: its owner, re-checked by the gateway on every run (INV-04).</summary>
    public static AutomationActor ActorOf(EndpointCheckDefinition check) =>
        new("check:" + check.Id, $"Endpoint check '{check.Name}'",
            string.IsNullOrWhiteSpace(check.OwnerId) ? null : check.OwnerId);

    public async Task<EndpointCheckRun> RunAsync(EndpointCheckDefinition check, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(check);
        var at = _time.GetUtcNow().UtcDateTime;
        var arguments = new { serverId = check.ServerId, checkId = check.Id };

        // 1. The definition against the server as it is now: a check saved for a server that lost its shell transport,
        // or one whose stored values no longer pass, runs nothing — recorded as the gateway's refusal.
        var errors = EndpointCheckValidator.Validate(check.ToDraft(), _servers.GetServer(check.ServerId));
        if (errors.Count > 0 || !EndpointCheckValidator.TryTarget(check.ToDraft(), out var target, out _))
        {
            var reason = errors.Count > 0 ? string.Join(" ", errors) : "The target is not valid.";
            var refused = await _automation.RefuseAsync(ActorOf(check), "endpointcheck.run", arguments, reason, ct);
            return new EndpointCheckRun(check.Id, at, Models.EndpointCheckOutcome.Refused, Bounded(reason),
                NullIfEmpty(refused.OperationId));
        }

        var outcome = await _automation.RunAsync(ActorOf(check), "endpointcheck.run", arguments,
            innerCt => ProbeAsync(check, target, at, innerCt), ct);

        return outcome.Status switch
        {
            MutationStatus.Executed => outcome.Value! with { OperationId = NullIfEmpty(outcome.OperationId) },
            MutationStatus.Failed => new EndpointCheckRun(check.Id, at, Models.EndpointCheckOutcome.Failed,
                Bounded(outcome.Error?.Message ?? outcome.Reason ?? "The run failed."), NullIfEmpty(outcome.OperationId)),
            _ => new EndpointCheckRun(check.Id, at, Models.EndpointCheckOutcome.Refused,
                Bounded($"Refused: {outcome.Reason}"), NullIfEmpty(outcome.OperationId)),
        };
    }

    public Task<EndpointCheckRun> RunInDecisionAsync(EndpointCheckDefinition check, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(check);
        var at = _time.GetUtcNow().UtcDateTime;
        var errors = EndpointCheckValidator.Validate(check.ToDraft(), _servers.GetServer(check.ServerId));
        if (errors.Count > 0 || !EndpointCheckValidator.TryTarget(check.ToDraft(), out var target, out _))
            return Task.FromResult(new EndpointCheckRun(check.Id, at, Models.EndpointCheckOutcome.Refused,
                Bounded(errors.Count > 0 ? string.Join(" ", errors) : "The target is not valid.")));
        return ProbeAsync(check, target, at, ct);
    }

    private async Task<EndpointCheckRun> ProbeAsync(EndpointCheckDefinition check, EndpointCheckValidator.Target target,
        DateTime at, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(EndpointCheckValidator.RunDeadline(check.TimeoutSeconds));
        var attempt = TimeSpan.FromSeconds(check.TimeoutSeconds);
        try
        {
            // 2.–3. The addresses: a literal is its own; a name is resolved on the host, completely, or not at all.
            IReadOnlyList<IPAddress> addresses;
            if (target.Literal is { } literal)
            {
                addresses = [OutboundAddressPolicy.Canonicalize(literal)];
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
                addresses = answer.Addresses;
            }

            // Every address the host would use must be public or excepted for this host and port — not only the ones
            // tried: a name that also answers an internal address is refused as a whole.
            var exceptions = OutboundExceptionList.From(check.Exceptions, 0);
            foreach (var address in addresses)
            {
                if (OutboundAddressPolicy.DeniedRange(address) is { } range
                    && !exceptions.Allows(target.Host, target.Port, address))
                    return Run(check, at, Models.EndpointCheckOutcome.Refused,
                        $"{address} ({range}) is an internal address; an Admin can allow it with an exception for " +
                        $"{target.Host}:{target.Port}.");
            }

            // 4. At most four, in answer order, until one answers.
            var tried = addresses.Take(MaxAddresses).ToList();
            ProbeResult? last = null;
            IPAddress? lastAddress = null;
            for (var i = 0; i < tried.Count; i++)
            {
                var address = tried[i];
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

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
