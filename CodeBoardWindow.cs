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
/// Canvas board for code structure (Classes, Functions, Interfaces, etc.)
/// with typed input/output ports and port-to-port connections.
/// </summary>
public class CodeBoardWindow : Window
{
    // ── State ──────────────────────────────────────────────────────────────
    private readonly string    _projFolder;
    private readonly CodeBoard _board;
    private readonly string?   _themePath;
    private CodeBoardData      _boardData;

    private Canvas?      _canvas;
    private ScrollViewer? _scroll;

    // entity ID → loaded CodeEntity
    private readonly Dictionary<string, CodeEntity> _entities = new();
    // entity ID → card Border on canvas
    private readonly Dictionary<string, Border>     _cards    = new();
    // "{entityId}:{portId}" → Ellipse on canvas
    private readonly Dictionary<string, Ellipse>    _portDots = new();
    // relation ID → list of Line segments
    private readonly Dictionary<string, List<Line>> _relLines = new();
    // relation ID → arrowhead Polygon
    private readonly Dictionary<string, Polygon>    _relArrows = new();

    // ── Connect mode ───────────────────────────────────────────────────────
    private bool    _connectMode           = false;
    private string? _connectFromEntityId   = null;
    private string? _connectFromPortId     = null;
    private Line?   _rubberBand            = null;

    // ── Selection ──────────────────────────────────────────────────────────
    private readonly HashSet<string> _selectedIds = new();

    // ── Zoom ───────────────────────────────────────────────────────────────
    private double _zoom = 1.0;

    // ── Constants ──────────────────────────────────────────────────────────
    private const double PortRadius  = 6;  // half of port dot diameter
    private const double DefaultCardW = 180;

    // ── Constructor ────────────────────────────────────────────────────────

    public CodeBoardWindow(string projFolder, CodeBoard board, string? themePath)
    {
        _projFolder = projFolder;
        _board      = board;
        _themePath  = themePath;
        _boardData  = CodeBoardDataService.Load(projFolder, board.Id);

        Title                 = board.Symbol + "  " + board.Name;
        Width                 = 1280;
        Height                = 800;
        MinWidth              = 640;
        MinHeight             = 480;
        ShowInTaskbar         = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode            = ResizeMode.CanResize;

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
        Loaded            += (_, _) => BuildContent();
    }

    // ── UI Build ───────────────────────────────────────────────────────────

    private void BuildContent()
    {
        // Load all entities that have positions on this board
        foreach (var entityType in CodeEntityService.EntityTypes)
        {
            foreach (var e in CodeEntityService.LoadAll(_projFolder, entityType))
                _entities[e.Id] = e;
        }

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Content = root;

        // Toolbar
        var toolbar = BuildToolbar();
        Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);

        // Canvas in ScrollViewer
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto
        };
        Grid.SetRow(_scroll, 1);
        root.Children.Add(_scroll);

        _canvas = new Canvas
        {
            Width  = 3000,
            Height = 2000,
            ClipToBounds = false
        };
        _canvas.SetResourceReference(Canvas.BackgroundProperty, "ContentBgBrush");
        _scroll.Content = _canvas;

        // Canvas keyboard shortcut: Delete removes selected
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && _selectedIds.Count > 0)
                RemoveSelectedFromBoard();
        };

        // Canvas click → deselect / cancel connect / rubber band start
        _canvas.MouseLeftButtonDown += Canvas_MouseLeftButtonDown;
        _canvas.MouseMove           += Canvas_MouseMove;
        _canvas.MouseLeftButtonUp   += Canvas_MouseLeftButtonUp;
        _canvas.MouseRightButtonDown += Canvas_RightClick;

        // Zoom: Ctrl + scroll wheel
        _scroll.PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            _zoom = Math.Clamp(_zoom + (e.Delta > 0 ? 0.1 : -0.1), 0.25, 3.0);
            _canvas.LayoutTransform = new ScaleTransform(_zoom, _zoom);
            e.Handled = true;
        };

        // Render all entities that have positions
        foreach (var kv in _boardData.Positions.ToList())
        {
            if (_entities.TryGetValue(kv.Key, out var entity))
                RenderCard(entity, kv.Value);
            else
                _boardData.Positions.Remove(kv.Key); // entity deleted from disk
        }

        // Defer line rendering until cards have measured sizes
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RenderAllRelations));
    }

    private Border BuildToolbar()
    {
        var toolbar = new Border { Padding = new Thickness(12, 8, 12, 8) };
        toolbar.SetResourceReference(Border.BackgroundProperty, "SidebarBgBrush");

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        toolbar.Child = row;

        // Add entity dropdown
        var addBtn = Btn(Properties.Loc.S("Code_AddToBoard"), Properties.Loc.S("Code_AddToBoardTip"));
        addBtn.Click += (_, _) => ShowAddEntityMenu(addBtn);
        row.Children.Add(addBtn);

        Spacer(row, 8);

        // Connect mode toggle
        var connectBtn = Btn(Properties.Loc.S("Code_ConnectPorts"), Properties.Loc.S("Code_ConnectPortsTip"));
        connectBtn.Click += (_, _) =>
        {
            _connectMode = !_connectMode;
            _connectFromEntityId = null;
            _connectFromPortId   = null;
            RemoveRubberBand();
            connectBtn.FontWeight = _connectMode ? FontWeights.Bold : FontWeights.Normal;
            connectBtn.SetResourceReference(Button.BackgroundProperty,
                _connectMode ? "AccentBgBrush" : "ControlBgBrush");
        };
        row.Children.Add(connectBtn);

        Spacer(row, 8);

        // Delete selected
        var delBtn = Btn(Properties.Loc.S("Code_RemoveCards"), Properties.Loc.S("Code_RemoveCardsTip"));
        delBtn.Click += (_, _) => RemoveSelectedFromBoard();
        row.Children.Add(delBtn);

        Spacer(row, 16);

        // Zoom reset
        var zoomBtn = Btn("1:1", Properties.Loc.S("Common_ResetZoomTip"));
        zoomBtn.Click += (_, _) =>
        {
            _zoom = 1.0;
            _canvas!.LayoutTransform = Transform.Identity;
        };
        row.Children.Add(zoomBtn);

        return toolbar;
    }

    // ── Card rendering ─────────────────────────────────────────────────────

    private void RenderCard(CodeEntity entity, CodeCardPosition pos)
    {
        if (_cards.ContainsKey(entity.Id)) return; // already rendered

        var card = BuildCard(entity);
        Canvas.SetLeft(card, pos.X);
        Canvas.SetTop(card,  pos.Y);
        Panel.SetZIndex(card, 2);
        _canvas!.Children.Add(card);
        _cards[entity.Id] = card;

        // Wire drag & click
        bool   dragging   = false;
        var    dragOffset = new Point();

        card.MouseLeftButtonDown += (_, e) =>
        {
            if (_connectMode) { e.Handled = true; return; } // port dots handle connect
            if (e.ClickCount >= 2) { ShowEntityEditor(entity); e.Handled = true; return; }

            _selectedIds.Clear();
            _selectedIds.Add(entity.Id);
            RefreshSelectionVisuals();

            dragging   = true;
            dragOffset = e.GetPosition(card);
            card.CaptureMouse();
            e.Handled = true;
        };

        card.MouseMove += (_, e) =>
        {
            if (!dragging) return;
            var pt  = e.GetPosition(_canvas);
            var nx  = Snap(Math.Max(0, pt.X - dragOffset.X));
            var ny  = Snap(Math.Max(0, pt.Y - dragOffset.Y));
            Canvas.SetLeft(card, nx);
            Canvas.SetTop(card,  ny);
            GrowCanvasFor(nx, ny, card.ActualWidth, card.ActualHeight);
            UpdatePortPositions(entity.Id);
            UpdateRelationsForEntity(entity.Id);
            e.Handled = true;
        };

        card.MouseLeftButtonUp += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            card.ReleaseMouseCapture();
            var x = Canvas.GetLeft(card);
            var y = Canvas.GetTop(card);
            if (_boardData.Positions.TryGetValue(entity.Id, out var p)) { p.X = x; p.Y = y; }
            CodeBoardDataService.Save(_projFolder, _board.Id, _boardData);
            e.Handled = true;
        };

        card.MouseEnter += (_, _) => { if (!_selectedIds.Contains(entity.Id)) card.Effect = GlowEffect(); };
        card.MouseLeave += (_, _) => card.Effect = null;

        card.MouseRightButtonDown += (_, e) =>
        {
            ShowCardContextMenu(entity);
            e.Handled = true;
        };

        // Render port dots after layout
        card.SizeChanged += (_, _) => UpdatePortPositions(entity.Id);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => UpdatePortPositions(entity.Id)));
    }

    private Border BuildCard(CodeEntity entity)
    {
        var (typeColor, typeSymbol) = EntityTypeStyle(entity.EntityType);

        var card = new Border
        {
            CornerRadius    = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            MinWidth        = DefaultCardW,
            Cursor          = Cursors.SizeAll,
            Tag             = entity.Id
        };
        card.SetResourceReference(Border.BackgroundProperty,   "CardBgBrush");
        card.SetResourceReference(Border.BorderBrushProperty,  "ControlBorderBrush");

        var stack = new StackPanel();
        card.Child = stack;

        // Header
        var header = new Border
        {
            Background    = new SolidColorBrush(typeColor),
            CornerRadius  = new CornerRadius(5, 5, 0, 0),
            Padding       = new Thickness(8, 5, 8, 5)
        };
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
        headerRow.Children.Add(new TextBlock
        {
            Text       = typeSymbol + " ",
            FontSize   = 12,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        });
        headerRow.Children.Add(new TextBlock
        {
            Text       = entity.Name,
            FontSize   = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Child = headerRow;
        stack.Children.Add(header);

        // Type badge
        var typeBadge = new Border
        {
            Background    = new SolidColorBrush(Color.FromArgb(30, typeColor.R, typeColor.G, typeColor.B)),
            Padding       = new Thickness(8, 2, 8, 2)
        };
        typeBadge.Child = new TextBlock
        {
            Text       = entity.EntityType.ToString(),
            FontSize   = 10,
            Foreground = new SolidColorBrush(typeColor),
            FontStyle  = FontStyles.Italic
        };
        stack.Children.Add(typeBadge);

        // Inheritance / implements line (Class / Struct)
        if (entity.EntityType is CodeEntityType.Class or CodeEntityType.Struct)
        {
            var relText = new List<string>();
            if (!string.IsNullOrEmpty(entity.BaseClassId) && _entities.TryGetValue(entity.BaseClassId, out var baseE))
                relText.Add($"⊳ {baseE.Name}");
            var ifaceNames = entity.ImplementsIds
                .Where(_entities.ContainsKey)
                .Select(id => _entities[id].Name)
                .ToList();
            if (ifaceNames.Count > 0)
                relText.Add($"◁ {string.Join(", ", ifaceNames)}");
            if (relText.Count > 0)
                stack.Children.Add(SectionText(string.Join("   ", relText), italic: true, opacity: 0.85));
        }

        // Object: instance-of
        if (entity.EntityType == CodeEntityType.Object && !string.IsNullOrEmpty(entity.InstanceOfId)
            && _entities.TryGetValue(entity.InstanceOfId, out var cls))
        {
            stack.Children.Add(SectionText($": {cls.Name}", italic: true, opacity: 0.85));
        }

        // Description
        if (!string.IsNullOrWhiteSpace(entity.Description))
        {
            var descText = new TextBlock
            {
                Text         = entity.Description,
                FontSize     = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth     = 260,
                Opacity      = 0.75,
                Margin       = new Thickness(8, 4, 8, 4)
            };
            descText.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
            stack.Children.Add(descText);
        }

        // Fields section (Class / Struct)
        if (entity.Fields.Count > 0)
        {
            stack.Children.Add(Divider());
            var fieldStack = new StackPanel { Margin = new Thickness(8, 4, 8, 4) };
            foreach (var f in entity.Fields)
            {
                var stat = f.IsStatic ? "static " : "";
                fieldStack.Children.Add(MemberLine($"{VisSymbol(f.Visibility)} {stat}{f.Name}: {f.DataType}"));
            }
            stack.Children.Add(WrapInBorder(fieldStack));
        }

        // Methods section (Class / Struct / Interface)
        if (entity.Methods.Count > 0)
        {
            stack.Children.Add(Divider());
            var methodStack = new StackPanel { Margin = new Thickness(8, 4, 8, 4) };
            foreach (var m in entity.Methods)
            {
                var ps   = string.Join(", ", m.Parameters.Select(p => $"{ConvSymbol(p.Convention)}{p.DataType} {p.Name}"));
                var stat = m.IsStatic ? "static " : "";
                methodStack.Children.Add(MemberLine($"{VisSymbol(m.Visibility)} {stat}{m.Name}({ps}): {m.ReturnType}", bold: true));
            }
            stack.Children.Add(WrapInBorder(methodStack));
        }

        // Enum values
        if (entity.EntityType == CodeEntityType.Enum && entity.EnumValues.Count > 0)
        {
            stack.Children.Add(Divider());
            var enumStack = new StackPanel { Margin = new Thickness(8, 4, 8, 4) };
            foreach (var v in entity.EnumValues)
                enumStack.Children.Add(MemberLine($"• {v}"));
            stack.Children.Add(WrapInBorder(enumStack));
        }

        // Ports list
        var inputs  = entity.Ports.Where(p => p.Direction == PortDirection.Input).ToList();
        var outputs = entity.Ports.Where(p => p.Direction == PortDirection.Output).ToList();

        if (inputs.Count > 0 || outputs.Count > 0)
        {
            var portBorder = new Border { Padding = new Thickness(8, 4, 8, 6) };
            var portStack  = new StackPanel();

            foreach (var port in inputs)
                portStack.Children.Add(PortLabel(port, isInput: true));
            foreach (var port in outputs)
                portStack.Children.Add(PortLabel(port, isInput: false));

            portBorder.Child = portStack;
            stack.Children.Add(portBorder);
        }

        return card;
    }

    private static TextBlock PortLabel(CodePort port, bool isInput)
    {
        var conv = port.Convention switch
        {
            PassingConvention.Reference => "&",
            PassingConvention.Pointer   => "*",
            _                           => ""
        };
        var text = isInput
            ? $"→ {port.Name}: {conv}{port.DataType}"
            : $"{port.Name}: {conv}{port.DataType} →";
        return new TextBlock
        {
            Text     = text,
            FontSize = 10,
            Opacity  = 0.8,
            Margin   = new Thickness(0, 1, 0, 1),
            Foreground = new SolidColorBrush(isInput ? Color.FromRgb(0x42, 0xA5, 0xF5)
                                                     : Color.FromRgb(0x66, 0xBB, 0x6A))
        };
    }

    // ── UML section helpers ────────────────────────────────────────────────

    private static string VisSymbol(CodeVisibility v) => v switch
    {
        CodeVisibility.Public    => "+",
        CodeVisibility.Private   => "−",
        CodeVisibility.Protected => "#",
        CodeVisibility.Internal  => "~",
        _                        => " "
    };

    private static string ConvSymbol(PassingConvention c) => c switch
    {
        PassingConvention.Reference => "&",
        PassingConvention.Pointer   => "*",
        _                           => ""
    };

    private TextBlock SectionText(string text, bool italic = false, double opacity = 1.0)
    {
        var tb = new TextBlock
        {
            Text         = text,
            FontSize     = 10,
            FontStyle    = italic ? FontStyles.Italic : FontStyles.Normal,
            Opacity      = opacity,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth     = 260,
            Margin       = new Thickness(8, 2, 8, 2)
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
        return tb;
    }

    private TextBlock MemberLine(string text, bool bold = false)
    {
        var tb = new TextBlock
        {
            Text         = text,
            FontSize     = 11,
            FontFamily   = new FontFamily("Consolas, Cascadia Mono, monospace"),
            FontWeight   = bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth     = 260,
            Margin       = new Thickness(0, 1, 0, 1)
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
        return tb;
    }

    private static Border WrapInBorder(UIElement child) => new() { Child = child };

    private Border Divider()
    {
        var b = new Border { Height = 1, Margin = new Thickness(0, 1, 0, 1) };
        b.SetResourceReference(Border.BackgroundProperty, "ControlBorderBrush");
        return b;
    }

    // ── Port dot rendering ─────────────────────────────────────────────────

    /// <summary>Expands the canvas when a card approaches its right/bottom edge.</summary>
    private void GrowCanvasFor(double x, double y, double w, double h)
    {
        if (_canvas is null) return;
        const double margin = 400;
        if (x + w + margin > _canvas.Width)  _canvas.Width  = x + w + margin;
        if (y + h + margin > _canvas.Height) _canvas.Height = y + h + margin;
    }

    private void UpdatePortPositions(string entityId)
    {
        if (!_cards.TryGetValue(entityId, out var card)) return;
        if (!_entities.TryGetValue(entityId, out var entity)) return;
        if (!_boardData.Positions.TryGetValue(entityId, out var pos)) return;

        double x = Canvas.GetLeft(card);
        double y = Canvas.GetTop(card);
        double w = card.ActualWidth  > 0 ? card.ActualWidth  : DefaultCardW;
        double h = card.ActualHeight > 0 ? card.ActualHeight : 80;

        var inputs  = entity.Ports.Where(p => p.Direction == PortDirection.Input).ToList();
        var outputs = entity.Ports.Where(p => p.Direction == PortDirection.Output).ToList();

        PlacePortDots(entity, inputs,  x, y, w, h, pos.PortOrientation, isInput: true);
        PlacePortDots(entity, outputs, x, y, w, h, pos.PortOrientation, isInput: false);
    }

    private void PlacePortDots(CodeEntity entity, List<CodePort> ports,
        double cardX, double cardY, double cardW, double cardH,
        PortOrientation orientation, bool isInput)
    {
        int n = ports.Count;
        for (int i = 0; i < n; i++)
        {
            var port   = ports[i];
            var key    = $"{entity.Id}:{port.Id}";
            var dot    = GetOrCreatePortDot(entity.Id, port);

            double cx, cy;
            if (orientation == PortOrientation.Horizontal)
            {
                cx = isInput ? cardX - PortRadius : cardX + cardW - PortRadius;
                cy = cardY + cardH * (i + 1.0) / (n + 1.0) - PortRadius;
            }
            else
            {
                cx = cardX + cardW * (i + 1.0) / (n + 1.0) - PortRadius;
                cy = isInput ? cardY - PortRadius : cardY + cardH - PortRadius;
            }

            Canvas.SetLeft(dot, cx);
            Canvas.SetTop(dot,  cy);
        }
    }

    private Ellipse GetOrCreatePortDot(string entityId, CodePort port)
    {
        var key = $"{entityId}:{port.Id}";
        if (_portDots.TryGetValue(key, out var existing)) return existing;

        var isInput = port.Direction == PortDirection.Input;
        var dot = new Ellipse
        {
            Width           = PortRadius * 2,
            Height          = PortRadius * 2,
            Fill            = new SolidColorBrush(isInput
                                  ? Color.FromRgb(0x42, 0xA5, 0xF5)
                                  : Color.FromRgb(0x66, 0xBB, 0x6A)),
            Stroke          = Brushes.White,
            StrokeThickness = 1.5,
            Cursor          = Cursors.Cross,
            ToolTip         = BuildPortTooltip(port),
            Tag             = key
        };
        Panel.SetZIndex(dot, 5);
        _canvas!.Children.Add(dot);
        _portDots[key] = dot;

        dot.MouseLeftButtonDown += (_, e) =>
        {
            if (!_connectMode) return;
            HandlePortClick(entityId, port.Id, port.Direction);
            e.Handled = true;
        };

        return dot;
    }

    private static string BuildPortTooltip(CodePort port)
    {
        var conv = port.Convention switch
        {
            PassingConvention.Reference => "ref ",
            PassingConvention.Pointer   => "ptr ",
            _                           => ""
        };
        return $"{port.Direction}: {port.Name} ({conv}{port.DataType})";
    }

    // ── Connect mode ───────────────────────────────────────────────────────

    private void HandlePortClick(string entityId, string portId, PortDirection direction)
    {
        if (_connectFromEntityId is null)
        {
            // First click — must be Output port
            if (direction != PortDirection.Output)
            {
                MessageBox.Show(Properties.Loc.S("Code_ConnStartOutput"),
                    Properties.Loc.S("Code_ConnTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _connectFromEntityId = entityId;
            _connectFromPortId   = portId;
            EnsureRubberBand();
        }
        else
        {
            // Second click — must be Input port on a different entity
            if (direction != PortDirection.Input)
            {
                MessageBox.Show(Properties.Loc.S("Code_ConnEndInput"),
                    Properties.Loc.S("Code_ConnTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (entityId == _connectFromEntityId)
            {
                _connectFromEntityId = null;
                _connectFromPortId   = null;
                RemoveRubberBand();
                return;
            }

            // Type-safety: output and input must match in passing convention AND data type,
            // so a Pointer/Reference/Direct mismatch can't be wired in by accident.
            var srcPort = FindPort(_connectFromEntityId!, _connectFromPortId!);
            var dstPort = FindPort(entityId, portId);
            if (srcPort is not null && dstPort is not null)
            {
                if (srcPort.Convention != dstPort.Convention)
                {
                    MessageBox.Show(
                        string.Format(Properties.Loc.S("Code_MismatchConv"),
                            srcPort.Name, srcPort.Convention, dstPort.Name, dstPort.Convention),
                        Properties.Loc.S("Code_MismatchTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    _connectFromEntityId = null; _connectFromPortId = null; RemoveRubberBand();
                    return;
                }
                if (!NormType(srcPort.DataType).Equals(NormType(dstPort.DataType), StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(
                        string.Format(Properties.Loc.S("Code_MismatchType"),
                            srcPort.Name, srcPort.DataType, dstPort.Name, dstPort.DataType),
                        Properties.Loc.S("Code_MismatchTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    _connectFromEntityId = null; _connectFromPortId = null; RemoveRubberBand();
                    return;
                }
            }

            // Create relation
            var rel = new CodeRelation
            {
                FromId     = _connectFromEntityId,
                FromPortId = _connectFromPortId!,
                ToId       = entityId,
                ToPortId   = portId
            };
            _boardData.Relations.Add(rel);
            CodeBoardDataService.Save(_projFolder, _board.Id, _boardData);
            RenderRelation(rel);

            _connectFromEntityId = null;
            _connectFromPortId   = null;
            RemoveRubberBand();
        }
    }

    private CodePort? FindPort(string entityId, string portId)
    {
        if (!_entities.TryGetValue(entityId, out var e)) return null;
        return e.Ports.FirstOrDefault(p => p.Id == portId);
    }

    /// <summary>Normalises a data type for comparison (trims and strips trailing pointer/ref marks).</summary>
    private static string NormType(string t) =>
        (t ?? "").Trim().TrimEnd('*', '&', ' ');

    private void EnsureRubberBand()
    {
        if (_rubberBand is not null) return;
        _rubberBand = new Line
        {
            Stroke           = Brushes.DodgerBlue,
            StrokeThickness  = 1.5,
            StrokeDashArray  = [4, 4],
            IsHitTestVisible = false
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

    // ── Relation rendering ─────────────────────────────────────────────────

    private void RenderAllRelations()
    {
        foreach (var rel in _boardData.Relations)
            RenderRelation(rel);
    }

    private void RenderRelation(CodeRelation rel)
    {
        // Remove old visuals
        if (_relLines.TryGetValue(rel.Id, out var old))
            foreach (var l in old) _canvas!.Children.Remove(l);
        _relLines.Remove(rel.Id);
        if (_relArrows.TryGetValue(rel.Id, out var oa)) _canvas!.Children.Remove(oa);
        _relArrows.Remove(rel.Id);

        var (p1, p2) = GetPortCenters(rel);
        if (p1 is null || p2 is null) return;

        var color = ParseColor(rel.LineColor);

        // Build waypoint path: p1 → waypoints → p2
        var points = new List<Point> { p1.Value };
        foreach (var wp in rel.Waypoints) points.Add(new Point(wp.X, wp.Y));
        points.Add(p2.Value);

        var lines = new List<Line>();
        for (int i = 0; i < points.Count - 1; i++)
        {
            var line = new Line
            {
                X1              = points[i].X,
                Y1              = points[i].Y,
                X2              = points[i + 1].X,
                Y2              = points[i + 1].Y,
                Stroke          = new SolidColorBrush(color),
                StrokeThickness = rel.Thickness,
                IsHitTestVisible = false
            };
            ApplyLineStyle(line, rel.LineStyle);
            Panel.SetZIndex(line, 1);
            _canvas!.Children.Add(line);
            lines.Add(line);
        }
        _relLines[rel.Id] = lines;

        // Hit zone + context menu
        var hit = new Line
        {
            X1               = p1.Value.X,
            Y1               = p1.Value.Y,
            X2               = p2.Value.X,
            Y2               = p2.Value.Y,
            Stroke           = Brushes.Transparent,
            StrokeThickness  = 12,
            Cursor           = Cursors.Hand,
            Tag              = rel.Id
        };
        Panel.SetZIndex(hit, 3);
        _canvas!.Children.Add(hit);
        var capRel = rel;
        hit.MouseRightButtonDown += (_, e) =>
        {
            ShowRelationContextMenu(capRel);
            e.Handled = true;
        };
        lines.Add(hit);

        // Arrowhead
        if (rel.HasArrow)
        {
            var arrow = BuildArrow(points[^2], points[^1], color);
            Panel.SetZIndex(arrow, 2);
            _canvas!.Children.Add(arrow);
            _relArrows[rel.Id] = arrow;
        }
    }

    private (Point? p1, Point? p2) GetPortCenters(CodeRelation rel)
    {
        Point? p1 = GetPortCenter(rel.FromId, rel.FromPortId);
        Point? p2 = GetPortCenter(rel.ToId,   rel.ToPortId);
        return (p1, p2);
    }

    private Point? GetPortCenter(string entityId, string portId)
    {
        var key = $"{entityId}:{portId}";
        if (!_portDots.TryGetValue(key, out var dot)) return null;
        double x = Canvas.GetLeft(dot) + PortRadius;
        double y = Canvas.GetTop(dot)  + PortRadius;
        return new Point(x, y);
    }

    private void UpdateRelationsForEntity(string entityId)
    {
        foreach (var rel in _boardData.Relations)
        {
            if (rel.FromId == entityId || rel.ToId == entityId)
                RenderRelation(rel);
        }
    }

    private static Polygon BuildArrow(Point from, Point to, Color color)
    {
        var dir = to - from;
        var len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
        if (len < 0.001) return new Polygon();
        var u = new Vector(dir.X / len, dir.Y / len);
        var perp = new Vector(-u.Y, u.X);
        const double aw = 6, al = 10;
        var tip  = to;
        var b1   = tip - u * al + perp * aw;
        var b2   = tip - u * al - perp * aw;
        return new Polygon
        {
            Points          = [new Point(tip.X, tip.Y), new Point(b1.X, b1.Y), new Point(b2.X, b2.Y)],
            Fill            = new SolidColorBrush(color),
            IsHitTestVisible = false
        };
    }

    private static void ApplyLineStyle(Line line, BoardLineStyle style)
    {
        line.StrokeDashArray = style switch
        {
            BoardLineStyle.Dotted    => [2, 3],
            BoardLineStyle.Dashed    => [6, 3],
            BoardLineStyle.DotDash   => [6, 3, 2, 3],
            _                        => null
        };
    }

    // ── Canvas mouse events (rubber band + deselect) ───────────────────────

    private bool    _rubberSelecting = false;
    private Point   _rubberStart;
    private System.Windows.Shapes.Rectangle? _rubberRect;

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_connectMode) return;
        // Deselect
        _selectedIds.Clear();
        RefreshSelectionVisuals();
        _rubberStart     = e.GetPosition(_canvas);
        _rubberSelecting = true;
        _canvas!.CaptureMouse();
        e.Handled = true;
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        // Update rubber band connection line
        if (_connectMode && _connectFromEntityId is not null && _rubberBand is not null)
        {
            var srcKey = $"{_connectFromEntityId}:{_connectFromPortId}";
            if (_portDots.TryGetValue(srcKey, out var srcDot))
            {
                _rubberBand.X1 = Canvas.GetLeft(srcDot) + PortRadius;
                _rubberBand.Y1 = Canvas.GetTop(srcDot)  + PortRadius;
            }
            var mp = e.GetPosition(_canvas);
            _rubberBand.X2 = mp.X;
            _rubberBand.Y2 = mp.Y;
        }

        // Rubber-band selection rectangle
        if (_rubberSelecting)
        {
            var cur = e.GetPosition(_canvas);
            if (_rubberRect is null)
            {
                _rubberRect = new System.Windows.Shapes.Rectangle
                {
                    Stroke          = Brushes.DodgerBlue,
                    StrokeDashArray = [4, 2],
                    StrokeThickness = 1,
                    Fill            = new SolidColorBrush(Color.FromArgb(30, 30, 144, 255)),
                    IsHitTestVisible = false
                };
                Panel.SetZIndex(_rubberRect, 50);
                _canvas!.Children.Add(_rubberRect);
            }
            double x = Math.Min(_rubberStart.X, cur.X);
            double y = Math.Min(_rubberStart.Y, cur.Y);
            double w = Math.Abs(cur.X - _rubberStart.X);
            double h = Math.Abs(cur.Y - _rubberStart.Y);
            Canvas.SetLeft(_rubberRect, x); Canvas.SetTop(_rubberRect, y);
            _rubberRect.Width = w; _rubberRect.Height = h;
        }
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_rubberSelecting)
        {
            _rubberSelecting = false;
            _canvas!.ReleaseMouseCapture();
            if (_rubberRect is not null)
            {
                // Select all cards inside rubber band
                double rx = Canvas.GetLeft(_rubberRect), ry = Canvas.GetTop(_rubberRect);
                double rw = _rubberRect.Width, rh = _rubberRect.Height;
                foreach (var (id, card) in _cards)
                {
                    double cx = Canvas.GetLeft(card), cy = Canvas.GetTop(card);
                    if (cx >= rx && cy >= ry && cx + card.ActualWidth <= rx + rw && cy + card.ActualHeight <= ry + rh)
                        _selectedIds.Add(id);
                }
                _canvas.Children.Remove(_rubberRect);
                _rubberRect = null;
                RefreshSelectionVisuals();
            }
        }
    }

    private void Canvas_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (_connectMode)
        {
            _connectFromEntityId = null;
            _connectFromPortId   = null;
            RemoveRubberBand();
            return;
        }
        var cm = new ContextMenu();
        foreach (var t in CodeEntityService.EntityTypes)
        {
            var capType = t;
            var mi = new MenuItem { Header = string.Format(Properties.Loc.S("Code_AddType"), capType) };
            mi.Click += (_, _) => ShowAddEntityOfType(capType, e.GetPosition(_canvas));
            cm.Items.Add(mi);
        }
        cm.IsOpen = true;
    }

    // ── Context menus ──────────────────────────────────────────────────────

    private void ShowCardContextMenu(CodeEntity entity)
    {
        var cm = new ContextMenu();

        var editMi = new MenuItem { Header = Properties.Loc.S("Code_Edit") };
        editMi.Click += (_, _) => ShowEntityEditor(entity);
        cm.Items.Add(editMi);

        if (entity.EntityType == CodeEntityType.Function)
        {
            var flowMi = new MenuItem { Header = Properties.Loc.S("Code_SketchFlow") };
            flowMi.Click += (_, _) =>
                DiagramLauncher.ChooseAndOpen(this, _projFolder, entity.Id, entity.Name, _themePath);
            cm.Items.Add(flowMi);
        }

        cm.Items.Add(new Separator());

        if (_boardData.Positions.TryGetValue(entity.Id, out var pos))
        {
            var orientMi = new MenuItem
            {
                Header = pos.PortOrientation == PortOrientation.Horizontal
                    ? Properties.Loc.S("Code_SwitchVertical")
                    : Properties.Loc.S("Code_SwitchHorizontal")
            };
            orientMi.Click += (_, _) =>
            {
                pos.PortOrientation = pos.PortOrientation == PortOrientation.Horizontal
                    ? PortOrientation.Vertical
                    : PortOrientation.Horizontal;
                CodeBoardDataService.Save(_projFolder, _board.Id, _boardData);
                UpdatePortPositions(entity.Id);
                UpdateRelationsForEntity(entity.Id);
            };
            cm.Items.Add(orientMi);
            cm.Items.Add(new Separator());
        }

        var removeMi = new MenuItem { Header = Properties.Loc.S("Code_RemoveFromBoard") };
        removeMi.Click += (_, _) => RemoveFromBoard(new[] { entity.Id });
        cm.Items.Add(removeMi);

        var deleteMi = new MenuItem { Header = Properties.Loc.S("Code_DeletePerm") };
        deleteMi.Click += (_, _) =>
        {
            var res = MessageBox.Show(
                string.Format(Properties.Loc.S("Code_DeletePermConfirm"), entity.Name),
                Properties.Loc.S("Code_DeleteEntityTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;
            CodeEntityService.Delete(_projFolder, entity.EntityType.ToString(), entity.Id);
            _entities.Remove(entity.Id);
            RemoveFromBoard(new[] { entity.Id });
        };
        cm.Items.Add(deleteMi);

        cm.IsOpen = true;
    }

    private void ShowRelationContextMenu(CodeRelation rel)
    {
        var cm = new ContextMenu();
        var delMi = new MenuItem { Header = Properties.Loc.S("Code_DeleteConnection") };
        delMi.Click += (_, _) =>
        {
            _boardData.Relations.Remove(rel);
            CodeBoardDataService.Save(_projFolder, _board.Id, _boardData);
            if (_relLines.TryGetValue(rel.Id, out var ls)) foreach (var l in ls) _canvas!.Children.Remove(l);
            _relLines.Remove(rel.Id);
            if (_relArrows.TryGetValue(rel.Id, out var a)) _canvas!.Children.Remove(a);
            _relArrows.Remove(rel.Id);
        };
        cm.Items.Add(delMi);
        cm.IsOpen = true;
    }

    // ── Add entity ─────────────────────────────────────────────────────────

    private void ShowAddEntityMenu(Button anchor)
    {
        var cm = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var t in CodeEntityService.EntityTypes)
        {
            var capType = t;
            var mi = new MenuItem { Header = capType };
            mi.Click += (_, _) => ShowAddEntityOfType(capType, new Point(60, 60));
            cm.Items.Add(mi);
        }
        // Option to add existing entity
        cm.Items.Add(new Separator());
        var existMi = new MenuItem { Header = Properties.Loc.S("Code_AddExisting") };
        existMi.Click += (_, _) => ShowAddExistingEntityDialog();
        cm.Items.Add(existMi);
        cm.IsOpen = true;
    }

    private void ShowAddEntityOfType(string entityTypeName, Point dropPoint)
    {
        var dialog = new Window
        {
            Title                 = string.Format(Properties.Loc.S("Code_NewTypeTitle"), entityTypeName),
            Width                 = 400,
            Height                = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ResizeMode            = ResizeMode.NoResize
        };
        ApplyThemeTo(dialog);

        var g = new Grid { Margin = new Thickness(16) };
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        dialog.Content = g;

        var lbl = new TextBlock { Text = Properties.Loc.S("Common_NameColon"), Margin = new Thickness(0, 0, 0, 4) };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
        Grid.SetRow(lbl, 0);
        g.Children.Add(lbl);

        var box = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
        box.SetResourceReference(TextBox.BackgroundProperty,   "InputBgBrush");
        box.SetResourceReference(TextBox.ForegroundProperty,   "SidebarTextBrush");
        box.SetResourceReference(TextBox.BorderBrushProperty,  "ControlBorderBrush");
        Grid.SetRow(box, 1);
        g.Children.Add(box);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(btnRow, 2);
        g.Children.Add(btnRow);

        var okBtn = Btn(Properties.Loc.S("Common_Add"), null);
        okBtn.Click += (_, _) =>
        {
            var name = box.Text.Trim();
            if (string.IsNullOrEmpty(name)) return;
            if (!Enum.TryParse<CodeEntityType>(entityTypeName, out var et)) return;
            var entity = new CodeEntity { Name = name, EntityType = et };
            _entities[entity.Id] = entity;
            CodeEntityService.Save(_projFolder, entityTypeName, entity);
            var pos = new CodeCardPosition { X = dropPoint.X, Y = dropPoint.Y };
            _boardData.Positions[entity.Id] = pos;
            CodeBoardDataService.Save(_projFolder, _board.Id, _boardData);
            RenderCard(entity, pos);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => UpdatePortPositions(entity.Id)));
            dialog.Close();
        };
        box.KeyDown += (_, e) => { if (e.Key == Key.Return) okBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
        btnRow.Children.Add(okBtn);
        var cancelBtn = Btn(Properties.Loc.S("Common_Cancel"), null);
        cancelBtn.Margin = new Thickness(8, 0, 0, 0);
        cancelBtn.Click += (_, _) => dialog.Close();
        btnRow.Children.Add(cancelBtn);

        dialog.Loaded += (_, _) => box.Focus();
        dialog.ShowDialog();
    }

    private void ShowAddExistingEntityDialog()
    {
        var allOnBoard = _boardData.Positions.Keys.ToHashSet();
        var available  = _entities.Values.Where(e => !allOnBoard.Contains(e.Id)).ToList();

        // Also load from disk (entities that haven't been loaded into _entities yet)
        foreach (var t in CodeEntityService.EntityTypes)
        {
            foreach (var e in CodeEntityService.LoadAll(_projFolder, t))
            {
                if (!allOnBoard.Contains(e.Id) && !_entities.ContainsKey(e.Id))
                {
                    _entities[e.Id] = e;
                    available.Add(e);
                }
            }
        }

        if (available.Count == 0)
        {
            MessageBox.Show(Properties.Loc.S("Code_AllOnBoard"), Properties.Loc.S("Code_AddEntityTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Window
        {
            Title                 = Properties.Loc.S("Code_AddExistingTitle"),
            Width                 = 360,
            Height                = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ResizeMode            = ResizeMode.NoResize
        };
        ApplyThemeTo(dialog);

        var g = new Grid { Margin = new Thickness(12) };
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        dialog.Content = g;

        var lb = new ListBox { Margin = new Thickness(0, 0, 0, 8) };
        lb.SetResourceReference(ListBox.BackgroundProperty,  "InputBgBrush");
        lb.SetResourceReference(ListBox.ForegroundProperty,  "SidebarTextBrush");
        lb.SetResourceReference(ListBox.BorderBrushProperty, "ControlBorderBrush");
        foreach (var e in available.OrderBy(e => e.EntityType.ToString()).ThenBy(e => e.Name))
        {
            lb.Items.Add(new ListBoxItem
            {
                Content = $"{e.EntityType}  {e.Name}",
                Tag     = e
            });
        }
        Grid.SetRow(lb, 0);
        g.Children.Add(lb);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(btnRow, 1);
        g.Children.Add(btnRow);

        var addBtn = Btn(Properties.Loc.S("Common_Add"), null);
        addBtn.Click += (_, _) =>
        {
            if (lb.SelectedItem is not ListBoxItem { Tag: CodeEntity entity }) return;
            var pos = new CodeCardPosition { X = 80, Y = 80 };
            _boardData.Positions[entity.Id] = pos;
            CodeBoardDataService.Save(_projFolder, _board.Id, _boardData);
            RenderCard(entity, pos);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => UpdatePortPositions(entity.Id)));
            dialog.Close();
        };
        btnRow.Children.Add(addBtn);
        var cancelBtn = Btn(Properties.Loc.S("Common_Cancel"), null);
        cancelBtn.Margin = new Thickness(8, 0, 0, 0);
        cancelBtn.Click += (_, _) => dialog.Close();
        btnRow.Children.Add(cancelBtn);

        dialog.ShowDialog();
    }

    // ── Entity editor (delegates to the shared standalone dialog) ──────────

    private void ShowEntityEditor(CodeEntity entity)
    {
        // Ensure dropdowns can see all entities (base class / interface / instance)
        foreach (var t in CodeEntityService.EntityTypes)
            foreach (var e in CodeEntityService.LoadAll(_projFolder, t))
                _entities.TryAdd(e.Id, e);

        var dlg = new CodeEntityEditorDialog(_projFolder, entity, _entities, _themePath) { Owner = this };
        dlg.ShowDialog();
        if (!dlg.Saved) return;

        _entities[entity.Id] = entity;

        // Rebuild card visuals
        if (_cards.TryGetValue(entity.Id, out var oldCard))
        {
            _canvas!.Children.Remove(oldCard);
            _cards.Remove(entity.Id);
        }
        foreach (var key in _portDots.Keys.Where(k => k.StartsWith(entity.Id + ":")).ToList())
        {
            _canvas!.Children.Remove(_portDots[key]);
            _portDots.Remove(key);
        }
        if (_boardData.Positions.TryGetValue(entity.Id, out var pos))
        {
            RenderCard(entity, pos);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                UpdatePortPositions(entity.Id);
                UpdateRelationsForEntity(entity.Id);
            }));
        }
    }

    // ── Remove from board ──────────────────────────────────────────────────

    private void RemoveSelectedFromBoard() => RemoveFromBoard(_selectedIds.ToList());

    private void RemoveFromBoard(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            _boardData.Positions.Remove(id);

            // Remove card
            if (_cards.TryGetValue(id, out var card))
            {
                _canvas!.Children.Remove(card);
                _cards.Remove(id);
            }

            // Remove port dots
            foreach (var key in _portDots.Keys.Where(k => k.StartsWith(id + ":")).ToList())
            {
                _canvas!.Children.Remove(_portDots[key]);
                _portDots.Remove(key);
            }

            // Remove touching relations
            var toRemove = _boardData.Relations.Where(r => r.FromId == id || r.ToId == id).ToList();
            foreach (var rel in toRemove)
            {
                _boardData.Relations.Remove(rel);
                if (_relLines.TryGetValue(rel.Id, out var ls)) foreach (var l in ls) _canvas!.Children.Remove(l);
                _relLines.Remove(rel.Id);
                if (_relArrows.TryGetValue(rel.Id, out var a)) _canvas!.Children.Remove(a);
                _relArrows.Remove(rel.Id);
            }
        }
        _selectedIds.Clear();
        CodeBoardDataService.Save(_projFolder, _board.Id, _boardData);
    }

    // ── Selection visuals ──────────────────────────────────────────────────

    private void RefreshSelectionVisuals()
    {
        bool any = _selectedIds.Count > 0;
        var accent = (Brush)(TryFindResource("AccentHighlightBrush") ?? new SolidColorBrush(Colors.DodgerBlue));
        foreach (var (id, card) in _cards)
        {
            bool sel = _selectedIds.Contains(id);
            card.Opacity = any && !sel ? 0.45 : 1.0;
            if (sel) { card.BorderBrush = accent; card.BorderThickness = new Thickness(2); }
            else { card.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush"); card.BorderThickness = new Thickness(1); }
        }
    }

    // ── Snap to grid ───────────────────────────────────────────────────────

    private double Snap(double v)
    {
        if (!_boardData.SnapToGrid || _boardData.GridSize < 1) return v;
        return Math.Round(v / _boardData.GridSize) * _boardData.GridSize;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static (Color color, string symbol) EntityTypeStyle(CodeEntityType t) => t switch
    {
        CodeEntityType.Class     => (Color.FromRgb(0x19, 0x76, 0xD2), "🧱"),
        CodeEntityType.Struct    => (Color.FromRgb(0x00, 0x89, 0x7B), "📦"),
        CodeEntityType.Interface => (Color.FromRgb(0x6A, 0x1B, 0x9A), "🔷"),
        CodeEntityType.Enum      => (Color.FromRgb(0xE6, 0x51, 0x00), "📋"),
        CodeEntityType.Function  => (Color.FromRgb(0x2E, 0x7D, 0x32), "⚡"),
        CodeEntityType.Namespace => (Color.FromRgb(0x37, 0x47, 0x4F), "📁"),
        _                        => (Color.FromRgb(0x55, 0x55, 0x55), "⚙")
    };

    private static Color ParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Colors.DodgerBlue; }
    }

    private static System.Windows.Media.Effects.DropShadowEffect GlowEffect() =>
        new() { Color = Colors.White, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.5 };

    private Button Btn(string label, string? tooltip)
    {
        var b = new Button
        {
            Content = label,
            Padding = new Thickness(10, 5, 10, 5),
            Margin  = new Thickness(0, 0, 4, 0),
            FontSize = 12,
            ToolTip = tooltip
        };
        b.SetResourceReference(Button.StyleProperty,      "ModernButton");
        b.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        b.SetResourceReference(Button.ForegroundProperty, "SidebarTextBrush");
        return b;
    }

    private static void Spacer(Panel p, double w) =>
        p.Children.Add(new Border { Width = w });

    private TextBox LabeledTextBox(Grid g, int row, string label, string value, bool multiLine = false)
    {
        var lbl = new TextBlock { Text = label, Margin = new Thickness(0, row == 0 ? 0 : 8, 0, 2) };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
        Grid.SetRow(lbl, row); g.Children.Add(lbl);

        var box = new TextBox
        {
            Text              = value,
            AcceptsReturn     = multiLine,
            TextWrapping      = multiLine ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Height            = multiLine ? 60 : double.NaN,
            VerticalScrollBarVisibility = multiLine ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            Margin            = new Thickness(0, 0, 0, 4)
        };
        box.SetResourceReference(TextBox.BackgroundProperty,  "InputBgBrush");
        box.SetResourceReference(TextBox.ForegroundProperty,  "SidebarTextBrush");
        box.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        Grid.SetRow(box, row); g.Children.Add(box);
        return box;
    }

    private void ApplyThemeTo(Window w)
    {
        if (!string.IsNullOrWhiteSpace(_themePath))
        {
            try
            {
                var dict = OxsuitLoader.Load(_themePath);
                if (dict is not null) w.Resources.MergedDictionaries.Add(dict);
            }
            catch { }
        }
        w.SetResourceReference(BackgroundProperty, "ContentBgBrush");
    }
}
