using System.Text;

namespace Whiskers.Services.Vpn;

/// <summary>
/// An enrollment key handed to a VPN CLI as a file (R1-43, F-21): <c>tailscale up --auth-key=file:&lt;path&gt;</c>,
/// <c>netbird up --setup-key-file=&lt;path&gt;</c>. A command line is readable by every process on the host (and the
/// container runs with <c>pid: host</c>); the file is not. It lives in a fresh directory created with mode 0700, is
/// created with mode 0600 in the same call (no window in which it is wider), holds the key without a newline, and is
/// deleted with its directory as soon as the CLI returned. On Linux a file whose mode is not exactly 0600 is refused
/// before the key is written. On Windows the modes do not apply (the temp directory is the user's own).
/// </summary>
public sealed class VpnSecretFile : IDisposable
{
    /// <summary>Mode of the file: owner read/write only.</summary>
    public const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Mode of the directory around it: owner only.</summary>
    public const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly string _directory;

    private VpnSecretFile(string directory, string path)
    {
        _directory = directory;
        Path = path;
    }

    /// <summary>The file's absolute path. Not a secret: it is what goes on the command line.</summary>
    public string Path { get; }

    /// <summary>Writes <paramref name="secret"/> to a new private file under <paramref name="parent"/> (default: the
    /// system temp directory). Throws when the file cannot be created privately; nothing is left behind then.</summary>
    public static VpnSecretFile Create(string secret, string? parent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var root = string.IsNullOrWhiteSpace(parent) ? System.IO.Path.GetTempPath() : parent;
        var directory = System.IO.Path.Combine(root, "whiskers-vpn-" + Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "key");
        try
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, DirectoryMode);

            var options = new FileStreamOptions
            {
                Mode = System.IO.FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FileMode;
            using (var stream = new FileStream(path, options))
            {
                // Checked before the key is written: a umask can only narrow the mode, but a file system that ignores
                // it (or a mount that forces wider modes) must not receive the key.
                if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(path) != FileMode)
                    throw new IOException("The VPN key file could not be created with mode 0600.");
                var bytes = Encoding.UTF8.GetBytes(secret.Trim());
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            return new VpnSecretFile(directory, path);
        }
        catch
        {
            Delete(directory, path);
            throw;
        }
    }

    public void Dispose() => Delete(_directory, Path);

    private static void Delete(string directory, string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort; temp dir */ }
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort; temp dir */ }
    }
}
