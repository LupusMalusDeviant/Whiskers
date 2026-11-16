using Whiskers.Services.Server;

namespace Whiskers.Services.Onboarding;

/// <summary>The onboarding pipeline's steps. Every step is idempotent — re-running the onboarding after a failure
/// resumes safely (installed pieces are detected and skipped), which is the "resume after abort" story for the UI.
/// Contract 0018: <see cref="CloudBootstrap"/>, <see cref="HostKey"/> and <see cref="BootstrapCleanup"/> are appended
/// (the enum's order is not the run order): CloudBootstrap, HostKey, Bootstrap … Verify, BootstrapCleanup.</summary>
public enum OnboardingStep
{
    Bootstrap,
    TailscaleInstall,
    TailscaleLogin,
    Docker,
    NodeExporter,
    Certificate,
    MtlsProxy,
    ScrapeConfig,
    Switchover,
    Verify,
    /// <summary>Contract 0018 part D: gives Whiskers SSH access to a fresh cloud VM (only with a bootstrap method).</summary>
    CloudBootstrap,
    /// <summary>Contract 0018 part C: pins (or compares) the SSH host key before the first SSH command.</summary>
    HostKey,
    /// <summary>Contract 0018 part D: removes a generated key's line from the host, then the key itself.</summary>
    BootstrapCleanup,
}

/// <summary>Contract 0018 part D: how the bootstrap gets SSH access.</summary>
public enum BootstrapMethod
{
    /// <summary>Use the credential on the record (uploaded key or password). Today's behaviour.</summary>
    None,
    /// <summary>Hetzner only: generate a key, boot the rescue system with it, append the public key to the real
    /// system's root authorized_keys, read the real host keys from disk, boot back.</summary>
    HetznerRescueKey,
}

/// <param name="ConfirmVmName">The VM name the person confirmed when it differs from the record's name (precondition 1);
/// null otherwise. Part of the call's arguments, so the approval binds it.</param>
/// <param name="AbandonBootstrap">Runs only <c>BootstrapCleanup</c> for a run that failed after injection (see "Standing
/// bootstrap key"), then ends.</param>
public sealed record OnboardingOptions(BootstrapMethod Bootstrap, string? ConfirmVmName = null,
    bool AbandonBootstrap = false)
{
    /// <summary>The argument value of a method as the gateway sees it (<c>none</c> / <c>hetzner_rescue</c>).</summary>
    public static string ArgumentOf(BootstrapMethod method) => method == BootstrapMethod.HetznerRescueKey ? "hetzner_rescue" : "none";
}

/// <summary>One progress report of a run: the step, a human-readable line and, once, the Tailscale login link.</summary>
public sealed record OnboardingProgress(OnboardingStep Step, string Message, string? TailscaleLoginUrl = null);

/// <summary>Outcome of an onboarding run: which steps completed, where it failed (if it did) and a
/// human-readable, actionable error — instead of raw stderr — for the UI to display.</summary>
public sealed record OnboardingResult(
    bool Success,
    IReadOnlyList<OnboardingStep> CompletedSteps,
    OnboardingStep? FailedStep,
    string? Error)
{
    /// <summary>Contract 0018: the run ended <c>Failed</c> while the bootstrap line is still on the host.</summary>
    public bool StandingBootstrapKey { get; init; }

    /// <summary>Contract 0018: the pin the run recorded or compared (null when the run did not reach step HostKey).</summary>
    public SshHostKeyPin? HostKey { get; init; }

    /// <summary>Contract 0018: what the cloud bootstrap or the cleanup could not undo, and the standing-key warning.</summary>
    public IReadOnlyList<string> CleanupWarnings { get; init; } = [];

    /// <summary>Contract 0018: the two fingerprints of a host-key mismatch at step HostKey (pinned, served), for the
    /// "Replace pin…" dialog; null otherwise.</summary>
    public (string Pinned, string Served)? HostKeyMismatch { get; init; }

    public static OnboardingResult Ok(IReadOnlyList<OnboardingStep> completed) =>
        new(true, completed, null, null);

    public static OnboardingResult Fail(IReadOnlyList<OnboardingStep> completed, OnboardingStep step, string error) =>
        new(false, completed, step, error);

    /// <summary>Actionable German hint per step — shown to the user instead of (in addition to) the
    /// raw command error. Every hint ends with the reassurance that a re-run resumes safely.</summary>
    public static string Hint(OnboardingStep step) => step switch
    {
        OnboardingStep.Bootstrap => "Bootstrap-Verbindung fehlgeschlagen — SSH-Host, Port und Zugangsdaten (Key/Passwort) prüfen.",
        OnboardingStep.TailscaleInstall => "Tailscale-Installation fehlgeschlagen — hat der Host Internetzugang und sudo-Rechte?",
        OnboardingStep.TailscaleLogin => "Tailscale-Login nicht abgeschlossen — den Login-Link im Browser öffnen und den Node bestätigen.",
        OnboardingStep.Docker => "Docker-Installation fehlgeschlagen — ist get.docker.com erreichbar und die Distribution unterstützt?",
        OnboardingStep.NodeExporter => "node-exporter-Deployment fehlgeschlagen — Docker Compose auf dem Host prüfen (Port 9100 frei?).",
        OnboardingStep.Certificate => "Zertifikats-Ausstellung fehlgeschlagen — läuft der step-ca-Container auf dem Whiskers-Host?",
        OnboardingStep.MtlsProxy => "mTLS-Proxy-Deployment fehlgeschlagen — ist Port 2376 auf dem Host frei?",
        OnboardingStep.ScrapeConfig => "Scrape-Config-Eintrag fehlgeschlagen — existiert /opt/telemetry-vm auf dem Whiskers-Host?",
        OnboardingStep.Switchover => "Umstellung auf TCP+mTLS fehlgeschlagen — Serverkonfiguration prüfen.",
        OnboardingStep.Verify => "mTLS-Verbindung noch nicht erreichbar — Firewall (Port 2376 im Tailnet) und ghostunnel-Logs auf dem Host prüfen.",
        OnboardingStep.CloudBootstrap => "Cloud-Bootstrap fehlgeschlagen — Bindung an die VM, Token-Rechte (Lesen und Schreiben) und Rescue-Status bei Hetzner prüfen.",
        OnboardingStep.HostKey => "SSH-Host-Key nicht bestätigt — bei einer Abweichung beide Fingerprints vergleichen (neu installiert, IP wiederverwendet oder Angriff?).",
        OnboardingStep.BootstrapCleanup => "Bootstrap-Zugang nicht entfernt — die Zeile in /root/.ssh/authorized_keys steht noch; erneut starten oder „Bootstrap-Zugang entfernen“.",
        _ => "Unbekannter Schritt.",
    };
}
