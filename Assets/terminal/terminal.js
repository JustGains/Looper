// Multi-session xterm host. The WPF shell controls lifecycle via postMessage.
// We keep one xterm.Terminal per session in memory; only the "active" one
// is mounted in the DOM and visible.
(function () {
    'use strict';

    const pane = document.getElementById('pane');
    const empty = document.getElementById('empty');

    /** @type {Map<string, {term: Terminal, fit: FitAddon, container: HTMLDivElement}>} */
    const sessions = new Map();
    let activeId = null;

    // Matches the app's dark palette. ANSI colors picked to be close to
    // Windows Terminal's Campbell profile but slightly warmer.
    const theme = {
        background: '#1a1c1e',
        foreground: '#d4d4d4',
        cursor: '#ffd200',
        cursorAccent: '#1a1c1e',
        selectionBackground: 'rgba(255, 210, 0, 0.25)',
        black: '#0c0c0c',
        red: '#c5524f',
        green: '#4e9a7a',
        yellow: '#c9a441',
        blue: '#5b8fb9',
        magenta: '#b088d0',
        cyan: '#5aa6a6',
        white: '#cccccc',
        brightBlack: '#5a5a60',
        brightRed: '#e06c6c',
        brightGreen: '#6fc89a',
        brightYellow: '#e6c06b',
        brightBlue: '#7fb0db',
        brightMagenta: '#c9a1e4',
        brightCyan: '#7fc7c7',
        brightWhite: '#ffffff',
    };

    function post(msg) {
        // chrome.webview is injected by WebView2; guard for dev preview in plain browser.
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage(msg);
        }
    }

    function refreshEmptyState() {
        if (sessions.size === 0) {
            empty.classList.remove('hidden');
        } else {
            empty.classList.add('hidden');
        }
    }

    function createSession(id) {
        if (sessions.has(id)) return sessions.get(id);

        const container = document.createElement('div');
        container.className = 'session';
        container.dataset.sessionId = id;
        pane.appendChild(container);

        const term = new Terminal({
            cursorBlink: true,
            cursorStyle: 'bar',
            fontFamily: "'Cascadia Code', 'Cascadia Mono', Consolas, monospace",
            fontSize: 13,
            lineHeight: 1.2,
            letterSpacing: 0,
            scrollback: 10000,
            allowProposedApi: true,
            allowTransparency: false,
            theme: theme,
        });
        const fit = new FitAddon.FitAddon();
        term.loadAddon(fit);
        term.open(container);

        // Clipboard shortcuts. Ctrl+V / Ctrl+Shift+V → paste, Ctrl+Shift+C → copy.
        // We return false to swallow the key so xterm doesn't also write a
        // literal ^V to stdin. Returning false tells xterm "I handled this".
        term.attachCustomKeyEventHandler((e) => {
            if (e.type !== 'keydown') return true;
            const ctrl = e.ctrlKey || e.metaKey;
            if (!ctrl) return true;
            const k = (e.key || '').toLowerCase();
            if (k === 'v') {
                pasteFromClipboard(term);
                e.preventDefault();
                return false;
            }
            if (e.shiftKey && k === 'c') {
                copySelection(term);
                e.preventDefault();
                return false;
            }
            return true;
        });

        // Right-click: copy if there's a selection, otherwise paste.
        // Default WebView2 context menus are disabled, so this is the only
        // way for users to access mouse-driven clipboard actions.
        container.addEventListener('contextmenu', (e) => {
            e.preventDefault();
            const sel = term.getSelection();
            if (sel && sel.length > 0) {
                copySelection(term);
            } else {
                pasteFromClipboard(term);
            }
        });

        // Pump user input to the pty.
        term.onData(data => post({ type: 'stdin', id: id, data: data }));
        term.onBinary(data => post({ type: 'stdin', id: id, data: data }));

        // When xterm itself decides to resize (e.g. after fit), notify pty.
        term.onResize(size => post({ type: 'resize', id: id, cols: size.cols, rows: size.rows }));

        const entry = { term, fit, container };
        sessions.set(id, entry);
        refreshEmptyState();
        return entry;
    }

    function destroySession(id) {
        const s = sessions.get(id);
        if (!s) return;
        try { s.term.dispose(); } catch (_) { }
        try { s.container.remove(); } catch (_) { }
        sessions.delete(id);
        if (activeId === id) activeId = null;
        refreshEmptyState();
    }

    function activateSession(id) {
        // Hide all, show target.
        for (const [sid, s] of sessions) {
            if (sid === id) {
                s.container.classList.add('active');
            } else {
                s.container.classList.remove('active');
            }
        }
        activeId = id || null;
        if (activeId) {
            const s = sessions.get(activeId);
            if (s) {
                // Fit on activation in case the pane was resized while we were hidden.
                safeFit(s);
                setTimeout(() => s.term.focus(), 0);
            }
        }
    }

    function writeToSession(id, b64) {
        const s = sessions.get(id);
        if (!s) return;
        const binary = atob(b64);
        const bytes = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
        s.term.write(bytes);
    }

    function clearSession(id) {
        const s = sessions.get(id);
        if (!s) return;
        s.term.clear();
    }

    function pasteFromClipboard(term) {
        if (!navigator.clipboard || !navigator.clipboard.readText) return;
        navigator.clipboard.readText().then(text => {
            if (text) term.paste(text);
        }).catch(() => { /* permission denied or empty clipboard */ });
    }

    function copySelection(term) {
        const sel = term.getSelection();
        if (!sel) return;
        if (navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(sel).catch(() => { });
        }
    }

    function safeFit(entry) {
        try {
            entry.fit.fit();
        } catch (_) { /* container may be zero-sized during tab switch */ }
    }

    // Refit on window resize / tab activation.
    const ro = new ResizeObserver(() => {
        if (!activeId) return;
        const s = sessions.get(activeId);
        if (s) safeFit(s);
    });
    ro.observe(pane);
    window.addEventListener('resize', () => {
        if (!activeId) return;
        const s = sessions.get(activeId);
        if (s) safeFit(s);
    });

    // Inbound messages from the WPF host.
    window.chrome.webview.addEventListener('message', (event) => {
        const msg = event.data;
        if (!msg || !msg.type) return;
        switch (msg.type) {
            case 'create':
                createSession(msg.id);
                break;
            case 'destroy':
                destroySession(msg.id);
                break;
            case 'activate':
                activateSession(msg.id);
                break;
            case 'stdout':
                writeToSession(msg.id, msg.b64);
                break;
            case 'clear':
                clearSession(msg.id);
                break;
            case 'focus':
                {
                    const s = sessions.get(msg.id || activeId);
                    if (s) s.term.focus();
                }
                break;
        }
    });

    refreshEmptyState();
    post({ type: 'ready' });
})();
