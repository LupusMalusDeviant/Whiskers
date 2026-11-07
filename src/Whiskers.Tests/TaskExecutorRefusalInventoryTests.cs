using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whiskers.Tests;

/// <summary>
/// INV-45, S-01 (review rounds 10 and 11): the scheduler's refusals before the gateway are found in the source, not
/// kept by hand.
/// <list type="bullet">
/// <item>The task methods are the ones the dispatcher's switch arms call (<c>ExecuteAsync</c>), whatever their names.</item>
/// <item>In each, every <c>return</c> written before the first <c>RunGatedAsync</c> must be
/// <c>return await RefusedAsync(…)</c> — the gateway's Denied event — and no <c>throw</c> may be written there.</item>
/// <item>Only <c>RunGatedAsync</c> calls <c>_automation.RunAsync</c>: it marks the run as having reached the gateway.</item>
/// <item><c>ApplyRetention</c> must exist, and each of its <c>return</c>s follows the gateway's refusal
/// (<c>_automation.RefuseAsync</c>), except "no retention configured" (<c>maxBackups &lt;= 0</c>).</item>
/// </list>
/// Limits, by design: the check reads text, not control flow — "before the gateway" is the source position of the
/// first <c>RunGatedAsync</c> — and sees only returns and throws written in those methods, not exceptions thrown by a
/// method they call, nor a retention that skips by <c>if</c>/<c>else</c>, <c>break</c> or a caught exception. What it
/// cannot see is covered by the dispatcher's backstop (<c>TaskExecutor.ExecuteAsync</c>): any exception before the
/// gateway is one Denied event; the matrix (<c>AuditPathMatrixTests</c>, S-01) proves it case by case.
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
                        2 => await this.RunSilent(t),
                        3 => await ExecuteThrows(t),
                        4 => await ExecuteDirect(t),
                        5 => await ExecuteMissing(t),
                        _ => await RefusedAsync(t, "x", new { }, "unknown"),
                    };
                    return r;
                }

                Task<O> RunGatedAsync(T t, string c, object a, Func<C, Task<int>> w) => _automation.RunAsync(A(t), c, a, w);

                async Task<(bool, string)> ExecuteGood(T t)
                {
                    if (t.Id is null) return await RefusedAsync(t, "c", new { }, "none");
                    Func<int> f = () => { return 1; };
                    var outcome = await RunGatedAsync(t, "c", new { }, _ => Work());
                    if (!outcome.Ok) return (false, "after the decision: the gateway recorded it");
                    return (true, "ok");
                }

                async Task<(bool, string)> RunSilent(T t)
                {
                    if (t.Id is null) return await RefusedAsync(t, "c", new { }, "none");
                    if (t.Bad) return (false, "refused without an event");
                    var outcome = await RunGatedAsync(t, "c", new { }, _ => Work());
                    return (true, "ok");
                }

                async Task<(bool, string)> ExecuteThrows(T t)
                {
                    if (t.Bad) throw new InvalidOperationException("caught by the dispatcher");
                    var outcome = await RunGatedAsync(t, "c", new { }, _ => Work());
                    return (true, "ok");
                }

                async Task<(bool, string)> ExecuteDirect(T t) =>
                    (await _automation.RunAsync(A(t), "c", new { }, _ => Work())).Value;

                async Task ApplyRetention(T t, int maxBackups)
                {
                    if (maxBackups <= 0) return;
                    if (t.Unsafe)
                    {
                        await _automation.RefuseAsync(A(t), "d", new { }, "unsafe");
                        return;
                    }
                    if (t.Other) return;
                    await RunGatedAsync(t, "d", new { }, _ => Work());
                }
            }
            """;

        var (methods, violations) = Check(source);

        Assert.Equal(["ExecuteDirect", "ExecuteGood", "ExecuteMissing", "ExecuteThrows", "RunSilent"],
            methods.Order(StringComparer.Ordinal));
        Assert.Equal(5, violations.Count);
        Assert.Contains(violations, v => v.StartsWith("RunSilent:") && v.Contains("refused without an event"));
        Assert.Contains(violations, v => v.StartsWith("ExecuteThrows:") && v.Contains("throw"));
        Assert.Contains(violations, v => v.StartsWith("ExecuteMissing:") && v.Contains("not found"));
        Assert.Contains(violations, v => v.StartsWith("ExecuteDirect:") && v.Contains("_automation.RunAsync"));
        Assert.Contains(violations, v => v.StartsWith("ApplyRetention:") && v.Contains("if (t.Other) return;"));
        Assert.DoesNotContain(violations, v => v.StartsWith("ExecuteGood:") || v.StartsWith("RunGatedAsync:"));

        // A retention method that is renamed or gone is a failure, not a skipped check.
        var (_, withoutRetention) = Check("""
            class TaskExecutor
            {
                async Task<(bool, string)> ExecuteAsync(T t) => t.Kind switch { _ => await RefusedAsync(t, "x", new { }, "u") };
            }
            """);
        Assert.Equal(["ApplyRetention: not found"], withoutRetention);
    }

    /// <summary>The task methods the dispatcher's switch arms call, and every place one of them — or the retention —
    /// can end before the gateway without the gateway's event.</summary>
    private static (List<string> Methods, List<string> Violations) Check(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var executor = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.Text == "TaskExecutor");
        var members = executor.Members.OfType<MethodDeclarationSyntax>().ToList();
        var dispatcher = members.Single(m => m.Identifier.Text == "ExecuteAsync");
        var methods = dispatcher.DescendantNodes().OfType<SwitchExpressionArmSyntax>()
            .Select(arm => arm.Expression.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault())
            .Select(call => call?.Expression switch
            {
                IdentifierNameSyntax n => n.Identifier.Text,
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } m => m.Name.Identifier.Text,
                _ => null,
            })
            .OfType<string>()
            .Where(n => n != "RefusedAsync")   // an arm that refuses is on record itself
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
                .FirstOrDefault(i => i.Expression is IdentifierNameSyntax { Identifier.Text: "RunGatedAsync" })?.SpanStart ?? int.MaxValue;
            foreach (var node in Own(method).Where(n => n.SpanStart < gateway))
            {
                if (node is ReturnStatementSyntax ret && !IsRefusal(ret))
                    violations.Add($"{name}: {ret} ends the run before the gateway without its event");
                if (node is ThrowStatementSyntax or ThrowExpressionSyntax)
                    violations.Add($"{name}: a throw before the gateway ({node}) — leave it to the dispatcher's backstop or refuse");
            }
        }

        // Only the wrapper asks the gateway directly: a run that bypassed it would not be known to have reached it.
        foreach (var method in members.Where(m => m.Identifier.Text != "RunGatedAsync"))
            foreach (var call in method.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(IsDirectGatewayRun))
                violations.Add($"{method.Identifier.Text}: {call} calls _automation.RunAsync directly, not through RunGatedAsync");

        // A retention deletion skipped by a check is an attempt refused before its decision: the gateway's refusal
        // comes first. Only "no retention configured" returns without one — nothing was attempted.
        var retention = members.FirstOrDefault(m => m.Identifier.Text == "ApplyRetention");
        if (retention is null) violations.Add("ApplyRetention: not found");
        else
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

    private static bool IsDirectGatewayRun(InvocationExpressionSyntax invocation) =>
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
