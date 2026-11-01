using System.Collections.Concurrent;

namespace Whiskers.Services.Persistence;

/// <summary>A JSON store that could not be read and was recovered from its previous generation (R1-35).</summary>
public sealed record JsonStoreRecoveryEvent(string Path, string PreservedAs, DateTime AtUtc);

/// <summary>
/// What <see cref="JsonFileStore{T}"/> recovered in this process, for the production-readiness check: a recovery is
/// never silent (R1-35). Process-wide because the stores are created outside the container.
/// </summary>
public static class JsonStoreRecovery
{
    private static readonly ConcurrentQueue<JsonStoreRecoveryEvent> Events = new();

    public static IReadOnlyList<JsonStoreRecoveryEvent> Recovered => Events.ToArray();

    /// <summary><see cref="JsonStoreRecoveryEvent.PreservedAs"/> of a store whose file was missing (nothing to keep).</summary>
    public const string Missing = "(missing)";

    internal static void Record(JsonStoreRecoveryEvent recovery)
    {
        Events.Enqueue(recovery);
        Console.Error.WriteLine(recovery.PreservedAs == Missing
            ? $"[store] {recovery.Path} was missing and was restored from its previous generation."
            : $"[store] {recovery.Path} could not be read and was restored from its previous generation; " +
              $"the unreadable file was kept as {recovery.PreservedAs}.");
    }
}

/// <summary>A JSON store whose file and previous generation are both unreadable: the start stops instead of carrying
/// on with an empty (for roles, whitelist or permissions: more permissive) store. The file is left as it is.</summary>
public sealed class JsonStoreCorruptException(string path, string preservedAs, Exception inner)
    : Exception($"{path} cannot be read and has no readable previous generation ({inner.Message}). The file is unchanged " +
        $"(a copy is at {preservedAs}); repair or remove it, or restore a backup.", inner)
{
    public string Path { get; } = path;
}
