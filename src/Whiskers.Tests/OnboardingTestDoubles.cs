using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Services.Server;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Vault;

namespace Whiskers.Tests;

/// <summary>Contract 0018 tests: an interface stand-in whose methods are answered by name. A handler returns the plain
/// result (wrapped into a completed task for async members) or throws (a faulted task); an unknown member returns the
/// default. Every call is recorded by name, in order.</summary>
/// <summary>Forwards every call to a real <typeparamref name="T"/>, except the methods given a handler (e.g. a store
/// write that fails).</summary>
public class ForwardingProxy<T> : DispatchProxy where T : class
{
    private T _target = null!;
    private Dictionary<string, Func<object?[], object?>> _handlers = new(StringComparer.Ordinal);

    public static T Create(T target, Dictionary<string, Func<object?[], object?>> handlers)
    {
        var service = DispatchProxy.Create<T, ForwardingProxy<T>>();
        var proxy = (ForwardingProxy<T>)(object)service;
        proxy._target = target;
        proxy._handlers = handlers;
        return service;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var m = method!;
        if (_handlers.TryGetValue(m.Name, out var handler)) return handler(args ?? []);
        try { return m.Invoke(_target, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
    }
}

public class ScriptedProxy<T> : DispatchProxy where T : class
{
    public Dictionary<string, Func<object?[], object?>> Handlers { get; } = new(StringComparer.Ordinal);
    public List<string> Calls { get; } = [];

    public static (T Service, ScriptedProxy<T> Script) Create()
    {
        var service = DispatchProxy.Create<T, ScriptedProxy<T>>();
        return (service, (ScriptedProxy<T>)(object)service);
    }

    public ScriptedProxy<T> On(string method, Func<object?[], object?> handler)
    {
        Handlers[method] = handler;
        return this;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var m = method!;
        Calls.Add(m.Name);
        var type = m.ReturnType;
        var inner = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>) ? type.GetGenericArguments()[0] : null;
        object? value;
        try
        {
            value = Handlers.TryGetValue(m.Name, out var handler) ? handler(args ?? []) : Default(inner ?? type);
        }
        catch (Exception ex)
        {
            if (type == typeof(Task)) return Task.FromException(ex);
            if (inner is not null)
                return typeof(Task).GetMethods().Single(x => x.Name == nameof(Task.FromException) && x.IsGenericMethod)
                    .MakeGenericMethod(inner).Invoke(null, [ex]);
            throw;
        }
        if (value is Task) return value;
        if (type == typeof(Task)) return Task.CompletedTask;
        if (inner is not null)
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [value ?? Default(inner)]);
        return value;
    }

    private static object? Default(Type t) => t == typeof(void) || !t.IsValueType ? null : Activator.CreateInstance(t);
}

/// <summary>A scripted <see cref="ISshToolchain"/>: keyscan answers, fingerprints derived from the key text, a
/// known_hosts held in memory per file, and a record of every ssh call. No process starts.</summary>
public sealed class FakeSshToolchain : ISshToolchain
{
    /// <summary>What ssh-keyscan returns per call; the last entry repeats.</summary>
    public Queue<IReadOnlyList<string>> Scans { get; } = new();
    public IReadOnlyList<string> LastScan { get; set; } = [];
    public Func<int, bool> PortOpen { get; set; } = _ => true;
    public int PortProbes { get; private set; }
    public List<(RescueSshCall Call, string Command)> SshCalls { get; } = [];
    public Func<RescueSshCall, string, CommandResult> SshAnswer { get; set; } = (_, _) => new CommandResult { ExitCode = 0 };
    public List<string> GeneratedIn { get; } = [];
    public string GeneratedPublicKey { get; set; } = "ssh-ed25519 " + KeyText('g');

    /// <summary>A base64-looking key body made of one repeated character, so tests can tell keys apart.</summary>
    public static string KeyText(char c) => "AAAAC3NzaC1lZDI1NTE5AAAA" + new string(c, 43);

    /// <summary>The fingerprint the fake computes: derived from the key body only (as ssh-keygen -l ignores comments).</summary>
    public static string FingerprintOf(string line) =>
        SshHostKeys.Parse(line) is { } p ? "SHA256:fp-" + p.Key[^6..] + "-" + p.Type : throw new InvalidOperationException("not a key");

    public Task<IReadOnlyList<string>> KeyscanAsync(string host, int port, CancellationToken ct)
    {
        if (Scans.Count > 0) LastScan = Scans.Dequeue();
        return Task.FromResult(LastScan);
    }

    /// <summary>When set, <c>FingerprintAsync</c> fails with it (a process that cannot start, a tool that answers nothing).</summary>
    public Exception? FingerprintFails { get; set; }

    public Task<string> FingerprintAsync(string publicKey, CancellationToken ct) =>
        FingerprintFails is { } e ? Task.FromException<string>(e) : Task.FromResult(FingerprintOf(publicKey));

    /// <summary>Reads the real file (plain lines only, like <c>ssh-keygen -F</c> for unhashed entries).</summary>
    public Task<IReadOnlyList<string>> KnownHostsLookupAsync(string name, string knownHostsFile, CancellationToken ct)
    {
        Lookups.Add((name, knownHostsFile));
        return Task.FromResult<IReadOnlyList<string>>(File.Exists(knownHostsFile)
            ? File.ReadAllLines(knownHostsFile).Where(l => l.StartsWith(name + " ", StringComparison.Ordinal))
                .Select(l => l[(name.Length + 1)..]).ToList()
            : []);
    }

    public List<(string Name, string File)> Lookups { get; } = [];

    public Task KnownHostsRemoveAsync(string name, string knownHostsFile, CancellationToken ct)
    {
        if (File.Exists(knownHostsFile))
            File.WriteAllLines(knownHostsFile, File.ReadAllLines(knownHostsFile).Where(l => !l.StartsWith(name + " ", StringComparison.Ordinal)));
        return Task.CompletedTask;
    }

    public Task<bool> PortOpenAsync(string host, int port, TimeSpan timeout, CancellationToken ct) =>
        Task.FromResult(PortOpen(++PortProbes));

    public async Task<GeneratedSshKey> GenerateEd25519Async(string directory, string comment, CancellationToken ct)
    {
        GeneratedIn.Add(directory);
        var path = Path.Combine(directory, "id_ed25519");
        await File.WriteAllTextAsync(path, SealedKeyFilesTests.NewPem(), ct);
        await File.WriteAllTextAsync(path + ".pub", GeneratedPublicKey + " " + comment + "\n", ct);
        return new GeneratedSshKey(path, GeneratedPublicKey + " " + comment);
    }

    public Task<string> PublicKeyOfAsync(string privateKeyPath, CancellationToken ct) => Task.FromResult(GeneratedPublicKey);

    public Task<CommandResult> SshAsync(RescueSshCall call, string command, TimeSpan timeout, CancellationToken ct)
    {
        SshCalls.Add((call, command));
        return Task.FromResult(SshAnswer(call, command));
    }
}

/// <summary>A server store on disk with its own started vault and sealed key files, removed on dispose.</summary>
internal sealed class TempServerStore : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "whiskers-o2-" + Guid.NewGuid().ToString("N"));
    public DataPathOptions Paths { get; }
    public VaultService Vault { get; }
    public ServerConfigService Servers { get; }
    public SealedKeyFiles Keys { get; }

    public TempServerStore()
    {
        Directory.CreateDirectory(Root);
        Paths = new DataPathOptions(Root);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["VAULT_KEY"] = "test-vault-" + Guid.NewGuid().ToString("N") })
            .Build();
        Vault = new VaultService(config, NullLogger<VaultService>.Instance, Path.Combine(Root, "vault.json"));
        Vault.InitializeAsync().GetAwaiter().GetResult();
        Servers = new ServerConfigService(Options.Create(new DockerSettings { SocketPath = "unix:///nope.sock" }),
            NullLogger<ServerConfigService>.Instance, storePath: Path.Combine(Root, "servers.json"), dataPaths: Paths,
            sealedKeys: Keys = new SealedKeyFiles(Vault, Paths));
        Servers.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Fixed onboarding settings for a test.</summary>
internal static class OnboardingOptionsFor
{
    public static IOptionsMonitor<OnboardingSettings> Settings(bool enabled = true, string? authKeyRef = null) =>
        new StaticOptionsMonitor<OnboardingSettings>(new OnboardingSettings
        {
            Tailscale = new OnboardingTailscaleSettings { AuthKeyRef = authKeyRef },
            CloudBootstrap = new CloudBootstrapSettings { Enabled = enabled, SoftRebootSeconds = 9, BootTimeoutSeconds = 9 },
        });
}
