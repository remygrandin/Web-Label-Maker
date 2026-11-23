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
    public DateTime Modified { get; set; } = DateTime.UtcNow;
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
