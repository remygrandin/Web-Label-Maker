using LabelMaker.Models;
using Microsoft.JSInterop;

namespace LabelMaker.Services;

/// <summary>Connection to a printer over Web Serial and the print job flow.</summary>
public sealed class PrinterService
{
    readonly IJSRuntime _js;
    readonly Editor _ed;
    readonly Shell _shell;

    public PrinterService(IJSRuntime js, Editor ed, Shell shell)
    {
        _js = js; _ed = ed; _shell = shell;
        _ed.Mutated += WarnMismatch; // the label may have switched tape
    }

    public bool Supported { get; private set; } = true;
    public bool Connected { get; private set; }
    public bool Busy { get; private set; }
    public string PortInfo { get; private set; } = "";
    public PrinterStatus? Status { get; private set; }
    public List<string> Log { get; } = new();
    public PrintOptions Options { get; } = new();
    public event Action? Changed;

    public PrinterModel Model => Brother.Model(_shell.Settings.PrinterModel);
    public MediaInfo Media => Models.Media.Get(_ed.Doc.MediaId);
    public TapeGeometry? Geometry => Brother.Geometry(Model, Media);

    void Say(string line)
    {
        Log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        if (Log.Count > 200) Log.RemoveAt(0);
        Changed?.Invoke();
    }

    public void SetModel(string id)
    {
        _shell.Settings.PrinterModel = id;
        _shell.SaveSettings();
        // lay the label out at this printer's resolution so dithering and barcode modules match its dots
        if (Math.Abs(_ed.Doc.Dpi - Model.Dpi) > 0.5) _ed.Mutate(() => _ed.Doc.Dpi = Model.Dpi);
        Changed?.Invoke();
    }

    readonly SemaphoreSlim _io = new(1, 1);   // one conversation with the printer at a time
    bool _started;
    CancellationTokenSource? _watch;
    int _warnedTape = -1;

    /// <summary>The loaded cassette is a different width from the label on screen.</summary>
    public bool TapeMismatch => Status is { } s && s.MediaWidthCode != 0 && s.MediaWidthCode != Media.WidthCode;

    /// <summary>
    /// Called once at start-up, and again whenever a printer view opens: reopens the port used
    /// last time (no prompt needed) unless the user disconnected on purpose.
    /// </summary>
    public async Task Init()
    {
        Supported = await _js.InvokeAsync<bool>("LM.serial.supported");
        if (!Supported) { Changed?.Invoke(); return; }
        bool first = !_started;
        _started = true;
        Connected = await _js.InvokeAsync<bool>("LM.serial.connected");
        if (Connected) { if (!first) await Query(); }
        else if (_shell.Settings.AutoConnect) await Reconnect();
        Changed?.Invoke();
    }

    /// <summary>Reopen the last port without a prompt. Stays quiet when the printer is off or out of range.</summary>
    public async Task<bool> Reconnect()
    {
        if (Busy || Connecting) return Connected;
        Connecting = true;
        Changed?.Invoke();
        try
        {
            if (await _js.InvokeAsync<string?>("LM.serial.reconnect") is { } info)
            {
                Connected = true;
                PortInfo = info;
                if (await Query() is null)
                {
                    // the port opens even when the printer is switched off: do not call that connected
                    await _js.InvokeVoidAsync("LM.serial.close");
                    Connected = false;
                    Say("The printer did not answer; it may be off or out of range.");
                }
                else { Say($"Reconnected to {info}."); Watch(); }
            }
        }
        catch (JSException ex) { Connected = false; Say($"Could not reopen the port: {FirstLine(ex.Message)}"); }
        finally { Connecting = false; Changed?.Invoke(); }
        return Connected;
    }

    public bool Connecting { get; private set; }

    /// <summary>Opens the browser's port picker; must run inside a click handler.</summary>
    public async Task Connect()
    {
        try
        {
            PortInfo = await _js.InvokeAsync<string>("LM.serial.connect");
            Connected = true;
            _shell.Settings.AutoConnect = true;
            _shell.SaveSettings();
            Say($"Connected to {PortInfo}.");
            await Query();
            Watch();
        }
        catch (JSException ex)
        {
            Connected = false;
            if (!ex.Message.Contains("No port selected")) Say($"Could not open the port: {FirstLine(ex.Message)}");
        }
        Changed?.Invoke();
    }

    public async Task Disconnect()
    {
        _watch?.Cancel();
        await _js.InvokeVoidAsync("LM.serial.close");
        Connected = false;
        Status = null;
        // a deliberate disconnect is remembered: no automatic reconnect next time
        _shell.Settings.AutoConnect = false;
        _shell.SaveSettings();
        Say("Disconnected.");
    }

    /// <summary>While connected, ask the printer now and then what is loaded, so a cassette swap shows up.</summary>
    async void Watch()
    {
        _watch?.Cancel();
        var cts = _watch = new CancellationTokenSource();
        try
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(20000, cts.Token);
                if (!Connected) return;
                if (Busy) continue;
                if (await Query(quiet: true) is null && !Busy)
                {
                    await _js.InvokeVoidAsync("LM.serial.close");
                    Connected = false;
                    Status = null;
                    Say("Lost contact with the printer.");
                    return;
                }
            }
        }
        catch (TaskCanceledException) { }
        catch (JSException) { }
    }

    static string FirstLine(string s) => s.Split('\n')[0];

    /// <summary>Reset the printer and ask what is loaded. Returns null when it does not answer.</summary>
    public async Task<PrinterStatus?> Query(bool quiet = false)
    {
        await _io.WaitAsync();
        try { return await QueryCore(quiet); }
        finally { _io.Release(); }
    }

    async Task<PrinterStatus?> QueryCore(bool quiet = false)
    {
        try
        {
            // a Bluetooth link can be half-open on first use: retry a few times
            for (int attempt = 0; attempt < 3; attempt++)
            {
                await _js.InvokeVoidAsync("LM.serial.flushInput");
                await _js.InvokeVoidAsync("LM.serial.write", Brother.Preamble(Model));
                var raw = await _js.InvokeAsync<byte[]?>("LM.serial.readStatus", 1500);
                if (raw is { Length: 32 })
                {
                    var before = Status;
                    Status = new PrinterStatus { Raw = raw };
                    bool changed = before is null || before.MediaWidthCode != Status.MediaWidthCode
                        || before.TapeColorCode != Status.TapeColorCode || !before.Errors.SequenceEqual(Status.Errors);
                    if (!quiet || changed)
                        Say($"Printer reports {Status.MediaWidth} tape ({Status.ModelName})" +
                            (Status.Errors.Count > 0 ? $" — {string.Join(", ", Status.Errors)}" : "."));
                    WarnMismatch();
                    Changed?.Invoke();
                    return Status;
                }
            }
            Status = null;
            if (!quiet) Say("The printer did not answer the status request.");
        }
        catch (JSException ex)
        {
            Status = null;
            Connected = await _js.InvokeAsync<bool>("LM.serial.connected");
            if (!quiet) Say($"Status request failed: {FirstLine(ex.Message)}");
        }
        Changed?.Invoke();
        return null;
    }

    /// <summary>The cassette in the printer, as a tape the label can use; null when unknown.</summary>
    public MediaInfo? LoadedMedia => Status is { MediaWidthCode: not 0 } s ? Models.Media.All.FirstOrDefault(m => m.WidthCode == s.MediaWidthCode) : null;

    /// <summary>Set the label to what is loaded: tape width, and tape and print colour when the printer reports them.</summary>
    public async Task MatchLabel()
    {
        if (Connected) await Query(quiet: true);
        if (Status is not { } s || LoadedMedia is not { } loaded)
        {
            _shell.Toast(Connected ? "The printer reports no tape cassette." : "Connect the printer first (top right).");
            return;
        }
        if (loaded.Id != _ed.Doc.MediaId && !await _ed.ChangeTape(loaded.Id)) return;
        bool colours = s.HasColors && s.TapeColor.Id is not null && s.TextColor.Id is not null;
        if (colours && (s.TapeColor.Id != _ed.Doc.TapeColor || s.TextColor.Id != _ed.Doc.InkColor))
            _ed.Mutate(() => { _ed.Doc.TapeColor = s.TapeColor.Id!; _ed.Doc.InkColor = s.TextColor.Id!; });
        _shell.Toast(colours
            ? $"Label set to {loaded.Label} tape, {s.TextColor.Name} on {s.TapeColor.Name.ToLowerInvariant()}."
            : $"Label set to {loaded.Label} tape. The printer did not report a colour it knows.");
    }

    /// <summary>Say once, per cassette and label width, that they do not match.</summary>
    public void WarnMismatch()
    {
        if (!TapeMismatch) { _warnedTape = -1; return; }
        int key = Status!.MediaWidthCode * 1000 + Media.WidthCode;
        if (key == _warnedTape) return;
        _warnedTape = key;
        _shell.Toast($"The printer holds {Status.MediaWidth} tape, but this label is laid out for {Media.Label}.");
    }

    RenderFrame Frame()
    {
        var f = _ed.BuildFrame(_ed.Doc);
        f.Dpi = Model.Dpi;
        return f;
    }

    /// <summary>Cut marks this printer can act on, in dots. Printers without a cutter print the label in one piece.</summary>
    List<CutMark> ActiveCuts => Model.AutoCut ? _ed.Doc.Cuts.OrderBy(c => c.X).ToList() : new();

    public string? CutNote => _ed.Doc.Cuts.Count == 0 ? null
        : !Model.AutoCut ? "This printer has no automatic cutter, so cut marks are ignored."
        : !Model.HalfCut && _ed.Doc.Cuts.Any(c => c.Half) ? "This printer cannot half cut: half-cut marks become a gap without a cut."
        : null;

    int Dots(double mm) => (int)Math.Round(mm * Model.Dpi / 25.4);

    public string? PreviewError { get; private set; }

    public async Task<string?> Preview()
    {
        PreviewError = null;
        if (Geometry is not { } g) return null;
        var f = Frame();
        int w = Dots(f.DocW), h = Dots(f.DocH);
        int y0 = (int)Math.Round((h - g.PrintPins) / 2.0);
        var rects = new List<int>();
        try
        {
            foreach (var (x0, lines) in Brother.Pieces(Model, Options, w, ActiveCuts.Select(c => Dots(c.X))))
                rects.AddRange([x0, y0, lines, g.PrintPins]);
        }
        catch (InvalidOperationException ex) { PreviewError = ex.Message; }
        return await _js.InvokeAsync<string>("LM.printPreview", f, rects);
    }

    public async Task Print(bool ignoreTapeCheck = false)
    {
        if (Busy || Geometry is not { } g) return;
        Busy = true;
        Changed?.Invoke();
        try
        {
            await _io.WaitAsync();
            var status = await QueryCore();
            if (status is not null)
            {
                if (status.Errors.Count > 0) { Say("Not printing: clear the printer error first."); return; }
                if (!ignoreTapeCheck && status.MediaWidthCode != Media.WidthCode)
                {
                    Say($"Not printing: the printer holds {status.MediaWidth} tape but this label is laid out for {Media.Label}.");
                    return;
                }
            }
            else if (!Connected) return;
            else Say("Sending the job without a status reply.");

            // One page per piece between cut marks, per copy. A label using {seq} is drawn afresh for each copy.
            int copies = Math.Clamp(Options.Copies, 1, 99);
            bool seq = _ed.UsesSeq;
            var counter = _ed.Doc.Counter;
            var cuts = ActiveCuts;
            var pages = new List<Brother.PrintPage>();
            byte[]? raster = null;
            try
            {
                for (int copy = 0; copy < copies; copy++)
                {
                    if (raster is null || seq)
                    {
                        _ed.SeqOverride = counter.Value + copy * counter.Step;
                        _ed.Sync(); // text boxes and code matrices for this copy's values
                        raster = await _js.InvokeAsync<byte[]>("LM.rasterize", Frame());
                    }
                    int w = BitConverter.ToInt32(raster, 0), h = BitConverter.ToInt32(raster, 4);
                    var bits = raster[8..];
                    var pieces = Brother.Pieces(Model, Options, w, cuts.Select(c => Dots(c.X)));
                    for (int i = 0; i < pieces.Count; i++)
                    {
                        bool end = i == pieces.Count - 1;
                        pages.Add(new Brother.PrintPage(bits, w, h, pieces[i].X0, pieces[i].Lines,
                            Cut: end ? Options.AutoCut : !cuts[i].Half,
                            Half: end ? Options.HalfCut : cuts[i].Half));
                    }
                }
            }
            finally { _ed.SeqOverride = null; _ed.Sync(); }

            var job = Brother.BuildJob(Model, Media, g, Options, pages);
            Say($"Sending {job.Length:N0} bytes ({pages.Count} page{(pages.Count > 1 ? "s" : "")}, {pages.Sum(p => p.Lines)} raster lines).");
            await _js.InvokeVoidAsync("LM.serial.write", job);

            // The printer announces phase changes, then "printing completed" per page.
            int done = 0;
            var deadline = DateTime.UtcNow.AddSeconds(30 + 20 * pages.Count);
            while (done < pages.Count && DateTime.UtcNow < deadline)
            {
                var raw = await _js.InvokeAsync<byte[]?>("LM.serial.readStatus", 2000);
                if (raw is not { Length: 32 }) continue;
                var s = new PrinterStatus { Raw = raw };
                switch (s.StatusType)
                {
                    case 0x01: done++; Say(pages.Count > 1 ? $"Printed page {done} of {pages.Count}." : "Printing completed."); break;
                    case 0x02:
                        Status = s;
                        Say($"Printer error: {(s.Errors.Count > 0 ? string.Join(", ", s.Errors) : "unspecified")}.");
                        await _js.InvokeVoidAsync("LM.serial.write", Brother.Preamble(Model)[..^3]); // invalidate + initialise
                        return;
                    case 0x04: Say("The printer was switched off."); return;
                    case 0x06 when s.Phase == 1: Say("Printing…"); break;
                }
            }
            if (done < pages.Count) Say("No completion message from the printer; it may still have printed.");
            else _shell.Toast(copies > 1 ? $"Printed {copies} labels." : "Label printed.");

            // the next print run carries on where this one stopped
            if (seq) _ed.Mutate(() => counter.Value += counter.Step * copies);
        }
        catch (Exception ex)
        {
            Connected = await _js.InvokeAsync<bool>("LM.serial.connected");
            Say($"Print failed: {FirstLine(ex.Message)}");
        }
        finally
        {
            _io.Release();
            Busy = false;
            Changed?.Invoke();
        }
    }
}
