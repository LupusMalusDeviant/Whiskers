namespace Whiskers.Tests;

/// <summary>
/// Contract 0011, D7 (INV-56, the "UI hints"): <c>SharedResource.resx</c> (English) and <c>SharedResource.de.resx</c>
/// (German) hold the same keys, each with a non-empty value and the same placeholders, so no UI text exists in one
/// language only and no translation drops or invents an inserted value. What a hint means is not checked here.
/// </summary>
public class ResxParityTests
{
    [Fact]
    public void Both_resource_files_hold_the_same_keys_with_the_same_placeholders()
    {
        var en = ReadinessLocalizationTests.Resx("SharedResource.resx");
        var de = ReadinessLocalizationTests.Resx("SharedResource.de.resx");

        Assert.Empty(en.Keys.Except(de.Keys).Order(StringComparer.Ordinal));   // English only
        Assert.Empty(de.Keys.Except(en.Keys).Order(StringComparer.Ordinal));   // German only
        var problems = new List<string>();
        foreach (var (key, english) in en.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var german = de[key];
            if (string.IsNullOrWhiteSpace(english)) problems.Add($"{key}: empty in English");
            if (string.IsNullOrWhiteSpace(german)) problems.Add($"{key}: empty in German");
            var (pe, pd) = (ReadinessLocalizationTests.Placeholders(english), ReadinessLocalizationTests.Placeholders(german));
            if (!pe.SetEquals(pd)) problems.Add($"{key}: placeholders en {{{string.Join(",", pe)}}}, de {{{string.Join(",", pd)}}}");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Each_key_appears_once_per_file()
    {
        foreach (var file in new[] { "SharedResource.resx", "SharedResource.de.resx" })
        {
            var names = System.Xml.Linq.XDocument.Load(ReadinessLocalizationTests.ResourcePath(file)).Root!.Elements("data")
                .Select(d => (string)d.Attribute("name")!).ToList();
            Assert.Empty(names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"{file}: {g.Key}"));
        }
    }
}
