namespace Whiskers.Services.Auth;

/// <summary>Well-known authentication scheme names and claim types used across the auth paths.
/// Centralized so the "instant admin" magic string lives in exactly one place.</summary>
public static class AuthConstants
{
    /// <summary>Trusted-LAN mode: real authentication is globally disabled and every request runs as Admin.
    /// Only minted by the auth-bypass middleware when AUTH_DISABLED is set.</summary>
    public const string AuthDisabledScheme = "AuthDisabled";

    /// <summary>In-process agent tool execution. The synthetic identity carries the caller's real MCP level
    /// in <see cref="McpLevelClaim"/> and must be enforced at that level — it is NOT admin.</summary>
    public const string AgentSyntheticScheme = "AgentSynthetic";

    /// <summary>Claim carrying the resolved MCP permission level (read/write/admin) on a synthetic agent identity.</summary>
    public const string McpLevelClaim = "sw:mcp-level";

    /// <summary>The authentication type of a local username/password sign-in (<c>/login-local</c>).</summary>
    public const string LocalPasswordScheme = "LocalPassword";

    /// <summary>Claim carrying the local account's security stamp at sign-in (R1-26). ASP.NET Identity's own claim
    /// type, so a stamp change (password change, R1-49) ends every cookie issued before it. Never logged.</summary>
    public const string SecurityStampClaim = "AspNet.Identity.SecurityStamp";
}
