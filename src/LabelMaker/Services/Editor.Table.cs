using LabelMaker.Models;

namespace LabelMaker.Services;

/// <summary>What the renderer worked out for a table: column widths and font size per cell, in mm.</summary>
public sealed class TableMetrics
{
    public double[] ColW { get; set; } = [];
    public double[] Font { get; set; } = [];
}

/// <summary>A cell's box inside its (unrotated) table, in mm from the table's top-left.</summary>
public sealed record CellBox(double X, double Y, double W, double H, double Font);

// Tables: a grid of single-line text cells, edited in place one cell at a time.
public sealed partial class Editor
{
    public string? CellTableId { get; private set; }
    public int CellIndex { get; private set; }
    public TableNode? EditingTable => Find(CellTableId) as TableNode;

    public TableMetrics Metrics(TableNode t) => _js.Invoke<TableMetrics>("LM.tableLayout", Resolved(t));

    public CellBox CellRect(TableNode t, int index)
    {
        var m = Metrics(t);
        int cols = Math.Max(1, t.Cols), c = index % cols, r = index / cols;
        double rowH = t.H / Math.Max(1, t.Rows);
        return new CellBox(m.ColW.Take(c).Sum(), r * rowH, m.ColW.ElementAtOrDefault(c), rowH, m.Font.ElementAtOrDefault(index));
    }

    /// <summary>The cell under a label-space point.</summary>
    int CellAt(TableNode t, double wx, double wy)
    {
        var (lx, ly) = Geo.Rot(wx - t.Cx, wy - t.Cy, -t.Rotation);
        double x = lx + t.W / 2, y = ly + t.H / 2;
        var widths = Metrics(t).ColW;
        int c = 0;
        for (double edge = 0; c < t.Cols - 1 && x > edge + widths[c]; c++) edge += widths[c];
        int r = Math.Clamp((int)(y / (t.H / Math.Max(1, t.Rows))), 0, t.Rows - 1);
        return r * t.Cols + c;
    }

    public void BeginCellEdit(TableNode t, int index)
    {
        Normalize(t);
        CellTableId = t.Id;
        CellIndex = Math.Clamp(index, 0, t.Rows * t.Cols - 1);
        if (Single != t) Select(t);
        Refresh();
    }

    public void EndCellEdit()
    {
        if (CellTableId is null) return;
        CellTableId = null;
        Refresh();
    }

    /// <summary>Tab / Shift+Tab step through the cells; Enter goes down a row. Leaving the table ends editing.</summary>
    public void MoveCell(int delta)
    {
        if (EditingTable is not { } t) return;
        int next = CellIndex + delta;
        if (next < 0 || next >= t.Rows * t.Cols) { EndCellEdit(); return; }
        CellIndex = next;
        Refresh();
    }

    public void SetCell(TableNode t, int index, string text) => Mutate(() =>
    {
        Normalize(t);
        if (index >= 0 && index < t.Cells.Count) t.Cells[index] = text;
    }, $"cell:{t.Id}:{index}");

    /// <summary>Change the grid, keeping every cell that still exists.</summary>
    public void ResizeTable(TableNode t, int rows, int cols)
    {
        rows = Math.Clamp(rows, 1, TableNode.MaxRows);
        cols = Math.Clamp(cols, 1, TableNode.MaxCols);
        if (rows == t.Rows && cols == t.Cols) return;
        CellTableId = null;
        Mutate(() =>
        {
            var cells = new List<string>();
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    cells.Add(r < t.Rows && c < t.Cols ? t.Cell(r * t.Cols + c) : "");
            // columns keep their width, growing from the table's left edge; the height (the tape's) stays
            double cx = t.Cx, cy = t.Cy, w = t.W * cols / t.Cols;
            var (dx, dy) = Geo.Rot((w - t.W) / 2, 0, t.Rotation);
            t.W = w;
            t.X = cx + dx - w / 2; t.Y = cy + dy - t.H / 2;
            t.Rows = rows; t.Cols = cols; t.Cells = cells;
        });
    }

    static void Normalize(TableNode t)
    {
        int count = Math.Max(1, t.Rows) * Math.Max(1, t.Cols);
        while (t.Cells.Count < count) t.Cells.Add("");
        if (t.Cells.Count > count) t.Cells.RemoveRange(count, t.Cells.Count - count);
    }

    TableNode MakeTable(int rows, int cols)
    {
        rows = Math.Clamp(rows, 1, TableNode.MaxRows);
        cols = Math.Clamp(cols, 1, TableNode.MaxCols);
        var family = Settings.RecentFonts.FirstOrDefault() ?? "Roboto Condensed";
        _ = LoadFont(family);
        var t = new TableNode
        {
            Rows = rows, Cols = cols, FontFamily = family,
            H = Math.Max(2, Math.Round(UsableHeight, 1)),
            W = Math.Min(cols * 9, Math.Max(cols * 3, Doc.WidthMm - Doc.Margins.L - Doc.Margins.R)),
        };
        Normalize(t);
        return t;
    }
}
