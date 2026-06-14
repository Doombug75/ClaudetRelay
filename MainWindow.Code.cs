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

        var exportBtn = new Button
        {
            Content    = Properties.Loc.S("Code_ExportCode"),
            FontSize   = 12,
            Padding    = new Thickness(10, 5, 10, 5),
            Margin     = new Thickness(0, 4, 0, 6),
            VerticalAlignment = VerticalAlignment.Center
        };
        exportBtn.SetResourceReference(Button.StyleProperty,      "ModernButton");
        exportBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        exportBtn.SetResourceReference(Button.ForegroundProperty, "SidebarTextBrush");
        exportBtn.ToolTip = Properties.Loc.S("Code_ExportTooltip");
        exportBtn.Click += (_, _) => ShowCodeExportDialog(projFolder);
        Grid.SetColumn(exportBtn, 1);
        topRow.Children.Add(exportBtn);

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
                BuildCodeContent(projFolder);
            }));
        }

        if (_codeBoardsMode) BuildCodeBoardGallery(projFolder);
        else                 BuildCodeLibrary(projFolder);
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

        // Sub-toolbar with "+ New Board"
        var subBar = new Border { Padding = new Thickness(16, 8, 16, 8) };
        subBar.SetResourceReference(Border.BackgroundProperty, "SidebarBgBrush");
        Grid.SetRow(subBar, 0);
        host.Children.Add(subBar);

        var addBtn = new Button
        {
            Content = Properties.Loc.S("Code_NewBoard"),
            Padding = new Thickness(10, 5, 10, 5),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Right
        };
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
        subBar.Child = addBtn;

        // Board cards grid
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(20, 16, 20, 16)
        };
        Grid.SetRow(scroll, 1);
        host.Children.Add(scroll);

        if (boards.Count == 0)
        {
            var hint = new TextBlock
            {
                Text              = Properties.Loc.S("Code_NoBoards"),
                TextAlignment     = TextAlignment.Center,
                FontSize          = 13,
                Opacity           = 0.55,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin            = new Thickness(0, 60, 0, 0)
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
            scroll.Content = hint;
            return;
        }

        var wrap = new WrapPanel { ItemWidth = 220 };
        scroll.Content = wrap;

        foreach (var board in boards.OrderByDescending(b => b.UpdatedAt))
        {
            var capBoard = board;
            var card     = BuildCodeBoardCard(capBoard, projFolder, boards);
            wrap.Children.Add(card);
        }
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
        barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });  // sort
        barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });  // new
        bar.Child = barRow;

        var search = new TextBox
        {
            Text   = _codeLibSearch,
            Height = 28,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 0, 8, 0)
        };
        search.SetResourceReference(TextBox.BackgroundProperty,  "InputBgBrush");
        search.SetResourceReference(TextBox.ForegroundProperty,  "SidebarTextBrush");
        search.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        search.Tag = "🔍 search…";
        Grid.SetColumn(search, 0);
        barRow.Children.Add(search);

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
        Grid.SetColumn(sortCombo, 1);
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
        Grid.SetColumn(newBtn, 2);
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

        var list = new StackPanel();
        scroll.Content = list;

        // Snapshot of all entities (for editor dropdowns)
        var allKnown = new Dictionary<string, CodeEntity>();
        foreach (var t in CodeEntityService.EntityTypes)
            foreach (var e in CodeEntityService.LoadAll(projFolder, t))
                allKnown[e.Id] = e;

        void Refresh()
        {
            list.Children.Clear();
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

            if (sorted.Count == 0)
            {
                list.Children.Add(new TextBlock
                {
                    Text       = string.Format(Properties.Loc.S("Code_NoEntities"), _codeLibType),
                    Opacity    = 0.55,
                    FontSize   = 13,
                    Margin     = new Thickness(0, 40, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                return;
            }

            foreach (var e in sorted)
                list.Children.Add(BuildCodeLibraryRow(projFolder, e, allKnown, Refresh));
        }
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

    private Border BuildCodeLibraryRow(string projFolder, CodeEntity entity,
        Dictionary<string, CodeEntity> allKnown, Action refresh)
    {
        var row = new Border
        {
            CornerRadius    = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(12, 8, 12, 8),
            Margin          = new Thickness(0, 0, 0, 6),
            Cursor          = Cursors.Hand
        };
        row.SetResourceReference(Border.BackgroundProperty,  "CardBgBrush");
        row.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Child = grid;

        var info = new StackPanel();
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        var nameLine = new TextBlock { Text = entity.Name, FontSize = 13, FontWeight = FontWeights.SemiBold };
        nameLine.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
        info.Children.Add(nameLine);

        // Summary: member counts / signature
        string summary = entity.EntityType switch
        {
            CodeEntityType.Function  => $"({string.Join(", ", entity.Ports.Where(p => p.Direction == PortDirection.Input).Select(p => p.DataType))})",
            CodeEntityType.Enum      => $"{entity.EnumValues.Count} values",
            _                        => $"{entity.Fields.Count} fields · {entity.Methods.Count} methods"
        };
        var sub = new TextBlock { Text = summary, FontSize = 11, Opacity = 0.6 };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
        info.Children.Add(sub);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        // Flow sketch button — for functions (the "attach a flowchart" entry point)
        if (entity.EntityType == CodeEntityType.Function)
        {
            var flowBtn = new Button { Content = Properties.Loc.S("Code_FlowBtn"), Padding = new Thickness(8, 2, 8, 2), FontSize = 12, Margin = new Thickness(0, 0, 6, 0) };
            flowBtn.SetResourceReference(Button.StyleProperty,      "ModernButton");
            flowBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
            flowBtn.SetResourceReference(Button.ForegroundProperty, "SidebarTextBrush");
            flowBtn.ToolTip = Properties.Loc.S("Code_FlowTooltip");
            flowBtn.Click += (_, e) =>
            {
                e.Handled = true;
                DiagramLauncher.ChooseAndOpen(this, projFolder, entity.Id, entity.Name, _currentThemePath);
            };
            actions.Children.Add(flowBtn);
        }

        var delBtn = new Button { Content = "🗑", Padding = new Thickness(8, 2, 8, 2), FontSize = 12 };
        delBtn.SetResourceReference(Button.StyleProperty,      "ModernButton");
        delBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        delBtn.SetResourceReference(Button.ForegroundProperty, "SidebarTextBrush");
        delBtn.ToolTip = Properties.Loc.S("Code_DeleteEntityTooltip");
        actions.Children.Add(delBtn);

        delBtn.Click += (_, e) =>
        {
            e.Handled = true;
            var res = MessageBox.Show(string.Format(Properties.Loc.S("Code_DeleteEntityConfirm"), entity.Name),
                Properties.Loc.S("Code_DeleteEntityTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;
            CodeEntityService.Delete(projFolder, entity.EntityType.ToString(), entity.Id);
            allKnown.Remove(entity.Id);
            refresh();
        };

        row.MouseLeftButtonDown += (_, _) =>
        {
            var dlg = new CodeEntityEditorDialog(projFolder, entity, allKnown, _currentThemePath) { Owner = this };
            dlg.ShowDialog();
            if (dlg.Saved) { allKnown[entity.Id] = entity; refresh(); }
        };

        row.MouseEnter += (_, _) =>
            row.Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = Colors.Black, BlurRadius = 8, ShadowDepth = 1, Opacity = 0.2 };
        row.MouseLeave += (_, _) => row.Effect = null;

        return row;
    }

    private Border BuildCodeBoardCard(CodeBoard board, string projFolder, List<CodeBoard> allBoards)
    {
        var card = new Border
        {
            Width           = 200,
            Height          = 120,
            CornerRadius    = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Margin          = new Thickness(0, 0, 16, 16),
            Cursor          = Cursors.Hand
        };
        card.SetResourceReference(Border.BackgroundProperty,  "CardBgBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");

        var stack = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
        card.Child = stack;

        var symbol = new TextBlock
        {
            Text      = board.Symbol,
            FontSize  = 28,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin    = new Thickness(0, 0, 0, 6)
        };
        symbol.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
        stack.Children.Add(symbol);

        var name = new TextBlock
        {
            Text                = board.Name,
            FontSize            = 13,
            FontWeight          = FontWeights.SemiBold,
            TextAlignment       = TextAlignment.Center,
            TextWrapping        = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "SidebarTextBrush");
        stack.Children.Add(name);

        // Open on double-click or Enter
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount >= 2) OpenOrActivateCodeWindow(projFolder, board);
        };

        card.MouseEnter += (_, _) =>
            card.Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = Colors.Black, BlurRadius = 12, ShadowDepth = 2, Opacity = 0.25 };
        card.MouseLeave += (_, _) => card.Effect = null;

        // Right-click context menu
        card.MouseRightButtonDown += (_, e) =>
        {
            var cm = new ContextMenu();

            var openMi = new MenuItem { Header = Properties.Loc.S("Code_OpenBoard") };
            openMi.Click += (_, _) => OpenOrActivateCodeWindow(projFolder, board);
            cm.Items.Add(openMi);

            cm.Items.Add(new Separator());

            var renameMi = new MenuItem { Header = Properties.Loc.S("Code_RenameBoard") };
            renameMi.Click += (_, _) =>
            {
                if (ShowCodeBoardSettingsDialog(board))
                {
                    board.UpdatedAt = DateTime.UtcNow;
                    CodeBoardRegistryService.Save(projFolder, allBoards);
                    BuildCodeContent(projFolder);
                }
            };
            cm.Items.Add(renameMi);

            cm.Items.Add(new Separator());

            var delMi = new MenuItem { Header = Properties.Loc.S("Code_DeleteBoard") };
            delMi.Click += (_, _) =>
            {
                var res = MessageBox.Show(
                    string.Format(Properties.Loc.S("Code_DeleteBoardConfirm"), board.Name),
                    Properties.Loc.S("Code_DeleteBoardTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;

                // Close window if open
                if (_openCodeWindows.TryGetValue(board.Id, out var w)) { w.Close(); _openCodeWindows.Remove(board.Id); }

                allBoards.Remove(board);
                CodeBoardRegistryService.Save(projFolder, allBoards);
                BuildCodeContent(projFolder);
            };
            cm.Items.Add(delMi);

            cm.IsOpen = true;
            e.Handled = true;
        };

        return card;
    }

    private void OpenOrActivateCodeWindow(string projFolder, CodeBoard board)
    {
        if (_openCodeWindows.TryGetValue(board.Id, out var existing) && existing.IsLoaded)
        {
            existing.Activate();
            return;
        }
        var win = new CodeBoardWindow(projFolder, board, _currentThemePath);
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

    private void ShowCodeExportDialog(string projFolder)
    {
        // Gather all entities
        var all = new List<CodeEntity>();
        foreach (var t in CodeEntityService.EntityTypes)
            all.AddRange(CodeEntityService.LoadAll(projFolder, t));

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
        void Regen() => preview.Text = CodeExportService.Generate(all, CurrentLang());
        Regen();
        langCombo.SelectionChanged += (_, _) => Regen();

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
}
