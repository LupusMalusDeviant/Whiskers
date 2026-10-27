using System.Text.Json;
using Whiskers.Models;
using Whiskers.Services.Scheduler;

namespace Whiskers.Services.Mutations;

/// <summary>
/// The level an operation needs given its arguments (R1-05, INV-19). The gateway takes the stricter of this and the
/// capability's own level before the caller check and before an approval is requested, so an approver is held to
/// what the operation really does, not to the outer tool's level.
/// </summary>
public interface IEffectiveRequirementResolver
{
    Task<string> ResolveAsync(Capability capability, IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken ct);
}

/// <summary>
/// Scheduler tasks carry the level of what they run: a CustomCommand is <c>host.exec</c>, retention deletes old dumps
/// (<c>db.backup_delete</c>, <c>volume.backup_delete</c>), a self-backup creates and prunes recovery points
/// (<c>selfbackup.create</c>, <c>selfbackup.prune</c>). Retention is read with <see cref="TaskExecutor.RetentionOf"/>, the
/// rule the executor and the Scheduled tasks page use. Unknown task types fall back to Admin.
/// </summary>
public sealed class SchedulerRequirementResolver : IEffectiveRequirementResolver
{
    private readonly ICapabilityCatalog _catalog;
    private readonly IServiceProvider _services;

    public SchedulerRequirementResolver(ICapabilityCatalog catalog, IServiceProvider services)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public async Task<string> ResolveAsync(Capability capability, IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken ct)
    {
        switch (capability.Id)
        {
            case "scheduler.create":
                // Same representations the tool accepts after the agent binder (a name or a number, as text or as a
                // JSON number). Anything else, or a number outside the enum, is held to Admin: fail closed.
                if (!TryTaskType(arguments, out var type))
                    return McpPermissionLevels.Admin;
                return LevelFor(type, RetentionRequested(arguments));

            case "scheduler.run":
                // The id exactly as the tool receives it after the agent binder (a JSON number becomes its text).
                // No id, or no task with it, is held to Admin: fail closed.
                if (TaskIdOf(arguments) is not { } taskId)
                    return McpPermissionLevels.Admin;
                // Fail closed: without the scheduler the task's requirement cannot be known.
                var scheduler = _services.GetService<ISchedulerService>()
                    ?? throw new InvalidOperationException("The scheduler is not available.");
                var task = (await scheduler.GetTasksAsync()).FirstOrDefault(x => x.TaskId == taskId);
                return task is null ? McpPermissionLevels.Admin : LevelFor(task.TaskType, TaskExecutor.RetentionOf(task) > 0);

            default:
                return capability.RequiredLevel;
        }
    }

    /// <summary>The level a task of this type needs, from the capabilities it performs.</summary>
    public string LevelFor(ScheduledTaskType type, bool retention) => type switch
    {
        ScheduledTaskType.CustomCommand => LevelOf("host.exec"),
        ScheduledTaskType.ContainerRestart => LevelOf("container.restart"),
        ScheduledTaskType.DbBackup => Stricter(LevelOf("db.backup"), retention ? LevelOf("db.backup_delete") : null),
        ScheduledTaskType.VolumeBackup => Stricter(LevelOf("volume.backup"), retention ? LevelOf("volume.backup_delete") : null),
        ScheduledTaskType.Cleanup => LevelOf("host.prune"),
        ScheduledTaskType.SelfBackup => Stricter(LevelOf("selfbackup.create"), LevelOf("selfbackup.prune")),
        _ => McpPermissionLevels.Admin,
    };

    /// <summary>The stricter of two levels; <c>null</c> stands for "no further requirement".</summary>
    public static string Stricter(string a, string? b) =>
        b is null || McpPermissionLevels.HasAccess(a, b) ? a : b;

    /// <summary>
    /// The level of an underlying capability, including a stricter level configured in Settings → MCP. A disabled
    /// underlying capability refuses the operation (the gateway turns the exception into a Deny).
    /// </summary>
    private string LevelOf(string capabilityId)
    {
        var capability = _catalog.Find(capabilityId);
        if (capability is null)
            return McpPermissionLevels.Admin;
        var overrides = _services.GetService<ICapabilityOverrides>();
        if (overrides is null)
            return capability.RequiredLevel;
        var availability = overrides.Resolve(capability);
        if (!availability.Enabled)
            throw new CapabilityDisabledException(capability);
        return Stricter(capability.RequiredLevel, availability.RequiredLevel);
    }

    /// <summary>Reads <c>taskId</c> the way the agent binder hands it to the tool: a string as it is, any other JSON
    /// value as its raw text. <c>null</c> when absent.</summary>
    public static string? TaskIdOf(IReadOnlyDictionary<string, JsonElement> arguments) =>
        !arguments.TryGetValue("taskId", out var id) || id.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : Whiskers.Services.Agent.AgentArgumentBinder.ConvertJson(id, typeof(string)) as string;

    /// <summary>Reads <c>taskType</c> the way <c>create_scheduled_task</c> does; only defined enum values count.</summary>
    public static bool TryTaskType(IReadOnlyDictionary<string, JsonElement> arguments, out ScheduledTaskType type)
    {
        type = default;
        if (!arguments.TryGetValue("taskType", out var t))
            return false;
        var text = t.ValueKind switch
        {
            JsonValueKind.String => t.GetString(),
            JsonValueKind.Number => t.GetRawText(),
            _ => null,
        };
        return text is not null && Enum.TryParse(text.Trim(), true, out type) && Enum.IsDefined(type);
    }

    /// <summary>
    /// Whether the created task will prune old backups: the tool stores <c>maxBackups</c> only when it is above 0 and
    /// defaults to 7, so absent or null means retention. A value that cannot be read as an integer is treated as
    /// retention (the stricter reading).
    /// </summary>
    public static bool RetentionRequested(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        if (!arguments.TryGetValue("maxBackups", out var m) || m.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return true;
        if (m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out var n))
            return n > 0;
        if (m.ValueKind == JsonValueKind.String && int.TryParse(m.GetString()?.Trim(), out var s))
            return s > 0;
        return true;
    }
}

/// <summary>An operation needs a capability that is switched off in Settings → MCP.</summary>
public sealed class CapabilityDisabledException(Capability capability)
    : InvalidOperationException($"'{capability.McpTool ?? capability.Id}' is disabled in Settings → MCP.")
{
    public Capability Capability { get; } = capability;
}
