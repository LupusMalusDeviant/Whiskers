using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whiskers.Tests;

/// <summary>
/// INV-45, S-01 (review round 10, BLOCKER-1): the scheduler's refusals before the gateway are found in the source,
/// not kept by hand. Every task method the executor dispatches to may end a run before it asks the gateway
/// (<c>_automation.RunAsync</c>) only through <c>RefusedAsync</c> — the gateway's Denied event — and may not throw
/// there; a skipped retention deletion is the gateway's refusal before it returns. A new early return anywhere else
/// fails here, before it can be a run with no event.
/// </summary>
public class TaskExecutorRefusalInventoryTests
{
    [Fact]
    public void Every_refusal_of_a_scheduled_run_before_the_gateway_is_on_record()
    {
        var source = File.ReadAllText(Path.Combine(SourceRoot(), "src", "Whiskers", "Services", "Scheduler", "TaskExecutor.cs"));

        var (methods, violations) = Check(source);

        Assert.Empty(violations);
        // The dispatcher's six task methods were all found and checked: the check cannot pass by finding none.
        Assert.Equal(
            ["ExecuteCleanup", "ExecuteContainerRestart", "ExecuteCustomCommand", "ExecuteDbBackup", "ExecuteSelfBackup",
             "ExecuteVolumeBackup"],
            methods.Order(StringComparer.Ordinal));
    }

    /// <summary>The check itself: it finds what it must and passes what it may.</summary>
    [Fact]
    public void The_check_finds_an_unrecorded_early_return_a_throw_and_a_silent_retention_skip()
    {
        const string source = """
            class TaskExecutor
            {
                async Task<(bool, string)> ExecuteAsync(T t)
                {
                    var r = t.Kind switch
                    {
                        1 => await ExecuteGood(t),
                        2 => await ExecuteSilent(t),
                        3 => await ExecuteThrows(t),
                        _ => await ExecuteMissing(t),
                    };
                    return r;
                }

                async Task<(bool, string)> ExecuteGood(T t)
                {
                    if (t.Id is null) return await RefusedAsync(t, "c", new { }, "none");
                    Func<int> f = () => { return 1; };
                    var outcome = await _automation.RunAsync(A(t), "c", new { }, _ => Work());
                    if (!outcome.Ok) return (false, "after the decision: the gateway recorded it");
                    return (true, "ok");
                }

                async Task<(bool, string)> ExecuteSilent(T t)
                {
                    if (t.Id is null) return await RefusedAsync(t, "c", new { }, "none");
                    if (t.Bad) return (false, "refused without an event");
                    var outcome = await _automation.RunAsync(A(t), "c", new { }, _ => Work());
                    return (true, "ok");
                }

                async Task<(bool, string)> ExecuteThrows(T t)
                {
                    if (t.Bad) throw new InvalidOperationException("caught by the dispatcher: no event");
                    var outcome = await _automation.RunAsync(A(t), "c", new { }, _ => Work());
                    return (true, "ok");
                }

                async Task ApplyRetention(T t, int maxBackups)
                {
                    if (maxBackups <= 0) return;
                    if (t.Unsafe)
                    {
                        await _automation.RefuseAsync(A(t), "d", new { }, "unsafe");
                        return;
                    }
                    if (t.Other) return;
                    await _automation.RunAsync(A(t), "d", new { }, _ => Work());
                }
            }
            """;

        var (methods, violations) = Check(source);

        Assert.Equal(["ExecuteGood", "ExecuteMissing", "ExecuteSilent", "ExecuteThrows"], methods.Order(StringComparer.Ordinal));
        Assert.Equal(4, violations.Count);
        Assert.Contains(violations, v => v.StartsWith("ExecuteSilent:") && v.Contains("refused without an event"));
        Assert.Contains(violations, v => v.StartsWith("ExecuteThrows:") && v.Contains("throw"));
        Assert.Contains(violations, v => v.StartsWith("ExecuteMissing:") && v.Contains("not found"));
        Assert.Contains(violations, v => v.StartsWith("ApplyRetention:") && v.Contains("if (t.Other) return;"));
        Assert.DoesNotContain(violations, v => v.StartsWith("ExecuteGood:"));
    }

    /// <summary>The task methods <c>ExecuteAsync</c> dispatches to, and every place one of them — or the retention —
    /// can end before the gateway without the gateway's event.</summary>
    private static (List<string> Methods, List<string> Violations) Check(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var executor = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.Text == "TaskExecutor");
        var members = executor.Members.OfType<MethodDeclarationSyntax>().ToList();
        var dispatcher = members.Single(m => m.Identifier.Text == "ExecuteAsync");
        var methods = dispatcher.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(i => i.Expression is IdentifierNameSyntax n ? n.Identifier.Text : null)
            .OfType<string>()
            .Where(n => n.StartsWith("Execute", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var violations = new List<string>();
        foreach (var name in methods)
        {
            var method = members.FirstOrDefault(m => m.Identifier.Text == name);
            if (method is null)
            {
                violations.Add($"{name}: dispatched to but not found");
                continue;
            }
            var gateway = Own(method).OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(IsGatewayRun)?.SpanStart ?? int.MaxValue;
            foreach (var node in Own(method).Where(n => n.SpanStart < gateway))
            {
                if (node is ReturnStatementSyntax ret && !IsRefusal(ret))
                    violations.Add($"{name}: {ret} ends the run before the gateway without its event");
                if (node is ThrowStatementSyntax or ThrowExpressionSyntax)
                    violations.Add($"{name}: a throw before the gateway ends the run without an event ({node})");
            }
        }

        // A retention deletion skipped by a check is an attempt refused before its decision: the gateway's refusal
        // comes first. Only "no retention configured" returns without one — nothing was attempted.
        if (members.FirstOrDefault(m => m.Identifier.Text == "ApplyRetention") is { } retention)
            foreach (var ret in Own(retention).OfType<ReturnStatementSyntax>())
            {
                if (ret.Parent is IfStatementSyntax { Condition: var c } && c.ToString() == "maxBackups <= 0") continue;
                if (ret.Parent is BlockSyntax block && block.Statements.IndexOf(ret) is > 0 and var i
                    && block.Statements[i - 1] is ExpressionStatementSyntax { Expression: AwaitExpressionSyntax aw }
                    && aw.Expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma }
                    && ma.Name.Identifier.Text == "RefuseAsync" && ma.Expression.ToString() == "_automation")
                    continue;
                var line = ret.Parent is IfStatementSyntax ifs ? ifs.ToString() : ret.ToString();
                violations.Add($"ApplyRetention: {line} skips a retention deletion without the gateway's refusal");
            }

        return (methods, violations);
    }

    /// <summary>The method's own nodes in source order — not those of a lambda or local function inside it, which
    /// run elsewhere (inside the gateway's delegate, or not at all).</summary>
    private static IEnumerable<SyntaxNode> Own(MethodDeclarationSyntax method) =>
        method.DescendantNodes(n => n is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax);

    private static bool IsGatewayRun(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "RunAsync" } access
        && access.Expression.ToString() == "_automation";

    private static bool IsRefusal(ReturnStatementSyntax ret) =>
        ret.Expression is AwaitExpressionSyntax
        {
            Expression: InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "RefusedAsync" } },
        };

    private static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Whiskers.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Whiskers.slnx not found above the test output.");
    }
}
