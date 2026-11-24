// WebLabelMaker serial transport (Web Serial). A Bluetooth printer paired with the OS
// shows up as a serial port, so the same code drives USB-serial and Bluetooth SPP.
//
// The port is only held open while the app is talking to the printer (a status check or a
// print job) and is closed straight after. A serial port can be open in one place at a
// time, so this is what lets several tabs share one printer; a Web Lock makes them take turns.
(() => {
    const LM = window.LM;
    let port = null;            // the port the user chose; not open between conversations
    let reader = null, queue = [], waiters = [], alive = false, release = null;

    const wake = () => { const w = waiters; waiters = []; w.forEach(f => f()); };
    const sleep = (ms) => new Promise(r => setTimeout(r, ms));

    async function pump() {
        try {
            while (alive) {
                const { value, done } = await reader.read();
                if (done) break;
                if (value) { for (const b of value) queue.push(b); wake(); }
            }
        } catch { /* port lost */ }
        alive = false;
        wake();
    }

    const waitData = (ms) => new Promise((resolve) => {
        const t = setTimeout(resolve, ms);
        waiters.push(() => { clearTimeout(t); resolve(); });
    });

    const describe = (p) => {
        const info = p.getInfo ? p.getInfo() : {};
        if (info.bluetoothServiceClassId) return 'Bluetooth serial port';
        if (info.usbVendorId) return `USB serial ${info.usbVendorId.toString(16).padStart(4, '0')}:${(info.usbProductId || 0).toString(16).padStart(4, '0')}`;
        return 'Serial port';
    };

    // Wait for our turn: another tab may be talking to the printer.
    const takeTurn = () => new Promise((resolve, reject) => {
        if (!navigator.locks) { resolve(); return; }
        const giveUp = new AbortController();
        const timer = setTimeout(() => giveUp.abort(), 25000);
        navigator.locks.request('wlm-printer', { signal: giveUp.signal }, () => {
            clearTimeout(timer);
            resolve();
            return new Promise((done) => { release = done; });
        }).catch(() => reject(new Error('BUSY: the printer is in use by another tab.')));
    });
    const endTurn = () => { const r = release; release = null; if (r) r(); };

    async function openPort() {
        // The baud rate is ignored by Bluetooth serial links but the API requires one.
        // A port another tab has just closed can refuse for a moment: try a few times.
        for (let attempt = 0; ; attempt++) {
            try { await port.open({ baudRate: 9600, bufferSize: 16384 }); break; }
            catch (e) { if (attempt >= 3) throw e; await sleep(400); }
        }
        queue = []; alive = true;
        reader = port.readable.getReader();
        pump();
    }

    async function closePort() {
        alive = false;
        try { if (reader) await reader.cancel(); } catch { }
        try { if (reader) reader.releaseLock(); } catch { }
        try { if (port) await port.close(); } catch { }
        reader = null; queue = [];
        wake();
    }

    LM.serial = {
        supported: () => 'serial' in navigator,
        // a port has been chosen (it is not kept open)
        connected: () => !!port,
        visible: () => document.visibilityState === 'visible',

        // Must be called from a user gesture: shows the browser's port picker.
        connect: async () => {
            const p = await navigator.serial.requestPort();
            await closePort();
            port = p;
            // remember which of the granted ports this is, to use it next time without a prompt
            try { localStorage.setItem('wlm.port', String((await navigator.serial.getPorts()).indexOf(p))); } catch { }
            return describe(p);
        },

        // Take up the port used last time (the browser keeps the permission), without a prompt.
        // Returns null when there is none.
        reconnect: async () => {
            if (!('serial' in navigator)) return null;
            const ports = await navigator.serial.getPorts();
            let last = -1;
            try { last = parseInt(localStorage.getItem('wlm.port') ?? '-1', 10); } catch { }
            const p = ports[last] ?? (ports.length === 1 ? ports[0] : null);
            if (!p) return null;
            await closePort();
            port = p;
            return describe(p);
        },

        // Forget the port.
        close: async () => { await closePort(); endTurn(); port = null; },

        // Start a conversation: wait for other tabs, then open the port. Always pair with end().
        begin: async () => {
            if (!port) throw new Error('The printer is not connected.');
            await takeTurn();
            try { await openPort(); }
            catch (e) { await closePort(); endTurn(); throw e; }
        },
        // Finish a conversation: close the port so other tabs can use it.
        end: async () => { await closePort(); endTurn(); },

        write: async (bytes) => {
            if (!alive) throw new Error('The printer is not connected.');
            const writer = port.writable.getWriter();
            try {
                for (let i = 0; i < bytes.length; i += 512) await writer.write(bytes.subarray(i, i + 512));
            } finally { writer.releaseLock(); }
        },

        flushInput: () => { queue = []; },

        // Next 32-byte Brother status frame (re-synchronised on its 80 20 42 header), or null on timeout.
        readStatus: async (timeoutMs) => {
            const deadline = Date.now() + timeoutMs;
            for (;;) {
                while (queue.length >= 3 && !(queue[0] === 0x80 && queue[1] === 0x20 && queue[2] === 0x42)) queue.shift();
                if (queue.length >= 32) return new Uint8Array(queue.splice(0, 32));
                const left = deadline - Date.now();
                if (left <= 0 || !alive) return null;
                await waitData(left);
            }
        },
    };

    // Print preview: the 1-bit raster, with everything outside the printable window tinted.
    LM.printPreview = async (frame, rects) => {
        const url = await LM.toPng(frame, true, 0);
        const img = new Image();
        await new Promise((res) => { img.onload = res; img.src = url; });
        const c = document.createElement('canvas');
        c.width = img.width; c.height = img.height;
        const ctx = c.getContext('2d');
        ctx.drawImage(img, 0, 0);
        ctx.fillStyle = 'rgba(229, 69, 11, .28)';
        ctx.beginPath(); ctx.rect(0, 0, c.width, c.height);
        for (let i = 0; i + 3 < rects.length; i += 4) ctx.rect(rects[i], rects[i + 1], rects[i + 2], rects[i + 3]);
        ctx.fill("evenodd");
        return c.toDataURL('image/png');
    };
})();
