using MudBlazor;

namespace Whiskers.Components.Layout;

/// <summary>One selectable look: a primary/secondary accent, a second accent for gradients,
/// and the three glass background tones. The matching CSS variables live in app.css under
/// <c>html[data-theme="{Key}"]</c>; this record drives the MudBlazor palette so MudBlazor
/// components track the same colours.</summary>
public sealed record AppTheme(
    string Key,
    string Name,
    string Primary,
    string Secondary,
    string Accent2,
    string BgPrimary,
    string BgSurface,
    string BgElevated);

/// <summary>The built-in glassmorphism themes (all dark, distinguished by their gradient accent).
/// Order is the order shown in the picker; the first entry is the default.</summary>
public static class AppThemes
{
    public static readonly IReadOnlyList<AppTheme> All = new[]
    {
        new AppTheme("ember",  "Ember",  "#f97316", "#fb923c", "#fbbf24", "#09090b", "#111113", "#18181b"),
        new AppTheme("aurora", "Aurora", "#2dd4bf", "#34d399", "#22d3ee", "#0a0f0e", "#0d1714", "#11201c"),
        new AppTheme("nebula", "Nebula", "#a78bfa", "#c084fc", "#f472b6", "#0b0a12", "#12101d", "#181527"),
        new AppTheme("ocean",  "Ocean",  "#38bdf8", "#60a5fa", "#6366f1", "#080d14", "#0c1420", "#101b2b"),
        new AppTheme("rose",   "Rosé",   "#fb7185", "#f472b6", "#e879f9", "#120a0d", "#1a0f14", "#22141b"),
    };

    public const string DefaultKey = "ember";

    public static AppTheme Get(string? key) =>
        All.FirstOrDefault(t => t.Key == key) ?? All[0];

    /// <summary>Builds a MudBlazor theme (light + dark palette) from an <see cref="AppTheme"/>.
    /// Status colours (success/warning/error/info) stay constant across themes so health signals
    /// read the same. The light palette uses neutral zinc surfaces for every theme — only the
    /// accents differ — mirroring the <c>html[data-mode="light"]</c> CSS variable block (F12).</summary>
    public static MudTheme ToMudTheme(this AppTheme t) => new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = t.Primary,
            Secondary = t.Secondary,
            Tertiary = t.Accent2,
            Info = "#3b82f6",
            Success = "#22c55e",
            Warning = "#f59e0b",
            Error = "#ef4444",
            AppbarBackground = "#fafafa",
            Background = "#f4f4f5",
            Surface = "#ffffff",
            DrawerBackground = "#fafafa",
            TextPrimary = "#18181b",
            TextSecondary = "#52525b",
            ActionDefault = "#52525b",
            DrawerText = "#52525b",
            DrawerIcon = "#71717a",
            TableStriped = "#f4f4f5",
            Divider = "#e4e4e7",
            LinesDefault = "#e4e4e7",
        },
        PaletteDark = new PaletteDark
        {
            Primary = t.Primary,
            Secondary = t.Secondary,
            Tertiary = t.Accent2,
            Info = "#3b82f6",
            Success = "#22c55e",
            Warning = "#f59e0b",
            Error = "#ef4444",
            AppbarBackground = t.BgPrimary,
            Background = t.BgPrimary,
            Surface = t.BgSurface,
            DrawerBackground = t.BgPrimary,
            TextPrimary = "#e4e4e7",
            TextSecondary = "#a1a1aa",
            ActionDefault = "#a1a1aa",
            DrawerText = "#a1a1aa",
            DrawerIcon = "#71717a",
            TableStriped = t.BgElevated,
            Divider = "#27272a",
            LinesDefault = "#27272a",
        },
    };
}
