using Microsoft.Extensions.Options;
using Whiskers.Services.Mutations;
using Whiskers.Services.Vpn.Providers;

namespace Whiskers.Services.Vpn;

/// <summary>
/// On startup, brings the active VPN provider up — replacing the Tailscale bring-up that used to
/// live in entrypoint.sh. With the default "none" provider nothing runs (the VPN runs on the
/// host/sidecar, or entrypoint.sh still handles it for legacy deployments).
/// <para>R1-43 (S-07, INV-20/38/45): the bring-up starts a daemon as root and joins the mesh, so it is a mutation like
/// any other. It runs through the Mutation Gateway as <c>vpn.bootstrap</c>, unattended, on the authority of the admin
/// who took it over in Settings → Automation owners (<see cref="IAutomationOwnerStore.VpnBootstrap"/>), re-read at every
/// start — never as a synthetic admin and never with an implied confirmation. Without an owner, with an unreadable or
/// suspended owner entry, or when the gateway refuses, nothing runs; every attempt is one audit event (a refusal made
/// here is recorded as the gateway's Denied event). The result is explicit: an error in the log, the status in
/// <see cref="IVpnBootstrapState"/> (shown in Settings), and — with <see cref="VpnSettings.FailStartupOnError"/> — a
/// start that stops.</para>
/// </summary>
public class VpnBootstrapHostedService : IHostedService
{
    internal const string Capability = "vpn.bootstrap";
    internal const string RunnerId = "vpn-bootstrap";
    internal const string DisplayName = "VPN bring-up";

    private readonly IVpnService _vpn;
    private readonly IAutomationMutationRunner _automation;
    private readonly IAutomationOwnerStore _owners;
    private readonly IVpnBootstrapState _state;
    private readonly VpnSettings _settings;
    private readonly ILogger<VpnBootstrapHostedService> _logger;
    private readonly IReadOnlyList<string>? _secretParents;

    public VpnBootstrapHostedService(IVpnService vpn, IAutomationMutationRunner automation, IAutomationOwnerStore owners,
        IVpnBootstrapState state, IOptions<VpnSettings> settings, ILogger<VpnBootstrapHostedService> logger)
        : this(vpn, automation, owners, state, settings, logger, null)
    {
    }

    /// <summary>For tests: where stale key directories are looked for (default: <see cref="VpnSecretFile.DefaultParents"/>).</summary>
    internal VpnBootstrapHostedService(IVpnService vpn, IAutomationMutationRunner automation, IAutomationOwnerStore owners,
        IVpnBootstrapState state, IOptions<VpnSettings> settings, ILogger<VpnBootstrapHostedService> logger,
        IReadOnlyList<string>? secretParents)
    {
        _secretParents = secretParents;
        _vpn = vpn ?? throw new ArgumentNullException(nameof(vpn));
        _automation = automation ?? throw new ArgumentNullException(nameof(automation));
        _owners = owners ?? throw new ArgumentNullException(nameof(owners));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _settings = (settings ?? throw new ArgumentNullException(nameof(settings))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var report = await RunAsync(cancellationToken);
        _state.Set(report);
        if (!report.Ok && _settings.FailStartupOnError)
            throw new VpnBootstrapFailedException(report);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>One bring-up through the gateway. Internal for tests.</summary>
    internal async Task<VpnBootstrapReport> RunAsync(CancellationToken ct)
    {
        // A key file a crash or a kill left behind during an earlier start's CLI call (m3): gone before anything else.
        VpnSecretFile.RemoveStale(_secretParents, _logger);
        if (_settings.KeyFileWarning is { } keyFileWarning) _logger.LogWarning("[vpn] {Warning}", keyFileWarning);

        var provider = _vpn.Active;
        if (provider.Id == NoopVpnProvider.ProviderId)
        {
            _logger.LogInformation("[vpn] provider=none — VPN managed outside the container");
            return new VpnBootstrapReport(VpnBootstrapStatus.NotManaged, "The VPN is managed outside Whiskers.", DateTime.UtcNow);
        }

        var arguments = ArgumentsFor(provider.Id);
        if (_owners.IsBlocked(IAutomationOwnerStore.VpnBootstrap))
        {
            const string blocked = "The VPN bring-up owner could not be read, or the bring-up is suspended: an admin " +
                                   "must take it over in Settings → Automation owners.";
            return await RefuseAsync(VpnBootstrapStatus.Blocked, new AutomationActor(RunnerId, DisplayName, null),
                arguments, blocked, ct);
        }

        var owner = _owners.Get(IAutomationOwnerStore.VpnBootstrap)?.OwnerId;
        if (string.IsNullOrWhiteSpace(owner))
        {
            const string noOwner = "No admin has taken the VPN bring-up over (Settings → Automation owners): the VPN " +
                                   "was not brought up. It runs at the next start after an admin takes it over.";
            return await RefuseAsync(VpnBootstrapStatus.NoOwner, new AutomationActor(RunnerId, DisplayName, null),
                arguments, noOwner, ct);
        }

        // Only an admin taking it over in Settings records the owner, so the owner is also its provenance.
        var actor = new AutomationActor(RunnerId, DisplayName, owner, owner);
        _logger.LogInformation("[vpn] bringing up provider '{Provider}'", provider.Id);
        var settingsNote = SettingsNote(provider.Id);
        var outcome = await _automation.RunAsync(actor, Capability, arguments, async token =>
        {
            MutationScope.Note(settingsNote);
            var result = await provider.EnsureUpAsync(token);
            // The message is scrubbed by the provider; first failure reason wins in the scope.
            if (!result.Success) MutationScope.Fail($"{result.State}: {result.Message}");
            return result;
        }, ct);

        switch (outcome.Status)
        {
            case MutationStatus.Executed:
                return new VpnBootstrapReport(VpnBootstrapStatus.Done, outcome.Value?.Message ?? "Done.", DateTime.UtcNow,
                    outcome.OperationId);
            case MutationStatus.Failed:
                var failure = outcome.Value is { } value ? $"{value.State}: {value.Message}" : outcome.Reason ?? "Failed.";
                _logger.LogError("[vpn] bring-up of '{Provider}' failed (operation {OperationId}): {Reason}",
                    provider.Id, outcome.OperationId, failure);
                return new VpnBootstrapReport(VpnBootstrapStatus.Failed, failure, DateTime.UtcNow, outcome.OperationId);
            default:
                var reason = outcome.Reason ?? "Refused.";
                _logger.LogError("[vpn] bring-up of '{Provider}' refused (operation {OperationId}): {Reason}",
                    provider.Id, outcome.OperationId, reason);
                await SuspendIfOwnerLostAsync(actor, ct);
                return new VpnBootstrapReport(VpnBootstrapStatus.Refused, reason, DateTime.UtcNow, outcome.OperationId);
        }
    }

    /// <summary>The bring-up refused itself before the gateway: recorded as the gateway's Denied event.</summary>
    private async Task<VpnBootstrapReport> RefuseAsync(VpnBootstrapStatus status, AutomationActor actor,
        object arguments, string reason, CancellationToken ct)
    {
        _logger.LogError("[vpn] bring-up not run: {Reason}", reason);
        string? operationId = null;
        try
        {
            operationId = (await _automation.RefuseAsync(actor, Capability, arguments, reason, ct)).OperationId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The refusal stands either way; the audit trail's own failure is reported by the gateway.
            _logger.LogError(ex, "[vpn] the refused bring-up could not be recorded");
        }
        return new VpnBootstrapReport(status, reason, DateTime.UtcNow, string.IsNullOrEmpty(operationId) ? null : operationId);
    }

    /// <summary>The owner lost their authority (PC-D6): the bring-up is suspended until an admin takes it over.</summary>
    private async Task SuspendIfOwnerLostAsync(AutomationActor actor, CancellationToken ct)
    {
        if (_automation.LostAuthority(actor, Capability) is not { } lost) return;
        var result = await _owners.SuspendAsync(IAutomationOwnerStore.VpnBootstrap, actor.OwnerId, lost, ct);
        if (result != SuspendResult.Superseded)
            _logger.LogError("[vpn] the VPN bring-up was suspended: {Reason}. Take it over again in Settings → " +
                             "Automation owners", lost);
    }

    /// <summary>What the gateway decides on and records: the provider, and whether a key is configured — never the key.
    /// The mesh hostname and the control URL are not decision arguments: they name Whiskers itself on the mesh (by
    /// default its container name), which the control-plane patterns would read as a protected container. They are
    /// recorded in the event's details instead (<see cref="SettingsNote"/>).</summary>
    private object ArgumentsFor(string providerId)
    {
        var tailscale = providerId == "tailscale";
        var key = tailscale ? _settings.Tailscale.AuthKey : _settings.Netbird.SetupKey;
        return new
        {
            provider = providerId,
            acceptRoutes = tailscale && _settings.AcceptRoutes,
            enrollmentKey = string.IsNullOrWhiteSpace(key) ? "none" : "configured",
        };
    }

    /// <summary>The configured values that become CLI arguments, for the event's details. A URL's user info is never
    /// recorded (the provider refuses such a URL anyway).</summary>
    private string SettingsNote(string providerId)
    {
        var url = providerId == "tailscale" ? _settings.Tailscale.LoginServer : _settings.Netbird.ManagementUrl;
        return $"VPN settings: hostname={(string.IsNullOrWhiteSpace(_settings.Hostname) ? "(container hostname)" : _settings.Hostname)}; " +
               $"control URL={(string.IsNullOrWhiteSpace(url) ? "(default)" : PolicyDecisions.WithoutUrlCredentials(url))}";
    }
}
