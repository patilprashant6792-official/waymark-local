// Small helpers the admin pages call through IJSRuntime. Nothing here talks to the network.
window.waymark = {
    download: function (fileName, text, contentType) {
        const blob = new Blob([text], { type: contentType || 'application/json' });
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url;
        link.download = fileName;
        document.body.appendChild(link);
        link.click();
        link.remove();
        URL.revokeObjectURL(url);
    },
    focusById: function (id) {
        const element = document.getElementById(id);
        if (element) {
            element.focus();
        }
    },
    copy: async function (text) {
        await navigator.clipboard.writeText(text);
    }
};

// The live console follows new output while the reader is at the bottom and stays put once they scroll up to read.
window.wmConsole = {
    states: new WeakMap(),
    state: function (element) {
        let state = this.states.get(element);
        if (!state) {
            state = { stuck: true, index: -1 };
            element.addEventListener('scroll', function () {
                state.stuck = element.scrollHeight - element.scrollTop - element.clientHeight < 40;
            }, { passive: true });
            this.states.set(element, state);
        }
        return state;
    },
    follow: function (id, force) {
        const element = document.getElementById(id);
        if (!element) {
            return;
        }
        const state = this.state(element);
        if (force || state.stuck) {
            element.scrollTop = element.scrollHeight;
            state.stuck = true;
        }
    },
    // Marks the lines of the output that match and moves to the next or previous one. mode is 'text' (the query) or 'error'
    // (a heuristic: error-like words or red text). step is 1, -1, or 0 to only refresh the marks. Returns how many lines matched.
    find: function (id, query, mode, step, reset) {
        const element = document.getElementById(id);
        if (!element) {
            return { count: 0, index: -1 };
        }
        const state = this.state(element);
        const needle = (query || '').toLowerCase();
        const errorWords = /\b(error|errors|failed|failure|exception|fatal)\b/i;
        const noErrors = /\b0 error/i;
        const hits = [];
        element.querySelectorAll('.wm-term-line').forEach(function (line) {
            line.classList.remove('hit', 'current');
            const text = line.textContent || '';
            const isHit = mode === 'error'
                ? (errorWords.test(text) && !noErrors.test(text)) || line.querySelector('.fg1, .fg9') !== null
                : needle.length > 0 && text.toLowerCase().includes(needle);
            if (isHit) {
                line.classList.add('hit');
                hits.push(line);
            }
        });
        if (reset) {
            state.index = -1;
        }
        if (hits.length === 0) {
            state.index = -1;
            return { count: 0, index: -1 };
        }
        if (step !== 0) {
            state.index = state.index < 0
                ? (step > 0 ? 0 : hits.length - 1)
                : (state.index + step + hits.length) % hits.length;
        } else if (state.index >= hits.length) {
            state.index = hits.length - 1;
        }
        if (state.index >= 0) {
            const current = hits[state.index];
            current.classList.add('current');
            if (step !== 0) {
                element.scrollTop = Math.max(0, current.offsetTop - element.clientHeight / 2);
                state.stuck = false;
            }
        }
        return { count: hits.length, index: state.index };
    },
    clearFind: function (id) {
        const element = document.getElementById(id);
        if (!element) {
            return;
        }
        element.querySelectorAll('.wm-term-line.hit, .wm-term-line.current').forEach(function (line) {
            line.classList.remove('hit', 'current');
        });
        this.state(element).index = -1;
    },
    // A remembered view preference. Returns the saved value; saves first when a value is given. Storage can be blocked, so it never throws.
    pref: function (key, value) {
        try {
            if (value !== null && value !== undefined) {
                window.localStorage.setItem('wm-pref-' + key, value);
            }
            return window.localStorage.getItem('wm-pref-' + key);
        } catch (error) {
            return null;
        }
    }
};

// The Ctrl+K palette: the component registers itself here, and the shortcut asks it to open or close. While it is open, Tab stays in its search box.
window.wmPalette = {
    handler: null,
    register: function (reference) {
        this.unregister();
        this.handler = function (event) {
            if ((event.ctrlKey || event.metaKey) && !event.altKey && (event.key || '').toLowerCase() === 'k') {
                event.preventDefault();
                reference.invokeMethodAsync('ToggleAsync');
            } else if (event.key === 'Tab') {
                const input = document.getElementById('wm-palette-input');
                if (input) {
                    event.preventDefault();
                    input.focus();
                }
            }
        };
        document.addEventListener('keydown', this.handler);
    },
    unregister: function () {
        if (this.handler) {
            document.removeEventListener('keydown', this.handler);
            this.handler = null;
        }
    }
};

(function () {
    const isTyping = function (target) {
        if (!target) {
            return false;
        }
        const tag = (target.tagName || '').toLowerCase();
        return tag === 'input' || tag === 'textarea' || tag === 'select' || target.isContentEditable;
    };

    document.addEventListener('keydown', function (event) {
        if (event.ctrlKey || event.metaKey || event.altKey || isTyping(event.target)) {
            return;
        }

        if (event.key === '/') {
            const search = ['audit-search', 'tools-search', 'wm-term-search', 'wm-live-search']
                .map(function (id) { return document.getElementById(id); })
                .find(function (element) { return element !== null; });
            if (search) {
                event.preventDefault();
                search.focus();
            }
        } else if (event.key === 'n' || event.key === 'N' || event.key === 'e' || event.key === 'w') {
            const id = event.key === 'n' ? 'wm-term-next' : event.key === 'N' ? 'wm-term-prev' : event.key === 'e' ? 'wm-term-error' : 'wm-term-wrap';
            const button = document.getElementById(id);
            if (button && !button.disabled) {
                event.preventDefault();
                button.click();
            }
        } else if (event.key === '[') {
            const toggle = document.getElementById('wm-nav-toggle');
            if (toggle && toggle.offsetParent !== null) {
                event.preventDefault();
                toggle.click();
            }
        } else if (event.key === '?') {
            const panel = document.getElementById('wm-shortcuts');
            if (panel) {
                event.preventDefault();
                panel.hidden = !panel.hidden;
                if (!panel.hidden) {
                    panel.focus();
                }
            }
        } else if (event.key === 'Escape') {
            const panel = document.getElementById('wm-shortcuts');
            if (panel && !panel.hidden) {
                panel.hidden = true;
            }
        }
    });
})();

// The dashboard's Rearrange mode. The page renders the cards and their sizes; while the owner rearranges, this code only overrides
// them with the --wm-o, --wm-w and --wm-h custom properties, so the attributes Blazor owns are never touched. "read" returns the
// result for the page to validate and save, and "end" drops the overrides again (which is also how Cancel works).
window.wmGrid = (function () {
    const COLUMNS = 12;
    const MAX_ROWS = 24;
    const sessions = new Map();

    const clamp = function (value, low, high) { return Math.max(low, Math.min(high, value)); };
    const int = function (value, fallback) { const parsed = parseInt(value, 10); return Number.isNaN(parsed) ? fallback : parsed; };
    const reduceMotion = function () { return !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches); };
    const contains = function (rect, x, y) { return x >= rect.left && x <= rect.right && y >= rect.top && y <= rect.bottom; };

    function scrollParent(element) {
        let node = element.parentElement;
        while (node && node !== document.body) {
            const overflow = getComputedStyle(node).overflowY;
            if ((overflow === 'auto' || overflow === 'scroll') && node.scrollHeight > node.clientHeight) {
                return node;
            }
            node = node.parentElement;
        }
        return document.scrollingElement || document.documentElement;
    }

    function snapshot(cells) {
        const rects = new Map();
        cells.forEach(function (cell) { rects.set(cell, cell.getBoundingClientRect()); });
        return rects;
    }

    // Slides every card from where it was to where it is now, so a reorder or a resize reads as movement and not as a jump.
    function play(before, except) {
        if (reduceMotion()) {
            return;
        }
        before.forEach(function (from, cell) {
            if (cell === except) {
                return;
            }
            const to = cell.getBoundingClientRect();
            const dx = from.left - to.left;
            const dy = from.top - to.top;
            if (dx !== 0 || dy !== 0) {
                cell.animate([{ transform: 'translate(' + dx + 'px,' + dy + 'px)' }, { transform: 'none' }], { duration: 160, easing: 'ease-out' });
            }
        });
    }

    function applyOrder(session) {
        session.order.forEach(function (cell, index) { cell.style.setProperty('--wm-o', String(index)); });
    }

    function applySize(session, cell) {
        const size = session.sizes.get(cell);
        cell.style.setProperty('--wm-w', String(size.w));
        cell.style.setProperty('--wm-h', String(size.h));
    }

    function announce(session, text) {
        if (session.status) {
            session.status.textContent = text;
        }
    }

    function titleOf(cell) { return cell.dataset.title || 'Card'; }

    function moveTo(session, cell, index) {
        const from = session.order.indexOf(cell);
        const target = clamp(index, 0, session.order.length - 1);
        if (from === target) {
            return false;
        }
        const before = snapshot(session.order);
        session.order.splice(from, 1);
        session.order.splice(target, 0, cell);
        applyOrder(session);
        play(before, cell);
        return true;
    }

    function setSize(session, cell, width, height) {
        const size = session.sizes.get(cell);
        const w = clamp(width, int(cell.dataset.minW, 1), COLUMNS);
        const h = clamp(height, int(cell.dataset.minH, 1), MAX_ROWS);
        if (size.w === w && size.h === h) {
            return false;
        }
        const before = snapshot(session.order);
        size.w = w;
        size.h = h;
        applySize(session, cell);
        play(before, cell);
        return true;
    }

    function schedule(session) {
        const drag = session.drag;
        if (drag && !drag.frame) {
            drag.frame = requestAnimationFrame(function () {
                drag.frame = 0;
                if (session.drag === drag) {
                    tick(session);
                }
            });
        }
    }

    function tick(session) {
        const drag = session.drag;
        if (drag.resizing) {
            resizeStep(session, drag);
        } else {
            moveStep(session, drag);
        }
        edgeScroll(session, drag);
    }

    // The dragged card follows the pointer; it takes the place of whichever other card the pointer is over.
    function moveStep(session, drag) {
        const cell = drag.cell;
        cell.style.transform = 'none';
        let slot = cell.getBoundingClientRect();
        const now = performance.now();
        const travelled = Math.hypot(drag.x - drag.swap.x, drag.y - drag.swap.y);
        if (now - drag.swap.time > 120 && travelled > 6) {
            const target = session.order.find(function (other) {
                return other !== cell && contains(other.getBoundingClientRect(), drag.x, drag.y);
            });
            if (target && moveTo(session, cell, session.order.indexOf(target))) {
                drag.swap = { time: now, x: drag.x, y: drag.y };
                cell.style.transform = 'none';
                slot = cell.getBoundingClientRect();
                announce(session, titleOf(cell) + ' is now number ' + (session.order.indexOf(cell) + 1) + ' of ' + session.order.length);
            }
        }
        cell.style.transform = 'translate(' + (drag.x - drag.grabX - slot.left) + 'px,' + (drag.y - drag.grabY - slot.top) + 'px)';
    }

    // The size follows the pointer, measured from where the card started, in whole columns and rows.
    function resizeStep(session, drag) {
        const style = getComputedStyle(session.grid);
        const gap = parseFloat(style.columnGap) || 0;
        const rowGap = parseFloat(style.rowGap) || gap;
        const rowHeight = parseFloat(style.gridAutoRows) || 44;
        const columnWidth = (session.grid.getBoundingClientRect().width - gap * (COLUMNS - 1)) / COLUMNS;
        const scrolled = drag.scroller.scrollTop - drag.startScroll;
        const width = Math.round((drag.x - drag.startLeft + gap) / (columnWidth + gap));
        const height = Math.round((drag.y + scrolled - drag.startTop + rowGap) / (rowHeight + rowGap));
        setSize(session, drag.cell, width, height);
        const size = session.sizes.get(drag.cell);
        drag.cell.dataset.size = size.w + ' \u00d7 ' + size.h;
    }

    // Near the top or bottom edge the page scrolls by itself, so a card can be dragged further than the screen shows.
    function edgeScroll(session, drag) {
        const root = drag.scroller === document.scrollingElement || drag.scroller === document.documentElement;
        const bounds = root ? { top: 0, bottom: window.innerHeight } : drag.scroller.getBoundingClientRect();
        const zone = 56;
        let step = 0;
        if (drag.y < bounds.top + zone) {
            step = -Math.ceil((bounds.top + zone - drag.y) / 4);
        } else if (drag.y > bounds.bottom - zone) {
            step = Math.ceil((drag.y - (bounds.bottom - zone)) / 4);
        }
        step = clamp(step, -24, 24);
        if (step !== 0) {
            const before = drag.scroller.scrollTop;
            drag.scroller.scrollTop += step;
            if (drag.scroller.scrollTop !== before) {
                schedule(session);
            }
        }
    }

    function pointerDown(session, event) {
        if (event.button !== 0 || session.drag) {
            return;
        }
        const cell = event.target.closest ? event.target.closest('.wm-cell') : null;
        if (!cell || cell.parentElement !== session.grid) {
            return;
        }
        event.preventDefault();
        cell.focus({ preventScroll: true });
        const rect = cell.getBoundingClientRect();
        const scroller = scrollParent(session.grid);
        const drag = {
            cell: cell,
            resizing: event.target.closest('.wm-resize') !== null,
            pointerId: event.pointerId,
            x: event.clientX,
            y: event.clientY,
            grabX: event.clientX - rect.left,
            grabY: event.clientY - rect.top,
            startLeft: rect.left,
            startTop: rect.top,
            startScroll: scroller.scrollTop,
            scroller: scroller,
            swap: { time: 0, x: event.clientX, y: event.clientY },
            frame: 0
        };
        drag.onMove = function (moved) { drag.x = moved.clientX; drag.y = moved.clientY; schedule(session); };
        drag.onEnd = function () { finish(session); };
        session.drag = drag;
        cell.setPointerCapture(event.pointerId);
        cell.classList.add(drag.resizing ? 'resizing' : 'dragging');
        cell.addEventListener('pointermove', drag.onMove);
        cell.addEventListener('pointerup', drag.onEnd);
        cell.addEventListener('pointercancel', drag.onEnd);
        cell.addEventListener('lostpointercapture', drag.onEnd);
        schedule(session);
    }

    function finish(session) {
        const drag = session.drag;
        if (!drag) {
            return;
        }
        session.drag = null;
        cancelAnimationFrame(drag.frame);
        const cell = drag.cell;
        cell.removeEventListener('pointermove', drag.onMove);
        cell.removeEventListener('pointerup', drag.onEnd);
        cell.removeEventListener('pointercancel', drag.onEnd);
        cell.removeEventListener('lostpointercapture', drag.onEnd);
        try {
            cell.releasePointerCapture(drag.pointerId);
        } catch (error) {
            // The pointer was already released.
        }
        cell.classList.remove('dragging', 'resizing');
        delete cell.dataset.size;
        if (!drag.resizing) {
            // The card settles into its slot from wherever it was dropped.
            const from = cell.getBoundingClientRect();
            cell.style.transform = '';
            const to = cell.getBoundingClientRect();
            if (!reduceMotion()) {
                cell.animate([{ transform: 'translate(' + (from.left - to.left) + 'px,' + (from.top - to.top) + 'px)' }, { transform: 'none' }], { duration: 160, easing: 'ease-out' });
            }
        }
    }

    // Keyboard: Alt+arrows move the focused card, Shift+arrows resize it (Left/Right the width, Up/Down the height).
    function keyDown(session, event) {
        const cell = event.target;
        if (session.drag || !cell.classList || !cell.classList.contains('wm-cell') || cell.parentElement !== session.grid) {
            return;
        }
        const direction = { ArrowLeft: -1, ArrowUp: -1, ArrowRight: 1, ArrowDown: 1 }[event.key];
        if (direction === undefined || event.ctrlKey || event.metaKey || event.altKey === event.shiftKey) {
            return;
        }
        event.preventDefault();
        if (event.altKey) {
            if (moveTo(session, cell, session.order.indexOf(cell) + direction)) {
                announce(session, titleOf(cell) + ' moved to number ' + (session.order.indexOf(cell) + 1) + ' of ' + session.order.length);
            }
            return;
        }
        const size = session.sizes.get(cell);
        const horizontal = event.key === 'ArrowLeft' || event.key === 'ArrowRight';
        if (setSize(session, cell, size.w + (horizontal ? direction : 0), size.h + (horizontal ? 0 : direction))) {
            announce(session, titleOf(cell) + ' is now ' + size.w + ' columns wide and ' + size.h + ' rows high');
        }
    }

    function end(id) {
        const session = sessions.get(id);
        if (!session) {
            return;
        }
        finish(session);
        session.grid.removeEventListener('pointerdown', session.onDown);
        session.grid.removeEventListener('keydown', session.onKey);
        session.order.forEach(function (cell) {
            cell.style.removeProperty('--wm-o');
            cell.style.removeProperty('--wm-w');
            cell.style.removeProperty('--wm-h');
            cell.style.transform = '';
        });
        sessions.delete(id);
    }

    function begin(id) {
        end(id);
        const grid = document.getElementById(id);
        if (!grid) {
            return false;
        }
        const session = {
            grid: grid,
            order: Array.from(grid.querySelectorAll(':scope > .wm-cell')),
            sizes: new Map(),
            drag: null,
            status: document.getElementById('wm-grid-status')
        };
        session.order.forEach(function (cell) {
            session.sizes.set(cell, { w: int(cell.dataset.w, 4), h: int(cell.dataset.h, 4) });
        });
        session.onDown = function (event) { pointerDown(session, event); };
        session.onKey = function (event) { keyDown(session, event); };
        grid.addEventListener('pointerdown', session.onDown);
        grid.addEventListener('keydown', session.onKey);
        sessions.set(id, session);
        return true;
    }

    // The current order and sizes, in the shape the page validates and saves.
    function read(id) {
        const session = sessions.get(id);
        if (!session) {
            return null;
        }
        return JSON.stringify({
            cards: session.order.map(function (cell) {
                const size = session.sizes.get(cell);
                return { id: cell.dataset.id, w: size.w, h: size.h };
            })
        });
    }

    // Shows another layout (the default, for "Reset") without saving it.
    function apply(id, json) {
        const session = sessions.get(id);
        if (!session) {
            return false;
        }
        let layout;
        try {
            layout = JSON.parse(json);
        } catch (error) {
            return false;
        }
        if (!layout || !Array.isArray(layout.cards)) {
            return false;
        }
        const before = snapshot(session.order);
        const byId = new Map(session.order.map(function (cell) { return [cell.dataset.id, cell]; }));
        const next = [];
        layout.cards.forEach(function (card) {
            const cell = byId.get(card.id);
            if (cell && !next.includes(cell)) {
                next.push(cell);
                const size = session.sizes.get(cell);
                size.w = clamp(int(card.w, size.w), int(cell.dataset.minW, 1), COLUMNS);
                size.h = clamp(int(card.h, size.h), int(cell.dataset.minH, 1), MAX_ROWS);
            }
        });
        session.order.forEach(function (cell) {
            if (!next.includes(cell)) {
                next.push(cell);
            }
        });
        session.order = next;
        applyOrder(session);
        session.order.forEach(function (cell) { applySize(session, cell); });
        play(before, null);
        announce(session, 'Layout reset to the default. Press Done to keep it.');
        return true;
    }

    return { begin: begin, end: end, read: read, apply: apply };
})();
