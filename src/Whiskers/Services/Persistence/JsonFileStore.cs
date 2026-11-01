using System.Collections.Concurrent;
using System.Text.Json;

namespace Whiskers.Services.Persistence;

/// <summary>
/// One JSON file holding one object (roles, whitelist, vault, servers, permissions, …). R1-35:
/// <list type="bullet">
/// <item>Saves are published by <see cref="DurableFile"/> (flushed temporary file, one rename), keeping the file that was
/// in place as <c>&lt;file&gt;.bak</c>; saves to one path are serialized across every instance of the process.</item>
/// <item>A file that cannot be parsed (torn by a power loss, edited by hand) is copied aside as
/// <c>&lt;file&gt;.corrupt-&lt;time&gt;</c> and the previous generation is used and written back — recorded in
/// <see cref="JsonStoreRecovery"/> and shown by the readiness check. Without a readable previous generation the load
/// throws <see cref="JsonStoreCorruptException"/>: never an empty, possibly more permissive, store.</item>
/// <item>A file that cannot be opened (permissions, a lock) throws as it is: it is not missing.</item>
/// <item>A store that may still hold a legacy plaintext secret (<c>holdsPlaintextSecret</c>) keeps no previous
/// generation of a file that holds one: a secret moved into the vault must not stay behind in the <c>.bak</c>.</item>
/// </list>
/// </summary>
public class JsonFileStore<T> where T : class, new()
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

    private readonly string _filePath;
    private readonly SemaphoreSlim _semaphore;
    private readonly Func<T, bool>? _holdsPlaintextSecret;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public JsonFileStore(string filePath, Func<T, bool>? holdsPlaintextSecret = null)
    {
        _filePath = filePath;
        _holdsPlaintextSecret = holdsPlaintextSecret;
        _semaphore = Locks.GetOrAdd(Path.GetFullPath(filePath), _ => new SemaphoreSlim(1, 1));
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public async Task<T> LoadAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            var backup = _filePath + DurableFile.BackupSuffix;
            ThrowIfNotAFile();
            if (!File.Exists(_filePath))
            {
                // Only a store never written has neither; a file removed by hand while its previous generation is still
                // there is recovered like an unreadable one.
                if (!File.Exists(backup)) return new T();
                return await RecoverAsync(backup, preservedAs: JsonStoreRecovery.Missing, new FileNotFoundException("The file is missing.", _filePath));
            }

            var json = await File.ReadAllTextAsync(_filePath);
            try { return Parse(json); }
            catch (JsonException ex)
            {
                var preserved = PreserveUnreadable();
                return await RecoverAsync(backup, preserved, ex);
            }
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
            var current = await CurrentHoldsPlaintextSecretAsync();
            if (current == true)
            {
                // First, so that no crash leaves an older generation with the secret behind once it moved.
                var previous = _filePath + DurableFile.BackupSuffix;
                if (File.Exists(previous)) File.Delete(previous);
            }
            // An unreadable current file is no previous generation; the one kept before stays (it was checked when kept).
            await DurableFile.WriteAllTextAsync(_filePath, json, keepPrevious: current == false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>Whether the store was ever written: its file, or its previous generation alone (which
    /// <see cref="LoadAsync"/> recovers). A store with only a <c>.bak</c> is not a first run to seed.</summary>
    /// A directory in the file's place (a bind mount of a missing host file) counts as existing, so that the load
    /// refuses it instead of the caller seeding a first run.
    public bool Exists() => File.Exists(_filePath) || File.Exists(_filePath + DurableFile.BackupSuffix)
                            || Directory.Exists(_filePath) || Directory.Exists(_filePath + DurableFile.BackupSuffix);

    /// <summary>Removes the store with its previous generation (a reset, not a damage: nothing comes back).</summary>
    public async Task DeleteAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            if (File.Exists(_filePath)) File.Delete(_filePath);
            var previous = _filePath + DurableFile.BackupSuffix;
            if (File.Exists(previous)) File.Delete(previous);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // false: the current file may be kept as the previous generation; true: it holds a plaintext secret; null: it is
    // unreadable (never kept, being no readable generation and possibly holding a secret).
    private async Task<bool?> CurrentHoldsPlaintextSecretAsync()
    {
        if (!File.Exists(_filePath)) return false;
        try
        {
            var current = Parse(await File.ReadAllTextAsync(_filePath));
            return _holdsPlaintextSecret?.Invoke(current) ?? false;
        }
        catch (JsonException) { return null; }
    }

    // A directory is no missing file: File.Exists says false for it, and the store would read as never written.
    private void ThrowIfNotAFile()
    {
        foreach (var path in new[] { _filePath, _filePath + DurableFile.BackupSuffix })
            if (Directory.Exists(path))
                throw new IOException($"{path} is a directory, not a file; the store cannot be read.");
    }

    // "null" is no store: read as unreadable rather than as an empty one.
    private static T Parse(string json)
        => JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new JsonException("The file holds null.");

    private async Task<T> RecoverAsync(string backup, string preservedAs, Exception cause)
    {
        T recovered;
        try
        {
            if (!File.Exists(backup)) throw cause;
            recovered = Parse(await File.ReadAllTextAsync(backup));
        }
        catch (Exception ex) when (ex is JsonException or FileNotFoundException)
        {
            throw new JsonStoreCorruptException(_filePath, preservedAs, cause);
        }
        // Written back so the next save keeps a readable previous generation, not the unreadable file.
        await DurableFile.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(recovered, JsonOptions));
        JsonStoreRecovery.Record(new JsonStoreRecoveryEvent(_filePath, preservedAs, DateTime.UtcNow));
        return recovered;
    }

    // A copy under a name of its own, never overwriting an earlier one; the original stays where it is until a readable
    // generation replaces it. A start that stops on the same unreadable file again (a restart loop) adds no new copy.
    private string PreserveUnreadable()
    {
        var current = File.ReadAllBytes(_filePath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(_filePath))!;
        var same = Directory.EnumerateFiles(dir, Path.GetFileName(_filePath) + ".corrupt-*")
            .Order(StringComparer.Ordinal)
            .LastOrDefault(f => new FileInfo(f).Length == current.Length && File.ReadAllBytes(f).AsSpan().SequenceEqual(current));
        if (same is not null) return same;

        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        for (var n = 0; ; n++)
        {
            var target = n == 0 ? $"{_filePath}.corrupt-{stamp}" : $"{_filePath}.corrupt-{stamp}-{n}";
            try
            {
                File.Copy(_filePath, target, overwrite: false);
                return target;
            }
            catch (IOException) when (File.Exists(target) && n < 100) { }
        }
    }
}
