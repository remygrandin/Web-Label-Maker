// Labelbench I/O: Google Fonts loading, IndexedDB library, file import/export.
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
        const req = indexedDB.open('labelbench', 1);
        req.onupgradeneeded = () => {
            for (const s of ['docs', 'templates', 'meta']) if (!req.result.objectStoreNames.contains(s)) req.result.createObjectStore(s, { keyPath: 'id' });
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

    const pick = (accept) => new Promise((resolve) => {
        const input = document.createElement('input');
        input.type = 'file'; input.accept = accept;
        input.onchange = () => resolve(input.files[0] || null);
        input.oncancel = () => resolve(null);
        input.click();
    });

    LM.pickText = async (accept) => {
        const file = await pick(accept);
        return file ? [file.name, await file.text()] : null;
    };

    // Read an image, cap its size, and return a compact data URL to embed in the document.
    LM.readImageFile = async (file) => {
        try {
            const url = URL.createObjectURL(file);
            const img = new Image();
            await new Promise((res, rej) => { img.onload = res; img.onerror = rej; img.src = url; });
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
})();
