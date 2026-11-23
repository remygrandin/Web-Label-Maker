namespace LabelMaker.Models;

public readonly record struct Rect(double X, double Y, double W, double H)
{
    public double R => X + W;
    public double B => Y + H;
    public double Cx => X + W / 2;
    public double Cy => Y + H / 2;

    public Rect Union(Rect o)
    {
        double x = Math.Min(X, o.X), y = Math.Min(Y, o.Y);
        return new Rect(x, y, Math.Max(R, o.R) - x, Math.Max(B, o.B) - y);
    }

    public bool Intersects(Rect o) => X < o.R && R > o.X && Y < o.B && B > o.Y;
}

/// <summary>An oriented box: centre, size and rotation in degrees.</summary>
public readonly record struct Frame(double Cx, double Cy, double W, double H, double Rot);

public static class Geo
{
    public static (double X, double Y) Rot(double x, double y, double deg)
    {
        if (deg == 0) return (x, y);
        double r = deg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        return (x * c - y * s, x * s + y * c);
    }

    public static double Norm(double deg)
    {
        deg %= 360;
        if (deg < 0) deg += 360;
        return Math.Round(deg, 3) >= 360 ? 0 : deg;
    }

    public static IEnumerable<Node> Leaves(Node n)
    {
        if (n is GroupNode g)
        {
            foreach (var c in g.Children)
                foreach (var l in Leaves(c)) yield return l;
        }
        else yield return n;
    }

    public static IEnumerable<Node> Walk(IEnumerable<Node> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            if (n is GroupNode g)
                foreach (var c in Walk(g.Children)) yield return c;
        }
    }

    /// <summary>Axis-aligned bounds of a leaf, accounting for rotation.</summary>
    public static Rect Aabb(Node n)
    {
        if (n.Rotation == 0) return new Rect(n.X, n.Y, n.W, n.H);
        double r = n.Rotation * Math.PI / 180, c = Math.Abs(Math.Cos(r)), s = Math.Abs(Math.Sin(r));
        double w = n.W * c + n.H * s, h = n.W * s + n.H * c;
        return new Rect(n.Cx - w / 2, n.Cy - h / 2, w, h);
    }

    public static Rect? Bounds(Node n)
    {
        if (n is not GroupNode g) return Aabb(n);
        return Bounds(g.Children);
    }

    public static Rect? Bounds(IEnumerable<Node> nodes)
    {
        Rect? acc = null;
        foreach (var n in nodes)
        {
            var b = Bounds(n);
            if (b is null) continue;
            acc = acc is null ? b : acc.Value.Union(b.Value);
        }
        return acc;
    }

    public static bool Contains(Node leaf, double x, double y, double tol)
    {
        var (lx, ly) = Rot(x - leaf.Cx, y - leaf.Cy, -leaf.Rotation);
        return Math.Abs(lx) <= Math.Max(leaf.W / 2, tol) && Math.Abs(ly) <= Math.Max(leaf.H / 2, tol);
    }

    public static void Translate(Node n, double dx, double dy)
    {
        foreach (var l in Leaves(n)) { l.X += dx; l.Y += dy; }
    }
}
