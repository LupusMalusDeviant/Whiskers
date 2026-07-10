using Microsoft.AspNetCore.Http;

namespace Whiskers.Startup;

/// <summary>Path/verb rules for the W1 setup-redirect middleware, split out so they're unit-testable without a
/// live pipeline.</summary>
public static class SetupRedirectPaths
{
    // Anonymous infrastructure that must never be funneled to /setup. (Static wwwroot assets are already served
    // by the preceding UseStaticFiles() and short-circuit before this middleware.)
    private static readonly string[] Exempt =
    {
        "/healthz", "/readyz",       // orchestrator probes
        "/set-culture",              // language switch (wizard step 1)
        "/_blazor", "/_framework",   // Blazor SignalR transport + framework files
        "/_content",                 // RCL static assets (MudBlazor css/js)
        "/mcp",                      // machine MCP clients (bearer)
        "/api/webhooks",             // HMAC webhooks
        "/metrics",                  // scrape-token endpoint
    };

    public static bool IsExempt(PathString path)
        => Exempt.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

    // Redirect only genuine top-level page navigations. Sub-resources (css/js/fonts/img/xhr) carry a non-HTML
    // Accept, so this exempts them regardless of URL — critical because MapStaticAssets serves FINGERPRINTED
    // assets (e.g. /app.&lt;hash&gt;.css) at the ROOT, which no prefix allowlist would catch.
    public static bool IsHtmlNavigation(HttpRequest req)
        => HttpMethods.IsGet(req.Method)
           && req.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
}
