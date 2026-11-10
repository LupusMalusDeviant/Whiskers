namespace Whiskers.Tests.ReleaseGate;

// Contract 0008 (R1-51): the types of the mechanical release gate. Everything here is test code; nothing ships.

/// <summary>The four statuses of SECURITY_INVARIANTS.md: ✅ ⚠️ ❌ ❓.</summary>
public enum InvariantStatus { Holds, Partial, Violated, Unverified }

/// <summary>A test named in the test column. A planned entry may name no class yet (INV-18): then Class is null.</summary>
public sealed record TestReference(string? Class, string? Method, bool Planned, string Raw);

public sealed record InvariantRow(string Id, InvariantStatus? Status, string StatusCell,
                                  IReadOnlyList<TestReference> Tests, string Package, int Line);

public sealed record OpenInvariant(string Id, InvariantStatus? Status, string Package, string Reason, string? Reference, int Line);
public sealed record RetiredInvariant(string Id, string DecisionId, DateOnly? Date, int Line);
public sealed record KnownViolation(string Id, InvariantStatus? Status, string DecisionId, string Advisory,
                                    string LimitationLink, string Release, string ReviewBy, int Line);
public sealed record AcceptedException(string Id, InvariantStatus? Shipped, string DecisionId,
                                       string LimitationLink, string ReviewBy, int Line);
public sealed record ReleaseDecision(string Id, string Kind, string Invariant, string? Release, string Status,
                                     DateOnly? Date, string DecidedBy, int Line)
{
    /// <summary>All cells as written, for the immutability comparison (R18).</summary>
    public string Cells { get; init; } = "";
}
public sealed record PlatformSkip(string Test, string SkipsWhen, string? RunsUnskippedIn, string Why, int Line);
public sealed record MandatorySuite(string Workflow, string Job, string? Step, string Proves, int Line);

/// <summary>A row of PRODUCT_CONTRACT.md §7. Class is one of M A S T U.</summary>
public sealed record PathRow(string Id, string Class, string? McpTool, string LevelCell,
                             IReadOnlyList<string> Capabilities, string? NoCapabilityReason, int Line);

/// <summary>One finding of the gate: the rule (R1…R18), what it is about and what to do.</summary>
public sealed record GateFinding(string Rule, string Subject, string Message)
{
    public override string ToString() => $"[{Rule}] {Subject}: {Message}";
}

/// <summary>Each mode is strictly stricter than the one before (contract 0008, D2).</summary>
public enum GateMode { Development, Line, Release }

public sealed record GateVersion(int Major, int Minor, int Patch) : IComparable<GateVersion>
{
    public int CompareTo(GateVersion? other) => other is null ? 1
        : Major != other.Major ? Major.CompareTo(other.Major)
        : Minor != other.Minor ? Minor.CompareTo(other.Minor)
        : Patch.CompareTo(other.Patch);

    public static bool operator <(GateVersion a, GateVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(GateVersion a, GateVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(GateVersion a, GateVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(GateVersion a, GateVersion b) => a.CompareTo(b) >= 0;

    public static readonly GateVersion One = new(1, 0, 0);

    public GateVersion NextPatch() => this with { Patch = Patch + 1 };

    public static GateVersion? TryParse(string? text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text ?? "", @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$");
        return m.Success ? new(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

/// <summary>The tables of docs/v1/RELEASE_GATE.md, with every problem met while reading them.</summary>
public sealed class ReleaseGateTables
{
    public string? Pin { get; init; }
    public int PinLine { get; init; }
    public IReadOnlyList<OpenInvariant> Open { get; init; } = [];
    public IReadOnlyList<RetiredInvariant> Retired { get; init; } = [];
    public IReadOnlyList<KnownViolation> Known { get; init; } = [];
    public IReadOnlyList<AcceptedException> Exceptions { get; init; } = [];
    public IReadOnlyList<PlatformSkip> PlatformSkips { get; init; } = [];
    public IReadOnlyList<MandatorySuite> Suites { get; init; } = [];
    public IReadOnlyList<string> Problems { get; init; } = [];
}

/// <summary>Where a named test is proven: the CI run whose manifest it is rendered into (R9).</summary>
public enum TestRun { InProcess, Docker, ImageRuntime, None }

/// <summary>Everything the gate reads, as text, so every rule can run on mutated copies of the real files.</summary>
public sealed record GateInputs
{
    public required string Invariants { get; init; }              // docs/v1/SECURITY_INVARIANTS.md
    public required string ReleaseGate { get; init; }             // docs/v1/RELEASE_GATE.md
    public required string Contract { get; init; }                // docs/v1/PRODUCT_CONTRACT.md
    public string? PathCapabilities { get; init; }                // docs/v1/PATH_CAPABILITIES.md (D6 b)
    public string? CapabilityInventory { get; init; }             // docs/v1/CAPABILITY_INVENTORY.md (D7, generated)
    /// <summary>docs/v1/CLAIMS.md (contract 0011, Q3): the tests its claims name are required like a ✅ invariant's (R8).</summary>
    public string? Claims { get; init; }
    /// <summary>Workflow file name (ci.yml, release.yml, security.yml) → text.</summary>
    public required IReadOnlyDictionary<string, string> Workflows { get; init; }
    /// <summary>Manifest file name (in-process.txt, docker.txt, image-runtime.txt) → text; a missing one is absent.</summary>
    public required IReadOnlyDictionary<string, string> Manifests { get; init; }
    /// <summary>Repository-relative path → text, or null when there is no such file (limitation links, R11/R17).</summary>
    public required Func<string, string?> ReadDocument { get; init; }
    public required ITestCatalogView Tests { get; init; }
    public required Whiskers.Services.Mutations.ICapabilityCatalog Capabilities { get; init; }
    /// <summary>The names of every MCP tool the enabled modules serve, read tools included.</summary>
    public required IReadOnlyCollection<string> McpTools { get; init; }
    /// <summary><c>MutationGatewayCoverageTests.Inventory</c>: (surface, capability, boundary).</summary>
    public required IReadOnlyList<(string Surface, string Capability, string Boundary)> Surfaces { get; init; }
    /// <summary><c>MutationCompositions.Entries</c>: outer → directly composed operations.</summary>
    public required IReadOnlyList<(string Outer, IReadOnlyList<string> Inner)> Compositions { get; init; }
    public required DateOnly Today { get; init; }
}
