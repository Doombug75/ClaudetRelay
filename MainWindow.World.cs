using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using SysIO = System.IO;
using ClaudetRelay.Services;

namespace ClaudetRelay;

// World-building editor -- all world/board methods and dialogs.
// Partial class companion to MainWindow.xaml.cs.
public partial class MainWindow
{
    private void WorldButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProjectFolder is null) return;

        // ── External world editor (plugin) ────────────────────────────────
        var extPath = SettingsService.Load().ExternalWorldEditorPath;
        if (!string.IsNullOrWhiteSpace(extPath) && SysIO.File.Exists(extPath))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(extPath)
                {
                    Arguments      = $"\"{_currentProjectFolder}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not launch external world editor:\n{ex.Message}",
                    "World Editor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }

        // ── Built-in editor ───────────────────────────────────────────────
        ShowWorldPanel(WorldContent.Visibility != Visibility.Visible);
    }

    // ── World editor panel ────────────────────────────────────────────────

    private string          _worldActiveType        = "";
    private bool            _worldBoardsMode        = false;
    private string          _worldBoardsSort        = "date_desc"; // name_asc | name_desc | date_asc | date_desc
    private readonly HashSet<string> _worldBoardsTypeFilter = new(); // empty = all; otherwise filter by EntityTypes intersection
    private bool   _entityEditOpen  = false;   // true while ShowEntityEditDialog is open

    // Entity list filter state (reset when switching entity type)
    private string? _worldFilterName               = null;
    private string? _worldFilterFaction            = null;
    private string? _worldFilterArc                = null;
    private string? _worldFilterAlignment          = null;
    private bool    _worldFilterCommonKnowledge    = false;
    private bool    _worldFilterHistoricalKnowledge = false;
    private string  _worldSortMode                 = "name_asc";

    // Open board windows keyed by board ID (singleton per board)
    private readonly Dictionary<string, WorldBoardWindow> _openBoardWindows = new();

    private void ShowWorldPanel(bool show)
    {
        if (show && _currentProjectFolder is not null)
        {
            // Collapse all other project panels first
            RoadmapContent   .Visibility = Visibility.Collapsed;
            RoadmapButton.FontWeight     = FontWeights.Normal;
            FilesContent     .Visibility = Visibility.Collapsed;
            FilesButton.FontWeight       = FontWeights.Normal;

            // Hide chat-only buttons and deactivate Chat sub-tab
            ChatOnlyButtonsPanel .Visibility = Visibility.Collapsed;
            ChatViewButton.FontWeight         = FontWeights.Normal;

            if (string.IsNullOrEmpty(_worldActiveType))
            {
                var first = GetWorldEntityTypes().FirstOrDefault() ?? "Character";
                _worldActiveType = first.TrimEnd('s'); // always store singular ("Character" not "Characters")
            }
            // Make the panel visible BEFORE building children so that
            // SetResourceReference lookups have a live visual tree to walk.
            WorldContent     .Visibility = Visibility.Visible;
            ChatScrollViewer .Visibility = Visibility.Collapsed;
            InputArea        .Visibility = Visibility.Collapsed;
            WorldButton.FontWeight       = FontWeights.SemiBold;
            BuildWorldContent();
        }
        else
        {
            WorldContent     .Visibility = Visibility.Collapsed;
            ChatScrollViewer .Visibility = Visibility.Visible;
            InputArea        .Visibility = Visibility.Visible;
            WorldButton.FontWeight       = FontWeights.Normal;

            // Restore chat-only buttons and mark Chat sub-tab active
            ChatOnlyButtonsPanel .Visibility = Visibility.Visible;
            ChatViewButton.FontWeight         = _currentProjectFolder is not null
                                               ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    /// <summary>Returns the entity types for the current project type (e.g. Characters, Locations…).
    /// "Lores" is always included so the lore library is always accessible regardless of project-type config.</summary>
    private string[] GetWorldEntityTypes()
    {
        var wf = _currentProjectType?.GetWorldFolderList();
        var types = (wf is { Length: > 0 } ? wf : new[] { "Characters", "Locations" }).ToList();
        if (!types.Any(t => t.TrimEnd('s').Equals("Lore", StringComparison.OrdinalIgnoreCase)))
            types.Add("Lores");
        return types.ToArray();
    }

    private void BuildWorldContent()
    {
        if (_currentProjectFolder is null) return;
        var projFolder   = _currentProjectFolder;
        var entityTypes  = GetWorldEntityTypes();

        WorldContent.Children.Clear();
        WorldContent.RowDefinitions.Clear();
        WorldContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // toolbar
        WorldContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // cards

        // ── Tab toolbar ────────────────────────────────────────────────────
        var toolbar = new Border
        {
            Padding = new Thickness(16, 10, 16, 0)
        };
        toolbar.SetResourceReference(Border.BackgroundProperty, "SidebarBgBrush");
        Grid.SetRow(toolbar, 0);
        WorldContent.Children.Add(toolbar);

        var tabRow = new Grid();
        tabRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tabRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Child = tabRow;

        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(tabs, 0);
        tabRow.Children.Add(tabs);

        // Singular label map: "Characters" → "Character", "Locations" → "Location" etc.
        string Singular(string t) => t.TrimEnd('s');

        foreach (var et in entityTypes)
        {
            var capturedEt  = et;
            var isActive    = !_worldBoardsMode &&
                              (string.Equals(et, _worldActiveType, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(Singular(et), _worldActiveType, StringComparison.OrdinalIgnoreCase));
            var tab = new Button
            {
                Content     = et,
                FontSize    = 13,
                FontFamily  = new FontFamily("Segoe UI"),
                FontWeight  = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                Padding     = new Thickness(14, 8, 14, 10),
                BorderThickness = new Thickness(0),
                Cursor      = Cursors.Hand,
                Background  = Brushes.Transparent
            };
            tab.SetResourceReference(Button.ForegroundProperty,
                isActive ? "AccentHighlightBrush" : "SidebarDimBrush");
            tab.Click += (_, _) =>
            {
                _worldBoardsMode    = false;
                _worldActiveType    = Singular(capturedEt);
                // Reset filters when switching entity type
                _worldFilterName                = null;
                _worldFilterFaction             = null;
                _worldFilterArc                 = null;
                _worldFilterAlignment           = null;
                _worldFilterCommonKnowledge     = false;
                _worldFilterHistoricalKnowledge = false;
                _worldSortMode                  = "name_asc";
                BuildWorldContent();
            };
            tabs.Children.Add(tab);
        }

        // "🗺 Boards" gallery tab
        var boardTab = new Button
        {
            Content         = Properties.Loc.S("World_Boards"),
            FontSize        = 13,
            FontFamily      = new FontFamily("Segoe UI"),
            FontWeight      = _worldBoardsMode ? FontWeights.SemiBold : FontWeights.Normal,
            Padding         = new Thickness(14, 8, 14, 10),
            BorderThickness = new Thickness(0),
            Cursor          = Cursors.Hand,
            Background      = Brushes.Transparent
        };
        boardTab.SetResourceReference(Button.ForegroundProperty,
            _worldBoardsMode ? "AccentHighlightBrush" : "SidebarDimBrush");
        boardTab.Click += (_, _) => { _worldBoardsMode = true; BuildWorldContent(); };
        tabs.Children.Add(boardTab);

        // Right-side button area
        var rightPanel = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(rightPanel, 1);
        tabRow.Children.Add(rightPanel);

        // ── Board gallery mode ─────────────────────────────────────────────
        if (_worldBoardsMode)
        {
            BuildWorldBoardGallery(projFolder);
            return;
        }

        // "+ New" button (card-grid mode only)
        var newBtn = MakeFilePanelButton($"+ New {_worldActiveType}", isPrimary: true);
        newBtn.FontSize = 12;
        newBtn.Padding  = new Thickness(12, 6, 12, 6);
        newBtn.Margin   = new Thickness(0, 4, 0, 4);
        rightPanel.Children.Add(newBtn);

        // ── Filter bar ─────────────────────────────────────────────────────
        var filterBorder = new Border
        {
            Padding = new Thickness(16, 6, 16, 6),
            BorderThickness = new Thickness(0, 1, 0, 1)
        };
        filterBorder.SetResourceReference(Border.BackgroundProperty,  "SidebarBgBrush");
        filterBorder.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");

        // Insert as Row 1; push scroll area to Row 2
        WorldContent.RowDefinitions.Insert(1, new RowDefinition { Height = GridLength.Auto });
        WorldContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid.SetRow(filterBorder, 1);
        WorldContent.Children.Add(filterBorder);

        // Load ALL entities for filter option extraction
        var allEntities = WorldEntityService.List(projFolder, _worldActiveType);

        // Extract unique filter values
        var factionVals   = allEntities.Where(e => e.Fields.TryGetValue("Faction",   out var v) && !string.IsNullOrWhiteSpace(v)).Select(e => e.Fields["Faction"]).Distinct().OrderBy(x => x).ToList();
        var arcVals       = allEntities.Where(e => e.Fields.TryGetValue("Arc",       out var v) && !string.IsNullOrWhiteSpace(v)).Select(e => e.Fields["Arc"]).Distinct().OrderBy(x => x).ToList();
        var alignVals     = allEntities.Where(e => e.Fields.TryGetValue("Alignment", out var v) && !string.IsNullOrWhiteSpace(v)).Select(e => e.Fields["Alignment"]).Distinct().OrderBy(x => x).ToList();
        bool hasMissingFaction   = allEntities.Any(e => !e.Fields.TryGetValue("Faction",   out var v) || string.IsNullOrWhiteSpace(v));
        bool hasMissingArc       = allEntities.Any(e => !e.Fields.TryGetValue("Arc",       out var v) || string.IsNullOrWhiteSpace(v));
        bool hasMissingAlignment = allEntities.Any(e => !e.Fields.TryGetValue("Alignment", out var v) || string.IsNullOrWhiteSpace(v));

        var filterRow = new WrapPanel { Orientation = Orientation.Horizontal };
        filterBorder.Child = filterRow;

        // Name search
        var nameBox = new TextBox
        {
            Text = _worldFilterName ?? "", Width = 150, FontSize = 11,
            Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 2, 8, 2),
            BorderThickness = new Thickness(1), ToolTip = "Search by name"
        };
        nameBox.SetResourceReference(TextBox.BackgroundProperty,  "ControlBgBrush");
        nameBox.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        nameBox.SetResourceReference(TextBox.ForegroundProperty,  "ContentTextBrush");
        nameBox.TextChanged += (_, _) => { _worldFilterName = string.IsNullOrWhiteSpace(nameBox.Text) ? null : nameBox.Text; BuildWorldContent(); };
        filterRow.Children.Add(nameBox);

        // Helper to build a filter dropdown
        void AddFilterCombo(string allLabel, List<string> options, bool hasMissing, string noneLabel,
                            Func<string?> getCurrent, Action<string?> setCurrent)
        {
            if (options.Count == 0 && !hasMissing) return;
            var combo = new ComboBox { Width = 140, FontSize = 11, Margin = new Thickness(0, 2, 8, 2) };
            combo.Items.Add(allLabel);
            if (hasMissing) combo.Items.Add(noneLabel);
            foreach (var v in options) combo.Items.Add(v);
            // Restore current selection
            var cur = getCurrent();
            if (cur == null) combo.SelectedIndex = 0;
            else if (cur == "<<none>>") combo.SelectedIndex = hasMissing ? 1 : 0;
            else { var idx = combo.Items.IndexOf(cur); combo.SelectedIndex = idx >= 0 ? idx : 0; }
            combo.SelectionChanged += (_, _) =>
            {
                var sel = combo.SelectedItem?.ToString();
                if (sel == null || sel == allLabel) setCurrent(null);
                else if (sel == noneLabel)          setCurrent("<<none>>");
                else                                setCurrent(sel);
                BuildWorldContent();
            };
            filterRow.Children.Add(combo);
        }

        bool isLoreView = _worldActiveType == "Lore";

        if (factionVals.Count > 0 || hasMissingFaction)
            AddFilterCombo(Properties.Loc.S("World_AllFactions"), factionVals, hasMissingFaction, Properties.Loc.S("World_NoFaction"),
                () => _worldFilterFaction, v => _worldFilterFaction = v);
        if (arcVals.Count > 0 || hasMissingArc)
            AddFilterCombo(Properties.Loc.S("World_AllArcs"), arcVals, hasMissingArc, Properties.Loc.S("World_NoArc"),
                () => _worldFilterArc, v => _worldFilterArc = v);
        if (!isLoreView && (alignVals.Count > 0 || hasMissingAlignment))
            AddFilterCombo(Properties.Loc.S("World_AllAlignments"), alignVals, hasMissingAlignment, Properties.Loc.S("World_NoAlignment"),
                () => _worldFilterAlignment, v => _worldFilterAlignment = v);

        if (isLoreView)
        {
            CheckBox MakeKnowledgeCheck(string label, bool current, Action<bool> set)
            {
                var chk = new CheckBox
                {
                    Content = label, IsChecked = current,
                    FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
                    Margin = new Thickness(0, 2, 10, 2), VerticalAlignment = VerticalAlignment.Center
                };
                chk.SetResourceReference(CheckBox.ForegroundProperty, "ContentTextBrush");
                chk.Checked   += (_, _) => { set(true);  BuildWorldContent(); };
                chk.Unchecked += (_, _) => { set(false); BuildWorldContent(); };
                return chk;
            }
            filterRow.Children.Add(MakeKnowledgeCheck(Properties.Loc.S("World_CommonKnowledge"),    _worldFilterCommonKnowledge,    v => _worldFilterCommonKnowledge    = v));
            filterRow.Children.Add(MakeKnowledgeCheck(Properties.Loc.S("World_HistoricalKnowledge"), _worldFilterHistoricalKnowledge, v => _worldFilterHistoricalKnowledge = v));
        }

        // Sort buttons
        Button MakeSortBtn(string label, string mode)
        {
            var b = MakeFilePanelButton(label, isPrimary: _worldSortMode == mode);
            b.FontSize = 10; b.Padding = new Thickness(6, 3, 6, 3); b.Margin = new Thickness(0, 2, 4, 2);
            b.Click += (_, _) => { _worldSortMode = mode; BuildWorldContent(); };
            return b;
        }
        filterRow.Children.Add(MakeSortBtn("A→Z",  "name_asc"));
        filterRow.Children.Add(MakeSortBtn("Z→A",  "name_desc"));
        filterRow.Children.Add(MakeSortBtn("📅↑",  "date_asc"));
        filterRow.Children.Add(MakeSortBtn("📅↓",  "date_desc"));

        var resetBtn = MakeFilePanelButton(Properties.Loc.S("World_Reset"), isPrimary: false);
        resetBtn.FontSize = 10; resetBtn.Padding = new Thickness(6, 3, 6, 3); resetBtn.Margin = new Thickness(4, 2, 0, 2);
        resetBtn.Click += (_, _) =>
        {
            _worldFilterName                = null; _worldFilterFaction   = null;
            _worldFilterArc                 = null; _worldFilterAlignment = null;
            _worldFilterCommonKnowledge     = false;
            _worldFilterHistoricalKnowledge = false;
            _worldSortMode                  = "name_asc";
            nameBox.Text = "";
            BuildWorldContent();
        };
        filterRow.Children.Add(resetBtn);

        // Apply filters + sort
        var entities = allEntities.Where(e =>
        {
            if (!string.IsNullOrWhiteSpace(_worldFilterName) &&
                !e.Name.Contains(_worldFilterName, StringComparison.OrdinalIgnoreCase)) return false;
            if (_worldFilterFaction != null)
            {
                var v = e.Fields.TryGetValue("Faction", out var fv) ? fv : null;
                if (!(_worldFilterFaction == "<<none>>" ? string.IsNullOrWhiteSpace(v) : v == _worldFilterFaction)) return false;
            }
            if (_worldFilterArc != null)
            {
                var v = e.Fields.TryGetValue("Arc", out var av) ? av : null;
                if (!(_worldFilterArc == "<<none>>" ? string.IsNullOrWhiteSpace(v) : v == _worldFilterArc)) return false;
            }
            if (_worldFilterAlignment != null && e.EntityType != "Lore")
            {
                var v = e.Fields.TryGetValue("Alignment", out var alv) ? alv : null;
                if (!(_worldFilterAlignment == "<<none>>" ? string.IsNullOrWhiteSpace(v) : v == _worldFilterAlignment)) return false;
            }
            if (_worldFilterCommonKnowledge && e.EntityType == "Lore")
            {
                if (!e.Fields.TryGetValue("CommonKnowledge", out var ck) || ck != "true") return false;
            }
            if (_worldFilterHistoricalKnowledge && e.EntityType == "Lore")
            {
                if (!e.Fields.TryGetValue("HistoricalKnowledge", out var hk) || hk != "true") return false;
            }
            return true;
        }).ToList();

        switch (_worldSortMode)
        {
            case "name_desc": entities.Sort((a, b) => string.Compare(b.Name, a.Name, StringComparison.OrdinalIgnoreCase)); break;
            case "date_asc":  entities.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt)); break;
            case "date_desc": entities.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt)); break;
            default:          entities.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)); break;
        }

        // ── Card scroll area ───────────────────────────────────────────────
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(16, 14, 16, 16)
        };
        scroll.SetResourceReference(ScrollViewer.BackgroundProperty, "ContentBgBrush");
        Grid.SetRow(scroll, 2);
        WorldContent.Children.Add(scroll);

        void Refresh() { BuildWorldContent(); }

        bool filtersActive = _worldFilterName != null || _worldFilterFaction != null
                          || _worldFilterArc != null || _worldFilterAlignment != null;

        if (allEntities.Count == 0)
        {
            var emptyContainer = new Border
            {
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(24, 20, 24, 20), Margin = new Thickness(0, 4, 0, 0)
            };
            emptyContainer.SetResourceReference(Border.BackgroundProperty,  "ControlBgBrush");
            emptyContainer.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
            var emptyText = new TextBlock { Text = $"No {_worldActiveType.ToLower()}s yet.", FontSize = 15, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            emptyText.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
            var emptyHint = new TextBlock { Text = $"Click '+ New {_worldActiveType}' in the toolbar to create one.", FontSize = 13, FontFamily = new FontFamily("Segoe UI"), TextWrapping = TextWrapping.Wrap };
            emptyHint.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            var emptyStack = new StackPanel(); emptyStack.Children.Add(emptyText); emptyStack.Children.Add(emptyHint);
            emptyContainer.Child = emptyStack;
            scroll.Content = emptyContainer;
        }
        else if (entities.Count == 0 && filtersActive)
        {
            var noMatch = new TextBlock
            {
                Text = Properties.Loc.S("World_NoFilterResults"),
                FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4)
            };
            noMatch.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            scroll.Content = noMatch;
        }
        else
        {
            var cardsWrap = new WrapPanel { Orientation = Orientation.Horizontal, ItemWidth = 240 };
            List<WorldEntity> cardFactions = [];
            if (_worldActiveType is "Character" or "Location" or "Lore")
                cardFactions = WorldEntityService.List(projFolder, "Faction");
            foreach (var entity in entities)
                cardsWrap.Children.Add(BuildEntityCard(entity, projFolder, Refresh, cardFactions));
            scroll.Content = cardsWrap;
        }

        // Wire "+ New" after entities are loaded so Refresh captures the right scope
        newBtn.Click += (_, _) =>
        {
            var newEntity = new WorldEntity { EntityType = _worldActiveType };
            if (ShowEntityEditDialog(newEntity, projFolder, isNew: true))
            {
                WorldEntityService.Save(projFolder, newEntity);
                Refresh();
            }
        };
    }

    /// <summary>
    /// Creates a small faction-dot badge: white outer ring, coloured inner disc, tooltip = faction name.
    /// </summary>
    private static UIElement MakeFactionDot(string hexColor, string factionName)
    {
        Color col;
        try   { col = (Color)ColorConverter.ConvertFromString(hexColor)!; }
        catch { col = Colors.Gray; }

        var grid = new Grid
        {
            Width   = 16,
            Height  = 16,
            ToolTip = factionName,
            Margin  = new Thickness(0, 0, 4, 4)
        };
        var outer = new Ellipse
        {
            Width           = 16,
            Height          = 16,
            Fill            = Brushes.White,
            Stroke          = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)),
            StrokeThickness = 0.5
        };
        var inner = new Ellipse
        {
            Width  = 10,
            Height = 10,
            Fill   = new SolidColorBrush(col),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center
        };
        grid.Children.Add(outer);
        grid.Children.Add(inner);
        return grid;
    }

    private Border BuildEntityCard(WorldEntity entity, string projFolder, Action refresh,
                                   IReadOnlyList<WorldEntity>? allFactions = null)
    {
        var schema    = WorldEntitySchemas.For(entity.EntityType);
        bool isChar   = entity.EntityType == "Character";
        bool isLoc    = entity.EntityType == "Location";
        bool isFac    = entity.EntityType == "Faction";
        const double ThumbW = 62;

        // ── Resolve faction accent colour ──────────────────────────────────
        Color? factionAccent = null;
        if (isFac && !string.IsNullOrEmpty(entity.FactionColor))
            try { factionAccent = (Color)ColorConverter.ConvertFromString(entity.FactionColor)!; } catch { }

        // ── Card border ────────────────────────────────────────────────────
        var card = new Border
        {
            Width           = 228,
            Margin          = new Thickness(0, 0, 12, 12),
            Padding         = new Thickness(0),          // thumbnail column handles left edge
            CornerRadius    = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            ClipToBounds    = true
        };
        card.SetResourceReference(Border.BackgroundProperty,  "ControlBgBrush");
        if (factionAccent.HasValue)
            card.BorderBrush = new SolidColorBrush(
                Color.FromArgb(160, factionAccent.Value.R, factionAccent.Value.G, factionAccent.Value.B));
        else
            card.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");

        // ── Outer grid: thumbnail col + content col ────────────────────────
        var outerGrid = new Grid();
        outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        card.Child = outerGrid;

        // ── Thumbnail column ───────────────────────────────────────────────
        // Character: portrait (3:4),  Location: square image,  Faction: image or colour strip
        string? imgPath = null;
        double thumbH   = ThumbW;
        if (isChar && !string.IsNullOrWhiteSpace(entity.PortraitFileName))
        {
            imgPath = WorldEntityService.GetPortraitPath(projFolder, entity.PortraitFileName);
            thumbH  = ThumbW * 4.0 / 3.0;  // 3:4 portrait
        }
        else if ((isLoc || isFac) && !string.IsNullOrWhiteSpace(entity.ImageFileName))
        {
            imgPath = WorldEntityService.GetImagePath(projFolder, entity.ImageFileName);
            thumbH  = ThumbW;
        }

        var thumbCol = new Border
        {
            Width           = ThumbW,
            CornerRadius    = new CornerRadius(7, 0, 0, 7),
            ClipToBounds    = true,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Grid.SetColumn(thumbCol, 0);
        outerGrid.Children.Add(thumbCol);

        if (imgPath != null && System.IO.File.Exists(imgPath))
        {
            var bmp = ThumbnailService.LoadThumb(imgPath);
            if (bmp != null)
            {
                thumbCol.Child = new System.Windows.Controls.Image
                {
                    Source  = bmp,
                    Stretch = Stretch.UniformToFill
                };
            }
            else
            {
                // No image loaded — show color strip fallback
                thumbCol.Width = 6;
                if (factionAccent.HasValue)
                    thumbCol.Background = new SolidColorBrush(factionAccent.Value);
                else
                    thumbCol.SetResourceReference(Border.BackgroundProperty, "ControlBorderBrush");
            }
        }
        else if (factionAccent.HasValue)
        {
            // Faction: no image — wide colour strip
            thumbCol.Width  = 6;
            thumbCol.CornerRadius = new CornerRadius(7, 0, 0, 7);
            thumbCol.Background   = new SolidColorBrush(factionAccent.Value);
        }
        else
        {
            // Nothing to show — collapse the column
            thumbCol.Width = 0;
        }

        // ── Content column ─────────────────────────────────────────────────
        var inner = new StackPanel { Margin = new Thickness(12, 9, 12, 9) };
        Grid.SetColumn(inner, 1);
        outerGrid.Children.Add(inner);

        var nameText = new TextBlock
        {
            Text         = entity.Name,
            FontSize     = 13,
            FontWeight   = FontWeights.SemiBold,
            FontFamily   = new FontFamily("Segoe UI"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin       = new Thickness(0, 0, 0, 6)
        };
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "ControlTextBrush");
        inner.Children.Add(nameText);

        // Up to 3 schema field previews
        int maxFieldW = (int)(228 - (thumbCol.Width > 6 ? ThumbW : thumbCol.Width) - 24 - 4);
        foreach (var (field, _) in schema.Take(3))
        {
            if (!entity.Fields.TryGetValue(field, out var val) || string.IsNullOrWhiteSpace(val))
                continue;
            var row  = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
            var lbl  = new TextBlock { Text = field + ": ", FontSize = 10, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI") };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "ControlDimBrush");
            var vt   = new TextBlock { Text = val, FontSize = 10, FontFamily = new FontFamily("Segoe UI"),
                                       TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = maxFieldW };
            vt.SetResourceReference(TextBlock.ForegroundProperty, "ControlDimBrush");
            row.Children.Add(lbl); row.Children.Add(vt);
            inner.Children.Add(row);
        }

        // ── Faction membership dots (Characters + Locations) ─────────────
        if ((isChar || isLoc) && allFactions != null)
        {
            var myFactions = allFactions
                .Where(f => f.MemberIds.Contains(entity.Id) && !string.IsNullOrEmpty(f.FactionColor))
                .ToList();
            if (myFactions.Count > 0)
            {
                var sep = new Rectangle { Height = 1, Margin = new Thickness(0, 6, 0, 4), Opacity = 0.25 };
                sep.SetResourceReference(Rectangle.FillProperty, "ControlBorderBrush");
                inner.Children.Add(sep);
                var dotPanel = new WrapPanel { Orientation = Orientation.Horizontal };
                foreach (var f in myFactions)
                    dotPanel.Children.Add(MakeFactionDot(f.FactionColor, f.Name));
                inner.Children.Add(dotPanel);
            }
        }

        // ── Faction knowledge dots (Lore — factions stored on entity.FactionIds) ──
        bool isLoreCard = entity.EntityType == "Lore";
        if (isLoreCard && allFactions != null && entity.FactionIds.Count > 0)
        {
            var myFactions = allFactions
                .Where(f => entity.FactionIds.Contains(f.Id) && !string.IsNullOrEmpty(f.FactionColor))
                .ToList();
            if (myFactions.Count > 0)
            {
                var sep = new Rectangle { Height = 1, Margin = new Thickness(0, 6, 0, 4), Opacity = 0.25 };
                sep.SetResourceReference(Rectangle.FillProperty, "ControlBorderBrush");
                inner.Children.Add(sep);
                var dotPanel = new WrapPanel { Orientation = Orientation.Horizontal };
                foreach (var f in myFactions)
                    dotPanel.Children.Add(MakeFactionDot(f.FactionColor, f.Name));
                inner.Children.Add(dotPanel);
            }
        }

        // ── Context menu: edit + delete + send to board ───────────────────
        void DoDelete()
        {
            if (MessageBox.Show($"Delete {entity.EntityType} '{entity.Name}'?",
                    Properties.Loc.S("World_ConfirmDelete"), MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes) return;
            // Delete thumbnails for portraits/images
            if (!string.IsNullOrWhiteSpace(entity.PortraitFileName))
                ThumbnailService.DeleteThumb(WorldEntityService.GetPortraitPath(projFolder, entity.PortraitFileName));
            if (!string.IsNullOrWhiteSpace(entity.ImageFileName))
                ThumbnailService.DeleteThumb(WorldEntityService.GetImagePath(projFolder, entity.ImageFileName));
            WorldEntityService.Delete(projFolder, entity);
            refresh();
        }

        var ctx = new ContextMenu();

        var editItem = new MenuItem { Header = Properties.Loc.S("World_EditItem") };
        editItem.Click += (_, _) =>
        {
            if (_entityEditOpen) { ShowEntityReadOnlyDialog(entity); return; }
            var copy    = CloneEntity(entity);
            var oldName = entity.Name;
            if (ShowEntityEditDialog(copy, projFolder, isNew: false))
            {
                if (!string.Equals(copy.Name, oldName, StringComparison.Ordinal))
                    WorldEntityService.Rename(projFolder, copy, oldName);
                else
                    WorldEntityService.Save(projFolder, copy);
                refresh();
            }
        };
        ctx.Items.Add(editItem);
        ctx.Items.Add(new Separator());

        var delItem = new MenuItem { Header = Properties.Loc.S("World_DeleteItem") };
        delItem.Click += (_, _) => DoDelete();
        ctx.Items.Add(delItem);

        // "Send to World Board" submenu – boards loaded lazily on open.
        // WPF only fires SubmenuOpened when Items.Count > 0, so we seed one placeholder item
        // to make the arrow appear; the handler replaces it with real entries.
        var sendItem = new MenuItem { Header = Properties.Loc.S("World_SendToBoard") };
        sendItem.Items.Add(new MenuItem { Header = "…", IsEnabled = false }); // placeholder forces arrow
        sendItem.SubmenuOpened += (_, _) =>
        {
            sendItem.Items.Clear();
            var allBoards = WorldBoardRegistryService.Load(projFolder);
            if (allBoards.Count == 0)
            {
                sendItem.Items.Add(new MenuItem { Header = "(no boards created yet)", IsEnabled = false });
                return;
            }
            // Show only boards that accept this entity type
            var matching = allBoards
                .Where(b => b.EntityTypes.Any(t =>
                    string.Equals(t, entity.EntityType, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (matching.Count == 0)
            {
                sendItem.Items.Add(new MenuItem
                {
                    Header = $"(no board configured for {entity.EntityType}s)",
                    IsEnabled = false
                });
                return;
            }
            foreach (var b in matching)
            {
                var capturedBoard = b;
                var mi = new MenuItem { Header = capturedBoard.Symbol + "  " + capturedBoard.Name };
                mi.Click += (_, _) =>
                {
                    var bData = EntityBoardService.Load(projFolder, capturedBoard.Id);
                    if (bData.Positions.ContainsKey(entity.Id))
                    {
                        MessageBox.Show($"'{entity.Name}' is already on board \"{capturedBoard.Name}\".",
                            Properties.Loc.S("World_AlreadyOnBoard"), MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    // Place at a position snapped to the board's grid
                    double gs   = bData.GridSize > 0 ? bData.GridSize : 10;
                    double maxY = bData.Positions.Count > 0
                        ? bData.Positions.Values.Max(p => p.Y) : 60;
                    double placeY = Math.Round((maxY + 120) / gs) * gs;
                    bData.Positions[entity.Id] = new BoardPosition { X = 60, Y = placeY };
                    EntityBoardService.Save(projFolder, capturedBoard.Id, bData);
                    MessageBox.Show($"'{entity.Name}' added to board \"{capturedBoard.Name}\".",
                        Properties.Loc.S("Btn_Done"), MessageBoxButton.OK, MessageBoxImage.Information);
                };
                sendItem.Items.Add(mi);
            }
        };
        ctx.Items.Add(sendItem);
        card.ContextMenu = ctx;

        // Double-click opens the edit dialog directly
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount < 2) return;
            e.Handled = true;
            editItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        };

        return card;
    }

    /// <summary>
    /// Shows a modal dialog to create or edit a world entity.
    /// Returns true if the user confirmed; the entity is modified in place.
    /// </summary>
    private bool ShowEntityEditDialog(WorldEntity entity, string projFolder, bool isNew) =>
        WorldEntityEditDialog.Show(entity, projFolder, isNew, _currentThemePath, this, ref _entityEditOpen);

    /// <summary>
    /// Simple picker dialog: shows characters not already in excludeIds.
    /// Returns the selected character or null if cancelled.
    /// </summary>
    private WorldEntity? ShowCharacterPickerDialog(List<WorldEntity> characters,
                                                    List<string> excludeIds,
                                                    string projFolder)
    {
        var eligible = characters.Where(c => !excludeIds.Contains(c.Id)).ToList();
        if (eligible.Count == 0)
        {
            MessageBox.Show("All characters are already members of this faction.",
                "Nothing to add", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        WorldEntity? result = null;

        var win = new Window
        {
            Title                 = "Add Member",
            Width                 = 320,
            Height                = Math.Min(480, 80 + eligible.Count * 42),
            MinHeight             = 120,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ShowInTaskbar         = false,
            ResizeMode            = ResizeMode.CanResize
        };
        ApplyThemeToDialog(win);
        win.SetResourceReference(Window.BackgroundProperty, "ContentBgBrush");

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        win.Content = scroll;

        var list = new StackPanel { Margin = new Thickness(12, 12, 12, 12) };
        scroll.Content = list;

        foreach (var ch in eligible)
        {
            var captured = ch;
            var item = new Border
            {
                Padding         = new Thickness(12, 8, 12, 8),
                Margin          = new Thickness(0, 0, 0, 4),
                CornerRadius    = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Cursor          = Cursors.Hand
            };
            item.SetResourceReference(Border.BackgroundProperty,   "ControlBgBrush");
            item.SetResourceReference(Border.BorderBrushProperty,  "ControlBorderBrush");

            var nameBlock = new TextBlock
            {
                Text       = ch.Name,
                FontSize   = 13,
                FontFamily = new FontFamily("Segoe UI")
            };
            // Item sits on ControlBgBrush → use ControlTextBrush.
            nameBlock.SetResourceReference(TextBlock.ForegroundProperty, "ControlTextBrush");
            item.Child = nameBlock;

            item.MouseLeftButtonDown += (_, _) =>
            {
                result = captured;
                win.DialogResult = true;
            };
            list.Children.Add(item);
        }

        win.ShowDialog();
        return result;
    }

    private static WorldEntity CloneEntity(WorldEntity src) => new()
    {
        Id           = src.Id,
        Name         = src.Name,
        EntityType   = src.EntityType,
        CreatedAt    = src.CreatedAt,
        UpdatedAt    = src.UpdatedAt,
        Fields       = new Dictionary<string, string>(src.Fields),
        Notes        = src.Notes,
        FactionColor = src.FactionColor,
        MemberIds    = [..src.MemberIds]
    };

    // ── Simple input dialog ────────────────────────────────────────────────

    private string? ShowSimpleInputDialog(string title, string labelText, string initial)
    {
        var win = new Window
        {
            Title = title, Width = 380, Height = 160, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this, ShowInTaskbar = false
        };
        ApplyThemeToDialog(win);
        win.SetResourceReference(Window.BackgroundProperty, "ContentBgBrush");

        var stack = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        win.Content = stack;
        var lbl = new TextBlock { Text = labelText, FontSize = 12, Margin = new Thickness(0, 0, 0, 6) };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        stack.Children.Add(lbl);

        var tb = new TextBox { Text = initial, FontSize = 13, Padding = new Thickness(8, 5, 8, 5), BorderThickness = new Thickness(1) };
        tb.SetResourceReference(TextBox.BackgroundProperty,  "ControlBgBrush");
        tb.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        tb.SetResourceReference(TextBox.ForegroundProperty,  "ContentTextBrush");
        stack.Children.Add(tb);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        stack.Children.Add(btnRow);
        var cancelBtn = MakeFilePanelButton(Properties.Loc.S("Btn_Cancel"), isPrimary: false);
        cancelBtn.Padding = new Thickness(12, 5, 12, 5);
        cancelBtn.Click  += (_, _) => win.DialogResult = false;
        btnRow.Children.Add(cancelBtn);
        var okBtn = MakeFilePanelButton(Properties.Loc.S("Btn_OK"), isPrimary: true);
        okBtn.Padding = new Thickness(12, 5, 12, 5); okBtn.Margin = new Thickness(8, 0, 0, 0);
        okBtn.Click  += (_, _) => win.DialogResult = true;
        btnRow.Children.Add(okBtn);

        tb.Focus(); tb.SelectAll();
        tb.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Return) { win.DialogResult = true;  e.Handled = true; }
            if (e.Key == Key.Escape) { win.DialogResult = false; e.Handled = true; }
        };
        return win.ShowDialog() == true ? tb.Text.Trim() : null;
    }

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

        var typeLabel = new TextBlock { Text = entity.EntityType, FontSize = 11, Margin = new Thickness(0, 2, 0, 14) };
        typeLabel.SetResourceReference(TextBlock.ForegroundProperty, "AccentHighlightBrush");
        root.Children.Add(typeLabel);

        void AddField(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var lbl = new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            root.Children.Add(lbl);
            var val = new TextBlock { Text = value, FontSize = 13, FontFamily = new FontFamily("Segoe UI"), TextWrapping = TextWrapping.Wrap };
            val.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
            root.Children.Add(val);
        }
        foreach (var (field, _) in schema)
            if (entity.Fields.TryGetValue(field, out var v)) AddField(field, v);
        if (!string.IsNullOrWhiteSpace(entity.Notes)) AddField("Notes", entity.Notes);

        var closeBtn = MakeFilePanelButton("Close", isPrimary: false);
        closeBtn.HorizontalAlignment = HorizontalAlignment.Right;
        closeBtn.Padding = new Thickness(14, 7, 14, 7); closeBtn.Margin = new Thickness(0, 18, 0, 0);
        closeBtn.Click += (_, _) => win.Close();
        root.Children.Add(closeBtn);
        win.Show();
    }

    // ── World entity context for AI system prompts ─────────────────────────

    private string BuildWorldEntityContext()
    {
        if (_currentProjectFolder is null) return "";
        if (_currentProjectType?.HasWorldBuilding != true) return "";

        var entityTypes = GetWorldEntityTypes();
        var sb          = new System.Text.StringBuilder();
        bool hasContent = false;

        foreach (var etPlural in entityTypes)
        {
            var et       = etPlural.TrimEnd('s');
            var entities = WorldEntityService.List(_currentProjectFolder, et);
            if (entities.Count == 0) continue;

            if (!hasContent) { sb.Append("\n\n## World Entities"); hasContent = true; }
            sb.Append($"\n\n### {etPlural}");

            var schema = WorldEntitySchemas.For(et);
            foreach (var entity in entities)
            {
                sb.Append($"\n**{entity.Name}**");
                var parts = new List<string>();
                foreach (var (field, _) in schema)
                    if (entity.Fields.TryGetValue(field, out var fv) && !string.IsNullOrWhiteSpace(fv))
                        parts.Add($"{field}: {fv}");
                if (parts.Count > 0) sb.Append(" - " + string.Join("; ", parts));
                if (!string.IsNullOrWhiteSpace(entity.Notes)) sb.Append($"\n  Notes: {entity.Notes}");
            }

            // Relations from all boards for this entity type
            var boards = WorldBoardRegistryService.Load(_currentProjectFolder);
            var idToName = entities.ToDictionary(e => e.Id, e => e.Name);
            var relLines = new List<string>();
            foreach (var board in boards)
            {
                if (!board.EntityTypes.Contains(et)) continue;
                var boardData = EntityBoardService.Load(_currentProjectFolder, board.Id);
                foreach (var r in boardData.Relations)
                {
                    if (!idToName.ContainsKey(r.FromId) || !idToName.ContainsKey(r.ToId)) continue;
                    var cap = string.IsNullOrWhiteSpace(r.Caption) ? "related to" : r.Caption;
                    relLines.Add($"  - {idToName[r.FromId]} → {cap} → {idToName[r.ToId]}");
                }
            }
            if (relLines.Count > 0)
            {
                sb.Append($"\n\n**{etPlural} Relations:**");
                foreach (var rl in relLines) sb.Append($"\n{rl}");
            }
        }

        return hasContent ? sb.ToString() : "";
    }

    // ── Board gallery ─────────────────────────────────────────────────────

    private void BuildWorldBoardGallery(string projFolder)
    {
        var boards = WorldBoardRegistryService.Load(projFolder);

        // Apply type filter
        var filtered = _worldBoardsTypeFilter.Count == 0
            ? boards
            : boards.Where(b => b.EntityTypes.Any(et => _worldBoardsTypeFilter.Contains(et))).ToList();

        // Apply sort
        var sorted = _worldBoardsSort switch
        {
            "name_asc"  => filtered.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            "name_desc" => filtered.OrderByDescending(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            "date_asc"  => filtered.OrderBy(b => b.UpdatedAt).ToList(),
            _           => filtered.OrderByDescending(b => b.UpdatedAt).ToList()  // date_desc (default)
        };

        // Scroll area fills row 1
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(18, 14, 18, 18)
        };
        scroll.SetResourceReference(ScrollViewer.BackgroundProperty, "ContentBgBrush");
        Grid.SetRow(scroll, 1);
        WorldContent.Children.Add(scroll);

        var body = new StackPanel();
        scroll.Content = body;

        // ── Toolbar: sort + type filter + add ─────────────────────────────
        var toolStack = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        body.Children.Add(toolStack);

        // Row 1: Sort + New Board
        var sortRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        sortRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sortRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolStack.Children.Add(sortRow);

        var sortPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(sortPanel, 0);
        sortRow.Children.Add(sortPanel);

        var sortLbl = new TextBlock { Text = Properties.Loc.S("Projects_Sort"), FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        sortLbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        sortPanel.Children.Add(sortLbl);

        Button MakeSortBtn(string label, string key)
        {
            var b = MakeFilePanelButton(label, _worldBoardsSort == key);
            b.FontSize = 11; b.Padding = new Thickness(10, 4, 10, 4); b.Margin = new Thickness(0, 0, 4, 0);
            b.Click += (_, _) => { _worldBoardsSort = key; BuildWorldContent(); };
            return b;
        }
        sortPanel.Children.Add(MakeSortBtn("A→Z",        "name_asc"));
        sortPanel.Children.Add(MakeSortBtn("Z→A",        "name_desc"));
        sortPanel.Children.Add(MakeSortBtn("Date ↑",     "date_asc"));
        sortPanel.Children.Add(MakeSortBtn("Date ↓",     "date_desc"));

        var addBoardBtn = MakeFilePanelButton(Properties.Loc.S("World_NewBoard"), isPrimary: true);
        addBoardBtn.FontSize = 12; addBoardBtn.Padding = new Thickness(14, 6, 14, 6);
        Grid.SetColumn(addBoardBtn, 1);
        sortRow.Children.Add(addBoardBtn);
        addBoardBtn.Click += (_, _) =>
        {
            var newBoard = ShowNewBoardDialog(projFolder);
            if (newBoard is null) return;
            boards.Add(newBoard);
            WorldBoardRegistryService.Save(projFolder, boards);
            BuildWorldContent();
        };

        // Row 2: Content-type filter chips
        var filterRow = new StackPanel { Orientation = Orientation.Horizontal };
        toolStack.Children.Add(filterRow);

        var filterLbl = new TextBlock { Text = Properties.Loc.S("World_Filter"), FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        filterLbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        filterRow.Children.Add(filterLbl);

        foreach (var et in new[] { "Character", "Faction", "Location", "Lore" })
        {
            var capEt  = et;
            bool active = _worldBoardsTypeFilter.Contains(et);
            var chip = MakeFilePanelButton(et, active);
            chip.FontSize = 11; chip.Padding = new Thickness(10, 4, 10, 4); chip.Margin = new Thickness(0, 0, 4, 0);
            chip.Click += (_, _) =>
            {
                if (_worldBoardsTypeFilter.Contains(capEt)) _worldBoardsTypeFilter.Remove(capEt);
                else _worldBoardsTypeFilter.Add(capEt);
                BuildWorldContent();
            };
            filterRow.Children.Add(chip);
        }

        if (_worldBoardsTypeFilter.Count > 0)
        {
            var clearBtn = MakeFilePanelButton("✕ Clear", false);
            clearBtn.FontSize = 11; clearBtn.Padding = new Thickness(8, 4, 8, 4);
            clearBtn.Click += (_, _) => { _worldBoardsTypeFilter.Clear(); BuildWorldContent(); };
            filterRow.Children.Add(clearBtn);
        }

        // ── Tile grid ──────────────────────────────────────────────────────
        if (sorted.Count == 0)
        {
            var emptyBox = new Border
            {
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(28, 22, 28, 22), Margin = new Thickness(0, 4, 0, 0)
            };
            emptyBox.SetResourceReference(Border.BackgroundProperty,  "ControlBgBrush");
            emptyBox.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");

            var emptyStack = new StackPanel();
            emptyBox.Child = emptyStack;

            var emptyTitle = new TextBlock
            {
                Text = Properties.Loc.S("World_NoBoardsYet"), FontSize = 15, FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 0, 0, 8)
            };
            emptyTitle.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
            emptyStack.Children.Add(emptyTitle);

            var emptyHint = new TextBlock
            {
                Text = Properties.Loc.S("World_NoBoardsHint"),
                FontSize = 13, FontFamily = new FontFamily("Segoe UI"), TextWrapping = TextWrapping.Wrap
            };
            emptyHint.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            emptyStack.Children.Add(emptyHint);

            body.Children.Add(emptyBox);
            return;
        }

        var tileWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        body.Children.Add(tileWrap);

        foreach (var board in sorted)
        {
            var capturedBoard = board;
            var entityCount   = board.EntityTypes.Sum(et => WorldEntityService.List(projFolder, et).Count);

            var tile = new Border
            {
                Width = 160, Height = 185,
                Margin = new Thickness(0, 0, 14, 14),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                    { BlurRadius = 8, ShadowDepth = 2, Opacity = 0.18, Color = Colors.Black }
            };
            tile.SetResourceReference(Border.BackgroundProperty,  "ControlBgBrush");
            tile.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");

            var tileInner = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            tile.Child = tileInner;

            // Symbol
            var symBlock = new TextBlock
            {
                Text = board.Symbol, FontSize = 40,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 22, 0, 8),
                TextAlignment = TextAlignment.Center
            };
            symBlock.SetResourceReference(TextBlock.ForegroundProperty, "AccentHighlightBrush");
            tileInner.Children.Add(symBlock);

            // Name
            var nameBlock = new TextBlock
            {
                Text = board.Name, FontSize = 13, FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 140, Margin = new Thickness(8, 0, 8, 6)
            };
            nameBlock.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
            tileInner.Children.Add(nameBlock);

            // Entity type chips
            var chipPanel = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(4, 0, 4, 6) };
            tileInner.Children.Add(chipPanel);
            foreach (var et in board.EntityTypes)
            {
                var chip = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(2, 2, 2, 2), BorderThickness = new Thickness(1) };
                chip.SetResourceReference(Border.BackgroundProperty,  "SidebarBgBrush");
                chip.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
                var chipTb = new TextBlock { Text = et, FontSize = 9, FontFamily = new FontFamily("Segoe UI") };
                chipTb.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
                chip.Child = chipTb;
                chipPanel.Children.Add(chip);
            }

            // Entity count + date
            var countTb = new TextBlock
            {
                Text = $"{entityCount} entit{(entityCount == 1 ? "y" : "ies")}  ·  {board.UpdatedAt:MMM d, yyyy}",
                FontSize = 9, FontFamily = new FontFamily("Segoe UI"),
                HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 148, Margin = new Thickness(4, 0, 4, 0)
            };
            countTb.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            tileInner.Children.Add(countTb);

            // Hover
            tile.MouseEnter += (_, _) => tile.Opacity = 0.85;
            tile.MouseLeave += (_, _) => tile.Opacity = 1.0;

            // Click → open board window (singleton per board ID)
            tile.MouseLeftButtonUp += (_, _) => OpenOrActivateBoardWindow(projFolder, capturedBoard);

            // Context menu: rename, settings, delete
            var ctx = new ContextMenu();

            var renameItem = new MenuItem { Header = Properties.Loc.S("World_RenameBoard") };
            renameItem.Click += (_, _) =>
            {
                var newName = ShowSimpleInputDialog(Properties.Loc.S("World_RenameBoardTitle"), Properties.Loc.S("World_BoardNameLabel"), capturedBoard.Name);
                if (newName is null || newName == capturedBoard.Name) return;
                capturedBoard.Name    = newName;
                capturedBoard.UpdatedAt = DateTime.UtcNow;
                WorldBoardRegistryService.Save(projFolder, boards);
                if (_openBoardWindows.TryGetValue(capturedBoard.Id, out var bw))
                    bw.Title = capturedBoard.Symbol + "  " + capturedBoard.Name;
                BuildWorldContent();
            };
            ctx.Items.Add(renameItem);

            var settingsItem = new MenuItem { Header = Properties.Loc.S("World_BoardSettings") };
            settingsItem.Click += (_, _) =>
            {
                if (ShowBoardSettingsDialog(capturedBoard))
                {
                    capturedBoard.UpdatedAt = DateTime.UtcNow;
                    WorldBoardRegistryService.Save(projFolder, boards);
                    if (_openBoardWindows.TryGetValue(capturedBoard.Id, out var bw))
                    {
                        bw.Title = capturedBoard.Symbol + "  " + capturedBoard.Name;
                        bw.BuildBoardContent();
                    }
                    BuildWorldContent();
                }
            };
            ctx.Items.Add(settingsItem);

            ctx.Items.Add(new Separator());

            var deleteItem = new MenuItem { Header = Properties.Loc.S("World_DeleteBoard") };
            deleteItem.Click += (_, _) =>
            {
                if (MessageBox.Show($"Delete board \"{capturedBoard.Name}\"?\nAll card positions and relations will be lost.",
                        Properties.Loc.S("World_ConfirmDelete"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                boards.Remove(capturedBoard);
                WorldBoardRegistryService.Save(projFolder, boards);
                if (_openBoardWindows.TryGetValue(capturedBoard.Id, out var bw)) bw.Close();
                BuildWorldContent();
            };
            ctx.Items.Add(deleteItem);

            tile.ContextMenu = ctx;
            tileWrap.Children.Add(tile);
        }
    }

    private void OpenOrActivateBoardWindow(string projFolder, WorldBoard board)
    {
        // Bring existing window to front if already open
        if (_openBoardWindows.TryGetValue(board.Id, out var existing))
        {
            if (existing.IsLoaded) { existing.Activate(); return; }
            _openBoardWindows.Remove(board.Id);
        }

        var win = new WorldBoardWindow(projFolder, board, _currentThemePath);
        _openBoardWindows[board.Id] = win;
        win.Closed += (_, _) => _openBoardWindows.Remove(board.Id);
        win.Show();
    }

    /// <summary>Dialog to create a new board: name, symbol, entity types.</summary>
    private WorldBoard? ShowNewBoardDialog(string projFolder)
    {
        var availableTypes = WorldEntitySchemas.All.Keys.ToList();
        // Pre-select the types this project type actually uses; fall back to all if none are known
        var projectTypes   = GetWorldEntityTypes().Select(t => t.TrimEnd('s')).ToHashSet();
        var selectedTypes  = projectTypes.Count > 0
            ? projectTypes
            : new HashSet<string>(availableTypes);
        string selectedSymbol = WorldBoardRegistryService.SymbolPalette[0];

        var win = new Window
        {
            Title = Properties.Loc.S("World_NewBoardTitle"), Width = 440,
            SizeToContent = SizeToContent.Height, MaxHeight = 620,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this, ShowInTaskbar = false
        };
        ApplyThemeToDialog(win);
        win.SetResourceReference(Window.BackgroundProperty, "ContentBgBrush");

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(24, 20, 24, 20) };
        win.Content = scroll;
        var root = new StackPanel();
        scroll.Content = root;

        // Name
        var nameLbl = new TextBlock { Text = Properties.Loc.S("World_BoardName"), FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        nameLbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        root.Children.Add(nameLbl);
        var nameBox = new TextBox { Text = "New Board", FontSize = 13, Padding = new Thickness(8, 5, 8, 5), BorderThickness = new Thickness(1) };
        nameBox.SetResourceReference(TextBox.BackgroundProperty,  "ControlBgBrush");
        nameBox.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        nameBox.SetResourceReference(TextBox.ForegroundProperty,  "ContentTextBrush");
        root.Children.Add(nameBox);

        // Symbol picker
        var symLbl = new TextBlock { Text = Properties.Loc.S("World_Symbol"), FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 8) };
        symLbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        root.Children.Add(symLbl);

        var symGrid = new WrapPanel { Orientation = Orientation.Horizontal };
        root.Children.Add(symGrid);
        var symBorders = new List<Border>();

        void UpdateSymSel()
        {
            foreach (var sb in symBorders)
            {
                bool sel = (string)sb.Tag == selectedSymbol;
                sb.BorderThickness = new Thickness(sel ? 2 : 0);
                sb.SetResourceReference(Border.BorderBrushProperty, sel ? "AccentHighlightBrush" : "ControlBorderBrush");
            }
        }
        foreach (var sym in WorldBoardRegistryService.SymbolPalette)
        {
            var capturedSym = sym;
            var sb = new Border
            {
                Width = 44, Height = 44, CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, Tag = sym
            };
            sb.SetResourceReference(Border.BackgroundProperty, "ControlBgBrush");
            var st = new TextBlock { Text = sym, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            sb.Child = st;
            sb.MouseLeftButtonDown += (_, _) => { selectedSymbol = capturedSym; UpdateSymSel(); };
            symBorders.Add(sb);
            symGrid.Children.Add(sb);
        }
        UpdateSymSel();

        // Entity types
        var etLbl = new TextBlock { Text = Properties.Loc.S("World_EntityTypes"), FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 8) };
        etLbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        root.Children.Add(etLbl);

        foreach (var et in availableTypes)
        {
            var capturedEt = et;
            var chk = new CheckBox
            {
                Content = WorldEntitySchemas.LocalizeEntityType(et), IsChecked = selectedTypes.Contains(et),
                FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand
            };
            chk.SetResourceReference(CheckBox.ForegroundProperty, "ContentTextBrush");
            chk.Checked   += (_, _) => selectedTypes.Add(capturedEt);
            chk.Unchecked += (_, _) => selectedTypes.Remove(capturedEt);
            root.Children.Add(chk);
        }

        // Buttons
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        root.Children.Add(btnRow);
        var cancelBtn = MakeFilePanelButton(Properties.Loc.S("Btn_Cancel"), isPrimary: false);
        cancelBtn.Padding = new Thickness(14, 7, 14, 7);
        cancelBtn.Click  += (_, _) => win.DialogResult = false;
        btnRow.Children.Add(cancelBtn);
        var createBtn = MakeFilePanelButton(Properties.Loc.S("World_CreateBoard"), isPrimary: true);
        createBtn.Padding = new Thickness(14, 7, 14, 7);
        createBtn.Margin  = new Thickness(8, 0, 0, 0);
        createBtn.Click  += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text)) { MessageBox.Show("Name cannot be empty.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (selectedTypes.Count == 0)               { MessageBox.Show("Select at least one entity type.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            win.DialogResult = true;
        };
        btnRow.Children.Add(createBtn);

        nameBox.Focus(); nameBox.SelectAll();
        if (win.ShowDialog() != true) return null;

        return new WorldBoard
        {
            Name        = nameBox.Text.Trim(),
            Symbol      = selectedSymbol,
            EntityTypes = availableTypes.Where(et => selectedTypes.Contains(et)).ToList()
        };
    }

    /// <summary>Dialog to edit an existing board's symbol and entity types.</summary>
    private bool ShowBoardSettingsDialog(WorldBoard board)
    {
        var availableTypes = WorldEntitySchemas.All.Keys.ToList();
        var selectedTypes  = new HashSet<string>(board.EntityTypes);
        string selectedSymbol = board.Symbol;

        var win = new Window
        {
            Title = $"{Properties.Loc.S("World_BoardSettingsTitle")} — {board.Name}", Width = 440,
            SizeToContent = SizeToContent.Height, MaxHeight = 600,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this, ShowInTaskbar = false
        };
        ApplyThemeToDialog(win);
        win.SetResourceReference(Window.BackgroundProperty, "ContentBgBrush");

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(24, 20, 24, 20) };
        win.Content = scroll;
        var root = new StackPanel();
        scroll.Content = root;

        // Symbol picker
        var symLbl = new TextBlock { Text = Properties.Loc.S("World_Symbol"), FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        symLbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        root.Children.Add(symLbl);

        var symGrid = new WrapPanel { Orientation = Orientation.Horizontal };
        root.Children.Add(symGrid);
        var symBorders = new List<Border>();

        void UpdateSymSel()
        {
            foreach (var sb in symBorders)
            {
                bool sel = (string)sb.Tag == selectedSymbol;
                sb.BorderThickness = new Thickness(sel ? 2 : 0);
                sb.SetResourceReference(Border.BorderBrushProperty, sel ? "AccentHighlightBrush" : "ControlBorderBrush");
            }
        }
        foreach (var sym in WorldBoardRegistryService.SymbolPalette)
        {
            var capturedSym = sym;
            var sb = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, Tag = sym };
            sb.SetResourceReference(Border.BackgroundProperty, "ControlBgBrush");
            var st = new TextBlock { Text = sym, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            sb.Child = st;
            sb.MouseLeftButtonDown += (_, _) => { selectedSymbol = capturedSym; UpdateSymSel(); };
            symBorders.Add(sb);
            symGrid.Children.Add(sb);
        }
        UpdateSymSel();

        // Entity types
        var etLbl = new TextBlock { Text = Properties.Loc.S("World_EntityTypes"), FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 8) };
        etLbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        root.Children.Add(etLbl);

        foreach (var et in availableTypes)
        {
            var capturedEt = et;
            var chk = new CheckBox { Content = WorldEntitySchemas.LocalizeEntityType(et), IsChecked = selectedTypes.Contains(et), FontSize = 13, FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand };
            chk.SetResourceReference(CheckBox.ForegroundProperty, "ContentTextBrush");
            chk.Checked   += (_, _) => selectedTypes.Add(capturedEt);
            chk.Unchecked += (_, _) => selectedTypes.Remove(capturedEt);
            root.Children.Add(chk);
        }

        // Buttons
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        root.Children.Add(btnRow);
        var cancelBtn = MakeFilePanelButton(Properties.Loc.S("Btn_Cancel"), isPrimary: false);
        cancelBtn.Padding = new Thickness(14, 7, 14, 7);
        cancelBtn.Click  += (_, _) => win.DialogResult = false;
        btnRow.Children.Add(cancelBtn);
        var saveBtn = MakeFilePanelButton(Properties.Loc.S("Btn_Save"), isPrimary: true);
        saveBtn.Padding = new Thickness(14, 7, 14, 7);
        saveBtn.Margin  = new Thickness(8, 0, 0, 0);
        saveBtn.Click  += (_, _) =>
        {
            if (selectedTypes.Count == 0) { MessageBox.Show("Select at least one entity type.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            win.DialogResult = true;
        };
        btnRow.Children.Add(saveBtn);

        if (win.ShowDialog() != true) return false;
        board.Symbol      = selectedSymbol;
        board.EntityTypes = availableTypes.Where(et => selectedTypes.Contains(et)).ToList();
        return true;
    }
}

