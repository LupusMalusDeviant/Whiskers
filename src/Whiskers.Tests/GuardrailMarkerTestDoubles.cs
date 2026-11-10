using Whiskers.Services.Guardrails;

namespace Whiskers.Tests;

/// <summary>Contract 0010: the database's record that a guardrail policy existed, in memory, with switchable failures.</summary>
public sealed class InMemoryGuardrailPolicyMarker : IGuardrailPolicyMarker
{
    public DateTime? RecordedAt { get; set; }
    public string? Source { get; private set; }
    public bool Snapshots { get; set; }
    public Exception? ReadFails { get; set; }
    public Exception? WriteFails { get; set; }
    public int Writes { get; private set; }
    public int Reads { get; private set; }

    public Task<DateTime?> RecordedAtAsync(CancellationToken ct = default)
    {
        Reads++;
        if (ReadFails is not null) throw ReadFails;
        return Task.FromResult(RecordedAt);
    }

    public Task<bool> PolicySnapshotsExistAsync(CancellationToken ct = default)
    {
        Reads++;
        if (ReadFails is not null) throw ReadFails;
        return Task.FromResult(Snapshots);
    }

    public Task EnsureAsync(string source, CancellationToken ct = default)
    {
        Writes++;
        if (WriteFails is not null) throw WriteFails;
        if (RecordedAt is null)
        {
            RecordedAt = DateTime.UtcNow;
            Source = source;
        }
        return Task.CompletedTask;
    }
}

public static class GuardrailStoreTestExtensions
{
    /// <summary>Both start phases (contract 0010 D4), as <c>RunWhiskersStartupAsync</c> runs them.</summary>
    public static async Task<GuardrailStore> StartAsync(this GuardrailStore store)
    {
        await store.InitializeAsync();
        await store.ReconcileMarkerAsync();
        return store;
    }

    /// <summary>A store over <paramref name="path"/> with an empty in-memory record: a missing file is a first start.</summary>
    public static GuardrailStore FirstStartStore(string path) => new(filePath: path, marker: new InMemoryGuardrailPolicyMarker());
}
