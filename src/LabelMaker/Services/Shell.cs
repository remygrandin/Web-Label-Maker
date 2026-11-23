using LabelMaker.Models;
using Microsoft.JSInterop;

namespace LabelMaker.Services;

public enum Dialog { None, Library, Templates, Print }

public sealed class AppSettings
{
    /// <summary>"system", "light" or "dark".</summary>
    public string Theme { get; set; } = "system";
    public bool SnapObjects { get; set; } = true;
    public bool SnapMargins { get; set; } = true;
    public bool SnapLabel { get; set; } = true;
    public bool ShowMargins { get; set; } = true;
    /// <summary>"auto", "selection", "label" or "margins".</summary>
    public string AlignTo { get; set; } = "auto";
    public string PrinterModel { get; set; } = "pt-e560bt";
    public List<string> RecentFonts { get; set; } = new();
}

/// <summary>App chrome state: persisted settings, the open dialog, toasts.</summary>
public sealed class Shell
{
    const string Key = "wlm.settings";
    readonly IJSInProcessRuntime _js;
    CancellationTokenSource? _toastCts;

    public Shell(IJSRuntime js)
    {
        _js = (IJSInProcessRuntime)js;
        try
        {
            var raw = _js.Invoke<string?>("LM.getPref", Key);
            if (!string.IsNullOrEmpty(raw)) Settings = DocJson.Read<AppSettings>(raw) ?? new();
        }
        catch { Settings = new(); }
    }

    public AppSettings Settings { get; } = new();
    public Dialog Dialog { get; private set; }
    public string? ToastText { get; private set; }

    /// <summary>Raised when chrome state changes.</summary>
    public event Action? Changed;

    public void SaveSettings()
    {
        _js.InvokeVoid("LM.setPref", Key, DocJson.Write(Settings));
        Changed?.Invoke();
    }

    public void CycleTheme()
    {
        Settings.Theme = Settings.Theme switch { "system" => "light", "light" => "dark", _ => "system" };
        _js.InvokeVoid("LM.setTheme", Settings.Theme);
        SaveSettings();
    }

    public void Open(Dialog d) { Dialog = d; Changed?.Invoke(); }
    public void Close() { Dialog = Dialog.None; Changed?.Invoke(); }

    public async void Toast(string text)
    {
        _toastCts?.Cancel();
        var cts = _toastCts = new CancellationTokenSource();
        ToastText = text;
        Changed?.Invoke();
        try { await Task.Delay(2600, cts.Token); } catch (TaskCanceledException) { return; }
        ToastText = null;
        Changed?.Invoke();
    }
}
