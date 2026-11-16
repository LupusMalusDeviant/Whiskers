using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Models.Hetzner;
using Whiskers.Services.Hetzner;
using Whiskers.Services.Onboarding;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0018 part D: a Hetzner rescue bootstrap against a scripted provider, a scripted OpenSSH toolchain and a
/// temporary server store, for the test classes the contract names (<see cref="CloudBindingResolutionTests"/>,
/// <see cref="OnboardingSecretsSinkTests"/>). Every provider call records its VM id; the rescue answer carries a
/// password made per run.
/// </summary>
internal sealed class RescueBootstrapRig : IDisposable
{
    public const long VmId = 20;
    public static readonly string HostKey = "ssh-ed25519 " + FakeSshToolchain.KeyText('h');

    public TempServerStore Store { get; } = new();
    public FakeSshToolchain Ssh { get; } = new();
    public AcceptingHostKeyPins Pins { get; } = new();
    public ScriptedProxy<IHetznerService> Cloud { get; }
    public IHetznerService Hetzner { get; }
    public string Password { get; } = "rescue-" + Guid.NewGuid().ToString("N");
    public ListLogger Log { get; } = new();
    public List<(string Method, long Id)> VmCalls { get; } = [];
    private readonly string _keyBase;

    public HetznerServer Vm { get; } = new()
    {
        Id = VmId, Name = "web-new", Status = "running", Created = "2026-01-02T03:04:05+00:00",
        PublicNet = new HetznerPublicNet { Ipv4 = new HetznerIpv4 { Ip = "203.0.113.20" } },
    };

    public RescueBootstrapRig()
    {
        _keyBase = Path.Combine(Store.Root, "shm");
        Directory.CreateDirectory(_keyBase);
        (Hetzner, Cloud) = ScriptedProxy<IHetznerService>.Create();
        Cloud.On(nameof(IHetznerService.GetServerAsync), a => { VmCalls.Add(("get", (long)a[1]!)); return Vm.Id == (long)a[1]! ? Vm : null; })
            .On(nameof(IHetznerService.CreateSshKeyAsync), _ => new HetznerSshKey { Id = 77 })
            .On(nameof(IHetznerService.EnableRescueWithKeysAsync), a => { VmCalls.Add(("rescue", (long)a[1]!)); return new HetznerActionResponse { RootPassword = Password }; })
            .On(nameof(IHetznerService.RebootAsync), a => { VmCalls.Add(("reboot", (long)a[1]!)); return null; })
            .On(nameof(IHetznerService.ResetAsync), a => { VmCalls.Add(("reset", (long)a[1]!)); return null; })
            .On(nameof(IHetznerService.DisableRescueAsync), a => { VmCalls.Add(("unrescue", (long)a[1]!)); return null; });
        Ssh.PortOpen = n => n % 2 == 0;   // each reboot: port 22 goes away once, then answers
        Ssh.LastScan = [HostKey];
        Ssh.SshAnswer = (_, _) => new CommandResult
        {
            ExitCode = 0, Output = $"{RescueCommands.HostKeysBegin}\n{HostKey} root@web-new\n{RescueCommands.HostKeysEnd}\n",
        };
    }

    public void Dispose() => Store.Dispose();

    public async Task<ServerConfig> RecordAsync(string id = "web-new", long? vmId = VmId, string ip = "203.0.113.20")
    {
        var reference = $"cloudkey:{id}:{Guid.NewGuid():N}";
        await Store.Vault.SetSecretAsync(reference, "tok-" + Guid.NewGuid().ToString("N"));
        await Store.Servers.AddServerAsync(new ServerConfig
        {
            Id = id, Name = "web-new", ConnectionType = ConnectionType.SSH, SshHost = ip, SshUser = "root", SshPort = 22,
            CloudProvider = CloudProvider.Hetzner, CloudServerId = vmId, CloudKeyRef = reference,
        });
        return Store.Servers.GetServer(id)!;
    }

    public Task<CloudBootstrapResult> RunAsync(ServerConfig server) =>
        new HetznerRescueBootstrap(new TestServices(Hetzner, Store.Vault), Store.Servers, Ssh,
            new SshKeyLeases(Store.Servers, Store.Keys, NullLogger<SshKeyLeases>.Instance, _keyBase, _ => true),
            Pins, OnboardingOptionsFor.Settings(), Log, _keyBase, _ => true)
        {
            Delay = (_, _) => Task.CompletedTask,
        }.PrepareAsync(server, new OnboardingOptions(BootstrapMethod.HetznerRescueKey), new Progress<OnboardingProgress>(), default);

    /// <summary>Provider calls other than reads.</summary>
    public List<string> Mutations() => Cloud.Calls.Where(c => c is not nameof(IHetznerService.GetServerAsync)).ToList();

    internal sealed class ListLogger : ILogger<HetznerRescueBootstrap>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }
}
