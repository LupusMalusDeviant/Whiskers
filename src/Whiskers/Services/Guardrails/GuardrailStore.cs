using System.Text.Json;
using System.Text.Json.Nodes;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Guardrails;

/// <summary>Persists the guardrail presets in their OWN file (guardrails.json), separate from the
/// agent settings path. Writing is admin-only — a compromised settings editor cannot relax the
/// guardrails. The engine reads only the active preset's policy via <see cref="Current"/>.
/// <para>R1-02: callers only ever get copies (<see cref="Current"/>, <see cref="Config"/>) and a save is copied in,
/// validated, persisted and only then made live — a failed write leaves the previous policy active. A file that
/// cannot be read or holds an invalid configuration puts the store into <see cref="GuardrailPolicy.FailClosed"/>
/// until the file is repaired on the host and Whiskers restarted (INV-25, INV-28; no save gets past it since R1-11). Configuration and load status are published together as one
/// immutable state, so a reader never pairs a configuration with the wrong status.</para>
/// <para>Contract 0010: once a policy has existed (the database's record, <see cref="IGuardrailPolicyMarker"/>), missing
/// both <c>guardrails.json</c> and <c>.bak</c> is fail-closed too, and so is a record that cannot be read. The start runs in
/// two phases: <see cref="InitializeAsync"/> (Order 80) never creates the SafeDefault; <see cref="ReconcileMarkerAsync"/>
/// decides once the schema is current.</para></summary>
public sealed class GuardrailStore : IGuardrailStore, IInitializable
{
    /// <summary>Name of the single preset <see cref="Config"/> shows while the store is fail-closed.</summary>
    public const string FailClosedPreset = "Fail-closed";

    /// <summary>What is enforced: a valid configuration, or (when <paramref name="LoadError"/> is set) the
    /// fail-closed policy.</summary>
    private sealed record State(GuardrailConfig Config, string? LoadError);

    private readonly JsonFileStore<GuardrailConfig> _store;
    private readonly string _filePath;
    private readonly ILogger<GuardrailStore>? _logger;
    private readonly IGuardrailPolicyMarker? _marker;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile State _state = new(GuardrailConfig.SafeDefault(), null);

    // Contract 0010: what phase 1 (the Order-80 load) found, for phase 2 (ReconcileMarkerAsync).
    private volatile bool _undecided;          // neither file: the record decides in phase 2
    private volatile bool _fileAtLoad;         // a policy file (readable or not, or only .bak) was there
    private volatile string _markerSource = GuardrailPolicyMarkerSources.Backfill;
    private volatile bool _markerRecorded;
    private volatile string? _markerProblem;

    /// <summary>Contract 0010 D4: the fail-closed reason between phase 1 and phase 2 when neither file exists.</summary>
    public const string NotDecidedReason = "guardrail policy not decided yet: guardrails.json and guardrails.json.bak are missing, and the record of an earlier policy is read once the database is current.";

    public event Action? Changed;

    public GuardrailStore(ILogger<GuardrailStore>? logger = null, string? filePath = null, DataPathOptions? dataPaths = null,
        IGuardrailPolicyMarker? marker = null)
    {
        _logger = logger;
        _filePath = filePath ?? (dataPaths ?? DataPathOptions.Default).GuardrailsJson;
        _store = new JsonFileStore<GuardrailConfig>(_filePath);
        _marker = marker;
    }

    /// <summary>Contract 0010: true once the database confirmed the record that a guardrail policy has existed.</summary>
    public bool MarkerRecorded => _markerRecorded;

    /// <summary>Contract 0010 D3: why the record could not be written (retried at the next save or start), or <c>null</c>.</summary>
    public string? MarkerProblem => _markerProblem;

    public GuardrailPolicy Current
    {
        get
        {
            var state = _state;
            return state.LoadError is null ? state.Config.ActivePolicy().Clone() : GuardrailPolicy.FailClosed();
        }
    }

    public GuardrailConfig Config
    {
        get
        {
            var state = _state;
            return state.LoadError is null
                ? state.Config.Clone()
                : new GuardrailConfig
                {
                    ActivePreset = FailClosedPreset,
                    Presets = { new GuardrailPreset { Name = FailClosedPreset, Policy = GuardrailPolicy.FailClosed() } },
                };
        }
    }

    /// <summary>Why the stored configuration is not enforced (the fail-closed policy is), or <c>null</c>.</summary>
    public string? LoadError => _state.LoadError;

    /// <inheritdoc />
    public (string Preset, GuardrailPolicy Policy) Active
    {
        get
        {
            var state = _state;
            return state.LoadError is null
                ? (state.Config.ActivePreset, state.Config.ActivePolicy().Clone())
                : (FailClosedPreset, GuardrailPolicy.FailClosed());
        }
    }

    public int Order => 80;

    private volatile bool _loaded;

    /// <summary>True once the configuration was read from disk (<see cref="InitializeAsync"/> or
    /// <see cref="InspectAsync"/>). Before that <see cref="Current"/> is only the in-memory SafeDefault.</summary>
    public bool IsLoaded => _loaded;

    /// <summary>Phase 1 (contract 0010 D4): loads the config from disk and migrates a legacy single-policy file into a
    /// "Standard" preset. With neither <c>guardrails.json</c> nor <c>.bak</c> it saves nothing: the store stays fail-closed
    /// (<see cref="NotDecidedReason"/>) until <see cref="ReconcileMarkerAsync"/> has read the database's record.</summary>
    public Task InitializeAsync(CancellationToken ct = default) => LoadAsync(persist: true, ct);

    /// <summary>Contract 0009 A1.2: the same load and validation for a host CLI next to the running instance, but
    /// read-only: nothing is saved, no unreadable copy is kept, a legacy file is migrated in memory only. A missing file
    /// follows contract 0010's table read-only: the record is read, never written; the SafeDefault is held in memory only
    /// when there is no record and no snapshot, and a failed read is fail-closed.</summary>
    public Task InspectAsync(CancellationToken ct = default) => LoadAsync(persist: false, ct);

    /// <summary>Phase 2 (contract 0010 D4), run once both database schemas are current and before requests are served:
    /// with a policy file present it ensures the record (D3 a); with neither file it decides by the record (first start →
    /// SafeDefault saved, then recorded; record present, snapshot rows or an unreadable record → fail-closed). Raises
    /// <see cref="Changed"/>.</summary>
    public async Task ReconcileMarkerAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!_loaded)
            {
                await LoadLockedAsync(persist: true, ct);
                _loaded = true;
            }
            if (_undecided)
            {
                _undecided = false;
                await DecideMissingAsync(persist: true, ct);
            }
            else if (_fileAtLoad)
            {
                await TryEnsureMarkerAsync(_markerSource);
            }
        }
        finally
        {
            _lock.Release();
        }

        Changed?.Invoke();
    }

    /// <summary>Contract 0010 D6: the host CLI <c>--reset-guardrails</c>. Writes <see cref="GuardrailConfig.SafeDefault"/>
    /// with the kill switch on (Q2) and ensures the record (<c>reset-cli</c>). Refuses (<see cref="InvalidOperationException"/>)
    /// while <c>guardrails.json</c> or <c>.bak</c> exists: those are repaired, not replaced. Returns the record problem, if
    /// any (the file is written then; the next start records it).</summary>
    public async Task<string?> ResetOnHostAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_store.Exists())
                throw new InvalidOperationException(
                    "guardrails.json or guardrails.json.bak exists in the data directory; repair it (or restore it from a backup) instead of resetting.");
            var config = GuardrailConfig.SafeDefault();
            config.Presets.First(p => p.Name == config.ActivePreset).Policy.ReadOnlyMode = true;
            await _store.SaveAsync(config);
            _state = new State(config.Clone(), null);
            _undecided = false;
            _fileAtLoad = true;
            _loaded = true;
            await TryEnsureMarkerAsync(GuardrailPolicyMarkerSources.ResetCli);
            _logger?.LogWarning("Guardrails: reset on the host (--reset-guardrails): SafeDefault with the kill switch on.");
            return _markerProblem;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Contract 0010: true when <c>guardrails.json</c> or <c>.bak</c> (or a directory in their place) exists.</summary>
    public bool PolicyFileExists() => _store.Exists();

    /// <summary>The table of contract 0010 for "missing / missing": decided by the record and <c>PolicySnapshots</c>.</summary>
    private async Task DecideMissingAsync(bool persist, CancellationToken ct)
    {
        if (_marker is null)
        {
            FailClosed(false, MarkerUnreadableReason("no record store is configured"), null);
            return;
        }

        DateTime? recordedAt;
        bool snapshots;
        try
        {
            recordedAt = await _marker.RecordedAtAsync(ct);
            snapshots = recordedAt is null && await _marker.PolicySnapshotsExistAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            FailClosed(false, MarkerUnreadableReason(ex.GetType().Name), ex);
            return;
        }

        if (recordedAt is { } at)
        {
            _markerRecorded = true;
            FailClosed(false, PolicyExistedReason($"recorded {at:yyyy-MM-dd HH:mm} UTC"), null);
            return;
        }
        if (snapshots)
        {
            // D8: gateway decisions were made under a policy here — it existed. Recorded now (not by the read-only
            // inspection), and fail-closed either way.
            if (persist) await TryEnsureMarkerAsync(GuardrailPolicyMarkerSources.SnapshotEvidence);
            FailClosed(false, PolicyExistedReason("policy decisions were recorded under it"), null);
            return;
        }

        var initial = GuardrailConfig.SafeDefault();
        if (!persist)
        {
            _state = new State(initial, null);
            _logger?.LogInformation("Guardrails: no file and no record of an earlier policy; SafeDefault (not saved).");
            return;
        }
        try
        {
            await _store.SaveAsync(initial);   // the file first, then the record (D3 b)
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            FailClosed(false, $"the safe default could not be saved as guardrails.json ({ex.GetType().Name}).", ex);
            return;
        }
        _state = new State(initial, null);
        _fileAtLoad = true;
        _logger?.LogInformation("Guardrails: SafeDefault created.");
        await TryEnsureMarkerAsync(GuardrailPolicyMarkerSources.Created);
    }

    /// <summary>D3: writes the record, best effort. A failure changes nothing about the enforced policy; it is logged,
    /// shown on the readiness card (<see cref="MarkerProblem"/>) and retried at the next save or start.</summary>
    private async Task TryEnsureMarkerAsync(string source)
    {
        if (_marker is null)
        {
            _markerProblem = "no record store is configured";
            _logger?.LogError("Guardrails: the record that a guardrail policy exists cannot be written: no record store is configured");
            return;
        }
        try
        {
            await _marker.EnsureAsync(source, CancellationToken.None);
            _markerRecorded = true;
            _markerProblem = null;
        }
        catch (Exception ex)
        {
            _markerProblem = $"the record that a guardrail policy exists could not be written ({ex.GetType().Name})";
            _logger?.LogError(ex, "Guardrails: the record that a guardrail policy exists could not be written; the policy is enforced as it is, and the record is retried at the next save or start");
        }
    }

    private const string WayBack = "Restore guardrails.json on the host or run --reset-guardrails, then restart.";

    private static string PolicyExistedReason(string evidence) =>
        $"guardrails.json and guardrails.json.bak are missing, but this instance had a guardrail policy ({evidence}). {WayBack}";

    private static string MarkerUnreadableReason(string error) =>
        $"guardrails.json and guardrails.json.bak are missing, and the record of an earlier guardrail policy could not be read ({error}). {WayBack}";

    private async Task LoadAsync(bool persist, CancellationToken ct)
    {
        await _lock.WaitAsync();   // as before: the start-up load is not cancelled while it waits for the lock
        try
        {
            await LoadLockedAsync(persist, ct);
            _loaded = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task LoadLockedAsync(bool persist, CancellationToken ct)
    {
        try
        {
            if (!_store.Exists())
            {
                // Contract 0010 D4: a missing policy is decided by the database's record, which phase 1 cannot read yet
                // (the schema may not be current). Nothing is saved here; until phase 2 the store is fail-closed.
                _fileAtLoad = false;
                if (persist)
                {
                    _undecided = true;
                    _state = new State(new GuardrailConfig(), NotDecidedReason);
                    _logger?.LogInformation("Guardrails: neither guardrails.json nor guardrails.json.bak exists; fail-closed until the record of an earlier policy is read.");
                    return;
                }
                await DecideMissingAsync(persist: false, ct);
                return;
            }
            _undecided = false;
            _fileAtLoad = true;
            _markerSource = GuardrailPolicyMarkerSources.Backfill;

            string json;
            JsonObject root;
            try
            {
                json = await File.ReadAllTextAsync(_filePath, ct);
                root = JsonNode.Parse(json) as JsonObject
                       ?? throw new JsonException("The file holds no JSON object.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                FailClosed(persist, $"guardrails.json could not be read ({ex.GetType().Name}).", ex);
                return;
            }

            // The checks below read one occurrence of a property, the deserializer another: a name given twice (in
            // any casing) could pass the check and enforce the other value. Such a file is refused.
            if (DuplicateProperty(root) is { } duplicate)
            {
                FailClosed(persist, $"guardrails.json is invalid: the property '{duplicate}' appears more than once.", null);
                return;
            }

            GuardrailConfig loaded;
            if (IsPresetFile(root))
            {
                // Whiskers writes every field of a preset file. One that is missing would be filled with a permissive
                // default by the deserializer, so the file is refused instead (INV-25).
                if (IncompletePresetFile(root) is { } incomplete)
                {
                    FailClosed(persist, $"guardrails.json is invalid: {incomplete}", null);
                    return;
                }
                try
                {
                    loaded = root.Deserialize<GuardrailConfig>(JsonOptions)
                             ?? throw new JsonException("The file holds no configuration.");
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                {
                    FailClosed(persist, $"guardrails.json could not be read ({ex.GetType().Name}).", ex);
                    return;
                }
            }
            else
            {
                // No preset fields at all: migrated only when it is positively a legacy single-policy file. Anything
                // else (unrelated JSON) is invalid and is neither guessed at nor overwritten.
                if (LegacyPolicy(root) is not { } legacy)
                {
                    FailClosed(persist, "guardrails.json is invalid: it holds no presets and no legacy policy.", null);
                    return;
                }
                var migrated = new GuardrailConfig
                {
                    ActivePreset = "Standard",
                    Presets = new() { new GuardrailPreset { Name = "Standard", Policy = legacy } },
                };
                if (migrated.Problem() is { } legacyProblem)
                {
                    FailClosed(persist, $"guardrails.json is invalid: {legacyProblem}", null);
                    return;
                }
                if (persist) await _store.SaveAsync(migrated);
                loaded = migrated;
                _logger?.LogInformation(persist
                    ? "Guardrails: legacy policy migrated into preset 'Standard'."
                    : "Guardrails: legacy policy read as preset 'Standard' (not saved).");
            }

            // An active preset that does not exist (or any other invalid content) is not guessed at: picking another
            // preset could enforce a weaker policy than the one an admin chose.
            if (loaded.Problem() is { } problem)
            {
                FailClosed(persist, $"guardrails.json is invalid: {problem}", null);
                return;
            }

            _state = new State(loaded.Clone(), null);
            _markerSource = GuardrailPolicyMarkerSources.Loaded;
            _logger?.LogInformation("Guardrails loaded ({Count} presets, active={Active}).",
                loaded.Presets.Count, loaded.ActivePreset);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Whatever else goes wrong while loading, the store stays recoverable and enforces nothing weaker.
            FailClosed(persist, $"guardrails.json could not be loaded ({ex.GetType().Name}).", ex);
        }
    }

    public async Task SaveConfigAsync(GuardrailConfig config, AgentPrincipal editor, CancellationToken ct = default)
    {
        RequireAdmin(editor);
        ArgumentNullException.ThrowIfNull(config);
        // Validate what was handed in, then the copy that is stored and enforced: a copy nobody else holds.
        if (config.Problem() is { } given)
            throw new GuardrailConfigException(given);
        var next = config.Clone();
        if (next.Problem() is { } problem)
            throw new GuardrailConfigException(problem);

        await _lock.WaitAsync(ct);
        try
        {
            var before = _state;
            await _store.SaveAsync(next);   // throws → the previous policy stays live
            _state = new State(next, null);
            await AfterSavedAsync();
            RecordChange(before, next);
            _logger?.LogWarning("Guardrails changed by {Editor} ({Count} presets, active={Active}).",
                Whiskers.Utils.LogSafe.Value(editor.DisplayName), next.Presets.Count, next.ActivePreset);
        }
        finally
        {
            _lock.Release();
        }

        Changed?.Invoke();
    }

    public async Task SaveAsync(GuardrailPolicy policy, AgentPrincipal editor, CancellationToken ct = default)
    {
        // Back-compat: replace the active preset's policy — on a copy, made live only after it is persisted.
        RequireAdmin(editor);
        ArgumentNullException.ThrowIfNull(policy);
        await _lock.WaitAsync(ct);
        try
        {
            var state = _state;
            var next = state.LoadError is null ? state.Config.Clone() : new GuardrailConfig { ActivePreset = "Standard" };
            var active = next.Presets.FirstOrDefault(p => p.Name == next.ActivePreset);
            if (active is null)
            {
                active = new GuardrailPreset { Name = next.ActivePreset };
                next.Presets.Add(active);
            }
            active.Policy = policy.Clone();
            if (next.Problem() is { } problem)
                throw new GuardrailConfigException(problem);
            await _store.SaveAsync(next);
            _state = new State(next, null);
            await AfterSavedAsync();
            RecordChange(state, next);
        }
        finally
        {
            _lock.Release();
        }

        Changed?.Invoke();
    }

    /// <summary>A successful save wrote a policy file: the record is ensured if it is not confirmed yet (D3 c).</summary>
    private async Task AfterSavedAsync()
    {
        _undecided = false;
        _fileAtLoad = true;
        if (!_markerRecorded) await TryEnsureMarkerAsync(GuardrailPolicyMarkerSources.Backfill);
    }

    /// <summary>Enforces <see cref="GuardrailPolicy.FailClosed"/> until a valid <c>guardrails.json</c> is put back on the
    /// host (repaired, or restored from a backup) or <c>--reset-guardrails</c> is run there, and Whiskers restarted
    /// (contract 0010 D6). The unreadable file is kept next to it (<paramref name="keepCopy"/>; not for a read-only
    /// inspection).</summary>
    private void FailClosed(bool keepCopy, string reason, Exception? ex)
    {
        _state = new State(new GuardrailConfig(), reason);
        _logger?.LogError(ex, "Guardrails: {Reason} Every mutation is refused until a valid guardrails.json is put back on the host (repaired or restored) or --reset-guardrails is run there, and Whiskers is restarted",
            reason);
        try
        {
            if (keepCopy && File.Exists(_filePath))
                File.Copy(_filePath, $"{_filePath}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
        }
        catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(copyEx, "Guardrails: the unreadable file could not be kept");
        }
    }

    public async Task DisableKillSwitchAsync(AgentPrincipal editor, CancellationToken ct = default)
    {
        RequireAdmin(editor);
        await _lock.WaitAsync(ct);
        try
        {
            if (_state.LoadError is not null)
                throw new InvalidOperationException("The guardrail configuration could not be loaded; fix it before changing it.");
            var next = _state.Config.Clone();
            var active = next.Presets.FirstOrDefault(p => p.Name == next.ActivePreset);
            if (active is null || !active.Policy.ReadOnlyMode) return;
            active.Policy.ReadOnlyMode = false;
            var before = _state;
            await _store.SaveAsync(next);   // throws → the kill switch stays on
            _state = new State(next, null);
            await AfterSavedAsync();
            RecordChange(before, next);
            _logger?.LogWarning("Kill switch switched off by {Editor} (preset {Preset}).", Whiskers.Utils.LogSafe.Value(editor.DisplayName), active.Name);
        }
        finally
        {
            _lock.Release();
        }

        Changed?.Invoke();
    }

    /// <summary>The policy change as part of the gateway's event (R1-22): versioned, every changed rule named. The
    /// version of an unreadable file is that of no policy.</summary>
    private static void RecordChange(State before, GuardrailConfig after) =>
        Whiskers.Services.Mutations.ConfigChange.Record("guardrails", before.LoadError is null ? before.Config : null, after);

    private static void RequireAdmin(AgentPrincipal editor)
    {
        if (editor.PermissionLevel != McpPermissionLevels.Admin)
            throw new UnauthorizedAccessException(
                $"Guardrails may only be changed by admins ({editor.DisplayName} is '{editor.PermissionLevel}').");
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly string[] PolicyFields =
    [
        nameof(GuardrailPolicy.MaxAutonomousLevel), nameof(GuardrailPolicy.ReadOnlyMode),
        nameof(GuardrailPolicy.RequireConfirmationForWrites), nameof(GuardrailPolicy.ToolDenyList),
        nameof(GuardrailPolicy.ToolAllowList), nameof(GuardrailPolicy.ToolMode), nameof(GuardrailPolicy.ProtectedResources),
        nameof(GuardrailPolicy.ForbiddenArgPatterns), nameof(GuardrailPolicy.MaxActionsPerSession),
    ];

    /// <summary>Policy fields a file may leave out (added after the format was fixed); when present they are kept,
    /// never dropped — a legacy file that turns one on must not lose it in the migration.
    /// Contract 0006: the exposure label prefixes and confirm classes are optional too (<c>null</c> or missing = the
    /// default), so preset files written before them stay complete.</summary>
    private static readonly string[] OptionalPolicyFields =
    [
        nameof(GuardrailPolicy.RequireSecondApprover), nameof(GuardrailPolicy.ExposureLabelPrefixes),
        nameof(GuardrailPolicy.ExposureConfirmClasses), nameof(GuardrailPolicy.KillSwitchBlockedExemptions),
    ];

    private static JsonNode? Field(JsonObject obj, string name, out bool present)
    {
        foreach (var (key, value) in obj)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                present = true;
                return value;
            }
        }
        present = false;
        return null;
    }

    /// <summary>The first property name that occurs twice in one object (ignoring case), anywhere in the tree.</summary>
    private static string? DuplicateProperty(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in obj)
                {
                    if (!seen.Add(key)) return key;
                    if (DuplicateProperty(value) is { } inner) return inner;
                }
                return null;
            }
            case JsonArray array:
                foreach (var item in array)
                    if (DuplicateProperty(item) is { } inner) return inner;
                return null;
            default:
                return null;
        }
    }

    /// <summary>A file that names presets or an active preset is the preset format, however incomplete.</summary>
    private static bool IsPresetFile(JsonObject root)
    {
        Field(root, nameof(GuardrailConfig.Presets), out var presets);
        Field(root, nameof(GuardrailConfig.ActivePreset), out var active);
        return presets || active;
    }

    /// <summary>Why a preset file is incomplete, or <c>null</c>: the active preset, a non-empty preset list, and
    /// for every preset a name and a policy object carrying every policy field.</summary>
    private static string? IncompletePresetFile(JsonObject root)
    {
        if (Field(root, nameof(GuardrailConfig.ActivePreset), out _) is not JsonValue)
            return "the active preset is missing.";
        if (Field(root, nameof(GuardrailConfig.Presets), out _) is not JsonArray { Count: > 0 } presets)
            return "it holds no presets.";
        foreach (var node in presets)
        {
            if (node is not JsonObject preset) return "a preset is not an object.";
            if (Field(preset, nameof(GuardrailPreset.Name), out _) is not JsonValue nameNode)
                return "a preset has no name.";
            var name = nameNode.ToJsonString();
            if (Field(preset, nameof(GuardrailPreset.Policy), out _) is not JsonObject policy)
                return $"preset {name} has no policy.";
            foreach (var field in PolicyFields)
            {
                Field(policy, field, out var present);
                if (!present) return $"preset {name}: '{field}' is missing.";
            }
        }
        return null;
    }

    /// <summary>A legacy single-policy file: an object carrying at least one known policy field. Fields it does not
    /// carry keep the restrictive default rather than the permissive <c>new GuardrailPolicy()</c>.</summary>
    private static GuardrailPolicy? LegacyPolicy(JsonObject root)
    {
        if (!PolicyFields.Any(f => { Field(root, f, out var present); return present; })) return null;
        try
        {
            var merged = JsonSerializer.SerializeToNode(GuardrailPolicy.SafeDefault())!.AsObject();
            foreach (var field in PolicyFields.Concat(OptionalPolicyFields))
            {
                var value = Field(root, field, out var present);
                if (present) merged[field] = value?.DeepClone();
            }
            return merged.Deserialize<GuardrailPolicy>(JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }
}
