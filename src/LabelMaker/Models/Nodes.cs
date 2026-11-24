using System.Text.Json.Serialization;

namespace LabelMaker.Models;

// All geometry is in millimetres, in label space (origin = top-left of the label).
// Leaves carry an absolute box + rotation about its centre; groups are purely
// structural and take their bounds from their children.

[JsonConverter(typeof(JsonStringEnumConverter<TextMode>))]
public enum TextMode { Auto, Wrap, Fit }

[JsonConverter(typeof(JsonStringEnumConverter<HAlign>))]
public enum HAlign { Left, Center, Right }

[JsonConverter(typeof(JsonStringEnumConverter<VAlign>))]
public enum VAlign { Top, Middle, Bottom }

[JsonConverter(typeof(JsonStringEnumConverter<Ink>))]
public enum Ink { None, Black, White }

[JsonConverter(typeof(JsonStringEnumConverter<ShapeKind>))]
public enum ShapeKind
{
    Rect, Ellipse, Line, Triangle, Diamond, Polygon, Star, Arrow,
    RightTriangle, Parallelogram, Trapezoid, Cross, Chevron, DoubleArrow, Heart, Bubble, Semicircle, Check,
}

[JsonConverter(typeof(JsonStringEnumConverter<DashStyle>))]
public enum DashStyle { Solid, Dashed, Dotted }

[JsonConverter(typeof(JsonStringEnumConverter<DitherMode>))]
public enum DitherMode { Threshold, FloydSteinberg, Atkinson, Bayer }

[JsonConverter(typeof(JsonStringEnumConverter<CodeFormat>))]
public enum CodeFormat { QrCode, DataMatrix, Aztec, Pdf417, Code128, Code39, Code93, Ean13, Ean8, UpcA, Itf, Codabar }

[JsonConverter(typeof(JsonStringEnumConverter<EccLevel>))]
public enum EccLevel { L, M, Q, H }

[JsonConverter(typeof(JsonStringEnumConverter<TableHeader>))]
public enum TableHeader { None, Bold, Invert }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TableNode), "table")]
[JsonDerivedType(typeof(TextNode), "text")]
[JsonDerivedType(typeof(ShapeNode), "shape")]
[JsonDerivedType(typeof(ImageNode), "image")]
[JsonDerivedType(typeof(BarcodeNode), "barcode")]
[JsonDerivedType(typeof(GroupNode), "group")]
public abstract class Node
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = "";
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 10;
    public double H { get; set; } = 10;
    public double Rotation { get; set; }
    /// <summary>Resizing keeps the width/height ratio.</summary>
    public bool LockAspect { get; set; }
    /// <summary>Numeric properties driven by an expression: property name → expression.</summary>
    public Dictionary<string, string>? Bindings { get; set; }

    public Node Shallow() => (Node)MemberwiseClone();

    [JsonIgnore] public double Cx => X + W / 2;
    [JsonIgnore] public double Cy => Y + H / 2;
}

public sealed class TextNode : Node
{
    public string Text { get; set; } = "Text";
    public string FontFamily { get; set; } = "Inter";
    /// <summary>Font size in points.</summary>
    public double FontSize { get; set; } = 12;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool Strike { get; set; }
    public HAlign Align { get; set; } = HAlign.Left;
    public VAlign VAlign { get; set; } = VAlign.Middle;
    public double LineHeight { get; set; } = 1.1;
    /// <summary>Letter spacing in 1/1000 em.</summary>
    public double Tracking { get; set; }
    /// <summary>Auto: the box hugs the text. Wrap: fixed box, words wrap. Fit: the text scales to fill the box.</summary>
    public TextMode Mode { get; set; } = TextMode.Auto;
    /// <summary>Upright letters one above the other; each line of text becomes a column.</summary>
    public bool Stacked { get; set; }
    /// <summary>White text on a black box.</summary>
    public bool Invert { get; set; }
    public double Padding { get; set; }
}

public sealed class ShapeNode : Node
{
    public ShapeKind Kind { get; set; } = ShapeKind.Rect;
    public Ink Fill { get; set; } = Ink.None;
    public Ink Stroke { get; set; } = Ink.Black;
    public double StrokeWidth { get; set; } = 0.4;
    public DashStyle Dash { get; set; } = DashStyle.Solid;
    public double CornerRadius { get; set; }
    /// <summary>Corner count for polygons, point count for stars.</summary>
    public int Sides { get; set; } = 5;
}

public sealed class ImageNode : Node
{
    public ImageNode() => LockAspect = true;

    public string AssetId { get; set; } = "";
    public DitherMode Dither { get; set; } = DitherMode.FloydSteinberg;
    public int Threshold { get; set; } = 128;
    public int Brightness { get; set; }
    public int Contrast { get; set; }
    public bool Invert { get; set; }
}

public sealed class BarcodeNode : Node
{
    public CodeFormat Format { get; set; } = CodeFormat.QrCode;
    public string Value { get; set; } = "";
    public EccLevel Ecc { get; set; } = EccLevel.M;
    public bool QuietZone { get; set; } = true;
    /// <summary>Human-readable line under linear codes.</summary>
    public bool ShowText { get; set; } = true;
    /// <summary>How the value is composed: "text" (free), or "url", "wifi", "vcard", "email", "phone", "sms", "geo" built from <see cref="Fields"/>.</summary>
    public string Content { get; set; } = "text";
    public Dictionary<string, string>? Fields { get; set; }

    [JsonIgnore]
    public bool Is2D => Format is CodeFormat.QrCode or CodeFormat.DataMatrix or CodeFormat.Aztec or CodeFormat.Pdf417;
}

/// <summary>A small grid of single-line text cells sharing one font.</summary>
public sealed class TableNode : Node
{
    public const int MaxRows = 8, MaxCols = 10;

    public int Rows { get; set; } = 2;
    public int Cols { get; set; } = 2;
    /// <summary>Cell texts, row by row.</summary>
    public List<string> Cells { get; set; } = new();
    public string FontFamily { get; set; } = "Roboto Condensed";
    /// <summary>Font size in points, used when <see cref="AutoSize"/> is off (text still shrinks to stay inside its cell).</summary>
    public double FontSize { get; set; } = 6;
    /// <summary>One size for the whole table: the largest at which every cell fits.</summary>
    public bool AutoSize { get; set; } = true;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public HAlign Align { get; set; } = HAlign.Center;
    public TableHeader Header { get; set; } = TableHeader.None;
    /// <summary>Columns share the width in proportion to their content rather than equally.</summary>
    public bool FitColumns { get; set; } = true;
    /// <summary>Outer and inner line widths in mm; 0 hides them.</summary>
    public double Border { get; set; } = 0.3;
    public double Grid { get; set; } = 0.2;
    public double Padding { get; set; } = 0.4;

    public string Cell(int index) => index >= 0 && index < Cells.Count ? Cells[index] ?? "" : "";
}

public sealed class GroupNode : Node
{
    public List<Node> Children { get; set; } = new();
    public bool Collapsed { get; set; }
}

public static class Ids
{
    public static string New() => Guid.NewGuid().ToString("N")[..12];
}
