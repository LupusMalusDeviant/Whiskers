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
/// immutable state, so a reader never pairs a configuration with the wrong status.</para></summary>
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
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile State _state = new(GuardrailConfig.SafeDefault(), null);

    public event Action? Changed;

    public GuardrailStore(ILogger<GuardrailStore>? logger = null, string? filePath = null, DataPathOptions? dataPaths = null)
    {
        _logger = logger;
        _filePath = filePath ?? (dataPaths ?? DataPathOptions.Default).GuardrailsJson;
        _store = new JsonFileStore<GuardrailConfig>(_filePath);
    }

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

    /// <summary>Loads the config from disk; creates the SafeDefault on first run and migrates a
    /// legacy single-policy file into a "Standard" preset.</summary>
    public int Order => 80;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync();
        try
        {
            if (!_store.Exists())
            {
                var initial = GuardrailConfig.SafeDefault();
                await _store.SaveAsync(initial);
                _state = new State(initial, null);
                _logger?.LogInformation("Guardrails: SafeDefault created.");
                return;
            }

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
                FailClosed($"guardrails.json could not be read ({ex.GetType().Name}).", ex);
                return;
            }

            // The checks below read one occurrence of a property, the deserializer another: a name given twice (in
            // any casing) could pass the check and enforce the other value. Such a file is refused.
            if (DuplicateProperty(root) is { } duplicate)
            {
                FailClosed($"guardrails.json is invalid: the property '{duplicate}' appears more than once.", null);
                return;
            }

            GuardrailConfig loaded;
            if (IsPresetFile(root))
            {
                // Whiskers writes every field of a preset file. One that is missing would be filled with a permissive
                // default by the deserializer, so the file is refused instead (INV-25).
                if (IncompletePresetFile(root) is { } incomplete)
                {
                    FailClosed($"guardrails.json is invalid: {incomplete}", null);
                    return;
                }
                try
                {
                    loaded = root.Deserialize<GuardrailConfig>(JsonOptions)
                             ?? throw new JsonException("The file holds no configuration.");
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                {
                    FailClosed($"guardrails.json could not be read ({ex.GetType().Name}).", ex);
                    return;
                }
            }
            else
            {
                // No preset fields at all: migrated only when it is positively a legacy single-policy file. Anything
                // else (unrelated JSON) is invalid and is neither guessed at nor overwritten.
                if (LegacyPolicy(root) is not { } legacy)
                {
                    FailClosed("guardrails.json is invalid: it holds no presets and no legacy policy.", null);
                    return;
                }
                var migrated = new GuardrailConfig
                {
                    ActivePreset = "Standard",
                    Presets = new() { new GuardrailPreset { Name = "Standard", Policy = legacy } },
                };
                if (migrated.Problem() is { } legacyProblem)
                {
                    FailClosed($"guardrails.json is invalid: {legacyProblem}", null);
                    return;
                }
                await _store.SaveAsync(migrated);
                loaded = migrated;
                _logger?.LogInformation("Guardrails: legacy policy migrated into preset 'Standard'.");
            }

            // An active preset that does not exist (or any other invalid content) is not guessed at: picking another
            // preset could enforce a weaker policy than the one an admin chose.
            if (loaded.Problem() is { } problem)
            {
                FailClosed($"guardrails.json is invalid: {problem}", null);
                return;
            }

            _state = new State(loaded.Clone(), null);
            _logger?.LogInformation("Guardrails loaded ({Count} presets, active={Active}).",
                loaded.Presets.Count, loaded.ActivePreset);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Whatever else goes wrong while loading, the store stays recoverable and enforces nothing weaker.
            FailClosed($"guardrails.json could not be loaded ({ex.GetType().Name}).", ex);
        }
        finally
        {
            _lock.Release();
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
            RecordChange(before, next);
            _logger?.LogWarning("Guardrails changed by {Editor} ({Count} presets, active={Active}).",
                editor.DisplayName, next.Presets.Count, next.ActivePreset);
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
            RecordChange(state, next);
        }
        finally
        {
            _lock.Release();
        }

        Changed?.Invoke();
    }

    /// <summary>Enforces <see cref="GuardrailPolicy.FailClosed"/> until <c>guardrails.json</c> is repaired (or removed,
    /// which recreates the SafeDefault) on the host and Whiskers restarted. The unreadable file is kept next to it.</summary>
    private void FailClosed(string reason, Exception? ex)
    {
        _state = new State(new GuardrailConfig(), reason);
        _logger?.LogError(ex, "Guardrails: {Reason} Every mutation is refused until guardrails.json is repaired or removed on the host and Whiskers is restarted",
            reason);
        try
        {
            if (File.Exists(_filePath))
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
            RecordChange(before, next);
            _logger?.LogWarning("Kill switch switched off by {Editor} (preset {Preset}).", editor.DisplayName, active.Name);
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
    /// never dropped — a legacy file that turns one on must not lose it in the migration.</summary>
    private static readonly string[] OptionalPolicyFields = [nameof(GuardrailPolicy.RequireSecondApprover)];

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
