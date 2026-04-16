using System.Text.Json.Serialization;

namespace ServerWatch.Models.Coolify;

public class CoolifyEnvironmentVar
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("is_build_time")]
    public bool IsBuildTime { get; set; }

    [JsonPropertyName("is_preview")]
    public bool IsPreview { get; set; }
}
