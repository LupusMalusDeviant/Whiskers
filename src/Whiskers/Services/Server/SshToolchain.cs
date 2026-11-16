using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using Whiskers.Utils;

namespace Whiskers.Services.Server;

/// <summary>
/// Contract 0018: the process seam under the OpenSSH tools (<c>ssh-keyscan</c>, <c>ssh-keygen</c>, <c>ssh</c>). Every
/// argument is one argv element; nothing runs through a shell. Tests replace it.
/// </summary>
public interface ISshProcessRunner
{
    Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);
}

/// <summary>A key pair <c>ssh-keygen</c> wrote: the private key file and the public key line (<c>type key comment</c>).</summary>
public sealed record GeneratedSshKey(string PrivateKeyPath, string PublicKeyLine);

/// <summary>One SSH command to a host outside the server registry (the rescue system), with its own known_hosts file.</summary>
public sealed record RescueSshCall(string Host, int Port, string User, string KeyPath, string KnownHostsFile);

/// <summary>
/// Contract 0018 parts C and D: the OpenSSH operations onboarding needs. Fingerprints are computed by
/// <c>ssh-keygen -l</c>, never by Whiskers' own code; known_hosts lines are looked up with <c>ssh-keygen -F</c> and
/// removed with <c>ssh-keygen -R</c> (they may be hashed).
/// </summary>
public interface ISshToolchain
{
    /// <summary>The host keys the endpoint serves (<c>type key</c>, ed25519 / ecdsa / rsa only); empty when none answered.</summary>
    Task<IReadOnlyList<string>> KeyscanAsync(string host, int port, CancellationToken ct);

    /// <summary>The <c>SHA256:…</c> fingerprint of a public key (<c>type key [comment]</c>), from <c>ssh-keygen -lf</c>.</summary>
    Task<string> FingerprintAsync(string publicKey, CancellationToken ct);

    /// <summary>The known_hosts lines for <paramref name="name"/> (<c>type key</c>), found with <c>ssh-keygen -F</c>.</summary>
    Task<IReadOnlyList<string>> KnownHostsLookupAsync(string name, string knownHostsFile, CancellationToken ct);

    /// <summary>Removes every line of <paramref name="name"/> with <c>ssh-keygen -R</c>.</summary>
    Task KnownHostsRemoveAsync(string name, string knownHostsFile, CancellationToken ct);

    /// <summary>Whether a TCP connection to <paramref name="host"/>:<paramref name="port"/> opens within the timeout.</summary>
    Task<bool> PortOpenAsync(string host, int port, TimeSpan timeout, CancellationToken ct);

    /// <summary>Writes a new ed25519 key without passphrase into <paramref name="directory"/> (<c>ssh-keygen -t ed25519 -N ""</c>).</summary>
    Task<GeneratedSshKey> GenerateEd25519Async(string directory, string comment, CancellationToken ct);

    /// <summary>The public key (<c>type key</c>) of a private key file (<c>ssh-keygen -y</c>).</summary>
    Task<string> PublicKeyOfAsync(string privateKeyPath, CancellationToken ct);

    /// <summary>Runs one command over ssh with the given key and known_hosts file (<c>StrictHostKeyChecking=accept-new</c>).</summary>
    Task<CommandResult> SshAsync(RescueSshCall call, string command, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Host-key helpers shared by the pins, the toolchain and the bootstrap.</summary>
public static class SshHostKeys
{
    /// <summary>The key types Whiskers pins, in preference order (ed25519, ecdsa, rsa).</summary>
    public static int Preference(string keyType) => keyType switch
    {
        "ssh-ed25519" => 0,
        _ when keyType.StartsWith("ecdsa-sha2-", StringComparison.Ordinal) => 1,
        "ssh-rsa" => 2,
        _ => -1,
    };

    /// <summary>The known_hosts name of an endpoint as ssh records it: the bare host for port 22, <c>[host]:port</c> otherwise.</summary>
    public static string EndpointName(string host, int port) =>
        port == 22 ? host : $"[{host}]:{port.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Parses a public key line (<c>[marker] [hostpattern] type key [comment]</c>): the type and the base64 key,
    /// or null for a comment, a marker line or a type Whiskers does not pin.</summary>
    public static (string Type, string Key)? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var t = line.Trim();
        if (t.StartsWith('#') || t.StartsWith('@')) return null;
        var parts = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < parts.Length && i < 2; i++)
            if (Preference(parts[i]) >= 0 && IsBase64(parts[i + 1]))
                return (parts[i], parts[i + 1]);
        return null;
    }

    private static bool IsBase64(string s) =>
        s.Length >= 16 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=');
}

/// <inheritdoc cref="ISshProcessRunner" />
public sealed class SshProcessRunner : ISshProcessRunner
{
    private readonly ILogger<SshProcessRunner> _logger;

    public SshProcessRunner(ILogger<SshProcessRunner> logger) => _logger = logger;

    public async Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        SecretEnvironment.Scrub(psi);
        using var process = new Process { StartInfo = psi };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            process.Start();
            process.StandardInput.Close();   // nothing waits for a prompt (a passphrase or a host-key question)
            var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
            return new CommandResult { ExitCode = process.ExitCode, Output = await stdout, Error = await stderr };
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return new CommandResult { ExitCode = -1, Error = ct.IsCancellationRequested ? HostCommandExecutor.CancelledMessage : $"{fileName} timed out" };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // The arguments are not logged: they may name a key file or a destination.
            _logger.LogWarning("{FileName} could not run: {Error}", fileName, ex.GetType().Name);
            return new CommandResult { ExitCode = -1, Error = $"{fileName} could not run ({ex.GetType().Name})" };
        }
    }
}

/// <inheritdoc cref="ISshToolchain" />
public sealed class SshToolchain : ISshToolchain
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(20);
    private readonly ISshProcessRunner _run;

    public SshToolchain(ISshProcessRunner run) => _run = run;

    public async Task<IReadOnlyList<string>> KeyscanAsync(string host, int port, CancellationToken ct)
    {
        if (!CommandOperands.IsSafeHost(host)) throw new UnsafeOperandException("SSH host", host);
        if (!CommandOperands.IsValidPort(port)) throw new UnsafeOperandException("SSH port", port.ToString(CultureInfo.InvariantCulture));
        var r = await _run.RunAsync("ssh-keyscan",
            ["-T", "10", "-p", port.ToString(CultureInfo.InvariantCulture), "-t", "ed25519,ecdsa,rsa", "--", host], ToolTimeout, ct);
        return Keys(r.Output);
    }

    public async Task<string> FingerprintAsync(string publicKey, CancellationToken ct)
    {
        var parsed = SshHostKeys.Parse(publicKey) ?? throw new InvalidOperationException("Not a public key Whiskers pins.");
        var dir = Path.Combine(Path.GetTempPath(), "whiskers-fp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "key.pub");
            await File.WriteAllTextAsync(file, $"{parsed.Type} {parsed.Key}\n", ct);
            var r = await _run.RunAsync("ssh-keygen", ["-l", "-E", "sha256", "-f", file], ToolTimeout, ct);
            // "256 SHA256:abc… comment (ED25519)"
            var fp = r.Success
                ? r.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(t => t.StartsWith("SHA256:", StringComparison.Ordinal))
                : null;
            return fp ?? throw new InvalidOperationException("ssh-keygen gave no fingerprint for the host key.");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort, public data */ }
        }
    }

    public async Task<IReadOnlyList<string>> KnownHostsLookupAsync(string name, string knownHostsFile, CancellationToken ct)
    {
        if (!File.Exists(knownHostsFile)) return [];
        var r = await _run.RunAsync("ssh-keygen", ["-F", name, "-f", knownHostsFile], ToolTimeout, ct);
        if (r.ExitCode == 1) return [];   // not found
        if (!r.Success) throw new InvalidOperationException("ssh-keygen could not read the known_hosts file.");
        return Keys(r.Output);
    }

    public async Task KnownHostsRemoveAsync(string name, string knownHostsFile, CancellationToken ct)
    {
        if (!File.Exists(knownHostsFile)) return;
        var r = await _run.RunAsync("ssh-keygen", ["-R", name, "-f", knownHostsFile], ToolTimeout, ct);
        if (!r.Success) throw new InvalidOperationException("ssh-keygen could not remove the known_hosts line.");
    }

    public async Task<bool> PortOpenAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (SocketException) { return false; }
    }

    public async Task<GeneratedSshKey> GenerateEd25519Async(string directory, string comment, CancellationToken ct)
    {
        var path = Path.Combine(directory, "id_ed25519");
        var r = await _run.RunAsync("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-C", comment, "-f", path], ToolTimeout, ct);
        if (!r.Success || !File.Exists(path) || !File.Exists(path + ".pub"))
            throw new InvalidOperationException("ssh-keygen could not generate the bootstrap key.");
        return new GeneratedSshKey(path, (await File.ReadAllTextAsync(path + ".pub", ct)).Trim());
    }

    public async Task<string> PublicKeyOfAsync(string privateKeyPath, CancellationToken ct)
    {
        var r = await _run.RunAsync("ssh-keygen", ["-y", "-f", privateKeyPath], ToolTimeout, ct);
        var parsed = r.Success ? SshHostKeys.Parse(r.Output) : null;
        return parsed is { } p ? $"{p.Type} {p.Key}" : throw new InvalidOperationException("The stored bootstrap key cannot be read.");
    }

    public Task<CommandResult> SshAsync(RescueSshCall call, string command, TimeSpan timeout, CancellationToken ct)
    {
        var destination = CommandOperands.SshDestination(call.User, call.Host, call.Port);
        return _run.RunAsync("ssh",
        [
            // Trust on first use against a run-scoped, empty known_hosts file (contract 0018 D step 5): the rescue
            // system's key never enters Whiskers' own known_hosts.
            "-o", "StrictHostKeyChecking=accept-new",
            "-o", $"UserKnownHostsFile={call.KnownHostsFile}",
            "-o", "GlobalKnownHostsFile=/dev/null",
            "-o", "BatchMode=yes",
            "-o", "IdentitiesOnly=yes",
            "-o", "ConnectTimeout=10",
            "-p", call.Port.ToString(CultureInfo.InvariantCulture),
            "-i", call.KeyPath,
            "--", destination, command,
        ], timeout, ct);
    }

    private static IReadOnlyList<string> Keys(string output) =>
        output.Split('\n').Select(SshHostKeys.Parse).Where(p => p is not null)
            .Select(p => $"{p!.Value.Type} {p.Value.Key}").Distinct(StringComparer.Ordinal).ToList();
}
