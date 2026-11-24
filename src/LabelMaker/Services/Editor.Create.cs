using LabelMaker.Models;

namespace LabelMaker.Services;

// Creation tools: pick one in the rail, then drag on the canvas (or click for a default size).
public sealed partial class Editor
{
    /// <summary>
    /// The armed creation tool, or null for the selection tool. One of "text",
    /// "shape:Kind[:preset]", "code:Format", "table:rows:cols", "image", "cut:Half", "cut:Full".
    /// </summary>
    public string? Tool { get; private set; }

    (string AssetId, double Aspect)? _pendingImage;

    public void SetTool(string? tool)
    {
        Tool = tool == Tool ? null : tool;
        if (Tool != "image") _pendingImage = null;
        Selection.Clear();
        Refresh();
    }

    /// <summary>Arm the image tool with a picked file; the next drag places it.</summary>
    public void SetImageTool(string dataUrl, double pxW, double pxH)
    {
        _pendingImage = (AddAsset(dataUrl), pxW / Math.Max(1, pxH));
        Tool = "image";
        Selection.Clear();
        Sync();
        Refresh();
    }

    /// <summary>Vector art (SVG, icons) is line work: print it solid rather than dithered.</summary>
    static DitherMode DitherFor(string? dataUrl) =>
        dataUrl?.StartsWith("data:image/svg", StringComparison.Ordinal) == true ? DitherMode.Threshold : DitherMode.FloydSteinberg;

    /// <summary>Aspect ratio (w/h) the armed tool is locked to, if any.</summary>
    double? ToolAspect => Tool switch
    {
        "image" => _pendingImage?.Aspect,
        "code:QrCode" or "code:DataMatrix" or "code:Aztec" => 1,
        _ => null,
    };

    /// <summary>A new object for the armed tool at its default size.</summary>
    Node? MakeNode()
    {
        var parts = (Tool ?? "").Split(':');
        double h = Math.Max(2, Math.Round(UsableHeight * 0.8, 1));
        switch (parts[0])
        {
            case "text":
            {
                var family = Settings.RecentFonts.FirstOrDefault() ?? "Inter";
                _ = LoadFont(family); // metrics settle through OnFontsLoaded once the face arrives
                var t = new TextNode { Text = "Text", FontSize = Math.Max(4, Math.Round(UsableHeight * 1.25 * 2) / 2), FontFamily = family, Bold = true };
                Remeasure(t);
                return t;
            }
            case "shape" when Enum.TryParse<ShapeKind>(parts[1], out var kind):
            {
                var s = new ShapeNode { Kind = kind, H = h, W = h };
                switch (kind)
                {
                    case ShapeKind.Rect: s.W = Math.Round(h * 1.8, 1); break;
                    case ShapeKind.Line: s.W = Math.Min(24, Math.Max(8, Doc.WidthMm * 0.4)); s.H = 1; break;
                    case ShapeKind.Arrow or ShapeKind.DoubleArrow or ShapeKind.Chevron:
                        s.W = Math.Round(h * 1.6, 1); s.H = Math.Round(h * 0.8, 1); s.Fill = Ink.Black; s.Stroke = Ink.None; break;
                    case ShapeKind.Parallelogram or ShapeKind.Trapezoid or ShapeKind.Bubble: s.W = Math.Round(h * 1.5, 1); break;
                    case ShapeKind.Semicircle: s.W = Math.Round(h * 1.6, 1); s.H = Math.Round(h * 0.8, 1); break;
                    case ShapeKind.Star or ShapeKind.Heart or ShapeKind.Check or ShapeKind.Cross: s.Fill = Ink.Black; s.Stroke = Ink.None; break;
                }
                // presets: "shape:Polygon:6", "shape:Rect:round", "shape:Rect:pill", "shape:Rect:square"
                if (parts.Length > 2)
                {
                    if (int.TryParse(parts[2], out var sides)) s.Sides = sides;
                    else if (parts[2] == "round") s.CornerRadius = Math.Round(h * 0.2, 1);
                    else if (parts[2] == "pill") s.CornerRadius = 100;
                    else if (parts[2] == "square") s.W = h;
                }
                else if (kind == ShapeKind.Polygon) s.Sides = 6;
                return s;
            }
            case "code" when Enum.TryParse<CodeFormat>(parts[1], out var format):
            {
                double ch = Math.Max(3, Math.Round(UsableHeight, 1));
                var b = new BarcodeNode { Format = format, Value = SampleValue(format), H = ch, W = ch };
                if (!b.Is2D) { b.W = Math.Min(36, Math.Max(18, Doc.WidthMm * 0.5)); b.ShowText = ch >= 8; }
                else if (format == CodeFormat.Pdf417) b.W = ch * 2.4;
                return b;
            }
            case "table": // "table:rows:cols"
                return MakeTable(parts.Length > 1 && int.TryParse(parts[1], out var tr) ? tr : 2, parts.Length > 2 && int.TryParse(parts[2], out var tc) ? tc : 2);
            case "image" when _pendingImage is { } img:
            {
                double ih = Math.Max(3, UsableHeight);
                return new ImageNode { AssetId = img.AssetId, H = ih, W = ih * img.Aspect, Dither = DitherFor(Doc.Assets.GetValueOrDefault(img.AssetId)) };
            }
        }
        return null;
    }

    /// <summary>The box being dragged out, honouring Shift (square) and the tool's aspect lock.</summary>
    Rect CreateRect(double wx, double wy, bool shift)
    {
        double dx = wx - _wx0, dy = wy - _wy0;
        double? aspect = ToolAspect ?? (shift ? 1 : null);
        if (aspect is { } a && Tool != "shape:Line")
        {
            double w = Math.Max(Math.Abs(dx), Math.Abs(dy) * a);
            dx = Math.Sign(dx == 0 ? 1 : dx) * w;
            dy = Math.Sign(dy == 0 ? 1 : dy) * w / a;
        }
        return new Rect(Math.Min(_wx0, _wx0 + dx), Math.Min(_wy0, _wy0 + dy), Math.Abs(dx), Math.Abs(dy));
    }

    /// <summary>The object being dragged out, drawn live until the button is released.</summary>
    Node? _creating;
    bool _creatingShown;

    void BeginCreate()
    {
        _creating = MakeNode();
        _creatingShown = false;
    }

    /// <summary>Follow the pointer with the real object rather than an empty box.</summary>
    void PreviewCreate(double wx, double wy, bool shift)
    {
        if (_creating is null) return;
        Shape(_creating, wx, wy, shift, dragged: true);
        if (_creating is BarcodeNode code) Barcodes.Ensure(code, _js);
        _creatingShown = true;
    }

    void FinishCreate(double wx, double wy, bool shift, bool dragged)
    {
        // a plain click gets the untouched default size
        var node = dragged ? _creating ?? MakeNode() : MakeNode();
        if (node is not null) Shape(node, wx, wy, shift, dragged); // while the tool (and its aspect lock) is still armed
        _creating = null;
        _creatingShown = false;
        Tool = null;
        _pendingImage = null;
        if (node is null) { Refresh(); return; }

        Mutate(() =>
        {
            ScopeList.Add(node);
            Select(node);
        });
        if (node is TextNode text) BeginEdit(text, selectAll: true);
        else if (node is TableNode table) BeginCellEdit(table, 0);
    }

    /// <summary>Size and place a new object from the drag (or centre it on a click).</summary>
    void Shape(Node node, double wx, double wy, bool shift, bool dragged)
    {
        var rect = CreateRect(wx, wy, shift);
        if (!dragged)
        {
            node.X = _wx0 - node.W / 2;
            node.Y = _wy0 - node.H / 2;
        }
        else if (node is ShapeNode { Kind: ShapeKind.Line })
        {
            double ang = Math.Atan2(wy - _wy0, wx - _wx0) * 180 / Math.PI;
            if (shift) ang = Math.Round(ang / 15) * 15;
            else if (Math.Abs(ang - Math.Round(ang / 90) * 90) < 2) ang = Math.Round(ang / 90) * 90;
            double len = Math.Max(0.5, Math.Sqrt((wx - _wx0) * (wx - _wx0) + (wy - _wy0) * (wy - _wy0)));
            var (ex, ey) = Geo.Rot(len, 0, ang);
            node.W = len;
            node.X = _wx0 + ex / 2 - len / 2;
            node.Y = _wy0 + ey / 2 - node.H / 2;
            node.Rotation = Geo.Norm(ang);
        }
        else
        {
            var r = rect;
            node.X = r.X; node.Y = r.Y;
            node.W = Math.Max(0.5, r.W); node.H = Math.Max(0.5, r.H);
            // a dragged-out text box has a fixed width and wraps
            if (node is TextNode t) t.Mode = TextMode.Wrap;
        }
    }

    /// <summary>Insert an object without the tool flow (paste, drop): beside existing content, or centred when full.</summary>
    public void Add(Node node)
    {
        Mutate(() =>
        {
            var area = MarginRect;
            node.Y = area.Cy - node.H / 2;
            double right = Geo.Bounds(Doc.Nodes.Where(n => n.Visible)) is { } used ? Math.Max(area.X, used.R + 1.5) : area.X;
            if (Doc.AutoWidth || right + node.W <= area.R + 0.01) node.X = right;
            else node.X = Math.Max(area.X, area.Cx - node.W / 2);
            ScopeList.Add(node);
            Select(node);
        });
    }

    public static string SampleValue(CodeFormat f) => f switch
    {
        CodeFormat.Ean13 => "5901234123457",
        CodeFormat.Ean8 => "96385074",
        CodeFormat.UpcA => "036000291452",
        CodeFormat.Itf => "12345678",
        CodeFormat.Codabar => "A12345B",
        CodeFormat.Code39 or CodeFormat.Code93 => "ABC-1234",
        CodeFormat.Code128 => "LB-000123",
        _ => "https://example.com",
    };

    /// <summary>Place a dropped or pasted image; (sx, sy) is a canvas position or negative to auto-place.</summary>
    public void AddImage(string dataUrl, double pxW, double pxH, double sx = -1, double sy = -1)
    {
        double h = Math.Max(3, UsableHeight), w = h * pxW / Math.Max(1, pxH);
        double maxW = Math.Max(5, Doc.WidthMm - Doc.Margins.L - Doc.Margins.R);
        if (w > maxW && !Doc.AutoWidth) { h *= maxW / w; w = maxW; }
        var node = new ImageNode { AssetId = AddAsset(dataUrl), W = w, H = h, Dither = DitherFor(dataUrl) };
        Add(node);
        if (sx >= 0 && sy >= 0)
        {
            var (wx, wy) = ToWorld(sx, sy);
            node.X = wx - w / 2; node.Y = wy - h / 2;
            Touch();
        }
    }

    public void ReplaceImage(ImageNode node, string dataUrl, double pxW, double pxH) => Mutate(() =>
    {
        node.AssetId = AddAsset(dataUrl);
        double cx = node.Cx, cy = node.Cy;
        node.W = node.H * pxW / Math.Max(1, pxH);
        node.X = cx - node.W / 2; node.Y = cy - node.H / 2;
    });

    // ---- inline text editing -------------------------------------------------------------

    public string? EditingId { get; private set; }
    public bool EditSelectAll { get; private set; }
    public TextNode? EditingNode => Find(EditingId) as TextNode;

    public void BeginEdit(TextNode node, bool selectAll = false)
    {
        EditingId = node.Id;
        EditSelectAll = selectAll;
        Select(node);
        Refresh();
    }

    public void EndEdit()
    {
        if (EditingId is null) return;
        var node = EditingNode;
        EditingId = null;
        // an emptied text box is just clutter
        if (node is not null && string.IsNullOrWhiteSpace(node.Text)) { Select(node); DeleteSelection(); }
        else Refresh();
    }
}
