using LabelMaker.Models;

namespace LabelMaker.Services;

public enum Order { Front, Forward, Backward, Back }
public enum DropPos { Above, Below, Inside }

// Document-level commands: create, delete, clipboard, grouping, ordering, alignment.
public sealed partial class Editor
{
    string? _clipboard;
    int _pasteCount;

    Rect LabelRect => new(0, 0, Doc.WidthMm, Doc.HeightMm);
    Rect MarginRect => new(Doc.Margins.L, Doc.Margins.T,
        Math.Max(1, Doc.WidthMm - Doc.Margins.L - Doc.Margins.R), Math.Max(1, Doc.HeightMm - Doc.Margins.T - Doc.Margins.B));

    /// <summary>Usable height between the top and bottom margins.</summary>
    public double UsableHeight => MarginRect.H;

    // ---- selection -----------------------------------------------------------------------

    public void SelectAll()
    {
        Selection.Clear();
        Selection.AddRange(ScopeList.Where(n => n.Visible && !n.Locked).Select(n => n.Id));
        Refresh();
    }

    public void ClearSelection()
    {
        Selection.Clear();
        Refresh();
    }

    /// <summary>Select from the layers panel: any depth, scope follows the node's parent.</summary>
    public void SelectFromTree(Node node, bool additive)
    {
        if (additive)
        {
            if (!Selection.Remove(node.Id)) Selection.Add(node.Id);
            // a node and one of its ancestors cannot both be selected
            var sel = SelectedNodes.ToList();
            foreach (var n in sel)
                if (PathTo(n).SkipLast(1).Any(sel.Contains) && n != node) Selection.Remove(n.Id);
            foreach (var a in PathTo(node).SkipLast(1)) Selection.Remove(a.Id);
        }
        else Select(node);
        ScopeId = Locate(node).Parent?.Id;
        Refresh();
    }

    // ---- create / delete -------------------------------------------------------------------

    public string AddAsset(string dataUrl)
    {
        var existing = Doc.Assets.FirstOrDefault(a => a.Value == dataUrl).Key;
        if (existing is not null) return existing;
        var id = Ids.New();
        Doc.Assets[id] = dataUrl;
        return id;
    }

    public void DeleteSelection()
    {
        if (Selection.Count == 0) return;
        Mutate(() =>
        {
            foreach (var n in SelectedNodes.ToList()) Locate(n).List.Remove(n);
            Selection.Clear();
            Prune();
        });
    }

    /// <summary>Drop groups left without children.</summary>
    void Prune()
    {
        bool Clean(List<Node> list)
        {
            foreach (var g in list.OfType<GroupNode>().ToList())
                if (Clean(g.Children)) list.Remove(g);
            return list.Count == 0;
        }
        Clean(Doc.Nodes);
        if (Find(ScopeId) is null) ScopeId = null;
        Selection.RemoveAll(id => Find(id) is null);
    }

    public void Nudge(double dx, double dy)
    {
        if (Selection.Count == 0 || SelectionLocked) return;
        Mutate(() => { foreach (var n in SelectedNodes) Geo.Translate(n, dx, dy); }, "nudge");
    }

    // ---- clipboard -----------------------------------------------------------------------

    List<Node> SelectedInOrder()
    {
        var order = All().ToList();
        return SelectedNodes.OrderBy(order.IndexOf).ToList();
    }

    public void Copy()
    {
        if (Selection.Count == 0) return;
        _clipboard = DocJson.Write(SelectedInOrder());
        _pasteCount = 0;
    }

    public void Cut()
    {
        Copy();
        DeleteSelection();
    }

    public void Paste()
    {
        if (_clipboard is null) return;
        _pasteCount++;
        InsertCopies(DocJson.CloneNodes(DocJson.Read<List<Node>>(_clipboard)!), 1.5 * _pasteCount);
    }

    public void Duplicate()
    {
        if (Selection.Count == 0) return;
        InsertCopies(DocJson.CloneNodes(SelectedInOrder()), 1.5);
    }

    void InsertCopies(List<Node> copies, double offset)
    {
        Mutate(() =>
        {
            foreach (var c in copies) { Geo.Translate(c, offset, offset); ScopeList.Add(c); }
            Select(copies.ToArray());
        });
    }

    // ---- grouping -------------------------------------------------------------------------

    public bool CanGroup => Selection.Count >= 2;
    public bool CanUngroup => SelectedNodes.Any(n => n is GroupNode);

    public void Group()
    {
        if (!CanGroup) return;
        Mutate(() =>
        {
            var nodes = SelectedInOrder();
            var (list, parent) = Locate(nodes[^1]);
            var marker = nodes[^1];
            var group = new GroupNode();
            list.Insert(list.IndexOf(marker), group);
            foreach (var n in nodes) Locate(n).List.Remove(n);
            group.Children.AddRange(nodes);
            Prune();
            Select(group);
            ScopeId = parent?.Id;
        });
    }

    public void Ungroup()
    {
        if (!CanUngroup) return;
        Mutate(() =>
        {
            var freed = new List<Node>();
            foreach (var g in SelectedNodes.OfType<GroupNode>().ToList())
            {
                var (list, _) = Locate(g);
                int i = list.IndexOf(g);
                list.RemoveAt(i);
                list.InsertRange(i, g.Children);
                freed.AddRange(g.Children);
                if (ScopeId == g.Id) ScopeId = null;
            }
            var keep = SelectedNodes.Where(n => n is not GroupNode).ToList();
            Select(keep.Concat(freed).ToArray());
        });
    }

    // ---- stacking order ---------------------------------------------------------------------

    public void Reorder(Order order)
    {
        if (Selection.Count == 0) return;
        Mutate(() =>
        {
            foreach (var grp in SelectedInOrder().GroupBy(n => Locate(n).List))
            {
                var list = grp.Key;
                var moving = grp.ToList();
                switch (order)
                {
                    case Order.Front: list.RemoveAll(moving.Contains); list.AddRange(moving); break;
                    case Order.Back: list.RemoveAll(moving.Contains); list.InsertRange(0, moving); break;
                    case Order.Forward:
                        for (int i = list.Count - 2; i >= 0; i--)
                            if (moving.Contains(list[i]) && !moving.Contains(list[i + 1])) (list[i], list[i + 1]) = (list[i + 1], list[i]);
                        break;
                    case Order.Backward:
                        for (int i = 1; i < list.Count; i++)
                            if (moving.Contains(list[i]) && !moving.Contains(list[i - 1])) (list[i], list[i - 1]) = (list[i - 1], list[i]);
                        break;
                }
            }
        });
    }

    /// <summary>
    /// Layers-panel drag and drop. "Above" in the panel means in front of the
    /// target, i.e. later in the list.
    /// </summary>
    public void MoveInTree(IReadOnlyList<string> ids, string targetId, DropPos pos)
    {
        var target = Find(targetId);
        var moving = ids.Select(Find).Where(n => n is not null).Cast<Node>().ToList();
        if (target is null || moving.Count == 0) return;
        // never drop a group into itself
        if (moving.Any(m => m == target || PathTo(target).Contains(m))) return;
        var order = All().ToList();
        moving = moving.OrderBy(order.IndexOf).ToList();
        Mutate(() =>
        {
            foreach (var m in moving) Locate(m).List.Remove(m);
            if (pos == DropPos.Inside && target is GroupNode g)
            {
                g.Children.AddRange(moving);
                g.Collapsed = false;
            }
            else
            {
                var (list, _) = Locate(target);
                int i = list.IndexOf(target);
                list.InsertRange(pos == DropPos.Above ? i + 1 : i, moving);
            }
            Prune();
            Select(moving.ToArray());
            ScopeId = Locate(moving[0]).Parent?.Id;
        });
    }

    public void SetVisible(Node n, bool visible) => Mutate(() => n.Visible = visible);
    public void SetLocked(Node n, bool locked) => Mutate(() => n.Locked = locked);
    /// <summary>Names are kept identifier-like so expressions can refer to them.</summary>
    public void Rename(Node n, string name) => Mutate(() => n.Name = CleanName(name));

    // ---- align / distribute -----------------------------------------------------------------

    Rect? AlignTarget(List<Node> nodes) => Settings.AlignTo switch
    {
        "label" => LabelRect,
        "margins" => MarginRect,
        "selection" => Geo.Bounds(nodes),
        _ => nodes.Count > 1 ? Geo.Bounds(nodes) : MarginRect,
    };

    /// <summary>mode: left, hcenter, right, top, vmiddle, bottom.</summary>
    public void Align(string mode)
    {
        var nodes = SelectedNodes.ToList();
        if (nodes.Count == 0 || AlignTarget(nodes) is not { } t) return;
        Mutate(() =>
        {
            foreach (var n in nodes)
            {
                if (Geo.Bounds(n) is not { } b) continue;
                double dx = mode switch { "left" => t.X - b.X, "hcenter" => t.Cx - b.Cx, "right" => t.R - b.R, _ => 0 };
                double dy = mode switch { "top" => t.Y - b.Y, "vmiddle" => t.Cy - b.Cy, "bottom" => t.B - b.B, _ => 0 };
                Geo.Translate(n, dx, dy);
            }
        });
    }

    /// <summary>Equalise the gaps between the selected objects along one axis.</summary>
    public void Distribute(bool horizontal)
    {
        var items = SelectedNodes.Select(n => (Node: n, B: Geo.Bounds(n))).Where(i => i.B is not null)
            .Select(i => (i.Node, B: i.B!.Value)).OrderBy(i => horizontal ? i.B.Cx : i.B.Cy).ToList();
        if (items.Count < 2) return;
        // with an explicit target (or only two objects) spread across it; otherwise keep the outer two fixed
        Rect span = Settings.AlignTo switch
        {
            "label" => LabelRect,
            "margins" => MarginRect,
            _ => items.Count == 2 ? MarginRect : Geo.Bounds(items.Select(i => i.Node))!.Value,
        };
        Mutate(() =>
        {
            double total = items.Sum(i => horizontal ? i.B.W : i.B.H);
            double gap = ((horizontal ? span.W : span.H) - total) / (items.Count - 1);
            double at = horizontal ? span.X : span.Y;
            foreach (var (node, b) in items)
            {
                if (horizontal) Geo.Translate(node, at - b.X, 0); else Geo.Translate(node, 0, at - b.Y);
                at += (horizontal ? b.W : b.H) + gap;
            }
        });
    }

    // ---- document settings ---------------------------------------------------------------------

    /// <summary>
    /// Switch tape the way the user does it: a label with content asks whether to scale it
    /// with the tape or keep its sizes. Returns false when the user cancelled.
    /// </summary>
    public async Task<bool> ChangeTape(string id)
    {
        if (id == Doc.MediaId) return true;
        if (Doc.Nodes.Count == 0) { SetMedia(id); return true; }
        int answer = await _shell.Ask("Change tape",
            $"Switching from {Media.Get(Doc.MediaId).Label} to {Media.Get(id).Label} tape. Scale the content to fit the new tape, or keep every object at its current size?",
            "Scale content", "Keep sizes");
        if (answer < 0) return false;
        SetMedia(id, scale: answer == 0);
        return true;
    }

    /// <summary>
    /// Switch tape. With <paramref name="scale"/> the content is resized in proportion to the
    /// printable height (and the label length with it); otherwise it keeps its size and is re-centred.
    /// </summary>
    public void SetMedia(string mediaId, bool scale = false)
    {
        var m = Media.Get(mediaId);
        Mutate(() =>
        {
            double oldH = Doc.HeightMm, oldTop = Doc.Margins.T, oldUsable = Math.Max(0.1, UsableHeight);
            Doc.MediaId = m.Id;
            Doc.HeightMm = m.WidthMm;
            Doc.Dpi = Brother.Model(Settings.PrinterModel).Dpi;
            var d = Media.DefaultMargins(m);
            Doc.Margins.T = d.T; Doc.Margins.B = d.B;
            double k = Math.Max(0.1, UsableHeight) / oldUsable;
            if (!scale || Math.Abs(k - 1) < 1e-6)
            {
                // keep content vertically centred on the new tape
                double dy = (m.WidthMm - oldH) / 2;
                foreach (var n in Doc.Nodes) Geo.Translate(n, 0, dy);
                return;
            }

            string layout = Doc.Layout.Kind;
            // a plain label scales from its left margin; cabling layouts keep their positions along the tape
            bool along = layout == "none";
            double left = Doc.Margins.L;
            foreach (var n in Geo.Walk(Doc.Nodes).Where(n => n is not GroupNode))
            {
                double cx = along ? left + (n.Cx - left) * k : n.Cx, cy = Doc.Margins.T + (n.Cy - oldTop) * k;
                n.W *= k; n.H *= k;
                n.X = cx - n.W / 2; n.Y = cy - n.H / 2;
                switch (n)
                {
                    case TextNode t: t.FontSize *= k; t.Padding *= k; break;
                    case ShapeNode s:
                        s.StrokeWidth *= k;
                        if (s.CornerRadius < 100) s.CornerRadius *= k;
                        if (s.Kind == ShapeKind.Line) { n.H = 1; n.Y = cy - 0.5; } // a line's box height is nominal
                        break;
                    case TableNode tb: tb.FontSize *= k; tb.Padding *= k; break;
                }
            }
            if (along)
            {
                if (!Doc.AutoWidth) Doc.WidthMm = Math.Max(5, Math.Round(left + (Doc.WidthMm - left - Doc.Margins.R) * k + Doc.Margins.R, 1));
                foreach (var c in Doc.Cuts) c.X = Math.Round(left + (c.X - left) * k, 1);
            }
            else if (layout == "ports") BuildPorts(null);
        });
        ZoomToFit();
    }
}
