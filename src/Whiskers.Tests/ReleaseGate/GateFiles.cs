using Microsoft.Extensions.Configuration;
using Whiskers.Mcp;
using Whiskers.Modules;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests.ReleaseGate;

/// <summary>The real repository files the gate reads, and the inputs built from them.</summary>
internal static class GateFiles
{
    public const string Invariants = "docs/v1/SECURITY_INVARIANTS.md";
    public const string ReleaseGateDoc = "docs/v1/RELEASE_GATE.md";
    public const string Contract = "docs/v1/PRODUCT_CONTRACT.md";
    public const string PathCapabilities = "docs/v1/PATH_CAPABILITIES.md";
    public const string Inventory = "docs/v1/CAPABILITY_INVENTORY.md";
    public const string Csproj = "src/Whiskers/Whiskers.csproj";
    public static readonly string[] WorkflowFiles = ["ci.yml", "release.yml", "security.yml"];
    public static readonly string[] ManifestFiles = [ReleaseGate.InProcessManifest, ReleaseGate.DockerManifest, ReleaseGate.ImageRuntimeManifest];

    public static string Root => McpToolCatalogSnapshotTests.RepositoryRoot();

    public static string Full(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>A repository file, line endings normalised, or null when it does not exist (or lies outside the repo).</summary>
    public static string? Read(string relative)
    {
        if (relative.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return null;
        var path = Full(relative);
        return File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : null;
    }

    public static string Require(string relative)
        => Read(relative) ?? throw new FileNotFoundException($"{relative} is missing", Full(relative));

    private static readonly Lazy<IReadOnlyCollection<string>> Tools = new(() =>
        McpToolLevelCatalog.Declarations(ModuleCatalog.DiscoverEnabled(new ConfigurationBuilder().Build()))
            .Select(d => d.ToolName).ToHashSet(StringComparer.Ordinal));

    public static GateInputs Inputs() => new()
    {
        Invariants = Require(Invariants),
        ReleaseGate = Read(ReleaseGateDoc) ?? "",
        Contract = Require(Contract),
        PathCapabilities = Read(PathCapabilities),
        CapabilityInventory = Read(Inventory),
        Claims = Read(Whiskers.Tests.Claims.ClaimRules.Register),
        Workflows = WorkflowFiles.ToDictionary(f => f, f => Require($".github/workflows/{f}"), StringComparer.Ordinal),
        Manifests = ManifestFiles.Select(f => (f, Text: Read($".github/required-tests/{f}"))).Where(x => x.Text is not null)
            .ToDictionary(x => x.f, x => x.Text!, StringComparer.Ordinal),
        ReadDocument = Read,
        Tests = TestCatalog.Instance,
        Capabilities = new CapabilityCatalog(),
        McpTools = Tools.Value,
        Surfaces = MutationGatewayCoverageTests.Inventory,
        Compositions = MutationCompositions.Entries,
        Today = DateOnly.FromDateTime(DateTime.UtcNow),
    };

    public static GateVersion Version() => ReleaseGate.ReadVersion(Require(Csproj));
}
