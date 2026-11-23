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

    public async Task Init()
    {
        Supported = await _js.InvokeAsync<bool>("LM.serial.supported");
        if (!Supported) return;
        Connected = await _js.InvokeAsync<bool>("LM.serial.connected");
        if (!Connected && await _js.InvokeAsync<string?>("LM.serial.reconnect") is { } info)
        {
            Connected = true;
            PortInfo = info;
            Say($"Reconnected to {info}.");
            await Query();
        }
        Changed?.Invoke();
    }

    /// <summary>Opens the browser's port picker; must run inside a click handler.</summary>
    public async Task Connect()
    {
        try
        {
            PortInfo = await _js.InvokeAsync<string>("LM.serial.connect");
            Connected = true;
            Say($"Connected to {PortInfo}.");
            await Query();
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
        await _js.InvokeVoidAsync("LM.serial.close");
        Connected = false;
        Status = null;
        Say("Disconnected.");
    }

    static string FirstLine(string s) => s.Split('\n')[0];

    /// <summary>Reset the printer and ask what is loaded. Returns null when it does not answer.</summary>
    public async Task<PrinterStatus?> Query()
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
                    Status = new PrinterStatus { Raw = raw };
                    Say($"Printer reports {Status.MediaWidth} tape ({Status.ModelName})" +
                        (Status.Errors.Count > 0 ? $" — {string.Join(", ", Status.Errors)}" : "."));
                    Changed?.Invoke();
                    return Status;
                }
            }
            Status = null;
            Say("The printer did not answer the status request.");
        }
        catch (JSException ex)
        {
            Status = null;
            Connected = await _js.InvokeAsync<bool>("LM.serial.connected");
            Say($"Status request failed: {FirstLine(ex.Message)}");
        }
        Changed?.Invoke();
        return null;
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
            var status = await Query();
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
            Busy = false;
            Changed?.Invoke();
        }
    }
}
