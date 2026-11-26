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

    /// <summary>A port has been chosen. It is only opened while talking to the printer, so other tabs can use it too.</summary>
    public bool Linked { get; private set; }

    /// <summary>
    /// Called once at start-up, and again whenever a printer view opens: takes up the port used
    /// last time (no prompt needed) unless the user disconnected on purpose.
    /// </summary>
    public async Task Init()
    {
        Supported = await _js.InvokeAsync<bool>("LM.serial.supported");
        if (!Supported) { Changed?.Invoke(); return; }
        _started = true;
        if (Linked) await Query();
        else if (_shell.Settings.AutoConnect) await Reconnect();
        Changed?.Invoke();
    }

    /// <summary>Take up the last port without a prompt and ask the printer what is loaded.</summary>
    public async Task<bool> Reconnect()
    {
        if (Busy || Connecting) return Connected;
        Connecting = true;
        Changed?.Invoke();
        try
        {
            if (await _js.InvokeAsync<string?>("LM.serial.reconnect") is { } info)
            {
                Linked = true;
                PortInfo = info;
                if (await Query() is null) Say("The printer did not answer; it may be off or out of range.");
                Watch();
            }
        }
        catch (JSException ex) { Connected = false; Say($"Could not reach the port: {FirstLine(ex.Message)}"); }
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
            Linked = true;
            _shell.Settings.AutoConnect = true;
            _shell.SaveSettings();
            Say($"Using {PortInfo}.");
            await Query();
            Watch();
        }
        catch (JSException ex)
        {
            if (!ex.Message.Contains("No port selected")) Say($"Could not use the port: {FirstLine(ex.Message)}");
        }
        Changed?.Invoke();
    }

    public async Task Disconnect()
    {
        _watch?.Cancel();
        await _js.InvokeVoidAsync("LM.serial.close");
        Linked = false;
        Connected = false;
        Status = null;
        // a deliberate disconnect is remembered: no automatic reconnect next time
        _shell.Settings.AutoConnect = false;
        _shell.SaveSettings();
        Say("Disconnected.");
    }

    /// <summary>
    /// While a port is chosen, ask the printer now and then what is loaded, so a cassette swap or
    /// the printer being switched on or off shows up. Only the tab being looked at does this.
    /// </summary>
    async void Watch()
    {
        _watch?.Cancel();
        var cts = _watch = new CancellationTokenSource();
        try
        {
            while (!cts.IsCancellationRequested && Linked)
            {
                await Task.Delay(20000, cts.Token);
                if (Busy || Connecting || !await _js.InvokeAsync<bool>("LM.serial.visible")) continue;
                await Query(quiet: true);
            }
        }
        catch (TaskCanceledException) { }
        catch (JSException) { }
    }

    static string FirstLine(string s) => s.Split('\n')[0];

    /// <summary>
    /// Open the port, reset the printer and ask what is loaded, then close the port again.
    /// Returns null when the printer does not answer. <see cref="Connected"/> follows the outcome,
    /// except when another tab is using the printer: then nothing changes.
    /// </summary>
    public async Task<PrinterStatus?> Query(bool quiet = false)
    {
        await _io.WaitAsync();
        bool was = Connected;
        try
        {
            try { await _js.InvokeVoidAsync("LM.serial.begin"); }
            catch (JSException ex)
            {
                if (ex.Message.Contains("BUSY")) { if (!quiet) Say("The printer is in use by another tab; try again in a moment."); return Status; }
                Connected = false; Status = null;
                if (!quiet || was) Say($"Could not open the port: {FirstLine(ex.Message)}");
                Changed?.Invoke();
                return null;
            }
            var status = await QueryCore(quiet);
            Connected = status is not null;
            if (was && !Connected) Say("Lost contact with the printer.");
            else if (!was && Connected && quiet) Say("The printer is back.");
            return status;
        }
        finally
        {
            try { await _js.InvokeVoidAsync("LM.serial.end"); } catch (JSException) { }
            _io.Release();
            Changed?.Invoke();
        }
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
    /// <summary>Something the user should know about how this label will come out, without it being an error.</summary>
    public string? PreviewNote { get; private set; }

    public async Task<string?> Preview()
    {
        PreviewError = null;
        PreviewNote = null;
        if (Geometry is not { } g) return null;
        var f = Frame();
        int w = Dots(f.DocW), h = Dots(f.DocH);
        if (Brother.StretchedLength(Model, Options, w, ActiveCuts.Count > 0) is { } dots)
            PreviewNote = $"This label is shorter than the printer can make with a {Brother.FeedDots(Model, Options) * 25.4 / Model.Dpi:0.#} mm feed. " +
                $"The whole design will print, centred on about {dots * 25.4 / Model.Dpi:0.#} mm of tape. A smaller feed gives a shorter label.";
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

    /// <summary>After a page, wait briefly for the "waiting to receive" phase change before sending more.</summary>
    async Task WaitReady()
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until)
        {
            var raw = await _js.InvokeAsync<byte[]?>("LM.serial.readStatus", 500);
            if (raw is not { Length: 32 }) continue;
            var s = new PrinterStatus { Raw = raw };
            if (s.StatusType == 0x06 && s.Phase == 0) return;
        }
    }

    public async Task Print(bool ignoreTapeCheck = false)
    {
        if (Busy || Geometry is not { } g) return;
        Busy = true;
        bool sending = false;
        Changed?.Invoke();
        try
        {
            await _io.WaitAsync();
            await _js.InvokeVoidAsync("LM.serial.begin");
            var status = await QueryCore();
            Connected = status is not null;
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

            // One page at a time, as Brother's reference describes: send a page, wait for the printer
            // to say it has printed it and is ready to receive again, then send the next. Sending
            // every page in one go overruns the printer's buffer after a few labels and leaves it
            // stuck on a half-received page.
            var jobs = Brother.BuildPageJobs(Model, Media, g, Options, pages);
            Say($"Printing {pages.Count} page{(pages.Count > 1 ? "s" : "")} ({jobs.Sum(j => j.Length):N0} bytes, {pages.Sum(p => p.Lines)} raster lines).");
            int done = 0;
            bool silent = false;
            for (int n = 0; n < jobs.Count; n++)
            {
                await _js.InvokeVoidAsync("LM.serial.flushInput");
                sending = true;
                await _js.InvokeVoidAsync("LM.serial.write", jobs[n]);
                sending = false;

                // generous: the page has to be printed, and a cut takes a moment
                var deadline = DateTime.UtcNow.AddSeconds(25 + pages[n].Lines / 40.0);
                bool printed = false;
                while (!printed && DateTime.UtcNow < deadline)
                {
                    var raw = await _js.InvokeAsync<byte[]?>("LM.serial.readStatus", 2000);
                    if (raw is not { Length: 32 }) continue;
                    var s = new PrinterStatus { Raw = raw };
                    switch (s.StatusType)
                    {
                        case 0x01:
                            printed = true; done++;
                            Say(pages.Count > 1 ? $"Printed page {done} of {pages.Count}." : "Printing completed.");
                            break;
                        case 0x02:
                            Status = s;
                            Say($"Printer error: {(s.Errors.Count > 0 ? string.Join(", ", s.Errors) : "unspecified")}.");
                            await _js.InvokeVoidAsync("LM.serial.write", Brother.Preamble(Model)[..^3]); // invalidate + initialise
                            return;
                        case 0x04: Say("The printer was switched off."); return;
                    }
                }
                if (!printed)
                {
                    // No word from the printer. Carry on rather than leave the job without its final
                    // feed and cut, but say so: from here the pages are sent without confirmation.
                    if (!silent) Say("No completion message from the printer; continuing without confirmation.");
                    silent = true;
                    continue;
                }
                // it then announces that it is waiting for data again
                if (n < jobs.Count - 1) await WaitReady();
            }
            if (done < pages.Count) Say("The printer did not confirm every page; check what was printed.");
            else _shell.Toast(copies > 1 ? $"Printed {copies} labels." : "Label printed.");

            // the next print run carries on where this one stopped
            if (seq) _ed.Mutate(() => counter.Value += counter.Step * copies);
        }
        catch (Exception ex)
        {
            // A job cut off part-way leaves the printer waiting for the rest: clear it now rather
            // than at the next status check. Best effort, since the link may be what failed.
            if (sending)
                try { await _js.InvokeVoidAsync("LM.serial.write", Brother.Preamble(Model)[..^3]); } catch (JSException) { }
            Say(ex.Message.Contains("BUSY") ? "Not printing: the printer is in use by another tab." : $"Print failed: {FirstLine(ex.Message)}");
        }
        finally
        {
            try { await _js.InvokeVoidAsync("LM.serial.end"); } catch (JSException) { }
            _io.Release();
            Busy = false;
            Changed?.Invoke();
        }
    }
}
