using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Whiskers.Models;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.AuditLog;

/// <summary>The audit trail as one JSON document (R1-23): the verification result, the checkpoints, the chain and
/// every row — what a reader needs to check the trail again outside Whiskers (ADR-0006 gives the hash rules).
/// Streamed row by row, so a long trail is not held in memory.</summary>
public static class AuditExport
{
    /// <summary>The event recorded before an export starts.</summary>
    public const string Action = "audit.export";

    public static async Task WriteAsync(MetricsDbContext db, AuditChainReport report, Stream output, DateTime exportedUtc,
        CancellationToken ct = default)
    {
        await using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = false });
        json.WriteStartObject();
        json.WriteString("format", "whiskers-audit-export/v1");
        json.WriteString("exportedAt", exportedUtc);

        json.WritePropertyName("verification");
        JsonSerializer.Serialize(json, report);

        json.WriteStartArray("checkpoints");
        foreach (var checkpoint in await db.AuditCheckpoints.AsNoTracking().OrderBy(c => c.Seq).ThenBy(c => c.Id)
                     .ToListAsync(ct))
            JsonSerializer.Serialize(json, checkpoint);
        json.WriteEndArray();

        json.WriteStartArray("chain");
        await foreach (var link in db.AuditChain.AsNoTracking().OrderBy(c => c.Seq).AsAsyncEnumerable().WithCancellation(ct))
        {
            JsonSerializer.Serialize(json, link);
            if (json.BytesPending > 64 * 1024) await json.FlushAsync(ct);
        }
        json.WriteEndArray();

        json.WriteStartArray("rows");
        await foreach (var row in db.AuditLog.AsNoTracking().OrderBy(a => a.Id).AsAsyncEnumerable().WithCancellation(ct))
        {
            JsonSerializer.Serialize(json, row);
            if (json.BytesPending > 64 * 1024) await json.FlushAsync(ct);
        }
        json.WriteEndArray();

        json.WriteEndObject();
        await json.FlushAsync(ct);
    }
}
