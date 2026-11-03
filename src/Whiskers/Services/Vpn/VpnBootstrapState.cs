namespace Whiskers.Services.Vpn;

/// <summary>How the VPN bring-up at this start ended (R1-43).</summary>
public enum VpnBootstrapStatus
{
    /// <summary>Not run yet in this process.</summary>
    Pending,
    /// <summary>Provider <c>none</c>: the VPN is managed outside Whiskers; nothing to bring up.</summary>
    NotManaged,
    /// <summary>The mesh is up (or managed by the host).</summary>
    Done,
    /// <summary>Refused before anything ran: no admin has taken the bring-up over.</summary>
    NoOwner,
    /// <summary>Refused before anything ran: the owner file could not be read, or the bring-up is suspended.</summary>
    Blocked,
    /// <summary>Refused by the gateway (authority, policy).</summary>
    Refused,
    /// <summary>Ran and failed.</summary>
    Failed,
}

/// <summary>The bring-up's result as the status page and the log show it. <see cref="Message"/> never holds a key.</summary>
public sealed record VpnBootstrapReport(VpnBootstrapStatus Status, string Message, DateTime At, string? OperationId = null)
{
    /// <summary>Nothing is wrong: no VPN to manage, or it came up.</summary>
    public bool Ok => Status is VpnBootstrapStatus.NotManaged or VpnBootstrapStatus.Done;
}

/// <summary>The last bring-up of this process, for the UI (Settings → Automation owners).</summary>
public interface IVpnBootstrapState
{
    VpnBootstrapReport Last { get; }

    void Set(VpnBootstrapReport report);
}

/// <inheritdoc />
public sealed class VpnBootstrapState : IVpnBootstrapState
{
    private VpnBootstrapReport _last = new(VpnBootstrapStatus.Pending, "", DateTime.UtcNow);

    public VpnBootstrapReport Last => Volatile.Read(ref _last);

    public void Set(VpnBootstrapReport report) =>
        Volatile.Write(ref _last, report ?? throw new ArgumentNullException(nameof(report)));
}

/// <summary>The bring-up failed and <see cref="VpnSettings.FailStartupOnError"/> is set: Whiskers does not start.</summary>
public sealed class VpnBootstrapFailedException(VpnBootstrapReport report)
    : InvalidOperationException($"The VPN bring-up did not succeed ({report.Status}): {report.Message} " +
                                "Whiskers does not start because Vpn:FailStartupOnError is set.")
{
    public VpnBootstrapReport Report { get; } = report;
}
