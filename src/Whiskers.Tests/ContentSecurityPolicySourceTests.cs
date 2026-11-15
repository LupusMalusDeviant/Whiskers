using System.Text.RegularExpressions;

namespace Whiskers.Tests;

/// <summary>Contract 0017 F2: the CSP is <c>script-src 'self'</c>, without <c>'unsafe-inline'</c> or
/// <c>'unsafe-eval'</c>. A component that renders an inline <c>&lt;script&gt;</c> block or an <c>on*="…"</c> attribute,
/// or calls <c>eval</c> through JS interop, would silently stop working in the browser; this guard fails instead.</summary>
public sealed class ContentSecurityPolicySourceTests
{
    private static IEnumerable<(string Path, string Text)> Components() =>
        Directory.EnumerateFiles(DeploymentScripts.RepoFile("src", "Whiskers", "Components"), "*.razor", SearchOption.AllDirectories)
            .Select(p => (p, File.ReadAllText(p)));

    [Fact]
    public void No_component_renders_an_inline_script_or_event_handler()
    {
        // Blazor's own @onclick and MudBlazor's OnClick parameters are not HTML attributes; a plain onclick="…" is.
        // Case-sensitive on purpose: HTML handlers are written in lower case, component parameters are not.
        var inlineScript = new Regex(@"<script(?![^>]*\bsrc=)[^>]*>", RegexOptions.IgnoreCase);
        var handler = new Regex(@"(?<![@\w.-])on[a-z]+\s*=\s*[""']");
        var offenders = Components()
            .Where(c => inlineScript.IsMatch(c.Text) || handler.IsMatch(c.Text))
            .Select(c => c.Path).ToList();
        Assert.True(offenders.Count == 0, "inline script the CSP blocks in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void No_component_calls_eval_through_js_interop()
    {
        var eval = new Regex(@"Invoke(Void)?Async(<[^>]*>)?\(\s*""eval""", RegexOptions.IgnoreCase);
        var offenders = Components().Where(c => eval.IsMatch(c.Text)).Select(c => c.Path).ToList();
        Assert.True(offenders.Count == 0, "eval interop the CSP blocks in: " + string.Join(", ", offenders));
    }
}
