using System.Text.RegularExpressions;

namespace Whiskers.Tests;

// R1-29 (INV-38): the three adoption calls write vault.json, servers.json and app-settings.json outside the Mutation
// Gateway. That is designed for the start-up migration only — so no other production code may name them, or a later
// convenience call would be a silent gateway bypass. (The vault's read-back check is private to VaultService.)
public class CredentialAdoptionCallersTests
{
    private static readonly Regex AdoptionCall = new(@"\bAdopt(LegacyCredential|CloudKeyRef|DnsTokenRef|SettingRef|ApiKeyRef)Async\b", RegexOptions.Compiled);

    // Where they are declared and implemented, and their one caller.
    private static readonly string[] Allowed =
    [
        "Services/Vault/IVaultService.cs",
        "Services/Vault/VaultService.cs",
        "Services/ServerConfig/IServerConfigService.cs",
        "Services/ServerConfig/ServerConfigService.cs",
        "Services/Persistence/AppSettingsStore.cs",
        "Services/Agent/AgentSettingsStore.cs",
        "Services/Vault/CredentialVaultMigration.cs",
    ];

    [Fact]
    public void Only_the_start_up_migration_calls_the_adoption_methods()
    {
        var app = Path.Combine(SourceRoot(), "src", "Whiskers");
        var offenders = Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(app, f).Replace('\\', '/'))
            .Where(rel => !Allowed.Contains(rel))
            .Where(rel => AdoptionCall.IsMatch(File.ReadAllText(Path.Combine(app, rel))))
            .ToList();

        Assert.True(offenders.Count == 0, "Adoption methods named outside the migration: " + string.Join(", ", offenders));
        // The scan sees the real caller (it proves the scan, not the rule).
        Assert.Matches(AdoptionCall, File.ReadAllText(Path.Combine(app, "Services", "Vault", "CredentialVaultMigration.cs")));
    }

    private static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Whiskers.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Whiskers.slnx not found above the test output.");
    }
}
