namespace Whiskers.Models;

public class VolumeBackupEntity
{
    public long Id { get; set; }
    public string BackupId { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string VolumeName { get; set; } = "";
    public string ContainerName { get; set; } = "";
    public string ServerId { get; set; } = "local";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? Notes { get; set; }
}
