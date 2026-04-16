namespace ServerWatch.Models;

public class VaultEntry
{
    public string Key { get; set; } = "";
    public string EncryptedValue { get; set; } = "";  // AES-encrypted, base64
    public string? ContainerId { get; set; }          // Optional: linked container
    public string? ContainerName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public int? RotateAfterDays { get; set; }         // Optional: rotation reminder
}

public class VaultData
{
    public List<VaultEntry> Entries { get; set; } = new();
}
