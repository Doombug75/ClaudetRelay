using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using ClaudetRelay.Properties;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClaudetRelay.Services;
using SysIO = System.IO;

namespace ClaudetRelay;

public partial class MainWindow
{
    // ── Project list ───────────────────────────────────────────────────────

    private void RefreshProjectList()
    {
        var settings = SettingsService.Load();
        var folder   = ProjectService.ResolveFolder(settings.ProjectsFolder);
        var projects = ProjectService.ListProjects(folder);

#if DEBUG
        System.Diagnostics.Debug.WriteLine($"🔍 [RefreshProjectList] Folder: {folder}");
        System.Diagnostics.Debug.WriteLine($"🔍 [RefreshProjectList] Projects found: {projects.Count}");
        if (System.IO.Directory.Exists(folder))
        {
            var dirs = System.IO.Directory.GetDirectories(folder);
            System.Diagnostics.Debug.WriteLine($"🔍 [RefreshProjectList] Subdirectories in folder: {dirs.Length}");
            foreach (var d in dirs)
                System.Diagnostics.Debug.WriteLine($"  - {System.IO.Path.GetFileName(d)}");
        }
#endif

        // Sort projects based on current sort mode
        if (_projectSortMode == "Alphabetical")
            projects = projects.OrderBy(p => p.Item2.ProjectName, StringComparer.OrdinalIgnoreCase).ToList();
        else // "LastOpened"
            projects = projects.OrderByDescending(p => p.Item2.LastOpened).ToList();

        ProjectListPanel.Children.Clear();
        _selectedProjectFolder = null;
        OpenProjectButton  .IsEnabled = false;
        DeleteProjectButton.IsEnabled = false;

        if (projects.Count == 0)
        {
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
                Text      = "No projects yet.",
                FontSize  = 15, FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap,
                Margin    = new Thickness(0, 0, 0, 8)
            };
            emptyText.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");

            var emptyHint = new TextBlock
            {
                Text      = "Click \"New Project\" below to create one.",
                FontSize  = 13, FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap
            };
            emptyHint.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");

            var emptyStack = new StackPanel();
            emptyStack.Children.Add(emptyText);
            emptyStack.Children.Add(emptyHint);
            emptyContainer.Child = emptyStack;

            ProjectListPanel.Children.Add(emptyContainer);
            return;
        }

        foreach (var (projFolder, meta) in projects)
        {
            var card = BuildProjectCard(projFolder, meta);
            ProjectListPanel.Children.Add(card);
        }
    }

    private Border BuildProjectCard(string projFolder, ProjectSettings meta)
    {
        // Resolve the project type so we can show its icon
        var ptd = ResolveProjectType(meta.ProjectTypeName);

        // ── Header: type icon + project name ─────────────────────────────────
        var typeIconTb = new TextBlock
        {
            Text       = ptd.Icon,
            FontSize   = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Margin     = new Thickness(0, 0, 8, 0)
        };
        typeIconTb.SetResourceReference(TextBlock.ForegroundProperty, "ControlHighBrush");

        var nameLabel = new TextBlock
        {
            Text       = meta.ProjectName,
            FontSize   = 13, FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap
        };
        nameLabel.SetResourceReference(TextBlock.ForegroundProperty, "ControlTextBrush");

        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal
        };
        titleRow.Children.Add(typeIconTb);
        var titleColumn = new StackPanel();
        titleColumn.Children.Add(nameLabel);
        var typeLabel = new TextBlock
        {
            Text       = ptd.Name,
            FontSize   = 10,
            FontFamily = new FontFamily("Segoe UI")
        };
        typeLabel.SetResourceReference(TextBlock.ForegroundProperty, "ControlDimBrush");
        titleColumn.Children.Add(typeLabel);
        titleRow.Children.Add(titleColumn);

        // ── Metadata: date ───────────────────────────────────────────────────
        var dateLabel = new TextBlock
        {
            Text       = $"Opened: {meta.LastOpened.ToLocalTime():MMM d, yyyy HH:mm}",
            FontSize   = 10, FontFamily = new FontFamily("Segoe UI"),
            Margin     = new Thickness(0, 8, 0, 0)
        };
        dateLabel.SetResourceReference(TextBlock.ForegroundProperty, "ControlDimBrush");

        // ── Participants ─────────────────────────────────────────────────────
        var liveActiveNames = meta.Roles
            .Where(r => r.IsActive && !string.IsNullOrWhiteSpace(r.DisplayName))
            .Select(r => r.DisplayName)
            .ToList();

        var participantsLabel = new TextBlock
        {
            FontSize     = 10, FontFamily = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 4, 0, 0)
        };
        participantsLabel.SetResourceReference(TextBlock.ForegroundProperty, "ControlDimBrush");
        if (liveActiveNames.Count > 0)
            participantsLabel.Text = $"Active: {string.Join(", ", liveActiveNames)}";

        // ── Action buttons (bottom row) ──────────────────────────────────────
        var btnLoad = new Button
        {
            Content             = "📂 Open",
            FontSize            = 11, FontFamily = new FontFamily("Segoe UI"),
            Padding             = new Thickness(8, 5, 8, 5),
            Margin              = new Thickness(0, 0, 4, 0),
            Style               = (Style)FindResource("ModernButton"),
            Background          = (Brush)FindResource("ControlBgBrush"),
            Foreground          = (Brush)FindResource("ControlTextBrush"),
            Cursor              = Cursors.Hand
        };
        var capturedFolderForLoad = projFolder;
        btnLoad.Click += (_, e) =>
        {
            e.Handled = true;
            OpenProject(capturedFolderForLoad);
        };

        var btnBackup = new Button
        {
            Content             = "💾",
            FontSize            = 12,
            Width               = 28,
            Height              = 28,
            Padding             = new Thickness(0),
            Margin              = new Thickness(4, 0, 4, 0),
            Style               = (Style)FindResource("ModernButton"),
            Background          = (Brush)FindResource("ControlBgBrush"),
            Foreground          = (Brush)FindResource("ControlHighBrush"),
            ToolTip             = "Create ZIP backup",
            Cursor              = Cursors.Hand
        };
        var capturedFolderForBackup = projFolder;
        var capturedNameForBackup   = meta.ProjectName;
        btnBackup.Click += async (_, e) =>
        {
            e.Handled = true;
            await CreateProjectBackupAsync(capturedFolderForBackup, capturedNameForBackup);
        };

        var btnSettings = new Button
        {
            Content             = "⚙",
            FontSize            = 12,
            Width               = 28,
            Height              = 28,
            Padding             = new Thickness(0),
            Margin              = new Thickness(4, 0, 0, 0),
            Style               = (Style)FindResource("ModernButton"),
            Background          = (Brush)FindResource("ControlBgBrush"),
            Foreground          = (Brush)FindResource("ControlHighBrush"),
            ToolTip             = Loc.S("ToolTip_ProjectSettings"),
            Cursor              = Cursors.Hand
        };
        btnSettings.Click += (_, e) =>
        {
            e.Handled = true;
            ShowProjectSettingsDialog(projFolder, meta.ProjectName);
        };

        var buttonsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 10, 0, 0)
        };
        buttonsRow.Children.Add(btnLoad);
        buttonsRow.Children.Add(btnBackup);
        buttonsRow.Children.Add(btnSettings);

        // ── Bridge-active badge (shown when this project is the current Bridge project) ──
        bool isBridgeProject = _bridgeProjectFolder is not null &&
            string.Equals(System.IO.Path.GetFullPath(projFolder),
                          System.IO.Path.GetFullPath(_bridgeProjectFolder),
                          StringComparison.OrdinalIgnoreCase);

        // ── Main card content (vertical stack) ────────────────────────────────
        var contentStack = new StackPanel();
        contentStack.Children.Add(titleRow);
        contentStack.Children.Add(dateLabel);
        if (!string.IsNullOrEmpty(participantsLabel.Text))
            contentStack.Children.Add(participantsLabel);

        if (isBridgeProject)
        {
            var bridgeBadge = new Border
            {
                CornerRadius        = new CornerRadius(4),
                Padding             = new Thickness(6, 2, 6, 2),
                Margin              = new Thickness(0, 6, 0, 0),
                BorderThickness     = new Thickness(2),
                Background          = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            bridgeBadge.SetResourceReference(Border.BorderBrushProperty, "AccentHighlightBrush");
            var bridgeText = new TextBlock
            {
                Text       = "🔌 Bridge active",
                FontSize   = 10,
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold
            };
            bridgeText.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
            bridgeBadge.Child = bridgeText;
            contentStack.Children.Add(bridgeBadge);
        }

        contentStack.Children.Add(buttonsRow);

        // ── Card border ──────────────────────────────────────────────────────
        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding      = new Thickness(10, 10, 10, 10),
            Margin       = new Thickness(0, 0, 12, 12),
            Cursor       = Cursors.Hand,
            Child        = contentStack,
            Tag          = projFolder,
            Width        = 220,
            MinHeight    = 130
        };
        card.SetResourceReference(Border.BackgroundProperty, "ControlBgBrush");
        if (isBridgeProject)
        {
            // Accent border to make the whole card stand out
            card.BorderThickness = new Thickness(2);
            card.SetResourceReference(Border.BorderBrushProperty, "AccentHighlightBrush");
        }
        else
        {
            card.BorderThickness = new Thickness(1);
            card.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        }

        card.MouseLeftButtonDown += (_, _) => SelectProjectCard(card, projFolder);
        card.MouseLeftButtonUp   += (_, e) =>
        {
            if (e.ClickCount >= 2) OpenProject(projFolder);
        };

        // ── Right-click context menu ───────────────────────────────────────
        var ctxMenu    = new ContextMenu();
        var exportHtml = new MenuItem { Header = Loc.S("MenuItem_ExportHtml") };
        var exportMd   = new MenuItem { Header = Loc.S("MenuItem_ExportMarkdown") };
        var browseItem = new MenuItem { Header = Loc.S("MenuItem_BrowseFiles") };

        var capturedFolder = projFolder;
        var capturedMeta   = meta;
        exportHtml.Click += (_, _) => ExportProject(capturedFolder, capturedMeta, "html");
        exportMd.Click   += (_, _) => ExportProject(capturedFolder, capturedMeta, "md");
        browseItem.Click += (_, _) =>
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(capturedFolder)
                    { UseShellExecute = true });

        ctxMenu.Items.Add(exportHtml);
        ctxMenu.Items.Add(exportMd);
        ctxMenu.Items.Add(new Separator());
        ctxMenu.Items.Add(browseItem);
        card.ContextMenu = ctxMenu;

        return card;
    }

    private void ExportProject(string projFolder, ProjectSettings meta, string format)
    {
        var entries = ProjectService.LoadChatLog(projFolder);
        if (entries.Count == 0)
        {
            MessageBox.Show(Loc.S("Err_NoChatHistory"),
                            "Nothing to export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var isHtml   = format == "html";
        var safeName = string.Join("_", meta.ProjectName
            .Split(SysIO.Path.GetInvalidFileNameChars())).Trim();

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title       = $"Export \"{meta.ProjectName}\"",
            FileName    = $"{safeName}-export",
            Filter      = isHtml
                ? "HTML file (*.html)|*.html"
                : "Markdown file (*.md)|*.md|Text file (*.txt)|*.txt",
            DefaultExt  = format
        };
        if (dlg.ShowDialog() != true) return;

        var fontSettings = SettingsService.Load();
        var content = isHtml
            ? ExportService.GenerateHtml(meta.ProjectName, entries,
                                         fontSettings.ChatFontFamily,
                                         fontSettings.ChatFontSize,
                                         fontSettings.ChatBubbleWidthPercent)
            : ExportService.GenerateMarkdown(meta.ProjectName, entries);

        SysIO.File.WriteAllText(dlg.FileName, content, System.Text.Encoding.UTF8);

        var result = MessageBox.Show(
            $"Exported {entries.Count} messages to\n{dlg.FileName}\n\nOpen the file now?",
            "Export complete", MessageBoxButton.YesNo, MessageBoxImage.Information);

        if (result == MessageBoxResult.Yes)
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
    }

    private void SelectProjectCard(Border clicked, string folder)
    {
        // Deselect all
        foreach (Border c in ProjectListPanel.Children.OfType<Border>())
            c.SetResourceReference(Border.BackgroundProperty, "ControlBgBrush");

        // Highlight selected
        clicked.SetResourceReference(Border.BackgroundProperty, "ControlHoverBrush");

        _selectedProjectFolder     = folder;
        OpenProjectButton  .IsEnabled = true;
        DeleteProjectButton.IsEnabled = true;
    }

    private void SortProjects_Click(object sender, RoutedEventArgs e)
    {
        if (sender == SortAlphabetButton)
            _projectSortMode = "Alphabetical";
        else if (sender == SortLastOpenedButton)
            _projectSortMode = "LastOpened";

        UpdateProjectSortButtons();
        RefreshProjectList();
    }

    private void RefreshProjectsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshProjectList();
        AddSystemMessage("🔄 Project list refreshed from disk.");
    }

    private void UpdateProjectSortButtons()
    {
        var isAlpha = _projectSortMode == "Alphabetical";
        var isLastOpened = _projectSortMode == "LastOpened";

        // Update A-Z button
        if (isAlpha)
        {
            SortAlphabetButton.SetResourceReference(Button.ForegroundProperty, "SidebarTextBrush");
            SortAlphabetButton.SetResourceReference(Button.BackgroundProperty, "ControlHoverBrush");
        }
        else
        {
            SortAlphabetButton.SetResourceReference(Button.ForegroundProperty, "SidebarDimBrush");
            SortAlphabetButton.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        }

        // Update Last Opened button
        if (isLastOpened)
        {
            SortLastOpenedButton.SetResourceReference(Button.ForegroundProperty, "SidebarTextBrush");
            SortLastOpenedButton.SetResourceReference(Button.BackgroundProperty, "ControlHoverBrush");
        }
        else
        {
            SortLastOpenedButton.SetResourceReference(Button.ForegroundProperty, "SidebarDimBrush");
            SortLastOpenedButton.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        }
    }

    // ── Project CRUD ───────────────────────────────────────────────────────

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsService.Load();
        var folder   = ProjectService.ResolveFolder(settings.ProjectsFolder);

        // Ask for name + description; re-prompt if the name is already taken
        string? name        = null;
        string  description = "";
        while (true)
        {
            var result = ShowNewProjectDialog(name ?? "My Project", description);
            if (result is null) return; // user cancelled
            (name, description) = result.Value;

            if (!ProjectService.ProjectNameExists(folder, name)) break;

            MessageBox.Show(
                $"A project named \"{name}\" already exists.\n\nPlease choose a different name.",
                "Name already taken",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Ask user to pick a project type
        var chosenType = ShowProjectTypePicker();
        if (chosenType is null) return; // user cancelled

        var projFolder = ProjectService.CreateProject(folder, name,
                                                       chosenType.Name,
                                                       chosenType.GetWorldFolderList());

        // Store the description entered at creation time
        var meta = ProjectService.LoadProject(projFolder)!;
        meta.Description = description;
        ProjectService.SaveProject(projFolder, meta);

        // Seed type-specific starter files
        SeedProjectTemplates(projFolder, chosenType, name);

        RefreshProjectList();

        // Switch to Projects tab so user sees the newly created project
        ProjectsTabButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    // ── Project template seeding ───────────────────────────────────────────

    /// <summary>
    /// Seeds starter files into a freshly created project folder from the
    /// ProjectTypes/_common/ and ProjectTypes/{TypeSafeName}/ folders.
    /// Never overwrites an existing file. Supports {{ProjectName}} substitution.
    /// A third-party project type just drops a seed folder next to its .xaml -
    /// no code changes required.
    /// </summary>
    private static void SeedProjectTemplates(string projFolder,
        ProjectTypeDefinition type, string projectName)
    {
        var typesBase = SysIO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProjectTypes");

        // Common seeds for every project type
        SeedFromFolder(projFolder, SysIO.Path.Combine(typesBase, "_common"), projectName);

        // Type-specific seeds: folder name = type name with spaces → underscores
        var safeName = new string(type.Name
            .Select(c => SysIO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)
            .ToArray()).Replace(' ', '_');
        SeedFromFolder(projFolder, SysIO.Path.Combine(typesBase, safeName), projectName);
    }

    private static void SeedFromFolder(string projFolder, string seedFolder, string projectName)
    {
        if (!SysIO.Directory.Exists(seedFolder)) return;
        foreach (var file in SysIO.Directory.GetFiles(seedFolder, "*.*",
                                                       SysIO.SearchOption.AllDirectories))
        {
            var relative = SysIO.Path.GetRelativePath(seedFolder, file);
            var content  = SysIO.File.ReadAllText(file, System.Text.Encoding.UTF8)
                               .Replace("{{ProjectName}}", projectName);
            SeedFile(projFolder, relative, content);
        }
    }

    private static void SeedFile(string projFolder, string relativePath, string content)
    {
        var full = SysIO.Path.GetFullPath(SysIO.Path.Combine(projFolder, relativePath));
        if (SysIO.File.Exists(full)) return;   // never overwrite
        SysIO.Directory.CreateDirectory(SysIO.Path.GetDirectoryName(full)!);
        SysIO.File.WriteAllText(full, content, System.Text.Encoding.UTF8);
    }

    /// <summary>
    /// Shows a modal dialog listing all loaded project types.
    /// Returns the chosen type, or null if the user cancelled.
    /// </summary>
    private ProjectTypeDefinition? ShowProjectTypePicker()
    {
        var typesDir = SysIO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProjectTypes");

        while (true)   // loops back if the user opens the editor then wants to pick a type
        {

        // ── Cache brushes from MainWindow (theme-aware) ───────────────────
        // New Window instances don't inherit MainWindow's MergedDictionaries,
        // so SetResourceReference would resolve nothing → black window.
        // Resolve once here on 'this' and assign directly, same as all other dialogs.
        var bgBrush          = (Brush)FindResource("ContentBgBrush") ?? new SolidColorBrush(Color.FromRgb(30, 30, 30));
        var textBrush        = (Brush)FindResource("ContentTextBrush") ?? new SolidColorBrush(Color.FromRgb(220, 220, 220));
        var sidebarTextBrush = (Brush)FindResource("SidebarTextBrush") ?? new SolidColorBrush(Color.FromRgb(200, 200, 200));
        var subtextBrush     = (Brush)FindResource("ControlDimBrush") ?? new SolidColorBrush(Color.FromRgb(130, 130, 130));
        var inputBrush       = (Brush)FindResource("ControlBgBrush") ?? new SolidColorBrush(Color.FromRgb(50, 50, 50));
        var surfaceBrush     = (Brush)FindResource("ControlHoverBrush") ?? new SolidColorBrush(Color.FromRgb(70, 70, 70));
        var accentBrush      = (Brush)FindResource("AccentBgBrush") ?? new SolidColorBrush(Color.FromRgb(100, 150, 200));
        var accentTextBrush  = (Brush)FindResource("AccentTextBrush") ?? new SolidColorBrush(Color.FromRgb(255, 255, 255));
        var controlHighBrush = (Brush)FindResource("ControlHighBrush") ?? new SolidColorBrush(Color.FromRgb(100, 200, 255));
        var btnStyle         = (Style)FindResource("ModernButton") ?? new Style();

        ProjectTypeDefinition? result = null;

        // ── Dialog window (3x3 grid: 3 cols × 150px + margins + scrollbar) ──
        var dlg = new Window
        {
            Title                 = Loc.S("Dlg_ChooseProjectType"),
            Width                 = 530,
            Height                = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ResizeMode            = ResizeMode.NoResize,
            ShowInTaskbar         = false,
            Background            = bgBrush
        };
        ApplyThemeToDialog(dlg);

        // ── Header ────────────────────────────────────────────────────────
        var header = new TextBlock
        {
            Text         = Loc.S("Dlg_ChooseTypeQuestion"),
            FontSize     = 15,
            FontWeight   = FontWeights.SemiBold,
            FontFamily   = new FontFamily("Segoe UI"),
            Foreground   = textBrush,
            Margin       = new Thickness(20, 20, 20, 4)
        };

        var subtitle = new TextBlock
        {
            Text         = Loc.S("Dlg_ChooseTypeHint"),
            FontSize     = 12,
            FontFamily   = new FontFamily("Segoe UI"),
            Foreground   = subtextBrush,
            Margin       = new Thickness(20, 0, 20, 14),
            TextWrapping = TextWrapping.Wrap
        };

        // ── Type cards grid (3 columns) ──────────────────────────────────────
        var typeGrid = new Grid();
        typeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        typeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        typeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var typeScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = typeGrid,
            Margin = new Thickness(16, 0, 16, 0),
            Padding = new Thickness(0, 0, 4, 0)
        };

        Border? selectedCard = null;
        ProjectTypeDefinition? selectedType = null;

        Button? okBtn = null; // forward reference

        int cardIndex = 0;
        foreach (var ptd in _projectTypes)
        {
            var capturedPtd = ptd;

            var iconTb = new TextBlock
            {
                Text                = ptd.Icon,
                FontSize            = 24,
                Foreground          = controlHighBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin              = new Thickness(0, 0, 0, 4)
            };

            var nameTb = new TextBlock
            {
                Text                = ptd.Name,
                FontSize            = 12,
                FontWeight          = FontWeights.SemiBold,
                FontFamily          = new FontFamily("Segoe UI"),
                Foreground          = sidebarTextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping        = TextWrapping.Wrap,
                TextAlignment       = TextAlignment.Center
            };

            var descTb = new TextBlock
            {
                Text                = ptd.Description,
                FontSize            = 10,
                FontFamily          = new FontFamily("Segoe UI"),
                Foreground          = subtextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping        = TextWrapping.Wrap,
                TextAlignment       = TextAlignment.Center,
                Margin              = new Thickness(0, 4, 0, 0)
            };

            var cardContent = new StackPanel { Width = 130 };
            cardContent.Children.Add(iconTb);
            cardContent.Children.Add(nameTb);
            cardContent.Children.Add(descTb);

            var typeCard = new Border
            {
                CornerRadius    = new CornerRadius(10),
                Padding         = new Thickness(10, 12, 10, 12),
                Margin          = new Thickness(8, 8, 8, 8),
                Cursor          = Cursors.Hand,
                Child           = cardContent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                BorderThickness = new Thickness(2),
                BorderBrush     = Brushes.Transparent,
                Background      = inputBrush
            };

            typeCard.MouseEnter += (_, _) =>
            {
                if (typeCard != selectedCard)
                    typeCard.Background = surfaceBrush;
            };
            typeCard.MouseLeave += (_, _) =>
            {
                if (typeCard != selectedCard)
                    typeCard.Background = inputBrush;
            };
            typeCard.MouseLeftButtonDown += (_, _) =>
            {
                // Deselect previous
                if (selectedCard is not null)
                {
                    selectedCard.BorderBrush = Brushes.Transparent;
                    selectedCard.Background  = inputBrush;
                }
                // Select this
                selectedCard = typeCard;
                selectedType = capturedPtd;
                typeCard.Background  = surfaceBrush;
                typeCard.BorderBrush = accentBrush;
                if (okBtn is not null) okBtn.IsEnabled = true;
            };

            // Calculate grid position (3 columns)
            int row = cardIndex / 3;
            int col = cardIndex % 3;

            // Add row definition if needed
            while (typeGrid.RowDefinitions.Count <= row)
                typeGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Add card to grid
            Grid.SetRow(typeCard, row);
            Grid.SetColumn(typeCard, col);
            typeGrid.Children.Add(typeCard);

            cardIndex++;
        }

        // Pre-select General (first card in grid)
        var generalCard = typeGrid.Children.OfType<Border>().FirstOrDefault();
        if (generalCard is not null)
        {
            selectedCard = generalCard;
            selectedType = _projectTypes.FirstOrDefault();
            generalCard.Background  = surfaceBrush;
            generalCard.BorderBrush = accentBrush;
        }

        // ── Buttons ───────────────────────────────────────────────────────
        okBtn = new Button
        {
            Content    = Loc.S("Btn_CreateProject"),
            Width      = 130,
            Height     = 36,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 13,
            Margin     = new Thickness(0, 0, 8, 0),
            IsEnabled  = true,
            Style      = btnStyle,
            Background = accentBrush,
            Foreground = accentTextBrush
        };

        var cancelBtn = new Button
        {
            Content    = "Cancel",
            Width      = 80,
            Height     = 36,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 13,
            Style      = btnStyle,
            Background = inputBrush,
            Foreground = subtextBrush
        };

        bool openEditor = false;
        okBtn    .Click += (_, _) => { result = selectedType; dlg.DialogResult = true; };
        cancelBtn.Click += (_, _) => dlg.DialogResult = false;

        // "Manage Types…" - opens the editor, then loops back to the picker
        var manageBtn = new Button
        {
            Content    = Loc.S("Btn_ManageTypes"),
            Height     = 36,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 12,
            Style      = btnStyle,
            Background = inputBrush,
            Foreground = subtextBrush,
            Margin     = new Thickness(20, 0, 0, 0)
        };
        manageBtn.Click += (_, _) => { openEditor = true; dlg.DialogResult = false; };

        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(20, 14, 20, 20)
        };
        btnRow.Children.Add(okBtn);
        btnRow.Children.Add(cancelBtn);

        // Footer row: manage-types link left, ok/cancel right
        var footerRow = new Grid { Margin = new Thickness(0) };
        footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(manageBtn, 0);
        Grid.SetColumn(btnRow,    1);
        footerRow.Children.Add(manageBtn);
        footerRow.Children.Add(btnRow);

        // ── Layout ────────────────────────────────────────────────────────
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // header + subtitle
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // cards with scroll
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // footer

        var headerStack = new StackPanel();
        headerStack.Children.Add(header);
        headerStack.Children.Add(subtitle);
        Grid.SetRow(headerStack, 0);
        root.Children.Add(headerStack);

        Grid.SetRow(typeScroll, 1);
        root.Children.Add(typeScroll);

        Grid.SetRow(footerRow, 2);
        root.Children.Add(footerRow);

        dlg.Content = root;

        dlg.ShowDialog();

        if (!openEditor) return result;

        // User wants to manage types - open editor then loop back to picker
        var editor = new ProjectTypeEditorWindow(this, typesDir, _currentThemePath);
        editor.ShowDialog();
        LoadProjectTypes();

        } // end while(true)
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProjectFolder is not null)
            OpenProject(_selectedProjectFolder);
    }

    private void DeleteProject_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProjectFolder is null) return;

        var meta = ProjectService.LoadProject(_selectedProjectFolder);
        var name = meta?.ProjectName ?? SysIO.Path.GetFileName(_selectedProjectFolder);

        var result = MessageBox.Show(
            $"Delete project \"{name}\"?\n\nThis cannot be undone.",
            "Delete Project",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        if (_currentProjectFolder == _selectedProjectFolder)
            CloseCurrentProject();

        ProjectService.DeleteProject(_selectedProjectFolder);
        _selectedProjectFolder = null;
        RefreshProjectList();
    }

    private void OpenProject(string projFolder)
    {
        var loaded = ProjectService.LoadProject(projFolder);
        if (loaded is null) { MessageBox.Show(Loc.S("Err_CouldNotReadProject"), Loc.S("Dlg_BridgeError")); return; }

        // ── Guard: ask before switching away from an already-open project ────
        if (_currentProjectFolder is not null &&
            !string.Equals(_currentProjectFolder, projFolder, StringComparison.OrdinalIgnoreCase))
        {
            var currentName = _projectSettings?.ProjectName
                              ?? SysIO.Path.GetFileName(_currentProjectFolder);
            var newName     = loaded.ProjectName
                              ?? SysIO.Path.GetFileName(projFolder);
            var confirm = MessageBox.Show(
                $"\"{currentName}\" is currently open.\n\nClose it and open \"{newName}\" instead?",
                "Switch Project",
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK) return;
            CloseCurrentProject();
        }

        // ── Mismatch check BEFORE touching any UI or state ──────────────────
        if (loaded.ActiveParticipants is { Count: > 0 })
        {
            var mismatches = GetParticipantMismatches(
                loaded.ActiveParticipants,
                SettingsService.Load().Participants);

            if (mismatches.Count > 0)
            {
                ShowMismatchBlockDialog(projFolder, loaded.ActiveParticipants, mismatches);
                return;
            }
        }

        // ── Everything checks out - actually load the project ────────────────

        // Save participant state for the project we're leaving
        if (_currentProjectFolder is not null && _currentProjectFolder != projFolder)
            SaveProjectParticipants();

        // Update LastOpened and persist (single save)
        loaded.LastOpened = DateTime.UtcNow;
        ProjectService.SaveProject(projFolder, loaded);

        // Switch to Chat tab
        ActivateTab(Tab.Chat);

        // Clear current chat
        _streamCts?.Cancel();
        ChatPanel.Children.Clear();
        _sharedHistory.Clear();

        // Store project state - _currentProject and _projectSettings are the SAME object
        _currentProjectFolder = projFolder;
        _projectSettings      = loaded;
        _currentProject       = loaded;

        // ── Auto-populate roles if missing ────────────────────────────────────
        // New projects have empty Roles; without this, RefreshParticipantBadges
        // finds nothing and no coordinator is ever shown or enforced.
        EnsureProjectRolesPopulated(loaded, projFolder);
        _superRoles           = null;   // cleared; will be loaded lazily by GetSuperRoleInstruction
        _currentProjectType   = ResolveProjectType(loaded.ProjectTypeName);
        _currentRoadmap       = RoadmapService.Load(projFolder);
        _projectLanguage      = loaded.Language;
        _maxDialogDepth       = Math.Max(1, loaded.MaxDialogDepth);
        _sessionStartTime     = DateTime.Now;
        _workSessionFired     = false;

        // Guarantee all expected project subfolders exist (idempotent - no-op if present).
        // This repairs projects created before PROJECTSETTINGS was introduced, and ensures
        // manually-deleted folders are restored before any read/write operations run.
        EnsureProjectFolders(projFolder);

        // Restore this project's saved participants
        if (loaded.ActiveParticipants is { Count: > 0 })
            ReInitializeParticipantsFrom(loaded.ActiveParticipants);

        // Always snapshot current live participants into ActiveParticipants before
        // any coordinator automation fires.  For existing projects this is a no-op;
        // for brand-new projects it ensures ResolveRoleAtGroupIndex can match roles.
        SaveProjectParticipants();

        // Reflect CO / R roles on all sidebar cards for this project
        RefreshParticipantBadges();

        // Hide the top tab bar — project mode is self-contained.
        // The only way out is ✕ Close, which restores the tab bar.
        MainTabBar.Visibility = Visibility.Collapsed;

        // Update header
        ChatHeaderTitle.Text              = loaded.ProjectName;
        ProjectSettingsButton.Visibility  = Visibility.Visible;
        CloseProjectButton   .Visibility  = Visibility.Visible;
        BackupButton         .Visibility  = Visibility.Visible;
        FilesButton          .Visibility  = Visibility.Visible;
        ChatViewButton       .Visibility  = Visibility.Visible;
        ChatViewButton.FontWeight         = FontWeights.SemiBold;  // chat is active by default
        RoadmapButton        .Visibility  = _currentProjectType.HasRoadmap
                                            ? Visibility.Visible : Visibility.Collapsed;
        WorldButton          .Visibility  = _currentProjectType.HasWorldBuilding
                                            ? Visibility.Visible : Visibility.Collapsed;

        // Load chat history
        var log = ProjectService.LoadChatLog(projFolder);
        foreach (var entry in log)
            RenderChatLogEntry(entry);

        if (log.Count == 0)
            AddSystemMessage($"Project \"{loaded.ProjectName}\" opened. Start chatting!");
        else
            AddSystemMessage($"Project \"{loaded.ProjectName}\" - {log.Count} messages loaded.");

        ChatScrollViewer.ScrollToBottom();

        // Any mode with a coordinator: refresh the capability profile if participants changed.
        // SuperPowers → RoadmapBuilding → WorkSession all chain together.
        // Dispatch RoadmapBuilding independently as a fallback for projects where SuperPowers
        // does not run (it chains into WorkSession itself).
        Dispatcher.InvokeAsync(async () => await CheckAndTriggerSuperPowersAsync(),
                               System.Windows.Threading.DispatcherPriority.Background);
        Dispatcher.InvokeAsync(async () => await CheckAndTriggerRoadmapBuildingAsync(),
                               System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Ensures <paramref name="ps"/> has at least one Role entry and exactly one Coordinator.
    /// Called on every project open so that brand-new projects (empty Roles) immediately
    /// show correct badges and coordinator routing without requiring a settings visit first.
    /// </summary>
    private void EnsureProjectRolesPopulated(ProjectSettings ps, string projFolder)
    {
        bool changed = false;

        // If no roles at all, seed from ActiveParticipants
        if (ps.Roles.Count == 0 && ps.ActiveParticipants is { Count: > 0 })
        {
            var enabled = ps.ActiveParticipants.Where(p => p.Enabled).ToList();
            for (int i = 0; i < enabled.Count; i++)
            {
                var p = enabled[i];
                ps.Roles.Add(new ProjectParticipantRole
                {
                    Provider      = p.Type,
                    Model         = p.Model,
                    DisplayName   = string.IsNullOrWhiteSpace(p.Name)
                                        ? FormatModelDisplayName(p.Model)
                                        : p.Name,
                    IsCoordinator = i == 0,   // first participant becomes coordinator
                    IsActive      = p.Enabled
                });
            }
            changed = true;
        }

        // If roles exist but no coordinator, auto-assign the first non-Ollama (or first)
        if (ps.Roles.Count > 0 && !ps.Roles.Any(r => r.IsCoordinator))
        {
            var autoCoord = ps.Roles.FirstOrDefault(r =>
                                !string.Equals(r.Provider, "Ollama",
                                    StringComparison.OrdinalIgnoreCase))
                            ?? ps.Roles[0];
            autoCoord.IsCoordinator = true;
            changed = true;
        }

        if (changed)
            ProjectService.SaveProject(projFolder, ps);
    }

    /// <summary>
    /// Ensures all expected project subfolders exist. Safe to call on every open -
    /// <see cref="Directory.CreateDirectory"/> is a no-op when the folder already exists.
    /// Repairs projects that were created before PROJECTSETTINGS was introduced,
    /// and restores any folder that was manually deleted.
    /// </summary>
    private static void EnsureProjectFolders(string projFolder)
    {
        SysIO.Directory.CreateDirectory(SysIO.Path.Combine(projFolder, "INPUT"));
        SysIO.Directory.CreateDirectory(SysIO.Path.Combine(projFolder, "PROJECTPLAN"));
        SysIO.Directory.CreateDirectory(SysIO.Path.Combine(projFolder, "OUTPUT"));
        SysIO.Directory.CreateDirectory(SysIO.Path.Combine(projFolder, "AI-Characters"));
        SysIO.Directory.CreateDirectory(SysIO.Path.Combine(projFolder, "PROJECTSETTINGS"));
    }

    private void CloseCurrentProject()
    {
        ShowRoadmapPanel(false);
        SaveProjectParticipants();   // persist current participants before clearing state
        _currentProjectFolder            = null;
        _currentProject                  = null;
        _currentProjectType              = null;
        _currentRoadmap                  = null;
        _projectSettings                 = null;
        _superRoles                      = null;
        _projectLanguage                 = "";
        _maxDialogDepth                  = 1;
        _sessionStartTime                = null;
        _workSessionFired                = false;
        // Restore top tab bar — back to general (no-project) mode.
        MainTabBar.Visibility = Visibility.Visible;

        ChatHeaderTitle.Text             = "Chat";
        ProjectSettingsButton.Visibility = Visibility.Collapsed;
        CloseProjectButton   .Visibility = Visibility.Collapsed;
        BackupButton         .Visibility = Visibility.Collapsed;
        ChatViewButton       .Visibility = Visibility.Collapsed;
        FilesButton          .Visibility = Visibility.Collapsed;
        FilesContent         .Visibility = Visibility.Collapsed;
        RoadmapButton        .Visibility = Visibility.Collapsed;
        WorldButton          .Visibility = Visibility.Collapsed;
        WorldContent         .Visibility = Visibility.Collapsed;
        ChatOnlyButtonsPanel .Visibility = Visibility.Visible;

        // Clear CO/R badges - no project is active
        RefreshParticipantBadges();

        // Restore the globally configured participants (enabled only - skip empty disabled slots)
        var globalSettings = SettingsService.Load();
        ReInitializeParticipantsFrom(globalSettings.Participants.Where(p => p.Enabled).ToList());

        // If we had switched to a project's Bridge agent roster, restore the global one now.
        RestoreGlobalBridgeAgentsIfNeeded();
    }

    // ── Project participant mismatch detection ─────────────────────────────

    /// <summary>
    /// Compares <paramref name="projectParticipants"/> (saved with the project) against
    /// <paramref name="globalParticipants"/>.  Returns every slot whose provider+model is no
    /// longer present in the global config, together with what the global config has at that slot.
    /// Disabled project participants are skipped - they are intentionally off.
    /// </summary>
    private static List<ParticipantMismatch> GetParticipantMismatches(
        List<ParticipantConfig> projectParticipants,
        List<ParticipantConfig> globalParticipants)
    {
        var mismatches = new List<ParticipantMismatch>();

        for (int i = 0; i < projectParticipants.Count; i++)
        {
            var proj = projectParticipants[i];
            if (!proj.Enabled) continue;   // intentionally disabled - not a mismatch

            bool inGlobal = globalParticipants.Any(g =>
                g.Enabled &&
                (
                    // Primary match: same provider type + model (original logic)
                    (string.Equals(g.Type,  proj.Type,  StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(g.Model, proj.Model, StringComparison.OrdinalIgnoreCase))
                    ||
                    // Fallback: same display name + model, ignoring provider type.
                    // Lets the user change only the provider (e.g. Anthropic → OpenRouter)
                    // without the mismatch dialog firing for every project.
                    (!string.IsNullOrWhiteSpace(proj.Name) &&
                     !string.IsNullOrWhiteSpace(g.Name) &&
                     string.Equals(g.Name,  proj.Name,  StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(g.Model, proj.Model, StringComparison.OrdinalIgnoreCase))
                ));

            if (inGlobal) continue;

            // Build description of what the global config currently has at this index
            string             globalDesc;
            ParticipantConfig? globalReplacement = null;
            if (i < globalParticipants.Count)
            {
                var g     = globalParticipants[i];
                var gName = string.IsNullOrEmpty(g.Name) ? $"\"{g.Model}\"" : $"\"{g.Name}\"";
                globalDesc        = $"{gName}  ({g.Type} · {g.Model})";
                globalReplacement = g;
            }
            else
                globalDesc = "- not configured -";

            var projName = string.IsNullOrEmpty(proj.Name) ? proj.Model : proj.Name;
            mismatches.Add(new ParticipantMismatch(
                i + 1, projName, proj.Type, proj.Model, globalDesc, globalReplacement));
        }

        return mismatches;
    }

    /// <summary>
    /// Blocks project loading and presents the user with three options:
    /// go to Participant Settings, open the Project Participant Fix dialog, or cancel.
    /// </summary>
    private void ShowMismatchBlockDialog(string projFolder,
                                         List<ParticipantConfig>   activePs,
                                         List<ParticipantMismatch> mismatches)
    {
        var win = new Window
        {
            Title                 = Loc.S("Dlg_ParticipantMismatch"),
            Width                 = 520,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ResizeMode            = ResizeMode.NoResize,
            ShowInTaskbar         = false
        };
        ApplyThemeToDialog(win);
        win.SetResourceReference(Window.BackgroundProperty, "ContentBgBrush");

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        // ── Header ────────────────────────────────────────────────────────
        var headerText = mismatches.Count == 1
            ? "1 project participant is no longer in your configuration:"
            : $"{mismatches.Count} project participants no longer match your configuration:";
        var header = new TextBlock
        {
            Text         = "⚠  " + headerText,
            FontSize     = 13,
            FontWeight   = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 16)
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        panel.Children.Add(header);

        // ── Mismatch rows ─────────────────────────────────────────────────
        var warnBrush = new SolidColorBrush(Color.FromRgb(255, 185, 0));
        foreach (var m in mismatches)
        {
            var projLine = new TextBlock
            {
                Text         = $"Slot {m.Slot}:  \"{m.ProjectName}\"  ({m.ProjectType} · {m.ProjectModel})",
                FontSize     = 12,
                FontWeight   = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Foreground   = warnBrush
            };
            var globalLine = new TextBlock
            {
                Text         = $"  ↳  Slot {m.Slot} now:  {m.GlobalDesc}",
                FontSize     = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 2, 0, 10)
            };
            globalLine.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
            panel.Children.Add(projLine);
            panel.Children.Add(globalLine);
        }

        // ── Separator ─────────────────────────────────────────────────────
        var sep = new Rectangle { Height = 1, Margin = new Thickness(0, 4, 0, 16) };
        sep.SetResourceReference(Rectangle.FillProperty, "ControlBgBrush");
        panel.Children.Add(sep);

        // ── Footer note ───────────────────────────────────────────────────
        var note = new TextBlock
        {
            Text         = "The project will not be loaded until the participants are resolved.\n" +
                           "Adjust your participant configuration - or fix the saved project participants.",
            FontSize     = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 20)
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        panel.Children.Add(note);

        // ── Buttons ───────────────────────────────────────────────────────
        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var btnStyle = FindResource("ModernButton") as Style;

        var participantSettingsBtn = new Button
        {
            Content = Loc.S("Btn_ParticipantSettings"),
            Height  = 32,
            Padding = new Thickness(14, 0, 14, 0),
            Margin  = new Thickness(0, 0, 8, 0),
            Style   = btnStyle
        };
        participantSettingsBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        participantSettingsBtn.SetResourceReference(Button.ForegroundProperty, "ControlTextBrush");
        participantSettingsBtn.Click += (_, _) =>
        {
            win.Close();
            SettingsButton_Click(null!, null!);
        };

        var projectSettingsBtn = new Button
        {
            Content = Loc.S("Btn_ProjectSettings"),
            Height  = 32,
            Padding = new Thickness(14, 0, 14, 0),
            Margin  = new Thickness(0, 0, 8, 0),
            Style   = btnStyle
        };
        projectSettingsBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        projectSettingsBtn.SetResourceReference(Button.ForegroundProperty, "ControlTextBrush");
        projectSettingsBtn.Click += (_, _) =>
        {
            win.Close();
            ShowProjectParticipantFixDialog(projFolder, activePs, mismatches);
        };

        var cancelBtn = new Button
        {
            Content   = "Cancel",
            Height    = 32,
            Padding   = new Thickness(14, 0, 14, 0),
            IsDefault = true,
            Style     = btnStyle
        };
        cancelBtn.SetResourceReference(Button.BackgroundProperty, "AccentBgBrush");
        cancelBtn.SetResourceReference(Button.ForegroundProperty, "AccentTextBrush");
        cancelBtn.Click += (_, _) => win.Close();

        btnRow.Children.Add(participantSettingsBtn);
        btnRow.Children.Add(projectSettingsBtn);
        btnRow.Children.Add(cancelBtn);
        panel.Children.Add(btnRow);

        win.Content = new ScrollViewer
        {
            Content                       = panel,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        win.ShowDialog();
    }

    /// <summary>
    /// Shows a dialog where the user can fix mismatched project participants:
    /// adopt the current global config for that slot, deactivate, or remove the mismatched entry.
    /// On save, writes the corrected list back to the project file and then re-opens the project.
    /// </summary>
    private void ShowProjectParticipantFixDialog(string projFolder,
                                                  List<ParticipantConfig>   activePs,
                                                  List<ParticipantMismatch> mismatches)
    {
        // Work on a deep copy so we don't mutate the original before the user confirms
        var workingPs    = activePs.Select(p => new ParticipantConfig
        {
            Name      = p.Name,
            Type      = p.Type,
            Model     = p.Model,
            ServerUrl = p.ServerUrl,
            Enabled   = p.Enabled
        }).ToList();
        var removedSlots = new HashSet<int>();   // 0-based indices to exclude on save

        var win = new Window
        {
            Title                 = Loc.S("Dlg_FixParticipants"),
            Width                 = 600,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ResizeMode            = ResizeMode.NoResize,
            ShowInTaskbar         = false
        };
        ApplyThemeToDialog(win);
        win.SetResourceReference(Window.BackgroundProperty, "ContentBgBrush");

        var outerPanel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        // ── Header ────────────────────────────────────────────────────────
        var header = new TextBlock
        {
            Text         = "Saved project participants - please resolve all conflicts:",
            FontSize     = 13,
            FontWeight   = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 16)
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        outerPanel.Children.Add(header);

        var warnBrush  = new SolidColorBrush(Color.FromRgb(255, 185, 0));
        var btnStyle   = FindResource("ModernButton") as Style;

        // ── One row per saved participant ─────────────────────────────────
        for (int i = 0; i < workingPs.Count; i++)
        {
            var idx      = i;                    // captured in closures
            var p        = workingPs[i];
            var mismatch = mismatches.FirstOrDefault(m => m.Slot == i + 1);
            bool isMismatch = mismatch.Slot > 0; // Slot=0 means default(struct) → not found

            var rowBorder = new Border
            {
                CornerRadius    = new CornerRadius(8),
                Padding         = new Thickness(12, 8, 12, 8),
                Margin          = new Thickness(0, 0, 0, 6),
                BorderThickness = isMismatch ? new Thickness(1.5) : new Thickness(0),
                BorderBrush     = isMismatch ? warnBrush : null
            };
            rowBorder.SetResourceReference(Border.BackgroundProperty, "ControlBgBrush");

            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var displayName = string.IsNullOrEmpty(p.Name) ? p.Model : p.Name;
            var nameText    = new TextBlock
            {
                FontSize     = 12,
                FontWeight   = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (isMismatch)
            {
                nameText.Text       = $"⚠  Slot {i + 1}:  {displayName}  ({p.Type} · {p.Model})";
                nameText.Foreground = warnBrush;
            }
            else
            {
                nameText.Text = $"✓  Slot {i + 1}:  {displayName}  ({p.Type} · {p.Model})";
                nameText.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
            }
            Grid.SetColumn(nameText, 0);
            rowGrid.Children.Add(nameText);

            if (isMismatch)
            {
                var actionRow = new StackPanel
                {
                    Orientation       = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = new Thickness(8, 0, 0, 0)
                };

                // 🔄 Apply - only when a global replacement exists at this slot
                if (mismatch.GlobalReplacement is not null)
                {
                    var rep       = mismatch.GlobalReplacement;
                    var adoptBtn  = new Button
                    {
                        Content = Loc.S("Btn_Apply"),
                        Height  = 26,
                        Padding = new Thickness(8, 0, 8, 0),
                        Margin  = new Thickness(0, 0, 4, 0),
                        Style   = btnStyle,
                        ToolTip = $"Replace with:  {mismatch.GlobalDesc}"
                    };
                    adoptBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
                    adoptBtn.SetResourceReference(Button.ForegroundProperty, "ControlTextBrush");
                    adoptBtn.Click += (_, _) =>
                    {
                        workingPs[idx].Name      = rep.Name;
                        workingPs[idx].Type      = rep.Type;
                        workingPs[idx].Model     = rep.Model;
                        workingPs[idx].ServerUrl = rep.ServerUrl;
                        workingPs[idx].Enabled   = true;
                        var updName = string.IsNullOrEmpty(rep.Name) ? rep.Model : rep.Name;
                        nameText.Text = $"✓  Slot {idx + 1}:  {updName}  ({rep.Type} · {rep.Model})";
                        nameText.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
                        rowBorder.BorderThickness = new Thickness(0);
                        actionRow.Visibility      = Visibility.Collapsed;
                    };
                    actionRow.Children.Add(adoptBtn);
                }

                // ⏸ Disable
                var disableBtn = new Button
                {
                    Content = Loc.S("Btn_Disable"),
                    Height  = 26,
                    Padding = new Thickness(8, 0, 8, 0),
                    Margin  = new Thickness(0, 0, 4, 0),
                    Style   = btnStyle,
                    ToolTip = "Disable this participant for this project"
                };
                disableBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
                disableBtn.SetResourceReference(Button.ForegroundProperty, "ControlTextBrush");
                disableBtn.Click += (_, _) =>
                {
                    workingPs[idx].Enabled    = false;
                    nameText.Text             = $"⏸  Slot {idx + 1}:  {displayName}  (disabled)";
                    nameText.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
                    rowBorder.BorderThickness = new Thickness(0);
                    actionRow.Visibility      = Visibility.Collapsed;
                };
                actionRow.Children.Add(disableBtn);

                // 🗑 Remove
                var removeBtn = new Button
                {
                    Content = Loc.S("Btn_RemoveParticipant"),
                    Height  = 26,
                    Padding = new Thickness(8, 0, 8, 0),
                    Style   = btnStyle,
                    ToolTip = "Remove this participant from the project"
                };
                removeBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
                removeBtn.SetResourceReference(Button.ForegroundProperty, "ControlTextBrush");
                removeBtn.Click += (_, _) =>
                {
                    removedSlots.Add(idx);
                    rowBorder.BorderThickness = new Thickness(0);
                    rowBorder.Opacity         = 0.35;
                    nameText.Text             = $"🗑  Slot {idx + 1}:  {displayName}  (removing)";
                    nameText.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
                    actionRow.Visibility      = Visibility.Collapsed;
                };
                actionRow.Children.Add(removeBtn);

                Grid.SetColumn(actionRow, 1);
                rowGrid.Children.Add(actionRow);
            }

            rowBorder.Child = rowGrid;
            outerPanel.Children.Add(rowBorder);
        }

        // ── Separator ─────────────────────────────────────────────────────
        var sep = new Rectangle { Height = 1, Margin = new Thickness(0, 8, 0, 16) };
        sep.SetResourceReference(Rectangle.FillProperty, "ControlBgBrush");
        outerPanel.Children.Add(sep);

        // ── Bottom buttons ─────────────────────────────────────────────────
        var bottomRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var btnStyle2 = FindResource("ModernButton") as Style;
        bool saved    = false;

        var cancelBtn = new Button
        {
            Content = "Cancel",
            Height  = 32,
            Padding = new Thickness(14, 0, 14, 0),
            Margin  = new Thickness(0, 0, 8, 0),
            Style   = btnStyle2
        };
        cancelBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        cancelBtn.SetResourceReference(Button.ForegroundProperty, "ControlTextBrush");
        cancelBtn.Click += (_, _) => win.Close();

        var saveBtn = new Button
        {
            Content   = Loc.S("Btn_SaveAndLoad"),
            Height    = 32,
            Padding   = new Thickness(14, 0, 14, 0),
            IsDefault = true,
            Style     = btnStyle2
        };
        saveBtn.SetResourceReference(Button.BackgroundProperty, "AccentBgBrush");
        saveBtn.SetResourceReference(Button.ForegroundProperty, "AccentTextBrush");
        saveBtn.Click += (_, _) => { saved = true; win.Close(); };

        bottomRow.Children.Add(cancelBtn);
        bottomRow.Children.Add(saveBtn);
        outerPanel.Children.Add(bottomRow);

        win.Content = new ScrollViewer
        {
            Content                       = outerPanel,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        win.ShowDialog();

        if (!saved) return;

        // ── Persist fixes and re-open ──────────────────────────────────────
        var finalPs = workingPs
            .Where((_, i) => !removedSlots.Contains(i))
            .ToList();

        var ps = ProjectService.LoadProject(projFolder) ?? new ProjectSettings();
        ps.ActiveParticipants = finalPs;
        try { ProjectService.SaveProject(projFolder, ps); }
        catch (Exception ex)
        {
            MessageBox.Show($"Error saving project participants:\n{ex.Message}",
                            "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Re-open - mismatch check will either pass or show the dialog again
        OpenProject(projFolder);
    }

    private void RoadmapButton_Click(object sender, RoutedEventArgs e)
        => ShowRoadmapPanel(RoadmapContent.Visibility != Visibility.Visible);

    // ── Roadmap panel show/hide ────────────────────────────────────────────

    private void ShowRoadmapPanel(bool show)
    {
        if (show && _currentRoadmap is not null)
        {
            // Collapse all other project panels first
            WorldContent     .Visibility = Visibility.Collapsed;
            WorldButton.FontWeight       = FontWeights.Normal;
            FilesContent     .Visibility = Visibility.Collapsed;
            FilesButton.FontWeight       = FontWeights.Normal;

            // Hide chat-only buttons and deactivate Chat sub-tab
            ChatOnlyButtonsPanel .Visibility = Visibility.Collapsed;
            ChatViewButton.FontWeight         = FontWeights.Normal;

            BuildRoadmapContent();
            RoadmapContent   .Visibility = Visibility.Visible;
            ChatScrollViewer .Visibility = Visibility.Collapsed;
            InputArea        .Visibility = Visibility.Collapsed;
            RoadmapButton.FontWeight     = FontWeights.SemiBold;
        }
        else
        {
            RoadmapContent   .Visibility = Visibility.Collapsed;
            ChatScrollViewer .Visibility = Visibility.Visible;
            InputArea        .Visibility = Visibility.Visible;
            RoadmapButton.FontWeight     = FontWeights.Normal;

            // Restore chat-only buttons and mark Chat sub-tab active
            ChatOnlyButtonsPanel .Visibility = Visibility.Visible;
            ChatViewButton.FontWeight         = _currentProjectFolder is not null
                                               ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    // ── Files panel ───────────────────────────────────────────────────────

    private void FilesButton_Click(object sender, RoutedEventArgs e)
        => ShowFilesPanel(FilesContent.Visibility != Visibility.Visible);

    private void ShowFilesPanel(bool show)
    {
        if (show && _currentProjectFolder is not null)
        {
            // Collapse all other project panels first
            WorldContent     .Visibility = Visibility.Collapsed;
            WorldButton.FontWeight       = FontWeights.Normal;
            RoadmapContent   .Visibility = Visibility.Collapsed;
            RoadmapButton.FontWeight     = FontWeights.Normal;

            // Hide chat-only buttons and deactivate Chat sub-tab
            ChatOnlyButtonsPanel .Visibility = Visibility.Collapsed;
            ChatViewButton.FontWeight         = FontWeights.Normal;

            BuildFilesContent();
            FilesContent     .Visibility = Visibility.Visible;
            ChatScrollViewer .Visibility = Visibility.Collapsed;
            InputArea        .Visibility = Visibility.Collapsed;
            FilesButton.FontWeight       = FontWeights.SemiBold;
        }
        else
        {
            FilesContent     .Visibility = Visibility.Collapsed;
            ChatScrollViewer .Visibility = Visibility.Visible;
            InputArea        .Visibility = Visibility.Visible;
            FilesButton.FontWeight       = FontWeights.Normal;

            // Restore chat-only buttons and mark Chat sub-tab active
            ChatOnlyButtonsPanel .Visibility = Visibility.Visible;
            ChatViewButton.FontWeight         = _currentProjectFolder is not null
                                               ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    /// <summary>Rebuilds the Files panel content from the current project folder.</summary>
    private void BuildFilesContent()
    {
        if (_currentProjectFolder is null) return;

        FilesContent.Children.Clear();
        FilesContent.RowDefinitions.Clear();
        FilesContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding                       = new Thickness(20, 14, 20, 14)
        };
        Grid.SetRow(scroll, 0);
        FilesContent.Children.Add(scroll);

        var root = new StackPanel();
        scroll.Content = root;

        // ── Top toolbar ────────────────────────────────────────────────────
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };

        var refreshBtn = MakeFilePanelButton("↻  Refresh", isPrimary: false);
        refreshBtn.Click += (_, _) => BuildFilesContent();
        refreshBtn.ToolTip = "Reload file listings from disk";
        toolbar.Children.Add(refreshBtn);

        var openFolderBtn = MakeFilePanelButton("📁  Open Project Folder", isPrimary: false);
        openFolderBtn.Margin = new Thickness(8, 0, 0, 0);
        openFolderBtn.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                      (_currentProjectFolder!) { UseShellExecute = true }); }
            catch { /* ignore */ }
        };
        openFolderBtn.ToolTip = "Open project folder in Windows Explorer";
        toolbar.Children.Add(openFolderBtn);

        root.Children.Add(toolbar);

        // ── Three sections ─────────────────────────────────────────────────
        root.Children.Add(BuildFilesSection(
            header:      "🔒  INPUT  -  user-managed, AI read-only",
            folder:      "INPUT",
            canDelete:   false,
            canPromote:  false,
            description: "Drop finished or reference files here. AI can read them but cannot write or delete them.",
            dimDesc:     true));

        root.Children.Add(BuildFilesSection(
            header:      "📤  OUTPUT  -  AI deliverables",
            folder:      "OUTPUT",
            canDelete:   true,
            canPromote:  true,
            description: "Files written by AI participants. Promote a finished file to INPUT to lock it from further AI edits.",
            dimDesc:     false));

        root.Children.Add(BuildFilesSection(
            header:      "📝  PROJECTPLAN  -  plans & notes",
            folder:      "PROJECTPLAN",
            canDelete:   true,
            canPromote:  false,
            description: "Plans, outlines, task lists, and notes written by AI or the user.",
            dimDesc:     false));
    }

    private FrameworkElement BuildFilesSection(
        string header, string folder, bool canDelete, bool canPromote, string description, bool dimDesc)
    {
        var projFolder = _currentProjectFolder!;
        var absFolder  = SysIO.Path.Combine(projFolder, folder);

        var section = new Border
        {
            Background      = (Brush)FindResource("ControlBgBrush"),
            BorderBrush     = (Brush)FindResource("ControlBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(8),
            Margin          = new Thickness(0, 0, 0, 14),
            Padding         = new Thickness(14, 12, 14, 12)
        };

        var inner = new StackPanel();
        section.Child = inner;

        // ── Section header row ────────────────────────────────────────────
        var headerRow = new Grid();
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerText = new TextBlock
        {
            Text       = header,
            FontSize   = 12,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"),
            Margin     = new Thickness(0, 0, 0, 4)
        };
        headerText.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        Grid.SetColumn(headerText, 0);
        headerRow.Children.Add(headerText);

        // "Open folder" icon button in header
        var openDirBtn = new Button
        {
            Content         = "📁",
            FontSize        = 13,
            Padding         = new Thickness(4, 2, 4, 2),
            Background      = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor          = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip         = $"Open {folder}/ in Explorer"
        };
        openDirBtn.SetResourceReference(Button.ForegroundProperty, "SidebarDimBrush");
        openDirBtn.Click += (_, _) =>
        {
            SysIO.Directory.CreateDirectory(absFolder);
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                      (absFolder) { UseShellExecute = true }); }
            catch { /* ignore */ }
        };
        Grid.SetColumn(openDirBtn, 1);
        headerRow.Children.Add(openDirBtn);
        inner.Children.Add(headerRow);

        // ── Description ───────────────────────────────────────────────────
        var desc = new TextBlock
        {
            Text        = description,
            FontSize    = 11,
            FontFamily  = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap,
            Margin      = new Thickness(0, 0, 0, 10)
        };
        desc.SetResourceReference(TextBlock.ForegroundProperty,
            dimDesc ? "SidebarDimBrush" : "ContentDimBrush");
        inner.Children.Add(desc);

        // ── Separator ─────────────────────────────────────────────────────
        var sep = new Rectangle { Height = 1, Margin = new Thickness(0, 0, 0, 10) };
        sep.SetResourceReference(Rectangle.FillProperty, "ControlBorderBrush");
        inner.Children.Add(sep);

        // ── File rows ─────────────────────────────────────────────────────
        var files = SysIO.Directory.Exists(absFolder)
            ? SysIO.Directory.GetFiles(absFolder)
                .Where(f => !SysIO.Path.GetFileName(f).StartsWith("_"))  // skip _versions etc.
                .OrderBy(f => SysIO.Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        if (files.Count == 0)
        {
            var empty = new TextBlock
            {
                Text       = "(no files)",
                FontSize   = 12,
                FontFamily = new FontFamily("Segoe UI"),
                Margin     = new Thickness(4, 0, 0, 0)
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            inner.Children.Add(empty);
        }
        else
        {
            foreach (var filePath in files)
            {
                var fileRow = BuildFileRow(filePath, projFolder, folder, canDelete, canPromote, inner);
                inner.Children.Add(fileRow);
            }
        }

        return section;
    }

    private FrameworkElement BuildFileRow(
        string filePath, string projFolder, string folderName,
        bool canDelete, bool canPromote, StackPanel parentPanel)
    {
        var fileName = SysIO.Path.GetFileName(filePath);
        var verDir   = SysIO.Path.Combine(SysIO.Path.GetDirectoryName(filePath)!, "_versions");
        var stem     = SysIO.Path.GetFileNameWithoutExtension(filePath);
        var ext      = SysIO.Path.GetExtension(filePath);

        // Wrapper holds the file row AND the collapsible versions panel
        var wrapper = new StackPanel { Margin = new Thickness(0, 1, 0, 1) };

        // Versions panel - built lazily when first opened, hidden initially
        var versionsPanel = new Border
        {
            Visibility      = Visibility.Collapsed,
            Margin          = new Thickness(10, 0, 0, 4),
            Padding         = new Thickness(10, 8, 10, 8),
            CornerRadius    = new CornerRadius(6),
            BorderThickness = new Thickness(1),
        };
        versionsPanel.SetResourceReference(Border.BackgroundProperty,   "SidebarBgBrush");
        versionsPanel.SetResourceReference(Border.BorderBrushProperty,  "ControlBorderBrush");

        // ── File row ───────────────────────────────────────────────────────
        var row = new Border
        {
            Padding         = new Thickness(6, 5, 6, 5),
            CornerRadius    = new CornerRadius(5),
            Background      = Brushes.Transparent
        };
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "ControlHoverBrush");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        wrapper.Children.Add(row);
        wrapper.Children.Add(versionsPanel);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Child = grid;

        // ── File name ──────────────────────────────────────────────────────
        var namePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var nameText = new TextBlock
        {
            Text              = fileName,
            FontSize          = 12,
            FontFamily        = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            MaxWidth          = 340,
            Cursor            = Cursors.Hand,
            ToolTip           = filePath
        };
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        nameText.MouseLeftButtonUp += (_, _) => OpenFileInEditor(filePath);
        namePanel.Children.Add(nameText);

        // ── Version history toggle badge ───────────────────────────────────
        // Built here so the button can reference versionsPanel; count is re-read on every toggle.
        var verBadgeBtn = new Button
        {
            Padding         = new Thickness(5, 1, 5, 1),
            Margin          = new Thickness(6, 0, 0, 0),
            BorderThickness = new Thickness(0),
            Cursor          = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility      = Visibility.Collapsed   // shown only when versions exist
        };
        verBadgeBtn.SetResourceReference(Button.BackgroundProperty, "ControlHoverBrush");

        void RefreshVerBadge()
        {
            var vFiles = SysIO.Directory.Exists(verDir)
                ? SysIO.Directory.GetFiles(verDir, $"{stem}_*{ext}") : [];
            if (vFiles.Length == 0) { verBadgeBtn.Visibility = Visibility.Collapsed; return; }
            verBadgeBtn.Visibility = Visibility.Visible;
            var isOpen = versionsPanel.Visibility == Visibility.Visible;
            var vText  = new TextBlock
            {
                Text       = $"{(isOpen ? "▾" : "▸")} {vFiles.Length} ver.",
                FontSize   = 10,
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold
            };
            vText.SetResourceReference(TextBlock.ForegroundProperty,
                isOpen ? "AccentHighlightBrush" : "SidebarDimBrush");
            verBadgeBtn.Content = vText;
            verBadgeBtn.ToolTip = isOpen
                ? "Hide version history"
                : $"{vFiles.Length} saved version(s) - click to view";
        }

        verBadgeBtn.Click += (_, _) =>
        {
            bool opening = versionsPanel.Visibility != Visibility.Visible;
            if (opening)
            {
                // Build the panel fresh each time so counts stay accurate
                versionsPanel.Child = BuildVersionsPanel(
                    filePath, projFolder, stem, ext, verDir,
                    onChanged: () => { RefreshVerBadge(); RebuildVersionsPanel(); });
                void RebuildVersionsPanel() =>
                    versionsPanel.Child = BuildVersionsPanel(
                        filePath, projFolder, stem, ext, verDir,
                        onChanged: () => { RefreshVerBadge(); RebuildVersionsPanel(); });
            }
            versionsPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
            RefreshVerBadge();
        };

        RefreshVerBadge();
        namePanel.Children.Add(verBadgeBtn);

        Grid.SetColumn(namePanel, 0);
        grid.Children.Add(namePanel);

        // ── Action buttons ─────────────────────────────────────────────────
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        var openBtn = MakeFilePanelButton("Open", isPrimary: false);
        openBtn.Margin  = new Thickness(4, 0, 0, 0);
        openBtn.ToolTip = $"Open {fileName} in default application";
        openBtn.Click  += (_, _) => OpenFileInEditor(filePath);
        actions.Children.Add(openBtn);

        if (canPromote)
        {
            var promoteBtn = MakeFilePanelButton("🔒 → INPUT", isPrimary: true);
            promoteBtn.Margin  = new Thickness(4, 0, 0, 0);
            promoteBtn.ToolTip = $"Copy {fileName} to INPUT and lock it from AI edits";
            promoteBtn.Click  += (_, _) =>
            {
                var destFolder = SysIO.Path.Combine(projFolder, "INPUT");
                SysIO.Directory.CreateDirectory(destFolder);
                var dest = SysIO.Path.Combine(destFolder, fileName);
                if (SysIO.File.Exists(dest)) BackupIfExists(dest, projFolder);
                SysIO.File.Copy(filePath, dest, overwrite: true);
                AddSystemMessage($"🔒  {fileName} promoted to INPUT/ - AI can no longer modify it.");
                BuildFilesContent();
            };
            actions.Children.Add(promoteBtn);
        }

        if (canDelete)
        {
            var delBtn = MakeFilePanelButton("🗑", isPrimary: false);
            delBtn.Margin  = new Thickness(4, 0, 0, 0);
            delBtn.FontSize = 13;
            delBtn.Padding  = new Thickness(8, 5, 8, 5);
            delBtn.ToolTip  = $"Delete {fileName}";
            delBtn.SetResourceReference(Button.ForegroundProperty, "AccentHighlightBrush");
            delBtn.Click   += (_, _) =>
            {
                var result = MessageBox.Show(
                    $"Delete {folderName}/{fileName}?\n\nThis cannot be undone.",
                    "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) return;
                try
                {
                    SysIO.File.Delete(filePath);
                    if (parentPanel.Children.Contains(wrapper))
                        parentPanel.Children.Remove(wrapper);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not delete file:\n{ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            actions.Children.Add(delBtn);
        }

        return wrapper;
    }

    /// <summary>
    /// Builds the inline version history panel for one file.
    /// <paramref name="onChanged"/> is called after any restore or delete so the badge
    /// and panel content can be refreshed.
    /// </summary>
    private StackPanel BuildVersionsPanel(
        string filePath, string projFolder,
        string stem, string ext, string verDir,
        Action onChanged)
    {
        var panel = new StackPanel();

        var vFiles = SysIO.Directory.Exists(verDir)
            ? SysIO.Directory.GetFiles(verDir, $"{stem}_*{ext}")
                .OrderByDescending(f => SysIO.Path.GetFileName(f), StringComparer.Ordinal)
                .ToList()
            : [];

        if (vFiles.Count == 0)
        {
            var none = new TextBlock
            {
                Text = "No saved versions.",
                FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 4)
            };
            none.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            panel.Children.Add(none);
            return panel;
        }

        // ── Header row: title + Delete All ────────────────────────────────
        var headerRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "Version history  (newest first)",
            FontSize = 10, FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        Grid.SetColumn(title, 0);
        headerRow.Children.Add(title);

        var deleteAllBtn = MakeFilePanelButton("🧹 Delete All", isPrimary: false);
        deleteAllBtn.FontSize = 10;
        deleteAllBtn.Padding  = new Thickness(7, 3, 7, 3);
        deleteAllBtn.SetResourceReference(Button.ForegroundProperty, "AccentHighlightBrush");
        deleteAllBtn.ToolTip = "Delete all saved versions of this file (current file is unaffected)";
        deleteAllBtn.Click  += (_, _) =>
        {
            var r = MessageBox.Show(
                $"Delete all {vFiles.Count} saved version(s) of {SysIO.Path.GetFileName(filePath)}?\n\n" +
                "The current file is not affected.",
                "Delete All Versions", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
            foreach (var vf in vFiles)
            {
                try { SysIO.File.Delete(vf); } catch { /* ignore */ }
            }
            onChanged();
        };
        Grid.SetColumn(deleteAllBtn, 1);
        headerRow.Children.Add(deleteAllBtn);
        panel.Children.Add(headerRow);

        // ── One row per version ────────────────────────────────────────────
        foreach (var vPath in vFiles)
        {
            var vName  = SysIO.Path.GetFileName(vPath);
            var label  = FormatVersionLabel(vName, stem, ext);

            var vRow = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            vRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            vRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var vLabel = new TextBlock
            {
                Text              = label,
                FontSize          = 11,
                FontFamily        = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis
            };
            vLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
            Grid.SetColumn(vLabel, 0);
            vRow.Children.Add(vLabel);

            var vActions = new StackPanel { Orientation = Orientation.Horizontal };
            Grid.SetColumn(vActions, 1);
            vRow.Children.Add(vActions);

            // Open version
            var vOpen = MakeFilePanelButton("Open", isPrimary: false);
            vOpen.FontSize = 10; vOpen.Padding = new Thickness(7, 3, 7, 3);
            vOpen.ToolTip  = $"Open this version in default application";
            vOpen.Click   += (_, _) => OpenFileInEditor(vPath);
            vActions.Children.Add(vOpen);

            // Restore version
            var capturedVPath = vPath;
            var vRestore = MakeFilePanelButton("↺ Restore", isPrimary: true);
            vRestore.FontSize = 10; vRestore.Padding = new Thickness(7, 3, 7, 3);
            vRestore.Margin   = new Thickness(4, 0, 0, 0);
            vRestore.ToolTip  = "Restore this version as the current file (current is backed up first)";
            vRestore.Click   += (_, _) =>
            {
                // Backup the current file before restoring
                BackupIfExists(filePath, projFolder);
                SysIO.File.Copy(capturedVPath, filePath, overwrite: true);
                AddSystemMessage(
                    $"↺  Restored {SysIO.Path.GetFileName(filePath)} from version: {label}");
                onChanged();
            };
            vActions.Children.Add(vRestore);

            // Delete this version
            var vDel = MakeFilePanelButton("🗑", isPrimary: false);
            vDel.FontSize = 12; vDel.Padding = new Thickness(6, 3, 6, 3);
            vDel.Margin   = new Thickness(4, 0, 0, 0);
            vDel.ToolTip  = "Delete this version";
            vDel.SetResourceReference(Button.ForegroundProperty, "AccentHighlightBrush");
            vDel.Click   += (_, _) =>
            {
                try { SysIO.File.Delete(capturedVPath); }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not delete version:\n{ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                onChanged();
            };
            vActions.Children.Add(vDel);

            panel.Children.Add(vRow);
        }

        return panel;
    }

    /// <summary>
    /// Converts a version filename like <c>Chapter_02_20260530_143022.md</c> into a
    /// human-readable label like <c>2026-05-30  14:30:22</c>.
    /// Falls back to the raw filename if parsing fails.
    /// </summary>
    private static string FormatVersionLabel(string vName, string stem, string ext)
    {
        // Expected suffix after stem: _YYYYMMDD_HHmmss  (optionally _1, _2 … for collisions)
        var suffix = SysIO.Path.GetFileNameWithoutExtension(vName);
        if (suffix.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase))
            suffix = suffix[(stem.Length + 1)..];  // strip leading "stem_"

        // Try to parse first 15 chars as YYYYMMDD_HHmmss
        if (suffix.Length >= 15 &&
            DateTime.TryParseExact(suffix[..15], "yyyyMMdd_HHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dt))
        {
            return dt.ToString("yyyy-MM-dd  HH:mm:ss");
        }
        return vName;  // fallback: raw filename
    }

    private static void OpenFileInEditor(string filePath)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(filePath)
                { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open file:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private Button MakeFilePanelButton(string label, bool isPrimary)
    {
        var btn = new Button
        {
            Content         = label,
            FontSize        = 11,
            FontFamily      = new FontFamily("Segoe UI"),
            Padding         = new Thickness(9, 4, 9, 4),
            BorderThickness = new Thickness(1),
            Cursor          = Cursors.Hand,
        };
        btn.SetResourceReference(Button.BackgroundProperty,
            isPrimary ? "ControlHoverBrush" : "ControlBgBrush");
        btn.SetResourceReference(Button.ForegroundProperty,
            isPrimary ? "AccentHighlightBrush" : "SidebarTextBrush");
        btn.SetResourceReference(Button.BorderBrushProperty, "ControlBorderBrush");

        // Simple hover effect via triggers in code
        btn.MouseEnter += (_, _) => btn.Opacity = 0.80;
        btn.MouseLeave += (_, _) => btn.Opacity = 1.00;
        return btn;
    }

    // ── Roadmap UI builder ────────────────────────────────────────────────

    private void BuildRoadmapContent()
    {
        RoadmapContent.Children.Clear();
        if (_currentRoadmap is null) return;

        // ── Theme brushes (must come from MainWindow, not SetResourceReference) ──
        var bgBrush      = (Brush)FindResource("ContentBgBrush");
        var sidebarBrush = (Brush)FindResource("SidebarBgBrush");
        var textBrush    = (Brush)FindResource("ContentTextBrush");
        var subtextBrush = (Brush)FindResource("ContentDimBrush");
        var inputBrush   = (Brush)FindResource("ControlBgBrush");
        var surfaceBrush = (Brush)FindResource("ControlHoverBrush");
        var accentBrush  = (Brush)FindResource("AccentBgBrush");
        var claudeBrush  = (Brush)FindResource("PrimaryAccentBrush");
        var ollamaBrush  = (Brush)FindResource("SecondaryAccentBrush");
        var btnStyle     = (Style)FindResource("ModernButton");

        // ── Root DockPanel ────────────────────────────────────────────────
        var dock = new DockPanel { LastChildFill = true };

        // ── Toolbar ───────────────────────────────────────────────────────
        var toolbar = new Border
        {
            Background = sidebarBrush,
            Padding    = new Thickness(16, 10, 16, 10)
        };
        DockPanel.SetDock(toolbar, Dock.Top);

        var tbGrid = new Grid();
        tbGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tbGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tbTitle = new TextBlock
        {
            Text              = "📊 Roadmap",
            FontSize          = 15,
            FontWeight        = FontWeights.SemiBold,
            FontFamily        = new FontFamily("Segoe UI"),
            Foreground        = textBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(tbTitle, 0);

        var addMsBtn = new Button
        {
            Content    = "+ Milestone",
            Style      = btnStyle,
            Background = inputBrush,
            Foreground = claudeBrush,
            FontSize   = 12,
            Padding    = new Thickness(10, 5, 10, 5)
        };
        addMsBtn.Click += (_, _) =>
        {
            var r = ShowMilestoneDialog();
            if (r is null) return;
            _currentRoadmap!.Milestones.Add(new RoadmapMilestone
            {
                Title         = r.Value.title,
                Description   = r.Value.desc,
                DateNote      = r.Value.dateNote,
                ImageFileName = r.Value.imageFileName,
                CreatedBy     = "User"
            });
            SaveRoadmap();
            BuildRoadmapContent();
        };

        var exportBtn = new Button
        {
            Content    = "⬇ Export HTML5",
            Style      = btnStyle,
            Background = inputBrush,
            Foreground = subtextBrush,
            FontSize   = 12,
            Padding    = new Thickness(10, 5, 10, 5),
            Margin     = new Thickness(0, 0, 8, 0),
            ToolTip    = "Export roadmap as interactive HTML5 document"
        };
        exportBtn.Click += (_, _) => ExportRoadmapToHtml();

        var tbBtns = new StackPanel { Orientation = Orientation.Horizontal };
        tbBtns.Children.Add(exportBtn);
        tbBtns.Children.Add(addMsBtn);
        Grid.SetColumn(tbBtns, 1);

        tbGrid.Children.Add(tbTitle);
        tbGrid.Children.Add(tbBtns);
        toolbar.Child = tbGrid;

        var tbSep = new Rectangle { Height = 1, Fill = inputBrush };
        DockPanel.SetDock(tbSep, Dock.Top);

        dock.Children.Add(toolbar);
        dock.Children.Add(tbSep);

        // ── Empty state ───────────────────────────────────────────────────
        if (_currentRoadmap.Milestones.Count == 0)
        {
            var empty = new StackPanel
            {
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin              = new Thickness(40)
            };
            empty.Children.Add(new TextBlock
            {
                Text                = "📊",
                FontSize            = 48,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin              = new Thickness(0, 0, 0, 12)
            });
            empty.Children.Add(new TextBlock
            {
                Text                = "No roadmap items yet.\nClick  \"+ Milestone\"  to add the first milestone.",
                FontSize            = 14,
                FontFamily          = new FontFamily("Segoe UI"),
                Foreground          = subtextBrush,
                TextAlignment       = TextAlignment.Center,
                TextWrapping        = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            dock.Children.Add(empty);
            RoadmapContent.Children.Add(dock);
            return;
        }

        // ── Scrollable milestone list ─────────────────────────────────────
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding                       = new Thickness(20, 16, 20, 16)
        };

        var list = new StackPanel();

        foreach (var ms in _currentRoadmap.Milestones)
        {
            var capturedMs = ms;

            // ── Milestone header ──────────────────────────────────────────
            var msHeaderGrid = new Grid { Margin = new Thickness(0) };
            msHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            msHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            msHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            msHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var msIcon = new TextBlock
            {
                Text              = RoadmapService.StatusIcon(ms.Status),
                FontSize          = 16,
                Margin            = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var msTitle = new TextBlock
            {
                Text              = ms.Title,
                FontSize          = 14,
                FontWeight        = FontWeights.SemiBold,
                FontFamily        = new FontFamily("Segoe UI"),
                Foreground        = textBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis
            };

            var msPct = new TextBlock
            {
                Text              = $"{ms.Progress}%",
                FontSize          = 12,
                FontFamily        = new FontFamily("Segoe UI"),
                Foreground        = subtextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth          = 38,
                TextAlignment     = TextAlignment.Right,
                Margin            = new Thickness(8, 0, 12, 0)
            };

            var msBtns = new StackPanel { Orientation = Orientation.Horizontal };

            var addItemBtn = new Button
            {
                Content    = "+ Item",
                Style      = btnStyle,
                Background = Brushes.Transparent,
                Foreground = claudeBrush,
                FontSize   = 11,
                Padding    = new Thickness(8, 4, 8, 4),
                Margin     = new Thickness(0, 0, 4, 0)
            };
            addItemBtn.Click += (_, _) =>
            {
                var r = ShowItemDialog();
                if (r is null) return;
                capturedMs.Items.Add(new RoadmapItem
                {
                    Title         = r.Value.title,
                    Description   = r.Value.desc,
                    Progress      = r.Value.progress,
                    DateNote      = r.Value.dateNote,
                    ImageFileName = r.Value.imageFileName,
                    Status        = r.Value.progress >= 100 ? ItemStatus.Done
                                  : r.Value.progress > 0    ? ItemStatus.InProgress
                                  : ItemStatus.Todo,
                    CreatedBy     = "User"
                });
                UpdateMilestoneStatus(capturedMs);
                SaveRoadmap();
                BuildRoadmapContent();
            };

            var editMsBtn = new Button
            {
                Content    = "✏",
                Style      = btnStyle,
                Background = Brushes.Transparent,
                Foreground = subtextBrush,
                FontSize   = 13,
                Padding    = new Thickness(6, 4, 6, 4),
                Margin     = new Thickness(0, 0, 4, 0),
                ToolTip    = "Edit milestone"
            };
            editMsBtn.Click += (_, _) =>
            {
                var r = ShowMilestoneDialog(capturedMs.Title, capturedMs.Description,
                                            capturedMs.DateNote, capturedMs.ImageFileName);
                if (r is null) return;
                capturedMs.Title       = r.Value.title;
                capturedMs.Description = r.Value.desc;
                capturedMs.DateNote    = r.Value.dateNote;
                // Handle image change
                if (r.Value.imageFileName != capturedMs.ImageFileName)
                {
                    if (!string.IsNullOrEmpty(capturedMs.ImageFileName))
                    {
                        var oldPath = RoadmapService.GetImagePath(_currentProjectFolder!, capturedMs.ImageFileName);
                        try { if (System.IO.File.Exists(oldPath)) System.IO.File.Delete(oldPath); } catch { }
                    }
                    capturedMs.ImageFileName = r.Value.imageFileName;
                }
                SaveRoadmap();
                BuildRoadmapContent();
            };

            var delMsBtn = new Button
            {
                Content    = "🗑",
                Style      = btnStyle,
                Background = Brushes.Transparent,
                Foreground = subtextBrush,
                FontSize   = 13,
                Padding    = new Thickness(6, 4, 6, 4),
                ToolTip    = "Delete milestone"
            };
            delMsBtn.Click += (_, _) =>
            {
                if (MessageBox.Show(
                    $"Delete milestone \"{capturedMs.Title}\" and all its items?",
                    "Delete Milestone", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                    != MessageBoxResult.Yes) return;
                _currentRoadmap!.Milestones.Remove(capturedMs);
                SaveRoadmap();
                BuildRoadmapContent();
            };

            msBtns.Children.Add(addItemBtn);
            msBtns.Children.Add(editMsBtn);
            msBtns.Children.Add(delMsBtn);

            Grid.SetColumn(msIcon,  0);
            Grid.SetColumn(msTitle, 1);
            Grid.SetColumn(msPct,   2);
            Grid.SetColumn(msBtns,  3);
            msHeaderGrid.Children.Add(msIcon);
            msHeaderGrid.Children.Add(msTitle);
            msHeaderGrid.Children.Add(msPct);
            msHeaderGrid.Children.Add(msBtns);

            var msHeaderBorder = new Border
            {
                Background = surfaceBrush,
                Padding    = new Thickness(14, 10, 10, 10),
                Child      = msHeaderGrid
            };

            // ── Milestone date note + image thumbnail ─────────────────────
            Border? msExtraBorderToAdd = null;
            if (!string.IsNullOrWhiteSpace(ms.DateNote) || !string.IsNullOrWhiteSpace(ms.ImageFileName))
            {
                var msExtraRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 4, 10, 4) };

                if (!string.IsNullOrWhiteSpace(ms.DateNote))
                {
                    var chip = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 8, 0) };
                    chip.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
                    chip.SetResourceReference(Border.BackgroundProperty,  "ControlBgBrush");
                    var chipText = new TextBlock { Text = "🗓 " + ms.DateNote, FontSize = 10, FontFamily = new FontFamily("Segoe UI") };
                    chipText.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
                    chip.Child = chipText;
                    msExtraRow.Children.Add(chip);
                }

                if (!string.IsNullOrWhiteSpace(ms.ImageFileName))
                {
                    var imgPath = RoadmapService.GetImagePath(_currentProjectFolder!, ms.ImageFileName);
                    if (System.IO.File.Exists(imgPath))
                    {
                        try
                        {
                            var bmp = new System.Windows.Media.Imaging.BitmapImage();
                            bmp.BeginInit(); bmp.UriSource = new Uri(imgPath); bmp.DecodePixelHeight = 48; bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bmp.EndInit(); bmp.Freeze();
                            var img = new System.Windows.Controls.Image { Source = bmp, Height = 36, Stretch = Stretch.Uniform, Cursor = Cursors.Hand, ToolTip = "Click to open image" };
                            img.MouseLeftButtonDown += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(imgPath) { UseShellExecute = true });
                            var imgBorder = new Border { Child = img, CornerRadius = new CornerRadius(3), ClipToBounds = true };
                            msExtraRow.Children.Add(imgBorder);
                        }
                        catch { }
                    }
                }

                msExtraBorderToAdd = new Border { Background = surfaceBrush, Child = msExtraRow };
            }

            // ── Milestone progress bar ────────────────────────────────────
            var msPbFill = ms.Status == ItemStatus.Done ? ollamaBrush : accentBrush;
            var msPb     = MakeProgressBar(ms.Progress, msPbFill, bgBrush, 4);

            // ── Item rows ─────────────────────────────────────────────────
            var itemsStack = new StackPanel { Background = inputBrush };
            itemsStack.Children.Add(msPb);

            if (ms.Items.Count == 0)
            {
                itemsStack.Children.Add(new TextBlock
                {
                    Text       = "No items yet - click  \"+ Item\"  to add the first task.",
                    FontSize   = 12,
                    FontFamily = new FontFamily("Segoe UI"),
                    Foreground = subtextBrush,
                    Margin     = new Thickness(14, 8, 14, 8)
                });
            }
            else
            {
                for (int idx = 0; idx < ms.Items.Count; idx++)
                {
                    var item = ms.Items[idx];
                    itemsStack.Children.Add(
                        BuildItemRow(capturedMs, item,
                            textBrush, subtextBrush, inputBrush,
                            bgBrush, accentBrush, ollamaBrush, btnStyle));

                    if (idx < ms.Items.Count - 1)
                        itemsStack.Children.Add(new Rectangle
                        {
                            Height = 1,
                            Fill   = bgBrush,
                            Margin = new Thickness(14, 0, 14, 0)
                        });
                }
            }

            // ── Combine header + optional extra row + items in a rounded card ──
            var inner = new StackPanel();
            inner.Children.Add(msHeaderBorder);
            if (msExtraBorderToAdd is not null) inner.Children.Add(msExtraBorderToAdd);
            inner.Children.Add(itemsStack);

            var card = new Border
            {
                CornerRadius    = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                BorderBrush     = surfaceBrush,
                Margin          = new Thickness(0, 0, 0, 14),
                Child           = inner,
                ClipToBounds    = true
            };

            list.Children.Add(card);
        }

        scroll.Content = list;
        dock.Children.Add(scroll);
        RoadmapContent.Children.Add(dock);
    }

    private static Grid MakeProgressBar(int progress, Brush fill, Brush bg, double height = 6)
    {
        progress = Math.Clamp(progress, 0, 100);
        var g = new Grid { Height = height };
        g.ColumnDefinitions.Add(new ColumnDefinition
            { Width = new GridLength(progress, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition
            { Width = new GridLength(100 - progress, GridUnitType.Star) });
        var fillRect = new Rectangle { Fill = fill };
        var bgRect   = new Rectangle { Fill = bg };
        Grid.SetColumn(fillRect, 0);
        Grid.SetColumn(bgRect,   1);
        g.Children.Add(fillRect);
        g.Children.Add(bgRect);
        return g;
    }

    private UIElement BuildItemRow(
        RoadmapMilestone ms,   RoadmapItem item,
        Brush textBrush,       Brush subtextBrush,
        Brush inputBrush,      Brush bgBrush,
        Brush accentBrush,     Brush ollamaBrush,
        Style btnStyle)
    {
        var capturedMs   = ms;
        var capturedItem = item;

        var rowGrid = new Grid();
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                     // icon
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                     // id chip
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // title
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100, GridUnitType.Pixel) }); // bar
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36, GridUnitType.Pixel) });  // pct
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                     // buttons

        var iconTb = new TextBlock
        {
            Text              = RoadmapService.StatusIcon(item.Status),
            FontSize          = 13,
            Margin            = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var idBorder = new Border
        {
            Background        = bgBrush,
            CornerRadius      = new CornerRadius(4),
            Padding           = new Thickness(4, 1, 4, 1),
            Margin            = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child             = new TextBlock
            {
                Text       = item.Id,
                FontSize   = 9,
                FontFamily = new FontFamily("Consolas"),
                Foreground = subtextBrush
            }
        };

        var titleTb = new TextBlock
        {
            Text             = item.Title,
            FontSize         = 13,
            FontFamily       = new FontFamily("Segoe UI"),
            Foreground       = item.Status == ItemStatus.Done ? subtextBrush : textBrush,
            TextDecorations  = item.Status == ItemStatus.Done ? TextDecorations.Strikethrough : null,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming     = TextTrimming.CharacterEllipsis,
            Margin           = new Thickness(0, 0, 8, 0)
        };

        var pbFill = item.Status == ItemStatus.Done ? ollamaBrush : accentBrush;
        var pb     = MakeProgressBar(item.Progress, pbFill, bgBrush, 5);
        pb.VerticalAlignment = VerticalAlignment.Center;
        pb.Margin            = new Thickness(0, 0, 6, 0);

        var pctTb = new TextBlock
        {
            Text              = $"{item.Progress}%",
            FontSize          = 10,
            FontFamily        = new FontFamily("Segoe UI"),
            Foreground        = subtextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment     = TextAlignment.Right,
            Margin            = new Thickness(0, 0, 8, 0)
        };

        var btns = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        var setPctBtn = new Button
        {
            Content    = "Set%",
            Style      = btnStyle,
            Background = Brushes.Transparent,
            Foreground = subtextBrush,
            FontSize   = 10,
            Padding    = new Thickness(6, 3, 6, 3),
            Margin     = new Thickness(0, 0, 2, 0),
            ToolTip    = "Set progress"
        };
        setPctBtn.Click += (_, _) =>
        {
            var v = ShowProgressSliderDialog(capturedItem.Progress);
            if (v is null) return;
            capturedItem.Progress = v.Value;
            capturedItem.Status   = v.Value >= 100 ? ItemStatus.Done
                                  : v.Value > 0    ? ItemStatus.InProgress
                                  : ItemStatus.Todo;
            UpdateMilestoneStatus(capturedMs);
            SaveRoadmap();
            BuildRoadmapContent();
        };

        var editBtn = new Button
        {
            Content    = "✏",
            Style      = btnStyle,
            Background = Brushes.Transparent,
            Foreground = subtextBrush,
            FontSize   = 12,
            Padding    = new Thickness(6, 3, 6, 3),
            Margin     = new Thickness(0, 0, 2, 0),
            ToolTip    = "Edit item"
        };
        editBtn.Click += (_, _) =>
        {
            var r = ShowItemDialog(capturedItem.Title, capturedItem.Description,
                                   capturedItem.Progress, capturedItem.DateNote, capturedItem.ImageFileName);
            if (r is null) return;
            capturedItem.Title       = r.Value.title;
            capturedItem.Description = r.Value.desc;
            capturedItem.Progress    = r.Value.progress;
            capturedItem.DateNote    = r.Value.dateNote;
            if (r.Value.imageFileName != capturedItem.ImageFileName)
            {
                if (!string.IsNullOrEmpty(capturedItem.ImageFileName))
                {
                    var oldPath = RoadmapService.GetImagePath(_currentProjectFolder!, capturedItem.ImageFileName);
                    try { if (System.IO.File.Exists(oldPath)) System.IO.File.Delete(oldPath); } catch { }
                }
                capturedItem.ImageFileName = r.Value.imageFileName;
            }
            capturedItem.Status      = r.Value.progress >= 100 ? ItemStatus.Done
                                     : r.Value.progress > 0    ? ItemStatus.InProgress
                                     : ItemStatus.Todo;
            UpdateMilestoneStatus(capturedMs);
            SaveRoadmap();
            BuildRoadmapContent();
        };

        btns.Children.Add(setPctBtn);
        btns.Children.Add(editBtn);

        if (item.Status != ItemStatus.Done)
        {
            var doneBtn = new Button
            {
                Content    = "✓",
                Style      = btnStyle,
                Background = Brushes.Transparent,
                Foreground = ollamaBrush,
                FontSize   = 13,
                Padding    = new Thickness(6, 3, 6, 3),
                Margin     = new Thickness(0, 0, 2, 0),
                ToolTip    = "Mark as done"
            };
            doneBtn.Click += (_, _) =>
            {
                capturedItem.Progress    = 100;
                capturedItem.Status      = ItemStatus.Done;
                capturedItem.CompletedBy = "User";
                capturedItem.CompletedAt = DateTime.UtcNow;
                UpdateMilestoneStatus(capturedMs);
                SaveRoadmap();
                BuildRoadmapContent();
            };
            btns.Children.Add(doneBtn);
        }

        var delBtn = new Button
        {
            Content    = "🗑",
            Style      = btnStyle,
            Background = Brushes.Transparent,
            Foreground = subtextBrush,
            FontSize   = 12,
            Padding    = new Thickness(6, 3, 6, 3),
            ToolTip    = "Delete item"
        };
        delBtn.Click += (_, _) =>
        {
            capturedMs.Items.Remove(capturedItem);
            UpdateMilestoneStatus(capturedMs);
            SaveRoadmap();
            BuildRoadmapContent();
        };
        btns.Children.Add(delBtn);

        Grid.SetColumn(iconTb,  0);
        Grid.SetColumn(idBorder, 1);
        Grid.SetColumn(titleTb, 2);
        Grid.SetColumn(pb,      3);
        Grid.SetColumn(pctTb,   4);
        Grid.SetColumn(btns,    5);
        rowGrid.Children.Add(iconTb);
        rowGrid.Children.Add(idBorder);
        rowGrid.Children.Add(titleTb);
        rowGrid.Children.Add(pb);
        rowGrid.Children.Add(pctTb);
        rowGrid.Children.Add(btns);

        // DateNote chip + image thumbnail inline with the item row
        if (!string.IsNullOrWhiteSpace(item.DateNote) || !string.IsNullOrWhiteSpace(item.ImageFileName))
        {
            var extraRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(34, 0, 10, 4) };

            if (!string.IsNullOrWhiteSpace(item.DateNote))
            {
                var chip = new Border { CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(0, 0, 6, 0) };
                chip.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
                chip.SetResourceReference(Border.BackgroundProperty,  "ControlBgBrush");
                var chipTxt = new TextBlock { Text = "🗓 " + item.DateNote, FontSize = 9, FontFamily = new FontFamily("Segoe UI") };
                chipTxt.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
                chip.Child = chipTxt;
                extraRow.Children.Add(chip);
            }

            if (!string.IsNullOrWhiteSpace(item.ImageFileName))
            {
                var imgPath = RoadmapService.GetImagePath(_currentProjectFolder!, item.ImageFileName);
                if (System.IO.File.Exists(imgPath))
                {
                    try
                    {
                        var bmp = new System.Windows.Media.Imaging.BitmapImage();
                        bmp.BeginInit(); bmp.UriSource = new Uri(imgPath); bmp.DecodePixelHeight = 40; bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bmp.EndInit(); bmp.Freeze();
                        var img = new System.Windows.Controls.Image { Source = bmp, Height = 28, Stretch = Stretch.Uniform, Cursor = Cursors.Hand, ToolTip = "Click to open image" };
                        img.MouseLeftButtonDown += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(imgPath) { UseShellExecute = true });
                        var imgBdr = new Border { Child = img, CornerRadius = new CornerRadius(2), ClipToBounds = true };
                        extraRow.Children.Add(imgBdr);
                    }
                    catch { }
                }
            }

            var wrapper = new StackPanel();
            wrapper.Children.Add(new Border { Padding = new Thickness(14, 8, 10, 2), Child = rowGrid });
            wrapper.Children.Add(extraRow);
            return wrapper;
        }

        return new Border { Padding = new Thickness(14, 8, 10, 8), Child = rowGrid };
    }

    // ── Roadmap image zone ────────────────────────────────────────────────

    /// <summary>
    /// Builds a compact image attachment zone for milestone/item dialogs.
    /// <paramref name="imageFileName"/> is updated in place via the ref.
    /// Returns the panel UIElement to embed in the dialog layout.
    /// </summary>
    private FrameworkElement BuildRoadmapImageZone(
        string imageFileName, Action<string> setImageFileName,
        Brush inputBrush, Brush textBrush, Brush subBrush, Style btnStyle)
    {
        string currentFile = imageFileName;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 16, 0) };

        Border thumbBorder;
        System.Windows.Controls.Image? thumbImg = null;

        void Refresh()
        {
            panel.Children.Clear();
            if (!string.IsNullOrEmpty(currentFile))
            {
                var fullPath = RoadmapService.GetImagePath(_currentProjectFolder!, currentFile);
                if (System.IO.File.Exists(fullPath))
                {
                    try
                    {
                        var bmp = new System.Windows.Media.Imaging.BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource       = new Uri(fullPath);
                        bmp.DecodePixelHeight = 80;
                        bmp.CacheOption     = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bmp.EndInit(); bmp.Freeze();
                        thumbImg = new System.Windows.Controls.Image { Source = bmp, Height = 60, Stretch = Stretch.Uniform };
                        var tb = new Border { Child = thumbImg, Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand, ToolTip = "Click to open" };
                        tb.MouseLeftButtonDown += (_, _) =>
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fullPath) { UseShellExecute = true });
                        panel.Children.Add(tb);
                    }
                    catch { }
                }
                var removeBtn = new Button { Content = "✕ Remove", Style = btnStyle, Background = Brushes.Transparent, Foreground = subBrush, FontSize = 10, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 0, 6, 0) };
                removeBtn.Click += (_, _) =>
                {
                    var old = RoadmapService.GetImagePath(_currentProjectFolder!, currentFile);
                    try { if (System.IO.File.Exists(old)) System.IO.File.Delete(old); } catch { }
                    currentFile = "";
                    setImageFileName("");
                    Refresh();
                };
                panel.Children.Add(removeBtn);
            }
            else
            {
                var attachBtn = new Button { Content = "🖼 Attach image…", Style = btnStyle, Background = inputBrush, Foreground = textBrush, FontSize = 11, Padding = new Thickness(8, 4, 8, 4) };
                attachBtn.Click += (_, _) =>
                {
                    var ofd = new Microsoft.Win32.OpenFileDialog
                    {
                        Title  = "Attach image",
                        Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|All files|*.*"
                    };
                    if (ofd.ShowDialog() != true) return;
                    var folder = RoadmapService.GetImagesFolder(_currentProjectFolder!);
                    System.IO.Directory.CreateDirectory(folder);
                    var ext      = System.IO.Path.GetExtension(ofd.FileName);
                    var newName  = $"roadmap_{Guid.NewGuid():N8}{ext}";
                    var destPath = System.IO.Path.Combine(folder, newName);
                    System.IO.File.Copy(ofd.FileName, destPath, overwrite: true);
                    currentFile = newName;
                    setImageFileName(newName);
                    Refresh();
                };
                panel.Children.Add(attachBtn);
            }
        }

        Refresh();
        // Return a wrapper that exposes the final value after dialog closes
        // by reading imageFileName via the ref — but since C# ref can't be captured,
        // we wire it through the Refresh closure instead.
        return panel;
    }

    // ── Roadmap rich-text helpers ─────────────────────────────────────────

    /// <summary>Serializes the RichTextBox FlowDocument to a XAML string.
    /// Returns empty string if the document is effectively empty.</summary>
    private static string RtbToXaml(RichTextBox rtb)
    {
        var range = new TextRange(rtb.Document.ContentStart, rtb.Document.ContentEnd);
        if (string.IsNullOrWhiteSpace(range.Text)) return "";
        try   { return XamlWriter.Save(rtb.Document); }
        catch { return range.Text; }   // plain-text fallback
    }

    /// <summary>Loads a stored description (XAML or legacy plain text) into a RichTextBox.</summary>
    private static void LoadXamlIntoRtb(RichTextBox rtb, string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        if (content.TrimStart().StartsWith('<'))
        {
            try
            {
                var doc = (FlowDocument)XamlReader.Parse(content);
                rtb.Document = doc;
                return;
            }
            catch { /* fall through to plain text */ }
        }
        rtb.Document = new FlowDocument(new Paragraph(new Run(content)));
    }

    /// <summary>Builds a compact rich-text formatting toolbar that sends editing commands
    /// to <paramref name="rtb"/>. Returns a Border suitable for placing above the editor.</summary>
    private FrameworkElement BuildFormattingToolbar(
        RichTextBox rtb, Brush inputBrush, Brush textBrush, Brush subtextBrush)
    {
        var bar = new WrapPanel { Orientation = Orientation.Horizontal };

        void AddSep() => bar.Children.Add(new Rectangle
        {
            Width  = 1, Height = 18, Fill = subtextBrush, Opacity = 0.35,
            Margin = new Thickness(4, 1, 4, 1), VerticalAlignment = VerticalAlignment.Center
        });

        // ── Formatting buttons ─────────────────────────────────────────────
        Button FmtBtn(string text, string tip,
                      FontWeight fw, FontStyle fs, RoutedUICommand cmd)
        {
            var b = new Button
            {
                Content         = text,
                ToolTip         = tip,
                Width           = 28, Height = 26,
                Margin          = new Thickness(1),
                BorderThickness = new Thickness(0),
                Background      = Brushes.Transparent,
                Foreground      = textBrush,
                FontSize        = 13,
                FontFamily      = new FontFamily("Segoe UI"),
                FontWeight      = fw,
                FontStyle       = fs,
                Cursor          = Cursors.Hand,
                Padding         = new Thickness(2, 0, 2, 0)
            };
            b.Click += (_, _) => { cmd.Execute(null, rtb); rtb.Focus(); };
            return b;
        }

        // ── Swatch buttons (text color / highlight) ────────────────────────
        Button SwatchBtn(Color fillColor, string tip, bool isHighlight)
        {
            bool reset = fillColor == Colors.Transparent;
            var b = new Button
            {
                Width           = 20, Height = 20,
                Margin          = new Thickness(1),
                BorderThickness = new Thickness(reset ? 1 : 0),
                BorderBrush     = subtextBrush,
                Background      = reset ? inputBrush : new SolidColorBrush(fillColor),
                Foreground      = textBrush,
                FontSize        = 9,
                Cursor          = Cursors.Hand,
                Padding         = new Thickness(0),
                ToolTip         = tip,
                Content         = reset ? (object)(isHighlight ? "✕" : "A") : null
            };
            var fc = fillColor;
            b.Click += (_, _) =>
            {
                var prop = isHighlight
                    ? TextElement.BackgroundProperty
                    : TextElement.ForegroundProperty;
                if (reset)
                    rtb.Selection.ApplyPropertyValue(prop, DependencyProperty.UnsetValue);
                else
                    rtb.Selection.ApplyPropertyValue(prop, new SolidColorBrush(fc));
                rtb.Focus();
            };
            return b;
        }

        // Bold / Italic / Underline
        bar.Children.Add(FmtBtn("B",  "Bold (Ctrl+B)",
            FontWeights.Bold,   FontStyles.Normal, EditingCommands.ToggleBold));
        bar.Children.Add(FmtBtn("I",  "Italic (Ctrl+I)",
            FontWeights.Normal, FontStyles.Italic, EditingCommands.ToggleItalic));
        bar.Children.Add(FmtBtn("U",  "Underline (Ctrl+U)",
            FontWeights.Normal, FontStyles.Normal, EditingCommands.ToggleUnderline));
        AddSep();

        // Lists
        bar.Children.Add(FmtBtn("•",  "Bullet list",
            FontWeights.Bold,   FontStyles.Normal, EditingCommands.ToggleBullets));
        bar.Children.Add(FmtBtn("1.", "Numbered list",
            FontWeights.Normal, FontStyles.Normal, EditingCommands.ToggleNumbering));
        AddSep();

        // Text color swatches
        foreach (var (color, tip) in new (Color col, string tip)[]
        {
            (Colors.Transparent,               "Default text color"),
            (Color.FromRgb(0xE8, 0x48, 0x55),  "Red"),
            (Color.FromRgb(0xF4, 0xA2, 0x61),  "Orange"),
            (Color.FromRgb(0x52, 0xB7, 0x88),  "Green"),
            (Color.FromRgb(0x4C, 0xC9, 0xF0),  "Blue"),
            (Color.FromRgb(0xB5, 0x17, 0x9E),  "Purple"),
        })
            bar.Children.Add(SwatchBtn(color, tip, isHighlight: false));

        AddSep();

        // Highlight swatches
        foreach (var (color, tip) in new (Color col, string tip)[]
        {
            (Colors.Transparent,               "No highlight"),
            (Color.FromRgb(0xFF, 0xF0, 0x00),  "Yellow highlight"),
            (Color.FromRgb(0xA8, 0xFF, 0xC0),  "Green highlight"),
            (Color.FromRgb(0xAE, 0xD6, 0xFF),  "Blue highlight"),
            (Color.FromRgb(0xFF, 0xAE, 0xD6),  "Pink highlight"),
        })
            bar.Children.Add(SwatchBtn(color, tip, isHighlight: true));

        return new Border
        {
            Child        = bar,
            Background   = inputBrush,
            CornerRadius = new CornerRadius(6),
            Padding      = new Thickness(6, 4, 6, 4),
            Margin       = new Thickness(16, 0, 16, 6)
        };
    }

    // ── Roadmap dialogs ───────────────────────────────────────────────────

    private (string title, string desc, string dateNote, string imageFileName)? ShowMilestoneDialog(
        string title = "", string desc = "", string dateNote = "", string imageFileName = "")
    {
        var isEdit     = !string.IsNullOrEmpty(title);
        var bgBrush    = (Brush)FindResource("SidebarBgBrush");
        var textBrush  = (Brush)FindResource("ContentTextBrush");
        var subBrush   = (Brush)FindResource("ContentDimBrush");
        var inputBrush = (Brush)FindResource("ControlBgBrush");
        var accentBrush= (Brush)FindResource("AccentBgBrush");
        var claudeBrush= (Brush)FindResource("PrimaryAccentBrush");
        var btnStyle   = (Style)FindResource("ModernButton");

        (string, string, string, string)? result = null;
        string currentImageFileName = imageFileName;

        var dlg = new Window
        {
            Title                 = isEdit ? "Edit Milestone" : "Add Milestone",
            Width                 = 560,
            Height                = 560,
            MinWidth              = 420,
            MinHeight             = 420,
            Owner                 = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode            = ResizeMode.CanResize,
            ShowInTaskbar         = false,
            Background            = bgBrush
        };
        ApplyThemeToDialog(dlg);

        // Title input
        var titleBox = new TextBox
        {
            Text                     = title,
            FontSize                 = 13,
            FontFamily               = new FontFamily("Segoe UI"),
            Margin                   = new Thickness(16, 0, 16, 0),
            Height                   = 36,
            Padding                  = new Thickness(10, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness          = new Thickness(0),
            Background               = inputBrush,
            Foreground               = textBrush,
            CaretBrush               = textBrush
        };

        // Description rich-text editor
        var descRtb = new RichTextBox
        {
            FontSize                     = 13,
            FontFamily                   = new FontFamily("Segoe UI"),
            BorderThickness              = new Thickness(0),
            Background                   = Brushes.Transparent,
            Foreground                   = textBrush,
            CaretBrush                   = textBrush,
            SelectionBrush               = accentBrush,
            AcceptsReturn                = true,
            AcceptsTab                   = false,
            VerticalScrollBarVisibility  = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility= ScrollBarVisibility.Disabled,
            Padding                      = new Thickness(8)
        };
        descRtb.Document.PagePadding = new Thickness(0);
        if (!string.IsNullOrEmpty(desc)) LoadXamlIntoRtb(descRtb, desc);

        var toolbar   = BuildFormattingToolbar(descRtb, inputBrush, textBrush, subBrush);
        var descBorder= new Border
        {
            Background   = inputBrush,
            CornerRadius = new CornerRadius(8),
            Margin       = new Thickness(16, 0, 16, 0),
            Padding      = new Thickness(2),
            Child        = descRtb
        };

        // Date note
        MakeDialogLabel("When / Timing (optional)", textBrush, out var dateLbl);
        dateLbl.Margin = new Thickness(16, 10, 16, 4);
        var dateBox = new TextBox
        {
            Text                     = dateNote,
            FontSize                 = 13,
            FontFamily               = new FontFamily("Segoe UI"),
            Margin                   = new Thickness(16, 0, 16, 0),
            Height                   = 32,
            Padding                  = new Thickness(8, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness          = new Thickness(0),
            Background               = inputBrush,
            Foreground               = textBrush,
            CaretBrush               = textBrush,
            ToolTip                  = "e.g. \"sometime in September\", \"Q1 2027\", \"when I get to it\""
        };

        // Image attachment
        MakeDialogLabel("Image (optional)", textBrush, out var imgLbl);
        imgLbl.Margin = new Thickness(16, 10, 16, 4);
        var imgPanel = BuildRoadmapImageZone(currentImageFileName, v => currentImageFileName = v, inputBrush, textBrush, subBrush, btnStyle);

        // Buttons
        var okBtn = new Button
        {
            Content    = isEdit ? "Save" : "Add",
            IsDefault  = true,
            Height     = 34,
            MinWidth   = 80,
            Margin     = new Thickness(0, 0, 8, 0),
            Style      = btnStyle,
            Background = claudeBrush,
            Foreground = (Brush)FindResource("AccentTextBrush")
        };
        okBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(titleBox.Text)) return;
            result = (titleBox.Text.Trim(), RtbToXaml(descRtb), dateBox.Text.Trim(), currentImageFileName);
            dlg.Close();
        };

        var cancelBtn = new Button
        {
            Content    = "Cancel",
            IsCancel   = true,
            Height     = 34,
            MinWidth   = 80,
            Style      = btnStyle,
            Background = inputBrush,
            Foreground = textBrush
        };
        cancelBtn.Click += (_, _) => dlg.Close();

        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(16, 10, 16, 14)
        };
        btnRow.Children.Add(okBtn);
        btnRow.Children.Add(cancelBtn);

        // Labels
        MakeDialogLabel("Title",                  textBrush, out var titleLbl);
        MakeDialogLabel("Description (optional)", textBrush, out var descLbl);
        descLbl.Margin = new Thickness(16, 10, 16, 4);

        // Layout: DockPanel — buttons bottom, title+date+image+toolbar top, RTB fills
        var topSection = new StackPanel();
        topSection.Children.Add(titleLbl);
        topSection.Children.Add(titleBox);
        topSection.Children.Add(dateLbl);
        topSection.Children.Add(dateBox);
        topSection.Children.Add(imgLbl);
        topSection.Children.Add(imgPanel);
        topSection.Children.Add(descLbl);
        topSection.Children.Add(toolbar);

        var outerDock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(btnRow,     Dock.Bottom);
        outerDock.Children.Add(btnRow);
        DockPanel.SetDock(topSection, Dock.Top);
        outerDock.Children.Add(topSection);
        outerDock.Children.Add(descBorder);

        dlg.Content = outerDock;
        dlg.Loaded += (_, _) => { titleBox.Focus(); titleBox.SelectAll(); };
        dlg.ShowDialog();
        return result;
    }

    private (string title, string desc, int progress, string dateNote, string imageFileName)? ShowItemDialog(
        string title = "", string desc = "", int progress = 0, string dateNote = "", string imageFileName = "")
    {
        var isEdit      = !string.IsNullOrEmpty(title);
        var bgBrush     = (Brush)FindResource("SidebarBgBrush");
        var textBrush   = (Brush)FindResource("ContentTextBrush");
        var subBrush    = (Brush)FindResource("ContentDimBrush");
        var inputBrush  = (Brush)FindResource("ControlBgBrush");
        var claudeBrush = (Brush)FindResource("PrimaryAccentBrush");
        var accentBrush = (Brush)FindResource("AccentBgBrush");
        var btnStyle    = (Style)FindResource("ModernButton");

        (string, string, int, string, string)? result = null;
        string currentImageFileName = imageFileName;

        var dlg = new Window
        {
            Title                 = isEdit ? "Edit Item" : "Add Item",
            Width                 = 560,
            Height                = 560,
            MinWidth              = 420,
            MinHeight             = 420,
            Owner                 = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode            = ResizeMode.CanResize,
            ShowInTaskbar         = false,
            Background            = bgBrush
        };
        ApplyThemeToDialog(dlg);

        // Title input
        var titleBox = new TextBox
        {
            Text                     = title,
            FontSize                 = 13,
            FontFamily               = new FontFamily("Segoe UI"),
            Margin                   = new Thickness(16, 0, 16, 0),
            Height                   = 36,
            Padding                  = new Thickness(10, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness          = new Thickness(0),
            Background               = inputBrush,
            Foreground               = textBrush,
            CaretBrush               = textBrush
        };

        // Description rich-text editor
        var descRtb = new RichTextBox
        {
            FontSize                     = 13,
            FontFamily                   = new FontFamily("Segoe UI"),
            BorderThickness              = new Thickness(0),
            Background                   = Brushes.Transparent,
            Foreground                   = textBrush,
            CaretBrush                   = textBrush,
            SelectionBrush               = accentBrush,
            AcceptsReturn                = true,
            AcceptsTab                   = false,
            VerticalScrollBarVisibility  = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility= ScrollBarVisibility.Disabled,
            Padding                      = new Thickness(8)
        };
        descRtb.Document.PagePadding = new Thickness(0);
        if (!string.IsNullOrEmpty(desc)) LoadXamlIntoRtb(descRtb, desc);

        var toolbar    = BuildFormattingToolbar(descRtb, inputBrush, textBrush, subBrush);
        var descBorder = new Border
        {
            Background   = inputBrush,
            CornerRadius = new CornerRadius(8),
            Margin       = new Thickness(16, 0, 16, 0),
            Padding      = new Thickness(2),
            Child        = descRtb
        };

        // Date note
        MakeDialogLabel("When / Timing (optional)", textBrush, out var dateLbl2);
        dateLbl2.Margin = new Thickness(16, 8, 16, 4);
        var dateBox2 = new TextBox
        {
            Text                     = dateNote,
            FontSize                 = 13,
            FontFamily               = new FontFamily("Segoe UI"),
            Margin                   = new Thickness(16, 0, 16, 0),
            Height                   = 32,
            Padding                  = new Thickness(8, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness          = new Thickness(0),
            Background               = inputBrush,
            Foreground               = textBrush,
            CaretBrush               = textBrush,
            ToolTip                  = "e.g. \"by end of October\", \"when chapter 3 is done\""
        };

        // Image attachment
        MakeDialogLabel("Image (optional)", textBrush, out var imgLbl2);
        imgLbl2.Margin = new Thickness(16, 8, 16, 4);
        var imgPanel2 = BuildRoadmapImageZone(currentImageFileName, v => currentImageFileName = v, inputBrush, textBrush, subBrush, btnStyle);

        // Progress
        var pctLabel = new TextBlock
        {
            Text       = $"Initial progress: {progress}%",
            FontSize   = 12,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = subBrush,
            Margin     = new Thickness(16, 8, 16, 2)
        };
        var slider = new Slider
        {
            Minimum             = 0,
            Maximum             = 100,
            Value               = progress,
            TickFrequency       = 10,
            IsSnapToTickEnabled = false,
            Margin              = new Thickness(16, 0, 16, 0)
        };
        slider.ValueChanged += (_, e) =>
            pctLabel.Text = $"Initial progress: {(int)e.NewValue}%";

        // Buttons
        var okBtn = new Button
        {
            Content    = isEdit ? "Save" : "Add",
            IsDefault  = true,
            Height     = 34,
            MinWidth   = 80,
            Margin     = new Thickness(0, 0, 8, 0),
            Style      = btnStyle,
            Background = claudeBrush,
            Foreground = (Brush)FindResource("AccentTextBrush")
        };
        okBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(titleBox.Text)) return;
            result = (titleBox.Text.Trim(), RtbToXaml(descRtb), (int)slider.Value,
                      dateBox2.Text.Trim(), currentImageFileName);
            dlg.Close();
        };

        var cancelBtn = new Button
        {
            Content    = "Cancel",
            IsCancel   = true,
            Height     = 34,
            MinWidth   = 80,
            Style      = btnStyle,
            Background = inputBrush,
            Foreground = textBrush
        };
        cancelBtn.Click += (_, _) => dlg.Close();

        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(16, 10, 16, 14)
        };
        btnRow.Children.Add(okBtn);
        btnRow.Children.Add(cancelBtn);

        // Labels
        MakeDialogLabel("Title",                  textBrush, out var titleLbl);
        MakeDialogLabel("Description (optional)", textBrush, out var descLbl);
        descLbl.Margin = new Thickness(16, 10, 16, 4);

        // Bottom section: progress + buttons (fixed height)
        var bottomSection = new StackPanel();
        bottomSection.Children.Add(pctLabel);
        bottomSection.Children.Add(slider);
        bottomSection.Children.Add(btnRow);

        // Top section: title + date + image + desc label + toolbar
        var topSection = new StackPanel();
        topSection.Children.Add(titleLbl);
        topSection.Children.Add(titleBox);
        topSection.Children.Add(dateLbl2);
        topSection.Children.Add(dateBox2);
        topSection.Children.Add(imgLbl2);
        topSection.Children.Add(imgPanel2);
        topSection.Children.Add(descLbl);
        topSection.Children.Add(toolbar);

        // Layout: DockPanel - fixed sections dock, RTB fills
        var outerDock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(bottomSection, Dock.Bottom);
        outerDock.Children.Add(bottomSection);
        DockPanel.SetDock(topSection, Dock.Top);
        outerDock.Children.Add(topSection);
        outerDock.Children.Add(descBorder);   // fills remaining height

        dlg.Content = outerDock;
        dlg.Loaded += (_, _) => { titleBox.Focus(); titleBox.SelectAll(); };
        dlg.ShowDialog();
        return result;
    }

    private int? ShowProgressSliderDialog(int currentValue)
    {
        var bgBrush    = (Brush)FindResource("SidebarBgBrush");
        var textBrush  = (Brush)FindResource("ContentTextBrush");
        var subtextBrush=(Brush)FindResource("ContentDimBrush");
        var inputBrush = (Brush)FindResource("ControlBgBrush");
        var accentBrush= (Brush)FindResource("AccentBgBrush");
        var btnStyle   = (Style)FindResource("ModernButton");

        int? result = null;

        var dlg = new Window
        {
            Title                 = "Set Progress",
            Width                 = 360,
            Height                = 195,
            Owner                 = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode            = ResizeMode.NoResize,
            ShowInTaskbar         = false,
            Background            = bgBrush
        };
        ApplyThemeToDialog(dlg);

        var pctTb = new TextBlock
        {
            Text                = $"{currentValue}%",
            FontSize            = 24,
            FontWeight          = FontWeights.SemiBold,
            FontFamily          = new FontFamily("Segoe UI"),
            Foreground          = accentBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(16, 16, 16, 4)
        };

        var slider = new Slider
        {
            Minimum             = 0,
            Maximum             = 100,
            Value               = currentValue,
            TickFrequency       = 10,
            IsSnapToTickEnabled = false,
            Margin              = new Thickness(16, 0, 16, 16)
        };
        slider.ValueChanged += (_, e) => pctTb.Text = $"{(int)e.NewValue}%";

        var okBtn = new Button
        {
            Content    = "Set",
            IsDefault  = true,
            Height     = 34,
            Margin     = new Thickness(16, 0, 8, 16),
            Style      = btnStyle,
            Background = accentBrush,
            Foreground = bgBrush
        };
        okBtn.Click += (_, _) => { result = (int)slider.Value; dlg.Close(); };

        var cancelBtn = new Button
        {
            Content    = "Cancel",
            IsCancel   = true,
            Height     = 34,
            Margin     = new Thickness(0, 0, 16, 16),
            Style      = btnStyle,
            Background = inputBrush,
            Foreground = textBrush
        };
        cancelBtn.Click += (_, _) => dlg.Close();

        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        btnRow.Children.Add(okBtn);
        btnRow.Children.Add(cancelBtn);

        var panel = new StackPanel();
        panel.Children.Add(pctTb);
        panel.Children.Add(slider);
        panel.Children.Add(btnRow);
        dlg.Content = panel;

        dlg.Loaded += (_, _) => slider.Focus();
        dlg.ShowDialog();
        return result;
    }

    private static void MakeDialogLabel(string text, Brush fg, out TextBlock lbl)
    {
        lbl = new TextBlock
        {
            Text       = text,
            FontSize   = 12,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = fg,
            Margin     = new Thickness(16, 12, 16, 4)
        };
    }

    // ── Roadmap helpers ───────────────────────────────────────────────────

    private void SaveRoadmap()
    {
        if (_currentRoadmap is not null && _currentProjectFolder is not null)
            RoadmapService.Save(_currentProjectFolder, _currentRoadmap);
    }

    // ── HTML5 Roadmap Export ──────────────────────────────────────────────

    private void ExportRoadmapToHtml()
    {
        if (_currentRoadmap is null || _currentProjectFolder is null) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title            = "Export Roadmap as HTML5",
            Filter           = "HTML file|*.html",
            FileName         = $"{_currentProject?.ProjectName ?? "Roadmap"}_roadmap.html",
            DefaultExt       = "html",
            InitialDirectory = _currentProjectFolder
        };
        if (dlg.ShowDialog() != true) return;

        var html = BuildRoadmapHtml(_currentProject?.ProjectName ?? "Roadmap",
                                    _currentRoadmap, _currentProjectFolder);
        System.IO.File.WriteAllText(dlg.FileName, html, System.Text.Encoding.UTF8);

        // Open in default browser
        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
    }

    private static string BuildRoadmapHtml(string projectName, Roadmap roadmap, string projFolder)
    {
        // Inline images as base64 so the HTML is fully self-contained
        static string InlineImage(string path)
        {
            if (!System.IO.File.Exists(path)) return "";
            try
            {
                var bytes  = System.IO.File.ReadAllBytes(path);
                var b64    = Convert.ToBase64String(bytes);
                var ext    = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                var mime   = ext switch { "png" => "image/png", "gif" => "image/gif",
                                          "webp" => "image/webp", _ => "image/jpeg" };
                return $"data:{mime};base64,{b64}";
            }
            catch { return ""; }
        }

        static string H(string s) => System.Net.WebUtility.HtmlEncode(s);

        static string StatusClass(ItemStatus s) => s switch
        {
            ItemStatus.Done       => "done",
            ItemStatus.InProgress => "progress",
            _                     => "todo"
        };

        static string StatusLabel(ItemStatus s) => s switch
        {
            ItemStatus.Done       => "Done",
            ItemStatus.InProgress => "In Progress",
            _                     => "To Do"
        };

        var totalItems    = roadmap.Milestones.Sum(m => m.Items.Count);
        var doneItems     = roadmap.Milestones.Sum(m => m.Items.Count(i => i.Status == ItemStatus.Done));
        var overallPct    = totalItems > 0 ? doneItems * 100 / totalItems : 0;
        var exported      = DateTime.Now.ToString("MMMM d, yyyy");

        // ── Build milestone HTML ──────────────────────────────────────────
        var msHtml = new System.Text.StringBuilder();
        foreach (var ms in roadmap.Milestones)
        {
            var msImgTag = "";
            if (!string.IsNullOrWhiteSpace(ms.ImageFileName))
            {
                var src = InlineImage(RoadmapService.GetImagePath(projFolder, ms.ImageFileName));
                if (src.Length > 0) msImgTag = $"""<img class="ms-img" src="{src}" alt="milestone image">""";
            }
            var msDateChip = string.IsNullOrWhiteSpace(ms.DateNote) ? ""
                : $"""<span class="date-chip">🗓 {H(ms.DateNote)}</span>""";
            var msDescHtml = string.IsNullOrWhiteSpace(ms.Description) ? ""
                : $"""<p class="ms-desc">{H(RoadmapService.DescToPlainText(ms.Description))}</p>""";

            // Items
            var itemsHtml = new System.Text.StringBuilder();
            foreach (var it in ms.Items)
            {
                var itImgTag = "";
                if (!string.IsNullOrWhiteSpace(it.ImageFileName))
                {
                    var src = InlineImage(RoadmapService.GetImagePath(projFolder, it.ImageFileName));
                    if (src.Length > 0) itImgTag = $"""<img class="it-img" src="{src}" alt="item image">""";
                }
                var itDateChip = string.IsNullOrWhiteSpace(it.DateNote) ? ""
                    : $"""<span class="date-chip small">🗓 {H(it.DateNote)}</span>""";
                var itDesc = string.IsNullOrWhiteSpace(it.Description) ? ""
                    : $"""<p class="it-desc">{H(RoadmapService.DescToPlainText(it.Description))}</p>""";
                var doneBy = it.Status == ItemStatus.Done && !string.IsNullOrWhiteSpace(it.CompletedBy)
                    ? $"""<span class="completed-by">✓ {H(it.CompletedBy)}</span>""" : "";

                itemsHtml.Append($"""
                    <li class="item {StatusClass(it.Status)}">
                      <div class="item-header">
                        <span class="status-dot {StatusClass(it.Status)}" title="{StatusLabel(it.Status)}"></span>
                        <span class="item-title">{H(it.Title)}</span>
                        {itDateChip}
                        {doneBy}
                        <span class="item-pct">{it.Progress}%</span>
                      </div>
                      <div class="item-bar-wrap"><div class="item-bar {StatusClass(it.Status)}" style="width:{it.Progress}%"></div></div>
                      {itDesc}
                      {itImgTag}
                    </li>
                """);
            }

            msHtml.Append($"""
                <div class="milestone {StatusClass(ms.Status)}" data-id="{H(ms.Id)}">
                  <div class="ms-header" onclick="toggleMs(this)">
                    <div class="ms-header-left">
                      <span class="ms-chevron">▼</span>
                      <span class="ms-status {StatusClass(ms.Status)}">{StatusLabel(ms.Status)}</span>
                      <span class="ms-title">{H(ms.Title)}</span>
                      {msDateChip}
                    </div>
                    <div class="ms-header-right">
                      <span class="ms-pct">{ms.Progress}%</span>
                      <div class="ms-bar-wrap"><div class="ms-bar {StatusClass(ms.Status)}" style="width:{ms.Progress}%"></div></div>
                    </div>
                  </div>
                  <div class="ms-body">
                    {msDescHtml}
                    {msImgTag}
                    <ul class="items">{itemsHtml}</ul>
                  </div>
                </div>
            """);
        }

        // CSS and JS are appended as verbatim strings to avoid C# interpolation
        // brace-escaping issues with nested CSS/JS curly braces.
        const string CSS = @"
  :root {
    --bg:#1a1a2e;--surface:#16213e;--card:#0f3460;--accent:#e94560;--accent2:#533483;
    --done:#2e7d32;--progress:#1565c0;--todo:#37474f;--text:#e0e0e0;--subtext:#90a4ae;
    --border:rgba(255,255,255,0.08);--radius:10px;
  }
  *{box-sizing:border-box;margin:0;padding:0;}
  body{font-family:'Segoe UI',system-ui,sans-serif;background:var(--bg);color:var(--text);min-height:100vh;}
  .hero{background:linear-gradient(135deg,var(--surface)0%,var(--card)100%);padding:40px 32px 32px;border-bottom:1px solid var(--border);}
  .hero h1{font-size:2rem;font-weight:700;margin-bottom:4px;}
  .hero .subtitle{color:var(--subtext);font-size:.9rem;margin-bottom:24px;}
  .hero .stats{display:flex;gap:32px;flex-wrap:wrap;}
  .stat{display:flex;flex-direction:column;}
  .stat .val{font-size:1.6rem;font-weight:700;color:var(--accent);}
  .stat .lbl{font-size:.75rem;color:var(--subtext);text-transform:uppercase;letter-spacing:.05em;}
  .overall-bar-wrap{margin-top:20px;background:rgba(255,255,255,0.07);border-radius:6px;height:8px;}
  .overall-bar{height:8px;border-radius:6px;background:linear-gradient(90deg,var(--accent),var(--accent2));transition:width .6s ease;}
  .controls{padding:16px 32px;display:flex;gap:10px;flex-wrap:wrap;align-items:center;border-bottom:1px solid var(--border);background:var(--surface);}
  .btn{padding:6px 14px;border-radius:6px;border:1px solid var(--border);background:var(--card);color:var(--text);cursor:pointer;font-size:.82rem;transition:opacity .15s;}
  .btn:hover{opacity:.8;}
  .btn.active{border-color:var(--accent);color:var(--accent);}
  .search{flex:1;min-width:180px;max-width:320px;padding:6px 12px;border-radius:6px;border:1px solid var(--border);background:var(--card);color:var(--text);font-size:.85rem;}
  .search:focus{outline:none;border-color:var(--accent);}
  .roadmap{padding:24px 32px;max-width:960px;margin:0 auto;}
  .milestone{background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);margin-bottom:14px;overflow:hidden;transition:box-shadow .2s;}
  .milestone:hover{box-shadow:0 4px 20px rgba(0,0,0,.35);}
  .milestone.done>.ms-header{opacity:.7;}
  .ms-header{display:flex;justify-content:space-between;align-items:center;padding:14px 18px;cursor:pointer;user-select:none;background:var(--card);gap:12px;}
  .ms-header:hover{background:rgba(255,255,255,.04);}
  .ms-header-left{display:flex;align-items:center;gap:10px;flex:1;min-width:0;flex-wrap:wrap;}
  .ms-header-right{display:flex;align-items:center;gap:10px;flex-shrink:0;}
  .ms-chevron{font-size:.75rem;transition:transform .25s;color:var(--subtext);}
  .ms-chevron.collapsed{transform:rotate(-90deg);}
  .ms-title{font-weight:600;font-size:1rem;}
  .ms-pct{font-size:.85rem;color:var(--subtext);min-width:36px;text-align:right;}
  .ms-bar-wrap{width:100px;height:6px;background:rgba(255,255,255,.1);border-radius:3px;}
  .ms-bar{height:6px;border-radius:3px;transition:width .4s ease;}
  .ms-bar.done{background:var(--done);}
  .ms-bar.progress{background:var(--progress);}
  .ms-bar.todo{background:var(--todo);}
  .ms-status{font-size:.68rem;padding:2px 8px;border-radius:10px;font-weight:600;text-transform:uppercase;letter-spacing:.04em;flex-shrink:0;}
  .ms-status.done{background:var(--done);}
  .ms-status.progress{background:var(--progress);}
  .ms-status.todo{background:var(--todo);}
  .ms-body{padding:0 18px 14px;}
  .ms-body.hidden{display:none;}
  .ms-desc{color:var(--subtext);font-size:.87rem;margin:12px 0 8px;line-height:1.5;}
  .ms-img{max-width:100%;max-height:280px;border-radius:6px;margin:10px 0;object-fit:contain;cursor:pointer;}
  .ms-img:hover{opacity:.9;}
  .items{list-style:none;margin-top:8px;}
  .item{padding:10px 12px;border-radius:7px;margin-bottom:6px;background:rgba(255,255,255,.03);border:1px solid var(--border);transition:background .15s;}
  .item:hover{background:rgba(255,255,255,.06);}
  .item.done .item-title{text-decoration:line-through;opacity:.55;}
  .item-header{display:flex;align-items:center;gap:8px;flex-wrap:wrap;}
  .status-dot{width:9px;height:9px;border-radius:50%;flex-shrink:0;}
  .status-dot.done{background:var(--done);}
  .status-dot.progress{background:var(--progress);}
  .status-dot.todo{background:var(--todo);}
  .item-title{font-size:.9rem;flex:1;min-width:0;}
  .item-pct{font-size:.75rem;color:var(--subtext);margin-left:auto;}
  .item-bar-wrap{height:3px;background:rgba(255,255,255,.08);border-radius:2px;margin:6px 0;}
  .item-bar{height:3px;border-radius:2px;}
  .item-bar.done{background:var(--done);}
  .item-bar.progress{background:var(--progress);}
  .item-bar.todo{background:var(--todo);}
  .it-desc{font-size:.82rem;color:var(--subtext);margin-top:5px;line-height:1.45;}
  .it-img{max-width:100%;max-height:200px;border-radius:5px;margin-top:8px;object-fit:contain;cursor:pointer;}
  .it-img:hover{opacity:.9;}
  .completed-by{font-size:.72rem;color:#66bb6a;}
  .date-chip{font-size:.72rem;background:rgba(255,255,255,.07);border:1px solid var(--border);border-radius:4px;padding:1px 7px;color:var(--subtext);white-space:nowrap;}
  .date-chip.small{font-size:.68rem;}
  #lightbox{display:none;position:fixed;inset:0;background:rgba(0,0,0,.85);z-index:9999;align-items:center;justify-content:center;cursor:zoom-out;}
  #lightbox.open{display:flex;}
  #lightbox img{max-width:90vw;max-height:90vh;border-radius:8px;box-shadow:0 8px 40px rgba(0,0,0,.7);}
  footer{text-align:center;padding:28px;color:var(--subtext);font-size:.78rem;border-top:1px solid var(--border);margin-top:16px;}
  @media(max-width:600px){.hero{padding:24px 16px 20px;}.roadmap{padding:16px;}.controls{padding:12px 16px;}.ms-bar-wrap{width:60px;}}";

        const string JS = @"
  function toggleMs(header){
    var body=header.nextElementSibling;
    var chev=header.querySelector('.ms-chevron');
    body.classList.toggle('hidden');
    chev.classList.toggle('collapsed');
  }
  function expandAll(){
    document.querySelectorAll('.ms-body').forEach(function(b){
      b.classList.remove('hidden');
      b.previousElementSibling.querySelector('.ms-chevron').classList.remove('collapsed');
    });
  }
  function collapseAll(){
    document.querySelectorAll('.ms-body').forEach(function(b){
      b.classList.add('hidden');
      b.previousElementSibling.querySelector('.ms-chevron').classList.add('collapsed');
    });
  }
  var _statusFilter='all', _searchTerm='';
  function filterAll(btn){_statusFilter='all';setActiveBtn(btn);applyFilter();}
  function filterStatus(s,b){_statusFilter=s;setActiveBtn(b);applyFilter();}
  function setActiveBtn(btn){
    document.querySelectorAll('.controls .btn').forEach(function(b){b.classList.remove('active');});
    btn.classList.add('active');
  }
  function applyFilter(){
    _searchTerm=document.querySelector('.search').value.toLowerCase();
    document.querySelectorAll('.milestone').forEach(function(ms){
      var msTitle=ms.querySelector('.ms-title').textContent.toLowerCase();
      var items=ms.querySelectorAll('.item');
      var statusOk=_statusFilter==='all'||ms.classList.contains(_statusFilter);
      var textOk=!_searchTerm||msTitle.includes(_searchTerm);
      var anyMatch=false;
      items.forEach(function(it){
        var itTitle=it.querySelector('.item-title').textContent.toLowerCase();
        var ok=(_statusFilter==='all'||it.classList.contains(_statusFilter))&&
               (!_searchTerm||itTitle.includes(_searchTerm)||msTitle.includes(_searchTerm));
        it.style.display=ok?'':'none';
        if(ok)anyMatch=true;
      });
      ms.style.display=((statusOk&&textOk)||anyMatch)?'':'none';
      if(_searchTerm&&anyMatch){
        ms.querySelector('.ms-body').classList.remove('hidden');
        ms.querySelector('.ms-chevron').classList.remove('collapsed');
      }
    });
  }
  document.querySelectorAll('.ms-img,.it-img').forEach(function(img){
    img.addEventListener('click',function(e){
      e.stopPropagation();
      document.getElementById('lb-img').src=img.src;
      document.getElementById('lightbox').classList.add('open');
    });
  });
  function closeLightbox(){document.getElementById('lightbox').classList.remove('open');}
  document.addEventListener('keydown',function(e){if(e.key==='Escape')closeLightbox();});
  document.querySelectorAll('.milestone.done').forEach(function(ms){
    var body=ms.querySelector('.ms-body');
    var chev=ms.querySelector('.ms-chevron');
    if(body)body.classList.add('hidden');
    if(chev)chev.classList.add('collapsed');
  });";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"UTF-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine($"<title>{H(projectName)} — Roadmap</title>");
        sb.AppendLine("<style>"); sb.AppendLine(CSS); sb.AppendLine("</style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine($@"<div class=""hero"">
  <h1>&#128202; {H(projectName)}</h1>
  <p class=""subtitle"">Roadmap &middot; Exported {H(exported)}</p>
  <div class=""stats"">
    <div class=""stat""><span class=""val"">{roadmap.Milestones.Count}</span><span class=""lbl"">Milestones</span></div>
    <div class=""stat""><span class=""val"">{totalItems}</span><span class=""lbl"">Items</span></div>
    <div class=""stat""><span class=""val"">{doneItems}</span><span class=""lbl"">Done</span></div>
    <div class=""stat""><span class=""val"">{overallPct}%</span><span class=""lbl"">Overall</span></div>
  </div>
  <div class=""overall-bar-wrap""><div class=""overall-bar"" style=""width:{overallPct}%""></div></div>
</div>
<div class=""controls"">
  <input class=""search"" type=""text"" placeholder=""&#128269;  Search milestones &amp; items&hellip;"" oninput=""applyFilter()"">
  <button class=""btn active"" onclick=""filterAll(this)"">All</button>
  <button class=""btn"" onclick=""filterStatus('todo',this)"">To Do</button>
  <button class=""btn"" onclick=""filterStatus('progress',this)"">In Progress</button>
  <button class=""btn"" onclick=""filterStatus('done',this)"">Done</button>
  <button class=""btn"" onclick=""expandAll()"">Expand all</button>
  <button class=""btn"" onclick=""collapseAll()"">Collapse all</button>
</div>
<div class=""roadmap"" id=""roadmap"">
{msHtml}
</div>
<footer>Generated by ClaudetRelay &middot; {H(projectName)} &middot; {H(exported)}</footer>
<div id=""lightbox"" onclick=""closeLightbox()""><img id=""lb-img"" src="""" alt=""""></div>");
        sb.AppendLine("<script>"); sb.AppendLine(JS); sb.AppendLine("</script>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }


    private static void UpdateMilestoneStatus(RoadmapMilestone ms)
    {
        if (ms.Items.Count == 0) { ms.Status = ItemStatus.Todo; return; }
        if (ms.Items.All(i => i.Status == ItemStatus.Done))
        {
            ms.Status      = ItemStatus.Done;
            ms.CompletedAt ??= DateTime.UtcNow;
        }
        else if (ms.Items.Any(i => i.Status != ItemStatus.Todo))
        {
            ms.Status      = ItemStatus.InProgress;
            ms.CompletedAt = null;
        }
        else
        {
            ms.Status      = ItemStatus.Todo;
            ms.CompletedAt = null;
        }
    }

    /// <summary>
    /// Scans an AI response for [ROADMAP:update:id:N] and (coordinator only)
    /// [ROADMAP:complete:id] tags, applies them to the current roadmap,
    /// saves, and returns the text with all tags stripped.
    /// </summary>
    private string ApplyRoadmapCommands(string text, string sender, bool isCoordinator)
    {
        if (_currentRoadmap is null || _currentProjectFolder is null) return text;

        bool changed = false;

        // <roadmapproposal>…</roadmapproposal> - build/replace the whole roadmap
        var proposalMatch = RoadmapProposalRx.Match(text);
        if (proposalMatch.Success)
        {
            var parsed = ParseRoadmapProposal(proposalMatch.Groups[1].Value, sender);
            if (parsed.Milestones.Count > 0)
            {
                _currentRoadmap = parsed;
                RoadmapService.Save(_currentProjectFolder, _currentRoadmap);
                var itemCount = parsed.Milestones.Sum(m => m.Items.Count);
                AddSystemMessage(
                    $"✅ Roadmap saved - {parsed.Milestones.Count} milestone(s), {itemCount} task(s). " +
                    $"Click 📊 Roadmap to view or edit.");
                Dispatcher.InvokeAsync(() => ShowRoadmapPanel(true),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
            // Strip the proposal tag regardless - never show raw XML to the user
            text = RoadmapProposalRx.Replace(text, "").Trim();
        }

        // <roadmap-describe id="...">description text</roadmap-describe>  - coordinator only
        if (isCoordinator)
        {
            text = RoadmapDescribeRx.Replace(text, m =>
            {
                var id   = m.Groups[1].Value.ToLowerInvariant();
                var desc = m.Groups[2].Value.Trim();

                // Search items first, then milestones
                var item = _currentRoadmap.Milestones
                    .SelectMany(ms => ms.Items)
                    .FirstOrDefault(i => i.Id == id);
                if (item is not null)
                {
                    item.Description = desc;
                    changed = true;
                    return "";
                }
                var milestone = _currentRoadmap.Milestones
                    .FirstOrDefault(ms => ms.Id == id);
                if (milestone is not null)
                {
                    milestone.Description = desc;
                    changed = true;
                }
                return "";
            });

            // <roadmap-additem milestone="..." title="..." description="..."/>  - coordinator only
            text = RoadmapAddItemRx.Replace(text, m =>
            {
                var milestoneRef = m.Groups[1].Value.Trim();
                var title        = m.Groups[2].Value.Trim();
                var desc         = m.Groups[3].Success ? m.Groups[3].Value.Trim() : "";

                if (string.IsNullOrWhiteSpace(title)) return "";

                // Match by id or title (case-insensitive)
                var parent = _currentRoadmap.Milestones.FirstOrDefault(ms =>
                    string.Equals(ms.Id, milestoneRef, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ms.Title, milestoneRef, StringComparison.OrdinalIgnoreCase));

                if (parent is not null)
                {
                    parent.Items.Add(new RoadmapItem
                    {
                        Title       = title,
                        Description = desc,
                        CreatedBy   = sender
                    });
                    UpdateMilestoneStatus(parent);
                    changed = true;
                }
                return "";
            });

            // <roadmap-addmilestone>MILESTONE:/ITEM: format</roadmap-addmilestone>  - coordinator only
            var addMsMatch = RoadmapAddMilestoneRx.Match(text);
            if (addMsMatch.Success)
            {
                var parsed = ParseRoadmapProposal(addMsMatch.Groups[1].Value, sender);
                foreach (var ms in parsed.Milestones)
                    _currentRoadmap.Milestones.Add(ms);
                if (parsed.Milestones.Count > 0)
                {
                    var newItemCount = parsed.Milestones.Sum(ms => ms.Items.Count);
                    AddSystemMessage(
                        $"✅ Roadmap extended - {parsed.Milestones.Count} new milestone(s), " +
                        $"{newItemCount} task(s) added.");
                    changed = true;
                }
                text = RoadmapAddMilestoneRx.Replace(text, "").Trim();
            }
        }

        // [ROADMAP:update:xxxxxxxx:N]
        text = RoadmapUpdateRx.Replace(text, m =>
        {
            var id       = m.Groups[1].Value.ToLowerInvariant();
            var progress = Math.Clamp(int.Parse(m.Groups[2].Value), 0, 100);
            var item     = _currentRoadmap.Milestones
                               .SelectMany(ms => ms.Items)
                               .FirstOrDefault(i => i.Id == id);
            if (item is not null)
            {
                item.Progress = progress;
                item.Status   = progress >= 100 ? ItemStatus.Done
                              : progress > 0    ? ItemStatus.InProgress
                              : ItemStatus.Todo;
                var parent = _currentRoadmap.Milestones.First(ms => ms.Items.Contains(item));
                UpdateMilestoneStatus(parent);
                changed = true;
            }
            return "";
        });

        // [ROADMAP:complete:xxxxxxxx]  - coordinator only
        if (isCoordinator)
        {
            text = RoadmapCompleteRx.Replace(text, m =>
            {
                var id   = m.Groups[1].Value.ToLowerInvariant();
                var item = _currentRoadmap.Milestones
                               .SelectMany(ms => ms.Items)
                               .FirstOrDefault(i => i.Id == id);
                if (item is not null)
                {
                    item.Progress    = 100;
                    item.Status      = ItemStatus.Done;
                    item.CompletedBy = sender;
                    item.CompletedAt = DateTime.UtcNow;
                    var parent = _currentRoadmap.Milestones.First(ms => ms.Items.Contains(item));
                    UpdateMilestoneStatus(parent);
                    changed = true;
                }
                return "";
            });
        }

        if (changed)
        {
            SaveRoadmap();
            if (RoadmapContent.Visibility == Visibility.Visible)
                BuildRoadmapContent();
        }

        return text.Trim();
    }

    private static readonly Regex RoadmapUpdateRx =
        new(@"\[ROADMAP:update:([0-9a-f]{8}):(\d{1,3})\]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RoadmapCompleteRx =
        new(@"\[ROADMAP:complete:([0-9a-f]{8})\]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RoadmapProposalRx =
        new(@"<roadmapproposal>(.*?)</roadmapproposal>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Coordinator-only enrichment tags
    private static readonly Regex RoadmapDescribeRx =
        new(@"<roadmap-describe\s+id=""([^""]+)"">(.*?)</roadmap-describe>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RoadmapAddItemRx =
        new(@"<roadmap-additem\s+milestone=""([^""]+)""\s+title=""([^""]+)""(?:\s+description=""([^""]*)"")?/?>\s*(?:</roadmap-additem>)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RoadmapAddMilestoneRx =
        new(@"<roadmap-addmilestone>(.*?)</roadmap-addmilestone>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parses the body of a <c>&lt;roadmapproposal&gt;</c> tag into a <see cref="Roadmap"/>.
    /// Expected format (one directive per line):
    /// <code>
    /// MILESTONE: Title | Optional description
    ///   ITEM: Title | Optional description
    ///   ITEM: Another task
    /// MILESTONE: Second milestone
    ///   ITEM: ...
    /// </code>
    /// Lines not matching either directive are ignored.
    /// </summary>
    private static Roadmap ParseRoadmapProposal(string body, string createdBy)
    {
        var roadmap  = new Roadmap();
        RoadmapMilestone? current = null;

        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("MILESTONE:", StringComparison.OrdinalIgnoreCase))
            {
                var rest  = line[10..].Trim();
                var parts = rest.Split('|', 2, StringSplitOptions.TrimEntries);
                current = new RoadmapMilestone
                {
                    Title       = parts[0],
                    Description = parts.Length > 1 ? parts[1] : "",
                    CreatedBy   = createdBy
                };
                roadmap.Milestones.Add(current);
            }
            else if (line.StartsWith("ITEM:", StringComparison.OrdinalIgnoreCase) && current is not null)
            {
                var rest  = line[5..].Trim();
                var parts = rest.Split('|', 2, StringSplitOptions.TrimEntries);
                current.Items.Add(new RoadmapItem
                {
                    Title       = parts[0],
                    Description = parts.Length > 1 ? parts[1] : "",
                    CreatedBy   = createdBy
                });
            }
        }

        return roadmap;
    }

    /// <summary>
    /// Returns roadmap context text for injection into AI system prompts.
    /// When the roadmap is empty and the participant is a Coordinator or Planner,
    /// injects the <c>&lt;roadmapproposal&gt;</c> tag format so they know how to submit one.
    /// </summary>
    private string BuildRoadmapContext(ProjectParticipantRole? role)
    {
        if (_currentRoadmap is null) return "";

        if (_currentRoadmap.Milestones.Count == 0)
        {
            if (_currentProjectType?.HasRoadmap == true &&
                (role?.IsCoordinator == true || role?.IsPlanner == true))
            {
                return "\n\n--- ROADMAP ---\n" +
                       "This project has no roadmap yet. You are helping the user build one through " +
                       "conversation. Once you have gathered enough information about their goals and " +
                       "deliverables, propose a complete roadmap using this format:\n\n" +
                       "<roadmapproposal>\n" +
                       "MILESTONE: Milestone title | Optional description\n" +
                       "  ITEM: Task title | Optional description\n" +
                       "  ITEM: Another task | Another description\n" +
                       "MILESTONE: Second milestone | Description\n" +
                       "  ITEM: ...\n" +
                       "</roadmapproposal>\n\n" +
                       "The proposal will be parsed and saved automatically. " +
                       "Only include the tag when you have enough information to propose a meaningful roadmap.\n" +
                       "--- END ROADMAP ---";
            }
            return "";
        }

        return RoadmapService.GetContextText(_currentRoadmap, role?.IsCoordinator == true);
    }

    /// <summary>
    /// Returns a clock-watching instruction for the coordinator so it can suggest breaks
    /// when the user has been working for an extended period.
    /// Only emits when the current role is a coordinator and a session is active.
    /// </summary>
    private string BuildSessionTimeInstruction(ProjectParticipantRole? role)
    {
        if (role?.IsCoordinator != true) return "";
        if (_sessionStartTime is null)   return "";

        var elapsed = DateTime.Now - _sessionStartTime.Value;
        var minutes = (int)elapsed.TotalMinutes;
        if (minutes < 1) return "";

        var timeStr = minutes < 60
            ? $"{minutes} minute{(minutes == 1 ? "" : "s")}"
            : $"{(int)elapsed.TotalHours}h {elapsed.Minutes:D2}m";

        var thresholdNote = elapsed.TotalHours switch
        {
            >= 10 => "\n  IMPORTANT: The user has been at this for 10+ hours. " +
                     "Once or twice every hour, firmly but kindly remind them that rest and sleep " +
                     "will do them far more good than pushing on, and actively encourage them to stop for today.",
            >= 8  => "\n  NOTE: The user has been working for 8+ hours (with only small breaks). " +
                     "Find a natural moment to suggest they step away from the screen, get some fresh " +
                     "air, and move around - their body needs it.",
            >= 3  => "\n  NOTE: The user has been working non-stop for 3+ hours. " +
                     "When the moment feels right, gently ask whether they'd like a short break, " +
                     "a coffee, or something to eat.",
            _     => ""
        };

        return $"\n\n--- WORK SESSION CLOCK ---\n" +
               $"Session time so far: {timeStr}.{thresholdNote}\n" +
               $"--- END WORK SESSION CLOCK ---";
    }

    private void ProjectSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProjectFolder is not null && _currentProject is not null)
            ShowProjectSettingsDialog(_currentProjectFolder, _currentProject.ProjectName);
    }

    private void ShowProjectSettingsDialog(string projFolder, string projectName)
    {
        var ps = ProjectService.LoadProject(projFolder) ?? new ProjectSettings { ProjectName = projectName };

        // ── Normalize potentially corrupted role data ──────────────────────
        // The old aliasing bug (before the deep-copy fix) could save every
        // participant with IsCoordinator=true and/or IsReasoner=true because
        // all rows shared the same role object.  We silently repair both:
        //   • More than one coordinator → impossible by design; keep only first.
        //   • All participants are reasoners → almost certainly a bug artefact;
        //     reset all to false so the user starts from a clean slate.
        if (ps.Roles.Count > 1)
        {
            bool foundCoord = false;
            foreach (var r in ps.Roles)
            {
                if (r.IsCoordinator)
                {
                    if (foundCoord) r.IsCoordinator = false;
                    else            foundCoord = true;
                }
            }

            if (ps.Roles.All(r => r.IsReasoner))
                foreach (var r in ps.Roles)
                    r.IsReasoner = false;
        }

        var win = new Window
        {
            Title                 = $"Project Settings - {projectName}",
            Width                 = 520,
            SizeToContent         = SizeToContent.Height,
            Owner                 = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode            = ResizeMode.NoResize,
            ShowInTaskbar         = false,
            Background            = (Brush)FindResource("SidebarBgBrush")
        };
        ApplyThemeToDialog(win);

        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 4) };

        // ── Project Description ────────────────────────────────────────────
        var descLabel = new TextBlock
        {
            Text       = "PROJECT DESCRIPTION",
            FontSize   = 11, FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"),
            Margin     = new Thickness(0, 0, 0, 6),
            Foreground = (Brush)FindResource("ContentDimBrush")
        };

        var descBox = new TextBox
        {
            Text            = ps.Description,
            FontSize        = 13, FontFamily = new FontFamily("Segoe UI"),
            MinHeight       = 72,
            BorderThickness = new Thickness(0),
            Padding         = new Thickness(10, 8, 10, 8),
            TextWrapping    = TextWrapping.Wrap,
            AcceptsReturn   = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background      = (Brush)FindResource("ControlBgBrush"),
            Foreground      = (Brush)FindResource("ContentTextBrush"),
            CaretBrush      = (Brush)FindResource("InputTextBrush"),
            SelectionBrush  = (Brush)FindResource("PrimaryAccentBrush")
        };

        var descHint = new TextBlock
        {
            Text         = "Shown to all AI participants as project context. " +
                           "Example: \"A dark fantasy novel about a dragon who falls in love with a wizard.\"",
            FontSize     = 11, FontFamily = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 4, 0, 16),
            Foreground   = (Brush)FindResource("ContentDimBrush")
        };

        var descSep = new Rectangle
        {
            Height = 1, Margin = new Thickness(0, 0, 0, 16),
            Fill   = (Brush)FindResource("ControlBgBrush")
        };

        root.Children.Add(descLabel);
        root.Children.Add(descBox);
        root.Children.Add(descHint);
        root.Children.Add(descSep);

        // ── Orchestration Mode ─────────────────────────────────────────────
        var modeLabel = new TextBlock
        {
            Text       = "ORCHESTRATION MODE",
            FontSize   = 11, FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"),
            Margin     = new Thickness(0, 0, 0, 8),
            Foreground = (Brush)FindResource("ContentDimBrush")
        };

        RadioButton MakeRadio(string text, string tip, OrchestrationMode mode) => new RadioButton
        {
            Content     = text,
            IsChecked   = ps.OrchestrationMode == mode,
            GroupName   = "OrcMode",
            FontSize    = 13, FontFamily = new FontFamily("Segoe UI"),
            Margin      = new Thickness(0, 0, 0, 6),
            Foreground  = (Brush)FindResource("ContentTextBrush"),
            Tag         = mode,
            ToolTip     = tip
        };

        // CoordinatorFirst is checked for projects that were previously saved as CoordinatorAuto
        // (legacy value 3) since CoordinatorAuto is no longer exposed in the UI.
        var radioCoordFirst = MakeRadio("Coordinator-first  (default)",
            "The Coordinator answers first and decides which Reasoner(s) should respond next.\n" +
            "Reasoners are triggered when the Coordinator tags them (e.g. @Reasoner).\n" +
            "Coordinator automation (SuperPowers calibration, work-session greeting) runs automatically.",
            OrchestrationMode.CoordinatorFirst);
        // Treat legacy CoordinatorAuto as CoordinatorFirst in the UI
        if (ps.OrchestrationMode == OrchestrationMode.CoordinatorAuto)
            radioCoordFirst.IsChecked = true;

        var radioCoordSum = MakeRadio("All respond, Coordinator summarizes",
            "All participants respond normally. The Coordinator then receives all answers\n" +
            "as context and writes a final synthesising summary.",
            OrchestrationMode.CoordinatorSummarizes);

        var radioCoordOnly = MakeRadio("Coordinator Only  (hidden AI-to-AI)",
            "The user communicates only with the Coordinator.\n" +
            "All AI-to-AI work (Coordinator deliberation + Reasoner responses) is hidden.\n" +
            "Small status indicators show which participant is active.\n" +
            "Only the Coordinator's final synthesis is shown to the user.",
            OrchestrationMode.CoordinatorOnly);

        var radioAll = MakeRadio("Full Manual Mode",
            "Every active participant answers every user message - no coordinator automation.\n" +
            "No SuperPowers calibration, no work-session greeting.\n" +
            "Use when you want to manage all task assignments yourself.",
            OrchestrationMode.AllRespond);

        // Reset-roadmap-planning link - shown when roadmap building has already been started
        var resetRoadmapTb = new TextBlock
        {
            FontFamily  = new FontFamily("Segoe UI"),
            FontSize    = 11,
            Margin      = new Thickness(20, 0, 0, 4),
            Visibility  = (_currentProjectType?.HasRoadmap == true && ps.RoadmapInitialized)
                          ? Visibility.Visible : Visibility.Collapsed
        };
        resetRoadmapTb.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        var resetRoadmapLink = new Hyperlink();
        resetRoadmapLink.Inlines.Add("🗺 Reset roadmap planning (coordinator re-starts conversation on next open)");
        resetRoadmapLink.Click += (_, _) =>
        {
            ps.RoadmapInitialized = false;
            resetRoadmapTb.Visibility = Visibility.Collapsed;
        };
        resetRoadmapTb.Inlines.Add(resetRoadmapLink);

        var modeStack = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        modeStack.Children.Add(modeLabel);
        modeStack.Children.Add(radioCoordFirst);
        modeStack.Children.Add(radioCoordSum);
        modeStack.Children.Add(radioCoordOnly);
        modeStack.Children.Add(radioAll);
        modeStack.Children.Add(resetRoadmapTb);
        root.Children.Add(modeStack);

        // ── Language ───────────────────────────────────────────────────────
        var langLabel = new TextBlock
        {
            Text       = "RESPONSE LANGUAGE",
            FontSize   = 11, FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"),
            Margin     = new Thickness(0, 0, 0, 6),
            Foreground = (Brush)FindResource("ContentDimBrush")
        };

        var langCombo = new ComboBox
        {
            IsEditable            = true,
            Text                  = ps.Language,
            FontSize              = 13,
            FontFamily            = new FontFamily("Segoe UI"),
            Margin                = new Thickness(0, 0, 0, 6),
            ToolTip               = "Leave empty to let the model follow the conversation language"
        };
        foreach (var lang in new[]
        {
            "", "English", "Deutsch", "Français", "Español", "Italiano",
            "Português", "Nederlands", "Polski", "Русский", "日本語", "中文"
        })
        {
            var item = new ComboBoxItem { Content = lang };
            langCombo.Items.Add(item);
            if (lang == ps.Language) langCombo.SelectedItem = item;
        }

        var langHint = new TextBlock
        {
            Text       = "Empty = follow the conversation language",
            FontSize   = 11, FontFamily = new FontFamily("Segoe UI"),
            Margin     = new Thickness(0, 0, 0, 16),
            Foreground = (Brush)FindResource("ContentDimBrush")
        };

        root.Children.Add(langLabel);
        root.Children.Add(langCombo);
        root.Children.Add(langHint);

        // ── Max. Dialog Depth ──────────────────────────────────────────────
        var depthLabel = new TextBlock
        {
            Text       = "MAX. DIALOG DEPTH",
            FontSize   = 11, FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"),
            Margin     = new Thickness(0, 0, 0, 6),
            Foreground = (Brush)FindResource("ContentDimBrush")
        };

        var depthBox = new TextBox
        {
            Text              = ps.MaxDialogDepth.ToString(),
            Width             = 60,
            Height            = 32,
            FontSize          = 13,
            FontFamily        = new FontFamily("Segoe UI"),
            TextAlignment     = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 10, 0),
            Foreground        = (Brush)FindResource("ContentTextBrush"),
            Background        = (Brush)FindResource("ControlBgBrush"),
            BorderBrush       = (Brush)FindResource("ControlBgBrush"),
            ToolTip           = "Positive integer. 1 = only respond to user (no AI-to-AI chaining)."
        };
        // Allow only digits
        depthBox.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);

        var depthHintTb = new TextBlock
        {
            Text              = "How many AI-to-AI response rounds are allowed before the user must send a new message.",
            FontSize          = 11, FontFamily = new FontFamily("Segoe UI"),
            TextWrapping      = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground        = (Brush)FindResource("ContentDimBrush")
        };

        var depthRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 0, 0, 16)
        };
        depthRow.Children.Add(depthBox);
        depthRow.Children.Add(depthHintTb);

        root.Children.Add(depthLabel);
        root.Children.Add(depthRow);

        // ── Default Response Length ────────────────────────────────────────
        var defLenLabel = new TextBlock
        {
            Text       = "DEFAULT RESPONSE LENGTH",
            FontSize   = 11, FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"),
            Margin     = new Thickness(0, 0, 0, 6),
            Foreground = (Brush)FindResource("ContentDimBrush")
        };

        var defLenSlider = new Slider
        {
            Minimum             = 0, Maximum = 100,
            Value               = ps.DefaultResponseLength,
            TickFrequency       = 10,
            IsSnapToTickEnabled = false,
            Width               = 220,
            Margin              = new Thickness(0, 0, 10, 0),
            VerticalAlignment   = VerticalAlignment.Center
        };

        var defLenValueTb = new TextBlock
        {
            FontSize          = 12, FontFamily = new FontFamily("Segoe UI"),
            Width             = 80,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground        = (Brush)FindResource("ContentTextBrush")
        };

        string DefLenName(int v) => v switch
        {
            < 10  => "One-liner",
            < 30  => "Brief",
            < 45  => "Concise",
            <= 55 => "Balanced",
            < 70  => "Moderate",
            < 90  => "Detailed",
            _     => "Monologue"
        };
        defLenValueTb.Text = DefLenName(ps.DefaultResponseLength);
        defLenSlider.ValueChanged += (_, e) =>
            defLenValueTb.Text = DefLenName((int)e.NewValue);

        // "Apply to All" is wired after the allRoles list is populated below
        var applyAllBtn = new Button
        {
            Content           = "Apply to all",
            Height            = 28, Padding = new Thickness(10, 0, 10, 0),
            Style             = (Style)FindResource("ModernButton"),
            Background        = (Brush)FindResource("ControlBgBrush"),
            Foreground        = (Brush)FindResource("ContentTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(10, 0, 0, 0),
            ToolTip           = "Override the response length for every participant in this project"
        };

        var defLenRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 0, 0, 16)
        };
        defLenRow.Children.Add(defLenSlider);
        defLenRow.Children.Add(defLenValueTb);
        defLenRow.Children.Add(applyAllBtn);

        root.Children.Add(defLenLabel);
        root.Children.Add(defLenRow);

        // ── Default Chattiness ─────────────────────────────────────────────
        var defChatLabel = new TextBlock
        {
            Text       = "CHATTINESS",
            FontSize   = 11, FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"),
            Margin     = new Thickness(0, 0, 0, 6),
            Foreground = (Brush)FindResource("ContentDimBrush")
        };

        int defChatValue = ps.DefaultChattiness >= 0 ? ps.DefaultChattiness
                         : (int)Math.Clamp(SettingsService.Load().GlobalChattiness, 0, 100);

        var defChatSlider = new Slider
        {
            Minimum = 0, Maximum = 100,
            Value   = defChatValue,
            TickFrequency = 10, IsSnapToTickEnabled = false,
            Width = 220, Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var defChatValueTb = new TextBlock
        {
            FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
            Width = 100, VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("ContentTextBrush"),
            Text = SettingsWindow.FormatChattinessLabel(defChatValue)
        };
        defChatSlider.ValueChanged += (_, e) =>
            defChatValueTb.Text = SettingsWindow.FormatChattinessLabel((int)e.NewValue);

        var defChatRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 0, 0, 4)
        };
        defChatRow.Children.Add(defChatSlider);
        defChatRow.Children.Add(defChatValueTb);

        var defChatHint = new TextBlock
        {
            Text = "Overrides the global Chattiness setting for this project. " +
                   "Silent = only respond when addressed. Chatty = always keep discussing.",
            FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            Foreground = (Brush)FindResource("ContentDimBrush")
        };

        root.Children.Add(defChatLabel);
        root.Children.Add(defChatRow);
        root.Children.Add(defChatHint);

        // ── Separator ──────────────────────────────────────────────────────
        var sep = new Rectangle
        {
            Height  = 1,
            Margin  = new Thickness(0, 0, 0, 14),
            Fill    = (Brush)FindResource("ControlBgBrush")
        };
        root.Children.Add(sep);

        // ── Participant Roles ──────────────────────────────────────────────
        var rolesLabel = new TextBlock
        {
            Text       = "PARTICIPANT ROLES",
            FontSize   = 11, FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"),
            Margin     = new Thickness(0, 0, 0, 10),
            Foreground = (Brush)FindResource("ContentDimBrush")
        };
        root.Children.Add(rolesLabel);

        // Collect all currently enabled participants from global settings
        var appSettings = SettingsService.Load();
        var enabledParticipants = appSettings.Participants
            .Where(p => p.Enabled)
            .Select(p =>
            {
                var provider    = p.Type;
                var model       = p.Model;
                var displayName = string.IsNullOrEmpty(p.Name)
                    ? FormatModelDisplayName(model)
                    : p.Name;
                return (provider, model, displayName);
            })
            .ToList();

        if (enabledParticipants.Count == 0)
        {
            var noParticipants = new TextBlock
            {
                Text       = "No participants are currently enabled. Enable participants in 👤 Participant Config first.",
                FontSize   = 12, FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap,
                Margin     = new Thickness(0, 0, 0, 12),
                Foreground = (Brush)FindResource("ContentDimBrush")
            };
            root.Children.Add(noParticipants);
        }

        // Build a compact row per participant; full editing via per-character popup
        var roleRows = new List<(string provider, string model, Func<ProjectParticipantRole> GetRole)>();
        var allRoles = new List<ProjectParticipantRole>(); // for "Apply to All"

        // If no coordinator is set yet, default the first participant to CO
        bool anyCoordinator = ps.Roles.Any(r => r.IsCoordinator);

        for (int pi = 0; pi < enabledParticipants.Count; pi++)
        {
            var (provider, model, displayName) = enabledParticipants[pi];

            // ── Role lookup: positional first, key-based as fallback ───────
            // When the old aliasing bug saved duplicate provider+model entries,
            // a key-only lookup always returned entry[0] for every participant,
            // making everyone appear as coordinator.  Positional matching gives
            // each participant its own dedicated slot regardless of duplicate keys.
            ProjectParticipantRole? existing = null;
            if (pi < ps.Roles.Count)
            {
                var candidate = ps.Roles[pi];
                if (string.Equals(candidate.Provider, provider, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.Model,    model,    StringComparison.OrdinalIgnoreCase))
                    existing = candidate;
            }
            existing ??= ps.Get(provider, model);   // fallback for changed participant list

            bool available = provider == "Ollama"
                             || !string.IsNullOrWhiteSpace(WindowsCredentialManager.Load(provider));

            // Fresh working copy - one per participant, no shared references
            var role = new ProjectParticipantRole
            {
                Provider         = provider,
                Model            = model,
                DisplayName      = displayName,
                AnswerAsName     = existing?.AnswerAsName     ?? "",
                RoleInstruction  = existing?.RoleInstruction  ?? "",
                ResponseLength   = existing?.ResponseLength   ?? ps.DefaultResponseLength,
                IsCoordinator    = existing?.IsCoordinator    ?? (!anyCoordinator && pi == 0),
                IsReasoner       = existing?.IsReasoner       ?? false,
                ReasonerPriority = existing?.ReasonerPriority ?? 5,
                IsCritic         = existing?.IsCritic         ?? false,
                IsPlanner        = existing?.IsPlanner        ?? false,
                IsResearcher     = existing?.IsResearcher     ?? false,
                IsActive         = existing?.IsActive         ?? true
            };

            // ── Avatar chip with CO / R badge overlay ────────────────────────
            var avatarCircle = new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(17),
                Background          = (Brush)FindResource(available ? "SecondaryAccentBrush" : "ContentDimBrush"),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Top
            };
            avatarCircle.Child = new TextBlock
            {
                Text = FormatModelAvatarLabel(model),
                FontSize = 11, FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("AccentTextBrush")
            };

            // CO badge - gold, top-right
            var coBadgeText = new TextBlock
            {
                Text = "CO", FontSize = 8, FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"), Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };
            var coBadgeBorder = new Border
            {
                CornerRadius        = new CornerRadius(3),
                Padding             = new Thickness(2, 0, 2, 0),
                Height              = 13,
                Background          = new SolidColorBrush(Color.FromRgb(255, 215, 0)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Top,
                Visibility          = role.IsCoordinator ? Visibility.Visible : Visibility.Collapsed,
                Child = coBadgeText
            };

            // R badge - silver, center-right
            var rBadgeText = new TextBlock
            {
                Text = "R", FontSize = 8, FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"), Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };
            var rBadgeBorder = new Border
            {
                CornerRadius        = new CornerRadius(3),
                Padding             = new Thickness(2, 0, 2, 0),
                Height              = 13,
                Background          = (Brush)FindResource("ContentDimBrush"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Center,
                Visibility          = role.IsReasoner ? Visibility.Visible : Visibility.Collapsed,
                Child = rBadgeText
            };

            // CR badge - brass, top-left
            var crBadgeBorder = new Border
            {
                CornerRadius        = new CornerRadius(3),
                Padding             = new Thickness(2, 0, 2, 0),
                Height              = 13,
                Background          = new SolidColorBrush(Color.FromRgb(205, 149, 12)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Top,
                Visibility          = role.IsCritic ? Visibility.Visible : Visibility.Collapsed,
                Child               = new TextBlock { Text = "CR", FontSize = 8, FontWeight = FontWeights.Bold,
                                          FontFamily = new FontFamily("Segoe UI"), Foreground = Brushes.Black,
                                          HorizontalAlignment = HorizontalAlignment.Center,
                                          VerticalAlignment   = VerticalAlignment.Center }
            };

            // PL badge - amber, bottom-left
            var plBadgeBorder = new Border
            {
                CornerRadius        = new CornerRadius(3),
                Padding             = new Thickness(2, 0, 2, 0),
                Height              = 13,
                Background          = new SolidColorBrush(Color.FromRgb(255, 140, 0)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Bottom,
                Visibility          = role.IsPlanner ? Visibility.Visible : Visibility.Collapsed,
                Child               = new TextBlock { Text = "PL", FontSize = 8, FontWeight = FontWeights.Bold,
                                          FontFamily = new FontFamily("Segoe UI"), Foreground = Brushes.Black,
                                          HorizontalAlignment = HorizontalAlignment.Center,
                                          VerticalAlignment   = VerticalAlignment.Center }
            };

            // RS badge - steel blue, bottom-right
            var rsBadgeBorder = new Border
            {
                CornerRadius        = new CornerRadius(3),
                Padding             = new Thickness(2, 0, 2, 0),
                Height              = 13,
                Background          = new SolidColorBrush(Color.FromRgb(70, 130, 180)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Bottom,
                Visibility          = role.IsResearcher ? Visibility.Visible : Visibility.Collapsed,
                Child               = new TextBlock { Text = "RS", FontSize = 8, FontWeight = FontWeights.Bold,
                                          FontFamily = new FontFamily("Segoe UI"), Foreground = Brushes.White,
                                          HorizontalAlignment = HorizontalAlignment.Center,
                                          VerticalAlignment   = VerticalAlignment.Center }
            };

            // WR badge - green, bottom-center (avoids covering the avatar initials)
            // Show for any participant with explicit write access OR the coordinator (write always implied).
            var wrBadgeBorder = new Border
            {
                CornerRadius        = new CornerRadius(3),
                Padding             = new Thickness(2, 0, 2, 0),
                Height              = 13,
                Background          = new SolidColorBrush(Color.FromRgb(34, 139, 34)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Bottom,
                Visibility          = (role.IsWriteAccess || role.IsCoordinator) ? Visibility.Visible : Visibility.Collapsed,
                Child               = new TextBlock { Text = "WR", FontSize = 8, FontWeight = FontWeights.Bold,
                                          FontFamily = new FontFamily("Segoe UI"), Foreground = Brushes.White,
                                          HorizontalAlignment = HorizontalAlignment.Center,
                                          VerticalAlignment   = VerticalAlignment.Center }
            };

            // Grid container (still named avatarBorder - column-set code below unchanged)
            var avatarBorder = new Grid
            {
                Width             = 38, Height = 38,
                Margin            = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            avatarBorder.Children.Add(avatarCircle);
            avatarBorder.Children.Add(coBadgeBorder);
            avatarBorder.Children.Add(rBadgeBorder);
            avatarBorder.Children.Add(crBadgeBorder);
            avatarBorder.Children.Add(plBadgeBorder);
            avatarBorder.Children.Add(rsBadgeBorder);
            avatarBorder.Children.Add(wrBadgeBorder);

            // ── Name + model sub-label ─────────────────────────────────────
            var nameTb = new TextBlock
            {
                Text = displayName, FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource(available ? "ContentTextBrush" : "ContentDimBrush")
            };
            var modelTb = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(role.AnswerAsName)
                           ? $"{provider}  ·  {model}"
                           : $"{provider}  ·  {model}  ·  🎭 {role.AnswerAsName}",
                FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
                Foreground = (Brush)FindResource("ContentDimBrush"),
                Margin = new Thickness(0, 1, 0, 0)
            };
            var nameStack = new StackPanel();
            nameStack.Children.Add(nameTb);
            nameStack.Children.Add(modelTb);

            // ── Active toggle ──────────────────────────────────────────────
            var activeCheck = new CheckBox
            {
                IsChecked  = role.IsActive,
                IsEnabled  = available,
                ToolTip    = "Active in this scene",
                Margin     = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            // ── Edit button ────────────────────────────────────────────────
            var editBtn = new Button
            {
                Content    = "✏ Edit",
                Height     = 28, Padding = new Thickness(10, 0, 10, 0),
                Style      = (Style)FindResource("ModernButton"),
                Background = (Brush)FindResource("ControlBgBrush"),
                Foreground = (Brush)FindResource("ContentTextBrush"),
                IsEnabled  = available,
                VerticalAlignment = VerticalAlignment.Center
            };

            var capturedRole    = role;
            var capturedModelTb = modelTb;
            var capturedCoBadge = coBadgeBorder;
            var capturedRBadge  = rBadgeBorder;
            var capturedCrBadge = crBadgeBorder;
            var capturedPlBadge = plBadgeBorder;
            var capturedRsBadge = rsBadgeBorder;
            var capturedWrBadge = wrBadgeBorder;
            editBtn.Click += (_, _) =>
            {
                if (ShowCharacterEditorDialog(capturedRole, projFolder, displayName))
                {
                    // Refresh subtitle
                    capturedModelTb.Text = string.IsNullOrWhiteSpace(capturedRole.AnswerAsName)
                        ? $"{provider}  ·  {model}"
                        : $"{provider}  ·  {model}  ·  🎭 {capturedRole.AnswerAsName}";
                    // Refresh all role badges independently
                    capturedCoBadge.Visibility = capturedRole.IsCoordinator                                    ? Visibility.Visible : Visibility.Collapsed;
                    capturedRBadge .Visibility = capturedRole.IsReasoner                                       ? Visibility.Visible : Visibility.Collapsed;
                    capturedCrBadge.Visibility = capturedRole.IsCritic                                         ? Visibility.Visible : Visibility.Collapsed;
                    capturedPlBadge.Visibility = capturedRole.IsPlanner                                        ? Visibility.Visible : Visibility.Collapsed;
                    capturedRsBadge.Visibility = capturedRole.IsResearcher                                     ? Visibility.Visible : Visibility.Collapsed;
                    capturedWrBadge.Visibility = (capturedRole.IsWriteAccess || capturedRole.IsCoordinator)    ? Visibility.Visible : Visibility.Collapsed;
                }
            };

            // ── Row grid ───────────────────────────────────────────────────
            var rowGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(avatarBorder, 0);
            Grid.SetColumn(nameStack,    1);
            Grid.SetColumn(activeCheck,  2);
            Grid.SetColumn(editBtn,      3);
            rowGrid.Children.Add(avatarBorder);
            rowGrid.Children.Add(nameStack);
            rowGrid.Children.Add(activeCheck);
            rowGrid.Children.Add(editBtn);
            root.Children.Add(rowGrid);

            // Inactive dimming - dims everything in the row (checkbox stays clickable)
            var capturedActive  = activeCheck;
            var capturedRowGrid = rowGrid;
            void UpdateDim()
            {
                bool active = capturedActive.IsChecked == true;
                capturedRowGrid.Opacity = !available ? 0.45 : !active ? 0.5 : 1.0;
            }
            capturedActive.Checked   += (_, _) => UpdateDim();
            capturedActive.Unchecked += (_, _) => UpdateDim();
            UpdateDim(); // apply initial state

            roleRows.Add((provider, model, () =>
            {
                capturedRole.IsActive = capturedActive.IsChecked == true;
                return capturedRole;
            }));
            allRoles.Add(role);
        }

        // Wire "Apply to All" now that allRoles is fully populated
        applyAllBtn.Click += (_, _) =>
        {
            var len = (int)defLenSlider.Value;
            foreach (var r in allRoles)
                r.ResponseLength = len;
            AddSystemMessage($"✅  Response length set to {DefLenName(len)} for all participants.");
        };

        // ── Buttons ────────────────────────────────────────────────────────
        var sep2 = new Rectangle
        {
            Height = 1, Margin = new Thickness(0, 4, 0, 12),
            Fill   = (Brush)FindResource("ControlBgBrush")
        };
        root.Children.Add(sep2);

        var saveBtn = new Button
        {
            Content    = "Save",
            IsDefault  = true,
            Height     = 36, Margin = new Thickness(0, 0, 8, 16),
            Style      = (Style)FindResource("ModernButton"),
            Background = (Brush)FindResource("PrimaryAccentBrush"),
            Foreground = (Brush)FindResource("AccentTextBrush"),
            FontWeight = FontWeights.SemiBold
        };
        var cancelBtn = new Button
        {
            Content    = "Cancel",
            IsCancel   = true,
            Height     = 36, Margin = new Thickness(0, 0, 0, 16),
            Style      = (Style)FindResource("ModernButton"),
            Background = (Brush)FindResource("ControlBgBrush"),
            Foreground = (Brush)FindResource("ContentTextBrush")
        };
        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        btnRow.Children.Add(saveBtn);
        btnRow.Children.Add(cancelBtn);
        root.Children.Add(btnRow);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 600,
            Content   = root
        };
        win.Content = scroll;

        saveBtn.Click += (_, _) =>
        {
            // Collect orchestration mode
            ps.OrchestrationMode = radioCoordFirst.IsChecked == true ? OrchestrationMode.CoordinatorFirst
                                 : radioCoordSum  .IsChecked == true ? OrchestrationMode.CoordinatorSummarizes
                                 : radioCoordOnly .IsChecked == true ? OrchestrationMode.CoordinatorOnly
                                 : OrchestrationMode.AllRespond;  // radioAll = Full Manual Mode

            // Collect language
            ps.Language = langCombo.Text.Trim();

            // Collect max dialog depth
            ps.MaxDialogDepth = int.TryParse(depthBox.Text, out var d) && d >= 1 ? d : 1;

            // Collect default response length
            ps.DefaultResponseLength = (int)defLenSlider.Value;

            // Collect chattiness override
            ps.DefaultChattiness = (int)defChatSlider.Value;

            // Collect roles
            ps.Roles.Clear();
            foreach (var (_, _, getRoleSnapshot) in roleRows)
                ps.Roles.Add(getRoleSnapshot());

            // ── Enforce exactly one coordinator ───────────────────────────────
            var coordinators = ps.Roles.Where(r => r.IsCoordinator).ToList();

            if (coordinators.Count == 0 && ps.Roles.Count > 0)
            {
                // No coordinator - auto-assign the first participant (prefer Cloud AI over Ollama
                // since cloud models have larger context windows and handle routing better).
                var autoCoord = ps.Roles.FirstOrDefault(r =>
                                    !string.Equals(r.Provider, "Ollama", StringComparison.OrdinalIgnoreCase))
                             ?? ps.Roles[0];
                autoCoord.IsCoordinator = true;
                autoCoord.IsReasoner    = false;   // coordinator can't simultaneously be a reasoner
                MessageBox.Show(
                    $"No Coordinator was set - \"{autoCoord.DisplayName}\" has been automatically assigned as Coordinator.\n\n" +
                    "Every project needs a Coordinator to route messages and manage the team.",
                    "Coordinator Auto-Assigned", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (coordinators.Count > 1)
            {
                // Multiple coordinators - keep only the first, quietly fix the rest.
                foreach (var r in coordinators.Skip(1)) r.IsCoordinator = false;
                MessageBox.Show(
                    $"Only one Coordinator is allowed - \"{coordinators[0].DisplayName}\" has been kept.",
                    "Multiple Coordinators Fixed", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            // A coordinator cannot simultaneously be a reasoner.
            foreach (var r in ps.Roles.Where(r => r.IsCoordinator))
                r.IsReasoner = false;

            ps.Description = descBox.Text.Trim();
            ProjectService.SaveProject(projFolder, ps);

            // Keep live fields in sync if this is the currently open project
            if (_currentProjectFolder == projFolder)
            {
                _projectLanguage  = ps.Language;
                _maxDialogDepth   = ps.MaxDialogDepth;
                _projectSettings  = ps;
                _currentProject   = ps;
                RefreshParticipantBadges();
            }

            win.DialogResult = true;
        };

        win.ShowDialog();
    }

    // ── Per-participant character editor ───────────────────────────────────

    /// <summary>
    /// Opens a modal character editor for one participant.
    /// Edits <paramref name="role"/> in-place when the user presses OK.
    /// Returns true if the user confirmed.
    /// </summary>
    private bool ShowCharacterEditorDialog(ProjectParticipantRole role, string projFolder, string displayName)
    {
        // ── Snapshot for reset ────────────────────────────────────────────
        var snap = new ProjectParticipantRole
        {
            Provider         = role.Provider,
            Model            = role.Model,
            DisplayName      = role.DisplayName,
            AnswerAsName     = role.AnswerAsName,
            RoleInstruction  = role.RoleInstruction,
            ResponseLength   = role.ResponseLength,
            IsCoordinator    = role.IsCoordinator,
            IsReasoner       = role.IsReasoner,
            ReasonerPriority = role.ReasonerPriority,
            IsCritic         = role.IsCritic,
            IsPlanner        = role.IsPlanner,
            IsResearcher     = role.IsResearcher,
            IsWriteAccess    = role.IsWriteAccess,
            IsActive         = role.IsActive
        };

        // ── Window ────────────────────────────────────────────────────────
        var win = new Window
        {
            Title                 = $"Character Editor - {displayName}",
            Width                 = 480,
            MaxHeight             = 800,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            Background            = (Brush)FindResource("ContentBgBrush"),
            ResizeMode            = ResizeMode.NoResize
        };
        ApplyThemeToDialog(win);

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 8) };

        // ── Local helpers ─────────────────────────────────────────────────
        TextBlock SectionHeader(string text) => new TextBlock
        {
            Text             = text,
            FontSize         = 11,
            FontWeight       = FontWeights.SemiBold,
            FontFamily       = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("ContentDimBrush"),
            Margin     = new Thickness(0, 14, 0, 6)
        };

        TextBlock MakeLabel(string text) => new TextBlock
        {
            Text       = text,
            FontSize   = 12,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("ContentTextBrush"),
            Margin     = new Thickness(0, 0, 0, 4)
        };

        TextBox MakeTextBox(string text, bool multiline = false) => new TextBox
        {
            Text            = text,
            FontSize        = 13,
            FontFamily      = new FontFamily("Segoe UI"),
            Background      = (Brush)FindResource("ControlBgBrush"),
            Foreground      = (Brush)FindResource("ContentTextBrush"),
            CaretBrush      = (Brush)FindResource("InputTextBrush"),
            BorderBrush     = (Brush)FindResource("ContentDimBrush"),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(8, 6, 8, 6),
            Margin          = new Thickness(0, 0, 0, 12),
            AcceptsReturn   = multiline,
            TextWrapping    = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight       = multiline ? 110 : 0,
            MaxHeight       = multiline ? 200 : double.PositiveInfinity,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled
        };

        Button MakeBtn(string content, Brush bg, Brush fg) => new Button
        {
            Content    = content,
            Height     = 30,
            Padding    = new Thickness(12, 0, 12, 0),
            Style      = (Style)FindResource("ModernButton"),
            Background = bg,
            Foreground = fg,
            Margin     = new Thickness(0, 0, 8, 0)
        };

        static string LengthLabel(double v) => v switch
        {
            < 10  => "One-liner",
            < 30  => "Short",
            < 45  => "Concise",
            <= 55 => "Default",
            < 70  => "Moderate",
            < 90  => "Elaborate",
            _     => "Monologue"
        };

        // ── IDENTITY ──────────────────────────────────────────────────────
        root.Children.Add(SectionHeader("IDENTITY"));

        root.Children.Add(MakeLabel("Answer as (character name):"));
        var answerAsBox = MakeTextBox(role.AnswerAsName);
        root.Children.Add(answerAsBox);

        root.Children.Add(MakeLabel("Role instruction:"));
        var instrBox = MakeTextBox(role.RoleInstruction, multiline: true);
        root.Children.Add(instrBox);

        // ── RESPONSE LENGTH ───────────────────────────────────────────────
        root.Children.Add(SectionHeader("RESPONSE LENGTH"));

        var lengthValueLbl = new TextBlock
        {
            Text                = LengthLabel(role.ResponseLength),
            FontSize            = 12,
            FontFamily          = new FontFamily("Segoe UI"),
            Foreground          = (Brush)FindResource("ContentDimBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(0, 0, 0, 4)
        };
        var lengthSlider = new Slider
        {
            Minimum             = 0,
            Maximum             = 100,
            Value               = role.ResponseLength,
            TickFrequency       = 10,
            IsSnapToTickEnabled = false,
            Margin              = new Thickness(0, 0, 0, 2)
        };
        lengthSlider.ValueChanged += (_, e) => lengthValueLbl.Text = LengthLabel(e.NewValue);

        // End labels row (Short - Long)
        var lengthEndRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        lengthEndRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lengthEndRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var shortEndLbl = new TextBlock { Text = "Short", FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("ContentDimBrush"), HorizontalAlignment = HorizontalAlignment.Left };
        var longEndLbl  = new TextBlock { Text = "Long",  FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("ContentDimBrush"), HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(shortEndLbl, 0); Grid.SetColumn(longEndLbl, 1);
        lengthEndRow.Children.Add(shortEndLbl); lengthEndRow.Children.Add(longEndLbl);

        root.Children.Add(lengthValueLbl);
        root.Children.Add(lengthSlider);
        root.Children.Add(lengthEndRow);

        // ── ORCHESTRATION ─────────────────────────────────────────────────
        root.Children.Add(SectionHeader("ORCHESTRATION"));

        var coordCheck = new CheckBox
        {
            Content    = "Coordinator - routes messages to reasoners",
            IsChecked  = role.IsCoordinator,
            FontSize   = 13,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("ContentTextBrush"),
            ToolTip    = "Only one coordinator per project.",
            Margin     = new Thickness(0, 0, 0, 8)
        };
        root.Children.Add(coordCheck);

        var reasonerCheck = new CheckBox
        {
            Content    = "Reasoner - executes delegated tasks",
            IsChecked  = role.IsReasoner,
            FontSize   = 13,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("ContentTextBrush"),
            Margin     = new Thickness(0, 0, 0, 8)
        };
        root.Children.Add(reasonerCheck);

        // Priority sub-panel (shown only when IsReasoner)
        var priorityPanel = new StackPanel
        {
            Visibility = role.IsReasoner ? Visibility.Visible : Visibility.Collapsed,
            Margin     = new Thickness(20, 0, 0, 10)
        };
        var priorityLbl = new TextBlock
        {
            Text       = $"Priority: {role.ReasonerPriority}  (1 = lowest, 10 = highest)",
            FontSize   = 12,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("ContentTextBrush"),
            Margin     = new Thickness(0, 0, 0, 4)
        };
        var prioritySlider = new Slider
        {
            Minimum             = 1,
            Maximum             = 10,
            Value               = role.ReasonerPriority,
            TickFrequency       = 1,
            IsSnapToTickEnabled = true,
            ToolTip             = "Higher number = higher priority (called first among reasoners). Lower number = called later."
        };
        prioritySlider.ValueChanged += (_, e) =>
            priorityLbl.Text = $"Priority: {(int)e.NewValue}  (1 = lowest, 10 = highest)";
        priorityPanel.Children.Add(priorityLbl);
        priorityPanel.Children.Add(prioritySlider);
        root.Children.Add(priorityPanel);

        // Coordinator â†" Reasoner are mutually exclusive routing roles
        coordCheck  .Checked += (_, _) => { if (reasonerCheck.IsChecked == true) reasonerCheck.IsChecked = false; };
        reasonerCheck.Checked += (_, _) => { if (coordCheck.IsChecked   == true) coordCheck.IsChecked   = false; };

        reasonerCheck.Checked   += (_, _) => priorityPanel.Visibility = Visibility.Visible;
        reasonerCheck.Unchecked += (_, _) => priorityPanel.Visibility = Visibility.Collapsed;

        // Critic, Planner, Researcher - independent specialisation roles
        var criticCheck = new CheckBox
        {
            Content    = "Critic - reviews output for consistency, logic errors, and hallucinations",
            IsChecked  = role.IsCritic,
            FontSize   = 13,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("ContentTextBrush"),
            ToolTip    = "Critic reviews the output of other participants after they respond. Brass badge (CR).",
            Margin     = new Thickness(0, 0, 0, 8)
        };
        root.Children.Add(criticCheck);

        var plannerCheck = new CheckBox
        {
            Content    = "Planner - breaks the user's goal into a structured plan before execution",
            IsChecked  = role.IsPlanner,
            FontSize   = 13,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("ContentTextBrush"),
            ToolTip    = "Planner is called first to produce a work plan. Amber badge (PL).",
            Margin     = new Thickness(0, 0, 0, 8)
        };
        root.Children.Add(plannerCheck);

        var researcherCheck = new CheckBox
        {
            Content    = "Researcher - gathers context and references before main answer",
            IsChecked  = role.IsResearcher,
            FontSize   = 13,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("ContentTextBrush"),
            ToolTip    = "Researcher is called second (after Planner) to supply background knowledge. Steel-blue badge (RS).",
            Margin     = new Thickness(0, 0, 0, 8)
        };
        root.Children.Add(researcherCheck);

        var writeAccessCheck = new CheckBox
        {
            Content    = "Write Access (WR) - may write files using <output> and <projectplan> tags",
            // Pre-check for CO and R: they imply write access by default.
            // Also covers existing saved roles where IsWriteAccess was false before this field existed.
            IsChecked  = role.IsWriteAccess || role.IsCoordinator || role.IsReasoner,
            FontSize   = 13,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("ContentTextBrush"),
            ToolTip    = "Grants this participant file-write access. Coordinators always have write access. " +
                         "All other participants are read-only by default. Green badge (WR).",
            Margin     = new Thickness(0, 0, 0, 8)
        };
        root.Children.Add(writeAccessCheck);

        // CO and R default to write access - pre-check WR as a convenience.
        // WR stays separate so it can be unchecked freely (e.g. a routing-only Reasoner
        // whose SuperRole is purely analytical and never needs to write files).
        coordCheck   .Checked += (_, _) => { writeAccessCheck.IsChecked = true; };
        reasonerCheck.Checked += (_, _) => { writeAccessCheck.IsChecked = true; };

        // ── CHARACTER FILE ────────────────────────────────────────────────
        root.Children.Add(SectionHeader("CHARACTER FILE"));

        var fileRow    = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        var loadCharBtn = MakeBtn("📂 Load character", (Brush)FindResource("ControlBgBrush"), (Brush)FindResource("ContentTextBrush"));
        var saveCharBtn = MakeBtn("💾 Save as character", (Brush)FindResource("ControlBgBrush"), (Brush)FindResource("ContentTextBrush"));
        fileRow.Children.Add(loadCharBtn);
        fileRow.Children.Add(saveCharBtn);
        root.Children.Add(fileRow);

        // Load character
        loadCharBtn.Click += (_, _) =>
        {
            var chars = ProjectService.ListCharacterFiles(projFolder);
            if (chars.Count == 0)
            {
                MessageBox.Show("No character files found in this project's Characters folder.",
                    "No Characters", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var picker = new Window
            {
                Title                 = "Load Character",
                Width                 = 300,
                SizeToContent         = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner                 = win,
                Background            = (Brush)FindResource("ContentBgBrush"),
                ResizeMode            = ResizeMode.NoResize
            };
            ApplyThemeToDialog(picker);
            var pp = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
            pp.Children.Add(new TextBlock
            {
                Text = "Select a character:", FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
                Foreground = (Brush)FindResource("ContentTextBrush"), Margin = new Thickness(0, 0, 0, 8)
            });
            var lb = new ListBox
            {
                Background = (Brush)FindResource("ControlBgBrush"), Foreground = (Brush)FindResource("ContentTextBrush"),
                BorderBrush = (Brush)FindResource("ContentDimBrush"), BorderThickness = new Thickness(1),
                MaxHeight = 200, Margin = new Thickness(0, 0, 0, 12)
            };
            foreach (var c in chars) lb.Items.Add(c);
            lb.SelectedIndex = 0;
            pp.Children.Add(lb);

            var pRow    = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var pOk     = new Button { Content = "Load", IsDefault = true, Height = 30, Padding = new Thickness(14, 0, 14, 0),
                Style = (Style)FindResource("ModernButton"),
                Background = (Brush)FindResource("PrimaryAccentBrush"), Foreground = (Brush)FindResource("AccentTextBrush"),
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0) };
            var pCancel = new Button { Content = "Cancel", IsCancel = true, Height = 30, Padding = new Thickness(14, 0, 14, 0),
                Style = (Style)FindResource("ModernButton"),
                Background = (Brush)FindResource("ControlBgBrush"), Foreground = (Brush)FindResource("ContentTextBrush") };
            pRow.Children.Add(pOk); pRow.Children.Add(pCancel);
            pp.Children.Add(pRow);
            picker.Content = pp;
            pOk.Click += (_, _) => { if (lb.SelectedItem is string) picker.DialogResult = true; };

            if (picker.ShowDialog() == true && lb.SelectedItem is string chosen)
            {
                var data = ProjectService.LoadCharacterFile(projFolder, chosen);
                if (data is not null)
                {
                    answerAsBox.Text   = data.AnswerAsName;
                    instrBox.Text      = data.RoleInstruction;
                    lengthSlider.Value = data.ResponseLength;
                }
            }
        };

        // Save character
        saveCharBtn.Click += (_, _) =>
        {
            var suggestedName = string.IsNullOrWhiteSpace(answerAsBox.Text) ? displayName : answerAsBox.Text.Trim();

            var nameWin = new Window
            {
                Title                 = "Save Character",
                Width                 = 300,
                SizeToContent         = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner                 = win,
                Background            = (Brush)FindResource("ContentBgBrush"),
                ResizeMode            = ResizeMode.NoResize
            };
            ApplyThemeToDialog(nameWin);
            var np = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
            np.Children.Add(new TextBlock
            {
                Text = "Save as character name:", FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
                Foreground = (Brush)FindResource("ContentTextBrush"), Margin = new Thickness(0, 0, 0, 8)
            });
            var nameBox = new TextBox
            {
                Text = suggestedName, FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
                Background = (Brush)FindResource("ControlBgBrush"), Foreground = (Brush)FindResource("ContentTextBrush"),
                CaretBrush = (Brush)FindResource("InputTextBrush"),
                BorderBrush = (Brush)FindResource("ContentDimBrush"), BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 12)
            };
            np.Children.Add(nameBox);
            var nRow    = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var nOk     = new Button { Content = "Save", IsDefault = true, Height = 30, Padding = new Thickness(14, 0, 14, 0),
                Style = (Style)FindResource("ModernButton"),
                Background = (Brush)FindResource("PrimaryAccentBrush"), Foreground = (Brush)FindResource("AccentTextBrush"),
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0) };
            var nCancel = new Button { Content = "Cancel", IsCancel = true, Height = 30, Padding = new Thickness(14, 0, 14, 0),
                Style = (Style)FindResource("ModernButton"),
                Background = (Brush)FindResource("ControlBgBrush"), Foreground = (Brush)FindResource("ContentTextBrush") };
            nRow.Children.Add(nOk); nRow.Children.Add(nCancel);
            np.Children.Add(nRow);
            nameWin.Content = np;
            nOk.Click += (_, _) => { nameWin.DialogResult = true; };

            if (nameWin.ShowDialog() == true)
            {
                var finalName = nameBox.Text.Trim();
                if (!string.IsNullOrWhiteSpace(finalName))
                {
                    ProjectService.SaveCharacterFile(projFolder, finalName, new CharacterData
                    {
                        AnswerAsName    = answerAsBox.Text.Trim(),
                        RoleInstruction = instrBox.Text.Trim(),
                        ResponseLength  = (int)Math.Round(lengthSlider.Value)
                    });
                    MessageBox.Show($"Character \"{finalName}\" saved to the Characters folder.",
                        "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        };

        // ── Separator ─────────────────────────────────────────────────────
        root.Children.Add(new Rectangle
        {
            Height = 1, Fill = (Brush)FindResource("ControlBgBrush"), Margin = new Thickness(0, 4, 0, 12)
        });

        // ── Bottom row: Reset | [spacer] | OK · Cancel ────────────────────
        var bottomRow = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var resetBtn = new Button
        {
            Content   = "↩ Reset",
            Height    = 34, Padding = new Thickness(12, 0, 12, 0),
            Style     = (Style)FindResource("ModernButton"),
            Background = (Brush)FindResource("ControlBgBrush"),
            Foreground = (Brush)FindResource("ContentTextBrush")
        };
        Grid.SetColumn(resetBtn, 0);

        var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var okBtn = new Button
        {
            Content    = "OK",
            IsDefault  = true,
            Height     = 34, Padding = new Thickness(20, 0, 20, 0),
            Style      = (Style)FindResource("ModernButton"),
            Background = (Brush)FindResource("PrimaryAccentBrush"),
            Foreground = (Brush)FindResource("AccentTextBrush"),
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, 0, 8, 0)
        };
        var cancelEditorBtn = new Button
        {
            Content    = "Cancel",
            IsCancel   = true,
            Height     = 34, Padding = new Thickness(14, 0, 14, 0),
            Style      = (Style)FindResource("ModernButton"),
            Background = (Brush)FindResource("ControlBgBrush"),
            Foreground = (Brush)FindResource("ContentTextBrush")
        };
        rightBtns.Children.Add(okBtn);
        rightBtns.Children.Add(cancelEditorBtn);
        Grid.SetColumn(rightBtns, 2);

        bottomRow.Children.Add(resetBtn);
        bottomRow.Children.Add(rightBtns);
        root.Children.Add(bottomRow);

        win.Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root
        };

        // ── Reset handler ─────────────────────────────────────────────────
        resetBtn.Click += (_, _) =>
        {
            answerAsBox.Text          = snap.AnswerAsName;
            instrBox.Text             = snap.RoleInstruction;
            lengthSlider.Value        = snap.ResponseLength;
            coordCheck.IsChecked        = snap.IsCoordinator;
            reasonerCheck.IsChecked     = snap.IsReasoner;
            prioritySlider.Value        = snap.ReasonerPriority;
            criticCheck.IsChecked       = snap.IsCritic;
            plannerCheck.IsChecked      = snap.IsPlanner;
            researcherCheck.IsChecked   = snap.IsResearcher;
            writeAccessCheck.IsChecked  = snap.IsWriteAccess;
        };

        // ── OK handler - write back in-place ──────────────────────────────
        okBtn.Click += (_, _) =>
        {
            role.AnswerAsName     = answerAsBox.Text.Trim();
            role.RoleInstruction  = instrBox.Text.Trim();
            role.ResponseLength   = (int)Math.Round(lengthSlider.Value);
            role.IsCoordinator    = coordCheck.IsChecked      == true;
            role.IsReasoner       = reasonerCheck.IsChecked   == true;
            role.ReasonerPriority = (int)Math.Round(prioritySlider.Value);
            role.IsCritic         = criticCheck.IsChecked       == true;
            role.IsPlanner        = plannerCheck.IsChecked      == true;
            role.IsResearcher     = researcherCheck.IsChecked   == true;
            role.IsWriteAccess    = writeAccessCheck.IsChecked  == true;
            win.DialogResult      = true;
        };

        return win.ShowDialog() == true;
    }
}
