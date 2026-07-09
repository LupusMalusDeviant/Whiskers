namespace Whiskers.Services;

/// <summary>
/// Marks a singleton that needs an async warm-up before the app starts serving (loading its JSON
/// store, seeding defaults, migrating legacy data, …). The composition root resolves every
/// <see cref="IInitializable"/> and runs <see cref="InitializeAsync"/> in ascending <see cref="Order"/>,
/// replacing the previously hand-wired <c>InitializeAsync</c> calls in <c>Program.cs</c>.
/// </summary>
/// <remarks>
/// RoadToSAP Phase 0 scaffolding. <see cref="Order"/> values mirror the historical Program.cs call
/// order exactly — do not reshuffle without checking that no initializer depends on an earlier one.
/// </remarks>
public interface IInitializable
{
    /// <summary>Ascending run order; lower runs first. Values follow the historical Program.cs order.</summary>
    int Order { get; }

    /// <summary>Warm up the service. Runs once at startup, before the host begins serving requests.</summary>
    Task InitializeAsync(CancellationToken ct);
}
