using DotNet.Testcontainers.Containers;

namespace Whiskers.Tests;

/// <summary>
/// R1-47 (TA-50, F-13): the one rule for tests that need a Docker daemon (category <see cref="Category"/>). On a
/// developer machine without Docker they are skipped. On CI (<c>CI</c> set, as GitHub Actions does) a container that
/// does not start is a failure, never a skip: the PostgreSQL metrics and Identity suites are mandatory there, and the
/// CI job additionally refuses a run of this category with any skipped test (<c>.github/scripts/check-test-results.sh</c>).
/// </summary>
internal static class DockerRequirement
{
    /// <summary>The trait value every Docker-backed test class carries: <c>[Trait("Category", "RequiresDocker")]</c>.</summary>
    public const string Category = "RequiresDocker";

    /// <summary>True on CI, where nothing in this category may be skipped.</summary>
    public static bool OnCi => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))
                               && !string.Equals(Environment.GetEnvironmentVariable("CI"), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>Starts <paramref name="container"/>; returns the failure instead of throwing, so the test decides.</summary>
    public static async Task<Exception?> TryStartAsync(IContainer container)
    {
        try
        {
            await container.StartAsync();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>Skips the calling test when the container did not start — except on CI, where that throws.</summary>
    public static void SkipUnlessStarted(Exception? startFailure, string what = "The PostgreSQL test server") =>
        SkipUnlessStarted(startFailure, what, OnCi);

    internal static void SkipUnlessStarted(Exception? startFailure, string what, bool onCi)
    {
        if (startFailure is null) return;
        if (onCi)
            throw new InvalidOperationException($"{what} did not start on CI; this suite is mandatory there.", startFailure);
        Skip.If(true, $"Docker daemon not available ({startFailure.GetBaseException().Message})");
    }

    /// <summary>A missing local prerequisite (e.g. client tools) skips locally and fails on CI.</summary>
    public static void SkipUnlessAvailable(bool available, string reason) => SkipUnlessAvailable(available, reason, OnCi);

    internal static void SkipUnlessAvailable(bool available, string reason, bool onCi)
    {
        if (available) return;
        if (onCi) throw new InvalidOperationException($"{reason} on CI; this suite is mandatory there.");
        Skip.If(true, reason);
    }
}
