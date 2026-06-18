namespace ServerWatch.Configuration;

public class HetznerSettings
{
    public const string SectionName = "Hetzner";

    public bool Enabled { get; set; }

    /// <summary>
    /// Hetzner Cloud API token. Only persisted to disk in plaintext when the Vault
    /// (VAULT_KEY) is NOT available; otherwise it is stored encrypted in the vault and
    /// this field is left blank on disk. Read access via HetznerConfigService.GetToken().
    /// </summary>
    public string ApiToken { get; set; } = "";

    public int PollingIntervalSeconds { get; set; } = 60;

    /// <summary>Outgoing-traffic usage (% of included) above which a server is flagged in the UI.</summary>
    public int TrafficWarnPercent { get; set; } = 80;
}
