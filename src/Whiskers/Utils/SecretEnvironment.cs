using System.Collections;
using System.Diagnostics;

namespace Whiskers.Utils;

/// <summary>
/// Environment variables that carry a VPN enrollment key (R1-43, F-21) and must never reach a child process: under
/// <c>pid: host</c> every host user can read a process's environment in <c>/proc/&lt;pid&gt;/environ</c>. They are removed
/// from Whiskers' own environment once the configuration has read them (<see cref="RemoveFromProcess"/>), and from the
/// environment of every process Whiskers starts (<see cref="Scrub"/>), so a child never inherits one even if it was set
/// again later. Names are matched without case and with <c>__</c> and <c>:</c> alike, as the configuration reads them.
/// <para>What neither can change: the environment block the kernel recorded when the process was started — what
/// <c>/proc/&lt;whiskers-pid&gt;/environ</c> shows. <c>entrypoint.sh</c> therefore moves the keys into a 0600 file before it
/// starts Whiskers; deliver them as files (<c>Vpn__Tailscale__AuthKeyFile</c>, <c>Vpn__Netbird__SetupKeyFile</c>) when
/// Whiskers is started any other way.</para>
/// </summary>
public static class SecretEnvironment
{
    /// <summary>The names, normalised (<c>__</c> → <c>:</c>), compared without case.</summary>
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Vpn:Tailscale:AuthKey", "Vpn:Netbird:SetupKey",   // the configuration keys docker-compose sets
        "TAILSCALE_AUTHKEY", "NETBIRD_SETUP_KEY",           // the .env names the legacy entrypoint reads
        "TS_AUTHKEY", "NB_SETUP_KEY",                       // what Tailscale's image and the NetBird CLI read
    };

    /// <summary>True when <paramref name="name"/> is one of <see cref="Names"/>.</summary>
    public static bool IsSecret(string? name) =>
        !string.IsNullOrEmpty(name) && Names.Contains(name.Replace("__", ":", StringComparison.Ordinal));

    /// <summary>Removes every secret name from the environment <paramref name="psi"/> hands to its child. Call it on
    /// every <see cref="ProcessStartInfo"/> Whiskers creates (<c>SecretEnvironmentTests</c> checks that each one does).</summary>
    public static ProcessStartInfo Scrub(ProcessStartInfo psi)
    {
        ArgumentNullException.ThrowIfNull(psi);
        foreach (var name in psi.Environment.Keys.Where(IsSecret).ToList())
            psi.Environment.Remove(name);
        return psi;
    }

    /// <summary>Removes every secret name from this process's environment and returns how many were set. Call it once
    /// the configuration has loaded the environment: the values stay in the configuration, not in what a child inherits.</summary>
    public static int RemoveFromProcess()
    {
        var removed = 0;
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string name || !IsSecret(name)) continue;
            Environment.SetEnvironmentVariable(name, null);
            removed++;
        }
        return removed;
    }
}
