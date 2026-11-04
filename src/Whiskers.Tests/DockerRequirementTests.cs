using System.Text.RegularExpressions;

namespace Whiskers.Tests;

/// <summary>
/// R1-47: the Docker-backed suites (PostgreSQL metrics, Identity, backup/restore) cannot quietly turn into skips on CI.
/// The rule itself is checked, and so is every test file that starts a container: it carries the category CI runs on its
/// own (and counts) and goes through <see cref="DockerRequirement"/> instead of a hand-written <c>Skip</c>.
/// </summary>
public class DockerRequirementTests
{
    [Fact]
    public void A_container_that_did_not_start_fails_on_ci_and_skips_only_locally()
    {
        var failure = new InvalidOperationException("Cannot connect to the Docker daemon");
        var onCi = Assert.Throws<InvalidOperationException>(() => DockerRequirement.SkipUnlessStarted(failure, "The server", onCi: true));
        Assert.Same(failure, onCi.InnerException);
        Assert.Contains("mandatory", onCi.Message);

        Assert.Throws<Xunit.SkipException>(() => DockerRequirement.SkipUnlessStarted(failure, "The server", onCi: false));
        DockerRequirement.SkipUnlessStarted(null, "The server", onCi: true);   // started: nothing happens

        Assert.Throws<InvalidOperationException>(() => DockerRequirement.SkipUnlessAvailable(false, "pg_dump missing", onCi: true));
        Assert.Throws<Xunit.SkipException>(() => DockerRequirement.SkipUnlessAvailable(false, "pg_dump missing", onCi: false));
        DockerRequirement.SkipUnlessAvailable(true, "pg_dump missing", onCi: true);
    }

    [Fact]
    public void Every_test_file_that_starts_a_container_is_in_the_counted_category_and_uses_the_rule()
    {
        var files = Directory.GetFiles(TestsDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"^using Testcontainers\.", RegexOptions.Multiline))
            .ToList();
        // The suites this package makes mandatory must be among them.
        foreach (var expected in new[] { "PostgresSmokeTests.cs", "PostgresIdentityTests.cs", "PostgresBackupRestoreTests.cs" })
            Assert.Contains(files, f => Path.GetFileName(f) == expected);

        var problems = files.SelectMany(f => Violations(Path.GetFileName(f), File.ReadAllText(f))).ToList();
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Theory]
    [InlineData("[Trait(\"Category\", DockerRequirement.Category)]", "")]                               // not in the counted category
    [InlineData("DockerRequirement.SkipUnlessStarted(_startFailure);", "Skip.IfNot(_up, \"Docker daemon not available\");")] // own skip
    public void A_container_test_outside_the_rule_is_caught(string from, string to)
    {
        var text = File.ReadAllText(Path.Combine(TestsDirectory(), "PostgresSmokeTests.cs"));
        Assert.Contains(from, text);
        Assert.NotEmpty(Violations("PostgresSmokeTests.cs", text.Replace(from, to, StringComparison.Ordinal)));
    }

    private static IEnumerable<string> Violations(string file, string text)
    {
        if (!Regex.IsMatch(text, @"\[Trait\(""Category"",\s*(DockerRequirement\.Category|""RequiresDocker"")\)\]"))
            yield return $"{file} starts a container but is not in the '{DockerRequirement.Category}' category CI runs and counts";
        if (!text.Contains("DockerRequirement.SkipUnlessStarted(", StringComparison.Ordinal))
            yield return $"{file} starts a container without DockerRequirement.SkipUnlessStarted (a failed start must fail on CI)";
        if (Regex.IsMatch(text, @"Skip\.(If|IfNot)\("))
            yield return $"{file} skips by hand; go through DockerRequirement so CI fails instead of skipping";
    }

    private static string TestsDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Whiskers.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, "src", "Whiskers.Tests");
    }
}
