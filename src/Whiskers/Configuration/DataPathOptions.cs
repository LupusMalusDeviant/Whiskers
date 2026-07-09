namespace Whiskers.Configuration;

/// <summary>
/// Central resolver for every path under the application data directory. Replaces the hard-coded
/// <c>/app/data</c> literals that used to be scattered across services (DB connection string,
/// JSON stores, DataProtection keys, ssh-keys/, mtls/, backups/, …). The root is configurable via
/// the <c>WHISKERS_DATA_DIR</c> environment variable (or the <c>Whiskers:DataDir</c> config key),
/// defaulting to <c>/app/data</c> so existing container deployments keep working byte-for-byte.
///
/// This is the ONE place a <c>/app/data</c> literal is allowed to live.
///
/// Consumers receive it via DI as an optional last constructor parameter; the container fills it
/// from the registered singleton in production, while tests that pass an explicit path keep working
/// unchanged (a registered type is injected even into a parameter that has a default value; the
/// default is only used when the type is not registered). Where DI is bypassed entirely, fall back
/// to <see cref="Default"/>.
/// </summary>
public sealed class DataPathOptions
{
    /// <summary>Environment variable that overrides the data root.</summary>
    public const string EnvVarName = "WHISKERS_DATA_DIR";

    /// <summary>Configuration key (e.g. appsettings) that overrides the data root.</summary>
    public const string ConfigKey = "Whiskers:DataDir";

    // The single intentional hard-coded default. Kept here — and only here — so the container's
    // default mount (/app/data) resolves identically to before this refactor.
    private const string DefaultRootDir = "/app/data";

    /// <summary>Absolute root directory that all other paths derive from. No trailing separator.</summary>
    public string RootDir { get; }

    public DataPathOptions(string? rootDir = null)
        => RootDir = string.IsNullOrWhiteSpace(rootDir)
            ? DefaultRootDir
            : rootDir.TrimEnd('/', '\\');

    /// <summary>Builds the options from configuration (which already layers in environment variables).
    /// Used at bootstrap, before the DI container exists.</summary>
    public static DataPathOptions FromConfiguration(IConfiguration configuration)
        => new(configuration[EnvVarName] ?? configuration[ConfigKey]);

    /// <summary>
    /// Fallback instance for code paths that are constructed outside DI (unit tests, one-off tools).
    /// Honors <see cref="EnvVarName"/> when set, otherwise resolves to <c>/app/data</c>. Production
    /// code receives the DI-registered instance instead of this one.
    /// </summary>
    public static DataPathOptions Default { get; } =
        new(Environment.GetEnvironmentVariable(EnvVarName));

    // Join with a forward slash rather than Path.Combine: these are POSIX paths inside the Linux
    // container (some end up embedded in remote shell commands), so the separator must stay '/'
    // regardless of the host OS running the process. .NET file IO accepts '/' on Windows too.
    private string P(string relative) => $"{RootDir}/{relative}";

    // --- SQLite metrics database ---
    public string DbPath => P("metrics.db");
    public string DbConnectionString => $"Data Source={DbPath}";

    // --- DataProtection keys (antiforgery token ring) ---
    public string KeysDir => P("keys");

    // --- UI-writable config layers (reload-on-change JSON sources) ---
    public string AgentSettingsJson => P("agent-settings.json");
    public string AppSettingsJson => P("app-settings.json");

    // --- JSON config stores ---
    public string ApiKeysJson => P("api-keys.json");
    public string McpPermissionsJson => P("mcp-permissions.json");
    public string ServersJson => P("servers.json");
    public string VaultJson => P("vault.json");
    public string RolesJson => P("roles.json");
    public string WhitelistJson => P("whitelist.json");
    public string NotificationPrefsJson => P("notification-prefs.json");
    public string GuardrailsJson => P("guardrails.json");
    public string CveFindingsJson => P("cve-findings.json");
    public string AiTriggersJson => P("ai-triggers.json");

    // --- Directories ---
    public string SshKeysDir => P("ssh-keys");
    public string MtlsDir => P("mtls");
    public string BackupsDir => P("backups");
    public string ChatDir => P("chat");
    public string AgentChatDir => P("agent-chat");

    // --- Mesh-VPN state (VpnSettings defaults derive from these) ---
    public string TailscaleStateDir => P("tailscale");
    public string NetbirdConfigPath => P("netbird/config.json");
}
