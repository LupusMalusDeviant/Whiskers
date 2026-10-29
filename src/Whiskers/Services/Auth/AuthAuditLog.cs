using System.Security.Claims;
using Whiskers.Services.AuditLog;

namespace Whiskers.Services.Auth;

/// <summary>Writes sign-in events through <see cref="IAuditLogService"/>, which never throws: a lost row is logged and
/// reported to the audit health check instead.</summary>
public sealed class AuthAuditLog : IAuthAuditLog
{
    /// <summary>The audit target type of every sign-in event.</summary>
    public const string Target = "auth";

    public const string LoginAction = "auth.login";
    public const string LoginFailedAction = "auth.login_failed";
    public const string LogoutAction = "auth.logout";

    private readonly IAuditLogService _audit;

    public AuthAuditLog(IAuditLogService audit) => _audit = audit;

    public Task SignedInAsync(HttpContext context, string method, string email) =>
        _audit.LogAsync(email, "web", LoginAction, Target, method, method, Details(context, null));

    public Task SignInRefusedAsync(HttpContext context, string method, string? account, string reason) =>
        _audit.LogAsync(string.IsNullOrWhiteSpace(account) ? "anonymous" : account, "web", LoginFailedAction, Target,
            method, method, Details(context, reason), success: false);

    public Task SignedOutAsync(HttpContext context)
    {
        var email = context.User.FindFirstValue(ClaimTypes.Email);
        // Nothing to record for a visitor who was not signed in.
        if (string.IsNullOrEmpty(email)) return Task.CompletedTask;
        var method = context.User.Identity?.AuthenticationType ?? "";
        return _audit.LogAsync(email, "web", LogoutAction, Target, method, method, Details(context, null));
    }

    /// <summary>The client address (after the forwarded-headers middleware) and, for a refusal, the reason.</summary>
    private static string Details(HttpContext context, string? reason)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return reason is null ? $"ip={ip}" : $"reason={reason}; ip={ip}";
    }
}
