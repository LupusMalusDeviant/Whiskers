# Resources — UI localization (i18n)

The `.resx` string tables behind Whiskers' UI localization (missingFeatures **F2**). Wired up in
[`Program.cs`](../Program.cs) via `AddLocalization(o => o.ResourcesPath = "Resources")` +
`UseRequestLocalization`.

- **`en` is the default / fallback culture** (the neutral `.resx`, no culture suffix); **`de` is a full
  translation** (`*.de.resx`, compiled into the `de/Whiskers.resources.dll` satellite). This flips the
  app's original German-only strings toward English-first.
- Culture is chosen per request from the **user's cookie** (`.AspNetCore.Culture`), then the browser's
  **`Accept-Language`**, defaulting to `en`.

## Files

| File | Purpose |
|---|---|
| `SharedResource.resx` | Shared UI strings, **English** (neutral / fallback). |
| `SharedResource.de.resx` | Same keys, **German**. |
| [`../SharedResource.cs`](../SharedResource.cs) | Marker type; inject `IStringLocalizer<SharedResource>`. |

## Using it in a component

```razor
@inject IStringLocalizer<SharedResource> L
...
<MudText>@L["Login_Subtitle"]</MudText>          @* simple *@
<MudButton>@L["Login_Button_Oidc", providerName]</MudButton>   @* {0} = arg *@
```

- **Key convention:** `Page_Element_Purpose` (e.g. `Login_Button_Google`). Add the key to **both**
  `.resx` files (English in the neutral one, German in `.de.resx`). A missing key falls back to the key
  name, so English (neutral) is the safety net.
- The [`LanguageSwitcher`](../Components/Shared/LanguageSwitcher.razor) in the app bar posts to the
  anonymous `/set-culture` endpoint (sets the cookie, full reload → the Blazor circuit restarts in the
  new culture).

## Status — F2 is an ongoing sweep

Done so far: the localization **infrastructure**, the language switcher, and the **Login** page (pilot).
Still hard-coded German and to be migrated page-by-page (max ~3 pages per PR per the roadmap): the
remaining ~34 pages, the ~179 Snackbar messages, the notification/log-alert texts, and
`HelpContentService` (the in-app handbook → per-language Markdown). `NavMenu` is deliberately left for
SAP Phase 1, which switches it to the module registry (whose `NavItem.LocKey` is the i18n hook). Only
localize new work against these tables; do not re-hardcode strings.
