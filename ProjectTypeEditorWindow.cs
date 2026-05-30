using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ClaudetRelay.Services;

namespace ClaudetRelay;

/// <summary>
/// In-built editor for creating, modifying and deleting project types
/// stored as XAML ResourceDictionaries in ProjectTypes/*.xaml.
/// </summary>
internal sealed class ProjectTypeEditorWindow : Window
{
    // ── Internals ──────────────────────────────────────────────────────────

    private sealed record TypeEntry(ProjectTypeDefinition Def, string? FilePath)
    {
        public override string ToString() => $"{Def.Icon}  {Def.Name}";
    }

    // Common emoji available in the icon picker, grouped visually
    private static readonly string[] PickerEmoji =
    [
        "📁","📋","📝","📊","📈","💡","🎯","⚙️","🔧","🔬",
        "📖","🎨","🎵","🎭","🎬","🎮","✏️","🖋️","📷","🎙️",
        "💼","🏢","📱","💻","🖥️","🤝","📧","💰","📦","🏗️",
        "🧪","🔭","🧬","🌍","🌐","🌌","⚗️","🧲","🔋","💊",
        "🚀","🏆","🎓","❤️","🌟","🌈","🏠","🗺️","⚡","🎁",
    ];

    // Help content for each field: (title, body)
    private static readonly (string Title, string Body)[] FieldHelp =
    [
        ("Name",
         "The display name of this project type. Shown in menus, the \"New Project\" picker, and the project header.\n\n" +
         "Examples:\n  • Novel\n  • Software Project\n  • Theatre Production\n\nKeep it short — one to three words works best."),

        ("Icon",
         "A single emoji that visually represents this project type. " +
         "Shown next to the name in all menus and picker cards.\n\n" +
         "Examples:\n  • 📖  for a Novel\n  • 💻  for a Software project\n  • 🎭  for Theatre\n\n" +
         "Click the icon button to open the picker, or type any emoji directly."),

        ("Description",
         "A one-to-three sentence description of what this project type is for. " +
         "Shown on the type card when the user picks a type for a new project.\n\n" +
         "Examples:\n  • \"A work of long-form fiction. Organise your story into chapters and scenes, " +
         "build a cast of characters and track your plot notes.\"\n\n" +
         "  • \"A software development project. Plan features, assign tasks and track deadlines.\""),

        ("Structure Hierarchy",
         "Defines the organisational levels inside this project — pipe-separated, outermost level first.\n\n" +
         "This controls how items are nested in the project tree.\n\n" +
         "Examples:\n  • Chapter|Scene  (fiction with chapters containing scenes)\n" +
         "  • Milestone|Task  (software with milestones containing tasks)\n" +
         "  • Act|Scene  (theatre with acts containing scenes)\n\n" +
         "Leave empty for a flat project with no nested structure."),

        ("Structure Icons",
         "One emoji per level, in the same order as the Structure Hierarchy above. " +
         "These icons appear in the project tree next to each item.\n\n" +
         "Examples:\n  • 📖|🎬  (Chapter icon | Scene icon)\n" +
         "  • 🏁|✅  (Milestone icon | Task icon)\n\n" +
         "Must have the same number of entries as Structure Hierarchy. " +
         "Leave empty to use default icons."),

        ("World-Building Folders",
         "Pipe-separated names for sub-folders created inside PROJECTPLAN/ for world-building content. " +
         "The World Building button in the project sidebar opens these folders.\n\n" +
         "Examples:\n  • Characters|Factions|Locations  (for a novel or game world)\n" +
         "  • Characters|Props|Sets  (for theatre or film)\n\n" +
         "Leave empty if this project type has no world-building content. " +
         "Also enable the \"World Building\" feature flag below."),

        ("Feature Flags",
         "Toggle which panels and features are active for this project type:\n\n" +
         "  📋 Roadmap — shows the roadmap/milestone panel in the sidebar.\n" +
         "  🌍 World Building — enables the world-building folders and the world button.\n" +
         "  👤 Assignees — allows tasks to be assigned to team members.\n" +
         "  📅 Deadlines — adds deadline fields to tasks and milestones.\n" +
         "  📝 Plot Notes — shows a dedicated plot notes section (fiction projects).\n" +
         "  🎭 Stage Directions — enables stage-direction formatting (theatre/screenplay)."),

        ("AI System Prompt Hint",
         "A short paragraph appended to every AI participant's system prompt whenever this project type is active. " +
         "Use it to give the AI context about the domain and how it should behave.\n\n" +
         "Example (Novel):\n  \"You are co-writing a novel. Think in terms of plot arcs, character development, " +
         "scene pacing and narrative voice. Flag plot holes or character inconsistencies when you spot them.\"\n\n" +
         "Example (Software):\n  \"You are part of a software development team. Reason carefully about " +
         "requirements and architecture before suggesting solutions. Identify edge cases and security concerns.\""),
    ];

    private readonly string  _typesDir;
    private string?          _editingFilePath;

    // ── Controls ──────────────────────────────────────────────────────────

    private readonly ListBox    _typeList;
    private readonly TextBlock  _editorPlaceholder;
    private readonly StackPanel _editorPanel;
    private          ScrollViewer? _editorScroll;

    private readonly TextBox    _nameBox;
    private readonly Button     _iconBtn;       // shows current icon + opens picker
    private          string     _currentIcon = "📁";
    private readonly TextBox    _descBox;
    private readonly TextBox    _hierarchyBox;
    private readonly TextBox    _iconsBox;
    private readonly TextBox    _worldFoldersBox;
    private readonly CheckBox   _roadmapCheck;
    private readonly CheckBox   _worldBuildingCheck;
    private readonly CheckBox   _assigneesCheck;
    private readonly CheckBox   _deadlinesCheck;
    private readonly CheckBox   _plotNotesCheck;
    private readonly CheckBox   _stageDirectionsCheck;
    private readonly TextBox    _hintBox;
    private readonly Button     _deleteBtn;

    // ── Construction ──────────────────────────────────────────────────────

    public ProjectTypeEditorWindow(Window owner, string typesDir, string? themePath)
    {
        _typesDir = typesDir;

        if (themePath is not null)
        {
            try
            {
                var dict = OxsuitLoader.Load(themePath);
                if (dict is not null)
                    Resources.MergedDictionaries.Add(dict);
            }
            catch { /* theme unavailable */ }
        }

        Owner                 = owner;
        Title                 = "Project Type Editor · ClaudetRelay";
        Width                 = 820;
        Height                = 680;
        MinWidth              = 700;
        MinHeight             = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode            = ResizeMode.CanResize;
        ShowInTaskbar         = false;
        Background            = Br("SidebarBgBrush");

        // ── Root grid: left list | right form ─────────────────────────────
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Content = root;

        // ── Left panel ────────────────────────────────────────────────────
        var leftPanel = new DockPanel { Background = Br("SidebarBgBrush") };
        Grid.SetColumn(leftPanel, 0);
        root.Children.Add(leftPanel);

        var leftTitle = new TextBlock
        {
            Text       = "PROJECT TYPES",
            FontSize   = 10,
            FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = Br("ContentDimBrush"),
            Margin     = new Thickness(12, 14, 12, 6)
        };
        DockPanel.SetDock(leftTitle, Dock.Top);
        leftPanel.Children.Add(leftTitle);

        var leftBtns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(8, 6, 8, 10)
        };
        DockPanel.SetDock(leftBtns, Dock.Bottom);
        leftPanel.Children.Add(leftBtns);

        var newBtn    = MakeSmallBtn("＋ New",  Br("SidebarTextBrush"));
        newBtn.Click += NewType_Click;
        newBtn.Margin = new Thickness(0, 0, 6, 0);
        leftBtns.Children.Add(newBtn);

        _deleteBtn          = MakeSmallBtn("Delete", Br("ContentDimBrush"));
        _deleteBtn.Click   += DeleteType_Click;
        _deleteBtn.IsEnabled = false;
        leftBtns.Children.Add(_deleteBtn);

        // Vertical separator
        var sepLine = new Border
        {
            Width               = 1,
            Background          = Br("ControlBgBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Stretch
        };
        Grid.SetColumn(sepLine, 0);
        root.Children.Add(sepLine);

        _typeList = new ListBox
        {
            Background        = Brushes.Transparent,
            BorderThickness   = new Thickness(0),
            FontFamily        = new FontFamily("Segoe UI"),
            FontSize          = 13,
            Foreground        = Br("SidebarTextBrush"),
            SelectionMode     = SelectionMode.Single,
            Margin            = new Thickness(6, 0, 6, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        _typeList.SelectionChanged += TypeList_SelectionChanged;
        leftPanel.Children.Add(_typeList);

        // ── Right panel ───────────────────────────────────────────────────
        var rightBg = new Grid { Background = Br("ContentBgBrush") };
        Grid.SetColumn(rightBg, 1);
        root.Children.Add(rightBg);

        _editorPlaceholder = new TextBlock
        {
            Text                = "Select a project type from the list,\nor click  ＋ New  to create one.",
            FontSize            = 13,
            FontFamily          = new FontFamily("Segoe UI"),
            Foreground          = Br("ContentDimBrush"),
            TextAlignment       = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            TextWrapping        = TextWrapping.Wrap
        };
        rightBg.Children.Add(_editorPlaceholder);

        _editorScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Visibility = Visibility.Collapsed
        };
        rightBg.Children.Add(_editorScroll);

        _editorPanel = new StackPanel { Margin = new Thickness(24, 18, 24, 20) };
        _editorScroll.Content = _editorPanel;

        // ── NAME + ICON row ───────────────────────────────────────────────
        // Header row: "NAME" label + (?) | gap | "ICON" label
        var nameIconHeaderRow = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        nameIconHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameIconHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        nameIconHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        nameIconHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });

        var nameLbl = MakeLabelText("NAME");
        var nameHelpBtn = MakeHelpBtn(0);   // FieldHelp[0] = Name

        var iconLbl = MakeLabelText("ICON");
        Grid.SetColumn(nameLbl,     0);
        Grid.SetColumn(nameHelpBtn, 1);
        Grid.SetColumn(iconLbl,     3);
        nameIconHeaderRow.Children.Add(nameLbl);
        nameIconHeaderRow.Children.Add(nameHelpBtn);
        nameIconHeaderRow.Children.Add(iconLbl);
        _editorPanel.Children.Add(nameIconHeaderRow);

        // Input row: nameBox | gap | iconBtn
        _nameBox = MakeTextBox(14);
        _iconBtn = new Button
        {
            Content         = "📁",
            FontSize        = 18,
            FontFamily      = new FontFamily("Segoe UI"),
            Background      = Br("ControlBgBrush"),
            Foreground      = Br("ContentTextBrush"),
            BorderThickness = new Thickness(0),
            Cursor          = Cursors.Hand,
            Height          = 38,
            Width           = 70,
            ToolTip         = "Pick an icon"
        };
        _iconBtn.Click += IconBtn_Click;

        var nameIconInputRow = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        nameIconInputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameIconInputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        nameIconInputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });

        var nameBorder = WrapInput(_nameBox);
        Grid.SetColumn(nameBorder, 0);
        Grid.SetColumn(_iconBtn,   2);
        nameIconInputRow.Children.Add(nameBorder);
        nameIconInputRow.Children.Add(_iconBtn);
        _editorPanel.Children.Add(nameIconInputRow);

        // ── Remaining fields ──────────────────────────────────────────────
        _descBox        = MakeTextBox(13, multiLine: true, minHeight: 68);
        _hierarchyBox   = MakeTextBox(13);
        _iconsBox       = MakeTextBox(13);
        _worldFoldersBox= MakeTextBox(13);
        _hintBox        = MakeTextBox(13, multiLine: true, minHeight: 110);

        // DESCRIPTION (help index 2)
        _editorPanel.Children.Add(MakeLabelRow("DESCRIPTION", 2));
        _editorPanel.Children.Add(WrapInput(_descBox, bottomMargin: 4));
        _editorPanel.Children.Add(MakeHint("Shown on the type card when the user creates a new project."));

        // STRUCTURE HIERARCHY (help index 3)
        _editorPanel.Children.Add(MakeLabelRow("STRUCTURE HIERARCHY", 3, topMargin: 14));
        _editorPanel.Children.Add(WrapInput(_hierarchyBox, bottomMargin: 4));
        _editorPanel.Children.Add(MakeHint("Pipe-separated level names, outermost first.  e.g.  Chapter|Scene"));

        // STRUCTURE ICONS (help index 4)
        _editorPanel.Children.Add(MakeLabelRow("STRUCTURE ICONS", 4, topMargin: 14));
        _editorPanel.Children.Add(WrapInput(_iconsBox, bottomMargin: 4));
        _editorPanel.Children.Add(MakeHint("One emoji per level, matching the hierarchy above.  e.g.  📖|🎬"));

        // WORLD-BUILDING FOLDERS (help index 5)
        _editorPanel.Children.Add(MakeLabelRow("WORLD-BUILDING FOLDERS", 5, topMargin: 14));
        _editorPanel.Children.Add(WrapInput(_worldFoldersBox, bottomMargin: 4));
        _editorPanel.Children.Add(MakeHint("Sub-folders inside PROJECTPLAN/ for world content.  e.g.  Characters|Locations"));

        // FEATURE FLAGS (help index 6)
        _editorPanel.Children.Add(MakeLabelRow("FEATURE FLAGS", 6, topMargin: 14));

        _roadmapCheck         = MakeCheck("📋  Roadmap",           true);
        _worldBuildingCheck   = MakeCheck("🌍  World Building",    false);
        _assigneesCheck       = MakeCheck("👤  Assignees",         false);
        _deadlinesCheck       = MakeCheck("📅  Deadlines",         false);
        _plotNotesCheck       = MakeCheck("📝  Plot Notes",        false);
        _stageDirectionsCheck = MakeCheck("🎭  Stage Directions",  false);

        var flagGrid = new UniformGrid { Columns = 2, Margin = new Thickness(4, 4, 0, 0) };
        flagGrid.Children.Add(_roadmapCheck);
        flagGrid.Children.Add(_worldBuildingCheck);
        flagGrid.Children.Add(_assigneesCheck);
        flagGrid.Children.Add(_deadlinesCheck);
        flagGrid.Children.Add(_plotNotesCheck);
        flagGrid.Children.Add(_stageDirectionsCheck);
        _editorPanel.Children.Add(flagGrid);

        // AI SYSTEM PROMPT HINT (help index 7)
        _editorPanel.Children.Add(MakeLabelRow("AI SYSTEM PROMPT HINT", 7, topMargin: 16));
        _editorPanel.Children.Add(WrapInput(_hintBox, bottomMargin: 4));
        _editorPanel.Children.Add(MakeHint("Appended to every AI participant's system prompt while this project type is active."));

        // ── Save / Close buttons ──────────────────────────────────────────
        var saveBtn = MakeActionBtn("Save Type", Br("AccentBgBrush"), Br("SidebarBgBrush"));
        saveBtn.Click += SaveType_Click;
        var closeBtn = MakeActionBtn("Close", Br("ControlBgBrush"), Br("ContentTextBrush"));
        closeBtn.Margin = new Thickness(10, 0, 0, 0);
        closeBtn.Click += (_, _) => Close();

        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, 20, 0, 4)
        };
        btnRow.Children.Add(saveBtn);
        btnRow.Children.Add(closeBtn);
        _editorPanel.Children.Add(btnRow);

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded  += (_, _) => ApplyCaptionColor();

        LoadTypes();
    }

    // ── Icon picker ───────────────────────────────────────────────────────

    private void IconBtn_Click(object sender, RoutedEventArgs e)
    {
        var popup = new Popup
        {
            PlacementTarget = _iconBtn,
            Placement       = PlacementMode.Bottom,
            StaysOpen       = false,
            AllowsTransparency = true
        };

        var wrap = new WrapPanel { Width = 280, Margin = new Thickness(4) };

        foreach (var em in PickerEmoji)
        {
            var captured = em;
            var btn = new Button
            {
                Content         = em,
                FontSize        = 20,
                Width           = 40,
                Height          = 40,
                Background      = Brushes.Transparent,
                Foreground      = Br("SidebarTextBrush"),
                BorderThickness = new Thickness(0),
                Cursor          = Cursors.Hand,
                Margin          = new Thickness(1),
                ToolTip         = em
            };
            btn.Click += (_, _) =>
            {
                SetIcon(captured);
                popup.IsOpen = false;
            };
            wrap.Children.Add(btn);
        }

        popup.Child = new Border
        {
            Background      = Br("InputBgBrush"),
            BorderBrush     = Br("AccentBgBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(8),
            Padding         = new Thickness(6),
            Child           = wrap
        };

        popup.IsOpen = true;
    }

    private void SetIcon(string icon)
    {
        _currentIcon    = icon;
        _iconBtn.Content = icon;
    }

    // ── Help popup ────────────────────────────────────────────────────────

    private Button MakeHelpBtn(int helpIndex)
    {
        var btn = new Button
        {
            Content         = "?",
            Width           = 18,
            Height          = 18,
            FontSize        = 10,
            FontFamily      = new FontFamily("Segoe UI"),
            FontWeight      = FontWeights.Bold,
            Background      = Br("ControlBgBrush"),
            Foreground      = Br("ContentDimBrush"),
            BorderThickness = new Thickness(0),
            Cursor          = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding         = new Thickness(0),
            ToolTip         = "Help"
        };
        btn.Click += (s, _) => ShowHelp((Button)s!, helpIndex);
        return btn;
    }

    private void ShowHelp(Button anchor, int helpIndex)
    {
        var (title, body) = FieldHelp[helpIndex];

        var titleBlock = new TextBlock
        {
            Text       = title,
            FontSize   = 13,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = Br("SidebarTextBrush"),
            Margin     = new Thickness(0, 0, 0, 8)
        };
        var bodyBlock = new TextBlock
        {
            Text         = body,
            FontSize     = 12,
            FontFamily   = new FontFamily("Segoe UI"),
            Foreground   = Br("SidebarTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight   = 18
        };

        var popup = new Popup
        {
            PlacementTarget    = anchor,
            Placement          = PlacementMode.Left,
            StaysOpen          = false,
            AllowsTransparency = true,
            Width              = 360
        };

        popup.Child = new Border
        {
            Background      = Br("SidebarBgBrush"),
            BorderBrush     = Br("AccentBgBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(8),
            Padding         = new Thickness(16, 12, 16, 14),
            MaxWidth        = 360,
            Child           = new StackPanel
            {
                Children = { titleBlock, bodyBlock }
            }
        };

        popup.IsOpen = true;
    }

    // ── Data ──────────────────────────────────────────────────────────────

    private void LoadTypes()
    {
        var selectedName = (_typeList.SelectedItem as TypeEntry)?.Def.Name;
        _typeList.Items.Clear();

        if (!Directory.Exists(_typesDir)) return;

        foreach (var file in Directory.GetFiles(_typesDir, "*.xaml")
                                      .OrderBy(Path.GetFileNameWithoutExtension,
                                               StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var dict = new ResourceDictionary { Source = new Uri(file) };
                if (dict["ProjectType"] is ProjectTypeDefinition ptd)
                    _typeList.Items.Add(new TypeEntry(ptd, file));
            }
            catch { /* skip malformed */ }
        }

        if (selectedName is not null)
            foreach (TypeEntry item in _typeList.Items)
                if (item.Def.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase))
                { _typeList.SelectedItem = item; break; }
    }

    // ── Event handlers ────────────────────────────────────────────────────

    private void TypeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_typeList.SelectedItem is not TypeEntry entry) { ShowPlaceholder(); return; }
        PopulateForm(entry.Def, entry.FilePath);
        _deleteBtn.IsEnabled = true;
    }

    private void NewType_Click(object sender, RoutedEventArgs e)
    {
        _typeList.SelectedItem = null;
        _deleteBtn.IsEnabled   = false;
        PopulateForm(new ProjectTypeDefinition
            { Name = "New Type", Icon = "📁", HasRoadmap = true }, filePath: null);
        _nameBox.Focus();
        _nameBox.SelectAll();
    }

    private void DeleteType_Click(object sender, RoutedEventArgs e)
    {
        if (_typeList.SelectedItem is not TypeEntry entry) return;
        if (entry.FilePath is null) { _typeList.SelectedItem = null; return; }

        var isGeneral = entry.Def.Name.Equals("General", StringComparison.OrdinalIgnoreCase);
        var msg = isGeneral
            ? "\"General\" is the built-in fallback type.\nIf deleted, ClaudetRelay will recreate a default one automatically.\n\nDelete anyway?"
            : $"Delete project type \"{entry.Def.Name}\"?\n\nFile: {Path.GetFileName(entry.FilePath)}\n\nThis cannot be undone.";

        if (MessageBox.Show(msg, "Delete Project Type",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        try { File.Delete(entry.FilePath); }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not delete file:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        ShowPlaceholder();
        _editingFilePath     = null;
        _deleteBtn.IsEnabled = false;
        LoadTypes();
    }

    private void SaveType_Click(object sender, RoutedEventArgs e)
    {
        var name = _nameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Please enter a name for the project type.", "Missing Name",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            _nameBox.Focus();
            return;
        }

        var icon     = string.IsNullOrWhiteSpace(_currentIcon) ? "📁" : _currentIcon;
        var safeName = DeriveFileName(name);
        var newPath  = Path.Combine(_typesDir, safeName + ".xaml");

        // Delete old file if it was renamed
        if (_editingFilePath is not null &&
            !string.Equals(_editingFilePath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(_editingFilePath); } catch { /* best effort */ }
        }

        var def = new ProjectTypeDefinition
        {
            Name                = name,
            Icon                = icon,
            Description         = _descBox.Text.Trim(),
            StructureHierarchy  = _hierarchyBox.Text.Trim(),
            StructureIcons      = _iconsBox.Text.Trim(),
            WorldFolders        = _worldFoldersBox.Text.Trim(),
            HasRoadmap          = _roadmapCheck.IsChecked         == true,
            HasWorldBuilding    = _worldBuildingCheck.IsChecked   == true,
            HasAssignees        = _assigneesCheck.IsChecked       == true,
            HasDeadlines        = _deadlinesCheck.IsChecked       == true,
            HasPlotNotes        = _plotNotesCheck.IsChecked       == true,
            HasStageDirections  = _stageDirectionsCheck.IsChecked == true,
            SystemPromptHint    = _hintBox.Text.Trim()
        };

        try
        {
            Directory.CreateDirectory(_typesDir);
            File.WriteAllText(newPath, BuildXaml(def), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save:\n{ex.Message}", "Save Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _editingFilePath = newPath;
        LoadTypes();

        foreach (TypeEntry item in _typeList.Items)
            if (string.Equals(item.FilePath, newPath, StringComparison.OrdinalIgnoreCase))
            { _typeList.SelectedItem = item; break; }
    }

    // ── Form helpers ──────────────────────────────────────────────────────

    private void PopulateForm(ProjectTypeDefinition def, string? filePath)
    {
        _editingFilePath = filePath;
        _nameBox.Text              = def.Name;
        SetIcon(def.Icon);
        _descBox.Text              = def.Description;
        _hierarchyBox.Text         = def.StructureHierarchy;
        _iconsBox.Text             = def.StructureIcons;
        _worldFoldersBox.Text      = def.WorldFolders;
        _roadmapCheck.IsChecked         = def.HasRoadmap;
        _worldBuildingCheck.IsChecked   = def.HasWorldBuilding;
        _assigneesCheck.IsChecked       = def.HasAssignees;
        _deadlinesCheck.IsChecked       = def.HasDeadlines;
        _plotNotesCheck.IsChecked       = def.HasPlotNotes;
        _stageDirectionsCheck.IsChecked = def.HasStageDirections;
        _hintBox.Text = def.SystemPromptHint;

        if (_editorScroll is not null) _editorScroll.Visibility = Visibility.Visible;
        _editorPlaceholder.Visibility = Visibility.Collapsed;
    }

    private void ShowPlaceholder()
    {
        if (_editorScroll is not null) _editorScroll.Visibility = Visibility.Collapsed;
        _editorPlaceholder.Visibility = Visibility.Visible;
    }

    // ── XAML generation ───────────────────────────────────────────────────

    private static string BuildXaml(ProjectTypeDefinition d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"");
        sb.AppendLine("                    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"");
        sb.AppendLine("                    xmlns:svc=\"clr-namespace:ClaudetRelay.Services;assembly=ClaudetRelay\">");
        sb.Append("    <svc:ProjectTypeDefinition x:Key=\"ProjectType\"");
        sb.Append($"\n        Name=\"{X(d.Name)}\"");
        sb.Append($"\n        Icon=\"{X(d.Icon)}\"");
        if (!string.IsNullOrWhiteSpace(d.Description))
            sb.Append($"\n        Description=\"{X(d.Description)}\"");
        if (!string.IsNullOrWhiteSpace(d.StructureHierarchy))
            sb.Append($"\n        StructureHierarchy=\"{X(d.StructureHierarchy)}\"");
        if (!string.IsNullOrWhiteSpace(d.StructureIcons))
            sb.Append($"\n        StructureIcons=\"{X(d.StructureIcons)}\"");
        if (!string.IsNullOrWhiteSpace(d.WorldFolders))
            sb.Append($"\n        WorldFolders=\"{X(d.WorldFolders)}\"");
        sb.Append($"\n        HasRoadmap=\"{d.HasRoadmap}\"");
        sb.Append($"\n        HasWorldBuilding=\"{d.HasWorldBuilding}\"");
        if (d.HasAssignees)       sb.Append("\n        HasAssignees=\"True\"");
        if (d.HasDeadlines)       sb.Append("\n        HasDeadlines=\"True\"");
        if (d.HasPlotNotes)       sb.Append("\n        HasPlotNotes=\"True\"");
        if (d.HasStageDirections) sb.Append("\n        HasStageDirections=\"True\"");
        if (!string.IsNullOrWhiteSpace(d.SystemPromptHint))
            sb.Append($"\n        SystemPromptHint=\"{X(d.SystemPromptHint)}\"");
        sb.AppendLine("/>");
        sb.AppendLine("</ResourceDictionary>");
        return sb.ToString();
    }

    private static string X(string s) => s
        .Replace("&",  "&amp;")
        .Replace("<",  "&lt;")
        .Replace(">",  "&gt;")
        .Replace("\"", "&quot;");

    private static string DeriveFileName(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (char.IsWhiteSpace(c) && sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        var result = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(result) ? "NewProjectType" : result;
    }

    // ── DWM title-bar theming ─────────────────────────────────────────────

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DwmwaCaptionColor = 35;   // Windows 11+ only
    private const int DwmwaTextColor    = 36;

    private void ApplyCaptionColor()
    {
        try
        {
            var helper = new WindowInteropHelper(this);
            if (helper.Handle == IntPtr.Zero) return;

            // Title-bar background = SidebarBrush
            if (Br("SidebarBgBrush") is SolidColorBrush bg)
            {
                var c = bg.Color;
                int colorRef = c.R | (c.G << 8) | (c.B << 16);
                DwmSetWindowAttribute(helper.Handle, DwmwaCaptionColor, ref colorRef, sizeof(int));
            }

            // Title-bar text = SidebarTextBrush
            if (Br("SidebarTextBrush") is SolidColorBrush fg)
            {
                var c = fg.Color;
                int colorRef = c.R | (c.G << 8) | (c.B << 16);
                DwmSetWindowAttribute(helper.Handle, DwmwaTextColor, ref colorRef, sizeof(int));
            }
        }
        catch { /* DWM unavailable (Windows 10 or older) */ }
    }

    // ── UI factory helpers ────────────────────────────────────────────────

    private Brush Br(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private TextBlock MakeLabelText(string text) => new()
    {
        Text       = text,
        FontSize   = 10,
        FontWeight = FontWeights.Bold,
        FontFamily = new FontFamily("Segoe UI"),
        Foreground = Br("ContentDimBrush"),
        VerticalAlignment = VerticalAlignment.Bottom
    };

    /// <summary>Label row: text on left, ? button on right.</summary>
    private Grid MakeLabelRow(string label, int helpIndex, double topMargin = 8)
    {
        var g = new Grid { Margin = new Thickness(0, topMargin, 0, 5) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });

        var lbl = MakeLabelText(label);
        var btn = MakeHelpBtn(helpIndex);
        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(btn, 1);
        g.Children.Add(lbl);
        g.Children.Add(btn);
        return g;
    }

    private TextBlock MakeHint(string text) => new()
    {
        Text         = text,
        FontSize     = 11,
        FontFamily   = new FontFamily("Segoe UI"),
        Foreground   = Br("ContentDimBrush"),
        TextWrapping = TextWrapping.Wrap,
        Margin       = new Thickness(0, 3, 0, 0)
    };

    private TextBox MakeTextBox(double fontSize, bool multiLine = false, double minHeight = 36) => new()
    {
        FontSize        = fontSize,
        FontFamily      = new FontFamily("Segoe UI"),
        Background      = Brushes.Transparent,
        Foreground      = Br("ContentTextBrush"),
        CaretBrush      = Br("ContentTextBrush"),
        SelectionBrush  = Br("AccentBgBrush"),
        BorderThickness = new Thickness(0),
        Padding         = new Thickness(10, multiLine ? 8 : 0, 10, multiLine ? 8 : 0),
        VerticalContentAlignment    = multiLine ? VerticalAlignment.Top : VerticalAlignment.Center,
        TextWrapping                = multiLine ? TextWrapping.Wrap : TextWrapping.NoWrap,
        AcceptsReturn               = multiLine,
        MinHeight                   = minHeight,
        MaxHeight                   = multiLine ? minHeight * 2 : 36,
        VerticalScrollBarVisibility = multiLine ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled
    };

    private Border WrapInput(UIElement child, double bottomMargin = 0, int gridColumn = -1)
    {
        var b = new Border
        {
            Background      = Br("InputBgBrush"),
            BorderBrush     = Br("ControlBgBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(6),
            Margin          = new Thickness(0, 0, 0, bottomMargin),
            Child           = child
        };
        if (gridColumn >= 0) Grid.SetColumn(b, gridColumn);
        return b;
    }

    private CheckBox MakeCheck(string label, bool isChecked) => new()
    {
        Content    = label,
        IsChecked  = isChecked,
        FontFamily = new FontFamily("Segoe UI"),
        FontSize   = 13,
        Foreground = Br("ContentTextBrush"),
        Margin     = new Thickness(0, 4, 0, 4)
    };

    private static Button MakeSmallBtn(string label, Brush fg) => new()
    {
        Content         = label,
        Background      = Brushes.Transparent,
        Foreground      = fg,
        FontSize        = 12,
        FontFamily      = new FontFamily("Segoe UI"),
        Padding         = new Thickness(10, 5, 10, 5),
        Cursor          = Cursors.Hand,
        BorderBrush     = Brushes.Transparent,
        BorderThickness = new Thickness(0)
    };

    private Button MakeActionBtn(string label, Brush bg, Brush fg) => new()
    {
        Content         = label,
        Background      = bg,
        Foreground      = fg,
        FontSize        = 13,
        FontFamily      = new FontFamily("Segoe UI"),
        Padding         = new Thickness(16, 7, 16, 7),
        MinWidth        = 110,
        Cursor          = Cursors.Hand,
        BorderBrush     = Brushes.Transparent,
        BorderThickness = new Thickness(0)
    };
}
