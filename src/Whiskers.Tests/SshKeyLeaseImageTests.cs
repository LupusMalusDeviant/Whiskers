namespace Whiskers.Tests;

/// <summary>
/// Contract 0015 Part A (D2), image runtime suite: in the built image, <c>/dev/shm</c> is memory-backed, the app user's
/// <c>ssh -i &lt;lease&gt;</c> with a 0600 key in a 0700 lease directory there authenticates against a test sshd, and the
/// lease is gone afterwards. The sshd is installed into a throw-away container of the same image (the image itself ships
/// only the client). Runs in CI's <c>image-runtime</c> job on amd64 and arm64 (<c>WHISKERS_IMAGE</c>).
/// </summary>
public class SshKeyLeaseImageTests
{
    [SkippableFact]
    [Trait("Category", "ImageRuntime")]
    public void Ssh_authenticates_with_a_lease_in_dev_shm_and_the_lease_is_removed()
    {
        var image = ImageRuntimeToolsTests.ImageUnderTest();
        const string script =
            "set -e; " +
            "apt-get update -qq >/dev/null 2>&1; " +
            "DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends openssh-server >/dev/null 2>&1; " +
            "mkdir -p /run/sshd; ssh-keygen -A >/dev/null; " +
            "useradd -m -s /bin/sh probe; " +
            "ssh-keygen -q -t ed25519 -N '' -f /tmp/probe; " +
            "mkdir -p /home/probe/.ssh; cp /tmp/probe.pub /home/probe/.ssh/authorized_keys; " +
            "chown -R probe /home/probe/.ssh; chmod 700 /home/probe/.ssh; chmod 600 /home/probe/.ssh/authorized_keys; " +
            "/usr/sbin/sshd -p 2222; " +
            // The lease directory as SshKeyLeases makes it: memory-backed, 0700, the key 0600, owned by the app user.
            "grep -Eq ' /dev/shm .* - (tmpfs|ramfs) ' /proc/self/mountinfo && echo shm-memory-backed; " +
            "L=/dev/shm/whiskers-ssh-image-test; mkdir -m 700 \"$L\"; F=\"$L/$(head -c 16 /dev/urandom | od -An -tx1 | tr -d ' \\n')\"; " +
            "install -m 600 /tmp/probe \"$F\"; rm -f /tmp/probe; chown -R 10001:10001 \"$L\"; " +
            "setpriv --reuid=10001 --regid=10001 --clear-groups env HOME=/tmp ssh -i \"$F\" -p 2222 -o BatchMode=yes " +
            "-o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR -- probe@127.0.0.1 'echo whiskers-ssh-$((6*7))'; " +
            // Disposed: overwritten and deleted.
            "head -c \"$(stat -c %s \"$F\")\" /dev/zero > \"$F\"; rm -f \"$F\"; test ! -e \"$F\" && echo lease-removed";
        var (exit, stdout, stderr) = ImageRuntimeToolsTests.Docker(
            ["run", "--rm", "--user", "0", "--entrypoint", "sh", image, "-c", script], timeoutMs: 600_000);
        var output = stdout + stderr;
        Assert.True(exit == 0, $"the lease check in the image failed (exit {exit}):\n{output}");
        Assert.Contains("shm-memory-backed", stdout);
        Assert.Contains("whiskers-ssh-42", stdout);
        Assert.Contains("lease-removed", stdout);
    }
}
