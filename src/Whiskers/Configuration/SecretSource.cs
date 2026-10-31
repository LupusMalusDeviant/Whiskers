namespace Whiskers.Configuration;

/// <summary>
/// Reads a secret setting (R1-28): <c>NAME_FILE</c> (a Docker/Kubernetes secret file) wins over <c>NAME</c>, as for
/// <c>WHISKERS_DB_CONNECTION_FILE</c>. A file that is configured but missing, unreadable or empty is an error — never
/// a silent fall back to the plain variable or to no secret at all.
/// </summary>
public static class SecretSource
{
    /// <summary>The secret, or null when neither <c>NAME_FILE</c> nor <c>NAME</c> is set.</summary>
    /// <exception cref="SecretFileException"><c>NAME_FILE</c> is set and its file cannot be read or is empty.</exception>
    public static string? Read(IConfiguration configuration, string name)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var setting = name + "_FILE";
        var path = configuration[setting] ?? Environment.GetEnvironmentVariable(setting);
        if (!string.IsNullOrWhiteSpace(path))
        {
            string value;
            try
            {
                value = File.ReadAllText(path).Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new SecretFileException(setting, ex);
            }
            return value.Length > 0 ? value : throw new SecretFileException(setting, null);
        }
        var plain = configuration[name] ?? Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(plain) ? null : plain;
    }

    /// <summary>The vault passphrase: <c>VAULT_KEY_FILE</c> or <c>VAULT_KEY</c>.</summary>
    public static string? VaultKey(IConfiguration configuration) => Read(configuration, "VAULT_KEY");
}

/// <summary>A configured secret file cannot be used: the app does not start without it (fail closed).</summary>
public sealed class SecretFileException(string setting, Exception? inner)
    : InvalidOperationException($"{setting} is set, but its file cannot be read or is empty. Whiskers does not start "
                                + "without the secret it names: fix the file or remove the setting.", inner)
{
    /// <summary>The setting that names the file, e.g. <c>VAULT_KEY_FILE</c>.</summary>
    public string Setting { get; } = setting;
}
