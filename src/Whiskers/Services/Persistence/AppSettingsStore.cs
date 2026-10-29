using System.Text.Json;
using System.Text.Json.Nodes;
using Whiskers.Configuration;

namespace Whiskers.Services.Persistence;

/// <summary>Writes UI-edited settings into /app/data/app-settings.json, the last configuration
/// layer (overrides env, reloadOnChange) — so changes apply live via IOptionsMonitor without a
/// restart. One section per settings class (e.g. "Mattermost", "MetricAlert").</summary>
public interface IAppSettingsStore
{
    [Whiskers.Services.Mutations.MutatingOperation("config.save")]
    Task SaveSectionAsync<T>(string section, T value);

    /// <summary>Writes several sections in one file replacement (R1-11): either all of them take effect or, when
    /// the write fails, none.</summary>
    [Whiskers.Services.Mutations.MutatingOperation("config.save")]
    Task SaveSectionsAsync(IReadOnlyDictionary<string, object> sections);
}

public sealed class AppSettingsStore : IAppSettingsStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AppSettingsStore(string? path = null, DataPathOptions? dataPaths = null)
        => _path = path ?? (dataPaths ?? DataPathOptions.Default).AppSettingsJson;

    public Task SaveSectionAsync<T>(string section, T value) =>
        SaveSectionsAsync(new Dictionary<string, object> { [section] = JsonSerializer.SerializeToNode(value)! });

    public async Task SaveSectionsAsync(IReadOnlyDictionary<string, object> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        await _lock.WaitAsync();
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            JsonObject root;
            if (File.Exists(_path))
                root = JsonNode.Parse(await File.ReadAllTextAsync(_path)) as JsonObject ?? new JsonObject();
            else
                root = new JsonObject();

            var changes = new List<(string Section, JsonNode? Before, JsonNode? After)>();
            foreach (var (section, value) in sections)
            {
                var after = value as JsonNode ?? JsonSerializer.SerializeToNode(value, value.GetType());
                changes.Add((section, root[section]?.DeepClone(), after?.DeepClone()));
                root[section] = after;
            }

            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);

            // Written: what changed goes into the gateway's event, versioned and redacted (R1-22).
            foreach (var (section, before, after) in changes)
                Whiskers.Services.Mutations.ConfigChange.Record(section, ToElement(before), ToElement(after));
        }
        finally
        {
            _lock.Release();
        }
    }

    private static JsonElement? ToElement(JsonNode? node) =>
        node is null ? null : JsonSerializer.Deserialize<JsonElement>(node.ToJsonString());
}
