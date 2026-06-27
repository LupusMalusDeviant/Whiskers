using System.Text.Json;
using System.Text.Json.Nodes;

namespace ServerWatch.Services.Persistence;

/// <summary>Writes UI-edited settings into /app/data/app-settings.json, the last configuration
/// layer (overrides env, reloadOnChange) — so changes apply live via IOptionsMonitor without a
/// restart. One section per settings class (e.g. "Mattermost", "MetricAlert").</summary>
public interface IAppSettingsStore
{
    Task SaveSectionAsync<T>(string section, T value);
}

public sealed class AppSettingsStore : IAppSettingsStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AppSettingsStore(string? path = null)
        => _path = path ?? "/app/data/app-settings.json";

    public async Task SaveSectionAsync<T>(string section, T value)
    {
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

            root[section] = JsonSerializer.SerializeToNode(value);

            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }
}
