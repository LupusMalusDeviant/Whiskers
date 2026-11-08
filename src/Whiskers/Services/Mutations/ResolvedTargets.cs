namespace Whiskers.Services.Mutations;

/// <summary>How a capability's target resolves (contract 0012, D2). Every capability of the catalog declares one.</summary>
public enum TargetResolutionKind
{
    /// <summary>It acts on one object of the inventory, resolved by its stable identity and confirmed right before it
    /// runs (D9).</summary>
    Object,

    /// <summary>It acts on a server as a whole (a host-wide command, a deployment): the server is its object.</summary>
    Server,

    /// <summary>It creates its object: only the server (and the name it will have) is resolved. A capability that saves
    /// an object which may or may not exist yet (an upsert) is declared here as well, with
    /// <see cref="ResolvedTarget"/> as its type: the resolver hands it the existing object or a
    /// <see cref="ResolvedNew"/>.</summary>
    Creates,

    /// <summary>Its object is Whiskers itself or the whole fleet (the kill switch, notifications, fleet-wide runs):
    /// <see cref="ResolvedTarget.ServerId"/> is <c>null</c>.</summary>
    Singleton,
}

/// <summary>
/// The object a mutation acts on, as the gateway resolved it from the inventory (contract 0012, D1). Only
/// <see cref="MutationTargetResolver"/> constructs one: every constructor takes a
/// <see cref="MutationTargetResolver.ResolverToken"/>, whose single instance is private to the resolver, so a page or a
/// service in the same assembly cannot make up an object of its own (D4 a). All properties are read-only, so a
/// <c>with</c> expression can only copy it.
/// <para><see cref="Record"/> is the recorded form (decision record, audit, approval subject): the
/// <see cref="MutationTarget"/> the gateway decided on, unchanged.</para>
/// </summary>
public abstract record ResolvedTarget
{
    private protected ResolvedTarget(MutationTargetResolver.ResolverToken token, MutationTarget record, string? fingerprint)
    {
        ArgumentNullException.ThrowIfNull(token);
        Record = record ?? throw new ArgumentNullException(nameof(record));
        Fingerprint = fingerprint;
    }

    /// <summary>The recorded form of this object: what the decision, the approval and the audit name (INV-22, INV-45).</summary>
    public MutationTarget Record { get; }

    /// <summary>The target kind (<c>container</c>, <c>volume</c>, <c>config</c>, …).</summary>
    public string Kind => Record.Kind;

    /// <summary>The canonical server; <c>null</c> only for a Whiskers-wide object.</summary>
    public string? ServerId => string.IsNullOrEmpty(Record.ServerId) ? null : Record.ServerId;

    /// <summary>The object's stable identity (D2): a full container id, a backup id, a rule id, a store id, …</summary>
    public string Id => Record.Id;

    /// <summary>The object's name in the inventory, if it has one.</summary>
    public string? Name => Record.Name;

    /// <summary>The inventory's further names of the object itself.</summary>
    public IReadOnlyList<string> Aliases => Record.Aliases;

    /// <summary>What the operation also touches (R1-06).</summary>
    public IReadOnlyList<string> Related => Record.Related;

    /// <summary>What <see cref="IMutationTargetResolver.ConfirmAsync"/> compares right before the object is acted on
    /// (D9): a container's id and creation time, a volume's name and driver, the existence of a record's id, … Lives in
    /// memory between the resolution and the run only (D14).</summary>
    public string? Fingerprint { get; }
}

/// <summary>A server as a whole (kind <c>server</c>).</summary>
public sealed record ResolvedServer : ResolvedTarget
{
    public ResolvedServer(MutationTargetResolver.ResolverToken token, MutationTarget record, string? fingerprint)
        : base(token, record, fingerprint) { }
}

/// <summary>A host-level operation's server, with the named sub-object on it when the operation acts on one (an nginx
/// site, a systemd unit, a firewall rule — D11; filled from contract 0012 step 6 on).</summary>
public sealed record ResolvedHostObject : ResolvedTarget
{
    public ResolvedHostObject(MutationTargetResolver.ResolverToken token, MutationTarget record, string? fingerprint,
        string? subObject)
        : base(token, record, fingerprint) => SubObject = subObject;

    /// <summary>The named object on the server, or <c>null</c> when the server itself is the object.</summary>
    public string? SubObject { get; }
}

/// <summary>A container: its full id, name and server.</summary>
public sealed record ResolvedContainer : ResolvedTarget
{
    public ResolvedContainer(MutationTargetResolver.ResolverToken token, MutationTarget record, string? fingerprint,
        string? composeProject)
        : base(token, record, fingerprint) => ComposeProject = composeProject;

    /// <summary>The compose project the container belongs to, if any.</summary>
    public string? ComposeProject { get; }
}

/// <summary>A Docker network.</summary>
public sealed record ResolvedNetwork : ResolvedTarget
{
    public ResolvedNetwork(MutationTargetResolver.ResolverToken token, MutationTarget record, string? fingerprint)
        : base(token, record, fingerprint) { }

    /// <summary>For a connect / disconnect: the resolved id of the container named in the call.</summary>
    public string? RelatedContainerId => Record.RelatedContainerId;
}

/// <summary>A Docker volume on its server (D5): identified by server, name and driver.</summary>
public sealed record ResolvedVolume : ResolvedTarget
{
    public ResolvedVolume(MutationTargetResolver.ResolverToken token, MutationTarget record, string? fingerprint,
        string driver)
        : base(token, record, fingerprint) => Driver = driver ?? "";

    public string Driver { get; }
}

/// <summary>A recorded volume backup (D5): <see cref="ResolvedTarget.Id"/> is the backup id, <see cref="ResolvedTarget.Name"/>
/// the backed-up volume, the archive's file name an alias.</summary>
public sealed record ResolvedVolumeBackup : ResolvedTarget
{
    public ResolvedVolumeBackup(MutationTargetResolver.ResolverToken token, MutationTarget record, string? fingerprint,
        string volumeName, string fileName, string? restoreInto, bool restoreIntoExists)
        : base(token, record, fingerprint)
    {
        VolumeName = volumeName ?? "";
        FileName = fileName ?? "";
        RestoreInto = restoreInto;
        RestoreIntoExists = restoreIntoExists;
    }

    public string VolumeName { get; }

    public string FileName { get; }

    /// <summary>For a restore: the volume the archive goes into (the backed-up volume unless the call named another).</summary>
    public string? RestoreInto { get; }

    /// <summary>For a restore: whether <see cref="RestoreInto"/> exists (it is then related); otherwise the restore
    /// creates it (Q4).</summary>
    public bool RestoreIntoExists { get; }
}

/// <summary>A log-alert rule (D6). Whiskers-wide: <see cref="ResolvedTarget.ServerId"/> is <c>null</c>.</summary>
public sealed record ResolvedLogAlertRule : ResolvedTarget
{
    public ResolvedLogAlertRule(MutationTargetResolver.ResolverToken token, MutationTarget record, string? fingerprint)
        : base(token, record, fingerprint) { }
}

/// <summary>A Kubernetes workload (or a Docker container shown as one): the pod and its controller. The controller's
/// kind, namespace and UID follow with contract 0012 step 5 (D8).</summary>
public sealed record ResolvedWorkload : ResolvedTarget
{
    public ResolvedWorkload(MutationTargetResolver.ResolverToken token, MutationTarget record, string? fingerprint,
        string podId, string? controllerName)
        : base(token, record, fingerprint)
    {
        PodId = podId ?? "";
        ControllerName = controllerName;
    }

    public string PodId { get; }

    public string? ControllerName { get; }
}

/// <summary>A cloud server (the Whiskers server it is bound to).</summary>
public sealed record ResolvedCloudServer : ResolvedTarget
{
    public ResolvedCloudServer(MutationTargetResolver.ResolverToken token, MutationTarget record, string? fingerprint)
        : base(token, record, fingerprint) { }
}

/// <summary>A scheduled task, by its task id.</summary>
public sealed record ResolvedScheduledTask : ResolvedTarget
{
    public ResolvedScheduledTask(MutationTargetResolver.ResolverToken token, MutationTarget record, string? fingerprint)
        : base(token, record, fingerprint) { }
}

/// <summary>A DNS zone, normalised.</summary>
public sealed record ResolvedDnsZone : ResolvedTarget
{
    public ResolvedDnsZone(MutationTargetResolver.ResolverToken token, MutationTarget record, string? fingerprint)
        : base(token, record, fingerprint) { }
}

/// <summary>A Git deployment app at the revision that was decided on.</summary>
public sealed record ResolvedGitApp : ResolvedTarget
{
    public ResolvedGitApp(MutationTargetResolver.ResolverToken token, MutationTarget record, string? fingerprint,
        int revision)
        : base(token, record, fingerprint) => Revision = revision;

    public int Revision { get; }
}

/// <summary>A stored webhook. Its resolved action target follows with contract 0012 step 4 (D7).</summary>
public sealed record ResolvedWebhook : ResolvedTarget
{
    public ResolvedWebhook(MutationTargetResolver.ResolverToken token, MutationTarget record, string? fingerprint,
        ResolvedTarget? action)
        : base(token, record, fingerprint) => Action = action;

    /// <summary>What the webhook's action acts on, when the decision resolved it (step 4); <c>null</c> otherwise.</summary>
    public ResolvedTarget? Action { get; }
}

/// <summary>A record of Whiskers' own (D12): a settings section, an account, an identity binding, an MCP key, a
/// registry, a vault secret, a self-backup, an endpoint check, an automation feature — or, for a
/// <see cref="TargetResolutionKind.Singleton"/> capability, Whiskers itself (<see cref="Store"/> <c>whiskers</c> or
/// <c>fleet</c>).</summary>
public sealed record ResolvedRecord : ResolvedTarget
{
    public ResolvedRecord(MutationTargetResolver.ResolverToken token, MutationTarget record, string? fingerprint,
        string store, string recordId)
        : base(token, record, fingerprint)
    {
        Store = store ?? "";
        RecordId = recordId ?? "";
    }

    /// <summary>Where the record lives: <c>config-section</c>, <c>account</c>, <c>identity</c>, <c>mcp-key</c>,
    /// <c>registry</c>, <c>vault-secret</c>, <c>self-backup</c>, <c>endpoint-check</c>, <c>automation-feature</c>,
    /// <c>whiskers</c>, <c>fleet</c>.</summary>
    public string Store { get; }

    /// <summary>The record's id in its store (for an endpoint check its check id, while <see cref="ResolvedTarget.Id"/>
    /// keeps the recorded server).</summary>
    public string RecordId { get; }
}

/// <summary>An object the operation creates: only the server and, where known, the name it will have.</summary>
public sealed record ResolvedNew : ResolvedTarget
{
    public ResolvedNew(MutationTargetResolver.ResolverToken token, MutationTarget record)
        : base(token, record, null) { }
}
