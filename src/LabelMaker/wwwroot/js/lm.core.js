// WebLabelMaker core: shared state, preferences, theme, canvas input forwarding.
(() => {
    const LM = window.LM = {};
    const state = LM.state = { canvas: null, ref: null, frame: null, raf: 0, space: false };

    // ---- preferences / theme -------------------------------------------------
    LM.getPref = (key) => { try { return localStorage.getItem(key); } catch { return null; } };
    LM.setPref = (key, value) => { try { localStorage.setItem(key, value); } catch { } };
    LM.setTheme = (theme) => {
        if (theme === 'light' || theme === 'dark') document.documentElement.dataset.theme = theme;
        else delete document.documentElement.dataset.theme;
        LM.rerender && LM.rerender();
    };
    matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => LM.rerender && LM.rerender());

    // Tab and Enter move between table cells instead of leaving the field
    document.addEventListener('keydown', (e) => {
        if (e.target?.id === 'cell-text' && (e.key === 'Tab' || e.key === 'Enter')) e.preventDefault();
    }, true);

    LM.focus = (selector, select) => {
        const el = document.querySelector(selector);
        if (!el) return;
        el.focus();
        if (select && el.select) el.select();
    };
    LM.rectOf = (el) => { const r = el.getBoundingClientRect(); return [r.left, r.top, r.width, r.height]; };

    // ---- canvas input ----------------------------------------------------------
    const isEditable = (el) => el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.tagName === 'SELECT' || el.isContentEditable);
    const mods = (e) => (e.shiftKey ? 1 : 0) | ((e.ctrlKey || e.metaKey) ? 2 : 0) | (e.altKey ? 4 : 0) | (state.space ? 8 : 0);

    LM.initCanvas = (canvas, ref) => {
        state.canvas = canvas; state.ref = ref;
        const host = canvas.parentElement;
        const pos = (e) => { const r = canvas.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; };

        const resize = () => {
            const dpr = window.devicePixelRatio || 1;
            const w = host.clientWidth, h = host.clientHeight;
            canvas.width = Math.max(1, Math.round(w * dpr));
            canvas.height = Math.max(1, Math.round(h * dpr));
            ref.invokeMethod('OnCanvasResize', w, h);
            LM.rerender();
        };
        new ResizeObserver(resize).observe(host);
        resize();

        canvas.addEventListener('pointerdown', (e) => {
            if (isEditable(document.activeElement)) document.activeElement.blur();
            canvas.setPointerCapture(e.pointerId);
            const [x, y] = pos(e);
            ref.invokeMethod('OnPointerDown', x, y, e.button, mods(e));
            e.preventDefault();
        });
        let pending = null;
        canvas.addEventListener('pointermove', (e) => {
            const first = !pending;
            pending = e;
            if (!first) return;
            requestAnimationFrame(() => {
                const ev = pending; pending = null;
                const [x, y] = pos(ev);
                canvas.style.cursor = ref.invokeMethod('OnPointerMove', x, y, mods(ev)) || 'default';
            });
        });
        const up = (e) => {
            const [x, y] = pos(e);
            ref.invokeMethod('OnPointerUp', x, y, mods(e));
        };
        canvas.addEventListener('pointerup', up);
        canvas.addEventListener('pointercancel', up);
        canvas.addEventListener('dblclick', (e) => { const [x, y] = pos(e); ref.invokeMethod('OnDoubleClick', x, y); });
        canvas.addEventListener('contextmenu', (e) => e.preventDefault());
        canvas.addEventListener('wheel', (e) => {
            e.preventDefault();
            const [x, y] = pos(e);
            const k = e.deltaMode === 1 ? 32 : 1;
            ref.invokeMethod('OnWheel', x, y, e.deltaX * k, e.deltaY * k, mods(e));
        }, { passive: false });

        window.addEventListener('keydown', (e) => {
            if (e.code === 'Space' && !isEditable(e.target)) { state.space = true; e.preventDefault(); return; }
            const mod = e.ctrlKey || e.metaKey;
            if (isEditable(e.target)) {
                // Inside a field only the document-level shortcuts apply.
                if (!(mod && e.key.toLowerCase() === 's')) return;
            }
            if (mod && e.key.toLowerCase() === 'v') return; // handled by the paste event
            if (ref.invokeMethod('OnKey', e.key, mods(e))) e.preventDefault();
        });
        window.addEventListener('keyup', (e) => { if (e.code === 'Space') state.space = false; });
        window.addEventListener('blur', () => { state.space = false; });

        window.addEventListener('paste', async (e) => {
            if (isEditable(e.target)) return;
            const file = [...(e.clipboardData?.files || [])].find(f => f.type.startsWith('image/'));
            e.preventDefault();
            if (file) {
                const img = await LM.readImageFile(file);
                if (img) ref.invokeMethodAsync('OnImageDropped', img.dataUrl, img.w, img.h, -1, -1);
            } else {
                ref.invokeMethod('OnPaste');
            }
        });

        host.addEventListener('dragover', (e) => {
            if ([...(e.dataTransfer?.types || [])].includes('Files')) { e.preventDefault(); host.classList.add('dropping'); }
        });
        host.addEventListener('dragleave', () => host.classList.remove('dropping'));
        host.addEventListener('drop', async (e) => {
            host.classList.remove('dropping');
            const file = [...(e.dataTransfer?.files || [])].find(f => f.type.startsWith('image/'));
            if (!file) return;
            e.preventDefault();
            const [x, y] = pos(e);
            const img = await LM.readImageFile(file);
            if (img) ref.invokeMethodAsync('OnImageDropped', img.dataUrl, img.w, img.h, x, y);
        });

        document.fonts.addEventListener('loadingdone', () => ref.invokeMethodAsync('OnFontsLoaded'));
    };
})();
