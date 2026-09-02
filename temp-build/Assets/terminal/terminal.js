// Multi-session xterm host. The WPF shell controls lifecycle via postMessage.
// We keep one xterm.Terminal per session in memory; only the "active" one
// is mounted in the DOM and visible.
(function () {
    'use strict';

    const pane = document.getElementById('pane');
    const empty = document.getElementById('empty');
    const emptyCta = document.getElementById('empty-cta');
    const emptyError = document.getElementById('empty-error');
    const searchBar = document.getElementById('search');
    const searchInput = document.getElementById('search-input');
    const searchStatus = document.getElementById('search-status');
    const searchPrev = document.getElementById('search-prev');
    const searchNext = document.getElementById('search-next');
    const searchClose = document.getElementById('search-close');

    if (emptyCta) {
        // Same code path as the toolbar `+` and Ctrl+Shift+T — always go
        // through the host so session lifecycle stays in one place.
        emptyCta.addEventListener('click', () => post({ type: 'new-session' }));
    }

    /** @type {Map<string, {term: Terminal, fit: FitAddon, container: HTMLDivElement}>} */
    const sessions = new Map();
    let activeId = null;

    // Font size persists across sessions in memory. Apps can override this
    // via the `set-font-size` host message; users tune it with Ctrl+= / -.
    const DEFAULT_FONT_SIZE = 13;
    const MIN_FONT_SIZE = 8;
    const MAX_FONT_SIZE = 32;
    let fontSize = DEFAULT_FONT_SIZE;

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
            fontSize: fontSize,
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

        // Keyboard shortcuts. Returning false tells xterm "I handled this"
        // so the keystroke isn't also written as a literal control char to stdin.
        //   Ctrl+V / Ctrl+Shift+V → paste text
        //   Ctrl+Shift+C          → copy selection
        //   Ctrl+Shift+K          → clear active terminal viewport + scrollback
        //   Ctrl+Shift+T          → spawn a new session with the default shell
        //   Ctrl+F                → open the find-in-buffer overlay
        //   Ctrl+= / Ctrl+-       → font size up / down (active + new sessions)
        //   Ctrl+0                → reset font size
        //   Alt+V                 → paste clipboard image as a temp-file path
        //                           (agents in the pty can't read the host
        //                           clipboard themselves; falls back to text
        //                           paste if the clipboard isn't an image)
        term.attachCustomKeyEventHandler((e) => {
            if (e.type !== 'keydown') return true;
            const k = (e.key || '').toLowerCase();
            if (e.altKey && k === 'v') {
                requestImagePaste(id);
                e.preventDefault();
                return false;
            }
            const ctrl = e.ctrlKey || e.metaKey;
            if (!ctrl) return true;
            if (k === 'v') {
                requestPaste(id);
                e.preventDefault();
                return false;
            }
            if (e.shiftKey && k === 'c') {
                copySelection(term);
                e.preventDefault();
                return false;
            }
            if (e.shiftKey && k === 'k') {
                clearSession(id);
                e.preventDefault();
                return false;
            }
            if (e.shiftKey && k === 't') {
                // Host owns session lifecycle so the new tab spawns even if
                // the active terminal is mid-paste / mid-render. Bridge
                // posts back a `create` once the panel allocates a session.
                post({ type: 'new-session' });
                e.preventDefault();
                return false;
            }
            if (k === 'f') {
                openSearch();
                e.preventDefault();
                return false;
            }
            // Font size: '=' (with shift = '+') zooms in, '-' zooms out, '0'
            // resets. Use the physical key rather than e.key so layouts that
            // shift '=' to a dead-key still respond to Ctrl+=.
            if (k === '=' || e.key === '+') {
                setFontSize(fontSize + 1);
                e.preventDefault();
                return false;
            }
            if (k === '-') {
                setFontSize(fontSize - 1);
                e.preventDefault();
                return false;
            }
            if (k === '0') {
                setFontSize(DEFAULT_FONT_SIZE);
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
                requestPaste(id);
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
        searchLastQueryByTab.delete(id);
        if (searchTotalCache && searchTotalCache.sessionId === id) searchTotalCache = null;
        if (activeId === id) activeId = null;
        refreshEmptyState();
    }

    function activateSession(id) {
        // Switching sessions invalidates the search anchor — close the
        // overlay so the user explicitly re-opens it on the new session
        // (avoids stale match indicators referring to the prior buffer).
        if (id !== activeId && searchBar && !searchBar.classList.contains('hidden'))
            closeSearch();
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
        // Mark the match-count cache stale and let it expire after 250ms of
        // output silence — bursty writes (npm install, big diff scrolls)
        // would otherwise re-count on every step. The flag is enough; the
        // actual invalidation happens in `runSearch` once the timer fires.
        if (searchTotalCache && searchTotalCache.sessionId === id)
            scheduleSearchCacheInvalidate(id);
    }

    let searchCacheTimer = null;
    function scheduleSearchCacheInvalidate(id) {
        if (searchCacheTimer) clearTimeout(searchCacheTimer);
        searchCacheTimer = setTimeout(() => {
            searchCacheTimer = null;
            if (searchTotalCache && searchTotalCache.sessionId === id)
                searchTotalCache = null;
        }, 250);
    }

    function clearSession(id) {
        const s = sessions.get(id);
        if (!s) return;
        s.term.clear();
        // term.clear() only wipes the buffer; cursor stays where it is and
        // the shell still believes nothing changed. We don't round-trip a
        // host notification — the bridge stays minimal and the user can
        // hit Enter if they want a fresh prompt.
    }

    function setFontSize(next) {
        next = Math.max(MIN_FONT_SIZE, Math.min(MAX_FONT_SIZE, Math.round(next)));
        if (next === fontSize) return;
        fontSize = next;
        // Apply to every existing session so a multi-tab user sees one
        // consistent size; new sessions also pick up `fontSize` at create.
        for (const s of sessions.values()) {
            try {
                s.term.options.fontSize = fontSize;
                safeFit(s);
            } catch (_) { /* xterm may be mid-disposal */ }
        }
        // Persist on the host side so the choice survives a panel rebind.
        post({ type: 'font-size', value: fontSize });
    }

    // Visual feedback when paste/image-paste request returns nothing.
    // Bell + a one-line write that doesn't go through the shell — uses
    // xterm's local write so the line is added without disturbing stdin.
    function flashEmptyPaste(id, kind) {
        const s = sessions.get(id || activeId);
        if (!s) return;
        try {
            // Visual bell + grey hint in dim text. Keep it short so wrap doesn't
            // break the prompt the user is staring at.
            const label = kind === 'image' ? 'no image on clipboard' : 'clipboard empty';
            s.term.write('\x07\x1b[2m\x1b[90m  ' + label + '\x1b[0m\r\n');
        } catch (_) { }
    }

    // ---- find-in-buffer ---------------------------------------------------
    // No xterm-addon-search vendored; we implement a minimal substring search
    // against `term.buffer.active`. Case-insensitive, wrap-around, scrolls
    // the matched line into view and selects it. Live-updates as the user
    // types so it feels closer to a browser find than a modal dialog.

    let searchLastMatch = null; // { line, col, length, sessionId }
    // Per-session memory of the user's last query so re-opening Ctrl+F on the
    // same tab pre-fills the input — matches browser-find ergonomics.
    /** @type {Map<string, string>} */
    const searchLastQueryByTab = new Map();
    // Cache of total matches for the current (sessionId, query) so the status
    // shows `k of N` without recounting on every step. Invalidated when query
    // or session changes, or when fresh stdout arrives in the active session.
    let searchTotalCache = null; // { sessionId, query, total }

    let emptyErrorTimer = null;
    const EmptyErrorVisibleMs = 6000;
    function flashEmptyState(reason, kind) {
        // No active session to search — flash the empty-state div so the
        // user can see the keybinding fired but landed nowhere. When a
        // failure reason is provided (host AddSession threw), surface it
        // in a styled inline caption rather than the slow native tooltip.
        if (!empty) return;
        if (emptyError) {
            if (typeof reason === 'string' && reason.length > 0) {
                emptyError.textContent = reason;
                emptyError.hidden = false;
                // Each consecutive flash gets its own clean 6s window:
                // existing timer is cleared first, so back-to-back failures
                // never inherit a partial countdown from the prior one.
                if (emptyErrorTimer) { clearTimeout(emptyErrorTimer); emptyErrorTimer = null; }
                emptyError.classList.toggle('info', kind === 'info');
                // Force reflow so the .show transition runs from opacity 0
                // even when re-flashing on top of an already-visible banner.
                emptyError.classList.remove('show');
                void emptyError.offsetWidth;
                emptyError.classList.add('show');
                emptyErrorTimer = setTimeout(() => {
                    emptyErrorTimer = null;
                    try {
                        emptyError.classList.remove('show');
                        // Wait for the fade-out to complete before hiding.
                        setTimeout(() => { try { emptyError.hidden = true; } catch (_) { } }, 220);
                    } catch (_) { }
                }, EmptyErrorVisibleMs);
            } else {
                emptyError.classList.remove('show');
                emptyError.classList.remove('info');
                emptyError.hidden = true;
            }
        }
        empty.classList.remove('flash');
        // Force a reflow so re-adding the class restarts the animation
        // even when the user mashes Ctrl+F.
        void empty.offsetWidth;
        empty.classList.add('flash');
        setTimeout(() => { try { empty.classList.remove('flash'); } catch (_) { } }, 700);
    }

    function openSearch() {
        if (!searchBar) return;
        if (!activeId) { flashEmptyState(); return; }
        // Idempotent: a second Ctrl+F while the bar is open just refocuses
        // the input instead of clobbering the user's current query (which
        // happens when re-running through the open path).
        if (!searchBar.classList.contains('hidden')) {
            try {
                searchInput.focus();
                searchInput.select();
            } catch (_) { }
            return;
        }
        searchBar.classList.remove('hidden');
        const remembered = searchLastQueryByTab.get(activeId) || '';
        searchInput.value = remembered;
        searchStatus.textContent = '';
        searchTotalCache = null;
        // Buttons stay disabled until we confirm at least one match exists
        // for the (possibly remembered) query — runSearch flips them on.
        setSearchNavEnabled(false);
        // Defer focus so the overlay is realized before WebView2 hands it the
        // caret — prevents a frame where xterm grabs keyboard focus back.
        setTimeout(() => {
            try {
                searchInput.focus();
                searchInput.select();
            } catch (_) { }
            // If we pre-filled, jump straight to the first match.
            if (remembered) runSearch(+1);
        }, 0);
    }

    function closeSearch() {
        if (!searchBar) return;
        searchBar.classList.add('hidden');
        searchBar.classList.remove('idle');
        searchLastMatch = null;
        if (searchBlurTimer) { clearTimeout(searchBlurTimer); searchBlurTimer = null; }
        // Return focus to the active terminal so typing resumes immediately.
        const s = sessions.get(activeId);
        if (s) try { s.term.focus(); } catch (_) { }
    }

    function findInBuffer(term, query, startLine, startCol, dir) {
        // dir: +1 forward, -1 backward. Wrap-around is bounded by total lines
        // to avoid infinite loops on empty buffers.
        const buf = term.buffer.active;
        const total = buf.length;
        if (total === 0 || !query) return null;
        const q = query.toLowerCase();
        let line = startLine;
        let col = startCol;
        for (let i = 0; i <= total; i++) {
            const lineObj = buf.getLine(line);
            if (lineObj) {
                const text = lineObj.translateToString(true).toLowerCase();
                if (dir > 0) {
                    const idx = text.indexOf(q, col + 1);
                    if (idx >= 0) return { line, col: idx, length: query.length };
                } else {
                    const slice = col >= 0 ? text.substring(0, col) : text;
                    const idx = slice.lastIndexOf(q);
                    if (idx >= 0) return { line, col: idx, length: query.length };
                }
            }
            // Step to next line and reset the per-line column scan.
            line = (line + dir + total) % total;
            col = dir > 0 ? -1 : Number.MAX_SAFE_INTEGER;
        }
        return null;
    }

    function countMatches(term, query) {
        const buf = term.buffer.active;
        const total = buf.length;
        if (total === 0 || !query) return 0;
        const q = query.toLowerCase();
        let count = 0;
        // Bounded so a 50k-line buffer with a 1-char query doesn't tank the
        // UI thread. The cap is ~10× our scrollback (10000); if we hit it
        // the status simply shows `1 of 1000+` rather than freezing.
        const MaxLinesScanned = 100000;
        const limit = Math.min(total, MaxLinesScanned);
        for (let i = 0; i < limit; i++) {
            const lineObj = buf.getLine(i);
            if (!lineObj) continue;
            const text = lineObj.translateToString(true).toLowerCase();
            if (!text) continue;
            let from = 0;
            while (true) {
                const idx = text.indexOf(q, from);
                if (idx < 0) break;
                count++;
                from = idx + Math.max(1, q.length);
            }
        }
        return total > MaxLinesScanned ? -count : count;
    }

    function indexOfMatch(term, query, target) {
        // Counts matches strictly before `target` so the status can render
        // `k of N`. Costs another full pass on each step but only when a
        // match exists, and the total cap keeps it bounded.
        const buf = term.buffer.active;
        const total = buf.length;
        if (!query || !target) return -1;
        const q = query.toLowerCase();
        let n = 0;
        for (let line = 0; line < total; line++) {
            const lineObj = buf.getLine(line);
            if (!lineObj) continue;
            const text = lineObj.translateToString(true).toLowerCase();
            if (!text) continue;
            let from = 0;
            while (true) {
                const idx = text.indexOf(q, from);
                if (idx < 0) break;
                if (line === target.line && idx === target.col) return n;
                n++;
                from = idx + Math.max(1, q.length);
            }
        }
        return -1;
    }

    function setSearchNavEnabled(enabled) {
        if (searchPrev) searchPrev.disabled = !enabled;
        if (searchNext) searchNext.disabled = !enabled;
    }

    function runSearch(dir) {
        const s = sessions.get(activeId);
        if (!s) return;
        const query = searchInput.value;
        if (!query) {
            searchStatus.textContent = '';
            try { s.term.clearSelection(); } catch (_) { }
            searchLastMatch = null;
            searchTotalCache = null;
            setSearchNavEnabled(false);
            return;
        }
        searchLastQueryByTab.set(activeId, query);
        const buf = s.term.buffer.active;
        // Anchor: start from previous match if we have one for this session,
        // otherwise from the top of the viewport so the first hit is near
        // what the user is already looking at.
        let startLine, startCol;
        if (searchLastMatch && searchLastMatch.sessionId === activeId) {
            startLine = searchLastMatch.line;
            startCol = searchLastMatch.col;
        } else {
            startLine = buf.viewportY;
            startCol = -1;
        }
        const match = findInBuffer(s.term, query, startLine, startCol, dir);
        if (!match) {
            searchStatus.textContent = 'no match';
            try { s.term.clearSelection(); } catch (_) { }
            searchLastMatch = null;
            setSearchNavEnabled(false);
            return;
        }
        setSearchNavEnabled(true);
        // Carry forward the prior index when query+session match: stepping
        // is then O(1) instead of an O(N) `indexOfMatch` per step. Falls
        // back to a full lookup when query, session, or cache changed.
        const reusable =
            searchLastMatch &&
            searchLastMatch.sessionId === activeId &&
            searchLastMatch.query === query &&
            typeof searchLastMatch.index === 'number';

        // `k of N` status — total is cached per (session, query) so steps
        // through the same query don't recount the buffer. Negative total
        // (over-cap) is rendered as `N+`.
        const prevTotal = searchTotalCache && searchTotalCache.sessionId === activeId
            && searchTotalCache.query === query ? searchTotalCache.total : null;
        if (!searchTotalCache ||
            searchTotalCache.sessionId !== activeId ||
            searchTotalCache.query !== query) {
            searchTotalCache = {
                sessionId: activeId,
                query: query,
                total: countMatches(s.term, query),
            };
        }
        const total = searchTotalCache.total;
        const totalAbs = Math.abs(total);
        const totalChanged = prevTotal !== null && prevTotal !== total;

        let nextIndex;
        if (reusable && totalAbs > 0 && !totalChanged) {
            nextIndex = (searchLastMatch.index + (dir > 0 ? 1 : -1) + totalAbs) % totalAbs;
        } else {
            // Cache invalidated mid-step or query/session changed — fall back
            // to a full lookup so we don't carry a stale index forward into a
            // buffer whose match positions have shifted (new stdout, etc.).
            const located = indexOfMatch(s.term, query, match);
            nextIndex = located >= 0 ? located : 0;
        }

        searchLastMatch = { ...match, sessionId: activeId, query, index: nextIndex };
        // Scroll so the match is on-screen with a small top margin. xterm's
        // scrollLines is relative; absolute positioning is via viewport diff.
        try {
            const target = Math.max(0, match.line - 2);
            const diff = target - buf.viewportY;
            if (diff !== 0) s.term.scrollLines(diff);
        } catch (_) { }
        try { s.term.select(match.col, match.line, match.length); } catch (_) { }

        const k = nextIndex + 1;
        if (total === 0) {
            searchStatus.textContent = 'no match';
        } else if (total < 0) {
            searchStatus.textContent = `${k} of ${-total}+`;
        } else {
            searchStatus.textContent = `${k} of ${total}`;
        }
    }

    let searchBlurTimer = null;
    const SearchIdleMs = 4000;
    function scheduleSearchBlurClose() {
        // Only auto-collapse if the query is empty — an active query means
        // the user may still be navigating matches by clicking into xterm.
        // The bar then persists until they hit Esc or ✕.
        if (!searchBar || searchBar.classList.contains('hidden')) return;
        if ((searchInput.value || '').length > 0) {
            cancelSearchBlurClose();
            return;
        }
        if (searchBlurTimer) clearTimeout(searchBlurTimer);
        // Restart the hairline animation cleanly: remove the class, force a
        // reflow so the keyframe state resets, then re-add. Otherwise the
        // animation carries over partial progress from the previous schedule.
        searchBar.classList.remove('idle');
        void searchBar.offsetWidth;
        searchBar.classList.add('idle');
        searchBlurTimer = setTimeout(() => {
            searchBlurTimer = null;
            if (!searchBar || searchBar.classList.contains('hidden')) return;
            if (document.activeElement === searchInput) return;
            closeSearch();
        }, SearchIdleMs);
    }
    function cancelSearchBlurClose() {
        if (searchBlurTimer) { clearTimeout(searchBlurTimer); searchBlurTimer = null; }
        if (searchBar) searchBar.classList.remove('idle');
    }

    if (searchInput) {
        searchInput.addEventListener('input', () => {
            // Re-anchor at top of viewport for fresh queries — typing should
            // search from where the user is, not from the prior match.
            searchLastMatch = null;
            cancelSearchBlurClose();
            runSearch(+1);
        });
        searchInput.addEventListener('focus', cancelSearchBlurClose);
        searchInput.addEventListener('blur', scheduleSearchBlurClose);
        searchInput.addEventListener('keydown', (e) => {
            if (e.key === 'Escape') { closeSearch(); e.preventDefault(); return; }
            if (e.key === 'Enter') {
                runSearch(e.shiftKey ? -1 : +1);
                e.preventDefault();
                return;
            }
            if (e.key === 'F3') {
                runSearch(e.shiftKey ? -1 : +1);
                e.preventDefault();
                return;
            }
        });
    }
    if (searchPrev) searchPrev.addEventListener('click', () => runSearch(-1));
    if (searchNext) searchNext.addEventListener('click', () => runSearch(+1));
    if (searchClose) searchClose.addEventListener('click', () => closeSearch());

    // Global keybindings — capture phase so xterm's `attachCustomKeyEventHandler`
    // doesn't get to swallow them first.
    //   Esc: dismiss the find overlay (only when it's open)
    //   Ctrl+F / Cmd+F: toggle the find overlay regardless of focus, so it
    //                   works when search input has focus, when xterm doesn't,
    //                   or when the empty-state CTA has focus
    document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape') {
            if (!searchBar || searchBar.classList.contains('hidden')) return;
            closeSearch();
            e.preventDefault();
            e.stopPropagation();
            return;
        }
        if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey
            && (e.key === 'f' || e.key === 'F')) {
            if (!searchBar || !activeId) return;
            openSearch();
            e.preventDefault();
            e.stopPropagation();
        }
    }, true);

    // navigator.clipboard is unavailable here — http://terminal.local is not
    // a secure context. Bridge through the WPF host instead, which has
    // unrestricted access to System.Windows.Clipboard.
    function requestPaste(id) {
        post({ type: 'paste-request', id: id });
    }

    function requestImagePaste(id) {
        post({ type: 'image-paste-request', id: id });
    }

    function copySelection(term) {
        const sel = term.getSelection();
        if (sel) post({ type: 'copy', text: sel });
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
            case 'paste':
                {
                    const s = sessions.get(msg.id || activeId);
                    if (s && msg.text) {
                        // Multi-line pastes are surprising: most shells run
                        // each line as soon as a `\n` arrives, even with
                        // bracketed-paste in play if the program isn't using
                        // it. Show a one-line dim hint so the user notices
                        // before lines start executing. Hint goes through
                        // term.write so it doesn't get sent to stdin.
                        if (msg.warnMultiline && /\r\n|\n|\r/.test(msg.text)) {
                            const lines = msg.text.split(/\r\n|\n|\r/).length;
                            try {
                                s.term.write(
                                    '\x1b[2m\x1b[90m  pasting ' + lines +
                                    ' lines — shell may run them immediately\x1b[0m\r\n'
                                );
                            } catch (_) { }
                        }
                        s.term.paste(msg.text);
                    }
                }
                break;
            case 'paste-empty':
                flashEmptyPaste(msg.id, msg.kind);
                break;
            case 'set-font-size':
                if (typeof msg.value === 'number') setFontSize(msg.value);
                break;
            case 'flash-empty':
                flashEmptyState(
                    typeof msg.reason === 'string' ? msg.reason : null,
                    typeof msg.kind === 'string' ? msg.kind : 'error');
                break;
            case 'theme':
                // Host-driven CSS custom properties so the WPF Accent brush
                // is the single source of truth; CSS uses var(--accent, …)
                // with the existing hex as a fallback.
                if (typeof msg.accent === 'string')
                    document.documentElement.style.setProperty('--accent', msg.accent);
                break;
        }
    });

    refreshEmptyState();
    // Send the page's effective banner width so the host can trim error
    // text to fit. Update on resize too — `pane` width tracks the panel.
    function postBannerWidth() {
        try {
            const w = (pane && pane.clientWidth) || window.innerWidth || 480;
            // Trim further to leave 24px breathing room on each edge.
            post({ type: 'banner-width', width: Math.max(160, Math.floor(w - 48)) });
        } catch (_) { }
    }
    postBannerWidth();
    window.addEventListener('resize', postBannerWidth);
    post({ type: 'ready' });
})();
