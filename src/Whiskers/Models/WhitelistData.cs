using System.Text.Json.Serialization;

namespace Whiskers.Models;

public class WhitelistData
{
    public bool Enabled { get; set; }
    public List<string> Emails { get; set; } = new();

    /// <summary>
    /// Contract 0017 H2/H3: the addresses <c>LocalAccountService.CreateAsync</c> added, address → local account id. Only
    /// <c>WhitelistService</c> writes it: <c>SaveWhitelistAsync</c> ignores what a caller passes, keeps the record of
    /// every address that stays listed and drops it for an address the save removes. Absent (not written) while empty,
    /// so a whitelist without such entries is stored as before.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? AddedForAccounts { get; set; }
}
