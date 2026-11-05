namespace Whiskers.Models;

/// <summary>R1-57 (contract 0004): what an endpoint check probes. Stored as its name (string), so the order may change.</summary>
public enum EndpointCheckKind
{
    Http,
    Tcp,
    TlsExpiry,
}

/// <summary>R1-57: the outcome of one run. <see cref="Refused"/> = a policy refusal before anything ran on the target
/// (a denied address, an invalid definition, the gateway); it counts as a failure for alerting.</summary>
public enum EndpointCheckOutcome
{
    Passed,
    Failed,
    Refused,
}

/// <summary>R1-57 (contract 0004): one endpoint check, run on the managed host it is bound to through the host
/// command executor. Only Admins define checks (<c>endpointcheck.save</c>); every run is re-checked against
/// <see cref="OwnerId"/>'s authority and suspended when it is gone (INV-04).</summary>
public class EndpointCheckEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>The managed host the check runs on (it needs a shell transport).</summary>
    public string ServerId { get; set; } = "";

    /// <summary>Display only; never part of a command.</summary>
    public string Name { get; set; } = "";

    public EndpointCheckKind Kind { get; set; }

    /// <summary>HTTP: an absolute http/https URL. TCP/TLS: a host name or a canonical IP literal.</summary>
    public string Target { get; set; } = "";

    /// <summary>TCP/TLS: required. HTTP: optional, and then equal to the URL's effective port.</summary>
    public int? Port { get; set; }

    /// <summary>HTTP only: the expected status (100–599); null = any 2xx.</summary>
    public int? ExpectedStatus { get; set; }

    /// <summary>TLS only: fewer days left than this fails the check (default 14).</summary>
    public int? TlsWarnDays { get; set; }

    /// <summary>At least 60 s (validated), at most a day.</summary>
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>1–30 s, per attempt.</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Admin-only internal-target exceptions in R1-54's shape, as JSON: <c>[{"Host","Port","Addresses":[…]}]</c>.</summary>
    public string ExceptionsJson { get; set; } = "[]";

    public bool Enabled { get; set; } = true;

    /// <summary>Why the check was switched off by Whiskers (its owner lost the authority to run it); null otherwise.</summary>
    public string? SuspendedReason { get; set; }

    /// <summary>The owner (<c>user:&lt;email&gt;</c>), the Admin who saved the check last.</summary>
    public string OwnerId { get; set; } = "";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>When the check last ran (any outcome); null before its first run.</summary>
    public DateTime? LastRunUtc { get; set; }

    /// <summary>The last run's outcome; null before its first run.</summary>
    public EndpointCheckOutcome? LastOutcome { get; set; }
}

/// <summary>R1-57: one run's result. Kept 7 days per check (the last one of each check is always kept).</summary>
public class EndpointCheckResultEntity
{
    public long Id { get; set; }
    public string CheckId { get; set; } = "";
    public DateTime AtUtc { get; set; }

    /// <summary>The gateway operation id of the run (R1-18): the monitor's own <c>endpointcheck.run</c> decision (also a
    /// refused one), or for "Run now" the UI decision the run happened in. Null only for a run that could not be
    /// completed at all (an exception the monitor recorded as a failure).</summary>
    public string? OperationId { get; set; }

    public EndpointCheckOutcome Outcome { get; set; }

    /// <summary>One bounded, redacted line (at most 300 characters).</summary>
    public string Detail { get; set; } = "";

    public int? LatencyMs { get; set; }
    public int? HttpStatus { get; set; }
    public DateTime? TlsNotAfterUtc { get; set; }

    /// <summary>The address that answered; null when none did or nothing was tried.</summary>
    public string? AnsweredAddress { get; set; }

    public int AddressesTried { get; set; }
    public int AddressesNotTried { get; set; }
}
