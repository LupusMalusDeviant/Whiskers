namespace Whiskers.Services.Docker.Budget;

public enum ServerCircuitState
{
    /// <summary>Normal operation — calls go through.</summary>
    Closed,

    /// <summary>The server keeps failing; calls fail immediately without touching the network.</summary>
    Open,

    /// <summary>The cooldown has elapsed and exactly one probe is allowed through to find out.</summary>
    HalfOpen
}

public sealed record ServerCircuitSnapshot(
    string ServerId,
    ServerCircuitState State,
    int ConsecutiveFailures,
    DateTime? OpenedAt,
    string? LastReason);

/// <summary>Raised when a server's circuit is open and a call is refused without reaching the network.</summary>
public sealed class ServerCircuitOpenException(string serverId, string? reason)
    : Exception($"Calls to server '{serverId}' are paused: {reason ?? "repeated failures"}.")
{
    public string ServerId { get; } = serverId;
}

/// <summary>
/// Stops asking a server that has stopped answering (Plan-0001 WP4).
///
/// <para>A host that fails every call does not get healthier from being asked again every cycle by five
/// different loops — it gets slower, and so does Whiskers. After a run of failures the circuit opens and
/// further calls fail immediately, at no cost to the server. After a cooldown one probe is let through; if it
/// succeeds the circuit closes again, so a recovered host returns on its own without anyone restarting
/// Whiskers.</para>
///
/// <para><b>Every transition is announced.</b> This is not optional politeness: an open circuit means Whiskers
/// has stopped looking at that host, and an unannounced pause is indistinguishable from "all quiet" — the
/// exact confusion that let the 2026-08-26 incident run for six days. The rule from the incident report is
/// that self-throttling is always reported.</para>
/// </summary>
public interface IServerCircuitBreaker
{
    /// <summary>Throws <see cref="ServerCircuitOpenException"/> when the circuit is open. When the cooldown
    /// has elapsed it lets exactly one caller through as a probe and returns.</summary>
    void ThrowIfOpen(string serverId);

    void RecordSuccess(string serverId);

    /// <summary>Counts a failure. Only transport-level failures count — a "container not found" says nothing
    /// about the host's health, and counting it would open the circuit on a perfectly healthy server.</summary>
    void RecordFailure(string serverId, Exception exception);

    /// <summary>Ends a half-open probe that produced no verdict — the call never reached the host (client
    /// build failed, our own queue gave up) or the host answered with an application error. Without this the
    /// probe slot stays taken and every later caller fails fast until Whiskers restarts. Idempotent: a no-op
    /// once <see cref="RecordSuccess"/> or <see cref="RecordFailure"/> has already settled the probe.</summary>
    void ReleaseProbe(string serverId);

    ServerCircuitSnapshot Snapshot(string serverId);
    IReadOnlyList<ServerCircuitSnapshot> SnapshotAll();
}
