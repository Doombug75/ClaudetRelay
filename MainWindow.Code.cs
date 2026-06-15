using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SysIO = System.IO;
using ClaudetRelay.Models;
using ClaudetRelay.Services;

namespace ClaudetRelay;

// Code-board panel -- gallery and board management.
// Partial class companion to MainWindow.xaml.cs.
public partial class MainWindow
{
    private readonly Dictionary<string, CodeBoardWindow> _openCodeWindows = new();

    // Code panel view state
    private bool   _codeBoardsMode = true;           // true = board gallery, false = library list
    private string _codeLibType    = "Class";        // active entity type in library mode
    private string _codeLibSearch  = "";             // library search filter
    private string _codeLibSort    = "name_asc";     // name_asc | name_desc | modified_asc | modified_desc
    private readonly HashSet<string> _codeLibSelected = new();  // selected entity IDs in the library
    private string? _codeLibAnchor;                  // anchor for Shift range-selection in the library
    private string _codeLibView    = "cards";        // cards | list | table
    private string _codeBoardSearch = "";            // board gallery search filter
    private string _codeBoardSort   = "modified_desc"; // name_asc | name_desc | modified_asc | modified_desc
    private string _codeBoardView   = "cards";        // cards | list | table (board gallery)
    private readonly HashSet<string> _codeBoardSelected = new();  // selected board IDs
    private string? _codeBoardAnchor;                 // anchor for Shift range-selection (boards)

    /// <summary>Shared width for the name-filter text box across all library views.</summary>
    internal const double LibFilterWidth = 240;

    private void CodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProjectFolder is null) return;
        ShowCodePanel(CodeContent.Visibility != Visibility.Visible);
    }

    private void ShowCodePanel(bool show)
    {
        if (show && _currentProjectFolder is not null)
        {
            // Collapse all other project panels
            RoadmapContent   .Visibility = Visibility.Collapsed;
            RoadmapButton.FontWeight     = FontWeights.Normal;
            FilesContent     .Visibility = Visibility.Collapsed;
            FilesButton.FontWeight       = FontWeights.Normal;
            WorldContent     .Visibility = Visibility.Collapsed;
            WorldButton.FontWeight       = FontWeights.Normal;

            ChatOnlyButtonsPanel .Visibility = Visibility.Collapsed;
            ChatViewButton.FontWeight         = FontWeights.Normal;

            CodeContent      .Visibility = Visibility.Visible;
            ChatScrollViewer .Visibility = Visibility.Collapsed;
            InputArea        .Visibility = Visibility.Collapsed;
            CodeButton.FontWeight        = FontWeights.SemiBold;

            BuildCodeContent(_currentProjectFolder);
        }
        else
        {
            CodeContent      .Visibility = Visibility.Collapsed;
            ChatScrollViewer .Visibility = Visibility.Visible;
            InputArea        .Visibility = Visibility.Visible;
            CodeButton.FontWeight        = FontWeights.Normal;

            ChatOnlyButtonsPanel .Visibility = Visibility.Visible;
            ChatViewButton.FontWeight         = _currentProjectFolder is not null
                                               ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    // ── Top-level builder: tab bar + dispatch to gallery or library ─────────

    private void BuildCodeContent(string projFolder)
    {
        CodeContent.Children.Clear();
        CodeContent.RowDefinitions.Clear();
        CodeContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // tab bar
        CodeContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // content

        // ── Tab bar ──
        var toolbar = new Border { Padding = new Thickness(16, 8, 16, 0) };
        toolbar.SetResourceReference(Border.BackgroundProperty, "SidebarBgBrush");
        Grid.SetRow(toolbar, 0);
        CodeContent.Children.Add(toolbar);

        var topRow = new Grid();
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Child = topRow;

        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(tabs, 0);
        topRow.Children.Add(tabs);

        var exportPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 6) };
        Grid.SetColumn(exportPanel, 1);
        topRow.Children.Add(exportPanel);

        Button MakeExportBtn(string label, string tip)
        {
            var b = new Button { Content = label, FontSize = 11, Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(4, 0, 0, 0), ToolTip = tip };
            b.SetResourceReference(Button.StyleProperty,      "ModernButton");
            b.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
            b.SetResourceReference(Button.ForegroundProperty, "SidebarTextBrush");
            return b;
        }

        var exportAllBtn = MakeExportBtn(Properties.Loc.S("Code_ExportCode"), Properties.Loc.S("Code_ExportTooltip"));
        exportAllBtn.Click += (_, _) => ShowCodeExportDialog(projFolder);
        exportPanel.Children.Add(exportAllBtn);

        var exportSelTopBtn = MakeExportBtn(Properties.Loc.S("Code_ExportSelected"), Properties.Loc.S("Code_ExportSelectedTip"));
        exportSelTopBtn.Click += (_, _) => ExportCurrentSelection(projFolder);
        exportPanel.Children.Add(exportSelTopBtn);

        Button MakeTab(string label, bool active, Action onClick)
        {
            var t = new Button
            {
                Content         = label,
                FontSize        = 13,
                FontWeight      = active ? FontWeights.SemiBold : FontWeights.Normal,
                Padding         = new Thickness(14, 8, 14, 10),
                BorderThickness = new Thickness(0),
                Cursor          = Cursors.Hand,
                Background      = Brushes.Transparent
            };
            t.SetResourceReference(Button.ForegroundProperty, active ? "AccentHighlightBrush" : "SidebarDimBrush");
            t.Click += (_, _) => onClick();
            return t;
        }

        tabs.Children.Add(MakeTab(Properties.Loc.S("Code_Tab_Boards"), _codeBoardsMode, () => { _codeBoardsMode = true; BuildCodeContent(projFolder); }));
        foreach (var et in CodeEntityService.EntityTypes)
        {
            var capEt = et;
            bool active = !_codeBoardsMode && _codeLibType == capEt;
            tabs.Children.Add(MakeTab(capEt, active, () =>
            {
                _codeBoardsMode = false;
                _codeLibType    = capEt;
                _codeLibSearch  = "";
                _codeLibSelected.Clear();
                _codeLibAnchor  = null;
                BuildCodeContent(projFolder);
            }));
        }

        if (_codeBoardsMode) BuildCodeBoardGallery(projFolder);
        else                 BuildCodeLibrary(projFolder);
    }

    /// <summary>Exports the currently selected entities — board mode: entities on the
    /// selected boards; library mode: the selected entries.</summary>
    private void ExportCurrentSelection(string projFolder)
    {
        var all = new Dictionary<string, CodeEntity>();
        foreach (var t in CodeEntityService.EntityTypes)
            foreach (var e in CodeEntityService.LoadAll(projFolder, t))
                all[e.Id] = e;

        var ids = new HashSet<string>();
        if (_codeBoardsMode)
        {
            foreach (var bid in _codeBoardSelected)
                foreach (var eid in CodeBoardDataService.Load(projFolder, bid).Positions.Keys)
                    ids.Add(eid);
        }
        else
        {
            foreach (var id in _codeLibSelected) ids.Add(id);
        }

        var sel = ids.Where(all.ContainsKey).Select(id => all[id]).ToList();
        if (sel.Count == 0)
        {
            MessageBox.Show(Properties.Loc.S("Code_NoSelection"), Properties.Loc.S("Code_ExportSelected"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ShowCodeExportDialog(projFolder, sel);
    }

    // ── Board gallery ───────────────────────────────────────────────────────

    private void BuildCodeBoardGallery(string projFolder)
    {
        var boards = CodeBoardRegistryService.Load(projFolder);

        var host = new Grid();
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(host, 1);
        CodeContent.Children.Add(host);

        // Sub-toolbar: search · sort · + New Board
        var subBar = new Border { Padding = new Thickness(16, 8, 16, 8) };
        subBar.SetResourceReference(Border.BackgroundProperty, "SidebarBgBrush");
        Grid.SetRow(subBar, 0);
        host.Children.Add(subBar);

        var barRow = new Grid();
        barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // search
        barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // spacer
        barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // view
        barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // sort
        barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // new
        subBar.Child = barRow;

        var search = new TextBox { Text = _codeBoardSearch, Width = LibFilterWidth, Height = 28, HorizontalAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 0, 8, 0) };
        search.SetResourceReference(TextBox.BackgroundProperty,  "InputBgBrush");
        search.SetResourceReference(TextBox.ForegroundProperty,  "SidebarTextBrush");
        search.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        Grid.SetColumn(search, 0);
        barRow.Children.Add(search);

        // View switcher
        var viewPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(viewPanel, 2);
        barRow.Children.Add(viewPanel);
        var viewBtns = new Dictionary<string, Button>();
        void StyleViewButtons()
        {
            foreach (var (key, b) in viewBtns)
                b.SetResourceReference(Button.BackgroundProperty, key == _codeBoardView ? "AccentBgBrush" : "ControlBgBrush");
        }
        Button MakeViewBtn(string key, string glyph, string tip)
        {
            var b = new Button { Content = glyph, Padding = new Thickness(8, 4, 8, 4), FontSize = 13, Margin = new Thickness(0, 0, 2, 0), ToolTip = tip };
            b.SetResourceReference(Button.StyleProperty, "ModernButton");
            b.SetResourceReference(Button.ForegroundProperty, "SidebarTextBrush");
            viewBtns[key] = b;
            return b;
        }

        var sortCombo = new ComboBox { Width = 170, Margin = new Thickness(8, 0, 0, 0) };
        sortCombo.SetResourceReference(StyleProperty, "ModernComboBox");
        var sortOpts = new (string Label, string Key)[]
        {
            (Properties.Loc.S("Code_Sort_NameAsc"), "name_asc"), (Properties.Loc.S("Code_Sort_NameDesc"), "name_desc"),
            (Properties.Loc.S("Code_Sort_ModifiedAsc"), "modified_asc"), (Properties.Loc.S("Code_Sort_ModifiedDesc"), "modified_desc"),
        };
        foreach (var o in sortOpts) sortCombo.Items.Add(o.Label);
        sortCombo.SelectedIndex = Math.Max(0, Array.FindIndex(sortOpts, o => o.Key == _codeBoardSort));
        Grid.SetColumn(sortCombo, 3);
        barRow.Children.Add(sortCombo);

        var addBtn = new Button { Content = Properties.Loc.S("Code_NewBoard"), Padding = new Thickness(10, 5, 10, 5), FontSize = 12, Margin = new Thickness(8, 0, 0, 0) };
        addBtn.SetResourceReference(Button.StyleProperty,      "ModernButton");
        addBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        addBtn.SetResourceReference(Button.ForegroundProperty, "SidebarTextBrush");
        addBtn.Click += (_, _) =>
        {
            var board = ShowNewCodeBoardDialog(projFolder);
            if (board is null) return;
            boards.Add(board);
            CodeBoardRegistryService.Save(projFolder, boards);
            BuildCodeContent(projFolder);
        };
        Grid.SetColumn(addBtn, 4);
        barRow.Children.Add(addBtn);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(20, 16, 20, 16)
        };
        Grid.SetRow(scroll, 1);
        host.Children.Add(scroll);
        var listHost = new Border();
        scroll.Content = listHost;

        var boardOrder   = new List<string>();
        var boardBorders = new Dictionary<string, Border>();

        void UpdateBoardHighlights()
        {
            var accent = (Brush)(TryFindResource("AccentHighlightBrush") ?? new SolidColorBrush(Colors.DodgerBlue));
            foreach (var (id, b) in boardBorders)
            {
                if (_codeBoardSelected.Contains(id)) { b.BorderBrush = accent; b.BorderThickness = new Thickness(2); }
                else { b.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush"); b.BorderThickness = new Thickness(1); }
            }
        }

        void HandleBoardClick(string id, MouseButtonEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            if ((mods & ModifierKeys.Shift) != 0 && _codeBoardAnchor is not null)
            {
                int a = boardOrder.IndexOf(_codeBoardAnchor), b = boardOrder.IndexOf(id);
                if (a >= 0 && b >= 0) { if (a > b) (a, b) = (b, a); _codeBoardSelected.Clear(); for (int i = a; i <= b; i++) _codeBoardSelected.Add(boardOrder[i]); }
            }
            else if ((mods & ModifierKeys.Control) != 0) { if (!_codeBoardSelected.Add(id)) _codeBoardSelected.Remove(id); _codeBoardAnchor = id; }
            else { _codeBoardSelected.Clear(); _codeBoardSelected.Add(id); _codeBoardAnchor = id; }
            UpdateBoardHighlights();
        }

        void WireBoard(Border el, CodeBoard board)
        {
            el.Cursor = Cursors.Hand;
            el.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount >= 2) { OpenOrActivateCodeWindow(projFolder, board); return; }
                HandleBoardClick(board.Id, e);
            };
            el.MouseRightButtonDown += (_, e) =>
            {
                e.Handled = true;
                var cm = new ContextMenu();
                var openMi = new MenuItem { Header = Properties.Loc.S("Code_OpenBoard") };
                openMi.Click += (_, _) => OpenOrActivateCodeWindow(projFolder, board);
                cm.Items.Add(openMi);
                var expMi = new MenuItem { Header = Properties.Loc.S("Code_ExportThis") };
                expMi.Click += (_, _) =>
                {
                    var all = new Dictionary<string, CodeEntity>();
                    foreach (var t in CodeEntityService.EntityTypes) foreach (var en in CodeEntityService.LoadAll(projFolder, t)) all[en.Id] = en;
                    var sel = CodeBoardDataService.Load(projFolder, board.Id).Positions.Keys.Where(all.ContainsKey).Select(id => all[id]).ToList();
                    if (sel.Count == 0) { MessageBox.Show(Properties.Loc.S("Code_NoSelection"), Properties.Loc.S("Code_ExportSelected"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
                    ShowCodeExportDialog(projFolder, sel);
                };
                cm.Items.Add(expMi);
                cm.Items.Add(new Separator());
                var renameMi = new MenuItem { Header = Properties.Loc.S("Code_RenameBoard") };
                renameMi.Click += (_, _) => { if (ShowCodeBoardSettingsDialog(board)) { board.UpdatedAt = DateTime.UtcNow; CodeBoardRegistryService.Save(projFolder, boards); BuildCodeContent(projFolder); } };
                cm.Items.Add(renameMi);
                var delMi = new MenuItem { Header = Properties.Loc.S("Code_DeleteBoard") };
                delMi.Click += (_, _) =>
                {
                    var res = MessageBox.Show(string.Format(Properties.Loc.S("Code_DeleteBoardConfirm"), board.Name), Properties.Loc.S("Code_DeleteBoardTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (res != MessageBoxResult.Yes) return;
                    if (_openCodeWindows.TryGetValue(board.Id, out var w)) { w.Close(); _openCodeWindows.Remove(board.Id); }
                    boards.Remove(board); CodeBoardRegistryService.Save(projFolder, boards); BuildCodeContent(projFolder);
                };
                cm.Items.Add(delMi);
                cm.IsOpen = true;
            };
        }

        Border BuildBoardListRow(CodeBoard board)
        {
            var row = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 0, 2) };
            row.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Child = grid;
            var sym = new TextBlock { Text = board.Symbol, FontSize = 13, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            sym.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush"); Grid.SetColumn(sym, 0); grid.Children.Add(sym);
            var nm = new TextBlock { Text = board.Name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            nm.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush"); Grid.SetColumn(nm, 1); grid.Children.Add(nm);
            var date = new TextBlock { Text = board.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), FontSize = 11, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            date.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush"); Grid.SetColumn(date, 2); grid.Children.Add(date);
            WireBoard(row, board);
            return row;
        }

        Border BuildBoardTableCell(CodeBoard board)
        {
            var cell = new Border { Width = 168, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 6, 6) };
            cell.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            cell.Child = sp;
            var sym = new TextBlock { Text = board.Symbol, FontSize = 13, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            sym.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush"); sp.Children.Add(sym);
            var nm = new TextBlock { Text = board.Name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 128 };
            nm.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush"); sp.Children.Add(nm);
            WireBoard(cell, board);
            return cell;
        }

        void DoRefreshBoards()
        {
            boardOrder.Clear(); boardBorders.Clear();
            var filtered = boards.Where(b => string.IsNullOrWhiteSpace(_codeBoardSearch)
                || b.Name.Contains(_codeBoardSearch, StringComparison.OrdinalIgnoreCase));
            var ordered = _codeBoardSort switch
            {
                "name_asc"     => filtered.OrderBy(b => b.Name),
                "name_desc"    => filtered.OrderByDescending(b => b.Name),
                "modified_asc" => filtered.OrderBy(b => b.UpdatedAt),
                _              => filtered.OrderByDescending(b => b.UpdatedAt),
            };
            var shown = ordered.ToList();

            Panel container = _codeBoardView == "list" ? new StackPanel() : new WrapPanel();
            listHost.Child = container;

            if (shown.Count == 0)
            {
                var hint = new TextBlock { Text = boards.Count == 0 ? Properties.Loc.S("Code_NoBoards") : Properties.Loc.S("Code_NoMatch"), FontSize = 13, Opacity = 0.55, Margin = new Thickness(4, 40, 0, 0) };
                hint.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
                container.Children.Add(hint);
                return;
            }
            foreach (var board in shown)
            {
                boardOrder.Add(board.Id);
                Border el = _codeBoardView switch
                {
                    "list"  => BuildBoardListRow(board),
                    "table" => BuildBoardTableCell(board),
                    _       => BuildCodeBoardCard(board, projFolder, boards, WireBoard),
                };
                boardBorders[board.Id] = el;
                container.Children.Add(el);
            }
            UpdateBoardHighlights();
        }

        void SetBoardView(string v) { _codeBoardView = v; StyleViewButtons(); DoRefreshBoards(); }
        var cardsBtn = MakeViewBtn("cards", "▦", Properties.Loc.S("Code_View_Cards")); cardsBtn.Click += (_, _) => SetBoardView("cards"); viewPanel.Children.Add(cardsBtn);
        var listBtn  = MakeViewBtn("list",  "☰", Properties.Loc.S("Code_View_List"));  listBtn.Click  += (_, _) => SetBoardView("list");  viewPanel.Children.Add(listBtn);
        var tableBtn = MakeViewBtn("table", "▤", Properties.Loc.S("Code_View_Table")); tableBtn.Click += (_, _) => SetBoardView("table"); viewPanel.Children.Add(tableBtn);
        StyleViewButtons();

        DoRefreshBoards();

        search.TextChanged += (_, _) => { _codeBoardSearch = search.Text; DoRefreshBoards(); };
        sortCombo.SelectionChanged += (_, _) => { _codeBoardSort = sortOpts[Math.Max(0, sortCombo.SelectedIndex)].Key; DoRefreshBoards(); };
    }

    // ── Library list (entities of the active type, searchable) ──────────────

    private void BuildCodeLibrary(string projFolder)
    {
        var host = new Grid();
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // search + new
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(host, 1);
        CodeContent.Children.Add(host);

        // Search + New row
        var bar = new Border { Padding = new Thickness(16, 8, 16, 8) };
        bar.SetResourceReference(Border.BackgroundProperty, "SidebarBgBrush");
        Grid.SetRow(bar, 0);
        host.Children.Add(bar);

        var barRow = new Grid();
        barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // search (fixed width)
        barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // spacer
        barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // view switcher
        barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // sort
        barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // new
        bar.Child = barRow;

        var search = new TextBox
        {
            Text   = _codeLibSearch,
            Width  = LibFilterWidth,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 0, 8, 0)
        };
        search.SetResourceReference(TextBox.BackgroundProperty,  "InputBgBrush");
        search.SetResourceReference(TextBox.ForegroundProperty,  "SidebarTextBrush");
        search.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        search.Tag = "🔍 search…";
        Grid.SetColumn(search, 0);
        barRow.Children.Add(search);

        // View switcher: Cards / List / Table
        var viewPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(viewPanel, 2);
        barRow.Children.Add(viewPanel);
        var viewBtns = new Dictionary<string, Button>();
        void StyleViewButtons()
        {
            foreach (var (key, b) in viewBtns)
                b.SetResourceReference(Button.BackgroundProperty, key == _codeLibView ? "AccentBgBrush" : "ControlBgBrush");
        }
        Button MakeViewBtn(string key, string glyph, string tip)
        {
            var b = new Button { Content = glyph, Padding = new Thickness(8, 4, 8, 4), FontSize = 13, Margin = new Thickness(0, 0, 2, 0), ToolTip = tip };
            b.SetResourceReference(Button.StyleProperty,      "ModernButton");
            b.SetResourceReference(Button.ForegroundProperty, "SidebarTextBrush");
            viewBtns[key] = b;
            return b;
        }

        // Sort selector
        var sortCombo = new ComboBox { Width = 170, Margin = new Thickness(8, 0, 0, 0) };
        sortCombo.SetResourceReference(StyleProperty, "ModernComboBox");
        var sortOpts = new (string Label, string Key)[]
        {
            (Properties.Loc.S("Code_Sort_NameAsc"), "name_asc"), (Properties.Loc.S("Code_Sort_NameDesc"), "name_desc"),
            (Properties.Loc.S("Code_Sort_ModifiedAsc"), "modified_asc"), (Properties.Loc.S("Code_Sort_ModifiedDesc"), "modified_desc"),
        };
        foreach (var o in sortOpts) sortCombo.Items.Add(o.Label);
        sortCombo.SelectedIndex = Math.Max(0, Array.FindIndex(sortOpts, o => o.Key == _codeLibSort));
        Grid.SetColumn(sortCombo, 3);
        barRow.Children.Add(sortCombo);

        var newBtn = new Button
        {
            Content = string.Format(Properties.Loc.S("Code_NewEntity"), _codeLibType),
            Padding = new Thickness(10, 5, 10, 5),
            FontSize = 12,
            Margin   = new Thickness(8, 0, 0, 0)
        };
        newBtn.SetResourceReference(Button.StyleProperty,      "ModernButton");
        newBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        newBtn.SetResourceReference(Button.ForegroundProperty, "SidebarTextBrush");
        Grid.SetColumn(newBtn, 4);
        barRow.Children.Add(newBtn);

        // Entity list
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(20, 12, 20, 12)
        };
        Grid.SetRow(scroll, 1);
        host.Children.Add(scroll);

        // Container is swapped per view (WrapPanel for cards/table, StackPanel for list).
        var listHost = new Border();
        scroll.Content = listHost;

        // Snapshot of all entities (for editor dropdowns)
        var allKnown = new Dictionary<string, CodeEntity>();
        foreach (var t in CodeEntityService.EntityTypes)
            foreach (var e in CodeEntityService.LoadAll(projFolder, t))
                allKnown[e.Id] = e;

        var shownOrder = new List<string>();                 // entity IDs in displayed order (for Shift range)
        var rowBorders = new Dictionary<string, Border>();   // id → row visual (for highlight updates)

        void UpdateHighlights()
        {
            var accent = (Brush)(TryFindResource("AccentHighlightBrush") ?? new SolidColorBrush(Colors.DodgerBlue));
            foreach (var (id, b) in rowBorders)
            {
                if (_codeLibSelected.Contains(id))
                {
                    b.BorderBrush     = accent;
                    b.BorderThickness = new Thickness(2);
                }
                else
                {
                    b.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
                    b.BorderThickness = new Thickness(1);
                }
            }
        }

        void HandleRowClick(string id, MouseButtonEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            if ((mods & ModifierKeys.Shift) != 0 && _codeLibAnchor is not null)
            {
                int a = shownOrder.IndexOf(_codeLibAnchor), b = shownOrder.IndexOf(id);
                if (a >= 0 && b >= 0)
                {
                    if (a > b) (a, b) = (b, a);
                    _codeLibSelected.Clear();
                    for (int i = a; i <= b; i++) _codeLibSelected.Add(shownOrder[i]);
                }
            }
            else if ((mods & ModifierKeys.Control) != 0)
            {
                if (!_codeLibSelected.Add(id)) _codeLibSelected.Remove(id);
                _codeLibAnchor = id;
            }
            else
            {
                _codeLibSelected.Clear();
                _codeLibSelected.Add(id);
                _codeLibAnchor = id;
            }
            UpdateHighlights();
        }

        void OpenEditor(CodeEntity entity, Action refresh)
        {
            var dlg = new CodeEntityEditorDialog(projFolder, entity, allKnown, _currentThemePath) { Owner = this };
            dlg.ShowDialog();
            if (dlg.Saved) { allKnown[entity.Id] = entity; refresh(); }
        }

        void DeleteEntity(CodeEntity entity, Action refresh)
        {
            var res = MessageBox.Show(string.Format(Properties.Loc.S("Code_DeleteEntityConfirm"), entity.Name),
                Properties.Loc.S("Code_DeleteEntityTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;
            CodeEntityService.Delete(projFolder, entity.EntityType.ToString(), entity.Id);
            allKnown.Remove(entity.Id);
            _codeLibSelected.Remove(entity.Id);
            refresh();
        }

        // Shared event wiring for any view element (card / list row / table cell).
        void WireSelection(Border el, CodeEntity entity)
        {
            el.Cursor = Cursors.Hand;
            el.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount >= 2) { OpenEditor(entity, Refresh); return; }
                HandleRowClick(entity.Id, e);
            };
            el.MouseRightButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (!_codeLibSelected.Contains(entity.Id)) { _codeLibSelected.Clear(); _codeLibSelected.Add(entity.Id); _codeLibAnchor = entity.Id; UpdateHighlights(); }
                var cm = new ContextMenu();
                var editMi = new MenuItem { Header = Properties.Loc.S("Code_Edit") };
                editMi.Click += (_, _) => OpenEditor(entity, Refresh);
                cm.Items.Add(editMi);
                if (entity.EntityType == CodeEntityType.Function)
                {
                    var flowMi = new MenuItem { Header = Properties.Loc.S("Code_SketchFlow") };
                    flowMi.Click += (_, _) => DiagramLauncher.ChooseAndOpen(this, projFolder, entity.Id, entity.Name, _currentThemePath);
                    cm.Items.Add(flowMi);
                }
                var expMi = new MenuItem { Header = Properties.Loc.S("Code_ExportThis") };
                expMi.Click += (_, _) => ShowCodeExportDialog(projFolder, new[] { entity });
                cm.Items.Add(expMi);
                cm.Items.Add(new Separator());
                var delMi = new MenuItem { Header = Properties.Loc.S("Code_DeletePerm") };
                delMi.Click += (_, _) => DeleteEntity(entity, Refresh);
                cm.Items.Add(delMi);
                cm.IsOpen = true;
            };
        }

        string Summary(CodeEntity e) => e.EntityType switch
        {
            CodeEntityType.Function => $"({string.Join(", ", e.Ports.Where(p => p.Direction == PortDirection.Input).Select(p => p.DataType))})",
            CodeEntityType.Enum     => $"{e.EnumValues.Count} values",
            _                       => $"{e.Fields.Count} fields · {e.Methods.Count} methods"
        };

        // ── Cards view: narrow cards in a wrap panel ──
        Border BuildCardSmall(CodeEntity entity)
        {
            var card = new Border { Width = 124, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(8, 7, 8, 7), Margin = new Thickness(0, 0, 8, 8) };
            card.SetResourceReference(Border.BackgroundProperty,  "CardBgBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
            var st = new StackPanel();
            card.Child = st;
            var sym = new TextBlock { Text = CodeTypeSymbol(entity.EntityType), FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center };
            sym.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
            st.Children.Add(sym);
            var nm = new TextBlock { Text = entity.Name, FontSize = 12, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxHeight = 32 };
            nm.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
            st.Children.Add(nm);
            var sub = new TextBlock { Text = Summary(entity), FontSize = 10, Opacity = 0.55, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
            st.Children.Add(sub);
            WireSelection(card, entity);
            return card;
        }

        // ── List view: one row per entity, small icon + name + modified date ──
        Border BuildListRow(CodeEntity entity)
        {
            var row = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 0, 2) };
            row.SetResourceReference(Border.BackgroundProperty,  "CardBgBrush");
            row.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Child = grid;
            var sym = new TextBlock { Text = CodeTypeSymbol(entity.EntityType), FontSize = 13, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            sym.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
            Grid.SetColumn(sym, 0); grid.Children.Add(sym);
            var nm = new TextBlock { Text = entity.Name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            nm.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
            Grid.SetColumn(nm, 1); grid.Children.Add(nm);
            var ft = CodeEntityService.FileTime(projFolder, _codeLibType, entity.Id);
            var date = new TextBlock { Text = ft == DateTime.MinValue ? "" : ft.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), FontSize = 11, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            date.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
            Grid.SetColumn(date, 2); grid.Children.Add(date);
            WireSelection(row, entity);
            return row;
        }

        // ── Table view: compact icon + name cells in a wrap panel (no date) ──
        Border BuildTableCell(CodeEntity entity)
        {
            var cell = new Border { Width = 168, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 6, 6) };
            cell.SetResourceReference(Border.BackgroundProperty,  "CardBgBrush");
            cell.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            cell.Child = sp;
            var sym = new TextBlock { Text = CodeTypeSymbol(entity.EntityType), FontSize = 13, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            sym.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
            sp.Children.Add(sym);
            var nm = new TextBlock { Text = entity.Name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 128 };
            nm.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
            sp.Children.Add(nm);
            WireSelection(cell, entity);
            return cell;
        }

        void Refresh()
        {
            shownOrder.Clear();
            rowBorders.Clear();

            var filtered = CodeEntityService.LoadAll(projFolder, _codeLibType)
                .Where(e => string.IsNullOrWhiteSpace(_codeLibSearch)
                         || e.Name.Contains(_codeLibSearch, StringComparison.OrdinalIgnoreCase));

            var entities = _codeLibSort switch
            {
                "name_desc"     => filtered.OrderByDescending(e => e.Name),
                "modified_asc"  => filtered.OrderBy(e => CodeEntityService.FileTime(projFolder, _codeLibType, e.Id)),
                "modified_desc" => filtered.OrderByDescending(e => CodeEntityService.FileTime(projFolder, _codeLibType, e.Id)),
                _               => filtered.OrderBy(e => e.Name),
            };
            var sorted = entities.ToList();

            Panel container = _codeLibView == "list" ? new StackPanel() : new WrapPanel();
            listHost.Child = container;

            if (sorted.Count == 0)
            {
                var hint = new TextBlock
                {
                    Text = string.Format(Properties.Loc.S("Code_NoEntities"), _codeLibType),
                    Opacity = 0.55, FontSize = 13, Margin = new Thickness(4, 40, 0, 0)
                };
                hint.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
                container.Children.Add(hint);
                return;
            }

            foreach (var e in sorted)
            {
                shownOrder.Add(e.Id);
                Border el = _codeLibView switch
                {
                    "list"  => BuildListRow(e),
                    "table" => BuildTableCell(e),
                    _       => BuildCardSmall(e),
                };
                rowBorders[e.Id] = el;
                container.Children.Add(el);
            }
            UpdateHighlights();
        }

        // Wire up the view switcher now that Refresh exists.
        void SetView(string v) { _codeLibView = v; StyleViewButtons(); Refresh(); }
        var cardsBtn = MakeViewBtn("cards", "▦", Properties.Loc.S("Code_View_Cards")); cardsBtn.Click += (_, _) => SetView("cards"); viewPanel.Children.Add(cardsBtn);
        var listBtn  = MakeViewBtn("list",  "☰", Properties.Loc.S("Code_View_List"));  listBtn.Click  += (_, _) => SetView("list");  viewPanel.Children.Add(listBtn);
        var tableBtn = MakeViewBtn("table", "▤", Properties.Loc.S("Code_View_Table")); tableBtn.Click += (_, _) => SetView("table"); viewPanel.Children.Add(tableBtn);
        StyleViewButtons();

        Refresh();

        search.TextChanged += (_, _) => { _codeLibSearch = search.Text; Refresh(); };
        sortCombo.SelectionChanged += (_, _) =>
        {
            _codeLibSort = sortOpts[Math.Max(0, sortCombo.SelectedIndex)].Key;
            Refresh();
        };


        newBtn.Click += (_, _) =>
        {
            if (!Enum.TryParse<CodeEntityType>(_codeLibType, out var et)) return;
            var entity = new CodeEntity { Name = $"New{_codeLibType}", EntityType = et };
            var dlg = new CodeEntityEditorDialog(projFolder, entity, allKnown, _currentThemePath) { Owner = this };
            dlg.ShowDialog();
            if (dlg.Saved) { allKnown[entity.Id] = entity; Refresh(); }
        };
    }

    private Border BuildCodeBoardCard(CodeBoard board, string projFolder, List<CodeBoard> allBoards,
        Action<Border, CodeBoard> wire)
    {
        var card = new Border
        {
            Width           = 132,
            Height          = 84,
            CornerRadius    = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Margin          = new Thickness(0, 0, 10, 10),
            Cursor          = Cursors.Hand
        };
        card.SetResourceReference(Border.BackgroundProperty,  "CardBgBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");

        var stack = new StackPanel { Margin = new Thickness(8, 7, 8, 7), VerticalAlignment = VerticalAlignment.Center };
        card.Child = stack;

        var symbol = new TextBlock
        {
            Text      = board.Symbol,
            FontSize  = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin    = new Thickness(0, 0, 0, 4)
        };
        symbol.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
        stack.Children.Add(symbol);

        var name = new TextBlock
        {
            Text                = board.Name,
            FontSize            = 12,
            FontWeight          = FontWeights.SemiBold,
            TextAlignment       = TextAlignment.Center,
            TextWrapping        = TextWrapping.Wrap,
            MaxHeight           = 32,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
        stack.Children.Add(name);

        card.MouseEnter += (_, _) =>
            card.Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = Colors.Black, BlurRadius = 12, ShadowDepth = 2, Opacity = 0.25 };
        card.MouseLeave += (_, _) => card.Effect = null;

        wire(card, board);
        return card;
    }

    private void OpenOrActivateCodeWindow(string projFolder, CodeBoard board)
    {
        if (_openCodeWindows.TryGetValue(board.Id, out var existing) && existing.IsLoaded)
        {
            existing.Activate();
            return;
        }
        var win = new CodeBoardWindow(projFolder, board, _currentThemePath,
            subset => ShowCodeExportDialog(projFolder, subset));
        win.Owner = this;
        win.Closed += (_, _) => _openCodeWindows.Remove(board.Id);
        _openCodeWindows[board.Id] = win;
        win.Show();
    }

    private CodeBoard? ShowNewCodeBoardDialog(string projFolder)
    {
        var dialog = new Window
        {
            Title                 = Properties.Loc.S("Code_NewBoardTitle"),
            Width                 = 400,
            Height                = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ResizeMode            = ResizeMode.NoResize
        };
        ApplyDialogTheme(dialog);

        var g = new Grid { Margin = new Thickness(16) };
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // name label
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // name box
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // symbol label
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // symbol palette
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // buttons
        dialog.Content = g;

        AddLabel(g, 0, Properties.Loc.S("Code_BoardName"));
        var nameBox = AddTextBox(g, 1);

        AddLabel(g, 2, Properties.Loc.S("Code_Symbol"));
        string selectedSymbol = CodeBoardRegistryService.SymbolPalette[0];
        Border? selectedBorder = null;

        var paletteWrap = new WrapPanel { Margin = new Thickness(0, 4, 0, 8) };
        var paletteScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        paletteScroll.Content = paletteWrap;
        Grid.SetRow(paletteScroll, 3);
        g.Children.Add(paletteScroll);

        foreach (var sym in CodeBoardRegistryService.SymbolPalette)
        {
            var capSym = sym;
            var symBorder = new Border
            {
                Width  = 38, Height = 38,
                CornerRadius    = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Margin          = new Thickness(2),
                Cursor          = Cursors.Hand
            };
            symBorder.SetResourceReference(Border.BackgroundProperty,  "ControlBgBrush");
            symBorder.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
            var symText = new TextBlock
            {
                Text                = capSym,
                FontSize            = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };
            symText.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
            symBorder.Child = symText;
            symBorder.MouseLeftButtonDown += (_, _) =>
            {
                selectedSymbol = capSym;
                if (selectedBorder is not null)
                    selectedBorder.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
                symBorder.BorderBrush = SystemColors.HighlightBrush;
                selectedBorder = symBorder;
            };
            if (sym == selectedSymbol) { symBorder.BorderBrush = SystemColors.HighlightBrush; selectedBorder = symBorder; }
            paletteWrap.Children.Add(symBorder);
        }

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(btnRow, 4); g.Children.Add(btnRow);

        CodeBoard? result = null;
        var okBtn = MakeDialogBtn(Properties.Loc.S("Code_Create"));
        okBtn.Click += (_, _) =>
        {
            var name = nameBox.Text.Trim();
            if (string.IsNullOrEmpty(name)) { nameBox.Focus(); return; }
            result = new CodeBoard { Name = name, Symbol = selectedSymbol };
            dialog.DialogResult = true;
        };
        nameBox.KeyDown += (_, e) => { if (e.Key == Key.Return) okBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
        btnRow.Children.Add(okBtn);

        var cancelBtn = MakeDialogBtn(Properties.Loc.S("Common_Cancel"));
        cancelBtn.Margin = new Thickness(8, 0, 0, 0);
        cancelBtn.Click += (_, _) => dialog.DialogResult = false;
        btnRow.Children.Add(cancelBtn);

        dialog.Loaded += (_, _) => nameBox.Focus();
        dialog.ShowDialog();
        return result;
    }

    private bool ShowCodeBoardSettingsDialog(CodeBoard board)
    {
        var dialog = new Window
        {
            Title                 = Properties.Loc.S("Code_BoardSettings"),
            Width                 = 400,
            Height                = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ResizeMode            = ResizeMode.NoResize
        };
        ApplyDialogTheme(dialog);

        var g = new Grid { Margin = new Thickness(16) };
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        dialog.Content = g;

        AddLabel(g, 0, Properties.Loc.S("Code_BoardName"));
        var nameBox = AddTextBox(g, 1, board.Name);

        AddLabel(g, 2, Properties.Loc.S("Code_Symbol"));
        string selectedSymbol = board.Symbol;
        Border? selectedBorder = null;

        var paletteWrap   = new WrapPanel { Margin = new Thickness(0, 4, 0, 8) };
        var paletteScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        paletteScroll.Content = paletteWrap;
        Grid.SetRow(paletteScroll, 3); g.Children.Add(paletteScroll);

        foreach (var sym in CodeBoardRegistryService.SymbolPalette)
        {
            var capSym    = sym;
            var symBorder = new Border
            {
                Width = 38, Height = 38,
                CornerRadius    = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Margin          = new Thickness(2),
                Cursor          = Cursors.Hand
            };
            symBorder.SetResourceReference(Border.BackgroundProperty,  "ControlBgBrush");
            symBorder.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
            var symText = new TextBlock
            {
                Text = capSym, FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };
            symText.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
            symBorder.Child = symText;
            symBorder.MouseLeftButtonDown += (_, _) =>
            {
                selectedSymbol = capSym;
                if (selectedBorder is not null)
                    selectedBorder.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
                symBorder.BorderBrush = SystemColors.HighlightBrush;
                selectedBorder = symBorder;
            };
            if (sym == board.Symbol) { symBorder.BorderBrush = SystemColors.HighlightBrush; selectedBorder = symBorder; }
            paletteWrap.Children.Add(symBorder);
        }

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(btnRow, 4); g.Children.Add(btnRow);

        bool saved = false;
        var saveBtn = MakeDialogBtn(Properties.Loc.S("Common_Save"));
        saveBtn.Click += (_, _) =>
        {
            var name = nameBox.Text.Trim();
            if (string.IsNullOrEmpty(name)) { nameBox.Focus(); return; }
            board.Name   = name;
            board.Symbol = selectedSymbol;
            saved = true;
            dialog.DialogResult = true;
        };
        btnRow.Children.Add(saveBtn);

        var cancelBtn = MakeDialogBtn(Properties.Loc.S("Common_Cancel"));
        cancelBtn.Margin = new Thickness(8, 0, 0, 0);
        cancelBtn.Click += (_, _) => dialog.DialogResult = false;
        btnRow.Children.Add(cancelBtn);

        dialog.ShowDialog();
        return saved;
    }

    // ── Dialog helpers ─────────────────────────────────────────────────────

    private void ApplyDialogTheme(Window w)
    {
        if (!string.IsNullOrWhiteSpace(_currentThemePath))
        {
            try
            {
                var dict = OxsuitLoader.Load(_currentThemePath);
                if (dict is not null) w.Resources.MergedDictionaries.Add(dict);
            }
            catch { }
        }
        w.SetResourceReference(BackgroundProperty, "ContentBgBrush");
        w.SourceInitialized += (_, _) => ParticipantsWindow.TryApplyTitleBarTo(w);
    }

    private static void AddLabel(Grid g, int row, string text)
    {
        var lbl = new TextBlock { Text = text, Margin = new Thickness(0, row == 0 ? 0 : 8, 0, 2) };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
        Grid.SetRow(lbl, row); g.Children.Add(lbl);
    }

    private static TextBox AddTextBox(Grid g, int row, string value = "")
    {
        var box = new TextBox { Text = value, Margin = new Thickness(0, 0, 0, 4) };
        box.SetResourceReference(TextBox.BackgroundProperty,  "InputBgBrush");
        box.SetResourceReference(TextBox.ForegroundProperty,  "SidebarTextBrush");
        box.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        Grid.SetRow(box, row); g.Children.Add(box);
        return box;
    }

    private static Button MakeDialogBtn(string label)
    {
        var b = new Button
        {
            Content = label,
            Padding = new Thickness(12, 6, 12, 6),
            FontSize = 12
        };
        b.SetResourceReference(Button.StyleProperty,      "ModernButton");
        b.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        b.SetResourceReference(Button.ForegroundProperty, "SidebarTextBrush");
        return b;
    }

    // ── Code export (skeleton generation) ───────────────────────────────────

    private void ShowCodeExportDialog(string projFolder, IEnumerable<CodeEntity>? entities = null)
    {
        // Use the given subset (e.g. selected cards) or gather all entities.
        var all = entities?.ToList();
        if (all is null)
        {
            all = new List<CodeEntity>();
            foreach (var t in CodeEntityService.EntityTypes)
                all.AddRange(CodeEntityService.LoadAll(projFolder, t));
        }

        if (all.Count == 0)
        {
            MessageBox.Show(Properties.Loc.S("Code_NoEntitiesExport"), Properties.Loc.S("Code_ExportMsgTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Window
        {
            Title                 = Properties.Loc.S("Code_ExportTitle"),
            Width                 = 720,
            Height                = 640,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ResizeMode            = ResizeMode.CanResize
        };
        ApplyDialogTheme(dialog);

        var g = new Grid { Margin = new Thickness(14) };
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // controls
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // preview
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // buttons
        dialog.Content = g;

        // Language selector
        var ctrlRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        Grid.SetRow(ctrlRow, 0); g.Children.Add(ctrlRow);

        var langLbl = new TextBlock { Text = Properties.Loc.S("Code_Language"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        langLbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
        ctrlRow.Children.Add(langLbl);

        var langCombo = new ComboBox { Width = 160 };
        langCombo.SetResourceReference(StyleProperty, "ModernComboBox");
        var langs = new (string Label, ExportLanguage Lang)[]
        {
            ("C#", ExportLanguage.CSharp), ("C++", ExportLanguage.Cpp),
            ("Java", ExportLanguage.Java), ("TypeScript", ExportLanguage.TypeScript),
            ("Python", ExportLanguage.Python), ("Kotlin", ExportLanguage.Kotlin),
            ("Swift", ExportLanguage.Swift), ("PHP", ExportLanguage.Php),
            ("Go", ExportLanguage.Go), ("Rust", ExportLanguage.Rust),
        };
        foreach (var l in langs) langCombo.Items.Add(l.Label);
        langCombo.SelectedIndex = 0;
        ctrlRow.Children.Add(langCombo);

        var aiBtn = MakeDialogBtn(Properties.Loc.S("Code_GenAI"));
        aiBtn.Margin  = new Thickness(12, 0, 0, 0);
        aiBtn.ToolTip = Properties.Loc.S("Code_GenAITip");
        ctrlRow.Children.Add(aiBtn);

        // Preview box
        var preview = new TextBox
        {
            IsReadOnly        = true,
            AcceptsReturn     = true,
            TextWrapping      = TextWrapping.NoWrap,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily        = new FontFamily("Consolas, Cascadia Mono, monospace"),
            FontSize          = 12
        };
        preview.SetResourceReference(TextBox.BackgroundProperty,  "InputBgBrush");
        preview.SetResourceReference(TextBox.ForegroundProperty,  "SidebarTextBrush");
        preview.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        Grid.SetRow(preview, 1); g.Children.Add(preview);

        ExportLanguage CurrentLang() => langs[Math.Max(0, langCombo.SelectedIndex)].Lang;
        void Regen() => preview.Text = CodeExportService.Generate(all, CurrentLang(), projFolder);
        Regen();
        langCombo.SelectionChanged += (_, _) => Regen();

        // ── Generate full implementation with an AI participant ──
        aiBtn.Click += async (_, _) =>
        {
            var (ollama, cloud, cancelled) = ResolveCodeGenerator();
            if (cancelled) return;
            if (ollama is null && cloud is null)
            {
                MessageBox.Show(Properties.Loc.S("Code_NoAI"), Properties.Loc.S("Code_ExportMsgTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var lang     = CurrentLang();
            var skeleton = CodeExportService.Generate(all, lang, projFolder);
            var prompt   = BuildAiCodegenPrompt(all, lang, projFolder, skeleton);
            var system   = "You are a senior software engineer. Implement the stubbed bodies in the given code skeleton. " +
                           "Use the names, descriptions and flowchart hints to write idiomatic, correct code. " +
                           "Keep the existing signatures, types and structure; replace every stub " +
                           "(NotImplementedException / TODO / pass / unimplemented!() / panic) with a real implementation. " +
                           "Return ONLY the complete code — no explanations, no markdown fences.";

            aiBtn.IsEnabled = false; langCombo.IsEnabled = false;
            var origLabel = aiBtn.Content;
            aiBtn.Content = Properties.Loc.S("Code_GenAIBusy");
            preview.Text = "";
            try
            {
                var sb = new System.Text.StringBuilder();
                if (ollama is not null)
                {
                    var hist = new List<OllamaChatMessage> { new("system", system), new("user", prompt) };
                    await foreach (var tok in ollama.StreamAsync(hist, System.Threading.CancellationToken.None))
                    { sb.Append(tok); preview.Text = sb.ToString(); preview.ScrollToEnd(); }
                }
                else
                {
                    var hist = new List<CloudAIMessage> { new("user", prompt, "System") };
                    await foreach (var tok in cloud!.StreamAsync(hist, system, System.Threading.CancellationToken.None))
                    { sb.Append(tok); preview.Text = sb.ToString(); preview.ScrollToEnd(); }
                }
                if (sb.Length == 0) preview.Text = skeleton;   // fall back if the model returned nothing
            }
            catch (Exception ex)
            {
                preview.Text = skeleton;
                MessageBox.Show(string.Format(Properties.Loc.S("Code_SaveFailed"), ex.Message),
                    Properties.Loc.S("Code_ExportMsgTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                aiBtn.Content = origLabel; aiBtn.IsEnabled = true; langCombo.IsEnabled = true;
            }
        };

        // Buttons
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        Grid.SetRow(btnRow, 2); g.Children.Add(btnRow);

        var copyBtn = MakeDialogBtn(Properties.Loc.S("Code_Copy"));
        copyBtn.Click += (_, _) =>
        {
            try { Clipboard.SetText(preview.Text); } catch { }
        };
        btnRow.Children.Add(copyBtn);

        var saveBtn = MakeDialogBtn(Properties.Loc.S("Code_SaveOutput"));
        saveBtn.Margin = new Thickness(8, 0, 0, 0);
        saveBtn.Click += (_, _) =>
        {
            try
            {
                var ext = CodeExportService.FileExtension(CurrentLang());
                var dir = SysIO.Path.Combine(projFolder, "OUTPUT", "generated");
                SysIO.Directory.CreateDirectory(dir);
                var file = SysIO.Path.Combine(dir, $"skeleton_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}");
                SysIO.File.WriteAllText(file, preview.Text);
                MessageBox.Show(string.Format(Properties.Loc.S("Code_SavedTo"), SysIO.Path.GetFileName(file)), Properties.Loc.S("Code_ExportMsgTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(Properties.Loc.S("Code_SaveFailed"), ex.Message), Properties.Loc.S("Code_ExportMsgTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        btnRow.Children.Add(saveBtn);

        var closeBtn = MakeDialogBtn(Properties.Loc.S("Common_Close"));
        closeBtn.Margin = new Thickness(8, 0, 0, 0);
        closeBtn.Click += (_, _) => dialog.Close();
        btnRow.Children.Add(closeBtn);

        dialog.ShowDialog();
    }

    /// <summary>
    /// Builds the AI code-generation prompt: the deterministic skeleton (which already
    /// embeds structogram-derived bodies) plus textual flowchart hints for any function
    /// or method that has a flowchart but no structogram (the PAP→code-via-AI case).
    /// </summary>
    private string BuildAiCodegenPrompt(List<CodeEntity> all, ExportLanguage lang, string projFolder, string skeleton)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Target language: {lang}.");
        sb.AppendLine("=== CODE SKELETON ===");
        sb.AppendLine(skeleton);

        var hints = new System.Text.StringBuilder();
        void AddFlowHint(string key, string label)
        {
            if (!FlowChartService.Exists(projFolder, key)) return;
            if (StructogramService.Exists(projFolder, key)) return; // structogram already drives the body
            var fc = FlowChartService.Load(projFolder, key);
            var desc = DescribeFlow(fc);
            if (!string.IsNullOrWhiteSpace(desc))
                hints.AppendLine($"- {label}:\n{desc}");
        }

        foreach (var e in all)
        {
            if (e.EntityType == CodeEntityType.Function) AddFlowHint(e.Id, e.Name);
            foreach (var m in e.Methods)
                AddFlowHint($"{e.Id}#{m.Id}", $"{e.Name}.{m.Name}");
        }

        if (hints.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("=== FLOWCHART HINTS (implement these control flows) ===");
            sb.Append(hints);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Resolves which participant generates code: the one configured in Manager Settings
    /// (Code Generator), or — when that is empty or offline — asks the user to pick one.
    /// Returns cancelled=true if the user dismissed the picker. Never falls back to the
    /// Claudette brain.
    /// </summary>
    private (OllamaService? ollama, ICloudAIService? cloud, bool cancelled) ResolveCodeGenerator()
    {
        var online = new List<(string Name, OllamaService? O, ICloudAIService? C)>();
        foreach (var u in _ollamaParticipants.Where(u => u.Data.Enabled && u.Data.IsOnline == true))
            online.Add((u.Data.DisplayName, u.Data.Service, null));
        foreach (var u in _cloudAIParticipants.Where(u => u.Data.Enabled && u.Data.IsOnline == true))
            online.Add((u.Data.DisplayName, null, u.Data.Service));

        if (online.Count == 0) return (null, null, false);   // caller shows the "no AI" message

        var configured = SettingsService.Load().CodeGeneratorName;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var match = online.FirstOrDefault(x => x.Name.Equals(configured, StringComparison.OrdinalIgnoreCase));
            if (match.Name is not null) return (match.O, match.C, false);
            // configured participant is offline → fall through to the picker
        }

        var picked = ShowCodeGeneratorPicker(online);
        if (picked is null) return (null, null, true);       // user cancelled
        return (picked.Value.O, picked.Value.C, false);
    }

    private (string Name, OllamaService? O, ICloudAIService? C)? ShowCodeGeneratorPicker(
        List<(string Name, OllamaService? O, ICloudAIService? C)> online)
    {
        var dialog = new Window
        {
            Title                 = Properties.Loc.S("Code_PickGenerator"),
            Width                 = 360,
            Height                = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ResizeMode            = ResizeMode.NoResize
        };
        ApplyDialogTheme(dialog);

        var g = new Grid { Margin = new Thickness(14) };
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        dialog.Content = g;

        var lbl = new TextBlock { Text = Properties.Loc.S("Code_PickGeneratorHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
        Grid.SetRow(lbl, 0); g.Children.Add(lbl);

        var list = new ListBox();
        list.SetResourceReference(ListBox.BackgroundProperty,  "InputBgBrush");
        list.SetResourceReference(ListBox.ForegroundProperty,  "SidebarTextBrush");
        list.SetResourceReference(ListBox.BorderBrushProperty, "ControlBorderBrush");
        foreach (var p in online) list.Items.Add(new ListBoxItem { Content = p.Name, Tag = p.Name });
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        Grid.SetRow(list, 1); g.Children.Add(list);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        Grid.SetRow(btnRow, 2); g.Children.Add(btnRow);

        (string Name, OllamaService? O, ICloudAIService? C)? result = null;
        var okBtn = MakeDialogBtn(Properties.Loc.S("Common_OK"));
        okBtn.Click += (_, _) =>
        {
            if (list.SelectedItem is ListBoxItem { Tag: string n })
            {
                var m = online.FirstOrDefault(x => x.Name == n);
                if (m.Name is not null) result = m;
            }
            dialog.DialogResult = true;
        };
        list.MouseDoubleClick += (_, _) => okBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        btnRow.Children.Add(okBtn);

        var cancelBtn = MakeDialogBtn(Properties.Loc.S("Common_Cancel"));
        cancelBtn.Margin = new Thickness(8, 0, 0, 0);
        cancelBtn.Click += (_, _) => dialog.DialogResult = false;
        btnRow.Children.Add(cancelBtn);

        return dialog.ShowDialog() == true ? result : null;
    }

    /// <summary>Small glyph for a code entity type (used in library list/table/card views).</summary>
    private static string CodeTypeSymbol(CodeEntityType t) => t switch
    {
        CodeEntityType.Namespace => "📁",
        CodeEntityType.Class     => "🧱",
        CodeEntityType.Struct    => "📦",
        CodeEntityType.Interface => "🔷",
        CodeEntityType.Enum      => "📋",
        CodeEntityType.Function  => "⚡",
        CodeEntityType.Object    => "🔹",
        _                        => "•"
    };

    /// <summary>Renders a flowchart as plain text edges: "from -label-> to" using node texts.</summary>
    private static string DescribeFlow(Models.FlowChartData fc)
    {
        var byId = fc.Nodes.ToDictionary(n => n.Id, n => string.IsNullOrWhiteSpace(n.Text) ? n.Kind.ToString() : n.Text);
        var lines = new List<string>();
        foreach (var c in fc.Connections)
        {
            var from = byId.TryGetValue(c.FromId, out var f) ? f : "?";
            var to   = byId.TryGetValue(c.ToId,   out var t) ? t : "?";
            var lbl  = string.IsNullOrWhiteSpace(c.Label) ? "" : $" [{c.Label}]";
            lines.Add($"    {from} ->{lbl} {to}");
        }
        return string.Join("\n", lines);
    }
}
