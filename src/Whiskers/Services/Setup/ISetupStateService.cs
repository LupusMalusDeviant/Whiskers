namespace Whiskers.Services.Setup;

/// <summary>First-run setup state (W1). "Complete" = an Admin exists. Until then the setup-redirect
/// middleware funnels HTML navigation to <c>/setup</c>. <see cref="IsSetupComplete"/> is O(1) (cached);
/// completion is atomic.</summary>
public interface ISetupStateService
{
    bool IsSetupComplete { get; }
    /// <summary>Checks the one-time setup token. Until an admin exists, whoever completes the wizard becomes
    /// Admin — root on every managed host — so the wizard must prove access to the server, not just to the port
    /// (audit TA-02). The token comes from <c>WHISKERS_SETUP_TOKEN</c> or the generated <c>setup-token.txt</c>.</summary>
    bool VerifySetupToken(string? token);
    Task<SetupCompletionResult> CompleteSetupAsync(SetupAdminRequest req, CancellationToken ct = default);
}
