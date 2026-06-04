using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClaudetRelay.Services;

namespace ClaudetRelay;

/// <summary>
/// Standalone board editor window for a single WorldBoard.
/// Shows a free-canvas view of entities and their relations.
/// Multiple board windows can be open simultaneously.
/// </summary>
public class WorldBoardWindow : Window
{
    // ── Core state ─────────────────────────────────────────────────────────
    private readonly string     _projFolder;
    private readonly WorldBoard _board;
    private readonly string?    _themePath;

    // Board canvas state
    private EntityBoardData    _boardData;
    private Canvas?            _boardCanvas;
    private bool               _boardConnectMode = false;
    private string?            _boardConnSrcId   = null;
    private bool               _entityEditOpen   = false;

    private readonly Dictionary<string, Border>               _boardCards         = new();
    private readonly Dictionary<string, (Line L1, Line? L2)> _boardLines         = new();
    private readonly Dictionary<string, Border>               _boardCaptionBadges = new();

    // Filter state
    private string?  _filterName      = null;
    private string?  _filterFaction   = null;
    private string?  _filterArc       = null;
    private string?  _filterAlignment = null;
    private string   _sortMode        = "name_asc";  // "name_asc", "name_desc", "date_asc", "date_desc"

    // ── Constructor ────────────────────────────────────────────────────────

    public WorldBoardWindow(string projFolder, WorldBoard board, string? themePath)
    {
        _projFolder = projFolder;
        _board      = board;
        _themePath  = themePath;
        _boardData  = EntityBoardService.Load(projFolder, board.Id);

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
        Loaded            += (_, _) => BuildBoardContent();
    }

    // ── Filter & Sort ──────────────────────────────────────────────────────

    private List<WorldEntity> GetFilteredAndSortedEntities(List<WorldEntity> allEntities)
    {
        var filtered = allEntities
            .Where(e =>
            {
                // Name filter
                if (!string.IsNullOrWhiteSpace(_filterName) &&
                    !e.Name.Contains(_filterName, StringComparison.OrdinalIgnoreCase))
                    return false;

                // Faction filter
                if (_filterFaction != null)
                {
                    var factionVal = e.Fields.TryGetValue("Faction", out var f) ? f : null;
                    bool match = (_filterFaction == "<<none>>" && string.IsNullOrWhiteSpace(factionVal)) ||
                                 (!string.IsNullOrWhiteSpace(factionVal) && factionVal == _filterFaction);
                    if (!match) return false;
                }

                // Arc filter
                if (_filterArc != null)
                {
                    var arcVal = e.Fields.TryGetValue("Arc", out var a) ? a : null;
                    bool match = (_filterArc == "<<none>>" && string.IsNullOrWhiteSpace(arcVal)) ||
                                 (!string.IsNullOrWhiteSpace(arcVal) && arcVal == _filterArc);
                    if (!match) return false;
                }

                // Alignment filter
                if (_filterAlignment != null)
                {
                    var alignVal = e.Fields.TryGetValue("Alignment", out var al) ? al : null;
                    bool match = (_filterAlignment == "<<none>>" && string.IsNullOrWhiteSpace(alignVal)) ||
                                 (!string.IsNullOrWhiteSpace(alignVal) && alignVal == _filterAlignment);
                    if (!match) return false;
                }

                return true;
            })
            .ToList();

        // Apply sorting
        switch (_sortMode)
        {
            case "name_desc":
                filtered.Sort((a, b) => string.Compare(b.Name, a.Name, StringComparison.OrdinalIgnoreCase));
                break;
            case "date_asc":
                filtered.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
                break;
            case "date_desc":
                filtered.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
                break;
            default: // name_asc
                filtered.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                break;
        }

        return filtered;
    }

    private (List<string> factions, List<string> arcs, List<string> alignments) ExtractFilterOptions(List<WorldEntity> allEntities)
    {
        var factions = new HashSet<string>();
        var arcs = new HashSet<string>();
        var alignments = new HashSet<string>();
        bool hasMissingFaction = false, hasMissingArc = false, hasMissingAlignment = false;

        foreach (var e in allEntities)
        {
            if (e.Fields.TryGetValue("Faction", out var f) && !string.IsNullOrWhiteSpace(f))
                factions.Add(f);
            else
                hasMissingFaction = true;

            if (e.Fields.TryGetValue("Arc", out var a) && !string.IsNullOrWhiteSpace(a))
                arcs.Add(a);
            else
                hasMissingArc = true;

            if (e.Fields.TryGetValue("Alignment", out var al) && !string.IsNullOrWhiteSpace(al))
                alignments.Add(al);
            else
                hasMissingAlignment = true;
        }

        var factionList = factions.OrderBy(x => x).ToList();
        var arcList = arcs.OrderBy(x => x).ToList();
        var alignmentList = alignments.OrderBy(x => x).ToList();

        if (hasMissingFaction) factionList.Insert(0, "<<none>>");
        if (hasMissingArc) arcList.Insert(0, "<<none>>");
        if (hasMissingAlignment) alignmentList.Insert(0, "<<none>>");

        return (factionList, arcList, alignmentList);
    }

    // ── Build ──────────────────────────────────────────────────────────────

    public void BuildBoardContent()
    {
        const double EstCardH = 90;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Content = root;

        // ── Toolbar ────────────────────────────────────────────────────────
        var toolBorder = new Border { Padding = new Thickness(14, 7, 14, 7), BorderThickness = new Thickness(0, 0, 0, 1) };
        toolBorder.SetResourceReference(Border.BackgroundProperty,  "ControlBgBrush");
        toolBorder.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        Grid.SetRow(toolBorder, 0);
        root.Children.Add(toolBorder);

        var toolPanel = new StackPanel { Orientation = Orientation.Horizontal };
        toolBorder.Child = toolPanel;

        var connectBtn = MakeBtn(_boardConnectMode ? "🔗 Connecting…" : "🔗 Add Relation", _boardConnectMode);
        connectBtn.Click += (_, _) => { _boardConnectMode = !_boardConnectMode; _boardConnSrcId = null; BuildBoardContent(); };
        toolPanel.Children.Add(connectBtn);

        var arrangeBtn = MakeBtn("📐 Auto-arrange", false);
        arrangeBtn.Margin = new Thickness(6, 0, 0, 0);
        arrangeBtn.Click += (_, _) => AutoArrangeBoard();
        toolPanel.Children.Add(arrangeBtn);

        foreach (var et in _board.EntityTypes)
        {
            var capturedEt = et;
            var addBtn = MakeBtn($"+ {et}", false);
            addBtn.Margin = new Thickness(6, 0, 0, 0);
            addBtn.ToolTip = $"Pick existing {et}s to add to this board";
            addBtn.Click += (_, _) =>
            {
                var allOfType   = WorldEntityService.List(_projFolder, capturedEt);
                var alreadyOnBoard = _boardData.Positions.Keys.ToHashSet();
                var eligible    = allOfType.Where(e => !alreadyOnBoard.Contains(e.Id)).ToList();
                if (eligible.Count == 0)
                {
                    MessageBox.Show($"All {capturedEt}s are already on this board, or none exist yet.\n\nCreate new ones from the {capturedEt}s list view.",
                        $"No {capturedEt}s to add", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var picked = ShowEntityPickerDialog(eligible, $"Add {capturedEt}s to Board");
                if (picked.Count == 0) return;
                // Place picked entities at sensible default positions
                var usedYs = _boardData.Positions.Values.Select(p => p.Y).DefaultIfEmpty(0).Max();
                double placeY = usedYs + 200;
                double placeX = 40;
                foreach (var e in picked)
                {
                    _boardData.Positions[e.Id] = new BoardPosition { X = placeX, Y = placeY };
                    placeX += 200;
                    if (placeX > 1400) { placeX = 40; placeY += 200; }
                }
                EntityBoardService.Save(_projFolder, _board.Id, _boardData);
                BuildBoardContent();
            };
            toolPanel.Children.Add(addBtn);
        }

        // Load all entities for filter options extraction
        var allEntities = _board.EntityTypes
            .SelectMany(et => WorldEntityService.List(_projFolder, et))
            .ToList();

        var (factionOptions, arcOptions, alignmentOptions) = ExtractFilterOptions(allEntities);

        // Name search textbox
        var nameSearchBox = new TextBox
        {
            Text = _filterName ?? "", ToolTip = "Search by name",
            Width = 140, FontSize = 11, Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(20, 0, 0, 0), BorderThickness = new Thickness(1)
        };
        nameSearchBox.SetResourceReference(TextBox.BackgroundProperty,  "ControlBgBrush");
        nameSearchBox.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        nameSearchBox.SetResourceReference(TextBox.ForegroundProperty,  "ContentTextBrush");
        nameSearchBox.TextChanged += (_, _) =>
        {
            _filterName = string.IsNullOrWhiteSpace(nameSearchBox.Text) ? null : nameSearchBox.Text;
            BuildBoardContent();
        };
        toolPanel.Children.Add(nameSearchBox);

        // Faction dropdown
        if (factionOptions.Count > 0)
        {
            var factionCombo = new ComboBox { Width = 140, FontSize = 11, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(8, 0, 0, 0) };
            factionCombo.Items.Add("All Factions");
            foreach (var f in factionOptions)
                factionCombo.Items.Add(f == "<<none>>" ? "(no faction)" : f);
            factionCombo.SelectedIndex = 0;
            factionCombo.SelectionChanged += (_, _) =>
            {
                int idx = factionCombo.SelectedIndex;
                _filterFaction = idx <= 0 ? null : (factionOptions[idx - 1] == "<<none>>" ? "<<none>>" : factionOptions[idx - 1]);
                BuildBoardContent();
            };
            toolPanel.Children.Add(factionCombo);
        }

        // Arc dropdown
        if (arcOptions.Count > 0)
        {
            var arcCombo = new ComboBox { Width = 140, FontSize = 11, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(8, 0, 0, 0) };
            arcCombo.Items.Add("All Arcs");
            foreach (var a in arcOptions)
                arcCombo.Items.Add(a == "<<none>>" ? "(no arc)" : a);
            arcCombo.SelectedIndex = 0;
            arcCombo.SelectionChanged += (_, _) =>
            {
                int idx = arcCombo.SelectedIndex;
                _filterArc = idx <= 0 ? null : (arcOptions[idx - 1] == "<<none>>" ? "<<none>>" : arcOptions[idx - 1]);
                BuildBoardContent();
            };
            toolPanel.Children.Add(arcCombo);
        }

        // Alignment dropdown
        if (alignmentOptions.Count > 0)
        {
            var alignCombo = new ComboBox { Width = 140, FontSize = 11, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(8, 0, 0, 0) };
            alignCombo.Items.Add("All Alignments");
            foreach (var al in alignmentOptions)
                alignCombo.Items.Add(al == "<<none>>" ? "(no alignment)" : al);
            alignCombo.SelectedIndex = 0;
            alignCombo.SelectionChanged += (_, _) =>
            {
                int idx = alignCombo.SelectedIndex;
                _filterAlignment = idx <= 0 ? null : (alignmentOptions[idx - 1] == "<<none>>" ? "<<none>>" : alignmentOptions[idx - 1]);
                BuildBoardContent();
            };
            toolPanel.Children.Add(alignCombo);
        }

        // Sort buttons
        var sortPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 0, 0, 0) };
        toolPanel.Children.Add(sortPanel);

        var sortNameAscBtn = MakeBtn("A→Z", _sortMode == "name_asc");
        sortNameAscBtn.FontSize = 10; sortNameAscBtn.Padding = new Thickness(6, 3, 6, 3);
        sortNameAscBtn.Click += (_, _) => { _sortMode = "name_asc"; BuildBoardContent(); };
        sortPanel.Children.Add(sortNameAscBtn);

        var sortNameDescBtn = MakeBtn("Z→A", _sortMode == "name_desc");
        sortNameDescBtn.FontSize = 10; sortNameDescBtn.Padding = new Thickness(6, 3, 6, 3);
        sortNameDescBtn.Margin = new Thickness(4, 0, 0, 0);
        sortNameDescBtn.Click += (_, _) => { _sortMode = "name_desc"; BuildBoardContent(); };
        sortPanel.Children.Add(sortNameDescBtn);

        var sortDateOldestBtn = MakeBtn("📅↑", _sortMode == "date_asc");
        sortDateOldestBtn.FontSize = 10; sortDateOldestBtn.Padding = new Thickness(6, 3, 6, 3);
        sortDateOldestBtn.Margin = new Thickness(8, 0, 0, 0);
        sortPanel.Children.Add(sortDateOldestBtn);

        var sortDateNewestBtn = MakeBtn("📅↓", _sortMode == "date_desc");
        sortDateNewestBtn.FontSize = 10; sortDateNewestBtn.Padding = new Thickness(6, 3, 6, 3);
        sortDateNewestBtn.Margin = new Thickness(4, 0, 0, 0);
        sortDateNewestBtn.Click += (_, _) => { _sortMode = "date_desc"; BuildBoardContent(); };
        sortPanel.Children.Add(sortDateNewestBtn);

        sortDateOldestBtn.Click += (_, _) => { _sortMode = "date_asc"; BuildBoardContent(); };

        var resetBtn = MakeBtn("↺ Reset", false);
        resetBtn.FontSize = 10; resetBtn.Padding = new Thickness(6, 3, 6, 3);
        resetBtn.Margin = new Thickness(12, 0, 0, 0);
        resetBtn.Click += (_, _) =>
        {
            _filterName = null;
            _filterFaction = null;
            _filterArc = null;
            _filterAlignment = null;
            _sortMode = "name_asc";
            nameSearchBox.Text = "";
            BuildBoardContent();
        };
        sortPanel.Children.Add(resetBtn);

        var hint = _boardConnectMode
            ? (_boardConnSrcId == null ? "← Click a card to start" : "← Now click the target card")
            : "Drag cards · Double-click to view · 🔗 to connect";
        var hintBlock = new TextBlock
        {
            Text = hint, FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0)
        };
        hintBlock.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        toolPanel.Children.Add(hintBlock);

        // ── Board area (canvas + floating legend) ──────────────────────────
        var boardGrid = new Grid();
        Grid.SetRow(boardGrid, 1);
        root.Children.Add(boardGrid);

        var boardScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto
        };
        boardScroll.SetResourceReference(ScrollViewer.BackgroundProperty, "ContentBgBrush");
        boardGrid.Children.Add(boardScroll);

        var canvas = new Canvas { Width = 2400, Height = 1800 };
        canvas.SetResourceReference(Canvas.BackgroundProperty, "ContentBgBrush");
        boardScroll.Content = canvas;
        _boardCanvas = canvas;

        // Reload board data
        _boardData = EntityBoardService.Load(_projFolder, _board.Id);
        _boardCards.Clear();
        _boardLines.Clear();
        _boardCaptionBadges.Clear();

        // Only show entities explicitly placed on this board (position exists).
        // New entities are added via the "+" picker, not auto-placed.
        var boardPlacedEntities = allEntities
            .Where(e => _boardData.Positions.ContainsKey(e.Id))
            .ToList();
        var boardFactions = allEntities.Where(e => e.EntityType == "Faction").ToList();

        // Remove positions for entities that no longer exist (deleted)
        bool dirty = false;
        {
            var validIds = allEntities.Select(e => e.Id).ToHashSet();
            var stalePos = _boardData.Positions.Keys.Where(k => !validIds.Contains(k)).ToList();
            foreach (var sk in stalePos) { _boardData.Positions.Remove(sk); dirty = true; }
        }

        // Apply filter + sort to board-placed entities only
        var displayedEntities = GetFilteredAndSortedEntities(boardPlacedEntities);

        // Prune relations where either endpoint was deleted or removed from board
        var entityById = allEntities.ToDictionary(e => e.Id);
        var staleRels  = _boardData.Relations
            .Where(r => !entityById.ContainsKey(r.FromId) || !entityById.ContainsKey(r.ToId))
            .ToList();
        foreach (var sr in staleRels) { _boardData.Relations.Remove(sr); dirty = true; }
        if (dirty) EntityBoardService.Save(_projFolder, _board.Id, _boardData);

        // ── Render relations (z=0) + caption badges (z=1) ─────────────────
        foreach (var rel in _boardData.Relations)
        {
            var fp = _boardData.Positions[rel.FromId];
            var tp = _boardData.Positions[rel.ToId];
            var x1 = fp.X + (fp.CardWidth  > 0 ? fp.CardWidth  : 160) / 2; var y1 = fp.Y + EstCardH / 2;
            var x2 = tp.X + (tp.CardWidth  > 0 ? tp.CardWidth  : 160) / 2; var y2 = tp.Y + EstCardH / 2;
            RenderRelationVisuals(canvas, rel.Id, x1, y1, x2, y2, rel);

            var captionBadge = new Border
            {
                CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 2, 5, 2),
                BorderThickness = new Thickness(1), Cursor = Cursors.Hand
            };
            captionBadge.SetResourceReference(Border.BackgroundProperty,  "ControlBgBrush");
            captionBadge.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
            var captionText = new TextBlock
            {
                Text = rel.Caption, FontSize = 10, FontFamily = new FontFamily("Segoe UI"),
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 140
            };
            captionText.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
            captionBadge.Child = captionText;
            Canvas.SetLeft(captionBadge, (x1 + x2) / 2 - 30);
            Canvas.SetTop (captionBadge, (y1 + y2) / 2 - 12);
            Panel.SetZIndex(captionBadge, 1);

            var capturedRel = rel;
            var ctx = new ContextMenu();
            var editRelItem = new MenuItem { Header = "✏ Edit relation" };
            editRelItem.Click += (_, _) =>
            {
                var result = ShowRelationDialog(capturedRel.Caption, capturedRel.LegendLabel,
                    capturedRel.LineStyle, capturedRel.LineColor, capturedRel.Thickness, "Edit Relation");
                if (result is not null)
                {
                    capturedRel.Caption     = result.Caption;
                    capturedRel.LegendLabel = result.LegendLabel;
                    capturedRel.LineStyle   = result.LineStyle;
                    capturedRel.LineColor   = result.LineColor;
                    capturedRel.Thickness   = result.Thickness;
                    EntityBoardService.Save(_projFolder, _board.Id, _boardData);
                    BuildBoardContent();
                }
            };
            var delRelItem = new MenuItem { Header = "🗑 Delete relation" };
            delRelItem.Click += (_, _) =>
            {
                _boardData.Relations.Remove(capturedRel);
                EntityBoardService.Save(_projFolder, _board.Id, _boardData);
                BuildBoardContent();
            };
            ctx.Items.Add(editRelItem);
            ctx.Items.Add(delRelItem);
            captionBadge.ContextMenu = ctx;
            canvas.Children.Add(captionBadge);
            _boardCaptionBadges[rel.Id] = captionBadge;
        }

        // ── Render entity cards (z=2) ──────────────────────────────────────
        foreach (var entity in displayedEntities)
        {
            var pos  = _boardData.Positions[entity.Id];
            var card = BuildBoardCard(entity, boardFactions);
            Canvas.SetLeft(card, pos.X);
            Canvas.SetTop (card, pos.Y);
            Panel.SetZIndex(card, 2);
            canvas.Children.Add(card);
            _boardCards[entity.Id] = card;

            var capturedId     = entity.Id;
            var capturedEntity = entity;
            bool isDragging    = false;
            var  dragOffset    = new Point();

            card.MouseLeftButtonDown += (_, e) =>
            {
                if (_boardConnectMode) { HandleBoardConnectClick(capturedId); e.Handled = true; return; }
                if (e.ClickCount >= 2) { ShowEntityReadOnlyDialog(capturedEntity); e.Handled = true; return; }
                isDragging = true;
                dragOffset = e.GetPosition(card);
                card.CaptureMouse();
                e.Handled = true;
            };
            card.MouseMove += (_, e) =>
            {
                if (!isDragging) return;
                var pt = e.GetPosition(_boardCanvas);
                var nx = Math.Max(0, pt.X - dragOffset.X);
                var ny = Math.Max(0, pt.Y - dragOffset.Y);
                Canvas.SetLeft(card, nx);
                Canvas.SetTop (card, ny);
                UpdateBoardLines(capturedId, nx, ny);
                e.Handled = true;
            };
            card.MouseLeftButtonUp += (_, e) =>
            {
                if (!isDragging) return;
                isDragging = false;
                card.ReleaseMouseCapture();
                var nx = Canvas.GetLeft(card);
                var ny = Canvas.GetTop(card);
                if (_boardData.Positions.TryGetValue(capturedId, out var bp)) { bp.X = nx; bp.Y = ny; }
                else _boardData.Positions[capturedId] = new BoardPosition { X = nx, Y = ny };
                EntityBoardService.Save(_projFolder, _board.Id, _boardData);
                e.Handled = true;
            };
        }

        // Floating legend overlay
        var legendOverlay = BuildBoardLegend();
        legendOverlay.HorizontalAlignment = HorizontalAlignment.Left;
        legendOverlay.VerticalAlignment   = VerticalAlignment.Top;
        legendOverlay.Margin              = new Thickness(10, 10, 0, 0);
        Panel.SetZIndex(legendOverlay, 10);
        boardGrid.Children.Add(legendOverlay);

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RefreshAllBoardLinePositions));
    }

    // ── Board card ─────────────────────────────────────────────────────────

    private Border BuildBoardCard(WorldEntity entity, IReadOnlyList<WorldEntity> boardFactions)
    {
        const double DefaultCardW = 160;
        const double ThumbW = 52;

        bool isConnSrc = _boardConnSrcId == entity.Id;
        bool isFaction = entity.EntityType == "Faction";
        Color? factionAccent = null;
        if (isFaction && !string.IsNullOrEmpty(entity.FactionColor))
            try { factionAccent = (Color)ColorConverter.ConvertFromString(entity.FactionColor)!; } catch { }

        // Resolve stored card size
        double cardW = DefaultCardW;
        double cardH = double.NaN;
        if (_boardData.Positions.TryGetValue(entity.Id, out var bpRef))
        {
            if (bpRef.CardWidth  > 0) cardW = bpRef.CardWidth;
            if (bpRef.CardHeight > 0) cardH = bpRef.CardHeight;
        }

        var card = new Border
        {
            Width           = cardW,
            Height          = double.IsNaN(cardH) ? double.NaN : cardH,
            CornerRadius    = new CornerRadius(8),
            BorderThickness = new Thickness(isConnSrc ? 2 : 1),
            Padding         = new Thickness(8, 7, 8, 2),
            Cursor          = _boardConnectMode ? Cursors.Hand : Cursors.SizeAll,
            ClipToBounds    = true,
            Effect          = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 2, Opacity = 0.22, Color = Colors.Black }
        };
        card.SetResourceReference(Border.BackgroundProperty, "ControlBgBrush");
        card.BorderBrush = isConnSrc
            ? (Brush)(TryFindResource("AccentHighlightBrush") ?? new SolidColorBrush(Colors.DodgerBlue))
            : factionAccent.HasValue
                ? new SolidColorBrush(Color.FromArgb(180, factionAccent.Value.R, factionAccent.Value.G, factionAccent.Value.B))
                : (Brush)(TryFindResource("ControlBorderBrush") ?? new SolidColorBrush(Colors.Gray));

        // ── Card grid: content row + resize strip ──────────────────────────
        var cardGrid = new Grid();
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14, GridUnitType.Pixel) });
        card.Child = cardGrid;

        // ── Content area ───────────────────────────────────────────────────
        var contentRow = new Grid();
        contentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // thumbnail
        contentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // text
        Grid.SetRow(contentRow, 0);
        cardGrid.Children.Add(contentRow);

        // Thumbnail (portrait for Character, image for Location)
        string? thumbPath = null;
        if (entity.EntityType == "Character" && !string.IsNullOrWhiteSpace(entity.PortraitFileName))
            thumbPath = WorldEntityService.GetPortraitPath(_projFolder, entity.PortraitFileName);
        else if (entity.EntityType == "Location" && !string.IsNullOrWhiteSpace(entity.ImageFileName))
            thumbPath = WorldEntityService.GetImagePath(_projFolder, entity.ImageFileName);

        if (thumbPath != null && System.IO.File.Exists(thumbPath))
        {
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.UriSource        = new Uri(thumbPath);
                bmp.DecodePixelWidth = (int)(ThumbW * 2); // 2× for crisp HiDPI
                bmp.CacheOption      = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();

                double thumbH = entity.EntityType == "Character" ? ThumbW * 4 / 3.0 : ThumbW;
                var img = new System.Windows.Controls.Image
                {
                    Source  = bmp,
                    Width   = ThumbW,
                    Height  = thumbH,
                    Stretch = Stretch.UniformToFill
                };
                var thumbBorder = new Border
                {
                    Width = ThumbW, Height = thumbH,
                    CornerRadius = new CornerRadius(5, 0, 0, 5),
                    ClipToBounds = true,
                    Margin = new Thickness(-8, -7, 6, 0),  // bleed to card edge
                    Child = img
                };
                Grid.SetColumn(thumbBorder, 0);
                contentRow.Children.Add(thumbBorder);
            }
            catch { }
        }
        else if (isFaction && factionAccent.HasValue)
        {
            // Faction colour strip instead of thumbnail
            var strip = new Border
            {
                Width = 6, CornerRadius = new CornerRadius(5, 0, 0, 5),
                Margin = new Thickness(-8, -7, 6, 0),
                Background = new SolidColorBrush(factionAccent.Value)
            };
            Grid.SetColumn(strip, 0);
            contentRow.Children.Add(strip);
        }

        // Text stack
        var textStack = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
        Grid.SetColumn(textStack, 1);
        contentRow.Children.Add(textStack);

        // Name + type badge row
        var nameText = new TextBlock
        {
            Text = entity.Name, FontSize = 11, FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"), TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 2)
        };
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        textStack.Children.Add(nameText);

        var typeBadgeText = new TextBlock { Text = entity.EntityType, FontSize = 8, FontFamily = new FontFamily("Segoe UI") };
        if (factionAccent.HasValue) typeBadgeText.Foreground = new SolidColorBrush(factionAccent.Value);
        else typeBadgeText.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        textStack.Children.Add(typeBadgeText);

        // Up to 2 schema fields
        var schema = WorldEntitySchemas.For(entity.EntityType);
        int shown = 0;
        foreach (var (field, _) in schema)
        {
            if (shown >= 2) break;
            if (!entity.Fields.TryGetValue(field, out var val) || string.IsNullOrWhiteSpace(val)) continue;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 0) };
            var lbl = new TextBlock { Text = field + ": ", FontSize = 9, FontWeight = FontWeights.SemiBold };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            var vt = new TextBlock { Text = val, FontSize = 9, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = cardW - ThumbW - 28 };
            vt.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
            row.Children.Add(lbl); row.Children.Add(vt);
            textStack.Children.Add(row);
            shown++;
        }

        // ── Resize grip (bottom-right) ──────────────────────────────────────
        var gripRow = new Grid();
        Grid.SetRow(gripRow, 1);
        cardGrid.Children.Add(gripRow);

        var grip = new Border
        {
            Width = 14, Height = 14,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Bottom,
            Cursor    = Cursors.SizeNWSE,
            Background = Brushes.Transparent,
            ToolTip   = "Drag to resize"
        };
        var gripCanvas = new Canvas { Width = 12, Height = 10 };
        for (int i = 2; i <= 10; i += 3)
        {
            var ln = new Line { X1 = i, Y1 = 10, X2 = 10, Y2 = i, StrokeThickness = 1, Opacity = 0.45 };
            ln.SetResourceReference(Line.StrokeProperty, "SidebarDimBrush");
            gripCanvas.Children.Add(ln);
        }
        grip.Child = gripCanvas;
        gripRow.Children.Add(grip);

        // Resize drag state
        bool   isResizing    = false;
        double resizeStartX  = 0, resizeStartY  = 0;
        double resizeStartW  = 0, resizeStartH  = 0;

        grip.MouseLeftButtonDown += (s, e) =>
        {
            isResizing   = true;
            var pt       = e.GetPosition(_boardCanvas);
            resizeStartX = pt.X; resizeStartY = pt.Y;
            resizeStartW = card.ActualWidth;
            resizeStartH = card.ActualHeight;
            grip.CaptureMouse();
            e.Handled = true;
        };
        grip.MouseMove += (s, e) =>
        {
            if (!isResizing) return;
            var pt   = e.GetPosition(_boardCanvas);
            var newW = Math.Max(100, resizeStartW + (pt.X - resizeStartX));
            var newH = Math.Max(60,  resizeStartH + (pt.Y - resizeStartY));
            card.Width  = newW;
            card.Height = newH;
            e.Handled   = true;
        };
        grip.MouseLeftButtonUp += (s, e) =>
        {
            if (!isResizing) return;
            isResizing = false;
            grip.ReleaseMouseCapture();
            var capturedId2 = entity.Id;
            if (_boardData.Positions.TryGetValue(capturedId2, out var bp2))
            {
                bp2.CardWidth  = card.Width;
                bp2.CardHeight = card.Height;
            }
            EntityBoardService.Save(_projFolder, _board.Id, _boardData);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render,
                new Action(RefreshAllBoardLinePositions));
            e.Handled = true;
        };

        // Remove-from-board context menu item
        var ctx = new ContextMenu();
        var removeItem = new MenuItem { Header = "✕ Remove from board" };
        removeItem.Click += (_, _) =>
        {
            _boardData.Positions.Remove(entity.Id);
            // Also remove relations touching this entity
            _boardData.Relations.RemoveAll(r => r.FromId == entity.Id || r.ToId == entity.Id);
            EntityBoardService.Save(_projFolder, _board.Id, _boardData);
            BuildBoardContent();
        };
        ctx.Items.Add(removeItem);
        var editItem = new MenuItem { Header = "✏ Edit" };
        editItem.Click += (_, _) =>
        {
            var copy    = CloneEntity(entity);
            var oldName = entity.Name;
            if (ShowEntityEditDialog(copy, isNew: false))
            {
                if (!string.Equals(copy.Name, oldName, StringComparison.Ordinal))
                    WorldEntityService.Rename(_projFolder, copy, oldName);
                else
                    WorldEntityService.Save(_projFolder, copy);
                BuildBoardContent();
            }
        };
        ctx.Items.Add(editItem);
        card.ContextMenu = ctx;

        return card;
    }

    // ── Entity picker dialog ───────────────────────────────────────────────

    private List<WorldEntity> ShowEntityPickerDialog(List<WorldEntity> eligible, string title)
    {
        var selected = new List<WorldEntity>();
        var win = new Window
        {
            Title = title, Width = 380,
            Height = Math.Min(560, 120 + eligible.Count * 36),
            MinHeight = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this, ShowInTaskbar = false, ResizeMode = ResizeMode.CanResize
        };
        ApplyThemeToDialog(win);
        win.SetResourceReference(Window.BackgroundProperty, "ContentBgBrush");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        win.Content = root;

        // Search box
        var searchBox = new TextBox
        {
            Margin = new Thickness(12, 10, 12, 6), FontSize = 12,
            Padding = new Thickness(6, 4, 6, 4), BorderThickness = new Thickness(1),
            ToolTip = "Filter by name"
        };
        searchBox.SetResourceReference(TextBox.BackgroundProperty,  "ControlBgBrush");
        searchBox.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        searchBox.SetResourceReference(TextBox.ForegroundProperty,  "ContentTextBrush");
        Grid.SetRow(searchBox, 0);
        root.Children.Add(searchBox);

        // Scroll list
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12, 0, 12, 6) };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var checkList = new StackPanel();
        scroll.Content = checkList;

        var checks = new List<(WorldEntity entity, CheckBox cb)>();
        foreach (var e in eligible.OrderBy(x => x.EntityType).ThenBy(x => x.Name))
        {
            var cb = new CheckBox
            {
                Margin = new Thickness(0, 2, 0, 2), Cursor = Cursors.Hand,
                Content = new TextBlock
                {
                    FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            };
            ((TextBlock)cb.Content).Text = $"{e.Name}  [{e.EntityType}]";
            cb.SetResourceReference(CheckBox.ForegroundProperty, "ContentTextBrush");
            checks.Add((e, cb));
            checkList.Children.Add(cb);
        }

        searchBox.TextChanged += (_, _) =>
        {
            var q = searchBox.Text;
            foreach (var (e, cb) in checks)
                cb.Visibility = string.IsNullOrWhiteSpace(q) || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible : Visibility.Collapsed;
        };

        // Button row
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 0, 12, 12)
        };
        Grid.SetRow(btnRow, 2);
        root.Children.Add(btnRow);

        var countLabel = new TextBlock
        {
            FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        countLabel.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        btnRow.Children.Add(countLabel);

        void UpdateCount()
        {
            var n = checks.Count(x => x.cb.IsChecked == true);
            countLabel.Text = n > 0 ? $"{n} selected" : "";
        }
        foreach (var (_, cb) in checks) cb.Checked += (_, _) => UpdateCount();
        foreach (var (_, cb) in checks) cb.Unchecked += (_, _) => UpdateCount();

        var cancelBtn = MakeBtn("Cancel", false); cancelBtn.Padding = new Thickness(12, 5, 12, 5);
        cancelBtn.Click += (_, _) => win.DialogResult = false;
        btnRow.Children.Add(cancelBtn);

        var addBtn = MakeBtn("+ Add to Board", true); addBtn.Padding = new Thickness(12, 5, 12, 5); addBtn.Margin = new Thickness(8, 0, 0, 0);
        addBtn.Click += (_, _) => win.DialogResult = true;
        btnRow.Children.Add(addBtn);

        searchBox.Focus();
        if (win.ShowDialog() == true)
            selected = checks.Where(x => x.cb.IsChecked == true).Select(x => x.entity).ToList();

        return selected;
    }

    // ── Legend ─────────────────────────────────────────────────────────────

    private Border BuildBoardLegend()
    {
        bool visible = _boardData.LegendVisible;
        var panel = new Border
        {
            CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8), MinWidth = 140, MaxWidth = 220,
            Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.18, Color = Colors.Black }
        };
        panel.SetResourceReference(Border.BackgroundProperty,  "ControlBgBrush");
        panel.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");

        var content = new StackPanel();
        panel.Child = content;

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(headerRow);

        var title = new TextBlock { Text = "Legend", FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        headerRow.Children.Add(title);

        var toggleBtn = new Button
        {
            Content = visible ? "▲" : "▼", FontSize = 9,
            Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(6, 0, 0, 0),
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, Cursor = Cursors.Hand
        };
        toggleBtn.SetResourceReference(Button.ForegroundProperty, "SidebarDimBrush");
        headerRow.Children.Add(toggleBtn);

        var entriesPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 0), Visibility = visible ? Visibility.Visible : Visibility.Collapsed };
        content.Children.Add(entriesPanel);

        var entries = _boardData.Relations
            .Where(r => !string.IsNullOrWhiteSpace(r.LegendLabel))
            .GroupBy(r => (r.LegendLabel, r.LineStyle, r.LineColor, r.Thickness))
            .Select(g => g.First())
            .ToList();

        if (entries.Count == 0)
        {
            var hint = new TextBlock { Text = "Right-click a relation\nto add a legend label.", FontSize = 10, TextWrapping = TextWrapping.Wrap };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            entriesPanel.Children.Add(hint);
        }
        else
        {
            foreach (var rel in entries)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                entriesPanel.Children.Add(row);

                var prev = new Canvas { Width = 40, Height = 12, Margin = new Thickness(0, 0, 6, 0) };
                void AddPreviewLine(double yOff)
                {
                    var ln = new Line { X1 = 0, Y1 = yOff, X2 = 40, Y2 = yOff,
                        StrokeThickness = Math.Min(rel.Thickness, 2.5), Opacity = 0.9 };
                    var da = GetDashArray(rel.LineStyle);
                    if (da is not null) { ln.StrokeDashArray = da; if (rel.LineStyle is BoardLineStyle.Dotted or BoardLineStyle.DoubleDotted) ln.StrokeDashCap = PenLineCap.Round; }
                    ln.SetResourceReference(Line.StrokeProperty, rel.LineColor);
                    prev.Children.Add(ln);
                }
                AddPreviewLine(IsDoubleLine(rel.LineStyle) ? 4 : 6);
                if (IsDoubleLine(rel.LineStyle)) AddPreviewLine(9);
                row.Children.Add(prev);

                var lbl = new TextBlock
                {
                    Text = rel.LegendLabel, FontSize = 10,
                    TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 130,
                    VerticalAlignment = VerticalAlignment.Center
                };
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
                row.Children.Add(lbl);
            }
        }

        toggleBtn.Click += (_, _) =>
        {
            _boardData.LegendVisible = !_boardData.LegendVisible;
            EntityBoardService.Save(_projFolder, _board.Id, _boardData);
            BuildBoardContent();
        };

        return panel;
    }

    // ── Relation rendering ─────────────────────────────────────────────────

    private void RenderRelationVisuals(Canvas canvas, string relId, double x1, double y1, double x2, double y2, BoardRelation rel)
    {
        bool       isDouble  = IsDoubleLine(rel.LineStyle);
        var        dashArray = GetDashArray(rel.LineStyle);
        double     thickness = Math.Max(1, rel.Thickness);
        PenLineCap cap       = rel.LineStyle is BoardLineStyle.Dotted or BoardLineStyle.DoubleDotted
                               ? PenLineCap.Round : PenLineCap.Flat;

        double px = 0, py = 0;
        if (isDouble)
        {
            var dx = x2 - x1; var dy = y2 - y1;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len > 0) { double off = thickness + 1.5; px = -dy / len * off; py = dx / len * off; }
        }

        Line MakeLine(double ox, double oy)
        {
            var ln = new Line
            {
                X1 = x1 + ox, Y1 = y1 + oy, X2 = x2 + ox, Y2 = y2 + oy,
                StrokeThickness = thickness, Opacity = 0.85,
                StrokeStartLineCap = cap, StrokeEndLineCap = cap
            };
            if (dashArray is not null) { ln.StrokeDashArray = dashArray; ln.StrokeDashCap = cap; }
            ln.SetResourceReference(Line.StrokeProperty, rel.LineColor);
            Panel.SetZIndex(ln, 0);
            canvas.Children.Add(ln);
            return ln;
        }

        var l1 = MakeLine( px,  py);
        var l2 = isDouble ? MakeLine(-px, -py) : null;
        _boardLines[relId] = (l1, l2);
    }

    private void UpdateBoardLines(string entityId, double newX, double newY)
    {
        double hw = 100, hh = 45;
        if (_boardCards.TryGetValue(entityId, out var movedCard) && movedCard.ActualWidth > 0)
        {
            hw = movedCard.ActualWidth  / 2;
            hh = movedCard.ActualHeight / 2;
        }
        var cx = newX + hw; var cy = newY + hh;

        foreach (var rel in _boardData.Relations)
        {
            bool isFrom = rel.FromId == entityId;
            bool isTo   = rel.ToId   == entityId;
            if (!isFrom && !isTo) continue;
            if (!_boardLines.TryGetValue(rel.Id, out var lines)) continue;

            var otherId  = isFrom ? rel.ToId : rel.FromId;
            var (ox, oy) = GetBoardCardCenter(otherId);
            double x1 = isFrom ? cx : ox, y1 = isFrom ? cy : oy;
            double x2 = isFrom ? ox : cx, y2 = isFrom ? oy : cy;
            ApplyLineGeometry(lines.L1, lines.L2, rel.LineStyle, rel.Thickness, x1, y1, x2, y2);

            if (_boardCaptionBadges.TryGetValue(rel.Id, out var badge))
            {
                Canvas.SetLeft(badge, (x1 + x2) / 2 - 30);
                Canvas.SetTop (badge, (y1 + y2) / 2 - 12);
            }
        }
    }

    private void RefreshAllBoardLinePositions()
    {
        foreach (var rel in _boardData.Relations)
        {
            if (!_boardLines.TryGetValue(rel.Id, out var lines)) continue;
            var (x1, y1) = GetBoardCardCenter(rel.FromId);
            var (x2, y2) = GetBoardCardCenter(rel.ToId);
            ApplyLineGeometry(lines.L1, lines.L2, rel.LineStyle, rel.Thickness, x1, y1, x2, y2);
            if (_boardCaptionBadges.TryGetValue(rel.Id, out var badge))
            {
                Canvas.SetLeft(badge, (x1 + x2) / 2 - 30);
                Canvas.SetTop (badge, (y1 + y2) / 2 - 12);
            }
        }
    }

    private (double cx, double cy) GetBoardCardCenter(string entityId)
    {
        if (!_boardCards.TryGetValue(entityId, out var card)) return (100, 100);
        var x = Canvas.GetLeft(card); var y = Canvas.GetTop(card);
        var w = card.ActualWidth  > 0 ? card.ActualWidth  : 200;
        var h = card.ActualHeight > 0 ? card.ActualHeight : 90;
        return (x + w / 2, y + h / 2);
    }

    private static void ApplyLineGeometry(Line l1, Line? l2, BoardLineStyle style, double thickness,
        double x1, double y1, double x2, double y2)
    {
        double px = 0, py = 0;
        if (IsDoubleLine(style))
        {
            var dx = x2 - x1; var dy = y2 - y1;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len > 0) { double off = Math.Max(1, thickness) + 1.5; px = -dy / len * off; py = dx / len * off; }
        }
        l1.X1 = x1 + px; l1.Y1 = y1 + py; l1.X2 = x2 + px; l1.Y2 = y2 + py;
        if (l2 is not null) { l2.X1 = x1 - px; l2.Y1 = y1 - py; l2.X2 = x2 - px; l2.Y2 = y2 - py; }
    }

    private static bool IsDoubleLine(BoardLineStyle s) =>
        s is BoardLineStyle.DoubleSolid or BoardLineStyle.DoubleDotted
          or BoardLineStyle.DoubleDashed or BoardLineStyle.DoubleDotDash;

    private static DoubleCollection? GetDashArray(BoardLineStyle style) => style switch
    {
        BoardLineStyle.Dotted        => new DoubleCollection { 1, 3 },
        BoardLineStyle.Dashed        => new DoubleCollection { 5, 3 },
        BoardLineStyle.DotDash       => new DoubleCollection { 1, 3, 5, 3 },
        BoardLineStyle.DoubleDotted  => new DoubleCollection { 1, 3 },
        BoardLineStyle.DoubleDashed  => new DoubleCollection { 5, 3 },
        BoardLineStyle.DoubleDotDash => new DoubleCollection { 1, 3, 5, 3 },
        _                            => null
    };

    // ── Connect mode ───────────────────────────────────────────────────────

    private void HandleBoardConnectClick(string entityId)
    {
        if (_boardConnSrcId == null)
        {
            _boardConnSrcId = entityId;
            BuildBoardContent();
        }
        else if (_boardConnSrcId == entityId)
        {
            _boardConnSrcId   = null;
            _boardConnectMode = false;
            BuildBoardContent();
        }
        else
        {
            var result = ShowRelationDialog(title: "New Relation");
            if (result is not null)
            {
                _boardData.Relations.Add(new BoardRelation
                {
                    FromId      = _boardConnSrcId,
                    ToId        = entityId,
                    Caption     = result.Caption,
                    LegendLabel = result.LegendLabel,
                    LineStyle   = result.LineStyle,
                    LineColor   = result.LineColor,
                    Thickness   = result.Thickness
                });
                EntityBoardService.Save(_projFolder, _board.Id, _boardData);
            }
            _boardConnSrcId   = null;
            _boardConnectMode = false;
            BuildBoardContent();
        }
    }

    // ── Auto-arrange ───────────────────────────────────────────────────────

    private void AutoArrangeBoard()
    {
        const int Cols = 4, CardW = 200, CardH = 120, PadX = 60, PadY = 50;
        int row = 0;
        foreach (var et in _board.EntityTypes)
        {
            var typeEntities = WorldEntityService.List(_projFolder, et);
            int col = 0;
            foreach (var e in typeEntities)
            {
                _boardData.Positions[e.Id] = new BoardPosition
                {
                    X = 40 + col * (CardW + PadX),
                    Y = 40 + row * (CardH + PadY)
                };
                if (++col >= Cols) { col = 0; row++; }
            }
            if (typeEntities.Count > 0) row++;
        }
        EntityBoardService.Save(_projFolder, _board.Id, _boardData);
        BuildBoardContent();
    }


    // ── Entity edit dialog ─────────────────────────────────────────────────

    private bool ShowEntityEditDialog(WorldEntity entity, bool isNew) =>
        WorldEntityEditDialog.Show(entity, _projFolder, isNew, _themePath, this, ref _entityEditOpen);


    // ── Entity read-only dialog ────────────────────────────────────────────

    private void ShowEntityReadOnlyDialog(WorldEntity entity)
    {
        var schema = WorldEntitySchemas.For(entity.EntityType);
        var win = new Window
        {
            Title = $"{entity.EntityType}: {entity.Name}", Width = 460, Height = 500,
            MinWidth = 340, MinHeight = 280, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this, ShowInTaskbar = false, ResizeMode = ResizeMode.CanResize
        };
        ApplyThemeToDialog(win);
        win.SetResourceReference(Window.BackgroundProperty, "ContentBgBrush");

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(24, 20, 24, 20) };
        win.Content = scroll;
        var root = new StackPanel();
        scroll.Content = root;

        var nameBlock = new TextBlock { Text = entity.Name, FontSize = 18, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Segoe UI"), TextWrapping = TextWrapping.Wrap };
        nameBlock.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        root.Children.Add(nameBlock);

        var typeLbl = new TextBlock { Text = entity.EntityType, FontSize = 11, FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 2, 0, 14) };
        typeLbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        root.Children.Add(typeLbl);

        foreach (var (field, _) in schema)
        {
            if (!entity.Fields.TryGetValue(field, out var val) || string.IsNullOrWhiteSpace(val)) continue;
            var lbl = new TextBlock { Text = field, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            var vt = new TextBlock { Text = val, FontSize = 13, TextWrapping = TextWrapping.Wrap };
            vt.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
            root.Children.Add(lbl);
            root.Children.Add(vt);
        }

        if (!string.IsNullOrWhiteSpace(entity.Notes))
        {
            var nl = new TextBlock { Text = "Notes", FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) };
            nl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            var nv = new TextBlock { Text = entity.Notes, FontSize = 13, TextWrapping = TextWrapping.Wrap };
            nv.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
            root.Children.Add(nl);
            root.Children.Add(nv);
        }

        win.Show();
    }

    // ── Relation dialog ────────────────────────────────────────────────────

    private record RelationDialogResult(string Caption, string LegendLabel, BoardLineStyle LineStyle, string LineColor, double Thickness);

    private RelationDialogResult? ShowRelationDialog(
        string captionInit = "", string legendInit = "",
        BoardLineStyle styleInit = BoardLineStyle.Solid,
        string colorInit = "AccentHighlightBrush", double thicknessInit = 1.5,
        string title = "New Relation")
    {
        var win = new Window
        {
            Title = title, Width = 440, Height = 490, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this, ShowInTaskbar = false
        };
        ApplyThemeToDialog(win);
        win.SetResourceReference(Window.BackgroundProperty, "ContentBgBrush");

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(20, 16, 20, 16) };
        win.Content = scroll;
        var root = new StackPanel();
        scroll.Content = root;

        void AddLabel(string text)
        {
            var lbl = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            root.Children.Add(lbl);
        }
        TextBox MakeTextBox(string value, int maxLen = 200)
        {
            var tb = new TextBox { Text = value, FontSize = 13, MaxLength = maxLen, Padding = new Thickness(8, 5, 8, 5), BorderThickness = new Thickness(1) };
            tb.SetResourceReference(TextBox.BackgroundProperty,  "ControlBgBrush");
            tb.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
            tb.SetResourceReference(TextBox.ForegroundProperty,  "ContentTextBrush");
            root.Children.Add(tb);
            return tb;
        }

        AddLabel("Caption (shown on line):");
        var captionBox = MakeTextBox(captionInit);
        AddLabel("Legend label (max 20 chars):");
        var legendBox = MakeTextBox(legendInit, maxLen: 20);

        AddLabel("Line style:");
        var styleItems = new (string Display, BoardLineStyle Value)[]
        {
            ("━━━━━  Solid",         BoardLineStyle.Solid),
            ("· · · · ·  Dotted",   BoardLineStyle.Dotted),
            ("─ ─ ─ ─  Dashed",     BoardLineStyle.Dashed),
            ("·─·─·  Dot-dash",      BoardLineStyle.DotDash),
            ("══════  Double solid", BoardLineStyle.DoubleSolid),
            ("⁚ ⁚ ⁚  Double dotted",BoardLineStyle.DoubleDotted),
            ("═ ═ ═  Double dashed", BoardLineStyle.DoubleDashed),
            ("·═·═  Double dot-dash",BoardLineStyle.DoubleDotDash)
        };
        var styleCombo = new ComboBox { FontSize = 12, Padding = new Thickness(8, 4, 8, 4) };
        int styleInitIdx = 0;
        for (int i = 0; i < styleItems.Length; i++) { styleCombo.Items.Add(styleItems[i].Display); if (styleItems[i].Value == styleInit) styleInitIdx = i; }
        styleCombo.SelectedIndex = styleInitIdx;
        root.Children.Add(styleCombo);

        AddLabel("Line colour:");
        var colorOptions = new (string Key, string Label)[]
        {
            ("AccentHighlightBrush", "Accent 1"), ("PrimaryAccentBrush", "Accent 2"),
            ("SecondaryAccentBrush", "Accent 3"), ("AccentBgBrush",       "Accent 4")
        };
        var swatchRow = new WrapPanel { Orientation = Orientation.Horizontal };
        root.Children.Add(swatchRow);
        string selectedColor = colorInit;
        var swatches = new List<Border>();
        foreach (var (key, label) in colorOptions)
        {
            var capturedKey = key;
            bool isSel = key == colorInit;
            var swatch = new Border { Width = 80, Height = 26, Margin = new Thickness(0, 0, 6, 0), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(isSel ? 2 : 1), Cursor = Cursors.Hand, Tag = key };
            swatch.SetResourceReference(Border.BackgroundProperty,  key);
            swatch.SetResourceReference(Border.BorderBrushProperty, isSel ? "ContentTextBrush" : "ControlBorderBrush");
            var slbl = new TextBlock { Text = label, FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            slbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentBgBrush");
            swatch.Child = slbl;
            swatch.MouseLeftButtonDown += (_, _) =>
            {
                selectedColor = capturedKey;
                foreach (var sw in swatches)
                {
                    bool sel = sw.Tag?.ToString() == capturedKey;
                    sw.BorderThickness = new Thickness(sel ? 2 : 1);
                    sw.SetResourceReference(Border.BorderBrushProperty, sel ? "ContentTextBrush" : "ControlBorderBrush");
                }
            };
            swatches.Add(swatch);
            swatchRow.Children.Add(swatch);
        }

        AddLabel("Thickness (1-10 px):");
        var thickRow = new StackPanel { Orientation = Orientation.Horizontal };
        root.Children.Add(thickRow);
        var thickSlider = new Slider { Minimum = 1, Maximum = 10, Value = thicknessInit, Width = 200, VerticalAlignment = VerticalAlignment.Center, IsSnapToTickEnabled = true, TickFrequency = 0.5 };
        var thickLbl    = new TextBlock { Text = $"{thicknessInit:F1} px", FontSize = 12, Width = 44, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        thickLbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        thickSlider.ValueChanged += (_, e) => thickLbl.Text = $"{e.NewValue:F1} px";
        thickRow.Children.Add(thickSlider);
        thickRow.Children.Add(thickLbl);

        var dialogBtnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        root.Children.Add(dialogBtnRow);
        var cancelBtn = MakeBtn("Cancel", false); cancelBtn.Padding = new Thickness(12, 6, 12, 6); cancelBtn.Click += (_, _) => win.DialogResult = false;
        var okBtn     = MakeBtn("OK",     true);  okBtn.Padding = new Thickness(12, 6, 12, 6); okBtn.Margin = new Thickness(8, 0, 0, 0); okBtn.Click += (_, _) => win.DialogResult = true;
        dialogBtnRow.Children.Add(cancelBtn); dialogBtnRow.Children.Add(okBtn);
        captionBox.Focus(); captionBox.SelectAll();
        captionBox.PreviewKeyDown += (_, e) => { if (e.Key == Key.Return) win.DialogResult = true; };

        if (win.ShowDialog() != true) return null;
        var chosenStyle = styleItems[Math.Max(0, Math.Min(styleCombo.SelectedIndex, styleItems.Length - 1))].Value;
        return new RelationDialogResult(captionBox.Text.Trim(), legendBox.Text.Trim(), chosenStyle, selectedColor, thickSlider.Value);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private void ApplyThemeToDialog(Window win)
    {
        if (!string.IsNullOrWhiteSpace(_themePath))
        {
            try
            {
                var dict = OxsuitLoader.Load(_themePath);
                if (dict is not null) win.Resources.MergedDictionaries.Add(dict);
            }
            catch { }
        }
        win.SourceInitialized += (_, _) => ParticipantsWindow.TryApplyTitleBarTo(win);
    }

    private Button MakeBtn(string label, bool isPrimary)
    {
        var btn = new Button
        {
            Content = label, FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
            Padding = new Thickness(9, 4, 9, 4), BorderThickness = new Thickness(1), Cursor = Cursors.Hand
        };
        btn.SetResourceReference(Button.BackgroundProperty,   isPrimary ? "ControlHoverBrush"    : "ControlBgBrush");
        btn.SetResourceReference(Button.ForegroundProperty,   isPrimary ? "AccentHighlightBrush" : "SidebarTextBrush");
        btn.SetResourceReference(Button.BorderBrushProperty,  "ControlBorderBrush");
        btn.MouseEnter += (_, _) => btn.Opacity = 0.80;
        btn.MouseLeave += (_, _) => btn.Opacity = 1.00;
        return btn;
    }

    private static WorldEntity CloneEntity(WorldEntity src) => new()
    {
        Id = src.Id, Name = src.Name, EntityType = src.EntityType,
        CreatedAt = src.CreatedAt, UpdatedAt = src.UpdatedAt,
        Fields = new Dictionary<string, string>(src.Fields),
        Notes = src.Notes, FactionColor = src.FactionColor, MemberIds = [..src.MemberIds],
        PortraitFileName = src.PortraitFileName, ImageFileName = src.ImageFileName
    };
}
