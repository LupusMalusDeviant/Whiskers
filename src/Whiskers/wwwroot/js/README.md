# wwwroot/js

JavaScript interop modules invoked from Blazor components via `IJSRuntime`, for things that need direct DOM/browser APIs.

## Files

| File | Purpose |
|---|---|
| `graph-interop.js` | Renders/drives the container relationship graph ([`../../Components/Pages/ContainerGraph.razor`](../../Components/Pages/ContainerGraph.razor)). |
| `terminal-interop.js` | Drives the in-browser terminal (xterm-style) for the terminal pages, driven by the terminal Blazor components via JS interop. |
| `theme-interop.js` | Persists the chosen UI theme (`sw-theme` → `<html data-theme="…">`) and the dark/light/system mode (`sw-mode` → resolved `<html data-mode="dark|light">`) in `localStorage` (see [`../../Components/Layout/AppThemes.cs`](../../Components/Layout/AppThemes.cs)). |
