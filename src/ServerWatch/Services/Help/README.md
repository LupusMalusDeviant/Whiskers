# Services / Help

Supplies the content for the in-app **Hilfe** page (`/help`), a complete user handbook.

| File | Purpose |
|---|---|
| `IHelpContentService.cs` / `HelpContentService.cs` | Returns the ordered list of handbook **chapters**. Content is static: Markdown prose plus a few hand-drawn, theme-aware **SVG diagrams** and **screenshot placeholders** for UI-heavy chapters. No external state. |

The data model lives in [`../../Models/Help/HelpModels.cs`](../../Models/Help/HelpModels.cs)
(`HelpChapter` > `HelpSection` > optional `HelpFigure`). The page
[`../../Components/Pages/Help.razor`](../../Components/Pages/Help.razor) renders chapters with a
searchable table of contents, turns each section's Markdown into HTML via **Markdig**, and draws
SVG figures inline or shows a placeholder box where a real screenshot can be dropped in later.

**To extend the handbook:** add a `HelpChapter` to the list in `HelpContentService`. Use
`Shot("caption")` for a screenshot placeholder or `Diagram("caption", svg)` for an inline SVG.
SVG diagrams should use the theme CSS variables (`var(--sw-accent-primary)`, `var(--sw-text-primary)`
…) so they track the active theme. Registered interface-first in `Program.cs`.
