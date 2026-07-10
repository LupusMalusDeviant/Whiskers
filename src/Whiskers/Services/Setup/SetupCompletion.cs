namespace Whiskers.Services.Setup;

/// <summary>The admin account collected by the wizard. Local = create an ASP.NET Identity user (F1);
/// federated = the operator will sign in with Google/OIDC, so only the email is seeded (whitelist + Admin role).</summary>
public sealed class SetupAdminRequest
{
    public bool IsLocal { get; init; }
    public string Email { get; init; } = "";
    public string? Password { get; init; }   // required when IsLocal
}

public enum SetupCompletionStatus { Success, AlreadyComplete, Failed }

public sealed class SetupCompletionResult
{
    public SetupCompletionStatus Status { get; private init; }
    public IReadOnlyList<string> Errors { get; private init; } = Array.Empty<string>();

    public static readonly SetupCompletionResult Success = new() { Status = SetupCompletionStatus.Success };
    public static readonly SetupCompletionResult AlreadyComplete = new() { Status = SetupCompletionStatus.AlreadyComplete };
    public static SetupCompletionResult Failed(IEnumerable<string> errors)
        => new() { Status = SetupCompletionStatus.Failed, Errors = errors.ToList() };
}
