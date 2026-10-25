// Theme persistence + application. The chosen theme key is stored in localStorage and
// reflected as <html data-theme="..."> so the CSS variable overrides in app.css apply.
// The dark/light mode preference ("dark" | "light" | "system") is stored separately as
// 'sw-mode' and reflected as <html data-mode="dark|light"> (resolved, never "system").
window.swTheme = {
    set: function (key) {
        try { localStorage.setItem('sw-theme', key); } catch (e) { }
        document.documentElement.setAttribute('data-theme', key);
    },
    get: function () {
        try { return localStorage.getItem('sw-theme'); } catch (e) { return null; }
    },
    getMode: function () {
        try { return localStorage.getItem('sw-mode') || 'system'; } catch (e) { return 'system'; }
    },
    setMode: function (mode, isDark) {
        try { localStorage.setItem('sw-mode', mode); } catch (e) { }
        document.documentElement.setAttribute('data-mode', isDark ? 'dark' : 'light');
    },
    // Applies a resolved dark/light value without touching the stored preference
    // (used when the OS preference changes while mode = "system").
    applyDark: function (isDark) {
        document.documentElement.setAttribute('data-mode', isDark ? 'dark' : 'light');
    }
};

// Scrolls a container to its bottom (used to keep the agent chat pinned to the newest message).
window.swChat = {
    scrollToBottom: function (el) {
        if (el) { el.scrollTop = el.scrollHeight; }
    }
};

// Clipboard copy (MCP API keys, VAULT_KEY, webhook secrets) without echoing the value in a toast.
// Resolves to true only when the text really reached the clipboard. navigator.clipboard exists only in a
// secure context (HTTPS or localhost) — Whiskers reached over plain http on a tailnet address has none —
// and it rejects without a focused document. The old version swallowed both and the page reported
// "copied" while the clipboard held whatever was there before. The fallback copies through a hidden
// textarea, which works on plain http as long as the click is still the active user gesture.
window.swClipboard = {
    copy: async function (text) {
        if (typeof text !== 'string' || text.length === 0) return false;
        if (navigator.clipboard && window.isSecureContext) {
            try { await navigator.clipboard.writeText(text); return true; } catch (e) { /* fall back below */ }
        }
        var ta = document.createElement('textarea');
        try {
            ta.value = text;
            ta.setAttribute('readonly', '');
            ta.style.position = 'fixed';
            ta.style.top = '-1000px';
            ta.style.opacity = '0';
            // Insert next to the focused element rather than at <body>: a modal dialog's focus trap
            // (MudBlazor) would otherwise pull focus back out of the textarea and the copy fails.
            var active = document.activeElement;
            var host = (active && active !== document.body && active.parentNode) || document.body;
            host.appendChild(ta);
            ta.focus();
            ta.select();
            ta.setSelectionRange(0, text.length);
            return document.execCommand('copy') === true;
        } catch (e) {
            return false;
        } finally {
            if (ta.parentNode) ta.parentNode.removeChild(ta);
        }
    }
};

// Makes a fixed-position panel draggable by a handle element (used by the agent chat widget).
// The document-level mousemove/mouseup listeners are registered ONCE at module load; enable() only
// wires the per-handle mousedown (guarded, and GC'd with the handle element). Registering the document
// listeners inside enable() leaked a pair on every widget open/close.
window.swDrag = (function () {
    var active = null; // { panel, sx, sy, ox, oy } while dragging
    document.addEventListener('mousemove', function (e) {
        if (!active) return;
        var nx = Math.max(0, Math.min(window.innerWidth - 80, active.ox + e.clientX - active.sx));
        var ny = Math.max(0, Math.min(window.innerHeight - 40, active.oy + e.clientY - active.sy));
        active.panel.style.left = nx + 'px'; active.panel.style.top = ny + 'px';
    });
    document.addEventListener('mouseup', function () { active = null; });
    return {
        enable: function (panelId, handleId) {
            var panel = document.getElementById(panelId);
            var handle = document.getElementById(handleId);
            if (!panel || !handle || handle._swdrag) return;
            handle._swdrag = true;
            handle.style.cursor = 'move';
            handle.addEventListener('mousedown', function (e) {
                if (e.target.closest('button')) return; // header buttons must stay clickable
                var r = panel.getBoundingClientRect();
                active = { panel: panel, sx: e.clientX, sy: e.clientY, ox: r.left, oy: r.top };
                panel.style.right = 'auto'; panel.style.bottom = 'auto';
                panel.style.left = r.left + 'px'; panel.style.top = r.top + 'px';
                e.preventDefault();
            });
        }
    };
})();

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
