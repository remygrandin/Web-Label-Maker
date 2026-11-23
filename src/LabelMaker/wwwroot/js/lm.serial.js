// WebLabelMaker serial transport (Web Serial). A Bluetooth printer paired with the OS
// shows up as a serial port, so the same code drives USB-serial and Bluetooth SPP.
(() => {
    const LM = window.LM;
    let port = null, reader = null, queue = [], waiters = [], alive = false;

    const wake = () => { const w = waiters; waiters = []; w.forEach(f => f()); };

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

    async function open(p) {
        await LM.serial.close();
        // The baud rate is ignored by Bluetooth serial links but the API requires one.
        await p.open({ baudRate: 9600, bufferSize: 16384 });
        port = p; queue = []; alive = true;
        reader = port.readable.getReader();
        pump();
        const info = p.getInfo ? p.getInfo() : {};
        if (info.bluetoothServiceClassId) return 'Bluetooth serial port';
        if (info.usbVendorId) return `USB serial ${info.usbVendorId.toString(16).padStart(4, '0')}:${(info.usbProductId || 0).toString(16).padStart(4, '0')}`;
        return 'Serial port';
    }

    LM.serial = {
        supported: () => 'serial' in navigator,
        connected: () => alive,

        // Must be called from a user gesture: shows the browser's port picker.
        connect: async () => open(await navigator.serial.requestPort()),

        // Reopen a port the user already granted, without a prompt. Returns null when there is none.
        reconnect: async () => {
            if (!('serial' in navigator)) return null;
            const ports = await navigator.serial.getPorts();
            if (ports.length !== 1) return null;
            try { return await open(ports[0]); } catch { return null; }
        },

        close: async () => {
            alive = false;
            try { if (reader) await reader.cancel(); } catch { }
            try { if (reader) reader.releaseLock(); } catch { }
            try { if (port) await port.close(); } catch { }
            port = null; reader = null; queue = [];
            wake();
        },

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
    LM.printPreview = async (frame, x0, y0, w, h) => {
        const url = await LM.toPng(frame, true, 0);
        const img = new Image();
        await new Promise((res) => { img.onload = res; img.src = url; });
        const c = document.createElement('canvas');
        c.width = img.width; c.height = img.height;
        const ctx = c.getContext('2d');
        ctx.drawImage(img, 0, 0);
        ctx.fillStyle = 'rgba(229, 69, 11, .28)';
        ctx.beginPath(); ctx.rect(0, 0, c.width, c.height); ctx.rect(x0, y0, w, h); ctx.fill('evenodd');
        return c.toDataURL('image/png');
    };
})();
