using System.Reflection;

namespace Whiskers.Utils;

/// <summary>The app's display version, resolved once from the assembly's
/// <see cref="AssemblyInformationalVersionAttribute"/> (the csproj &lt;Version&gt;) with any
/// <c>+git-sha</c> build-metadata suffix trimmed off. Used for the UI version tag.</summary>
public static class AppVersion
{
    /// <summary>e.g. <c>"0.13.1"</c>. Falls back to the numeric assembly version, then <c>"dev"</c>.</summary>
    public static string Display { get; } = Resolve();

    private static string Resolve()
    {
        var informational = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+'); // strip SourceLink's "+<commit-sha>" build metadata
            return plus >= 0 ? informational[..plus] : informational;
        }

        return typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "dev";
    }
}
