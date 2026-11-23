// WebLabelMaker renderer. One drawing path serves the editor canvas, thumbnails and
// the 1-bit print raster, so what is on screen is what reaches the print head.
(() => {
    const LM = window.LM;
    const state = LM.state;
    const assets = new Map();    // asset id -> { img, ready }
    const matrices = new Map();  // barcode key -> { w, h, canvas } | { error }
    const dithers = new Map();   // cache key -> canvas
    const MM_PER_PT = 25.4 / 72;
    const REF = 100;             // text is laid out at 100px and scaled, so wrapping is zoom-independent
    const RULER = 18;

    const meas = document.createElement('canvas').getContext('2d');

    // ---- assets ----------------------------------------------------------------
    LM.setAsset = (id, dataUrl) => {
        if (assets.has(id)) return;
        const a = { img: new Image(), ready: false, failed: false };
        assets.set(id, a);
        a.img.onload = () => { a.ready = true; LM.rerender(); };
        a.img.onerror = () => { a.failed = true; };
        a.img.src = dataUrl;
    };
    const assetsSettled = (frame) => Promise.all(frame.items.filter(n => n.type === 'image').map(n => {
        const a = assets.get(n.assetId);
        if (!a || a.ready || a.failed) return null;
        return new Promise(res => { a.img.addEventListener('load', res, { once: true }); a.img.addEventListener('error', res, { once: true }); });
    }));

    LM.setMatrix = (key, w, h, bits, error) => {
        if (error) { matrices.set(key, { error }); return; }
        const c = document.createElement('canvas');
        c.width = w; c.height = h;
        const cx = c.getContext('2d');
        const id = cx.createImageData(w, h);
        for (let i = 0; i < w * h; i++) {
            const v = bits[i] ? 0 : 255;
            id.data[i * 4] = id.data[i * 4 + 1] = id.data[i * 4 + 2] = v;
            id.data[i * 4 + 3] = 255;
        }
        cx.putImageData(id, 0, 0);
        matrices.set(key, { w, h, canvas: c });
    };
    const matrixKey = (n) => `${n.format}|${n.ecc}|${n.quietZone ? 1 : 0}|${n.value}`;
    const is2D = (f) => f === 'QrCode' || f === 'DataMatrix' || f === 'Aztec' || f === 'Pdf417';

    // ---- text ------------------------------------------------------------------
    const fontOf = (n, px) => `${n.italic ? 'italic ' : ''}${n.bold ? 700 : 400} ${px}px "${n.fontFamily}", "IBM Plex Sans", sans-serif`;

    function layoutText(n) {
        const fmm = Math.max(0.1, n.fontSize * MM_PER_PT);
        meas.font = fontOf(n, REF);
        meas.letterSpacing = ((n.tracking || 0) / 1000 * REF) + 'px';
        const m = meas.measureText('Hg');
        const asc = m.fontBoundingBoxAscent ?? REF * 0.8, desc = m.fontBoundingBoxDescent ?? REF * 0.2;
        const lh = (asc + desc) * (n.lineHeight || 1);
        const pad = n.padding || 0;
        const lines = [];
        const width = (s) => meas.measureText(s).width;
        const maxW = n.mode === 'Wrap' ? Math.max(1, (n.w - 2 * pad) / fmm * REF) : Infinity;
        for (const para of String(n.text ?? '').split('\n')) {
            if (maxW === Infinity || width(para) <= maxW) { lines.push({ t: para, w: width(para) }); continue; }
            let cur = '';
            for (const word of para.split(/(\s+)/)) {
                if (!word) continue;
                if (cur && width(cur + word) > maxW && word.trim()) {
                    lines.push({ t: cur.trimEnd(), w: width(cur.trimEnd()) });
                    cur = word;
                } else cur += word;
                while (width(cur) > maxW && cur.length > 1) { // a single word wider than the box
                    let k = cur.length - 1;
                    while (k > 1 && width(cur.slice(0, k)) > maxW) k--;
                    lines.push({ t: cur.slice(0, k), w: width(cur.slice(0, k)) });
                    cur = cur.slice(k);
                }
            }
            lines.push({ t: cur.trimEnd(), w: width(cur.trimEnd()) });
        }
        const w = Math.max(0, ...lines.map(l => l.w));
        return { fmm, asc, desc, lh, lines, w, h: lines.length * lh };
    }

    // Natural size of a text node in mm (used for auto-sized text boxes).
    LM.measureText = (n) => {
        const L = layoutText(n);
        const k = L.fmm / REF;
        return [L.w * k, L.h * k];
    };

    // What the inline editor needs to sit exactly on the drawn text, in mm:
    // [effective font size, line height, text block height].
    LM.textEditMetrics = (n) => {
        const L = layoutText(n);
        const pad = n.padding || 0;
        let k = L.fmm / REF;
        if (n.mode === 'Fit' && L.w && L.h) k = Math.min(Math.max(0.01, n.w - 2 * pad) / L.w, Math.max(0.01, n.h - 2 * pad) / L.h);
        return [k * REF, L.lh * k, L.h * k];
    };

    function drawText(ctx, n) {
        const w = n.w, h = n.h, pad = n.padding || 0;
        if (n.invert) { ctx.fillStyle = '#000'; ctx.fillRect(-w / 2, -h / 2, w, h); }
        const L = layoutText(n);
        if (!L.w || !L.h) return;
        const iw = Math.max(0.01, w - 2 * pad), ih = Math.max(0.01, h - 2 * pad);
        let k = L.fmm / REF;
        if (n.mode === 'Fit') k = Math.min(iw / L.w, ih / L.h);
        const bh = L.h * k;
        const va = n.mode === 'Auto' ? 'Top' : n.vAlign;
        const y0 = -ih / 2 + (va === 'Middle' ? (ih - bh) / 2 : va === 'Bottom' ? ih - bh : 0);
        ctx.save();
        ctx.scale(k, k);
        ctx.font = fontOf(n, REF);
        ctx.letterSpacing = ((n.tracking || 0) / 1000 * REF) + 'px';
        ctx.fillStyle = n.invert ? '#fff' : '#000';
        ctx.textBaseline = 'alphabetic';
        const base = (L.lh - (L.asc + L.desc)) / 2 + L.asc;
        L.lines.forEach((ln, i) => {
            const lw = ln.w * k;
            const x = n.align === 'Center' ? -lw / 2 : n.align === 'Right' ? iw / 2 - lw : -iw / 2;
            ctx.fillText(ln.t, x / k, y0 / k + i * L.lh + base);
        });
        ctx.restore();
    }

    // ---- shapes ----------------------------------------------------------------
    const ink = (v) => v === 'Black' ? '#000' : v === 'White' ? '#fff' : null;

    function polyPoints(count, inner) {
        const pts = [];
        const n = inner ? count * 2 : count;
        for (let i = 0; i < n; i++) {
            const a = -Math.PI / 2 + i * 2 * Math.PI / n;
            const r = inner && i % 2 ? inner : 1;
            pts.push([Math.cos(a) * r, Math.sin(a) * r]);
        }
        // normalise to the unit box so the shape fills its frame
        const xs = pts.map(p => p[0]), ys = pts.map(p => p[1]);
        const x0 = Math.min(...xs), x1 = Math.max(...xs), y0 = Math.min(...ys), y1 = Math.max(...ys);
        return pts.map(p => [(p[0] - x0) / (x1 - x0) - 0.5, (p[1] - y0) / (y1 - y0) - 0.5]);
    }

    function shapePath(ctx, n, w, h) {
        ctx.beginPath();
        const poly = (pts) => { pts.forEach((p, i) => i ? ctx.lineTo(p[0] * w, p[1] * h) : ctx.moveTo(p[0] * w, p[1] * h)); ctx.closePath(); };
        switch (n.kind) {
            case 'Ellipse': ctx.ellipse(0, 0, w / 2, h / 2, 0, 0, Math.PI * 2); break;
            case 'Triangle': poly([[0, -0.5], [0.5, 0.5], [-0.5, 0.5]]); break;
            case 'Diamond': poly([[0, -0.5], [0.5, 0], [0, 0.5], [-0.5, 0]]); break;
            case 'Polygon': poly(polyPoints(Math.max(3, n.sides || 5))); break;
            case 'Star': poly(polyPoints(Math.max(3, n.sides || 5), 0.45)); break;
            case 'Arrow': poly([[-0.5, -0.22], [0.1, -0.22], [0.1, -0.5], [0.5, 0], [0.1, 0.5], [0.1, 0.22], [-0.5, 0.22]]); break;
            case 'DoubleArrow': poly([[-0.5, 0], [-0.2, -0.5], [-0.2, -0.2], [0.2, -0.2], [0.2, -0.5], [0.5, 0], [0.2, 0.5], [0.2, 0.2], [-0.2, 0.2], [-0.2, 0.5]]); break;
            case 'Chevron': poly([[-0.5, -0.5], [0.15, -0.5], [0.5, 0], [0.15, 0.5], [-0.5, 0.5], [-0.15, 0]]); break;
            case 'RightTriangle': poly([[-0.5, -0.5], [0.5, 0.5], [-0.5, 0.5]]); break;
            case 'Parallelogram': poly([[-0.25, -0.5], [0.5, -0.5], [0.25, 0.5], [-0.5, 0.5]]); break;
            case 'Trapezoid': poly([[-0.25, -0.5], [0.25, -0.5], [0.5, 0.5], [-0.5, 0.5]]); break;
            case 'Cross': { const a = 1 / 6; poly([[-a, -0.5], [a, -0.5], [a, -a], [0.5, -a], [0.5, a], [a, a], [a, 0.5], [-a, 0.5], [-a, a], [-0.5, a], [-0.5, -a], [-a, -a]]); break; }
            case 'Check': poly([[-0.5, 0.05], [-0.32, -0.13], [-0.15, 0.1], [0.32, -0.5], [0.5, -0.35], [-0.15, 0.5]]); break;
            case 'Semicircle': ctx.moveTo(-w / 2, h / 2); ctx.ellipse(0, h / 2, w / 2, h, 0, Math.PI, 2 * Math.PI); ctx.closePath(); break;
            case 'Heart':
                ctx.moveTo(0, 0.5 * h);
                ctx.bezierCurveTo(-0.62 * w, 0.08 * h, -0.56 * w, -0.5 * h, -0.25 * w, -0.5 * h);
                ctx.bezierCurveTo(-0.1 * w, -0.5 * h, 0, -0.4 * h, 0, -0.28 * h);
                ctx.bezierCurveTo(0, -0.4 * h, 0.1 * w, -0.5 * h, 0.25 * w, -0.5 * h);
                ctx.bezierCurveTo(0.56 * w, -0.5 * h, 0.62 * w, 0.08 * h, 0, 0.5 * h);
                ctx.closePath();
                break;
            case 'Bubble': {
                const bh = h * 0.74, r = Math.min(bh, w) * 0.22, x0 = -w / 2, y0 = -h / 2, x1 = w / 2, y1 = y0 + bh;
                ctx.moveTo(x0 + r, y0);
                ctx.arcTo(x1, y0, x1, y1, r); ctx.arcTo(x1, y1, x0, y1, r);
                ctx.lineTo(x0 + w * 0.42, y1); ctx.lineTo(x0 + w * 0.2, h / 2); ctx.lineTo(x0 + w * 0.24, y1);
                ctx.arcTo(x0, y1, x0, y0, r); ctx.arcTo(x0, y0, x1, y0, r);
                ctx.closePath();
                break;
            }
            default: {
                const r = Math.max(0, Math.min(n.cornerRadius || 0, w / 2, h / 2));
                if (r > 0) ctx.roundRect(-w / 2, -h / 2, w, h, r); else ctx.rect(-w / 2, -h / 2, w, h);
            }
        }
    }

    function drawShape(ctx, n) {
        const sw = Math.max(0, n.strokeWidth || 0);
        const stroke = sw > 0 ? ink(n.stroke) : null;
        const dash = n.dash === 'Dashed' ? [sw * 3, sw * 2] : n.dash === 'Dotted' ? [0.001, sw * 2] : [];
        if (n.kind === 'Line') {
            if (!stroke) return;
            ctx.beginPath(); ctx.moveTo(-n.w / 2, 0); ctx.lineTo(n.w / 2, 0);
            ctx.lineWidth = sw; ctx.strokeStyle = stroke; ctx.setLineDash(dash);
            ctx.lineCap = n.dash === 'Dotted' ? 'round' : 'butt';
            ctx.stroke();
            return;
        }
        const fill = ink(n.fill);
        if (fill) { shapePath(ctx, n, n.w, n.h); ctx.fillStyle = fill; ctx.fill(); }
        if (stroke) {
            // keep the stroke inside the frame so the box is the printed extent
            const w = Math.max(0.01, n.w - sw), h = Math.max(0.01, n.h - sw);
            shapePath(ctx, { ...n, cornerRadius: Math.max(0, (n.cornerRadius || 0) - sw / 2) }, w, h);
            ctx.lineWidth = sw; ctx.strokeStyle = stroke; ctx.setLineDash(dash);
            ctx.lineJoin = 'miter'; ctx.lineCap = n.dash === 'Dotted' ? 'round' : 'butt';
            ctx.stroke();
        }
    }

    // ---- images / dithering ------------------------------------------------------
    const BAYER8 = [0, 32, 8, 40, 2, 34, 10, 42, 48, 16, 56, 24, 50, 18, 58, 26, 12, 44, 4, 36, 14, 46, 6, 38, 60, 28, 52, 20, 62, 30, 54, 22,
        3, 35, 11, 43, 1, 33, 9, 41, 51, 19, 59, 27, 49, 17, 57, 25, 15, 47, 7, 39, 13, 45, 5, 37, 63, 31, 55, 23, 61, 29, 53, 21];

    function dither(n, dpi) {
        const a = assets.get(n.assetId);
        if (!a || !a.ready) return null;
        const pw = Math.max(1, Math.min(6000, Math.round(n.w * dpi / 25.4)));
        const ph = Math.max(1, Math.min(6000, Math.round(n.h * dpi / 25.4)));
        const key = [n.assetId, pw, ph, n.dither, n.threshold, n.brightness, n.contrast, n.invert ? 1 : 0].join('|');
        let c = dithers.get(key);
        if (c) return c;
        c = document.createElement('canvas');
        c.width = pw; c.height = ph;
        const cx = c.getContext('2d', { willReadFrequently: true });
        cx.fillStyle = '#fff'; cx.fillRect(0, 0, pw, ph);
        cx.imageSmoothingQuality = 'high';
        cx.drawImage(a.img, 0, 0, pw, ph);
        const id = cx.getImageData(0, 0, pw, ph), d = id.data;
        const g = new Float32Array(pw * ph);
        const con = Math.max(-100, Math.min(100, n.contrast || 0)) * 2.55;
        const cf = (259 * (con + 255)) / (255 * (259 - con));
        const bri = (n.brightness || 0) * 2.55;
        for (let i = 0; i < pw * ph; i++) {
            let v = 0.299 * d[i * 4] + 0.587 * d[i * 4 + 1] + 0.114 * d[i * 4 + 2];
            v = (v - 128) * cf + 128 + bri;
            if (n.invert) v = 255 - v;
            g[i] = v < 0 ? 0 : v > 255 ? 255 : v;
        }
        const thr = n.threshold ?? 128;
        const spread = (x, y, e) => { if (x >= 0 && x < pw && y < ph) g[y * pw + x] += e; };
        for (let y = 0; y < ph; y++) for (let x = 0; x < pw; x++) {
            const i = y * pw + x, old = g[i];
            let out;
            if (n.dither === 'Bayer') out = old > (BAYER8[(y & 7) * 8 + (x & 7)] + 0.5) * 4 + (thr - 128) ? 255 : 0;
            else out = old >= thr ? 255 : 0;
            if (n.dither === 'FloydSteinberg') {
                const e = old - out;
                spread(x + 1, y, e * 7 / 16); spread(x - 1, y + 1, e * 3 / 16); spread(x, y + 1, e * 5 / 16); spread(x + 1, y + 1, e / 16);
            } else if (n.dither === 'Atkinson') {
                const e = (old - out) / 8;
                spread(x + 1, y, e); spread(x + 2, y, e); spread(x - 1, y + 1, e); spread(x, y + 1, e); spread(x + 1, y + 1, e); spread(x, y + 2, e);
            }
            d[i * 4] = d[i * 4 + 1] = d[i * 4 + 2] = out; d[i * 4 + 3] = 255;
        }
        cx.putImageData(id, 0, 0);
        if (dithers.size > 60) dithers.delete(dithers.keys().next().value);
        dithers.set(key, c);
        return c;
    }

    function placeholder(ctx, n, label) {
        ctx.save();
        ctx.fillStyle = '#fff'; ctx.fillRect(-n.w / 2, -n.h / 2, n.w, n.h);
        ctx.lineWidth = 0.15; ctx.strokeStyle = '#000'; ctx.setLineDash([0.6, 0.6]);
        ctx.strokeRect(-n.w / 2 + 0.08, -n.h / 2 + 0.08, n.w - 0.16, n.h - 0.16);
        const fs = Math.max(0.8, Math.min(2.4, n.h * 0.3, n.w * 0.12));
        ctx.scale(fs / 20, fs / 20);
        ctx.font = '500 20px "IBM Plex Mono", monospace'; ctx.fillStyle = '#000';
        ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
        ctx.fillText(label, 0, 0);
        ctx.restore();
    }

    // Draw a bitmap into a box given in mm. In print mode with no rotation the
    // target is snapped to whole device pixels so dots stay crisp.
    function blit(ctx, src, n, s, print, bx, by, bw, bh, dpi) {
        ctx.imageSmoothingEnabled = !print && s < dpi / 25.4 * 0.75 && src.width > 1 && src.height > 1;
        if (print && !n.rotation) {
            ctx.save();
            ctx.setTransform(1, 0, 0, 1, 0, 0);
            const x = Math.round((n.x + n.w / 2 + bx) * s), y = Math.round((n.y + n.h / 2 + by) * s);
            ctx.drawImage(src, x, y, Math.max(1, Math.round(bw * s)), Math.max(1, Math.round(bh * s)));
            ctx.restore();
        } else ctx.drawImage(src, bx, by, bw, bh);
    }

    function drawImage(ctx, n, s, print, dpi) {
        const c = dither(n, dpi);
        if (!c) { placeholder(ctx, n, 'image'); return; }
        blit(ctx, c, n, s, print, -n.w / 2, -n.h / 2, n.w, n.h, dpi);
    }

    // ---- barcodes ----------------------------------------------------------------
    function drawBarcode(ctx, n, s, print, dpi) {
        const m = matrices.get(matrixKey(n));
        if (!m) { placeholder(ctx, n, '…'); return; }
        if (m.error) { placeholder(ctx, n, '! invalid'); return; }
        const dot = 25.4 / dpi;
        ctx.fillStyle = '#fff'; ctx.fillRect(-n.w / 2, -n.h / 2, n.w, n.h);
        if (is2D(n.format)) {
            // whole printer dots per module, centred in the box
            const dots = Math.max(1, Math.floor(Math.min(n.w / dot / m.w, n.h / dot / m.h)));
            let bw = dots * m.w * dot, bh = dots * m.h * dot;
            if (bw > n.w || bh > n.h) { const k = Math.min(n.w / bw, n.h / bh); bw *= k; bh *= k; }
            blit(ctx, m.canvas, n, s, print, -bw / 2, -bh / 2, bw, bh, dpi);
            return;
        }
        const th = n.showText ? Math.max(1.4, Math.min(3, n.h * 0.24)) : 0;
        const dots = Math.max(1, Math.floor(n.w / dot / m.w));
        const bw = Math.min(n.w, dots * m.w * dot);
        const barH = Math.max(0.5, n.h - th);
        blit(ctx, m.canvas, n, s, print, -bw / 2, -n.h / 2, bw, barH, dpi);
        if (th) {
            ctx.save();
            ctx.translate(0, n.h / 2 - th / 2);
            const k = th * 0.82 / 20;
            ctx.scale(k, k);
            ctx.font = '500 20px "IBM Plex Mono", monospace';
            ctx.fillStyle = '#000'; ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
            const maxW = n.w / k;
            const tw = ctx.measureText(n.value).width;
            if (tw > maxW) ctx.scale(maxW / tw, 1);
            ctx.fillText(n.value, 0, 1);
            ctx.restore();
        }
    }

    // ---- tables ------------------------------------------------------------------
    // Column widths (mm), row height and a text scale per cell (mm per REF px).
    function tableLayout(n) {
        const rows = Math.max(1, n.rows | 0), cols = Math.max(1, n.cols | 0);
        const pad = n.padding || 0, line = Math.max(n.grid || 0, n.border || 0);
        const ex = 2 * pad + line, ey = pad + line;   // room lost to padding and lines in a cell
        const cells = [];
        for (let i = 0; i < rows * cols; i++) {
            const t = {
                text: String(n.cells?.[i] ?? '').replace(/\n/g, ' '), fontFamily: n.fontFamily, italic: n.italic,
                bold: n.bold || (i < cols && n.header !== 'None'), fontSize: n.fontSize, lineHeight: 1, mode: 'Auto',
            };
            cells.push({ t, L: layoutText(t) });
        }
        let colW = new Array(cols).fill(n.w / cols);
        if (n.fitColumns) {
            // every column gets the same text scale: width = scale × widest text + padding
            const nat = colW.map((_, c) => Math.max(REF * 0.6, ...cells.filter((_, i) => i % cols === c).map(q => q.L.w)));
            const scale = (n.w - cols * ex) / nat.reduce((a, b) => a + b, 0);
            if (scale > 0) colW = nat.map(v => v * scale + ex);
        }
        const rowH = n.h / rows;
        const fits = cells.map((q, i) => Math.max(0.001, Math.min(
            q.L.w ? (colW[i % cols] - ex) / q.L.w : Infinity, (rowH - ey) / q.L.h)));
        const fixed = Math.max(0.1, n.fontSize * MM_PER_PT) / REF;
        const used = fits.filter((_, i) => cells[i].L.w);
        const common = Math.min(...(used.length ? used : fits));
        const k = fits.map(f => n.autoSize ? Math.min(common, f) : Math.min(fixed, f));
        return { rows, cols, colW, rowH, cells, k, ex };
    }

    // For the cell editor and hit-testing: column widths in mm, font size per cell in mm.
    LM.tableLayout = (n) => { const T = tableLayout(n); return { colW: T.colW, font: T.k.map(k => k * REF) }; };

    function drawTable(ctx, n) {
        const T = tableLayout(n), x0 = -n.w / 2, y0 = -n.h / 2;
        const inv = n.header === 'Invert';
        if (inv) { ctx.fillStyle = '#000'; ctx.fillRect(x0, y0, n.w, T.rowH); }
        ctx.textBaseline = 'alphabetic';
        for (let r = 0; r < T.rows; r++) {
            let x = x0;
            for (let c = 0; c < T.cols; c++) {
                const i = r * T.cols + c, q = T.cells[i], cw = T.colW[c];
                if (q.L.w) {
                    const k = T.k[i], tw = q.L.w * k, th = q.L.h * k;
                    const tx = n.align === 'Left' ? x + T.ex / 2 : n.align === 'Right' ? x + cw - T.ex / 2 - tw : x + (cw - tw) / 2;
                    ctx.save();
                    ctx.translate(tx, y0 + r * T.rowH + (T.rowH - th) / 2);
                    ctx.scale(k, k);
                    ctx.font = fontOf(q.t, REF);
                    ctx.letterSpacing = '0px';
                    ctx.fillStyle = inv && r === 0 ? '#fff' : '#000';
                    ctx.fillText(q.L.lines[0].t, 0, (q.L.lh - (q.L.asc + q.L.desc)) / 2 + q.L.asc);
                    ctx.restore();
                }
                x += cw;
            }
        }
        ctx.setLineDash([]); ctx.lineCap = 'butt'; ctx.lineJoin = 'miter';
        if (n.grid > 0) {
            const vertical = (from, to, color) => {
                ctx.beginPath();
                for (let c = 1, x = x0; c < T.cols; c++) { x += T.colW[c - 1]; ctx.moveTo(x, from); ctx.lineTo(x, to); }
                ctx.strokeStyle = color; ctx.stroke();
            };
            ctx.lineWidth = n.grid;
            vertical(y0, y0 + n.h, '#000');
            if (inv) vertical(y0, y0 + T.rowH, '#fff');
            ctx.beginPath();
            for (let r = 1; r < T.rows; r++) { ctx.moveTo(x0, y0 + r * T.rowH); ctx.lineTo(x0 + n.w, y0 + r * T.rowH); }
            ctx.strokeStyle = '#000'; ctx.stroke();
        }
        if (n.border > 0) {
            const b = Math.min(n.border, n.w / 2, n.h / 2);
            ctx.lineWidth = b; ctx.strokeStyle = '#000';
            ctx.strokeRect(x0 + b / 2, y0 + b / 2, n.w - b, n.h - b);
        }
    }

    // ---- items -------------------------------------------------------------------
    function drawItems(ctx, frame, s, print) {
        for (const n of frame.items) {
            ctx.save();
            ctx.translate((n.x + n.w / 2) * s, (n.y + n.h / 2) * s);
            if (n.rotation) ctx.rotate(n.rotation * Math.PI / 180);
            ctx.scale(s, s);
            switch (n.type) {
                case 'text': drawText(ctx, n); break;
                case 'shape': drawShape(ctx, n); break;
                case 'table': drawTable(ctx, n); break;
                case 'image': drawImage(ctx, n, s, print, frame.dpi); break;
                case 'barcode': drawBarcode(ctx, n, s, print, frame.dpi); break;
            }
            ctx.restore();
        }
    }

    // ---- tape colours ----------------------------------------------------------------
    // Everything is drawn black on white (what the print head sees). For the preview,
    // map that onto the chosen print colour and tape colour.
    const rgb = (hex) => { const v = parseInt(String(hex || '').replace('#', ''), 16); return [(v >> 16) & 255, (v >> 8) & 255, v & 255]; };

    function recolor(ctx, x, y, w, h, tape, ink) {
        if (!tape || !ink || (tape.toLowerCase() === '#ffffff' && ink === '#000000')) return;
        const cw = ctx.canvas.width, ch = ctx.canvas.height;
        const x0 = Math.max(0, Math.round(x)), y0 = Math.max(0, Math.round(y));
        const x1 = Math.min(cw, Math.round(x + w)), y1 = Math.min(ch, Math.round(y + h));
        if (x1 <= x0 || y1 <= y0) return;
        const id = ctx.getImageData(x0, y0, x1 - x0, y1 - y0), d = id.data;
        const [tr, tg, tb] = rgb(tape), [ir, ig, ib] = rgb(ink);
        for (let i = 0; i < d.length; i += 4) {
            const k = (0.299 * d[i] + 0.587 * d[i + 1] + 0.114 * d[i + 2]) / 255; // 0 = ink, 1 = tape
            d[i] = ir + (tr - ir) * k; d[i + 1] = ig + (tg - ig) * k; d[i + 2] = ib + (tb - ib) * k;
        }
        ctx.putImageData(id, x0, y0);
    }

    // ---- editor canvas -----------------------------------------------------------
    function theme() {
        const cs = getComputedStyle(document.documentElement);
        const g = (k) => cs.getPropertyValue(k).trim();
        return { bg: g('--bg'), panel: g('--panel'), line: g('--line'), strong: g('--line-strong'), ink3: g('--ink-3'), accent: g('--accent'), guide: g('--guide'), cut: g('--cut') };
    }

    function boxPath(ctx, f, b) {
        const r = (b.rot || 0) * Math.PI / 180, c = Math.cos(r), s = Math.sin(r), z = f.zoom;
        const pt = (lx, ly) => [f.panX + (b.cx + lx * c - ly * s) * z, f.panY + (b.cy + lx * s + ly * c) * z];
        const p = [pt(-b.w / 2, -b.h / 2), pt(b.w / 2, -b.h / 2), pt(b.w / 2, b.h / 2), pt(-b.w / 2, b.h / 2)];
        ctx.beginPath();
        p.forEach((q, i) => i ? ctx.lineTo(Math.round(q[0]) + 0.5, Math.round(q[1]) + 0.5) : ctx.moveTo(Math.round(q[0]) + 0.5, Math.round(q[1]) + 0.5));
        ctx.closePath();
    }

    function rulers(ctx, f, t, W, H) {
        const z = f.zoom;
        ctx.fillStyle = t.panel;
        ctx.fillRect(0, 0, W, RULER); ctx.fillRect(0, 0, RULER, H);
        ctx.fillStyle = t.line;
        ctx.fillRect(0, RULER - 1, W, 1); ctx.fillRect(RULER - 1, 0, 1, H);
        const steps = [1, 2, 5, 10, 20, 50, 100, 200];
        const major = steps.find(v => v * z >= 46) || 500;
        const minor = major / (major * z / 10 >= 4 ? 10 : 5);
        ctx.font = '9px "IBM Plex Mono", monospace';
        ctx.textBaseline = 'top';
        for (const horiz of [true, false]) {
            const pan = horiz ? f.panX : f.panY, len = horiz ? W : H;
            const from = Math.floor((RULER - pan) / z / minor) * minor, to = (len - pan) / z;
            for (let i = 0, v = from; v <= to; i++, v = from + i * minor) {
                const p = Math.round(pan + v * z);
                if (p < RULER) continue;
                const isMajor = Math.abs(v / major - Math.round(v / major)) < 1e-6;
                const tick = isMajor ? RULER - 1 : 4;
                ctx.fillStyle = isMajor ? t.ink3 : t.strong;
                if (horiz) ctx.fillRect(p, RULER - 1 - tick, 1, tick); else ctx.fillRect(RULER - 1 - tick, p, tick, 1);
                if (!isMajor) continue;
                ctx.fillStyle = t.ink3;
                const label = String(Math.round(v));
                if (horiz) ctx.fillText(label, p + 3, 2);
                else { ctx.save(); ctx.translate(2, p - 3); ctx.rotate(-Math.PI / 2); ctx.fillText(label, 0, 0); ctx.restore(); }
            }
        }
        ctx.fillStyle = t.panel; ctx.fillRect(0, 0, RULER, RULER);
        ctx.fillStyle = t.line; ctx.fillRect(0, RULER - 1, RULER, 1); ctx.fillRect(RULER - 1, 0, 1, RULER);
    }

    function paint() {
        state.raf = 0;
        const canvas = state.canvas, f = state.frame;
        if (!canvas || !f) return;
        const ctx = canvas.getContext('2d');
        const dpr = window.devicePixelRatio || 1;
        const W = canvas.width / dpr, H = canvas.height / dpr, z = f.zoom, t = theme();
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        ctx.fillStyle = t.bg; ctx.fillRect(0, 0, W, H);

        const px = Math.round(f.panX), py = Math.round(f.panY), pw = Math.round(f.docW * z), ph = Math.round(f.docH * z);
        ctx.fillStyle = 'rgba(0,0,0,.16)'; ctx.fillRect(px + 3, py + 4, pw, ph);
        ctx.fillStyle = '#fff'; ctx.fillRect(px, py, pw, ph);

        ctx.save();
        ctx.translate(f.panX, f.panY);
        drawItems(ctx, f, z, false);
        ctx.restore();
        recolor(ctx, px * dpr, py * dpr, pw * dpr, ph * dpr, f.tape, f.ink);

        // ghost whatever hangs off the label: it will not print
        ctx.save();
        ctx.globalAlpha = 0.78; ctx.fillStyle = t.bg;
        ctx.beginPath(); ctx.rect(0, 0, W, H); ctx.rect(px, py, pw, ph); ctx.fill('evenodd');
        ctx.restore();

        if (f.showMargins) {
            const m = f.margins;
            const mx = px + m.l * z, my = py + m.t * z, mw = pw - (m.l + m.r) * z, mh = ph - (m.t + m.b) * z;
            if (mw > 0 && mh > 0) {
                ctx.save();
                ctx.globalAlpha = 0.09; ctx.fillStyle = t.guide;
                ctx.beginPath(); ctx.rect(px, py, pw, ph); ctx.rect(mx, my, mw, mh); ctx.fill('evenodd');
                ctx.globalAlpha = 1; ctx.strokeStyle = t.guide; ctx.lineWidth = 1; ctx.setLineDash([4, 3]);
                ctx.strokeRect(Math.round(mx) + 0.5, Math.round(my) + 0.5, Math.round(mw) - 1, Math.round(mh) - 1);
                ctx.restore();
            }
        }

        // layout guides: zones the layout fills in by itself, and fold / cell lines
        ctx.save();
        ctx.font = '500 9px "IBM Plex Mono", monospace'; ctx.textBaseline = 'alphabetic';
        for (const q of f.zones || []) {
            const x = Math.round(f.panX + q.x * z), w = Math.round(q.w * z);
            ctx.globalAlpha = 0.1; ctx.fillStyle = t.guide; ctx.fillRect(x, py, w, ph);
            ctx.globalAlpha = 1;
            if (ctx.measureText(q.label).width < w - 6) ctx.fillText(q.label, x + 4, py + ph + 13);
        }
        ctx.strokeStyle = t.guide; ctx.lineWidth = 1; ctx.setLineDash([1, 3]);
        for (const d of f.dividers || []) {
            const x = Math.round(f.panX + d * z) + 0.5;
            ctx.beginPath(); ctx.moveTo(x, py - 6); ctx.lineTo(x, py + ph + 6); ctx.stroke();
        }
        ctx.restore();

        // cut marks: a line across the tape with a tab above it to grab
        for (const c of f.cuts || []) {
            const x = Math.round(f.panX + c.x * z) + 0.5;
            ctx.save();
            ctx.strokeStyle = t.cut; ctx.fillStyle = t.cut; ctx.lineWidth = 1;
            ctx.setLineDash(c.half ? [2, 3] : [7, 3]);
            ctx.beginPath(); ctx.moveTo(x, py - 3); ctx.lineTo(x, py + ph + 6); ctx.stroke();
            ctx.setLineDash([]);
            ctx.beginPath(); ctx.moveTo(x, py - 3); ctx.lineTo(x - 5, py - 9); ctx.lineTo(x - 5, py - 17); ctx.lineTo(x + 5, py - 17); ctx.lineTo(x + 5, py - 9); ctx.closePath();
            if (c.half) { ctx.fillStyle = t.bg; ctx.fill(); ctx.stroke(); } else ctx.fill();
            ctx.font = '500 9px "IBM Plex Mono", monospace'; ctx.fillStyle = t.cut; ctx.textBaseline = 'alphabetic';
            ctx.fillText(c.half ? 'HALF CUT' : 'CUT', x + 9, py - 9);
            ctx.restore();
        }

        ctx.lineWidth = 1;
        if (f.scope) { ctx.save(); ctx.strokeStyle = t.accent; ctx.setLineDash([2, 3]); boxPath(ctx, f, f.scope); ctx.stroke(); ctx.restore(); }
        if (f.hover) { ctx.strokeStyle = t.accent; ctx.globalAlpha = 0.55; boxPath(ctx, f, f.hover); ctx.stroke(); ctx.globalAlpha = 1; }
        ctx.strokeStyle = t.accent;
        for (const b of f.sel || []) { boxPath(ctx, f, b); ctx.stroke(); }

        ctx.fillStyle = t.accent;
        for (const g of f.guides || []) {
            if (g.v) ctx.fillRect(Math.round(f.panX + g.pos * z), 0, 1, H);
            else ctx.fillRect(0, Math.round(f.panY + g.pos * z), W, 1);
        }

        for (const h of f.handles || []) {
            ctx.fillStyle = '#fff'; ctx.strokeStyle = t.accent; ctx.lineWidth = 1;
            ctx.beginPath();
            if (h.round) ctx.arc(h.x, h.y, 4.5, 0, Math.PI * 2);
            else ctx.rect(Math.round(h.x) - 3.5, Math.round(h.y) - 3.5, 7, 7);
            ctx.fill(); ctx.stroke();
        }

        if (f.marquee) {
            const q = f.marquee;
            const x = f.panX + q[0] * z, y = f.panY + q[1] * z;
            ctx.fillStyle = t.accent; ctx.globalAlpha = 0.1; ctx.fillRect(x, y, q[2] * z, q[3] * z);
            ctx.globalAlpha = 1; ctx.strokeStyle = t.accent; ctx.strokeRect(Math.round(x) + 0.5, Math.round(y) + 0.5, Math.round(q[2] * z), Math.round(q[3] * z));
        }

        rulers(ctx, f, t, W, H);
    }

    LM.render = (frame) => { state.frame = frame; LM.rerender(); };
    LM.rerender = () => { if (!state.raf && state.frame) state.raf = requestAnimationFrame(paint); };

    // ---- offscreen output ----------------------------------------------------------
    function toCanvas(frame, s, print) {
        const c = document.createElement('canvas');
        c.width = Math.max(1, Math.round(frame.docW * s));
        c.height = Math.max(1, Math.round(frame.docH * s));
        const ctx = c.getContext('2d', { willReadFrequently: true });
        ctx.fillStyle = '#fff'; ctx.fillRect(0, 0, c.width, c.height);
        drawItems(ctx, frame, s, print);
        if (!print) recolor(ctx, 0, 0, c.width, c.height, frame.tape, frame.ink);
        return c;
    }

    function threshold(c) {
        const ctx = c.getContext('2d');
        const id = ctx.getImageData(0, 0, c.width, c.height), d = id.data;
        const bits = new Uint8Array(c.width * c.height);
        for (let i = 0; i < bits.length; i++) {
            const v = 0.299 * d[i * 4] + 0.587 * d[i * 4 + 1] + 0.114 * d[i * 4 + 2];
            bits[i] = v < 128 ? 1 : 0;
            d[i * 4] = d[i * 4 + 1] = d[i * 4 + 2] = bits[i] ? 0 : 255; d[i * 4 + 3] = 255;
        }
        ctx.putImageData(id, 0, 0);
        return bits;
    }

    // 1-bit raster at the document dpi: 8-byte header (w, h little-endian) + one byte per dot.
    LM.rasterize = async (frame) => {
        await assetsSettled(frame);
        const c = toCanvas(frame, frame.dpi / 25.4, true);
        const bits = threshold(c);
        const out = new Uint8Array(8 + bits.length);
        new DataView(out.buffer).setUint32(0, c.width, true);
        new DataView(out.buffer).setUint32(4, c.height, true);
        out.set(bits, 8);
        return out;
    };

    // PNG data URL. mono = exactly what prints; otherwise a smooth thumbnail.
    LM.toPng = async (frame, mono, maxW) => {
        await assetsSettled(frame);
        let s = frame.dpi / 25.4;
        if (!mono && maxW) s = Math.min(s * 2, maxW / frame.docW, 160 / frame.docH * 1.5);
        const c = toCanvas(frame, s, mono);
        if (mono) threshold(c);
        return c.toDataURL('image/png');
    };
})();
