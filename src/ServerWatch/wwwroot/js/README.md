# wwwroot/js

JavaScript interop modules invoked from Blazor components via `IJSRuntime`, for things that need direct DOM/browser APIs.

## Files

| File | Purpose |
|---|---|
| `graph-interop.js` | Renders/drives the container relationship graph ([`../../Components/Pages/ContainerGraph.razor`](../../Components/Pages/ContainerGraph.razor)). |
| `terminal-interop.js` | Drives the in-browser terminal (xterm-style) for the terminal pages, bridging to [`TerminalHub`](../../Hubs/TerminalHub.cs). |
