// Applies the saved theme + dark/light mode before first paint (no flash of default colours).
// Loaded synchronously from <head> in App.razor; a file instead of an inline block, so the CSP can say script-src 'self'.
(function () {
    try { document.documentElement.setAttribute('data-theme', localStorage.getItem('sw-theme') || 'ember'); }
    catch (e) { document.documentElement.setAttribute('data-theme', 'ember'); }
    var dark = true;
    try {
        var mode = localStorage.getItem('sw-mode') || 'system';
        dark = mode === 'dark' || (mode === 'system' &&
            !(window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches));
    } catch (e) { }
    document.documentElement.setAttribute('data-mode', dark ? 'dark' : 'light');
})();
