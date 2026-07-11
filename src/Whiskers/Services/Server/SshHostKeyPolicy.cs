using Whiskers.Configuration;

namespace Whiskers.Services.Server;

/// <summary>HOCH-11 (ADR-0002): the single source of the SSH host-key verification options used by
/// ALL three ssh spawn sites (SshTunnelManager, HostCommandExecutor, TerminalSession).
///
/// <c>StrictHostKeyChecking=accept-new</c> is trust-on-first-use: the first contact records the
/// host key into our own known_hosts file (kept under the data dir so it survives container
/// rebuilds); any LATER key change makes ssh fail hard instead of silently talking to a
/// possible man-in-the-middle. Operators can pre-seed the file with <c>ssh-keyscan</c> (done for
/// the existing fleet at rollout) and must remove a host's line after an intentional rebuild
/// (<c>ssh-keygen -R</c>-style) before reconnecting.</summary>
public static class SshHostKeyPolicy
{
    public static string KnownHostsFile(DataPathOptions? paths = null)
        => $"{(paths ?? DataPathOptions.Default).SshKeysDir}/known_hosts";

    /// <summary>The two ssh options every spawn site appends. Also ensures the containing
    /// directory exists (ssh does not create it and would fail otherwise).</summary>
    public static string[] Options(DataPathOptions? paths = null)
    {
        var file = KnownHostsFile(paths);
        try
        {
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch
        {
            // Best effort — if the data dir is broken, ssh will surface a clear error itself.
        }
        return new[]
        {
            "-o", "StrictHostKeyChecking=accept-new",
            "-o", $"UserKnownHostsFile={file}",
        };
    }
}
