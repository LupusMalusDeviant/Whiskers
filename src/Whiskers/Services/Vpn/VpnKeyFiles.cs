namespace Whiskers.Services.Vpn;

/// <summary>
/// Reads the enrollment keys delivered as files (R1-43): <c>Vpn:Tailscale:AuthKeyFile</c>,
/// <c>Vpn:Netbird:SetupKeyFile</c> — a Docker secret, or the file <c>entrypoint.sh</c> writes from the environment
/// variable. Read once when the options are built; a file wins over the inline value. A file that cannot be read sets
/// <see cref="VpnSettings.KeyFileProblem"/>, so the bring-up fails explicitly instead of running without a key; a file
/// readable by group or others sets <see cref="VpnSettings.KeyFileWarning"/>. Neither text holds the key.
/// </summary>
public static class VpnKeyFiles
{
    public static void Apply(VpnSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.KeyFileProblem = null;
        settings.KeyFileWarning = null;
        if (Read(settings, settings.Tailscale.AuthKeyFile, "Vpn:Tailscale:AuthKeyFile") is { } authKey)
            settings.Tailscale.AuthKey = authKey;
        if (Read(settings, settings.Netbird.SetupKeyFile, "Vpn:Netbird:SetupKeyFile") is { } setupKey)
            settings.Netbird.SetupKey = setupKey;
    }

    private static string? Read(VpnSettings settings, string? path, string setting)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var key = File.ReadAllText(path).Trim();
            if (key.Length == 0)
            {
                settings.KeyFileProblem ??= $"The file named by {setting} ('{path}') is empty.";
                return null;
            }
            if (!OperatingSystem.IsWindows()
                && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead
                                                  | UnixFileMode.OtherWrite)) != 0)
                settings.KeyFileWarning ??= $"The file named by {setting} ('{path}') is readable by group or others; " +
                                            "give it mode 0600 (or 0400) and the user Whiskers runs as.";
            return key;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException)
        {
            // The type only: the key never reaches an exception message, the path does not need one.
            settings.KeyFileProblem ??= $"The file named by {setting} ('{path}') could not be read ({ex.GetType().Name}).";
            return null;
        }
    }
}
