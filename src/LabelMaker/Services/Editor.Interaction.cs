using LabelMaker.Models;

namespace LabelMaker.Services;

// Pointer and keyboard handling for the canvas.
public sealed partial class Editor
{
    enum Drag { None, Pan, Marquee, PendingMove, Move, Resize, Rotate, LineEnd }
    enum HandleKind { Resize, Rotate, LineA, LineB }

    readonly record struct Handle(double Sx, double Sy, int Hx, int Hy, HandleKind Kind);
    sealed record LeafStart(Node Node, double X, double Y, double W, double H, double Rot, double Font)
    {
        public double Cx => X + W / 2;
        public double Cy => Y + H / 2;
    }

    Drag _drag;
    double _sx, _sy, _wx0, _wy0, _pan0X, _pan0Y, _angle0;
    int _hx, _hy;
    bool _lineEndB, _changed, _forceUniform;
    Frame _f0;
    Rect _b0;
    List<LeafStart> _starts = new();
    string? _clickTarget, _hoverId;
    double[]? _marquee;
    List<GuideLine> _guides = new();

    // ---- selection geometry ------------------------------------------------------------

    Frame? SelFrame()
    {
        var nodes = SelectedNodes.ToList();
        if (nodes.Count == 0) return null;
        if (nodes.Count == 1 && nodes[0] is not GroupNode)
        {
            var n = nodes[0];
            return new Frame(n.Cx, n.Cy, n.W, n.H, n.Rotation);
        }
        return Geo.Bounds(nodes) is { } b ? new Frame(b.Cx, b.Cy, b.W, b.H, 0) : null;
    }

    bool SelectionLocked => SelectedNodes.Any(n => PathTo(n).Any(p => p.Locked));
    bool SelIsLine => Single is ShapeNode { Kind: ShapeKind.Line };

    List<Handle> Handles()
    {
        var list = new List<Handle>();
        if (SelFrame() is not { } f || SelectionLocked) return list;
        Handle At(double lx, double ly, int hx, int hy, HandleKind k)
        {
            var (x, y) = Geo.Rot(lx, ly, f.Rot);
            return new Handle(PanX + (f.Cx + x) * Zoom, PanY + (f.Cy + y) * Zoom, hx, hy, k);
        }
        if (SelIsLine)
        {
            list.Add(At(-f.W / 2, 0, -1, 0, HandleKind.LineA));
            list.Add(At(f.W / 2, 0, 1, 0, HandleKind.LineB));
            return list;
        }
        bool wide = f.W * Zoom >= 28, tall = f.H * Zoom >= 28;
        for (int hy = -1; hy <= 1; hy++)
            for (int hx = -1; hx <= 1; hx++)
            {
                if (hx == 0 && hy == 0) continue;
                if (hx == 0 && !wide) continue;
                if (hy == 0 && !tall) continue;
                list.Add(At(hx * f.W / 2, hy * f.H / 2, hx, hy, HandleKind.Resize));
            }
        list.Add(At(0, -f.H / 2 - 22 / Zoom, 0, 0, HandleKind.Rotate));
        return list;
    }

    Handle? HitHandle(double sx, double sy)
    {
        Handle? best = null;
        double bestD = 8 * 8;
        foreach (var h in Handles())
        {
            double d = (h.Sx - sx) * (h.Sx - sx) + (h.Sy - sy) * (h.Sy - sy);
            if (d <= bestD) { bestD = d; best = h; }
        }
        return best;
    }

    Node? HitLeaf(double wx, double wy)
    {
        double tol = 4 / Zoom;
        foreach (var (n, locked) in VisibleLeaves().Reverse())
            if (!locked && Geo.Contains(n, wx, wy, tol)) return n;
        return null;
    }

    /// <summary>The object a click on this leaf selects: its ancestor sitting directly in the current scope.</summary>
    Node Resolve(Node leaf)
    {
        var path = PathTo(leaf);
        int i = ScopeId is null ? -1 : path.FindIndex(p => p.Id == ScopeId);
        if (i < 0) { ScopeId = null; return path[0]; }
        return path[Math.Min(i + 1, path.Count - 1)];
    }

    void Select(params Node[] nodes)
    {
        Selection.Clear();
        Selection.AddRange(nodes.Select(n => n.Id));
    }

    static bool NeedsUniform(Node n) => n is TextNode { Mode: TextMode.Auto } or BarcodeNode { Is2D: true } or ImageNode { LockAspect: true };

    void BeginTransform()
    {
        _f0 = SelFrame() ?? default;
        _starts = SelectedNodes.SelectMany(Geo.Leaves)
            .Select(n => new LeafStart(n, n.X, n.Y, n.W, n.H, n.Rotation, (n as TextNode)?.FontSize ?? 0)).ToList();
        _b0 = Geo.Bounds(SelectedNodes) ?? default;
        _changed = false;
        _forceUniform = _starts.Count == 1 && Single is not GroupNode
            ? NeedsUniform(_starts[0].Node)
            : _starts.Any(s => NeedsUniform(s.Node) || Math.Abs(s.Rot % 90) > 0.01);
    }

    void MarkChanged()
    {
        if (_changed) return;
        _changed = true;
        PushUndo();
    }

    // ---- snapping ----------------------------------------------------------------------

    (List<double> Xs, List<double> Ys) SnapLines()
    {
        var xs = new List<double>();
        var ys = new List<double>();
        if (Settings.SnapLabel)
        {
            xs.AddRange([0, Doc.WidthMm / 2, Doc.WidthMm]);
            ys.AddRange([0, Doc.HeightMm / 2, Doc.HeightMm]);
        }
        if (Settings.SnapMargins)
        {
            var m = Doc.Margins;
            xs.AddRange([m.L, Doc.WidthMm - m.R]);
            ys.AddRange([m.T, Doc.HeightMm - m.B]);
        }
        if (Settings.SnapObjects)
        {
            var moving = _starts.Select(s => s.Node).ToHashSet();
            foreach (var (n, _) in VisibleLeaves())
            {
                if (moving.Contains(n)) continue;
                var b = Geo.Aabb(n);
                xs.AddRange([b.X, b.Cx, b.R]);
                ys.AddRange([b.Y, b.Cy, b.B]);
            }
        }
        return (xs, ys);
    }

    /// <summary>Smallest correction bringing one of <paramref name="edges"/> onto a snap line.</summary>
    static double? SnapDelta(IEnumerable<double> edges, List<double> lines, double tol, out double line)
    {
        double? best = null;
        line = 0;
        foreach (var e in edges)
            foreach (var l in lines)
            {
                double d = l - e;
                if (Math.Abs(d) <= tol && (best is null || Math.Abs(d) < Math.Abs(best.Value))) { best = d; line = l; }
            }
        return best;
    }

    // ---- pointer -----------------------------------------------------------------------

    public void PointerDown(double sx, double sy, int button, int mods)
    {
        bool shift = (mods & 1) != 0, space = (mods & 8) != 0;
        _sx = sx; _sy = sy;
        (_wx0, _wy0) = ToWorld(sx, sy);
        _guides = new();
        _hoverId = null;
        _clickTarget = null;

        if (button == 1 || button == 2 || space) { _drag = Drag.Pan; _pan0X = PanX; _pan0Y = PanY; return; }
        if (button != 0) return;

        if (HitHandle(sx, sy) is { } h)
        {
            BeginTransform();
            switch (h.Kind)
            {
                case HandleKind.Resize: _drag = Drag.Resize; _hx = h.Hx; _hy = h.Hy; break;
                case HandleKind.Rotate: _drag = Drag.Rotate; _angle0 = Math.Atan2(_wy0 - _f0.Cy, _wx0 - _f0.Cx); break;
                default: _drag = Drag.LineEnd; _lineEndB = h.Kind == HandleKind.LineB; break;
            }
            return;
        }

        if (HitLeaf(_wx0, _wy0) is { } leaf)
        {
            var target = Resolve(leaf);
            if (shift)
            {
                if (!Selection.Remove(target.Id)) Selection.Add(target.Id);
                if (!Selection.Contains(target.Id)) { Refresh(); return; }
            }
            else if (!Selection.Contains(target.Id)) Select(target);
            else _clickTarget = target.Id;
            BeginTransform();
            _drag = SelectionLocked ? Drag.None : Drag.PendingMove;
            Refresh();
            return;
        }

        if (!shift) { Selection.Clear(); ScopeId = null; }
        _drag = Drag.Marquee;
        Refresh();
    }

    /// <summary>Returns the CSS cursor to show.</summary>
    public string PointerMove(double sx, double sy, int mods)
    {
        bool shift = (mods & 1) != 0, ctrl = (mods & 2) != 0;
        var (wx, wy) = ToWorld(sx, sy);
        Cursor = (wx, wy);

        switch (_drag)
        {
            case Drag.None:
            {
                string cursor = "default";
                string? hover = null;
                if (HitHandle(sx, sy) is { } h) cursor = CursorFor(h);
                else if (HitLeaf(wx, wy) is { } leaf)
                {
                    var path = PathTo(leaf);
                    int i = ScopeId is null ? -1 : path.FindIndex(p => p.Id == ScopeId);
                    hover = (i < 0 ? path[0] : path[Math.Min(i + 1, path.Count - 1)]).Id;
                    if (Selection.Contains(hover)) cursor = "move";
                }
                if (hover != _hoverId) { _hoverId = hover; Render(); }
                CursorMoved?.Invoke();
                return cursor;
            }
            case Drag.Pan:
                PanX = _pan0X + sx - _sx; PanY = _pan0Y + sy - _sy;
                Render();
                return "grabbing";
            case Drag.Marquee:
                _marquee = [Math.Min(_wx0, wx), Math.Min(_wy0, wy), Math.Abs(wx - _wx0), Math.Abs(wy - _wy0)];
                Render();
                return "crosshair";
            case Drag.PendingMove:
                if (Math.Abs(sx - _sx) + Math.Abs(sy - _sy) < 4) return "move";
                _drag = Drag.Move;
                goto case Drag.Move;
            case Drag.Move:
                MoveBy(wx - _wx0, wy - _wy0, shift, !ctrl);
                break;
            case Drag.Resize:
                ApplyResize(ResizeFrame(wx, wy, shift || _forceUniform, !ctrl));
                break;
            case Drag.Rotate:
                RotateTo(wx, wy, shift);
                break;
            case Drag.LineEnd:
                MoveLineEnd(wx, wy, shift);
                break;
        }
        MarkChangedIfDragging();
        Render();
        Changed?.Invoke();
        return _drag switch { Drag.Move => "move", Drag.Rotate => "grabbing", _ => "crosshair" };
    }

    /// <summary>Raised on every hover move so the status bar can track the cursor.</summary>
    public event Action? CursorMoved;

    void MarkChangedIfDragging() { /* undo is pushed lazily inside the transform appliers */ }

    public void PointerUp(double sx, double sy, int mods)
    {
        bool shift = (mods & 1) != 0;
        var drag = _drag;
        _drag = Drag.None;
        _guides = new();
        switch (drag)
        {
            case Drag.Marquee when _marquee is { } m:
            {
                var box = new Rect(m[0], m[1], m[2], m[3]);
                var hits = ScopeList.Where(n => n.Visible && !n.Locked && Geo.Bounds(n) is { } b && b.Intersects(box)).Select(n => n.Id);
                foreach (var id in hits) if (!Selection.Contains(id)) Selection.Add(id);
                break;
            }
            case Drag.PendingMove when _clickTarget is not null && !shift && Selection.Count > 1:
                Selection.Clear();
                Selection.Add(_clickTarget);
                break;
        }
        _marquee = null;
        if (_changed) { _changed = false; Touch(); } else Refresh();
    }

    public void DoubleClick(double sx, double sy)
    {
        var (wx, wy) = ToWorld(sx, sy);
        if (HitLeaf(wx, wy) is not { } leaf) return;
        var target = Resolve(leaf);
        if (target is GroupNode g)
        {
            ScopeId = g.Id;
            Select(Resolve(leaf));
            Refresh();
        }
        else if (target is TextNode)
        {
            Select(target);
            Refresh();
            TextFocusRequested?.Invoke();
        }
    }

    public void Wheel(double sx, double sy, double dx, double dy, int mods)
    {
        if ((mods & 2) != 0) { ZoomAt(sx, sy, Math.Exp(-dy * 0.0022)); return; }
        if ((mods & 1) != 0 && dx == 0) (dx, dy) = (dy, 0);
        PanX -= dx; PanY -= dy;
        Render();
    }

    string CursorFor(Handle h)
    {
        if (h.Kind == HandleKind.Rotate) return "grab";
        if (h.Kind != HandleKind.Resize) return "crosshair";
        double a = Geo.Norm(Math.Atan2(h.Hy, h.Hx) * 180 / Math.PI + (SelFrame()?.Rot ?? 0));
        int oct = (int)Math.Round(a / 45) % 4;
        return oct switch { 0 => "ew-resize", 1 => "nwse-resize", 2 => "ns-resize", _ => "nesw-resize" };
    }

    // ---- transforms ---------------------------------------------------------------------

    void MoveBy(double dx, double dy, bool axisLock, bool snap)
    {
        if (axisLock) { if (Math.Abs(dx) > Math.Abs(dy)) dy = 0; else dx = 0; }
        _guides = new();
        if (snap)
        {
            var (xs, ys) = SnapLines();
            double tol = 6 / Zoom;
            var b = new Rect(_b0.X + dx, _b0.Y + dy, _b0.W, _b0.H);
            if (SnapDelta([b.X, b.Cx, b.R], xs, tol, out var lx) is { } ddx) { dx += ddx; _guides.Add(new GuideLine(true, lx)); }
            if (SnapDelta([b.Y, b.Cy, b.B], ys, tol, out var ly) is { } ddy) { dy += ddy; _guides.Add(new GuideLine(false, ly)); }
        }
        MarkChanged();
        foreach (var s in _starts) { s.Node.X = s.X + dx; s.Node.Y = s.Y + dy; }
    }

    Frame ResizeFrame(double wx, double wy, bool uniform, bool snap)
    {
        var f = _f0;
        _guides = new();
        if (snap && f.Rot == 0 && !uniform)
        {
            var (xs, ys) = SnapLines();
            double tol = 6 / Zoom;
            if (_hx != 0 && SnapDelta([wx], xs, tol, out var lx) is { } ddx) { wx += ddx; _guides.Add(new GuideLine(true, lx)); }
            if (_hy != 0 && SnapDelta([wy], ys, tol, out var ly) is { } ddy) { wy += ddy; _guides.Add(new GuideLine(false, ly)); }
        }
        var (qx, qy) = Geo.Rot(wx - f.Cx, wy - f.Cy, -f.Rot);
        double ax = -_hx * f.W / 2, ay = -_hy * f.H / 2;
        const double min = 0.3;
        double w = _hx != 0 ? Math.Max(min, (qx - ax) * _hx) : f.W;
        double h = _hy != 0 ? Math.Max(min, (qy - ay) * _hy) : f.H;
        if (uniform && f.W > 0 && f.H > 0)
        {
            double s = _hx != 0 && _hy != 0 ? Math.Max(w / f.W, h / f.H) : _hx != 0 ? w / f.W : h / f.H;
            w = f.W * s; h = f.H * s;
        }
        var (ox, oy) = Geo.Rot(ax + _hx * w / 2, ay + _hy * h / 2, f.Rot);
        return new Frame(f.Cx + ox, f.Cy + oy, w, h, f.Rot);
    }

    void ApplyResize(Frame nf)
    {
        if (_f0.W <= 0 || _f0.H <= 0) return;
        MarkChanged();
        double sx = nf.W / _f0.W, sy = nf.H / _f0.H;
        if (_starts.Count == 1 && Single is not GroupNode)
        {
            var s = _starts[0];
            s.Node.W = nf.W; s.Node.H = nf.H;
            s.Node.X = nf.Cx - nf.W / 2; s.Node.Y = nf.Cy - nf.H / 2;
            if (s.Node is TextNode { Mode: TextMode.Auto } t) t.FontSize = Math.Max(1, s.Font * sy);
            return;
        }
        // several objects: scale every box about the fixed corner of the selection frame
        double ax = _f0.Cx - _hx * _f0.W / 2, ay = _f0.Cy - _hy * _f0.H / 2;
        if (_hx == 0) ax = _f0.Cx;
        if (_hy == 0) ay = _f0.Cy;
        foreach (var s in _starts)
        {
            bool quarter = (int)Math.Round(s.Rot / 90) % 2 != 0;
            double w = s.W * (quarter ? sy : sx), h = s.H * (quarter ? sx : sy);
            double cx = ax + (s.Cx - ax) * sx, cy = ay + (s.Cy - ay) * sy;
            s.Node.W = w; s.Node.H = h; s.Node.X = cx - w / 2; s.Node.Y = cy - h / 2;
            if (s.Node is TextNode { Mode: TextMode.Auto } t) t.FontSize = Math.Max(1, s.Font * Math.Min(sx, sy));
        }
    }

    void RotateTo(double wx, double wy, bool snap15)
    {
        double delta = (Math.Atan2(wy - _f0.Cy, wx - _f0.Cx) - _angle0) * 180 / Math.PI;
        if (snap15)
        {
            double target = Math.Round((_f0.Rot + delta) / 15) * 15;
            delta = target - _f0.Rot;
        }
        else
        {
            // gentle magnetism to right angles
            double r = Geo.Norm(_f0.Rot + delta), near = Math.Round(r / 90) * 90;
            if (Math.Abs(r - near) < 2.5) delta += near - r;
        }
        MarkChanged();
        foreach (var s in _starts)
        {
            var (ox, oy) = Geo.Rot(s.Cx - _f0.Cx, s.Cy - _f0.Cy, delta);
            s.Node.X = _f0.Cx + ox - s.W / 2;
            s.Node.Y = _f0.Cy + oy - s.H / 2;
            s.Node.Rotation = Geo.Norm(s.Rot + delta);
        }
    }

    void MoveLineEnd(double wx, double wy, bool snap15)
    {
        var s = _starts[0];
        var (ex, ey) = Geo.Rot(s.W / 2, 0, s.Rot);
        // the end that stays put
        double fx = _lineEndB ? s.Cx - ex : s.Cx + ex, fy = _lineEndB ? s.Cy - ey : s.Cy + ey;
        double ang = Math.Atan2(wy - fy, wx - fx) * 180 / Math.PI, len = Math.Max(0.5, Math.Sqrt((wx - fx) * (wx - fx) + (wy - fy) * (wy - fy)));
        if (snap15)
        {
            ang = Math.Round(ang / 15) * 15;
            var (px, py) = Geo.Rot(len, 0, ang);
            wx = fx + px; wy = fy + py;
        }
        else if (Math.Abs(ang - Math.Round(ang / 90) * 90) < 2)
        {
            ang = Math.Round(ang / 90) * 90;
            var (px, py) = Geo.Rot(len, 0, ang);
            wx = fx + px; wy = fy + py;
        }
        MarkChanged();
        var n = s.Node;
        n.W = len;
        n.X = (fx + wx) / 2 - len / 2;
        n.Y = (fy + wy) / 2 - n.H / 2;
        n.Rotation = Geo.Norm(_lineEndB ? ang : ang + 180);
    }

    // ---- keyboard -----------------------------------------------------------------------

    public bool Key(string key, int mods)
    {
        bool shift = (mods & 1) != 0, ctrl = (mods & 2) != 0;
        string k = key.Length == 1 ? key.ToLowerInvariant() : key;
        if (ctrl)
        {
            switch (k)
            {
                case "z": if (shift) Redo(); else Undo(); return true;
                case "y": Redo(); return true;
                case "a": SelectAll(); return true;
                case "c": Copy(); return true;
                case "x": Cut(); return true;
                case "d": Duplicate(); return true;
                case "g": if (shift) Ungroup(); else Group(); return true;
                case "s": SaveRequested?.Invoke(); return true;
                case "0": ZoomToFit(); return true;
                case "=" or "+": ZoomBy(1.25); return true;
                case "-": ZoomBy(0.8); return true;
                case "]": Reorder(shift ? Order.Front : Order.Forward); return true;
                case "[": Reorder(shift ? Order.Back : Order.Backward); return true;
            }
            return false;
        }
        double step = shift ? 1 : 0.25;
        switch (k)
        {
            case "Delete" or "Backspace": DeleteSelection(); return true;
            case "ArrowLeft": Nudge(-step, 0); return true;
            case "ArrowRight": Nudge(step, 0); return true;
            case "ArrowUp": Nudge(0, -step); return true;
            case "ArrowDown": Nudge(0, step); return true;
            case "Escape":
                if (Find(ScopeId) is { } scope) { ScopeId = Locate(scope).Parent?.Id; Select(scope); }
                else Selection.Clear();
                Refresh();
                return true;
        }
        return false;
    }
}
