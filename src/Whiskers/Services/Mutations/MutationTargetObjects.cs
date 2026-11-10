using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Whiskers.Models;
using Whiskers.Services.Auth;
using Whiskers.Services.Backup;
using Whiskers.Services.EndpointChecks;
using Whiskers.Services.LogMonitor;
using Whiskers.Services.Mcp;
using Whiskers.Services.Registries;
using Whiskers.Services.Vault;

namespace Whiskers.Services.Mutations;

/// <summary>
/// Contract 0012 (D1, D2, D5, D6, D12): the typed <see cref="ResolvedTarget"/> of every resolution, and the resolver
/// branches for volumes, volume backups, log-alert rules and Whiskers' own records. Only this class constructs a
/// <see cref="ResolvedTarget"/>: the <see cref="ResolverToken"/> every constructor needs has one instance, private to it.
/// </summary>
public sealed partial class MutationTargetResolver
{
    /// <summary>
    /// The proof a <see cref="ResolvedTarget"/> was built by the resolver (D4 a). Its constructor is private and its
    /// one instance is handed only to <see cref="MutationTargetResolver"/> (by the type's own initializer, which as a
    /// nested type may write the resolver's private field). Pages share the assembly, so <c>internal</c> would not be
    /// enough; nothing else can obtain an instance without reflection.
    /// </summary>
    public sealed class ResolverToken
    {
        private ResolverToken() { }

        static ResolverToken() => s_issued = new ResolverToken();
    }

    private static ResolverToken? s_issued;

    private static ResolverToken Token
    {
        get
        {
            if (s_issued is null) RuntimeHelpers.RunClassConstructor(typeof(ResolverToken).TypeHandle);
            return s_issued ?? throw new InvalidOperationException("The resolver token was not issued.");
        }
    }

    /// <summary>The resolution with its typed object (<see cref="TargetResolution.Object"/>), built from the final
    /// recorded target — after the containers a command names were added (R1-16) — so the object and the record agree.</summary>
    private static TargetResolution Finish(Capability capability, TargetResolution resolution)
    {
        if (resolution.Target is not { } target) return resolution;
        var built = resolution.Build is { } build ? build(target) : Default(capability, target);
        return resolution with { Object = built, Build = null };
    }

    /// <summary>The object of a branch that needs nothing beyond the recorded target.</summary>
    private static ResolvedTarget Default(Capability capability, MutationTarget target)
    {
        var token = Token;
        // Not found in the inventory because the operation creates it (Pass): only its server and name.
        if (!target.Resolved) return new ResolvedNew(token, target);
        return capability.TargetKind switch
        {
            "container" => new ResolvedContainer(token, target, target.Id, null),
            "workload" => new ResolvedWorkload(token, target, target.Id, target.Id, target.Name),
            "network" => new ResolvedNetwork(token, target, target.Id),
            "host" => new ResolvedHostObject(token, target, target.ServerId, null),
            "server" => new ResolvedServer(token, target, target.ServerId),
            "cloud-server" => new ResolvedCloudServer(token, target, target.Id),
            "scheduled-task" => new ResolvedScheduledTask(token, target, target.Id),
            "dns-zone" => new ResolvedDnsZone(token, target, target.Id),
            "git-deploy-app" => new ResolvedGitApp(token, target, target.Id, 0),
            "webhook" => new ResolvedWebhook(token, target, target.Id, null),
            _ => throw new InvalidOperationException($"No resolved object for the kind '{capability.TargetKind}'."),
        };
    }

    /// <inheritdoc />
    public async Task<TargetResolution> ConfirmAsync(MutationRequest request, Capability capability, ResolvedTarget target,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(target);
        // Resolved once more the way the decision resolved it; the object must keep its stable identity and fingerprint
        // (a name that now points to a recreated container resolves to another id: changed).
        var again = await ResolveAsync(request, capability, ct);
        var named = target.Name ?? target.Id;
        if (again.Object is not { } now)
            return again.Code is MutationReasonCodes.TargetUnknown or null
                ? new TargetResolution(null, $"{named} no longer exists; nothing ran.", MutationReasonCodes.TargetUnknown, [named])
                : new TargetResolution(null, $"{named} changed after the decision ({again.Error}); nothing ran.",
                    MutationReasonCodes.TargetChanged, [named]);
        var same = now.GetType() == target.GetType()
                   && string.Equals(now.Kind, target.Kind, StringComparison.Ordinal)
                   && string.Equals(now.Id, target.Id, StringComparison.Ordinal)
                   && string.Equals(now.ServerId, target.ServerId, StringComparison.Ordinal)
                   && string.Equals(now.Fingerprint, target.Fingerprint, StringComparison.Ordinal);
        return same
            ? new TargetResolution(target.Record, null) { Object = target }
            : new TargetResolution(null, $"{named} changed after the decision; nothing ran.",
                MutationReasonCodes.TargetChanged, [named]);
    }

    private static TargetResolution Done(MutationTarget target, Func<MutationTarget, ResolvedTarget> build) =>
        Done(target) with { Build = build };

    /// <summary>A fingerprint of values that change when the object is replaced: compared, never shown.</summary>
    internal static string Hash(params string?[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", parts.Select(p => p ?? "")))));

    // ---- Volumes (D5) ----

    /// <summary>
    /// <c>volume.backup</c>: the server first (an empty one is the default server, decided once — R1-06), then the
    /// volume on it by its exact name from <see cref="IVolumeBackupService.ListVolumeInfoAsync"/>. Docker volume names
    /// are unique per host; no prefix is accepted. <c>volume.restore</c> / <c>volume.backup_delete</c>: the backup
    /// record by its id, on its stored server (a removed server is refused); a restore into an existing volume adds that
    /// volume to what the operation touches, into a name that does not exist it creates it (Q4).
    /// </summary>
    private async Task<TargetResolution> VolumeAsync(MutationRequest request, Capability capability, MutationTarget target,
        CancellationToken ct)
    {
        if (_services.GetService<IVolumeBackupService>() is not { } backups)
            return Refuse("Volume backups are not available to resolve the target.");
        if (capability.Id == "volume.backup")
        {
            if (string.IsNullOrWhiteSpace(target.Id)) return Refuse("The volume name is required.");
            var (server, error) = ResolveServer(target.ServerId);
            if (server is null) return Refuse(error!, target.ServerId);
            var volumes = await backups.ListVolumeInfoAsync(server.Id, ct);
            var volume = volumes.FirstOrDefault(v => v.Name == target.Id);
            if (volume is null) return Refuse($"Volume not found: {target.Id}", target.Id);
            return Done(target with { Id = volume.Name, Name = volume.Name, ServerId = server.Id },
                t => new ResolvedVolume(Token, t, Hash(volume.Name, volume.Driver), volume.Driver));
        }

        if (string.IsNullOrWhiteSpace(target.Id)) return Refuse("The backup id is required.");
        var backup = (await backups.ListBackupsAsync()).FirstOrDefault(b => b.BackupId == target.Id);
        if (backup is null) return Refuse($"Volume backup not found: {target.Id}", target.Id);
        // The stored server, exactly: a backup of a server that was removed is refused, never moved to the default.
        var (stored, storedError) = string.IsNullOrWhiteSpace(backup.ServerId)
            ? (null, "The backup names no server.")
            : ResolveServer(backup.ServerId);
        if (stored is null || stored.Id != backup.ServerId)
            return Refuse(storedError ?? $"Unknown server: '{backup.ServerId}'.", backup.ServerId);
        if (!string.IsNullOrWhiteSpace(target.ServerId) && ResolveServer(target.ServerId).Server?.Id != stored.Id)
            return Refuse($"The backup '{backup.BackupId}' is on '{stored.Id}', not on '{target.ServerId}'.");

        var resolved = target with
        {
            Id = backup.BackupId, Name = backup.VolumeName, ServerId = stored.Id,
            Aliases = Aliases(backup.FileName),
        };
        string? restoreInto = null;
        var intoExists = false;
        if (capability.Id == "volume.restore")
        {
            restoreInto = MutationTargets.String(request.Arguments, "targetVolume") ?? backup.VolumeName;
            var volumes = await backups.ListVolumeInfoAsync(stored.Id, ct);
            intoExists = volumes.Any(v => v.Name == restoreInto);
            if (intoExists)
                resolved = resolved with
                {
                    Related = resolved.Related.Append(restoreInto).Distinct(StringComparer.Ordinal).ToList(),
                    RelatedResources = [.. resolved.RelatedResources, Aliases(restoreInto)],
                };
        }
        return Done(resolved, t => new ResolvedVolumeBackup(Token, t, Hash(backup.BackupId), backup.VolumeName,
            backup.FileName, restoreInto, intoExists));
    }

    // ---- Log alerts (D6) ----

    /// <summary>A log-alert rule by its <c>ruleId</c>; a call naming only a name matches names exactly, and more than one
    /// is ambiguous. Whiskers-wide: no server. The rule's container filter is recorded, not resolved (Q5).</summary>
    private async Task<TargetResolution> LogAlertAsync(Capability capability, MutationTarget target)
    {
        if (CapabilityCatalog.Creates.Contains(capability.Id)) return Pass(target with { ServerId = null });
        if (string.IsNullOrWhiteSpace(target.Id)) return Refuse("The log-alert rule is required.");
        if (_services.GetService<ILogMonitorService>() is not { } monitor)
            return Refuse("Log alerts are not available to resolve the target.");
        var rules = await monitor.GetRulesAsync();
        var rule = rules.FirstOrDefault(r => r.RuleId == target.Id);
        if (rule is null)
        {
            var byName = rules.Where(r => r.Name == target.Id).ToList();
            if (byName.Count > 1)
                return Refuse($"Ambiguous log-alert rule '{target.Id}' — {byName.Count} rules have that name; use the rule id.", target.Id);
            rule = byName.FirstOrDefault();
        }
        if (rule is null) return Refuse($"Log-alert rule not found: {target.Id}", target.Id);
        return Done(target with { Id = rule.RuleId, Name = rule.Name, ServerId = null, Aliases = Aliases(rule.Name) },
            t => new ResolvedLogAlertRule(Token, t, Hash(rule.RuleId)));
    }

    // ---- Whiskers' own records (D12) ----

    /// <summary>
    /// Whiskers-wide kinds (<c>config</c>, <c>account</c>, <c>identity</c>, <c>whiskers</c>, <c>fleet</c>) and the
    /// endpoint checks of a server: the record against its store. A creation passes with no server; a singleton is
    /// Whiskers itself. An unknown record is refused before policy.
    /// </summary>
    private async Task<TargetResolution> RecordAsync(MutationRequest request, Capability capability, MutationTarget target,
        CancellationToken ct)
    {
        var whiskersWide = target with { ServerId = null };
        if (CapabilityCatalog.Creates.Contains(capability.Id)) return Pass(whiskersWide);
        if (CapabilityCatalog.Singletons.Contains(capability.Id))
            return Done(whiskersWide, t => new ResolvedRecord(Token, t, Hash(capability.TargetKind),
                capability.TargetKind == "fleet" ? "fleet" : "whiskers", capability.TargetKind == "fleet" ? "fleet" : "whiskers"));

        string Arg(string key) => MutationTargets.String(request.Arguments, key) ?? "";
        switch (capability.Id)
        {
            case "mcp.key_delete":
            {
                var keyId = Arg("keyId");
                if (_services.GetService<IMcpPermissionService>() is not { } keys)
                    return Refuse("MCP keys are not available to resolve the target.");
                var key = keys.GetPermissionData().ApiKeys.FirstOrDefault(k => k.Id == keyId);
                return key is null
                    ? Refuse($"MCP key not found: {keyId}", keyId)
                    : Section(whiskersWide, "mcp-api-keys", "mcp-key", key.Id, key.Name);
            }
            case "vault.secret_set" or "vault.secret_delete":
            {
                var key = Arg("key");
                if (key.Length == 0) return Refuse("The vault secret is required.");
                if (_services.GetService<IVaultService>() is not { } vault)
                    return Refuse("The vault is not available to resolve the target.");
                var exists = vault.ListSecrets().Any(s => s.Key == key);
                if (!exists)
                    return capability.Id == "vault.secret_set"
                        ? Pass(whiskersWide)
                        : Refuse($"Vault secret not found: {key}", key);
                return Section(whiskersWide, "vault", "vault-secret", key, key);
            }
            case "registry.save" or "registry.delete":
            {
                var registryId = Arg("registryId");
                if (registryId.Length == 0)
                    return capability.Id == "registry.save" ? Pass(whiskersWide) : Refuse("The registry is required.");
                if (_services.GetService<IRegistryConfigService>() is not { } registries)
                    return Refuse("Registries are not available to resolve the target.");
                var registry = (await registries.GetRegistriesAsync()).FirstOrDefault(r => r.Id == registryId);
                return registry is null
                    ? Refuse($"Registry not found: {registryId}", registryId)
                    : Section(whiskersWide, "registries", "registry", registry.Id, registry.Name);
            }
            case "endpointcheck.save" or "endpointcheck.delete" or "endpointcheck.run":
                return EndpointCheck(request, capability, target);
            case "automation.owner_set":
            {
                if (!IAutomationOwnerStore.Features.Contains(target.Id, StringComparer.Ordinal))
                    return Refuse($"Unknown automation feature: '{target.Id}'.", target.Id);
                return Done(whiskersWide, t => new ResolvedRecord(Token, t, Hash(t.Id), "automation-feature", t.Id));
            }
            case "selfbackup.delete":
            {
                if (target.Id.Length == 0) return Refuse("The self-backup is required.");
                if (_services.GetService<IBackupService>() is not { } self)
                    return Refuse("Self-backups are not available to resolve the target.");
                var archives = await self.ListBackupsAsync(ct);
                var archive = archives.FirstOrDefault(b => b.Id == target.Id)
                              ?? archives.FirstOrDefault(b => b.FileName == target.Id);
                if (archive is null) return Refuse($"Self-backup not found: {target.Id}", target.Id);
                return Done(whiskersWide with { Id = archive.Id, Name = archive.FileName, Aliases = Aliases(archive.FileName) },
                    t => new ResolvedRecord(Token, t, Hash(archive.Id), "self-backup", archive.Id));
            }
        }

        switch (capability.TargetKind)
        {
            case "config":
            {
                // Settings, access lists, MCP permissions and the guardrail policy: one section of the closed list.
                var section = NullIfEmpty(target.Id) ?? (capability.Id == "config.agent" ? "agent" : null);
                if (section is null || !ConfigSections.All.Contains(section))
                    return Refuse($"Unknown settings section: '{target.Id}'.", target.Id);
                return Section(whiskersWide with { Id = section }, section, "config-section", section, section);
            }
            case "account":
            {
                if (target.Id.Length == 0) return Refuse("The account is required.");
                if (_services.GetService<ILocalAccountDirectory>() is not { } accounts)
                    return Refuse("Local accounts are not available to resolve the target.");
                var account = await accounts.FindAsync(target.Id, ct);
                if (string.IsNullOrEmpty(account)) return Refuse($"Account not found: {target.Id}", target.Id);
                return Done(whiskersWide with { Id = account },
                    t => new ResolvedRecord(Token, t, Hash(account), "account", account));
            }
            case "identity":
            {
                if (target.Id.Length == 0) return Refuse("The identity binding is required.");
                if (_services.GetService<IIdentityBindingStore>() is not { IsAvailable: true } bindings)
                    return Refuse("Identity bindings are not available to resolve the target.");
                var snapshot = bindings.Snapshot();
                var known = capability.Id == "identity.binding_confirm"
                    ? snapshot.Pending.Any(p => p.Id == target.Id)
                    : snapshot.Bindings.Any(b => b.Id == target.Id) || snapshot.Pending.Any(p => p.Id == target.Id);
                if (!known) return Refuse($"Identity binding not found: {target.Id}", target.Id);
                return Done(whiskersWide, t => new ResolvedRecord(Token, t, Hash(t.Id), "identity", t.Id));
            }
        }
        return Refuse($"'{capability.Id}' has no record to resolve.");
    }

    /// <summary>A record that lives under a settings section: the recorded target stays the section (as it was
    /// decided and audited before), the record's own id is <see cref="ResolvedRecord.RecordId"/>.</summary>
    private static TargetResolution Section(MutationTarget whiskersWide, string section, string store, string recordId,
        string? name) =>
        Done(whiskersWide with { Id = NullIfEmpty(whiskersWide.Id) ?? section, Name = whiskersWide.Name ?? name },
            t => new ResolvedRecord(Token, t, Hash(store, recordId), store, recordId));

    /// <summary>An endpoint check (contract 0004) on the server it is bound to: the recorded target stays the server;
    /// the check is the record. Saving a new one (no id) resolves only the server.</summary>
    private TargetResolution EndpointCheck(MutationRequest request, Capability capability, MutationTarget target)
    {
        var server = Server(target, NullIfEmpty(target.Id) ?? target.ServerId, mustExist: true);
        if (server.Target is not { } onServer) return server;
        var checkId = MutationTargets.String(request.Arguments, "checkId");
        if (checkId is null)
            return capability.Id == "endpointcheck.save" ? server with { Build = t => new ResolvedNew(Token, t) } : Refuse("The endpoint check is required.");
        if (_services.GetService<IEndpointCheckStore>() is not { } checks)
            return Refuse("Endpoint checks are not available to resolve the target.");
        var check = checks.List().FirstOrDefault(c => c.Id == checkId);
        if (check is null)
            return capability.Id == "endpointcheck.save"
                ? server with { Build = t => new ResolvedNew(Token, t) }
                : Refuse($"Endpoint check not found: {checkId}", checkId);
        // An existing check is saved, deleted or run where it is bound — a call naming another server is refused.
        if (capability.Id != "endpointcheck.save" && check.ServerId != onServer.ServerId)
            return Refuse($"The endpoint check '{check.Name}' is bound to '{check.ServerId}', not to '{onServer.ServerId}'.");
        return server with { Build = t => new ResolvedRecord(Token, t, Hash(check.Id, check.ServerId), "endpoint-check", check.Id) };
    }
}

/// <summary>Contract 0012 (D12): the closed list of settings sections a <c>config</c>-kind capability may name. A section
/// outside it is refused before policy. <c>ConfigSectionsTests</c> checks every literal section a page passes.</summary>
public static class ConfigSections
{
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "agent", "aichat", "cvemonitor", "diskhealth", "dns", "guardrails", "healthmonitor", "imageupdate",
        "mcp-api-keys", "mcp-permissions", "metricalert", "metrics-retention", "notification-channels", "notifications",
        "outbound-exceptions", "quiet-hours", "registries", "roles", "terminal", "vault", "whitelist",
    };
}
