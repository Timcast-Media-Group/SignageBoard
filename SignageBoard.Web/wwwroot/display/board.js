(function () {
    "use strict";

    const STAGE_WIDTH = 1920;
    const STAGE_HEIGHT = 1080;
    const MIN_FONT_PX = 16;
    const DRAFT = 0;
    const PUBLISHED = 1;
    const IMPORTANT = 1;
    const URGENT = 2;

    const clockFormat = new Intl.DateTimeFormat("en-US", {
        timeZone: "America/New_York",
        weekday: "short",
        hour: "numeric",
        minute: "2-digit",
        second: "2-digit"
    });

    const sentFormat = new Intl.DateTimeFormat("en-US", {
        timeZone: "America/New_York",
        weekday: "short",
        month: "short",
        day: "numeric",
        hour: "numeric",
        minute: "2-digit"
    });

    const states = new WeakMap();

    function element(tag, className, text) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== undefined) node.textContent = text;
        return node;
    }

    function ensure(container) {
        let state = states.get(container);
        if (state) return state;

        container.classList.add("sb-stage");
        container.textContent = "";

        state = {
            clock: element("div", "sb-clock"),
            board: element("div", "sb-board"),
            empty: element("div", "sb-empty", "No notices"),
            snapshot: null,
            options: {},
            key: null,
            scale: 1,
            timer: null,
            onResize: null
        };

        container.appendChild(state.clock);
        container.appendChild(state.board);
        container.appendChild(state.empty);
        states.set(container, state);

        tick(state);
        state.timer = setInterval(() => tick(state), 1000);
        return state;
    }

    function utcTime(value) {
        if (!value) return null;
        const hasZone = /([zZ]|[+-]\d\d:?\d\d)$/.test(value);
        const time = Date.parse(hasZone ? value : value + "Z");
        return Number.isNaN(time) ? null : time;
    }

    function expiryTime(note) {
        return utcTime(note.expiresAt);
    }

    function byline(note) {
        const parts = [];
        if (note.createdByName) parts.push(note.createdByName);
        const sent = utcTime(note.createdAt);
        if (sent !== null) parts.push(sentFormat.format(new Date(sent)));
        return parts.join(" · ");
    }

    function visibleNotes(state, now) {
        const snapshot = state.snapshot;
        const notes = snapshot && Array.isArray(snapshot.notes) ? snapshot.notes : [];
        const live = notes.filter(note => {
            const expires = expiryTime(note);
            return expires === null || expires > now;
        });

        const published = live.filter(note => note.status === PUBLISHED);
        const shown = snapshot && snapshot.singleNoteMode ? published.slice(0, 1) : published;

        if (state.options.showDrafts !== true) return shown;
        return shown.concat(live.filter(note => note.status === DRAFT));
    }

    function buildNote(note, showDrafts) {
        const card = element("div", "sb-note");
        const tags = [];

        if (note.priority === URGENT) {
            card.classList.add("sb-urgent");
            tags.push(["URGENT", "sb-tag-urgent"]);
        } else if (note.priority === IMPORTANT) {
            card.classList.add("sb-important");
            tags.push(["IMPORTANT", "sb-tag-important"]);
        }

        if (showDrafts && note.status === DRAFT) {
            card.classList.add("sb-draft");
            tags.unshift(["DRAFT", "sb-tag-draft"]);
        }

        if (tags.length > 0) {
            const row = element("div", "sb-tags");
            tags.forEach(tag => row.appendChild(element("span", tag[1], tag[0])));
            card.appendChild(row);
        }

        card.appendChild(element("span", "sb-body", note.body || ""));

        const meta = byline(note);
        if (meta) card.appendChild(element("div", "sb-byline", meta));
        return card;
    }

    function draw(state, force) {
        if (!state.snapshot) return;

        const list = visibleNotes(state, Date.now());
        const showDrafts = state.options.showDrafts === true;
        const single = state.snapshot.singleNoteMode === true && list.length <= 1;
        const key = (single ? "s" : "a") + (showDrafts ? "d" : "") + ":" +
            list.map(note => note.id + "/" + note.status + "/" + note.priority).join("|");

        if (!force && key === state.key) return;
        state.key = key;

        const fragment = document.createDocumentFragment();
        list.forEach(note => fragment.appendChild(buildNote(note, showDrafts)));

        state.board.textContent = "";
        state.board.classList.toggle("sb-single", single);
        state.board.appendChild(fragment);
        state.empty.style.display = list.length > 0 ? "none" : "flex";
        fit(state);
    }

    function overflowing(board) {
        return board.scrollHeight > board.clientHeight + 1 || board.scrollWidth > board.clientWidth + 1;
    }

    function shrinkToFit(board, max, min) {
        board.style.fontSize = max + "px";
        if (!overflowing(board)) return max;

        let low = min;
        let high = max;
        for (let i = 0; i < 16; i++) {
            const mid = (low + high) / 2;
            board.style.fontSize = mid + "px";
            if (overflowing(board)) high = mid;
            else low = mid;
        }

        board.style.fontSize = low + "px";
        return low;
    }

    function largestFit(board, max) {
        const oneColumn = shrinkToFit(board, max, MIN_FONT_PX);
        if (oneColumn === max || board.classList.contains("sb-single") || board.childElementCount < 3) return oneColumn;

        board.classList.add("sb-cols");
        const twoColumns = shrinkToFit(board, max, MIN_FONT_PX);
        if (twoColumns >= oneColumn * 1.15) return twoColumns;

        board.classList.remove("sb-cols");
        board.style.fontSize = oneColumn + "px";
        return oneColumn;
    }

    function fit(state) {
        const board = state.board;
        board.style.fontSize = "";
        board.classList.remove("sb-cols");

        if (board.childElementCount === 0) return;

        const base = parseFloat(window.getComputedStyle(board).fontSize) / state.scale;
        const fitted = largestFit(board, base);
        const target = fitted * state.scale;

        if (state.scale <= 1) {
            board.style.fontSize = target + "px";
            return;
        }

        shrinkToFit(board, target, fitted);
    }

    function tick(state) {
        const text = clockFormat.format(new Date());
        if (state.clock.textContent !== text) state.clock.textContent = text;
        draw(state, false);
    }

    function applyFit(container) {
        const parent = container.parentElement;
        if (!parent) return;

        if (window.getComputedStyle(parent).position === "static") parent.style.position = "relative";
        parent.style.overflow = "hidden";

        const width = parent.clientWidth;
        const height = parent.clientHeight;
        if (!width || !height) return;

        const scale = Math.min(width / STAGE_WIDTH, height / STAGE_HEIGHT);
        const x = (width - STAGE_WIDTH * scale) / 2;
        const y = (height - STAGE_HEIGHT * scale) / 2;
        container.style.transform = `translate(${x}px, ${y}px) scale(${scale})`;
    }

    function render(container, snapshot, options) {
        const state = ensure(container);
        state.snapshot = snapshot || { notes: [] };
        state.options = options || {};
        draw(state, true);
    }

    function setScale(container, percent) {
        const state = ensure(container);
        const value = Number(percent);
        state.scale = (Number.isFinite(value) && value > 0 ? value : 100) / 100;
        container.style.setProperty("--scale", String(state.scale));
        fit(state);
    }

    function fitToParent(container) {
        const state = ensure(container);
        applyFit(container);

        if (!state.onResize) {
            state.onResize = () => applyFit(container);
            window.addEventListener("resize", state.onResize);
        }
    }

    function dispose(container) {
        const state = states.get(container);
        if (!state) return;

        clearInterval(state.timer);
        if (state.onResize) window.removeEventListener("resize", state.onResize);
        states.delete(container);
    }

    window.SignageBoard = { render, setScale, fitToParent, dispose };
})();
