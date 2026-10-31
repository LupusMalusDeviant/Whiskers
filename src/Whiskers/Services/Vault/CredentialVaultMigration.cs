using System.Text.Json.Nodes;
using Whiskers.Configuration;
using Whiskers.Services.Cloud;
using Whiskers.Services.Persistence;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Services.Vault;

/// <summary>
/// Start-up migration of R1-29 (INV-52): moves the plaintext cloud API keys of servers.json and the plaintext DNS token
/// of app-settings.json into the vault. Each credential is written to the vault, read back from vault.json on disk and
/// compared, and only then is the plaintext replaced by the vault key in its file — so an interrupted run leaves
/// either the plaintext (and runs again at the next start, under the same vault key) or the finished state, never a
/// credential that exists nowhere. Without a usable vault nothing moves; the credentials stay usable and the readiness
/// check reports them as plaintext.
/// </summary>
/// <remarks>
/// A migration of data already stored, at start and before anything is served — like the vault's own legacy
/// conversion. There is no principal and no decision to take, and nothing changes but where a credential is kept, so
/// it uses the stores' narrow adoption calls (each accepts only a fixed migration key, or only replaces exactly the
/// plaintext it is given by a vault key) — never a general vault or settings write.
/// </remarks>
public sealed class CredentialVaultMigration : IInitializable
{
    private readonly IVaultService _vault;
    private readonly IServerConfigService _servers;
    private readonly IAppSettingsStore _appSettings;
    private readonly ILogger<CredentialVaultMigration> _logger;

    public CredentialVaultMigration(IVaultService vault, IServerConfigService servers, IAppSettingsStore appSettings,
        ILogger<CredentialVaultMigration> logger)
    {
        _vault = vault;
        _servers = servers;
        _appSettings = appSettings;
        _logger = logger;
    }

    /// <summary>After the vault (40) and the server registry (50).</summary>
    public int Order => 55;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var plaintextServers = _servers.GetServers().Where(s => s.HasPlaintextCloudKey()).ToList();
        var dns = await _appSettings.ReadSectionAsync(DnsSettings.SectionName);
        var dnsPlaintext = PlaintextDnsToken(dns);
        if (plaintextServers.Count == 0 && dnsPlaintext is null) return;

        if (!_vault.IsEnabled)
        {
            _logger.LogWarning("{Count} credential(s) are stored in plaintext ({Servers} cloud API key(s), DNS token: {Dns}) and are "
                               + "not moved into the vault: it is {State}. Set the right VAULT_KEY and restart.",
                plaintextServers.Count + (dnsPlaintext is null ? 0 : 1), plaintextServers.Count, dnsPlaintext is not null,
                _vault.KeyState == VaultKeyState.Wrong ? "locked" : "disabled");
            return;
        }

        foreach (var server in plaintextServers)
        {
            ct.ThrowIfCancellationRequested();
            var vaultKey = CredentialRefs.MigratedCloudKey(server.Id);
            var what = $"cloud API key of server {server.Id}";
            if (!await StoreAsync(vaultKey, server.CloudApiKey!, what)) continue;
            var key = server.CloudApiKey!;
            if (await AdoptAsync(() => _servers.AdoptCloudKeyRefAsync(server.Id, key, vaultKey), what))
                _logger.LogInformation("Cloud API key of server {ServerId} moved into the vault", server.Id);
        }

        if (dnsPlaintext is not null && await StoreAsync(CredentialRefs.MigratedDnsToken, dnsPlaintext, "DNS token")
            && await AdoptAsync(() => _appSettings.AdoptDnsTokenRefAsync(dnsPlaintext, CredentialRefs.MigratedDnsToken), "DNS token"))
            _logger.LogInformation("DNS token moved into the vault");
    }

    /// <summary>Replaces the plaintext in its file by the vault key. A file that cannot be rewritten leaves the plaintext
    /// where it was (still usable, reported, retried at the next start) and does not stop the start.</summary>
    private async Task<bool> AdoptAsync(Func<Task<bool>> adopt, string what)
    {
        try
        {
            return await adopt();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Vault migration: the {What} is in the vault, but its file could not be rewritten; it stays in "
                                 + "plaintext until the next start", what);
            return false;
        }
    }

    /// <summary>Writes <paramref name="value"/> to the vault and reads it back from vault.json on disk.</summary>
    private async Task<bool> StoreAsync(string vaultKey, string value, string what)
    {
        try
        {
            if (await _vault.AdoptLegacyCredentialAsync(vaultKey, value)) return true;
            _logger.LogError("Vault migration: the {What} could not be read back from vault.json; it stays in plaintext", what);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(ex, "Vault migration: the {What} could not be stored in the vault; it stays in plaintext", what);
        }
        return false;
    }

    /// <summary>The plaintext DNS token stored in app-settings.json, when the section names no vault key.</summary>
    private static string? PlaintextDnsToken(JsonObject? section)
    {
        if (section is null) return null;
        string? Field(string name) => section.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value
            is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (!string.IsNullOrWhiteSpace(Field(nameof(DnsSettings.ApiTokenRef)))) return null;
        var token = Field(nameof(DnsSettings.ApiToken));
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }
}
