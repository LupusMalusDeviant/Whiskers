using System.Text.Json;

namespace ServerWatch.Services.Persistence;

public class JsonFileStore<T> where T : class, new()
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public JsonFileStore(string filePath)
    {
        _filePath = filePath;
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public async Task<T> LoadAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            if (!File.Exists(_filePath))
                return new T();

            var json = await File.ReadAllTextAsync(_filePath);
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? new T();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task SaveAsync(T data)
    {
        await _semaphore.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(data, JsonOptions);
            var tmpPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(tmpPath, json);
            File.Move(tmpPath, _filePath, overwrite: true);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public bool Exists() => File.Exists(_filePath);
}
