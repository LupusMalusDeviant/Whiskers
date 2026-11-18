using Microsoft.Extensions.Configuration;
using Whiskers.Mcp;
using Whiskers.Modules;

namespace Whiskers.Tests;

/// <summary>
/// Pins the served MCP surface to a checked-in file (Plan-0013 WP3).
///
/// <para>The per-module counts in <see cref="McpToolRegistrationTests"/> catch a module falling out. This catches
/// the finer moves: a tool renamed, re-levelled, added or dropped. Both exist because the failure mode of this
/// layer is silence — the MCP server shipped zero tools from 0.12.0 to 0.13.0 and no test, log or alert said so.
/// A generated file under version control turns every change to the surface into a diff someone has to accept.</para>
/// </summary>
public class McpToolCatalogSnapshotTests
{
    private const string CatalogPath = "docs/guide/mcp-tool-catalog.md";

    private static IReadOnlyList<IWhiskersModule> EnabledModules() =>
        ModuleCatalog.DiscoverEnabled(new ConfigurationBuilder().Build());

    internal static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Whiskers.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, $"could not find Whiskers.slnx above {AppContext.BaseDirectory}");
        return dir!.FullName;
    }

    [Fact]
    public void Catalog_on_disk_matches_the_tools_the_code_declares()
    {
        var expected = McpToolCatalogRenderer.Render(EnabledModules());
        var path = Path.Combine(RepositoryRoot(), CatalogPath.Replace('/', Path.DirectorySeparatorChar));

        var actual = File.Exists(path) ? File.ReadAllText(path) : null;

        if (actual is null || !string.Equals(Normalise(actual), Normalise(expected), StringComparison.Ordinal))
        {
            // Write the fresh render next to it so the fix is a diff-and-move, not a hand-transcription. The
            // sidecar is never read back by the test — refreshing the catalog stays a human decision, because
            // a snapshot that repairs itself would pin nothing.
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".actual", expected);

            Assert.Fail(actual is null
                ? $"{CatalogPath} is missing — it is generated. A fresh render was written to {CatalogPath}.actual; " +
                  $"review it and move it into place."
                : $"The MCP tool surface changed but {CatalogPath} was not updated.\n" +
                  $"A fresh render was written to {CatalogPath}.actual — diff it, and if the change is intended, " +
                  $"replace the catalog with it in the same commit.");
        }
    }

    [Fact]
    public void Every_catalogued_tool_carries_a_level_and_a_description()
    {
        // An undeclared level renders as "**undeclared**" rather than throwing, so the catalog stays readable
        // even mid-edit. That must never survive into a commit — and a tool without a description is a tool the
        // agent has to guess about, which is how it picks the wrong one.
        var declarations = McpToolLevelCatalog.Declarations(EnabledModules());

        var noLevel = declarations.Where(d => d.Level is null).Select(d => d.ToolName).ToList();
        var noDescription = declarations
            .Where(d => string.IsNullOrWhiteSpace(d.Description))
            .Select(d => d.ToolName)
            .ToList();

        Assert.True(noLevel.Count == 0, "Tools without a declared level: " + string.Join(", ", noLevel));
        Assert.True(noDescription.Count == 0, "Tools without a description: " + string.Join(", ", noDescription));
    }

    [Fact]
    public void Every_tool_is_attributed_to_exactly_one_module()
    {
        var declarations = McpToolLevelCatalog.Declarations(EnabledModules());

        var duplicates = declarations
            .GroupBy(d => d.ToolName, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} (modules: {string.Join(", ", g.Select(d => d.ModuleId))})")
            .ToList();

        Assert.True(duplicates.Count == 0, "Tools claimed by more than one module: " + string.Join("; ", duplicates));
    }

    // Line endings differ between the generator (\n via StringBuilder.AppendLine on the host) and whatever the
    // file was committed with; the surface is the content, not the bytes.
    private static string Normalise(string text) => text.Replace("\r\n", "\n").TrimEnd();
}
