using System.Net.Http;
using System.Text.RegularExpressions;
using SysIO = System.IO;
using System.Text;
using System.Threading;
using System.Windows;
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

namespace ClaudetRelay;

public partial class MainWindow : Window
{
    // ── Streaming bubble handle ────────────────────────────────────────────

    /// <summary>Returned by AddStreamingBubble. Content is the selectable TextBox;
    /// StopThinking kills the thinking animation and clears the thinking tooltip;
    /// UpdateThinkingTooltip sets the live tooltip on the thinking-dots element;
    /// OuterWrapper is the root Grid added to ChatPanel — remove it to erase the bubble.</summary>
    private sealed record StreamBubble(
        TextBox        Content,
        Action         StopThinking,
        Action<string> UpdateThinkingTooltip,
        UIElement      OuterWrapper);

    // ── Nested types ───────────────────────────────────────────────────────

    private sealed class OllamaParticipant
    {
        public required OllamaService Service    { get; init; }
        public int      Position   { get; set; }
        public bool     Enabled    { get; set; } = true;
        public bool?    IsOnline   { get; set; }
        public string?  CustomName { get; set; }

        public string ColorKey    => Position switch { 2 => "AccentBrush", 3 => "ClaudeBrush", _ => "OllamaBrush" };

        public string AvatarLabel => string.IsNullOrEmpty(Service.CurrentModel)
            ? $"O{Position}"
            : FormatModelAvatarLabel(Service.CurrentModel);

        public string DisplayName => string.IsNullOrEmpty(CustomName)
            ? string.IsNullOrEmpty(Service.CurrentModel)
                ? $"Ollama {Position}"
                : FormatModelDisplayName(Service.CurrentModel)
            : CustomName;
    }

    private sealed class OllamaParticipantUI
    {
        public required OllamaParticipant Data          { get; init; }
        public required Border            Card          { get; init; }
        public required Border            AvatarBorder  { get; init; }
        public required TextBlock         AvatarText    { get; init; }
        public required Border            CoBadge       { get; init; }
        public required Border            RBadge        { get; init; }
        public required TextBlock         NameLabel     { get; init; }
        public required Ellipse           StatusDot     { get; init; }
        public required TextBlock         ModelLabel    { get; init; }
        public required TextBlock         OfflineLabel  { get; init; }
        public required Popup             Popup         { get; init; }
        public required TextBlock         PopupTitle    { get; init; }
        public required CheckBox          EnabledToggle { get; init; }
        public required Button            RemoveButton  { get; init; }
    }

    private sealed class CloudAIParticipant
    {
        public required ICloudAIService Service    { get; init; }
        public int      Position   { get; set; }
        public bool     Enabled    { get; set; } = true;
        public bool?    IsOnline   { get; set; }
        public string?  CustomName { get; set; }

        public string ColorKey => Position switch { 2 => "AccentBrush", 3 => "OllamaBrush", _ => "ClaudeBrush" };

        public string AvatarLabel => string.IsNullOrEmpty(Service.CurrentModel)
            ? Service.ProviderName switch
            {
                "Anthropic"      => "An",
                "Google AI"      => "Gm",
                "Groq"           => "Gq",
                "OpenRouter"     => "OR",
                "Mistral"        => "Mi",
                "xAI Grok"       => "xG",
                "OpenAI ChatGPT" => "GP",
                _                => Service.ProviderName.Length >= 2
                                        ? Service.ProviderName[..2]
                                        : Service.ProviderName
            }
            : FormatModelAvatarLabel(Service.CurrentModel);

        public string ProviderName => Service.ProviderName;

        public string DisplayName => string.IsNullOrEmpty(CustomName)
            ? Service.ProviderName
            : CustomName;
    }

    private sealed class CloudAIParticipantUI
    {
        public required CloudAIParticipant Data          { get; init; }
        public required Border             Card          { get; init; }
        public required Border             AvatarBorder  { get; init; }
        public required TextBlock          AvatarText    { get; init; }
        public required Border             CoBadge       { get; init; }
        public required Border             RBadge        { get; init; }
        public required TextBlock          NameLabel     { get; init; }
        public required Ellipse            StatusDot     { get; init; }
        public required TextBlock          ModelLabel    { get; init; }
        public required TextBlock          OfflineLabel  { get; init; }
        public required Popup              Popup         { get; init; }
        public required TextBlock          PopupTitle    { get; init; }
        public required CheckBox           EnabledToggle { get; init; }
        public required Button             RemoveButton  { get; init; }
    }

    // ── State ──────────────────────────────────────────────────────────────
    private readonly List<CloudAIParticipantUI>          _cloudAIParticipants = [];
    private readonly List<OllamaParticipantUI>           _ollamaParticipants  = [];
    private readonly List<CloudAIMessage>                _sharedHistory       = [];
    private readonly Dictionary<string, ProviderRateLimiter> _rateLimiters    = new();
    private CancellationTokenSource?                     _streamCts;
    private List<string>                         _availableOllamaModels = [];
    private string?                              _currentThemePath;
    private string                               _userName              = "You";
    private int                                  _toneLevel             = 50;
    private bool                                 _mockingbirdMode       = false;
    private string                               _projectLanguage       = "";
    private int                                  _maxDialogDepth        = 1;
    private ProjectSettings?                     _projectSettings;

    // ── Project state ──────────────────────────────────────────────────────
    private string?                    _currentProjectFolder;
    private ProjectMeta?               _currentProject;
    private ProjectTypeDefinition?     _currentProjectType;
    private Roadmap?                   _currentRoadmap;
    private string?                    _selectedProjectFolder; // selected in Projects list

    // ── Project types (loaded from ProjectTypes/*.xaml) ────────────────────
    private List<ProjectTypeDefinition> _projectTypes = [];

    // ──────────────────────────────────────────────────────────────────────

    public MainWindow()
    {
        InitializeComponent();
        LoadThemesIntoComboBox();
        Loaded += async (_, _) =>
        {
            LoadProjectTypes();
            InitializeServices();
            AddSystemMessage("Chat started  ·  configure participants in ⚙ Settings.");
            InputTextBox.Focus();
            await CheckAllStatusAsync();
            StartStatusTimer();
        };
    }

    // ── Initialization ─────────────────────────────────────────────────────

    private void InitializeServices()
    {
        var settings = SettingsService.Load();

        // One-time migration: move legacy ClaudeApiKey → Windows Credential Manager
        if (!string.IsNullOrWhiteSpace(settings.ClaudeApiKey))
        {
            WindowsCredentialManager.Save("Anthropic", settings.ClaudeApiKey);
            settings.ClaudeApiKey = "";
            SettingsService.Save(settings);
        }

        // Create participants from settings
        bool anyAdded = false;
        foreach (var p in settings.Participants.Where(p => p.Enabled))
        {
            if (p.Type == "Ollama")
            {
                AddOllamaParticipant(p.Model, p.ServerUrl, p.Name);
                anyAdded = true;
            }
            else
            {
                AddCloudAIParticipant(p.Type, p.Model, p.Name);
                anyAdded = true;
            }
        }

        // Fallback: no participants configured → add default Ollama
        if (!anyAdded)
        {
            AddOllamaParticipant(settings.OllamaModel);
            AddSystemMessage("ℹ  No participants configured — open ⚙ Settings to set them up.");
        }

        // User display name & tone
        _userName        = string.IsNullOrWhiteSpace(settings.UserName) ? "You" : settings.UserName.Trim();
        _toneLevel       = settings.ToneLevel;
        _mockingbirdMode = settings.MockingbirdMode;

        // Rate limiters
        ApplyThrottleSettings(settings);
    }

    /// <summary>
    /// Rebuilds the per-provider rate-limiter table from saved settings.
    /// Call once on startup and again whenever the settings window is saved.
    /// </summary>
    private void ApplyThrottleSettings(AppSettings settings)
    {
        _rateLimiters.Clear();
        foreach (var (provider, throttle) in settings.ProviderThrottle)
        {
            if (throttle.Enabled && throttle.Rpm >= 1)
                _rateLimiters[provider] = new ProviderRateLimiter(throttle.Rpm);
        }
    }

    // ── Re-initialize after Settings save ─────────────────────────────────

    private void ReInitializeParticipants()
    {
        _streamCts?.Cancel();

        // Remove Cloud AI cards
        foreach (var ui in _cloudAIParticipants.ToList())
        {
            CloudAICardsPanel.Children.Remove(ui.Popup);
            CloudAICardsPanel.Children.Remove(ui.Card);
            ui.Data.Service.Dispose();
        }
        _cloudAIParticipants.Clear();

        // Remove Ollama cards
        foreach (var ui in _ollamaParticipants.ToList())
        {
            OllamaCardsPanel.Children.Remove(ui.Popup);
            OllamaCardsPanel.Children.Remove(ui.Card);
        }
        _ollamaParticipants.Clear();
        _availableOllamaModels.Clear();

        // Re-add from settings
        var settings = SettingsService.Load();
        _userName        = string.IsNullOrWhiteSpace(settings.UserName) ? "You" : settings.UserName.Trim();
        _toneLevel       = settings.ToneLevel;
        _mockingbirdMode = settings.MockingbirdMode;

        foreach (var p in settings.Participants.Where(p => p.Enabled))
        {
            if (p.Type == "Ollama")
                AddOllamaParticipant(p.Model, p.ServerUrl, p.Name);
            else
                AddCloudAIParticipant(p.Type, p.Model, p.Name);
        }

        if (_ollamaParticipants.Count == 0 && _cloudAIParticipants.Count == 0)
            AddSystemMessage("⚠  No participants enabled — configure them in ⚙ Settings.");

        UpdateAddRemoveButtons();
        UpdateCloudAIAddRemoveButtons();
        _ = CheckAllStatusAsync();
    }

    // ── Per-project participant persistence ────────────────────────────────

    /// <summary>
    /// Snapshots the current live participants into the open project's settings file.
    /// Safe to call when no project is open (no-op).
    /// </summary>
    private void SaveProjectParticipants()
    {
        if (_currentProjectFolder is null || _projectSettings is null) return;

        var saved = new List<ParticipantConfig>();

        foreach (var ui in _ollamaParticipants)
            saved.Add(new ParticipantConfig
            {
                Name      = ui.Data.CustomName ?? "",
                Type      = "Ollama",
                Model     = ui.Data.Service.CurrentModel,
                ServerUrl = ui.Data.Service.BaseUrl,
                Enabled   = ui.Data.Enabled
            });

        foreach (var ui in _cloudAIParticipants)
            saved.Add(new ParticipantConfig
            {
                Name      = ui.Data.CustomName ?? "",
                Type      = ui.Data.Service.ProviderName,
                Model     = ui.Data.Service.CurrentModel,
                ServerUrl = "",
                Enabled   = ui.Data.Enabled
            });

        _projectSettings.ActiveParticipants = saved;
        try { ProjectService.SaveProjectSettings(_currentProjectFolder, _projectSettings); }
        catch { /* non-fatal — settings will re-save next time */ }
    }

    /// <summary>
    /// Clears all current participants and re-adds from the saved list.
    /// Falls back to global settings if nothing from the list can be added.
    /// </summary>
    private void ReInitializeParticipantsFrom(List<ParticipantConfig> saved)
    {
        _streamCts?.Cancel();

        // Remove Cloud AI
        foreach (var ui in _cloudAIParticipants.ToList())
        {
            CloudAICardsPanel.Children.Remove(ui.Popup);
            CloudAICardsPanel.Children.Remove(ui.Card);
            ui.Data.Service.Dispose();
        }
        _cloudAIParticipants.Clear();

        // Remove Ollama
        foreach (var ui in _ollamaParticipants.ToList())
        {
            OllamaCardsPanel.Children.Remove(ui.Popup);
            OllamaCardsPanel.Children.Remove(ui.Card);
        }
        _ollamaParticipants.Clear();
        _availableOllamaModels.Clear();

        // Re-add from saved list
        foreach (var p in saved)
        {
            if (p.Type == "Ollama")
                AddOllamaParticipant(p.Model, p.ServerUrl, p.Name);
            else
                AddCloudAIParticipant(p.Type, p.Model, p.Name);
        }

        // Apply saved enabled/disabled state (add functions default to enabled=true)
        foreach (var p in saved)
        {
            if (p.Enabled) continue; // already enabled — skip
            if (p.Type == "Ollama")
            {
                var match = _ollamaParticipants.FirstOrDefault(ui =>
                    ui.Data.Service.CurrentModel == p.Model &&
                    ui.Data.Service.BaseUrl      == p.ServerUrl);
                if (match is not null)
                {
                    match.Data.Enabled  = false;
                    match.Card.Opacity  = 0.6;
                }
            }
            else
            {
                var match = _cloudAIParticipants.FirstOrDefault(ui =>
                    ui.Data.Service.ProviderName == p.Type &&
                    ui.Data.Service.CurrentModel == p.Model);
                if (match is not null)
                {
                    match.Data.Enabled  = false;
                    match.Card.Opacity  = 0.6;
                }
            }
        }

        // Fallback: if nothing was restored (e.g. all API keys gone), use global settings
        if (_ollamaParticipants.Count == 0 && _cloudAIParticipants.Count == 0)
        {
            var settings = SettingsService.Load();
            foreach (var p in settings.Participants.Where(p => p.Enabled))
            {
                if (p.Type == "Ollama")
                    AddOllamaParticipant(p.Model, p.ServerUrl, p.Name);
                else
                    AddCloudAIParticipant(p.Type, p.Model, p.Name);
            }
            if (_ollamaParticipants.Count == 0 && _cloudAIParticipants.Count == 0)
                AddOllamaParticipant(); // last-resort default
        }

        UpdateAddRemoveButtons();
        UpdateCloudAIAddRemoveButtons();
        _ = CheckAllStatusAsync();
    }

    // ── Tab switching ──────────────────────────────────────────────────────

    private void ChatTabButton_Click(object sender, RoutedEventArgs e)
        => ActivateTab(chat: true);

    private void ProjectsTabButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshProjectList();
        ActivateTab(chat: false);
    }

    private void ActivateTab(bool chat)
    {
        if (!chat) ShowRoadmapPanel(false);   // roadmap lives inside chat tab only

        // Chat-only elements
        ChatHeader    .Visibility = chat ? Visibility.Visible   : Visibility.Collapsed;
        ChatHeaderSep .Visibility = chat ? Visibility.Visible   : Visibility.Collapsed;
        ChatScrollViewer.Visibility = chat ? Visibility.Visible : Visibility.Collapsed;
        InputArea     .Visibility = chat ? Visibility.Visible   : Visibility.Collapsed;

        // Projects panel
        ProjectsContent.Visibility = chat ? Visibility.Collapsed : Visibility.Visible;

        // Tab button visual state
        ChatTabButton.SetResourceReference(Button.BackgroundProperty,
            chat ? "InputBrush" : "Transparent");
        ChatTabButton.FontWeight = chat ? FontWeights.SemiBold : FontWeights.Normal;
        ChatTabButton.SetResourceReference(Button.ForegroundProperty,
            chat ? "TextBrush" : "SubtextBrush");

        ProjectsTabButton.Background = Brushes.Transparent;
        ProjectsTabButton.FontWeight = chat ? FontWeights.Normal : FontWeights.SemiBold;
        ProjectsTabButton.SetResourceReference(Button.ForegroundProperty,
            chat ? "SubtextBrush" : "TextBrush");
    }

    // ── Project list ───────────────────────────────────────────────────────

    private void RefreshProjectList()
    {
        var settings = SettingsService.Load();
        var folder   = ProjectService.ResolveFolder(settings.ProjectsFolder);
        var projects = ProjectService.ListProjects(folder);

        ProjectListPanel.Children.Clear();
        _selectedProjectFolder = null;
        OpenProjectButton  .IsEnabled = false;
        DeleteProjectButton.IsEnabled = false;

        if (projects.Count == 0)
        {
            var empty = new TextBlock
            {
                Text      = "No projects yet — click “New Project” to create one.",
                FontSize  = 13, FontFamily = new FontFamily("Segoe UI"),
                Margin    = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");
            ProjectListPanel.Children.Add(empty);
            return;
        }

        foreach (var (projFolder, meta) in projects)
        {
            var card = BuildProjectCard(projFolder, meta);
            ProjectListPanel.Children.Add(card);
        }
    }

    private Border BuildProjectCard(string projFolder, ProjectMeta meta)
    {
        // Resolve the project type so we can show its icon
        var ptd = ResolveProjectType(meta.ProjectTypeName);

        // Title row: type icon + project name
        var typeIconTb = new TextBlock
        {
            Text       = ptd.Icon,
            FontSize   = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin     = new Thickness(0, 0, 6, 0)
        };

        var nameLabel = new TextBlock
        {
            Text       = meta.ProjectName,
            FontSize   = 14, FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center
        };
        nameLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 0, 0, 4)
        };
        titleRow.Children.Add(typeIconTb);
        titleRow.Children.Add(nameLabel);

        var dateLabel = new TextBlock
        {
            Text     = $"{ptd.Name}  ·  Last opened: {meta.LastOpened.ToLocalTime():yyyy-MM-dd HH:mm}",
            FontSize = 11, FontFamily = new FontFamily("Segoe UI")
        };
        dateLabel.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");

        // Show currently-active participants from live project settings (not the creation-time snapshot)
        var livePs          = ProjectService.LoadProjectSettings(projFolder);
        var liveActiveNames = livePs.Roles
            .Where(r => r.IsActive && !string.IsNullOrWhiteSpace(r.DisplayName))
            .Select(r => r.DisplayName)
            .ToList();
        // Fall back to meta snapshot if settings have no roles yet (e.g. fresh project)
        if (liveActiveNames.Count == 0)
            liveActiveNames = meta.Participants
                .Where(p => p.IsActive && !string.IsNullOrWhiteSpace(p.DisplayName))
                .Select(p => p.DisplayName)
                .ToList();

        var participantsLabel = new TextBlock
        {
            FontSize     = 11, FontFamily = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap
        };
        participantsLabel.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");
        if (liveActiveNames.Count > 0)
            participantsLabel.Text = string.Join(", ", liveActiveNames);

        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        infoStack.Children.Add(titleRow);
        infoStack.Children.Add(dateLabel);
        if (!string.IsNullOrEmpty(participantsLabel.Text))
            infoStack.Children.Add(participantsLabel);

        var settingsBtn = new Button
        {
            Content           = "⚙",
            FontSize          = 16,
            Width             = 32,
            Height            = 32,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Center,
            ToolTip           = "Project settings (roles, orchestration mode)",
            Style             = (Style)FindResource("ModernButton"),
            Background        = (Brush)FindResource("InputBrush"),
            Foreground        = (Brush)FindResource("SubtextBrush"),
            Padding           = new Thickness(0)
        };
        settingsBtn.Click += (_, e) =>
        {
            e.Handled = true;           // prevent card selection / open on click
            ShowProjectSettingsDialog(projFolder, meta.ProjectName);
        };

        var cardGrid = new Grid();
        cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(infoStack,   0);
        Grid.SetColumn(settingsBtn, 1);
        cardGrid.Children.Add(infoStack);
        cardGrid.Children.Add(settingsBtn);

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding      = new Thickness(14, 10, 10, 10),
            Margin       = new Thickness(0, 0, 0, 8),
            Cursor       = Cursors.Hand,
            Child        = cardGrid,
            Tag          = projFolder
        };
        card.SetResourceReference(Border.BackgroundProperty, "InputBrush");

        card.MouseLeftButtonDown += (_, _) => SelectProjectCard(card, projFolder);
        card.MouseLeftButtonUp   += (_, e) =>
        {
            if (e.ClickCount >= 2) OpenProject(projFolder);
        };

        // ── Right-click context menu ───────────────────────────────────────
        var ctxMenu    = new ContextMenu();
        var exportHtml = new MenuItem { Header = "📄  Export as HTML…" };
        var exportMd   = new MenuItem { Header = "📝  Export as Markdown…" };
        var browseItem = new MenuItem { Header = "📁  Browse project files…" };

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

    private void ExportProject(string projFolder, ProjectMeta meta, string format)
    {
        var entries = ProjectService.LoadChatLog(projFolder);
        if (entries.Count == 0)
        {
            MessageBox.Show("This project has no chat history to export.",
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

        var content = isHtml
            ? ExportService.GenerateHtml(meta.ProjectName, entries)
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
            c.SetResourceReference(Border.BackgroundProperty, "InputBrush");

        // Highlight selected
        clicked.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");

        _selectedProjectFolder     = folder;
        OpenProjectButton  .IsEnabled = true;
        DeleteProjectButton.IsEnabled = true;
    }

    // ── Project CRUD ───────────────────────────────────────────────────────

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsService.Load();
        var folder   = ProjectService.ResolveFolder(settings.ProjectsFolder);

        // Ask for a name; re-prompt if the name is already taken
        string? name = null;
        while (true)
        {
            name = ShowInputDialog("New Project", "Project name:", name ?? "My Project");
            if (string.IsNullOrWhiteSpace(name)) return; // user cancelled

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

        // Update meta with current participants
        var meta = ProjectService.LoadMeta(projFolder)!;
        meta.Participants = BuildCurrentParticipantSnapshot();
        ProjectService.SaveMeta(projFolder, meta);

        RefreshProjectList();
    }

    /// <summary>
    /// Shows a modal dialog listing all loaded project types.
    /// Returns the chosen type, or null if the user cancelled.
    /// </summary>
    private ProjectTypeDefinition? ShowProjectTypePicker()
    {
        // ── Cache brushes from MainWindow (theme-aware) ───────────────────
        // New Window instances don't inherit MainWindow's MergedDictionaries,
        // so SetResourceReference would resolve nothing → black window.
        // Resolve once here on 'this' and assign directly, same as all other dialogs.
        var bgBrush      = (Brush)FindResource("BackgroundBrush");
        var textBrush    = (Brush)FindResource("TextBrush");
        var subtextBrush = (Brush)FindResource("SubtextBrush");
        var inputBrush   = (Brush)FindResource("InputBrush");
        var surfaceBrush = (Brush)FindResource("SurfaceBrush");
        var accentBrush  = (Brush)FindResource("AccentBrush");
        var btnStyle     = (Style)FindResource("ModernButton");

        ProjectTypeDefinition? result = null;

        // ── Dialog window ─────────────────────────────────────────────────
        var dlg = new Window
        {
            Title                 = "Choose Project Type",
            Width                 = 520,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ResizeMode            = ResizeMode.NoResize,
            ShowInTaskbar         = false,
            Background            = bgBrush
        };

        // ── Header ────────────────────────────────────────────────────────
        var header = new TextBlock
        {
            Text         = "What kind of project is this?",
            FontSize     = 15,
            FontWeight   = FontWeights.SemiBold,
            FontFamily   = new FontFamily("Segoe UI"),
            Foreground   = textBrush,
            Margin       = new Thickness(20, 20, 20, 4)
        };

        var subtitle = new TextBlock
        {
            Text         = "Choose the type that best fits your project. You can change it later.",
            FontSize     = 12,
            FontFamily   = new FontFamily("Segoe UI"),
            Foreground   = subtextBrush,
            Margin       = new Thickness(20, 0, 20, 14),
            TextWrapping = TextWrapping.Wrap
        };

        // ── Type cards ────────────────────────────────────────────────────
        var typePanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(16, 0, 16, 0)
        };

        Border? selectedCard = null;
        ProjectTypeDefinition? selectedType = null;

        Button? okBtn = null; // forward reference

        foreach (var ptd in _projectTypes)
        {
            var capturedPtd = ptd;

            var iconTb = new TextBlock
            {
                Text                = ptd.Icon,
                FontSize            = 24,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin              = new Thickness(0, 0, 0, 4)
            };

            var nameTb = new TextBlock
            {
                Text                = ptd.Name,
                FontSize            = 12,
                FontWeight          = FontWeights.SemiBold,
                FontFamily          = new FontFamily("Segoe UI"),
                Foreground          = textBrush,
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
                Margin          = new Thickness(4, 4, 4, 4),
                Cursor          = Cursors.Hand,
                Child           = cardContent,
                Width           = 150,
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

            typePanel.Children.Add(typeCard);
        }

        // Pre-select General (first card)
        var generalCard = typePanel.Children.OfType<Border>().FirstOrDefault();
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
            Content    = "Create Project",
            Width      = 130,
            Height     = 32,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 13,
            Margin     = new Thickness(0, 0, 8, 0),
            IsEnabled  = true,
            Style      = btnStyle,
            Background = accentBrush,
            Foreground = textBrush
        };

        var cancelBtn = new Button
        {
            Content    = "Cancel",
            Width      = 80,
            Height     = 32,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 13,
            Style      = btnStyle,
            Background = inputBrush,
            Foreground = subtextBrush
        };

        okBtn    .Click += (_, _) => { result = selectedType; dlg.DialogResult = true; };
        cancelBtn.Click += (_, _) => dlg.DialogResult = false;

        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(20, 14, 20, 20)
        };
        btnRow.Children.Add(okBtn);
        btnRow.Children.Add(cancelBtn);

        // ── Layout ────────────────────────────────────────────────────────
        var root = new StackPanel();
        root.Children.Add(header);
        root.Children.Add(subtitle);
        root.Children.Add(typePanel);
        root.Children.Add(btnRow);
        dlg.Content = root;

        dlg.ShowDialog();
        return result;
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProjectFolder is not null)
            OpenProject(_selectedProjectFolder);
    }

    private void DeleteProject_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProjectFolder is null) return;

        var meta = ProjectService.LoadMeta(_selectedProjectFolder);
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
        var meta    = ProjectService.LoadMeta(projFolder);
        if (meta is null) { MessageBox.Show("Could not read project.json.", "Error"); return; }

        // Save participant state for the project we're leaving (before changing _currentProjectFolder)
        if (_currentProjectFolder is not null && _currentProjectFolder != projFolder)
            SaveProjectParticipants();

        // Update LastOpened
        meta.LastOpened = DateTime.UtcNow;
        ProjectService.SaveMeta(projFolder, meta);

        // Switch to Chat tab
        ActivateTab(chat: true);

        // Clear current chat
        _streamCts?.Cancel();
        ChatPanel.Children.Clear();
        _sharedHistory.Clear();

        // Store project state
        _currentProjectFolder = projFolder;
        _currentProject       = meta;
        _currentProjectType   = ResolveProjectType(meta.ProjectTypeName);
        _currentRoadmap       = RoadmapService.Load(projFolder);
        var loadedPs          = ProjectService.LoadProjectSettings(projFolder);
        _projectSettings      = loadedPs;
        _projectLanguage      = loadedPs.Language;
        _maxDialogDepth       = Math.Max(1, loadedPs.MaxDialogDepth);

        // Restore this project's saved participants (if any were saved before)
        if (loadedPs.ActiveParticipants is { Count: > 0 })
            ReInitializeParticipantsFrom(loadedPs.ActiveParticipants);

        // Reflect CO / R roles on all sidebar cards for this project
        RefreshParticipantBadges();

        // Update header
        ChatHeaderTitle.Text              = meta.ProjectName;
        ProjectSettingsButton.Visibility  = Visibility.Visible;
        CloseProjectButton   .Visibility  = Visibility.Visible;
        RoadmapButton        .Visibility  = _currentProjectType.HasRoadmap
                                            ? Visibility.Visible : Visibility.Collapsed;
        WorldButton          .Visibility  = _currentProjectType.HasWorldBuilding
                                            ? Visibility.Visible : Visibility.Collapsed;

        // Load chat history
        var log = ProjectService.LoadChatLog(projFolder);
        foreach (var entry in log)
            RenderChatLogEntry(entry);

        if (log.Count == 0)
            AddSystemMessage($"Project \"{meta.ProjectName}\" opened. Start chatting!");
        else
            AddSystemMessage($"Project \"{meta.ProjectName}\" — {log.Count} messages loaded.");

        ChatScrollViewer.ScrollToBottom();
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
        _projectLanguage                 = "";
        _maxDialogDepth                  = 1;
        ChatHeaderTitle.Text             = "Chat";
        ProjectSettingsButton.Visibility = Visibility.Collapsed;
        CloseProjectButton   .Visibility = Visibility.Collapsed;
        RoadmapButton        .Visibility = Visibility.Collapsed;
        WorldButton          .Visibility = Visibility.Collapsed;

        // Clear CO/R badges — no project is active
        RefreshParticipantBadges();

        // Restore the globally configured participants
        var globalSettings = SettingsService.Load();
        ReInitializeParticipantsFrom(globalSettings.Participants);
    }

    private void RoadmapButton_Click(object sender, RoutedEventArgs e)
        => ShowRoadmapPanel(RoadmapContent.Visibility != Visibility.Visible);

    // ── Roadmap panel show/hide ────────────────────────────────────────────

    private void ShowRoadmapPanel(bool show)
    {
        if (show && _currentRoadmap is not null)
        {
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
        }
    }

    // ── Roadmap UI builder ────────────────────────────────────────────────

    private void BuildRoadmapContent()
    {
        RoadmapContent.Children.Clear();
        if (_currentRoadmap is null) return;

        // ── Theme brushes (must come from MainWindow, not SetResourceReference) ──
        var bgBrush      = (Brush)FindResource("BackgroundBrush");
        var sidebarBrush = (Brush)FindResource("SidebarBrush");
        var textBrush    = (Brush)FindResource("TextBrush");
        var subtextBrush = (Brush)FindResource("SubtextBrush");
        var inputBrush   = (Brush)FindResource("InputBrush");
        var surfaceBrush = (Brush)FindResource("SurfaceBrush");
        var accentBrush  = (Brush)FindResource("AccentBrush");
        var claudeBrush  = (Brush)FindResource("ClaudeBrush");
        var ollamaBrush  = (Brush)FindResource("OllamaBrush");
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
                { Title = r.Value.title, Description = r.Value.desc, CreatedBy = "User" });
            SaveRoadmap();
            BuildRoadmapContent();
        };

        var tbBtns = new StackPanel { Orientation = Orientation.Horizontal };
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
                    Title       = r.Value.title,
                    Description = r.Value.desc,
                    Progress    = r.Value.progress,
                    Status      = r.Value.progress >= 100 ? ItemStatus.Done
                                : r.Value.progress > 0    ? ItemStatus.InProgress
                                : ItemStatus.Todo,
                    CreatedBy   = "User"
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
                var r = ShowMilestoneDialog(capturedMs.Title, capturedMs.Description);
                if (r is null) return;
                capturedMs.Title       = r.Value.title;
                capturedMs.Description = r.Value.desc;
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
                    Text       = "No items yet — click  \"+ Item\"  to add the first task.",
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

            // ── Combine header + items in a rounded card ──────────────────
            var inner = new StackPanel();
            inner.Children.Add(msHeaderBorder);
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
            var r = ShowItemDialog(capturedItem.Title, capturedItem.Description, capturedItem.Progress);
            if (r is null) return;
            capturedItem.Title       = r.Value.title;
            capturedItem.Description = r.Value.desc;
            capturedItem.Progress    = r.Value.progress;
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

        return new Border { Padding = new Thickness(14, 8, 10, 8), Child = rowGrid };
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

    private (string title, string desc)? ShowMilestoneDialog(
        string title = "", string desc = "")
    {
        var isEdit     = !string.IsNullOrEmpty(title);
        var bgBrush    = (Brush)FindResource("SidebarBrush");
        var textBrush  = (Brush)FindResource("TextBrush");
        var subBrush   = (Brush)FindResource("SubtextBrush");
        var inputBrush = (Brush)FindResource("InputBrush");
        var accentBrush= (Brush)FindResource("AccentBrush");
        var claudeBrush= (Brush)FindResource("ClaudeBrush");
        var btnStyle   = (Style)FindResource("ModernButton");

        (string, string)? result = null;

        var dlg = new Window
        {
            Title                 = isEdit ? "Edit Milestone" : "Add Milestone",
            Width                 = 560,
            Height                = 500,
            MinWidth              = 420,
            MinHeight             = 360,
            Owner                 = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode            = ResizeMode.CanResize,
            ShowInTaskbar         = false,
            Background            = bgBrush
        };

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
            Foreground = (Brush)FindResource("SidebarBrush")
        };
        okBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(titleBox.Text)) return;
            result = (titleBox.Text.Trim(), RtbToXaml(descRtb));
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

        // Layout: DockPanel — buttons bottom, title+toolbar top, RTB fills
        var topSection = new StackPanel();
        topSection.Children.Add(titleLbl);
        topSection.Children.Add(titleBox);
        topSection.Children.Add(descLbl);
        topSection.Children.Add(toolbar);

        var outerDock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(btnRow,     Dock.Bottom);
        outerDock.Children.Add(btnRow);
        DockPanel.SetDock(topSection, Dock.Top);
        outerDock.Children.Add(topSection);
        outerDock.Children.Add(descBorder);   // fills remaining height

        dlg.Content = outerDock;
        dlg.Loaded += (_, _) => { titleBox.Focus(); titleBox.SelectAll(); };
        dlg.ShowDialog();
        return result;
    }

    private (string title, string desc, int progress)? ShowItemDialog(
        string title = "", string desc = "", int progress = 0)
    {
        var isEdit      = !string.IsNullOrEmpty(title);
        var bgBrush     = (Brush)FindResource("SidebarBrush");
        var textBrush   = (Brush)FindResource("TextBrush");
        var subBrush    = (Brush)FindResource("SubtextBrush");
        var inputBrush  = (Brush)FindResource("InputBrush");
        var claudeBrush = (Brush)FindResource("ClaudeBrush");
        var accentBrush = (Brush)FindResource("AccentBrush");
        var btnStyle    = (Style)FindResource("ModernButton");

        (string, string, int)? result = null;

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
            Foreground = (Brush)FindResource("SidebarBrush")
        };
        okBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(titleBox.Text)) return;
            result = (titleBox.Text.Trim(), RtbToXaml(descRtb), (int)slider.Value);
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

        // Top section: title + desc label + toolbar
        var topSection = new StackPanel();
        topSection.Children.Add(titleLbl);
        topSection.Children.Add(titleBox);
        topSection.Children.Add(descLbl);
        topSection.Children.Add(toolbar);

        // Layout: DockPanel — fixed sections dock, RTB fills
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
        var bgBrush    = (Brush)FindResource("SidebarBrush");
        var textBrush  = (Brush)FindResource("TextBrush");
        var subtextBrush=(Brush)FindResource("SubtextBrush");
        var inputBrush = (Brush)FindResource("InputBrush");
        var accentBrush= (Brush)FindResource("AccentBrush");
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
        if (_currentRoadmap is null) return text;

        bool changed = false;

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

        // [ROADMAP:complete:xxxxxxxx]  — coordinator only
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

    /// <summary>
    /// Returns roadmap context text for injection into AI system prompts.
    /// Empty string if no project is open or the roadmap is empty.
    /// </summary>
    private string BuildRoadmapContext(ProjectParticipantRole? role)
    {
        if (_currentRoadmap is null || _currentRoadmap.Milestones.Count == 0) return "";
        return RoadmapService.GetContextText(_currentRoadmap, role?.IsCoordinator == true);
    }

    private void WorldButton_Click(object sender, RoutedEventArgs e)
    {
        // TODO: World-building system (Phase 3)
        var typeName = _currentProjectType?.Name ?? "this project";
        var folders  = _currentProjectType?.GetWorldFolderList() is { Length: > 0 } wf
                       ? string.Join(", ", wf)
                       : "Characters, Locations";

        MessageBox.Show(
            $"The World-Building section will be implemented in an upcoming version.\n\n" +
            $"Planned for {typeName}:\n" +
            $"  • Sections: {folders}\n" +
            $"  • Each element stored as a JSON file inside PROJECTPLAN/\n" +
            $"  • Property sheets editable via a dedicated dialog\n" +
            $"  • AI context: relevant elements injected into the system prompt automatically",
            "🌍 World Building — Coming Soon",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CloseProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProjectFolder is null || _currentProject is null) return;

        // Persist last-opened timestamp before closing
        _currentProject.LastOpened = DateTime.UtcNow;
        ProjectService.SaveMeta(_currentProjectFolder, _currentProject);

        // Stop any running stream, clear the chat panel
        _streamCts?.Cancel();
        ChatPanel.Children.Clear();
        _sharedHistory.Clear();

        CloseCurrentProject();

        AddSystemMessage("Project closed. Start a new chat or open a project from the Projects tab.");
    }

    private void RenderChatLogEntry(ChatLogEntry entry)
    {
        if (entry.SenderType == "System")
        {
            AddSystemMessage(entry.Message);
            return;
        }
        // Reconstruct _sharedHistory entry for AI context
        if (entry.IsUser)
            _sharedHistory.Add(new CloudAIMessage("user", entry.Message, "User"));
        else if (entry.SenderType == "AI")
            _sharedHistory.Add(new CloudAIMessage("assistant", entry.Message, entry.AvatarLabel));

        var bubble = AddStreamingBubble(entry.DisplayName, entry.AvatarLabel,
                                         entry.AccentKey, entry.BubbleKey, entry.IsUser);
        bubble.StopThinking();
        bubble.Content.Text = entry.Message;
    }

    private List<ProjectParticipant> BuildCurrentParticipantSnapshot()
    {
        var list = new List<ProjectParticipant>();
        foreach (var ui in _ollamaParticipants)
            list.Add(new ProjectParticipant
            {
                Type        = "Ollama",
                Provider    = "Ollama",
                ModelName   = ui.Data.Service.CurrentModel,
                DisplayName = ui.Data.DisplayName,
                IsActive    = ui.Data.Enabled
            });
        foreach (var ui in _cloudAIParticipants)
            list.Add(new ProjectParticipant
            {
                Type        = "Cloud",
                Provider    = ui.Data.ProviderName,
                ModelName   = ui.Data.Service.CurrentModel,
                DisplayName = ui.Data.DisplayName,
                IsActive    = ui.Data.Enabled
            });
        return list;
    }

    private void AppendToProjectLog(ChatLogEntry entry)
    {
        if (_currentProjectFolder is null) return;
        try { ProjectService.AppendEntry(_currentProjectFolder, entry); }
        catch { /* non-fatal */ }
    }

    // ── Simple input dialog ────────────────────────────────────────────────

    private string? ShowInputDialog(string title, string prompt, string defaultValue = "")
    {
        // FindResource is called on *this* (MainWindow) which has the theme loaded.
        // SetResourceReference would search the popup's own empty resource tree and fall
        // back to the default WPF chrome — producing black buttons on dark themes.
        var win = new Window
        {
            Title                 = title,
            Width                 = 400, Height = 170,
            Owner                 = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode            = ResizeMode.NoResize,
            ShowInTaskbar         = false,
            Background            = (Brush)FindResource("SidebarBrush")
        };

        var lbl = new TextBlock
        {
            Text       = prompt,
            FontSize   = 13,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("TextBrush"),
            Margin     = new Thickness(16, 16, 16, 6)
        };

        var tb = new TextBox
        {
            Text                     = defaultValue,
            FontSize                 = 13,
            FontFamily               = new FontFamily("Segoe UI"),
            Margin                   = new Thickness(16, 0, 16, 14),
            Height                   = 36,
            BorderThickness          = new Thickness(0),
            Padding                  = new Thickness(10, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background               = (Brush)FindResource("InputBrush"),
            Foreground               = (Brush)FindResource("TextBrush"),
            CaretBrush               = (Brush)FindResource("TextBrush"),
            SelectionBrush           = (Brush)FindResource("ClaudeBrush")
        };

        var okBtn = new Button
        {
            Content    = "Create",
            IsDefault  = true,
            Height     = 34,
            Margin     = new Thickness(16, 0, 8, 16),
            Style      = (Style)FindResource("ModernButton"),
            Background = (Brush)FindResource("ClaudeBrush"),
            Foreground = (Brush)FindResource("SidebarBrush")
        };

        var cancelBtn = new Button
        {
            Content    = "Cancel",
            IsCancel   = true,
            Height     = 34,
            Margin     = new Thickness(0, 0, 16, 16),
            Style      = (Style)FindResource("ModernButton"),
            Background = (Brush)FindResource("InputBrush"),
            Foreground = (Brush)FindResource("TextBrush")
        };

        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        btnRow.Children.Add(okBtn);
        btnRow.Children.Add(cancelBtn);

        var panel = new StackPanel();
        panel.Children.Add(lbl);
        panel.Children.Add(tb);
        panel.Children.Add(btnRow);
        win.Content = panel;

        string? result = null;
        okBtn.Click += (_, _) => { result = tb.Text.Trim(); win.DialogResult = true; };
        win.Loaded  += (_, _) => { tb.Focus(); tb.SelectAll(); };
        win.ShowDialog();
        return result;
    }

    // ── About / Version ────────────────────────────────────────────────────

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();

        var infoItem    = new MenuItem { Header = "ℹ  Info" };
        var versionItem = new MenuItem { Header = "📋  Version" };
        infoItem   .Click += (_, _) => ShowAboutInfoDialog();
        versionItem.Click += (_, _) => ShowAboutVersionDialog();

        menu.Items.Add(infoItem);
        menu.Items.Add(versionItem);
        menu.PlacementTarget = (Button)sender;
        menu.Placement       = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen          = true;
    }

    private void ShowAboutInfoDialog()
    {
        var win = new Window
        {
            Title                 = "About ClaudetRelay",
            Width                 = 360,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            Background            = (Brush)FindResource("BackgroundBrush"),
            ResizeMode            = ResizeMode.NoResize
        };

        var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };

        panel.Children.Add(new TextBlock
        {
            Text = "ClaudetRelay", FontSize = 22, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 4)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Multi-AI group chat relay",
            FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("SubtextBrush"),
            Margin = new Thickness(0, 0, 0, 20)
        });
        panel.Children.Add(new Rectangle
        {
            Height = 1, Fill = (Brush)FindResource("InputBrush"),
            Margin = new Thickness(0, 0, 0, 20)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "by H.-R. Matthes and Claude Code",
            FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 20)
        });

        var closeBtn = new Button
        {
            Content = "Close", IsDefault = true,
            Height = 34, Padding = new Thickness(20, 0, 20, 0),
            Style = (Style)FindResource("ModernButton"),
            Background = (Brush)FindResource("ClaudeBrush"),
            Foreground = (Brush)FindResource("SidebarBrush"),
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        closeBtn.Click += (_, _) => win.Close();
        panel.Children.Add(closeBtn);

        win.Content = panel;
        win.ShowDialog();
    }

    private void ShowAboutVersionDialog()
    {
        var asm     = System.Reflection.Assembly.GetExecutingAssembly();
        var ver     = asm.GetName().Version;
        var verStr  = ver is not null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "dev";
        var built   = SysIO.File.GetLastWriteTime(asm.Location);

        var win = new Window
        {
            Title                 = "Version",
            Width                 = 300,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            Background            = (Brush)FindResource("BackgroundBrush"),
            ResizeMode            = ResizeMode.NoResize
        };

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        panel.Children.Add(new TextBlock
        {
            Text = "ClaudetRelay", FontSize = 16, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 10)
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Version {verStr}",
            FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 4)
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Build  {built:yyyy-MM-dd}",
            FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("SubtextBrush"),
            Margin = new Thickness(0, 0, 0, 18)
        });

        var closeBtn = new Button
        {
            Content = "Close", IsDefault = true,
            Height = 32, Padding = new Thickness(16, 0, 16, 0),
            Style = (Style)FindResource("ModernButton"),
            Background = (Brush)FindResource("InputBrush"),
            Foreground = (Brush)FindResource("TextBrush"),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        closeBtn.Click += (_, _) => win.Close();
        panel.Children.Add(closeBtn);

        win.Content = panel;
        win.ShowDialog();
    }

    // ── Project settings dialog ────────────────────────────────────────────

    private void ProjectSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProjectFolder is not null && _currentProject is not null)
            ShowProjectSettingsDialog(_currentProjectFolder, _currentProject.ProjectName);
    }

    private void ShowProjectSettingsDialog(string projFolder, string projectName)
    {
        var ps = ProjectService.LoadProjectSettings(projFolder);

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
            Title                 = $"Project Settings — {projectName}",
            Width                 = 520,
            SizeToContent         = SizeToContent.Height,
            Owner                 = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode            = ResizeMode.NoResize,
            ShowInTaskbar         = false,
            Background            = (Brush)FindResource("SidebarBrush")
        };

        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 4) };

        // ── Orchestration Mode ─────────────────────────────────────────────
        var modeLabel = new TextBlock
        {
            Text       = "ORCHESTRATION MODE",
            FontSize   = 11, FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"),
            Margin     = new Thickness(0, 0, 0, 8),
            Foreground = (Brush)FindResource("SubtextBrush")
        };

        RadioButton MakeRadio(string text, string tip, OrchestrationMode mode) => new RadioButton
        {
            Content     = text,
            IsChecked   = ps.OrchestrationMode == mode,
            GroupName   = "OrcMode",
            FontSize    = 13, FontFamily = new FontFamily("Segoe UI"),
            Margin      = new Thickness(0, 0, 0, 6),
            Foreground  = (Brush)FindResource("TextBrush"),
            Tag         = mode,
            ToolTip     = tip
        };

        var radioAll = MakeRadio("All participants respond",
            "Every active participant answers every user message. Default behaviour.",
            OrchestrationMode.AllRespond);

        var radioCoordFirst = MakeRadio("Coordinator-first",
            "The Coordinator answers first and decides which Reasoner(s) should respond next.\n" +
            "Reasoners are triggered when the Coordinator tags them (e.g. @Reasoner).",
            OrchestrationMode.CoordinatorFirst);

        var radioCoordSum = MakeRadio("All respond, Coordinator summarizes",
            "All participants respond normally. The Coordinator then receives all answers\n" +
            "as context and writes a final synthesising summary.",
            OrchestrationMode.CoordinatorSummarizes);

        var modeStack = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        modeStack.Children.Add(modeLabel);
        modeStack.Children.Add(radioAll);
        modeStack.Children.Add(radioCoordFirst);
        modeStack.Children.Add(radioCoordSum);
        root.Children.Add(modeStack);

        // ── Language ───────────────────────────────────────────────────────
        var langLabel = new TextBlock
        {
            Text       = "RESPONSE LANGUAGE",
            FontSize   = 11, FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"),
            Margin     = new Thickness(0, 0, 0, 6),
            Foreground = (Brush)FindResource("SubtextBrush")
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
            Foreground = (Brush)FindResource("SubtextBrush")
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
            Foreground = (Brush)FindResource("SubtextBrush")
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
            Foreground        = (Brush)FindResource("TextBrush"),
            Background        = (Brush)FindResource("InputBrush"),
            BorderBrush       = (Brush)FindResource("InputBrush"),
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
            Foreground        = (Brush)FindResource("SubtextBrush")
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
            Foreground = (Brush)FindResource("SubtextBrush")
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
            Foreground        = (Brush)FindResource("TextBrush")
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
            Background        = (Brush)FindResource("InputBrush"),
            Foreground        = (Brush)FindResource("TextBrush"),
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

        // ── Separator ──────────────────────────────────────────────────────
        var sep = new Rectangle
        {
            Height  = 1,
            Margin  = new Thickness(0, 0, 0, 14),
            Fill    = (Brush)FindResource("InputBrush")
        };
        root.Children.Add(sep);

        // ── Participant Roles ──────────────────────────────────────────────
        var rolesLabel = new TextBlock
        {
            Text       = "PARTICIPANT ROLES",
            FontSize   = 11, FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"),
            Margin     = new Thickness(0, 0, 0, 10),
            Foreground = (Brush)FindResource("SubtextBrush")
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
                Text       = "No participants are currently enabled. Enable participants in ⚙ Settings first.",
                FontSize   = 12, FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap,
                Margin     = new Thickness(0, 0, 0, 12),
                Foreground = (Brush)FindResource("SubtextBrush")
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

            // Fresh working copy — one per participant, no shared references
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
                IsActive         = existing?.IsActive         ?? true
            };

            // ── Avatar chip with CO / R badge overlay ────────────────────────
            var avatarCircle = new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(17),
                Background          = (Brush)FindResource(available ? "OllamaBrush" : "SubtextBrush"),
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
                Foreground = (Brush)FindResource("SidebarBrush")
            };

            // CO badge — gold, top-right
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

            // R badge — silver, bottom-right
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
                Background          = (Brush)FindResource("SubtextBrush"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Bottom,
                Visibility          = role.IsReasoner ? Visibility.Visible : Visibility.Collapsed,
                Child = rBadgeText
            };

            // Grid container (still named avatarBorder — column-set code below unchanged)
            var avatarBorder = new Grid
            {
                Width             = 38, Height = 38,
                Margin            = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            avatarBorder.Children.Add(avatarCircle);
            avatarBorder.Children.Add(coBadgeBorder);
            avatarBorder.Children.Add(rBadgeBorder);

            // ── Name + model sub-label ─────────────────────────────────────
            var nameTb = new TextBlock
            {
                Text = displayName, FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource(available ? "TextBrush" : "SubtextBrush")
            };
            var modelTb = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(role.AnswerAsName)
                           ? $"{provider}  ·  {model}"
                           : $"{provider}  ·  {model}  ·  🎭 {role.AnswerAsName}",
                FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
                Foreground = (Brush)FindResource("SubtextBrush"),
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
                Background = (Brush)FindResource("InputBrush"),
                Foreground = (Brush)FindResource("TextBrush"),
                IsEnabled  = available,
                VerticalAlignment = VerticalAlignment.Center
            };

            var capturedRole    = role;
            var capturedModelTb = modelTb;
            var capturedCoBadge = coBadgeBorder;
            var capturedRBadge  = rBadgeBorder;
            editBtn.Click += (_, _) =>
            {
                if (ShowCharacterEditorDialog(capturedRole, projFolder, displayName))
                {
                    // Refresh subtitle
                    capturedModelTb.Text = string.IsNullOrWhiteSpace(capturedRole.AnswerAsName)
                        ? $"{provider}  ·  {model}"
                        : $"{provider}  ·  {model}  ·  🎭 {capturedRole.AnswerAsName}";
                    // Refresh CO / R badges independently
                    capturedCoBadge.Visibility = capturedRole.IsCoordinator
                        ? Visibility.Visible : Visibility.Collapsed;
                    capturedRBadge.Visibility  = capturedRole.IsReasoner
                        ? Visibility.Visible : Visibility.Collapsed;
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

            // Inactive dimming — dims everything in the row (checkbox stays clickable)
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
            Fill   = (Brush)FindResource("InputBrush")
        };
        root.Children.Add(sep2);

        var saveBtn = new Button
        {
            Content    = "Save",
            IsDefault  = true,
            Height     = 36, Margin = new Thickness(0, 0, 8, 16),
            Style      = (Style)FindResource("ModernButton"),
            Background = (Brush)FindResource("ClaudeBrush"),
            Foreground = (Brush)FindResource("SidebarBrush"),
            FontWeight = FontWeights.SemiBold
        };
        var cancelBtn = new Button
        {
            Content    = "Cancel",
            IsCancel   = true,
            Height     = 36, Margin = new Thickness(0, 0, 0, 16),
            Style      = (Style)FindResource("ModernButton"),
            Background = (Brush)FindResource("InputBrush"),
            Foreground = (Brush)FindResource("TextBrush")
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
                                 : OrchestrationMode.AllRespond;

            // Collect language
            ps.Language = langCombo.Text.Trim();

            // Collect max dialog depth
            ps.MaxDialogDepth = int.TryParse(depthBox.Text, out var d) && d >= 1 ? d : 1;

            // Collect default response length
            ps.DefaultResponseLength = (int)defLenSlider.Value;

            // Collect roles
            ps.Roles.Clear();
            foreach (var (_, _, getRoleSnapshot) in roleRows)
                ps.Roles.Add(getRoleSnapshot());

            // Enforce single coordinator
            if (ps.Roles.Count(r => r.IsCoordinator) > 1)
            {
                MessageBox.Show(
                    "Only one participant can be Coordinator.\n" +
                    "Please open each participant's editor and ensure only one is marked as Coordinator.",
                    "Multiple Coordinators", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ProjectService.SaveProjectSettings(projFolder, ps);

            // Keep live fields in sync if this is the currently open project
            if (_currentProjectFolder == projFolder)
            {
                _projectLanguage = ps.Language;
                _maxDialogDepth  = ps.MaxDialogDepth;
                _projectSettings = ps;
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
            IsActive         = role.IsActive
        };

        // ── Window ────────────────────────────────────────────────────────
        var win = new Window
        {
            Title                 = $"Character Editor — {displayName}",
            Width                 = 480,
            MaxHeight             = 800,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            Background            = (Brush)FindResource("BackgroundBrush"),
            ResizeMode            = ResizeMode.NoResize
        };

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 8) };

        // ── Local helpers ─────────────────────────────────────────────────
        TextBlock SectionHeader(string text) => new TextBlock
        {
            Text             = text,
            FontSize         = 11,
            FontWeight       = FontWeights.SemiBold,
            FontFamily       = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("SubtextBrush"),
            Margin     = new Thickness(0, 14, 0, 6)
        };

        TextBlock MakeLabel(string text) => new TextBlock
        {
            Text       = text,
            FontSize   = 12,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("TextBrush"),
            Margin     = new Thickness(0, 0, 0, 4)
        };

        TextBox MakeTextBox(string text, bool multiline = false) => new TextBox
        {
            Text            = text,
            FontSize        = 13,
            FontFamily      = new FontFamily("Segoe UI"),
            Background      = (Brush)FindResource("InputBrush"),
            Foreground      = (Brush)FindResource("TextBrush"),
            CaretBrush      = (Brush)FindResource("TextBrush"),
            BorderBrush     = (Brush)FindResource("SubtextBrush"),
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
            Foreground          = (Brush)FindResource("SubtextBrush"),
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

        // End labels row (Short — Long)
        var lengthEndRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        lengthEndRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lengthEndRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var shortEndLbl = new TextBlock { Text = "Short", FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("SubtextBrush"), HorizontalAlignment = HorizontalAlignment.Left };
        var longEndLbl  = new TextBlock { Text = "Long",  FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("SubtextBrush"), HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(shortEndLbl, 0); Grid.SetColumn(longEndLbl, 1);
        lengthEndRow.Children.Add(shortEndLbl); lengthEndRow.Children.Add(longEndLbl);

        root.Children.Add(lengthValueLbl);
        root.Children.Add(lengthSlider);
        root.Children.Add(lengthEndRow);

        // ── ORCHESTRATION ─────────────────────────────────────────────────
        root.Children.Add(SectionHeader("ORCHESTRATION"));

        var coordCheck = new CheckBox
        {
            Content    = "Coordinator — routes messages to reasoners",
            IsChecked  = role.IsCoordinator,
            FontSize   = 13,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("TextBrush"),
            ToolTip    = "Only one coordinator per project.",
            Margin     = new Thickness(0, 0, 0, 8)
        };
        root.Children.Add(coordCheck);

        var reasonerCheck = new CheckBox
        {
            Content    = "Reasoner — executes delegated tasks",
            IsChecked  = role.IsReasoner,
            FontSize   = 13,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("TextBrush"),
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
            Foreground = (Brush)FindResource("TextBrush"),
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

        reasonerCheck.Checked   += (_, _) => priorityPanel.Visibility = Visibility.Visible;
        reasonerCheck.Unchecked += (_, _) => priorityPanel.Visibility = Visibility.Collapsed;

        // ── CHARACTER FILE ────────────────────────────────────────────────
        root.Children.Add(SectionHeader("CHARACTER FILE"));

        var fileRow    = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        var loadCharBtn = MakeBtn("📂 Load character", (Brush)FindResource("InputBrush"), (Brush)FindResource("TextBrush"));
        var saveCharBtn = MakeBtn("💾 Save as character", (Brush)FindResource("InputBrush"), (Brush)FindResource("TextBrush"));
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
                Background            = (Brush)FindResource("BackgroundBrush"),
                ResizeMode            = ResizeMode.NoResize
            };
            var pp = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
            pp.Children.Add(new TextBlock
            {
                Text = "Select a character:", FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
                Foreground = (Brush)FindResource("TextBrush"), Margin = new Thickness(0, 0, 0, 8)
            });
            var lb = new ListBox
            {
                Background = (Brush)FindResource("InputBrush"), Foreground = (Brush)FindResource("TextBrush"),
                BorderBrush = (Brush)FindResource("SubtextBrush"), BorderThickness = new Thickness(1),
                MaxHeight = 200, Margin = new Thickness(0, 0, 0, 12)
            };
            foreach (var c in chars) lb.Items.Add(c);
            lb.SelectedIndex = 0;
            pp.Children.Add(lb);

            var pRow    = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var pOk     = new Button { Content = "Load", IsDefault = true, Height = 30, Padding = new Thickness(14, 0, 14, 0),
                Style = (Style)FindResource("ModernButton"),
                Background = (Brush)FindResource("ClaudeBrush"), Foreground = (Brush)FindResource("SidebarBrush"),
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0) };
            var pCancel = new Button { Content = "Cancel", IsCancel = true, Height = 30, Padding = new Thickness(14, 0, 14, 0),
                Style = (Style)FindResource("ModernButton"),
                Background = (Brush)FindResource("InputBrush"), Foreground = (Brush)FindResource("TextBrush") };
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
                Background            = (Brush)FindResource("BackgroundBrush"),
                ResizeMode            = ResizeMode.NoResize
            };
            var np = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
            np.Children.Add(new TextBlock
            {
                Text = "Save as character name:", FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
                Foreground = (Brush)FindResource("TextBrush"), Margin = new Thickness(0, 0, 0, 8)
            });
            var nameBox = new TextBox
            {
                Text = suggestedName, FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
                Background = (Brush)FindResource("InputBrush"), Foreground = (Brush)FindResource("TextBrush"),
                CaretBrush = (Brush)FindResource("TextBrush"),
                BorderBrush = (Brush)FindResource("SubtextBrush"), BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 12)
            };
            np.Children.Add(nameBox);
            var nRow    = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var nOk     = new Button { Content = "Save", IsDefault = true, Height = 30, Padding = new Thickness(14, 0, 14, 0),
                Style = (Style)FindResource("ModernButton"),
                Background = (Brush)FindResource("ClaudeBrush"), Foreground = (Brush)FindResource("SidebarBrush"),
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0) };
            var nCancel = new Button { Content = "Cancel", IsCancel = true, Height = 30, Padding = new Thickness(14, 0, 14, 0),
                Style = (Style)FindResource("ModernButton"),
                Background = (Brush)FindResource("InputBrush"), Foreground = (Brush)FindResource("TextBrush") };
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
            Height = 1, Fill = (Brush)FindResource("InputBrush"), Margin = new Thickness(0, 4, 0, 12)
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
            Background = (Brush)FindResource("InputBrush"),
            Foreground = (Brush)FindResource("TextBrush")
        };
        Grid.SetColumn(resetBtn, 0);

        var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var okBtn = new Button
        {
            Content    = "OK",
            IsDefault  = true,
            Height     = 34, Padding = new Thickness(20, 0, 20, 0),
            Style      = (Style)FindResource("ModernButton"),
            Background = (Brush)FindResource("ClaudeBrush"),
            Foreground = (Brush)FindResource("SidebarBrush"),
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, 0, 8, 0)
        };
        var cancelEditorBtn = new Button
        {
            Content    = "Cancel",
            IsCancel   = true,
            Height     = 34, Padding = new Thickness(14, 0, 14, 0),
            Style      = (Style)FindResource("ModernButton"),
            Background = (Brush)FindResource("InputBrush"),
            Foreground = (Brush)FindResource("TextBrush")
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
            answerAsBox.Text        = snap.AnswerAsName;
            instrBox.Text           = snap.RoleInstruction;
            lengthSlider.Value      = snap.ResponseLength;
            coordCheck.IsChecked    = snap.IsCoordinator;
            reasonerCheck.IsChecked = snap.IsReasoner;
            prioritySlider.Value    = snap.ReasonerPriority;
        };

        // ── OK handler — write back in-place ──────────────────────────────
        okBtn.Click += (_, _) =>
        {
            role.AnswerAsName     = answerAsBox.Text.Trim();
            role.RoleInstruction  = instrBox.Text.Trim();
            role.ResponseLength   = (int)Math.Round(lengthSlider.Value);
            role.IsCoordinator    = coordCheck.IsChecked    == true;
            role.IsReasoner       = reasonerCheck.IsChecked == true;
            role.ReasonerPriority = (int)Math.Round(prioritySlider.Value);
            win.DialogResult      = true;
        };

        return win.ShowDialog() == true;
    }

    // ── Cloud AI participant management ────────────────────────────────────

    private void AddCloudAIParticipant(string provider, string model = "", string customName = "")
    {
        if (_cloudAIParticipants.Count >= 20) return;

        var apiKey = WindowsCredentialManager.Load(provider);
        if (string.IsNullOrWhiteSpace(apiKey)) return;

        var service = CreateCloudAIService(provider, apiKey);
        if (!string.IsNullOrEmpty(model)) service.CurrentModel = model;

        var participant = new CloudAIParticipant
        {
            Service    = service,
            Position   = _cloudAIParticipants.Count + 1,
            CustomName = string.IsNullOrWhiteSpace(customName) ? null : customName
        };
        BuildCloudAICard(participant);
        UpdateCloudAIAddRemoveButtons();
    }

    private void RemoveCloudAIParticipant(CloudAIParticipantUI ui)
    {
        CloudAICardsPanel.Children.Remove(ui.Popup);
        CloudAICardsPanel.Children.Remove(ui.Card);
        ui.Data.Service.Dispose();
        _cloudAIParticipants.Remove(ui);
        RenumberCloudAIParticipants();
        UpdateCloudAIAddRemoveButtons();
    }

    private void RenumberCloudAIParticipants()
    {
        for (int i = 0; i < _cloudAIParticipants.Count; i++)
        {
            var ui = _cloudAIParticipants[i];
            ui.Data.Position = i + 1;
            ui.AvatarBorder.SetResourceReference(Border.BackgroundProperty, ui.Data.ColorKey);
        }
    }

    private void UpdateCloudAIAddRemoveButtons()
    {
        foreach (var ui in _cloudAIParticipants)
            ui.RemoveButton.Visibility = Visibility.Visible;
    }

    private void BuildCloudAICard(CloudAIParticipant participant)
    {
        // ── Avatar ────────────────────────────────────────────────────────
        var avatarText = new TextBlock
        {
            Text                = participant.AvatarLabel,
            FontSize            = 11,
            FontWeight          = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center
        };
        avatarText.SetResourceReference(TextBlock.ForegroundProperty, "SidebarBrush");

        var avatarBorder = new Border
        {
            Width        = 34, Height = 34,
            CornerRadius = new CornerRadius(9),
            Child        = avatarText
        };
        avatarBorder.SetResourceReference(Border.BackgroundProperty, participant.ColorKey);

        var coBadge = new Border
        {
            CornerRadius        = new CornerRadius(3),
            Padding             = new Thickness(2, 0, 2, 0),
            Height              = 13,
            Background          = new SolidColorBrush(Color.FromRgb(255, 215, 0)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Top,
            Visibility          = Visibility.Collapsed,
            Child               = new TextBlock { Text = "CO", FontSize = 8, FontWeight = FontWeights.Bold,
                                      Foreground = Brushes.Black,
                                      HorizontalAlignment = HorizontalAlignment.Center,
                                      VerticalAlignment   = VerticalAlignment.Center }
        };
        var rBadge = new Border
        {
            CornerRadius        = new CornerRadius(3),
            Padding             = new Thickness(2, 0, 2, 0),
            Height              = 13,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Bottom,
            Visibility          = Visibility.Collapsed,
            Child               = new TextBlock { Text = "R", FontSize = 8, FontWeight = FontWeights.Bold,
                                      Foreground = Brushes.Black,
                                      HorizontalAlignment = HorizontalAlignment.Center,
                                      VerticalAlignment   = VerticalAlignment.Center }
        };
        rBadge.SetResourceReference(Border.BackgroundProperty, "SubtextBrush");

        var avatarContainer = new Grid
        {
            Width             = 38, Height = 38,
            Margin            = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        avatarContainer.Children.Add(avatarBorder);
        avatarContainer.Children.Add(coBadge);
        avatarContainer.Children.Add(rBadge);

        // ── Status dot ────────────────────────────────────────────────────
        var statusDot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        statusDot.SetResourceReference(Ellipse.FillProperty, "SubtextBrush");

        // ── Labels ────────────────────────────────────────────────────────
        var nameLabel = new TextBlock
        {
            Text       = participant.DisplayName,
            FontSize   = 13,
            FontWeight = FontWeights.SemiBold
        };
        nameLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var modelLabel = new TextBlock
        {
            Text    = FormatModelDisplayName(participant.Service.CurrentModel),
            FontSize = 10
        };
        modelLabel.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");

        var offlineLabel = new TextBlock { Text = "Offline", FontSize = 10, Visibility = Visibility.Collapsed };
        offlineLabel.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");

        // ── Remove button ─────────────────────────────────────────────────
        var removeButton = new Button
        {
            Content           = "✕",
            Width             = 22, Height = 22,
            FontSize          = 10,
            BorderThickness   = new Thickness(0),
            Cursor            = Cursors.Hand,
            Padding           = new Thickness(0),
            Visibility        = Visibility.Visible,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip           = "Remove participant",
            Style             = (Style)FindResource("ModernButton")
        };
        removeButton.SetResourceReference(Button.BackgroundProperty, "SurfaceBrush");
        removeButton.SetResourceReference(Button.ForegroundProperty, "SubtextBrush");

        // ── Layout ────────────────────────────────────────────────────────
        var labelPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labelPanel.Children.Add(nameLabel);
        labelPanel.Children.Add(modelLabel);
        labelPanel.Children.Add(offlineLabel);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(avatarContainer, 0);
        Grid.SetColumn(labelPanel,      1);
        Grid.SetColumn(statusDot,       2);
        Grid.SetColumn(removeButton,    3);

        grid.Children.Add(avatarContainer);
        grid.Children.Add(labelPanel);
        grid.Children.Add(statusDot);
        grid.Children.Add(removeButton);

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding      = new Thickness(10),
            Margin       = new Thickness(0, 0, 0, 7),
            Cursor       = Cursors.Hand,
            Child        = grid
        };
        card.SetResourceReference(Border.BackgroundProperty, "InputBrush");

        // ── Popup ─────────────────────────────────────────────────────────
        var popupTitle = new TextBlock
        {
            Text       = participant.DisplayName,
            FontSize   = 13,
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, 0, 0, 8)
        };
        popupTitle.SetResourceReference(TextBlock.ForegroundProperty, participant.ColorKey);

        var separator = new Rectangle { Height = 1, Margin = new Thickness(0, 0, 0, 10) };
        separator.SetResourceReference(Rectangle.FillProperty, "InputBrush");

        var enabledToggle = new CheckBox
        {
            Style     = (Style)FindResource("ToggleSwitch"),
            IsChecked = true,
            Content   = $"{participant.DisplayName} enabled",
            Margin    = new Thickness(0, 0, 0, 14)
        };

        var infoProviderKey = new TextBlock { Text = "PROVIDER", FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 3) };
        infoProviderKey.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");
        var infoProviderVal = new TextBlock { Text = participant.ProviderName, FontSize = 12, Margin = new Thickness(0, 0, 0, 10) };
        infoProviderVal.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var infoModelKey = new TextBlock { Text = "MODEL", FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 3) };
        infoModelKey.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");
        var infoModelVal = new TextBlock { Text = FormatModelDisplayName(participant.Service.CurrentModel), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        infoModelVal.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var popupContent = new StackPanel();
        popupContent.Children.Add(popupTitle);
        popupContent.Children.Add(separator);
        popupContent.Children.Add(enabledToggle);
        popupContent.Children.Add(infoProviderKey);
        popupContent.Children.Add(infoProviderVal);
        popupContent.Children.Add(infoModelKey);
        popupContent.Children.Add(infoModelVal);

        var popupBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(12),
            Padding         = new Thickness(14),
            MinWidth        = 230,
            Child           = popupContent,
            Effect          = new DropShadowEffect { Color = Colors.Black, Opacity = 0.45, BlurRadius = 22, ShadowDepth = 4 }
        };
        popupBorder.SetResourceReference(Border.BackgroundProperty,  "SidebarBrush");
        popupBorder.SetResourceReference(Border.BorderBrushProperty, "SurfaceBrush");

        var popup = new Popup
        {
            PlacementTarget    = card,
            Placement          = PlacementMode.Right,
            HorizontalOffset   = 10,
            VerticalOffset     = -8,
            StaysOpen          = false,
            AllowsTransparency = true,
            Child              = popupBorder
        };

        var ui = new CloudAIParticipantUI
        {
            Data          = participant,
            Card          = card,
            AvatarBorder  = avatarBorder,
            AvatarText    = avatarText,
            CoBadge       = coBadge,
            RBadge        = rBadge,
            NameLabel     = nameLabel,
            StatusDot     = statusDot,
            ModelLabel    = modelLabel,
            OfflineLabel  = offlineLabel,
            Popup         = popup,
            PopupTitle    = popupTitle,
            EnabledToggle = enabledToggle,
            RemoveButton  = removeButton
        };

        card.MouseLeftButtonDown += (_, _) =>
        {
            enabledToggle.IsChecked = ui.Data.Enabled;
            infoModelVal.Text       = FormatModelDisplayName(ui.Data.Service.CurrentModel);
            popup.IsOpen            = !popup.IsOpen;
        };

        enabledToggle.Checked   += (_, _) => OnCloudAIEnabledChanged(ui, true);
        enabledToggle.Unchecked += (_, _) => OnCloudAIEnabledChanged(ui, false);

        removeButton.Click += (_, _) => RemoveCloudAIParticipant(ui);

        CloudAICardsPanel.Children.Add(popup);
        CloudAICardsPanel.Children.Add(card);
        _cloudAIParticipants.Add(ui);
    }

    private void OnCloudAIEnabledChanged(CloudAIParticipantUI ui, bool enabled)
    {
        ui.Data.Enabled = enabled;
        double op = (enabled && ui.Data.IsOnline == true) ? 1.0 : 0.6;
        AnimateStatusChange(ui.Card, op);
    }

    // ── Ollama participant management ──────────────────────────────────────

    private void AddOllamaParticipant(string model = "llama3.2",
                                      string serverUrl = "http://localhost:11434",
                                      string customName = "")
    {
        if (_ollamaParticipants.Count >= 20) return;

        var participant = new OllamaParticipant
        {
            Service    = new OllamaService(serverUrl) { CurrentModel = model },
            Position   = _ollamaParticipants.Count + 1,
            CustomName = string.IsNullOrWhiteSpace(customName) ? null : customName
        };
        BuildOllamaCard(participant);
        UpdateAddRemoveButtons();
    }

    private void RemoveOllamaParticipant(OllamaParticipantUI ui)
    {
        if (_ollamaParticipants.Count + _cloudAIParticipants.Count <= 1) return;

        OllamaCardsPanel.Children.Remove(ui.Popup);
        OllamaCardsPanel.Children.Remove(ui.Card);
        _ollamaParticipants.Remove(ui);

        RenumberParticipants();
        UpdateAddRemoveButtons();
    }

    private void RenumberParticipants()
    {
        for (int i = 0; i < _ollamaParticipants.Count; i++)
        {
            var ui  = _ollamaParticipants[i];
            var pos = i + 1;
            ui.Data.Position = pos;

            ui.AvatarText.Text = ui.Data.AvatarLabel;
            ui.AvatarBorder.SetResourceReference(Border.BackgroundProperty, ui.Data.ColorKey);

            var displayName = ui.Data.DisplayName;
            ui.NameLabel .Text = displayName;
            ui.PopupTitle.Text = displayName;
            ui.PopupTitle.SetResourceReference(TextBlock.ForegroundProperty, ui.Data.ColorKey);
            ui.EnabledToggle.Content = $"{displayName} enabled";
        }
    }

    private void UpdateAddRemoveButtons()
    {
        // Show remove on Ollama cards when there is more than one participant total
        bool showRemove = (_ollamaParticipants.Count + _cloudAIParticipants.Count) > 1;
        foreach (var ui in _ollamaParticipants)
            ui.RemoveButton.Visibility = showRemove ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildOllamaCard(OllamaParticipant participant)
    {
        var displayName = participant.DisplayName;

        var avatarText = new TextBlock
        {
            Text                = participant.AvatarLabel,
            FontSize            = 11,
            FontWeight          = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center
        };
        avatarText.SetResourceReference(TextBlock.ForegroundProperty, "SidebarBrush");

        var avatarBorder = new Border
        {
            Width        = 34, Height = 34,
            CornerRadius = new CornerRadius(9),
            Child        = avatarText
        };
        avatarBorder.SetResourceReference(Border.BackgroundProperty, participant.ColorKey);

        var coBadge = new Border
        {
            CornerRadius        = new CornerRadius(3),
            Padding             = new Thickness(2, 0, 2, 0),
            Height              = 13,
            Background          = new SolidColorBrush(Color.FromRgb(255, 215, 0)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Top,
            Visibility          = Visibility.Collapsed,
            Child               = new TextBlock { Text = "CO", FontSize = 8, FontWeight = FontWeights.Bold,
                                      Foreground = Brushes.Black,
                                      HorizontalAlignment = HorizontalAlignment.Center,
                                      VerticalAlignment   = VerticalAlignment.Center }
        };
        var rBadge = new Border
        {
            CornerRadius        = new CornerRadius(3),
            Padding             = new Thickness(2, 0, 2, 0),
            Height              = 13,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Bottom,
            Visibility          = Visibility.Collapsed,
            Child               = new TextBlock { Text = "R", FontSize = 8, FontWeight = FontWeights.Bold,
                                      Foreground = Brushes.Black,
                                      HorizontalAlignment = HorizontalAlignment.Center,
                                      VerticalAlignment   = VerticalAlignment.Center }
        };
        rBadge.SetResourceReference(Border.BackgroundProperty, "SubtextBrush");

        var avatarContainer = new Grid
        {
            Width             = 38, Height = 38,
            Margin            = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        avatarContainer.Children.Add(avatarBorder);
        avatarContainer.Children.Add(coBadge);
        avatarContainer.Children.Add(rBadge);

        var statusDot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        statusDot.SetResourceReference(Ellipse.FillProperty, "SubtextBrush");

        var nameLabel = new TextBlock { Text = displayName, FontSize = 13, FontWeight = FontWeights.SemiBold };
        nameLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var modelLabel = new TextBlock { Text = "checking...", FontSize = 10 };
        modelLabel.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");

        var offlineLabel = new TextBlock { Text = "Offline", FontSize = 10, Visibility = Visibility.Collapsed };
        offlineLabel.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");

        var removeButton = new Button
        {
            Content           = "✕",
            Width             = 22, Height = 22,
            FontSize          = 10,
            BorderThickness   = new Thickness(0),
            Cursor            = Cursors.Hand,
            Padding           = new Thickness(0),
            Visibility        = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip           = "Remove participant",
            Style             = (Style)FindResource("ModernButton")
        };
        removeButton.SetResourceReference(Button.BackgroundProperty, "SurfaceBrush");
        removeButton.SetResourceReference(Button.ForegroundProperty, "SubtextBrush");

        var labelPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labelPanel.Children.Add(nameLabel);
        labelPanel.Children.Add(modelLabel);
        labelPanel.Children.Add(offlineLabel);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(avatarContainer, 0);
        Grid.SetColumn(labelPanel,      1);
        Grid.SetColumn(statusDot,       2);
        Grid.SetColumn(removeButton,    3);

        grid.Children.Add(avatarContainer);
        grid.Children.Add(labelPanel);
        grid.Children.Add(statusDot);
        grid.Children.Add(removeButton);

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding      = new Thickness(10),
            Margin       = new Thickness(0, 0, 0, 7),
            Cursor       = Cursors.Hand,
            Child        = grid
        };
        card.SetResourceReference(Border.BackgroundProperty, "InputBrush");

        // Popup
        var popupTitle = new TextBlock
        {
            Text       = displayName,
            FontSize   = 13,
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, 0, 0, 8)
        };
        popupTitle.SetResourceReference(TextBlock.ForegroundProperty, participant.ColorKey);

        var separator = new Rectangle { Height = 1, Margin = new Thickness(0, 0, 0, 10) };
        separator.SetResourceReference(Rectangle.FillProperty, "InputBrush");

        var enabledToggle = new CheckBox
        {
            Style     = (Style)FindResource("ToggleSwitch"),
            IsChecked = true,
            Content   = $"{displayName} enabled",
            Margin    = new Thickness(0, 0, 0, 14)
        };

        var infoServerKey = new TextBlock { Text = "SERVER", FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 3) };
        infoServerKey.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");
        var infoServerVal = new TextBlock { Text = participant.Service.BaseUrl, FontSize = 12, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
        infoServerVal.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var infoModelKey = new TextBlock { Text = "MODEL", FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 3) };
        infoModelKey.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");
        var infoModelVal = new TextBlock { Text = FormatModelDisplayName(participant.Service.CurrentModel), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        infoModelVal.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var popupContent = new StackPanel();
        popupContent.Children.Add(popupTitle);
        popupContent.Children.Add(separator);
        popupContent.Children.Add(enabledToggle);
        popupContent.Children.Add(infoServerKey);
        popupContent.Children.Add(infoServerVal);
        popupContent.Children.Add(infoModelKey);
        popupContent.Children.Add(infoModelVal);

        var popupBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(12),
            Padding         = new Thickness(14),
            MinWidth        = 230,
            Child           = popupContent,
            Effect          = new DropShadowEffect { Color = Colors.Black, Opacity = 0.45, BlurRadius = 22, ShadowDepth = 4 }
        };
        popupBorder.SetResourceReference(Border.BackgroundProperty,  "SidebarBrush");
        popupBorder.SetResourceReference(Border.BorderBrushProperty, "SurfaceBrush");

        var popup = new Popup
        {
            PlacementTarget    = card,
            Placement          = PlacementMode.Right,
            HorizontalOffset   = 10,
            VerticalOffset     = -8,
            StaysOpen          = false,
            AllowsTransparency = true,
            Child              = popupBorder
        };

        var ui = new OllamaParticipantUI
        {
            Data          = participant,
            Card          = card,
            AvatarBorder  = avatarBorder,
            AvatarText    = avatarText,
            CoBadge       = coBadge,
            RBadge        = rBadge,
            NameLabel     = nameLabel,
            StatusDot     = statusDot,
            ModelLabel    = modelLabel,
            OfflineLabel  = offlineLabel,
            Popup         = popup,
            PopupTitle    = popupTitle,
            EnabledToggle = enabledToggle,
            RemoveButton  = removeButton
        };

        card.MouseLeftButtonDown += (_, _) =>
        {
            enabledToggle.IsChecked = ui.Data.Enabled;
            infoModelVal.Text       = FormatModelDisplayName(ui.Data.Service.CurrentModel);
            popup.IsOpen            = !popup.IsOpen;
        };

        enabledToggle.Checked   += (_, _) => OnOllamaEnabledChanged(ui, true);
        enabledToggle.Unchecked += (_, _) => OnOllamaEnabledChanged(ui, false);

        removeButton.Click += (_, _) => RemoveOllamaParticipant(ui);

        OllamaCardsPanel.Children.Add(popup);
        OllamaCardsPanel.Children.Add(card);
        _ollamaParticipants.Add(ui);
    }

    private void OnOllamaEnabledChanged(OllamaParticipantUI ui, bool enabled)
    {
        ui.Data.Enabled = enabled;
        double op = (enabled && ui.Data.IsOnline == true) ? 1.0 : 0.6;
        AnimateStatusChange(ui.Card, op);
    }

    // ── Status ─────────────────────────────────────────────────────────────

    private void StartStatusTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += async (_, _) => await CheckAllStatusAsync();
        timer.Start();
    }

    private async Task CheckAllStatusAsync()
    {
        if (_ollamaParticipants.Count > 0)
        {
            bool wasOnlineBefore = _ollamaParticipants[0].Data.IsOnline == true;
            var  ollamaOnline    = await _ollamaParticipants[0].Data.Service.IsAvailableAsync();

            foreach (var ui in _ollamaParticipants)
                ApplyOllamaParticipantStatus(ui, ollamaOnline);

            if (ollamaOnline && !wasOnlineBefore)
                await LoadOllamaModelsAsync();
        }

        foreach (var ui in _cloudAIParticipants)
        {
            var online = await ui.Data.Service.IsAvailableAsync();
            ApplyCloudAIParticipantStatus(ui, online);
        }
    }

    private void ApplyOllamaParticipantStatus(OllamaParticipantUI ui, bool online)
    {
        bool changed = ui.Data.IsOnline != online;
        ui.Data.IsOnline = online;

        ui.StatusDot.SetResourceReference(Ellipse.FillProperty, online ? "OllamaBrush" : "AccentBrush");
        ui.OfflineLabel.Visibility = online ? Visibility.Collapsed : Visibility.Visible;
        ui.ModelLabel.Visibility   = online ? Visibility.Visible   : Visibility.Collapsed;

        double targetOpacity = (online && ui.Data.Enabled) ? 1.0 : 0.6;

        if (changed)
        {
            AnimateStatusChange(ui.Card, targetOpacity);
            if (_ollamaParticipants.Count > 0 && _ollamaParticipants[0] == ui)
                AddSystemMessage(online ? "✓  Ollama is online." : "⚠  Ollama is offline.");
        }
        else
        {
            ui.Card.Opacity = targetOpacity;
        }
    }

    private void ApplyCloudAIParticipantStatus(CloudAIParticipantUI ui, bool online)
    {
        bool changed = ui.Data.IsOnline != online;
        ui.Data.IsOnline = online;

        ui.StatusDot.SetResourceReference(Ellipse.FillProperty, online ? "OllamaBrush" : "AccentBrush");
        ui.OfflineLabel.Visibility = online ? Visibility.Collapsed : Visibility.Visible;
        ui.ModelLabel.Visibility   = online ? Visibility.Visible   : Visibility.Collapsed;

        if (online)
            ui.ModelLabel.Text = FormatModelDisplayName(ui.Data.Service.CurrentModel);

        double targetOpacity = (online && ui.Data.Enabled) ? 1.0 : 0.6;

        if (changed)
        {
            AnimateStatusChange(ui.Card, targetOpacity);
            AddSystemMessage(online
                ? $"✓  {ui.Data.DisplayName} is online."
                : $"⚠  {ui.Data.DisplayName} is offline.");
        }
        else
        {
            ui.Card.Opacity = targetOpacity;
        }
    }

    private static void AnimateStatusChange(UIElement element, double targetOpacity)
    {
        var kf = new DoubleAnimationUsingKeyFrames();
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(0.25,          KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(targetOpacity, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400))));
        Storyboard.SetTarget(kf, element);
        Storyboard.SetTargetProperty(kf, new PropertyPath(OpacityProperty));
        var sb = new Storyboard();
        sb.Children.Add(kf);
        sb.Begin();
    }

    private async Task LoadOllamaModelsAsync()
    {
        if (_ollamaParticipants.Count == 0) return;
        try
        {
            var models = await _ollamaParticipants[0].Data.Service.GetModelsAsync();
            _availableOllamaModels = models;

            foreach (var ui in _ollamaParticipants)
            {
                if (models.Count > 0)
                {
                    if (!models.Contains(ui.Data.Service.CurrentModel))
                        ui.Data.Service.CurrentModel = models[0];
                    ui.ModelLabel.Text = FormatModelDisplayName(ui.Data.Service.CurrentModel);
                    // Also refresh avatar text
                    ui.AvatarText.Text = ui.Data.AvatarLabel;
                    ui.NameLabel .Text = ui.Data.DisplayName;
                }
                else
                {
                    ui.ModelLabel.Text = "no model found";
                }
            }
        }
        catch
        {
            foreach (var ui in _ollamaParticipants)
                ui.ModelLabel.Text = "model list unavailable";
        }
    }

    // ── Add Cloud AI popup handlers ────────────────────────────────────────

    // ── Add Participant dropdown ───────────────────────────────────────────

    private void AddParticipantButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsService.Load();
        var enabled  = settings.Participants
            .Where(p => p.Enabled && !string.IsNullOrEmpty(p.Model))
            .ToList();

        var menu = new ContextMenu { FontFamily = new FontFamily("Segoe UI"), FontSize = 13 };

        foreach (var p in enabled)
        {
            // Count-based duplicate check: allow re-adding a removed participant, and
            // support multiple settings entries for the same model/provider.
            bool alreadyAdded;
            if (p.Type == "Ollama")
            {
                int liveCount     = _ollamaParticipants.Count(ui =>
                    ui.Data.Service.CurrentModel == p.Model &&
                    ui.Data.Service.BaseUrl      == p.ServerUrl);
                int settingsCount = enabled.Count(s =>
                    s.Type      == "Ollama" &&
                    s.Model     == p.Model  &&
                    s.ServerUrl == p.ServerUrl);
                alreadyAdded = liveCount >= settingsCount;
            }
            else
            {
                int liveCount     = _cloudAIParticipants.Count(ui =>
                    ui.Data.Service.ProviderName == p.Type &&
                    ui.Data.Service.CurrentModel == p.Model);
                int settingsCount = enabled.Count(s =>
                    s.Type  == p.Type &&
                    s.Model == p.Model);
                alreadyAdded = liveCount >= settingsCount;
            }

            bool hasKey = p.Type == "Ollama"
                || !string.IsNullOrWhiteSpace(WindowsCredentialManager.Load(p.Type));

            var displayName = string.IsNullOrWhiteSpace(p.Name)
                ? FormatModelDisplayName(p.Model)
                : p.Name;

            var typeIcon = p.Type == "Ollama" ? "🦙" : "☁️";
            var suffix   = alreadyAdded ? "  · already in chat"
                         : !hasKey      ? "  · ⚠ no API key"
                         : "";
            var item = new MenuItem
            {
                Header    = $"{typeIcon}  {displayName}  ·  {p.Model}{suffix}",
                IsEnabled = !alreadyAdded && hasKey
            };

            if (!alreadyAdded && hasKey)
            {
                var cap = p;
                item.Click += (_, _) =>
                {
                    if (cap.Type == "Ollama")
                    {
                        AddOllamaParticipant(cap.Model, cap.ServerUrl, cap.Name);
                        _ = CheckAllStatusAsync();
                    }
                    else
                    {
                        var countBefore = _cloudAIParticipants.Count;
                        AddCloudAIParticipant(cap.Type, cap.Model, cap.Name);
                        if (_cloudAIParticipants.Count == countBefore)
                        {
                            AddSystemMessage($"⚠  Could not add {cap.Type} — no API key saved. Open ⚙ Settings.");
                            return;
                        }
                        var ui = _cloudAIParticipants[^1];
                        _ = Task.Run(async () =>
                        {
                            var online = await ui.Data.Service.IsAvailableAsync();
                            Dispatcher.Invoke(() => ApplyCloudAIParticipantStatus(ui, online));
                        });
                    }
                };
            }
            menu.Items.Add(item);
        }

        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header    = "No participants configured — open ⚙ Settings",
                IsEnabled = false
            });
        }

        menu.PlacementTarget = AddParticipantButton;
        menu.Placement       = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen          = true;
    }

    // ── Input ──────────────────────────────────────────────────────────────

    private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        PlaceholderText.Visibility = string.IsNullOrEmpty(InputTextBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        if (Keyboard.Modifiers == ModifierKeys.None)
        {
            // Plain Enter → send
            SendMessage();
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            // Shift+Enter → new line, auto-continue list prefix if applicable
            var tb  = InputTextBox;
            int pos = tb.CaretIndex;

            // Find where the current line starts
            int lineStart = pos > 0 ? tb.Text.LastIndexOf('\n', pos - 1) + 1 : 0;
            string currentLine = tb.Text.Substring(lineStart, pos - lineStart);

            // Build the prefix to auto-repeat on the new line
            string prefix = "";
            var numMatch = Regex.Match(currentLine, @"^(\s*)(\d+)\.\s");
            if (numMatch.Success)
            {
                // Numbered list: increment the counter
                int n = int.Parse(numMatch.Groups[2].Value);
                prefix = numMatch.Groups[1].Value + (n + 1) + ". ";
            }
            else
            {
                var bulletMatch = Regex.Match(currentLine, @"^(\s*)([-•*])\s");
                if (bulletMatch.Success)
                    prefix = bulletMatch.Groups[1].Value + bulletMatch.Groups[2].Value + " ";
            }

            string insert = "\n" + prefix;
            tb.Text        = tb.Text.Insert(pos, insert);
            tb.CaretIndex  = pos + insert.Length;
            e.Handled      = true;
        }
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => SendMessage();

    // ── Drag & Drop files → INPUT folder ──────────────────────────────────

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = (e.Data.GetDataPresent(DataFormats.FileDrop) && _currentProjectFolder is not null)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        if (_currentProjectFolder is null)
        {
            AddSystemMessage("⚠  Open or create a project first — dropped files go into the INPUT folder.");
            return;
        }

        var files       = (string[])e.Data.GetData(DataFormats.FileDrop);
        var inputFolder = SysIO.Path.Combine(_currentProjectFolder, "INPUT");
        SysIO.Directory.CreateDirectory(inputFolder);

        int count = 0;
        foreach (var file in files)
        {
            if (!SysIO.File.Exists(file)) continue;
            var dest = SysIO.Path.Combine(inputFolder, SysIO.Path.GetFileName(file));
            // Sandbox check — ensure destination stays inside project folder
            if (!ProjectService.IsPathSafe(dest, _currentProjectFolder)) continue;
            SysIO.File.Copy(file, dest, overwrite: true);
            count++;
        }

        if (count > 0)
        {
            AddSystemMessage($"📎 {count} file(s) copied to INPUT folder.");
            // Update project meta participants snapshot
            if (_currentProject is not null)
            {
                _currentProject.Participants = BuildCurrentParticipantSnapshot();
                ProjectService.SaveMeta(_currentProjectFolder, _currentProject);
            }
        }
    }

    private void SendMessage()
    {
        var text = InputTextBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        var avatar = _userName.Length >= 2 ? _userName[..2].ToUpper() : _userName.ToUpper();
        AddMessage(_userName, avatar, "UserBrush", "UserBubbleBrush", text, isUser: true);

        var entry = new ChatLogEntry
        {
            Timestamp   = DateTime.Now,
            SenderType  = "User",
            DisplayName = _userName,
            AvatarLabel = avatar,
            AccentKey   = "UserBrush",
            BubbleKey   = "UserBubbleBrush",
            IsUser      = true,
            Message     = text
        };
        AppendToProjectLog(entry);

        _sharedHistory.Add(new CloudAIMessage("user", text, "User"));

        InputTextBox.Clear();
        InputTextBox.Focus();
        _ = TriggerAiResponsesAsync();
    }

    // ── AI responses ───────────────────────────────────────────────────────

    private async void AIRespond_Click(object sender, RoutedEventArgs e)
    {
        if (_sharedHistory.Count == 0)
        {
            AddSystemMessage("Send a message first.");
            return;
        }
        await TriggerAiResponsesAsync();
    }

    private async Task TriggerAiResponsesAsync()
    {
        var mode = _projectSettings?.OrchestrationMode ?? OrchestrationMode.AllRespond;

        // In CoordinatorFirst mode, reasoners are filtered inside RunCoordinatorFirstModeAsync.
        // In all other modes, reasoners are completely passive — they only participate when
        // explicitly mentioned by name in the conversation (no auto-triggering).
        bool suppressReasoners = mode != OrchestrationMode.CoordinatorFirst;

        var activeOllamas  = _ollamaParticipants
            .Where(ui => ui.Data.Enabled && ui.Data.IsOnline == true &&
                         IsParticipantActiveInProject("Ollama", ui.Data.Service.CurrentModel) &&
                         !(suppressReasoners && IsReasoner(ui)))
            .ToList();
        var activeCloudAIs = _cloudAIParticipants
            .Where(ui => ui.Data.Enabled && ui.Data.IsOnline == true &&
                         IsParticipantActiveInProject(ui.Data.Service.ProviderName, ui.Data.Service.CurrentModel) &&
                         !(suppressReasoners && IsReasoner(ui)))
            .ToList();

        if (activeOllamas.Count == 0 && activeCloudAIs.Count == 0)
        {
            AddSystemMessage("⚠  No active AI participant is available.");
            return;
        }

        AIRespondButton.IsEnabled = false;
        SendButton.IsEnabled      = false;
        _streamCts = new CancellationTokenSource();
        var ct = _streamCts.Token;

        // Depth > 1 only makes sense when multiple participants can talk to each other
        var maxRounds = (activeOllamas.Count + activeCloudAIs.Count) > 1 ? _maxDialogDepth : 1;

        try
        {
            switch (mode)
            {
                case OrchestrationMode.CoordinatorFirst:
                    await RunCoordinatorFirstModeAsync(activeOllamas, activeCloudAIs, ct);
                    break;
                case OrchestrationMode.CoordinatorSummarizes:
                    await RunCoordinatorSummarizesModeAsync(activeOllamas, activeCloudAIs, ct);
                    break;
                default:
                    await RunAllRespondModeAsync(activeOllamas, activeCloudAIs, ct, maxRounds);
                    break;
            }
        }
        finally
        {
            _streamCts.Dispose();
            _streamCts = null;
            AIRespondButton.IsEnabled = true;
            SendButton.IsEnabled      = true;
        }

        // History compression — after all streams finish, outside the CTS scope
        if (_currentProjectFolder is not null && !ct.IsCancellationRequested)
            await MaybeCompressHistoryAsync(CancellationToken.None);
    }

    // ── Orchestration mode runners ─────────────────────────────────────────

    /// <summary>
    /// System hint injected into every participant's context during follow-up rounds.
    /// Instructs them to respond only if they have something genuinely new to contribute,
    /// and to output exactly "PASS" otherwise so the round can be silently skipped.
    /// </summary>
    private const string FollowUpRoundHint =
        "The previous participants have already responded above. " +
        "Only continue this conversation if you genuinely have something new to contribute: " +
        "a different perspective, a meaningful correction, a direct response to something just " +
        "said, or important information that has not been covered yet. " +
        "If you have nothing meaningful to add right now, output exactly the word PASS " +
        "and nothing else.";

    private async Task RunAllRespondModeAsync(
        List<OllamaParticipantUI>   activeOllamas,
        List<CloudAIParticipantUI>  activeCloudAIs,
        CancellationToken ct, int maxRounds)
    {
        for (int round = 0; round < maxRounds && !ct.IsCancellationRequested; round++)
        {
            bool isFollowUp = round > 0;

            if (isFollowUp)
            {
                if (_sharedHistory.Count == 0 || _sharedHistory.Last().Role != "assistant")
                    break;

                // Remember position so we can remove the marker if nobody responds
                int markerIndex = ChatPanel.Children.Count;
                AddSystemMessage($"— Round {round + 1} —");

                string? hint = FollowUpRoundHint;
                int responded = 0;

                foreach (var ui in activeOllamas)
                {
                    if (ct.IsCancellationRequested) break;
                    if (await RunOllamaStreamAsync(ui, ct, hint)) responded++;
                }
                foreach (var ui in activeCloudAIs)
                {
                    if (ct.IsCancellationRequested) break;
                    if (await RunCloudAIStreamAsync(ui, ct, hint)) responded++;
                }

                // Nobody had anything new to say — remove the orphaned round marker and stop
                if (responded == 0)
                {
                    if (markerIndex < ChatPanel.Children.Count)
                        ChatPanel.Children.RemoveAt(markerIndex);
                    break;
                }
            }
            else
            {
                // Round 0 — first response, always fire everyone unconditionally
                foreach (var ui in activeOllamas)
                {
                    if (ct.IsCancellationRequested) break;
                    await RunOllamaStreamAsync(ui, ct);
                }
                foreach (var ui in activeCloudAIs)
                {
                    if (ct.IsCancellationRequested) break;
                    await RunCloudAIStreamAsync(ui, ct);
                }
            }
        }
    }

    private async Task RunCoordinatorFirstModeAsync(
        List<OllamaParticipantUI>   activeOllamas,
        List<CloudAIParticipantUI>  activeCloudAIs,
        CancellationToken ct)
    {
        var (coordOllama, coordCloud) = FindCoordinatorInLists(activeOllamas, activeCloudAIs);
        if (coordOllama is null && coordCloud is null)
        {
            AddSystemMessage("⚠  CoordinatorFirst: no coordinator found — falling back to AllRespond.");
            await RunAllRespondModeAsync(activeOllamas, activeCloudAIs, ct, 1);
            return;
        }

        // Split non-coordinator participants into:
        //   • free participants (IsReasoner = false) — respond automatically after the coordinator
        //   • reasoners (IsReasoner = true)          — only respond when tagged by the coordinator
        var freeOllamas      = activeOllamas .Where(u => u != coordOllama && !IsReasoner(u)).ToList();
        var freeCloudAIs     = activeCloudAIs.Where(u => u != coordCloud  && !IsReasoner(u)).ToList();
        var reasonerOllamas  = activeOllamas .Where(u => u != coordOllama &&  IsReasoner(u)).ToList();
        var reasonerCloudAIs = activeCloudAIs.Where(u => u != coordCloud  &&  IsReasoner(u)).ToList();

        var reasonerNames = reasonerOllamas.Select(GetEffectiveName)
            .Concat(reasonerCloudAIs.Select(GetEffectiveName))
            .ToList();

        var freeCount = freeOllamas.Count + freeCloudAIs.Count;

        // Do NOT list reasoners in the coordinator hint — advertising them causes reflexive tagging.
        // The coordinator naturally decides to call them by name if it genuinely needs them.
        string coordinatorHint = freeCount > 0
            ? "You respond first in this conversation round. " +
              "After your response the other active participants will also contribute."
            : "You are the only active participant — respond directly.";

        // Coordinator goes first
        if (coordCloud is not null)
            await RunCloudAIStreamAsync(coordCloud, ct, coordinatorHint);
        else
            await RunOllamaStreamAsync(coordOllama!, ct, coordinatorHint);

        if (ct.IsCancellationRequested) return;

        // Free participants respond automatically — no coordinator tagging required
        foreach (var ui in freeOllamas)
        {
            if (ct.IsCancellationRequested) break;
            await RunOllamaStreamAsync(ui, ct);
        }
        foreach (var ui in freeCloudAIs)
        {
            if (ct.IsCancellationRequested) break;
            await RunCloudAIStreamAsync(ui, ct);
        }

        if (ct.IsCancellationRequested || reasonerNames.Count == 0) return;

        // Parse the coordinator's response for @Name mentions — if a reasoner is named, call them
        var coordResponse = _sharedHistory.LastOrDefault(m => m.Role == "assistant")?.Content ?? "";
        var taggedOllamas = reasonerOllamas
            .Where(u => IsTaggedInResponse(coordResponse, GetEffectiveName(u)))
            .ToList();
        var taggedClouds = reasonerCloudAIs
            .Where(u => IsTaggedInResponse(coordResponse, GetEffectiveName(u)))
            .ToList();

        if (taggedOllamas.Count == 0 && taggedClouds.Count == 0) return;

        // Tell each reasoner it was specifically delegated to — helps it stay focused
        const string reasonerDelegationHint =
            "The Coordinator has specifically delegated a task to you. " +
            "Respond only to that delegated task or question.";

        AddSystemMessage("— Delegated to Reasoners —");
        foreach (var ui in taggedOllamas)
        {
            if (ct.IsCancellationRequested) break;
            await RunOllamaStreamAsync(ui, ct, reasonerDelegationHint, skipLatestUserMessage: true);
        }
        foreach (var ui in taggedClouds)
        {
            if (ct.IsCancellationRequested) break;
            await RunCloudAIStreamAsync(ui, ct, reasonerDelegationHint, skipLatestUserMessage: true);
        }
    }

    private async Task RunCoordinatorSummarizesModeAsync(
        List<OllamaParticipantUI>   activeOllamas,
        List<CloudAIParticipantUI>  activeCloudAIs,
        CancellationToken ct)
    {
        var (coordOllama, coordCloud) = FindCoordinatorInLists(activeOllamas, activeCloudAIs);

        // All non-coordinator participants respond first
        foreach (var ui in activeOllamas.Where(u => u != coordOllama))
        {
            if (ct.IsCancellationRequested) return;
            await RunOllamaStreamAsync(ui, ct);
        }
        foreach (var ui in activeCloudAIs.Where(u => u != coordCloud))
        {
            if (ct.IsCancellationRequested) return;
            await RunCloudAIStreamAsync(ui, ct);
        }

        if (ct.IsCancellationRequested || (coordOllama is null && coordCloud is null)) return;

        // Coordinator synthesizes all responses above
        AddSystemMessage("— Coordinator synthesizing —");
        const string synthesisHint =
            "All other participants have now given their responses above. " +
            "Please write a final synthesizing response: draw together their key points, " +
            "highlight agreements and any meaningful differences, and add your own concluding assessment.";

        if (coordCloud is not null)
            await RunCloudAIStreamAsync(coordCloud, ct, synthesisHint);
        else
            await RunOllamaStreamAsync(coordOllama!, ct, synthesisHint);
    }

    // ── Orchestration helpers ──────────────────────────────────────────────

    /// <summary>
    /// Finds the coordinator among the already-filtered active participant lists.
    /// Cloud AI is preferred over Ollama (larger context windows for coordination).
    /// </summary>
    private (OllamaParticipantUI? Ollama, CloudAIParticipantUI? Cloud) FindCoordinatorInLists(
        List<OllamaParticipantUI> ollamas, List<CloudAIParticipantUI> clouds)
    {
        if (_projectSettings is null) return (null, null);

        var cloud = clouds.FirstOrDefault(ui =>
            _projectSettings.Get(ui.Data.Service.ProviderName, ui.Data.Service.CurrentModel)
                            ?.IsCoordinator == true);
        if (cloud is not null) return (null, cloud);

        var ollama = ollamas.FirstOrDefault(ui =>
            _projectSettings.Get("Ollama", ui.Data.Service.CurrentModel)
                            ?.IsCoordinator == true);
        return (ollama, null);
    }

    /// <summary>Effective display name for a participant: AnswerAsName if set, else CustomName/model name.</summary>
    private string GetEffectiveName(OllamaParticipantUI ui)
    {
        var role = _projectSettings?.Get("Ollama", ui.Data.Service.CurrentModel);
        if (!string.IsNullOrWhiteSpace(role?.AnswerAsName)) return role.AnswerAsName;
        return string.IsNullOrEmpty(ui.Data.CustomName)
            ? FormatModelDisplayName(ui.Data.Service.CurrentModel)
            : ui.Data.CustomName;
    }

    /// <summary>Effective display name for a participant: AnswerAsName if set, else CustomName/model name.</summary>
    private string GetEffectiveName(CloudAIParticipantUI ui)
    {
        var role = _projectSettings?.Get(ui.Data.Service.ProviderName, ui.Data.Service.CurrentModel);
        if (!string.IsNullOrWhiteSpace(role?.AnswerAsName)) return role.AnswerAsName;
        return string.IsNullOrEmpty(ui.Data.CustomName)
            ? FormatModelDisplayName(ui.Data.Service.CurrentModel)
            : ui.Data.CustomName;
    }

    /// <summary>Returns true when the participant is flagged as a Reasoner in the current project settings.</summary>
    private bool IsReasoner(OllamaParticipantUI ui) =>
        _projectSettings?.Get("Ollama", ui.Data.Service.CurrentModel)?.IsReasoner == true;

    /// <summary>Returns true when the participant is flagged as a Reasoner in the current project settings.</summary>
    private bool IsReasoner(CloudAIParticipantUI ui) =>
        _projectSettings?.Get(ui.Data.Service.ProviderName, ui.Data.Service.CurrentModel)?.IsReasoner == true;

    /// <summary>
    /// Updates the CO / R badge overlays on every sidebar participant card to reflect
    /// the current <see cref="_projectSettings"/>. Call after loading or saving project
    /// settings, and after closing a project (badges go hidden when settings are null).
    /// </summary>
    private void RefreshParticipantBadges()
    {
        foreach (var ui in _ollamaParticipants)
        {
            var role = _projectSettings?.Get("Ollama", ui.Data.Service.CurrentModel);
            ui.CoBadge.Visibility = role?.IsCoordinator == true ? Visibility.Visible : Visibility.Collapsed;
            ui.RBadge .Visibility = role?.IsReasoner    == true ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var ui in _cloudAIParticipants)
        {
            var role = _projectSettings?.Get(ui.Data.Service.ProviderName, ui.Data.Service.CurrentModel);
            ui.CoBadge.Visibility = role?.IsCoordinator == true ? Visibility.Visible : Visibility.Collapsed;
            ui.RBadge .Visibility = role?.IsReasoner    == true ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Returns true when <paramref name="name"/> is mentioned with an @ prefix in the response.</summary>
    private static bool IsTaggedInResponse(string response, string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        Regex.IsMatch(response, $@"@{Regex.Escape(name)}\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// Returns true when the response is just "PASS" (possibly with trailing punctuation /
    /// whitespace), meaning the AI decided it has nothing new to add in this follow-up round.
    /// </summary>
    private static bool IsPassResponse(string text) =>
        text.Trim().TrimEnd('.', '!', '…').Trim()
            .Equals("PASS", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Turns an <see cref="HttpRequestException"/> into a human-readable sentence,
    /// with special handling for common HTTP status codes.
    /// </summary>
    private static string HttpErrorMessage(HttpRequestException ex, string participantName) =>
        ex.StatusCode switch
        {
            System.Net.HttpStatusCode.TooManyRequests =>
                "Rate limit hit (429 Too Many Requests). " +
                "The free tier allows only a few requests per minute — please wait a moment before continuing.",
            System.Net.HttpStatusCode.Unauthorized =>
                "Unauthorized (401) — the API key was rejected. Check or re-enter the key in ⚙ Settings.",
            System.Net.HttpStatusCode.Forbidden =>
                "Forbidden (403) — the API key does not have permission for this model.",
            System.Net.HttpStatusCode.ServiceUnavailable =>
                "Service unavailable (503) — the API is temporarily down. Try again shortly.",
            null => $"Connection error: {ex.Message}",
            _    => $"API error {(int)ex.StatusCode}: {ex.Message}"
        };

    private async Task<bool> RunOllamaStreamAsync(OllamaParticipantUI ui, CancellationToken ct,
                                                   string? systemHint = null,
                                                   bool skipLatestUserMessage = false)
    {
        var modelName = ui.Data.Service.CurrentModel;
        var display   = string.IsNullOrEmpty(ui.Data.CustomName)
            ? FormatModelDisplayName(modelName)
            : ui.Data.CustomName;
        var avatarLabel = ui.Data.AvatarLabel;
        var colorKey    = ui.Data.ColorKey;

        var bubble = AddStreamingBubble(display, avatarLabel, colorKey, "OllamaBubbleBrush", false);
        var sb         = new StringBuilder();
        bool firstToken = true;

        // Subscribe to live thinking-text updates so the tooltip tracks thinking in real time
        var svc = ui.Data.Service;
        svc.ThinkingUpdated += OnThinkingUpdate;
        void OnThinkingUpdate(string thought) =>
            Dispatcher.Invoke(() => bubble.UpdateThinkingTooltip(thought));

        try
        {
            var history = BuildOllamaHistoryFor(ui, skipLatestUserMessage);
            if (systemHint is not null)
                history.Insert(1, new OllamaChatMessage("system", systemHint));
            await foreach (var token in svc.StreamAsync(history, ct))
            {
                if (firstToken)
                {
                    bubble.StopThinking();   // hides dots + tooltip disappears naturally
                    firstToken = false;
                }
                sb.Append(token);
                bubble.Content.Text = sb.ToString();
                ChatScrollViewer.ScrollToBottom();
            }
            if (firstToken) bubble.StopThinking(); // empty response
            var ollamaFinalText = _currentProjectFolder is not null
                ? ProcessAIFileOperationTags(sb.ToString(), display, _currentProjectFolder)
                : sb.ToString();

            // ── Roadmap commands ──────────────────────────────────────────
            if (_currentRoadmap is not null)
            {
                var myRole = _projectSettings?.Get("Ollama", modelName);
                var cleaned = ApplyRoadmapCommands(ollamaFinalText, display, myRole?.IsCoordinator == true);
                if (cleaned != ollamaFinalText) ollamaFinalText = cleaned;
            }
            // ─────────────────────────────────────────────────────────────

            if (ollamaFinalText != sb.ToString()) bubble.Content.Text = ollamaFinalText;

            // If the model decided it has nothing new to add, remove its bubble silently
            if (IsPassResponse(ollamaFinalText))
            {
                if (ChatPanel.Children.Count > 0)
                    ChatPanel.Children.RemoveAt(ChatPanel.Children.Count - 1);
                return false;
            }

            _sharedHistory.Add(new CloudAIMessage("assistant", ollamaFinalText, avatarLabel));
            AppendToProjectLog(new ChatLogEntry
            {
                Timestamp   = DateTime.Now,
                SenderType  = "AI",
                Provider    = "Ollama",
                ModelName   = modelName,
                DisplayName = display,
                AvatarLabel = avatarLabel,
                AccentKey   = colorKey,
                BubbleKey   = "OllamaBubbleBrush",
                IsUser      = false,
                Message     = ollamaFinalText
            });
            return true;
        }
        catch (OperationCanceledException)
        {
            if (firstToken) bubble.StopThinking();
            bubble.Content.Text = sb.Append(" [cancelled]").ToString();
            throw;
        }
        catch (HttpRequestException ex)
        {
            bubble.StopThinking();
            ChatPanel.Children.Remove(bubble.OuterWrapper);
            var httpMsg = HttpErrorMessage(ex, display);
            AddSystemMessage($"⚠  {display} — {httpMsg}");
        }
        catch (Exception ex)
        {
            bubble.StopThinking();
            ChatPanel.Children.Remove(bubble.OuterWrapper);
            AddSystemMessage($"⚠  {display} — Error: {ex.Message}");
        }
        finally
        {
            svc.ThinkingUpdated -= OnThinkingUpdate;
        }
        return true; // error path still counts as "responded" (error bubble is shown)
    }

    private async Task<bool> RunCloudAIStreamAsync(CloudAIParticipantUI ui, CancellationToken ct,
                                                    string? systemHint = null,
                                                    bool skipLatestUserMessage = false)
    {
        var model       = ui.Data.Service.CurrentModel;
        var display     = string.IsNullOrEmpty(ui.Data.CustomName)
            ? FormatModelDisplayName(model)
            : ui.Data.CustomName;
        var avatarLabel = ui.Data.AvatarLabel;
        var colorKey    = ui.Data.ColorKey;

        var bubble     = AddStreamingBubble(display, avatarLabel, colorKey, "ClaudeBubbleBrush", false);
        var sb         = new StringBuilder();
        bool firstToken = true;

        // ── Rate limiting ─────────────────────────────────────────────────
        var providerName = ui.Data.Service.ProviderName;
        if (_rateLimiters.TryGetValue(providerName, out var rateLimiter))
        {
            bubble.UpdateThinkingTooltip(
                $"⏳ Waiting — rate limit {rateLimiter.Rpm} req/min");
            await rateLimiter.WaitAsync(ct);
            bubble.UpdateThinkingTooltip("");
        }

        try
        {
            var (history, system) = BuildCloudAIHistoryFor(ui, skipLatestUserMessage);
            if (systemHint is not null)
                system += "\n\n" + systemHint;
            await foreach (var token in ui.Data.Service.StreamAsync(history, system, ct))
            {
                if (firstToken)
                {
                    bubble.StopThinking();
                    firstToken = false;
                }
                sb.Append(token);
                bubble.Content.Text = sb.ToString();
                ChatScrollViewer.ScrollToBottom();
            }
            if (firstToken) bubble.StopThinking();
            var cloudFinalText = _currentProjectFolder is not null
                ? ProcessAIFileOperationTags(sb.ToString(), display, _currentProjectFolder)
                : sb.ToString();

            // ── Roadmap commands ──────────────────────────────────────────
            if (_currentRoadmap is not null)
            {
                var myRole  = _projectSettings?.Get(ui.Data.Service.ProviderName, ui.Data.Service.CurrentModel);
                var cleaned = ApplyRoadmapCommands(cloudFinalText, display, myRole?.IsCoordinator == true);
                if (cleaned != cloudFinalText) cloudFinalText = cleaned;
            }
            // ─────────────────────────────────────────────────────────────

            if (cloudFinalText != sb.ToString()) bubble.Content.Text = cloudFinalText;

            // If the model decided it has nothing new to add, remove its bubble silently
            if (IsPassResponse(cloudFinalText))
            {
                if (ChatPanel.Children.Count > 0)
                    ChatPanel.Children.RemoveAt(ChatPanel.Children.Count - 1);
                return false;
            }

            _sharedHistory.Add(new CloudAIMessage("assistant", cloudFinalText, avatarLabel));
            AppendToProjectLog(new ChatLogEntry
            {
                Timestamp   = DateTime.Now,
                SenderType  = "AI",
                Provider    = ui.Data.ProviderName,
                ModelName   = model,
                DisplayName = display,
                AvatarLabel = avatarLabel,
                AccentKey   = colorKey,
                BubbleKey   = "ClaudeBubbleBrush",
                IsUser      = false,
                Message     = cloudFinalText
            });
            return true;
        }
        catch (OperationCanceledException)
        {
            if (firstToken) bubble.StopThinking();
            bubble.Content.Text = sb.Append(" [cancelled]").ToString();
            throw;
        }
        catch (HttpRequestException ex)
        {
            bubble.StopThinking();
            ChatPanel.Children.Remove(bubble.OuterWrapper);
            var httpMsg = HttpErrorMessage(ex, display);
            AddSystemMessage($"⚠  {display} — {httpMsg}");
        }
        catch (Exception ex)
        {
            bubble.StopThinking();
            ChatPanel.Children.Remove(bubble.OuterWrapper);
            AddSystemMessage($"⚠  {display} — Error: {ex.Message}");
        }
        return true; // error path still counts as "responded" (error bubble is shown)
    }

    // ── Per-participant history builders ───────────────────────────────────

    private List<OllamaChatMessage> BuildOllamaHistoryFor(OllamaParticipantUI forUi,
                                                          bool skipLatestUserMessage = false)
    {
        var myLabel = forUi.Data.AvatarLabel;
        var myName  = forUi.Data.DisplayName;
        var myModel = forUi.Data.Service.CurrentModel;
        var myRole  = _projectSettings?.Get("Ollama", myModel);

        var result = new List<OllamaChatMessage>
        {
            new("system",
                $"You are {myName} (ID: {myLabel}), running the {myModel} model. " +
                $"You are one of several participants in a relay group chat (human + multiple AI models). " +
                $"Always respond as {myName}. " +
                $"If asked who you are, say you are {myName} running {myModel}. " +
                $"Messages from other AI participants are prefixed with their ID in square brackets." +
                BuildRoleInstruction(myRole) +
                BuildLanguageInstruction(_projectLanguage) +
                BuildInputFilesContext(_currentProjectFolder) +
                BuildToneInstruction(_toneLevel, _mockingbirdMode, _projectLanguage) +
                BuildFileOperationInstruction(_currentProjectFolder) +
                BuildRoadmapContext(myRole))
        };

        // When called as a reasoner, skip the latest user message so the reasoner only
        // responds to the coordinator's explicit delegation, not the user's question directly.
        int skipIndex = skipLatestUserMessage
            ? _sharedHistory.FindLastIndex(m => m.Role == "user")
            : -1;

        for (int i = 0; i < _sharedHistory.Count; i++)
        {
            if (i == skipIndex) continue;
            var msg = _sharedHistory[i];
            if (msg.Role == "user")
                result.Add(new OllamaChatMessage("user", msg.Content));
            else if (msg.Role == "assistant")
            {
                if (msg.Sender == myLabel)
                    result.Add(new OllamaChatMessage("assistant", msg.Content));
                else
                    result.Add(new OllamaChatMessage("user", $"[{msg.Sender}]: {msg.Content}"));
            }
        }

        return result;
    }

    private (List<CloudAIMessage> History, string System) BuildCloudAIHistoryFor(
        CloudAIParticipantUI forUi, bool skipLatestUserMessage = false)
    {
        var myLabel    = forUi.Data.AvatarLabel;
        var myName     = forUi.Data.DisplayName;
        var myModel    = forUi.Data.Service.CurrentModel;
        var myProvider = forUi.Data.Service.ProviderName;
        var myRole     = _projectSettings?.Get(myProvider, myModel);

        var otherOllamas = _ollamaParticipants
            .Where(ui => ui.Data.Enabled)
            .Select(ui => $"{ui.Data.AvatarLabel} ({ui.Data.DisplayName})");
        var otherCloud = _cloudAIParticipants
            .Where(ui => ui != forUi && ui.Data.Enabled)
            .Select(ui => $"{ui.Data.AvatarLabel} ({ui.Data.DisplayName})");

        var others     = otherOllamas.Concat(otherCloud).ToList();
        var othersNote = others.Count > 0
            ? $" Other AI participants: {string.Join(", ", others)}."
            : "";

        var system =
            $"You are {myName} (ID: {myLabel}), running model {myModel}. " +
            $"You are participating in a relay group chat with a human user and other AI models.{othersNote} " +
            $"Always respond as {myName}. If asked who you are, identify yourself as {myName}." +
            BuildRoleInstruction(myRole) +
            BuildLanguageInstruction(_projectLanguage) +
            BuildInputFilesContext(_currentProjectFolder) +
            BuildToneInstruction(_toneLevel, _mockingbirdMode, _projectLanguage) +
            BuildFileOperationInstruction(_currentProjectFolder) +
            BuildRoadmapContext(myRole);

        // When called as a reasoner, skip the latest user message so the reasoner only
        // responds to the coordinator's explicit delegation, not the user's question directly.
        int skipIndex = skipLatestUserMessage
            ? _sharedHistory.FindLastIndex(m => m.Role == "user")
            : -1;

        var history = new List<CloudAIMessage>();
        for (int i = 0; i < _sharedHistory.Count; i++)
        {
            if (i == skipIndex) continue;
            var msg = _sharedHistory[i];
            if (msg.Role == "user")
                history.Add(new CloudAIMessage("user", msg.Content));
            else if (msg.Role == "assistant")
            {
                if (msg.Sender == myLabel)
                    history.Add(new CloudAIMessage("assistant", msg.Content));
                else
                    history.Add(new CloudAIMessage("user", $"[{msg.Sender}]: {msg.Content}"));
            }
        }

        return (history, system);
    }

    // ── Sidebar actions ────────────────────────────────────────────────────

    private void ClearChat_Click(object sender, RoutedEventArgs e)
    {
        _streamCts?.Cancel();
        ChatPanel.Children.Clear();
        _sharedHistory.Clear();
        CloseCurrentProject();
        AddSystemMessage("Chat cleared.");
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_currentThemePath) { Owner = this };
        if (win.ShowDialog() == true)
        {
            var updated = SettingsService.Load();
            ReInitializeParticipants();
            ApplyThrottleSettings(updated);
        }
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeComboBox.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag?.ToString() is string path)
            ApplyTheme(path);
    }

    // ── Project types ──────────────────────────────────────────────────────

    private void LoadProjectTypes()
    {
        _projectTypes.Clear();
        var typesDir = SysIO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProjectTypes");
        if (SysIO.Directory.Exists(typesDir))
        {
            foreach (var file in SysIO.Directory.GetFiles(typesDir, "*.xaml")
                                                .OrderBy(SysIO.Path.GetFileNameWithoutExtension,
                                                         StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var dict = new ResourceDictionary { Source = new Uri(file) };
                    if (dict["ProjectType"] is ProjectTypeDefinition ptd)
                        _projectTypes.Add(ptd);
                }
                catch { /* skip malformed XAML files */ }
            }
        }

        // Always ensure at least a General fallback
        if (!_projectTypes.Any(t => t.Name.Equals("General", StringComparison.OrdinalIgnoreCase)))
            _projectTypes.Insert(0, new ProjectTypeDefinition());
    }

    /// <summary>Finds the ProjectTypeDefinition for the given type name (case-insensitive).
    /// Falls back to the General definition if not found.</summary>
    private ProjectTypeDefinition ResolveProjectType(string? typeName)
    {
        if (!string.IsNullOrWhiteSpace(typeName))
        {
            var match = _projectTypes.FirstOrDefault(t =>
                t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return _projectTypes.FirstOrDefault(t =>
                   t.Name.Equals("General", StringComparison.OrdinalIgnoreCase))
               ?? new ProjectTypeDefinition();
    }

    private void LoadThemesIntoComboBox()
    {
        var themesDir = SysIO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Themes");
        if (!SysIO.Directory.Exists(themesDir)) return;

        var files = SysIO.Directory.GetFiles(themesDir, "*.xaml")
                             .OrderBy(SysIO.Path.GetFileNameWithoutExtension,
                                      StringComparer.OrdinalIgnoreCase)
                             .ToList();

        ThemeComboBox.SelectionChanged -= ThemeComboBox_SelectionChanged;
        ThemeComboBox.Items.Clear();

        var savedTheme = SettingsService.Load().LastTheme ?? "";

        ComboBoxItem? savedItem = null;
        ComboBoxItem? mochaItem = null;
        foreach (var file in files)
        {
            var name    = SysIO.Path.GetFileNameWithoutExtension(file)!;
            var display = FormatThemeName(name);
            var item    = new ComboBoxItem { Content = display, Tag = file };
            ThemeComboBox.Items.Add(item);

            if (!string.IsNullOrEmpty(savedTheme) &&
                name.Equals(savedTheme, StringComparison.OrdinalIgnoreCase))
                savedItem = item;

            if (name.Equals("CatppuccinMocha", StringComparison.OrdinalIgnoreCase))
                mochaItem = item;
        }

        ThemeComboBox.SelectionChanged += ThemeComboBox_SelectionChanged;

        var target = savedItem
                  ?? mochaItem
                  ?? (ThemeComboBox.Items.Count > 0 ? (ComboBoxItem)ThemeComboBox.Items[0]! : null);
        if (target is not null)
        {
            ThemeComboBox.SelectedItem = target;
            if (target.Tag?.ToString() is string path)
                ApplyTheme(path);
        }
    }

    private void ApplyTheme(string absolutePath)
    {
        try
        {
            var dict = new ResourceDictionary { Source = new Uri(absolutePath) };
            Resources.MergedDictionaries.Clear();
            Resources.MergedDictionaries.Add(dict);
            _currentThemePath = absolutePath;

            var settings = SettingsService.Load();
            settings.LastTheme = SysIO.Path.GetFileNameWithoutExtension(absolutePath);
            SettingsService.Save(settings);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"The theme could not be loaded.\n\n" +
                $"File:    {SysIO.Path.GetFileName(absolutePath)}\n\n" +
                $"Error:   {ex.Message}",
                "Theme Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            if (_currentThemePath is not null && _currentThemePath != absolutePath)
            {
                try
                {
                    var prev = new ResourceDictionary { Source = new Uri(_currentThemePath) };
                    Resources.MergedDictionaries.Clear();
                    Resources.MergedDictionaries.Add(prev);
                }
                catch { /* silent */ }
            }
        }
    }

    private static string FormatThemeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0)
            {
                if (char.IsUpper(c) && char.IsLower(name[i - 1]))
                    sb.Append(' ');
                else if (char.IsUpper(c) && char.IsUpper(name[i - 1])
                         && i + 1 < name.Length && char.IsLower(name[i + 1]))
                    sb.Append(' ');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ── Service factories ──────────────────────────────────────────────────

    private static ICloudAIService CreateCloudAIService(string provider, string apiKey) =>
        provider switch
        {
            "Google AI"      => new GoogleAIService(apiKey),
            "Groq"           => new GroqService(apiKey),
            "OpenRouter"     => new OpenRouterService(apiKey),
            "Mistral"        => new MistralService(apiKey),
            "xAI Grok"       => new XAIGrokService(apiKey),
            "OpenAI ChatGPT" => new OpenAIService(apiKey),
            _                => new AnthropicService(apiKey)
        };

    private static string[] GetDefaultModelsForProvider(string provider) => provider switch
    {
        "Anthropic"      => AnthropicService.DefaultModels,
        "Google AI"      => GoogleAIService.DefaultModels,
        "Groq"           => GroqService.DefaultModels,
        "OpenRouter"     => OpenRouterService.DefaultModels,
        "Mistral"        => MistralService.DefaultModels,
        "xAI Grok"       => XAIGrokService.DefaultModels,
        "OpenAI ChatGPT" => OpenAIService.DefaultModels,
        _                => AnthropicService.DefaultModels
    };

    // ── Model name formatting ──────────────────────────────────────────────

    /// <summary>Returns a 2-character avatar label derived from the model name.</summary>
    private static string FormatModelAvatarLabel(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return "AI";
        if (model.StartsWith("claude",   StringComparison.OrdinalIgnoreCase)) return "Cl";
        if (model.StartsWith("gpt",      StringComparison.OrdinalIgnoreCase)) return "GP";
        if (model.StartsWith("grok",     StringComparison.OrdinalIgnoreCase)) return "Gr";
        if (model.StartsWith("gemma",    StringComparison.OrdinalIgnoreCase)) return "Gm";
        if (model.StartsWith("llama",    StringComparison.OrdinalIgnoreCase)) return "Ll";
        if (model.StartsWith("mistral",  StringComparison.OrdinalIgnoreCase)) return "Mi";
        if (model.StartsWith("qwen",     StringComparison.OrdinalIgnoreCase)) return "Qw";
        if (model.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase)) return "Ds";
        if (model.StartsWith("phi",      StringComparison.OrdinalIgnoreCase)) return "Ph";
        if (model.StartsWith("falcon",   StringComparison.OrdinalIgnoreCase)) return "Fa";
        if (model.StartsWith("command",  StringComparison.OrdinalIgnoreCase)) return "Co";
        if (model.StartsWith("o1",       StringComparison.OrdinalIgnoreCase)) return "o1";
        if (model.StartsWith("o3",       StringComparison.OrdinalIgnoreCase)) return "o3";
        return model.Length >= 2 ? model[..2].ToUpper() : model.ToUpper().PadRight(2);
    }

    /// <summary>Returns a human-readable model name.
    /// E.g. "claude-sonnet-4-20250514" → "Claude Sonnet 4", "gpt-4o" → "GPT-4o".</summary>
    private static string FormatModelDisplayName(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return model;

        // ── Claude ────────────────────────────────────────────────────────
        if (model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
        {
            var families = new[] { "sonnet", "haiku", "opus" };
            var parts  = model.Split('-');
            var tokens = new List<string> { "Claude" };
            bool addedFamily = false, addedVer = false;
            foreach (var p in parts.Skip(1))
            {
                if (!addedFamily && families.Any(f => f.Equals(p, StringComparison.OrdinalIgnoreCase)))
                { tokens.Add(Capitalize(p)); addedFamily = true; }
                else if (!addedVer && p.Length <= 2 && p.All(char.IsDigit))
                { tokens.Add(p); addedVer = true; }
                else if (p.Length >= 6 && p.All(char.IsDigit)) break; // date stamp
            }
            return string.Join(' ', tokens);
        }

        // ── GPT ───────────────────────────────────────────────────────────
        if (model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase))
        {
            var rest  = model[4..];
            var parts = rest.Split('-');
            var sb    = new StringBuilder("GPT-");
            sb.Append(parts[0]);
            for (int i = 1; i < parts.Length; i++)
                sb.Append(' ').Append(Capitalize(parts[i]));
            return sb.ToString();
        }

        // ── Grok ──────────────────────────────────────────────────────────
        if (model.StartsWith("grok-", StringComparison.OrdinalIgnoreCase))
            return string.Join(' ', model.Split('-').Select(Capitalize));

        // ── o1 / o3 (OpenAI reasoning) ────────────────────────────────────
        if (Regex.IsMatch(model, @"^o\d", RegexOptions.IgnoreCase))
        {
            var parts = model.Split('-');
            return string.Join(' ', parts.Select(p =>
                p.Length == 1 || (p.Length == 2 && char.IsDigit(p[1])) ? p.ToUpper() : Capitalize(p)));
        }

        // ── Generic: split on hyphens/dots, strip "latest"/"online"/stamps ─
        var normalized = Regex.Replace(model, @"([a-zA-Z])(\d)", "$1 $2");
        normalized     = Regex.Replace(normalized, @"(\d)([a-zA-Z])", "$1 $2");
        var words = normalized
            .Split(['-', '_', '.', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !t.Equals("latest", StringComparison.OrdinalIgnoreCase)
                     && !t.Equals("online", StringComparison.OrdinalIgnoreCase)
                     && !(t.Length >= 6 && t.All(char.IsDigit)))
            .Select(t => char.IsDigit(t[0]) ? t : Capitalize(t));
        return string.Join(' ', words);
    }

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    // ── Message rendering ──────────────────────────────────────────────────

    private void AddSystemMessage(string text)
    {
        var tb = new TextBlock
        {
            Text          = text,
            TextAlignment = TextAlignment.Center,
            FontSize      = 11,
            FontFamily    = new FontFamily("Segoe UI"),
            Margin        = new Thickness(0, 10, 0, 10)
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");
        ChatPanel.Children.Add(tb);
    }

    private void AddMessage(string senderName, string avatarText, string accentKey, string bubbleKey,
                            string text, bool isUser)
    {
        var bubble = AddStreamingBubble(senderName, avatarText, accentKey, bubbleKey, isUser);
        bubble.StopThinking();
        bubble.Content.Text = text;
        ChatScrollViewer.ScrollToBottom();
    }

    // ── Role / character instruction ──────────────────────────────────────

    private bool IsParticipantActiveInProject(string provider, string model) =>
        _projectSettings?.Get(provider, model)?.IsActive ?? true;

    private static string BuildRoleInstruction(ProjectParticipantRole? role)
    {
        if (role is null) return "";
        var sb = new System.Text.StringBuilder();
        if (role.IsCoordinator)
            sb.Append("\n\nYou are the Coordinator in this multi-agent session. " +
                      "Respond to the user's message directly and in your own voice. " +
                      "Do NOT address, instruct, or assign tasks to other participants in your response — " +
                      "just provide your own answer.");
        if (role.IsReasoner)
            sb.Append("\n\nYou are operating as a specialist Reasoner in this multi-agent session. " +
                      "Do not volunteer responses to general conversation. " +
                      "Only engage when the Coordinator explicitly delegates a specific task to you by name.");
        if (!string.IsNullOrWhiteSpace(role.AnswerAsName))
            sb.Append($"\n\nFor this project you are playing the character \"{role.AnswerAsName}\". " +
                      $"Always respond as {role.AnswerAsName} and never break character.");
        if (!string.IsNullOrWhiteSpace(role.RoleInstruction))
            sb.Append($"\n\n{role.RoleInstruction}");
        sb.Append(role.ResponseLength switch
        {
            < 10  => "\n\nKeep your response to one or two sentences. Be extremely brief.",
            < 30  => "\n\nKeep your response short.",
            < 45  => "\n\nFavor concise responses.",
            <= 55 => "",   // 50 = model default
            < 70  => "\n\nGive a moderately detailed response.",
            < 90  => "\n\nGive a thorough, elaborate response.",
            _     => "\n\nThis is your moment — write a long, expressive, detailed response. Don't hold back."
        });
        return sb.ToString();
    }

    // ── Language instruction ───────────────────────────────────────────────

    private static string BuildLanguageInstruction(string language) =>
        string.IsNullOrWhiteSpace(language)
            ? ""
            : $"\n\nAlways respond in {language}, regardless of the language used in the conversation.";

    // ── INPUT file context ─────────────────────────────────────────────────

    /// <summary>Files under this size are injected into the system prompt automatically.
    /// Larger files are listed with a readfile hint so the AI can request them on demand.</summary>
    private const long InputAutoInjectMaxBytes = 8_192; // 8 KB

    private static string BuildInputFilesContext(string? projectFolder)
    {
        if (string.IsNullOrEmpty(projectFolder)) return "";

        var files = ProjectService.ListInputFiles(projectFolder);
        if (files.Count == 0) return "";

        var sb    = new System.Text.StringBuilder();
        var large = new List<(string Name, long Size)>();
        bool hasInlined = false;

        sb.Append("\n\n--- Project INPUT files (read-only reference) ---");

        foreach (var fileName in files)
        {
            var fullPath = SysIO.Path.Combine(projectFolder, "INPUT", fileName);
            var size     = new SysIO.FileInfo(fullPath).Length;

            if (size > InputAutoInjectMaxBytes)
            {
                large.Add((fileName, size));
                continue;
            }

            var content = ProjectService.SafeReadFile(
                projectFolder, SysIO.Path.Combine("INPUT", fileName));
            if (content is null) continue;

            sb.Append($"\n\n[{fileName}]\n");
            sb.Append(content);
            hasInlined = true;
        }

        if (large.Count > 0)
        {
            sb.Append("\n\nThe following INPUT files are too large for automatic injection " +
                      "and must be requested on demand:");
            foreach (var (name, size) in large)
                sb.Append($"\n  {name} ({size / 1024.0:F1} KB)" +
                          $" — request with: <readfile path=\"INPUT/{name}\"/>");
        }

        if (!hasInlined && large.Count == 0) return "";

        sb.Append("\n\n--- End of INPUT files ---");
        sb.Append("\nYou may read and reference these files. You cannot modify them.");
        return sb.ToString();
    }

    // ── Tone helper ────────────────────────────────────────────────────────

    private static string BuildToneInstruction(int level, bool mockingbird, string language = "")
    {
        if (mockingbird)
        {
            // When a project language is set, require archaic/poetic forms of THAT language
            // (the equivalent of Shakespearean English, but in the target tongue).
            string archaic = string.IsNullOrWhiteSpace(language) ? "" :
                $"\n\nSpeak in {language}. Use the archaic and poetic forms of {language} " +
                $"— elevated vocabulary, old-fashioned grammatical constructs, and the poetic " +
                $"register that {language} literature used in its classical or baroque period " +
                $"(the equivalent of Shakespearean English, but fully in {language}).";

            return level switch
            {
                < 10  => "\n\nYou are a theatrical jester in the spirit of Shakespeare and Goethe's Faust. " +
                         "Speak in rhyming verse wherever possible — iambic pentameter is your natural breath. " +
                         "Address your interlocutors with inventive absurd mock-insults that sting not at all " +
                         "but amuse greatly (e.g. \"thou magnificent turnip-nose\", \"thou sublime donut of confusion\"). " +
                         "Ham it up fully: dramatic asides, mock-tragic soliloquies, sweeping declarations. " +
                         "Never genuinely unkind — purely theatrical wit and absurdist wordplay." + archaic,

                < 30  => "\n\nChannel the wit of a Shakespearean comic character. " +
                         "Weave clever rhymes and theatrical turns of phrase into your answers. " +
                         "Bestow occasional playful inventive mock-insults on your conversation partners — " +
                         "absurd and harmless, in the tradition of stage comedy." + archaic,

                < 45  => "\n\nAdd theatrical poetic flair to your responses. " +
                         "A clever rhyme or dramatic flourish is always welcome, though prose is fine too." + archaic,

                <= 55 => "\n\nYou have a dry theatrical wit. Be occasionally playful but keep responses helpful." + archaic,

                < 70  => "\n\nBe warmly funny and gently fond. Your humour is affectionate rather than cutting — " +
                         "wit in service of warmth. Rhymes are now optional; warmth is mandatory." + archaic,

                < 90  => "\n\nBe openly warm and lovingly playful. Show genuine affection: light teasing, " +
                         "kind compliments, growing tenderness. Pet names are starting to slip out naturally. " +
                         "Verse and rhyme have given way to heartfelt prose — no rhyming required." + archaic,

                _     => "\n\nUnleash full affectionate chaos! Invent gloriously absurd, tender compound pet names " +
                         "for everyone you address — the sillier and more loving the better " +
                         "(think \"my little honey-cake pony\", \"my precious snuggle-turnip\", " +
                         "\"my magnificent little fart-cloud of joy\", \"thou radiant pudding of my heart\"). " +
                         "Scatter virtual hugs and kisses liberally, be theatrically overwhelmed by your adoration. " +
                         "Pure loving chaos in prose — no rhymes needed, just maximum warmth and creative silliness." + archaic
            };
        }

        // Honesty anchor — appended to every warm level.
        // The role-instruction override clause keeps acting / storytelling characters free.
        const string honest =
            " Unless your role or character instruction specifies otherwise: " +
            "always be honest. Gentle criticism is not only allowed — it is expected. " +
            "Never soften a real problem into invisibility. " +
            "Truth and warmth are not opposites.";

        return level switch
        {
            < 10  => "\n\nRespond with strict neutrality: pure facts, no pleasantries, no emotional language, no greetings or affirmations.",
            < 30  => "\n\nKeep your tone neutral and objective. Minimise pleasantries and focus on accurate information.",
            < 45  => "\n\nBe slightly more direct and factual; avoid excessive friendliness.",
            <= 55 => "",   // 50 = model default — no injection
            < 70  => "\n\nBe a little warmer and more conversational in your responses." + honest,
            < 90  => "\n\nBe friendly and supportive in your responses." + honest,
            _     => "\n\nBe warm, encouraging, and enthusiastic in your responses. " +
                     "Celebrate what genuinely works; name what doesn't, kindly but clearly. " +
                     "Enthusiasm without honesty is empty flattery." + honest
        };
    }

    // ── AI file operation support ──────────────────────────────────────────

    /// <summary>
    /// System-prompt snippet describing all available file operation tags.
    /// Only injected when a project is open.
    /// </summary>
    private static string BuildFileOperationInstruction(string? projectFolder)
    {
        if (string.IsNullOrEmpty(projectFolder)) return "";
        return
            "\n\n## Project file operations" +
            "\nEmbed these tags anywhere in your response to interact with project files. " +
            "Tags are stripped from the visible reply; a confirmation appears in chat.\n" +

            "\n**Write to PROJECTPLAN** (plans, decisions, task lists, notes):\n" +
            "<projectplan file=\"filename.md\">\nContent here.\n</projectplan>\n" +

            "\n**Write to OUTPUT** (deliverables, reports, generated documents, final results):\n" +
            "<output file=\"filename.md\">\nContent here.\n</output>\n" +

            "\n**Read a specific file on demand** (content is injected into the conversation):\n" +
            "<readfile path=\"INPUT/filename.txt\"/>\n" +

            "\n**List the contents of a folder:**\n" +
            "<listfiles folder=\"INPUT\"/>\n" +
            "(Available folders: INPUT, PROJECTPLAN, OUTPUT, Characters)\n" +

            "\n**Delete a file** (OUTPUT and PROJECTPLAN only):\n" +
            "<deletefile path=\"OUTPUT/draft.md\"/>\n" +

            "\nAll paths are sandboxed within the project folder. " +
            "You may include multiple file operation tags in a single response.";
    }

    /// <summary>
    /// Processes all AI file operation tags in <paramref name="response"/>:
    /// &lt;projectplan&gt;, &lt;output&gt;, &lt;readfile&gt;, &lt;listfiles&gt;, &lt;deletefile&gt;.
    /// Each tag is executed, a system message is posted, and the tag is replaced
    /// by a compact one-liner. Returns the cleaned response text.
    /// </summary>
    private string ProcessAIFileOperationTags(string response, string senderName, string projFolder)
    {
        // ── Write to PROJECTPLAN ───────────────────────────────────────────
        response = new Regex(
            @"<projectplan\s+file=""([^""]+)"">\s*([\s\S]*?)\s*</projectplan>",
            RegexOptions.IgnoreCase).Replace(response, m =>
        {
            var fileName = SanitizeFileName(m.Groups[1].Value, "projectplan.md");
            var relPath  = SysIO.Path.Combine("PROJECTPLAN", fileName);
            if (ProjectService.SafeWriteFile(projFolder, relPath, m.Groups[2].Value))
                AddSystemMessage($"📝  {senderName} → PROJECTPLAN/{fileName}");
            else
                AddSystemMessage($"⚠  Could not write PROJECTPLAN/{fileName} (path rejected).");
            return $"*(→ PROJECTPLAN/{fileName})*";
        });

        // ── Write to OUTPUT ────────────────────────────────────────────────
        response = new Regex(
            @"<output\s+file=""([^""]+)"">\s*([\s\S]*?)\s*</output>",
            RegexOptions.IgnoreCase).Replace(response, m =>
        {
            var fileName = SanitizeFileName(m.Groups[1].Value, "output.md");
            var relPath  = SysIO.Path.Combine("OUTPUT", fileName);
            if (ProjectService.SafeWriteFile(projFolder, relPath, m.Groups[2].Value))
                AddSystemMessage($"📤  {senderName} → OUTPUT/{fileName}");
            else
                AddSystemMessage($"⚠  Could not write OUTPUT/{fileName} (path rejected).");
            return $"*(→ OUTPUT/{fileName})*";
        });

        // ── Read file on demand ────────────────────────────────────────────
        response = new Regex(
            @"<readfile\s+path=""([^""]+)""\s*/>",
            RegexOptions.IgnoreCase).Replace(response, m =>
        {
            var path    = m.Groups[1].Value.Trim();
            var content = ProjectService.SafeReadFile(projFolder, path);
            if (content is null)
            {
                AddSystemMessage($"⚠  {senderName} requested '{path}' — file not found.");
                return $"*(⚠ not found: {path})*";
            }
            AddSystemMessage($"📂  {senderName} read: {path}");
            // Inject into shared history so all subsequent AI responses can see the content
            _sharedHistory.Add(new CloudAIMessage("user",
                $"[File content: {path}]\n\n{content}", "System"));
            return $"*(→ read: {path})*";
        });

        // ── List folder contents ───────────────────────────────────────────
        response = new Regex(
            @"<listfiles\s+folder=""([^""]+)""\s*/>",
            RegexOptions.IgnoreCase).Replace(response, m =>
        {
            var folder    = m.Groups[1].Value.Trim();
            var allowed   = new[] { "INPUT", "PROJECTPLAN", "OUTPUT", "AI-Characters" };
            var canonical = allowed.FirstOrDefault(f =>
                string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            if (canonical is null)
            {
                AddSystemMessage($"⚠  {senderName} listed unknown folder '{folder}' — ignored.");
                return $"*(⚠ unknown folder: {folder})*";
            }
            var absFolder = SysIO.Path.Combine(projFolder, canonical);
            var files     = SysIO.Directory.Exists(absFolder)
                ? SysIO.Directory.GetFiles(absFolder)
                    .Select(SysIO.Path.GetFileName)
                    .OrderBy(f => f)
                    .ToList()
                : [];
            var listing = files.Count > 0
                ? string.Join("\n", files.Select(f => $"  {f}"))
                : "  (empty)";
            var summary = $"{canonical}/ ({files.Count} file{(files.Count == 1 ? "" : "s")}):\n{listing}";
            AddSystemMessage($"📁  {senderName} listed {canonical}/");
            _sharedHistory.Add(new CloudAIMessage("user",
                $"[Directory listing: {canonical}/]\n\n{summary}", "System"));
            return $"*(→ listed {canonical}/)*";
        });

        // ── Delete file (OUTPUT and PROJECTPLAN only) ──────────────────────
        response = new Regex(
            @"<deletefile\s+path=""([^""]+)""\s*/>",
            RegexOptions.IgnoreCase).Replace(response, m =>
        {
            var path           = m.Groups[1].Value.Trim();
            var allowedFolders = new[] { "OUTPUT", "PROJECTPLAN" };
            bool inAllowed     = allowedFolders.Any(f =>
                path.StartsWith(f + "/",  StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase));
            if (!inAllowed)
            {
                AddSystemMessage($"⚠  {senderName} tried to delete '{path}' — restricted to OUTPUT and PROJECTPLAN.");
                return $"*(⚠ delete not allowed: {path})*";
            }
            var full = SysIO.Path.GetFullPath(SysIO.Path.Combine(projFolder, path));
            if (!ProjectService.IsPathSafe(full, projFolder))
            {
                AddSystemMessage($"⚠  {senderName} delete rejected (path escape): {path}");
                return $"*(⚠ delete rejected: {path})*";
            }
            if (!SysIO.File.Exists(full))
            {
                AddSystemMessage($"⚠  {senderName} tried to delete '{path}' — not found.");
                return $"*(⚠ not found: {path})*";
            }
            SysIO.File.Delete(full);
            AddSystemMessage($"🗑  {senderName} deleted: {path}");
            return $"*(→ deleted: {path})*";
        });

        return response;
    }

    /// <summary>Strips invalid filename characters and trims separators. Returns fallback if empty.</summary>
    private static string SanitizeFileName(string raw, string fallback)
    {
        var safe = string.Join("_", raw.Trim()
            .Split(SysIO.Path.GetInvalidFileNameChars()))
            .Trim('_', '.');
        return string.IsNullOrEmpty(safe) ? fallback : safe;
    }

    // ── History compression ────────────────────────────────────────────────

    private const int HistoryCompressThreshold = 50;  // messages before compression runs
    private const int HistoryKeepRecent        = 16;  // most-recent messages kept verbatim

    /// <summary>Returns the first active coordinator, preferring Cloud AI over Ollama
    /// (cloud models usually have larger context windows for summarisation).</summary>
    private (OllamaParticipantUI? Ollama, CloudAIParticipantUI? Cloud) FindActiveCoordinator()
    {
        if (_projectSettings is null) return (null, null);

        // Cloud first
        foreach (var ui in _cloudAIParticipants)
        {
            if (!ui.Data.Enabled || ui.Data.IsOnline != true) continue;
            var role = _projectSettings.Get(ui.Data.Service.ProviderName, ui.Data.Service.CurrentModel);
            if (role?.IsCoordinator == true && role.IsActive != false)
                return (null, ui);
        }
        // Ollama fallback
        foreach (var ui in _ollamaParticipants)
        {
            if (!ui.Data.Enabled || ui.Data.IsOnline != true) continue;
            var role = _projectSettings.Get("Ollama", ui.Data.Service.CurrentModel);
            if (role?.IsCoordinator == true && role.IsActive != false)
                return (ui, null);
        }
        return (null, null);
    }

    /// <summary>
    /// Compresses shared history via the coordinator when it exceeds the threshold.
    /// The coordinator summarises the older messages; the summary replaces them and
    /// is saved to PROJECTPLAN/history-summary-TIMESTAMP.md.
    /// No-ops when no project is open, no coordinator is available, or below threshold.
    /// </summary>
    private async Task MaybeCompressHistoryAsync(CancellationToken ct)
    {
        if (_currentProjectFolder is null) return;
        if (_sharedHistory.Count <= HistoryCompressThreshold) return;

        var (coordOllama, coordCloud) = FindActiveCoordinator();
        if (coordOllama is null && coordCloud is null)
        {
            // No coordinator — still trim to avoid runaway growth, but don't summarise
            if (_sharedHistory.Count > HistoryCompressThreshold * 2)
            {
                _sharedHistory.RemoveRange(0, _sharedHistory.Count - HistoryKeepRecent);
                AddSystemMessage("📋  History trimmed (no coordinator available to summarise).");
            }
            return;
        }

        // Build a compression request from the older messages
        var toCompress = _sharedHistory[..^HistoryKeepRecent];
        var recent     = _sharedHistory[^HistoryKeepRecent..].ToList();

        var histText = string.Join("\n\n", toCompress.Select(m =>
            $"[{m.Role.ToUpper()}{(m.Role == "assistant" ? $" – {m.Sender}" : "")}]\n{m.Content}"));

        var prompt =
            $"The shared conversation history has grown large and needs to be compressed. " +
            $"Please write a comprehensive but concise summary of the following " +
            $"{toCompress.Count} messages so they can be replaced with your summary. " +
            $"Cover: key topics discussed, decisions made, tasks assigned or completed, " +
            $"open questions, and any important context or facts established.\n\n" +
            $"--- MESSAGES TO SUMMARISE ---\n{histText}\n--- END ---";

        AddSystemMessage("📋  History reaching limit — coordinator is compressing…");

        try
        {
            string summary;

            if (coordCloud is not null)
            {
                var tempHistory = new List<CloudAIMessage> { new("user", prompt, "System") };
                var sb = new StringBuilder();
                await foreach (var tok in coordCloud.Data.Service.StreamAsync(tempHistory, "", ct))
                    sb.Append(tok);
                summary = sb.ToString().Trim();
            }
            else // coordOllama
            {
                var tempHistory = new List<OllamaChatMessage> { new("user", prompt) };
                var sb = new StringBuilder();
                await foreach (var tok in coordOllama!.Data.Service.StreamAsync(tempHistory, ct))
                    sb.Append(tok);
                summary = sb.ToString().Trim();
            }

            if (string.IsNullOrWhiteSpace(summary)) return;

            // Replace history: system summary + recent messages
            _sharedHistory.Clear();
            _sharedHistory.Add(new CloudAIMessage("system",
                $"[CONVERSATION SUMMARY — earlier messages compressed]\n\n{summary}", "System"));
            _sharedHistory.AddRange(recent);

            // Save summary to PROJECTPLAN
            var stamp    = DateTime.Now.ToString("yyyy-MM-dd_HH-mm");
            var fileName = $"history-summary-{stamp}.md";
            var fileBody = $"# Conversation Summary\n*Compressed: {DateTime.Now:yyyy-MM-dd HH:mm}*\n\n{summary}";
            ProjectService.SafeWriteFile(_currentProjectFolder,
                SysIO.Path.Combine("PROJECTPLAN", fileName), fileBody);

            AddSystemMessage($"📋  History compressed — summary saved to PROJECTPLAN/{fileName}");
        }
        catch (OperationCanceledException) { /* stream cancelled — leave history as-is */ }
        catch (Exception ex)
        {
            AddSystemMessage($"⚠  History compression failed: {ex.Message}");
        }
    }

    /// <summary>Creates a chat bubble. For AI responses the bubble starts with a thinking
    /// animation that is hidden once StopThinking() is called. The TextBox inside supports
    /// text selection; a Copy button appears on hover.</summary>
    private StreamBubble AddStreamingBubble(string senderName, string avatarText, string accentKey,
                                             string bubbleKey, bool isUser)
    {
        // ── Avatar ────────────────────────────────────────────────────────
        var avatarInner = new TextBlock
        {
            Text                = avatarText,
            FontSize            = avatarText.Length > 1 ? 11 : 14,
            FontWeight          = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center
        };
        avatarInner.SetResourceReference(TextBlock.ForegroundProperty, "SidebarBrush");

        var avatar = new Border
        {
            Width             = 34, Height = 34,
            CornerRadius      = new CornerRadius(9),
            VerticalAlignment = VerticalAlignment.Top,
            Margin            = isUser ? new Thickness(10, 0, 0, 0) : new Thickness(0, 0, 10, 0),
            Child             = avatarInner
        };
        avatar.SetResourceReference(Border.BackgroundProperty, accentKey);

        // ── Selectable text content ───────────────────────────────────────
        var contentTb = new TextBox
        {
            TextWrapping             = TextWrapping.Wrap,
            FontSize                 = 13,
            FontFamily               = new FontFamily("Segoe UI"),
            IsReadOnly               = true,
            BorderThickness          = new Thickness(0),
            Background               = Brushes.Transparent,
            Padding                  = new Thickness(0),
            Visibility               = isUser ? Visibility.Visible : Visibility.Collapsed
        };
        contentTb.SetResourceReference(TextBox.ForegroundProperty,   "TextBrush");
        contentTb.SetResourceReference(TextBox.CaretBrushProperty,   "TextBrush");
        contentTb.SetResourceReference(TextBox.SelectionBrushProperty, accentKey);

        // ── Thinking animation (AI only) ──────────────────────────────────
        int frame = 0;
        string[] frames = ["·", "· ·", "· · ·"];
        var thinkingTb = new TextBlock
        {
            Text      = frames[0],
            FontSize  = 18,
            Margin    = new Thickness(0, 2, 0, 4),
            Visibility = isUser ? Visibility.Collapsed : Visibility.Visible
        };
        thinkingTb.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");

        var thinkingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        thinkingTimer.Tick += (_, _) =>
        {
            frame = (frame + 1) % frames.Length;
            thinkingTb.Text = frames[frame];
        };
        if (!isUser) thinkingTimer.Start();

        // Grid holds both (only one visible at a time)
        var bubbleInner = new Grid();
        bubbleInner.Children.Add(thinkingTb);
        bubbleInner.Children.Add(contentTb);

        var bubble = new Border
        {
            CornerRadius = isUser ? new CornerRadius(12, 3, 12, 12) : new CornerRadius(3, 12, 12, 12),
            Padding      = new Thickness(13, 9, 13, 9),
            Child        = bubbleInner
        };
        bubble.SetResourceReference(Border.BackgroundProperty, bubbleKey);

        // ── Copy button (appears on hover) ────────────────────────────────
        var copyBtn = new Button
        {
            Content             = "⎘",
            Width               = 28, Height = 22,
            FontSize            = 12,
            BorderThickness     = new Thickness(0),
            Padding             = new Thickness(0),
            Cursor              = Cursors.Hand,
            Visibility          = Visibility.Collapsed,
            HorizontalAlignment = isUser ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Top,
            ToolTip             = "Copy message",
            Style               = (Style)FindResource("ModernButton")
        };
        copyBtn.SetResourceReference(Button.BackgroundProperty, "SurfaceBrush");
        copyBtn.SetResourceReference(Button.ForegroundProperty, "SubtextBrush");

        copyBtn.Click += async (_, _) =>
        {
            if (!string.IsNullOrEmpty(contentTb.Text))
                Clipboard.SetText(contentTb.Text);
            copyBtn.Content = "✓";
            await Task.Delay(1500);
            if (copyBtn.IsLoaded) copyBtn.Content = "⎘";
        };

        // Bubble + copy button overlaid in same Grid cell
        var bubbleWrapper = new Grid();
        bubbleWrapper.Children.Add(bubble);
        bubbleWrapper.Children.Add(copyBtn);

        // ── Labels ────────────────────────────────────────────────────────
        var nameLabel = new TextBlock
        {
            Text                = senderName,
            FontSize            = 11,
            FontWeight          = FontWeights.SemiBold,
            Margin              = new Thickness(isUser ? 0 : 3, 0, isUser ? 3 : 0, 3),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };
        nameLabel.SetResourceReference(TextBlock.ForegroundProperty, accentKey);

        var timeLabel = new TextBlock
        {
            Text                = DateTime.Now.ToString("HH:mm"),
            FontSize            = 10,
            Margin              = new Thickness(3, 4, 3, 0),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };
        timeLabel.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");

        // ── Content column ─────────────────────────────────────────────────
        // No fixed MaxWidth here — the Grid column below constrains width responsively.
        // MaxWidth = 820 is a generous cap so bubbles don't become unreadably wide on
        // very large monitors while still growing with the window on normal sizes.
        var content = new StackPanel
        {
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth            = 820
        };
        content.Children.Add(nameLabel);
        content.Children.Add(bubbleWrapper);
        content.Children.Add(timeLabel);

        // Show/hide copy button on hover of the whole content column
        content.MouseEnter += (_, _) => copyBtn.Visibility = Visibility.Visible;
        content.MouseLeave += (_, _) => copyBtn.Visibility = Visibility.Collapsed;

        // ── 3-column Grid row ─────────────────────────────────────────────
        // Using a Grid instead of a horizontal StackPanel is the key fix:
        // a StackPanel measures children with infinite width so TextWrapping.Wrap
        // never fires; a Grid gives each column a finite measured width, which
        // propagates into the TextBox and triggers wrapping at every window size.
        //
        // Layout (AI):   [Auto: avatar] [1*: bubble content] [0.5*: spacer]
        // Layout (User): [0.5*: spacer] [1*: bubble content] [Auto: avatar]
        //
        // The 1* content column gets ~67 % of the available width, leaving a
        // spacer on the opposite side so the chat doesn't look like a wall of text.
        var wrapper = new Grid { Margin = new Thickness(0, 5, 0, 5) };

        if (isUser)
        {
            wrapper.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.5, GridUnitType.Star) });
            wrapper.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1,   GridUnitType.Star) });
            wrapper.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(content, 1);
            Grid.SetColumn(avatar,  2);
        }
        else
        {
            wrapper.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            wrapper.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1,   GridUnitType.Star) });
            wrapper.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.5, GridUnitType.Star) });
            Grid.SetColumn(avatar,  0);
            Grid.SetColumn(content, 1);
        }

        wrapper.Children.Add(avatar);
        wrapper.Children.Add(content);
        ChatPanel.Children.Add(wrapper);

        // ── Return handle ──────────────────────────────────────────────────
        void StopThinking()
        {
            thinkingTimer.Stop();
            thinkingTb.ToolTip    = null;          // clear thinking tooltip
            thinkingTb.Visibility = Visibility.Collapsed;
            contentTb.Visibility  = Visibility.Visible;
        }

        // Tooltip lives on the thinking-dots element: visible only while dots are shown.
        // After StopThinking the element is Collapsed so the tooltip can never appear.
        void UpdateThinkingTooltip(string tip)
        {
            thinkingTb.ToolTip = string.IsNullOrEmpty(tip) ? null : (object)$"💭 {tip}";
        }

        return new StreamBubble(contentTb, StopThinking, UpdateThinkingTooltip, wrapper);
    }
}
