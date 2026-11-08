namespace Whiskers.Tests;

/// <summary>Test classes whose assertions measure real elapsed time against a bound (a deadline plus its clean-up
/// budget). xUnit runs a collection with parallelization disabled on its own, after the parallel ones, so a starved
/// thread pool on a small CI runner cannot stretch the measurement past a bound the product actually keeps.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WallClockTimingCollection
{
    public const string Name = "WallClockTiming";
}
