window.Whiskers = window.Whiskers || {};
window.Whiskers.Terminal = {
    instances: {},

    create: function (elementId, dotNetRef) {
        if (typeof Terminal === 'undefined') {
            console.error('xterm.js not loaded');
            return;
        }

        const term = new Terminal({
            cursorBlink: true,
            fontSize: 14,
            fontFamily: "'Cascadia Code', 'Fira Code', 'Consolas', monospace",
            theme: {
                background: '#1e1e1e',
                foreground: '#d4d4d4',
                cursor: '#d4d4d4',
                selectionBackground: '#264f78'
            },
            scrollback: 5000
        });

        const fitAddon = new FitAddon.FitAddon();
        term.loadAddon(fitAddon);

        // Make URLs in terminal output clickable (e.g. the Tailscale SSH `check`-mode login link).
        // Opens in a new tab; degrades gracefully if the addon script didn't load.
        if (typeof WebLinksAddon !== 'undefined') {
            term.loadAddon(new WebLinksAddon.WebLinksAddon(function (event, uri) {
                window.open(uri, '_blank', 'noopener,noreferrer');
            }));
        }

        const el = document.getElementById(elementId);
        if (!el) {
            console.error('Terminal container element not found:', elementId);
            return;
        }

        term.open(el);
        fitAddon.fit();

        term.onData(function (data) {
            dotNetRef.invokeMethodAsync('OnTerminalInput', data);
        });

        const resizeObserver = new ResizeObserver(function () {
            fitAddon.fit();
        });
        resizeObserver.observe(el);

        this.instances[elementId] = { term: term, fitAddon: fitAddon, resizeObserver: resizeObserver };
    },

    write: function (elementId, data) {
        var instance = this.instances[elementId];
        if (instance) {
            // Normalize line endings: bare \n -> \r\n for xterm.js
            instance.term.write(data.replace(/\r?\n/g, '\r\n'));
        }
    },

    dispose: function (elementId) {
        var instance = this.instances[elementId];
        if (instance) {
            instance.resizeObserver.disconnect();
            instance.term.dispose();
            delete this.instances[elementId];
        }
    }
};

// CSV file download helper
window.Whiskers.downloadCsv = function (fileName, csvContent) {
    var blob = new Blob(["\uFEFF" + csvContent], { type: "text/csv;charset=utf-8;" });
    var url = URL.createObjectURL(blob);
    var link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    URL.revokeObjectURL(url);
};
