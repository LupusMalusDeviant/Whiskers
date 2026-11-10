using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Server;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Vault;

namespace Whiskers.Tests;

// Contract 0015 Part A (D2): a stored SSH key is sealed; ssh reads it from a lease in a memory-backed directory that is
// removed after use — also after a failed start and, left by a crash, at the next start.
public sealed class SshKeyLeaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-tests", "lease-" + Guid.NewGuid().ToString("N"));
    private readonly string _shm;
    private readonly DataPathOptions _paths;

    public SshKeyLeaseTests()
    {
        _paths = new DataPathOptions(Path.Combine(_root, "data"));
        _shm = Path.Combine(_root, "shm");
        Directory.CreateDirectory(_paths.RootDir);
        Directory.CreateDirectory(_shm);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private async Task<(ServerConfigService Servers, SealedKeyFiles Keys, ServerConfig Server, string Pem)> WithStoredKeyAsync()
    {
        var keys = new SealedKeyFiles(TestVault.Usable, _paths);
        var servers = new ServerConfigService(Options.Create(new DockerSettings { SocketPath = "unix:///nope.sock" }),
            NullLogger<ServerConfigService>.Instance, dataPaths: _paths, sealedKeys: keys);
        await servers.InitializeAsync();
        await servers.AddServerAsync(new ServerConfig { Id = "edge", Name = "edge", ConnectionType = ConnectionType.SSH, SshHost = "127.0.0.1", SshPort = 1, SshUser = "probe" });
        var pem = SealedKeyFilesTests.NewPem();
        await servers.SaveSshKeyAsync("edge", "id_ecdsa", System.Text.Encoding.UTF8.GetBytes(pem));
        return (servers, keys, servers.GetServer("edge")!, pem);
    }

    private SshKeyLeases Leases(IServerConfigService servers, ISealedKeyFiles keys, bool memoryBacked = true)
        => new(servers, keys, NullLogger<SshKeyLeases>.Instance, _shm, _ => memoryBacked);

    [Fact]
    public async Task An_upload_is_stored_sealed_and_a_lease_holds_the_key_until_it_is_disposed()
    {
        var (servers, keys, server, pem) = await WithStoredKeyAsync();
        var stored = servers.GetSshKeyPath(server)!;
        Assert.True(SealedKeyFiles.IsSealedFile(stored));
        Assert.DoesNotContain("PRIVATE KEY", File.ReadAllText(stored));

        var leases = Leases(servers, keys);
        var lease = await leases.AcquireAsync(server, default);
        Assert.NotNull(lease);
        Assert.StartsWith(leases.LeaseDir, lease.Path);
        Assert.Equal(pem, File.ReadAllText(lease.Path));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(lease.Path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(leases.LeaseDir));
        }

        await lease.DisposeAsync();
        Assert.False(File.Exists(lease.Path));
        Assert.Empty(Directory.EnumerateFiles(leases.LeaseDir));
    }

    [Fact]
    public async Task A_directory_that_is_not_memory_backed_is_refused_and_nothing_is_unsealed()
    {
        var (servers, keys, server, _) = await WithStoredKeyAsync();
        var leases = Leases(servers, keys, memoryBacked: false);
        var refused = await Assert.ThrowsAsync<SealedKeyException>(() => leases.AcquireAsync(server, default));
        Assert.Contains("memory-backed", refused.Message);
        Assert.False(Directory.Exists(leases.LeaseDir) && Directory.EnumerateFiles(leases.LeaseDir).Any());
    }

    [Fact]
    public async Task A_server_without_a_key_gets_no_lease()
    {
        var (servers, keys, _, _) = await WithStoredKeyAsync();
        await servers.AddServerAsync(new ServerConfig { Id = "plain", Name = "plain", ConnectionType = ConnectionType.SSH, SshHost = "127.0.0.1" });
        Assert.Null(await Leases(servers, keys).AcquireAsync(servers.GetServer("plain")!, default));
    }

    [Fact]
    public async Task Leases_left_by_a_crash_are_removed_at_the_next_start()
    {
        var (servers, keys, server, _) = await WithStoredKeyAsync();
        var crashed = Leases(servers, keys);
        var left = await crashed.AcquireAsync(server, default);   // never disposed: the process "crashed"
        Assert.True(File.Exists(left!.Path));
        // A process that is gone left its directory too.
        var gone = Path.Combine(_shm, SshKeyLeases.DirPrefix + int.MaxValue);
        Directory.CreateDirectory(gone);
        File.WriteAllText(Path.Combine(gone, "abc"), "key");

        var next = Leases(servers, keys);
        await next.InitializeAsync();

        Assert.False(File.Exists(left.Path));
        Assert.False(Directory.Exists(gone));
    }

    [Fact]
    public async Task The_lease_is_removed_after_ssh_failed()
    {
        var (servers, keys, server, _) = await WithStoredKeyAsync();
        var leases = Leases(servers, keys);
        var executor = new HostCommandExecutor(servers, new FakeDocker(), NullLogger<HostCommandExecutor>.Instance, leases);

        // Port 1 on loopback refuses (or ssh is not installed): either way the command fails.
        var result = await executor.ExecuteAsync("edge", "true", TimeSpan.FromSeconds(20));

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(!Directory.Exists(leases.LeaseDir) || !Directory.EnumerateFiles(leases.LeaseDir).Any());
    }

    [Fact]
    public async Task An_upload_without_a_usable_vault_is_refused_and_nothing_is_written()
    {
        var off = new VaultService(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<VaultService>.Instance, dataPaths: _paths);
        await off.InitializeAsync();
        var servers = new ServerConfigService(Options.Create(new DockerSettings { SocketPath = "unix:///nope.sock" }),
            NullLogger<ServerConfigService>.Instance, dataPaths: _paths, sealedKeys: new SealedKeyFiles(off, _paths));
        await servers.InitializeAsync();
        await servers.AddServerAsync(new ServerConfig { Id = "edge", Name = "edge", ConnectionType = ConnectionType.SSH, SshHost = "127.0.0.1" });

        await Assert.ThrowsAsync<SealedKeyException>(() =>
            servers.SaveSshKeyAsync("edge", "id_ecdsa", System.Text.Encoding.UTF8.GetBytes(SealedKeyFilesTests.NewPem())));
        Assert.False(Directory.Exists(Path.Combine(_paths.SshKeysDir, "edge")));
        Assert.Null(servers.GetServer("edge")!.SshKeyFileName);
    }

    [Theory]
    [InlineData("/dev/shm/whiskers-ssh-1", "tmpfs")]
    [InlineData("/dev/shm", "tmpfs")]
    [InlineData("/app/data/ssh-keys", "overlay")]
    [InlineData("/run/secrets/x", "ramfs")]
    public void The_file_system_is_read_from_the_longest_mount_point(string dir, string expected)
    {
        const string mountinfo =
            "1 0 0:1 / / rw,relatime - overlay overlay rw,lowerdir=/x\n" +
            "2 1 0:2 / /dev rw,nosuid - tmpfs tmpfs rw\n" +
            "3 2 0:3 / /dev/shm rw,nosuid,nodev - tmpfs shm rw,size=65536k\n" +
            "4 1 0:4 / /run/secrets rw - ramfs none rw\n" +
            "5 1 0:5 / /dev/shmx rw - ext4 /dev/sda1 rw\n";
        Assert.Equal(expected, SshKeyLeases.FileSystemTypeOf(dir, mountinfo));
    }

    [SkippableFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task On_linux_the_real_lease_directory_is_on_tmpfs_with_owner_only_modes()
    {
        Skip.IfNot(OperatingSystem.IsLinux() && Directory.Exists(SshKeyLeases.DefaultBaseDir), "/dev/shm (Linux)");
        Assert.True(SshKeyLeases.IsMemoryBacked(SshKeyLeases.DefaultBaseDir));
        var (servers, keys, server, pem) = await WithStoredKeyAsync();
        var leases = new SshKeyLeases(servers, keys, NullLogger<SshKeyLeases>.Instance);
        await using (var lease = await leases.AcquireAsync(server, default))
        {
            Assert.StartsWith("/dev/shm/whiskers-ssh-", lease!.Path);
            Assert.Equal(pem, File.ReadAllText(lease.Path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(lease.Path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(leases.LeaseDir));
            await lease.DisposeAsync();
            Assert.False(File.Exists(lease.Path));
        }
    }
}
