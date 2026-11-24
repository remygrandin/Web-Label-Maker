using LabelMaker.Models;
using Microsoft.JSInterop;

namespace LabelMaker.Services;

public enum Dialog { None, Library, Templates, Print, Images, Icons }

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
    /// <summary>Icon search: leave out multi-colour sets, which only print as dither.</summary>
    public bool IconsMono { get; set; } = true;
    /// <summary>Icons placed on a label are also kept in the image library.</summary>
    public bool KeepIcons { get; set; }
    /// <summary>Reopen the last printer port at start-up; switched off by a deliberate disconnect.</summary>
    public bool AutoConnect { get; set; } = true;
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

    /// <summary>A question waiting for the user's answer.</summary>
    public sealed record Question(string Title, string Text, string[] Options, TaskCompletionSource<int> Done);

    public Question? Asking { get; private set; }

    /// <summary>Ask a question with a button per option; returns the chosen index, or -1 when dismissed.</summary>
    public Task<int> Ask(string title, string text, params string[] options)
    {
        Asking?.Done.TrySetResult(-1);
        Asking = new Question(title, text, options, new TaskCompletionSource<int>());
        Changed?.Invoke();
        return Asking.Done.Task;
    }

    public void Answer(int option)
    {
        var q = Asking;
        Asking = null;
        Changed?.Invoke();
        q?.Done.TrySetResult(option);
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
