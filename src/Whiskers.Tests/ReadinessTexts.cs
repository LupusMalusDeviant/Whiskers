using System.Globalization;
using Microsoft.Extensions.Localization;
using Whiskers.Services.Setup;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0011 (INV-56): localizers for the readiness tests. <see cref="English"/> and <see cref="German"/> resolve the
/// real resources in a fixed UI culture, so a test's wording does not depend on the machine's language;
/// <see cref="KeyEcho"/> answers every key with the key itself and records it (D6).
/// </summary>
internal static class ReadinessTexts
{
    public static IStringLocalizer<SharedResource> English { get; } = In("en");
    public static IStringLocalizer<SharedResource> German { get; } = In("de");

    public static IStringLocalizer<SharedResource> In(string culture)
        => new CultureLocalizer(ProductionReadinessService.DefaultText, CultureInfo.GetCultureInfo(culture));

    /// <summary>The English value of a resource key (the neutral resx).</summary>
    public static string En(string key, params object[] args) => English[key, args].Value;

    private sealed class CultureLocalizer(IStringLocalizer inner, CultureInfo culture) : IStringLocalizer<SharedResource>
    {
        public LocalizedString this[string name] => In(() => inner[name]);
        public LocalizedString this[string name, params object[] arguments] => In(() => inner[name, arguments]);
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => In(() => inner.GetAllStrings(includeParentCultures).ToList());

        private T In<T>(Func<T> read)
        {
            var (ui, current) = (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture);
            try
            {
                CultureInfo.CurrentUICulture = culture;
                CultureInfo.CurrentCulture = culture;
                return read();
            }
            finally
            {
                CultureInfo.CurrentUICulture = ui;
                CultureInfo.CurrentCulture = current;
            }
        }
    }

    /// <summary>Echoes every key as <c>⟦key⟧</c> (arguments are dropped) and records each key asked for.</summary>
    public sealed class KeyEcho : IStringLocalizer<SharedResource>
    {
        public HashSet<string> Asked { get; } = new(StringComparer.Ordinal);
        public LocalizedString this[string name] { get { lock (Asked) Asked.Add(name); return new(name, $"⟦{name}⟧"); } }
        public LocalizedString this[string name, params object[] arguments] => this[name];
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
