# Components/Layout

The application shell, the chrome that wraps every page: the main layout, the navigation menu, and the connection-lost modal. Each component has a co-located `.razor.css` (scoped styles) and, where needed, a `.razor.js` (interop).

## Files

| File | Purpose |
|---|---|
| `MainLayout.razor` (+ `.css`) | The top-level layout: app bar, drawer, content area. |
| `NavMenu.razor` (+ `.css`) | The sidebar navigation, grouped into Overview / Deployment / Infrastructure / Automation. |
| `ReconnectModal.razor` (+ `.css`, `.js`) | Overlay shown when the Blazor Server SignalR circuit drops, with reconnect handling. |
| `AppThemes.cs` | The glassmorphism theme catalog (`AppTheme` record + `AppThemes` list) and the MudTheme builder (light + dark palette) behind the AppBar theme picker. Persisted per-browser via [`../../wwwroot/js/theme-interop.js`](../../wwwroot/js/theme-interop.js). |

`MainLayout` also owns the dark/light/system mode toggle (F12): the resolved mode is bound to
`MudThemeProvider.IsDarkMode` and mirrored as `<html data-mode="dark|light">` so the CSS variable
overrides in `wwwroot/app.css` switch in lockstep. "System" follows the OS preference live via
`MudThemeProvider.WatchSystemDarkModeAsync`; the preference is stored per-browser as `sw-mode`.

## Related

- Pages rendered inside this layout: [`../Pages/`](../Pages/)
- Routing: [`../Routes.razor`](../Routes.razor)
