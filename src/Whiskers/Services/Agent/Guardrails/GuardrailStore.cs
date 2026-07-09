using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Agent.Guardrails;

/// <summary>Persists the guardrail presets in their OWN file (guardrails.json), separate from the
/// agent settings path. Writing is admin-only — a compromised settings editor cannot relax the
/// guardrails. The engine reads only the active preset's policy via <see cref="Current"/>.</summary>
public sealed class GuardrailStore : IGuardrailStore, IInitializable
{
    private readonly JsonFileStore<GuardrailConfig> _store;
    private readonly string _filePath;
    private readonly ILogger<GuardrailStore>? _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private GuardrailConfig _config = GuardrailConfig.SafeDefault();

    public event Action? Changed;

    public GuardrailStore(ILogger<GuardrailStore>? logger = null, string? filePath = null, DataPathOptions? dataPaths = null)
    {
        _logger = logger;
        _filePath = filePath ?? (dataPaths ?? DataPathOptions.Default).GuardrailsJson;
        _store = new JsonFileStore<GuardrailConfig>(_filePath);
    }

    public GuardrailPolicy Current => _config.ActivePolicy();
    public GuardrailConfig Config => _config;

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
                _config = GuardrailConfig.SafeDefault();
                await _store.SaveAsync(_config);
                _logger?.LogInformation("Guardrails: SafeDefault angelegt.");
                return;
            }

            _config = await _store.LoadAsync();

            // Migrate a legacy single-policy file (no presets) into a "Standard" preset.
            if (_config.Presets.Count == 0)
            {
                var legacy = await TryLoadLegacyPolicyAsync();
                _config = new GuardrailConfig
                {
                    ActivePreset = "Standard",
                    Presets = new() { new GuardrailPreset { Name = "Standard", Policy = legacy ?? GuardrailPolicy.SafeDefault() } },
                };
                await _store.SaveAsync(_config);
                _logger?.LogInformation("Guardrails: Legacy-Policy in Preset 'Standard' migriert.");
            }

            NormalizeActive();
            _logger?.LogInformation("Guardrails geladen ({Count} Presets, aktiv={Active}).",
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
        if (config.Presets.Count == 0)
            throw new InvalidOperationException("Mindestens ein Preset ist erforderlich.");

        await _lock.WaitAsync(ct);
        try
        {
            _config = config;
            NormalizeActive();
            await _store.SaveAsync(_config);
            _logger?.LogWarning("Guardrails geändert von {Editor} ({Count} Presets, aktiv={Active}).",
                editor.DisplayName, _config.Presets.Count, _config.ActivePreset);
        }
        finally
        {
            _lock.Release();
        }

        Changed?.Invoke();
    }

    public async Task SaveAsync(GuardrailPolicy policy, AgentPrincipal editor, CancellationToken ct = default)
    {
        // Back-compat: replace the active preset's policy.
        RequireAdmin(editor);
        await _lock.WaitAsync(ct);
        try
        {
            var active = _config.Presets.FirstOrDefault(p => p.Name == _config.ActivePreset)
                         ?? _config.Presets.FirstOrDefault();
            if (active is null)
            {
                active = new GuardrailPreset { Name = "Standard", Policy = policy };
                _config.Presets.Add(active);
                _config.ActivePreset = active.Name;
            }
            else
            {
                active.Policy = policy;
            }
            await _store.SaveAsync(_config);
        }
        finally
        {
            _lock.Release();
        }

        Changed?.Invoke();
    }

    private void NormalizeActive()
    {
        if (_config.Presets.Count == 0)
            _config.Presets.Add(new GuardrailPreset { Name = "Standard", Policy = GuardrailPolicy.SafeDefault() });
        if (!_config.Presets.Any(p => p.Name == _config.ActivePreset))
            _config.ActivePreset = _config.Presets[0].Name;
    }

    private static void RequireAdmin(AgentPrincipal editor)
    {
        if (editor.PermissionLevel != McpPermissionLevels.Admin)
            throw new UnauthorizedAccessException(
                $"Guardrails dürfen nur von Admins geändert werden ({editor.DisplayName} ist '{editor.PermissionLevel}').");
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
