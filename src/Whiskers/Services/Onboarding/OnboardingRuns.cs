using System.Collections.Concurrent;

namespace Whiskers.Services.Onboarding;

/// <summary>
/// Contract 0018 part E: the latest onboarding run per server, in memory only (no persistence). Read by the MCP tool
/// <c>get_onboarding_status</c> (Admin only, D5); a restart forgets every run.
/// </summary>
public interface IOnboardingRuns
{
    /// <summary>The snapshot of the latest run on <paramref name="serverId"/> (exact id), or null when none ran since
    /// the start.</summary>
    OnboardingRunSnapshot? Latest(string serverId);
}

/// <summary>Contract 0018 part E: the side that records runs (the onboarding service).</summary>
public interface IOnboardingRunRecorder
{
    /// <summary>Starts the record of a run on <paramref name="serverId"/>; it becomes that server's latest run.</summary>
    IOnboardingRunRecord Begin(string serverId, string operationId);
}

/// <summary>One run being recorded: its progress, then its result.</summary>
public interface IOnboardingRunRecord : IProgress<OnboardingProgress>
{
    /// <summary>The run ended with <paramref name="result"/> (the final one, standing-key warning included).</summary>
    void Finish(OnboardingResult result);
}

/// <summary>The latest run of one server as <c>get_onboarding_status</c> answers it.</summary>
/// <param name="Log">At most <see cref="OnboardingRuns.MaxLogLines"/> progress lines, the newest last.</param>
/// <param name="TailscaleLoginUrl">The Tailscale login link the run reported (Admin-only views; never audited).</param>
public sealed record OnboardingRunSnapshot(string ServerId, string OperationId, DateTime StartedUtc,
    OnboardingStep? CurrentStep, bool Finished, OnboardingResult? Result, IReadOnlyList<string> Log,
    string? TailscaleLoginUrl);

/// <summary>The in-memory registry behind <see cref="IOnboardingRuns"/> and <see cref="IOnboardingRunRecorder"/>.</summary>
public sealed class OnboardingRuns : IOnboardingRuns, IOnboardingRunRecorder
{
    /// <summary>The most progress lines a snapshot keeps (contract 0018: <c>log[≤200]</c>).</summary>
    public const int MaxLogLines = 200;

    private readonly ConcurrentDictionary<string, Run> _latest = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public OnboardingRuns(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public OnboardingRunSnapshot? Latest(string serverId) =>
        !string.IsNullOrEmpty(serverId) && _latest.TryGetValue(serverId, out var run) ? run.Snapshot() : null;

    public IOnboardingRunRecord Begin(string serverId, string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        var run = new Run(serverId, operationId, _time.GetUtcNow().UtcDateTime);
        _latest[serverId] = run;
        return run;
    }

    private sealed class Run(string serverId, string operationId, DateTime startedUtc) : IOnboardingRunRecord
    {
        private readonly object _gate = new();
        private readonly Queue<string> _log = new();
        private OnboardingStep? _step;
        private OnboardingResult? _result;
        private string? _loginUrl;

        public void Report(OnboardingProgress value)
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
            {
                if (_result is not null) return;
                _step = value.Step;
                if (value.TailscaleLoginUrl is { Length: > 0 } url) _loginUrl = url;
                _log.Enqueue(value.Message);
                while (_log.Count > MaxLogLines) _log.Dequeue();
            }
        }

        public void Finish(OnboardingResult result)
        {
            ArgumentNullException.ThrowIfNull(result);
            lock (_gate)
            {
                _result = result;
                _step = result.FailedStep ?? (result.CompletedSteps.Count > 0 ? result.CompletedSteps[^1] : _step);
            }
        }

        public OnboardingRunSnapshot Snapshot()
        {
            lock (_gate)
                return new OnboardingRunSnapshot(serverId, operationId, startedUtc, _step, _result is not null, _result,
                    _log.ToList(), _loginUrl);
        }
    }
}
