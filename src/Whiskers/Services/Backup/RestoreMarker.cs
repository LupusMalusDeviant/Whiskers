namespace Whiskers.Services.Backup;

/// <summary>
/// Commit marker for a pending restore, written atomically as the LAST step of <c>BeginRestoreAsync</c> and
/// consumed by <c>RestoreBootHandler</c> on the next boot. Its presence — and only its presence — authorises
/// the boot handler to swap the staged contents over the live data. Staging without a marker is ignored and
/// garbage-collected (covers a crash during extraction).
/// </summary>
public sealed class RestoreMarker
{
    /// <summary>Absolute path of the staging directory holding the already-decrypted, validated contents.</summary>
    public string StagingDir { get; set; } = "";

    /// <summary>When the restore was committed (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Creation timestamp of the backup being restored (from its manifest), for logging.</summary>
    public DateTime SourceCreatedAtUtc { get; set; }

    /// <summary>Provider recorded in the restored backup's manifest ("sqlite"/"postgres"), for logging.</summary>
    public string Provider { get; set; } = "";
}
