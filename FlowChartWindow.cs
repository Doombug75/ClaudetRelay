using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClaudetRelay.Models;
using ClaudetRelay.Services;

namespace ClaudetRelay;

/// <summary>
/// Flowchart (Programmablaufplan) canvas for sketching the logic of a function/method.
/// Classic shapes (start/end, process, decision, I/O, subroutine) joined by labelled arrows.
/// </summary>
public class FlowChartWindow : Window
{
    private readonly string _projFolder;
    private readonly string _key;          // entityId  or  entityId#methodId
    private readonly string? _themePath;
    private FlowChartData   _data;

    private Canvas?       _canvas;
    private ScrollViewer? _scroll;

    private readonly Dictionary<string, FrameworkElement> _nodeViews = new(); // node id → container
    private readonly Dictionary<string, List<UIElement>>  _connViews = new(); // conn id → visuals

    private enum EditMode { Select, Connect, Remove }
    private EditMode _mode = EditMode.Select;
    private bool    _connectMode => _mode == EditMode.Connect;
    private string? _connectFromId    = null;
    private Line?   _rubberBand        = null;
    private readonly HashSet<string> _selected = new();
    private double _zoom = 1.0;

    // Mode toolbar buttons (for active-state styling)
    private Button? _selectBtn, _connectBtn, _removeBtn;

    public FlowChartWindow(string projFolder, string key, string title, string? themePath)
    {
        _projFolder = projFolder;
        _key        = key;
        _themePath  = themePath;
        _data       = FlowChartService.Load(projFolder, key);
        if (string.IsNullOrEmpty(_data.Title)) _data.Title = title;

        Title                 = "🔁  Flow — " + (string.IsNullOrEmpty(title) ? "Untitled" : title);
        Width                 = 1100;
        Height                = 760;
        MinWidth              = 560;
        MinHeight             = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        if (!string.IsNullOrWhiteSpace(themePath))
        {
            try
            {
                var dict = OxsuitLoader.Load(themePath);
                if (dict is not null) Resources.MergedDictionaries.Add(dict);
            }
            catch { }
        }
        SetResourceReference(BackgroundProperty, "ContentBgBrush");
        SourceInitialized += (_, _) => ParticipantsWindow.TryApplyTitleBarTo(this);
        Loaded            += (_, _) => Build();
    }

    private void Save() => FlowChartService.Save(_projFolder, _key, _data);

    // ── Build ──────────────────────────────────────────────────────────────

    private void Build()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Content = root;

        var toolbar = BuildToolbar();
        Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);

        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto
        };
        Grid.SetRow(_scroll, 1);
        root.Children.Add(_scroll);

        _canvas = new Canvas { Width = 3000, Height = 2000, ClipToBounds = false };
        _canvas.SetResourceReference(Canvas.BackgroundProperty, "ContentBgBrush");
        _scroll.Content = _canvas;

        _canvas.MouseLeftButtonDown += (_, e) =>
        {
            if (_connectMode) return;
            _selected.Clear(); RefreshSelection();
        };
        _canvas.MouseMove += (_, e) =>
        {
            if (_connectMode && _connectFromId is not null && _rubberBand is not null)
            {
                var c = NodeCenter(_connectFromId);
                if (c is not null) { _rubberBand.X1 = c.Value.X; _rubberBand.Y1 = c.Value.Y; }
                var mp = e.GetPosition(_canvas);
                _rubberBand.X2 = mp.X; _rubberBand.Y2 = mp.Y;
            }
        };

        KeyDown += (_, e) => { if (e.Key == Key.Delete) RemoveSelected(); };

        _scroll.PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            _zoom = Math.Clamp(_zoom + (e.Delta > 0 ? 0.1 : -0.1), 0.3, 2.5);
            _canvas.LayoutTransform = new ScaleTransform(_zoom, _zoom);
            e.Handled = true;
        };

        foreach (var n in _data.Nodes) RenderNode(n);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RenderAllConnections));

        UiZoomHelper.Apply(this, UiZoomHelper.FromSettings(), scaleWindow: false);
    }

    private Border BuildToolbar()
    {
        var bar = new Border { Padding = new Thickness(12, 8, 12, 8) };
        bar.SetResourceReference(Border.BackgroundProperty, "SidebarBgBrush");

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Child = row;

        void AddShapeBtn(string label, FlowNodeKind kind)
        {
            var b = Btn(label, $"Add a {kind} node");
            b.Click += (_, _) => AddNode(kind);
            row.Children.Add(b);
        }

        AddShapeBtn("⬭ Start", FlowNodeKind.Start);
        AddShapeBtn("▭ Process", FlowNodeKind.Process);
        AddShapeBtn("◇ Decision", FlowNodeKind.Decision);
        AddShapeBtn("▱ I/O", FlowNodeKind.InputOutput);
        AddShapeBtn("⊟ Subroutine", FlowNodeKind.Subroutine);
        AddShapeBtn("⬭ End", FlowNodeKind.End);
        AddShapeBtn("✎ Note", FlowNodeKind.Comment);

        row.Children.Add(new Border { Width = 12 });

        // ── Three mutually-exclusive modes: Select / Connect / Remove ──
        _selectBtn  = Btn("➤ Select/Move", "Normal mode: select and drag nodes");
        _connectBtn = Btn("→ Connect", "Click a node, then another, to draw an arrow");
        _removeBtn  = Btn("✕ Remove", "Click a node or arrow to delete it");
        _selectBtn.Click  += (_, _) => SetMode(EditMode.Select);
        _connectBtn.Click += (_, _) => SetMode(EditMode.Connect);
        _removeBtn.Click  += (_, _) => SetMode(EditMode.Remove);
        row.Children.Add(_selectBtn);
        row.Children.Add(_connectBtn);
        row.Children.Add(_removeBtn);
        UpdateModeButtons();

        row.Children.Add(new Border { Width = 12 });
        var zoomBtn = Btn("1:1", "Reset zoom");
        zoomBtn.Click += (_, _) => { _zoom = 1.0; _canvas!.LayoutTransform = Transform.Identity; };
        row.Children.Add(zoomBtn);

        return bar;
    }

    private void SetMode(EditMode mode)
    {
        _mode = mode;
        _connectFromId = null;
        RemoveRubberBand();
        if (mode != EditMode.Select) { _selected.Clear(); RefreshSelection(); }
        UpdateModeButtons();
    }

    private void UpdateModeButtons()
    {
        void Style(Button? b, bool active)
        {
            if (b is null) return;
            b.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
            b.SetResourceReference(Button.BackgroundProperty, active ? "AccentBgBrush" : "ControlBgBrush");
        }
        Style(_selectBtn,  _mode == EditMode.Select);
        Style(_connectBtn, _mode == EditMode.Connect);
        Style(_removeBtn,  _mode == EditMode.Remove);
        if (_canvas is not null)
            _canvas.Cursor = _mode == EditMode.Remove ? Cursors.No : null;
    }

    // ── Node creation / rendering ──────────────────────────────────────────

    private void AddNode(FlowNodeKind kind)
    {
        var node = new FlowNode
        {
            Kind   = kind,
            Text   = DefaultText(kind),
            X      = 80 + _data.Nodes.Count % 6 * 30,
            Y      = 80 + _data.Nodes.Count % 6 * 30,
            Width  = kind == FlowNodeKind.Decision ? 150 : 140,
            Height = kind is FlowNodeKind.Start or FlowNodeKind.End ? 46 : 56
        };
        _data.Nodes.Add(node);
        Save();
        RenderNode(node);
    }

    private static string DefaultText(FlowNodeKind k) => k switch
    {
        FlowNodeKind.Start       => "Start",
        FlowNodeKind.End         => "End",
        FlowNodeKind.Decision    => "condition?",
        FlowNodeKind.InputOutput => "input / output",
        FlowNodeKind.Subroutine  => "call …",
        FlowNodeKind.Comment     => "note",
        _                        => "step"
    };

    private void RenderNode(FlowNode node)
    {
        if (_nodeViews.ContainsKey(node.Id)) return;

        var container = new Grid { Width = node.Width, Height = node.Height, Tag = node.Id, Cursor = Cursors.SizeAll };

        var (fill, stroke) = NodeColors(node.Kind);
        FrameworkElement shape = node.Kind switch
        {
            FlowNodeKind.Start or FlowNodeKind.End => new Border
            {
                CornerRadius    = new CornerRadius(node.Height / 2),
                Background      = new SolidColorBrush(fill),
                BorderBrush     = new SolidColorBrush(stroke),
                BorderThickness = new Thickness(1.5)
            },
            FlowNodeKind.Decision    => DiamondShape(node.Width, node.Height, fill, stroke),
            FlowNodeKind.InputOutput => ParallelogramShape(node.Width, node.Height, fill, stroke),
            FlowNodeKind.Subroutine  => SubroutineShape(fill, stroke),
            FlowNodeKind.Comment     => CommentShape(node.Width, node.Height, fill, stroke),
            _ => new Border
            {
                CornerRadius    = new CornerRadius(4),
                Background      = new SolidColorBrush(fill),
                BorderBrush     = new SolidColorBrush(stroke),
                BorderThickness = new Thickness(1.5)
            }
        };
        container.Children.Add(shape);

        var label = new TextBlock
        {
            Text                = node.Text,
            TextWrapping        = TextWrapping.Wrap,
            TextAlignment       = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            FontSize            = 11,
            Margin              = new Thickness(6, 2, 6, 2),
            Foreground          = new SolidColorBrush(stroke)
        };
        container.Children.Add(label);

        Canvas.SetLeft(container, node.X);
        Canvas.SetTop(container, node.Y);
        Panel.SetZIndex(container, 2);
        _canvas!.Children.Add(container);
        _nodeViews[node.Id] = container;
        GrowCanvasFor(node.X, node.Y, node.Width, node.Height);

        WireNode(container, node, label);
    }

    private void WireNode(FrameworkElement container, FlowNode node, TextBlock label)
    {
        bool dragging = false;
        var  offset   = new Point();

        container.MouseLeftButtonDown += (_, e) =>
        {
            if (_mode == EditMode.Remove) { DeleteNode(node.Id); e.Handled = true; return; }
            if (_connectMode) { HandleConnectClick(node.Id); e.Handled = true; return; }
            if (e.ClickCount >= 2) { EditNodeText(node, label); e.Handled = true; return; }
            _selected.Clear(); _selected.Add(node.Id); RefreshSelection();
            dragging = true; offset = e.GetPosition(container);
            container.CaptureMouse();
            e.Handled = true;
        };
        container.MouseMove += (_, e) =>
        {
            if (!dragging) return;
            var pt = e.GetPosition(_canvas);
            var nx = Snap(Math.Max(0, pt.X - offset.X));
            var ny = Snap(Math.Max(0, pt.Y - offset.Y));
            Canvas.SetLeft(container, nx); Canvas.SetTop(container, ny);
            node.X = nx; node.Y = ny;
            GrowCanvasFor(nx, ny, node.Width, node.Height);
            UpdateConnectionsFor(node.Id);
            e.Handled = true;
        };
        container.MouseLeftButtonUp += (_, e) =>
        {
            if (!dragging) return;
            dragging = false; container.ReleaseMouseCapture();
            Save();
            e.Handled = true;
        };
        container.MouseRightButtonDown += (_, e) =>
        {
            ShowNodeMenu(node, label);
            e.Handled = true;
        };
    }

    private void ShowNodeMenu(FlowNode node, TextBlock label)
    {
        var cm = new ContextMenu();
        var edit = new MenuItem { Header = "✎ Edit text…" };
        edit.Click += (_, _) => EditNodeText(node, label);
        cm.Items.Add(edit);
        var del = new MenuItem { Header = "✕ Delete node" };
        del.Click += (_, _) => { _selected.Clear(); _selected.Add(node.Id); RemoveSelected(); };
        cm.Items.Add(del);
        cm.IsOpen = true;
    }

    private void EditNodeText(FlowNode node, TextBlock label)
    {
        var text = PromptText("Node text", node.Text);
        if (text is null) return;
        node.Text = text;
        label.Text = text;
        Save();
    }

    // ── Connections ────────────────────────────────────────────────────────

    private void HandleConnectClick(string nodeId)
    {
        if (_connectFromId is null)
        {
            _connectFromId = nodeId;
            EnsureRubberBand();
        }
        else
        {
            if (nodeId == _connectFromId) { _connectFromId = null; RemoveRubberBand(); return; }
            var conn = new FlowConnection { FromId = _connectFromId, ToId = nodeId };
            // Offer a label for decision branches
            if (_data.Nodes.FirstOrDefault(n => n.Id == _connectFromId)?.Kind == FlowNodeKind.Decision)
                conn.Label = PromptText("Branch label (e.g. yes / no)", "") ?? "";
            _data.Connections.Add(conn);
            Save();
            RenderConnection(conn);
            _connectFromId = null;
            RemoveRubberBand();
        }
    }

    private void RenderAllConnections()
    {
        foreach (var c in _data.Connections) RenderConnection(c);
    }

    private void RenderConnection(FlowConnection conn)
    {
        if (_connViews.TryGetValue(conn.Id, out var old))
            foreach (var v in old) _canvas!.Children.Remove(v);
        _connViews.Remove(conn.Id);

        var a = NodeRect(conn.FromId);
        var b = NodeRect(conn.ToId);
        if (a is null || b is null) return;

        var ca = new Point(a.Value.X + a.Value.Width / 2, a.Value.Y + a.Value.Height / 2);
        var cb = new Point(b.Value.X + b.Value.Width / 2, b.Value.Y + b.Value.Height / 2);
        var p1 = RectBorderPoint(a.Value, cb);
        var p2 = RectBorderPoint(b.Value, ca);

        var color = ParseColor(conn.LineColor);
        var visuals = new List<UIElement>();

        var line = new Line
        {
            X1 = p1.X, Y1 = p1.Y, X2 = p2.X, Y2 = p2.Y,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = conn.Thickness,
            IsHitTestVisible = false
        };
        Panel.SetZIndex(line, 1);
        _canvas!.Children.Add(line); visuals.Add(line);

        var arrow = BuildArrow(p1, p2, color);
        Panel.SetZIndex(arrow, 1);
        _canvas.Children.Add(arrow); visuals.Add(arrow);

        // Hit zone for right-click delete / relabel
        var hit = new Line
        {
            X1 = p1.X, Y1 = p1.Y, X2 = p2.X, Y2 = p2.Y,
            Stroke = Brushes.Transparent, StrokeThickness = 12, Cursor = Cursors.Hand
        };
        Panel.SetZIndex(hit, 3);
        var capConn = conn;
        hit.MouseRightButtonDown += (_, e) => { ShowConnMenu(capConn); e.Handled = true; };
        hit.MouseLeftButtonDown += (_, e) =>
        {
            if (_mode != EditMode.Remove) return;
            DeleteConnection(capConn);
            e.Handled = true;
        };
        _canvas.Children.Add(hit); visuals.Add(hit);

        // Label
        if (!string.IsNullOrWhiteSpace(conn.Label))
        {
            var mid = new Point((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2);
            var badge = new Border
            {
                CornerRadius = new CornerRadius(3),
                Padding      = new Thickness(4, 1, 4, 1)
            };
            badge.SetResourceReference(Border.BackgroundProperty, "SidebarBgBrush");
            var t = new TextBlock { Text = conn.Label, FontSize = 10 };
            t.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
            badge.Child = t;
            Canvas.SetLeft(badge, mid.X - 10);
            Canvas.SetTop(badge, mid.Y - 9);
            Panel.SetZIndex(badge, 4);
            _canvas.Children.Add(badge); visuals.Add(badge);
        }

        _connViews[conn.Id] = visuals;
    }

    private void ShowConnMenu(FlowConnection conn)
    {
        var cm = new ContextMenu();
        var relabel = new MenuItem { Header = "✎ Edit label…" };
        relabel.Click += (_, _) =>
        {
            var t = PromptText("Arrow label", conn.Label);
            if (t is null) return;
            conn.Label = t; Save(); RenderConnection(conn);
        };
        cm.Items.Add(relabel);
        var del = new MenuItem { Header = "✕ Delete arrow" };
        del.Click += (_, _) =>
        {
            _data.Connections.Remove(conn);
            if (_connViews.TryGetValue(conn.Id, out var vs)) foreach (var v in vs) _canvas!.Children.Remove(v);
            _connViews.Remove(conn.Id);
            Save();
        };
        cm.Items.Add(del);
        cm.IsOpen = true;
    }

    private void UpdateConnectionsFor(string nodeId)
    {
        foreach (var c in _data.Connections)
            if (c.FromId == nodeId || c.ToId == nodeId) RenderConnection(c);
    }

    /// <summary>Expands the canvas when content approaches its right/bottom edge.</summary>
    private void GrowCanvasFor(double x, double y, double w, double h)
    {
        if (_canvas is null) return;
        const double margin = 400;
        if (x + w + margin > _canvas.Width)  _canvas.Width  = x + w + margin;
        if (y + h + margin > _canvas.Height) _canvas.Height = y + h + margin;
    }

    // ── Remove ─────────────────────────────────────────────────────────────

    private void RemoveSelected()
    {
        foreach (var id in _selected.ToList())
            DeleteNode(id, persist: false);
        _selected.Clear();
        Save();
    }

    private void DeleteNode(string id, bool persist = true)
    {
        _data.Nodes.RemoveAll(n => n.Id == id);
        if (_nodeViews.TryGetValue(id, out var v)) { _canvas!.Children.Remove(v); _nodeViews.Remove(id); }
        var conns = _data.Connections.Where(c => c.FromId == id || c.ToId == id).ToList();
        foreach (var c in conns)
        {
            _data.Connections.Remove(c);
            if (_connViews.TryGetValue(c.Id, out var vs)) foreach (var vv in vs) _canvas!.Children.Remove(vv);
            _connViews.Remove(c.Id);
        }
        if (persist) Save();
    }

    private void DeleteConnection(FlowConnection conn)
    {
        _data.Connections.Remove(conn);
        if (_connViews.TryGetValue(conn.Id, out var vs)) foreach (var v in vs) _canvas!.Children.Remove(v);
        _connViews.Remove(conn.Id);
        Save();
    }

    private void RefreshSelection()
    {
        var accent = (Brush)(TryFindResource("AccentHighlightBrush") ?? new SolidColorBrush(Colors.DodgerBlue));
        foreach (var (id, v) in _nodeViews)
        {
            bool sel = _selected.Contains(id);
            v.Effect = sel
                ? new System.Windows.Media.Effects.DropShadowEffect { Color = ((SolidColorBrush)accent).Color, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.9 }
                : null;
        }
    }

    // ── Shape builders ─────────────────────────────────────────────────────

    private static Polygon DiamondShape(double w, double h, Color fill, Color stroke) => new()
    {
        Points = [new Point(w / 2, 0), new Point(w, h / 2), new Point(w / 2, h), new Point(0, h / 2)],
        Fill = new SolidColorBrush(fill), Stroke = new SolidColorBrush(stroke), StrokeThickness = 1.5,
        Stretch = Stretch.Fill
    };

    private static Polygon ParallelogramShape(double w, double h, Color fill, Color stroke) => new()
    {
        Points = [new Point(w * 0.22, 0), new Point(w, 0), new Point(w * 0.78, h), new Point(0, h)],
        Fill = new SolidColorBrush(fill), Stroke = new SolidColorBrush(stroke), StrokeThickness = 1.5,
        Stretch = Stretch.Fill
    };

    private static Grid CommentShape(double w, double h, Color fill, Color stroke)
    {
        var g = new Grid();
        var rect = new Rectangle
        {
            RadiusX = 3, RadiusY = 3,
            Fill = new SolidColorBrush(fill),
            Stroke = new SolidColorBrush(stroke),
            StrokeThickness = 1,
            StrokeDashArray = [3, 2]
        };
        g.Children.Add(rect);
        return g;
    }

    private static Grid SubroutineShape(Color fill, Color stroke)
    {
        var g = new Grid();
        var outer = new Border
        {
            CornerRadius    = new CornerRadius(3),
            Background      = new SolidColorBrush(fill),
            BorderBrush     = new SolidColorBrush(stroke),
            BorderThickness = new Thickness(1.5)
        };
        g.Children.Add(outer);
        var leftBar  = new Border { BorderBrush = new SolidColorBrush(stroke), BorderThickness = new Thickness(1, 0, 0, 0), Margin = new Thickness(7, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        var rightBar = new Border { BorderBrush = new SolidColorBrush(stroke), BorderThickness = new Thickness(1, 0, 0, 0), Margin = new Thickness(0, 0, 7, 0), HorizontalAlignment = HorizontalAlignment.Right };
        g.Children.Add(leftBar);
        g.Children.Add(rightBar);
        return g;
    }

    private static (Color fill, Color stroke) NodeColors(FlowNodeKind k) => k switch
    {
        FlowNodeKind.Start       => (Color.FromRgb(0xC8, 0xE6, 0xC9), Color.FromRgb(0x2E, 0x7D, 0x32)),
        FlowNodeKind.End         => (Color.FromRgb(0xFF, 0xCD, 0xD2), Color.FromRgb(0xC6, 0x28, 0x28)),
        FlowNodeKind.Decision    => (Color.FromRgb(0xFF, 0xF1, 0xC4), Color.FromRgb(0xF5, 0x7F, 0x17)),
        FlowNodeKind.InputOutput => (Color.FromRgb(0xBB, 0xDE, 0xFB), Color.FromRgb(0x15, 0x65, 0xC0)),
        FlowNodeKind.Subroutine  => (Color.FromRgb(0xD1, 0xC4, 0xE9), Color.FromRgb(0x51, 0x2D, 0xA8)),
        FlowNodeKind.Comment     => (Color.FromRgb(0xEC, 0xEF, 0xF1), Color.FromRgb(0x45, 0x5A, 0x64)),
        _                        => (Color.FromRgb(0xE3, 0xF2, 0xFD), Color.FromRgb(0x15, 0x65, 0xC0))
    };

    // ── Geometry helpers ───────────────────────────────────────────────────

    private Rect? NodeRect(string id)
    {
        var n = _data.Nodes.FirstOrDefault(x => x.Id == id);
        if (n is null) return null;
        return new Rect(n.X, n.Y, n.Width, n.Height);
    }

    private Point? NodeCenter(string id)
    {
        var r = NodeRect(id);
        return r is null ? null : new Point(r.Value.X + r.Value.Width / 2, r.Value.Y + r.Value.Height / 2);
    }

    /// <summary>Point on the rectangle border in the direction of <paramref name="toward"/>.</summary>
    private static Point RectBorderPoint(Rect rect, Point toward)
    {
        var c  = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        double dx = toward.X - c.X, dy = toward.Y - c.Y;
        if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001) return c;
        double hw = rect.Width / 2, hh = rect.Height / 2;
        double scale = 1.0 / Math.Max(Math.Abs(dx) / hw, Math.Abs(dy) / hh);
        return new Point(c.X + dx * scale, c.Y + dy * scale);
    }

    private static Polygon BuildArrow(Point from, Point to, Color color)
    {
        var dir = to - from;
        var len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
        if (len < 0.001) return new Polygon();
        var u = new Vector(dir.X / len, dir.Y / len);
        var perp = new Vector(-u.Y, u.X);
        const double aw = 5, al = 11;
        var b1 = to - u * al + perp * aw;
        var b2 = to - u * al - perp * aw;
        return new Polygon
        {
            Points = [to, new Point(b1.X, b1.Y), new Point(b2.X, b2.Y)],
            Fill = new SolidColorBrush(color),
            IsHitTestVisible = false
        };
    }

    private static Color ParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Colors.Gray; }
    }

    private double Snap(double v)
    {
        if (!_data.SnapToGrid || _data.GridSize < 1) return v;
        return Math.Round(v / _data.GridSize) * _data.GridSize;
    }

    // ── Rubber band ────────────────────────────────────────────────────────

    private void EnsureRubberBand()
    {
        if (_rubberBand is not null) return;
        _rubberBand = new Line
        {
            Stroke = Brushes.DodgerBlue, StrokeThickness = 1.5,
            StrokeDashArray = [4, 4], IsHitTestVisible = false
        };
        Panel.SetZIndex(_rubberBand, 20);
        _canvas!.Children.Add(_rubberBand);
    }

    private void RemoveRubberBand()
    {
        if (_rubberBand is null) return;
        _canvas?.Children.Remove(_rubberBand);
        _rubberBand = null;
    }

    // ── Small UI helpers ───────────────────────────────────────────────────

    private Button Btn(string label, string? tooltip)
    {
        var b = new Button
        {
            Content = label, Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(0, 0, 4, 0), FontSize = 12, ToolTip = tooltip
        };
        b.SetResourceReference(StyleProperty,      "ModernButton");
        b.SetResourceReference(BackgroundProperty, "ControlBgBrush");
        b.SetResourceReference(ForegroundProperty, "SidebarTextBrush");
        return b;
    }

    private string? PromptText(string title, string initial)
    {
        var dlg = new Window
        {
            Title = title, Width = 360, Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this, ResizeMode = ResizeMode.NoResize
        };
        if (!string.IsNullOrWhiteSpace(_themePath))
        {
            try { var d = OxsuitLoader.Load(_themePath); if (d is not null) dlg.Resources.MergedDictionaries.Add(d); } catch { }
        }
        dlg.SetResourceReference(BackgroundProperty, "ContentBgBrush");
        dlg.SourceInitialized += (_, _) => ParticipantsWindow.TryApplyTitleBarTo(dlg);

        var g = new Grid { Margin = new Thickness(14) };
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        dlg.Content = g;

        var box = new TextBox { Text = initial, Margin = new Thickness(0, 0, 0, 12) };
        box.SetResourceReference(TextBox.BackgroundProperty,  "InputBgBrush");
        box.SetResourceReference(TextBox.ForegroundProperty,  "SidebarTextBrush");
        box.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        Grid.SetRow(box, 0); g.Children.Add(box);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(btnRow, 1); g.Children.Add(btnRow);

        string? result = null;
        var ok = Btn("OK", null);
        ok.Click += (_, _) => { result = box.Text; dlg.DialogResult = true; };
        btnRow.Children.Add(ok);
        box.KeyDown += (_, e) => { if (e.Key == Key.Return) { result = box.Text; dlg.DialogResult = true; } };
        var cancel = Btn("Cancel", null);
        cancel.Margin = new Thickness(8, 0, 0, 0);
        cancel.Click += (_, _) => dlg.DialogResult = false;
        btnRow.Children.Add(cancel);

        dlg.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dlg.ShowDialog() == true ? result : null;
    }
}
