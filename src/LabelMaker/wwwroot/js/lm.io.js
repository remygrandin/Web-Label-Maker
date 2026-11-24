// WebLabelMaker I/O: Google Fonts loading, IndexedDB library, file import/export.
(() => {
    const LM = window.LM;

    // ---- fonts -------------------------------------------------------------------
    const fonts = new Map(); // family -> Promise<boolean>

    const addSheet = (family, axis) => new Promise((resolve) => {
        const link = document.createElement('link');
        link.rel = 'stylesheet';
        link.href = `https://fonts.googleapis.com/css2?family=${encodeURIComponent(family).replace(/%20/g, '+')}${axis}&display=swap`;
        link.onload = () => resolve(true);
        link.onerror = () => { link.remove(); resolve(false); };
        document.head.appendChild(link);
    });

    // Any Google Fonts family by name. Tries the richest style set first, since
    // the CSS API rejects a request naming a style the family does not have.
    LM.loadFont = (family) => {
        family = String(family || '').trim();
        if (!family) return Promise.resolve(false);
        if (fonts.has(family)) return fonts.get(family);
        const p = (async () => {
            const ok = await addSheet(family, ':ital,wght@0,400;0,700;1,400;1,700')
                || await addSheet(family, ':wght@400;700')
                || await addSheet(family, '');
            if (!ok) return false;
            try {
                const faces = await Promise.all(['400', '700', 'italic 400', 'italic 700']
                    .map(v => document.fonts.load(`${v} 32px "${family}"`).catch(() => [])));
                return faces[0].length > 0;
            } catch { return false; }
        })();
        fonts.set(family, p);
        p.then(ok => { if (!ok) fonts.delete(family); });
        return p;
    };

    // ---- IndexedDB -----------------------------------------------------------------
    // Stores: "docs" and "templates" hold { id, name, modified, w, h, thumb, json };
    // "meta" holds single values such as the autosaved working document.
    let dbp = null;
    const db = () => dbp ??= new Promise((resolve, reject) => {
        // "images" (added in version 2) holds the user's image library: { id, name, modified, w, h, dataUrl }
        const req = indexedDB.open('weblabelmaker', 2);
        req.onupgradeneeded = () => {
            for (const s of ['docs', 'templates', 'meta', 'images']) if (!req.result.objectStoreNames.contains(s)) req.result.createObjectStore(s, { keyPath: 'id' });
        };
        req.onsuccess = () => resolve(req.result);
        req.onerror = () => reject(req.error);
    });
    const tx = async (store, mode, fn) => {
        const d = await db();
        return new Promise((resolve, reject) => {
            const t = d.transaction(store, mode);
            const req = fn(t.objectStore(store));
            t.oncomplete = () => resolve(req?.result);
            t.onerror = () => reject(t.error);
            t.onabort = () => reject(t.error);
        });
    };

    LM.db = {
        put: (store, rec) => tx(store, 'readwrite', s => s.put(rec)).then(() => true),
        del: (store, id) => tx(store, 'readwrite', s => s.delete(id)).then(() => true),
        get: async (store, id) => (await tx(store, 'readonly', s => s.get(id)))?.json ?? null,
        // metadata only, newest first
        list: async (store) => ((await tx(store, 'readonly', s => s.getAll())) || [])
            .map(({ json, ...meta }) => meta)
            .sort((a, b) => String(b.modified).localeCompare(String(a.modified))),
        all: async (store) => ((await tx(store, 'readonly', s => s.getAll())) || []).map(r => r.json),
    };

    // ---- files -----------------------------------------------------------------------
    LM.download = (name, text, mime) => {
        const url = URL.createObjectURL(new Blob([text], { type: mime || 'application/json' }));
        LM.downloadUrl(name, url);
        setTimeout(() => URL.revokeObjectURL(url), 4000);
    };
    LM.downloadUrl = (name, url) => {
        const a = document.createElement('a');
        a.href = url; a.download = name;
        document.body.appendChild(a); a.click(); a.remove();
    };

    const pickMany = (accept, multiple) => new Promise((resolve) => {
        const input = document.createElement('input');
        input.type = 'file'; input.accept = accept; input.multiple = !!multiple;
        input.onchange = () => resolve([...input.files]);
        input.oncancel = () => resolve([]);
        input.click();
    });
    const pick = async (accept) => (await pickMany(accept, false))[0] || null;

    const loadImage = (src) => new Promise((res, rej) => { const img = new Image(); img.onload = () => res(img); img.onerror = rej; img.src = src; });
    const svgDataUrl = (text) => 'data:image/svg+xml;base64,' + btoa(unescape(encodeURIComponent(text)));

    // An SVG stays a vector, so it is redrawn sharp at whatever size it is given on the label.
    async function readSvg(text, name) {
        const dataUrl = svgDataUrl(text);
        const img = await loadImage(dataUrl);
        return { dataUrl, w: img.naturalWidth || 300, h: img.naturalHeight || 300, name };
    }

    LM.pickText = async (accept) => {
        const file = await pick(accept);
        return file ? [file.name, await file.text()] : null;
    };

    // Read an image, cap its size, and return a compact data URL to embed in the document.
    LM.readImageFile = async (file) => {
        try {
            if (file.type === 'image/svg+xml' || /\.svg$/i.test(file.name)) return await readSvg(await file.text(), file.name);
            const url = URL.createObjectURL(file);
            const img = await loadImage(url);
            URL.revokeObjectURL(url);
            const max = 1600, k = Math.min(1, max / Math.max(img.naturalWidth, img.naturalHeight));
            const w = Math.max(1, Math.round(img.naturalWidth * k)), h = Math.max(1, Math.round(img.naturalHeight * k));
            const c = document.createElement('canvas');
            c.width = w; c.height = h;
            const ctx = c.getContext('2d');
            ctx.fillStyle = '#fff'; ctx.fillRect(0, 0, w, h);
            ctx.imageSmoothingQuality = 'high';
            ctx.drawImage(img, 0, 0, w, h);
            let dataUrl = c.toDataURL('image/png');
            if (dataUrl.length > 500000) dataUrl = c.toDataURL('image/jpeg', 0.9);
            return { dataUrl, w, h, name: file.name };
        } catch { return null; }
    };

    LM.pickImage = async () => {
        const file = await pick('image/*');
        return file ? LM.readImageFile(file) : null;
    };

    LM.pickImages = async () => {
        const out = [];
        for (const file of await pickMany('image/*', true)) { const img = await LM.readImageFile(file); if (img) out.push(img); }
        return out;
    };

    // ---- Iconify -----------------------------------------------------------------------
    // Open-source icon sets served by the public Iconify API. Only the search words and
    // the chosen icon's name leave the browser.
    const ICONIFY = 'https://api.iconify.design';

    // -> { icons: ["set:name", …], total } or { error }
    //
    // The service limits request rates, so a search costs one request for the names plus
    // one per icon set for the drawings (never one per icon), everything is cached, and
    // a search that has been overtaken by newer typing is dropped.
    const iconCache = new Map();    // "set:name" -> { id, dataUrl, w, h } | null
    const searchCache = new Map();  // query -> { ids, total }
    let searchSeq = 0;

    const rateLimited = (res) => res.status === 429
        ? 'The icon service is limiting requests. Wait a few seconds, then search again.'
        : `The icon service answered ${res.status}.`;

    // Build a standalone black SVG from Iconify icon data.
    function iconSvg(set, name) {
        let icon = set.icons?.[name], flipH = false, flipV = false, turns = 0;
        for (let alias = set.aliases?.[name], hops = 0; !icon && alias && hops < 5; hops++) {
            flipH ^= !!alias.hFlip; flipV ^= !!alias.vFlip; turns += alias.rotate || 0;
            icon = set.icons?.[alias.parent];
            alias = set.aliases?.[alias.parent];
        }
        if (!icon) return null;
        const left = icon.left ?? set.left ?? 0, top = icon.top ?? set.top ?? 0;
        const w = icon.width ?? set.width ?? 16, h = icon.height ?? set.height ?? 16;
        const ops = [];
        if (turns % 4) ops.push(`rotate(${(turns % 4) * 90} ${left + w / 2} ${top + h / 2})`);
        if (flipH) ops.push(`translate(${w + 2 * left} 0) scale(-1 1)`);
        if (flipV) ops.push(`translate(0 ${h + 2 * top}) scale(1 -1)`);
        const body = String(icon.body).replace(/currentColor/g, '#000');
        const k = 256 / h;
        const svg = `<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="${Math.round(w * k)}" height="256" viewBox="${left} ${top} ${w} ${h}">`
            + (ops.length ? `<g transform="${ops.join(' ')}">${body}</g>` : body) + '</svg>';
        return { dataUrl: svgDataUrl(svg), w: Math.round(w * k), h: 256 };
    }

    // Download the drawings for these ids: one request per icon set, a few at a time.
    async function loadIcons(ids, alive) {
        const bySet = new Map();
        for (const id of ids) {
            if (iconCache.has(id)) continue;
            const [set, name] = id.split(':');
            if (!bySet.has(set)) bySet.set(set, []);
            bySet.get(set).push(name);
        }
        const queue = [...bySet.entries()];
        let error = null;
        const worker = async () => {
            while (queue.length && !error && alive()) {
                const [set, names] = queue.shift();
                const res = await fetch(`${ICONIFY}/${set}.json?icons=${names.map(encodeURIComponent).join(',')}`);
                if (!res.ok) { error = rateLimited(res); return; }
                const data = await res.json();
                for (const name of names) {
                    const svg = iconSvg(data, name);
                    iconCache.set(`${set}:${name}`, svg ? { id: `${set}:${name}`, ...svg } : null);
                }
            }
        };
        await Promise.all([worker(), worker(), worker()]);
        return error;
    }

    // -> { icons: [{ id, dataUrl, w, h }], total, error, stale }
    LM.iconSearch = async (query, mono) => {
        const seq = ++searchSeq, alive = () => seq === searchSeq;
        const none = (error, stale) => ({ icons: [], total: 0, error: error || null, stale: !!stale });
        try {
            const q = String(query || '').trim().toLowerCase() + (mono ? ' palette=false' : '');
            let found = searchCache.get(q);
            if (!found) {
                const res = await fetch(`${ICONIFY}/search?query=${encodeURIComponent(q)}&limit=64`);
                if (!res.ok) return none(rateLimited(res));
                const data = await res.json();
                found = { ids: data.icons || [], total: data.total || 0 };
                searchCache.set(q, found);
            }
            if (!alive()) return none(null, true);
            const error = await loadIcons(found.ids, alive);
            if (!alive()) return none(null, true);
            const icons = found.ids.map(id => iconCache.get(id)).filter(Boolean);
            return { icons, total: found.total, error: icons.length ? null : error, stale: false };
        } catch {
            return alive() ? none('Could not reach the icon service. Check your connection.') : none(null, true);
        }
    };
})();
