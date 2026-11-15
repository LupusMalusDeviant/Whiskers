// Registers the PWA service worker. Loaded at the end of <body> in App.razor; a file instead of an inline block, so
// the CSP can say script-src 'self'. The URL resolves against the document's <base href>, as it did inline.
if ('serviceWorker' in navigator) {
    navigator.serviceWorker.register('service-worker.js').catch(function (err) { console.log('SW:', err); });
}
