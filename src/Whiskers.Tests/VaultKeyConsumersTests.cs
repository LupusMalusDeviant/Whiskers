using System.Text.RegularExpressions;
using Whiskers.Services.Vault;

namespace Whiskers.Tests;

// Contract 0015 Part B: every reader of VAULT_KEY and every use of the vault's master or field key is in
// VaultKeyConsumers.All, so a rotation cannot leave a consumer behind under the old key.
public class VaultKeyConsumersTests
{
    private static readonly Regex KeyUse = new(
        @"SecretSource\.VaultKey\(|\.ProtectField\(|\.TryUnprotectField\(|ProtectedFields\.Read\(|Read\([^)]*""VAULT_KEY",
        RegexOptions.CultureInvariant);

    [Fact]
    public void Every_file_that_reads_the_key_or_uses_the_vaults_keys_is_a_listed_consumer()
    {
        var src = DeploymentScripts.RepoFile("src", "Whiskers");
        var listed = VaultKeyConsumers.All.SelectMany(c => c.Source).ToHashSet(StringComparer.Ordinal);
        var using_ = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".cs", StringComparison.Ordinal) || p.EndsWith(".razor", StringComparison.Ordinal))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(p => KeyUse.IsMatch(File.ReadAllText(p)))
            .Select(p => Path.GetRelativePath(src, p).Replace('\\', '/'))
            .ToList();

        Assert.NotEmpty(using_);
        Assert.Empty(using_.Where(f => !listed.Contains(f)));
    }

    [Fact]
    public void Every_listed_source_exists_and_says_how_it_is_rotated()
    {
        var src = DeploymentScripts.RepoFile("src", "Whiskers");
        foreach (var consumer in VaultKeyConsumers.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(consumer.Rotation), consumer.Name);
            foreach (var file in consumer.Source) Assert.True(File.Exists(Path.Combine(src, file)), file);
        }
    }
}
