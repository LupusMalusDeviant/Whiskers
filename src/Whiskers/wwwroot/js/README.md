# wwwroot/js

JavaScript interop modules invoked from Blazor components via `IJSRuntime`, for things that need direct DOM/browser APIs.

## Files

| File | Purpose |
|---|---|
| `graph-interop.js` | Renders/drives the container relationship graph ([`../../Components/Pages/ContainerGraph.razor`](../../Components/Pages/ContainerGraph.razor)). |
| `terminal-interop.js` | Drives the in-browser terminal (xterm-style) for the terminal pages, driven by the terminal Blazor components via JS interop. |
| `theme-init.js` | Applies the saved theme and dark/light mode to `<html>` before first paint. Loaded as a blocking script in `<head>` of [`../../Components/App.razor`](../../Components/App.razor) (no `async`/`defer`), so the page never flashes the default colours. |
| `theme-interop.js` | Persists the chosen UI theme (`sw-theme` → `<html data-theme="…">`) and the dark/light/system mode (`sw-mode` → resolved `<html data-mode="dark|light">`) in `localStorage` (see [`../../Components/Layout/AppThemes.cs`](../../Components/Layout/AppThemes.cs)). Also hosts `swClipboard.copy`, which returns whether the copy really happened: `navigator.clipboard` where the context is secure, otherwise a selection copy — callers show a manual-copy hint on `false` instead of claiming success; and `swChat.scrollToBottom` / `swChat.scrollToBottomById` for the agent page and the floating AI chat. |
| `sw-register.js` | Registers the PWA service worker (`service-worker.js`, resolved against `<base href>`). Loaded at the end of `<body>` in `App.razor`. |

## Content Security Policy

Every answer carries `Content-Security-Policy: frame-ancestors 'none'; script-src 'self'; object-src 'none'; base-uri 'self'`
(contract 0017 F2). Scripts therefore load only as files from the app's own origin: no inline `<script>` block, no
`on*="…"` attribute (use Blazor's `@onclick` or CSS `:hover`), no `eval` / `new Function` and no
`JS.InvokeVoidAsync("eval", …)` — put a named function in one of these files instead.
`ContentSecurityPolicySourceTests` fails on an inline handler or an `eval` interop call in a `.razor` file.
