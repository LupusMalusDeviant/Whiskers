// Theme persistence + application. The chosen theme key is stored in localStorage and
// reflected as <html data-theme="..."> so the CSS variable overrides in app.css apply.
window.swTheme = {
    set: function (key) {
        try { localStorage.setItem('sw-theme', key); } catch (e) { }
        document.documentElement.setAttribute('data-theme', key);
    },
    get: function () {
        try { return localStorage.getItem('sw-theme'); } catch (e) { return null; }
    }
};

// Scrolls a container to its bottom (used to keep the agent chat pinned to the newest message).
window.swChat = {
    scrollToBottom: function (el) {
        if (el) { el.scrollTop = el.scrollHeight; }
    }
};
