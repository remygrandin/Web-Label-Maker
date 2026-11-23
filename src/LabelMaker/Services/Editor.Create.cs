using LabelMaker.Models;

namespace LabelMaker.Services;

// Factories for new objects, sized to suit the current tape.
public sealed partial class Editor
{
    public void AddText(string text = "Label")
    {
        double pt = Math.Max(4, Math.Round(UsableHeight * 1.25 * 2) / 2);
        var family = Settings.RecentFonts.FirstOrDefault() ?? "Inter";
        _ = LoadFont(family); // metrics settle through OnFontsLoaded once the face arrives
        var node = new TextNode { Text = text, FontSize = pt, FontFamily = family, Bold = true };
        Remeasure(node);
        Add(node);
        TextFocusRequested?.Invoke();
    }

    public void AddShape(ShapeKind kind)
    {
        double h = Math.Max(2, Math.Round(UsableHeight * 0.8, 1));
        var s = new ShapeNode { Kind = kind, H = h, W = h };
        switch (kind)
        {
            case ShapeKind.Rect: s.W = Math.Round(h * 1.8, 1); break;
            case ShapeKind.Arrow: s.W = Math.Round(h * 1.6, 1); s.H = Math.Round(h * 0.8, 1); s.Fill = Ink.Black; s.Stroke = Ink.None; break;
            case ShapeKind.Line: s.W = Math.Min(24, Math.Max(8, Doc.WidthMm * 0.4)); s.H = 1; break;
            case ShapeKind.Star: s.Fill = Ink.Black; s.Stroke = Ink.None; break;
            case ShapeKind.Polygon: s.Sides = 6; break;
        }
        Add(s);
    }

    public void AddBarcode(CodeFormat format)
    {
        double h = Math.Max(3, Math.Round(UsableHeight, 1));
        var b = new BarcodeNode { Format = format, Value = SampleValue(format), H = h, W = h };
        if (!b.Is2D) { b.W = Math.Min(40, Math.Max(20, Doc.WidthMm * 0.6)); b.ShowText = h >= 8; }
        else if (format == CodeFormat.Pdf417) b.W = h * 2.4;
        Add(b);
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

    /// <summary>Place an image; (sx, sy) is a canvas position or negative to centre it.</summary>
    public void AddImage(string dataUrl, double pxW, double pxH, double sx = -1, double sy = -1)
    {
        double h = Math.Max(3, UsableHeight), w = h * pxW / Math.Max(1, pxH);
        double maxW = Math.Max(5, Doc.WidthMm - Doc.Margins.L - Doc.Margins.R);
        if (w > maxW && !Doc.AutoWidth) { h *= maxW / w; w = maxW; }
        var node = new ImageNode { AssetId = AddAsset(dataUrl), W = w, H = h };
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
}
