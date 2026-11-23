using LabelMaker.Models;

namespace LabelMaker.Services;

/// <summary>A printer the app knows how to drive.</summary>
/// <param name="Verified">False when the profile rests on reverse-engineered or partly ambiguous documentation.</param>
public sealed record PrinterModel(string Id, string Name, int Pins, int Dpi, int InvalidateBytes, double MaxTapeMm,
    bool AutoCut, bool HalfCut, bool LastPageFlag, bool CutEveryCmd, bool PackBits, bool Verified, string Note);

public sealed class PrintOptions
{
    public int Copies { get; set; } = 1;
    public bool AutoCut { get; set; } = true;
    public bool HalfCut { get; set; }
    /// <summary>Chain printing: do not feed and cut after the last label (it stays in the printer until the next job).</summary>
    public bool Chain { get; set; }
    /// <summary>Blank feed the printer adds before and after the printed area.</summary>
    public double FeedMm { get; set; } = 2;
    /// <summary>Calibration: shifts the print across the tape, in dots.</summary>
    public int PinOffset { get; set; }
}

/// <summary>Where the label lands on the print head for a given tape.</summary>
public readonly record struct TapeGeometry(int FirstPin, int PrintPins);

public sealed class PrinterStatus
{
    public byte[] Raw { get; init; } = [];
    public int Model => Raw[4];
    public string ModelName => Model switch
    {
        0x7F => "PT-E560BT", 0x7E => "PT-E510", 0x7D => "PT-E310BT", 0x78 => "PT-P910BT", 0x72 => "PT-P300BT",
        var c => $"model code 0x{c:X2}",
    };
    public int MediaWidthCode => Raw[10];
    public int MediaType => Raw[11];
    public int StatusType => Raw[18];
    public int Phase => Raw[19];

    public string MediaWidth => MediaWidthCode switch { 0 => "no tape", 4 => "3.5 mm", var w => $"{w} mm" };

    public List<string> Errors
    {
        get
        {
            var list = new List<string>();
            int e1 = Raw[8], e2 = Raw[9];
            if ((e1 & 0x01) != 0) list.Add("No tape cassette");
            if ((e1 & 0x02) != 0) list.Add("End of tape");
            if ((e1 & 0x04) != 0) list.Add("Cutter jam");
            if ((e1 & 0x08) != 0) list.Add("Weak batteries");
            if ((e1 & 0x40) != 0) list.Add("High-voltage adapter");
            if ((e2 & 0x01) != 0) list.Add("Wrong tape for this job");
            if ((e2 & 0x04) != 0) list.Add("Communication error");
            if ((e2 & 0x08) != 0) list.Add("Communication buffer full");
            if ((e2 & 0x10) != 0) list.Add("Cover open");
            if ((e2 & 0x20) != 0) list.Add("Overheating");
            if ((e2 & 0x80) != 0) list.Add("System error");
            return list;
        }
    }
}

/// <summary>
/// Brother P-touch raster protocol (TZe tape printers), following Brother's
/// "Raster Command Reference" for the PT-E550W/P750W/P710BT and PT-P900 series.
/// </summary>
public static class Brother
{
    public static readonly PrinterModel[] Models =
    [
        new("pt-e560bt", "PT-E560BT / PT-E510", 128, 180, 200, 24, AutoCut: true, HalfCut: true, LastPageFlag: true, CutEveryCmd: true, PackBits: false, Verified: true,
            "Documented by Brother (PT-E310BT/E510/E560BT raster reference); matches a driver reported working on this model."),
        new("pt-p710bt", "PT-P710BT (Cube Plus)", 128, 180, 100, 24, AutoCut: true, HalfCut: false, LastPageFlag: false, CutEveryCmd: false, PackBits: true, Verified: true,
            "Documented by Brother; matches several working open-source drivers."),
        new("pt-p300bt", "PT-P300BT (Cube)", 128, 180, 100, 12, AutoCut: false, HalfCut: false, LastPageFlag: false, CutEveryCmd: false, PackBits: true, Verified: false,
            "Brother publishes no raster reference for this model; the profile follows community reverse-engineering (64-dot print band)."),
        new("pt-p910bt", "PT-P910BT (Cube XP)", 560, 360, 200, 36, AutoCut: true, HalfCut: true, LastPageFlag: true, CutEveryCmd: true, PackBits: true, Verified: false,
            "Follows Brother's PT-P900 reference. Sources disagree by 16 dots on vertical alignment; use the offset below if the print sits off-centre."),
        new("pt-128", "Other 180 dpi P-touch (128-pin)", 128, 180, 100, 24, AutoCut: true, HalfCut: true, LastPageFlag: false, CutEveryCmd: false, PackBits: true, Verified: false,
            "Generic profile for PT-P700 / P750W / E550W / H500-class printers reachable over a serial port."),
    ];

    public static PrinterModel Model(string id) => Models.FirstOrDefault(m => m.Id == id) ?? Models[0];

    // (first-byte-side margin, printable pins) per tape, from the raster references.
    static readonly Dictionary<string, TapeGeometry> Pins128 = new()
    {
        ["tze-3.5"] = new(52, 24), ["tze-6"] = new(48, 32), ["tze-9"] = new(39, 50),
        ["tze-12"] = new(29, 70), ["tze-18"] = new(8, 112), ["tze-24"] = new(0, 128),
    };

    // 560-pin head: the print area is off-centre. The first-byte side is the column
    // Brother labels "right margin" (as used by ptouch-print on PT-P900 hardware).
    static readonly Dictionary<string, TapeGeometry> Pins560 = new()
    {
        ["tze-3.5"] = new(264, 48), ["tze-6"] = new(256, 64), ["tze-9"] = new(235, 106), ["tze-12"] = new(213, 150),
        ["tze-18"] = new(171, 234), ["tze-24"] = new(128, 320), ["tze-36"] = new(61, 454),
    };

    public static TapeGeometry? Geometry(PrinterModel model, MediaInfo media)
    {
        if (media.WidthMm > model.MaxTapeMm) return null;
        if (!(model.Pins == 560 ? Pins560 : Pins128).TryGetValue(media.Id, out var g)) return null;
        if (model.Id == "pt-p300bt" && g.PrintPins > 64) g = new TapeGeometry((128 - 64) / 2, 64);
        return g;
    }

    public static int FeedDots(PrinterModel model, PrintOptions o) => Math.Max(14, (int)Math.Round(o.FeedMm * model.Dpi / 25.4));

    /// <summary>The window of the rasterised label (in dots) that reaches the tape.</summary>
    public static (int X, int Y, int W, int H) PrintWindow(PrinterModel model, TapeGeometry g, PrintOptions o, int width, int height)
    {
        int feed = FeedDots(model, o);
        int y0 = (int)Math.Round((height - g.PrintPins) / 2.0);
        return (feed, y0, Math.Max(0, width - 2 * feed), g.PrintPins);
    }

    public static byte[] Preamble(PrinterModel model)
    {
        var b = new List<byte>(new byte[model.InvalidateBytes]);
        b.AddRange([0x1B, 0x40]);       // initialise
        b.AddRange([0x1B, 0x69, 0x53]); // status request
        return b.ToArray();
    }

    /// <summary>
    /// Split a rasterised label of <paramref name="width"/> dots at the given cut positions.
    /// Each piece is printed as its own page and loses the printer's feed margin at both ends.
    /// </summary>
    public static List<(int X0, int Lines)> Pieces(PrinterModel model, PrintOptions o, int width, IEnumerable<int> cuts)
    {
        int feed = FeedDots(model, o);
        var edges = new List<int> { 0 };
        edges.AddRange(cuts.Where(c => c > 0 && c < width).Distinct().OrderBy(c => c));
        edges.Add(width);
        var pieces = new List<(int, int)>();
        for (int i = 0; i + 1 < edges.Count; i++)
        {
            int lines = edges[i + 1] - edges[i] - 2 * feed;
            if (lines < MinLines)
                throw new InvalidOperationException(edges.Count > 2
                    ? $"Piece {i + 1} is too short to print: allow at least {Math.Ceiling((MinLines + 2 * feed) * 25.4 / model.Dpi)} mm between cuts."
                    : "The label is too short for this printer.");
            pieces.Add((edges[i] + feed, lines));
        }
        return pieces;
    }

    /// <summary>Shortest printable page, in raster lines (Brother: 34 at 180 dpi).</summary>
    const int MinLines = 34;

    /// <summary>One printed page: a window of a 1-bit raster, and what happens to the tape after it.</summary>
    /// <param name="Bits">One byte per dot (1 = black), row-major; Width runs along the tape, Height across it.</param>
    /// <param name="Cut">Full cut after this page.</param>
    /// <param name="Half">Half cut after this page (tape only, backing intact).</param>
    public sealed record PrintPage(byte[] Bits, int Width, int Height, int X0, int Lines, bool Cut, bool Half);

    /// <summary>Build the raster job for a sequence of pages.</summary>
    public static byte[] BuildJob(PrinterModel model, MediaInfo media, TapeGeometry g, PrintOptions o, IReadOnlyList<PrintPage> pages)
    {
        if (pages.Count == 0) throw new InvalidOperationException("Nothing to print.");
        int lineBytes = model.Pins / 8, feed = FeedDots(model, o), pins = g.PrintPins;
        var job = new List<byte>(pages.Sum(p => p.Lines) * (lineBytes + 3) + 64 * pages.Count);
        var line = new byte[lineBytes];

        for (int n = 0; n < pages.Count; n++)
        {
            var page = pages[n];
            bool last = n == pages.Count - 1;
            int lines = page.Lines, width = page.Width, height = page.Height;
            int y0 = (int)Math.Round((height - pins) / 2.0);
            job.AddRange([0x1B, 0x69, 0x61, 0x01]);              // raster mode
            job.AddRange([0x1B, 0x69, 0x21, 0x00]);              // automatic status notification on
            int index = model.LastPageFlag ? (last ? 2 : n == 0 ? 0 : 1) : (n == 0 ? 0 : 1);
            job.AddRange([0x1B, 0x69, 0x7A, 0x84, 0x00, (byte)media.WidthCode, 0x00,
                (byte)lines, (byte)(lines >> 8), (byte)(lines >> 16), (byte)(lines >> 24), (byte)index, 0x00]);
            bool cut = page.Cut && model.AutoCut;
            job.AddRange([0x1B, 0x69, 0x4D, (byte)(cut ? 0x40 : 0x00)]);   // various mode: auto cut
            if (model.CutEveryCmd) job.AddRange([0x1B, 0x69, 0x41, (byte)(cut ? 1 : 0)]); // cut after every label (0 = never)
            int adv = (page.Half && model.HalfCut ? 0x04 : 0) | (o.Chain ? 0 : 0x08);
            job.AddRange([0x1B, 0x69, 0x4B, (byte)adv]);          // advanced mode: half cut / no chain printing
            job.AddRange([0x1B, 0x69, 0x64, (byte)feed, (byte)(feed >> 8)]); // feed margin
            job.AddRange([0x4D, (byte)(model.PackBits ? 0x02 : 0x00)]); // TIFF (PackBits) compression, or none

            // One raster line per label column, left edge first. Pin p is bit (7 - p % 8)
            // of byte p / 8; the top of the label sits on the first-byte side.
            for (int x = page.X0; x < page.X0 + lines; x++)
            {
                Array.Clear(line);
                bool any = false;
                for (int i = 0; i < pins; i++)
                {
                    int y = y0 + i, p = g.FirstPin + o.PinOffset + i;
                    if (y < 0 || y >= height || x < 0 || x >= width || p < 0 || p >= model.Pins) continue;
                    if (page.Bits[y * width + x] == 0) continue;
                    line[p >> 3] |= (byte)(0x80 >> (p & 7));
                    any = true;
                }
                if (!model.PackBits)                              // uncompressed: always a full line, no Z
                {
                    job.AddRange([0x47, (byte)lineBytes, 0]);
                    job.AddRange(line);
                    continue;
                }
                if (!any) { job.Add(0x5A); continue; }            // Z: blank line
                int at = job.Count;
                job.AddRange([0x47, 0, 0]);
                PackBits(line, job);
                int len = job.Count - at - 3;
                job[at + 1] = (byte)len;
                job[at + 2] = (byte)(len >> 8);
            }
            job.Add(last ? (byte)0x1A : (byte)0x0C);              // print (and feed on the last page)
        }
        return job.ToArray();
    }

    /// <summary>PackBits, as TIFF and the Brother raster mode define it.</summary>
    public static void PackBits(ReadOnlySpan<byte> src, List<byte> dst)
    {
        int i = 0;
        while (i < src.Length)
        {
            int run = 1;
            while (i + run < src.Length && run < 128 && src[i + run] == src[i]) run++;
            if (run >= 2)
            {
                dst.Add((byte)(257 - run));
                dst.Add(src[i]);
                i += run;
                continue;
            }
            int start = i++;
            while (i < src.Length && i - start < 128 && !(i + 1 < src.Length && src[i] == src[i + 1])) i++;
            dst.Add((byte)(i - start - 1));
            for (int k = start; k < i; k++) dst.Add(src[k]);
        }
    }
}
