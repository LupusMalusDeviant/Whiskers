using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServerWatch.Models.Hetzner;

// ─────────────────────────── Server ───────────────────────────

public class HetznerServer
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("created")] public string? Created { get; set; }
    [JsonPropertyName("rescue_enabled")] public bool RescueEnabled { get; set; }
    [JsonPropertyName("backup_window")] public string? BackupWindow { get; set; }
    [JsonPropertyName("included_traffic")] public long? IncludedTraffic { get; set; }
    [JsonPropertyName("outgoing_traffic")] public long? OutgoingTraffic { get; set; }
    [JsonPropertyName("ingoing_traffic")] public long? IngoingTraffic { get; set; }
    [JsonPropertyName("primary_disk_size")] public int PrimaryDiskSize { get; set; }

    [JsonPropertyName("public_net")] public HetznerPublicNet? PublicNet { get; set; }
    [JsonPropertyName("server_type")] public HetznerServerType? ServerType { get; set; }
    [JsonPropertyName("datacenter")] public HetznerDatacenter? Datacenter { get; set; }
    [JsonPropertyName("image")] public HetznerImage? Image { get; set; }
    [JsonPropertyName("labels")] public Dictionary<string, string>? Labels { get; set; }

    [JsonIgnore] public bool BackupsEnabled => !string.IsNullOrWhiteSpace(BackupWindow);
    [JsonIgnore] public string? Ipv4 => PublicNet?.Ipv4?.Ip;

    /// <summary>Outgoing traffic as a percentage of the included allowance (0 if unknown).</summary>
    [JsonIgnore]
    public double TrafficUsedPercent =>
        IncludedTraffic is > 0 && OutgoingTraffic.HasValue
            ? Math.Round(OutgoingTraffic.Value * 100.0 / IncludedTraffic.Value, 1)
            : 0;
}

public class HetznerPublicNet
{
    [JsonPropertyName("ipv4")] public HetznerIpv4? Ipv4 { get; set; }
    [JsonPropertyName("ipv6")] public HetznerIpv6? Ipv6 { get; set; }
}

public class HetznerIpv4
{
    [JsonPropertyName("ip")] public string? Ip { get; set; }
}

public class HetznerIpv6
{
    [JsonPropertyName("ip")] public string? Ip { get; set; }
}

public class HetznerServerType
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("cores")] public int Cores { get; set; }
    [JsonPropertyName("memory")] public double Memory { get; set; }
    [JsonPropertyName("disk")] public int Disk { get; set; }
}

public class HetznerDatacenter
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("location")] public HetznerLocation? Location { get; set; }
}

public class HetznerLocation
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("city")] public string? City { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }
}

// ─────────────────────────── Images / Snapshots ───────────────────────────

public class HetznerImage
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("image_size")] public double? ImageSize { get; set; }
    [JsonPropertyName("disk_size")] public double? DiskSize { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("created")] public string? Created { get; set; }
    [JsonPropertyName("created_from")] public HetznerCreatedFrom? CreatedFrom { get; set; }
}

public class HetznerCreatedFrom
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

// ─────────────────────────── Actions ───────────────────────────

public class HetznerAction
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("command")] public string? Command { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("progress")] public double Progress { get; set; }
    [JsonPropertyName("error")] public HetznerActionError? Error { get; set; }
}

public class HetznerActionError
{
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

// ─────────────────────────── Firewalls ───────────────────────────

public class HetznerFirewall
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("rules")] public List<HetznerFirewallRule> Rules { get; set; } = new();
    [JsonPropertyName("applied_to")] public List<JsonElement>? AppliedTo { get; set; }
}

public class HetznerFirewallRule
{
    [JsonPropertyName("direction")] public string? Direction { get; set; }
    [JsonPropertyName("protocol")] public string? Protocol { get; set; }
    [JsonPropertyName("port")] public string? Port { get; set; }
    [JsonPropertyName("source_ips")] public List<string>? SourceIps { get; set; }
    [JsonPropertyName("destination_ips")] public List<string>? DestinationIps { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
}

// ─────────────────────────── Metrics ───────────────────────────

public class HetznerMetrics
{
    [JsonPropertyName("start")] public string? Start { get; set; }
    [JsonPropertyName("end")] public string? End { get; set; }
    [JsonPropertyName("step")] public double Step { get; set; }
    [JsonPropertyName("time_series")] public Dictionary<string, HetznerTimeSeries> TimeSeries { get; set; } = new();
}

public class HetznerTimeSeries
{
    // Each entry is [unix_timestamp (number), "value" (string)].
    [JsonPropertyName("values")] public List<List<JsonElement>> Values { get; set; } = new();

    /// <summary>The most recent sample's numeric value, or null if the series is empty/unparsable.</summary>
    [JsonIgnore]
    public double? Latest
    {
        get
        {
            if (Values.Count == 0) return null;
            var last = Values[^1];
            if (last.Count < 2) return null;
            var raw = last[1];
            return raw.ValueKind switch
            {
                JsonValueKind.String => double.TryParse(raw.GetString(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null,
                JsonValueKind.Number => raw.GetDouble(),
                _ => null
            };
        }
    }
}

// ─────────────────────────── Pricing ───────────────────────────

public class HetznerPricing
{
    [JsonPropertyName("currency")] public string? Currency { get; set; }
    [JsonPropertyName("vat_rate")] public string? VatRate { get; set; }
    [JsonPropertyName("traffic")] public string? Traffic { get; set; }
    [JsonPropertyName("server_backup")] public HetznerBackupPricing? ServerBackup { get; set; }
}

public class HetznerBackupPricing
{
    [JsonPropertyName("percentage")] public string? Percentage { get; set; }
}

// ─────────────────────────── Response wrappers ───────────────────────────

public class HetznerServersResponse
{
    [JsonPropertyName("servers")] public List<HetznerServer> Servers { get; set; } = new();
}

public class HetznerServerResponse
{
    [JsonPropertyName("server")] public HetznerServer? Server { get; set; }
}

public class HetznerActionResponse
{
    [JsonPropertyName("action")] public HetznerAction? Action { get; set; }
    [JsonPropertyName("root_password")] public string? RootPassword { get; set; }
    [JsonPropertyName("image")] public HetznerImage? Image { get; set; }
}

public class HetznerImagesResponse
{
    [JsonPropertyName("images")] public List<HetznerImage> Images { get; set; } = new();
}

public class HetznerServerTypesResponse
{
    [JsonPropertyName("server_types")] public List<HetznerServerType> ServerTypes { get; set; } = new();
}

public class HetznerFirewallsResponse
{
    [JsonPropertyName("firewalls")] public List<HetznerFirewall> Firewalls { get; set; } = new();
}

public class HetznerMetricsResponse
{
    [JsonPropertyName("metrics")] public HetznerMetrics? Metrics { get; set; }
}

public class HetznerPricingResponse
{
    [JsonPropertyName("pricing")] public HetznerPricing? Pricing { get; set; }
}
