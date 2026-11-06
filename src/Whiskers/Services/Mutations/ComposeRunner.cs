using Whiskers.Models;
using Whiskers.Services.Docker;
using Whiskers.Services.Notifications;
using Whiskers.Services.Server;
using Whiskers.Utils;

namespace Whiskers.Services.Mutations;

/// <summary>A host command of a compose run that changes something: the caller decides how it runs — the host executor's
/// primitive inside a raw-host-command decision, or (Git deploy) the executor its own <c>build</c> and <c>up</c> always
/// used.</summary>
public delegate Task<CommandResult> ComposeHostCommand(string command, TimeSpan timeout, CancellationToken ct);

/// <summary>How a path runs its compose deployment (contract 0006, Amendments 6–10).</summary>
/// <param name="Pull">The path pulls (<c>compose.deploy</c>, <c>app.deploy</c>, webhook, Git deploy): step 2 pulls every
/// image that is not built (<c>--ignore-buildable</c>), step 3 builds every <c>build:</c> service with <c>--pull</c>. The
/// <c>.env</c> paths (<c>container.env_set</c>, <c>container.env_file_save</c>) pull and build only what is missing,
/// building without <c>--pull</c>.</param>
/// <param name="Timeout">Each command's budget (a script split around step 4 gives each command the budget it had).</param>
public sealed record ComposeRunOptions(bool Pull, TimeSpan Timeout)
{
    /// <summary>Git deploy: its own budgets for pull (300 s), build (600 s) and up (300 s).</summary>
    public TimeSpan? PullTimeout { get; init; }
    public TimeSpan? BuildTimeout { get; init; }
    public TimeSpan? UpTimeout { get; init; }

    /// <summary>Progress lines (Git deploy's log).</summary>
    public IProgress<string>? Progress { get; init; }
}

/// <summary>What a compose run did. <see cref="Refusal"/> is set when it stopped before <c>up</c> (nothing started; the
/// written files and the image tags are put back); <see cref="Violations"/> lists what the re-check after <c>up</c> found
/// undecided, in words that name the service, the kind and the key only.</summary>
public sealed record ComposeRunResult(bool Success, string Output, string Error)
{
    public string? Refusal { get; init; }
    public IReadOnlyList<string> Violations { get; init; } = [];
}

/// <summary>
/// Runs a planned compose deployment in the order of contract 0006 (Amendment 6): (1) write the files, (2) pull, (3)
/// build, (4) inspect every image the run will start, (5) <c>up --pull never --no-build</c> — then re-checks the containers
/// the run created against what was decided. One implementation for every path that runs a <see cref="ComposePlan"/>.
/// </summary>
public interface IComposeRunner
{
    Task<ComposeRunResult> RunAsync(ComposePlan plan, ComposeHostCommand mutate, ComposeRunOptions options,
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ComposeRunner : IComposeRunner
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromSeconds(30);
    private const int MaxOutputChars = 2_000_000;

    private readonly IHostCommandExecutor _reader;
    private readonly IDockerService _docker;
    private readonly ComposeExposureStopper _stopper;
    private readonly INotificationService? _notifications;
    private readonly ILogger<ComposeRunner>? _logger;

    public ComposeRunner(IHostCommandExecutor reader, IDockerService docker, INotificationService? notifications = null,
        ILogger<ComposeRunner>? logger = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _docker = docker ?? throw new ArgumentNullException(nameof(docker));
        _stopper = new ComposeExposureStopper(docker);
        _notifications = notifications;
        _logger = logger;
    }

    private sealed class Run
    {
        public required ComposePlan Plan { get; init; }
        public required ComposeHostCommand Mutate { get; init; }
        public required ComposeRunOptions Options { get; init; }
        public MutationScope? Scope { get; init; }
        public bool Agent { get; init; }
        public IReadOnlySet<string> Confirm { get; init; } = new HashSet<string>();
        public HashSet<string> Decided { get; } = new(StringComparer.Ordinal);
        public List<ComposeExposure> FromImages { get; } = [];
        /// <summary>The label keys of the images step 4 inspected, per service (review m3: such a key is no .env value).</summary>
        public HashSet<(string Service, string Key)> ImageLabelKeys { get; } = [];
        public List<string> Violations { get; } = [];
        public List<string> Output { get; } = [];
    }

    public async Task<ComposeRunResult> RunAsync(ComposePlan plan, ComposeHostCommand mutate, ComposeRunOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(mutate);
        ArgumentNullException.ThrowIfNull(options);
        var scope = MutationScope.Current;
        var run = new Run
        {
            Plan = plan, Mutate = mutate, Options = options, Scope = scope,
            Agent = scope?.AgentRuleSet ?? false,
            Confirm = scope?.AgentExposureConfirmClasses ?? new HashSet<string>(),
        };
        // What was decided: every exposure the enclosing decisions covered on this server (the plan's were checked by
        // EnsureDecided before the run). Outside a gateway scope only the plan's own.
        foreach (var identity in scope?.DecidedExposures(plan.ServerId) ?? plan.Exposures().Select(e => e.Identity).ToHashSet())
            run.Decided.Add(ComposeExposures.ComparisonKey(identity));

        var images = plan.ActiveServices.Select(s => s.Image).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var backup = $"/tmp/whiskers-compose-{Guid.NewGuid():N}";
        var written = false;
        var restoreFiles = false;
        var tagsAtRisk = false;      // from the start of step 2 until step 4 passed
        IReadOnlyDictionary<string, string?>? recorded = null;
        IReadOnlyDictionary<string, string?>? afterBuild = null;
        try
        {
            // 1) The files, exactly as planned; what they replace is kept for a refusal.
            Report(run, "① Writing the planned files…");
            var write = await mutate(plan.WriteScript(backup), options.Timeout, ct);
            if (!write.Success) return Failed(run, write, "Writing the compose files failed.");
            written = plan.WrittenFiles.Count > 0;

            // The tag each image points to now, so a refused run can put it back (Amendment 7).
            try
            {
                recorded = images.Count == 0 ? new Dictionary<string, string?>() : Ids(await InspectImagesAsync(plan, images, ct));
            }
            catch (ComposePlanException ex)
            {
                restoreFiles = true;
                return Refused(run, ex.Message);
            }

            // 2) Pull.
            tagsAtRisk = true;
            var active = plan.ActiveServices;
            if (options.Pull)
            {
                Report(run, "② docker compose pull…");
                var pull = await mutate(plan.PullCommand + " 2>&1", options.PullTimeout ?? options.Timeout, ct);
                if (!pull.Success) return Failed(run, pull, "docker compose pull failed.");
                Keep(run, pull);
            }
            else if (active.Where(s => !s.Build && recorded.GetValueOrDefault(s.Image!) is null).Select(s => s.Name).ToList()
                     is { Count: > 0 } missing)
            {
                Report(run, "② docker compose pull (missing images)…");
                var pull = await mutate(plan.PullServicesCommand(missing) + " 2>&1", options.PullTimeout ?? options.Timeout, ct);
                if (!pull.Success) return Failed(run, pull, "docker compose pull failed.");
                Keep(run, pull);
            }

            // 3) Build.
            if (options.Pull && active.Any(s => s.Build))
            {
                Report(run, "③ docker compose build…");
                var build = await mutate(plan.BuildCommand + " 2>&1", options.BuildTimeout ?? options.Timeout, ct);
                if (!build.Success) return Failed(run, build, "docker compose build failed.");
                Keep(run, build);
            }
            else if (!options.Pull
                     && active.Where(s => s.Build && recorded.GetValueOrDefault(s.Image!) is null).Select(s => s.Name).ToList()
                         is { Count: > 0 } toBuild)
            {
                Report(run, "③ docker compose build (missing images)…");
                var build = await mutate(plan.BuildServicesCommand(toBuild) + " 2>&1", options.BuildTimeout ?? options.Timeout, ct);
                if (!build.Success) return Failed(run, build, "docker compose build failed.");
                Keep(run, build);
            }

            // 4) Every image the run will start, as up will use it.
            Report(run, "④ Inspecting the images…");
            IReadOnlyDictionary<string, (string Id, IReadOnlyDictionary<string, string> Labels)?> inspected;
            try
            {
                inspected = images.Count == 0
                    ? new Dictionary<string, (string, IReadOnlyDictionary<string, string>)?>()
                    : await InspectImagesAsync(plan, images, ct);
            }
            catch (ComposePlanException ex)
            {
                restoreFiles = true;
                return Refused(run, ex.Message);
            }
            afterBuild = Ids(inspected);
            if (active.FirstOrDefault(s => inspected.GetValueOrDefault(s.Image!) is null) is { } absent)
            {
                restoreFiles = true;
                return Refused(run, $"The image of '{absent.Name}' is not on the server after pulling and building; nothing was started.");
            }
            if (CheckImages(run, active, inspected) is { } refusal)
            {
                restoreFiles = true;
                return Refused(run, refusal);
            }
            tagsAtRisk = false;

            // 5) Up — nothing pulled or built inside it.
            Report(run, "⑤ docker compose up -d…");
            CommandResult up;
            System.Runtime.ExceptionServices.ExceptionDispatchInfo? upThrew = null;
            try
            {
                up = await mutate(plan.UpCommand + " 2>&1", options.UpTimeout ?? options.Timeout, ct);
            }
            catch (Exception ex)
            {
                // Review m2: an up that threw (a timeout, a cancellation, the executor) may still have created
                // containers — the re-check runs all the same, then the exception goes on.
                upThrew = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
                up = new CommandResult { ExitCode = -1, Error = "docker compose up did not complete." };
            }
            Keep(run, up);
            // The re-check runs also when up failed: it may have created some containers.
            await RecheckAsync(run, ct);
            // Review NIT: nothing was deployed when up failed — no exposure_change then.
            if (up.Success) await NotifyChangeAsync(run);
            if (run.Violations.Count > 0) await NotifyViolationAsync(run);
            upThrew?.Throw();
            var success = up.Success && run.Violations.Count == 0;
            if (!up.Success) MutationScope.Fail("docker compose up failed.");
            return new ComposeRunResult(success, string.Join('\n', run.Output), up.Error) { Violations = run.Violations };
        }
        finally
        {
            var reported = run.Violations.Count;
            // Any exit from the start of step 2 without a passed step 4 — refusal, failure, timeout, cancellation — puts
            // back the tags that moved (Amendments 7, 9 and 10).
            if (tagsAtRisk && recorded is not null) await RestoreTagsAsync(run, recorded, afterBuild);
            if (restoreFiles && written)
            {
                var restored = await TryAsync(() => mutate(plan.RestoreScript(backup), RestoreTimeout, CancellationToken.None));
                if (restored?.Success != true) Violation(run, "the previous compose files could not be restored");
            }
            else if (written)
                await TryAsync(() => mutate(ComposePlan.CleanupScript(backup), RestoreTimeout, CancellationToken.None));
            if (run.Violations.Count > reported) await NotifyViolationAsync(run);
        }
    }

    // ------------------------------------------------------------------------------------------- step 4

    /// <summary>An image label of an exposure class that was not decided: under the agent rule set a class that needs a
    /// confirmation refuses the run (naming the key); otherwise the label joins this run's decided set and is audited
    /// and announced as an exposure change (Amendments 4 and 5).</summary>
    private static string? CheckImages(Run run,
        IReadOnlyList<ComposeServicePlan> active, IReadOnlyDictionary<string, (string Id, IReadOnlyDictionary<string, string> Labels)?> inspected)
    {
        foreach (var service in active)
        {
            var image = inspected[service.Image!]!.Value;
            foreach (var label in image.Labels.Keys) run.ImageLabelKeys.Add((service.Name, label.ToLowerInvariant()));
            foreach (var exposure in ComposeExposures.FromImageLabels(service.Name, image.Labels, run.Plan.ExposurePrefixes))
            {
                if (run.Decided.Contains(ComposeExposures.ComparisonKey(exposure.Identity))) continue;
                var cls = ExposureClasses.Of(exposure.Kind, exposure.Key, run.Plan.ExposurePrefixes);
                if (cls is null) continue;
                if (run.Agent && run.Confirm.Contains(cls))
                    return $"The image of '{service.Name}' carries the label {exposure.Key} (class '{cls}'), which was not " +
                           "decided; nothing was started.";
                run.Decided.Add(ComposeExposures.ComparisonKey(exposure.Identity));
                run.FromImages.Add(exposure);
                MutationScope.Note($"exposure from the image of {service.Name}: label {exposure.Key} (class '{cls}')");
            }
        }
        return null;
    }

    // ------------------------------------------------------------------------------------------- re-check

    /// <summary>
    /// The re-check after <c>up</c> (Amendments 2–10): the containers this run created or recreated (ids that were not in
    /// the plan's impact, of the plan's project and services) join the scope's stop set; each is inspected and its
    /// exposures compared on (service, kind, key, value) with what was decided; a namespace it shares must be a decided
    /// container or one this run created. A run that cannot be checked counts as an undecided exposure of class
    /// <c>public</c>. Under the agent rule set a container with an undecided exposure of a class that needs a confirmation
    /// is stopped (no restart, then stop, retried once); otherwise it keeps running and the run is reported as failed.
    /// </summary>
    private async Task RecheckAsync(Run run, CancellationToken ct)
    {
        var plan = run.Plan;
        IList<ContainerInfo> listed;
        try
        {
            listed = await _docker.ListContainersAsync(all: true, serverId: plan.ServerId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Re-check of project {Project}: the containers could not be listed", plan.ProjectName);
            Violation(run, "the containers of the project could not be listed after up; every one counts as undecided (class 'public')");
            if (run.Agent && run.Confirm.Contains(ExposureClasses.Public))
            {
                // Fallback (Amendments 5–7): stop every container of the project with the plan's own command.
                var stopped = await TryAsync(() => run.Mutate(plan.StopCommand + " 2>&1", RestoreTimeout, CancellationToken.None));
                if (stopped?.Success != true) Violation(run, "the containers of the project were not stopped");
            }
            return;
        }

        var impact = plan.Impact.Select(i => i.Container.Id).ToHashSet(StringComparer.Ordinal);
        var services = plan.Services.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var created = listed.DistinctBy(c => c.Id)
            .Where(c => c.Labels.GetValueOrDefault(ComposeModel.ProjectLabel) == plan.ProjectName
                        && c.Labels.GetValueOrDefault(ComposeModel.ServiceLabel) is { } s && services.Contains(s)
                        && !impact.Contains(c.Id))
            .ToList();
        // Computed on every path and under both rule sets (Amendment 10): the namespace rule reads it too.
        run.Scope?.AddCreatedByRun(plan.ServerId, created.Select(c => c.Id));
        if (created.Count == 0) return;

        var undecided = new Dictionary<string, (string Name, HashSet<string> Classes)>(StringComparer.Ordinal);
        void Undecided(ContainerInfo container, string cls, string what)
        {
            if (!undecided.TryGetValue(container.Id, out var entry))
                undecided[container.Id] = entry = (container.Name, new HashSet<string>(StringComparer.Ordinal));
            entry.Classes.Add(cls);
            Violation(run, $"{container.Labels.GetValueOrDefault(ComposeModel.ServiceLabel) ?? container.Name}: {what}");
        }

        IReadOnlyList<System.Text.Json.JsonElement>? documents = null;
        try
        {
            var result = await _reader.ExecuteAsync(plan.ServerId, ComposeExposures.InspectContainersCommand(created.Select(c => c.Id)),
                ReadTimeout, CancellationToken.None, MaxOutputChars);
            if (result.Success && !result.OutputTruncated) documents = ComposeExposures.ParseInspect(result.Output);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Re-check of project {Project}: the created containers could not be inspected", plan.ProjectName);
        }

        foreach (var container in created)
        {
            var document = documents?.FirstOrDefault(d => ComposeExposures.IdentityOf(d).Id == container.Id);
            if (document is not { } doc)
            {
                Undecided(container, ExposureClasses.Public, "the container could not be checked (counts as class 'public')");
                continue;
            }
            var service = ComposeExposures.IdentityOf(doc).Service ?? container.Labels.GetValueOrDefault(ComposeModel.ServiceLabel) ?? "";
            IReadOnlyList<ComposeExposure> exposures;
            IReadOnlyList<(string Field, string ContainerId)> shared;
            try
            {
                (exposures, shared) = ComposeExposures.FromInspect(service, doc, plan.ExposurePrefixes);
            }
            catch (ComposePlanException)
            {
                Undecided(container, ExposureClasses.Public, "the container could not be checked (counts as class 'public')");
                continue;
            }
            foreach (var exposure in exposures)
            {
                if (run.Decided.Contains(ComposeExposures.ComparisonKey(exposure.Identity))) continue;
                if (ExposureClasses.Of(exposure.Kind, exposure.Key, plan.ExposurePrefixes) is not { } cls) continue;
                var note = "";
                if (exposure is { Kind: ExposureKind.HostNamespace, Key: "cgroup" }
                    && !plan.Services.Any(s => s.Name == service && s.DeclaresCgroup))
                {
                    // Review M1: a cgroup namespace the plan did not set is the daemon's default — not an exposure of the
                    // deployment when it equals that default. A default that cannot be read fails closed.
                    var daemonDefault = await CgroupnsDefaultAsync(plan.ServerId);
                    if (string.Equals(daemonDefault, "host", StringComparison.Ordinal)) continue;
                    if (daemonDefault is null) note = "; the daemon's default cgroup namespace mode could not be read";
                }
                Undecided(container, cls, $"undecided {Named(run, exposure)} (class '{cls}'){note}");
            }
            // Amendment 9: a namespace shared at run time must be a decided container or one this run created.
            foreach (var (field, id) in shared)
                if (run.Scope is not { } scope
                    || !(scope.CoversRelated(plan.ServerId, id) || scope.CreatedByRun(plan.ServerId, id)))
                    Undecided(container, ExposureClasses.Host, $"shares the {field} namespace of a container that was not decided (class 'host')");
        }

        if (!run.Agent) return;
        foreach (var (id, (name, classes)) in undecided)
        {
            if (!classes.Any(run.Confirm.Contains)) continue;
            if (!await _stopper.StopAsync(id, plan.ServerId))
                Violation(run, $"{name}: container still running");
            else
                MutationScope.Note($"stopped {name}: an exposure it was started with was not decided", success: false);
        }
    }

    /// <summary>
    /// Review m3: how a violation names an exposure the re-check found — its kind and key, unless the key could be an
    /// <c>.env</c> value: the plan withheld a key of that service and kind, or a label key is neither one the plan read
    /// literally nor one of the inspected image's labels. Then the kind only.
    /// </summary>
    private static string Named(Run run, ComposeExposure exposure)
    {
        var planned = run.Plan.Exposures().Where(p => p.Service == exposure.Service && p.Kind == exposure.Kind).ToList();
        var hidden = planned.Any(p => p.KeyWithheld)
                     || (exposure.Kind == ExposureKind.RouteLabel
                         && !planned.Any(p => string.Equals(p.Key, exposure.Key, StringComparison.OrdinalIgnoreCase))
                         && !run.ImageLabelKeys.Contains((exposure.Service, exposure.Key.ToLowerInvariant())));
        return hidden
            ? $"{ExposureClasses.Noun(exposure.Kind)} (withheld)"
            : $"{ExposureClasses.Noun(exposure.Kind)} {exposure.Key}";
    }

    // ------------------------------------------------------------------------------------------- cgroup default

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _cgroupnsDefaults =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Review M1: the daemon's default cgroup namespace mode — <c>host</c> or <c>private</c> — read once per server and
    /// kept (a failed read is tried again next time): <c>default-cgroupns-mode</c> of <c>/etc/docker/daemon.json</c> when
    /// it sets one, otherwise <c>host</c> for cgroup v1 and <c>private</c> for v2 (<c>docker info</c>'s
    /// <c>CgroupVersion</c>). <c>null</c> when it cannot be read: a daemon.json that is not JSON, an unknown cgroup
    /// version, a failed command.
    /// </summary>
    private async Task<string?> CgroupnsDefaultAsync(string serverId)
    {
        if (_cgroupnsDefaults.TryGetValue(serverId, out var known)) return known;
        try
        {
            var result = await _reader.ExecuteAsync(serverId, ComposeExposures.CgroupnsDefaultCommand, ReadTimeout,
                CancellationToken.None, 64_000);
            if (!result.Success || result.OutputTruncated) return null;
            if (ComposeExposures.ParseCgroupnsDefault(result.Output) is not { } mode) return null;
            _cgroupnsDefaults[serverId] = mode;
            return mode;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogWarning(ex, "The default cgroup namespace mode of server {Server} could not be read", serverId);
            return null;
        }
    }

    // ------------------------------------------------------------------------------------------- tags

    /// <summary>Puts back each tag that moved since it was recorded — but only one that still points to what this run's
    /// pull and build left (a check followed by a tag; a tag another run moved meanwhile is left alone). A tag that was
    /// absent is removed (the tag only). Every command runs with its own 30 s and no cancellation.</summary>
    private async Task RestoreTagsAsync(Run run, IReadOnlyDictionary<string, string?> recorded,
        IReadOnlyDictionary<string, string?>? afterBuild)
    {
        var tags = recorded.Keys.ToList();
        IReadOnlyDictionary<string, string?> now;
        try
        {
            var result = await _reader.ExecuteAsync(run.Plan.ServerId, ComposeExposures.InspectImagesCommand(tags), RestoreTimeout,
                CancellationToken.None, MaxOutputChars);
            if (!result.Success || result.OutputTruncated)
                throw new ComposePlanException("The images could not be read.");
            now = Ids(ComposeExposures.ParseImages(result.Output, tags));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Tag restore of project {Project}: the images could not be read", run.Plan.ProjectName);
            foreach (var tag in tags) Violation(run, $"tag not restored: {tag}");
            return;
        }
        foreach (var tag in tags)
        {
            var before = recorded[tag];
            var current = now.GetValueOrDefault(tag);
            if (current == before) continue;                                          // never moved
            if (afterBuild is not null && afterBuild.GetValueOrDefault(tag) != current) continue;   // moved by another run
            var command = before is null
                ? $"docker image rm --no-prune {ShellUtils.Quote(tag)}"
                : $"docker tag {ShellUtils.Quote(before)} {ShellUtils.Quote(tag)}";
            var restored = await TryAsync(() => run.Mutate(command, RestoreTimeout, CancellationToken.None));
            if (restored?.Success != true) Violation(run, $"tag not restored: {tag}");
        }
    }

    // ------------------------------------------------------------------------------------------- notifications

    /// <summary>A person's (or a person's automation's) run that made a service public or gave it powers of the host:
    /// one <c>exposure_change</c> per deployment, with the service, the kind and the key only. A failed send undoes
    /// nothing.</summary>
    private async Task NotifyChangeAsync(Run run)
    {
        if (run.Agent || _notifications is null) return;
        var lines = run.Plan.Exposures().Concat(run.FromImages)
            .Where(e => ExposureClasses.Of(e.Kind, e.Key, run.Plan.ExposurePrefixes) is ExposureClasses.Public or ExposureClasses.Host)
            .Select(Safe).Distinct(StringComparer.Ordinal).ToList();
        if (lines.Count == 0) return;
        await SendAsync(NotificationEventTypes.ExposureChange, run,
            $"Compose project '{run.Plan.ProjectNameForAudit}' was deployed with exposure changes: {string.Join("; ", lines)}.");
    }

    private Task NotifyViolationAsync(Run run) =>
        SendAsync(NotificationEventTypes.ExposureViolation, run,
            $"Compose project '{run.Plan.ProjectNameForAudit}': {string.Join("; ", run.Violations.Distinct(StringComparer.Ordinal))}.");

    private async Task SendAsync(string eventType, Run run, string message)
    {
        if (_notifications is null) return;
        try
        {
            await _notifications.SendAsync(new NotificationEvent
            {
                EventType = eventType,
                ContainerName = run.Plan.ProjectNameForAudit,
                ServerId = run.Plan.ServerId,
                ImageInfo = message,
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "The {EventType} notification for project {Project} could not be sent", eventType,
                run.Plan.ProjectNameForAudit);
        }
    }

    /// <summary>The service, the kind and the key of an exposure — never its value; a key from a variable not even that.</summary>
    internal static string Safe(ComposeExposure exposure) =>
        exposure.KeyWithheld
            ? $"{exposure.Service}: {ExposureClasses.Noun(exposure.Kind)} (withheld)"
            : $"{exposure.Service}: {ExposureClasses.Noun(exposure.Kind)} {exposure.Key}";

    // ------------------------------------------------------------------------------------------- helpers

    private async Task<IReadOnlyDictionary<string, (string Id, IReadOnlyDictionary<string, string> Labels)?>> InspectImagesAsync(
        ComposePlan plan, IReadOnlyList<string> images, CancellationToken ct)
    {
        var result = await _reader.ExecuteAsync(plan.ServerId, ComposeExposures.InspectImagesCommand(images), ReadTimeout, ct,
            MaxOutputChars);
        if (!result.Success || result.OutputTruncated)
            throw new ComposePlanException("The images of the deployment could not be read on the server; nothing was started.");
        return ComposeExposures.ParseImages(result.Output, images);
    }

    private static IReadOnlyDictionary<string, string?> Ids(
        IReadOnlyDictionary<string, (string Id, IReadOnlyDictionary<string, string> Labels)?> images) =>
        images.ToDictionary(i => i.Key, i => i.Value?.Id, StringComparer.Ordinal);

    private static void Violation(Run run, string text)
    {
        run.Violations.Add(text);
        MutationScope.Note($"exposure violation: {text}", success: false);
    }

    private static void Report(Run run, string line) => run.Options.Progress?.Report(line);

    private static void Keep(Run run, CommandResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Output)) run.Output.Add(result.Output.Trim());
    }

    private static ComposeRunResult Failed(Run run, CommandResult result, string reason)
    {
        MutationScope.Fail(reason);
        Keep(run, result);
        return new ComposeRunResult(false, string.Join('\n', run.Output), result.Error);
    }

    private static ComposeRunResult Refused(Run run, string reason)
    {
        MutationScope.Fail(reason);
        return new ComposeRunResult(false, string.Join('\n', run.Output), reason) { Refusal = reason };
    }

    private async Task<CommandResult?> TryAsync(Func<Task<CommandResult>> command)
    {
        try
        {
            return await command();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "A compose clean-up command failed");
            return null;
        }
    }
}

/// <summary>
/// Stops a container a compose run created whose exposure was not decided (contract 0006, Amendments 2–5): no restart
/// (<c>container.disable_restart</c>), then <c>container.stop</c>, retried once. It runs inside <c>compose.deploy</c>,
/// <c>gitdeploy.deploy</c> or <c>container.env_set</c>, which all compose both operations; the scope guard checks the
/// open decision and binds the container to the run's stop set (<see cref="MutationScope.AddCreatedByRun"/>).
/// </summary>
[MutationHandler("compose.deploy")]
internal sealed class ComposeExposureStopper(IDockerService docker)
{
    public async Task<bool> StopAsync(string containerId, string serverId)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await docker.DisableRestartAsync(containerId, serverId);
                await docker.StopContainerAsync(containerId, serverId);
                return true;
            }
            catch (Exception) when (attempt == 0)
            {
                // retried once
            }
            catch (Exception)
            {
                return false;
            }
        }
        return false;
    }
}
