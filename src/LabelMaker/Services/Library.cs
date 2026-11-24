using LabelMaker.Models;
using Microsoft.JSInterop;

namespace LabelMaker.Services;

/// <summary>Listing info for a stored label or template (the document itself is loaded on demand).</summary>
public sealed record LibraryEntry(string Id, string Name, string Modified, double W, double H, string? Thumb);

/// <summary>A picture kept in the browser's image library (a bitmap or SVG as a data URL).</summary>
public sealed class StoredImage
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = "";
    public string Modified { get; set; } = DateTime.UtcNow.ToString("o");
    public double W { get; set; }
    public double H { get; set; }
    public string DataUrl { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public bool IsVector => DataUrl.StartsWith("data:image/svg", StringComparison.Ordinal);
}

/// <summary>An image read from a file or fetched from the icon service.</summary>
public sealed record PickedImage(string DataUrl, double W, double H, string Name);

/// <summary>The on-disk JSON envelope for exports: one label, one template, or a whole archive.</summary>
public sealed class LabelFile
{
    public const string FormatName = "weblabelmaker";
    public string Format { get; set; } = FormatName;
    public int Version { get; set; } = 1;
    /// <summary>"document", "template" or "archive".</summary>
    public string Kind { get; set; } = "document";
    public DateTime Exported { get; set; } = DateTime.UtcNow;
    public LabelDocument? Document { get; set; }
    public List<LabelDocument>? Documents { get; set; }
    public List<LabelDocument>? Templates { get; set; }
    public List<StoredImage>? Images { get; set; }
}

/// <summary>
/// Browser-local persistence (IndexedDB): the autosaved working label, the named
/// library, user templates, and JSON import/export of any of them.
/// </summary>
public sealed class Library
{
    public const string Docs = "docs", Templates = "templates";

    sealed class WorkingState
    {
        public LabelDocument Document { get; set; } = new();
        public bool Dirty { get; set; }
    }

    readonly IJSRuntime _js;
    readonly Editor _ed;
    readonly Shell _shell;
    CancellationTokenSource? _debounce;

    public Library(IJSRuntime js, Editor ed, Shell shell)
    {
        _js = js; _ed = ed; _shell = shell;
        _ed.Mutated += ScheduleAutosave;
        _ed.SaveRequested += () => _ = Save();
    }

    /// <summary>The working label also lives in the library, so autosave keeps that copy current.</summary>
    public bool InLibrary { get; private set; }
    public DateTime? SavedAt { get; private set; }
    public bool Pending { get; private set; }
    /// <summary>Raised when the autosave state changes.</summary>
    public event Action? StateChanged;

    /// <summary>Raised when stored labels or templates change.</summary>
    public event Action? Changed;

    /// <summary>Why the last save did not go through, in words for the user; null when saving works.</summary>
    public string? SaveError { get; private set; }

    const string BlockedText = "Not saved: another WebLabelMaker tab is still running an older version. Close or reload the other tabs.";
    bool _restored;

    /// <summary>
    /// Wait for a storage call. If it stalls, say why: an older copy of the app open in
    /// another tab keeps the database from being upgraded, and the call only finishes once that tab is gone.
    /// </summary>
    async Task<T> Stored<T>(ValueTask<T> call)
    {
        var task = call.AsTask();
        if (await Task.WhenAny(task, Task.Delay(4000)) != task)
        {
            bool blocked = false;
            try { blocked = await _js.InvokeAsync<bool>("LM.db.isBlocked"); } catch { /* keep waiting */ }
            SaveError = blocked ? BlockedText : "Saving is taking longer than usual…";
            StateChanged?.Invoke();
            if (blocked) _shell.Toast(BlockedText);
        }
        return await task;
    }

    // ---- working copy -------------------------------------------------------------------

    /// <summary>Reopen whatever was on the bench last time.</summary>
    public async Task Restore()
    {
        try
        {
            var json = await Stored(_js.InvokeAsync<string?>("LM.db.get", "meta", "autosave"));
            if (json is not null && DocJson.Read<WorkingState>(json) is { } state)
            {
                InLibrary = await _js.InvokeAsync<string?>("LM.db.get", Docs, state.Document.Id) is not null;
                await _ed.Load(state.Document, state.Dirty && !InLibrary);
                SavedAt = DateTime.Now;
                SaveError = null;
                _restored = true;
                StateChanged?.Invoke();
                return;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"Autosave restore failed: {ex.Message}"); }
        SaveError = null;
        _restored = true;
        await _ed.Load(BuiltInTemplates.Starter());
    }

    async void ScheduleAutosave()
    {
        // until the last session has been read back, saving would overwrite it
        if (!_restored) return;
        _debounce?.Cancel();
        if (!Pending) { Pending = true; StateChanged?.Invoke(); }
        var cts = _debounce = new CancellationTokenSource();
        try { await Task.Delay(600, cts.Token); } catch (TaskCanceledException) { return; }
        await Autosave();
    }

    async Task Autosave()
    {
        try
        {
            var json = DocJson.Write(new WorkingState { Document = Pack(_ed.Doc), Dirty = _ed.Dirty });
            await Stored(_js.InvokeAsync<bool>("LM.db.put", "meta", new { id = "autosave", json }));
            if (InLibrary)
            {
                await Put(Docs, _ed.Doc);
                if (_ed.Dirty) { _ed.Dirty = false; _ed.Refresh(); }
            }
            SavedAt = DateTime.Now;
            SaveError = null;
            Pending = false;
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Autosave failed: {ex.Message}");
            SaveError = $"Not saved: {ex.Message}";
            Pending = false;
            StateChanged?.Invoke();
        }
    }

    /// <summary>A copy of the document carrying only the image assets it still uses.</summary>
    static LabelDocument Pack(LabelDocument d)
    {
        var used = Geo.Walk(d.Nodes).OfType<ImageNode>().Select(i => i.AssetId).ToHashSet();
        var copy = d.ShallowCopy();
        copy.Assets = d.Assets.Where(a => used.Contains(a.Key)).ToDictionary(a => a.Key, a => a.Value);
        return copy;
    }

    // ---- library ---------------------------------------------------------------------------

    public async Task<List<LibraryEntry>> List(string store) =>
        await _js.InvokeAsync<List<LibraryEntry>>("LM.db.list", store) ?? new();

    async Task Put(string store, LabelDocument doc)
    {
        var packed = Pack(doc);
        string? thumb = null;
        try { thumb = await _ed.Thumbnail(doc); } catch { /* a missing thumbnail is not worth failing a save */ }
        await _js.InvokeVoidAsync("LM.db.put", store, new
        {
            id = packed.Id, name = packed.Name, modified = packed.Modified.ToString("o"),
            w = packed.WidthMm, h = packed.HeightMm, thumb, json = DocJson.Write(packed),
        });
    }

    public async Task<LabelDocument?> Get(string store, string id)
    {
        var json = await _js.InvokeAsync<string?>("LM.db.get", store, id);
        return json is null ? null : DocJson.Read<LabelDocument>(json);
    }

    public async Task Delete(string store, string id)
    {
        await _js.InvokeVoidAsync("LM.db.del", store, id);
        if (store == Docs && id == _ed.Doc.Id) { InLibrary = false; StateChanged?.Invoke(); }
        Changed?.Invoke();
    }

    /// <summary>Save the working label into the library under its current name.</summary>
    public async Task Save(bool quiet = false)
    {
        InLibrary = true;
        await Put(Docs, _ed.Doc);
        _ed.Dirty = false;
        await Autosave();
        _ed.Refresh();
        Changed?.Invoke();
        if (!quiet) _shell.Toast($"Saved “{_ed.Doc.Name}” to the library.");
    }

    public async Task SaveCopy()
    {
        _ed.Doc.Id = Ids.New();
        _ed.Doc.Name = NextName(_ed.Doc.Name, (await List(Docs)).Select(e => e.Name));
        await Save();
    }

    public async Task SaveAsTemplate()
    {
        var copy = DocJson.Clone(Pack(_ed.Doc));
        copy.Id = Ids.New();
        copy.Modified = DateTime.UtcNow;
        await Put(Templates, copy);
        Changed?.Invoke();
        _shell.Toast($"Saved “{copy.Name}” as a template.");
    }

    static string NextName(string name, IEnumerable<string> taken)
    {
        var set = taken.ToHashSet();
        var stem = System.Text.RegularExpressions.Regex.Replace(name, @"\s\(\d+\)$", "");
        for (int i = 2; ; i++)
        {
            var candidate = $"{stem} ({i})";
            if (!set.Contains(candidate)) return candidate;
        }
    }

    /// <summary>Unsaved work is never dropped: it is filed in the library before the bench is cleared.</summary>
    async Task KeepCurrent()
    {
        if (_ed.Dirty && _ed.Doc.Nodes.Count > 0)
        {
            await Save(quiet: true);
            _shell.Toast($"“{_ed.Doc.Name}” was saved to the library first.");
        }
    }

    public async Task Open(string id)
    {
        if (id == _ed.Doc.Id) return;
        if (await Get(Docs, id) is not { } doc) return;
        await KeepCurrent();
        InLibrary = true;
        await _ed.Load(doc);
        await Autosave();
    }

    public async Task New(LabelDocument? template = null)
    {
        await KeepCurrent();
        LabelDocument doc;
        if (template is null) doc = Editor.NewBlank(_ed.Doc.MediaId);
        else
        {
            doc = DocJson.Clone(template);
            doc.Id = Ids.New();
            doc.Nodes = DocJson.CloneNodes(doc.Nodes);
        }
        doc.Modified = DateTime.UtcNow;
        InLibrary = false;
        await _ed.Load(doc);
        await Autosave();
    }

    // ---- image library -----------------------------------------------------------------------

    public const string Images = "images";

    /// <summary>Raised when the stored images change.</summary>
    public event Action? ImagesChanged;

    public async Task<List<StoredImage>> ListImages() =>
        await _js.InvokeAsync<List<StoredImage>>("LM.db.list", Images) ?? new();

    ValueTask PutImage(StoredImage i) => _js.InvokeVoidAsync("LM.db.put", Images, new
    {
        id = i.Id, name = i.Name, modified = i.Modified, w = i.W, h = i.H, dataUrl = i.DataUrl,
    });

    public async Task AddImage(string name, string dataUrl, double w, double h)
    {
        // the same picture is kept once
        if ((await ListImages()).Any(i => i.DataUrl == dataUrl)) return;
        var stem = System.IO.Path.GetFileNameWithoutExtension(name);
        await PutImage(new StoredImage { Name = stem.Length > 0 ? stem : "Image", DataUrl = dataUrl, W = w, H = h });
        ImagesChanged?.Invoke();
    }

    /// <summary>Pick one or more files and keep them; returns how many were added.</summary>
    public async Task<int> AddImageFiles()
    {
        var picked = await _js.InvokeAsync<List<PickedImage>>("LM.pickImages") ?? new();
        foreach (var p in picked) await AddImage(p.Name, p.DataUrl, p.W, p.H);
        return picked.Count;
    }

    public async Task RenameImage(StoredImage image, string name)
    {
        image.Name = name.Trim().Length > 0 ? name.Trim() : image.Name;
        await PutImage(image);
        ImagesChanged?.Invoke();
    }

    public async Task DeleteImage(string id)
    {
        await _js.InvokeVoidAsync("LM.db.del", Images, id);
        ImagesChanged?.Invoke();
    }

    // ---- import / export ---------------------------------------------------------------------

    static string FileName(string name, string suffix)
    {
        var safe = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        return $"{(safe.Length == 0 ? "label" : safe)}.{suffix}";
    }

    ValueTask Download(string name, LabelFile file) => _js.InvokeVoidAsync("LM.download", name, DocJson.Write(file, pretty: true), "application/json");

    public ValueTask ExportCurrent() =>
        Download(FileName(_ed.Doc.Name, "label.json"), new LabelFile { Kind = "document", Document = Pack(_ed.Doc) });

    public async Task ExportEntry(string store, string id)
    {
        if (await Get(store, id) is not { } doc) return;
        bool template = store == Templates;
        await Download(FileName(doc.Name, template ? "template.json" : "label.json"),
            new LabelFile { Kind = template ? "template" : "document", Document = doc });
    }

    async Task<List<LabelDocument>> All(string store)
    {
        var raw = await _js.InvokeAsync<List<string>>("LM.db.all", store) ?? new();
        return raw.Select(j => DocJson.Read<LabelDocument>(j)).Where(d => d is not null).Cast<LabelDocument>().ToList();
    }

    /// <summary>Everything stored locally (labels and user templates) in one file.</summary>
    public async Task ExportArchive()
    {
        if (_ed.Dirty && _ed.Doc.Nodes.Count > 0) await Save(quiet: true);
        var file = new LabelFile { Kind = "archive", Documents = await All(Docs), Templates = await All(Templates), Images = await ListImages() };
        await Download($"weblabelmaker-archive-{DateTime.Now:yyyy-MM-dd}.json", file);
        _shell.Toast($"Exported {file.Documents.Count} labels, {file.Templates.Count} templates and {file.Images.Count} images.");
    }

    public async Task Import()
    {
        var picked = await _js.InvokeAsync<string[]?>("LM.pickText", ".json,application/json");
        if (picked is null) return;
        try
        {
            var file = DocJson.Read<LabelFile>(picked[1]);
            if (file is null || file.Format != LabelFile.FormatName) throw new FormatException("not a WebLabelMaker file");
            if (file.Kind == "archive")
            {
                foreach (var d in file.Documents ?? new()) await Put(Docs, d);
                foreach (var t in file.Templates ?? new()) await Put(Templates, t);
                foreach (var i in file.Images ?? new()) await PutImage(i);
                ImagesChanged?.Invoke();
                _shell.Toast($"Imported {file.Documents?.Count ?? 0} labels, {file.Templates?.Count ?? 0} templates and {file.Images?.Count ?? 0} images.");
            }
            else if (file.Document is { } doc)
            {
                if (file.Kind == "template")
                {
                    await Put(Templates, doc);
                    _shell.Toast($"Imported template “{doc.Name}”.");
                }
                else
                {
                    await Put(Docs, doc);
                    Changed?.Invoke();
                    if (doc.Id != _ed.Doc.Id) await Open(doc.Id); else { InLibrary = true; await _ed.Load(doc); }
                    _shell.Toast($"Imported “{doc.Name}”.");
                }
            }
            else throw new FormatException("the file holds no label");
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _shell.Toast($"Could not import {picked[0]}: {ex.Message}");
        }
    }

    public async Task ExportPng()
    {
        var url = await _js.InvokeAsync<string>("LM.toPng", _ed.BuildFrame(_ed.Doc), true, 0);
        await _js.InvokeVoidAsync("LM.downloadUrl", FileName(_ed.Doc.Name, "png"), url);
    }
}
