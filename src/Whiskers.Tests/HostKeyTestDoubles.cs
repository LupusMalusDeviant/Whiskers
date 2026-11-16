using Whiskers.Models;
using Whiskers.Services.Server;


namespace Whiskers.Tests;

/// <summary>Contract 0018 part C: a pins service that accepts every host and records nothing — for onboarding tests
/// whose subject is a later step. It counts the calls so a test can see the HostKey step ran.</summary>
public sealed class AcceptingHostKeyPins : ISshHostKeyPins
{
    public const string Fingerprint = "SHA256:test-accepting-pin";

    public List<string> Calls { get; } = [];

    public SshHostKeyPin? PinOf(ServerConfig server) =>
        server.SshHostKeyFingerprint is { } fp
            ? new SshHostKeyPin(server.SshHostKeyType ?? "ssh-ed25519", fp, server.SshHostKeySource ?? SshHostKeySource.FirstContact,
                server.SshHostKeyPinnedAtUtc, null)
            : null;

    /// <summary>A pin as if read from Whiskers' known_hosts for every endpoint (a host Whiskers has talked to before).</summary>
    public SshHostKeyPin? KnownHostsPin { get; set; }

    /// <summary>When set, <c>VerifyAsync</c> fails with it (a host that serves a different key).</summary>
    public Exception? VerifyFails { get; set; }

    public Task<SshHostKeyPin?> BackfillAsync(ServerConfig server, CancellationToken ct) => Task.FromResult(PinOf(server) ?? KnownHostsPin);

    public Task VerifyAsync(ServerConfig server, CancellationToken ct)
    {
        Calls.Add("verify:" + server.Id);
        return VerifyFails is { } e ? Task.FromException(e) : Task.CompletedTask;
    }

    public Task<(string KeyType, string Fingerprint)?> ServedAsync(ServerConfig server, CancellationToken ct) =>
        Task.FromResult<(string, string)?>(("ssh-ed25519", Fingerprint));

    /// <summary>Runs when a first contact is pinned (to see what had happened before it).</summary>
    public Action? BeforePin { get; set; }

    public Task<SshHostKeyPin> PinFirstContactAsync(ServerConfig server, CancellationToken ct)
    {
        BeforePin?.Invoke();
        Calls.Add("pin:" + server.Id);
        return Task.FromResult(new SshHostKeyPin("ssh-ed25519", Fingerprint, SshHostKeySource.FirstContact, DateTime.UtcNow, null));
    }

    public Task<SshHostKeyPin> PinFromDiskAsync(ServerConfig server, IReadOnlyList<string> publicKeyLines, CancellationToken ct)
    {
        Calls.Add("disk:" + server.Id);
        return Task.FromResult(new SshHostKeyPin("ssh-ed25519", Fingerprint, SshHostKeySource.RescueDisk, DateTime.UtcNow, null));
    }

    public Task<SshHostKeyPin> ReplaceAsync(string serverId, string expectedOldFingerprint, string expectedNewFingerprint,
        CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>A scripted OpenSSH process runner: answers by tool and records every argv, so tests see which tool ran
/// with which arguments and never start a process.</summary>
public sealed class ScriptedSshRunner : ISshProcessRunner
{
    public List<(string File, IReadOnlyList<string> Args)> Runs { get; } = [];

    public Func<string, IReadOnlyList<string>, CommandResult> Answer { get; set; } =
        (_, _) => new CommandResult { ExitCode = 0 };

    public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        Runs.Add((fileName, arguments.ToList()));
        return Task.FromResult(Answer(fileName, arguments));
    }
}
