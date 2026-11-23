using LabelMaker.Services;
using Microsoft.AspNetCore.Components;

namespace LabelMaker.Components;

/// <summary>Base for panels that redraw whenever the editor or the app chrome changes.</summary>
public abstract class EditorComponent : ComponentBase, IDisposable
{
    [Inject] protected Editor Ed { get; set; } = default!;
    [Inject] protected Shell Shell { get; set; } = default!;

    protected override void OnInitialized()
    {
        Ed.Changed += Update;
        Shell.Changed += Update;
    }

    protected void Update() => InvokeAsync(StateHasChanged);

    public virtual void Dispose()
    {
        Ed.Changed -= Update;
        Shell.Changed -= Update;
    }
}
