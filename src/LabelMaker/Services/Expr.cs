using System.Globalization;
using System.Text;

namespace LabelMaker.Services;

/// <summary>
/// The small expression language behind {…} macros and bound numeric fields.
///
///   {name}                a variable, or a built-in: seq, date, time
///   {Title.w + 2}         a property of the element named "Title", with arithmetic
///   {seq:0000}            a .NET format after a colon (zero padding, decimals, date patterns)
///   {pad(seq, 4)}         functions: pad, upper, lower, round, len, min, max
///
/// Values are numbers, strings or dates; + joins strings when either side is not a number.
/// A {…} that does not evaluate is left in the text untouched.
/// </summary>
public static class Expr
{
    /// <summary>Resolve a name (member is null) or "name.member". Return null when unknown.</summary>
    public delegate object? Lookup(string name, string? member);

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Template(string text, Lookup lookup)
    {
        if (!text.Contains('{')) return text;
        var sb = new StringBuilder(text.Length + 16);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '{' && i + 1 < text.Length && text[i + 1] == '{') { sb.Append('{'); i += 2; continue; }
            if (c == '}' && i + 1 < text.Length && text[i + 1] == '}') { sb.Append('}'); i += 2; continue; }
            int end = c == '{' ? text.IndexOf('}', i + 1) : -1;
            if (end < 0) { sb.Append(c); i++; continue; }
            var inner = text[(i + 1)..end];
            var (expr, format) = SplitFormat(inner);
            if (TryEval(expr, lookup, out var value)) sb.Append(Format(value, format));
            else sb.Append('{').Append(inner).Append('}');
            i = end + 1;
        }
        return sb.ToString();
    }

    static (string Expr, string? Format) SplitFormat(string inner)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ':' && depth == 0) return (inner[..i], inner[(i + 1)..]);
        }
        return (inner, null);
    }

    public static string Format(object? value, string? format = null)
    {
        try
        {
            return value switch
            {
                double d => string.IsNullOrEmpty(format) ? d.ToString("0.###", Inv) : d.ToString(format, Inv),
                DateTime t => t.ToString(string.IsNullOrEmpty(format) ? "yyyy-MM-dd" : format, Inv),
                null => "",
                _ => value.ToString() ?? "",
            };
        }
        catch (FormatException) { return value?.ToString() ?? ""; }
    }

    public static bool TryEval(string expr, Lookup lookup, out object? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(expr)) return false;
        try
        {
            var p = new Parser(expr, lookup);
            value = p.Expression();
            p.SkipSpace();
            return p.AtEnd && value is not null;
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or OverflowException)
        {
            return false;
        }
    }

    public static bool TryNumber(string expr, Lookup lookup, out double number)
    {
        number = 0;
        if (!TryEval(expr, lookup, out var v) || !AsNumber(v, out number)) return false;
        return double.IsFinite(number);
    }

    static bool AsNumber(object? v, out double d)
    {
        d = 0;
        switch (v)
        {
            case double x: d = x; return true;
            case bool b: d = b ? 1 : 0; return true;
            case string s: return double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, Inv, out d);
            default: return false;
        }
    }

    sealed class Parser(string src, Lookup lookup)
    {
        int _i;

        public bool AtEnd => _i >= src.Length;

        public void SkipSpace() { while (_i < src.Length && char.IsWhiteSpace(src[_i])) _i++; }

        bool Eat(char c)
        {
            SkipSpace();
            if (_i < src.Length && src[_i] == c) { _i++; return true; }
            return false;
        }

        public object? Expression()
        {
            var left = Term();
            for (;;)
            {
                if (Eat('+'))
                {
                    var right = Term();
                    left = AsNumber(left, out var a) && AsNumber(right, out var b) ? a + b : Format(left) + Format(right);
                }
                else if (Eat('-')) left = Num(left) - Num(Term());
                else return left;
            }
        }

        object? Term()
        {
            var left = Unary();
            for (;;)
            {
                if (Eat('*')) left = Num(left) * Num(Unary());
                else if (Eat('/')) left = Num(left) / Num(Unary());
                else return left;
            }
        }

        object? Unary() => Eat('-') ? -Num(Unary()) : Primary();

        static double Num(object? v) => AsNumber(v, out var d) ? d : throw new FormatException("not a number");

        object? Primary()
        {
            SkipSpace();
            if (AtEnd) throw new FormatException("unexpected end");
            char c = src[_i];
            if (c == '(')
            {
                _i++;
                var v = Expression();
                if (!Eat(')')) throw new FormatException("missing )");
                return v;
            }
            if (c is '"' or '\'')
            {
                int end = src.IndexOf(c, _i + 1);
                if (end < 0) throw new FormatException("unterminated string");
                var s = src[(_i + 1)..end];
                _i = end + 1;
                return s;
            }
            if (char.IsDigit(c) || c == '.')
            {
                int start = _i;
                while (_i < src.Length && (char.IsDigit(src[_i]) || src[_i] == '.')) _i++;
                return double.Parse(src[start.._i], NumberStyles.Float, Inv);
            }
            var name = Ident();
            if (Eat('('))
            {
                var args = new List<object?>();
                if (!Eat(')'))
                {
                    do args.Add(Expression()); while (Eat(','));
                    if (!Eat(')')) throw new FormatException("missing )");
                }
                return Call(name.ToLowerInvariant(), args);
            }
            string? member = null;
            if (_i < src.Length && src[_i] == '.') { _i++; member = Ident(); }
            return lookup(name, member) ?? throw new FormatException($"unknown name {name}");
        }

        string Ident()
        {
            SkipSpace();
            int start = _i;
            while (_i < src.Length && (char.IsLetterOrDigit(src[_i]) || src[_i] == '_')) _i++;
            if (_i == start) throw new FormatException("name expected");
            return src[start.._i];
        }

        static object? Call(string fn, List<object?> a)
        {
            object? Arg(int i) => i < a.Count ? a[i] : throw new FormatException("missing argument");
            switch (fn)
            {
                case "pad":
                {
                    var s = Format(Arg(0));
                    var fill = a.Count > 2 ? Format(a[2]) : "0";
                    return s.PadLeft((int)Num(Arg(1)), fill.Length > 0 ? fill[0] : '0');
                }
                case "upper": return Format(Arg(0)).ToUpperInvariant();
                case "lower": return Format(Arg(0)).ToLowerInvariant();
                case "len": return (double)Format(Arg(0)).Length;
                case "round": return Math.Round(Num(Arg(0)), a.Count > 1 ? (int)Num(a[1]) : 0);
                case "min": return a.Count == 0 ? throw new FormatException() : a.Min(Num);
                case "max": return a.Count == 0 ? throw new FormatException() : a.Max(Num);
                default: throw new FormatException($"unknown function {fn}");
            }
        }
    }
}
