# Components

The Blazor Server UI. Whiskers renders interactively with [MudBlazor](https://mudblazor.com/); these components depend on the [`Services/`](../Services/) interfaces via DI and stream live updates over SignalR ([`../Hubs/`](../Hubs/)).

User-facing strings here are German (the product's UI language); in-code comments are English.

## Files

| File | Purpose |
|---|---|
| `App.razor` | Root HTML document / host page for the Blazor app. |
| `Routes.razor` | Router, maps URLs to page components and applies the layout. |
| `_Imports.razor` | Shared `@using` directives for all components. |

## Subfolders

| Folder | Contents |
|---|---|
| [`Layout/`](Layout/) | App shell, main layout, navigation menu, reconnect modal |
| [`Pages/`](Pages/) | Routable pages (dashboard, container detail, settings, deploy, ...) |
| [`Shared/`](Shared/) | Reusable widgets (gauges, badges, log viewer, role guard, advisor chat) |

## Related

- Business logic: [`../Services/`](../Services/)
- Real-time transport: [`../Hubs/`](../Hubs/)
- Component wiring (DI, auth, routing): [`../Program.cs`](../Program.cs)
