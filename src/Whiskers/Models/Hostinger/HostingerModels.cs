using System.Text.Json.Serialization;

namespace Whiskers.Models.Hostinger;

public class HostingerVm
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("hostname")] public string? Hostname { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("cpus")] public int? Cpus { get; set; }
    [JsonPropertyName("memory")] public long? Memory { get; set; }   // MB
    [JsonPropertyName("disk")] public long? Disk { get; set; }       // MB
    [JsonPropertyName("ipv4")] public List<HostingerIp>? Ipv4 { get; set; }
    [JsonPropertyName("template")] public HostingerTemplate? Template { get; set; }
    [JsonPropertyName("plan")] public string? Plan { get; set; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }

    [JsonIgnore] public string? PrimaryIpv4 => Ipv4?.FirstOrDefault()?.Address;
}

public class HostingerIp
{
    [JsonPropertyName("address")] public string? Address { get; set; }
}

public class HostingerTemplate
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
}

public class HostingerSnapshot
{
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
    [JsonPropertyName("expires_at")] public string? ExpiresAt { get; set; }
}
