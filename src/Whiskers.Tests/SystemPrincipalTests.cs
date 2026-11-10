using System.Text.RegularExpressions;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0014 (D6, INV-01): <c>System</c> is used for a closed list of components; every <c>system:</c> id the
/// source builds is on it, and a new component needs a contract change.
/// </summary>
public sealed class SystemPrincipalTests
{
    [Fact]
    public void The_component_list_is_the_one_the_contract_names()
    {
        Assert.Equal(["approval-expiry", "audit-retention", "authority-check", "startup"],
            PrincipalIds.SystemComponents.Order(StringComparer.Ordinal));
        Assert.Equal(["confirm-identity-binding", "recover-local-account", "restore-backup"],
            PrincipalIds.HostCliCommands.Order(StringComparer.Ordinal));
        Assert.Equal("system:host-cli:restore-backup", PrincipalIds.HostCli("restore-backup"));
        Assert.Throws<ArgumentException>(() => PrincipalIds.HostCli("anything"));
        Assert.Throws<ArgumentException>(() => PrincipalIds.System("scheduler"));
    }

    [Fact]
    public void Every_system_component_the_source_names_is_registered()
    {
        var src = Path.Combine(AutomationActorSchemeTests.SourceRoot(), "src", "Whiskers");
        var named = new Regex(@"(?:PrincipalIds|AuditPrincipals)\.System\(\s*""([^""]+)""|(?:PrincipalIds|AuditPrincipals)\.HostCli\(\s*""([^""]+)""|""system:([a-z\-:]+)""");
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            foreach (Match m in named.Matches(File.ReadAllText(file)))
            {
                var component = m.Groups[1].Success ? m.Groups[1].Value
                    : m.Groups[2].Success ? PrincipalIds.HostCliComponent + m.Groups[2].Value
                    : m.Groups[3].Value;
                found.Add(component);
                Assert.True(PrincipalIds.KindOf(PrincipalIds.SystemPrefix + component) == MutationPrincipalKind.System,
                    $"{Path.GetFileName(file)} names the system component '{component}', which contract 0014 does not list");
            }
        }
        // The rows the contract moved to System are there.
        foreach (var expected in new[] { "audit-retention", "authority-check", "startup", "approval-expiry",
                     "host-cli:recover-local-account", "host-cli:confirm-identity-binding" })
            Assert.Contains(expected, found);
    }
}
