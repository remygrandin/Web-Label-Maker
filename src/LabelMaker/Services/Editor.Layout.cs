using LabelMaker.Models;

namespace LabelMaker.Services;

public sealed record LayoutZone(double X, double W, string Label);

// Cabling layouts: cable wrap (repeating cell), flag (two faces around a fold) and port strips.
public sealed partial class Editor
{
    /// <summary>Tape needed to go once round the cable.</summary>
    public static double Circumference(LabelLayout l) => Math.Round(Math.PI * Math.Max(0, l.Diameter), 1);

    /// <summary>Repeats needed for the wrap to cover the whole circumference.</summary>
    public static int WrapCount(LabelLayout l) => Math.Max(1, (int)Math.Ceiling(Circumference(l) / Math.Max(1, l.Pitch) - 1e-6));

    /// <summary>Where the second face of a flag starts.</summary>
    static double FlagShift(LabelLayout l) => l.FlagLength + Circumference(l);

    /// <summary>In these layouts the label length follows from the layout settings.</summary>
    static double? LayoutWidth(LabelDocument doc)
    {
        var l = doc.Layout;
        return l.Kind switch
        {
            "wrap" => WrapCount(l) * l.Pitch,
            "flag" => 2 * l.FlagLength + Circumference(l),
            "ports" => doc.Margins.L + l.Ports * l.PortPitch + doc.Margins.R,
            _ => null,
        };
    }

    /// <summary>
    /// Flag with automatic face length: each face hugs its content with the left margin either
    /// side. The content is kept against the start of its face, and side B moves with the fold.
    /// </summary>
    static void AutoFlag(LabelDocument doc)
    {
        var l = doc.Layout;
        if (l.Kind != "flag" || !l.FlagAuto) return;
        double circ = Circumference(l), middle = l.FlagLength + circ / 2, pad = doc.Margins.L;
        var visible = doc.Nodes.Where(n => n.Visible).ToList();
        var sideA = visible.Where(n => (Geo.Bounds(n)?.Cx ?? 0) < middle).ToList();
        var sideB = l.Mirror ? new List<Node>() : visible.Except(sideA).ToList();
        var a = Geo.Bounds(sideA);
        var b = Geo.Bounds(sideB);
        if (a is null && b is null) return;

        double length = Math.Clamp(Math.Round(Math.Max(a?.W ?? 0, b?.W ?? 0) + 2 * pad, 1), 5, 300);
        if (a is { } ra && Math.Abs(ra.X - pad) > 0.01)
            foreach (var n in sideA) Geo.Translate(n, pad - ra.X, 0);
        l.FlagLength = length;
        double startB = length + circ + pad;
        if (b is { } rb && Math.Abs(rb.X - startB) > 0.01)
            foreach (var n in sideB) Geo.Translate(n, startB - rb.X, 0);
    }

    /// <summary>
    /// Switch the flag's face length between automatic and fixed. Going automatic, text that was
    /// scaled to fill a box keeps its current size and lets its box follow the text instead,
    /// since a box that fills the face cannot also decide how long the face is.
    /// </summary>
    public void SetFlagAuto(bool auto)
    {
        if (Doc.Layout.FlagAuto == auto) return;
        Mutate(() =>
        {
            Doc.Layout.FlagAuto = auto;
            if (!auto) return;
            foreach (var t in Geo.Walk(Doc.Nodes).OfType<TextNode>().Where(t => t.Mode == TextMode.Fit))
            {
                var m = _js.Invoke<double[]>("LM.textEditMetrics", (TextNode)Resolved(t));
                double cy = t.Cy;
                t.FontSize = Math.Max(2, Math.Round(m[0] / 0.3528 * 2) / 2);
                t.Mode = TextMode.Auto;
                Remeasure(t);
                t.Y = cy - t.H / 2; // stay where the text was vertically
            }
        });
        ZoomToFit();
    }

    /// <summary>Copies the layout adds at draw time: wrap repeats, the mirrored flag face, printed fold lines.</summary>
    static IEnumerable<Node> LayoutCopies(LabelDocument doc, List<Node> items)
    {
        var l = doc.Layout;
        Node Shifted(Node n, double dx, string tag)
        {
            var c = n.Shallow();
            c.Id = n.Id + tag;
            c.X += dx;
            return c;
        }
        switch (l.Kind)
        {
            case "wrap":
                for (int k = 1; k < WrapCount(l); k++)
                    foreach (var n in items) yield return Shifted(n, k * l.Pitch, "#" + k);
                break;
            case "flag":
                if (l.Mirror)
                    foreach (var n in items.Where(n => n.Cx < l.FlagLength)) yield return Shifted(n, FlagShift(l), "#b");
                if (l.PrintGuides)
                    foreach (var x in Dividers(doc))
                        yield return new ShapeNode
                        {
                            Id = "fold" + x, Kind = ShapeKind.Line, W = doc.HeightMm, H = 1, Rotation = 90,
                            X = x - doc.HeightMm / 2, Y = doc.HeightMm / 2 - 0.5, StrokeWidth = 0.2, Dash = DashStyle.Dotted,
                        };
                break;
        }
    }

    /// <summary>Guide lines shown on the canvas (not printed).</summary>
    static List<double> Dividers(LabelDocument doc)
    {
        var l = doc.Layout;
        var xs = new List<double>();
        switch (l.Kind)
        {
            case "wrap":
                for (int k = 1; k < WrapCount(l); k++) xs.Add(k * l.Pitch);
                break;
            case "flag":
                if (Circumference(l) < 0.5) xs.Add(l.FlagLength + Circumference(l) / 2);
                else { xs.Add(l.FlagLength); xs.Add(FlagShift(l)); }
                break;
            case "ports":
                for (int k = 0; k <= l.Ports; k++) xs.Add(doc.Margins.L + k * l.PortPitch);
                break;
        }
        return xs;
    }

    static List<LayoutZone> Zones(LabelDocument doc)
    {
        var l = doc.Layout;
        var zones = new List<LayoutZone>();
        switch (l.Kind)
        {
            case "wrap" when WrapCount(l) > 1:
                zones.Add(new(l.Pitch, (WrapCount(l) - 1) * l.Pitch, "REPEATS OF THE FIRST CELL"));
                break;
            case "flag":
                if (Circumference(l) >= 0.5) zones.Add(new(l.FlagLength, Circumference(l), "CABLE"));
                if (l.Mirror) zones.Add(new(FlagShift(l), l.FlagLength, "COPY OF SIDE A"));
                break;
        }
        return zones;
    }

    // ---- switching and editing layouts ------------------------------------------------------

    public void SetLayout(string kind)
    {
        if (Doc.Layout.Kind == kind) return;
        Mutate(() =>
        {
            var l = Doc.Layout;
            l.Kind = kind;
            Doc.AutoWidth = false;
            Selection.Clear();
            ScopeId = null;
            // A blank label, or one holding just a line of text, is rearranged for the new layout.
            var only = Doc.Nodes.Count == 1 ? Doc.Nodes[0] as TextNode : null;
            bool simple = Doc.Nodes.Count == 0 || only is not null || Doc.Nodes.All(n => n.Name.StartsWith("port"));
            if (!simple) return;
            string family = only?.FontFamily ?? "Roboto Condensed";
            _ = LoadFont(family);
            double top = Doc.Margins.T, usable = UsableHeight;
            switch (kind)
            {
                case "wrap":
                    l.Pitch = Math.Max(3, Math.Round(usable * 0.5));
                    Doc.Nodes.Clear();
                    // one line of text reading across the tape, repeated round the cable
                    Doc.Nodes.Add(new TextNode
                    {
                        Text = only?.Text ?? "CABLE-01", FontFamily = family, Bold = true, Mode = TextMode.Fit, Align = HAlign.Center,
                        W = usable, H = l.Pitch - 0.8, Rotation = 90, X = l.Pitch / 2 - usable / 2, Y = Doc.HeightMm / 2 - (l.Pitch - 0.8) / 2,
                    });
                    break;
                case "flag":
                    Doc.Nodes.Clear();
                    Doc.Nodes.Add(new TextNode
                    {
                        Text = only?.Text ?? "SW1 · P12", FontFamily = family, Bold = true, Mode = TextMode.Fit, Align = HAlign.Center,
                        X = Doc.Margins.L, Y = top, W = Math.Max(4, l.FlagLength - 2 * Doc.Margins.L), H = usable,
                    });
                    break;
                case "ports":
                    Doc.Nodes.Clear();
                    BuildPorts(null);
                    break;
            }
        });
        ZoomToFit();
    }

    /// <summary>Change layout settings as one undo step; a port strip is rebuilt to match.</summary>
    public void UpdateLayout(Action<LabelLayout> change)
    {
        Mutate(() =>
        {
            var old = Doc.Layout.Clone();
            // remember port captions the user typed over the automatic numbering
            var custom = new Dictionary<int, string>();
            if (old.Kind == "ports")
            {
                var texts = Doc.Nodes.OfType<TextNode>().Where(t => t.Name.StartsWith("port")).OrderBy(t => t.X).ToList();
                for (int i = 0; i < texts.Count; i++)
                    if (texts[i].Text != old.Prefix + (old.First + i)) custom[i] = texts[i].Text;
            }
            change(Doc.Layout);
            var l = Doc.Layout;
            l.Pitch = Math.Clamp(l.Pitch, 1, 200);
            l.PortPitch = Math.Clamp(l.PortPitch, 3, 200);
            l.Diameter = Math.Clamp(l.Diameter, 0, 200);
            l.FlagLength = Math.Clamp(l.FlagLength, 5, 300);
            l.Ports = Math.Clamp(l.Ports, 1, 96);
            if (l.Kind == "ports") BuildPorts(custom);
        }, "layout");
        ZoomToFit();
    }

    void BuildPorts(Dictionary<int, string>? custom)
    {
        var l = Doc.Layout;
        var sample = Doc.Nodes.OfType<TextNode>().FirstOrDefault(t => t.Name.StartsWith("port"));
        string family = sample?.FontFamily ?? "Roboto Condensed";
        _ = LoadFont(family);
        Doc.Nodes.RemoveAll(n => n.Name.StartsWith("port"));
        double x0 = Doc.Margins.L, top = Doc.Margins.T, usable = UsableHeight, mid = Doc.HeightMm / 2;
        if (l.Separators)
            for (int i = 0; i <= l.Ports; i++)
                Doc.Nodes.Add(new ShapeNode
                {
                    Name = "portsep" + i, Kind = ShapeKind.Line, W = usable, H = 1, Rotation = 90, StrokeWidth = 0.2,
                    X = x0 + i * l.PortPitch - usable / 2, Y = mid - 0.5,
                });
        for (int i = 0; i < l.Ports; i++)
            Doc.Nodes.Add(new TextNode
            {
                Name = "port" + (l.First + i), Text = custom?.GetValueOrDefault(i) ?? l.Prefix + (l.First + i),
                FontFamily = family, Bold = sample?.Bold ?? true, Italic = sample?.Italic ?? false,
                Mode = TextMode.Fit, Align = HAlign.Center, VAlign = VAlign.Middle,
                X = x0 + i * l.PortPitch + 0.8, Y = top + 0.6, W = Math.Max(1, l.PortPitch - 1.6), H = Math.Max(1, usable - 1.2),
            });
    }

    /// <summary>A millimetre ruler on the current tape, for measuring port spacing on the equipment.</summary>
    public LabelDocument BuildRuler(double length)
    {
        length = Math.Clamp(Math.Round(length), 20, 300);
        const double x0 = 4;
        var doc = NewBlank(Doc.MediaId, x0 + length + 4);
        doc.Dpi = Doc.Dpi;
        doc.Name = $"Ruler {length:0} mm";
        doc.TapeColor = Doc.TapeColor;
        doc.InkColor = Doc.InkColor;
        double top = doc.Margins.T, usable = doc.HeightMm - doc.Margins.T - doc.Margins.B;
        bool numbers = usable >= 6;
        for (int mm = 0; mm <= length; mm++)
        {
            double h = usable * (mm % 10 == 0 ? (numbers ? 0.55 : 1) : mm % 5 == 0 ? 0.4 : 0.25);
            doc.Nodes.Add(new ShapeNode { Kind = ShapeKind.Rect, Fill = Ink.Black, Stroke = Ink.None, X = x0 + mm - 0.075, Y = top, W = 0.15, H = h });
            if (numbers && mm % 10 == 0)
                doc.Nodes.Add(new TextNode
                {
                    Text = mm.ToString(), FontFamily = "IBM Plex Mono", FontSize = Math.Min(7, usable * 0.36 / 0.3528 / 1.3), Align = HAlign.Center, VAlign = VAlign.Top,
                    X = x0 + mm, Y = top + usable * 0.6, W = 0.01, H = 0.01,
                });
        }
        return doc;
    }

    // ---- barcode boxes ---------------------------------------------------------------------------

    /// <summary>
    /// Shrink a code's box onto the symbol actually drawn. Modules are whole printer
    /// dots, so the symbol is usually a little smaller than the box that was dragged out.
    /// </summary>
    static void FitCodeBox(BarcodeNode n, BarcodeNode resolved, double dpi)
    {
        if (Barcodes.Size(resolved) is not { } m || m.W == 0 || m.H == 0) return;
        double dot = 25.4 / dpi, w, h;
        if (n.Is2D)
        {
            int dots = Math.Max(1, (int)Math.Floor(Math.Min(n.W / dot / m.W, n.H / dot / m.H) + 1e-6));
            w = dots * m.W * dot; h = dots * m.H * dot;
        }
        else
        {
            int dots = Math.Max(1, (int)Math.Floor(n.W / dot / m.W + 1e-6));
            w = dots * m.W * dot; h = n.H;
        }
        if (Math.Abs(w - n.W) < 1e-3 && Math.Abs(h - n.H) < 1e-3) return;
        double cx = n.Cx, cy = n.Cy;
        n.W = w; n.H = h; n.X = cx - w / 2; n.Y = cy - h / 2;
    }
}
