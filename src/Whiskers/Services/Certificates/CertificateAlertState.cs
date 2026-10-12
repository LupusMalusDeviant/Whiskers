using System.Text.Json;

namespace Whiskers.Services.Certificates;

/// <summary>Which stage was last announced for which certificate. Persisted because Whiskers restarts on every
/// deploy, and an in-memory state would repeat "expires in 25 days" each time.</summary>
public sealed class CertificateAlertState
{
    private readonly string _path;
    private Dictionary<string, int> _announced;

    public CertificateAlertState(string path)
    {
        _path = path;
        _announced = Load(path);
    }

    /// <summary>Returns the certificates that reached a new stage since the last call, records them, and forgets
    /// certificates that were not seen — a renewed certificate has a new fingerprint, and the old entry would
    /// otherwise linger forever.</summary>
    public IReadOnlyList<(ObservedCertificate Cert, int Stage)> Advance(
        IReadOnlyCollection<ObservedCertificate> observed, DateTime nowUtc)
    {
        var due = new List<(ObservedCertificate, int)>();
        var next = new Dictionary<string, int>();

        foreach (var cert in observed)
        {
            var stage = CertificateExpiry.StageFor(cert.NotAfterUtc, nowUtc);
            int? last = _announced.TryGetValue(cert.AlertKey, out var l) ? l : null;

            if (CertificateExpiry.ShouldAlert(stage, last))
            {
                due.Add((cert, stage!.Value));
                next[cert.AlertKey] = stage.Value;
            }
            else if (last is { } keep)
            {
                next[cert.AlertKey] = keep;
            }
        }

        _announced = next;
        Save();
        return due;
    }

    private static Dictionary<string, int> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt state file costs at most one repeated alert per certificate — not worth failing over.
        }
        return new();
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_announced));
        File.Move(tmp, _path, overwrite: true);
    }
}
