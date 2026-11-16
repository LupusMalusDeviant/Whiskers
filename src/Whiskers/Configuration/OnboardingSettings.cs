namespace Whiskers.Configuration;

/// <summary>Contract 0018: the <c>Onboarding</c> section (app-settings.json, set in the UI).</summary>
public sealed class OnboardingSettings
{
    public const string SectionName = "Onboarding";

    public OnboardingTailscaleSettings Tailscale { get; set; } = new();

    public CloudBootstrapSettings CloudBootstrap { get; set; } = new();
}

/// <summary>D9: an optional Tailscale auth key for new hosts.</summary>
public sealed class OnboardingTailscaleSettings
{
    /// <summary>The vault reference of a Tailscale auth key for new hosts. Set in the UI (Settings), never in a config
    /// file or over MCP; the key itself lives in the vault only. Unset: the login link as before.</summary>
    public string? AuthKeyRef { get; set; }
}

/// <summary>Part D: the bounds of the Hetzner rescue bootstrap.</summary>
public sealed class CloudBootstrapSettings
{
    /// <summary>How long a soft reboot may take to bring the VM down and back before the one hard reset.</summary>
    public int SoftRebootSeconds { get; set; } = 60;

    /// <summary>Bound for each "wait until the VM runs and port 22 answers".</summary>
    public int BootTimeoutSeconds { get; set; } = 240;

    /// <summary>Off: the rescue bootstrap is refused on every surface.</summary>
    public bool Enabled { get; set; } = true;
}
