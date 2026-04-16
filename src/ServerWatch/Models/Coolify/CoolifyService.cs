using System.Text.Json.Serialization;

namespace ServerWatch.Models.Coolify;

public class CoolifyServiceInfo
{
    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "unknown";

    [JsonPropertyName("created_at")]
    public DateTime? CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
