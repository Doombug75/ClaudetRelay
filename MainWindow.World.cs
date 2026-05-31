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

    private string _worldActiveType = "";   // which entity-type tab is selected

    // ── Board view state ───────────────────────────────────────────────────
    private bool             _worldBoardMode   = false;   // true = canvas board; false = card grid
    private EntityBoardData? _currentBoardData = null;
    private Canvas?          _boardCanvas      = null;
    private bool             _boardConnectMode = false;
    private string?          _boardConnSrcId   = null;
    private bool             _entityEditOpen   = false;   // true while ShowEntityEditDialog is open

    // Board rendering maps (cleared & rebuilt by BuildWorldBoard)
    private readonly Dictionary<string, Border>          _boardCards         = new();
    private readonly Dictionary<string, (Line L1, Line? L2)> _boardLines     = new();
    private readonly Dictionary<string, Border>          _boardCaptionBadges = new();

    private void ShowWorldPanel(bool show)
    {
        if (show && _currentProjectFolder is not null)
        {
            ShowRoadmapPanel(false);
            ShowFilesPanel(false);
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
        }
    }

    /// <summary>Returns the entity types for the current project type (e.g. Characters, Locations…).</summary>
    private string[] GetWorldEntityTypes()
    {
        var wf = _currentProjectType?.GetWorldFolderList();
        return wf is { Length: > 0 } ? wf : ["Characters", "Locations"];
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
            var isActive    = !_worldBoardMode &&
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
                _worldBoardMode  = false;
                _worldActiveType = Singular(capturedEt);
                BuildWorldContent();
            };
            tabs.Children.Add(tab);
        }

        // "🗺 Board" tab
        var boardTab = new Button
        {
            Content         = "🗺 Board",
            FontSize        = 13,
            FontFamily      = new FontFamily("Segoe UI"),
            FontWeight      = _worldBoardMode ? FontWeights.SemiBold : FontWeights.Normal,
            Padding         = new Thickness(14, 8, 14, 10),
            BorderThickness = new Thickness(0),
            Cursor          = Cursors.Hand,
            Background      = Brushes.Transparent
        };
        boardTab.SetResourceReference(Button.ForegroundProperty,
            _worldBoardMode ? "AccentHighlightBrush" : "SidebarDimBrush");
        boardTab.Click += (_, _) => { _worldBoardMode = true; BuildWorldContent(); };
        tabs.Children.Add(boardTab);

        // Right-side button area
        var rightPanel = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(rightPanel, 1);
        tabRow.Children.Add(rightPanel);

        // ── Board mode - build canvas and return early ─────────────────────
        if (_worldBoardMode)
        {
            BuildWorldBoard(projFolder);
            return;
        }

        // "+ New" button (card-grid mode only)
        var newBtn = MakeFilePanelButton($"+ New {_worldActiveType}", isPrimary: true);
        newBtn.FontSize = 12;
        newBtn.Padding  = new Thickness(12, 6, 12, 6);
        newBtn.Margin   = new Thickness(0, 4, 0, 4);
        rightPanel.Children.Add(newBtn);

        // ── Card scroll area ───────────────────────────────────────────────
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(16, 14, 16, 16)
        };
        // Apply theme background so WPF's Aero2 default system-white
        // does not bleed through behind the cards.
        scroll.SetResourceReference(ScrollViewer.BackgroundProperty, "ContentBgBrush");
        Grid.SetRow(scroll, 1);
        WorldContent.Children.Add(scroll);

        // Load entities for active type
        var entities = WorldEntityService.List(projFolder, _worldActiveType);

        void Refresh() { BuildWorldContent(); }

        if (entities.Count == 0)
        {
            // Empty state - styled container with proper visual separation
            var emptyContainer = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(24, 20, 24, 20),
                Margin = new Thickness(0, 4, 0, 0)
            };
            emptyContainer.SetResourceReference(Border.BackgroundProperty, "ControlBgBrush");
            emptyContainer.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");

            var emptyText = new TextBlock
            {
                Text        = $"No {_worldActiveType.ToLower()}s yet.",
                FontSize    = 15,
                FontWeight  = FontWeights.SemiBold,
                FontFamily  = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap,
                Margin      = new Thickness(0, 0, 0, 8)
            };
            emptyText.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");

            var emptyHint = new TextBlock
            {
                Text        = $"Click '+ New {_worldActiveType}' in the toolbar to create one.",
                FontSize    = 13,
                FontFamily  = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap
            };
            emptyHint.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");

            var emptyStack = new StackPanel();
            emptyStack.Children.Add(emptyText);
            emptyStack.Children.Add(emptyHint);
            emptyContainer.Child = emptyStack;

            scroll.Content = emptyContainer;
        }
        else
        {
            var cardsWrap = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                ItemWidth   = 240
            };
            // Load factions once so character cards can show membership dots
            List<WorldEntity> cardFactions = [];
            if (string.Equals(_worldActiveType, "Character", StringComparison.OrdinalIgnoreCase))
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
        var schema = WorldEntitySchemas.For(entity.EntityType);

        var card = new Border
        {
            Width           = 228,
            Margin          = new Thickness(0, 0, 12, 12),
            Padding         = new Thickness(14, 12, 14, 12),
            CornerRadius    = new CornerRadius(8),
            BorderThickness = new Thickness(1),
        };
        card.SetResourceReference(Border.BackgroundProperty,   "ControlBgBrush");
        card.SetResourceReference(Border.BorderBrushProperty,  "ControlBorderBrush");

        var inner = new StackPanel();
        card.Child = inner;

        // Name — use ControlTextBrush: the card background is ControlBgBrush,
        // so ControlText is the semantically correct foreground pairing.
        var nameText = new TextBlock
        {
            Text       = entity.Name,
            FontSize   = 13,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin     = new Thickness(0, 0, 0, 8)
        };
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "ControlTextBrush");
        inner.Children.Add(nameText);

        // Up to 3 field previews
        foreach (var (field, _) in schema.Take(3))
        {
            if (!entity.Fields.TryGetValue(field, out var val) || string.IsNullOrWhiteSpace(val))
                continue;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
            var lbl = new TextBlock
            {
                Text      = field + ": ",
                FontSize  = 11,
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold
            };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "ControlDimBrush");
            var valText = new TextBlock
            {
                Text        = val,
                FontSize    = 11,
                FontFamily  = new FontFamily("Segoe UI"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth    = 148
            };
            valText.SetResourceReference(TextBlock.ForegroundProperty, "ControlDimBrush");
            row.Children.Add(lbl);
            row.Children.Add(valText);
            inner.Children.Add(row);
        }

        // Notes preview
        if (!string.IsNullOrWhiteSpace(entity.Notes))
        {
            var notesPreview = new TextBlock
            {
                Text         = entity.Notes,
                FontSize     = 11,
                FontFamily   = new FontFamily("Segoe UI"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight    = 36,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 4, 0, 0)
            };
            notesPreview.SetResourceReference(TextBlock.ForegroundProperty, "ControlDimBrush");
            inner.Children.Add(notesPreview);
        }

        // ── Faction colour dot (for Faction cards) ────────────────────────
        if (entity.EntityType == "Faction" && !string.IsNullOrEmpty(entity.FactionColor))
        {
            Color fCol;
            try   { fCol = (Color)ColorConverter.ConvertFromString(entity.FactionColor)!; }
            catch { fCol = Colors.Gray; }

            var factionDotRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 6, 0, 0)
            };
            var factionDotOuter = new Ellipse
            {
                Width = 12, Height = 12,
                Fill  = new SolidColorBrush(fCol),
                VerticalAlignment = VerticalAlignment.Center
            };
            var factionColorLabel = new TextBlock
            {
                Text      = entity.FactionColor,
                FontSize  = 9,
                FontFamily = new FontFamily("Segoe UI"),
                Margin    = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            factionColorLabel.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            factionDotRow.Children.Add(factionDotOuter);
            factionDotRow.Children.Add(factionColorLabel);
            inner.Children.Add(factionDotRow);
        }

        // ── Faction membership dots (for Character cards) ─────────────────
        if (entity.EntityType == "Character" && allFactions != null)
        {
            var myFactions = allFactions
                .Where(f => f.MemberIds.Contains(entity.Id) && !string.IsNullOrEmpty(f.FactionColor))
                .ToList();

            if (myFactions.Count > 0)
            {
                var dotSep = new Rectangle
                {
                    Height  = 1,
                    Margin  = new Thickness(0, 8, 0, 6),
                    Opacity = 0.3
                };
                dotSep.SetResourceReference(Rectangle.FillProperty, "ControlBorderBrush");
                inner.Children.Add(dotSep);

                var dotPanel = new WrapPanel { Orientation = Orientation.Horizontal };
                foreach (var f in myFactions)
                    dotPanel.Children.Add(MakeFactionDot(f.FactionColor, f.Name));
                inner.Children.Add(dotPanel);
            }
        }

        // ── Action buttons ─────────────────────────────────────────────────
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 10, 0, 0)
        };
        inner.Children.Add(btnRow);

        var editBtn = MakeFilePanelButton("✏ Edit", isPrimary: false);
        editBtn.FontSize = 11; editBtn.Padding = new Thickness(9, 4, 9, 4);
        editBtn.Click  += (_, _) =>
        {
            // If another entity is already being edited, open this one as read-only
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
        btnRow.Children.Add(editBtn);

        var delBtn = MakeFilePanelButton("🗑", isPrimary: false);
        delBtn.FontSize = 12; delBtn.Padding = new Thickness(8, 4, 8, 4);
        delBtn.Margin   = new Thickness(6, 0, 0, 0);
        delBtn.SetResourceReference(Button.ForegroundProperty, "AccentHighlightBrush");
        delBtn.ToolTip  = $"Delete {entity.Name}";
        delBtn.Click   += (_, _) =>
        {
            if (MessageBox.Show($"Delete {entity.EntityType} '{entity.Name}'?",
                    "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes) return;
            WorldEntityService.Delete(projFolder, entity);
            refresh();
        };
        btnRow.Children.Add(delBtn);

        return card;
    }

    /// <summary>
    /// Shows a modal dialog to create or edit a world entity.
    /// Returns true if the user confirmed; the entity is modified in place.
    /// </summary>
    private bool ShowEntityEditDialog(WorldEntity entity, string projFolder, bool isNew)
    {
        var schema  = WorldEntitySchemas.For(entity.EntityType);
        var bgBrush = (Brush)FindResource("ContentBgBrush");
        var fg      = (Brush)FindResource("ContentTextBrush");
        var dimFg   = (Brush)FindResource("SidebarDimBrush");
        var inputBg = (Brush)FindResource("ControlBgBrush");
        var border  = (Brush)FindResource("ControlBorderBrush");

        bool isFaction = string.Equals(entity.EntityType, "Faction",
                                       StringComparison.OrdinalIgnoreCase);

        var win = new Window
        {
            Title                 = isNew ? $"New {entity.EntityType}" : $"Edit {entity.EntityType}",
            Width                 = 520,
            Height                = isFaction ? 720 : 600,
            MinWidth              = 400,
            MinHeight             = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ShowInTaskbar         = false,
            ResizeMode            = ResizeMode.CanResize
        };
        ApplyThemeToDialog(win);
        win.SetResourceReference(Window.BackgroundProperty, "ContentBgBrush");

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(24, 20, 24, 16)
        };
        win.Content = scroll;

        var root = new StackPanel();
        scroll.Content = root;

        TextBox MakeField(string label, string hint, string value, bool multiline = false)
        {
            var lbl = new TextBlock
            {
                Text       = label,
                FontSize   = 12,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
                Margin     = new Thickness(0, 12, 0, 4)
            };
            // Use SetResourceReference so the label colour is resolved from the
            // dialog window's theme dictionary (merged via ApplyThemeToDialog).
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            root.Children.Add(lbl);

            // Apply ModernTextBox style so WPF's Aero2 default (white BG, black text)
            // never overrides the OXSUIT theme colours.
            var tbStyle = TryFindResource("ModernTextBox") as Style;
            var tb = new TextBox
            {
                Text              = value,
                FontSize          = 13,
                FontFamily        = new FontFamily("Segoe UI"),
                Style             = tbStyle,
                TextWrapping      = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                AcceptsReturn     = multiline,
                MinHeight         = multiline ? 80 : 0,
                MaxHeight         = multiline ? 160 : double.PositiveInfinity,
                VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled
            };
            if (!string.IsNullOrEmpty(hint))
                tb.ToolTip = hint;
            root.Children.Add(tb);
            return tb;
        }

        // ── Name ──────────────────────────────────────────────────────────
        var nameBox = MakeField("Name", "", entity.Name);

        // ── Schema fields ─────────────────────────────────────────────────
        var fieldBoxes = new Dictionary<string, TextBox>();
        foreach (var (field, hint) in schema)
        {
            entity.Fields.TryGetValue(field, out var existing);
            fieldBoxes[field] = MakeField(field, hint, existing ?? "");
        }

        // ── Faction-only extras: colour + members ─────────────────────────
        string   selectedFactionColor  = entity.FactionColor;
        var      workingMemberIds      = entity.MemberIds.ToList();
        WrapPanel? memberChipPanel     = null;
        Dictionary<string, WorldEntity>? charById = null;

        if (isFaction)
        {
            // ── Colour picker ─────────────────────────────────────────────
            var colorLbl = new TextBlock
            {
                Text       = "Faction Colour",
                FontSize   = 12,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = dimFg,
                Margin     = new Thickness(0, 14, 0, 6)
            };
            root.Children.Add(colorLbl);

            var colorPanel = new WrapPanel { Orientation = Orientation.Horizontal };
            root.Children.Add(colorPanel);

            var swatches = new List<Border>();
            void UpdateSwatchSelection()
            {
                foreach (var sw in swatches)
                {
                    bool isSelected = (string)sw.Tag == selectedFactionColor;
                    sw.BorderThickness = new Thickness(isSelected ? 3 : 0);
                    sw.BorderBrush     = isSelected
                        ? new SolidColorBrush(Colors.White)
                        : Brushes.Transparent;
                    sw.Width  = isSelected ? 30 : 28;
                    sw.Height = isSelected ? 30 : 28;
                }
            }

            foreach (var hex in WorldEntitySchemas.FactionColorPalette)
            {
                var capturedHex = hex;
                Color col;
                try { col = (Color)ColorConverter.ConvertFromString(hex)!; }
                catch { col = Colors.Gray; }

                var swatch = new Border
                {
                    Width        = 28,
                    Height       = 28,
                    CornerRadius = new CornerRadius(14),
                    Background   = new SolidColorBrush(col),
                    Margin       = new Thickness(0, 0, 6, 6),
                    Cursor       = Cursors.Hand,
                    ToolTip      = hex,
                    Tag          = capturedHex
                };
                swatch.MouseLeftButtonDown += (_, _) =>
                {
                    selectedFactionColor = capturedHex;
                    UpdateSwatchSelection();
                };
                swatches.Add(swatch);
                colorPanel.Children.Add(swatch);
            }
            UpdateSwatchSelection();

            // ── Members ───────────────────────────────────────────────────
            var memberLbl = new TextBlock
            {
                Text       = "Members",
                FontSize   = 12,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = dimFg,
                Margin     = new Thickness(0, 14, 0, 6)
            };
            root.Children.Add(memberLbl);

            var allChars = WorldEntityService.List(projFolder, "Character");
            charById = allChars.ToDictionary(c => c.Id);

            memberChipPanel = new WrapPanel { Orientation = Orientation.Horizontal };
            root.Children.Add(memberChipPanel);

            void RefreshMemberChips()
            {
                memberChipPanel.Children.Clear();
                foreach (var membId in workingMemberIds.ToList())
                {
                    if (!charById.TryGetValue(membId, out var ch)) continue;
                    var capturedId = membId;

                    var chip = new Border
                    {
                        CornerRadius    = new CornerRadius(12),
                        Padding         = new Thickness(8, 4, 6, 4),
                        Margin          = new Thickness(0, 0, 6, 6),
                        BorderThickness = new Thickness(1),
                        Background      = inputBg,
                        BorderBrush     = border,
                    };
                    var chipRow = new StackPanel { Orientation = Orientation.Horizontal };
                    var chipName = new TextBlock
                    {
                        Text       = ch.Name,
                        FontSize   = 11,
                        FontFamily = new FontFamily("Segoe UI"),
                        Foreground = fg,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var removeBtn = new TextBlock
                    {
                        Text       = " ✕",
                        FontSize   = 10,
                        Foreground = dimFg,
                        Cursor     = Cursors.Hand,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin     = new Thickness(4, 0, 0, 0)
                    };
                    removeBtn.MouseLeftButtonDown += (_, _) =>
                    {
                        workingMemberIds.Remove(capturedId);
                        RefreshMemberChips();
                    };
                    chipRow.Children.Add(chipName);
                    chipRow.Children.Add(removeBtn);
                    chip.Child = chipRow;
                    memberChipPanel.Children.Add(chip);
                }

                // "＋ Add member" chip
                if (allChars.Any(c => !workingMemberIds.Contains(c.Id)))
                {
                    var addChip = new Border
                    {
                        CornerRadius    = new CornerRadius(12),
                        Padding         = new Thickness(8, 4, 8, 4),
                        Margin          = new Thickness(0, 0, 6, 6),
                        BorderThickness = new Thickness(1),
                        Background      = inputBg,
                        BorderBrush     = border,
                        Cursor          = Cursors.Hand
                    };
                    var addText = new TextBlock
                    {
                        Text       = "＋ Add member",
                        FontSize   = 11,
                        FontFamily = new FontFamily("Segoe UI"),
                        Foreground = fg
                    };
                    addChip.Child = addText;
                    addChip.MouseLeftButtonDown += (_, _) =>
                    {
                        var picked = ShowCharacterPickerDialog(allChars, workingMemberIds, projFolder);
                        if (picked != null && !workingMemberIds.Contains(picked.Id))
                        {
                            workingMemberIds.Add(picked.Id);
                            RefreshMemberChips();
                        }
                    };
                    memberChipPanel.Children.Add(addChip);
                }
            }

            RefreshMemberChips();
        }

        // ── Notes (always last) ───────────────────────────────────────────
        var notesBox = MakeField("Notes", "Freeform notes", entity.Notes, multiline: true);

        // ── Buttons ───────────────────────────────────────────────────────
        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, 20, 0, 0)
        };
        root.Children.Add(btnRow);

        var cancelBtn = MakeFilePanelButton("Cancel", isPrimary: false);
        cancelBtn.Padding = new Thickness(16, 8, 16, 8);
        cancelBtn.Click  += (_, _) => win.DialogResult = false;
        btnRow.Children.Add(cancelBtn);

        var saveBtn = MakeFilePanelButton(isNew ? "Create" : "Save", isPrimary: true);
        saveBtn.Padding = new Thickness(16, 8, 16, 8);
        saveBtn.Margin  = new Thickness(8, 0, 0, 0);
        saveBtn.Click  += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text))
            {
                MessageBox.Show("Name cannot be empty.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            entity.Name  = nameBox.Text.Trim();
            entity.Notes = notesBox.Text.Trim();
            entity.Fields.Clear();
            foreach (var (field, tb) in fieldBoxes)
                if (!string.IsNullOrWhiteSpace(tb.Text))
                    entity.Fields[field] = tb.Text.Trim();
            if (isFaction)
            {
                entity.FactionColor = selectedFactionColor;
                entity.MemberIds    = workingMemberIds;
            }
            win.DialogResult = true;
        };
        btnRow.Children.Add(saveBtn);

        _entityEditOpen = true;
        var dialogResult = win.ShowDialog() == true;
        _entityEditOpen  = false;
        return dialogResult;
    }

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

    // ── Entity board view ─────────────────────────────────────────────────

    /// <summary>
    /// Builds the canvas-based board view (Row 1 of WorldContent).
    /// Called only when _worldBoardMode is true.
    /// </summary>
    private void BuildWorldBoard(string projFolder)
    {
        const double CardW       = 200;
        const double EstCardH    = 90;    // estimated card height; corrected after layout by deferred refresh
        const double DefaultColW = 260;
        const double DefaultRowH = 180;

        // Grid for board area: Row 0 = toolbar, Row 1 = canvas scroll
        var boardGrid = new Grid();
        boardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        boardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(boardGrid, 1);
        WorldContent.Children.Add(boardGrid);

        // ── Board toolbar ──────────────────────────────────────────────────
        var bToolbar = new Border
        {
            Padding         = new Thickness(14, 7, 14, 7),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        bToolbar.SetResourceReference(Border.BackgroundProperty,  "ControlBgBrush");
        bToolbar.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        Grid.SetRow(bToolbar, 0);
        boardGrid.Children.Add(bToolbar);

        var bToolPanel = new StackPanel { Orientation = Orientation.Horizontal };
        bToolbar.Child = bToolPanel;

        var connectBtn = MakeFilePanelButton(_boardConnectMode ? "🔗 Connecting…" : "🔗 Add Relation",
                                             isPrimary: _boardConnectMode);
        connectBtn.FontSize = 11; connectBtn.Padding = new Thickness(10, 4, 10, 4);
        connectBtn.Click += (_, _) =>
        {
            _boardConnectMode = !_boardConnectMode;
            _boardConnSrcId   = null;
            BuildWorldContent();
        };
        bToolPanel.Children.Add(connectBtn);

        var arrangeBtn = MakeFilePanelButton("📐 Auto-arrange", isPrimary: false);
        arrangeBtn.FontSize = 11;
        arrangeBtn.Padding  = new Thickness(10, 4, 10, 4);
        arrangeBtn.Margin   = new Thickness(6, 0, 0, 0);
        arrangeBtn.Click   += (_, _) => AutoArrangeBoard(projFolder);
        bToolPanel.Children.Add(arrangeBtn);

        var hint = _boardConnectMode
            ? (_boardConnSrcId == null
                ? "← Click a card to start the relation"
                : "← Now click the target card")
            : "Drag cards freely · Double-click to view · 🔗 to connect";
        var hintBlock = new TextBlock
        {
            Text              = hint,
            FontSize          = 11,
            FontFamily        = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(14, 0, 0, 0)
        };
        hintBlock.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        bToolPanel.Children.Add(hintBlock);

        // ── Quick-add buttons ──────────────────────────────────────────────
        var entityTypes = GetWorldEntityTypes();
        foreach (var et in entityTypes)
        {
            var capturedEt     = et;
            var singularType   = et.TrimEnd('s');
            var addLabel = "+ " + singularType;
            var addBtn = MakeFilePanelButton(addLabel, isPrimary: false);
            addBtn.FontSize = 11;
            addBtn.Padding  = new Thickness(10, 4, 10, 4);
            addBtn.Margin   = new Thickness(6, 0, 0, 0);
            addBtn.ToolTip  = "Add a new " + singularType + " to the board";
            addBtn.Click   += (_, _) =>
            {
                var newEntity = new WorldEntity { EntityType = singularType };
                if (ShowEntityEditDialog(newEntity, projFolder, isNew: true))
                {
                    WorldEntityService.Save(projFolder, newEntity);
                    BuildWorldContent();
                }
            };
            bToolPanel.Children.Add(addBtn);
        }

        // ── Canvas scroll area ─────────────────────────────────────────────
        var boardScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto
        };
        boardScroll.SetResourceReference(ScrollViewer.BackgroundProperty, "ContentBgBrush");
        Grid.SetRow(boardScroll, 1);
        boardGrid.Children.Add(boardScroll);

        var canvas = new Canvas { Width = 2400, Height = 1800 };
        canvas.SetResourceReference(Canvas.BackgroundProperty, "ContentBgBrush");
        boardScroll.Content = canvas;
        _boardCanvas        = canvas;

        // Load combined board data (all entity types share one file)
        _currentBoardData = EntityBoardService.Load(projFolder, "_world");
        _boardCards.Clear();
        _boardLines.Clear();
        _boardCaptionBadges.Clear();

        // Load ALL entity types for the board (entityTypes already declared above for toolbar buttons)
        entityTypes = GetWorldEntityTypes();
        var entities    = entityTypes
            .SelectMany(et => WorldEntityService.List(projFolder, et.TrimEnd('s')))
            .ToList();

        // Also load factions for showing membership dots on character cards
        var boardFactions = entities.Where(e => e.EntityType == "Faction").ToList();

        // Assign default positions grouped by entity type
        bool dirty = false;
        {
            var validIds = entities.Select(e => e.Id).ToHashSet();
            var stalePos = _currentBoardData.Positions.Keys.Where(k => !validIds.Contains(k)).ToList();
            foreach (var sk in stalePos) { _currentBoardData.Positions.Remove(sk); dirty = true; }

            // Group new entities by type so each type starts on a new row band
            var typeOrder = entityTypes.Select(t => t.TrimEnd('s')).ToList();
            int typeRow   = 0;
            foreach (var typeKey in typeOrder)
            {
                int col = 0;
                foreach (var entity in entities.Where(e => e.EntityType == typeKey))
                {
                    if (!_currentBoardData.Positions.ContainsKey(entity.Id))
                    {
                        _currentBoardData.Positions[entity.Id] = new BoardPosition
                        {
                            X = 40 + col * DefaultColW,
                            Y = 40 + typeRow * DefaultRowH
                        };
                        col++; if (col >= 5) { col = 0; typeRow++; }
                        dirty = true;
                    }
                }
                if (entities.Any(e => e.EntityType == typeKey)) typeRow++;
            }
        }

        // Remove stale relations (entity deleted)
        var entityById = entities.ToDictionary(e => e.Id);
        var staleRels  = _currentBoardData.Relations
            .Where(r => !entityById.ContainsKey(r.FromId) || !entityById.ContainsKey(r.ToId))
            .ToList();
        foreach (var sr in staleRels) { _currentBoardData.Relations.Remove(sr); dirty = true; }
        if (dirty) EntityBoardService.Save(projFolder, "_world", _currentBoardData);

        // ── Render lines (z=0) + caption badges (z=1) ────────────────────
        foreach (var rel in _currentBoardData.Relations)
        {
            var fp = _currentBoardData.Positions[rel.FromId];
            var tp = _currentBoardData.Positions[rel.ToId];
            // Use estimated card height; corrected after layout by RefreshAllBoardLinePositions
            var x1 = fp.X + CardW / 2;  var y1 = fp.Y + EstCardH / 2;
            var x2 = tp.X + CardW / 2;  var y2 = tp.Y + EstCardH / 2;

            RenderRelationVisuals(canvas, rel.Id, x1, y1, x2, y2, rel);

            // Caption badge (z=1)
            var captionBadge = new Border
            {
                CornerRadius    = new CornerRadius(4),
                Padding         = new Thickness(5, 2, 5, 2),
                BorderThickness = new Thickness(1),
                Cursor          = Cursors.Hand
            };
            captionBadge.SetResourceReference(Border.BackgroundProperty,  "ControlBgBrush");
            captionBadge.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");

            var captionText = new TextBlock
            {
                Text         = rel.Caption,
                FontSize     = 10,
                FontFamily   = new FontFamily("Segoe UI"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth     = 140
            };
            captionText.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
            captionBadge.Child = captionText;

            Canvas.SetLeft(captionBadge, (x1 + x2) / 2 - 30);
            Canvas.SetTop (captionBadge, (y1 + y2) / 2 - 12);
            Panel.SetZIndex(captionBadge, 1);

            // Context menu: edit or delete
            var capturedRel = rel;
            var ctx = new ContextMenu();
            var editRelItem = new MenuItem { Header = "✏ Edit relation" };
            editRelItem.Click += (_, _) =>
            {
                var result = ShowRelationDialog(
                    capturedRel.Caption, capturedRel.LegendLabel,
                    capturedRel.LineStyle, capturedRel.LineColor, capturedRel.Thickness,
                    "Edit Relation");
                if (result is not null)
                {
                    capturedRel.Caption      = result.Caption;
                    capturedRel.LegendLabel  = result.LegendLabel;
                    capturedRel.LineStyle    = result.LineStyle;
                    capturedRel.LineColor    = result.LineColor;
                    capturedRel.Thickness    = result.Thickness;
                    EntityBoardService.Save(projFolder, "_world", _currentBoardData);
                    BuildWorldContent();
                }
            };
            var delRelItem = new MenuItem { Header = "🗑 Delete relation" };
            delRelItem.Click += (_, _) =>
            {
                _currentBoardData.Relations.Remove(capturedRel);
                EntityBoardService.Save(projFolder, "_world", _currentBoardData);
                BuildWorldContent();
            };
            ctx.Items.Add(editRelItem);
            ctx.Items.Add(delRelItem);
            captionBadge.ContextMenu = ctx;

            canvas.Children.Add(captionBadge);
            _boardCaptionBadges[rel.Id] = captionBadge;
        }

        // ── Render entity cards (z=2) ──────────────────────────────────────
        foreach (var entity in entities)
        {
            var pos  = _currentBoardData.Positions[entity.Id];
            var card = BuildBoardCard(entity, projFolder, boardFactions);
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
                if (_boardConnectMode)
                {
                    HandleBoardConnectClick(capturedId, projFolder);
                    e.Handled = true;
                    return;
                }
                if (e.ClickCount >= 2)
                {
                    ShowEntityReadOnlyDialog(capturedEntity);
                    e.Handled = true;
                    return;
                }
                isDragging = true;
                dragOffset = e.GetPosition(card);
                card.CaptureMouse();
                e.Handled  = true;
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
                if (_currentBoardData.Positions.TryGetValue(capturedId, out var bp))
                    { bp.X = nx; bp.Y = ny; }
                else
                    _currentBoardData.Positions[capturedId] = new BoardPosition { X = nx, Y = ny };
                EntityBoardService.Save(projFolder, "_world", _currentBoardData);
                e.Handled = true;
            };
        }

        // ── Floating legend overlay (top-left, above scroll area) ─────────
        var legendOverlay = BuildBoardLegend();
        Grid.SetRow(legendOverlay, 1);
        legendOverlay.HorizontalAlignment = HorizontalAlignment.Left;
        legendOverlay.VerticalAlignment   = VerticalAlignment.Top;
        legendOverlay.Margin              = new Thickness(10, 10, 0, 0);
        Panel.SetZIndex(legendOverlay, 10);
        boardGrid.Children.Add(legendOverlay);

        // ── Deferred line centering (uses actual rendered card sizes) ─────
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RefreshAllBoardLinePositions));
    }

    /// <summary>Updates all relation lines connected to the card being dragged (uses actual card size).</summary>
    private void UpdateBoardLines(string entityId, double newX, double newY)
    {
        if (_currentBoardData is null) return;
        double hw = 100, hh = 45;
        if (_boardCards.TryGetValue(entityId, out var movedCard) && movedCard.ActualWidth > 0)
        {
            hw = movedCard.ActualWidth  / 2;
            hh = movedCard.ActualHeight / 2;
        }
        var cx = newX + hw;
        var cy = newY + hh;

        foreach (var rel in _currentBoardData.Relations)
        {
            bool isFrom = rel.FromId == entityId;
            bool isTo   = rel.ToId   == entityId;
            if (!isFrom && !isTo) continue;
            if (!_boardLines.TryGetValue(rel.Id, out var lines)) continue;

            var otherId      = isFrom ? rel.ToId : rel.FromId;
            var (ox, oy)     = GetBoardCardCenter(otherId);
            double x1 = isFrom ? cx : ox,  y1 = isFrom ? cy : oy;
            double x2 = isFrom ? ox : cx,  y2 = isFrom ? oy : cy;

            ApplyLineGeometry(lines.L1, lines.L2, rel.LineStyle, rel.Thickness, x1, y1, x2, y2);

            if (_boardCaptionBadges.TryGetValue(rel.Id, out var badge))
            {
                Canvas.SetLeft(badge, (x1 + x2) / 2 - 30);
                Canvas.SetTop (badge, (y1 + y2) / 2 - 12);
            }
        }
    }

    /// <summary>Recalculates all line positions using actual card ActualWidth/ActualHeight.</summary>
    private void RefreshAllBoardLinePositions()
    {
        if (_currentBoardData is null) return;
        foreach (var rel in _currentBoardData.Relations)
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

    /// <summary>Returns the canvas center point of a board card using its actual rendered dimensions.</summary>
    private (double cx, double cy) GetBoardCardCenter(string entityId)
    {
        if (!_boardCards.TryGetValue(entityId, out var card)) return (100, 100);
        var x = Canvas.GetLeft(card);
        var y = Canvas.GetTop(card);
        var w = card.ActualWidth  > 0 ? card.ActualWidth  : 200;
        var h = card.ActualHeight > 0 ? card.ActualHeight : 90;
        return (x + w / 2, y + h / 2);
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

    /// <summary>Renders 1 or 2 Line elements for a relation and stores them in _boardLines.</summary>
    private void RenderRelationVisuals(
        Canvas canvas, string relId,
        double x1, double y1, double x2, double y2,
        BoardRelation rel)
    {
        bool       isDouble  = IsDoubleLine(rel.LineStyle);
        var        dashArray = GetDashArray(rel.LineStyle);
        double     thickness = Math.Max(1, rel.Thickness);
        PenLineCap cap       = rel.LineStyle is BoardLineStyle.Dotted or BoardLineStyle.DoubleDotted
                               ? PenLineCap.Round : PenLineCap.Flat;

        // Perpendicular offset for double lines
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

    /// <summary>Updates the geometry of existing line(s) for a relation (during drag or refresh).</summary>
    private static void ApplyLineGeometry(
        Line l1, Line? l2, BoardLineStyle style, double thickness,
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

    /// <summary>Handles a card click while in connect mode (step 1 = source, step 2 = target).</summary>
    private void HandleBoardConnectClick(string entityId, string projFolder)
    {
        if (_boardConnSrcId == null)
        {
            // Step 1 - select source
            _boardConnSrcId = entityId;
            BuildWorldContent();   // refresh to show source highlight + updated hint text
        }
        else if (_boardConnSrcId == entityId)
        {
            // Clicked same card - cancel
            _boardConnSrcId   = null;
            _boardConnectMode = false;
            BuildWorldContent();
        }
        else
        {
            // Step 2 - select target → show style dialog and save
            var result = ShowRelationDialog(title: "New Relation");
            _currentBoardData ??= EntityBoardService.Load(projFolder, "_world");
            if (result is not null)
            {
                _currentBoardData.Relations.Add(new BoardRelation
                {
                    FromId      = _boardConnSrcId,
                    ToId        = entityId,
                    Caption     = result.Caption,
                    LegendLabel = result.LegendLabel,
                    LineStyle   = result.LineStyle,
                    LineColor   = result.LineColor,
                    Thickness   = result.Thickness
                });
                EntityBoardService.Save(projFolder, "_world", _currentBoardData);
            }
            _boardConnSrcId   = null;
            _boardConnectMode = false;
            BuildWorldContent();
        }
    }

    /// <summary>Re-arranges all entity cards in a neat grid grouped by type and saves board positions.</summary>
    private void AutoArrangeBoard(string projFolder)
    {
        const int Cols = 4, CardW = 200, CardH = 120, PadX = 60, PadY = 50;
        _currentBoardData ??= EntityBoardService.Load(projFolder, "_world");

        var entityTypes = GetWorldEntityTypes();
        int row = 0;
        foreach (var et in entityTypes)
        {
            var typeEntities = WorldEntityService.List(projFolder, et.TrimEnd('s'));
            int col = 0;
            foreach (var e in typeEntities)
            {
                _currentBoardData.Positions[e.Id] = new BoardPosition
                {
                    X = 40 + col * (CardW + PadX),
                    Y = 40 + row * (CardH + PadY)
                };
                if (++col >= Cols) { col = 0; row++; }
            }
            if (typeEntities.Count > 0) row++;
        }
        EntityBoardService.Save(projFolder, "_world", _currentBoardData);
        BuildWorldContent();
    }

    /// <summary>
    /// Builds the floating legend panel that sits in the top-left corner of the board.
    /// Shows unique (LegendLabel, LineStyle, LineColor, Thickness) entries from relations.
    /// </summary>
    private Border BuildBoardLegend()
    {
        bool visible = _currentBoardData?.LegendVisible ?? true;

        var panel = new Border
        {
            CornerRadius    = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(10, 8, 10, 8),
            MinWidth        = 140,
            MaxWidth        = 220,
            Effect          = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.18, Color = Colors.Black }
        };
        panel.SetResourceReference(Border.BackgroundProperty,  "ControlBgBrush");
        panel.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");

        var content = new StackPanel();
        panel.Child = content;

        // Header row
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(headerRow);

        var title = new TextBlock
        {
            Text       = "Legend",
            FontSize   = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        headerRow.Children.Add(title);

        var toggleBtn = new Button
        {
            Content         = visible ? "▲" : "▼",
            FontSize        = 9,
            Padding         = new Thickness(4, 1, 4, 1),
            Margin          = new Thickness(6, 0, 0, 0),
            BorderThickness = new Thickness(0),
            Background      = Brushes.Transparent,
            Cursor          = Cursors.Hand
        };
        toggleBtn.SetResourceReference(Button.ForegroundProperty, "SidebarDimBrush");
        headerRow.Children.Add(toggleBtn);

        // Collapsible entries area
        var entriesPanel = new StackPanel
        {
            Margin     = new Thickness(0, 6, 0, 0),
            Visibility = visible ? Visibility.Visible : Visibility.Collapsed
        };
        content.Children.Add(entriesPanel);

        var entries = _currentBoardData?.Relations
            .Where(r => !string.IsNullOrWhiteSpace(r.LegendLabel))
            .GroupBy(r => (r.LegendLabel, r.LineStyle, r.LineColor, r.Thickness))
            .Select(g => g.First())
            .ToList() ?? [];

        if (entries.Count == 0)
        {
            var hint = new TextBlock
            {
                Text         = "Right-click a relation\nto add a legend label.",
                FontSize     = 10,
                TextWrapping = TextWrapping.Wrap
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            entriesPanel.Children.Add(hint);
        }
        else
        {
            foreach (var rel in entries)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                entriesPanel.Children.Add(row);

                // Mini line preview (40×12 canvas)
                var prev = new Canvas { Width = 40, Height = 12, Margin = new Thickness(0, 0, 6, 0) };
                double cap = IsDoubleLine(rel.LineStyle) ? 7.0 : 6.0;
                void AddPreviewLine(double yOff)
                {
                    var ln = new Line { X1 = 0, Y1 = yOff, X2 = 40, Y2 = yOff,
                        StrokeThickness = Math.Min(rel.Thickness, 2.5), Opacity = 0.9 };
                    var da = GetDashArray(rel.LineStyle);
                    if (da is not null)
                    {
                        ln.StrokeDashArray = da;
                        if (rel.LineStyle is BoardLineStyle.Dotted or BoardLineStyle.DoubleDotted)
                            ln.StrokeDashCap = PenLineCap.Round;
                    }
                    ln.SetResourceReference(Line.StrokeProperty, rel.LineColor);
                    prev.Children.Add(ln);
                }
                AddPreviewLine(IsDoubleLine(rel.LineStyle) ? 4 : 6);
                if (IsDoubleLine(rel.LineStyle)) AddPreviewLine(9);
                row.Children.Add(prev);

                var lbl = new TextBlock
                {
                    Text             = rel.LegendLabel,
                    FontSize         = 10,
                    TextTrimming     = TextTrimming.CharacterEllipsis,
                    MaxWidth         = 130,
                    VerticalAlignment = VerticalAlignment.Center
                };
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
                row.Children.Add(lbl);
            }
        }

        // Toggle handler
        toggleBtn.Click += (_, _) =>
        {
            if (_currentBoardData is not null)
                _currentBoardData.LegendVisible = !_currentBoardData.LegendVisible;
            if (_currentProjectFolder is not null)
                EntityBoardService.Save(_currentProjectFolder, "_world", _currentBoardData!);
            BuildWorldContent();
        };

        return panel;
    }

    // ── Relation style dialog ─────────────────────────────────────────────

    private record RelationDialogResult(
        string Caption, string LegendLabel,
        BoardLineStyle LineStyle, string LineColor, double Thickness);

    /// <summary>
    /// Modal dialog for creating or editing a relation's caption, legend label, line style, colour, and thickness.
    /// Returns null if the user cancels.
    /// </summary>
    private RelationDialogResult? ShowRelationDialog(
        string         captionInit     = "",
        string         legendInit      = "",
        BoardLineStyle styleInit       = BoardLineStyle.Solid,
        string         colorInit       = "AccentHighlightBrush",
        double         thicknessInit   = 1.5,
        string         title           = "New Relation")
    {
        var win = new Window
        {
            Title                 = title,
            Width                 = 440,
            Height                = 490,
            ResizeMode            = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ShowInTaskbar         = false
        };
        ApplyThemeToDialog(win);
        win.SetResourceReference(Window.BackgroundProperty, "ContentBgBrush");

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(20, 16, 20, 16)
        };
        win.Content = scroll;
        var root = new StackPanel();
        scroll.Content = root;

        void AddLabel(string text)
        {
            var lbl = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold,
                                      Margin = new Thickness(0, 10, 0, 4) };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            root.Children.Add(lbl);
        }
        TextBox MakeTextBox(string value, int maxLen = 200)
        {
            var tb = new TextBox { Text = value, FontSize = 13, MaxLength = maxLen,
                                   Padding = new Thickness(8, 5, 8, 5), BorderThickness = new Thickness(1) };
            tb.SetResourceReference(TextBox.BackgroundProperty,  "ControlBgBrush");
            tb.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
            tb.SetResourceReference(TextBox.ForegroundProperty,  "ContentTextBrush");
            root.Children.Add(tb);
            return tb;
        }

        // Caption
        AddLabel("Caption (shown on line):");
        var captionBox = MakeTextBox(captionInit);

        // Legend label
        AddLabel("Legend label (max 20 chars, shown in board legend):");
        var legendBox = MakeTextBox(legendInit, maxLen: 20);

        // Line style
        AddLabel("Line style:");
        var styleItems = new (string Display, BoardLineStyle Value)[]
        {
            ("━━━━━  Solid",            BoardLineStyle.Solid),
            ("· · · · ·  Dotted",      BoardLineStyle.Dotted),
            ("─ ─ ─ ─  Dashed",        BoardLineStyle.Dashed),
            ("·─·─·  Dot-dash",         BoardLineStyle.DotDash),
            ("══════  Double solid",    BoardLineStyle.DoubleSolid),
            ("⁚ ⁚ ⁚  Double dotted",   BoardLineStyle.DoubleDotted),
            ("═ ═ ═  Double dashed",    BoardLineStyle.DoubleDashed),
            ("·═·═  Double dot-dash",   BoardLineStyle.DoubleDotDash)
        };
        var styleCombo = new ComboBox { FontSize = 12, Padding = new Thickness(8, 4, 8, 4) };
        int styleInitIdx = 0;
        for (int i = 0; i < styleItems.Length; i++)
        {
            styleCombo.Items.Add(styleItems[i].Display);
            if (styleItems[i].Value == styleInit) styleInitIdx = i;
        }
        styleCombo.SelectedIndex = styleInitIdx;
        root.Children.Add(styleCombo);

        // Line colour (4 accent swatches)
        AddLabel("Line colour:");
        var colorOptions = new (string Key, string Label)[]
        {
            ("AccentHighlightBrush", "Accent 1"),
            ("PrimaryAccentBrush",   "Accent 2"),
            ("SecondaryAccentBrush", "Accent 3"),
            ("AccentBgBrush",        "Accent 4")
        };
        var swatchRow = new WrapPanel { Orientation = Orientation.Horizontal };
        root.Children.Add(swatchRow);
        string selectedColor = colorInit;
        var swatches = new List<Border>();

        foreach (var (key, label) in colorOptions)
        {
            var capturedKey = key;
            bool isSel = key == colorInit;
            var swatch = new Border
            {
                Width           = 80,
                Height          = 26,
                Margin          = new Thickness(0, 0, 6, 0),
                CornerRadius    = new CornerRadius(4),
                BorderThickness = new Thickness(isSel ? 2 : 1),
                Cursor          = Cursors.Hand,
                Tag             = key
            };
            swatch.SetResourceReference(Border.BackgroundProperty,  key);
            swatch.SetResourceReference(Border.BorderBrushProperty,
                isSel ? "ContentTextBrush" : "ControlBorderBrush");
            var slbl = new TextBlock
            {
                Text              = label,
                FontSize          = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            slbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentBgBrush");
            swatch.Child = slbl;
            swatch.MouseLeftButtonDown += (_, _) =>
            {
                selectedColor = capturedKey;
                foreach (var sw in swatches)
                {
                    bool sel = sw.Tag?.ToString() == capturedKey;
                    sw.BorderThickness = new Thickness(sel ? 2 : 1);
                    sw.SetResourceReference(Border.BorderBrushProperty,
                        sel ? "ContentTextBrush" : "ControlBorderBrush");
                }
            };
            swatches.Add(swatch);
            swatchRow.Children.Add(swatch);
        }

        // Thickness slider
        AddLabel("Thickness (1-10 px):");
        var thickRow = new StackPanel { Orientation = Orientation.Horizontal };
        root.Children.Add(thickRow);
        var thickSlider = new Slider
        {
            Minimum = 1, Maximum = 10, Value = thicknessInit,
            Width = 200, VerticalAlignment = VerticalAlignment.Center,
            IsSnapToTickEnabled = true, TickFrequency = 0.5
        };
        var thickLabel = new TextBlock
        {
            Text = $"{thicknessInit:F1} px", FontSize = 12, Width = 44,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0)
        };
        thickLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        thickSlider.ValueChanged += (_, e) => thickLabel.Text = $"{e.NewValue:F1} px";
        thickRow.Children.Add(thickSlider);
        thickRow.Children.Add(thickLabel);

        // OK / Cancel
        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, 16, 0, 0)
        };
        root.Children.Add(btnRow);
        var cancelBtn = MakeFilePanelButton("Cancel", isPrimary: false);
        cancelBtn.Padding = new Thickness(12, 6, 12, 6);
        cancelBtn.Click  += (_, _) => win.DialogResult = false;
        btnRow.Children.Add(cancelBtn);
        var okBtn = MakeFilePanelButton("OK", isPrimary: true);
        okBtn.Padding = new Thickness(12, 6, 12, 6);
        okBtn.Margin  = new Thickness(8, 0, 0, 0);
        okBtn.Click  += (_, _) => win.DialogResult = true;
        btnRow.Children.Add(okBtn);

        captionBox.Focus(); captionBox.SelectAll();
        captionBox.PreviewKeyDown += (_, e) => { if (e.Key == Key.Return) win.DialogResult = true; };

        if (win.ShowDialog() != true) return null;

        var chosenStyle = styleItems[Math.Max(0, Math.Min(styleCombo.SelectedIndex, styleItems.Length - 1))].Value;
        return new RelationDialogResult(
            captionBox.Text.Trim(),
            legendBox.Text.Trim(),
            chosenStyle, selectedColor,
            thickSlider.Value);
    }

    /// <summary>Builds a compact draggable card for the canvas board view.</summary>
    private Border BuildBoardCard(WorldEntity entity, string projFolder,
                                  IReadOnlyList<WorldEntity>? boardFactions = null)
    {
        bool isConnSrc = _boardConnSrcId == entity.Id;
        bool isFaction = entity.EntityType == "Faction";
        Color? factionAccent = null;
        if (isFaction && !string.IsNullOrEmpty(entity.FactionColor))
        {
            try { factionAccent = (Color)ColorConverter.ConvertFromString(entity.FactionColor)!; }
            catch { /* ignore */ }
        }

        var card = new Border
        {
            Width           = 200,
            CornerRadius    = new CornerRadius(8),
            BorderThickness = new Thickness(isConnSrc ? 2 : 1),
            Padding         = new Thickness(10, 8, 10, 8),
            Cursor          = _boardConnectMode ? Cursors.Hand : Cursors.SizeAll,
            Effect          = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 2, Opacity = 0.25,
                                                     Color = Colors.Black }
        };
        card.SetResourceReference(Border.BackgroundProperty, "ControlBgBrush");
        card.BorderBrush = isConnSrc
            ? (Brush)(TryFindResource("AccentHighlightBrush") ?? new SolidColorBrush(Colors.DodgerBlue))
            : factionAccent.HasValue
                ? new SolidColorBrush(Color.FromArgb(180,
                    factionAccent.Value.R, factionAccent.Value.G, factionAccent.Value.B))
                : (Brush)(TryFindResource("ControlBorderBrush") ?? new SolidColorBrush(Colors.Gray));

        var inner = new StackPanel();
        card.Child = inner;

        // ── Header: name + type badge ──────────────────────────────────────
        var headerRow = new Grid();
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.Children.Add(headerRow);

        var nameText = new TextBlock
        {
            Text         = entity.Name,
            FontSize     = 12,
            FontWeight   = FontWeights.SemiBold,
            FontFamily   = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 4, 4)
        };
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        Grid.SetColumn(nameText, 0);
        headerRow.Children.Add(nameText);

        var typeBadge = new Border
        {
            CornerRadius      = new CornerRadius(3),
            Padding           = new Thickness(4, 1, 4, 1),
            VerticalAlignment = VerticalAlignment.Top
        };
        if (factionAccent.HasValue)
            typeBadge.Background = new SolidColorBrush(
                Color.FromArgb(55, factionAccent.Value.R, factionAccent.Value.G, factionAccent.Value.B));
        else
            typeBadge.SetResourceReference(Border.BackgroundProperty, "ControlBgBrush");
        var typeBadgeText = new TextBlock { Text = entity.EntityType, FontSize = 9,
                                            FontFamily = new FontFamily("Segoe UI") };
        if (factionAccent.HasValue)
            typeBadgeText.Foreground = new SolidColorBrush(factionAccent.Value);
        else
            typeBadgeText.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        typeBadge.Child = typeBadgeText;
        Grid.SetColumn(typeBadge, 1);
        headerRow.Children.Add(typeBadge);

        // Up to 2 schema fields
        var schema = WorldEntitySchemas.For(entity.EntityType);
        int shown  = 0;
        foreach (var (field, _) in schema)
        {
            if (shown >= 2) break;
            if (!entity.Fields.TryGetValue(field, out var val) || string.IsNullOrWhiteSpace(val)) continue;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 0) };
            var lbl = new TextBlock { Text = field + ": ", FontSize = 10, FontWeight = FontWeights.SemiBold };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            var vt  = new TextBlock
            {
                Text         = val,
                FontSize     = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth     = 130
            };
            vt.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
            row.Children.Add(lbl); row.Children.Add(vt);
            inner.Children.Add(row);
            shown++;
        }

        // Edit button
        var editBtn = MakeFilePanelButton("✏ Edit", isPrimary: false);
        editBtn.FontSize = 10; editBtn.Padding = new Thickness(7, 3, 7, 3);
        editBtn.Margin  = new Thickness(0, 6, 0, 0);
        editBtn.Cursor  = Cursors.Hand;
        editBtn.Click  += (_, _) =>
        {
            var copy    = CloneEntity(entity);
            var oldName = entity.Name;
            if (ShowEntityEditDialog(copy, projFolder, isNew: false))
            {
                if (!string.Equals(copy.Name, oldName, StringComparison.Ordinal))
                    WorldEntityService.Rename(projFolder, copy, oldName);
                else
                    WorldEntityService.Save(projFolder, copy);
                BuildWorldContent();
            }
        };
        inner.Children.Add(editBtn);

        return card;
    }

    /// <summary>Shows a small single-line input prompt dialog. Returns the text, or null if cancelled.</summary>
    private string? ShowSimpleInputDialog(string title, string labelText, string initial)
    {
        var win = new Window
        {
            Title                 = title,
            Width                 = 380,
            Height                = 160,
            ResizeMode            = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ShowInTaskbar         = false
        };
        ApplyThemeToDialog(win);
        win.SetResourceReference(Window.BackgroundProperty, "ContentBgBrush");

        var stack = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        win.Content = stack;

        var lbl = new TextBlock { Text = labelText, FontSize = 12, Margin = new Thickness(0, 0, 0, 6) };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        stack.Children.Add(lbl);

        var tb = new TextBox
        {
            Text            = initial,
            FontSize        = 13,
            Padding         = new Thickness(8, 5, 8, 5),
            BorderThickness = new Thickness(1)
        };
        tb.SetResourceReference(TextBox.BackgroundProperty,  "ControlBgBrush");
        tb.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        tb.SetResourceReference(TextBox.ForegroundProperty,  "ContentTextBrush");
        stack.Children.Add(tb);

        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, 10, 0, 0)
        };
        stack.Children.Add(btnRow);

        var cancelBtn = MakeFilePanelButton("Cancel", isPrimary: false);
        cancelBtn.Padding = new Thickness(12, 5, 12, 5);
        cancelBtn.Click  += (_, _) => win.DialogResult = false;
        btnRow.Children.Add(cancelBtn);

        var okBtn = MakeFilePanelButton("OK", isPrimary: true);
        okBtn.Padding = new Thickness(12, 5, 12, 5);
        okBtn.Margin  = new Thickness(8, 0, 0, 0);
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

    /// <summary>
    /// Opens a non-blocking read-only view of an entity's fields.
    /// Used when another entity is already being edited.
    /// </summary>
    private void ShowEntityReadOnlyDialog(WorldEntity entity)
    {
        var schema = WorldEntitySchemas.For(entity.EntityType);

        var win = new Window
        {
            Title                 = $"{entity.EntityType}: {entity.Name}",
            Width                 = 460,
            Height                = 500,
            MinWidth              = 340,
            MinHeight             = 280,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ShowInTaskbar         = false,
            ResizeMode            = ResizeMode.CanResize
        };
        ApplyThemeToDialog(win);
        win.SetResourceReference(Window.BackgroundProperty, "ContentBgBrush");

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(24, 20, 24, 20)
        };
        win.Content = scroll;

        var root = new StackPanel();
        scroll.Content = root;

        // Name heading
        var nameBlock = new TextBlock
        {
            Text         = entity.Name,
            FontSize     = 18,
            FontWeight   = FontWeights.Bold,
            FontFamily   = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap
        };
        nameBlock.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        root.Children.Add(nameBlock);

        var typeLabel = new TextBlock
        {
            Text     = entity.EntityType,
            FontSize = 11,
            Margin   = new Thickness(0, 2, 0, 14)
        };
        typeLabel.SetResourceReference(TextBlock.ForegroundProperty, "AccentHighlightBrush");
        root.Children.Add(typeLabel);

        void AddField(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var lbl = new TextBlock
            {
                Text       = label,
                FontSize   = 11,
                FontWeight = FontWeights.SemiBold,
                Margin     = new Thickness(0, 8, 0, 2)
            };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            root.Children.Add(lbl);
            var val = new TextBlock
            {
                Text         = value,
                FontSize     = 13,
                FontFamily   = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap
            };
            val.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
            root.Children.Add(val);
        }

        foreach (var (field, _) in schema)
            if (entity.Fields.TryGetValue(field, out var v))
                AddField(field, v);

        if (!string.IsNullOrWhiteSpace(entity.Notes))
            AddField("Notes", entity.Notes);

        var closeBtn = MakeFilePanelButton("Close", isPrimary: false);
        closeBtn.HorizontalAlignment = HorizontalAlignment.Right;
        closeBtn.Padding = new Thickness(14, 7, 14, 7);
        closeBtn.Margin  = new Thickness(0, 18, 0, 0);
        closeBtn.Click  += (_, _) => win.Close();
        root.Children.Add(closeBtn);

        win.Show();   // Non-blocking - visible alongside the edit dialog
    }

    /// <summary>
    /// Builds a system-prompt section describing all world entities and their relations.
    /// Only injected when a project with world-building is open and entities exist.
    /// </summary>
    private string BuildWorldEntityContext()
    {
        if (_currentProjectFolder is null) return "";
        if (_currentProjectType?.HasWorldBuilding != true) return "";

        var entityTypes = GetWorldEntityTypes();
        var sb          = new System.Text.StringBuilder();
        bool hasContent = false;

        foreach (var etPlural in entityTypes)
        {
            var et       = etPlural.TrimEnd('s');   // "Characters" → "Character"
            var entities = WorldEntityService.List(_currentProjectFolder, et);
            if (entities.Count == 0) continue;

            if (!hasContent)
            {
                sb.Append("\n\n## World Entities");
                hasContent = true;
            }
            sb.Append($"\n\n### {etPlural}");

            var schema = WorldEntitySchemas.For(et);
            foreach (var entity in entities)
            {
                sb.Append($"\n**{entity.Name}**");
                var parts = new List<string>();
                foreach (var (field, _) in schema)
                    if (entity.Fields.TryGetValue(field, out var fv) && !string.IsNullOrWhiteSpace(fv))
                        parts.Add($"{field}: {fv}");
                if (parts.Count > 0)
                    sb.Append(" - " + string.Join("; ", parts));
                if (!string.IsNullOrWhiteSpace(entity.Notes))
                    sb.Append($"\n  Notes: {entity.Notes}");
            }

            // Relations
            var boardData  = EntityBoardService.Load(_currentProjectFolder, et);
            if (boardData.Relations.Count > 0)
            {
                var idToName = entities.ToDictionary(e => e.Id, e => e.Name);
                var relLines = boardData.Relations
                    .Where(r => idToName.ContainsKey(r.FromId) && idToName.ContainsKey(r.ToId))
                    .Select(r =>
                    {
                        var cap = string.IsNullOrWhiteSpace(r.Caption) ? "related to" : r.Caption;
                        return $"  - {idToName[r.FromId]} → {cap} → {idToName[r.ToId]}";
                    })
                    .ToList();
                if (relLines.Count > 0)
                {
                    sb.Append($"\n\n**{etPlural} Relations:**");
                    foreach (var rl in relLines)
                        sb.Append($"\n{rl}");
                }
            }
        }

        return hasContent ? sb.ToString() : "";
    }
}