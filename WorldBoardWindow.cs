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
    private ScrollViewer?      _boardScroll;
    private bool               _boardConnectMode = false;
    private string?            _boardConnSrcId   = null;
    private bool               _entityEditOpen   = false;

    private readonly Dictionary<string, Border>                    _boardCards         = new();
    /// <summary>Segments per relation: one entry per (from→wp0→…→to) segment pair.</summary>
    private readonly Dictionary<string, List<(Line L1, Line? L2)>> _boardLines        = new();
    private readonly Dictionary<string, Border>                    _boardCaptionBadges = new();
    private readonly Dictionary<string, Polygon>                   _boardArrows        = new();
    /// <summary>Waypoint bubble ellipses per relation, in waypoint order.</summary>
    private readonly Dictionary<string, List<Ellipse>>             _boardWpBubbles     = new();
    /// <summary>Rendered text-box borders, keyed by BoardTextBox.Id.</summary>
    private readonly Dictionary<string, Border>                    _boardTextBoxes     = new();
    /// <summary>Rendered board-pin borders, keyed by pinned board ID.</summary>
    private readonly Dictionary<string, Border>                    _boardPins          = new();
    /// <summary>When non-null, next card-click creates a relation branching from this waypoint.</summary>
    private (string RelId, string WpId)? _connFromWaypoint = null;
    /// <summary>Relation ID currently under the mouse cursor (for hover highlight).</summary>
    private string? _hoverRelId = null;
    /// <summary>Wide transparent hit-zone lines per relation for easier hovering on thin lines.</summary>
    private readonly Dictionary<string, List<Line>> _boardHitZones = new();

    // ── Selection state ────────────────────────────────────────────────────
    private readonly HashSet<string> _selectedIds = new();
    // Rubber-band selection rectangle drawn on right-mouse drag
    private System.Windows.Shapes.Rectangle? _selectionRect = null;
    private Point  _selDragStart;
    private bool   _selDragging = false;

    // Filter state
    private string?  _filterName               = null;
    private string?  _filterFaction            = null;
    private string?  _filterArc                = null;
    private string?  _filterAlignment          = null;
    private string?  _filterCharacter          = null;   // Lore: character who knows it
    private bool     _filterCommonKnowledge    = false;
    private bool     _filterHistoricalKnowledge = false;
    private string   _sortMode                 = "name_asc";
    private bool     _filtersVisible           = false;

    // Board zoom (applied as LayoutTransform on the canvas)
    private double   _boardZoom       = 1.0;

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

                // Alignment filter — Lore entities have no Alignment field, skip them
                bool isLoreE = string.Equals(e.EntityType, "Lore", StringComparison.OrdinalIgnoreCase);
                if (_filterAlignment != null && !isLoreE)
                {
                    var alignVal = e.Fields.TryGetValue("Alignment", out var al) ? al : null;
                    bool match = (_filterAlignment == "<<none>>" && string.IsNullOrWhiteSpace(alignVal)) ||
                                 (!string.IsNullOrWhiteSpace(alignVal) && alignVal == _filterAlignment);
                    if (!match) return false;
                }

                // Lore: character who knows it
                if (_filterCharacter != null && isLoreE)
                    if (!e.MemberIds.Contains(_filterCharacter)) return false;

                // Lore: common / historical knowledge tags
                if (_filterCommonKnowledge && isLoreE)
                    if (!e.Fields.TryGetValue("CommonKnowledge", out var ck) || ck != "true") return false;
                if (_filterHistoricalKnowledge && isLoreE)
                    if (!e.Fields.TryGetValue("HistoricalKnowledge", out var hk) || hk != "true") return false;

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

            bool isLoreEntry = string.Equals(e.EntityType, "Lore", StringComparison.OrdinalIgnoreCase);
            if (!isLoreEntry)
            {
                if (e.Fields.TryGetValue("Alignment", out var al) && !string.IsNullOrWhiteSpace(al))
                    alignments.Add(al);
                else
                    hasMissingAlignment = true;
            }
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

        // Toolbar layout:
        //   topWrap (WrapPanel) — action buttons + filter group wrap together
        //   hintRow (StackPanel) — hint text + snap controls, always on its own line
        var toolWrap = new StackPanel { Orientation = Orientation.Vertical };
        toolBorder.Child = toolWrap;

        var topWrap = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Left };
        toolWrap.Children.Add(topWrap);

        // ── Small vertical separator helper ────────────────────────────────
        UIElement MakeTSep() {
            var s = new Border { Width = 1, Margin = new Thickness(6, 3, 6, 3), Background = Brushes.Transparent };
            s.SetResourceReference(Border.BackgroundProperty, "ControlBorderBrush");
            // Make it visible without a hard color dependency
            s.Opacity = 0.4;
            return s;
        }

        var toolPanel = new StackPanel { Orientation = Orientation.Horizontal };
        topWrap.Children.Add(toolPanel);

        var connectBtn = MakeBtn(_boardConnectMode ? Properties.Loc.S("Board_Connecting") : Properties.Loc.S("Board_AddRelation"), _boardConnectMode);
        connectBtn.Click += (_, _) => { _boardConnectMode = !_boardConnectMode; _boardConnSrcId = null; BuildBoardContent(); };
        toolPanel.Children.Add(connectBtn);

        // ── "➕ Add" dropdown — replaces individual entity/text/board buttons ─
        var addDropBtn = MakeBtn(Properties.Loc.S("Board_AddDropdown"), false);
        addDropBtn.Margin  = new Thickness(6, 0, 0, 0);
        addDropBtn.ToolTip = "Add entities, text or board pins";
        addDropBtn.Click += (_, _) =>
        {
            double usedY = _boardData.Positions.Values.Select(p => p.Y).DefaultIfEmpty(0).Max();
            var menu = BuildAddContextMenu(Snap(40), Snap(usedY + 200));
            menu.PlacementTarget = addDropBtn;
            menu.Placement       = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen          = true;
        };
        toolPanel.Children.Add(addDropBtn);
        toolPanel.Children.Add(MakeTSep());

        var arrangeBtn = MakeBtn(Properties.Loc.S("Board_AutoArrange"), false);
        arrangeBtn.Click += (_, _) =>
        {
            if (_selectedIds.Count > 0)
                AutoArrangeBoard(_selectedIds);
            else
            {
                var r = MessageBox.Show(
                    Properties.Loc.S("Board_AutoArrangeConfirm"),
                    Properties.Loc.S("Board_AutoArrangeTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r == MessageBoxResult.Yes) AutoArrangeBoard(null);
            }
        };
        toolPanel.Children.Add(arrangeBtn);

        // ── Name search — always visible in toolbar ───────────────────────
        toolPanel.Children.Add(MakeTSep());
        var searchBorder = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center
        };
        searchBorder.SetResourceReference(Border.BorderBrushProperty, "SidebarDimBrush");
        searchBorder.SetResourceReference(Border.BackgroundProperty,   "ControlBgBrush");
        var nameSearchBox = new TextBox
        {
            Text = _filterName ?? "", ToolTip = "Filter by name",
            Width = 130, FontSize = 11, Padding = new Thickness(5, 3, 5, 3),
            BorderThickness = new Thickness(0), Background = Brushes.Transparent
        };
        nameSearchBox.SetResourceReference(TextBox.ForegroundProperty, "ContentTextBrush");
        nameSearchBox.TextChanged += (_, _) =>
        {
            _filterName = string.IsNullOrWhiteSpace(nameSearchBox.Text) ? null : nameSearchBox.Text;
            BuildBoardContent();
        };
        searchBorder.Child = nameSearchBox;
        toolPanel.Children.Add(searchBorder);

        // ── Filters toggle button ──────────────────────────────────────────
        toolPanel.Children.Add(MakeTSep());
        var filterToggle = MakeBtn(_filtersVisible ? Properties.Loc.S("Board_FiltersHide") : Properties.Loc.S("Board_FiltersShow"), _filtersVisible);
        filterToggle.ToolTip = "Show / hide filter controls";
        filterToggle.Click += (_, _) => { _filtersVisible = !_filtersVisible; BuildBoardContent(); };
        toolPanel.Children.Add(filterToggle);

        // Load all entities for filter options extraction
        var allEntities = _board.EntityTypes
            .SelectMany(et => WorldEntityService.List(_projFolder, et))
            .ToList();

        var (factionOptions, arcOptions, alignmentOptions) = ExtractFilterOptions(allEntities);

        // Lore-specific: characters referenced in Lore MemberIds
        bool boardHasLore = _board.EntityTypes.Any(et => string.Equals(et, "Lore", StringComparison.OrdinalIgnoreCase));
        List<WorldEntity> loreCharacters = [];
        if (boardHasLore)
        {
            var charIds = allEntities
                .Where(e => string.Equals(e.EntityType, "Lore", StringComparison.OrdinalIgnoreCase))
                .SelectMany(e => e.MemberIds)
                .Distinct().ToHashSet();
            loreCharacters = WorldEntityService.List(_projFolder, "Character")
                .Where(c => charIds.Contains(c.Id)).ToList();
        }

        // ── Filter group: collapsible, wraps naturally ─────────────────────
        var filterPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 2),
            Visibility = _filtersVisible ? Visibility.Visible : Visibility.Collapsed
        };
        topWrap.Children.Add(filterPanel);

        // Faction dropdown
        if (factionOptions.Count > 0)
        {
            var factionCombo = new ComboBox { Width = 140, FontSize = 11, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(8, 0, 0, 0) };
            factionCombo.Items.Add(Properties.Loc.S("World_AllFactions"));
            foreach (var f in factionOptions)
                factionCombo.Items.Add(f == "<<none>>" ? "(no faction)" : f);
            factionCombo.SelectedIndex = 0;
            factionCombo.SelectionChanged += (_, _) =>
            {
                int idx = factionCombo.SelectedIndex;
                _filterFaction = idx <= 0 ? null : (factionOptions[idx - 1] == "<<none>>" ? "<<none>>" : factionOptions[idx - 1]);
                BuildBoardContent();
            };
            filterPanel.Children.Add(factionCombo);
        }

        // Arc dropdown
        if (arcOptions.Count > 0)
        {
            var arcCombo = new ComboBox { Width = 140, FontSize = 11, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(8, 0, 0, 0) };
            arcCombo.Items.Add(Properties.Loc.S("World_AllArcs"));
            foreach (var a in arcOptions)
                arcCombo.Items.Add(a == "<<none>>" ? "(no arc)" : a);
            arcCombo.SelectedIndex = 0;
            arcCombo.SelectionChanged += (_, _) =>
            {
                int idx = arcCombo.SelectedIndex;
                _filterArc = idx <= 0 ? null : (arcOptions[idx - 1] == "<<none>>" ? "<<none>>" : arcOptions[idx - 1]);
                BuildBoardContent();
            };
            filterPanel.Children.Add(arcCombo);
        }

        // Alignment dropdown
        if (alignmentOptions.Count > 0)
        {
            var alignCombo = new ComboBox { Width = 140, FontSize = 11, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(8, 0, 0, 0) };
            alignCombo.Items.Add(Properties.Loc.S("World_AllAlignments"));
            foreach (var al in alignmentOptions)
                alignCombo.Items.Add(al == "<<none>>" ? "(no alignment)" : al);
            alignCombo.SelectedIndex = 0;
            alignCombo.SelectionChanged += (_, _) =>
            {
                int idx = alignCombo.SelectedIndex;
                _filterAlignment = idx <= 0 ? null : (alignmentOptions[idx - 1] == "<<none>>" ? "<<none>>" : alignmentOptions[idx - 1]);
                BuildBoardContent();
            };
            filterPanel.Children.Add(alignCombo);
        }

        // Lore: Character filter
        if (boardHasLore && loreCharacters.Count > 0)
        {
            var charCombo = new ComboBox { Width = 150, FontSize = 11, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(8, 2, 0, 2) };
            charCombo.Items.Add(Properties.Loc.S("Board_AllCharacters"));
            foreach (var ch in loreCharacters) charCombo.Items.Add(ch.Name);
            int selCharIdx = _filterCharacter == null ? 0 :
                loreCharacters.FindIndex(c => c.Id == _filterCharacter) + 1;
            charCombo.SelectedIndex = Math.Max(0, selCharIdx);
            charCombo.SelectionChanged += (_, _) =>
            {
                int idx = charCombo.SelectedIndex;
                _filterCharacter = idx <= 0 ? null : loreCharacters[idx - 1].Id;
                BuildBoardContent();
            };
            filterPanel.Children.Add(charCombo);
        }

        // Lore: CommonKnowledge / HistoricalKnowledge checkboxes
        if (boardHasLore)
        {
            var ckBox = new CheckBox
            {
                Content = Properties.Loc.S("World_CommonKnowledge"), IsChecked = _filterCommonKnowledge,
                FontSize = 11, Margin = new Thickness(10, 2, 0, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            ckBox.SetResourceReference(CheckBox.ForegroundProperty, "ContentTextBrush");
            ckBox.Checked   += (_, _) => { _filterCommonKnowledge = true;  BuildBoardContent(); };
            ckBox.Unchecked += (_, _) => { _filterCommonKnowledge = false; BuildBoardContent(); };
            filterPanel.Children.Add(ckBox);

            var hkBox = new CheckBox
            {
                Content = Properties.Loc.S("World_HistoricalKnowledge"), IsChecked = _filterHistoricalKnowledge,
                FontSize = 11, Margin = new Thickness(10, 2, 0, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            hkBox.SetResourceReference(CheckBox.ForegroundProperty, "ContentTextBrush");
            hkBox.Checked   += (_, _) => { _filterHistoricalKnowledge = true;  BuildBoardContent(); };
            hkBox.Unchecked += (_, _) => { _filterHistoricalKnowledge = false; BuildBoardContent(); };
            filterPanel.Children.Add(hkBox);
        }

        var resetBtn = MakeBtn(Properties.Loc.S("World_Reset"), false);
        resetBtn.FontSize = 10; resetBtn.Padding = new Thickness(6, 3, 6, 3);
        resetBtn.Margin = new Thickness(10, 2, 0, 2);
        resetBtn.Click += (_, _) =>
        {
            _filterName = null;
            _filterFaction = null;
            _filterArc = null;
            _filterAlignment = null;
            _filterCharacter = null;
            _filterCommonKnowledge = false;
            _filterHistoricalKnowledge = false;
            _sortMode = "name_asc";
            nameSearchBox.Text = "";
            BuildBoardContent();
        };
        filterPanel.Children.Add(resetBtn);

        // ── Hint + snap row — always on its own line beneath action/filter rows ──
        var hintRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 0)
        };
        toolWrap.Children.Add(hintRow);

        var hint = _boardConnectMode
            ? (_boardConnSrcId == null ? Properties.Loc.S("Board_ConnectHint1") : Properties.Loc.S("Board_ConnectHint2"))
            : Properties.Loc.S("Board_DragHint");
        var hintBlock = new TextBlock
        {
            Text = hint, FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center
        };
        hintBlock.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        hintRow.Children.Add(hintBlock);

        // ── Grid / snap controls ───────────────────────────────────────────
        var snapSep = new Separator { Style = null, Width = 1, Margin = new Thickness(14, 4, 14, 4) };
        snapSep.SetResourceReference(Separator.BackgroundProperty, "ControlBorderBrush");
        hintRow.Children.Add(snapSep);

        var snapCheck = new CheckBox
        {
            Content = Properties.Loc.S("Board_SnapToGrid"), IsChecked = _boardData.SnapToGrid,
            VerticalAlignment = VerticalAlignment.Center, FontSize = 11
        };
        snapCheck.SetResourceReference(CheckBox.ForegroundProperty, "ContentTextBrush");
        snapCheck.Checked   += (_, _) => { _boardData.SnapToGrid = true;  EntityBoardService.Save(_projFolder, _board.Id, _boardData); BuildBoardContent(); };
        snapCheck.Unchecked += (_, _) => { _boardData.SnapToGrid = false; EntityBoardService.Save(_projFolder, _board.Id, _boardData); BuildBoardContent(); };
        hintRow.Children.Add(snapCheck);

        var gridBtn = MakeBtn(Properties.Loc.S("Board_GridBtn"), false);
        gridBtn.FontSize = 10; gridBtn.Padding = new Thickness(8, 2, 8, 2);
        gridBtn.Margin   = new Thickness(8, 0, 0, 0);
        gridBtn.ToolTip  = "Grid settings";
        gridBtn.Click += (_, _) =>
        {
            ShowGridSettingsDialog();
        };
        hintRow.Children.Add(gridBtn);

        // ── Zoom controls ────────────────────────────────────────────────────
        var zoomSep = new Separator { Style = null, Width = 1, Margin = new Thickness(14, 4, 14, 4) };
        zoomSep.SetResourceReference(Separator.BackgroundProperty, "ControlBorderBrush");
        hintRow.Children.Add(zoomSep);

        var zoomOutBtn = MakeBtn("🔍−", false);
        zoomOutBtn.FontSize = 10; zoomOutBtn.Padding = new Thickness(6, 2, 6, 2);
        zoomOutBtn.ToolTip = "Zoom out (Ctrl+-)";
        zoomOutBtn.Click += (_, _) => { _boardZoom = Math.Max(0.25, Math.Round(_boardZoom - 0.1, 2)); BuildBoardContent(); };
        hintRow.Children.Add(zoomOutBtn);

        var zoomLabel = new TextBlock
        {
            Text = $"{(int)Math.Round(_boardZoom * 100)}%",
            FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0), MinWidth = 36, TextAlignment = TextAlignment.Center,
            Cursor = Cursors.Hand, ToolTip = "Click to reset zoom to 100%"
        };
        zoomLabel.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        zoomLabel.MouseLeftButtonDown += (_, _) => { _boardZoom = 1.0; BuildBoardContent(); };
        hintRow.Children.Add(zoomLabel);

        var zoomInBtn = MakeBtn("🔍+", false);
        zoomInBtn.FontSize = 10; zoomInBtn.Padding = new Thickness(6, 2, 6, 2);
        zoomInBtn.ToolTip = "Zoom in (Ctrl++)";
        zoomInBtn.Click += (_, _) => { _boardZoom = Math.Min(4.0, Math.Round(_boardZoom + 0.1, 2)); BuildBoardContent(); };
        hintRow.Children.Add(zoomInBtn);

        // Keyboard shortcuts for zoom
        KeyDown += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            if (e.Key == Key.OemPlus || e.Key == Key.Add)
            { _boardZoom = Math.Min(4.0, Math.Round(_boardZoom + 0.1, 2)); BuildBoardContent(); e.Handled = true; }
            else if (e.Key == Key.OemMinus || e.Key == Key.Subtract)
            { _boardZoom = Math.Max(0.25, Math.Round(_boardZoom - 0.1, 2)); BuildBoardContent(); e.Handled = true; }
            else if (e.Key == Key.D0 || e.Key == Key.NumPad0)
            { _boardZoom = 1.0; BuildBoardContent(); e.Handled = true; }
        };

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
        // Auto-expand canvas to fit existing content (grows in 400 px steps, never shrinks below 2400×1800)
        {
            var bd = EntityBoardService.Load(_projFolder, _board.Id);
            double xMax = 2400, yMax = 1800;
            foreach (var p in bd.Positions.Values)
            { xMax = Math.Max(xMax, p.X + (p.CardWidth > 0 ? p.CardWidth : 200) + 60); yMax = Math.Max(yMax, p.Y + (p.CardHeight > 0 ? p.CardHeight : 150) + 60); }
            foreach (var t in bd.TextBoxes)
            { xMax = Math.Max(xMax, t.X + t.Width + 60); yMax = Math.Max(yMax, t.Y + t.Height + 60); }
            foreach (var p in bd.BoardPinPositions.Values)
            { xMax = Math.Max(xMax, p.X + 180); yMax = Math.Max(yMax, p.Y + 150); }
            canvas.Width  = Math.Ceiling(xMax / 400) * 400;
            canvas.Height = Math.Ceiling(yMax / 400) * 400;
        }
        canvas.LayoutTransform = Math.Abs(_boardZoom - 1.0) < 0.001
            ? Transform.Identity
            : new ScaleTransform(_boardZoom, _boardZoom);
        boardScroll.Content = canvas;
        _boardCanvas = canvas;
        _boardScroll = boardScroll;

        // Reload board data
        _boardData = EntityBoardService.Load(_projFolder, _board.Id);
        ApplyGridBackground(canvas, _boardData.GridVisible, _boardData.GridSize, _boardData.GridColor);
        _boardCards.Clear();
        _boardLines.Clear();
        _boardCaptionBadges.Clear();
        _boardArrows.Clear();
        _boardWpBubbles.Clear();
        _boardTextBoxes.Clear();
        _boardHitZones.Clear();
        _boardPins.Clear();
        _hoverRelId = null;

        // Only show entities explicitly placed on this board (position exists).
        // New entities are added via the "+" picker, not auto-placed.
        var boardPlacedEntities = allEntities
            .Where(e => _boardData.Positions.ContainsKey(e.Id))
            .ToList();
        // Always load ALL project factions for membership lookup — even on boards without a Faction entity type.
        var boardFactions = WorldEntityService.List(_projFolder, "Faction");

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

        // ── Background image (z=-1) ───────────────────────────────────────────
        if (!string.IsNullOrEmpty(_boardData.BackgroundImagePath)
            && System.IO.File.Exists(_boardData.BackgroundImagePath))
        {
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage(
                    new Uri(_boardData.BackgroundImagePath));
                UIElement bgEl;
                if (_boardData.BgImageMode == "Repeat")
                {
                    double tileW = Math.Max(1, bmp.PixelWidth  * _boardData.BgImageScale / 100.0);
                    double tileH = Math.Max(1, bmp.PixelHeight * _boardData.BgImageScale / 100.0);
                    var imgDraw = new System.Windows.Media.ImageDrawing(bmp, new Rect(0, 0, tileW, tileH));
                    var db = new System.Windows.Media.DrawingBrush(imgDraw)
                    {
                        TileMode      = TileMode.Tile,
                        Viewport      = new Rect(0, 0, tileW, tileH),
                        ViewportUnits = BrushMappingMode.Absolute
                    };
                    bgEl = new System.Windows.Shapes.Rectangle
                    {
                        Width = canvas.Width, Height = canvas.Height,
                        Fill  = db, IsHitTestVisible = false, Opacity = 0.85
                    };
                }
                else
                {
                    var bgImg = new System.Windows.Controls.Image
                    {
                        IsHitTestVisible = false, Opacity = 0.85
                    };
                    if (_boardData.BgImageMode == "Scale")
                    {
                        bgImg.Stretch = Stretch.None;
                        bgImg.Width   = bmp.PixelWidth  * _boardData.BgImageScale / 100.0;
                        bgImg.Height  = bmp.PixelHeight * _boardData.BgImageScale / 100.0;
                    }
                    else
                    {
                        bgImg.Stretch = _boardData.BgImageMode switch
                        {
                            "Uniform"       => Stretch.Uniform,
                            "UniformToFill" => Stretch.UniformToFill,
                            _               => Stretch.Fill
                        };
                        bgImg.Width  = canvas.Width;
                        bgImg.Height = canvas.Height;
                    }
                    bgImg.Source = bmp;
                    bgEl = bgImg;
                }
                Canvas.SetLeft(bgEl, 0); Canvas.SetTop(bgEl, 0);
                Panel.SetZIndex(bgEl, -1);
                canvas.Children.Add(bgEl);
            }
            catch { }
        }

        // ── Render frames (z=0, added first so relations/cards render above) ─
        foreach (var bf in _boardData.Frames.ToList())
            RenderBoardFrame(canvas, bf);

        // ── Render relations (z=0) + caption badges (z=1) ─────────────────
        foreach (var rel in _boardData.Relations)
        {
            var fp = _boardData.Positions[rel.FromId];
            var tp = _boardData.Positions[rel.ToId];
            var x1 = fp.X + (fp.CardWidth  > 0 ? fp.CardWidth  : 160) / 2; var y1 = fp.Y + EstCardH / 2;
            var x2 = tp.X + (tp.CardWidth  > 0 ? tp.CardWidth  : 160) / 2; var y2 = tp.Y + EstCardH / 2;
            RenderRelationVisuals(canvas, rel.Id, x1, y1, x2, y2, rel);

            if (string.IsNullOrWhiteSpace(rel.Caption)) goto skipBadge;
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
            var editRelItem = new MenuItem { Header = Properties.Loc.S("Board_EditRelation") };
            editRelItem.Click += (_, _) =>
            {
                var result = ShowRelationDialog(capturedRel.Caption, capturedRel.LegendLabel,
                    capturedRel.LineStyle, capturedRel.LineColor, capturedRel.Thickness,
                    capturedRel.HasArrow, Properties.Loc.S("Board_EditRelationTitle"));
                if (result is not null)
                {
                    capturedRel.Caption     = result.Caption;
                    capturedRel.LegendLabel = result.LegendLabel;
                    capturedRel.LineStyle   = result.LineStyle;
                    capturedRel.LineColor   = result.LineColor;
                    capturedRel.Thickness   = result.Thickness;
                    capturedRel.HasArrow    = result.HasArrow;
                    MaybeAddLegendPreset(result);
                    EntityBoardService.Save(_projFolder, _board.Id, _boardData);
                    BuildBoardContent();
                }
            };
            var delRelItem = new MenuItem { Header = Properties.Loc.S("Board_DeleteRelation") };
            delRelItem.Click += (_, _) =>
            {
                _boardData.Relations.Remove(capturedRel);
                EntityBoardService.Save(_projFolder, _board.Id, _boardData);
                BuildBoardContent();
            };
            ctx.Items.Add(editRelItem);
            if (capturedRel.HasArrow)
            {
                var flipRelItem = new MenuItem { Header = Properties.Loc.S("Board_FlipArrow") };
                flipRelItem.Click += (_, _) =>
                {
                    (capturedRel.FromId, capturedRel.ToId) = (capturedRel.ToId, capturedRel.FromId);
                    capturedRel.Waypoints.Reverse();
                    (capturedRel.StartsAtJunction, capturedRel.EndsAtJunction) =
                        (capturedRel.EndsAtJunction, capturedRel.StartsAtJunction);
                    EntityBoardService.Save(_projFolder, _board.Id, _boardData);
                    BuildBoardContent();
                };
                ctx.Items.Add(flipRelItem);
            }
            ctx.Items.Add(delRelItem);
            captionBadge.ContextMenu = ctx;
            canvas.Children.Add(captionBadge);
            _boardCaptionBadges[rel.Id] = captionBadge;
            skipBadge: ;
        }

        // ── Render text boxes (z=2) ───────────────────────────────────────
        foreach (var tb in _boardData.TextBoxes)
            RenderBoardTextBox(canvas, tb);

        // ── Render entity cards (z=2, ordered by ZOrder so last-placed is on top) ──
        foreach (var entity in displayedEntities
            .OrderBy(e => _boardData.Positions.TryGetValue(e.Id, out var zp) ? zp.ZOrder : 0))
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
            var  containedCardIds = new List<string>();  // cards whose top-left was inside this card at drag-start

            // Hover glow — subtle border highlight without disturbing selection visuals
            card.MouseEnter += (_, _) =>
            {
                if (!_selectedIds.Contains(capturedId))
                    card.Effect = new System.Windows.Media.Effects.DropShadowEffect
                        { Color = Colors.White, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.5 };
            };
            card.MouseLeave += (_, _) => card.Effect = null;

            card.MouseLeftButtonDown += (_, e) =>
            {
                if (_boardConnectMode) { HandleBoardConnectClick(capturedId); e.Handled = true; return; }
                if (e.ClickCount >= 2) { ShowEntityReadOnlyDialog(capturedEntity); e.Handled = true; return; }

                // Selection: Shift+click toggles, plain click sets exclusive selection
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                    ToggleSelection(capturedId);
                else
                {
                    if (!_selectedIds.Contains(capturedId))
                    {
                        _selectedIds.Clear();
                        _selectedIds.Add(capturedId);
                        RefreshSelectionVisuals();
                    }
                }

                isDragging = true;
                dragOffset = e.GetPosition(card);

                // Snapshot cards contained inside this card (acts as frame when resized large)
                containedCardIds.Clear();
                if (_selectedIds.Count <= 1)
                {
                    var myX = Canvas.GetLeft(card);
                    var myY = Canvas.GetTop(card);
                    var myW = card.ActualWidth  > 0 ? card.ActualWidth  : 160;
                    var myH = card.ActualHeight > 0 ? card.ActualHeight :  80;
                    foreach (var (oid, ocard) in _boardCards)
                    {
                        if (oid == capturedId) continue;
                        var ox = Canvas.GetLeft(ocard); var oy = Canvas.GetTop(ocard);
                        if (ox >= myX && oy >= myY && ox <= myX + myW && oy <= myY + myH)
                            containedCardIds.Add(oid);
                    }
                }

                card.CaptureMouse();
                e.Handled = true;
            };
            card.MouseMove += (_, e) =>
            {
                if (!isDragging) return;
                var pt = e.GetPosition(_boardCanvas);
                var nx = Snap(Math.Max(0, pt.X - dragOffset.X));
                var ny = Snap(Math.Max(0, pt.Y - dragOffset.Y));
                // Move all selected cards together
                if (_selectedIds.Count > 1 && _selectedIds.Contains(capturedId))
                {
                    var oldX = Canvas.GetLeft(card); var oldY = Canvas.GetTop(card);
                    var dx = nx - oldX; var dy = ny - oldY;
                    foreach (var sid in _selectedIds)
                    {
                        if (!_boardCards.TryGetValue(sid, out var sc)) continue;
                        var sx = Canvas.GetLeft(sc) + dx; var sy = Canvas.GetTop(sc) + dy;
                        Canvas.SetLeft(sc, sx); Canvas.SetTop(sc, sy);
                        UpdateBoardLines(sid, sx, sy);
                    }
                }
                else
                {
                    var oldX = Canvas.GetLeft(card); var oldY = Canvas.GetTop(card);
                    var dx = nx - oldX; var dy = ny - oldY;
                    Canvas.SetLeft(card, nx); Canvas.SetTop(card, ny);
                    UpdateBoardLines(capturedId, nx, ny);
                    // Move contained cards along with the host
                    foreach (var cid in containedCardIds)
                    {
                        if (!_boardCards.TryGetValue(cid, out var cc)) continue;
                        var cx2 = Canvas.GetLeft(cc) + dx;
                        var cy2 = Canvas.GetTop(cc)  + dy;
                        Canvas.SetLeft(cc, cx2); Canvas.SetTop(cc, cy2);
                        UpdateBoardLines(cid, cx2, cy2);
                    }
                }
                e.Handled = true;
            };
            card.MouseLeftButtonUp += (_, e) =>
            {
                if (!isDragging) return;
                isDragging = false;
                card.ReleaseMouseCapture();
                // Persist positions for all moved cards (selected group OR host + contained)
                var toSave = _selectedIds.Count > 1 && _selectedIds.Contains(capturedId)
                    ? _selectedIds.AsEnumerable()
                    : new[] { capturedId }.Concat(containedCardIds);
                foreach (var sid in toSave)
                {
                    if (!_boardCards.TryGetValue(sid, out var sc)) continue;
                    var sx = Canvas.GetLeft(sc); var sy = Canvas.GetTop(sc);
                    if (_boardData.Positions.TryGetValue(sid, out var bp2)) { bp2.X = sx; bp2.Y = sy; }
                    else _boardData.Positions[sid] = new BoardPosition { X = sx, Y = sy };
                }
                // Bring dragged card and its contained cards to front.
                // The host card is re-added first, then contained cards on top of it,
                // so cards placed ON the host stay visually above it after the drag.
                int newMaxZ = _boardData.Positions.Values.DefaultIfEmpty()
                                  .Max(p => p?.ZOrder ?? 0);
                if (_boardData.Positions.TryGetValue(capturedId, out var bpFront))
                    bpFront.ZOrder = ++newMaxZ;
                _boardCanvas!.Children.Remove(card);
                _boardCanvas!.Children.Add(card);
                foreach (var cid in containedCardIds)
                {
                    if (_boardData.Positions.TryGetValue(cid, out var bpChild))
                        bpChild.ZOrder = ++newMaxZ;
                    if (_boardCards.TryGetValue(cid, out var cc))
                    {
                        _boardCanvas!.Children.Remove(cc);
                        _boardCanvas!.Children.Add(cc);
                    }
                }
                EntityBoardService.Save(_projFolder, _board.Id, _boardData);
                e.Handled = true;
            };
        }

        // ── Render board pins (z=2) ───────────────────────────────────────
        {
            var allPinBoards   = WorldBoardRegistryService.Load(_projFolder);
            var pinBoardById   = allPinBoards.ToDictionary(b => b.Id);
            foreach (var kv in _boardData.BoardPinPositions.ToList())
            {
                if (!pinBoardById.TryGetValue(kv.Key, out var pinBoard))
                { _boardData.BoardPinPositions.Remove(kv.Key); dirty = true; continue; }
                // Apply name filter
                if (!string.IsNullOrWhiteSpace(_filterName)
                    && !pinBoard.Name.Contains(_filterName, StringComparison.OrdinalIgnoreCase)) continue;

                var pinPos  = kv.Value;
                var capBoardId = kv.Key;
                var capBoard   = pinBoard;
                var pin = BuildBoardPin(pinBoard, pinPos);
                Canvas.SetLeft(pin, pinPos.X);
                Canvas.SetTop (pin, pinPos.Y);
                Panel.SetZIndex(pin, 1);   // below entity cards (z=2) — always acts as background
                canvas.Children.Add(pin);
                _boardPins[capBoardId] = pin;

                // ── Pin drag (carries contained entity cards and nested pins) ──
                bool pinDrag           = false;
                var  pinOffset         = new Point();
                var  pinContainedCards = new List<string>();
                var  pinContainedPins  = new List<string>();

                pin.MouseLeftButtonDown += (_, e) =>
                {
                    if (_boardConnectMode) { HandleBoardConnectClick(capBoardId); e.Handled = true; return; }
                    if (e.ClickCount >= 2)
                    {
                        var capTheme = _themePath;
                        var w = new WorldBoardWindow(_projFolder, capBoard, capTheme);
                        w.Owner = this; w.Show();
                        e.Handled = true; return;
                    }
                    pinDrag   = true;
                    pinOffset = e.GetPosition(pin);

                    // Snapshot entity cards and other pins whose top-left is inside this pin
                    pinContainedCards.Clear();
                    pinContainedPins.Clear();
                    var myX = Canvas.GetLeft(pin);
                    var myY = Canvas.GetTop(pin);
                    var myW = pin.ActualWidth  > 0 ? pin.ActualWidth  : 160;
                    var myH = pin.ActualHeight > 0 ? pin.ActualHeight :  80;
                    foreach (var (cid, cc) in _boardCards)
                    {
                        var cx = Canvas.GetLeft(cc); var cy = Canvas.GetTop(cc);
                        if (cx >= myX && cy >= myY && cx <= myX + myW && cy <= myY + myH)
                            pinContainedCards.Add(cid);
                    }
                    foreach (var (cid, cp) in _boardPins)
                    {
                        if (cid == capBoardId) continue;
                        var cx = Canvas.GetLeft(cp); var cy = Canvas.GetTop(cp);
                        if (cx >= myX && cy >= myY && cx <= myX + myW && cy <= myY + myH)
                            pinContainedPins.Add(cid);
                    }

                    pin.CaptureMouse();
                    e.Handled = true;
                };
                pin.MouseMove += (_, e) =>
                {
                    if (!pinDrag) return;
                    var pt  = e.GetPosition(_boardCanvas);
                    var nx  = Snap(Math.Max(0, pt.X - pinOffset.X));
                    var ny  = Snap(Math.Max(0, pt.Y - pinOffset.Y));
                    var dx  = nx - Canvas.GetLeft(pin);
                    var dy  = ny - Canvas.GetTop(pin);
                    Canvas.SetLeft(pin, nx); Canvas.SetTop(pin, ny);
                    // Move contained entity cards
                    foreach (var cid in pinContainedCards)
                    {
                        if (!_boardCards.TryGetValue(cid, out var cc)) continue;
                        var cx = Canvas.GetLeft(cc) + dx; var cy = Canvas.GetTop(cc) + dy;
                        Canvas.SetLeft(cc, cx); Canvas.SetTop(cc, cy);
                        UpdateBoardLines(cid, cx, cy);
                    }
                    // Move contained nested pins
                    foreach (var cid in pinContainedPins)
                    {
                        if (!_boardPins.TryGetValue(cid, out var cp)) continue;
                        Canvas.SetLeft(cp, Canvas.GetLeft(cp) + dx);
                        Canvas.SetTop (cp, Canvas.GetTop (cp) + dy);
                    }
                    e.Handled = true;
                };
                pin.MouseLeftButtonUp += (_, e) =>
                {
                    if (!pinDrag) return;
                    pinDrag = false;
                    pin.ReleaseMouseCapture();
                    // Save pin position
                    if (_boardData.BoardPinPositions.TryGetValue(capBoardId, out var pp))
                    { pp.X = Canvas.GetLeft(pin); pp.Y = Canvas.GetTop(pin); }
                    // Save contained entity card positions
                    foreach (var cid in pinContainedCards)
                    {
                        if (!_boardCards.TryGetValue(cid, out var cc)) continue;
                        var sx = Canvas.GetLeft(cc); var sy = Canvas.GetTop(cc);
                        if (_boardData.Positions.TryGetValue(cid, out var bp2)) { bp2.X = sx; bp2.Y = sy; }
                        else _boardData.Positions[cid] = new BoardPosition { X = sx, Y = sy };
                    }
                    // Save contained nested pin positions
                    foreach (var cid in pinContainedPins)
                    {
                        if (_boardPins.TryGetValue(cid, out var cp)
                            && _boardData.BoardPinPositions.TryGetValue(cid, out var pp2))
                        { pp2.X = Canvas.GetLeft(cp); pp2.Y = Canvas.GetTop(cp); }
                    }
                    EntityBoardService.Save(_projFolder, _board.Id, _boardData);
                    e.Handled = true;
                };

                // ── Pin context menu ──────────────────────────────────────
                var pinCtx = new ContextMenu();
                var openItem = new MenuItem { Header = "🗺  Open board" };
                openItem.Click += (_, _) =>
                {
                    var capTheme = _themePath;
                    var w = new WorldBoardWindow(_projFolder, capBoard, capTheme);
                    w.Owner = this; w.Show();
                };
                var removeItem = new MenuItem { Header = "🗑  Remove from this board" };
                removeItem.Click += (_, _) =>
                {
                    _boardData.BoardPinPositions.Remove(capBoardId);
                    EntityBoardService.Save(_projFolder, _board.Id, _boardData);
                    BuildBoardContent();
                };
                pinCtx.Items.Add(openItem);
                pinCtx.Items.Add(new Separator());
                pinCtx.Items.Add(removeItem);
                pin.ContextMenu = pinCtx;
            }
        }

        // ── Canvas right-click rubber-band selection ───────────────────────
        // ── Canvas pan (left-drag on empty space) + right-click menu ─────────
        canvas.Cursor = Cursors.Hand;   // default grab cursor on empty space

        bool   panPotential = false;
        bool   isPanDrag    = false;
        Point  panOrigin    = default;
        double panH0 = 0, panV0 = 0;

        canvas.MouseRightButtonDown += (_, e) =>
        {
            if (_boardConnectMode) return;
            // Only start rubber-band selection when clicking empty canvas background.
            // If the click originated from a card or any other child element that has
            // a ContextMenu, bail out so WPF can show that element's own menu.
            if (e.OriginalSource != canvas) return;
            _selDragStart  = e.GetPosition(canvas);
            _selDragging   = true;
            _selectionRect = new System.Windows.Shapes.Rectangle
            {
                Stroke          = new SolidColorBrush(Color.FromArgb(200, 80, 160, 255)),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                Fill            = new SolidColorBrush(Color.FromArgb(30, 80, 160, 255))
            };
            Panel.SetZIndex(_selectionRect, 20);
            canvas.Children.Add(_selectionRect);
            canvas.CaptureMouse();
            e.Handled = true;
        };
        canvas.MouseMove += (_, e) =>
        {
            // Rubber-band selection (right-drag)
            if (_selDragging && _selectionRect != null)
            {
                var cur = e.GetPosition(canvas);
                Canvas.SetLeft(_selectionRect, Math.Min(cur.X, _selDragStart.X));
                Canvas.SetTop (_selectionRect, Math.Min(cur.Y, _selDragStart.Y));
                _selectionRect.Width  = Math.Abs(cur.X - _selDragStart.X);
                _selectionRect.Height = Math.Abs(cur.Y - _selDragStart.Y);
                e.Handled = true;
                return;
            }
            // Pan (left-drag on empty space)
            if (!panPotential) return;
            var pt = e.GetPosition(_boardScroll);
            if (!isPanDrag)
            {
                if (Math.Abs(pt.X - panOrigin.X) > 4 || Math.Abs(pt.Y - panOrigin.Y) > 4)
                {
                    isPanDrag = true;
                    canvas.Cursor = Cursors.SizeAll;
                }
            }
            if (isPanDrag)
            {
                _boardScroll!.ScrollToHorizontalOffset(panH0 - (pt.X - panOrigin.X));
                _boardScroll!.ScrollToVerticalOffset  (panV0 - (pt.Y - panOrigin.Y));
                e.Handled = true;
            }
        };
        canvas.MouseRightButtonUp += (_, e) =>
        {
            if (!_selDragging || _selectionRect == null) return;
            _selDragging = false;
            canvas.ReleaseMouseCapture();
            var cur    = e.GetPosition(canvas);
            var selBox = new Rect(
                Math.Min(cur.X, _selDragStart.X), Math.Min(cur.Y, _selDragStart.Y),
                Math.Abs(cur.X - _selDragStart.X), Math.Abs(cur.Y - _selDragStart.Y));
            canvas.Children.Remove(_selectionRect);
            _selectionRect = null;

            if (selBox.Width > 6 || selBox.Height > 6)
            {
                // Rubber-band selection
                _selectedIds.Clear();
                foreach (var (eid, ecard) in _boardCards)
                {
                    var ex = Canvas.GetLeft(ecard); var ey = Canvas.GetTop(ecard);
                    if (selBox.IntersectsWith(new Rect(ex, ey,
                            ecard.ActualWidth  > 0 ? ecard.ActualWidth  : 160,
                            ecard.ActualHeight > 0 ? ecard.ActualHeight :  80)))
                        _selectedIds.Add(eid);
                }
                RefreshSelectionVisuals();
                e.Handled = true;
            }
            else if (!_boardConnectMode)
            {
                // Simple right-click on empty space → "Add" context menu at that position
                var menu = BuildAddContextMenu(
                    Snap(Math.Max(0, _selDragStart.X)),
                    Snap(Math.Max(0, _selDragStart.Y)));
                menu.PlacementTarget = canvas;
                menu.Placement       = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                menu.IsOpen          = true;
                e.Handled            = true;
            }
        };

        // ── Ctrl+C / Ctrl+V for board copy/paste ──────────────────────────
        KeyDown += OnBoardKeyDown;

        // ── Canvas left-click / left-drag: deselect (click) or pan (drag) ─
        canvas.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource != canvas) return;
            panPotential = true;
            panOrigin    = e.GetPosition(_boardScroll);
            panH0        = _boardScroll!.HorizontalOffset;
            panV0        = _boardScroll!.VerticalOffset;
            canvas.CaptureMouse();
            e.Handled = true;
        };
        canvas.MouseLeftButtonUp += (_, e) =>
        {
            if (!panPotential) return;
            canvas.ReleaseMouseCapture();
            if (!isPanDrag && !_boardConnectMode)
            {
                _selectedIds.Clear();
                RefreshSelectionVisuals();
            }
            panPotential = false;
            isPanDrag    = false;
            canvas.Cursor = Cursors.Hand;
            e.Handled = true;
        };

        // Floating legend overlay
        var legendOverlay = BuildBoardLegend();
        legendOverlay.HorizontalAlignment = HorizontalAlignment.Left;
        legendOverlay.VerticalAlignment   = VerticalAlignment.Top;
        legendOverlay.Margin              = new Thickness(10, 10, 0, 0);
        Panel.SetZIndex(legendOverlay, 10);
        boardGrid.Children.Add(legendOverlay);

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RefreshAllBoardLinePositions));

        // Match the main-app UI scale
        UiZoomHelper.Apply(this, UiZoomHelper.FromSettings(), scaleWindow: false);
    }

    // ── Selection helpers ──────────────────────────────────────────────────

    private void ToggleSelection(string entityId)
    {
        if (_selectedIds.Contains(entityId))
            _selectedIds.Remove(entityId);
        else
            _selectedIds.Add(entityId);
        RefreshSelectionVisuals();
    }

    private void RefreshSelectionVisuals()
    {
        // Cards: highlight selected, dim unselected when there's a selection
        bool anySelected = _selectedIds.Count > 0;
        foreach (var (eid, ecard) in _boardCards)
        {
            bool isSelected = _selectedIds.Contains(eid);
            ecard.Opacity = anySelected && !isSelected ? 0.45 : 1.0;
            if (isSelected)
            {
                ecard.BorderBrush     = (Brush)(TryFindResource("AccentHighlightBrush") ?? new SolidColorBrush(Colors.DodgerBlue));
                ecard.BorderThickness = new Thickness(2);
            }
            else
            {
                // Restore default border
                ecard.ClearValue(Border.BorderBrushProperty);
                ecard.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
                ecard.BorderThickness = new Thickness(1);
            }
        }
        // Lines: highlight only when BOTH endpoints are selected; hovered line is always bright
        foreach (var rel in _boardData.Relations)
        {
            bool bothSelected = _selectedIds.Contains(rel.FromId) && _selectedIds.Contains(rel.ToId);
            bool hovered      = rel.Id == _hoverRelId;
            double opacity = anySelected ? (bothSelected ? 1.0 : 0.15) : 0.85;
            if (hovered) opacity = 1.0;  // always full-bright while hovering

            if (_boardLines.TryGetValue(rel.Id, out var segs))
                foreach (var (l1, l2) in segs) { l1.Opacity=opacity; if(l2!=null) l2.Opacity=opacity; }
            if (_boardCaptionBadges.TryGetValue(rel.Id, out var badge)) badge.Opacity = opacity;
            if (_boardArrows.TryGetValue(rel.Id, out var arrow))        arrow.Opacity  = opacity;
            if (_boardWpBubbles.TryGetValue(rel.Id, out var bubs))
                foreach (var b in bubs) b.Opacity = opacity;
        }
    }

    // ── Board keyboard (Ctrl+C/V) ─────────────────────────────────────────

    private record struct CopiedEntity(string OriginalId, WorldEntity Entity, double X, double Y);
    private List<CopiedEntity> _copiedBoard = new();

    /// <summary>
    /// Confirms and permanently deletes the given entity IDs from the world and the board.
    /// </summary>
    /// <summary>
    /// Removes the given entity IDs from the board (positions + any touching
    /// relation lines).  Does NOT delete the entities from the world library.
    /// A confirmation is shown only when the removed cards have connection lines.
    /// </summary>
    private void RemoveFromBoard(IEnumerable<string> ids)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return;

        int relCount = _boardData.Relations.Count(
            r => idList.Contains(r.FromId) || idList.Contains(r.ToId));

        if (relCount > 0)
        {
            var msg = idList.Count == 1
                ? string.Format(Properties.Loc.S("Board_RemoveConfirmConnections"), relCount)
                : string.Format(Properties.Loc.S("Board_RemoveManyConfirmConnections"),
                                idList.Count, relCount);

            if (MessageBox.Show(msg, Properties.Loc.S("Board_RemoveFromBoard"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
        }

        foreach (var id in idList)
        {
            _boardData.Positions.Remove(id);
            _boardData.Relations.RemoveAll(r => r.FromId == id || r.ToId == id);
            _selectedIds.Remove(id);
        }

        EntityBoardService.Save(_projFolder, _board.Id, _boardData);
        BuildBoardContent();
    }

    private void OnBoardKeyDown(object sender, KeyEventArgs e)
    {
        // Delete / Backspace — remove selected cards from board (no modifier needed)
        if (e.Key is Key.Delete or Key.Back && _selectedIds.Count > 0)
        {
            RemoveFromBoard(_selectedIds.ToList());
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        if (e.Key == Key.A)
        {
            _selectedIds.Clear();
            foreach (var id in _boardCards.Keys) _selectedIds.Add(id);
            RefreshSelectionVisuals();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.C && _selectedIds.Count > 0)
        {
            _copiedBoard.Clear();
            foreach (var eid in _selectedIds)
            {
                if (!_boardCards.TryGetValue(eid, out var ecard)) continue;
                var allEntities = _board.EntityTypes
                    .SelectMany(et => WorldEntityService.List(_projFolder, et)).ToList();
                var ent = allEntities.FirstOrDefault(x => x.Id == eid);
                if (ent == null) continue;
                _copiedBoard.Add(new CopiedEntity(eid, ent,
                    Canvas.GetLeft(ecard), Canvas.GetTop(ecard)));
            }
            e.Handled = true;
        }
        else if (e.Key == Key.V && _copiedBoard.Count > 0)
        {
            // Paste on same board — offset by 40,40 from originals
            foreach (var ce in _copiedBoard)
            {
                if (!_boardData.Positions.ContainsKey(ce.OriginalId)) continue; // entity removed from board
                _boardData.Positions[ce.OriginalId] = new BoardPosition
                {
                    X = ce.X + 40, Y = ce.Y + 40,
                    CardWidth  = _boardData.Positions.TryGetValue(ce.OriginalId, out var bp3) ? bp3.CardWidth  : 0,
                    CardHeight = _boardData.Positions.TryGetValue(ce.OriginalId, out var bp4) ? bp4.CardHeight : 0
                };
            }
            // Also paste any relation between two pasted entities (they already exist, just reposition)
            EntityBoardService.Save(_projFolder, _board.Id, _boardData);
            BuildBoardContent();
            e.Handled = true;
        }
    }

    // ── Add context menu (toolbar dropdown + canvas right-click) ─────────────

    /// <summary>
    /// Builds the "Add" context menu with entity-type pickers, text box, and board pin.
    /// <paramref name="placeX"/>/<paramref name="placeY"/> are the canvas coordinates where
    /// items will be placed (snapped by the caller).
    /// </summary>
    private ContextMenu BuildAddContextMenu(double placeX, double placeY)
    {
        var menu = new ContextMenu();

        // ── Entity type entries ──────────────────────────────────────────
        foreach (var et in _board.EntityTypes)
        {
            var capturedEt = et;
            var item = new MenuItem { Header = $"+ {et}" };
            item.Click += (_, _) =>
            {
                var allOfType  = WorldEntityService.List(_projFolder, capturedEt);
                var onBoard    = _boardData.Positions.Keys.ToHashSet();
                var eligible   = allOfType.Where(e => !onBoard.Contains(e.Id)).ToList();
                if (eligible.Count == 0)
                {
                    MessageBox.Show(
                        $"All {capturedEt}s are already on this board, or none exist yet.\nCreate new ones from the {capturedEt}s list view.",
                        $"No {capturedEt}s to add", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var picked = ShowEntityPickerDialog(eligible, $"Add {capturedEt}s to Board", _projFolder);
                if (picked.Count == 0) return;
                double px = placeX, py = placeY;
                foreach (var e in picked)
                {
                    _boardData.Positions[e.Id] = new BoardPosition { X = Snap(px), Y = Snap(py) };
                    px += 200; if (px > 1400) { px = 40; py += 200; }
                }
                EntityBoardService.Save(_projFolder, _board.Id, _boardData);
                BuildBoardContent();
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        // ── Text box ────────────────────────────────────────────────────
        var textItem = new MenuItem { Header = Properties.Loc.S("Board_TextBox") };
        textItem.Click += (_, _) =>
        {
            var tb = new BoardTextBox { X = Snap(placeX), Y = Snap(placeY) };
            _boardData.TextBoxes.Add(tb);
            if (ShowTextBoxStyleDialog(tb))
                EntityBoardService.Save(_projFolder, _board.Id, _boardData);
            else
                _boardData.TextBoxes.Remove(tb);
            BuildBoardContent();
        };
        menu.Items.Add(textItem);

        // ── Background image ─────────────────────────────────────────────
        var bgImgItem = new MenuItem { Header = Properties.Loc.S("Board_BgImage") };
        bgImgItem.Click += (_, _) =>
        {
            ShowBackgroundImageDialog();
        };
        menu.Items.Add(bgImgItem);

        // ── Board pin ────────────────────────────────────────────────────
        var allBrds  = WorldBoardRegistryService.Load(_projFolder);
        var eligible2 = allBrds
            .Where(b => b.Id != _board.Id && !_boardData.BoardPinPositions.ContainsKey(b.Id))
            .ToList();
        if (eligible2.Count > 0)
        {
            var boardSub = new MenuItem { Header = Properties.Loc.S("Board_PinBoard") };
            boardSub.Items.Add(new MenuItem { Header = "…", IsEnabled = false }); // force arrow
            boardSub.SubmenuOpened += (_, _) =>
            {
                boardSub.Items.Clear();
                foreach (var b in eligible2)
                {
                    var cap = b;
                    var mi  = new MenuItem { Header = cap.Symbol + "  " + cap.Name };
                    mi.Click += (_, _) =>
                    {
                        _boardData.BoardPinPositions[cap.Id] = new BoardPosition
                            { X = Snap(placeX), Y = Snap(placeY) };
                        EntityBoardService.Save(_projFolder, _board.Id, _boardData);
                        BuildBoardContent();
                    };
                    boardSub.Items.Add(mi);
                }
            };
            menu.Items.Add(boardSub);
        }

        // ── Frame ─────────────────────────────────────────────────────────────
        var frameItem = new MenuItem { Header = Properties.Loc.S("Board_AddFrame") };
        frameItem.Click += (_, _) =>
        {
            var bf = new BoardFrame { X = Snap(placeX), Y = Snap(placeY) };
            if (ShowFrameEditDialog(bf))
            {
                _boardData.Frames.Add(bf);
                EntityBoardService.Save(_projFolder, _board.Id, _boardData);
                BuildBoardContent();
            }
        };
        menu.Items.Add(new Separator());
        menu.Items.Add(frameItem);

        return menu;
    }

    // ── Grid settings dialog ──────────────────────────────────────────────────

    private void ShowGridSettingsDialog()
    {
        var dlg = new Window
        {
            Title                 = Properties.Loc.S("Board_GridSettingsTitle"),
            SizeToContent         = SizeToContent.WidthAndHeight,
            MinWidth              = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ResizeMode            = ResizeMode.NoResize
        };
        foreach (var d in Resources.MergedDictionaries) dlg.Resources.MergedDictionaries.Add(d);
        dlg.Background = TryFindResource("ContentBgBrush") as Brush ?? Brushes.White;
        SourceInitialized += (_, _) => ParticipantsWindow.TryApplyTitleBarTo(dlg);

        var root = new StackPanel { Margin = new Thickness(16) };

        TextBlock Lbl(string t)
        {
            var tb = new TextBlock { Text = t, FontSize = 11, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 4) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            return tb;
        }

        // ── Visible grid + opacity slider ─────────────────────────────────────
        root.Children.Add(Lbl(Properties.Loc.S("Board_GridDisplay")));

        // Parse existing alpha and rgb from stored GridColor
        byte   curAlpha = 38;
        string? curRgb  = null;
        if (!string.IsNullOrEmpty(_boardData.GridColor))
        {
            try
            {
                var gc = (Color)ColorConverter.ConvertFromString(_boardData.GridColor)!;
                curAlpha = gc.A;
                curRgb   = $"#{gc.R:X2}{gc.G:X2}{gc.B:X2}";
            }
            catch { }
        }

        var displayRow = new StackPanel
            { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var visCheck = new CheckBox
        {
            Content = Properties.Loc.S("Board_ShowGridLines"), IsChecked = _boardData.GridVisible,
            VerticalAlignment = VerticalAlignment.Center
        };
        visCheck.SetResourceReference(CheckBox.ForegroundProperty, "ContentTextBrush");

        var alphaLbl = new TextBlock
        {
            Text = Properties.Loc.S("Board_GridOpacity"), Margin = new Thickness(16, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        alphaLbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");

        var alphaSlider = new Slider
        {
            Minimum = 5, Maximum = 255, Value = curAlpha, Width = 130,
            VerticalAlignment = VerticalAlignment.Center,
            IsSnapToTickEnabled = true, TickFrequency = 5
        };
        var alphaValLbl = new TextBlock
        {
            Text = $"{(int)Math.Round(curAlpha / 255.0 * 100)}%",
            Width = 36, Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        alphaValLbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        alphaSlider.ValueChanged += (_, e) =>
            alphaValLbl.Text = $"{(int)Math.Round(e.NewValue / 255.0 * 100)}%";

        displayRow.Children.Add(visCheck);
        displayRow.Children.Add(alphaLbl);
        displayRow.Children.Add(alphaSlider);
        displayRow.Children.Add(alphaValLbl);
        root.Children.Add(displayRow);

        // ── Grid line colour ──────────────────────────────────────────────────
        root.Children.Add(Lbl(Properties.Loc.S("Board_GridColour")));

        string? selGridRgb = curRgb;  // "#RRGGBB" without alpha; null = theme default
        var swatchWrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        var swatchList = new List<Border>();

        foreach (var hex in WorldEntitySchemas.FactionColorPalette)
        {
            var capHex = hex;
            Color c; try { c = (Color)ColorConverter.ConvertFromString(hex)!; } catch { c = Colors.Gray; }
            bool isSel = string.Equals(hex, curRgb, StringComparison.OrdinalIgnoreCase);
            var sw = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(4), Margin = new Thickness(2),
                Background      = new SolidColorBrush(c),
                BorderThickness = isSel ? new Thickness(2) : new Thickness(0),
                BorderBrush     = Brushes.White, Cursor = Cursors.Hand, ToolTip = hex
            };
            sw.MouseLeftButtonDown += (_, _) =>
            {
                selGridRgb = capHex;
                foreach (var s in swatchList) s.BorderThickness = new Thickness(0);
                sw.BorderThickness = new Thickness(2);
            };
            swatchList.Add(sw); swatchWrap.Children.Add(sw);
        }
        root.Children.Add(swatchWrap);

        // Reset button
        var resetColorBtn = new Button
        {
            Content = Properties.Loc.S("Board_ResetGridColour"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 0, 4)
        };
        resetColorBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        resetColorBtn.SetResourceReference(Button.ForegroundProperty, "ContentTextBrush");
        resetColorBtn.Click += (_, _) =>
        {
            selGridRgb = null;
            foreach (var s in swatchList) s.BorderThickness = new Thickness(0);
        };
        root.Children.Add(resetColorBtn);

        // ── Grid size ─────────────────────────────────────────────────────────
        root.Children.Add(Lbl(Properties.Loc.S("Board_GridSizeLabel")));
        var sizeBox = new TextBox
        {
            Text = ((int)_boardData.GridSize).ToString(), Width = 80, FontSize = 13,
            Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 0, 0, 12)
        };
        sizeBox.SetResourceReference(TextBox.BackgroundProperty,  "ControlBgBrush");
        sizeBox.SetResourceReference(TextBox.ForegroundProperty,  "ContentTextBrush");
        sizeBox.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        root.Children.Add(sizeBox);

        // ── Buttons ───────────────────────────────────────────────────────────
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right };
        var okBtn  = new Button { Content = Properties.Loc.S("Btn_OK"),     Padding = new Thickness(14, 4, 14, 4),
            Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var canBtn = new Button { Content = Properties.Loc.S("Btn_Cancel"), Padding = new Thickness(14, 4, 14, 4), IsCancel = true };
        foreach (var b in new[] { okBtn, canBtn })
        {
            b.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
            b.SetResourceReference(Button.ForegroundProperty, "ContentTextBrush");
        }
        btnRow.Children.Add(okBtn); btnRow.Children.Add(canBtn);
        root.Children.Add(btnRow);

        okBtn.Click += (_, _) =>
        {
            if (!double.TryParse(sizeBox.Text, out var sz) || sz < 4 || sz > 200)
            {
                MessageBox.Show(Properties.Loc.S("Board_GridSizeError"), Properties.Loc.S("Board_Invalid"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _boardData.GridVisible = visCheck.IsChecked == true;
            _boardData.GridSize    = sz;
            // Combine chosen RGB + opacity slider into "#AARRGGBB"
            if (selGridRgb == null)
            {
                _boardData.GridColor = null; // use theme default
            }
            else
            {
                byte alpha = (byte)Math.Round(alphaSlider.Value);
                try
                {
                    var gc = (Color)ColorConverter.ConvertFromString(selGridRgb)!;
                    _boardData.GridColor = $"#{alpha:X2}{gc.R:X2}{gc.G:X2}{gc.B:X2}";
                }
                catch { _boardData.GridColor = null; }
            }
            EntityBoardService.Save(_projFolder, _board.Id, _boardData);
            BuildBoardContent();
            dlg.Close();
        };
        canBtn.Click += (_, _) => dlg.Close();

        dlg.Content = root;
        dlg.ShowDialog();
    }

    // ── Background image dialog ───────────────────────────────────────────────

    private void ShowBackgroundImageDialog()
    {
        var dlg = new Window
        {
            Title                 = Properties.Loc.S("Board_BgImageTitle"),
            SizeToContent         = SizeToContent.WidthAndHeight,
            MinWidth              = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ResizeMode            = ResizeMode.NoResize
        };
        foreach (var d in Resources.MergedDictionaries) dlg.Resources.MergedDictionaries.Add(d);
        dlg.Background = TryFindResource("ContentBgBrush") as Brush ?? Brushes.White;

        var root = new StackPanel { Margin = new Thickness(16) };

        TextBlock Lbl(string t)
        {
            var tb = new TextBlock { Text = t, FontSize = 11, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 4) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            return tb;
        }

        // Current path
        root.Children.Add(Lbl(Properties.Loc.S("Board_ImageFile")));
        var pathRow  = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,8) };
        var pathBox  = new TextBox
        {
            Text = _boardData.BackgroundImagePath ?? "",
            Width = 240, Padding = new Thickness(4, 3, 4, 3),
            IsReadOnly = true
        };
        pathBox.SetResourceReference(TextBox.BackgroundProperty,  "ControlBgBrush");
        pathBox.SetResourceReference(TextBox.ForegroundProperty,  "SidebarDimBrush");
        pathBox.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        var browseBtn = new Button { Content = Properties.Loc.S("Board_BrowseBtn"), Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(8, 3, 8, 3) };
        browseBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        browseBtn.SetResourceReference(Button.ForegroundProperty, "ContentTextBrush");
        browseBtn.Click += (_, _) =>
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select background image",
                Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*"
            };
            if (ofd.ShowDialog() == true) pathBox.Text = ofd.FileName;
        };
        var clearBtn = new Button { Content = "✕", Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(6, 3, 6, 3), ToolTip = "Remove background image" };
        clearBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        clearBtn.SetResourceReference(Button.ForegroundProperty, "ContentTextBrush");
        clearBtn.Click += (_, _) => pathBox.Text = "";
        pathRow.Children.Add(pathBox); pathRow.Children.Add(browseBtn); pathRow.Children.Add(clearBtn);
        root.Children.Add(pathRow);

        // Mode
        root.Children.Add(Lbl(Properties.Loc.S("Board_DisplayMode")));
        var modeCombo = new ComboBox { Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0,0,0,8) };
        modeCombo.SetResourceReference(ComboBox.BackgroundProperty,  "ControlBgBrush");
        modeCombo.SetResourceReference(ComboBox.BorderBrushProperty, "ControlBorderBrush");
        var modes = new[] { ("Fill", "Stretch to fill board"), ("Uniform", "Fit (keep proportions)"),
                            ("UniformToFill", "Crop-fill (keep proportions)"),
                            ("Repeat", "Repeat / tile (uses scale %)"), ("Scale", "Scale at % (no repeat)") };
        foreach (var (key, label) in modes)
            modeCombo.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        int modeIdx = Array.FindIndex(modes, m => m.Item1 == _boardData.BgImageMode);
        modeCombo.SelectedIndex = Math.Max(0, modeIdx);
        root.Children.Add(modeCombo);

        // Scale (only used for "Scale" mode — still always shown)
        root.Children.Add(Lbl(Properties.Loc.S("Board_ScaleHint")));
        var scaleRow  = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,12) };
        var scaleBox  = new TextBox
        {
            Text = ((int)_boardData.BgImageScale).ToString(), Width = 70,
            Padding = new Thickness(4, 3, 4, 3)
        };
        scaleBox.SetResourceReference(TextBox.BackgroundProperty,  "ControlBgBrush");
        scaleBox.SetResourceReference(TextBox.ForegroundProperty,  "ContentTextBrush");
        scaleBox.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        var scalePct  = new TextBlock { Text = "%", Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center };
        scalePct.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        scaleRow.Children.Add(scaleBox); scaleRow.Children.Add(scalePct);
        root.Children.Add(scaleRow);

        // OK / Cancel
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right };
        var okBtn  = new Button { Content = Properties.Loc.S("Btn_OK"),     Padding = new Thickness(14, 4, 14, 4),
            Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var canBtn = new Button { Content = Properties.Loc.S("Btn_Cancel"), Padding = new Thickness(14, 4, 14, 4), IsCancel = true };
        foreach (var b in new[] { okBtn, canBtn })
        {
            b.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
            b.SetResourceReference(Button.ForegroundProperty, "ContentTextBrush");
        }
        btnRow.Children.Add(okBtn); btnRow.Children.Add(canBtn);
        root.Children.Add(btnRow);

        okBtn.Click += (_, _) =>
        {
            if (!double.TryParse(scaleBox.Text, out var sc) || sc < 1 || sc > 500)
            {
                MessageBox.Show("Scale must be between 1 and 500.", Properties.Loc.S("Board_Invalid"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var selMode = (modeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Fill";
            _boardData.BackgroundImagePath = string.IsNullOrWhiteSpace(pathBox.Text) ? null : pathBox.Text;
            _boardData.BgImageMode  = selMode;
            _boardData.BgImageScale = sc;
            EntityBoardService.Save(_projFolder, _board.Id, _boardData);
            BuildBoardContent();
            dlg.Close();
        };
        canBtn.Click += (_, _) => dlg.Close();

        dlg.Content = root;
        dlg.ShowDialog();
    }

    // ── Board pin tile ────────────────────────────────────────────────────────

    private Border BuildBoardPin(WorldBoard board, BoardPosition pinPos)
    {
        double pinW = pinPos.CardWidth  > 0 ? pinPos.CardWidth  : 120;
        double pinH = pinPos.CardHeight > 0 ? pinPos.CardHeight : 110;

        var pin = new Border
        {
            Width           = pinW,
            Height          = pinH,
            CornerRadius    = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background      = new SolidColorBrush(Color.FromArgb(12, 100, 100, 220)),
            Cursor          = _boardConnectMode ? Cursors.Hand : Cursors.SizeAll
        };
        pin.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");

        // Grid: content row + resize strip
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14, GridUnitType.Pixel) });
        pin.Child = g;

        // Symbol — accent colour, semi-transparent
        var sym = new TextBlock
        {
            Text = board.Symbol, FontSize = 32,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Opacity = 0.65
        };
        sym.SetResourceReference(TextBlock.ForegroundProperty, "AccentHighlightBrush");

        var nameBlock = new TextBlock
        {
            Text = board.Name, FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = pinW - 10, Margin = new Thickness(0, 2, 0, 0),
            Opacity = 0.7
        };
        nameBlock.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center
        };
        stack.Children.Add(sym);
        stack.Children.Add(nameBlock);
        Grid.SetRow(stack, 0);
        g.Children.Add(stack);

        // Resize grip row
        var gripRow = new DockPanel { LastChildFill = false };
        Grid.SetRow(gripRow, 1);
        g.Children.Add(gripRow);

        var grip = new Border
        {
            Width = 12, Height = 10,
            Margin = new Thickness(0, 0, 2, 2),
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeNWSE,
            ToolTip = "Drag to resize"
        };
        DockPanel.SetDock(grip, Dock.Right);
        var gripCanvas = new Canvas { Width = 12, Height = 10 };
        for (int i = 2; i <= 10; i += 3)
        {
            var ln = new Line { X1 = i, Y1 = 10, X2 = 10, Y2 = i, StrokeThickness = 1, Opacity = 0.45 };
            ln.SetResourceReference(Line.StrokeProperty, "SidebarDimBrush");
            gripCanvas.Children.Add(ln);
        }
        grip.Child = gripCanvas;
        gripRow.Children.Add(grip);

        // Resize drag
        bool   isResizing = false;
        double rsX = 0, rsY = 0, rsW = 0, rsH = 0;
        grip.MouseLeftButtonDown += (_, e) =>
        {
            isResizing = true;
            var pt = e.GetPosition(_boardCanvas);
            rsX = pt.X; rsY = pt.Y;
            rsW = pin.ActualWidth; rsH = pin.ActualHeight;
            grip.CaptureMouse(); e.Handled = true;
        };
        grip.MouseMove += (_, e) =>
        {
            if (!isResizing) return;
            var pt = e.GetPosition(_boardCanvas);
            pin.Width  = Snap(Math.Max(80,  rsW + (pt.X - rsX)));
            pin.Height = Snap(Math.Max(70,  rsH + (pt.Y - rsY)));
            e.Handled = true;
        };
        grip.MouseLeftButtonUp += (_, e) =>
        {
            if (!isResizing) return;
            isResizing = false;
            grip.ReleaseMouseCapture();
            pinPos.CardWidth  = pin.Width;
            pinPos.CardHeight = pin.Height;
            EntityBoardService.Save(_projFolder, _board.Id, _boardData);
            e.Handled = true;
        };

        // Hover: thin accent border
        pin.MouseEnter += (_, _) =>
        {
            pin.SetResourceReference(Border.BorderBrushProperty, "AccentHighlightBrush");
            pin.BorderThickness = new Thickness(1.5);
        };
        pin.MouseLeave += (_, _) =>
        {
            pin.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
            pin.BorderThickness = new Thickness(1);
        };

        return pin;
    }

    // ── Board frame ───────────────────────────────────────────────────────────

    private void RenderBoardFrame(Canvas canvas, BoardFrame bf)
    {
        Color frameColor = Colors.SteelBlue;
        try { frameColor = (Color)ColorConverter.ConvertFromString(bf.Color)!; } catch { }

        // Background fill + border (visual only, pass-through to items)
        var frameRect = new Rectangle
        {
            Width            = bf.Width,
            Height           = bf.Height,
            RadiusX          = 8,
            RadiusY          = 8,
            StrokeThickness  = 2,
            IsHitTestVisible = false,
            Fill             = new SolidColorBrush(Color.FromArgb(22, frameColor.R, frameColor.G, frameColor.B)),
            Stroke           = new SolidColorBrush(Color.FromArgb(120, frameColor.R, frameColor.G, frameColor.B))
        };
        Canvas.SetLeft(frameRect, bf.X); Canvas.SetTop(frameRect, bf.Y);
        Panel.SetZIndex(frameRect, 0);
        canvas.Children.Add(frameRect);

        // Header bar: label + drag handle (z=4, floats above cards)
        var headerBorder = new Border
        {
            Width        = bf.Width,
            Height       = 22,
            CornerRadius = new CornerRadius(8, 8, 0, 0),
            Background   = new SolidColorBrush(Color.FromArgb(65, frameColor.R, frameColor.G, frameColor.B)),
            Cursor       = Cursors.SizeAll
        };
        var labelBlock = new TextBlock
        {
            Text              = bf.Label,
            FontSize          = 10,
            FontWeight        = FontWeights.SemiBold,
            Foreground        = new SolidColorBrush(Color.FromArgb(210, frameColor.R, frameColor.G, frameColor.B)),
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Margin            = new Thickness(8, 3, 8, 3),
            VerticalAlignment = VerticalAlignment.Center
        };
        headerBorder.Child = labelBlock;
        Canvas.SetLeft(headerBorder, bf.X); Canvas.SetTop(headerBorder, bf.Y);
        Panel.SetZIndex(headerBorder, 4);
        canvas.Children.Add(headerBorder);

        // Resize grip — bottom-right corner (z=4)
        const double GripSz = 14;
        var frameGrip = new Border
        {
            Width      = GripSz,
            Height     = GripSz,
            Background = Brushes.Transparent,
            Cursor     = Cursors.SizeNWSE,
            ToolTip    = "Drag to resize frame"
        };
        var gripC = new Canvas { Width = 12, Height = 10 };
        for (int gi = 2; gi <= 10; gi += 3)
        {
            var ln = new Line
            {
                X1 = gi, Y1 = 10, X2 = 10, Y2 = gi,
                StrokeThickness = 1, Opacity = 0.6,
                Stroke = new SolidColorBrush(Color.FromArgb(180, frameColor.R, frameColor.G, frameColor.B))
            };
            gripC.Children.Add(ln);
        }
        frameGrip.Child = gripC;
        Canvas.SetLeft(frameGrip, bf.X + bf.Width - GripSz);
        Canvas.SetTop (frameGrip, bf.Y + bf.Height - GripSz);
        Panel.SetZIndex(frameGrip, 4);
        canvas.Children.Add(frameGrip);

        // ── Header drag: moves frame + all items whose top-left is inside ────
        bool frameDrag = false, frameMoved = false;
        var  frameDragOff  = new Point();
        var  captCardOff   = new Dictionary<string, Point>();
        var  captTbOff     = new Dictionary<string, Point>();
        var  captPinOff    = new Dictionary<string, Point>();

        headerBorder.MouseLeftButtonDown += (_, e) =>
        {
            if (_boardConnectMode) { HandleBoardConnectClick(bf.Id); e.Handled = true; return; }
            frameDrag = true; frameMoved = false;
            var raw = e.GetPosition(canvas);
            frameDragOff = new Point(raw.X - bf.X, raw.Y - bf.Y);

            captCardOff.Clear(); captTbOff.Clear(); captPinOff.Clear();
            foreach (var kv in _boardData.Positions)
                if (kv.Value.X >= bf.X && kv.Value.Y >= bf.Y && kv.Value.X <= bf.X + bf.Width && kv.Value.Y <= bf.Y + bf.Height)
                    captCardOff[kv.Key] = new Point(kv.Value.X - bf.X, kv.Value.Y - bf.Y);
            foreach (var tb in _boardData.TextBoxes)
                if (tb.X >= bf.X && tb.Y >= bf.Y && tb.X <= bf.X + bf.Width && tb.Y <= bf.Y + bf.Height)
                    captTbOff[tb.Id] = new Point(tb.X - bf.X, tb.Y - bf.Y);
            foreach (var kv in _boardData.BoardPinPositions)
                if (kv.Value.X >= bf.X && kv.Value.Y >= bf.Y && kv.Value.X <= bf.X + bf.Width && kv.Value.Y <= bf.Y + bf.Height)
                    captPinOff[kv.Key] = new Point(kv.Value.X - bf.X, kv.Value.Y - bf.Y);

            headerBorder.CaptureMouse(); e.Handled = true;
        };
        headerBorder.MouseMove += (_, e) =>
        {
            if (!frameDrag) return;
            frameMoved = true;
            var pt = e.GetPosition(canvas);
            var nx = Snap(Math.Max(0, pt.X - frameDragOff.X));
            var ny = Snap(Math.Max(0, pt.Y - frameDragOff.Y));

            Canvas.SetLeft(frameRect,    nx);                    Canvas.SetTop(frameRect,    ny);
            Canvas.SetLeft(headerBorder, nx);                    Canvas.SetTop(headerBorder, ny);
            Canvas.SetLeft(frameGrip,    nx + bf.Width - GripSz); Canvas.SetTop(frameGrip, ny + bf.Height - GripSz);

            foreach (var kv in captCardOff)
                if (_boardCards.TryGetValue(kv.Key, out var card))
                { Canvas.SetLeft(card, Snap(nx + kv.Value.X)); Canvas.SetTop(card, Snap(ny + kv.Value.Y)); }
            foreach (var kv in captTbOff)
                if (_boardTextBoxes.TryGetValue(kv.Key, out var tbEl))
                { Canvas.SetLeft(tbEl, Snap(nx + kv.Value.X)); Canvas.SetTop(tbEl, Snap(ny + kv.Value.Y)); }
            foreach (var kv in captPinOff)
                if (_boardPins.TryGetValue(kv.Key, out var pinEl))
                { Canvas.SetLeft(pinEl, Snap(nx + kv.Value.X)); Canvas.SetTop(pinEl, Snap(ny + kv.Value.Y)); }

            e.Handled = true;
        };
        headerBorder.MouseLeftButtonUp += (_, e) =>
        {
            if (!frameDrag) return;
            frameDrag = false;
            headerBorder.ReleaseMouseCapture();

            if (!frameMoved)
            {
                // Click without drag: select all contained entities
                _selectedIds.Clear();
                foreach (var kv in _boardData.Positions)
                    if (kv.Value.X >= bf.X && kv.Value.Y >= bf.Y && kv.Value.X <= bf.X + bf.Width && kv.Value.Y <= bf.Y + bf.Height)
                        _selectedIds.Add(kv.Key);
                RefreshSelectionVisuals();
                e.Handled = true;
                return;
            }

            bf.X = Canvas.GetLeft(frameRect);
            bf.Y = Canvas.GetTop(frameRect);

            foreach (var kv in captCardOff)
                if (_boardData.Positions.TryGetValue(kv.Key, out var bp) && _boardCards.TryGetValue(kv.Key, out var card))
                { bp.X = Canvas.GetLeft(card); bp.Y = Canvas.GetTop(card); }
            foreach (var kv in captTbOff)
            {
                var tbData = _boardData.TextBoxes.FirstOrDefault(t => t.Id == kv.Key);
                if (tbData != null && _boardTextBoxes.TryGetValue(kv.Key, out var tbEl))
                { tbData.X = Canvas.GetLeft(tbEl); tbData.Y = Canvas.GetTop(tbEl); }
            }
            foreach (var kv in captPinOff)
                if (_boardData.BoardPinPositions.TryGetValue(kv.Key, out var pp) && _boardPins.TryGetValue(kv.Key, out var pinEl))
                { pp.X = Canvas.GetLeft(pinEl); pp.Y = Canvas.GetTop(pinEl); }

            EntityBoardService.Save(_projFolder, _board.Id, _boardData);
            BuildBoardContent();
            e.Handled = true;
        };

        // ── Frame resize ──────────────────────────────────────────────────────
        bool   frameResize = false;
        double frX = 0, frY = 0, frW = 0, frH = 0;

        frameGrip.MouseLeftButtonDown += (_, e) =>
        {
            frameResize = true;
            var pt = e.GetPosition(canvas);
            frX = pt.X; frY = pt.Y;
            frW = bf.Width; frH = bf.Height;
            frameGrip.CaptureMouse(); e.Handled = true;
        };
        frameGrip.MouseMove += (_, e) =>
        {
            if (!frameResize) return;
            var pt = e.GetPosition(canvas);
            var nw = Snap(Math.Max(100, frW + (pt.X - frX)));
            var nh = Snap(Math.Max(60,  frH + (pt.Y - frY)));
            frameRect.Width    = nw; frameRect.Height = nh;
            headerBorder.Width = nw;
            Canvas.SetLeft(frameGrip, bf.X + nw - GripSz);
            Canvas.SetTop (frameGrip, bf.Y + nh - GripSz);
            e.Handled = true;
        };
        frameGrip.MouseLeftButtonUp += (_, e) =>
        {
            if (!frameResize) return;
            frameResize = false;
            frameGrip.ReleaseMouseCapture();
            bf.Width  = frameRect.Width;
            bf.Height = frameRect.Height;
            EntityBoardService.Save(_projFolder, _board.Id, _boardData);
            e.Handled = true;
        };

        // ── Context menu on header ────────────────────────────────────────────
        var frameCtx   = new ContextMenu();
        var editFrItem = new MenuItem { Header = Properties.Loc.S("Board_EditFrame") };
        editFrItem.Click += (_, _) =>
        {
            if (ShowFrameEditDialog(bf))
            {
                EntityBoardService.Save(_projFolder, _board.Id, _boardData);
                BuildBoardContent();
            }
        };
        var delFrItem  = new MenuItem { Header = Properties.Loc.S("Board_RemoveFrame") };
        delFrItem.Click += (_, _) =>
        {
            _boardData.Frames.Remove(bf);
            EntityBoardService.Save(_projFolder, _board.Id, _boardData);
            BuildBoardContent();
        };
        frameCtx.Items.Add(editFrItem);
        frameCtx.Items.Add(delFrItem);
        headerBorder.ContextMenu = frameCtx;
    }

    /// <summary>Dialog to set a frame's label and accent colour. Returns true on OK.</summary>
    private bool ShowFrameEditDialog(BoardFrame bf)
    {
        var dlg = new Window
        {
            Title                 = Properties.Loc.S("Board_EditFrameTitle"),
            SizeToContent         = SizeToContent.WidthAndHeight,
            MinWidth              = 280,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ResizeMode            = ResizeMode.NoResize
        };
        foreach (var rd in Resources.MergedDictionaries)
            dlg.Resources.MergedDictionaries.Add(rd);
        dlg.Background = TryFindResource("ContentBgBrush") as Brush ?? Brushes.White;

        var root = new StackPanel { Margin = new Thickness(16) };

        var lblLabel = new TextBlock { Text = Properties.Loc.S("Board_FrameLabel"), Margin = new Thickness(0, 0, 0, 4) };
        lblLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        var labelBox = new TextBox { Text = bf.Label, Padding = new Thickness(4), Margin = new Thickness(0, 0, 0, 12) };
        labelBox.SetResourceReference(TextBox.BackgroundProperty,   "ControlBgBrush");
        labelBox.SetResourceReference(TextBox.ForegroundProperty,   "ContentTextBrush");
        labelBox.SetResourceReference(TextBox.BorderBrushProperty,  "ControlBorderBrush");

        var lblColor = new TextBlock { Text = Properties.Loc.S("Board_FrameColorLbl"), Margin = new Thickness(0, 0, 0, 4) };
        lblColor.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");

        var colorPanel  = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
        string selColor = bf.Color;

        foreach (var hex in WorldEntitySchemas.FactionColorPalette)
        {
            var capHex = hex;
            Color c    = Colors.Gray;
            try { c = (Color)ColorConverter.ConvertFromString(hex)!; } catch { }
            bool isSel = string.Equals(hex, bf.Color, StringComparison.OrdinalIgnoreCase);
            var swatch = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(4), Margin = new Thickness(2),
                Background      = new SolidColorBrush(c),
                BorderThickness = isSel ? new Thickness(2) : new Thickness(0),
                BorderBrush     = Brushes.White,
                Cursor          = Cursors.Hand,
                ToolTip         = hex
            };
            swatch.MouseLeftButtonDown += (_, _) =>
            {
                selColor = capHex;
                foreach (Border b in colorPanel.Children) b.BorderThickness = new Thickness(0);
                swatch.BorderThickness = new Thickness(2);
            };
            colorPanel.Children.Add(swatch);
        }

        var btnRow    = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var okBtn     = new Button { Content = Properties.Loc.S("Btn_OK"),     Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancelBtn = new Button { Content = Properties.Loc.S("Btn_Cancel"), Padding = new Thickness(14, 4, 14, 4), IsCancel = true };
        foreach (var btn in new[] { okBtn, cancelBtn })
        {
            btn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
            btn.SetResourceReference(Button.ForegroundProperty, "ContentTextBrush");
        }
        btnRow.Children.Add(okBtn); btnRow.Children.Add(cancelBtn);

        bool result = false;
        okBtn.Click     += (_, _) => { bf.Label = labelBox.Text.Trim(); bf.Color = selColor; result = true; dlg.Close(); };
        cancelBtn.Click += (_, _) => dlg.Close();

        root.Children.Add(lblLabel); root.Children.Add(labelBox);
        root.Children.Add(lblColor); root.Children.Add(colorPanel);
        root.Children.Add(btnRow);
        dlg.Content = root;
        dlg.ShowDialog();
        return result;
    }

    // ── Board card ─────────────────────────────────────────────────────────

    private Border BuildBoardCard(WorldEntity entity, IReadOnlyList<WorldEntity> boardFactions)
    {
        const double DefaultCardW = 160;
        const double ThumbW = 52;

        bool isConnSrc = _boardConnSrcId == entity.Id;
        bool isFaction = entity.EntityType == "Faction";

        // Factions the current entity belongs to (by MemberIds)
        var memberFactions = isFaction
            ? []
            : boardFactions.Where(f => f.MemberIds.Contains(entity.Id)).ToList();

        Color? factionAccent = null;
        if (isFaction && !string.IsNullOrEmpty(entity.FactionColor))
        {
            try { factionAccent = (Color)ColorConverter.ConvertFromString(entity.FactionColor)!; } catch { }
        }
        else if (memberFactions.Count > 0 && !string.IsNullOrEmpty(memberFactions[0].FactionColor))
        {
            try { factionAccent = (Color)ColorConverter.ConvertFromString(memberFactions[0].FactionColor)!; } catch { }
        }

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

        // Thumbnail (portrait for Character, image for Location/Faction)
        string? thumbPath = null;
        if (entity.EntityType == "Character" && !string.IsNullOrWhiteSpace(entity.PortraitFileName))
            thumbPath = WorldEntityService.GetPortraitPath(_projFolder, entity.PortraitFileName);
        else if (!string.IsNullOrWhiteSpace(entity.ImageFileName))
            thumbPath = WorldEntityService.GetImagePath(_projFolder, entity.ImageFileName);

        if (thumbPath != null && System.IO.File.Exists(thumbPath))
        {
            var bmp = ThumbnailService.LoadThumb(thumbPath);
            if (bmp != null)
            {
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
        // (No left strip for non-faction entities — faction membership shown as dots below field rows)

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
            var vt = new TextBlock { Text = val, FontSize = 9, TextTrimming = TextTrimming.CharacterEllipsis };
            vt.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
            row.Children.Add(lbl); row.Children.Add(vt);
            textStack.Children.Add(row);
            shown++;
        }

        // ── Faction membership dots (non-faction entities) ─────────────────
        if (!isFaction && memberFactions.Count > 0)
        {
            var dotSep = new Rectangle { Height = 1, Margin = new Thickness(0, 4, 0, 3), Opacity = 0.18 };
            dotSep.SetResourceReference(Rectangle.FillProperty, "ControlBorderBrush");
            textStack.Children.Add(dotSep);
            var dotPanel = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var fac in memberFactions)
            {
                if (string.IsNullOrEmpty(fac.FactionColor)) continue;
                Color dc; try { dc = (Color)ColorConverter.ConvertFromString(fac.FactionColor)!; } catch { continue; }
                var dotGrid = new Grid { Width = 12, Height = 12, Margin = new Thickness(0, 0, 3, 2), ToolTip = fac.Name };
                var outer   = new Ellipse { Width = 12, Height = 12, Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromArgb(50, 0, 0, 0)), StrokeThickness = 0.5 };
                var inner   = new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(dc), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                dotGrid.Children.Add(outer); dotGrid.Children.Add(inner);
                dotPanel.Children.Add(dotGrid);
            }
            textStack.Children.Add(dotPanel);
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
            var newW = Snap(Math.Max(100, resizeStartW + (pt.X - resizeStartX)));
            var newH = Snap(Math.Max(60,  resizeStartH + (pt.Y - resizeStartY)));
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

        // Context menu: remove from board + edit
        var ctx = new ContextMenu();
        var removeItem = new MenuItem { Header = Properties.Loc.S("Board_RemoveFromBoard") };
        removeItem.Click += (_, _) => RemoveFromBoard(new[] { entity.Id });
        ctx.Items.Add(removeItem);
        var editItem = new MenuItem { Header = Properties.Loc.S("World_EditItem") };
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

    private List<WorldEntity> ShowEntityPickerDialog(List<WorldEntity> eligible, string title,
                                                      string? projFolder = null)
    {
        var selected = new List<WorldEntity>();
        var win = new Window
        {
            Title = title, Width = 420, Height = 560, MinHeight = 280,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this, ShowInTaskbar = false, ResizeMode = ResizeMode.CanResize
        };
        ApplyThemeToDialog(win);
        win.SetResourceReference(Window.BackgroundProperty, "ContentBgBrush");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // filters
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // list
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // buttons
        win.Content = root;

        // ── Filter bar ─────────────────────────────────────────────────────
        var filterStack = new StackPanel { Margin = new Thickness(10, 10, 10, 6) };
        Grid.SetRow(filterStack, 0);
        root.Children.Add(filterStack);

        // ── Dropdown helper: theme-bg + always black text (readable on any background) ──
        ComboBox MakePickerDropdown(string placeholder)
        {
            var cb = new ComboBox { Margin = new Thickness(0, 0, 0, 5), FontSize = 11,
                                    Foreground = new SolidColorBrush(Color.FromRgb(20, 20, 20)) };
            cb.SetResourceReference(ComboBox.BackgroundProperty,  "ControlBgBrush");
            cb.SetResourceReference(ComboBox.BorderBrushProperty, "ControlBorderBrush");
            cb.Items.Add(placeholder); cb.SelectedIndex = 0;
            return cb;
        }

        // Name search
        var searchBox = new TextBox
        {
            Margin = new Thickness(0, 0, 0, 5), FontSize = 12,
            Padding = new Thickness(6, 4, 6, 4), BorderThickness = new Thickness(1),
            ToolTip = "Filter by name"
        };
        searchBox.SetResourceReference(TextBox.BackgroundProperty,  "ControlBgBrush");
        searchBox.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        searchBox.SetResourceReference(TextBox.ForegroundProperty,  "ContentTextBrush");
        filterStack.Children.Add(searchBox);

        // ── Build faction lookup: characterId → faction names (via MemberIds) ──
        var charFactionNames = new Dictionary<string, List<string>>();
        if (projFolder != null)
        {
            foreach (var faction in WorldEntityService.List(projFolder, "Faction"))
                foreach (var memberId in faction.MemberIds)
                {
                    if (!charFactionNames.TryGetValue(memberId, out var list))
                        charFactionNames[memberId] = list = new List<string>();
                    list.Add(faction.Name);
                }
        }

        // ── Extract filter options from eligible entities ──────────────────
        bool hasChars = eligible.Any(e => e.EntityType == "Character");
        bool hasLocs  = eligible.Any(e => e.EntityType == "Location");
        bool hasFacs  = eligible.Any(e => e.EntityType == "Faction");
        bool hasLore  = eligible.Any(e => e.EntityType == "Lore");

        // Faction names (from MemberIds for chars, or entity name list for factions)
        var factionNamesForFilter = hasChars
            ? charFactionNames.Values.SelectMany(v => v).Distinct().OrderBy(v => v).ToList()
            : new List<string>();
        bool hasMissingFaction = hasChars && eligible.Any(e => e.EntityType == "Character"
            && (!charFactionNames.ContainsKey(e.Id) || charFactionNames[e.Id].Count == 0));

        List<string> UniqueFieldVals(string field) =>
            eligible.Select(e => e.Fields.TryGetValue(field, out var v) ? v : null)
                    .Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().OrderBy(v => v).ToList()!;
        bool HasMissingField(string field) =>
            eligible.Any(e => !e.Fields.TryGetValue(field, out var v) || string.IsNullOrWhiteSpace(v));

        var arcVals       = UniqueFieldVals("Arc");
        var alignVals     = UniqueFieldVals("Alignment");
        var typeVals      = UniqueFieldVals("Type");
        var categoryVals  = UniqueFieldVals("Category");
        var entityTypes   = eligible.Select(e => e.EntityType).Distinct().OrderBy(t => t).ToList();

        ComboBox? entityTypeCb = null;
        ComboBox? factionCb    = null;
        ComboBox? arcCb        = null;
        ComboBox? alignmentCb  = null;
        ComboBox? typeCb       = null;
        ComboBox? categoryCb   = null;

        if (entityTypes.Count > 1)
        {
            entityTypeCb = MakePickerDropdown(Properties.Loc.S("Board_AllTypes"));
            foreach (var t in entityTypes) entityTypeCb.Items.Add(t);
            filterStack.Children.Add(entityTypeCb);
        }
        if (factionNamesForFilter.Count > 0 || hasMissingFaction)
        {
            factionCb = MakePickerDropdown(Properties.Loc.S("World_AllFactions"));
            if (hasMissingFaction) factionCb.Items.Add(Properties.Loc.S("World_NoFaction"));
            foreach (var f in factionNamesForFilter) factionCb.Items.Add(f);
            filterStack.Children.Add(factionCb);
        }
        if (arcVals.Count > 0)
        {
            arcCb = MakePickerDropdown(Properties.Loc.S("World_AllArcs"));
            if (HasMissingField("Arc")) arcCb.Items.Add(Properties.Loc.S("World_NoArc"));
            foreach (var a in arcVals) arcCb.Items.Add(a);
            filterStack.Children.Add(arcCb);
        }
        if (alignVals.Count > 0)
        {
            alignmentCb = MakePickerDropdown(Properties.Loc.S("World_AllAlignments"));
            if (HasMissingField("Alignment")) alignmentCb.Items.Add(Properties.Loc.S("World_NoAlignment"));
            foreach (var al in alignVals) alignmentCb.Items.Add(al);
            filterStack.Children.Add(alignmentCb);
        }
        if (typeVals.Count > 0)
        {
            typeCb = MakePickerDropdown(Properties.Loc.S("Board_AllTypes"));
            if (HasMissingField("Type")) typeCb.Items.Add(Properties.Loc.S("Board_NoType"));
            foreach (var tv in typeVals) typeCb.Items.Add(tv);
            filterStack.Children.Add(typeCb);
        }
        if (categoryVals.Count > 0)
        {
            categoryCb = MakePickerDropdown(Properties.Loc.S("Board_AllCategories"));
            if (HasMissingField("Category")) categoryCb.Items.Add(Properties.Loc.S("Board_NoCategory"));
            foreach (var cv in categoryVals) categoryCb.Items.Add(cv);
            filterStack.Children.Add(categoryCb);
        }

        // ── Check list ─────────────────────────────────────────────────────
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(10, 0, 10, 4) };
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
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Text = entityTypes.Count > 1 ? $"{e.Name}  [{e.EntityType}]" : e.Name
                }
            };
            cb.SetResourceReference(CheckBox.ForegroundProperty, "ContentTextBrush");
            checks.Add((e, cb));
            checkList.Children.Add(cb);
        }

        // Apply all active filters
        void ApplyFilters()
        {
            var q      = searchBox.Text;
            var etSel  = entityTypeCb is { SelectedIndex: > 0 } ? entityTypeCb.SelectedItem as string : null;
            var facSel = factionCb    is { SelectedIndex: > 0 } ? factionCb.SelectedItem    as string : null;
            var arcSel = arcCb        is { SelectedIndex: > 0 } ? arcCb.SelectedItem        as string : null;
            var alnSel = alignmentCb  is { SelectedIndex: > 0 } ? alignmentCb.SelectedItem  as string : null;
            var typSel = typeCb       is { SelectedIndex: > 0 } ? typeCb.SelectedItem       as string : null;
            var catSel = categoryCb   is { SelectedIndex: > 0 } ? categoryCb.SelectedItem   as string : null;

            foreach (var (e, cb) in checks)
            {
                bool show = true;
                if (!string.IsNullOrWhiteSpace(q) && !e.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) show = false;
                if (etSel != null && e.EntityType != etSel) show = false;

                // Faction: for Characters use MemberIds map; for others not applicable
                if (facSel != null && e.EntityType == "Character")
                {
                    var myFacs = charFactionNames.TryGetValue(e.Id, out var fl) ? fl : null;
                    if (facSel == "(no faction)") { if (myFacs != null && myFacs.Count > 0) show = false; }
                    else if (myFacs == null || !myFacs.Contains(facSel)) show = false;
                }

                if (arcSel != null)
                {
                    e.Fields.TryGetValue("Arc", out var av);
                    if (arcSel == "(no arc)") { if (!string.IsNullOrWhiteSpace(av)) show = false; }
                    else if (av != arcSel) show = false;
                }
                if (alnSel != null)
                {
                    e.Fields.TryGetValue("Alignment", out var alv);
                    if (alnSel == "(no alignment)") { if (!string.IsNullOrWhiteSpace(alv)) show = false; }
                    else if (alv != alnSel) show = false;
                }
                if (typSel != null)
                {
                    e.Fields.TryGetValue("Type", out var tv);
                    if (typSel == "(no type)") { if (!string.IsNullOrWhiteSpace(tv)) show = false; }
                    else if (tv != typSel) show = false;
                }
                if (catSel != null)
                {
                    e.Fields.TryGetValue("Category", out var cv);
                    if (catSel == "(no category)") { if (!string.IsNullOrWhiteSpace(cv)) show = false; }
                    else if (cv != catSel) show = false;
                }
                cb.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        searchBox.TextChanged += (_, _) => ApplyFilters();
        if (entityTypeCb != null) entityTypeCb.SelectionChanged += (_, _) => ApplyFilters();
        if (factionCb    != null) factionCb.SelectionChanged    += (_, _) => ApplyFilters();
        if (arcCb        != null) arcCb.SelectionChanged        += (_, _) => ApplyFilters();
        if (alignmentCb  != null) alignmentCb.SelectionChanged  += (_, _) => ApplyFilters();
        if (typeCb       != null) typeCb.SelectionChanged       += (_, _) => ApplyFilters();
        if (categoryCb   != null) categoryCb.SelectionChanged   += (_, _) => ApplyFilters();

        // ── Button row ─────────────────────────────────────────────────────
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(10, 0, 10, 10)
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

        void UpdateCount() { var n = checks.Count(x => x.cb.IsChecked == true); countLabel.Text = n > 0 ? $"{n} selected" : ""; }
        foreach (var (_, cb) in checks) { cb.Checked += (_, _) => UpdateCount(); cb.Unchecked += (_, _) => UpdateCount(); }

        var cancelBtn = MakeBtn(Properties.Loc.S("Btn_Cancel"), false); cancelBtn.Padding = new Thickness(12, 5, 12, 5);
        cancelBtn.Click += (_, _) => win.DialogResult = false;
        btnRow.Children.Add(cancelBtn);

        var addBtn = MakeBtn(Properties.Loc.S("Board_AddToBoard"), true);
        addBtn.Padding = new Thickness(12, 5, 12, 5); addBtn.Margin = new Thickness(8, 0, 0, 0);
        addBtn.Click += (_, _) => win.DialogResult = true;
        btnRow.Children.Add(addBtn);

        searchBox.Focus();
        if (win.ShowDialog() == true)
            selected = checks.Where(x => x.cb.IsChecked == true).Select(x => x.entity).ToList();

        return selected;
    }

    // ── Text-box helpers ───────────────────────────────────────────────────

    private static Brush TbBrush(string hex)
    {
        try { var c = (Color)ColorConverter.ConvertFromString(hex); return new SolidColorBrush(c); }
        catch { return Brushes.Transparent; }
    }

    private void RenderBoardTextBox(Canvas canvas, BoardTextBox tb)
    {
        var halign = tb.HAlign switch
        {
            "Center"  => TextAlignment.Center,
            "Right"   => TextAlignment.Right,
            "Justify" => TextAlignment.Justify,
            _         => TextAlignment.Left
        };
        var tblock = new TextBlock
        {
            Text            = tb.Text,
            FontFamily      = new FontFamily(tb.FontFamily),
            FontSize        = Math.Max(6, tb.FontSize),
            FontWeight      = tb.Bold   ? FontWeights.Bold   : FontWeights.Normal,
            FontStyle       = tb.Italic ? FontStyles.Italic  : FontStyles.Normal,
            Foreground      = TbBrush(tb.TextColor),
            TextWrapping    = TextWrapping.Wrap,
            TextAlignment   = halign,
            VerticalAlignment = tb.VAlign switch
            {
                "Center" => VerticalAlignment.Center,
                "Bottom" => VerticalAlignment.Bottom,
                _        => VerticalAlignment.Top
            }
        };

        DoubleCollection? tbDash = tb.FrameStyle switch
        {
            "Dashed" => [6, 3],
            "Dotted" => [1, 3],
            _        => null
        };

        var border = new Border
        {
            Width           = Math.Max(20, tb.Width),
            Height          = Math.Max(10, tb.Height),
            Background      = TbBrush(tb.BgColor),
            BorderThickness = new Thickness(tb.FrameThick),
            Child           = tblock,
            Cursor          = Cursors.SizeAll
        };

        if (tb.FrameThick > 0 && tb.FrameStyle != "None")
        {
            if (tbDash != null)
            {
                // WPF Border can't do dashed via XAML easily; use a DrawingBrush overlay trick
                // Instead, just apply a solid border and let users know it's the style
                border.BorderBrush = TbBrush(tb.FrameColor);
            }
            else
                border.BorderBrush = TbBrush(tb.FrameColor);
        }

        Canvas.SetLeft(border, tb.X); Canvas.SetTop(border, tb.Y);
        Panel.SetZIndex(border, 2);
        canvas.Children.Add(border);
        _boardTextBoxes[tb.Id] = border;

        // ── Drag ──
        bool isTbDragging = false; Point tbOffset = default;
        var capturedTb = tb;
        border.MouseLeftButtonDown += (_, e) =>
        {
            if (_boardConnectMode) { e.Handled = true; return; }
            if (e.ClickCount >= 2)
            {
                if (ShowTextBoxStyleDialog(capturedTb))
                    EntityBoardService.Save(_projFolder, _board.Id, _boardData);
                BuildBoardContent();
                e.Handled = true;
                return;
            }
            isTbDragging = true; tbOffset = e.GetPosition(border);
            border.CaptureMouse(); e.Handled = true;
        };
        border.MouseMove += (_, e) =>
        {
            if (!isTbDragging) return;
            var p = e.GetPosition(canvas);
            capturedTb.X = Snap(Math.Max(0, p.X - tbOffset.X));
            capturedTb.Y = Snap(Math.Max(0, p.Y - tbOffset.Y));
            Canvas.SetLeft(border, capturedTb.X); Canvas.SetTop(border, capturedTb.Y);
            e.Handled = true;
        };
        border.MouseLeftButtonUp += (_, e) =>
        {
            if (!isTbDragging) return; isTbDragging = false; border.ReleaseMouseCapture();
            EntityBoardService.Save(_projFolder, _board.Id, _boardData); e.Handled = true;
        };

        // ── Resize grip (bottom-right corner) ──
        const double GripSz = 10;
        var grip = new System.Windows.Shapes.Rectangle
        {
            Width = GripSz, Height = GripSz, Fill = TbBrush(tb.FrameColor.Length > 0 ? tb.FrameColor : "#80808080"),
            Cursor = Cursors.SizeNWSE, Opacity = 0.6
        };
        Panel.SetZIndex(grip, 3);
        Canvas.SetLeft(grip, tb.X + tb.Width - GripSz); Canvas.SetTop(grip, tb.Y + tb.Height - GripSz);
        canvas.Children.Add(grip);

        bool isGripDrag = false; Point gripStart = default; double gripW0 = 0, gripH0 = 0;
        grip.MouseLeftButtonDown += (_, e) =>
        {
            isGripDrag = true; gripStart = e.GetPosition(canvas); gripW0 = capturedTb.Width; gripH0 = capturedTb.Height;
            grip.CaptureMouse(); e.Handled = true;
        };
        grip.MouseMove += (_, e) =>
        {
            if (!isGripDrag) return;
            var p = e.GetPosition(canvas);
            capturedTb.Width  = Snap(Math.Max(40, gripW0 + (p.X - gripStart.X)));
            capturedTb.Height = Snap(Math.Max(20, gripH0 + (p.Y - gripStart.Y)));
            border.Width = capturedTb.Width; border.Height = capturedTb.Height;
            Canvas.SetLeft(grip, capturedTb.X + capturedTb.Width - GripSz);
            Canvas.SetTop (grip, capturedTb.Y + capturedTb.Height - GripSz);
            e.Handled = true;
        };
        grip.MouseLeftButtonUp += (_, e) =>
        {
            if (!isGripDrag) return; isGripDrag = false; grip.ReleaseMouseCapture();
            EntityBoardService.Save(_projFolder, _board.Id, _boardData); e.Handled = true;
        };

        // ── Right-click → edit/delete ──
        border.MouseRightButtonDown += (_, e) =>
        {
            var mctx = new ContextMenu();
            var editTb = new MenuItem { Header = Properties.Loc.S("Board_EditTextBox") };
            editTb.Click += (_, _) =>
            {
                if (ShowTextBoxStyleDialog(capturedTb))
                    EntityBoardService.Save(_projFolder, _board.Id, _boardData);
                BuildBoardContent();
            };
            var delTb = new MenuItem { Header = Properties.Loc.S("Board_DeleteTextBox") };
            delTb.Click += (_, _) =>
            {
                _boardData.TextBoxes.Remove(capturedTb);
                EntityBoardService.Save(_projFolder, _board.Id, _boardData);
                BuildBoardContent();
            };
            mctx.Items.Add(editTb); mctx.Items.Add(delTb);
            mctx.IsOpen = true; e.Handled = true;
        };
    }

    private static readonly string[] TbFonts =
    [
        "Segoe UI", "Arial", "Times New Roman", "Courier New", "Georgia",
        "Verdana", "Trebuchet MS", "Comic Sans MS", "Impact", "Palatino Linotype"
    ];

    private static readonly string[] TbSwatches =
    [
        "#212121","#FFFFFF","#F44336","#E91E63","#9C27B0","#3F51B5",
        "#2196F3","#00BCD4","#4CAF50","#FFEB3B","#FF9800","#795548",
        "#607D8B","#000000","#FF5722","#8BC34A","#00BFFFCC","#80808080"
    ];

    /// <returns>true if user confirmed; false if cancelled.</returns>
    private bool ShowTextBoxStyleDialog(BoardTextBox tb)
    {
        var dlg = new Window
        {
            Title = Properties.Loc.S("Board_TextBoxStyleTitle"), Width = 520, Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            ResizeMode = ResizeMode.CanResize, WindowStyle = WindowStyle.ToolWindow
        };
        foreach (var d in Resources.MergedDictionaries) dlg.Resources.MergedDictionaries.Add(d);
        dlg.Background = (Brush)(TryFindResource("ContentBgBrush") ?? SystemColors.WindowBrush);
        dlg.SourceInitialized += (_, _) => ParticipantsWindow.TryApplyTitleBarTo(dlg);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var sp = new StackPanel { Margin = new Thickness(16) };
        scroll.Content = sp;
        dlg.Content = scroll;

        // ── Text content ──
        sp.Children.Add(MakeDlgLabel(Properties.Loc.S("Board_TbText")));
        var textEdit = new TextBox
        {
            Text = tb.Text, AcceptsReturn = true, MinLines = 3, MaxLines = 8,
            TextWrapping = TextWrapping.Wrap, Padding = new Thickness(6, 4, 6, 4),
            BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 10)
        };
        textEdit.SetResourceReference(TextBox.BackgroundProperty, "ControlBgBrush");
        textEdit.SetResourceReference(TextBox.ForegroundProperty, "ContentTextBrush");
        textEdit.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        sp.Children.Add(textEdit);

        // ── Font family ──
        sp.Children.Add(MakeDlgLabel(Properties.Loc.S("Board_TbFont")));
        var fontRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,10) };
        var fontCombo = new ComboBox { Width = 180, ItemsSource = TbFonts, SelectedItem = tb.FontFamily };
        fontCombo.SetResourceReference(ComboBox.BackgroundProperty, "ControlBgBrush");
        fontCombo.SetResourceReference(ComboBox.ForegroundProperty, "ContentTextBrush");
        var sizeBox = new TextBox
        {
            Text = ((int)tb.FontSize).ToString(), Width = 52, Padding = new Thickness(4,4,4,4),
            Margin = new Thickness(8,0,0,0), TextAlignment = TextAlignment.Center, BorderThickness = new Thickness(1)
        };
        sizeBox.SetResourceReference(TextBox.BackgroundProperty, "ControlBgBrush");
        sizeBox.SetResourceReference(TextBox.ForegroundProperty, "ContentTextBrush");
        sizeBox.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        var boldChk   = new CheckBox { Content = Properties.Loc.S("Board_TbBold"),   IsChecked = tb.Bold,   Margin = new Thickness(8,0,0,0), VerticalAlignment = VerticalAlignment.Center };
        var italicChk = new CheckBox { Content = Properties.Loc.S("Board_TbItalic"), IsChecked = tb.Italic, Margin = new Thickness(8,0,0,0), VerticalAlignment = VerticalAlignment.Center };
        boldChk.SetResourceReference(CheckBox.ForegroundProperty, "ContentTextBrush");
        italicChk.SetResourceReference(CheckBox.ForegroundProperty, "ContentTextBrush");
        fontRow.Children.Add(fontCombo); fontRow.Children.Add(sizeBox);
        fontRow.Children.Add(boldChk); fontRow.Children.Add(italicChk);
        sp.Children.Add(fontRow);

        // ── Text color ──
        sp.Children.Add(MakeDlgLabel(Properties.Loc.S("Board_TbTextColor")));
        string textColor = tb.TextColor;
        var (tcRow, _) = MakeSwatchRow(TbSwatches, textColor, c => textColor = c);
        sp.Children.Add(tcRow);

        // ── Alignment ──
        sp.Children.Add(MakeDlgLabel(Properties.Loc.S("Board_TbAlignment")));
        var alignRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,10) };
        string hAlign = tb.HAlign, vAlign = tb.VAlign;
        foreach (var ha in new[] { "Left","Center","Right","Justify" })
        {
            var capturedHa = ha;
            var btn = new Button
            {
                Content = ha switch { "Left"=>"⬅","Center"=>"⬆⬇","Right"=>"➡","Justify"=>"☰",_=>ha },
                Width=36, Height=28, ToolTip = ha,
                Margin = new Thickness(0,0,4,0),
                Tag = ha
            };
            btn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
            btn.SetResourceReference(Button.ForegroundProperty, "ContentTextBrush");
            btn.Click += (_, _) => { hAlign = capturedHa; foreach (Button b in alignRow.Children) b.FontWeight = (string?)b.Tag == hAlign ? FontWeights.Bold : FontWeights.Normal; };
            if (ha == hAlign) btn.FontWeight = FontWeights.Bold;
            alignRow.Children.Add(btn);
        }
        alignRow.Children.Add(new TextBlock { Text = "  V:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8,0,4,0) });
        foreach (var va in new[] { "Top","Center","Bottom" })
        {
            var capturedVa = va;
            var btn = new Button
            {
                Content = va, Width = 54, Height = 28,
                Margin = new Thickness(0,0,4,0), Tag = va
            };
            btn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
            btn.SetResourceReference(Button.ForegroundProperty, "ContentTextBrush");
            btn.Click += (_, _) => { vAlign = capturedVa; /* visual feedback would need refresh */ };
            if (va == vAlign) btn.FontWeight = FontWeights.Bold;
            alignRow.Children.Add(btn);
        }
        sp.Children.Add(alignRow);

        // ── Background color ──
        sp.Children.Add(MakeDlgLabel(Properties.Loc.S("Board_TbBackground")));
        string bgColor = tb.BgColor;
        var (bgRow, _) = MakeSwatchRow(
            ["#00000000","#FFFFFFFF","#FFFFFFE0","#FFE0F0FF","#FFE8FFE8","#FFFFF0E0","#FF2B2B2B","#FF1E1E2E"],
            bgColor, c => bgColor = c);
        sp.Children.Add(bgRow);

        // ── Frame/border ──
        sp.Children.Add(MakeDlgLabel(Properties.Loc.S("Board_TbFrame")));
        var frameRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,10) };
        string frameStyle = tb.FrameStyle;
        var styleCombo = new ComboBox { Width = 90, ItemsSource = new[]{"None","Solid","Dashed","Dotted"}, SelectedItem = tb.FrameStyle };
        styleCombo.SetResourceReference(ComboBox.BackgroundProperty, "ControlBgBrush");
        styleCombo.SetResourceReference(ComboBox.ForegroundProperty, "ContentTextBrush");
        styleCombo.SelectionChanged += (_, _) => { if (styleCombo.SelectedItem is string s) frameStyle = s; };
        var thickLbl = new TextBlock { Text = "  Thickness:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8,0,4,0) };
        thickLbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        var thickBox = new TextBox
        {
            Text = tb.FrameThick.ToString("F0"), Width = 44, TextAlignment = TextAlignment.Center,
            Padding = new Thickness(4,4,4,4), BorderThickness = new Thickness(1)
        };
        thickBox.SetResourceReference(TextBox.BackgroundProperty, "ControlBgBrush");
        thickBox.SetResourceReference(TextBox.ForegroundProperty, "ContentTextBrush");
        thickBox.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        frameRow.Children.Add(styleCombo); frameRow.Children.Add(thickLbl); frameRow.Children.Add(thickBox);
        sp.Children.Add(frameRow);

        sp.Children.Add(MakeDlgLabel(Properties.Loc.S("Board_TbFrameColor")));
        string frameColor = tb.FrameColor;
        var (fcRow, _) = MakeSwatchRow(TbSwatches, frameColor, c => frameColor = c);
        sp.Children.Add(fcRow);

        // ── OK / Cancel ──
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,12,0,0) };
        var ok = new Button { Content = Properties.Loc.S("Btn_OK"), Width = 80, IsDefault = true, Padding = new Thickness(0,6,0,6) };
        var cancel = new Button { Content = Properties.Loc.S("Btn_Cancel"), Width = 80, IsCancel = true, Padding = new Thickness(0,6,0,6), Margin = new Thickness(8,0,0,0) };
        ok.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        ok.SetResourceReference(Button.ForegroundProperty, "ContentTextBrush");
        cancel.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        cancel.SetResourceReference(Button.ForegroundProperty, "ContentTextBrush");
        ok.Click += (_, _) => dlg.DialogResult = true;
        btnRow.Children.Add(ok); btnRow.Children.Add(cancel);
        sp.Children.Add(btnRow);

        if (dlg.ShowDialog() != true) return false;

        tb.Text       = textEdit.Text;
        tb.FontFamily = (string?)fontCombo.SelectedItem ?? "Segoe UI";
        tb.FontSize   = double.TryParse(sizeBox.Text, out var fs) ? Math.Max(6, fs) : tb.FontSize;
        tb.Bold       = boldChk.IsChecked == true;
        tb.Italic     = italicChk.IsChecked == true;
        tb.TextColor  = textColor;
        tb.BgColor    = bgColor;
        tb.HAlign     = hAlign;
        tb.VAlign     = vAlign;
        tb.FrameStyle = frameStyle;
        tb.FrameThick = double.TryParse(thickBox.Text, out var ft) ? Math.Max(0, ft) : tb.FrameThick;
        tb.FrameColor = frameColor;
        return true;
    }

    private static TextBlock MakeDlgLabel(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 11, Margin = new Thickness(0,6,0,3), FontWeight = FontWeights.SemiBold };
        t.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        return t;
    }

    private static (StackPanel Row, string[] Colors) MakeSwatchRow(
        IEnumerable<string> swatches, string current, Action<string> onPick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,10) };
        foreach (var hex in swatches)
        {
            var capturedHex = hex;
            Color col; try { col = (Color)ColorConverter.ConvertFromString(hex); } catch { col = Colors.Gray; }
            var swatch = new Border
            {
                Width = 22, Height = 22, Margin = new Thickness(0,0,3,0), Cursor = Cursors.Hand,
                Background = new SolidColorBrush(col),
                BorderThickness = new Thickness(hex == current ? 2 : 1),
                CornerRadius = new CornerRadius(3)
            };
            swatch.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
            if (hex == current)
            {
                swatch.BorderBrush = Brushes.White;
                swatch.Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = Colors.Black, BlurRadius = 4, ShadowDepth = 0, Opacity = 0.7 };
            }
            swatch.MouseLeftButtonDown += (_, _) =>
            {
                onPick(capturedHex);
                foreach (Border b in row.Children)
                {
                    bool sel = (string?)b.Tag == capturedHex;
                    b.BorderThickness = new Thickness(sel ? 2 : 1);
                    b.BorderBrush = sel ? Brushes.White : (Brush)b.GetValue(Border.BorderBrushProperty);
                }
            };
            swatch.Tag = hex;
            row.Children.Add(swatch);
        }
        return (row, swatches.ToArray());
    }

    // ── Legend ─────────────────────────────────────────────────────────────

    private Border BuildBoardLegend()
    {
        // Ensure there is at least one preset
        if (_boardData.LinePresets.Count == 0)
            _boardData.LinePresets.Add(new BoardLinePreset { Name = "Standard" });

        bool visible = _boardData.LegendVisible;
        var panel = new Border
        {
            CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8), MinWidth = 160, MaxWidth = 240,
            Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.18, Color = Colors.Black }
        };
        panel.SetResourceReference(Border.BackgroundProperty,  "ControlBgBrush");
        panel.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");

        var content = new StackPanel();
        panel.Child = content;

        // ── Header row ─────────────────────────────────────────────────────
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 0) };
        content.Children.Add(headerRow);

        var title = new TextBlock { Text = Properties.Loc.S("Board_Legend"), FontSize = 11, FontWeight = FontWeights.SemiBold,
                                    VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
                                    ToolTip = "Right-click to edit line presets" };
        title.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        headerRow.Children.Add(title);

        var toggleBtn = new Button { Content = visible ? "▲" : "▼", FontSize = 9,
            Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(6, 0, 0, 0),
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        toggleBtn.SetResourceReference(Button.ForegroundProperty, "SidebarDimBrush");
        headerRow.Children.Add(toggleBtn);

        // ── Context menu on the legend panel ───────────────────────────────
        var legendCtx = new ContextMenu();
        var editPresetsItem = new MenuItem { Header = Properties.Loc.S("Board_EditLinePresets") };
        editPresetsItem.Click += (_, _) => ShowLegendEditorDialog();
        legendCtx.Items.Add(editPresetsItem);
        panel.ContextMenu = legendCtx;

        // ── Preset list ────────────────────────────────────────────────────
        var presetsPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 0),
                                            Visibility = visible ? Visibility.Visible : Visibility.Collapsed };
        content.Children.Add(presetsPanel);

        void RenderPresets()
        {
            presetsPanel.Children.Clear();
            foreach (var preset in _boardData.LinePresets)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal,
                                           Margin = new Thickness(0, 3, 0, 3), Cursor = Cursors.Hand };

                // Mini line preview
                var prev = new Canvas { Width = 46, Height = 14, Margin = new Thickness(0, 0, 6, 0) };
                var pln  = new Line { X1 = 0, Y1 = 7, X2 = 36, Y2 = 7,
                    StrokeThickness = Math.Min(preset.Thickness, 2.5), Opacity = 0.9 };
                var da = GetDashArray(preset.Style);
                if (da is not null) { pln.StrokeDashArray = da; pln.StrokeDashCap = PenLineCap.Round; }
                pln.Stroke = GetLineBrush(preset.Color);
                prev.Children.Add(pln);
                if (preset.HasArrow)
                {
                    var tip  = new Polygon { Fill = GetLineBrush(preset.Color), Opacity = 0.9,
                        Points = new PointCollection { new(46,7), new(38,3), new(38,11) } };
                    prev.Children.Add(tip);
                }
                if (IsDoubleLine(preset.Style))
                {
                    var pln2 = new Line { X1 = 0, Y1 = 10, X2 = 36, Y2 = 10,
                        StrokeThickness = Math.Min(preset.Thickness, 2.5), Opacity = 0.9,
                        StrokeDashArray = da };
                    pln2.Stroke = GetLineBrush(preset.Color);
                    prev.Children.Add(pln2);
                }
                row.Children.Add(prev);

                var lbl = new TextBlock { Text = preset.Name, FontSize = 10, MaxWidth = 140,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center };
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
                row.Children.Add(lbl);

                presetsPanel.Children.Add(row);
            }
        }
        RenderPresets();

        toggleBtn.Click += (_, _) =>
        {
            _boardData.LegendVisible = !_boardData.LegendVisible;
            EntityBoardService.Save(_projFolder, _board.Id, _boardData);
            BuildBoardContent();
        };

        return panel;
    }

    // ── Legend preset editor ───────────────────────────────────────────────

    private void ShowLegendEditorDialog()
    {
        var win = new Window
        {
            Title = Properties.Loc.S("Board_EditLinePresetsTitle"), Width = 520, Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this, ShowInTaskbar = false, ResizeMode = ResizeMode.CanResize
        };
        ApplyThemeToDialog(win);
        win.SetResourceReference(Window.BackgroundProperty, "ContentBgBrush");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        win.Content = root;

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(14, 10, 14, 10) };
        Grid.SetRow(scroll, 0);
        root.Children.Add(scroll);

        var listPanel = new StackPanel();
        scroll.Content = listPanel;

        void RebuildList()
        {
            listPanel.Children.Clear();
            foreach (var preset in _boardData.LinePresets.ToList())
            {
                var capturedPreset = preset;
                var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // Name + mini preview
                var nameStack = new StackPanel { Orientation = Orientation.Horizontal };
                var previewLn = new Line { X1 = 0, Y1 = 8, X2 = 40, Y2 = 8,
                    StrokeThickness = Math.Min(preset.Thickness, 2), Stroke = GetLineBrush(preset.Color) };
                var da = GetDashArray(preset.Style);
                if (da is not null) previewLn.StrokeDashArray = da;
                var previewC = new Canvas { Width = 44, Height = 16, VerticalAlignment = VerticalAlignment.Center };
                previewC.Children.Add(previewLn);
                nameStack.Children.Add(previewC);
                var nameTb = new TextBlock { Text = preset.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
                nameTb.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
                nameStack.Children.Add(nameTb);
                Grid.SetColumn(nameStack, 0);
                row.Children.Add(nameStack);

                // Edit button
                var editBtn = MakeBtn("✏", false); editBtn.Padding = new Thickness(8, 3, 8, 3);
                Grid.SetColumn(editBtn, 1);
                editBtn.Click += (_, _) =>
                {
                    var r = ShowPresetEditorDialog(capturedPreset);
                    if (r != null)
                    {
                        capturedPreset.Name = r.Name; capturedPreset.Style = r.Style;
                        capturedPreset.Color = r.Color; capturedPreset.Thickness = r.Thickness;
                        capturedPreset.HasArrow = r.HasArrow;
                        EntityBoardService.Save(_projFolder, _board.Id, _boardData);
                        RebuildList(); BuildBoardContent();
                    }
                };
                row.Children.Add(editBtn);

                // Delete button
                var delBtn = MakeBtn("✕", false); delBtn.Padding = new Thickness(8, 3, 8, 3);
                delBtn.Margin = new Thickness(4, 0, 0, 0);
                Grid.SetColumn(delBtn, 2);
                delBtn.Click += (_, _) =>
                {
                    _boardData.LinePresets.Remove(capturedPreset);
                    EntityBoardService.Save(_projFolder, _board.Id, _boardData);
                    RebuildList(); BuildBoardContent();
                };
                row.Children.Add(delBtn);
                listPanel.Children.Add(row);
            }
        }
        RebuildList();

        // Bottom row: Add + Close
        var botRow = new StackPanel { Orientation = Orientation.Horizontal,
                                      HorizontalAlignment = HorizontalAlignment.Right,
                                      Margin = new Thickness(14, 0, 14, 12) };
        Grid.SetRow(botRow, 1);
        root.Children.Add(botRow);

        var addBtn = MakeBtn(Properties.Loc.S("Board_AddPreset"), true); addBtn.Padding = new Thickness(12, 6, 12, 6);
        addBtn.Click += (_, _) =>
        {
            var newP = new BoardLinePreset { Name = "New Line" };
            var r = ShowPresetEditorDialog(newP);
            if (r != null)
            {
                newP.Name = r.Name; newP.Style = r.Style; newP.Color = r.Color;
                newP.Thickness = r.Thickness; newP.HasArrow = r.HasArrow;
                _boardData.LinePresets.Add(newP);
                EntityBoardService.Save(_projFolder, _board.Id, _boardData);
                RebuildList(); BuildBoardContent();
            }
        };
        botRow.Children.Add(addBtn);

        var closeBtn = MakeBtn(Properties.Loc.S("Btn_Close"), false); closeBtn.Padding = new Thickness(12, 6, 12, 6);
        closeBtn.Margin = new Thickness(8, 0, 0, 0);
        closeBtn.Click += (_, _) => win.Close();
        botRow.Children.Add(closeBtn);

        win.Show();
    }

    private record PresetEditorResult(string Name, BoardLineStyle Style, string Color, double Thickness, bool HasArrow);

    private PresetEditorResult? ShowPresetEditorDialog(BoardLinePreset preset)
    {
        var win = new Window
        {
            Title = Properties.Loc.S("Board_EditPresetTitle"), Width = 440, Height = 520, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this, ShowInTaskbar = false
        };
        ApplyThemeToDialog(win);
        win.SetResourceReference(Window.BackgroundProperty, "ContentBgBrush");

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(20, 16, 20, 16) };
        win.Content = scroll;
        var root = new StackPanel();
        scroll.Content = root;

        void Lbl(string t) { var l = new TextBlock { Text = t, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) }; l.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush"); root.Children.Add(l); }

        Lbl(Properties.Loc.S("Board_PresetName"));
        var nameBox = new TextBox { Text = preset.Name, FontSize = 13, Padding = new Thickness(8, 5, 8, 5), BorderThickness = new Thickness(1) };
        nameBox.SetResourceReference(TextBox.BackgroundProperty,  "ControlBgBrush");
        nameBox.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        nameBox.SetResourceReference(TextBox.ForegroundProperty,  "ContentTextBrush");
        root.Children.Add(nameBox);

        Lbl(Properties.Loc.S("Board_LineStyle"));
        var styleItems = new (string D, BoardLineStyle V)[]
        {
            ("━━━━  Solid", BoardLineStyle.Solid), ("· · ·  Dotted", BoardLineStyle.Dotted),
            ("─ ─ ─  Dashed", BoardLineStyle.Dashed), ("·─·  Dot-dash", BoardLineStyle.DotDash),
            ("══  Double solid", BoardLineStyle.DoubleSolid), ("⁚ ⁚  Dbl dotted", BoardLineStyle.DoubleDotted),
            ("═ ═  Dbl dashed", BoardLineStyle.DoubleDashed), ("·═·  Dbl dot-dash", BoardLineStyle.DoubleDotDash)
        };
        var styleCombo = new ComboBox { FontSize = 12 };
        for (int i = 0; i < styleItems.Length; i++) { styleCombo.Items.Add(styleItems[i].D); if (styleItems[i].V == preset.Style) styleCombo.SelectedIndex = i; }
        if (styleCombo.SelectedIndex < 0) styleCombo.SelectedIndex = 0;
        root.Children.Add(styleCombo);

        Lbl(Properties.Loc.S("Board_Colour"));
        string selColor = preset.Color.StartsWith('#') ? preset.Color : "#2196F3";
        var swatches = new List<Border>();
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        root.Children.Add(wrap);
        foreach (var hex in WorldEntitySchemas.FactionColorPalette)
        {
            var ch = hex;
            Color c; try { c = (Color)ColorConverter.ConvertFromString(hex)!; } catch { c = Colors.Gray; }
            bool isSel = string.Equals(hex, selColor, StringComparison.OrdinalIgnoreCase);
            var sw = new Border { Width = 24, Height = 24, Margin = new Thickness(0, 0, 4, 4),
                CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(c),
                BorderThickness = new Thickness(isSel ? 2.5 : 0),
                BorderBrush = isSel ? Brushes.White : Brushes.Transparent,
                Cursor = Cursors.Hand, Tag = hex };
            sw.MouseLeftButtonDown += (_, _) => { selColor = ch; foreach (var s in swatches) { bool b = string.Equals(s.Tag?.ToString(), ch, StringComparison.OrdinalIgnoreCase); s.BorderThickness = new Thickness(b ? 2.5 : 0); s.BorderBrush = b ? Brushes.White : Brushes.Transparent; } };
            swatches.Add(sw); wrap.Children.Add(sw);
        }

        Lbl(Properties.Loc.S("Board_Thickness"));
        var thkRow = new StackPanel { Orientation = Orientation.Horizontal };
        root.Children.Add(thkRow);
        var thkSlider = new Slider { Minimum = 1, Maximum = 10, Value = preset.Thickness, Width = 180, IsSnapToTickEnabled = true, TickFrequency = 0.5, VerticalAlignment = VerticalAlignment.Center };
        var thkLbl = new TextBlock { Text = $"{preset.Thickness:F1} px", Width = 44, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        thkLbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        thkSlider.ValueChanged += (_, e) => thkLbl.Text = $"{e.NewValue:F1} px";
        thkRow.Children.Add(thkSlider); thkRow.Children.Add(thkLbl);

        Lbl(Properties.Loc.S("Board_ArrowLbl"));
        var arrowCk = new CheckBox { Content = Properties.Loc.S("Board_DrawArrow"), IsChecked = preset.HasArrow };
        arrowCk.SetResourceReference(CheckBox.ForegroundProperty, "ContentTextBrush");
        root.Children.Add(arrowCk);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        root.Children.Add(btnRow);
        var cancelBtn = MakeBtn(Properties.Loc.S("Btn_Cancel"), false); cancelBtn.Padding = new Thickness(12, 6, 12, 6); cancelBtn.Click += (_, _) => win.DialogResult = false;
        var okBtn     = MakeBtn(Properties.Loc.S("Btn_OK"), true);      okBtn.Padding = new Thickness(12, 6, 12, 6); okBtn.Margin = new Thickness(8, 0, 0, 0); okBtn.Click += (_, _) => win.DialogResult = true;
        btnRow.Children.Add(cancelBtn); btnRow.Children.Add(okBtn);
        nameBox.Focus(); nameBox.SelectAll();

        if (win.ShowDialog() != true) return null;
        var style = styleItems[Math.Max(0, Math.Min(styleCombo.SelectedIndex, styleItems.Length - 1))].V;
        return new PresetEditorResult(nameBox.Text.Trim(), style, selColor, thkSlider.Value, arrowCk.IsChecked == true);
    }

    // ── Relation rendering ─────────────────────────────────────────────────

    // ── Relation multi-segment rendering ──────────────────────────────────

    private void RenderRelationVisuals(Canvas canvas, string relId,
        double x1, double y1, double x2, double y2, BoardRelation rel)
    {
        bool       isDouble  = IsDoubleLine(rel.LineStyle);
        var        dashArray = GetDashArray(rel.LineStyle);
        double     thickness = Math.Max(1, rel.Thickness);
        PenLineCap cap       = rel.LineStyle is BoardLineStyle.Dotted or BoardLineStyle.DoubleDotted
                               ? PenLineCap.Round : PenLineCap.Flat;
        var        brush     = GetLineBrush(rel.LineColor);

        // Compute card-border exit/entry points (use resolved WpPos for accurate aim)
        (double fax, double fay) = rel.Waypoints.Count > 0 ? WpPos(rel.Waypoints[0])  : (x2, y2);
        (double lax, double lay) = rel.Waypoints.Count > 0 ? WpPos(rel.Waypoints[^1]) : (x1, y1);
        (x1, y1) = StoredBorderPoint(rel.FromId, x1, y1, fax, fay);
        (x2, y2) = StoredBorderPoint(rel.ToId,   x2, y2, lax, lay);

        // Tagged point list respects junction flags and normal bends
        var pts  = BuildRelPoints(rel, x1, y1, x2, y2);
        const double TrimR = 8; // ring radius used for segment trimming

        var segs = new List<(Line L1, Line? L2)>();
        for (int i = 0; i < pts.Count - 1; i++)
        {
            double px1 = pts[i].X, py1 = pts[i].Y;
            double px2 = pts[i+1].X, py2 = pts[i+1].Y;
            bool isLast = (i == pts.Count - 2);

            // Trim segment ends where they meet a normal (non-junction) waypoint ring
            if (pts[i].IsNormalWp)   (px1, py1) = MoveToward(px1, py1, px2, py2, TrimR);
            if (pts[i+1].IsNormalWp) (px2, py2) = MoveToward(px2, py2, px1, py1, TrimR);

            double ex2 = px2, ey2 = py2;
            if (rel.HasArrow && isLast)
            {
                var dx = px2-px1; var dy = py2-py1; var len = Math.Sqrt(dx*dx+dy*dy);
                if (len > 0) { double al = 12+thickness*2; ex2 = px2-dx/len*al; ey2 = py2-dy/len*al; }
            }
            double ox = 0, oy = 0;
            if (isDouble)
            {
                var dx = px2-px1; var dy = py2-py1; var len = Math.Sqrt(dx*dx+dy*dy);
                if (len > 0) { double off = thickness+1.5; ox = -dy/len*off; oy = dx/len*off; }
            }
            Line MakeSeg(double oox, double ooy)
            {
                var ln = new Line { X1=px1+oox, Y1=py1+ooy, X2=ex2+oox, Y2=ey2+ooy,
                    StrokeThickness=thickness, Opacity=0.85, Stroke=brush,
                    StrokeStartLineCap=cap, StrokeEndLineCap=cap };
                if (dashArray is not null) { ln.StrokeDashArray = dashArray; ln.StrokeDashCap = cap; }
                Panel.SetZIndex(ln, 0); canvas.Children.Add(ln);
                return ln;
            }
            var l1 = MakeSeg(ox, oy);
            var l2 = isDouble ? MakeSeg(-ox, -oy) : null;

            // Right-click on any segment → "Add intersection" + relation ops
            int capturedSeg = i; var capturedRel2 = rel;
            void LineRightClick(object s, MouseButtonEventArgs e)
            {
                if (_selDragging) return;
                var pos = e.GetPosition(canvas);
                var mctx = new ContextMenu();
                var addWp = new MenuItem { Header = Properties.Loc.S("Board_AddIntersection") };
                addWp.Click += (_, _) =>
                {
                    SplitRelationAtSegment(capturedRel2, capturedSeg, pos.X, pos.Y);
                    EntityBoardService.Save(_projFolder, _board.Id, _boardData); BuildBoardContent();
                };
                mctx.Items.Add(addWp); mctx.Items.Add(new Separator());
                var editR = new MenuItem { Header = Properties.Loc.S("Board_EditRelation") };
                editR.Click += (_, _) =>
                {
                    var r = ShowRelationDialog(capturedRel2.Caption, capturedRel2.LegendLabel,
                        capturedRel2.LineStyle, capturedRel2.LineColor, capturedRel2.Thickness,
                        capturedRel2.HasArrow, Properties.Loc.S("Board_EditRelationTitle"));
                    if (r != null)
                    {
                        capturedRel2.Caption=r.Caption; capturedRel2.LegendLabel=r.LegendLabel;
                        capturedRel2.LineStyle=r.LineStyle; capturedRel2.LineColor=r.LineColor;
                        capturedRel2.Thickness=r.Thickness; capturedRel2.HasArrow=r.HasArrow;
                        MaybeAddLegendPreset(r);
                        EntityBoardService.Save(_projFolder, _board.Id, _boardData); BuildBoardContent();
                    }
                };
                mctx.Items.Add(editR);
                if (capturedRel2.HasArrow)
                {
                    var flipR = new MenuItem { Header = Properties.Loc.S("Board_FlipArrow") };
                    flipR.Click += (_, _) =>
                    {
                        (capturedRel2.FromId, capturedRel2.ToId) = (capturedRel2.ToId, capturedRel2.FromId);
                        capturedRel2.Waypoints.Reverse();
                        (capturedRel2.StartsAtJunction, capturedRel2.EndsAtJunction) =
                            (capturedRel2.EndsAtJunction, capturedRel2.StartsAtJunction);
                        EntityBoardService.Save(_projFolder, _board.Id, _boardData); BuildBoardContent();
                    };
                    mctx.Items.Add(flipR);
                }
                var delR = new MenuItem { Header = Properties.Loc.S("Board_DeleteRelation") };
                delR.Click += (_, _) => { _boardData.Relations.Remove(capturedRel2); EntityBoardService.Save(_projFolder, _board.Id, _boardData); BuildBoardContent(); };
                mctx.Items.Add(delR);
                mctx.IsOpen = true; e.Handled = true;
            }
            l1.MouseRightButtonDown += LineRightClick;
            if (l2 != null) l2.MouseRightButtonDown += LineRightClick;

            // Wide transparent hit-zone so thin lines are easy to hover/right-click
            var hitZone = new Line
            {
                X1 = l1.X1, Y1 = l1.Y1, X2 = l1.X2, Y2 = l1.Y2,
                StrokeThickness = Math.Max(12, thickness + 8),
                Stroke = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            Panel.SetZIndex(hitZone, 1);
            canvas.Children.Add(hitZone);
            string capturedHoverRelId = relId;
            hitZone.MouseEnter += (_, _) => { _hoverRelId = capturedHoverRelId; RefreshSelectionVisuals(); };
            hitZone.MouseLeave += (_, _) => { if(_hoverRelId==capturedHoverRelId) _hoverRelId=null; RefreshSelectionVisuals(); };
            hitZone.MouseRightButtonDown += LineRightClick;

            if (!_boardHitZones.ContainsKey(relId)) _boardHitZones[relId] = [];
            _boardHitZones[relId].Add(hitZone);

            segs.Add((l1, l2));
        }
        _boardLines[relId] = segs;

        // Arrowhead at the final point — for ring endpoints, tip lands on ring edge, not center
        if (rel.HasArrow && pts.Count >= 2)
        {
            var tipX = pts[^1].X; var tipY = pts[^1].Y;
            if (pts[^1].IsNormalWp)
                (tipX, tipY) = MoveToward(pts[^1].X, pts[^1].Y, pts[^2].X, pts[^2].Y, TrimR);
            var arrow = BuildArrowhead(pts[^2].X, pts[^2].Y, tipX, tipY, thickness, rel.LineColor);
            Panel.SetZIndex(arrow, 1); canvas.Children.Add(arrow);
            _boardArrows[relId] = arrow;
        }

        // Waypoint bubbles — hollow ring that mirrors the parent line's stroke style
        const double BR = 8;   // slightly larger than before for easy clicking
        Color bCol = brush is SolidColorBrush sb ? sb.Color : Colors.DodgerBlue;
        double bubStroke = Math.Max(1.5, thickness);
        var bubbles = new List<Ellipse>();
        for (int wi = 0; wi < rel.Waypoints.Count; wi++)
        {
            var wp = rel.Waypoints[wi]; var capturedWp = wp;
            var capturedRel3 = rel; int capturedWi = wi;

            // Linked waypoints follow their master — no independent bubble
            if (wp.LinkedToId != null) continue;
            var bub = new Ellipse
            {
                Width = BR*2, Height = BR*2, Cursor = Cursors.SizeAll,
                // 20% fill: opaque enough to cover lines inside, still shows background
                Fill            = new SolidColorBrush(Color.FromArgb(51, bCol.R, bCol.G, bCol.B)),
                Stroke          = brush,
                StrokeThickness = bubStroke
            };
            // Mirror the line's dash pattern so a dashed line has a dashed bubble ring
            if (dashArray is not null)
            {
                bub.StrokeDashArray = dashArray;
                bub.StrokeDashCap   = cap;
            }
            Panel.SetZIndex(bub, 3);
            Canvas.SetLeft(bub, wp.X-BR); Canvas.SetTop(bub, wp.Y-BR);
            canvas.Children.Add(bub); bubbles.Add(bub);

            bool isDraggingWp = false;
            bub.MouseLeftButtonDown += (s, e) =>
            {
                if (_boardConnectMode)
                {
                    // In connect mode a bubble click routes to/from the nearest line entity
                    HandleWaypointConnectClick(capturedRel3, capturedWp);
                    e.Handled = true;
                    return;
                }
                isDraggingWp=true; bub.CaptureMouse(); e.Handled=true;
            };
            bub.MouseMove += (s, e) =>
            {
                if (!isDraggingWp) return;
                var p = e.GetPosition(canvas);
                capturedWp.X=Snap(p.X); capturedWp.Y=Snap(p.Y);
                Canvas.SetLeft(bub, capturedWp.X-BR); Canvas.SetTop(bub, capturedWp.Y-BR);
                RebuildRelationSegments(capturedRel3.Id);
                // Cascade: rebuild every relation that has a waypoint linked to this master
                foreach (var lr in _boardData.Relations)
                    if (lr.Id != capturedRel3.Id &&
                        lr.Waypoints.Any(w => w.LinkedToId == capturedWp.Id))
                        RebuildRelationSegments(lr.Id);
                e.Handled=true;
            };
            bub.MouseLeftButtonUp += (s, e) =>
            {
                if (!isDraggingWp) return; isDraggingWp=false; bub.ReleaseMouseCapture();
                EntityBoardService.Save(_projFolder, _board.Id, _boardData); e.Handled=true;
            };
            bub.MouseRightButtonDown += (s, e) =>
            {
                // In connect mode: waypoints are not valid endpoints — swallow and ignore
                if (_boardConnectMode) { e.Handled = true; return; }

                var wctx = new ContextMenu();
                var conn = new MenuItem { Header = Properties.Loc.S("Board_ConnectFromHere") };
                conn.Click += (_, _) =>
                {
                    _connFromWaypoint = (capturedRel3.Id, capturedWp.Id);
                    _boardConnSrcId=null; _boardConnectMode=true; BuildBoardContent();
                };
                wctx.Items.Add(conn); wctx.Items.Add(new Separator());
                var rem = new MenuItem { Header = Properties.Loc.S("Board_RemoveIntersection") };
                rem.Click += (_, _) =>
                {
                    capturedRel3.Waypoints.RemoveAt(capturedWi);
                    EntityBoardService.Save(_projFolder, _board.Id, _boardData); BuildBoardContent();
                };
                wctx.Items.Add(rem); wctx.IsOpen=true; e.Handled=true;
            };
        }
        _boardWpBubbles[relId] = bubbles;
    }

    // ── Relation point-list helpers ───────────────────────────────────────

    /// <summary>
    /// Resolves a waypoint's actual position.
    /// When <see cref="BoardWaypoint.LinkedToId"/> is set the master waypoint is looked up
    /// so that all junction-connected lines move together.
    /// </summary>
    private (double X, double Y) WpPos(BoardWaypoint wp)
    {
        if (wp.LinkedToId == null) return (wp.X, wp.Y);
        foreach (var r in _boardData.Relations)
            foreach (var w in r.Waypoints)
                if (w.Id == wp.LinkedToId) return (w.X, w.Y);
        return (wp.X, wp.Y); // master deleted → fall back to cached coords
    }

    /// <summary>
    /// Builds the ordered point list for a relation's polyline.
    /// <para>Item3 (bool) = IsNormalWaypoint: true ⟹ this point is a movable bend
    /// and segments meeting here are trimmed by the ring radius.</para>
    /// Junction endpoints act as visual start/end terminals and are NOT trimmed.
    /// </summary>
    private List<(double X, double Y, bool IsNormalWp)> BuildRelPoints(
        BoardRelation rel, double x1, double y1, double x2, double y2)
    {
        var pts = new List<(double X, double Y, bool IsNormalWp)>();
        bool hasWps = rel.Waypoints.Count > 0;

        // ── Starting point ──
        if (rel.StartsAtJunction && hasWps)
        {
            var (wx, wy) = WpPos(rel.Waypoints[0]);
            pts.Add((wx, wy, true)); // junction origin — trim outgoing segment to ring edge
        }
        else
            pts.Add((x1, y1, false)); // FromId card border

        // ── Middle waypoints (normal bends, both sides trimmed by ring radius) ──
        int first = rel.StartsAtJunction && hasWps ? 1 : 0;
        int last  = rel.EndsAtJunction   && hasWps ? rel.Waypoints.Count - 1 : rel.Waypoints.Count;
        for (int i = first; i < last; i++)
        {
            var (wx, wy) = WpPos(rel.Waypoints[i]);
            pts.Add((wx, wy, true));
        }

        // ── Ending point ──
        if (rel.EndsAtJunction && hasWps)
        {
            var (wx, wy) = WpPos(rel.Waypoints[^1]);
            pts.Add((wx, wy, true)); // junction terminus — trim incoming segment to ring edge
        }
        else
            pts.Add((x2, y2, false)); // ToId card border

        return pts;
    }

    /// <summary>Moves point (fx,fy) toward (tx,ty) by <paramref name="d"/> pixels.</summary>
    private static (double, double) MoveToward(double fx, double fy, double tx, double ty, double d)
    {
        double dx = tx - fx, dy = ty - fy, len = Math.Sqrt(dx*dx + dy*dy);
        if (len <= d) return ((fx+tx)/2, (fy+ty)/2);
        return (fx + dx/len*d, fy + dy/len*d);
    }

    // ── Snap-to-grid ──────────────────────────────────────────────────────

    private double Snap(double v) =>
        _boardData.SnapToGrid && _boardData.GridSize >= 2
            ? Math.Round(v / _boardData.GridSize) * _boardData.GridSize : v;

    private void ApplyGridBackground(Canvas canvas, bool visible, double gs, string? colorHex)
    {
        if (!visible || gs < 4) { canvas.Background = Brushes.Transparent; return; }
        Color gc = Color.FromArgb(38, 128, 128, 128);
        if (!string.IsNullOrEmpty(colorHex))
        {
            try { gc = (Color)ColorConverter.ConvertFromString(colorHex)!; } catch { }
        }
        else
        {
            // Derive a subtle translucent version of the theme's ControlBorderBrush
            if (TryFindResource("ControlBorderBrush") is SolidColorBrush scb)
            {
                var bc = scb.Color;
                gc = Color.FromArgb(38, bc.R, bc.G, bc.B);
            }
        }
        var pen = new Pen(new SolidColorBrush(gc), 0.5); pen.Freeze();
        var dg = new DrawingGroup();
        dg.Children.Add(new GeometryDrawing(null, pen,
            new LineGeometry(new Point(gs, 0), new Point(gs, gs))));
        dg.Children.Add(new GeometryDrawing(null, pen,
            new LineGeometry(new Point(0, gs), new Point(gs, gs))));
        dg.Freeze();
        canvas.Background = new DrawingBrush(dg)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, gs, gs),
            ViewportUnits = BrushMappingMode.Absolute
        };
    }

    // ── Card-border helpers ────────────────────────────────────────────────

    /// <summary>
    /// Returns the point on the border of a card's rectangle that lies on the line from
    /// the card center (cx,cy) toward external point (tx,ty).
    /// hw/hh are the card half-width and half-height.
    /// </summary>
    private static (double bx, double by) BorderIntersect(
        double cx, double cy, double hw, double hh, double tx, double ty)
    {
        double dx = tx - cx, dy = ty - cy;
        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) return (cx, cy);

        double tMin = double.MaxValue;
        double bx = cx, by = cy;

        // Right / Left border (vertical sides)
        if (Math.Abs(dx) > 0.001)
        {
            double t = (dx > 0 ? hw : -hw) / dx;
            if (t > 0)
            {
                double y = cy + t * dy;
                if (y >= cy - hh - 1 && y <= cy + hh + 1 && t < tMin)
                { tMin = t; bx = cx + t * dx; by = y; }
            }
        }
        // Bottom / Top border (horizontal sides)
        if (Math.Abs(dy) > 0.001)
        {
            double t = (dy > 0 ? hh : -hh) / dy;
            if (t > 0)
            {
                double x = cx + t * dx;
                if (x >= cx - hw - 1 && x <= cx + hw + 1 && t < tMin)
                { tMin = t; bx = x; by = cy + t * dy; }
            }
        }
        return (bx, by);
    }

    /// <summary>Returns the card border point aimed toward (toX,toY), using stored position data (for initial render).</summary>
    private (double, double) StoredBorderPoint(string entityId, double cx, double cy, double toX, double toY)
    {
        if (!_boardData.Positions.TryGetValue(entityId, out var bp)) return (cx, cy);
        double hw = (bp.CardWidth  > 0 ? bp.CardWidth  : 160) / 2;
        double hh = (bp.CardHeight > 0 ? bp.CardHeight :  90) / 2;
        return BorderIntersect(cx, cy, hw, hh, toX, toY);
    }

    /// <summary>Returns the card border point aimed toward (toX,toY), using actual rendered card size.</summary>
    private (double, double) LiveBorderPoint(string entityId, double toX, double toY)
    {
        var (cx, cy) = GetBoardCardCenter(entityId);
        if (!_boardCards.TryGetValue(entityId, out var card)) return (cx, cy);
        double hw = (card.ActualWidth  > 0 ? card.ActualWidth  : 160) / 2;
        double hh = (card.ActualHeight > 0 ? card.ActualHeight :  80) / 2;
        return BorderIntersect(cx, cy, hw, hh, toX, toY);
    }

    private Polygon BuildArrowhead(double x1, double y1, double x2, double y2, double thickness, string lineColor)
    {
        double hw=5+thickness, len=12+thickness*2;
        var dx=x2-x1; var dy=y2-y1;
        double d=Math.Sqrt(dx*dx+dy*dy); if(d<1)d=1;
        double ux=dx/d, uy=dy/d, bx=x2-ux*len, by=y2-uy*len;
        return new Polygon
        {
            Points = new PointCollection { new(x2,y2), new(bx-uy*hw,by+ux*hw), new(bx+uy*hw,by-ux*hw) },
            Opacity=0.85, Fill=GetLineBrush(lineColor)
        };
    }

    /// <summary>Recomputes all segment coordinates for a relation during live drag (no visual rebuild).</summary>
    private void RebuildRelationSegments(string relId)
    {
        if (!_boardLines.TryGetValue(relId, out var segs)) return;
        var rel = _boardData.Relations.FirstOrDefault(r => r.Id == relId);
        if (rel == null) return;

        double thickness = Math.Max(1, rel.Thickness);
        bool isDouble    = IsDoubleLine(rel.LineStyle);
        const double TrimR = 8;

        var (scx, scy) = GetBoardCardCenter(rel.FromId);
        var (ecx, ecy) = GetBoardCardCenter(rel.ToId);
        var (fax2, fay2) = rel.Waypoints.Count > 0 ? WpPos(rel.Waypoints[0])  : (ecx, ecy);
        var (lax2, lay2) = rel.Waypoints.Count > 0 ? WpPos(rel.Waypoints[^1]) : (scx, scy);
        var (sx, sy) = LiveBorderPoint(rel.FromId, fax2, fay2);
        var (ex, ey) = LiveBorderPoint(rel.ToId,   lax2, lay2);

        var pts = BuildRelPoints(rel, sx, sy, ex, ey);

        for (int i = 0; i < Math.Min(segs.Count, pts.Count-1); i++)
        {
            double px1=pts[i].X,py1=pts[i].Y,px2=pts[i+1].X,py2=pts[i+1].Y;
            bool isLast=(i==segs.Count-1);

            // Trim at normal waypoint rings
            if(pts[i].IsNormalWp)   (px1,py1)=MoveToward(px1,py1,px2,py2,TrimR);
            if(pts[i+1].IsNormalWp) (px2,py2)=MoveToward(px2,py2,px1,py1,TrimR);

            double ex2=px2,ey2=py2;
            if(rel.HasArrow&&isLast)
            {
                var dx=px2-px1;var dy=py2-py1;var len=Math.Sqrt(dx*dx+dy*dy);
                if(len>0){double al=12+thickness*2;ex2=px2-dx/len*al;ey2=py2-dy/len*al;}
            }
            double ox=0,oy=0;
            if(isDouble){var dx=px2-px1;var dy=py2-py1;var len=Math.Sqrt(dx*dx+dy*dy);
                if(len>0){double off=thickness+1.5;ox=-dy/len*off;oy=dx/len*off;}}
            segs[i].L1.X1=px1+ox;segs[i].L1.Y1=py1+oy;segs[i].L1.X2=ex2+ox;segs[i].L1.Y2=ey2+oy;
            if(segs[i].L2 is{} l2){l2.X1=px1-ox;l2.Y1=py1-oy;l2.X2=ex2-ox;l2.Y2=ey2-oy;}
            // Keep hit zone in sync
            if(_boardHitZones.TryGetValue(relId,out var hz)&&i<hz.Count)
            { hz[i].X1=px1;hz[i].Y1=py1;hz[i].X2=ex2;hz[i].Y2=ey2; }
        }
        if(_boardArrows.TryGetValue(relId,out var arrow)&&pts.Count>=2)
        {
            var tipX=pts[^1].X; var tipY=pts[^1].Y;
            if(pts[^1].IsNormalWp) (tipX,tipY)=MoveToward(pts[^1].X,pts[^1].Y,pts[^2].X,pts[^2].Y,TrimR);
            ApplyArrowGeometry(arrow,pts[^2].X,pts[^2].Y,tipX,tipY,thickness);
        }
        double mx=(sx+ex)/2,my=(sy+ey)/2;
        if(pts.Count>=3){int mid=pts.Count/2;mx=(pts[mid].X+pts[mid-1].X)/2;my=(pts[mid].Y+pts[mid-1].Y)/2;}
        if(_boardCaptionBadges.TryGetValue(relId,out var badge)){Canvas.SetLeft(badge,mx-30);Canvas.SetTop(badge,my-12);}
    }

    private void UpdateBoardLines(string entityId, double newX, double newY)
    {
        foreach (var rel in _boardData.Relations)
        {
            if (rel.FromId != entityId && rel.ToId != entityId) continue;
            if (!_boardLines.ContainsKey(rel.Id)) continue;
            RebuildRelationSegments(rel.Id);
        }
    }

    private void RefreshAllBoardLinePositions()
    {
        foreach (var rel in _boardData.Relations)
            if (_boardLines.ContainsKey(rel.Id))
                RebuildRelationSegments(rel.Id);
    }

    private (double cx, double cy) GetBoardCardCenter(string entityId)
    {
        if (!_boardCards.TryGetValue(entityId, out var card)) return (100, 100);
        var x = Canvas.GetLeft(card); var y = Canvas.GetTop(card);
        var w = card.ActualWidth  > 0 ? card.ActualWidth  : 200;
        var h = card.ActualHeight > 0 ? card.ActualHeight : 90;
        return (x + w / 2, y + h / 2);
    }

    private static void ApplyArrowGeometry(Polygon arrow, double x1, double y1, double x2, double y2, double thickness)
    {
        double hw  = 5 + thickness;
        double len = 12 + thickness * 2;
        var dx = x2 - x1; var dy = y2 - y1;
        double d = Math.Sqrt(dx * dx + dy * dy); if (d < 1) d = 1;
        double ux = dx / d; double uy = dy / d;
        double bx = x2 - ux * len; double by = y2 - uy * len;
        arrow.Points = new PointCollection
        {
            new Point(x2, y2),
            new Point(bx - uy * hw, by + ux * hw),
            new Point(bx + uy * hw, by - ux * hw)
        };
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

    /// <summary>
    /// Replaces <paramref name="rel"/> with two independent relations that share a junction
    /// waypoint at the clicked position.  The split point is <paramref name="segIdx"/> — the
    /// rendering-loop segment index, i.e. how many pts-list steps from the start.
    /// After the split each half can be independently styled, arrowed, or flipped.
    /// </summary>
    private void SplitRelationAtSegment(BoardRelation rel, int segIdx, double posX, double posY)
    {
        // Master waypoint lives in the first half; second half has a linked copy.
        var masterWp = new BoardWaypoint { X = posX, Y = posY };
        var linkedWp = new BoardWaypoint { X = posX, Y = posY, LinkedToId = masterWp.Id };

        // When StartsAtJunction, pts[0] == Waypoints[0], so pts[segIdx] == Waypoints[segIdx].
        // Otherwise pts[0] == FromBorder, so the waypoint slice index is segIdx - 1.
        int wpSlice = rel.StartsAtJunction ? segIdx : segIdx - 1;
        // wpSlice = number of existing waypoints that go into the first half (may be –1 = none)

        var firstWps  = rel.Waypoints.Take(Math.Max(0, wpSlice + 1)).Append(masterWp).ToList();
        var secondWps = new List<BoardWaypoint> { linkedWp }
                        .Concat(rel.Waypoints.Skip(Math.Max(0, wpSlice + 1))).ToList();

        var first = new BoardRelation
        {
            FromId           = rel.FromId,
            ToId             = rel.ToId,
            Caption          = rel.Caption,
            LegendLabel      = rel.LegendLabel,
            LineStyle        = rel.LineStyle,
            LineColor        = rel.LineColor,
            Thickness        = rel.Thickness,
            HasArrow         = false,            // arrow control per-segment
            StartsAtJunction = rel.StartsAtJunction,
            EndsAtJunction   = true,             // terminates at the new ring
            Waypoints        = firstWps
        };
        var second = new BoardRelation
        {
            FromId           = rel.FromId,
            ToId             = rel.ToId,
            Caption          = "",
            LegendLabel      = rel.LegendLabel,
            LineStyle        = rel.LineStyle,
            LineColor        = rel.LineColor,
            Thickness        = rel.Thickness,
            HasArrow         = rel.HasArrow,     // original arrow carried to second half
            StartsAtJunction = true,             // originates at the new ring
            EndsAtJunction   = rel.EndsAtJunction,
            Waypoints        = secondWps
        };

        int idx = _boardData.Relations.IndexOf(rel);
        _boardData.Relations.Remove(rel);
        _boardData.Relations.Insert(idx,     first);
        _boardData.Relations.Insert(idx + 1, second);
    }

    private void ResetConnectMode()
    {
        _boardConnSrcId   = null;
        _connFromWaypoint = null;
        _boardConnectMode = false;
    }

    /// <summary>
    /// Called when the user clicks a waypoint bubble while in connect mode.
    /// If a source is already set → complete the connection TO this waypoint (no dialog, inherit style).
    /// If no source is set → this waypoint becomes the source.
    /// </summary>
    private void HandleWaypointConnectClick(BoardRelation parentRel, BoardWaypoint wp)
    {
        string nearestId = NearestEntityToWaypoint(parentRel, wp);

        if (_boardConnSrcId != null)
        {
            // Card → ring: line visually ENDS at the ring (EndsAtJunction).
            // LinkedToId ensures the junction waypoint follows its master when dragged.
            _boardData.Relations.Add(new BoardRelation
            {
                FromId         = _boardConnSrcId,
                ToId           = nearestId,
                LineStyle      = parentRel.LineStyle,
                LineColor      = parentRel.LineColor,
                Thickness      = parentRel.Thickness,
                HasArrow       = parentRel.HasArrow,
                Waypoints      = [new BoardWaypoint { X = wp.X, Y = wp.Y, LinkedToId = wp.Id }],
                EndsAtJunction = true
            });
            EntityBoardService.Save(_projFolder, _board.Id, _boardData);
            ResetConnectMode();
            BuildBoardContent();
        }
        else if (_connFromWaypoint.HasValue)
        {
            // Ring → ring
            var srcRelId = _connFromWaypoint.Value.RelId;
            var srcRel   = _boardData.Relations.FirstOrDefault(r => r.Id == srcRelId);
            var srcWp    = srcRel?.Waypoints.FirstOrDefault(w => w.Id == _connFromWaypoint.Value.WpId);
            if (srcRel != null && srcWp != null)
            {
                _boardData.Relations.Add(new BoardRelation
                {
                    FromId           = NearestEntityToWaypoint(srcRel, srcWp),
                    ToId             = nearestId,
                    LineStyle        = parentRel.LineStyle,
                    LineColor        = parentRel.LineColor,
                    Thickness        = parentRel.Thickness,
                    HasArrow         = parentRel.HasArrow,
                    Waypoints        = [new BoardWaypoint { X = srcWp.X, Y = srcWp.Y, LinkedToId = srcWp.Id },
                                        new BoardWaypoint { X = wp.X,   Y = wp.Y,   LinkedToId = wp.Id   }],
                    StartsAtJunction = true,
                    EndsAtJunction   = true
                });
                EntityBoardService.Save(_projFolder, _board.Id, _boardData);
            }
            ResetConnectMode();
            BuildBoardContent();
        }
        else
        {
            // Nothing selected → ring becomes source for next click
            _connFromWaypoint = (parentRel.Id, wp.Id);
            _boardConnSrcId   = null;
            BuildBoardContent();
        }
    }

    /// <summary>
    /// Returns whichever endpoint of <paramref name="rel"/> is physically closest to
    /// <paramref name="wp"/>.  Used so "connect to/from intersection" picks the intuitive entity.
    /// </summary>
    private string NearestEntityToWaypoint(BoardRelation rel, BoardWaypoint wp)
    {
        (double cx, double cy) Center(string id)
        {
            if (!_boardData.Positions.TryGetValue(id, out var p)) return (0, 0);
            return (p.X + (p.CardWidth  > 0 ? p.CardWidth  : 160) / 2,
                    p.Y + (p.CardHeight > 0 ? p.CardHeight :  90) / 2);
        }
        var (fx, fy) = Center(rel.FromId);
        var (tx, ty) = Center(rel.ToId);
        double fd = Math.Sqrt(Math.Pow(wp.X-fx,2) + Math.Pow(wp.Y-fy,2));
        double td = Math.Sqrt(Math.Pow(wp.X-tx,2) + Math.Pow(wp.Y-ty,2));
        return fd <= td ? rel.FromId : rel.ToId;
    }

    private void HandleBoardConnectClick(string entityId)
    {
        // ── Connect from a waypoint bubble to a card ───────────────────────
        if (_connFromWaypoint.HasValue)
        {
            var (srcRelId, srcWpId) = _connFromWaypoint.Value;
            var srcRel = _boardData.Relations.FirstOrDefault(r => r.Id == srcRelId);
            var srcWp  = srcRel?.Waypoints.FirstOrDefault(w => w.Id == srcWpId);
            if (srcRel != null && srcWp != null)
            {
                // Ring → card: line visually STARTS at the ring (StartsAtJunction).
                // LinkedToId so the junction waypoint follows the master ring.
                _boardData.Relations.Add(new BoardRelation
                {
                    FromId           = NearestEntityToWaypoint(srcRel, srcWp),
                    ToId             = entityId,
                    LineStyle        = srcRel.LineStyle,
                    LineColor        = srcRel.LineColor,
                    Thickness        = srcRel.Thickness,
                    HasArrow         = srcRel.HasArrow,
                    Waypoints        = [new BoardWaypoint { X = srcWp.X, Y = srcWp.Y, LinkedToId = srcWp.Id }],
                    StartsAtJunction = true
                });
                EntityBoardService.Save(_projFolder, _board.Id, _boardData);
            }
            ResetConnectMode();
            BuildBoardContent();
            return;
        }

        // ── Normal card-to-card connect ────────────────────────────────────
        if (_boardConnSrcId == null)
        {
            _boardConnSrcId = entityId;
            BuildBoardContent();
        }
        else if (_boardConnSrcId == entityId)
        {
            ResetConnectMode();
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
                    Thickness   = result.Thickness,
                    HasArrow    = result.HasArrow
                });
                MaybeAddLegendPreset(result);
                EntityBoardService.Save(_projFolder, _board.Id, _boardData);
            }
            ResetConnectMode();
            BuildBoardContent();
        }
    }

    /// <summary>
    /// If the relation result carries a non-empty LegendLabel, adds it as a new legend preset.
    /// Shows a notification if a preset with that name already exists.
    /// Must be called BEFORE EntityBoardService.Save so the preset is included.
    /// </summary>
    private void MaybeAddLegendPreset(RelationDialogResult result)
    {
        if (string.IsNullOrWhiteSpace(result.LegendLabel)) return;

        var existing = _boardData.LinePresets.FirstOrDefault(p =>
            string.Equals(p.Name, result.LegendLabel, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            MessageBox.Show(
                $"A legend entry named \"{result.LegendLabel}\" already exists.\n" +
                "The relation's legend label was saved, but no new preset was added.",
                "Legend", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _boardData.LinePresets.Add(new BoardLinePreset
        {
            Name      = result.LegendLabel,
            Style     = result.LineStyle,
            Color     = result.LineColor,
            Thickness = result.Thickness,
            HasArrow  = result.HasArrow
        });
    }

    // ── Auto-arrange ───────────────────────────────────────────────────────

    /// <summary>
    /// Arranges entities on the board in a grid.
    /// When <paramref name="onlyIds"/> is non-null, only those entities are repositioned;
    /// the rest keep their current positions.
    /// When null, all entities currently on the board are arranged.
    /// </summary>
    private void AutoArrangeBoard(IReadOnlyCollection<string>? onlyIds)
    {
        const int Cols = 4, CardW = 200, CardH = 120, PadX = 60, PadY = 50;

        // Which IDs to arrange — only those already placed on this board
        var toArrange = _boardData.Positions.Keys
            .Where(id => onlyIds == null || onlyIds.Contains(id))
            .OrderBy(id => id)   // stable order
            .ToList();

        // Anchor: start below the lowest unselected card when arranging a subset
        double startY = 40;
        if (onlyIds != null)
        {
            var otherYs = _boardData.Positions
                .Where(kv => !onlyIds.Contains(kv.Key))
                .Select(kv => kv.Value.Y + CardH)
                .DefaultIfEmpty(0);
            startY = otherYs.Max() + PadY;
        }

        int row = 0, col = 0;
        foreach (var et in _board.EntityTypes)
        {
            var ids = toArrange.Where(id =>
            {
                // look up entity type by checking loaded entities
                var entity = WorldEntityService.List(_projFolder, et).FirstOrDefault(e => e.Id == id);
                return entity != null;
            }).ToList();
            if (ids.Count == 0) continue;
            foreach (var id in ids)
            {
                var bp = _boardData.Positions[id];
                bp.X = 40 + col * (CardW + PadX);
                bp.Y = startY + row * (CardH + PadY);
                if (++col >= Cols) { col = 0; row++; }
            }
            row++;  col = 0;
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

    private record RelationDialogResult(string Caption, string LegendLabel, BoardLineStyle LineStyle,
                                        string LineColor, double Thickness, bool HasArrow);

    private RelationDialogResult? ShowRelationDialog(
        string captionInit = "", string legendInit = "",
        BoardLineStyle styleInit = BoardLineStyle.Solid,
        string colorInit = "#2196F3", double thicknessInit = 1.5,
        bool hasArrowInit = false,
        string? title = null)
    {
        var win = new Window
        {
            Title = title ?? Properties.Loc.S("Board_NewRelation"), Width = 440, Height = 490, ResizeMode = ResizeMode.NoResize,
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

        // Hoist all control vars so the preset lambda can capture them before creation
        if (!string.IsNullOrWhiteSpace(colorInit) && !colorInit.StartsWith('#')) colorInit = "#2196F3";
        string       selectedColor = colorInit;
        ComboBox     styleCombo    = null!;
        var          styleItems    = new (string Display, BoardLineStyle Value)[]
        {
            ("━━━━━  Solid",          BoardLineStyle.Solid),
            ("· · · · ·  Dotted",    BoardLineStyle.Dotted),
            ("─ ─ ─ ─  Dashed",      BoardLineStyle.Dashed),
            ("·─·─·  Dot-dash",       BoardLineStyle.DotDash),
            ("══════  Double solid",  BoardLineStyle.DoubleSolid),
            ("⁚ ⁚ ⁚  Double dotted", BoardLineStyle.DoubleDotted),
            ("═ ═ ═  Double dashed",  BoardLineStyle.DoubleDashed),
            ("·═·═  Double dot-dash", BoardLineStyle.DoubleDotDash)
        };
        List<Border> swatches     = [];
        Slider       thickSlider  = null!;
        CheckBox     arrowCheck   = null!;

        // ── Load from Legend preset ─────────────────────────────────────────
        var presets = _boardData.LinePresets;
        if (presets.Count > 0)
        {
            AddLabel(Properties.Loc.S("Board_LoadFromPreset"));
            var presetCombo = new ComboBox { FontSize = 12, Margin = new Thickness(0, 0, 0, 4),
                                             Foreground = new SolidColorBrush(Color.FromRgb(20,20,20)) };
            presetCombo.SetResourceReference(ComboBox.BackgroundProperty,  "ControlBgBrush");
            presetCombo.SetResourceReference(ComboBox.BorderBrushProperty, "ControlBorderBrush");
            presetCombo.Items.Add("— custom —");
            foreach (var p in presets) presetCombo.Items.Add(p.Name);
            presetCombo.SelectedIndex = 0;
            root.Children.Add(presetCombo);

            presetCombo.SelectionChanged += (_, _) =>
            {
                var idx = presetCombo.SelectedIndex;
                if (idx <= 0 || idx - 1 >= presets.Count) return;
                var pr = presets[idx - 1];
                int si = Array.FindIndex(styleItems, x => x.Value == pr.Style);
                if (si >= 0) styleCombo.SelectedIndex = si;
                selectedColor = pr.Color;
                foreach (var sw in swatches)
                {
                    bool sel = string.Equals(sw.Tag?.ToString(), pr.Color, StringComparison.OrdinalIgnoreCase);
                    sw.BorderThickness = new Thickness(sel ? 2.5 : 0);
                    sw.BorderBrush = sel ? Brushes.White : Brushes.Transparent;
                }
                thickSlider.Value   = pr.Thickness;
                arrowCheck.IsChecked = pr.HasArrow;
            };
        }

        AddLabel(Properties.Loc.S("Board_Caption"));
        var captionBox = MakeTextBox(captionInit);
        AddLabel(Properties.Loc.S("Board_LegendLabel"));
        var legendBox = MakeTextBox(legendInit, maxLen: 20);

        AddLabel(Properties.Loc.S("Board_LineStyle"));
        styleCombo = new ComboBox { FontSize = 12, Padding = new Thickness(8, 4, 8, 4) };
        int styleInitIdx = 0;
        for (int i = 0; i < styleItems.Length; i++) { styleCombo.Items.Add(styleItems[i].Display); if (styleItems[i].Value == styleInit) styleInitIdx = i; }
        styleCombo.SelectedIndex = styleInitIdx;
        root.Children.Add(styleCombo);

        AddLabel(Properties.Loc.S("Board_LineColour"));
        var swatchWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        root.Children.Add(swatchWrap);
        foreach (var hex in WorldEntitySchemas.FactionColorPalette)
        {
            var capturedHex = hex;
            Color c; try { c = (Color)ColorConverter.ConvertFromString(hex)!; } catch { c = Colors.Gray; }
            bool isSel = string.Equals(hex, colorInit, StringComparison.OrdinalIgnoreCase);
            var swatch = new Border { Width = 26, Height = 26, Margin = new Thickness(0, 0, 4, 4),
                CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(c),
                BorderThickness = new Thickness(isSel ? 2.5 : 0),
                BorderBrush = isSel ? Brushes.White : Brushes.Transparent,
                Cursor = Cursors.Hand, Tag = hex, ToolTip = hex };
            swatch.MouseLeftButtonDown += (_, _) =>
            {
                selectedColor = capturedHex;
                foreach (var sw in swatches)
                {
                    bool sel = string.Equals(sw.Tag?.ToString(), capturedHex, StringComparison.OrdinalIgnoreCase);
                    sw.BorderThickness = new Thickness(sel ? 2.5 : 0);
                    sw.BorderBrush = sel ? Brushes.White : Brushes.Transparent;
                }
            };
            swatches.Add(swatch); swatchWrap.Children.Add(swatch);
        }

        AddLabel(Properties.Loc.S("Board_ThicknessHint"));
        var thickRow = new StackPanel { Orientation = Orientation.Horizontal };
        root.Children.Add(thickRow);
        thickSlider = new Slider { Minimum = 1, Maximum = 10, Value = thicknessInit, Width = 200,
            VerticalAlignment = VerticalAlignment.Center, IsSnapToTickEnabled = true, TickFrequency = 0.5 };
        var thickLbl = new TextBlock { Text = $"{thicknessInit:F1} px", FontSize = 12, Width = 44,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        thickLbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        thickSlider.ValueChanged += (_, e) => thickLbl.Text = $"{e.NewValue:F1} px";
        thickRow.Children.Add(thickSlider); thickRow.Children.Add(thickLbl);

        AddLabel(Properties.Loc.S("Board_ArrowLbl"));
        arrowCheck = new CheckBox { Content = Properties.Loc.S("Board_DrawArrow"), IsChecked = hasArrowInit, Margin = new Thickness(0, 0, 0, 4) };
        arrowCheck.SetResourceReference(CheckBox.ForegroundProperty, "ContentTextBrush");
        root.Children.Add(arrowCheck);

        var dialogBtnRow = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        root.Children.Add(dialogBtnRow);
        var cancelBtn = MakeBtn(Properties.Loc.S("Btn_Cancel"), false); cancelBtn.Padding = new Thickness(12, 6, 12, 6);
        cancelBtn.Click += (_, _) => win.DialogResult = false;
        var okBtn = MakeBtn(Properties.Loc.S("Btn_OK"), true); okBtn.Padding = new Thickness(12, 6, 12, 6);
        okBtn.Margin = new Thickness(8, 0, 0, 0); okBtn.Click += (_, _) => win.DialogResult = true;
        dialogBtnRow.Children.Add(cancelBtn); dialogBtnRow.Children.Add(okBtn);
        captionBox.Focus(); captionBox.SelectAll();
        captionBox.PreviewKeyDown += (_, e) => { if (e.Key == Key.Return) win.DialogResult = true; };

        if (win.ShowDialog() != true) return null;
        var chosenStyle = styleItems[Math.Max(0, Math.Min(styleCombo.SelectedIndex, styleItems.Length - 1))].Value;
        return new RelationDialogResult(captionBox.Text.Trim(), legendBox.Text.Trim(),
                                        chosenStyle, selectedColor, thickSlider.Value,
                                        arrowCheck.IsChecked == true);
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

    /// <summary>Resolves a hex color string ("#RRGGBB") to a WPF SolidColorBrush.</summary>
    private static Brush GetLineBrush(string colorStr)
    {
        if (!string.IsNullOrWhiteSpace(colorStr))
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorStr)!); }
            catch { }
        return new SolidColorBrush(Colors.DimGray);
    }

    private static WorldEntity CloneEntity(WorldEntity src) => new()
    {
        Id               = src.Id,
        Name             = src.Name,
        EntityType       = src.EntityType,
        CreatedAt        = src.CreatedAt,
        UpdatedAt        = src.UpdatedAt,
        Fields           = new Dictionary<string, string>(src.Fields),
        Notes            = src.Notes,
        PortraitFileName = src.PortraitFileName,
        ImageFileName    = src.ImageFileName,
        FactionColor     = src.FactionColor,
        MemberIds        = [..src.MemberIds],
        FactionIds       = [..src.FactionIds]
    };
}
