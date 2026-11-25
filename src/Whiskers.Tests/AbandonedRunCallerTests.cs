using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0023 (4c): the callers that hold a lock, lease, drain entry or bookkeeping around a gateway run await the
/// outcome's <c>Completion</c> before they release it — so an abandoned run (or one with an abandoned nested run) keeps
/// its target protected until it really ends. The Git deployment's server lease is taken inside the action itself and
/// is therefore held to the action's real end without a change.
/// </summary>
public sealed class AbandonedRunCallerTests
{
    private static SyntaxNodeRoot Parse(params string[] path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Whiskers.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var file = Path.Combine([dir.FullName, "src", "Whiskers", .. path]);
        return new SyntaxNodeRoot(CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot());
    }

    private sealed record SyntaxNodeRoot(Microsoft.CodeAnalysis.SyntaxNode Root)
    {
        public MethodDeclarationSyntax Method(string name) =>
            Root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == name);
    }

    /// <summary>The method awaits <c>&lt;outcome&gt;.Completion</c> after its gateway call and before it returns.</summary>
    private static void AwaitsCompletionAfter(MethodDeclarationSyntax method, string call)
    {
        var text = method.ToFullString();
        var callAt = text.IndexOf(call, StringComparison.Ordinal);
        Assert.True(callAt >= 0, $"{method.Identifier.Text} no longer calls {call}");
        var awaits = method.DescendantNodes().OfType<AwaitExpressionSyntax>()
            .Where(a => a.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Completion" })
            .Select(a => a.SpanStart - method.FullSpan.Start)
            .ToList();
        Assert.True(awaits.Any(at => at > callAt),
            $"{method.Identifier.Text} must await the outcome's Completion after {call} (contract 0023, 4c)");
    }

    [Fact]
    public void The_webhook_run_keeps_its_lock_until_the_run_has_really_ended() =>
        AwaitsCompletionAfter(Parse("Services", "Webhooks", "WebhookService.cs").Method("RunAsOwnerAsync"),
            "automation.RunAsync");

    [Fact]
    public void A_scheduled_task_keeps_its_drain_entry_until_its_run_has_really_ended()
    {
        var root = Parse("Services", "Scheduler", "TaskExecutor.cs");
        AwaitsCompletionAfter(root.Method("UntilCompleteAsync"), "await pending");
        foreach (var wrapper in root.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                     .Where(m => m.Identifier.Text == "RunGatedAsync"))
            Assert.Contains("UntilCompleteAsync(pending)", wrapper.ToFullString());
    }

    [Fact]
    public void AutoUpdate_keeps_its_drain_entry_and_history_until_the_update_has_really_ended() =>
        AwaitsCompletionAfter(Parse("Services", "AutoUpdate", "AutoUpdateService.cs").Method("CheckAndUpdateAsync"),
            "_automation.RunAsync");

    [Fact]
    public void AutoPatch_keeps_its_drain_entry_until_the_patch_has_really_ended()
    {
        var root = Parse("Services", "AutoPatch", "AutoPatchService.cs");
        AwaitsCompletionAfter(root.Method("PatchGatedAsync"), "_automation.RunAsync");
    }

    [Fact]
    public void The_git_deployment_takes_its_server_lease_inside_the_action()
    {
        var deploy = Parse("Services", "GitDeploy", "GitDeployService.cs").Method("DeployAsync").ToFullString();
        // DeployAsync is the gateway action of gitdeploy.deploy (its callers hand it to the runner): the lease it takes
        // lasts as long as the action, abandoned or not.
        Assert.Contains("using var server = await _locks.AcquireAsync(_locks.ServerKey(app.ServerId), ct);", deploy);
    }
}
