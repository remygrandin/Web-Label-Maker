using LabelMaker.Models;

namespace LabelMaker.Services;

/// <summary>The templates that ship with the app. Each call builds a fresh document.</summary>
public static class BuiltInTemplates
{
    public static List<LabelDocument> All() =>
    [
        Starter(), CableFlag(), AssetTag(), BinLabel(), Wifi(), CautionStrip(), BarcodeStrip(), Address(),
    ];

    static LabelDocument Doc(string id, string name, string media, double length, params Node[] nodes)
    {
        var d = Editor.NewBlank(media, length);
        d.Id = "builtin-" + id;
        d.Name = name;
        d.Nodes = nodes.ToList();
        return d;
    }

    static TextNode Text(string text, double x, double y, double w, double h, string font, double pt, bool bold = false,
        TextMode mode = TextMode.Auto, HAlign align = HAlign.Left) => new()
    {
        Text = text, X = x, Y = y, W = w, H = h, FontFamily = font, FontSize = pt, Bold = bold, Mode = mode, Align = align,
        // the positions below give the top edge of text that sizes itself
        VAlign = mode == TextMode.Auto ? VAlign.Top : VAlign.Middle,
    };

    static BarcodeNode Code(CodeFormat f, string value, double x, double y, double w, double h, bool showText = false) => new()
    {
        Format = f, Value = value, X = x, Y = y, W = w, H = h, ShowText = showText,
    };

    public static LabelDocument Starter() => Doc("plain", "Plain text", "tze-12", 60,
        Text("YOUR TEXT", 2, 1.06, 56, 9.88, "Inter", 20, bold: true, mode: TextMode.Fit, align: HAlign.Center));

    static LabelDocument CableFlag() => Doc("cable-flag", "Cable flag", "tze-12", 84,
        Text("SW1 · PORT 12", 2, 2.2, 34, 7.6, "Roboto Mono", 12, bold: true, mode: TextMode.Fit, align: HAlign.Center),
        Text("SW1 · PORT 12", 48, 2.2, 34, 7.6, "Roboto Mono", 12, bold: true, mode: TextMode.Fit, align: HAlign.Center),
        new ShapeNode { Name = "Wrap line", Kind = ShapeKind.Line, X = 42 - 4.94, Y = 5.5, W = 9.88, H = 1, Rotation = 90, StrokeWidth = 0.25, Dash = DashStyle.Dashed });

    static LabelDocument AssetTag() => Doc("asset-tag", "Asset tag", "tze-24", 72,
        new ShapeNode { Name = "Border", X = 2, Y = 2.97, W = 68, H = 18.06, StrokeWidth = 0.5, CornerRadius = 1.6 },
        Code(CodeFormat.QrCode, "https://example.com/asset/000123", 3.6, 4.57, 14.86, 14.86),
        Text("PROPERTY OF", 20.5, 4.9, 20, 3, "IBM Plex Mono", 6.5),
        Text("Acme Workshop", 20.5, 8, 40, 6, "Inter", 14, bold: true),
        Text("ID 000123", 20.5, 14.8, 30, 4, "IBM Plex Mono", 10));

    static LabelDocument BinLabel()
    {
        var tag = Text("A-12", 2, 1.1, 19, 15.8, "Roboto Condensed", 30, bold: true, mode: TextMode.Fit, align: HAlign.Center);
        tag.Invert = true;
        tag.Padding = 1.6;
        var what = Text("M4 × 12 hex bolt, zinc", 23, 1.1, 39, 7.6, "Inter", 9.5, bold: true, mode: TextMode.Wrap);
        return Doc("bin", "Bin label", "tze-18", 64, tag, what, Code(CodeFormat.Code128, "BIN-A12", 23, 9.4, 39, 7.5));
    }

    static LabelDocument Wifi() => Doc("wifi", "Wi-Fi access", "tze-24", 76,
        Code(CodeFormat.QrCode, "WIFI:T:WPA;S:Workshop;P:change-me;;", 2, 2.97, 18.06, 18.06),
        Text("WI-FI", 22.5, 3.6, 12, 3, "IBM Plex Mono", 7, bold: true),
        Text("Workshop", 22.5, 7, 40, 6, "Inter", 15, bold: true),
        Text("change-me", 22.5, 14.6, 40, 5, "IBM Plex Mono", 11));

    static LabelDocument CautionStrip()
    {
        var head = Text("CAUTION", 2, 1.06, 24, 9.88, "Bebas Neue", 24, mode: TextMode.Fit, align: HAlign.Center);
        head.Invert = true;
        head.Padding = 1;
        return Doc("caution", "Caution strip", "tze-12", 72, head,
            Text("Do not switch off.\nBackup in progress.", 28, 1.06, 42, 9.88, "Inter", 8.5, bold: true, mode: TextMode.Wrap));
    }

    static LabelDocument BarcodeStrip() => Doc("barcode", "Barcode strip", "tze-12", 60,
        Code(CodeFormat.Code128, "LB-000123", 2, 1.06, 56, 9.88, showText: true));

    static LabelDocument Address()
    {
        var t = Text("Jane Doe\n12 Example Street\n75001 Paris", 2, 2.97, 76, 18.06, "Inter", 11.5, mode: TextMode.Wrap);
        t.LineHeight = 1.2;
        return Doc("address", "Address", "tze-24", 80, t);
    }
}
