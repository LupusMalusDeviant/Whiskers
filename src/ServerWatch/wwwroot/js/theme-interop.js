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

// Page-context helpers for the global agent widget: what page the user is on (route + visible text)
// and an optional screenshot of the current view (for vision-capable models).
window.swAgent = {
    getPageContext: function () {
        try {
            var main = document.querySelector('main') || document.body;
            var text = (main.innerText || '').replace(/ /g, ' ').replace(/[ \t]+\n/g, '\n').trim();
            if (text.length > 6000) text = text.slice(0, 6000) + ' …[gekürzt]';
            return { route: location.pathname + location.search, title: document.title, text: text };
        } catch (e) {
            return { route: location.pathname, title: document.title, text: '' };
        }
    },
    captureScreenshot: async function () {
        try {
            if (typeof html2canvas === 'undefined') return null;
            var canvas = await html2canvas(document.body, {
                scale: 0.6, logging: false, useCORS: true, backgroundColor: '#09090b',
                windowWidth: document.documentElement.clientWidth,
                windowHeight: document.documentElement.clientHeight
            });
            var dataUrl = canvas.toDataURL('image/jpeg', 0.7);
            return dataUrl.split(',')[1]; // base64 payload only
        } catch (e) {
            console.log('screenshot failed', e);
            return null;
        }
    }
};
