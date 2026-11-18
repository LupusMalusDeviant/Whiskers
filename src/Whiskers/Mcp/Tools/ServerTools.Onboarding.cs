using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Whiskers.Models;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;
using Whiskers.Services.Onboarding;
using Whiskers.Services.ServerConfig;
using Whiskers.Utils;

namespace Whiskers.Mcp.Tools;

/// <summary>
/// Contract 0018 part E: adding and onboarding a server over MCP. <c>add_server</c> (<c>server.create</c>) and
/// <c>onboard_server</c> (<c>server.onboard</c>) run through the Mutation Gateway (the filter decides them by their
/// capability, Admin only, D5); a bootstrap other than <c>none</c> is always held for a human Admin's approval
/// (<c>CloudBootstrapRule</c>). <c>get_onboarding_status</c> is an Admin-level read of the in-memory run snapshot. No
/// tool takes a cloud API key or any other secret. Only <c>add_server</c> is offered to the in-app agent (D6).
/// Every error is a <see cref="McpErrorEnvelope"/>.
/// </summary>
public partial class ServerTools
{
    private static readonly JsonSerializerOptions OnboardingJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    /// <summary>D8: the run is bound to the gateway's deadline of <c>server.onboard</c> (<c>Long</c>), not to the request.</summary>
    internal static readonly TimeSpan OnboardDeadline = new CapabilityCatalog().Find("server.onboard")!.Deadline;

    [McpToolLevel(McpPermissionLevels.Admin)]
    [McpServerTool, Description("Add a server from its name and public IP (contract 0018). The record is SSH to root@<ip>:22 " +
        "without a credential; Whiskers moves it to TCP + mTLS when it is onboarded (onboard_server). With cloudProvider " +
        "hetzner or hostinger, the VM is found by its exact public IP across the stored credentials of that provider (never by " +
        "name) and the credential is shared by reference; cloudCredentialFrom names the server whose stored credential to use " +
        "instead. No API key is ever passed here. Answers JSON. Admin only.")]
    public static async Task<string> AddServer(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IServerRegistration registration,
        IServerConfigService serverConfig,
        [Description("Display name, 1-64 characters (the id is derived from it)")] string name,
        [Description("The server's public IPv4 or IPv6 literal (Hetzner: IPv4 only); no hostname")] string ip,
        [Description("Optional group")] string? group = null,
        [Description("Cloud provider: none (default), hetzner or hostinger")] string cloudProvider = "none",
        [Description("Optional id of a server whose stored cloud credential to reuse (same provider). Omit to find it by the IP.")] string? cloudCredentialFrom = null,
        CancellationToken cancellationToken = default)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "add_server");
        if (denied != null) return OnboardingFailed(McpErrorEnvelope.Denied, denied);
        if (string.IsNullOrWhiteSpace(name)) return OnboardingFailed(McpErrorEnvelope.InvalidArgument, "Name is required.");
        if (string.IsNullOrWhiteSpace(ip)) return OnboardingFailed(McpErrorEnvelope.InvalidArgument, "IP is required.");
        if (ProviderOf(cloudProvider) is not { } provider)
            return OnboardingFailed(McpErrorEnvelope.InvalidArgument, "cloudProvider must be none, hetzner or hostinger.");

        ServerRegistrationResult added;
        try
        {
            added = await registration.AddAsync(new ServerRegistrationRequest(name, ip, group, provider,
                string.IsNullOrWhiteSpace(cloudCredentialFrom) ? null : cloudCredentialFrom.Trim()), cancellationToken);
        }
        catch (ServerRegistrationException ex)
        {
            return OnboardingFailed(ex.Message.Contains("already", StringComparison.OrdinalIgnoreCase)
                ? McpErrorEnvelope.Conflict : McpErrorEnvelope.InvalidArgument, SecretRedactor.Redact(ex.Message));
        }

        // Read back from the store: what the next onboarding will use (M-50).
        var stored = serverConfig.GetServer(added.ServerId);
        if (stored is null || !string.Equals(stored.Id, added.ServerId, StringComparison.Ordinal)
            || stored.ConnectionType != ConnectionType.SSH || stored.SshHost is null)
            return OnboardingFailed(McpErrorEnvelope.Failed, $"'{added.ServerId}' was saved, but reading it back did not show it.");
        MutationScope.Note($"server {stored.Id} added at {stored.SshHost}" +
                           (added.CredentialFrom is { } from ? $", cloud credential of server {from}" : ""));
        return JsonSerializer.Serialize(new
        {
            serverId = stored.Id,
            name = stored.Name,
            ssh = $"{stored.SshUser}@{stored.SshHost}:{stored.SshPort}",
            cloud = added.Cloud is { } vm
                ? new
                {
                    provider = vm.Provider.ToString().ToLowerInvariant(),
                    cloudServerId = vm.CloudServerId,
                    cloudServerName = vm.CloudServerName,
                    created = vm.CloudServerCreated,
                    status = vm.Status,
                }
                : null,
            credentialFrom = added.CredentialFrom,
            warnings = added.Warnings,
            next = "onboard_server",
        }, OnboardingJson);
    }

    [McpToolLevel(McpPermissionLevels.Admin)]
    [McpServerTool, Description("Onboard a server into the mesh + mTLS stack (contract 0018): pin its SSH host key, install " +
        "Tailscale, node-exporter, the server certificate and the mTLS proxy, then switch it to TCP + mTLS. Blocks until the " +
        "run ends (up to 30 minutes) and sends progress notifications when the client passes a progress token; a disconnect " +
        "does not stop the run (see get_onboarding_status). bootstrap=hetzner_rescue gives Whiskers SSH access to a fresh " +
        "Hetzner VM through the rescue system (two reboots) and always needs a human Admin's approval; confirmVmName is the " +
        "VM name when it differs from the server's name. abandon=true only removes a standing bootstrap key. A host that " +
        "already holds mTLS state is re-onboarded (or abandoned) only in the UI. Answers JSON. Admin only.")]
    public static async Task<string> OnboardServer(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IOnboardingService onboarding,
        IServerConfigService serverConfig,
        IProgress<ProgressNotificationValue> progress,
        [Description("Server id")] string server,
        [Description("Bootstrap method: none (default; the record's own credential) or hetzner_rescue")] string bootstrap = "none",
        [Description("Only with hetzner_rescue: the VM's current name when it differs from the server's name")] string? confirmVmName = null,
        [Description("true = only remove a standing bootstrap key (BootstrapCleanup), then end")] bool abandon = false,
        CancellationToken cancellationToken = default)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "onboard_server");
        if (denied != null) return OnboardingFailed(McpErrorEnvelope.Denied, denied);
        if (string.IsNullOrWhiteSpace(server)) return OnboardingFailed(McpErrorEnvelope.InvalidArgument, "Server id is required.");
        if (BootstrapOf(bootstrap) is not { } method)
            return OnboardingFailed(McpErrorEnvelope.InvalidArgument, "bootstrap must be none or hetzner_rescue.");
        if (abandon && method != BootstrapMethod.None)
            return OnboardingFailed(McpErrorEnvelope.InvalidArgument, "abandon runs only the clean-up; it takes bootstrap none.");
        if (!string.IsNullOrWhiteSpace(confirmVmName) && method == BootstrapMethod.None)
            return OnboardingFailed(McpErrorEnvelope.InvalidArgument, "confirmVmName applies only to bootstrap hetzner_rescue.");

        // INV-59: the run uses what the gateway decided on — the server, whether it held mTLS state, the host it reached —
        // never a second lookup. Outside a server.onboard decision nothing runs (fail closed).
        if (MutationScope.Current is not { CapabilityId: "server.onboard", DecidedTarget: { } decided } scope
            || string.IsNullOrEmpty(decided.Id))
            return OnboardingFailed(McpErrorEnvelope.Denied, "onboard_server runs only inside the Mutation Gateway's decision.");

        var options = new OnboardingOptions(method,
            ConfirmVmName: string.IsNullOrWhiteSpace(confirmVmName) ? null : confirmVmName, AbandonBootstrap: abandon);
        var step = 0;
        var notify = new SynchronousProgress(p => progress.Report(new ProgressNotificationValue
        {
            Progress = Interlocked.Increment(ref step),
            Message = p.TailscaleLoginUrl is { } url ? $"[{p.Step}] Tailscale login link: {url}" : $"[{p.Step}] {p.Message}",
        }));

        // D8: bound to the gateway's deadline, not to the request — a client that disconnects does not cut the run in the
        // middle of a reboot. The token the call came with is linked to the request, so it is not used here.
        using var deadline = new CancellationTokenSource(OnboardDeadline);
        var result = await onboarding.OnboardServerAsync(decided.Id, decided.HasControlPlaneState, decided.Connection,
            options, notify, deadline.Token);

        var record = serverConfig.GetServer(decided.Id) is { } r && string.Equals(r.Id, decided.Id, StringComparison.Ordinal) ? r : null;
        var answer = JsonSerializer.Serialize(new
        {
            operationId = scope.OperationId,
            success = result.Success,
            completedSteps = result.CompletedSteps.Select(s => s.ToString()).ToList(),
            failedStep = result.FailedStep?.ToString(),
            error = result.Error is null ? null : SecretRedactor.Redact(result.Error),
            hostKey = HostKeyOf(result.HostKey),
            tailscaleIp = result.Success && record is { ConnectionType: ConnectionType.TCP } ? record.TcpHost : null,
            standingBootstrapKey = result.StandingBootstrapKey,
            cleanupWarnings = result.CleanupWarnings.Select(SecretRedactor.Redact).ToList(),
        }, OnboardingJson);
        // A run that failed is a failed mutation; the client still reads the run's answer (marked as an error).
        if (!result.Success) MutationScope.Fail(result.Error ?? "The onboarding failed.");
        return answer;
    }

    [McpToolLevel(McpPermissionLevels.Admin)]
    [McpServerTool, Description("Read the latest onboarding run of a server (contract 0018): operation id, start, current step, " +
        "whether it finished, the last 200 progress lines, the Tailscale login link while one is open, whether a standing " +
        "bootstrap key is on the host, and the result. Kept in memory since Whiskers started. Admin only (the log carries the " +
        "login link and host details).")]
    public static CallToolResult GetOnboardingStatus(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IOnboardingRuns runs,
        IServerConfigService serverConfig,
        [Description("Server id")] string server)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "get_onboarding_status");
        if (denied != null) return McpErrorEnvelope.Result(McpErrorEnvelope.Denied, denied);
        if (string.IsNullOrWhiteSpace(server))
            return McpErrorEnvelope.Result(McpErrorEnvelope.InvalidArgument, "Server id is required.");

        // By its id (then by its id in another case, when that is unique) — never by a display name.
        var servers = serverConfig.GetServers();
        var record = servers.FirstOrDefault(s => string.Equals(s.Id, server, StringComparison.Ordinal));
        if (record is null)
        {
            var folded = servers.Where(s => string.Equals(s.Id, server, StringComparison.OrdinalIgnoreCase)).ToList();
            if (folded.Count > 1) return McpErrorEnvelope.Result(McpErrorEnvelope.InvalidArgument, $"Ambiguous server '{server}'.");
            record = folded.SingleOrDefault();
        }
        var id = record?.Id ?? server;
        var snapshot = runs.Latest(id);
        // INV-64: the standing key is reported until a resume, an abandon or server.remove removes it — also when no run
        // is in memory any more (after a restart). While a run is going on, its own result says it when it ends.
        var standingFingerprint = record?.BootstrapKeyInjectedFingerprint;
        var standing = snapshot is { Finished: false } ? false
            : snapshot?.Result?.StandingBootstrapKey == true || standingFingerprint is not null;
        if (snapshot is null && !standing)
            return McpErrorEnvelope.Result(McpErrorEnvelope.NotFound,
                record is null ? $"Unknown server '{server}'." : $"No onboarding run of '{id}' since Whiskers started.");

        var result = snapshot?.Result;
        var text = JsonSerializer.Serialize(new
        {
            serverId = id,
            operationId = snapshot?.OperationId,
            startedUtc = snapshot?.StartedUtc,
            currentStep = snapshot?.CurrentStep?.ToString(),
            finished = snapshot?.Finished ?? true,
            log = snapshot?.Log.Select(SecretRedactor.Redact).ToList() ?? [],
            // Admin-only (D5); shown while the run waits for the login, never written to the audit.
            tailscaleLoginUrl = snapshot is { Finished: false } ? snapshot.TailscaleLoginUrl : null,
            standingBootstrapKey = standing,
            standingBootstrapKeyFingerprint = standing ? standingFingerprint : null,
            result = result is null
                ? null
                : new
                {
                    success = result.Success,
                    completedSteps = result.CompletedSteps.Select(s => s.ToString()).ToList(),
                    failedStep = result.FailedStep?.ToString(),
                    error = result.Error is null ? null : SecretRedactor.Redact(result.Error),
                    hostKey = HostKeyOf(result.HostKey),
                    standingBootstrapKey = result.StandingBootstrapKey,
                    cleanupWarnings = result.CleanupWarnings.Select(SecretRedactor.Redact).ToList(),
                },
        }, OnboardingJson);
        return new CallToolResult { Content = [new TextContentBlock { Text = text }] };
    }

    /// <summary>The pin as the answers show it (public data).</summary>
    private static object? HostKeyOf(Whiskers.Services.Server.SshHostKeyPin? pin) =>
        pin is null ? null : new { type = pin.KeyType, fingerprint = pin.FingerprintSha256, source = pin.Source.ToString() };

    private static CloudProvider? ProviderOf(string? value) => (value ?? "none").Trim().ToLowerInvariant() switch
    {
        "" or "none" => CloudProvider.None,
        "hetzner" => CloudProvider.Hetzner,
        "hostinger" => CloudProvider.Hostinger,
        _ => null,
    };

    private static BootstrapMethod? BootstrapOf(string? value) => (value ?? "none").Trim().ToLowerInvariant() switch
    {
        "" or "none" => BootstrapMethod.None,
        "hetzner_rescue" => BootstrapMethod.HetznerRescueKey,
        _ => null,
    };

    /// <summary>A refused or failed call: the operation fails (R1-20/37) and the caller reads the reason code.</summary>
    private static string OnboardingFailed(string code, string message) => MutationScope.Fail(McpErrorEnvelope.Json(code, message));

    /// <summary>Reports on the calling thread: <see cref="Progress{T}"/> posts to a captured context, so lines could
    /// arrive after the run's answer.</summary>
    private sealed class SynchronousProgress(Action<OnboardingProgress> report) : IProgress<OnboardingProgress>
    {
        public void Report(OnboardingProgress value) => report(value);
    }
}
