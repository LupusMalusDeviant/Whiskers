using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Hetzner;
using Whiskers.Services.Certificates;
using Whiskers.Services.Cloud;
using Whiskers.Services.Hetzner;
using Whiskers.Services.Mutations;
using Whiskers.Services.Server;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Vault;

namespace Whiskers.Services.Onboarding;

/// <summary>
/// Contract 0018 part D (D1, D2, INV-64): the Hetzner rescue bootstrap. Generates an ed25519 key in a memory-backed
/// directory and stores it sealed, creates a project SSH key, boots the VM into rescue with it, appends the public key to
/// the real system's <c>/root/.ssh/authorized_keys</c>, reads the real host keys from its disk, boots back and pins those
/// keys. Whatever happens, the project key is deleted, rescue disabled and the VM reset once more when it is still in
/// rescue after a failure. The rescue root password Hetzner returns is never kept, logged or returned.
/// </summary>
public sealed class HetznerRescueBootstrap : ICloudBootstrap
{
    private static readonly TimeSpan RescueCommandTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly IServiceProvider _services;
    private readonly IServerConfigService _servers;
    private readonly ISshToolchain _ssh;
    private readonly ISshKeyLeases _leases;
    private readonly ISshHostKeyPins _pins;
    private readonly IOptionsMonitor<OnboardingSettings> _settings;
    private readonly ILogger<HetznerRescueBootstrap> _logger;
    private readonly string _keyBaseDir;
    private readonly Func<string, bool> _isMemoryBacked;

    public HetznerRescueBootstrap(IServiceProvider services, IServerConfigService servers, ISshToolchain ssh,
        ISshKeyLeases leases, ISshHostKeyPins pins, IOptionsMonitor<OnboardingSettings> settings,
        ILogger<HetznerRescueBootstrap> logger)
        : this(services, servers, ssh, leases, pins, settings, logger, SshKeyLeases.DefaultBaseDir, null)
    {
    }

    /// <summary>Tests: another memory-backed base directory and judgement.</summary>
    internal HetznerRescueBootstrap(IServiceProvider services, IServerConfigService servers, ISshToolchain ssh,
        ISshKeyLeases leases, ISshHostKeyPins pins, IOptionsMonitor<OnboardingSettings> settings,
        ILogger<HetznerRescueBootstrap> logger, string keyBaseDir, Func<string, bool>? isMemoryBacked)
    {
        _services = services;
        _servers = servers;
        _ssh = ssh;
        _leases = leases;
        _pins = pins;
        _settings = settings;
        _logger = logger;
        _keyBaseDir = keyBaseDir;
        _isMemoryBacked = isMemoryBacked ?? SshKeyLeases.IsMemoryBacked;
    }

    /// <summary>Test seam: how the polls wait.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public BootstrapMethod Method => BootstrapMethod.HetznerRescueKey;

    public async Task<CloudBootstrapResult> PrepareAsync(Models.ServerConfig server, OnboardingOptions options,
        IProgress<OnboardingProgress> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);
        var warnings = new List<string>();
        var settings = _settings.CurrentValue.CloudBootstrap;
        void Say(string message) => progress.Report(new OnboardingProgress(OnboardingStep.CloudBootstrap, message));
        CloudBootstrapResult Refuse(string error) => new(false, error, null, null, false, warnings);

        // 1) Preconditions — fail closed, before any provider call that changes something.
        if (!settings.Enabled) return Refuse("The cloud bootstrap is switched off (Onboarding:CloudBootstrap:Enabled).");
        if (server.CloudProvider != CloudProvider.Hetzner) return Refuse("The rescue bootstrap needs a Hetzner server.");
        if (server.CloudServerId is not { } vmId) return Refuse("The record is bound to no Hetzner VM; register it with its IP first.");
        if (server.SshHostKeyFingerprint is not null || _pins.PinOf(server) is not null)
            return Refuse("The record already has a pinned host key: the rescue bootstrap runs on fresh servers only (D2).");
        try
        {
            // A known_hosts line for the endpoint means Whiskers has talked to this host before: not fresh (D2).
            if (await _pins.BackfillAsync(server, ct) is not null)
                return Refuse("Whiskers' known_hosts already holds a host key for this address: the rescue bootstrap runs on fresh servers only (D2).");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return Refuse($"The known_hosts file could not be checked: {ex.Message}");
        }
        if (MtlsOnboardingState.Holds(server, _services.GetService<IMtlsClientCredentialStore>()))
            return Refuse("The record already holds mTLS state: the rescue bootstrap runs on fresh servers only (D2).");
        if (_servers.GetServers().Any(s => s.Id != server.Id && s.CloudProvider == CloudProvider.Hetzner && s.CloudServerId == vmId))
            return Refuse("Another server record is bound to the same VM.");
        var ip = server.SshHost?.Trim();
        if (string.IsNullOrEmpty(ip)) return Refuse("The record has no public IP.");
        if (_services.GetService<IHetznerService>() is not { } hetzner) return Refuse("The Hetzner client is not available.");
        string token;
        try
        {
            token = CloudCredentials.KeyOf(server, _services.GetRequiredService<IVaultService>())
                    ?? throw new InvalidOperationException("The record has no Hetzner token.");
        }
        catch (InvalidOperationException ex) { return Refuse(ex.Message); }

        HetznerServer? vm;
        try { vm = await hetzner.GetServerAsync(token, vmId, ct); }
        catch (HttpRequestException ex) { return Refuse(ex.Message); }
        if (vm is null) return Refuse($"Hetzner knows no VM {vmId} any more.");
        if (!string.Equals(vm.Ipv4, ip, StringComparison.Ordinal)) return Refuse("IP and VM no longer match.");
        if (!string.Equals(vm.Status, "running", StringComparison.OrdinalIgnoreCase)) return Refuse($"The VM is '{vm.Status}', not running.");
        if (!string.Equals(vm.Name, server.Name, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.ConfirmVmName, vm.Name, StringComparison.Ordinal))
            return Refuse($"The IP belongs to VM '{vm.Name}', not '{server.Name}'.");
        var nameDiffered = !string.Equals(vm.Name, server.Name, StringComparison.OrdinalIgnoreCase);
        MutationScope.Note($"cloud bootstrap: Hetzner VM {vm.Id} '{vm.Name}' at {ip}" + (nameDiffered ? " (name differs from the record; confirmed)" : ""));

        // 2) Key: a new one, or on a resume after the injection the very key of the earlier run.
        var marker = RescueCommands.Marker(server.Id, MutationScope.Current?.OperationId ?? Guid.NewGuid().ToString("N"));
        string publicLine;
        var generatedNow = false;
        if (server.BootstrapKeyInjectedFingerprint is { } injected)
        {
            var reused = await ReusableKeyAsync(server, injected, ct);
            if (reused is null)
                return Refuse("The bootstrap key of the earlier run is gone; abandon the onboarding first (Remove bootstrap access).");
            publicLine = $"{reused} {marker}";
        }
        else
        {
            try
            {
                publicLine = await GenerateAndSealAsync(server, marker, ct);
                generatedNow = true;
            }
            catch (Exception ex) when (ex is SealedKeyException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                return Refuse($"The bootstrap key could not be prepared: {ex.Message}");
            }
        }
        string? injectedFingerprint = null;
        long? projectKeyId = null;
        var rescueEnabled = false;
        var inRescue = false;
        var injectedNow = server.BootstrapKeyInjectedFingerprint is not null;
        var leftInRescue = false;
        var succeeded = false;
        string? error = null;
        SshHostKeyPin? pin = null;
        try
        {
            // Inside the try: a failure here still drops the key generated above.
            injectedFingerprint = await _ssh.FingerprintAsync(publicLine, ct);

            // 3) Project key (also proves the token can write).
            HetznerSshKey key;
            try
            {
                key = await hetzner.CreateSshKeyAsync(token, marker, publicLine,
                    new Dictionary<string, string> { ["managed-by"] = "whiskers" }, ct);
            }
            catch (HttpRequestException ex) { throw new BootstrapStepException($"The project SSH key could not be created: {ex.Message}"); }
            projectKeyId = key.Id;
            MutationScope.Note($"cloud bootstrap: project SSH key {key.Id} created");
            Say("Projekt-SSH-Key angelegt.");

            // 4) Rescue with that key, then a soft reboot (hard reset only if it does not come back).
            var before = await _ssh.KeyscanAsync(ip, 22, ct);
            // The answer carries the rescue root password: discarded here, never kept (the injected key is the access).
            _ = await hetzner.EnableRescueWithKeysAsync(token, vmId, [key.Id], ct);
            rescueEnabled = true;
            MutationScope.Note("cloud bootstrap: rescue enabled");
            Say("Rescue-System aktiviert, Neustart…");
            // From the reboot on the VM may be in rescue, whether or not the reboot was observed: a failure resets it.
            inRescue = true;
            await RebootAsync(hetzner, token, vmId, ip, before, settings, Say, ct);

            // 5) + 6) Rescue session (trust on first use on the rescue system, run-scoped known_hosts) and the disk.
            var inject = await InjectAsync(server, ip, publicLine, marker, ct);
            if (inject.LinePlaced)
            {
                // The line is on the disk (read back by the script) even when a later part of the script failed: it is
                // recorded and reported as a standing key until a resume, an abandon or a removal takes it.
                injectedNow = true;
                await RecordInjectedAsync(server.Id, injectedFingerprint!);
                MutationScope.Note($"cloud bootstrap: bootstrap key {injectedFingerprint} appended to /root/.ssh/authorized_keys");
            }
            if (inject.Failure is not null) throw new BootstrapStepException(inject.Failure);
            var hostKeys = inject.Keys;
            Say($"Bootstrap-Key eingetragen ({injectedFingerprint}); Host-Keys von der Platte gelesen.");

            // 7) Back to the real system.
            var rescueKeys = await _ssh.KeyscanAsync(ip, 22, ct);
            await hetzner.DisableRescueAsync(token, vmId, ct);
            rescueEnabled = false;
            MutationScope.Note("cloud bootstrap: rescue disabled");
            Say("Rescue-System deaktiviert, Neustart ins eigene System…");
            await RebootAsync(hetzner, token, vmId, ip, rescueKeys, settings, Say, ct);
            inRescue = false;

            // 8) Pin the keys read from the disk before the first contact with the real system.
            var current = _servers.GetServer(server.Id)?.Clone() ?? throw new BootstrapStepException("The record was removed meanwhile.");
            pin = await _pins.PinFromDiskAsync(current, hostKeys, ct);
            succeeded = true;
        }
        catch (BootstrapStepException ex) { error = ex.Message; }
        catch (SshHostKeyMismatchException ex) { error = ex.Message; }
        catch (OperationCanceledException) { error = "Cancelled."; }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or SealedKeyException)
        {
            error = Whiskers.Utils.SecretRedactor.Redact(ex.Message);
        }
        catch (Exception ex)
        {
            // Any other failure (a JSON answer that does not parse, a process that cannot start, …) fails the bootstrap
            // closed and still runs the full clean-up below.
            error = $"The cloud bootstrap failed ({ex.GetType().Name}): {Whiskers.Utils.SecretRedactor.Redact(ex.Message)}";
        }
        finally
        {
            // 9) Always: the project key goes; rescue is disabled; a VM still in rescue after a failure is reset once.
            // Driven by "not succeeded", never by the kind of exception; each step catches everything, so one failing
            // step (a timeout, an unreadable answer) never skips the next.
            var cleanupCt = CancellationToken.None;
            if (!succeeded) error ??= "The cloud bootstrap did not finish.";
            if (projectKeyId is { } id)
            {
                try { await hetzner.DeleteSshKeyAsync(token, id, cleanupCt); MutationScope.Note($"cloud bootstrap: project SSH key {id} deleted"); }
                catch (Exception ex) { warnings.Add($"project SSH key {id} could not be deleted: {CleanupReason(ex)}"); }
            }
            if (rescueEnabled)
            {
                try { await hetzner.DisableRescueAsync(token, vmId, cleanupCt); MutationScope.Note("cloud bootstrap: rescue disabled (clean-up)"); }
                catch (Exception ex)
                {
                    warnings.Add($"rescue mode could not be disabled: {CleanupReason(ex)}");
                    leftInRescue = true;
                }
            }
            if (!succeeded && inRescue)
            {
                try { await hetzner.ResetAsync(token, vmId, cleanupCt); MutationScope.Note("cloud bootstrap: VM reset to leave rescue (clean-up)"); }
                catch (Exception ex)
                {
                    warnings.Add($"the VM could not be reset out of rescue: {CleanupReason(ex)}");
                    leftInRescue = true;
                }
            }
            // Failed before the injection: the generated key is no access to anything; it goes at once.
            if (!succeeded && generatedNow && !injectedNow)
            {
                try { await DropGeneratedKeyAsync(server.Id); }
                catch (Exception ex) { warnings.Add($"the generated key could not be deleted: {ex.GetType().Name}"); }
            }
        }
        if (leftInRescue && error is null) error = "The VM may still be in rescue mode.";
        foreach (var w in warnings) MutationScope.Note("cloud bootstrap clean-up: " + w, success: false);
        return new CloudBootstrapResult(error is null, error, pin, injectedFingerprint, error is not null && injectedNow, warnings);
    }

    // ---- key -----------------------------------------------------------------------------------------------------

    /// <summary>Generates the key in a fresh 0700 directory under the memory-backed base, stores it sealed and removes the
    /// plaintext at once. Returns the public line (<c>type key comment</c>).</summary>
    private async Task<string> GenerateAndSealAsync(Models.ServerConfig server, string marker, CancellationToken ct)
    {
        if (!Directory.Exists(_keyBaseDir) || !_isMemoryBacked(_keyBaseDir))
            throw new InvalidOperationException($"no memory-backed directory ({_keyBaseDir} on tmpfs); no key is generated");
        var dir = Path.Combine(_keyBaseDir, "whiskers-bootstrap-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant());
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var key = await _ssh.GenerateEd25519Async(dir, marker, ct);
            var bytes = await File.ReadAllBytesAsync(key.PrivateKeyPath, ct);
            try
            {
                await _servers.SaveSshKeyAsync(server.Id, BootstrapKeyFileName(server.Id), bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
            var current = _servers.GetServer(server.Id)?.Clone() ?? throw new InvalidOperationException("The record was removed meanwhile.");
            current.BootstrapKeyGenerated = true;
            current.SshPassword = null;
            await _servers.UpdateServerAsync(current);
            return key.PublicKeyLine;
        }
        finally
        {
            foreach (var file in Directory.Exists(dir) ? Directory.GetFiles(dir) : []) SshKeyLeases.Remove(file);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* emptied above */ }
        }
    }

    /// <summary>The sealed key of the earlier run when its public fingerprint equals <paramref name="injected"/>; else null.</summary>
    private async Task<string?> ReusableKeyAsync(Models.ServerConfig server, string injected, CancellationToken ct)
    {
        if (!server.BootstrapKeyGenerated) return null;
        try
        {
            await using var lease = await _leases.AcquireAsync(server, ct);
            if (lease is null) return null;
            var publicKey = await _ssh.PublicKeyOfAsync(lease.Path, ct);
            return string.Equals(await _ssh.FingerprintAsync(publicKey, ct), injected, StringComparison.Ordinal) ? publicKey : null;
        }
        catch (Exception ex) when (ex is SealedKeyException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private async Task DropGeneratedKeyAsync(string serverId)
    {
        await _servers.DeleteSshKeyAsync(serverId);
        if (_servers.GetServer(serverId)?.Clone() is { } current)
        {
            current.BootstrapKeyGenerated = false;
            await _servers.UpdateServerAsync(current);
        }
    }

    /// <summary>The file name the generated key is stored under.</summary>
    public static string BootstrapKeyFileName(string serverId) => $"whiskers-bootstrap-{serverId}";

    // ---- rescue session ------------------------------------------------------------------------------------------

    /// <summary>The rescue session's outcome: whether the line is on the disk (the script printed the host-key begin marker,
    /// which it does only after reading the line back), the host keys, and why it failed (null on success).</summary>
    private sealed record InjectOutcome(bool LinePlaced, IReadOnlyList<string> Keys, string? Failure);

    private async Task<InjectOutcome> InjectAsync(Models.ServerConfig server, string ip, string publicLine, string marker,
        CancellationToken ct)
    {
        var record = _servers.GetServer(server.Id) ?? throw new BootstrapStepException("The record was removed meanwhile.");
        var runDir = Path.Combine(Path.GetTempPath(), "whiskers-rescue-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant());
        Directory.CreateDirectory(runDir);
        try
        {
            // A run-scoped, empty known_hosts: the rescue system's key never reaches Whiskers' own file.
            var knownHosts = Path.Combine(runDir, "known_hosts");
            await File.WriteAllTextAsync(knownHosts, "", ct);
            await using var lease = await _leases.AcquireAsync(record, ct)
                                    ?? throw new BootstrapStepException("The generated key cannot be opened.");
            var r = await _ssh.SshAsync(new RescueSshCall(ip, 22, "root", lease.Path, knownHosts),
                RescueCommands.InjectAndReadHostKeys(publicLine, marker), RescueCommandTimeout, ct);
            var placed = (r.Output ?? "").Contains(RescueCommands.HostKeysBegin, StringComparison.Ordinal);
            if (!r.Success)
                return new InjectOutcome(placed, [], "The rescue system could not prepare the disk: " +
                    Whiskers.Utils.SecretRedactor.Redact(string.IsNullOrWhiteSpace(r.Error) ? $"exit {r.ExitCode}" : r.Error.Trim()));
            var keys = RescueCommands.HostKeysOf(r.Output ?? "");
            return keys.Count > 0
                ? new InjectOutcome(placed, keys, null)
                : new InjectOutcome(placed, [], "No host key was found on the real disk.");
        }
        finally
        {
            try { Directory.Delete(runDir, recursive: true); } catch (IOException) { /* public data only */ }
        }
    }

    private async Task RecordInjectedAsync(string serverId, string fingerprint)
    {
        var current = _servers.GetServer(serverId)?.Clone() ?? throw new BootstrapStepException("The record was removed meanwhile.");
        current.BootstrapKeyInjectedFingerprint = fingerprint;
        await _servers.UpdateServerAsync(current);
    }

    // ---- reboots -------------------------------------------------------------------------------------------------

    /// <summary>A soft reboot; when the VM is not observed going down and coming back within the soft bound, one hard
    /// reset. Then waits until the VM runs and port 22 answers.</summary>
    private async Task RebootAsync(IHetznerService hetzner, string token, long vmId, string ip, IReadOnlyList<string> before,
        CloudBootstrapSettings settings, Action<string> say, CancellationToken ct)
    {
        await hetzner.RebootAsync(token, vmId, ct);
        MutationScope.Note("cloud bootstrap: soft reboot");
        if (!await CameBackAsync(ip, before, TimeSpan.FromSeconds(Math.Max(1, settings.SoftRebootSeconds)), ct))
        {
            say("Der normale Neustart kam nicht zurück — einmaliger Hard-Reset.");
            await hetzner.ResetAsync(token, vmId, ct);
            MutationScope.Note("cloud bootstrap: hard reset (the soft reboot did not come back)");
            if (!await CameBackAsync(ip, before, TimeSpan.FromSeconds(Math.Max(1, settings.BootTimeoutSeconds)), ct))
                throw new BootstrapStepException("The VM did not come back after the hard reset.");
        }
        // Running, by the provider's own word, within the boot bound.
        var attempts = Attempts(settings.BootTimeoutSeconds);
        for (var i = 0; i < attempts; i++)
        {
            if ((await hetzner.GetServerAsync(token, vmId, ct))?.Status is { } s && s.Equals("running", StringComparison.OrdinalIgnoreCase)) return;
            await Delay(PollInterval, ct);
        }
        throw new BootstrapStepException("The VM does not report 'running' after the reboot.");
    }

    /// <summary>An observed down-then-up: port 22 stops answering and answers again, or the host keys it serves differ from
    /// <paramref name="before"/>. A port that answers throughout with the same keys does not count.</summary>
    private async Task<bool> CameBackAsync(string ip, IReadOnlyList<string> before, TimeSpan bound, CancellationToken ct)
    {
        var wentDown = false;
        var attempts = Attempts((int)bound.TotalSeconds);
        for (var i = 0; i < attempts; i++)
        {
            await Delay(PollInterval, ct);
            if (!await _ssh.PortOpenAsync(ip, 22, ProbeTimeout, ct))
            {
                wentDown = true;
                continue;
            }
            if (wentDown) return true;
            var now = await _ssh.KeyscanAsync(ip, 22, ct);
            if (before.Count > 0 && now.Count > 0 && !now.ToHashSet(StringComparer.Ordinal).SetEquals(before)) return true;
        }
        return false;
    }

    /// <summary>Why a clean-up step failed, fit for a warning: the exception type and its redacted message.</summary>
    private static string CleanupReason(Exception ex) =>
        $"{ex.GetType().Name}: {Whiskers.Utils.SecretRedactor.Redact(ex.Message)}";

    private static int Attempts(int seconds) => Math.Max(1, (int)Math.Ceiling(seconds / PollInterval.TotalSeconds));

    /// <summary>A step of the bootstrap that failed with a reason fit for the person (no secret in it).</summary>
    private sealed class BootstrapStepException(string message) : Exception(message);
}
