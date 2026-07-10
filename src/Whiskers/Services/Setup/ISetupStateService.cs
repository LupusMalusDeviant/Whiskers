namespace Whiskers.Services.Setup;

/// <summary>First-run setup state (W1). "Complete" = an Admin exists. Until then the setup-redirect
/// middleware funnels HTML navigation to <c>/setup</c>. <see cref="IsSetupComplete"/> is O(1) (cached);
/// completion is atomic.</summary>
public interface ISetupStateService
{
    bool IsSetupComplete { get; }
    Task<SetupCompletionResult> CompleteSetupAsync(SetupAdminRequest req, CancellationToken ct = default);
}
