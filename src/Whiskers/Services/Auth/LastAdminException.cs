namespace Whiskers.Services.Auth;

/// <summary>A role change would leave an instance that has an explicit Admin without one (audit TA-02). Its own type,
/// so a page can tell this refusal from any other failure of a role save (review 1.0.0, T3); it stays an
/// <see cref="InvalidOperationException"/> for every caller that already catches that.</summary>
public sealed class LastAdminException : InvalidOperationException
{
    public LastAdminException() : base("At least one user must keep the Admin role.") { }
}
