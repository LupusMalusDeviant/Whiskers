using System.Text.RegularExpressions;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0014 (D1, INV-01): an automation actor is built only through its named factories, every runner id is in
/// the <see cref="PrincipalIds"/> scheme, and the ids are the ones the runners always used, byte for byte — so stored
/// owners, suspensions, requesters and the audit history stay continuous.
/// </summary>
public sealed class AutomationActorSchemeTests
{
    public static TheoryData<string, string, string, string?, string?> Factories() => new()
    {
        { "task", AutomationActor.ForTask("t1", "nightly", "user:a@example.com", "agent:s1").RunnerId, "task:t1", "Scheduled task 'nightly'", "agent:s1" },
        { "check", AutomationActor.ForCheck("c1", "web", "user:a@example.com").RunnerId, "check:c1", "Endpoint check 'web'", "user:a@example.com" },
        { "gitdeploy", AutomationActor.ForGitDeploy("g1", "shop", "user:a@example.com").RunnerId, "gitdeploy:g1", "Git deployment 'shop'", "user:a@example.com" },
        { "webhook", AutomationActor.ForWebhook("w1", "deploy", "user:a@example.com", 1).RunnerId, "webhook:w1", "Webhook 'deploy'", "user:a@example.com" },
        { "webhook-removed", AutomationActor.ForRemovedWebhook("w1").RunnerId, "webhook:w1", "Webhook w1 (removed)", null },
        { "autoupdate", AutomationActor.ForAutoUpdate("user:a@example.com").RunnerId, "autoupdate", "AutoUpdate", "user:a@example.com" },
        { "autopatch", AutomationActor.ForAutoPatch("user:a@example.com").RunnerId, "autopatch", "AutoPatch", "user:a@example.com" },
        { "autopatch-preview", AutomationActor.ForAutoPatchPreview("user:a@example.com").RunnerId, "autopatch-preview", "AutoPatch preview", null },
        { "vpn", AutomationActor.ForVpnBootstrap("VPN bring-up", "user:a@example.com").RunnerId, "vpn-bootstrap", "VPN bring-up", "user:a@example.com" },
    };

    private static AutomationActor Build(string factory) => factory switch
    {
        "task" => AutomationActor.ForTask("t1", "nightly", "user:a@example.com", "agent:s1"),
        "check" => AutomationActor.ForCheck("c1", "web", "user:a@example.com"),
        "gitdeploy" => AutomationActor.ForGitDeploy("g1", "shop", "user:a@example.com"),
        "webhook" => AutomationActor.ForWebhook("w1", "deploy", "user:a@example.com", 1),
        "webhook-removed" => AutomationActor.ForRemovedWebhook("w1"),
        "autoupdate" => AutomationActor.ForAutoUpdate("user:a@example.com"),
        "autopatch" => AutomationActor.ForAutoPatch("user:a@example.com"),
        "autopatch-preview" => AutomationActor.ForAutoPatchPreview("user:a@example.com"),
        "vpn" => AutomationActor.ForVpnBootstrap("VPN bring-up", "user:a@example.com"),
        _ => throw new ArgumentOutOfRangeException(nameof(factory)),
    };

    [Theory]
    [MemberData(nameof(Factories))]
    public void Each_factory_keeps_todays_id_and_display_and_is_in_the_scheme(string factory, string runnerId,
        string expectedId, string expectedDisplay, string? expectedCreatedVia)
    {
        Assert.Equal(expectedId, runnerId);
        var actor = Build(factory);
        Assert.Equal(expectedDisplay, actor.DisplayName);
        Assert.Equal(expectedCreatedVia, actor.CreatedVia);
        Assert.NotNull(PrincipalIds.KindOf(actor.RunnerId));
        Assert.NotNull(PrincipalIds.KindOf(PrincipalIds.Unowned(actor.RunnerId)));
    }

    [Fact]
    public void Production_code_builds_actors_only_inside_the_factories()
    {
        var src = Path.Combine(SourceRoot(), "src", "Whiskers");
        var ctor = new Regex(@"new\s+AutomationActor\s*\(|AutomationActor\??\s+\w+\s*=\s*new\s*\(|=>\s*new\s*\(\s*""(task|check|gitdeploy|webhook):");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            var text = File.ReadAllText(file);
            foreach (Match m in ctor.Matches(text))
            {
                // The only constructions allowed are the factories' own `new(` inside AutomationMutationRunner.cs.
                if (Path.GetFileName(file) == "AutomationMutationRunner.cs" && !m.Value.StartsWith("new AutomationActor", StringComparison.Ordinal))
                    continue;
                offenders.Add($"{Path.GetRelativePath(src, file)}: {m.Value}");
            }
        }
        Assert.Empty(offenders);

        // The factories in the runner's file use the private `new(` form only.
        var runner = File.ReadAllText(Path.Combine(src, "Services", "Mutations", "AutomationMutationRunner.cs"));
        Assert.DoesNotContain("new AutomationActor(", runner);
        Assert.Matches(@"internal AutomationActor\(string RunnerId", runner);
    }

    [Fact]
    public void The_automation_ids_are_kinds_of_the_scheme_and_others_are_not()
    {
        Assert.Equal(MutationPrincipalKind.Automation, PrincipalIds.KindOf("task:t1"));
        Assert.Equal(MutationPrincipalKind.Automation, PrincipalIds.KindOf("check:c1"));
        Assert.Equal(MutationPrincipalKind.Automation, PrincipalIds.KindOf("gitdeploy:g1"));
        Assert.Equal(MutationPrincipalKind.Automation, PrincipalIds.KindOf("trigger:x"));
        Assert.Equal(MutationPrincipalKind.Automation, PrincipalIds.KindOf("autoupdate"));
        Assert.Equal(MutationPrincipalKind.Automation, PrincipalIds.KindOf("unowned:task:t1"));
        Assert.Equal(MutationPrincipalKind.Webhook, PrincipalIds.KindOf("webhook:w1"));
        Assert.Equal(MutationPrincipalKind.Webhook, PrincipalIds.KindOf("unowned:webhook:w1"));
        Assert.Equal(MutationPrincipalKind.System, PrincipalIds.KindOf("system:audit-retention"));
        Assert.Equal(MutationPrincipalKind.System, PrincipalIds.KindOf("system:host-cli:restore-backup"));
        Assert.Null(PrincipalIds.KindOf("system:anything"));
        Assert.Null(PrincipalIds.KindOf("run:write"));
        Assert.Null(PrincipalIds.KindOf("unowned:whatever"));
        Assert.Null(PrincipalIds.KindOf("task:"));
        Assert.Throws<ArgumentException>(() => PrincipalIds.System("whatever"));
    }

    internal static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Whiskers.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Whiskers.slnx not found above the test output.");
    }
}
