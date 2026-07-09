namespace Whiskers.Models;

public class AuditLogEntity
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Actor { get; set; } = "";          // email or API key name
    public string ActorType { get; set; } = "";       // "web", "mcp", "system"
    public string Action { get; set; } = "";          // "container.start", "server.add", etc.
    public string TargetType { get; set; } = "";      // "container", "server", "config", "firewall"
    public string TargetId { get; set; } = "";        // container ID, server ID, etc.
    public string TargetName { get; set; } = "";      // human-readable name
    public string? Details { get; set; }              // JSON or free-text with additional context
    public string? ServerId { get; set; }
    public bool Success { get; set; } = true;
}
