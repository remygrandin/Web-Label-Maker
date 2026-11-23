using LabelMaker.Models;
using Microsoft.JSInterop;

namespace LabelMaker.Services;

/// <summary>What the canvas renderer needs for one frame.</summary>
public sealed class RenderFrame
{
    public double Zoom { get; set; }
    public double PanX { get; set; }
    public double PanY { get; set; }
    public double DocW { get; set; }
    public double DocH { get; set; }
    public double Dpi { get; set; }
    public Margins Margins { get; set; } = new();
    public bool ShowMargins { get; set; }
    public List<Node> Items { get; set; } = new();
    public List<Frame> Sel { get; set; } = new();
    public Frame? Scope { get; set; }
    public Frame? Hover { get; set; }
    public List<GuideLine> Guides { get; set; } = new();
    public List<HandleDot> Handles { get; set; } = new();
    public double[]? Marquee { get; set; }
    public List<CutMark> Cuts { get; set; } = new();
    /// <summary>Layout guide lines (x positions, mm) and labelled zones; shown, never printed.</summary>
    public List<double> Dividers { get; set; } = new();
    public List<LayoutZone> Zones { get; set; } = new();
    /// <summary>Tape and print colours as hex, for the preview.</summary>
    public string Tape { get; set; } = "#ffffff";
    public string Ink { get; set; } = "#000000";
}

public sealed record GuideLine(bool V, double Pos);
public sealed record HandleDot(double X, double Y, bool Round);

/// <summary>
/// The editing session: the open document, selection, view, undo history and
/// every operation the UI can perform on them.
/// </summary>
public sealed partial class Editor
{
    readonly IJSInProcessRuntime _js;
    readonly Shell _shell;
    readonly HashSet<string> _sentAssets = new();
    readonly Stack<string> _undo = new();
    readonly Stack<string> _redo = new();
    string? _coalesceKey;
    DateTime _coalesceAt;
    bool _fitPending = true;

    public Editor(IJSRuntime js, Shell shell)
    {
        _js = (IJSInProcessRuntime)js;
        _shell = shell;
        Doc = NewBlank();
    }

    public LabelDocument Doc { get; private set; }
    public List<string> Selection { get; } = new();
    /// <summary>The group the user has "entered" by double-clicking; null = top level.</summary>
    public string? ScopeId { get; private set; }
    public double Zoom { get; private set; } = 8;
    public double PanX { get; private set; } = 60;
    public double PanY { get; private set; } = 60;
    public double CanvasW { get; private set; }
    public double CanvasH { get; private set; }
    public bool CanvasReady { get; private set; }
    public bool Dirty { get; set; }
    public (double X, double Y)? Cursor { get; private set; }

    AppSettings Settings => _shell.Settings;

    /// <summary>Selection, view or document changed: panels should re-render.</summary>
    public event Action? Changed;
    /// <summary>Document content changed: time to autosave.</summary>
    public event Action? Mutated;
    public event Action? SaveRequested;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public static LabelDocument NewBlank(string mediaId = "tze-12", double length = 60)
    {
        var m = Media.Get(mediaId);
        return new LabelDocument { MediaId = m.Id, HeightMm = m.WidthMm, WidthMm = length, Dpi = m.Id == "tze-36" ? 360 : 180, Margins = Media.DefaultMargins(m) };
    }

    // ---- tree access ---------------------------------------------------------------

    public IEnumerable<Node> All() => Geo.Walk(Doc.Nodes);
    public Node? Find(string? id) => id is null ? null : All().FirstOrDefault(n => n.Id == id);
    public IEnumerable<Node> SelectedNodes => Selection.Select(Find).Where(n => n is not null)!;
    public Node? Single => Selection.Count == 1 ? Find(Selection[0]) : null;

    /// <summary>The list that holds the node, and the group owning that list (null at top level).</summary>
    public (List<Node> List, GroupNode? Parent) Locate(Node node)
    {
        (List<Node>, GroupNode?)? Search(List<Node> list, GroupNode? parent)
        {
            if (list.Contains(node)) return (list, parent);
            foreach (var g in list.OfType<GroupNode>())
                if (Search(g.Children, g) is { } hit) return hit;
            return null;
        }
        return Search(Doc.Nodes, null) ?? (Doc.Nodes, null);
    }

    /// <summary>Ancestors from the top level down to the node itself.</summary>
    List<Node> PathTo(Node target)
    {
        var path = new List<Node>();
        bool Search(List<Node> list)
        {
            foreach (var n in list)
            {
                path.Add(n);
                if (n == target || (n is GroupNode g && Search(g.Children))) return true;
                path.RemoveAt(path.Count - 1);
            }
            return false;
        }
        Search(Doc.Nodes);
        return path;
    }

    List<Node> ScopeList => Find(ScopeId) is GroupNode g ? g.Children : Doc.Nodes;

    /// <summary>Visible leaves back-to-front, with the inherited lock state.</summary>
    IEnumerable<(Node Node, bool Locked)> VisibleLeaves(List<Node>? list = null, bool locked = false)
    {
        foreach (var n in list ?? Doc.Nodes)
        {
            if (!n.Visible) continue;
            if (n is GroupNode g)
                foreach (var c in VisibleLeaves(g.Children, locked || g.Locked)) yield return c;
            else yield return (n, locked || n.Locked);
        }
    }

    public static string DisplayName(Node n)
    {
        if (!string.IsNullOrWhiteSpace(n.Name)) return n.Name;
        return n switch
        {
            TextNode t => string.IsNullOrWhiteSpace(t.Text) ? "Text" : t.Text.Split('\n')[0],
            ShapeNode s => s.Kind == ShapeKind.Rect ? "Rectangle" : s.Kind.ToString(),
            ImageNode => "Image",
            BarcodeNode b => $"{CodeNames.Of(b.Format)} · {b.Value}",
            TableNode tb => $"Table {tb.Rows}×{tb.Cols}",
            GroupNode => "Group",
            _ => "Object",
        };
    }

    // ---- document lifecycle ----------------------------------------------------------

    public async Task Load(LabelDocument doc, bool dirty = false)
    {
        Doc = doc;
        Selection.Clear();
        ScopeId = null;
        _undo.Clear();
        _redo.Clear();
        Dirty = dirty;
        _fitPending = true;
        await EnsureFonts(doc);
        Sync();
        if (CanvasReady) ZoomToFit(); else Changed?.Invoke();
    }

    public async Task EnsureFonts(LabelDocument doc)
    {
        var families = Fonts(Geo.Walk(doc.Nodes)).ToList();
        await Task.WhenAll(families.Select(f => LoadFont(f).AsTask()));
    }

    /// <summary>Font families the given objects use.</summary>
    public static IEnumerable<string> Fonts(IEnumerable<Node> nodes) =>
        nodes.Select(n => n switch { TextNode t => t.FontFamily, TableNode tb => tb.FontFamily, _ => null }).OfType<string>().Distinct();

    public ValueTask<bool> LoadFont(string family) => _js.InvokeAsync<bool>("LM.loadFont", family);

    /// <summary>Bring derived state in line with the model: text boxes, renderer caches, auto length.</summary>
    public void Sync() => Prepare(Doc);

    /// <summary>Push whatever the renderer needs for this document (also used for thumbnails).</summary>
    public void Prepare(LabelDocument doc)
    {
        _evalDoc = doc;
        try
        {
            ApplyBindings(doc);
            foreach (var n in Geo.Walk(doc.Nodes))
            {
                switch (n)
                {
                    case TextNode { Mode: TextMode.Auto } t: Remeasure(t); break;
                    case BarcodeNode code:
                    {
                        var shown = (BarcodeNode)Resolved(code);
                        Barcodes.Ensure(shown, _js);
                        FitCodeBox(code, shown, doc.Dpi);
                        break;
                    }
                }
            }
        }
        finally { _evalDoc = null; }
        foreach (var (id, url) in doc.Assets)
            if (_sentAssets.Add(id)) _js.InvokeVoid("LM.setAsset", id, url);
        if (LayoutWidth(doc) is { } fixedWidth) doc.WidthMm = Math.Max(5, Math.Round(fixedWidth, 1));
        else if (doc.AutoWidth && Geo.Bounds(doc.Nodes.Where(n => n.Visible)) is { } b)
            doc.WidthMm = Math.Max(8, Math.Round(b.R + doc.Margins.R, 1));
    }

    void Remeasure(TextNode t)
    {
        var m = _js.Invoke<double[]>("LM.measureText", (TextNode)Resolved(t));
        double nw = Math.Max(m[0], 0.5) + 2 * t.Padding, nh = Math.Max(m[1], 0.5) + 2 * t.Padding;
        double dw = nw - t.W, dh = nh - t.H;
        if (Math.Abs(dw) < 1e-4 && Math.Abs(dh) < 1e-4) return;
        // grow away from the anchored edge so aligned text stays put
        double lx = t.Align switch { HAlign.Left => dw / 2, HAlign.Right => -dw / 2, _ => 0 };
        var (sx, sy) = Geo.Rot(lx, dh / 2, t.Rotation);
        double cx = t.Cx + sx, cy = t.Cy + sy;
        t.W = nw; t.H = nh; t.X = cx - nw / 2; t.Y = cy - nh / 2;
    }

    // ---- undo ------------------------------------------------------------------------

    sealed class DocState
    {
        public string MediaId { get; set; } = "";
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
        public bool AutoWidth { get; set; }
        public double Dpi { get; set; }
        public Margins Margins { get; set; } = new();
        public List<Node> Nodes { get; set; } = new();
        public List<Variable> Variables { get; set; } = new();
        public Counter Counter { get; set; } = new();
        public List<CutMark> Cuts { get; set; } = new();
        public string TapeColor { get; set; } = "white";
        public string InkColor { get; set; } = "black";
        public LabelLayout Layout { get; set; } = new();
    }

    string Snapshot() => DocJson.Write(new DocState
    {
        MediaId = Doc.MediaId, WidthMm = Doc.WidthMm, HeightMm = Doc.HeightMm, AutoWidth = Doc.AutoWidth,
        Dpi = Doc.Dpi, Margins = Doc.Margins, Nodes = Doc.Nodes, Variables = Doc.Variables, Counter = Doc.Counter, Cuts = Doc.Cuts,
        TapeColor = Doc.TapeColor, InkColor = Doc.InkColor, Layout = Doc.Layout,
    });

    void Restore(string snapshot)
    {
        var s = DocJson.Read<DocState>(snapshot)!;
        Doc.MediaId = s.MediaId; Doc.WidthMm = s.WidthMm; Doc.HeightMm = s.HeightMm; Doc.AutoWidth = s.AutoWidth;
        Doc.Dpi = s.Dpi; Doc.Margins = s.Margins; Doc.Nodes = s.Nodes;
        Doc.Variables = s.Variables; Doc.Counter = s.Counter; Doc.Cuts = s.Cuts;
        Doc.TapeColor = s.TapeColor; Doc.InkColor = s.InkColor; Doc.Layout = s.Layout;
        Selection.RemoveAll(id => Find(id) is null);
        if (Find(ScopeId) is null) ScopeId = null;
    }

    void PushUndo(string? coalesce = null)
    {
        var now = DateTime.UtcNow;
        bool merge = coalesce is not null && coalesce == _coalesceKey && (now - _coalesceAt).TotalSeconds < 1.2;
        _coalesceKey = coalesce;
        _coalesceAt = now;
        if (merge) return;
        _undo.Push(Snapshot());
        _redo.Clear();
        if (_undo.Count > 120)
        {
            var keep = _undo.Reverse().Skip(_undo.Count - 100).ToList();
            _undo.Clear();
            keep.ForEach(_undo.Push);
        }
    }

    public void Undo() => Step(_undo, _redo);
    public void Redo() => Step(_redo, _undo);

    void Step(Stack<string> from, Stack<string> to)
    {
        if (from.Count == 0) return;
        to.Push(Snapshot());
        Restore(from.Pop());
        _coalesceKey = null;
        Touch();
    }

    /// <summary>
    /// Run a document change as one undo step. Consecutive calls sharing a
    /// <paramref name="coalesce"/> key (slider drags, nudges) collapse into one step.
    /// </summary>
    public void Mutate(Action change, string? coalesce = null)
    {
        PushUndo(coalesce);
        change();
        Touch();
    }

    /// <summary>The document changed: re-derive, mark dirty, autosave, repaint.</summary>
    public void Touch()
    {
        Sync();
        Doc.Modified = DateTime.UtcNow;
        Dirty = true;
        Mutated?.Invoke();
        Refresh();
    }

    /// <summary>Only the view or selection changed.</summary>
    public void Refresh()
    {
        Render();
        Changed?.Invoke();
    }

    // ---- view ------------------------------------------------------------------------

    const double Ruler = 18;

    public void CanvasResized(double w, double h)
    {
        CanvasW = w; CanvasH = h;
        CanvasReady = w > 0 && h > 0;
        if (CanvasReady && _fitPending) ZoomToFit(); else Render();
    }

    public void ZoomToFit()
    {
        if (!CanvasReady) { _fitPending = true; return; }
        _fitPending = false;
        double aw = CanvasW - Ruler - 96, ah = CanvasH - Ruler - 96;
        Zoom = Math.Clamp(Math.Min(aw / Doc.WidthMm, ah / Doc.HeightMm), 1, 28);
        PanX = Ruler + (CanvasW - Ruler - Doc.WidthMm * Zoom) / 2;
        PanY = Ruler + (CanvasH - Ruler - Doc.HeightMm * Zoom) / 2;
        Refresh();
    }

    public void ZoomAt(double sx, double sy, double factor)
    {
        double z = Math.Clamp(Zoom * factor, 0.8, 80);
        double wx = (sx - PanX) / Zoom, wy = (sy - PanY) / Zoom;
        Zoom = z;
        PanX = sx - wx * z;
        PanY = sy - wy * z;
        Refresh();
    }

    public void ZoomBy(double factor) => ZoomAt(CanvasW / 2, CanvasH / 2, factor);

    (double X, double Y) ToWorld(double sx, double sy) => ((sx - PanX) / Zoom, (sy - PanY) / Zoom);

    // ---- rendering -------------------------------------------------------------------

    public void Render()
    {
        if (!CanvasReady) return;
        var f = BuildFrame(Doc);
        f.Zoom = Zoom; f.PanX = PanX; f.PanY = PanY;
        f.ShowMargins = Settings.ShowMargins;
        foreach (var n in SelectedNodes)
        {
            if (n is GroupNode) { if (Geo.Bounds(n) is { } b) f.Sel.Add(new Frame(b.Cx, b.Cy, b.W, b.H, 0)); }
            else f.Sel.Add(new Frame(n.Cx, n.Cy, n.W, n.H, n.Rotation));
        }
        if (Selection.Count > 1 && SelFrame() is { } sf) f.Sel.Add(sf);
        if (Find(ScopeId) is { } scope && Geo.Bounds(scope) is { } sb)
            f.Scope = new Frame(sb.Cx, sb.Cy, sb.W + 6 / Zoom, sb.H + 6 / Zoom, 0);
        if (_drag == Drag.None && Find(_hoverId) is { } hov && !Selection.Contains(hov.Id) && Geo.Bounds(hov) is { } hb)
            f.Hover = hov is GroupNode ? new Frame(hb.Cx, hb.Cy, hb.W, hb.H, 0) : new Frame(hov.Cx, hov.Cy, hov.W, hov.H, hov.Rotation);
        if (EditingId is not null) f.Items.RemoveAll(n => n.Id == EditingId);
        f.Guides = _guides;
        if (_drag is Drag.None or Drag.Resize or Drag.Rotate or Drag.LineEnd)
            f.Handles = Handles().Select(h => new HandleDot(h.Sx, h.Sy, h.Kind != HandleKind.Resize)).ToList();
        f.Marquee = _marquee;
        _js.InvokeVoid("LM.render", f);
    }

    /// <summary>A frame holding just the document content (no editor overlays).</summary>
    public RenderFrame BuildFrame(LabelDocument doc)
    {
        _evalDoc = doc;
        try
        {
            var items = (doc == Doc ? VisibleLeaves() : VisibleLeavesOf(doc.Nodes)).Select(l => Resolved(l.Node)).ToList();
            items.AddRange(LayoutCopies(doc, items).ToList());
            return new RenderFrame
            {
                Zoom = 1, DocW = doc.WidthMm, DocH = doc.HeightMm, Dpi = doc.Dpi, Margins = doc.Margins, Cuts = doc.Cuts,
                Items = items, Dividers = Dividers(doc), Zones = Zones(doc),
                Tape = Media.Hex(Media.TapeColors, doc.TapeColor), Ink = Media.Hex(Media.InkColors, doc.InkColor),
            };
        }
        finally { _evalDoc = null; }
    }

    static IEnumerable<(Node Node, bool Locked)> VisibleLeavesOf(List<Node> list)
    {
        foreach (var n in list)
        {
            if (!n.Visible) continue;
            if (n is GroupNode g) foreach (var c in VisibleLeavesOf(g.Children)) yield return c;
            else yield return (n, false);
        }
    }

    public async Task<string> Thumbnail(LabelDocument doc)
    {
        await EnsureFonts(doc);
        Prepare(doc);
        return await _js.InvokeAsync<string>("LM.toPng", BuildFrame(doc), false, 480);
    }
}

public static class CodeNames
{
    public static string Of(CodeFormat f) => f switch
    {
        CodeFormat.QrCode => "QR Code",
        CodeFormat.DataMatrix => "Data Matrix",
        CodeFormat.Aztec => "Aztec",
        CodeFormat.Pdf417 => "PDF417",
        CodeFormat.Code128 => "Code 128",
        CodeFormat.Code39 => "Code 39",
        CodeFormat.Code93 => "Code 93",
        CodeFormat.Ean13 => "EAN-13",
        CodeFormat.Ean8 => "EAN-8",
        CodeFormat.UpcA => "UPC-A",
        CodeFormat.Itf => "ITF",
        CodeFormat.Codabar => "Codabar",
        _ => f.ToString(),
    };
}
