using Microsoft.AspNetCore.Http;

namespace Whiskers.Startup;

/// <summary>Path rules for the F3 maintenance middleware. A separate allowlist from
/// <see cref="SetupRedirectPaths"/> (so the two request gates stay independent) but it reuses
/// <see cref="SetupRedirectPaths.IsHtmlNavigation"/> to gate only top-level page navigations.</summary>
public static class MaintenancePaths
{
    // Infrastructure that must keep working during the brief maintenance window (probes, Blazor/framework
    // transport, RCL assets, machine MCP, webhooks, metrics). Sub-resources also carry a non-HTML Accept and
    // are exempted by IsHtmlNavigation regardless of URL.
    private static readonly string[] Exempt =
    {
        "/healthz", "/readyz",
        "/_blazor", "/_framework", "/_content",
        "/mcp",
        "/api/webhooks",
        "/metrics",
    };

    public static bool IsExempt(PathString path)
        => Exempt.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));
}
