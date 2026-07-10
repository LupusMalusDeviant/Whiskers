namespace Whiskers.Services.Onboarding;

/// <summary>The onboarding pipeline's steps, in execution order. Every step is idempotent —
/// re-running the onboarding after a failure resumes safely (installed pieces are detected and
/// skipped), which is the "resume after abort" story for the UI.</summary>
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
}

/// <summary>Outcome of an onboarding run: which steps completed, where it failed (if it did) and a
/// human-readable, actionable error — instead of raw stderr — for the UI to display.</summary>
public sealed record OnboardingResult(
    bool Success,
    IReadOnlyList<OnboardingStep> CompletedSteps,
    OnboardingStep? FailedStep,
    string? Error)
{
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
        _ => "Unbekannter Schritt.",
    };
}
