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
/// until an admin saves a valid one (INV-25, INV-28).</para></summary>
public sealed class GuardrailStore : IGuardrailStore, IInitializable
{
    private readonly JsonFileStore<GuardrailConfig> _store;
    private readonly string _filePath;
    private readonly ILogger<GuardrailStore>? _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private GuardrailConfig _config = GuardrailConfig.SafeDefault();
    private volatile string? _loadError;

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
            var config = Volatile.Read(ref _config);
            return _loadError is null ? config.ActivePolicy().Clone() : GuardrailPolicy.FailClosed();
        }
    }

    public GuardrailConfig Config => _loadError is null
        ? Volatile.Read(ref _config).Clone()
        : new GuardrailConfig
        {
            ActivePreset = FailClosedPreset,
            Presets = { new GuardrailPreset { Name = FailClosedPreset, Policy = GuardrailPolicy.FailClosed() } },
        };

    /// <summary>Why the stored configuration is not enforced (the fail-closed policy is), or <c>null</c>.</summary>
    public string? LoadError => _loadError;

    private const string FailClosedPreset = "Fail-closed (guardrails.json unreadable)";

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
                _config = initial;
                _logger?.LogInformation("Guardrails: SafeDefault created.");
                return;
            }

            GuardrailConfig loaded;
            try
            {
                loaded = await _store.LoadAsync() ?? new GuardrailConfig();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                           or NotSupportedException)
            {
                FailClosed($"guardrails.json could not be read ({ex.GetType().Name}).", ex);
                return;
            }

            // Migrate a legacy single-policy file (no presets) into a "Standard" preset.
            if (loaded.Presets is null || loaded.Presets.Count == 0)
            {
                var legacy = await TryLoadLegacyPolicyAsync();
                loaded = new GuardrailConfig
                {
                    ActivePreset = "Standard",
                    Presets = new() { new GuardrailPreset { Name = "Standard", Policy = legacy ?? GuardrailPolicy.SafeDefault() } },
                };
                await _store.SaveAsync(loaded);
                _logger?.LogInformation("Guardrails: legacy policy migrated into preset 'Standard'.");
            }

            // An active preset that does not exist (or any other invalid content) is not guessed at: picking another
            // preset could enforce a weaker policy than the one an admin chose.
            if (loaded.Problem() is { } problem)
            {
                FailClosed($"guardrails.json is invalid: {problem}", null);
                return;
            }

            _config = loaded;
            _loadError = null;
            _logger?.LogInformation("Guardrails loaded ({Count} presets, active={Active}).",
                _config.Presets.Count, _config.ActivePreset);
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
        // The caller keeps its object; what is stored and enforced is a copy nobody else holds.
        var next = config.Clone();
        if (next.Problem() is { } problem)
            throw new InvalidOperationException(problem);

        await _lock.WaitAsync(ct);
        try
        {
            await _store.SaveAsync(next);   // throws → the previous policy stays live
            Volatile.Write(ref _config, next);
            _loadError = null;
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
            var next = _loadError is null ? _config.Clone() : new GuardrailConfig { ActivePreset = "Standard" };
            var active = next.Presets.FirstOrDefault(p => p.Name == next.ActivePreset);
            if (active is null)
            {
                active = new GuardrailPreset { Name = next.ActivePreset };
                next.Presets.Add(active);
            }
            active.Policy = policy.Clone();
            if (next.Problem() is { } problem)
                throw new InvalidOperationException(problem);
            await _store.SaveAsync(next);
            Volatile.Write(ref _config, next);
            _loadError = null;
        }
        finally
        {
            _lock.Release();
        }

        Changed?.Invoke();
    }

    /// <summary>Enforces <see cref="GuardrailPolicy.FailClosed"/> until an admin saves a valid configuration. The
    /// unreadable file is kept next to it; the first save replaces it.</summary>
    private void FailClosed(string reason, Exception? ex)
    {
        _loadError = reason;
        _logger?.LogError(ex, "Guardrails: {Reason} Every mutation is refused until an admin saves a valid configuration",
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

    private static void RequireAdmin(AgentPrincipal editor)
    {
        if (editor.PermissionLevel != McpPermissionLevels.Admin)
            throw new UnauthorizedAccessException(
                $"Guardrails may only be changed by admins ({editor.DisplayName} is '{editor.PermissionLevel}').");
    }

    private async Task<GuardrailPolicy?> TryLoadLegacyPolicyAsync()
    {
        try
        {
            var json = await File.ReadAllTextAsync(_filePath);
            // A blank/unrelated JSON deserializes into a DEFAULT GuardrailPolicy that silently drops
            // SafeDefault's protections (ProtectedResources / ForbiddenArgPatterns). Only trust the file
            // as a legacy policy if it actually carries at least one known policy property.
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "MaxAutonomousLevel", "ReadOnlyMode", "RequireConfirmationForWrites", "ToolDenyList",
                "ToolAllowList", "ToolMode", "ProtectedResources", "ForbiddenArgPatterns", "MaxActionsPerSession"
            };
            if (!doc.RootElement.EnumerateObject().Any(p => known.Contains(p.Name))) return null;

            return System.Text.Json.JsonSerializer.Deserialize<GuardrailPolicy>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }
}
