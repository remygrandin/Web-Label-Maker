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

    public async Task<string?> Preview()
    {
        if (Geometry is not { } g) return null;
        var f = Frame();
        int w = (int)Math.Round(f.DocW * f.Dpi / 25.4), h = (int)Math.Round(f.DocH * f.Dpi / 25.4);
        var win = Brother.PrintWindow(Model, g, Options, w, h);
        return await _js.InvokeAsync<string>("LM.printPreview", f, win.X, win.Y, win.W, win.H);
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

            var raster = await _js.InvokeAsync<byte[]>("LM.rasterize", Frame());
            int w = BitConverter.ToInt32(raster, 0), h = BitConverter.ToInt32(raster, 4);
            var job = Brother.BuildJob(Model, Media, g, Options, raster[8..], w, h);
            Say($"Sending {job.Length:N0} bytes ({Math.Max(0, w - 2 * Brother.FeedDots(Model, Options))} raster lines × {Math.Clamp(Options.Copies, 1, 99)}).");
            await _js.InvokeVoidAsync("LM.serial.write", job);

            // The printer announces phase changes, then "printing completed" per page.
            int done = 0, copies = Math.Clamp(Options.Copies, 1, 99);
            var deadline = DateTime.UtcNow.AddSeconds(30 + 20 * copies);
            while (done < copies && DateTime.UtcNow < deadline)
            {
                var raw = await _js.InvokeAsync<byte[]?>("LM.serial.readStatus", 2000);
                if (raw is not { Length: 32 }) continue;
                var s = new PrinterStatus { Raw = raw };
                switch (s.StatusType)
                {
                    case 0x01: done++; Say(copies > 1 ? $"Printed {done} of {copies}." : "Printing completed."); break;
                    case 0x02:
                        Status = s;
                        Say($"Printer error: {(s.Errors.Count > 0 ? string.Join(", ", s.Errors) : "unspecified")}.");
                        await _js.InvokeVoidAsync("LM.serial.write", Brother.Preamble(Model)[..^3]); // invalidate + initialise
                        return;
                    case 0x04: Say("The printer was switched off."); return;
                    case 0x06 when s.Phase == 1: Say("Printing…"); break;
                }
            }
            if (done < copies) Say("No completion message from the printer; it may still have printed.");
            else _shell.Toast(copies > 1 ? $"Printed {copies} labels." : "Label printed.");
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
