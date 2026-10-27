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
                // The tool refuses an unparseable type itself; it can only lower nothing, so keep the outer level.
                if (!arguments.TryGetValue("taskType", out var t) || t.ValueKind != JsonValueKind.String
                    || !Enum.TryParse<ScheduledTaskType>(t.GetString(), true, out var type))
                    return capability.RequiredLevel;
                // The tool's default for maxBackups is 7, so a missing value means retention runs.
                var maxBackups = arguments.TryGetValue("maxBackups", out var m) && m.ValueKind == JsonValueKind.Number
                    ? m.GetInt32() : 7;
                return LevelFor(type, maxBackups > 0);

            case "scheduler.run":
                if (!arguments.TryGetValue("taskId", out var id) || id.ValueKind != JsonValueKind.String)
                    return capability.RequiredLevel;
                // Fail closed: without the scheduler the task's requirement cannot be known.
                var scheduler = _services.GetService<ISchedulerService>()
                    ?? throw new InvalidOperationException("The scheduler is not available.");
                var task = (await scheduler.GetTasksAsync()).FirstOrDefault(x => x.TaskId == id.GetString());
                return task is null ? capability.RequiredLevel : LevelFor(task.TaskType, TaskExecutor.RetentionOf(task) > 0);

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

    private string LevelOf(string capabilityId) =>
        _catalog.Find(capabilityId)?.RequiredLevel ?? McpPermissionLevels.Admin;
}
