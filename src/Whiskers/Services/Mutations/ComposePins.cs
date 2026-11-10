using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Whiskers.Services.Mutations;

/// <summary>The image one active service of a run starts, pinned (contract 0013, D10 as amended on 2026-11-10):
/// <paramref name="Reference"/> is the service's image as compose resolved it, <paramref name="Pin"/> the reference
/// <c>up</c> uses instead — the image id <c>sha256:…</c> — and <paramref name="ImageId"/> the id step 4 inspected, which
/// the pin must resolve to.</summary>
public sealed record ComposePin(string Service, string Reference, string Pin, string ImageId);

/// <summary>
/// Digest pinning of a compose run (contract 0013, D10–D13): after step 4 every image <c>up</c> starts is pinned to what
/// was inspected, verified on the host, and written to <c>&lt;project directory&gt;/.whiskers/pin.override.yml</c>, which
/// only <c>up</c> and the fallback <c>stop</c> use. A tag moved after the inspection changes nothing any more.
/// </summary>
public static partial class ComposePins
{
    /// <summary>Whiskers' own directory in a project directory.</summary>
    public const string DirectoryName = ".whiskers";

    /// <summary>The pin file's name in <see cref="DirectoryName"/>.</summary>
    public const string FileName = "pin.override.yml";

    /// <summary>The label every pinned container carries: the image reference the project names (D11, D14).</summary>
    public const string ReferenceLabel = "whiskers.image.ref";

    /// <summary>What <c>container.update</c> and AutoPatch answer for a pinned container (D14).</summary>
    public const string PinnedRefusal = "pinned by a Whiskers deployment — redeploy the project to update";

    /// <summary>The pin file of a project directory.</summary>
    public static string PathIn(string projectDirectory) =>
        ComposePaths.Join(projectDirectory, $"{DirectoryName}/{FileName}");

    /// <summary>Whether a compose file path is a pin file (D12): <c>…/.whiskers/pin.override.yml</c>.</summary>
    public static bool IsPinFile(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        const string suffix = DirectoryName + "/" + FileName;
        return normalized == suffix || normalized.EndsWith("/" + suffix, StringComparison.Ordinal);
    }

    /// <summary>Whether a container carries the pin label (D14).</summary>
    public static bool IsPinned(IEnumerable<KeyValuePair<string, string>>? labels) =>
        labels is not null && labels.Any(l => l.Key == ReferenceLabel && !string.IsNullOrWhiteSpace(l.Value));

    /// <summary>A digest as Docker writes one: <c>sha256:</c> and 64 lower-case hex digits.</summary>
    public static bool IsDigest(string? value) => value is not null && DigestPattern().IsMatch(value);

    [GeneratedRegex("^sha256:[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();

    /// <summary>
    /// D10 (amended 2026-11-10): the pin of every active service is the image id step 4 inspected — never a repo digest,
    /// which Docker 29's containerd store stops resolving once the tag is moved. Throws (the run is refused) for an id
    /// that is not <c>sha256:</c> + 64 hex digits, or a service whose image was not inspected.
    /// </summary>
    public static IReadOnlyList<ComposePin> Choose(IReadOnlyList<ComposeServicePlan> active,
        IReadOnlyDictionary<string, ComposeImage?> inspected)
    {
        var pins = new List<ComposePin>();
        foreach (var service in active)
        {
            if (service.Image is not { } reference || inspected.GetValueOrDefault(reference) is not { } image)
                throw new ComposePlanException($"The image of '{service.Name}' could not be pinned; nothing was started.");
            if (!IsDigest(image.Id))
                throw new ComposePlanException($"The image id of '{service.Name}' is not a sha256 digest; nothing was started.");
            pins.Add(new ComposePin(service.Name, reference, image.Id, image.Id));
        }
        return pins;
    }

    /// <summary>D11: the local inspection of every pin (<see cref="ComposeExposures.InspectImagesCommand"/>).</summary>
    public static string VerifyCommand(IReadOnlyList<ComposePin> pins) =>
        ComposeExposures.InspectImagesCommand(pins.Select(p => p.Pin).Distinct(StringComparer.Ordinal));

    /// <summary>D11: every pin must resolve on the host to the id step 4 inspected; anything else throws.</summary>
    public static void Verify(string output, IReadOnlyList<ComposePin> pins)
    {
        var resolved = ComposeExposures.ParseImages(output, pins.Select(p => p.Pin).Distinct(StringComparer.Ordinal).ToList());
        foreach (var pin in pins)
        {
            if (resolved.GetValueOrDefault(pin.Pin) is not { } image)
                throw new ComposePlanException($"The pinned image of '{pin.Service}' cannot be found on the server; nothing was started.");
            if (!string.Equals(image.Id, pin.ImageId, StringComparison.Ordinal))
                throw new ComposePlanException(
                    $"The pinned image of '{pin.Service}' is not the image that was inspected; nothing was started.");
        }
    }

    /// <summary>
    /// D11: the override file — each service's <c>image:</c> is its pin, and it carries <see cref="ReferenceLabel"/> with
    /// the reference the project names. Every string is a JSON (= YAML double-quoted) string with <c>$</c> written as
    /// <c>$$</c>, so compose interpolates nothing in it.
    /// </summary>
    public static string Yaml(IReadOnlyList<ComposePin> pins)
    {
        var sb = new StringBuilder()
            .Append("# Written by Whiskers on every deployment (contract 0013): the images it inspected, pinned.\n")
            .Append("# Do not edit; the next deployment rewrites it.\n")
            .Append("services:\n");
        foreach (var pin in pins)
            sb.Append("  ").Append(Quote(pin.Service)).Append(":\n")
              .Append("    image: ").Append(Quote(pin.Pin)).Append('\n')
              .Append("    labels:\n")
              .Append("      ").Append(ReferenceLabel).Append(": ").Append(Quote(pin.Reference)).Append('\n');
        return sb.ToString();
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value.Replace("$", "$$", StringComparison.Ordinal));

    /// <summary>What a run notes about its pins: the service and the first 12 digits of the image id — never the
    /// reference, which can come from an <c>.env</c> value.</summary>
    public static string Note(IReadOnlyList<ComposePin> pins) =>
        "pinned images: " + string.Join(", ", pins.Select(p =>
            $"{p.Service}={p.Pin[(p.Pin.IndexOf("sha256:", StringComparison.Ordinal) + 7)..][..12]}"));
}
