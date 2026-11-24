using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabelMaker.Models;

public sealed class Margins
{
    public double L { get; set; }
    public double T { get; set; }
    public double R { get; set; }
    public double B { get; set; }

    public Margins Clone() => new() { L = L, T = T, R = R, B = B };
}

public sealed class LabelDocument
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = "Untitled label";
    /// <summary>Media identifier, see <see cref="Media"/>.</summary>
    public string MediaId { get; set; } = "tze-12";
    /// <summary>Label length along the feed direction, mm.</summary>
    public double WidthMm { get; set; } = 60;
    /// <summary>Tape width, mm.</summary>
    public double HeightMm { get; set; } = 12;
    /// <summary>Grow or shrink the label length to fit its content.</summary>
    public bool AutoWidth { get; set; }
    public double Dpi { get; set; } = 180;
    public Margins Margins { get; set; } = new();
    /// <summary>Back-to-front: the last node is drawn on top.</summary>
    public List<Node> Nodes { get; set; } = new();
    /// <summary>Embedded images, asset id → data URL.</summary>
    public Dictionary<string, string> Assets { get; set; } = new();
    /// <summary>User-defined values, usable as {name} in text, code values and numeric fields.</summary>
    public List<Variable> Variables { get; set; } = new();
    /// <summary>Backs the {seq} macro; advances with every printed label.</summary>
    public Counter Counter { get; set; } = new();
    /// <summary>Tape (background) colour id, see <see cref="Media.TapeColors"/>. Preview only: the printer prints what is loaded.</summary>
    public string TapeColor { get; set; } = "white";
    /// <summary>Print (foreground) colour id, see <see cref="Media.InkColors"/>.</summary>
    public string InkColor { get; set; } = "black";
    /// <summary>Negative print: the whole label is printed solid with the content left blank.</summary>
    public bool Invert { get; set; }
    /// <summary>Cable wrap, flag or port strip arrangement.</summary>
    public LabelLayout Layout { get; set; } = new();
    /// <summary>Places along the label where the tape is cut.</summary>
    public List<CutMark> Cuts { get; set; } = new();
    public DateTime Modified { get; set; } = DateTime.UtcNow;

    public LabelDocument ShallowCopy() => (LabelDocument)MemberwiseClone();
}

/// <summary>A tape or label stock a document can be laid out on.</summary>
public sealed record MediaInfo(string Id, string Label, double WidthMm, double PrintableMm, int WidthCode);

public static class Media
{
    // Brother TZe tape. Printable heights come from the raster command reference
    // (print-area pins at 180 dpi; 36 mm is 454 pins at 360 dpi).
    public static readonly MediaInfo[] All =
    [
        new("tze-3.5", "3.5 mm", 3.5, 24 / 180.0 * 25.4, 0x04),
        new("tze-6", "6 mm", 6, 32 / 180.0 * 25.4, 0x06),
        new("tze-9", "9 mm", 9, 50 / 180.0 * 25.4, 0x09),
        new("tze-12", "12 mm", 12, 70 / 180.0 * 25.4, 0x0C),
        new("tze-18", "18 mm", 18, 112 / 180.0 * 25.4, 0x12),
        new("tze-24", "24 mm", 24, 128 / 180.0 * 25.4, 0x18),
        new("tze-36", "36 mm", 36, 454 / 360.0 * 25.4, 0x24),
    ];

    public static MediaInfo Get(string id) => All.FirstOrDefault(m => m.Id == id) ?? All[3];

    // The usual TZe tape and print colours. They only tint the preview.
    public static readonly ColorOption[] TapeColors =
    [
        new("white", "White", "#ffffff"), new("clear", "Clear", "#eef0ee"), new("yellow", "Yellow", "#ffd400"),
        new("black", "Black", "#141414"), new("red", "Red", "#d6262c"), new("blue", "Blue", "#1d5fc2"),
        new("green", "Green", "#1f9a4c"), new("orange", "Fluorescent orange", "#ff7a1c"), new("pink", "Pink", "#f3a6c4"),
        new("gray", "Gray", "#b7babf"), new("silver", "Matte silver", "#cfd2d6"), new("gold", "Satin gold", "#d9bf74"),
    ];

    public static readonly ColorOption[] InkColors =
    [
        new("black", "Black", "#000000"), new("white", "White", "#ffffff"), new("red", "Red", "#d6262c"),
        new("blue", "Blue", "#1d5fc2"), new("gold", "Gold", "#b8922e"),
    ];

    public static string Hex(ColorOption[] set, string id) => (set.FirstOrDefault(c => c.Id == id) ?? set[0]).Hex;

    /// <summary>Margins matching what the printer cannot reach on this tape.</summary>
    public static Margins DefaultMargins(MediaInfo m)
    {
        var v = Math.Round((m.WidthMm - m.PrintableMm) / 2, 2);
        return new Margins { L = 2, R = 2, T = v, B = v };
    }
}

public static class DocJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowOutOfOrderMetadataProperties = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static readonly JsonSerializerOptions Pretty = new(Options) { WriteIndented = true };

    public static string Write<T>(T value, bool pretty = false) => JsonSerializer.Serialize(value, pretty ? Pretty : Options);
    public static T? Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static T Clone<T>(T value) => Read<T>(Write(value))!;

    /// <summary>Deep copy with fresh ids, for paste / duplicate / "new from template".</summary>
    public static List<Node> CloneNodes(IEnumerable<Node> nodes)
    {
        var copy = Clone(nodes.ToList());
        void Reid(Node n)
        {
            n.Id = Ids.New();
            if (n is GroupNode g) g.Children.ForEach(Reid);
        }
        copy.ForEach(Reid);
        return copy;
    }
}

public sealed class Variable
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class Counter
{
    /// <summary>The value {seq} shows now, and the first one printed.</summary>
    public double Value { get; set; } = 1;
    public double Step { get; set; } = 1;
}

public sealed class CutMark
{
    public string Id { get; set; } = Ids.New();
    /// <summary>Position along the label, mm.</summary>
    public double X { get; set; }
    /// <summary>Half cut: the tape is cut but the backing is left whole.</summary>
    public bool Half { get; set; }
}

/// <summary>
/// Special arrangements for cabling work. "wrap" repeats the first cell along the
/// tape; "flag" folds around a cable with a face either side; "ports" is a strip of
/// evenly spaced cells for a row of sockets.
/// </summary>
public sealed class LabelLayout
{
    /// <summary>"none", "wrap", "flag" or "ports".</summary>
    public string Kind { get; set; } = "none";
    /// <summary>Cable diameter, mm (wrap and flag).</summary>
    public double Diameter { get; set; } = 6;
    /// <summary>Wrap: width of one repeated cell.</summary>
    public double Pitch { get; set; } = 5;
    /// <summary>Flag: length of each face.</summary>
    public double FlagLength { get; set; } = 25;
    /// <summary>Flag: the second face repeats the first.</summary>
    public bool Mirror { get; set; } = true;
    /// <summary>Flag: print the fold lines as dotted lines.</summary>
    public bool PrintGuides { get; set; }
    /// <summary>Ports: distance between the left edges of neighbouring ports, mm.</summary>
    public double PortPitch { get; set; } = 16;
    public int Ports { get; set; } = 8;
    public int First { get; set; } = 1;
    public string Prefix { get; set; } = "";
    public bool Separators { get; set; } = true;

    public LabelLayout Clone() => (LabelLayout)MemberwiseClone();
}

public sealed record ColorOption(string Id, string Label, string Hex);
