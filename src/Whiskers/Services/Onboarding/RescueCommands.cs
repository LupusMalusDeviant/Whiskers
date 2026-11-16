using System.Text;

namespace Whiskers.Services.Onboarding;

/// <summary>
/// Contract 0018 part D (INV-41): the fixed scripts of the rescue bootstrap and of <c>BootstrapCleanup</c>. Every value
/// (the public key line, the marker comment, the server id) reaches the shell only base64-encoded and is decoded into a
/// variable; no caller text becomes program text. Unit-tested by <c>RescueCommandsTests</c>.
/// </summary>
public static class RescueCommands
{
    /// <summary>Printed before and after the host keys the inject script reads from the real disk.</summary>
    public const string HostKeysBegin = "WHISKERS_HOSTKEYS_BEGIN";
    public const string HostKeysEnd = "WHISKERS_HOSTKEYS_END";

    /// <summary>Printed by the removal script when no bootstrap line of the server is left.</summary>
    public const string Clean = "WHISKERS_BOOTSTRAP_CLEAN";

    /// <summary>The comment prefix of every bootstrap line of a server; the full comment adds the 8 hex characters of
    /// the run's operation id.</summary>
    public static string MarkerPrefix(string serverId) => $"whiskers-bootstrap-{serverId}-";

    /// <summary>The comment of the bootstrap line of one run.</summary>
    public static string Marker(string serverId, string operationId)
    {
        var hex = new string((operationId ?? "").Where(Uri.IsHexDigit).Select(char.ToLowerInvariant).ToArray());
        if (hex.Length < 8) hex = (hex + Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant())[..8];
        return MarkerPrefix(serverId) + hex[..8];
    }

    /// <summary>
    /// Runs in the rescue system as root: finds the real root partition (the one holding <c>/etc/ssh/sshd_config</c>
    /// and <c>/root</c>), mounts it read-write, appends the public key line to <c>/root/.ssh/authorized_keys</c> (0700 /
    /// 0600 when missing; nothing appended when the key or the marker is already there), prints the real system's
    /// <c>/etc/ssh/ssh_host_*_key.pub</c> between <see cref="HostKeysBegin"/> and <see cref="HostKeysEnd"/>, unmounts and
    /// syncs.
    /// </summary>
    public static string InjectAndReadHostKeys(string publicKeyLine, string marker)
    {
        var line = publicKeyLine.Trim();
        if (line.IndexOfAny(['\n', '\r', '\0']) >= 0) throw new ArgumentException("A key line holds no line break.", nameof(publicKeyLine));
        var blob = line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: >= 2 } parts ? parts[1]
            : throw new ArgumentException("Not a public key line.", nameof(publicKeyLine));
        if (!line.EndsWith(" " + marker, StringComparison.Ordinal)) line = $"{line.Split(' ')[0]} {blob} {marker}";
        return string.Join("\n",
        [
            "set -eu",
            $"K=$(echo {B64(line)} | base64 -d)",
            $"B=$(echo {B64(blob)} | base64 -d)",
            $"M=$(echo {B64(marker)} | base64 -d)",
            "mp=/mnt/whiskers-root",
            "mkdir -p \"$mp\"",
            "umount \"$mp\" 2>/dev/null || true",
            "root=''",
            "for dev in $(lsblk -lnpo NAME,TYPE | awk '$2==\"part\" || $2==\"lvm\" {print $1}'); do",
            "  if mount \"$dev\" \"$mp\" 2>/dev/null; then",
            "    if [ -f \"$mp/etc/ssh/sshd_config\" ] && [ -d \"$mp/root\" ]; then root=\"$dev\"; break; fi",
            "    umount \"$mp\"",
            "  fi",
            "done",
            "[ -n \"$root\" ] || { echo 'no root partition with /etc/ssh/sshd_config and /root found' >&2; exit 3; }",
            "mkdir -p \"$mp/root/.ssh\"",
            "chmod 0700 \"$mp/root/.ssh\"",
            "touch \"$mp/root/.ssh/authorized_keys\"",
            "chmod 0600 \"$mp/root/.ssh/authorized_keys\"",
            "if ! grep -qF \"$B\" \"$mp/root/.ssh/authorized_keys\" && ! grep -qF \"$M\" \"$mp/root/.ssh/authorized_keys\"; then",
            "  printf '%s\\n' \"$K\" >> \"$mp/root/.ssh/authorized_keys\"",
            "fi",
            "grep -qF \"$B\" \"$mp/root/.ssh/authorized_keys\" || { echo 'the key line is not in authorized_keys' >&2; exit 4; }",
            $"echo {HostKeysBegin}",
            "cat \"$mp\"/etc/ssh/ssh_host_*_key.pub",
            $"echo {HostKeysEnd}",
            "sync",
            "umount \"$mp\" || true",   // the line is on the disk already (synced): a failing unmount must not fail the run
        ]);
    }

    /// <summary>The host key lines between the markers of <see cref="InjectAndReadHostKeys"/>'s output.</summary>
    public static IReadOnlyList<string> HostKeysOf(string output)
    {
        var begin = output.IndexOf(HostKeysBegin, StringComparison.Ordinal);
        var end = output.IndexOf(HostKeysEnd, StringComparison.Ordinal);
        if (begin < 0 || end < begin) return [];
        return output[(begin + HostKeysBegin.Length)..end].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => Whiskers.Services.Server.SshHostKeys.Parse(l) is not null).ToList();
    }

    /// <summary>
    /// Runs on the real host as root (mTLS host shell or SSH with the generated key): removes from
    /// <c>/root/.ssh/authorized_keys</c> every line whose comment is <c>whiskers-bootstrap-{serverId}-</c> followed by
    /// exactly 8 hex characters — the lines of every run on this server, never another server's or another comment's —
    /// keeps the file's inode and mode, reads it back and prints <see cref="Clean"/> only when none is left.
    /// </summary>
    public static string RemoveMarkerLines(string serverId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        return string.Join("\n",
        [
            "set -eu",
            $"P=$(echo {B64(MarkerPrefix(serverId))} | base64 -d)",
            "F=/root/.ssh/authorized_keys",
            "ours() { awk -v p=\"$P\" -v want=\"$1\" '{ c=$NF; m=0; if (index(c,p)==1) { r=substr(c,length(p)+1); if (length(r)==8 && r ~ /^[0-9a-f]+$/) m=1 } if (m==want) print }' \"$F\"; }",
            $"if [ ! -f \"$F\" ]; then echo {Clean}; exit 0; fi",
            "T=$(mktemp \"$F.whiskers.XXXXXX\")",
            "ours 0 > \"$T\"",
            "cat \"$T\" > \"$F\"",
            "rm -f \"$T\"",
            "if [ -n \"$(ours 1)\" ]; then echo 'a bootstrap line is still in authorized_keys' >&2; exit 4; fi",
            $"echo {Clean}",
        ]);
    }

    private static string B64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
}
