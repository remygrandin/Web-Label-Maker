using System.Globalization;
using System.Reflection;
using LabelMaker.Models;

namespace LabelMaker.Services;

// Variables, {…} macros, expression-bound properties, and cut marks.
public sealed partial class Editor
{
    LabelDocument? _evalDoc;
    int _evalDepth;

    /// <summary>When set, {seq} yields this instead of the document counter (used while printing copies).</summary>
    public double? SeqOverride { get; set; }

    LabelDocument EvalDoc => _evalDoc ?? Doc;

    // ---- name resolution ---------------------------------------------------------------

    object? Lookup(string name, string? member)
    {
        var doc = EvalDoc;
        if (member is null)
        {
            switch (name.ToLowerInvariant())
            {
                case "seq": return SeqOverride ?? doc.Counter.Value;
                case "date" or "now": return DateTime.Now;
                case "time": return DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
            }
            var v = doc.Variables.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (v is not null)
                return double.TryParse(v.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : Guarded(() => ResolveText(v.Value));
        }
        var node = Geo.Walk(doc.Nodes).FirstOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (node is null) return null;
        return Guarded(() => NodeValue(node, member));
    }

    // references can form loops (a names b, b names a): give up past a sane depth
    object? Guarded(Func<object?> eval)
    {
        if (_evalDepth > 8) return null;
        _evalDepth++;
        try { return eval(); }
        finally { _evalDepth--; }
    }

    object? NodeValue(Node n, string? member)
    {
        switch ((member ?? "value").ToLowerInvariant())
        {
            case "x": return n is GroupNode ? Geo.Bounds(n)?.X : n.X;
            case "y": return n is GroupNode ? Geo.Bounds(n)?.Y : n.Y;
            case "w" or "width": return Geo.Bounds(n)?.W;
            case "h" or "height": return Geo.Bounds(n)?.H;
            case "right": return Geo.Bounds(n)?.R;
            case "bottom": return Geo.Bounds(n)?.B;
            case "cx": return Geo.Bounds(n)?.Cx;
            case "cy": return Geo.Bounds(n)?.Cy;
            case "value" or "text":
                return n switch { TextNode t => ResolveText(t.Text), BarcodeNode b => ResolveText(b.Value), _ => null };
        }
        var p = n.GetType().GetProperty(member!, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return p?.GetValue(n) switch
        {
            double d => d,
            int i => (double)i,
            bool b => b ? 1.0 : 0.0,
            string s => s,
            Enum e => e.ToString(),
            _ => null,
        };
    }

    public string ResolveText(string text) => text.Contains('{') ? Expr.Template(text, Lookup) : text;

    /// <summary>The node as it should be drawn: macros in its text or code value expanded.</summary>
    Node Resolved(Node n)
    {
        switch (n)
        {
            case TextNode t when t.Text.Contains('{'):
            {
                var c = (TextNode)t.Shallow();
                c.Text = ResolveText(t.Text);
                return c;
            }
            case BarcodeNode b when b.Value.Contains('{'):
            {
                var c = (BarcodeNode)b.Shallow();
                c.Value = ResolveText(b.Value);
                return c;
            }
            case TableNode tb when tb.Cells.Any(s => s.Contains('{')):
            {
                var c = (TableNode)tb.Shallow();
                c.Cells = tb.Cells.Select(ResolveText).ToList();
                return c;
            }
            default: return n;
        }
    }

    public string? BarcodeError(BarcodeNode n) => Barcodes.Error((BarcodeNode)Resolved(n));

    public bool UsesSeq => All().Any(n => n is TextNode t && t.Text.Contains("seq", StringComparison.OrdinalIgnoreCase)
        || n is BarcodeNode b && b.Value.Contains("seq", StringComparison.OrdinalIgnoreCase)
        || n is TableNode tb && tb.Cells.Any(s => s.Contains("seq", StringComparison.OrdinalIgnoreCase)));

    // ---- bound numeric properties -----------------------------------------------------------

    static PropertyInfo? Prop(Node n, string name) =>
        n.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

    public static double GetNumber(Node n, string prop) => Prop(n, prop)?.GetValue(n) switch { double d => d, int i => i, _ => 0 };

    public static void SetNumber(Node n, string prop, double value)
    {
        var p = Prop(n, prop);
        if (p is null || !double.IsFinite(value)) return;
        if (p.Name is nameof(Node.W) or nameof(Node.H)) value = Math.Max(0.3, value);
        if (p.PropertyType == typeof(int)) p.SetValue(n, (int)Math.Round(value));
        else if (p.PropertyType == typeof(double)) p.SetValue(n, value);
    }

    public static string? BindingOf(Node n, string prop) => n.Bindings?.GetValueOrDefault(prop);

    public bool TryNumber(string expr, out double value) => Expr.TryNumber(expr, Lookup, out value);

    /// <summary>Drive a numeric property from an expression, or pass null to make it a plain value again.</summary>
    public void SetBinding(Node n, string prop, string? expr)
    {
        if (expr is null)
        {
            n.Bindings?.Remove(prop);
            if (n.Bindings is { Count: 0 }) n.Bindings = null;
        }
        else (n.Bindings ??= new())[prop] = expr;
    }

    void ApplyBindings(LabelDocument doc)
    {
        var bound = Geo.Walk(doc.Nodes).Where(n => n.Bindings is { Count: > 0 }).ToList();
        if (bound.Count == 0) return;
        // twice, so a binding that depends on another bound value settles regardless of order
        for (int pass = 0; pass < 2; pass++)
            foreach (var n in bound)
                foreach (var (prop, expr) in n.Bindings!)
                    if (Expr.TryNumber(expr, Lookup, out var v)) SetNumber(n, prop, v);
    }

    // ---- variables ------------------------------------------------------------------------------

    public void AddVariable() => Mutate(() =>
    {
        int i = Doc.Variables.Count + 1;
        string name;
        do name = $"var{i++}"; while (Doc.Variables.Any(v => v.Name == name));
        Doc.Variables.Add(new Variable { Name = name, Value = "" });
    });

    public void RemoveVariable(Variable v) => Mutate(() => Doc.Variables.Remove(v));

    public static string CleanName(string raw)
    {
        var s = new string(raw.Trim().Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        return s.Length > 0 && char.IsDigit(s[0]) ? "_" + s : s;
    }

    // ---- cut marks ------------------------------------------------------------------------------

    string? _dragCutId;

    public void AddCut(double x, bool half) => Mutate(() =>
    {
        x = Math.Clamp(Math.Round(x * 2) / 2, 1, Math.Max(1, Doc.WidthMm - 1));
        Doc.Cuts.Add(new CutMark { X = x, Half = half });
        Doc.Cuts.Sort((a, b) => a.X.CompareTo(b.X));
    });

    public void RemoveCut(CutMark c) => Mutate(() => Doc.Cuts.Remove(c));

    /// <summary>Cut marks are grabbed by their tab above the label.</summary>
    CutMark? HitCut(double sx, double sy)
    {
        if (sy < PanY - 18 || sy > PanY + 2) return null;
        return Doc.Cuts.FirstOrDefault(c => Math.Abs(PanX + c.X * Zoom - sx) <= 7);
    }
}
