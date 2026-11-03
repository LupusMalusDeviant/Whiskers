using System.Text;

namespace Whiskers.Services.Vpn;

/// <summary>
/// An enrollment key handed to a VPN CLI as a file (R1-43, F-21): <c>tailscale up --auth-key=file:&lt;path&gt;</c>,
/// <c>netbird up --setup-key-file=&lt;path&gt;</c>. A command line is readable by every process on the host (and the
/// container runs with <c>pid: host</c>); the file is not. It lives in a fresh directory created with mode 0700 (one
/// that already exists, or that is a link, is refused), is created with mode 0600 in the same call (no window in which
/// it is wider; both modes are checked before the key is written), holds the key without a newline, and is deleted
/// with its directory as soon as the CLI returned. It goes to memory-backed storage when there is one
/// (<c>WHISKERS_SECRET_DIR</c>, else <c>/dev/shm</c>, else the temp directory) and is not flushed to disk; a directory
/// left behind by a crash or a kill during the call is removed at the next start (<see cref="RemoveStale"/>). On
/// Windows the modes do not apply (the temp directory is the user's own).
/// </summary>
public sealed class VpnSecretFile : IDisposable
{
    /// <summary>Mode of the file: owner read/write only.</summary>
    public const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Mode of the directory around it: owner only.</summary>
    public const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Name prefix of the directories, also what <see cref="RemoveStale"/> removes.</summary>
    public const string DirectoryPrefix = "whiskers-vpn-";

    /// <summary>Overrides where the key files go (also honoured by <c>entrypoint.sh</c>).</summary>
    public const string ParentVariable = "WHISKERS_SECRET_DIR";

    private readonly string _directory;
    private readonly ILogger? _logger;

    private VpnSecretFile(string directory, string path, ILogger? logger)
    {
        _directory = directory;
        Path = path;
        _logger = logger;
    }

    /// <summary>The file's absolute path. Not a secret: it is what goes on the command line.</summary>
    public string Path { get; }

    /// <summary>Where key files go when no parent is given: only <see cref="ParentVariable"/> when it is set, else
    /// <c>/dev/shm</c> (memory, never written to the container layer) when it exists, then the temp directory.</summary>
    public static IReadOnlyList<string> DefaultParents()
    {
        // A configured directory is the only one: key files go nowhere else, and only there are leftovers looked for.
        if (Environment.GetEnvironmentVariable(ParentVariable) is { Length: > 0 } configured) return [configured];
        var parents = new List<string>();
        if (!OperatingSystem.IsWindows() && Directory.Exists("/dev/shm")) parents.Add("/dev/shm");
        parents.Add(System.IO.Path.GetTempPath());
        return parents;
    }

    /// <summary>Writes <paramref name="secret"/> to a new private file under <paramref name="parent"/> (default: the
    /// first usable of <see cref="DefaultParents"/>). Throws when the file cannot be created privately; nothing is left
    /// behind then.</summary>
    public static VpnSecretFile Create(string secret, string? parent = null, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        if (!string.IsNullOrWhiteSpace(parent)) return CreateIn(parent, secret, logger);
        Exception? last = null;
        foreach (var candidate in DefaultParents())
        {
            try { return CreateIn(candidate, secret, logger); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { last = ex; }
        }
        throw new IOException("No directory could hold the VPN key file privately.", last);
    }

    private static VpnSecretFile CreateIn(string root, string secret, ILogger? logger)
    {
        var directory = System.IO.Path.Combine(root, DirectoryPrefix + Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "key");
        // Never into a directory that is already there (or a link to one): it would not be ours.
        if (Directory.Exists(directory) || File.Exists(directory))
            throw new IOException("The VPN key directory already exists.");
        var created = false;
        try
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, DirectoryMode);
            created = true;
            if (!OperatingSystem.IsWindows()
                && (new DirectoryInfo(directory).LinkTarget is not null || File.GetUnixFileMode(directory) != DirectoryMode))
                throw new IOException("The VPN key directory could not be created with mode 0700.");

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
                // Not flushed to disk: the file lives for one CLI call, in memory where that is possible.
                stream.Write(Encoding.UTF8.GetBytes(secret.Trim()));
            }
            return new VpnSecretFile(directory, path, logger);
        }
        catch
        {
            if (created) Delete(directory, path, logger);
            throw;
        }
    }

    /// <summary>Removes key directories a previous process left behind (a crash or a kill during the CLI call) under
    /// <paramref name="parents"/> (default: <see cref="DefaultParents"/>). Returns how many were removed.</summary>
    public static int RemoveStale(IEnumerable<string>? parents = null, ILogger? logger = null)
    {
        var removed = 0;
        foreach (var parent in (parents ?? DefaultParents()).Distinct(StringComparer.Ordinal))
        {
            string[] stale;
            try { stale = Directory.Exists(parent) ? Directory.GetDirectories(parent, DirectoryPrefix + "*") : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var directory in stale)
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                    removed++;
                    logger?.LogWarning("[vpn] removed a VPN key directory left behind by an earlier start: {Path}", directory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger?.LogWarning("[vpn] a VPN key directory left behind could not be removed: {Path}", directory);
                }
            }
        }
        return removed;
    }

    public void Dispose() => Delete(_directory, Path, _logger);

    private static void Delete(string directory, string path, ILogger? logger)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The path only: a key file that stays is something to know about. The next start removes it.
            logger?.LogWarning("[vpn] the VPN key file could not be removed: {Path}", path);
        }
    }
}
