using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Vault;

namespace Whiskers.Services.Server;

/// <summary>
/// Contract 0015 Part A (D2): the external <c>ssh</c> process needs its key as a file (<c>-i</c>). The key is sealed at
/// rest; for one use it is unsealed into a fresh file in a memory-backed directory (<c>/dev/shm/whiskers-ssh-&lt;pid&gt;/</c>,
/// 0700, the file 0600 with a random name) that is deleted when the lease is disposed. A directory that is not
/// <c>tmpfs</c>/<c>ramfs</c> is refused: a plaintext key never reaches a disk.
/// </summary>
public interface ISshKeyLeases
{
    /// <summary>Unseals the server's key into a fresh file in a memory-backed directory (D2) and returns its path; the
    /// file is deleted when the lease is disposed. Null when the server has no key. Throws
    /// <see cref="SealedKeyException"/> with a reason when the key cannot be opened or no memory-backed directory exists.</summary>
    Task<SshKeyLease?> AcquireAsync(Models.ServerConfig server, CancellationToken ct);
}

/// <summary>One unsealed SSH key file; deleted (after its bytes are overwritten) on dispose.</summary>
public sealed class SshKeyLease : IAsyncDisposable, IDisposable
{
    private int _disposed;

    internal SshKeyLease(string path) => Path = path;

    public string Path { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        SshKeyLeases.Remove(Path);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <inheritdoc cref="ISshKeyLeases" />
public sealed class SshKeyLeases : ISshKeyLeases, IInitializable
{
    /// <summary>The memory-backed directory the leases live in (Linux).</summary>
    public const string DefaultBaseDir = "/dev/shm";
    internal const string DirPrefix = "whiskers-ssh-";

    private static readonly UnixFileMode DirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly UnixFileMode FileMode600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly IServerConfigService _servers;
    private readonly ISealedKeyFiles _keys;
    private readonly ILogger<SshKeyLeases> _logger;
    private readonly string _baseDir;
    private readonly Func<string, bool> _isMemoryBacked;
    private readonly int _pid = Environment.ProcessId;

    public SshKeyLeases(IServerConfigService servers, ISealedKeyFiles keys, ILogger<SshKeyLeases> logger)
        : this(servers, keys, logger, DefaultBaseDir, null)
    {
    }

    /// <summary>Tests: another base directory and another judgement of "memory-backed".</summary>
    internal SshKeyLeases(IServerConfigService servers, ISealedKeyFiles keys, ILogger<SshKeyLeases> logger, string baseDir,
        Func<string, bool>? isMemoryBacked)
    {
        _servers = servers;
        _keys = keys;
        _logger = logger;
        _baseDir = baseDir;
        _isMemoryBacked = isMemoryBacked ?? IsMemoryBacked;
    }

    /// <summary>This process's lease directory.</summary>
    public string LeaseDir => Path.Combine(_baseDir, DirPrefix + _pid.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>After the start migration of the keys (56): leases left by a crashed process are removed before anything runs ssh.</summary>
    public int Order => 57;

    public Task InitializeAsync(CancellationToken ct = default)
    {
        RemoveStaleLeases();
        return Task.CompletedTask;
    }

    /// <summary>Removes the lease directories of processes that no longer run, and every lease in this process's own
    /// directory (a process with the same id before a restart left them). Returns how many files were removed.</summary>
    public int RemoveStaleLeases()
    {
        if (!Directory.Exists(_baseDir)) return 0;
        var removed = 0;
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(_baseDir, DirPrefix + "*").ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        foreach (var dir in dirs)
        {
            if (!int.TryParse(Path.GetFileName(dir)[DirPrefix.Length..], out var pid)) continue;
            if (pid != _pid && IsRunning(pid)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    Remove(file);
                    removed++;
                }
                if (pid != _pid) Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("A stale SSH key lease directory could not be removed: {Error}", ex.GetType().Name);
            }
        }
        if (removed > 0) _logger.LogWarning("Removed {Count} SSH key lease file(s) left by an earlier process", removed);
        return removed;
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    public Task<SshKeyLease?> AcquireAsync(Models.ServerConfig server, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        ct.ThrowIfCancellationRequested();
        var stored = _servers.GetSshKeyPath(server);
        if (stored is null || string.IsNullOrEmpty(server.SshKeyFileName)) return Task.FromResult<SshKeyLease?>(null);

        // The directory is checked before the key is opened: nothing is unsealed that could not be kept off disk.
        var dir = PrepareDirectory();
        using var material = _keys.Read(stored, SealedKeyPurposes.SshKey(server.Id, server.SshKeyFileName));
        var path = Path.Combine(dir, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());
        var bytes = new byte[Encoding.UTF8.GetMaxByteCount(material.Pem.Length)];
        try
        {
            var n = Encoding.UTF8.GetBytes(material.Pem, bytes);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FileMode600;
            try
            {
                using var stream = new FileStream(path, options);
                stream.Write(bytes, 0, n);
                stream.Flush();
            }
            catch
            {
                Remove(path);
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
        return Task.FromResult<SshKeyLease?>(new SshKeyLease(path));
    }

    private string PrepareDirectory()
    {
        var dir = LeaseDir;
        try
        {
            if (!Directory.Exists(_baseDir) || !_isMemoryBacked(_baseDir))
                throw new SealedKeyException($"SSH with a stored key needs a memory-backed directory ({_baseDir} on tmpfs); "
                                             + "none is available, so the key is not unsealed.");
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
            else
            {
                Directory.CreateDirectory(dir, DirMode);
                File.SetUnixFileMode(dir, DirMode);   // an existing directory keeps no wider mode
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SealedKeyException($"The SSH key lease directory cannot be prepared ({ex.GetType().Name}).");
        }
        return dir;
    }

    /// <summary>Overwrites a lease file's bytes and deletes it; never throws.</summary>
    internal static void Remove(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var length = new FileInfo(path).Length;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.Write(new byte[length]);
                stream.Flush();
            }
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(path); } catch (Exception again) when (again is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Whether <paramref name="dir"/> lies on tmpfs or ramfs (read from <c>/proc/self/mountinfo</c>).</summary>
    public static bool IsMemoryBacked(string dir)
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            return FileSystemTypeOf(dir, File.ReadAllText("/proc/self/mountinfo")) is "tmpfs" or "ramfs";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The file-system type of the mount that holds <paramref name="dir"/>: the longest mount point that is a
    /// prefix of it (the last one listed wins for the same point). Null when none matches.</summary>
    public static string? FileSystemTypeOf(string dir, string mountinfo)
    {
        var target = dir.TrimEnd('/');
        if (target.Length == 0) target = "/";
        string? best = null;
        var bestLength = -1;
        foreach (var line in mountinfo.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // "36 35 98:0 /mnt1 /mnt2 rw,noatime master:1 - ext3 /dev/root rw,errors=continue"
            var parts = line.Split(' ');
            var separator = Array.IndexOf(parts, "-");
            if (parts.Length < 5 || separator < 0 || separator + 1 >= parts.Length) continue;
            var mountPoint = parts[4].Replace("\\040", " ", StringComparison.Ordinal);
            var covers = mountPoint == "/" || target == mountPoint || target.StartsWith(mountPoint + "/", StringComparison.Ordinal);
            if (!covers || mountPoint.Length < bestLength) continue;
            best = parts[separator + 1];
            bestLength = mountPoint.Length;
        }
        return best;
    }
}
