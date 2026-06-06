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

    // Common emoji available in the icon / symbol pickers, grouped visually
    private static readonly string[] PickerEmoji =
    [
        "📁","📋","📝","📊","📈","💡","🎯","⚙️","🔧","🔬",
        "📖","🎨","🎵","🎭","🎬","🎮","✏️","🖋️","📷","🎙️",
        "💼","🏢","📱","💻","🖥️","🤝","📧","💰","📦","🏗️",
        "🧪","🔭","🧬","🌍","🌐","🌌","⚗️","🧲","🔋","💊",
        "🚀","🏆","🎓","❤️","🌟","🌈","🏠","🗺️","⚡","🎁",
        "✅","☑️","🏁","🔖","📌","🗂️","🧩","🔑","💎","🎪",
    ];

    // ── Help content ──────────────────────────────────────────────────────

    private static readonly (string Title, string Body)[] FieldHelp =
    [
        // [0] Name
        ("Name",
         "The display name of this project type. Shown in menus, the \"New Project\" picker, and the project header.\n\n" +
         "Examples:\n  • Novel\n  • Software Project\n  • Theatre Production\n\nKeep it short — one to three words works best."),

        // [1] Icon
        ("Icon",
         "A single emoji that visually represents this project type. " +
         "Shown next to the name in all menus and picker cards.\n\n" +
         "Examples:\n  • 📖  for a Novel\n  • 💻  for a Software project\n  • 🎭  for Theatre\n\n" +
         "Click the icon button to open the picker, or type any emoji directly."),

        // [2] Description
        ("Description",
         "A one-to-three sentence description of what this project type is for. " +
         "Shown on the type card when the user picks a type for a new project.\n\n" +
         "Examples:\n  • \"A work of long-form fiction. Organise your story into chapters and scenes, " +
         "build a cast of characters and track your plot notes.\"\n\n" +
         "  • \"A software development project. Plan features, assign tasks and track deadlines.\""),

        // [3] Structure Levels (combined, replaces old Hierarchy + Icons)
        ("Structure Levels",
         "Defines the organisational levels inside this project — one entry per level, " +
         "outermost first.\n\n" +
         "Format:   LevelName(Emoji),LevelName(Emoji)\n\n" +
         "Examples:\n  • Chapter(📖),Scene(🎬)   — fiction with chapters containing scenes\n" +
         "  • Milestone(🏁),Task(✅)   — projects with milestones containing tasks\n" +
         "  • Act(🎭),Scene(🎬)        — theatre with acts containing scenes\n\n" +
         "The emoji is optional — write just   Chapter,Scene   if you prefer no icons.\n" +
         "Use the 🎨 button to insert an emoji at the cursor position.\n\n" +
         "Leave empty for a flat project with no nested structure."),

        // [4] World-Building Folders
        ("World-Building Folders",
         "Comma-separated names for sub-folders created inside PROJECTPLAN/ for world-building content. " +
         "The World button in the project header opens these folders.\n\n" +
         "Examples:\n  • Characters,Factions,Locations  (for a novel or game world)\n" +
         "  • Characters,Props,Sets            (for theatre or film)\n\n" +
         "Leave empty if this project type has no world-building content. " +
         "Also enable the \"World Building\" feature flag below."),

        // [5] Feature Flags
        ("Feature Flags",
         "Toggle which panels and features are active for this project type:\n\n" +
         "  📋 Roadmap — shows the roadmap/milestone panel in the project header.\n" +
         "  🌍 World Building — enables the world-building folders and the World button.\n" +
         "  👤 Assignees — allows tasks to be assigned to team members.\n" +
         "  📅 Deadlines — adds deadline fields to tasks and milestones.\n" +
         "  📝 Plot Notes — shows a dedicated plot notes section (fiction projects).\n" +
         "  🎭 Stage Directions — enables stage-direction formatting (theatre/screenplay)."),

        // [6] AI System Prompt Hint
        ("AI System Prompt Hint",
         "A short paragraph appended to every AI participant's system prompt whenever this project type is active. " +
         "Use it to give the AI context about the domain and how it should behave.\n\n" +
         "Example (Novel):\n  \"You are co-writing a novel. Think in terms of plot arcs, character development, " +
         "scene pacing and narrative voice. Flag plot holes or character inconsistencies when you spot them.\"\n\n" +
         "Example (Software):\n  \"You are part of a software development team. Reason carefully about " +
         "requirements and architecture before suggesting solutions. Identify edge cases and security concerns.\""),

        // [7] Seed Templates
        ("Seed Templates",
         "Files listed here are automatically created inside every new project of this type " +
         "the first time it is opened.\n\n" +
         "Seed files live in:   ProjectTypes/{TypeName}/\n" +
         "  matching the folder structure of the project — for example:\n" +
         "    PROJECTPLAN/story_outline.md\n" +
         "    INPUT/README.txt\n\n" +
         "The placeholder  {{ProjectName}}  in any seed file is replaced with the actual project name at creation time.\n\n" +
         "Files in   ProjectTypes/_common/   are seeded into every project type and are not listed here.\n\n" +
         "Seed files are never overwritten once created — safe to customise freely."),
    ];

    private readonly string _typesDir;
    private string?         _editingFilePath;

    // ── Controls ──────────────────────────────────────────────────────────

    private readonly ListBox    _typeList;
    private readonly TextBlock  _editorPlaceholder;
    private readonly StackPanel _editorPanel;
    private          ScrollViewer? _editorScroll;

    private readonly TextBox  _nameBox;
    private readonly Button   _iconBtn;
    private          string   _currentIcon = "📁";
    private readonly TextBox  _descBox;
    private readonly TextBox  _structureLevelsBox;   // combined "Name(Icon),Name(Icon)"
    private readonly TextBox  _worldFoldersBox;
    private readonly CheckBox _roadmapCheck;
    private readonly CheckBox _worldBuildingCheck;
    private readonly CheckBox _assigneesCheck;
    private readonly CheckBox _deadlinesCheck;
    private readonly CheckBox _plotNotesCheck;
    private readonly CheckBox _stageDirectionsCheck;
    private readonly TextBox  _hintBox;
    private readonly Button   _deleteBtn;
    private readonly StackPanel _seedFilesPanel;    // rebuilt when selection changes

    // ── Construction ──────────────────────────────────────────────────────

    public ProjectTypeEditorWindow(Window owner, string typesDir, string? themePath)
    {
        _typesDir = typesDir;

        if (themePath is not null)
        {
            try
            {
                var dict = OxsuitLoader.Load(themePath);
                if (dict is not null) Resources.MergedDictionaries.Add(dict);
            }
            catch { /* theme unavailable */ }
        }

        Owner                 = owner;
        Title                 = "Project Type Editor · ClaudetRelay";
        Width                 = 860;
        Height                = 720;
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
        UiZoomHelper.Apply(this, UiZoomHelper.FromSettings());

        // ── Left panel ────────────────────────────────────────────────────
        var leftPanel = new DockPanel { Background = Br("SidebarBgBrush") };
        Grid.SetColumn(leftPanel, 0);
        root.Children.Add(leftPanel);

        var leftTitle = new TextBlock
        {
            Text = "PROJECT TYPES", FontSize = 10, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI"), Foreground = Br("ContentDimBrush"),
            Margin = new Thickness(12, 14, 12, 6)
        };
        DockPanel.SetDock(leftTitle, Dock.Top);
        leftPanel.Children.Add(leftTitle);

        var leftBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 10) };
        DockPanel.SetDock(leftBtns, Dock.Bottom);
        leftPanel.Children.Add(leftBtns);

        var newBtn = MakeSmallBtn("＋ New", Br("SidebarTextBrush"));
        newBtn.Click += NewType_Click;
        newBtn.Margin = new Thickness(0, 0, 6, 0);
        leftBtns.Children.Add(newBtn);

        _deleteBtn           = MakeSmallBtn("Delete", Br("ContentDimBrush"));
        _deleteBtn.Click    += DeleteType_Click;
        _deleteBtn.IsEnabled = false;
        leftBtns.Children.Add(_deleteBtn);

        var sepLine = new Border
        {
            Width = 1, Background = Br("ControlBgBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Stretch
        };
        Grid.SetColumn(sepLine, 0);
        root.Children.Add(sepLine);

        _typeList = new ListBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"), FontSize = 13,
            Foreground = Br("SidebarTextBrush"), SelectionMode = SelectionMode.Single,
            Margin = new Thickness(6, 0, 6, 0),
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
            Text = "Select a project type from the list,\nor click  ＋ New  to create one.",
            FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
            Foreground = Br("ContentDimBrush"), TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
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
        var nameIconHeaderRow = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        nameIconHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameIconHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        nameIconHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        nameIconHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });

        var nameLbl     = MakeLabelText("NAME");
        var nameHelpBtn = MakeHelpBtn(0);
        var iconLbl     = MakeLabelText("ICON");
        Grid.SetColumn(nameLbl,     0); Grid.SetColumn(nameHelpBtn, 1); Grid.SetColumn(iconLbl, 3);
        nameIconHeaderRow.Children.Add(nameLbl);
        nameIconHeaderRow.Children.Add(nameHelpBtn);
        nameIconHeaderRow.Children.Add(iconLbl);
        _editorPanel.Children.Add(nameIconHeaderRow);

        _nameBox = MakeTextBox(14);
        _iconBtn = new Button
        {
            Content = "📁", FontSize = 18, FontFamily = new FontFamily("Segoe UI"),
            Background = Br("ControlBgBrush"), Foreground = Br("ContentTextBrush"),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            Height = 38, Width = 70, ToolTip = "Pick an icon"
        };
        _iconBtn.Click += IconBtn_Click;

        var nameIconInputRow = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        nameIconInputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameIconInputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        nameIconInputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        var nameInputBorder = WrapInput(_nameBox);
        Grid.SetColumn(nameInputBorder, 0);
        Grid.SetColumn(_iconBtn, 2);
        nameIconInputRow.Children.Add(nameInputBorder);
        nameIconInputRow.Children.Add(_iconBtn);
        _editorPanel.Children.Add(nameIconInputRow);

        // ── DESCRIPTION ───────────────────────────────────────────────────
        _descBox = MakeTextBox(13, multiLine: true, minHeight: 68);
        _editorPanel.Children.Add(MakeLabelRow("DESCRIPTION", 2));
        _editorPanel.Children.Add(WrapInput(_descBox, bottomMargin: 4));
        _editorPanel.Children.Add(MakeHint("Shown on the type card when the user creates a new project."));

        // ── STRUCTURE LEVELS (combined field) ────────────────────────────
        _structureLevelsBox = MakeTextBox(13, multiLine: true, minHeight: 60);

        // label row: "STRUCTURE LEVELS" + (?) + symbol picker button
        var slHeaderRow = new Grid { Margin = new Thickness(0, 14, 0, 5) };
        slHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        slHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        slHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var slLbl = MakeLabelText("STRUCTURE LEVELS");
        var slHelp = MakeHelpBtn(3);
        var symbolPickerBtn = new Button
        {
            Content = "🎨", FontSize = 14, Width = 28, Height = 22,
            Background = Br("ControlBgBrush"), Foreground = Br("ContentTextBrush"),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            Margin = new Thickness(6, 0, 0, 0), ToolTip = "Insert emoji at cursor position"
        };
        symbolPickerBtn.Click += (s, _) => OpenSymbolPicker((Button)s!);

        Grid.SetColumn(slLbl,           0);
        Grid.SetColumn(slHelp,          1);
        Grid.SetColumn(symbolPickerBtn, 2);
        slHeaderRow.Children.Add(slLbl);
        slHeaderRow.Children.Add(slHelp);
        slHeaderRow.Children.Add(symbolPickerBtn);
        _editorPanel.Children.Add(slHeaderRow);

        _editorPanel.Children.Add(WrapInput(_structureLevelsBox, bottomMargin: 4));
        _editorPanel.Children.Add(MakeHint("Format:  LevelName(Emoji),LevelName(Emoji)   e.g.  Chapter(📖),Scene(🎬)"));

        // ── WORLD-BUILDING FOLDERS ────────────────────────────────────────
        _worldFoldersBox = MakeTextBox(13);
        _editorPanel.Children.Add(MakeLabelRow("WORLD-BUILDING FOLDERS", 4, topMargin: 14));
        _editorPanel.Children.Add(WrapInput(_worldFoldersBox, bottomMargin: 4));
        _editorPanel.Children.Add(MakeHint("Comma-separated.  e.g.  Characters,Factions,Locations"));

        // ── FEATURE FLAGS ─────────────────────────────────────────────────
        _editorPanel.Children.Add(MakeLabelRow("FEATURE FLAGS", 5, topMargin: 14));
        _roadmapCheck         = MakeCheck("📋  Roadmap",          true);
        _worldBuildingCheck   = MakeCheck("🌍  World Building",   false);
        _assigneesCheck       = MakeCheck("👤  Assignees",        false);
        _deadlinesCheck       = MakeCheck("📅  Deadlines",        false);
        _plotNotesCheck       = MakeCheck("📝  Plot Notes",       false);
        _stageDirectionsCheck = MakeCheck("🎭  Stage Directions", false);
        var flagGrid = new UniformGrid { Columns = 2, Margin = new Thickness(4, 4, 0, 0) };
        flagGrid.Children.Add(_roadmapCheck);     flagGrid.Children.Add(_worldBuildingCheck);
        flagGrid.Children.Add(_assigneesCheck);   flagGrid.Children.Add(_deadlinesCheck);
        flagGrid.Children.Add(_plotNotesCheck);   flagGrid.Children.Add(_stageDirectionsCheck);
        _editorPanel.Children.Add(flagGrid);

        // ── AI SYSTEM PROMPT HINT ─────────────────────────────────────────
        _hintBox = MakeTextBox(13, multiLine: true, minHeight: 110);
        _editorPanel.Children.Add(MakeLabelRow("AI SYSTEM PROMPT HINT", 6, topMargin: 16));
        _editorPanel.Children.Add(WrapInput(_hintBox, bottomMargin: 4));
        _editorPanel.Children.Add(MakeHint("Appended to every AI participant's system prompt while this project type is active."));

        // ── SEED TEMPLATES ────────────────────────────────────────────────
        _seedFilesPanel = new StackPanel();
        _editorPanel.Children.Add(MakeLabelRow("SEED TEMPLATES", 7, topMargin: 20));
        _editorPanel.Children.Add(MakeHint("Files created automatically in every new project of this type.  {{ProjectName}} is substituted at creation time."));
        _editorPanel.Children.Add(_seedFilesPanel);

        // ── SAVE / CLOSE ──────────────────────────────────────────────────
        var saveBtn  = MakeActionBtn("Save Type", Br("AccentBgBrush"), Br("SidebarBgBrush"));
        saveBtn.Click += SaveType_Click;
        var closeBtn = MakeActionBtn("Close", Br("ControlBgBrush"), Br("ContentTextBrush"));
        closeBtn.Margin = new Thickness(10, 0, 0, 0);
        closeBtn.Click += (_, _) => Close();

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 4)
        };
        btnRow.Children.Add(saveBtn);
        btnRow.Children.Add(closeBtn);
        _editorPanel.Children.Add(btnRow);

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded  += (_, _) => ApplyCaptionColor();

        LoadTypes();
    }

    // ── Symbol picker (inserts emoji at cursor in _structureLevelsBox) ────

    private void OpenSymbolPicker(Button anchor)
    {
        var popup = new Popup
        {
            PlacementTarget    = anchor,
            Placement          = PlacementMode.Bottom,
            StaysOpen          = false,
            AllowsTransparency = true
        };

        var wrap = new WrapPanel { Width = 300, Margin = new Thickness(4) };
        foreach (var em in PickerEmoji)
        {
            var captured = em;
            var btn = new Button
            {
                Content = em, FontSize = 20, Width = 40, Height = 40,
                Background = Brushes.Transparent, Foreground = Br("SidebarTextBrush"),
                BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
                Margin = new Thickness(1), ToolTip = em
            };
            btn.Click += (_, _) =>
            {
                // Insert (emoji) at caret position in the structure levels box
                var tb  = _structureLevelsBox;
                var pos = tb.CaretIndex;
                var ins = $"({captured})";
                tb.Text = tb.Text.Insert(pos, ins);
                tb.CaretIndex = pos + ins.Length;
                tb.Focus();
                popup.IsOpen = false;
            };
            wrap.Children.Add(btn);
        }

        popup.Child = new Border
        {
            Background = Br("InputBgBrush"), BorderBrush = Br("AccentBgBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6), Child = wrap
        };
        popup.IsOpen = true;
    }

    // ── Icon picker (for the main type icon) ─────────────────────────────

    private void IconBtn_Click(object sender, RoutedEventArgs e)
    {
        var popup = new Popup
        {
            PlacementTarget    = _iconBtn,
            Placement          = PlacementMode.Bottom,
            StaysOpen          = false,
            AllowsTransparency = true
        };

        var wrap = new WrapPanel { Width = 300, Margin = new Thickness(4) };
        foreach (var em in PickerEmoji)
        {
            var captured = em;
            var btn = new Button
            {
                Content = em, FontSize = 20, Width = 40, Height = 40,
                Background = Brushes.Transparent, Foreground = Br("SidebarTextBrush"),
                BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
                Margin = new Thickness(1), ToolTip = em
            };
            btn.Click += (_, _) => { SetIcon(captured); popup.IsOpen = false; };
            wrap.Children.Add(btn);
        }

        popup.Child = new Border
        {
            Background = Br("InputBgBrush"), BorderBrush = Br("AccentBgBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6), Child = wrap
        };
        popup.IsOpen = true;
    }

    private void SetIcon(string icon) { _currentIcon = icon; _iconBtn.Content = icon; }

    // ── Help popup ────────────────────────────────────────────────────────

    private Button MakeHelpBtn(int helpIndex)
    {
        var btn = new Button
        {
            Content = "?", Width = 18, Height = 18, FontSize = 10,
            FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.Bold,
            Background = Br("ControlBgBrush"), Foreground = Br("ContentDimBrush"),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0), ToolTip = "Help"
        };
        btn.Click += (s, _) => ShowHelp((Button)s!, helpIndex);
        return btn;
    }

    private void ShowHelp(Button anchor, int helpIndex)
    {
        var (title, body) = FieldHelp[helpIndex];
        var popup = new Popup
        {
            PlacementTarget = anchor, Placement = PlacementMode.Left,
            StaysOpen = false, AllowsTransparency = true, Width = 380
        };
        popup.Child = new Border
        {
            Background = Br("SidebarBgBrush"), BorderBrush = Br("AccentBgBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 14), MaxWidth = 380,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold,
                        FontFamily = new FontFamily("Segoe UI"), Foreground = Br("SidebarTextBrush"),
                        Margin = new Thickness(0, 0, 0, 8)
                    },
                    new TextBlock
                    {
                        Text = body, FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
                        Foreground = Br("SidebarTextBrush"), TextWrapping = TextWrapping.Wrap,
                        LineHeight = 18
                    }
                }
            }
        };
        popup.IsOpen = true;
    }

    // ── Seed templates section ────────────────────────────────────────────

    private string GetSeedFolder(string? filePath, string typeName)
    {
        // Seed folder is derived purely from the type name — the XAML file doesn't need
        // to be saved first. This lets users add seed files to brand-new unsaved types.
        var safeName = new string(typeName
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)
            .ToArray()).Replace(' ', '_');
        return Path.Combine(_typesDir, safeName);
    }

    private void RebuildSeedFilesPanel(string seedFolder)
    {
        _seedFilesPanel.Children.Clear();

        // "Open seed folder" + "Add seed file" toolbar
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };

        var openFolderBtn = MakeSeedBtn("📁 Open Seed Folder");
        openFolderBtn.Click += (_, _) =>
        {
            Directory.CreateDirectory(seedFolder);
            try { System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(seedFolder) { UseShellExecute = true }); }
            catch { /* ignore */ }
        };
        toolbar.Children.Add(openFolderBtn);

        var addBtn = MakeSeedBtn("＋ Add Seed File");
        addBtn.Margin = new Thickness(8, 0, 0, 0);
        addBtn.Click += (_, _) => ShowAddSeedFileDialog(seedFolder);
        toolbar.Children.Add(addBtn);

        _seedFilesPanel.Children.Add(toolbar);

        // File list
        if (!Directory.Exists(seedFolder))
        {
            _seedFilesPanel.Children.Add(new TextBlock
            {
                Text = "(no seed files yet — click  ＋ Add Seed File  to create one)",
                FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
                Foreground = Br("ContentDimBrush")
            });
            return;
        }

        var files = Directory.GetFiles(seedFolder, "*.*", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            _seedFilesPanel.Children.Add(new TextBlock
            {
                Text = "(no seed files yet — click  ＋ Add Seed File  to create one)",
                FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
                Foreground = Br("ContentDimBrush")
            });
            return;
        }

        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(seedFolder, file).Replace('\\', '/');
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var lbl = new TextBlock
            {
                Text = rel, FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
                Foreground = Br("ContentTextBrush"), VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = file
            };
            Grid.SetColumn(lbl, 0);
            row.Children.Add(lbl);

            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);

            var capturedFile = file;
            var openBtn = MakeSeedBtn("Open");
            openBtn.Click += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(capturedFile) { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
            };
            actions.Children.Add(openBtn);

            var delBtn = MakeSeedBtn("🗑");
            delBtn.Foreground = Br("AccentHighlightBrush");
            delBtn.Margin = new Thickness(4, 0, 0, 0);
            delBtn.ToolTip = $"Delete seed file {rel}";
            delBtn.Click += (_, _) =>
            {
                if (MessageBox.Show($"Delete seed file\n{rel}?", "Confirm",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                try { File.Delete(capturedFile); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error); return; }
                RebuildSeedFilesPanel(seedFolder);
            };
            actions.Children.Add(delBtn);

            _seedFilesPanel.Children.Add(row);
        }
    }

    private void ShowAddSeedFileDialog(string seedFolder)
    {
        var bg  = Br("ContentBgBrush");
        var fg  = Br("ContentTextBrush");
        var dim = Br("ContentDimBrush");
        var inp = Br("ControlBgBrush");
        var brd = Br("ControlBorderBrush");

        var win = new Window
        {
            Title = "Add Seed File", Width = 520, Height = 480, MinWidth = 400, MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            Background = bg, ShowInTaskbar = false, ResizeMode = ResizeMode.CanResize
        };

        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        win.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        void AddLabel(string text)
        {
            root.Children.Add(new TextBlock
            {
                Text = text, FontSize = 10, FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"), Foreground = dim,
                Margin = new Thickness(0, 10, 0, 4)
            });
        }

        TextBox MakeInput(bool multi = false) => new()
        {
            FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
            Foreground = fg, Background = inp, BorderBrush = brd, BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6, 8, 6),
            TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap,
            AcceptsReturn = multi, MinHeight = multi ? 180 : 34,
            VerticalScrollBarVisibility = multi ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled
        };

        AddLabel("RELATIVE PATH  (folder/filename — e.g. PROJECTPLAN/my_template.md)");
        var pathBox = MakeInput();
        pathBox.ToolTip = "Path relative to the project folder. Use forward slashes.  Subfolders are created automatically.";
        root.Children.Add(pathBox);

        AddLabel("CONTENT  (use {{ProjectName}} as a placeholder for the project name)");
        var contentBox = MakeInput(multi: true);
        root.Children.Add(contentBox);

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        root.Children.Add(btnRow);

        var cancelBtn = MakeActionBtn("Cancel", Br("ControlBgBrush"), fg);
        cancelBtn.Click += (_, _) => win.DialogResult = false;
        btnRow.Children.Add(cancelBtn);

        var createBtn = MakeActionBtn("Create File", Br("AccentBgBrush"), Br("SidebarBgBrush"));
        createBtn.Margin = new Thickness(8, 0, 0, 0);
        createBtn.Click += (_, _) =>
        {
            var rel = pathBox.Text.Trim().Replace('/', Path.DirectorySeparatorChar)
                                         .Replace('\\', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(rel))
            {
                MessageBox.Show("Please enter a relative path.", "Missing Path",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var full = Path.GetFullPath(Path.Combine(seedFolder, rel));
            if (!full.StartsWith(Path.GetFullPath(seedFolder), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Path must stay inside the seed folder.", "Invalid Path",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, contentBox.Text, new UTF8Encoding(false));
                win.DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not create file:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        btnRow.Children.Add(createBtn);

        if (win.ShowDialog() == true)
            RebuildSeedFilesPanel(seedFolder);
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
        PopulateForm(new ProjectTypeDefinition { Name = "New Type", Icon = "📁", HasRoadmap = true }, null);
        _nameBox.Focus(); _nameBox.SelectAll();
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
        ShowPlaceholder(); _editingFilePath = null; _deleteBtn.IsEnabled = false;
        LoadTypes();
    }

    private void SaveType_Click(object sender, RoutedEventArgs e)
    {
        var name = _nameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Please enter a name for the project type.", "Missing Name",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            _nameBox.Focus(); return;
        }

        var icon     = string.IsNullOrWhiteSpace(_currentIcon) ? "📁" : _currentIcon;
        var safeName = DeriveFileName(name);
        var newPath  = Path.Combine(_typesDir, safeName + ".xaml");

        if (_editingFilePath is not null &&
            !string.Equals(_editingFilePath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(_editingFilePath); } catch { /* best effort */ }
        }

        // Parse combined "Chapter(📖),Scene(🎬)" back to two fields
        var (hierarchy, icons) = ParseStructureLevels(_structureLevelsBox.Text.Trim());

        var def = new ProjectTypeDefinition
        {
            Name               = name,
            Icon               = icon,
            Description        = _descBox.Text.Trim(),
            StructureHierarchy = hierarchy,
            StructureIcons     = icons,
            WorldFolders       = _worldFoldersBox.Text.Trim(),
            HasRoadmap         = _roadmapCheck.IsChecked         == true,
            HasWorldBuilding   = _worldBuildingCheck.IsChecked   == true,
            HasAssignees       = _assigneesCheck.IsChecked       == true,
            HasDeadlines       = _deadlinesCheck.IsChecked       == true,
            HasPlotNotes       = _plotNotesCheck.IsChecked       == true,
            HasStageDirections = _stageDirectionsCheck.IsChecked == true,
            SystemPromptHint   = _hintBox.Text.Trim()
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
        RebuildSeedFilesPanel(GetSeedFolder(newPath, name));
        LoadTypes();

        foreach (TypeEntry item in _typeList.Items)
            if (string.Equals(item.FilePath, newPath, StringComparison.OrdinalIgnoreCase))
            { _typeList.SelectedItem = item; break; }
    }

    // ── Form helpers ──────────────────────────────────────────────────────

    private void PopulateForm(ProjectTypeDefinition def, string? filePath)
    {
        _editingFilePath = filePath;
        _nameBox.Text = def.Name;
        SetIcon(def.Icon);
        _descBox.Text             = def.Description;
        _structureLevelsBox.Text  = BuildStructureLevels(def.StructureHierarchy, def.StructureIcons);
        _worldFoldersBox.Text     = def.WorldFolders.Replace("|", ",");  // normalise legacy separator
        _roadmapCheck        .IsChecked = def.HasRoadmap;
        _worldBuildingCheck  .IsChecked = def.HasWorldBuilding;
        _assigneesCheck      .IsChecked = def.HasAssignees;
        _deadlinesCheck      .IsChecked = def.HasDeadlines;
        _plotNotesCheck      .IsChecked = def.HasPlotNotes;
        _stageDirectionsCheck.IsChecked = def.HasStageDirections;
        _hintBox.Text = def.SystemPromptHint;

        RebuildSeedFilesPanel(GetSeedFolder(filePath, def.Name));

        if (_editorScroll is not null) _editorScroll.Visibility = Visibility.Visible;
        _editorPlaceholder.Visibility = Visibility.Collapsed;
    }

    private void ShowPlaceholder()
    {
        if (_editorScroll is not null) _editorScroll.Visibility = Visibility.Collapsed;
        _editorPlaceholder.Visibility = Visibility.Visible;
    }

    // ── Structure levels encoding / decoding ──────────────────────────────

    /// <summary>
    /// Converts "Chapter(📖),Scene(🎬)" into separate StructureHierarchy and StructureIcons strings.
    /// </summary>
    private static (string Hierarchy, string Icons) ParseStructureLevels(string combined)
    {
        if (string.IsNullOrWhiteSpace(combined)) return ("", "");

        var names = new List<string>();
        var icons = new List<string>();

        foreach (var entry in combined.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = entry.IndexOf('(');
            if (p >= 0 && entry.EndsWith(")"))
            {
                names.Add(entry[..p].Trim());
                icons.Add(entry[(p + 1)..^1].Trim());
            }
            else
            {
                names.Add(entry.Trim());
                icons.Add("");
            }
        }

        return (string.Join(",", names), string.Join(",", icons.Where(i => !string.IsNullOrEmpty(i))));
    }

    /// <summary>
    /// Converts separate StructureHierarchy / StructureIcons into the combined "Name(Icon),Name(Icon)" format.
    /// Handles both legacy '|' and current ',' separators.
    /// </summary>
    private static string BuildStructureLevels(string hierarchy, string icons)
    {
        if (string.IsNullOrWhiteSpace(hierarchy)) return "";

        char[] sep    = ['|', ','];
        var names     = hierarchy.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var iconList  = string.IsNullOrWhiteSpace(icons)
            ? []
            : icons.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var parts = new List<string>();
        for (int i = 0; i < names.Length; i++)
        {
            var icon = i < iconList.Length ? iconList[i] : "";
            parts.Add(string.IsNullOrEmpty(icon) ? names[i] : $"{names[i]}({icon})");
        }
        return string.Join(",", parts);
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

    private static string X(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

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
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor    = 36;

    private void ApplyCaptionColor()
    {
        try
        {
            var helper = new WindowInteropHelper(this);
            if (helper.Handle == IntPtr.Zero) return;
            if (Br("SidebarBgBrush") is SolidColorBrush bg)
            {
                var c = bg.Color; int cr = c.R | (c.G << 8) | (c.B << 16);
                DwmSetWindowAttribute(helper.Handle, DwmwaCaptionColor, ref cr, sizeof(int));
            }
            if (Br("SidebarTextBrush") is SolidColorBrush fg2)
            {
                var c = fg2.Color; int cr = c.R | (c.G << 8) | (c.B << 16);
                DwmSetWindowAttribute(helper.Handle, DwmwaTextColor, ref cr, sizeof(int));
            }
        }
        catch { /* DWM unavailable */ }
    }

    // ── UI factory helpers ────────────────────────────────────────────────

    private Brush Br(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private TextBlock MakeLabelText(string text) => new()
    {
        Text = text, FontSize = 10, FontWeight = FontWeights.Bold,
        FontFamily = new FontFamily("Segoe UI"), Foreground = Br("ContentDimBrush"),
        VerticalAlignment = VerticalAlignment.Bottom
    };

    private Grid MakeLabelRow(string label, int helpIndex, double topMargin = 8)
    {
        var g = new Grid { Margin = new Thickness(0, topMargin, 0, 5) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        var lbl = MakeLabelText(label);
        var btn = MakeHelpBtn(helpIndex);
        Grid.SetColumn(lbl, 0); Grid.SetColumn(btn, 1);
        g.Children.Add(lbl); g.Children.Add(btn);
        return g;
    }

    private TextBlock MakeHint(string text) => new()
    {
        Text = text, FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
        Foreground = Br("ContentDimBrush"), TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 3, 0, 0)
    };

    private TextBox MakeTextBox(double fontSize, bool multiLine = false, double minHeight = 36) => new()
    {
        FontSize = fontSize, FontFamily = new FontFamily("Segoe UI"),
        Background = Brushes.Transparent, Foreground = Br("ContentTextBrush"),
        CaretBrush = Br("ContentTextBrush"), SelectionBrush = Br("AccentBgBrush"),
        BorderThickness = new Thickness(0),
        Padding = new Thickness(10, multiLine ? 8 : 0, 10, multiLine ? 8 : 0),
        VerticalContentAlignment    = multiLine ? VerticalAlignment.Top : VerticalAlignment.Center,
        TextWrapping                = multiLine ? TextWrapping.Wrap : TextWrapping.NoWrap,
        AcceptsReturn               = multiLine,
        MinHeight                   = minHeight,
        MaxHeight                   = multiLine ? minHeight * 2.5 : 36,
        VerticalScrollBarVisibility = multiLine ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled
    };

    private Border WrapInput(UIElement child, double bottomMargin = 0)
    {
        return new Border
        {
            Background = Br("InputBgBrush"), BorderBrush = Br("ControlBgBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 0, bottomMargin), Child = child
        };
    }

    private CheckBox MakeCheck(string label, bool isChecked) => new()
    {
        Content = label, IsChecked = isChecked,
        FontFamily = new FontFamily("Segoe UI"), FontSize = 13,
        Foreground = Br("ContentTextBrush"), Margin = new Thickness(0, 4, 0, 4)
    };

    private static Button MakeSmallBtn(string label, Brush fg) => new()
    {
        Content = label, Background = Brushes.Transparent, Foreground = fg,
        FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
        Padding = new Thickness(10, 5, 10, 5), Cursor = Cursors.Hand,
        BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0)
    };

    private Button MakeActionBtn(string label, Brush bg, Brush fg) => new()
    {
        Content = label, Background = bg, Foreground = fg,
        FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
        Padding = new Thickness(16, 7, 16, 7), MinWidth = 110, Cursor = Cursors.Hand,
        BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0)
    };

    private Button MakeSeedBtn(string label) => new()
    {
        Content = label, FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
        Padding = new Thickness(8, 3, 8, 3), Cursor = Cursors.Hand,
        Background = Br("ControlBgBrush"), Foreground = Br("SidebarTextBrush"),
        BorderBrush = Br("ControlBorderBrush"), BorderThickness = new Thickness(1)
    };
}
