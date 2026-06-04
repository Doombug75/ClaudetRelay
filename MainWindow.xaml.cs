using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SysIO = System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
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
    /// OuterWrapper is the root Grid added to ChatPanel - remove it to erase the bubble.</summary>
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
        public string?  Mood       { get; set; }          // one-word mood, updated every 5 responses
        public int      ResponseCount { get; set; }       // counts completed visible responses

        public string ColorKey    => Position switch { 2 => "AccentBgBrush", 3 => "PrimaryAccentBrush", _ => "SecondaryAccentBrush" };

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
        public required Border            CrBadge       { get; init; }
        public required Border            PlBadge       { get; init; }
        public required Border            RsBadge       { get; init; }
        public required Border            WrBadge       { get; init; }
        public required StackPanel        BadgeRow      { get; init; }
        public required TextBlock         NameLabel     { get; init; }
        public required Ellipse           StatusDot     { get; init; }
        public required TextBlock         ModelLabel    { get; init; }
        public required TextBlock         OfflineLabel  { get; init; }
        public required Border            ErrorBadge    { get; init; }
        public required TextBlock         StatusLabel   { get; init; }
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
        public string?  Mood       { get; set; }          // one-word mood, updated every 5 responses
        public int      ResponseCount { get; set; }       // counts completed visible responses

        public string ColorKey => Position switch { 2 => "AccentBgBrush", 3 => "SecondaryAccentBrush", _ => "PrimaryAccentBrush" };

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
                "Ollama ☁"       => "O☁",
                _                => Service.ProviderName.Length >= 2
                                        ? Service.ProviderName[..2]
                                        : Service.ProviderName
            }
            : FormatModelAvatarLabel(Service.CurrentModel);

        public string ProviderName => Service.ProviderName;

        public string DisplayName => !string.IsNullOrEmpty(CustomName)  ? CustomName
                                  : !string.IsNullOrEmpty(Service.CurrentModel) ? Service.CurrentModel
                                  : Service.ProviderName;
    }

    private sealed class CloudAIParticipantUI
    {
        public required CloudAIParticipant Data          { get; init; }
        public required Border             Card          { get; init; }
        public required Border             AvatarBorder  { get; init; }
        public required TextBlock          AvatarText    { get; init; }
        public required Border             CoBadge       { get; init; }
        public required Border             RBadge        { get; init; }
        public required Border             CrBadge       { get; init; }
        public required Border             PlBadge       { get; init; }
        public required Border             RsBadge       { get; init; }
        public required Border             WrBadge       { get; init; }
        public required StackPanel         BadgeRow      { get; init; }
        public required TextBlock          NameLabel     { get; init; }
        public required Ellipse            StatusDot     { get; init; }
        public required TextBlock          ModelLabel    { get; init; }
        public required TextBlock          OfflineLabel  { get; init; }
        public required Border             ErrorBadge    { get; init; }
        public required TextBlock          StatusLabel   { get; init; }
        public required Popup              Popup         { get; init; }
        public required TextBlock          PopupTitle    { get; init; }
        public required CheckBox           EnabledToggle { get; init; }
        public required Button             RemoveButton  { get; init; }
    }

    /// <summary>Describes a single slot mismatch between the project's saved participant
    /// list and the current global configuration.</summary>
    private readonly record struct ParticipantMismatch(
        int                Slot,
        string             ProjectName,
        string             ProjectType,
        string             ProjectModel,
        string             GlobalDesc,           // human-readable description of what is globally at this slot
        ParticipantConfig? GlobalReplacement);   // the actual global participant at this slot, if any

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
    private int                                  _chattinessLevel       = 50;
    private bool                                 _mockingbirdMode       = false;
    private double                               _chatBubbleWidthPct    = 78.0;
    private string                               _projectLanguage       = "";
    private int                                  _maxDialogDepth        = 1;
    private bool                                 _aiDialogueEnabled     = false;
    private int                                  _aiDialogueMaxTurns    = 10;
    private int                                  _globalResponseLength  = 50;
    private ProjectSettings?                     _projectSettings;
    private ParticipantsWindow?                  _participantsWindow;

    // ── Project state ──────────────────────────────────────────────────────
    private string?                    _currentProjectFolder;
    private ProjectSettings?           _currentProject;   // same object as _projectSettings
    private ProjectTypeDefinition?     _currentProjectType;
    private Roadmap?                   _currentRoadmap;
    /// <summary>Cached SuperRoles for the open project. Null = not loaded yet or no file.</summary>
    private Dictionary<string, (string Title, string Instruction)>? _superRoles;
    private string?                    _selectedProjectFolder; // selected in Projects list
    private DateTime?                  _sessionStartTime;      // set on OpenProject, cleared on close
    private bool                       _workSessionFired;      // prevents double-greeting per open
    private string                     _projectSortMode = "LastOpened"; // "Alphabetical" or "LastOpened"

    // ── Project types (loaded from ProjectTypes/*.xaml) ────────────────────
    private List<ProjectTypeDefinition> _projectTypes = [];

    // ──────────────────────────────────────────────────────────────────────

    public MainWindow()
    {
        InitializeComponent();
        LoadThemesIntoComboBox();
        Loaded += async (_, _) =>
        {
            ApplyTitleBarTheme();                    // colour the OS title bar to match the theme
            StartClaudetteBlinkAnimation();          // draw attention to the Claudette button
            LoadProjectTypes();
            InitializeServices();
            var s = SettingsService.Load();
            _chatBubbleWidthPct = s.ChatBubbleWidthPercent;
            ApplyChatFont(s);                        // seed font resources before first bubble
            UpdateChatBubbleWidth();                 // seed bubble-width resource
            ApplyUiZoom(s.UiZoom);                   // scale all UI elements

            // ── Restore general chat history ──────────────────────────────
            var savedLog = GeneralChatLogService.LoadRecentLog();
            if (savedLog.Count > 0)
            {
                foreach (var entry in savedLog)
                    RenderChatLogEntry(entry);
                AddSystemMessage($"Chat resumed  ·  {savedLog.Count} messages loaded.");
                ChatScrollViewer.ScrollToBottom();
            }
            else
            {
                var anyParticipants = _ollamaParticipants.Count + _cloudAIParticipants.Count > 0;
                AddSystemMessage(anyParticipants
                    ? "Chat ready."
                    : "Welcome to ClaudetRelay!  Configure participants to get started - see the hint in the sidebar. 🐙");
            }

            InputTextBox.Focus();
            await CheckAllStatusAsync();
            StartStatusTimer();
        };
        // Recalculate bubble MaxWidth whenever the chat area resizes (e.g. window drag / maximize).
        //
        // ChatScrollViewer.SizeChanged is the primary trigger for window resize / maximize / restore:
        // the ScrollViewer is sized directly by the Grid column and fires reliably on every width change.
        // Both SizeChanged events call UpdateChatBubbleWidth() so bubbles reflow on every resize.
        // We always read ChatPanel.ActualWidth directly (most accurate; avoids scrollbar-width
        // error from the "e.NewSize.Width − 40" approach).
        // ChatScrollViewer.SizeChanged catches the initial window-show; ChatPanel.SizeChanged
        // catches subsequent height-only changes (new bubble added, same window width).
        ChatScrollViewer.SizeChanged += (_, _) => UpdateChatBubbleWidth();
        ChatPanel.SizeChanged        += (_, _) => UpdateChatBubbleWidth();
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

        // Create participants from settings (welcome hint handles the no-participants state)
        foreach (var p in settings.Participants.Where(p => p.Enabled))
        {
            if (p.Type == "Ollama")
                AddOllamaParticipant(p.Model, p.ServerUrl, p.Name);
            else
                AddCloudAIParticipant(p.Type, p.Model, p.Name);
        }

        // User display name & tone
        _userName          = string.IsNullOrWhiteSpace(settings.UserName) ? "You" : settings.UserName.Trim();
        _toneLevel         = settings.ToneLevel;
        _chattinessLevel   = settings.GlobalChattiness;
        _mockingbirdMode   = settings.MockingbirdMode;

        // AI dialogue toggle + depth
        _aiDialogueEnabled    = settings.AiDialogueEnabled;
        _aiDialogueMaxTurns   = Math.Clamp(settings.AiDialogueMaxTurns, 3, 100);
        _globalResponseLength = Math.Clamp(settings.GlobalResponseLength, 0, 100);
        UpdateAiDialogueButton();

        // Rate limiters
        ApplyThrottleSettings(settings);

        // Show welcome hint when nothing is configured yet
        RefreshWelcomeHint();
    }

    /// <summary>
    /// Rebuilds the per-provider rate-limiter table from saved settings.
    /// Call once on startup and again whenever the settings window is saved.
    /// </summary>
    private void ApplyThrottleSettings(AppSettings settings)
    {
        _rateLimiters.Clear();

        // Per-participant rate limits keyed by "type|model".
        // When two participants share the same type+model the most permissive limit wins
        // (they share an API budget anyway — the tighter one would block both).
        foreach (var p in settings.Participants.Where(p => p.RpmEnabled && p.Rpm >= 1))
        {
            var key = $"{p.Type}|{p.Model}";
            if (!_rateLimiters.TryGetValue(key, out var existing) || existing.Rpm < p.Rpm)
                _rateLimiters[key] = new ProviderRateLimiter(p.Rpm);
        }
    }

    // ── Re-initialize after Settings save ─────────────────────────────────

    private void ReInitializeParticipants()
    {
        _streamCts?.Cancel();

        // Remove Cloud AI cards
        foreach (var ui in _cloudAIParticipants.ToList())
        {
            ParticipantsPanel.Children.Remove(ui.Popup);
            ParticipantsPanel.Children.Remove(ui.Card);
            ui.Data.Service.Dispose();
        }
        _cloudAIParticipants.Clear();

        // Remove Ollama cards
        foreach (var ui in _ollamaParticipants.ToList())
        {
            ParticipantsPanel.Children.Remove(ui.Popup);
            ParticipantsPanel.Children.Remove(ui.Card);
        }
        _ollamaParticipants.Clear();
        _availableOllamaModels.Clear();

        // Re-add from settings
        var settings = SettingsService.Load();
        _userName             = string.IsNullOrWhiteSpace(settings.UserName) ? "You" : settings.UserName.Trim();
        _toneLevel            = settings.ToneLevel;
        _chattinessLevel      = settings.GlobalChattiness;
        _mockingbirdMode      = settings.MockingbirdMode;
        _aiDialogueEnabled    = settings.AiDialogueEnabled;
        _aiDialogueMaxTurns   = Math.Clamp(settings.AiDialogueMaxTurns, 3, 100);
        _globalResponseLength = Math.Clamp(settings.GlobalResponseLength, 0, 100);
        UpdateAiDialogueButton();

        foreach (var p in settings.Participants.Where(p => p.Enabled))
        {
            if (p.Type == "Ollama")
                AddOllamaParticipant(p.Model, p.ServerUrl, p.Name);
            else
                AddCloudAIParticipant(p.Type, p.Model, p.Name);
        }

        UpdateAddRemoveButtons();
        UpdateCloudAIAddRemoveButtons();
        RefreshWelcomeHint();
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

        // Walk the unified panel in visual (slot) order so the saved list always
        // matches top-to-bottom card order, regardless of participant type.
        foreach (FrameworkElement child in ParticipantsPanel.Children)
        {
            var cloud = _cloudAIParticipants.FirstOrDefault(u => ReferenceEquals(u.Card, child));
            if (cloud is not null)
            {
                saved.Add(new ParticipantConfig
                {
                    Name      = cloud.Data.CustomName ?? "",
                    Type      = cloud.Data.Service.ProviderName,
                    Model     = cloud.Data.Service.CurrentModel,
                    ServerUrl = "",
                    Enabled   = cloud.Data.Enabled
                });
                continue;
            }

            var ollama = _ollamaParticipants.FirstOrDefault(u => ReferenceEquals(u.Card, child));
            if (ollama is not null)
            {
                saved.Add(new ParticipantConfig
                {
                    Name      = ollama.Data.CustomName ?? "",
                    Type      = "Ollama",
                    Model     = ollama.Data.Service.CurrentModel,
                    ServerUrl = ollama.Data.Service.BaseUrl,
                    Enabled   = ollama.Data.Enabled
                });
            }
        }

        _projectSettings.ActiveParticipants = saved;
        try { ProjectService.SaveProject(_currentProjectFolder!, _projectSettings); }
        catch { /* non-fatal - settings will re-save next time */ }
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
            ParticipantsPanel.Children.Remove(ui.Popup);
            ParticipantsPanel.Children.Remove(ui.Card);
            ui.Data.Service.Dispose();
        }
        _cloudAIParticipants.Clear();

        // Remove Ollama
        foreach (var ui in _ollamaParticipants.ToList())
        {
            ParticipantsPanel.Children.Remove(ui.Popup);
            ParticipantsPanel.Children.Remove(ui.Card);
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
            if (p.Enabled) continue; // already enabled - skip
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
        }

        UpdateAddRemoveButtons();
        UpdateCloudAIAddRemoveButtons();
        RefreshWelcomeHint();
        _ = CheckAllStatusAsync();
    }

    // ── Tab switching ──────────────────────────────────────────────────────

    private void ChatTabButton_Click(object sender, RoutedEventArgs e)
        => ActivateTab(Tab.Chat);

    private void ChatViewButton_Click(object sender, RoutedEventArgs e)
    {
        // Collapse all project sub-panels and return to the chat view
        ShowWorldPanel(false);
        ShowRoadmapPanel(false);
        ShowFilesPanel(false);
    }

    private void ProjectsTabButton_Click(object sender, RoutedEventArgs e)
    {
        // Warn if a bridge project is loaded — concurrent roadmap writes could conflict.
        if (_bridgeProjectFolder is not null)
        {
            var name = _bridgeProject?.ProjectName ?? System.IO.Path.GetFileName(_bridgeProjectFolder);
            var result = MessageBox.Show(
                $"Bridge mode has \"{name}\" loaded.\n\n" +
                "Opening the same project here could cause conflicting roadmap writes.\n\n" +
                "Open a different project, or unload the bridge project first (Bridge → ✕).\n\n" +
                "Continue to Projects anyway?",
                "Bridge Project Active",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
        }
        RefreshProjectList();
        ActivateTab(Tab.Projects);
    }

    private void BridgeTabButton_Click(object sender, RoutedEventArgs e)
    {
        // Warn if a project is already open in Chat/Project mode —
        // Bridge runs its own independent MCP session and a simultaneously
        // open project here can cause conflicting saves to the same files.
        if (_currentProjectFolder is not null)
        {
            var name   = _currentProject?.ProjectName ?? System.IO.Path.GetFileName(_currentProjectFolder);
            var result = MessageBox.Show(
                $"Project \"{name}\" is currently open in Chat mode.\n\n" +
                "Bridge mode connects external AI clients (Claude Code, Claude Desktop) to " +
                "their own independent project session. Running both on the same project " +
                "at the same time can cause conflicting saves to roadmap and chat files.\n\n" +
                "Consider closing the project first (✕ Close in the header), or use " +
                "Bridge with a different project.\n\n" +
                "Open Bridge anyway?",
                "Project Already Open",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
        }

        ActivateTab(Tab.Bridge);
        BuildBridgeContent();
    }

    private enum Tab { Chat, Projects, Bridge }

    private void ActivateTab(Tab tab)
    {
        ShowRoadmapPanel(false);   // collapse any project overlay panels
        ShowFilesPanel(false);
        ShowWorldPanel(false);

        var isChat     = tab == Tab.Chat;
        var isProjects = tab == Tab.Projects;
        var isBridge   = tab == Tab.Bridge;

        // Content panels
        ChatHeader      .Visibility = isChat     ? Visibility.Visible   : Visibility.Collapsed;
        ChatHeaderSep   .Visibility = isChat     ? Visibility.Visible   : Visibility.Collapsed;
        ChatScrollViewer.Visibility = isChat     ? Visibility.Visible   : Visibility.Collapsed;
        InputArea       .Visibility = isChat     ? Visibility.Visible   : Visibility.Collapsed;
        ProjectsContent .Visibility = isProjects ? Visibility.Visible   : Visibility.Collapsed;
        BridgeContent   .Visibility = isBridge   ? Visibility.Visible   : Visibility.Collapsed;

        // Tab button states
        void SetTab(Button btn, bool active)
        {
            btn.SetResourceReference(Button.BackgroundProperty,
                active ? "ControlBgBrush" : "Transparent");
            btn.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            btn.SetResourceReference(Button.ForegroundProperty,
                active ? "ContentTextBrush" : "ContentDimBrush");
        }
        SetTab(ChatTabButton,     isChat);
        SetTab(ProjectsTabButton, isProjects);
        SetTab(BridgeTabButton,   isBridge);

        // Dim the Projects button and show a tooltip while a bridge project is loaded
        if (_bridgeProjectFolder is not null && !isProjects)
        {
            var name = _bridgeProject?.ProjectName ?? "project";
            ProjectsTabButton.Opacity = 0.45;
            ProjectsTabButton.ToolTip = $"Bridge has \"{name}\" loaded — open with care to avoid roadmap conflicts.";
        }
        else
        {
            ProjectsTabButton.Opacity = 1.0;
            ProjectsTabButton.ToolTip = null;
        }

        // Refresh sort button states when projects tab is shown
        if (isProjects) UpdateProjectSortButtons();
    }

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
            ToolTip             = "Project settings",
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
        var exportHtml = new MenuItem { Header = "🔄  Export Chat History as HTML…" };
        var exportMd   = new MenuItem { Header = "📝  Export Chat History as Markdown…" };
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

    private void ExportProject(string projFolder, ProjectSettings meta, string format)
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
            Title                 = "Choose Project Type",
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
            Content    = "Create Project",
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
            Content    = "⚙  Manage Types…",
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
        if (loaded is null) { MessageBox.Show("Could not read project file.", "Error"); return; }

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
            Title                 = "⚠  Participant Mismatch",
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
            Content = "👤  Participant Settings",
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
            Content = "⚙  Project Settings",
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
            Title                 = "⚙  Fix Project Participants",
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
                        Content = "🔄 Apply",
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
                    Content = "⏸ Disable",
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
                    Content = "🗑 Remove",
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
            Content   = "💾  Save & Load Project",
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


    // World-building editor methods live in MainWindow.World.cs


    // ── Bridge / MCP panel ────────────────────────────────────────────────

    private McpServer?    _mcpServer;
    private bool                   _controllerRunning     = false;
    private string                 _controllerChatHistory = "";   // display text — survives panel rebuilds
    private ModelControllerRunner? _controllerRunner;             // kept alive to preserve API conversation history

    // ── Bridge project state ───────────────────────────────────────────────
    // Independent of the main chat's open project (_currentProjectFolder).
    // Set by bridge_open_project; external MCP clients work with this context.
    private string?          _bridgeProjectFolder;
    private ProjectSettings? _bridgeProject;
    private Roadmap?         _bridgeRoadmap;

    // ── Bridge project roster state ────────────────────────────────────────
    // When the user loads a project's saved agent roster in the Bridge panel,
    // we swap the global BridgeAgents out and restore them on project close.
    private bool              _bridgeUsingProjectRoster;
    private List<BridgeAgent>? _globalBridgeAgentsBackup;

    /// <summary>
    /// Switches Bridge agents to the given project roster, saving the global roster first
    /// so it can be restored later with <see cref="RestoreGlobalBridgeAgentsIfNeeded"/>.
    /// </summary>
    private void ActivateProjectBridgeRoster(List<BridgeAgent> projectAgents)
    {
        var cfg = SettingsService.Load();
        _globalBridgeAgentsBackup = [.. cfg.BridgeAgents];   // snapshot current global
        cfg.BridgeAgents          = [.. projectAgents];       // apply project roster
        SettingsService.Save(cfg);
        _bridgeUsingProjectRoster = true;
        BuildBridgeContent();
    }

    /// <summary>
    /// If the project roster is active, restores the global Bridge agents and rebuilds the panel.
    /// Safe to call when no project roster is active (no-op).
    /// </summary>
    private void RestoreGlobalBridgeAgentsIfNeeded()
    {
        if (!_bridgeUsingProjectRoster || _globalBridgeAgentsBackup is null) return;
        var cfg = SettingsService.Load();
        cfg.BridgeAgents = _globalBridgeAgentsBackup;
        SettingsService.Save(cfg);
        _bridgeUsingProjectRoster  = false;
        _globalBridgeAgentsBackup  = null;
        BuildBridgeContent();
    }

    /// <summary>
    /// The project folder visible to Bridge tools — bridge-specific if explicitly opened,
    /// otherwise falls back to the main chat's open project (if any).
    /// </summary>
    private string?          BridgeProjectFolder => _bridgeProjectFolder ?? _currentProjectFolder;
    private ProjectSettings? BridgeProject       => _bridgeProject       ?? _currentProject;
    private Roadmap?         BridgeRoadmap       => _bridgeRoadmap       ?? _currentRoadmap;
    private static readonly System.Net.Http.HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        DefaultRequestHeaders = { { "User-Agent", "ClaudetRelay-Bridge/1.0" } }
    };
    private StackPanel?   _bridgeLogPanel;
    private ScrollViewer? _bridgeLogScroll;
    private readonly List<string> _bridgeLog = [];
    private const int BridgeLogMaxLines = 120;

    // ── Parallel task tracking ─────────────────────────────────────────────
    private sealed class BridgeTask
    {
        public string    Id          { get; } = Guid.NewGuid().ToString("N")[..8];
        public string    AgentName   { get; init; } = "";
        public string    OutputFile  { get; init; } = "";
        /// <summary>running | completed | failed | timeout</summary>
        public string    Status      { get; set; } = "running";
        public DateTime  StartedAt   { get; } = DateTime.Now;
        public DateTime? FinishedAt  { get; set; }
        /// <summary>Populated on failed or timeout - contains the original exception message.</summary>
        public string?   Error       { get; set; }
    }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BridgeTask>
        _bridgeTasks = new();

    private static readonly string[] CloudProviders =
    [
        "Anthropic", "Google AI", "Groq", "OpenRouter",
        "Mistral", "xAI Grok", "OpenAI ChatGPT"
    ];

    private void BuildBridgeContent()
    {
        BridgeContent.Children.Clear();
        BridgeContent.RowDefinitions.Clear();
        BridgeContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // mode bar
        BridgeContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // body

        var cfg = SettingsService.Load();

        // ── Mode toggle bar ────────────────────────────────────────────────
        var modeBarWrap = new StackPanel();
        Grid.SetRow(modeBarWrap, 0);
        BridgeContent.Children.Add(modeBarWrap);

        var modeBar = new Border { Padding = new Thickness(18, 10, 18, 10) };
        modeBar.SetResourceReference(Border.BackgroundProperty, "SidebarBgBrush");
        modeBarWrap.Children.Add(modeBar);

        var modeRow = new StackPanel { Orientation = Orientation.Horizontal };
        modeBar.Child = modeRow;

        var modeLbl = new TextBlock
        {
            Text = "Mode:", FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0)
        };
        modeLbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        modeRow.Children.Add(modeLbl);

        Button MakeModeBtn(string label, BridgeAgentMode mode)
        {
            bool active = cfg.BridgeMode == mode;
            var btn = new Button
            {
                Style         = (Style)FindResource("ModernButton"),
                Content       = label, FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
                Padding       = new Thickness(16, 6, 16, 6), Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 0),
                FontWeight    = active ? FontWeights.SemiBold : FontWeights.Normal
            };
            btn.SetResourceReference(Button.BackgroundProperty,
                active ? "ControlHoverBrush" : "ControlBgBrush");
            btn.SetResourceReference(Button.ForegroundProperty,
                active ? "AccentHighlightBrush" : "SidebarDimBrush");
            btn.SetResourceReference(Button.BorderBrushProperty,
                active ? "AccentHighlightBrush" : "ControlBorderBrush");
            btn.Click += (_, _) =>
            {
                var s = SettingsService.Load();
                s.BridgeMode = mode;
                SettingsService.Save(s);
                BuildBridgeContent();
            };
            return btn;
        }

        modeRow.Children.Add(MakeModeBtn("🔌  MCP Server",       BridgeAgentMode.McpServer));
        modeRow.Children.Add(MakeModeBtn("🤖  Model Controller", BridgeAgentMode.ModelController));

        var modeSep = new Rectangle { Height = 1 };
        modeSep.SetResourceReference(Rectangle.FillProperty, "ControlBgBrush");
        modeBarWrap.Children.Add(modeSep);

        // ── Scrollable body ────────────────────────────────────────────────
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(22, 18, 22, 18)
        };
        Grid.SetRow(scroll, 1);
        BridgeContent.Children.Add(scroll);

        var body = new StackPanel();
        scroll.Content = body;

        // ── Mode-specific section ──────────────────────────────────────────
        if (cfg.BridgeMode == BridgeAgentMode.McpServer)
            BuildBridgeMcpSection(body, cfg);
        else
            BuildBridgeControllerSection(body, cfg);

        // Agents & Folders is now on the Setup sub-tab of each mode
    }

    // ── MCP Server section ─────────────────────────────────────────────────

    private void BuildBridgeMcpSection(StackPanel body, AppSettings cfg)
    {
        // ── Sub-tab bar: [▶ Server] [⚙ Setup] ─────────────────────────────
        var subBar = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        subBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        subBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(subBar);

        var serverTabBtn = MakeBridgeSubTabBtn("▶  Server", active: true);
        var setupTabBtn  = MakeBridgeSubTabBtn("⚙  Setup",  active: false);
        Grid.SetColumn(serverTabBtn, 0); Grid.SetColumn(setupTabBtn, 1);
        subBar.Children.Add(serverTabBtn); subBar.Children.Add(setupTabBtn);

        var serverPanel = new StackPanel();
        var setupPanel  = new StackPanel { Visibility = Visibility.Collapsed };
        body.Children.Add(serverPanel);
        body.Children.Add(setupPanel);

        serverTabBtn.Click += (_, _) =>
        {
            serverPanel.Visibility = Visibility.Visible; setupPanel.Visibility = Visibility.Collapsed;
            SetSubTabActive(serverTabBtn, true); SetSubTabActive(setupTabBtn, false);
        };
        setupTabBtn.Click += (_, _) =>
        {
            serverPanel.Visibility = Visibility.Collapsed; setupPanel.Visibility = Visibility.Visible;
            SetSubTabActive(serverTabBtn, false); SetSubTabActive(setupTabBtn, true);
        };

        // ── SERVER panel ───────────────────────────────────────────────────
        var statusRow = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        serverPanel.Children.Add(statusRow);

        var leftStatus = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(leftStatus, 0);
        statusRow.Children.Add(leftStatus);

        var statusDot  = new TextBlock { FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        var statusText = new TextBlock { FontSize = 12, FontFamily = new FontFamily("Segoe UI"), VerticalAlignment = VerticalAlignment.Center };
        if (_mcpServer?.IsRunning == true)
        {
            statusDot.Text  = "●  "; statusDot.SetResourceReference(TextBlock.ForegroundProperty,  "AccentBgBrush");
            statusText.Text = $"Running on http://localhost:{_mcpServer.Port}/sse";
            statusText.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        }
        else
        {
            statusDot.Text  = "○  "; statusDot.SetResourceReference(TextBlock.ForegroundProperty,  "SidebarDimBrush");
            statusText.Text = "Stopped"; statusText.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        }
        leftStatus.Children.Add(statusDot); leftStatus.Children.Add(statusText);

        var portLabel = new TextBlock { Text = "    Port:", FontSize = 12, FontFamily = new FontFamily("Segoe UI"), VerticalAlignment = VerticalAlignment.Center };
        portLabel.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        leftStatus.Children.Add(portLabel);

        var portBox = new TextBox
        {
            Text = cfg.McpPort.ToString(), Width = 60, FontSize = 12,
            FontFamily = new FontFamily("Segoe UI"),
            Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(6, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            IsEnabled = _mcpServer?.IsRunning != true
        };
        portBox.SetResourceReference(TextBox.BackgroundProperty, "ControlBgBrush");
        portBox.SetResourceReference(TextBox.ForegroundProperty, "ContentTextBrush");
        portBox.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        leftStatus.Children.Add(portBox);

        var toggleBtn = new Button
        {
            FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
            Padding = new Thickness(16, 6, 16, 6), Cursor = Cursors.Hand, BorderThickness = new Thickness(0)
        };
        if (_mcpServer?.IsRunning == true)
        {
            toggleBtn.Content = "■  Stop";
            toggleBtn.SetResourceReference(Button.BackgroundProperty, "ControlHoverBrush");
            toggleBtn.SetResourceReference(Button.ForegroundProperty, "AccentHighlightBrush");
        }
        else
        {
            toggleBtn.Content = "▶  Start";
            toggleBtn.SetResourceReference(Button.BackgroundProperty, "AccentBgBrush");
            toggleBtn.SetResourceReference(Button.ForegroundProperty, "SidebarBgBrush");
        }
        toggleBtn.Click += (_, _) =>
        {
            if (_mcpServer?.IsRunning == true)
            {
                _mcpServer.Stop(); _mcpServer.Dispose(); _mcpServer = null;
            }
            else
            {
                if (!int.TryParse(portBox.Text, out int port) || port < 1024 || port > 65535)
                {
                    MessageBox.Show("Please enter a valid port number (1024-65535).",
                        "Invalid Port", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var s = SettingsService.Load(); s.McpPort = port; SettingsService.Save(s);
                _mcpServer = new McpServer(port, BuildMcpTools(BridgeAgentMode.McpServer),
                    line => Dispatcher.Invoke(() => BridgeLog(line)),
                    getInstructions: BuildBridgeInstructions);
                try { _mcpServer.Start(); }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not start MCP server:\n{ex.Message}",
                        "Bridge Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    _mcpServer.Dispose(); _mcpServer = null;
                }
            }
            BuildBridgeContent();
        };
        Grid.SetColumn(toggleBtn, 1);
        statusRow.Children.Add(toggleBtn);

        // ── Bridge project ─────────────────────────────────────────────────
        var projSectionLabel = new TextBlock
        {
            Text = "PROJECT", FontSize = 10, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 10, 0, 4)
        };
        projSectionLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        serverPanel.Children.Add(projSectionLabel);

        var projRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        projRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        projRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        projRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        serverPanel.Children.Add(projRow);

        var projNameTb = new TextBlock
        {
            FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        if (BridgeProject is not null)
        {
            projNameTb.Text = $"📂  {BridgeProject.ProjectName}  [{BridgeProject.ProjectTypeName}]";
            projNameTb.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        }
        else
        {
            projNameTb.Text = "No project loaded";
            projNameTb.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        }
        Grid.SetColumn(projNameTb, 0);
        projRow.Children.Add(projNameTb);

        var loadProjBtn = MakeBridgeSmallBtn("📂  Load Project…");
        loadProjBtn.Margin = new Thickness(8, 0, 0, 0);
        loadProjBtn.Click += (_, _) =>
        {
            var s          = SettingsService.Load();
            var rootFolder = Services.ProjectService.ResolveFolder(s.ProjectsFolder);
            var projects   = Services.ProjectService.ListProjects(rootFolder)
                                 .OrderByDescending(p => p.Settings.LastOpened)
                                 .ToList();

            if (projects.Count == 0)
            {
                MessageBox.Show("No projects found.\nCreate a project in Project mode first.",
                    "No Projects", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Simple picker dialog
            var win = new Window
            {
                Title = "Load Bridge Project", Width = 500,
                SizeToContent = SizeToContent.Height, MaxHeight = 600,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, ResizeMode = ResizeMode.NoResize
            };
            ApplyThemeToDialog(win);
            win.SetResourceReference(Window.BackgroundProperty, "ContentBgBrush");

            var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 20) };
            win.Content = panel;

            var hdr = new TextBlock
            {
                Text = "Select a project to load into Bridge mode:",
                FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 10)
            };
            hdr.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
            panel.Children.Add(hdr);

            var listBox = new ListBox
            {
                MaxHeight = 400, FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
                BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 14)
            };
            listBox.SetResourceReference(ListBox.BackgroundProperty, "ControlBgBrush");
            listBox.SetResourceReference(ListBox.ForegroundProperty, "ContentTextBrush");
            listBox.SetResourceReference(ListBox.BorderBrushProperty, "ControlBorderBrush");

            foreach (var (folder, proj) in projects)
            {
                var item = new ListBoxItem
                {
                    Tag     = folder,
                    Padding = new Thickness(8, 6, 8, 6)
                };
                var itemPanel = new StackPanel();
                var nameTb = new TextBlock
                {
                    Text = $"{proj.ProjectName}  [{proj.ProjectTypeName}]",
                    FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis
                };
                var dateTb = new TextBlock
                {
                    Text = $"Last opened: {proj.LastOpened:yyyy-MM-dd}  ·  {folder}",
                    FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis
                };
                dateTb.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
                itemPanel.Children.Add(nameTb);
                itemPanel.Children.Add(dateTb);
                item.Content = itemPanel;
                listBox.Items.Add(item);
            }
            if (listBox.Items.Count > 0) listBox.SelectedIndex = 0;
            panel.Children.Add(listBox);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            panel.Children.Add(btnRow);

            var cancelBtn = new Button
            {
                Content = "Cancel", Padding = new Thickness(16, 7, 16, 7),
                Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand
            };
            if (TryFindResource("ModernButton") is Style mbs) cancelBtn.Style = mbs;
            cancelBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
            cancelBtn.SetResourceReference(Button.ForegroundProperty, "ControlTextBrush");
            cancelBtn.Click += (_, _) => win.DialogResult = false;
            btnRow.Children.Add(cancelBtn);

            var loadBtn = new Button
            {
                Content = "Load", Padding = new Thickness(16, 7, 16, 7), Cursor = Cursors.Hand
            };
            if (TryFindResource("ModernButton") is Style lbs2) loadBtn.Style = lbs2;
            loadBtn.SetResourceReference(Button.BackgroundProperty, "AccentBgBrush");
            loadBtn.SetResourceReference(Button.ForegroundProperty, "AccentTextBrush");
            loadBtn.Click += (_, _) =>
            {
                if (listBox.SelectedItem is ListBoxItem { Tag: string folder2 })
                    win.DialogResult = true;
            };
            listBox.MouseDoubleClick += (_, _) =>
            {
                if (listBox.SelectedItem is ListBoxItem) win.DialogResult = true;
            };
            btnRow.Children.Add(loadBtn);

            if (win.ShowDialog() == true &&
                listBox.SelectedItem is ListBoxItem { Tag: string chosenFolder })
            {
                var proj2 = Services.ProjectService.LoadProject(chosenFolder);
                if (proj2 is not null)
                {
                    _bridgeProjectFolder = chosenFolder;
                    _bridgeProject       = proj2;
                    _bridgeRoadmap       = RoadmapService.Load(chosenFolder);
                    BridgeLog($"📂  Bridge project loaded: {proj2.ProjectName}");
                    BuildBridgeContent();
                    ActivateTab(Tab.Bridge);   // refresh tab button states (dims Projects)
                    if (ProjectsContent.Visibility == Visibility.Visible) RefreshProjectList();
                }
            }
        };
        Grid.SetColumn(loadProjBtn, 1);
        projRow.Children.Add(loadProjBtn);

        if (BridgeProject is not null)
        {
            var clearProjBtn = MakeBridgeSmallBtn("✕");
            clearProjBtn.Margin  = new Thickness(4, 0, 0, 0);
            clearProjBtn.ToolTip = "Unload bridge project";
            clearProjBtn.Click  += (_, _) =>
            {
                _bridgeProjectFolder = null;
                _bridgeProject       = null;
                _bridgeRoadmap       = null;
                BridgeLog("  Bridge project unloaded.");
                BuildBridgeContent();
                ActivateTab(Tab.Bridge);   // refresh tab button states (restores Projects)
                if (ProjectsContent.Visibility == Visibility.Visible) RefreshProjectList();
            };
            Grid.SetColumn(clearProjBtn, 2);
            projRow.Children.Add(clearProjBtn);
        }

        // Show roadmap summary when a project is loaded
        if (BridgeRoadmap is { } rm && rm.Milestones.Count > 0)
        {
            var rmSummary = new TextBlock
            {
                FontSize = 10, FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap
            };
            rmSummary.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            var parts = rm.Milestones.Select(m =>
            {
                var done  = m.Items.Count(i => i.Status == ItemStatus.Done);
                var total = m.Items.Count;
                return $"{RoadmapService.StatusIcon(m.Status)} {m.Title} {done}/{total}";
            });
            rmSummary.Text = string.Join("   ", parts);
            serverPanel.Children.Add(rmSummary);
        }

        // Activity log - fills remaining space
        var logHeaderRow = new Grid { Margin = new Thickness(0, 8, 0, 4) };
        logHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        logHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        serverPanel.Children.Add(logHeaderRow);

        var logLabel = new TextBlock
        {
            Text = "ACTIVITY LOG", FontSize = 10, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI"), VerticalAlignment = VerticalAlignment.Center
        };
        logLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        Grid.SetColumn(logLabel, 0);
        logHeaderRow.Children.Add(logLabel);

        var copyLogBtn = MakeBridgeSmallBtn("📋 Copy");
        copyLogBtn.ToolTip = "Copy all log text to clipboard";
        copyLogBtn.Click += (_, _) =>
        {
            if (_bridgeLogPanel is null) return;
            var lines = _bridgeLogPanel.Children
                .OfType<TextBlock>()
                .Select(tb => tb.Text);
            Clipboard.SetText(string.Join("\n", lines));
        };
        Grid.SetColumn(copyLogBtn, 1);
        logHeaderRow.Children.Add(copyLogBtn);

        var logBorder = new Border
        {
            MinHeight = 300, Padding = new Thickness(10, 8, 10, 8), BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 6, 0, 0)
        };
        logBorder.SetResourceReference(Border.BackgroundProperty, "SidebarBgBrush");
        logBorder.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        serverPanel.Children.Add(logBorder);

        _bridgeLogScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        logBorder.Child  = _bridgeLogScroll;
        _bridgeLogPanel  = new StackPanel();
        _bridgeLogScroll.Content = _bridgeLogPanel;
        foreach (var line in _bridgeLog) AppendLogLine(line);

        // ── SETUP panel ────────────────────────────────────────────────────
        BuildBridgeAgentsSection(setupPanel, cfg);
        setupPanel.Children.Add(BuildClientSetupCard(cfg));

        var mcpSettingsBtn = MakeBridgeSmallBtn("⚙  MCP Bridge Settings…");
        mcpSettingsBtn.FontSize = 12; mcpSettingsBtn.Padding = new Thickness(14, 7, 14, 7);
        mcpSettingsBtn.Margin   = new Thickness(0, 4, 0, 8);
        mcpSettingsBtn.ToolTip  = "Configure agents, folders and MCP tool access";
        mcpSettingsBtn.Click   += (_, _) => ShowMcpBridgeSettingsWindow();
        setupPanel.Children.Add(mcpSettingsBtn);
    }

    // ── Model Controller section ───────────────────────────────────────────

    private void BuildBridgeControllerSection(StackPanel body, AppSettings cfg)
    {
        // ── Sub-tab bar: [🤖 Chat] [⚙ Setup] ──────────────────────────────
        var subBar = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        subBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        subBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(subBar);

        var chatTabBtn  = MakeBridgeSubTabBtn("🤖  Chat",   active: true);
        var setupTabBtn = MakeBridgeSubTabBtn("⚙  Setup",   active: false);
        Grid.SetColumn(chatTabBtn, 0); Grid.SetColumn(setupTabBtn, 1);
        subBar.Children.Add(chatTabBtn); subBar.Children.Add(setupTabBtn);

        var chatPanel  = new StackPanel();
        var setupPanel = new StackPanel { Visibility = Visibility.Collapsed };
        body.Children.Add(chatPanel);
        body.Children.Add(setupPanel);

        chatTabBtn.Click += (_, _) =>
        {
            chatPanel.Visibility  = Visibility.Visible; setupPanel.Visibility = Visibility.Collapsed;
            SetSubTabActive(chatTabBtn, true); SetSubTabActive(setupTabBtn, false);
        };
        setupTabBtn.Click += (_, _) =>
        {
            chatPanel.Visibility  = Visibility.Collapsed; setupPanel.Visibility = Visibility.Visible;
            SetSubTabActive(chatTabBtn, false); SetSubTabActive(setupTabBtn, true);
        };

        // ── CHAT panel ─────────────────────────────────────────────────────
        // Controller picker (inline, compact)
        var infoBox = new Border
        {
            Padding = new Thickness(14, 10, 14, 10), CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 10)
        };
        infoBox.SetResourceReference(Border.BackgroundProperty, "SidebarBgBrush");
        infoBox.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        var infoText = new TextBlock
        {
            Text = "ℹ️  The controller AI directs local agents, keeping heavy token work on-device. " +
                   "Local agent responses don't cost cloud tokens - only the controller's coordination messages do.",
            FontSize = 12, FontFamily = new FontFamily("Segoe UI"), TextWrapping = TextWrapping.Wrap, LineHeight = 18
        };
        infoText.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        infoBox.Child = infoText;
        chatPanel.Children.Add(infoBox);

        // Controller picker
        AddSectionLabel(chatPanel, "CONTROLLER PARTICIPANT", topMargin: 0);

        var allParticipants = new List<(string Label, string Provider, string Model)>();
        foreach (var u in _cloudAIParticipants.Where(u => u.Data.Enabled))
        {
            var display = string.IsNullOrEmpty(u.Data.CustomName)
                ? FormatModelDisplayName(u.Data.Service.CurrentModel) : u.Data.CustomName;
            allParticipants.Add(($"{display}  [{u.Data.Service.ProviderName}]", u.Data.Service.ProviderName, u.Data.Service.CurrentModel));
        }
        foreach (var u in _ollamaParticipants.Where(u => u.Data.Enabled))
        {
            var display = string.IsNullOrEmpty(u.Data.CustomName)
                ? FormatModelDisplayName(u.Data.Service.CurrentModel) : u.Data.CustomName;
            allParticipants.Add(($"{display}  [Ollama]", "Ollama", u.Data.Service.CurrentModel));
        }

        if (allParticipants.Count == 0)
        {
            var noParticipants = new TextBlock
            {
                Text = "No participants are currently enabled. Enable participants in the sidebar first.",
                FontSize = 12, FontFamily = new FontFamily("Segoe UI"), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 20)
            };
            noParticipants.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            chatPanel.Children.Add(noParticipants);
        }
        else
        {
            var ctrlCombo = new ComboBox
            {
                Style    = (Style)FindResource("ModernComboBox"),
                FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
                Height   = 34, Margin = new Thickness(0, 6, 0, 20)
            };
            foreach (var (lbl, _, _) in allParticipants)
                ctrlCombo.Items.Add(lbl);

            int selIdx = allParticipants.FindIndex(p =>
                p.Provider == cfg.BridgeControllerProvider &&
                p.Model    == cfg.BridgeControllerModel);
            ctrlCombo.SelectedIndex = selIdx >= 0 ? selIdx : 0;

            ctrlCombo.SelectionChanged += (_, _) =>
            {
                var idx = ctrlCombo.SelectedIndex;
                if (idx < 0 || idx >= allParticipants.Count) return;
                var (_, prov, mdl) = allParticipants[idx];
                var s = SettingsService.Load();
                s.BridgeControllerProvider = prov;
                s.BridgeControllerModel    = mdl;
                SettingsService.Save(s);
            };
            chatPanel.Children.Add(ctrlCombo);
        }

        // ── Controller chat UI ─────────────────────────────────────────────
        var sep = new Rectangle { Height = 1, Margin = new Thickness(0, 4, 0, 14) };
        sep.SetResourceReference(Rectangle.FillProperty, "ControlBgBrush");
        chatPanel.Children.Add(sep);

        // Chat header row: label + clear button + font size controls
        var chatHeaderRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        chatHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        chatHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        chatPanel.Children.Add(chatHeaderRow);

        var chatLabel = new TextBlock
        {
            Text = "CONTROLLER CHAT", FontSize = 10, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI"), VerticalAlignment = VerticalAlignment.Center
        };
        chatLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        Grid.SetColumn(chatLabel, 0);
        chatHeaderRow.Children.Add(chatLabel);

        var fontRow = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(fontRow, 1);
        chatHeaderRow.Children.Add(fontRow);

        // Clear history button
        var clearBtn = MakeBridgeSmallBtn("🗑");
        clearBtn.FontSize = 10;
        clearBtn.ToolTip  = "Clear chat history";
        clearBtn.Margin   = new Thickness(0, 0, 8, 0);
        // wired up below, after outputBox is created
        fontRow.Children.Add(clearBtn);

        double ctrlFontSize = cfg.BridgeControllerFontSize;

        var fontSmallBtn = MakeBridgeSmallBtn("A-");
        fontSmallBtn.FontSize = 10;
        fontSmallBtn.ToolTip  = "Decrease font size";
        fontRow.Children.Add(fontSmallBtn);

        var fontSizeLbl = new TextBlock
        {
            Text = $"{ctrlFontSize:F0}pt", FontSize = 10,
            FontFamily = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 5, 0)
        };
        fontSizeLbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        fontRow.Children.Add(fontSizeLbl);

        var fontLargeBtn = MakeBridgeSmallBtn("A+");
        fontLargeBtn.FontSize = 10;
        fontLargeBtn.ToolTip  = "Increase font size";
        fontRow.Children.Add(fontLargeBtn);

        // Output area - read-only TextBox so text is selectable & copyable
        var outputBox = new TextBox
        {
            IsReadOnly = true, FontSize = ctrlFontSize,
            FontFamily = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            MinHeight = 160,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            AcceptsReturn = true, IsUndoEnabled = false,
            Margin = new Thickness(0, 0, 0, 8),
            ToolTip = "Select text and Ctrl+C to copy"
        };
        outputBox.SetResourceReference(TextBox.BackgroundProperty, "SidebarBgBrush");
        outputBox.SetResourceReference(TextBox.ForegroundProperty, "ContentTextBrush");
        outputBox.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        outputBox.MinHeight = 320;
        // Restore conversation history so switching tabs / rebuilding the panel
        // doesn't wipe the chat log.
        outputBox.Text = _controllerChatHistory;
        outputBox.ScrollToEnd();
        chatPanel.Children.Add(outputBox);

        // Wire up clear button now that outputBox is in scope.
        // Clears both the display text AND the runner's API-level conversation history.
        clearBtn.Click += (_, _) =>
        {
            outputBox.Text         = "";
            _controllerChatHistory = "";
            _controllerRunner?.ClearHistory();
        };

        // Font size buttons wire-up
        fontSmallBtn.Click += (_, _) =>
        {
            ctrlFontSize = Math.Max(9, ctrlFontSize - 1);
            outputBox.FontSize = ctrlFontSize;
            fontSizeLbl.Text   = $"{ctrlFontSize:F0}pt";
            var s = SettingsService.Load(); s.BridgeControllerFontSize = ctrlFontSize; SettingsService.Save(s);
        };
        fontLargeBtn.Click += (_, _) =>
        {
            ctrlFontSize = Math.Min(24, ctrlFontSize + 1);
            outputBox.FontSize = ctrlFontSize;
            fontSizeLbl.Text   = $"{ctrlFontSize:F0}pt";
            var s = SettingsService.Load(); s.BridgeControllerFontSize = ctrlFontSize; SettingsService.Save(s);
        };

        // Multi-line input + send button
        var inputRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        chatPanel.Children.Add(inputRow);

        var inputBox = new TextBox
        {
            FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
            Padding = new Thickness(8, 7, 8, 7), BorderThickness = new Thickness(1),
            TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            MinHeight = 60, MaxHeight = 140,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ToolTip = "Type your request - Enter to send, Shift+Enter for new line"
        };
        inputBox.SetResourceReference(TextBox.BackgroundProperty, "ControlBgBrush");
        inputBox.SetResourceReference(TextBox.ForegroundProperty, "ContentTextBrush");
        inputBox.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        Grid.SetColumn(inputBox, 0);
        inputRow.Children.Add(inputBox);

        var sendBtn = MakeBridgeActionBtn("▶ Run", isPrimary: true);
        sendBtn.Margin            = new Thickness(8, 0, 0, 0);
        sendBtn.VerticalAlignment = VerticalAlignment.Top;
        sendBtn.ToolTip           = "Run (Enter)";
        Grid.SetColumn(sendBtn, 1);
        inputRow.Children.Add(sendBtn);

        // Status label
        var statusLbl = new TextBlock
        {
            Text = "", FontSize = 10, FontFamily = new FontFamily("Segoe UI"),
            Margin = new Thickness(0, 2, 0, 4)
        };
        statusLbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        chatPanel.Children.Add(statusLbl);

        // Wire up send
        async void DoRun()
        {
            if (_controllerRunning) return;
            var prompt = inputBox.Text.Trim();
            if (string.IsNullOrEmpty(prompt)) return;

            var s = SettingsService.Load();
            if (string.IsNullOrEmpty(s.BridgeControllerProvider) ||
                string.IsNullOrEmpty(s.BridgeControllerModel))
            {
                outputBox.Text = "⚠ No controller model selected. Pick one above.";
                return;
            }

            _controllerRunning = true;
            sendBtn.IsEnabled  = false;
            inputBox.IsEnabled = false;
            sendBtn.Content    = "⏳";
            inputBox.Text      = "";

            // Append the new turn (don't overwrite — history must survive rebuilds).
            if (outputBox.Text.Length > 0) outputBox.AppendText("\n");
            outputBox.AppendText($"You: {prompt}\n\n");
            outputBox.ScrollToEnd();
            statusLbl.Text = "Running…";

            try
            {
                var apiKey = WindowsCredentialManager.Load(s.BridgeControllerProvider) ?? "";
                var svcUrl = s.Participants
                    .FirstOrDefault(p => string.Equals(p.Type, s.BridgeControllerProvider,
                        StringComparison.OrdinalIgnoreCase))?.ServerUrl ?? "http://localhost:11434";

                // Reuse the runner when provider/model/url are unchanged so that
                // the API-level conversation history (messages array) is preserved.
                // Recreate — and implicitly clear history — only when the model changes.
                var newRunner = new ModelControllerRunner(
                    s.BridgeControllerProvider, s.BridgeControllerModel,
                    apiKey, svcUrl,
                    BuildMcpTools(BridgeAgentMode.ModelController),
                    ExecuteToolByNameAndArgs,
                    line => Dispatcher.Invoke(() => BridgeLog(line)));

                if (_controllerRunner is null ||
                    _controllerRunner.ConfigKey != newRunner.ConfigKey)
                {
                    _controllerRunner = newRunner;
                }

                await _controllerRunner.RunAsync(prompt, chunk =>
                    Dispatcher.Invoke(() =>
                    {
                        outputBox.AppendText(chunk);
                        outputBox.ScrollToEnd();
                    }),
                    CancellationToken.None);

                statusLbl.Text = $"Done  ·  {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                outputBox.AppendText($"\n\n❌ Error: {ex.Message}");
                statusLbl.Text = "Error";
            }
            finally
            {
                _controllerRunning         = false;
                _controllerChatHistory     = outputBox.Text;   // persist across panel rebuilds
                sendBtn.IsEnabled          = true;
                inputBox.IsEnabled         = true;
                sendBtn.Content            = "▶ Run";
            }
        }

        sendBtn.Click += (_, _) => DoRun();
        // PreviewKeyDown intercepts BEFORE the TextBox inserts a newline
        // Enter = send, Shift+Enter = new line (falls through to AcceptsReturn)
        inputBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Return &&
                System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None)
            {
                e.Handled = true;
                DoRun();
            }
        };

        // ── SETUP panel ────────────────────────────────────────────────────
        BuildBridgeAgentsSection(setupPanel, cfg);

        var ctrlSettingsBtn = MakeBridgeSmallBtn("⚙  Controller Bridge Settings…");
        ctrlSettingsBtn.FontSize  = 12;
        ctrlSettingsBtn.Padding   = new Thickness(14, 7, 14, 7);
        ctrlSettingsBtn.Margin    = new Thickness(0, 0, 0, 8);
        ctrlSettingsBtn.ToolTip   = "Configure agents, folders and Controller tool access";
        ctrlSettingsBtn.Click    += (_, _) => ShowControllerBridgeSettingsWindow();
        setupPanel.Children.Add(ctrlSettingsBtn);

        body.Children.Add(new Border { Height = 6 });
    }

    // ── Agents + Folders section (collapsible) ─────────────────────────────

    private void BuildBridgeAgentsSection(StackPanel body, AppSettings cfg)
    {
        // ── Collapse toggle bar ───────────────────────────────────────────
        var toggleBar = new Border
        {
            Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1), Margin = new Thickness(0, 8, 0, 0),
            Cursor = Cursors.Hand
        };
        toggleBar.SetResourceReference(Border.BackgroundProperty, "ControlBgBrush");
        toggleBar.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        body.Children.Add(toggleBar);

        var toggleGrid = new Grid();
        toggleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toggleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toggleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toggleBar.Child = toggleGrid;

        var toggleIcon = new TextBlock
        {
            Text = "⚙", FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        toggleIcon.SetResourceReference(TextBlock.ForegroundProperty, "AccentPrimaryBrush");
        Grid.SetColumn(toggleIcon, 0);
        toggleGrid.Children.Add(toggleIcon);

        var toggleLbl = new TextBlock
        {
            Text = "Agents & Folders", FontSize = 12, FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"), VerticalAlignment = VerticalAlignment.Center
        };
        toggleLbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        Grid.SetColumn(toggleLbl, 1);
        toggleGrid.Children.Add(toggleLbl);

        var toggleChevron = new TextBlock
        {
            Text = "▾", FontSize = 13, VerticalAlignment = VerticalAlignment.Center
        };
        toggleChevron.SetResourceReference(TextBlock.ForegroundProperty, "AccentPrimaryBrush");
        Grid.SetColumn(toggleChevron, 2);
        toggleGrid.Children.Add(toggleChevron);

        // ── Collapsible content panel ─────────────────────────────────────
        var contentPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
        body.Children.Add(contentPanel);

        bool agentsExpanded = true;
        toggleBar.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled        = true;
            agentsExpanded   = !agentsExpanded;
            contentPanel.Visibility = agentsExpanded ? Visibility.Visible : Visibility.Collapsed;
            toggleChevron.Text      = agentsExpanded ? "▾" : "▸";
            toggleLbl.Text          = agentsExpanded ? "Agents & Folders" : "Agents & Folders  (click to expand)";
        };

        // Separator inside content
        var sep = new Rectangle { Height = 1, Margin = new Thickness(0, 10, 0, 16) };
        sep.SetResourceReference(Rectangle.FillProperty, "ControlBgBrush");
        contentPanel.Children.Add(sep);

        // Two-column grid: Agents | gap | Folders
        var twoCol = new Grid();
        twoCol.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        twoCol.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        twoCol.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var agentsCol  = new StackPanel();
        var foldersCol = new StackPanel();
        Grid.SetColumn(agentsCol,  0);
        Grid.SetColumn(foldersCol, 2);
        twoCol.Children.Add(agentsCol);
        twoCol.Children.Add(foldersCol);
        contentPanel.Children.Add(twoCol);

        // ── AGENTS column ────────────────────────────────────────────────
        var agentsHdr = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        agentsHdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        agentsHdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var agentsLbl = new TextBlock
        {
            Text = "AGENTS", FontSize = 10, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI"), VerticalAlignment = VerticalAlignment.Center
        };
        agentsLbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        Grid.SetColumn(agentsLbl, 0);
        agentsHdr.Children.Add(agentsLbl);

        var addBtns = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(addBtns, 1);
        agentsHdr.Children.Add(addBtns);

        var addLocalBtn = MakeBridgeSmallBtn("＋ Local");
        addLocalBtn.ToolTip = "Add a local Ollama model as an agent";
        addLocalBtn.Click  += (_, _) => ShowAddAgentDialog(isCloud: false, body, cfg);
        addBtns.Children.Add(addLocalBtn);

        var addCloudBtn = MakeBridgeSmallBtn("＋ Cloud");
        addCloudBtn.Margin  = new Thickness(4, 0, 0, 0);
        addCloudBtn.ToolTip = "Add a cloud AI model as an agent (use with care)";
        addCloudBtn.Click  += (_, _) => ShowAddAgentDialog(isCloud: true, body, cfg);
        addBtns.Children.Add(addCloudBtn);

        agentsCol.Children.Add(agentsHdr);

        var agentsSub = new TextBlock
        {
            Text = "Models exposed as tools.", FontSize = 11,
            FontFamily = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        };
        agentsSub.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        agentsCol.Children.Add(agentsSub);

        // ── Project roster banner ─────────────────────────────────────────
        // Shows when a project is open, offering to save/load/restore agent roster.
        var openProject = _currentProject;
        if (openProject is not null)
        {
            var rosterBanner = new Border
            {
                Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 8)
            };
            rosterBanner.SetResourceReference(Border.BorderBrushProperty, "AccentBgBrush");

            // tint: use project roster active → warm accent; available → subtle; no saved → very subtle
            bool projectHasRoster = openProject.SavedBridgeAgents is { Count: > 0 };
            if (_bridgeUsingProjectRoster)
                rosterBanner.SetResourceReference(Border.BackgroundProperty, "AccentBgBrush");
            else
                rosterBanner.SetResourceReference(Border.BackgroundProperty, "ControlBgBrush");

            var rosterStack = new StackPanel { Orientation = Orientation.Horizontal };
            rosterBanner.Child = rosterStack;

            var rosterIcon = new TextBlock
            {
                Text = _bridgeUsingProjectRoster ? "✅" : (projectHasRoster ? "📋" : "💾"),
                FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            rosterIcon.SetResourceReference(TextBlock.ForegroundProperty,
                _bridgeUsingProjectRoster ? "AccentTextBrush" : "ContentTextBrush");
            rosterStack.Children.Add(rosterIcon);

            var rosterLbl = new TextBlock
            {
                Text = _bridgeUsingProjectRoster
                    ? $"Project roster active"
                    : (projectHasRoster ? $"Project has a saved agent roster" : "Save agents to project"),
                FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 6, 0), MaxWidth = 130
            };
            rosterLbl.SetResourceReference(TextBlock.ForegroundProperty,
                _bridgeUsingProjectRoster ? "AccentTextBrush" : "ContentTextBrush");
            rosterStack.Children.Add(rosterLbl);

            // Buttons panel
            var rosterBtns = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };
            rosterStack.Children.Add(rosterBtns);

            if (_bridgeUsingProjectRoster)
            {
                // Active: offer restore global + update saved
                var restoreBtn = MakeBridgeSmallBtn("↩ Restore global");
                restoreBtn.FontSize = 10;
                restoreBtn.Padding  = new Thickness(6, 3, 6, 3);
                restoreBtn.ToolTip  = "Restore your global Bridge agent roster";
                restoreBtn.Click   += (_, _) => RestoreGlobalBridgeAgentsIfNeeded();
                rosterBtns.Children.Add(restoreBtn);

                var updateBtn = MakeBridgeSmallBtn("💾 Update saved");
                updateBtn.FontSize = 10;
                updateBtn.Padding  = new Thickness(6, 3, 6, 3);
                updateBtn.Margin   = new Thickness(0, 3, 0, 0);
                updateBtn.ToolTip  = "Save the current agent list as this project's roster";
                updateBtn.Click   += (_, _) =>
                {
                    var s = SettingsService.Load();
                    openProject.SavedBridgeAgents = [.. s.BridgeAgents];
                    Services.ProjectService.SaveProject(_currentProjectFolder!, openProject);
                    updateBtn.Content = "✓ Saved";
                    updateBtn.IsEnabled = false;
                };
                rosterBtns.Children.Add(updateBtn);
            }
            else if (projectHasRoster)
            {
                // Roster available but not active: offer to load it or update saved
                var loadBtn = MakeBridgeSmallBtn("▶ Use it");
                loadBtn.FontSize = 10;
                loadBtn.Padding  = new Thickness(6, 3, 6, 3);
                loadBtn.ToolTip  = "Replace current agents with this project's saved roster (global roster restored on project close)";
                loadBtn.Click   += (_, _) => ActivateProjectBridgeRoster(openProject.SavedBridgeAgents!);
                rosterBtns.Children.Add(loadBtn);

                var saveBtn = MakeBridgeSmallBtn("💾 Update");
                saveBtn.FontSize = 10;
                saveBtn.Padding  = new Thickness(6, 3, 6, 3);
                saveBtn.Margin   = new Thickness(0, 3, 0, 0);
                saveBtn.ToolTip  = "Overwrite the saved roster with the current agent list";
                saveBtn.Click   += (_, _) =>
                {
                    var s = SettingsService.Load();
                    openProject.SavedBridgeAgents = [.. s.BridgeAgents];
                    Services.ProjectService.SaveProject(_currentProjectFolder!, openProject);
                    saveBtn.Content = "✓ Updated";
                    saveBtn.IsEnabled = false;
                };
                rosterBtns.Children.Add(saveBtn);
            }
            else
            {
                // No saved roster yet: offer to save current
                var saveNewBtn = MakeBridgeSmallBtn("💾 Save");
                saveNewBtn.FontSize = 10;
                saveNewBtn.Padding  = new Thickness(6, 3, 6, 3);
                saveNewBtn.ToolTip  = "Save the current agent list as this project's roster";
                saveNewBtn.Click   += (_, _) =>
                {
                    var s = SettingsService.Load();
                    openProject.SavedBridgeAgents = [.. s.BridgeAgents];
                    Services.ProjectService.SaveProject(_currentProjectFolder!, openProject);
                    BuildBridgeContent();   // rebuild to show "roster available" state
                };
                rosterBtns.Children.Add(saveNewBtn);
            }

            agentsCol.Children.Add(rosterBanner);
        }

        var agentListPanel = new StackPanel();
        agentsCol.Children.Add(agentListPanel);
        RebuildAgentList(agentListPanel, cfg);

        // ── FOLDERS column ───────────────────────────────────────────────
        var foldersHdr = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        foldersHdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foldersHdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var foldersLbl = new TextBlock
        {
            Text = "FOLDERS", FontSize = 10, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI"), VerticalAlignment = VerticalAlignment.Center
        };
        foldersLbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        Grid.SetColumn(foldersLbl, 0);
        foldersHdr.Children.Add(foldersLbl);

        var addFolderBtn = MakeBridgeSmallBtn("＋ Add");
        addFolderBtn.ToolTip = "Add a folder agents can access";
        Grid.SetColumn(addFolderBtn, 1);
        foldersHdr.Children.Add(addFolderBtn);
        foldersCol.Children.Add(foldersHdr);

        var foldersSub = new TextBlock
        {
            Text = "Readable (and optionally writable) paths.", FontSize = 11,
            FontFamily = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        };
        foldersSub.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        foldersCol.Children.Add(foldersSub);

        var folderListPanel = new StackPanel();
        foldersCol.Children.Add(folderListPanel);
        RebuildFolderList(folderListPanel, cfg);

        addFolderBtn.Click += (_, _) => ShowAddFolderDialog(folderListPanel);

        // ── Temp workspace row ─────────────────────────────────────────
        var tempSep = new Rectangle { Height = 1, Margin = new Thickness(0, 10, 0, 8) };
        tempSep.SetResourceReference(Rectangle.FillProperty, "ControlBorderBrush");
        foldersCol.Children.Add(tempSep);

        var tempLbl = new TextBlock
        {
            Text = "TEMP WORKSPACE", FontSize = 10, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 0, 0, 4)
        };
        tempLbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        foldersCol.Children.Add(tempLbl);

        var tempSub = new TextBlock
        {
            Text = "Shared scratch folder for parallel agent tasks. Must be inside a write-enabled folder.",
            FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6)
        };
        tempSub.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        foldersCol.Children.Add(tempSub);

        var tempRow = new Grid();
        tempRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tempRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tempRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foldersCol.Children.Add(tempRow);

        var tempBox = new TextBox
        {
            Text = cfg.BridgeTempFolder, FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
            Padding = new Thickness(6, 5, 6, 5), BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Full path to the temp workspace folder"
        };
        tempBox.SetResourceReference(TextBox.BackgroundProperty, "ControlBgBrush");
        tempBox.SetResourceReference(TextBox.ForegroundProperty, "ContentTextBrush");
        tempBox.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
        tempBox.LostFocus += (_, _) =>
        {
            var s = SettingsService.Load(); s.BridgeTempFolder = tempBox.Text.Trim(); SettingsService.Save(s);
        };
        Grid.SetColumn(tempBox, 0);
        tempRow.Children.Add(tempBox);

        var tempBrowse = MakeBridgeSmallBtn("📂");
        tempBrowse.Margin = new Thickness(4, 0, 4, 0);
        tempBrowse.ToolTip = "Browse for temp workspace folder";
        tempBrowse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select temp workspace folder" };
            if (dialog.ShowDialog(this) != true) return;
            var path = dialog.FolderName;
            var s = SettingsService.Load();
            if (IsSystemPath(path)) { MessageBox.Show("Cannot use a system directory as temp workspace.", "System Directory", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            tempBox.Text = path; s.BridgeTempFolder = path; SettingsService.Save(s);
        };
        Grid.SetColumn(tempBrowse, 1);
        tempRow.Children.Add(tempBrowse);

        var tempOpen = MakeBridgeSmallBtn("🗂");
        tempOpen.ToolTip = "Open temp workspace in Explorer";
        tempOpen.Click += (_, _) =>
        {
            var p = tempBox.Text.Trim();
            if (string.IsNullOrEmpty(p)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true }); }
            catch { }
        };
        Grid.SetColumn(tempOpen, 2);
        tempRow.Children.Add(tempOpen);
    }

    // ── Agent list ──────────────────────────────────────────────────────────

    private void RebuildAgentList(StackPanel panel, AppSettings cfg)
    {
        panel.Children.Clear();

        if (cfg.BridgeAgents.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "No agents yet.\nAdd local Ollama models to get started.",
                FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            panel.Children.Add(empty);
            return;
        }

        foreach (var agent in cfg.BridgeAgents.ToList())
        {
            var capturedAgent = agent;
            var row = new Border
            {
                Padding = new Thickness(8, 7, 8, 7), CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 5)
            };
            row.SetResourceReference(Border.BackgroundProperty, "ControlBgBrush");
            row.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");

            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Child = rowGrid;

            var toggle = new CheckBox
            {
                IsChecked = agent.IsEnabled, Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center, ToolTip = "Enable this agent"
            };
            toggle.Checked   += (_, _) => { capturedAgent.IsEnabled = true;  SaveAgents(cfg); };
            toggle.Unchecked += (_, _) => { capturedAgent.IsEnabled = false; SaveAgents(cfg); };
            Grid.SetColumn(toggle, 0);
            rowGrid.Children.Add(toggle);

            var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(infoStack, 1);
            rowGrid.Children.Add(infoStack);

            var nameText = new TextBlock
            {
                Text = agent.Label, FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis
            };
            nameText.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
            infoStack.Children.Add(nameText);

            var detailText = new TextBlock
            {
                Text = agent.IsLocal ? $"{agent.Model}  ·  {agent.Provider}" : $"{agent.Provider}  ·  {agent.Model}",
                FontSize = 10, FontFamily = new FontFamily("Segoe UI"), TextTrimming = TextTrimming.CharacterEllipsis
            };
            detailText.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            infoStack.Children.Add(detailText);

            if (!agent.IsLocal)
            {
                var cloudTag = new TextBlock { Text = "☁  Cloud", FontSize = 10, FontFamily = new FontFamily("Segoe UI") };
                cloudTag.SetResourceReference(TextBlock.ForegroundProperty, "AccentHighlightBrush");
                infoStack.Children.Add(cloudTag);
            }

            var removeBtn = new Button
            {
                Content = "🗑", FontSize = 12, Padding = new Thickness(6, 3, 6, 3),
                BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"Remove {agent.Label}"
            };
            removeBtn.SetResourceReference(Button.ForegroundProperty, "AccentHighlightBrush");
            removeBtn.Click += (_, _) =>
            {
                var s = SettingsService.Load();
                s.BridgeAgents.RemoveAll(a => a.Id == capturedAgent.Id);
                SettingsService.Save(s);
                RebuildAgentList(panel, s);
            };
            Grid.SetColumn(removeBtn, 2);
            rowGrid.Children.Add(removeBtn);
            panel.Children.Add(row);
        }

        static void SaveAgents(AppSettings cfg)
        {
            var s = SettingsService.Load();
            foreach (var a in cfg.BridgeAgents)
            {
                var m = s.BridgeAgents.FirstOrDefault(x => x.Id == a.Id);
                if (m is not null) m.IsEnabled = a.IsEnabled;
            }
            SettingsService.Save(s);
        }
    }

    // ── Folder list ─────────────────────────────────────────────────────────

    private void RebuildFolderList(StackPanel panel, AppSettings? cfg = null)
    {
        cfg ??= SettingsService.Load();
        panel.Children.Clear();

        if (cfg.BridgeFolders.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "No folders yet.\nAdd folders agents can read from.",
                FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            panel.Children.Add(empty);
            return;
        }

        foreach (var folder in cfg.BridgeFolders.ToList())
        {
            var capturedFolder = folder;
            var row = new Border
            {
                Padding = new Thickness(8, 7, 8, 7), CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 5)
            };
            row.SetResourceReference(Border.BackgroundProperty, "ControlBgBrush");
            row.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");

            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Child = rowGrid;

            var infoStack = new StackPanel();
            Grid.SetColumn(infoStack, 0);
            rowGrid.Children.Add(infoStack);

            // Folder icon + name
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
            var folderIcon = new TextBlock
            {
                Text = "📁 ", FontSize = 12, VerticalAlignment = VerticalAlignment.Center
            };
            var nameText = new TextBlock
            {
                Text = folder.Label, FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center, ToolTip = folder.Path
            };
            nameText.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
            nameRow.Children.Add(folderIcon);
            nameRow.Children.Add(nameText);
            infoStack.Children.Add(nameRow);

            // Write access checkbox
            var writeCheck = new CheckBox
            {
                IsChecked = folder.AllowWrite,
                FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(2, 4, 0, 0), Cursor = Cursors.Hand,
                ToolTip = "Allow agents to write files in this folder"
            };
            writeCheck.SetResourceReference(CheckBox.ForegroundProperty, "SidebarDimBrush");

            // Build content inline so the brush resolves
            var writeLabel = new TextBlock
            {
                Text = "Allow write", FontSize = 11,
                FontFamily = new FontFamily("Segoe UI")
            };
            writeLabel.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            writeCheck.Content = writeLabel;

            writeCheck.Checked += (_, _) =>
            {
                if (IsSystemPath(capturedFolder.Path))
                {
                    writeCheck.IsChecked = false;   // snap back
                    MessageBox.Show(
                        "Write access cannot be granted to system directories.\n\n" +
                        $"Path: {capturedFolder.Path}\n\n" +
                        "This folder will remain read-only.",
                        "System Directory Protected",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                capturedFolder.AllowWrite = true;
                SaveFolders(cfg);
            };
            writeCheck.Unchecked += (_, _) => { capturedFolder.AllowWrite = false; SaveFolders(cfg); };
            infoStack.Children.Add(writeCheck);

            // Write badge (shown when write is enabled)
            var writeBadge = new TextBlock
            {
                Text = "✏ Writable", FontSize = 10, FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(2, 2, 0, 0),
                Visibility = folder.AllowWrite ? Visibility.Visible : Visibility.Collapsed
            };
            writeBadge.SetResourceReference(TextBlock.ForegroundProperty, "AccentHighlightBrush");
            writeCheck.Checked   += (_, _) => writeBadge.Visibility = Visibility.Visible;
            writeCheck.Unchecked += (_, _) => writeBadge.Visibility = Visibility.Collapsed;
            infoStack.Children.Add(writeBadge);

            // Action buttons column
            var actionBtns = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            Grid.SetColumn(actionBtns, 1);
            rowGrid.Children.Add(actionBtns);

            var openBtn = MakeBridgeSmallBtn("📂");
            openBtn.FontSize = 13;
            openBtn.Padding  = new Thickness(6, 3, 6, 3);
            openBtn.ToolTip  = $"Open folder in Explorer";
            openBtn.Click   += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(capturedFolder.Path) { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show($"Could not open folder:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
            };
            actionBtns.Children.Add(openBtn);

            var removeBtn = new Button
            {
                Content = "🗑", FontSize = 12, Padding = new Thickness(6, 3, 6, 3),
                BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                Cursor = Cursors.Hand, Margin = new Thickness(0, 4, 0, 0),
                ToolTip = $"Remove folder {folder.Label}"
            };
            removeBtn.SetResourceReference(Button.ForegroundProperty, "AccentHighlightBrush");
            removeBtn.Click += (_, _) =>
            {
                var s = SettingsService.Load();
                s.BridgeFolders.RemoveAll(f => f.Id == capturedFolder.Id);
                SettingsService.Save(s);
                RebuildFolderList(panel, s);
            };
            actionBtns.Children.Add(removeBtn);

            panel.Children.Add(row);
        }

        static void SaveFolders(AppSettings cfg)
        {
            var s = SettingsService.Load();
            foreach (var f in cfg.BridgeFolders)
            {
                var m = s.BridgeFolders.FirstOrDefault(x => x.Id == f.Id);
                if (m is not null) m.AllowWrite = f.AllowWrite;
            }
            SettingsService.Save(s);
        }
    }

    private void ShowAddFolderDialog(StackPanel folderListPanel)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title       = "Select a folder for Bridge agents",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true) return;

        var path = dialog.FolderName;
        if (string.IsNullOrWhiteSpace(path)) return;

        var s = SettingsService.Load();
        if (s.BridgeFolders.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("That folder is already in the list.", "Already Added",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var sysWarning = IsSystemPath(path)
            ? "\n\n⚠  This looks like a system directory. It will be added as read-only and write access cannot be enabled."
            : "";

        if (!string.IsNullOrEmpty(sysWarning))
            MessageBox.Show($"Added as read-only.{sysWarning}", "System Directory",
                MessageBoxButton.OK, MessageBoxImage.Warning);

        s.BridgeFolders.Add(new BridgeFolder { Path = path, AllowWrite = false });
        SettingsService.Save(s);
        RebuildFolderList(folderListPanel, s);
    }

    /// <summary>
    /// Returns true if <paramref name="path"/> is or is a subdirectory of a Windows
    /// system location that should never be given write access by AI agents.
    /// </summary>
    private static bool IsSystemPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        // Normalise: full path, no trailing separator, lowercase for comparison
        string norm;
        try { norm = System.IO.Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch { return false; }

        // Collect protected root paths from the OS itself (handles non-C-drive installs)
        var systemRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),  // C:\ProgramData
        };

        // Also protect drive roots (C:\, D:\, …) and a few hardcoded system paths
        var sysDrive = System.IO.Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System))
                       ?.TrimEnd('\\', '/') ?? "C:";
        systemRoots.Add(sysDrive);                           // e.g. "C:"  or  "C:\"
        systemRoots.Add(sysDrive + @"\");
        systemRoots.Add(sysDrive + @"\Recovery");
        systemRoots.Add(sysDrive + @"\System Volume Information");
        systemRoots.Add(sysDrive + @"\$Recycle.Bin");
        systemRoots.Add(sysDrive + @"\Boot");
        systemRoots.Add(sysDrive + @"\EFI");

        foreach (var root in systemRoots.Where(r => !string.IsNullOrEmpty(r)))
        {
            var normRoot = root.TrimEnd('\\', '/');
            if (norm.Equals(normRoot, StringComparison.OrdinalIgnoreCase) ||
                norm.StartsWith(normRoot + '\\', StringComparison.OrdinalIgnoreCase) ||
                norm.StartsWith(normRoot + '/', StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void ShowAddAgentDialog(bool isCloud, StackPanel body, AppSettings cfg)
    {
        // ── Cloud warning first ────────────────────────────────────────────
        if (isCloud)
        {
            var warnWin = new Window
            {
                Title = "Cloud Agent - Important Notice", Width = 460, Height = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
                ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize
            };
            ApplyThemeToDialog(warnWin);
            warnWin.SetResourceReference(Window.BackgroundProperty, "ContentBgBrush");
            var warnRoot = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            warnWin.Content = warnRoot;

            var warnTitle = new TextBlock
            {
                Text = "⚠  Cloud agents have cost implications",
                FontSize = 14, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14)
            };
            warnTitle.SetResourceReference(TextBlock.ForegroundProperty, "AccentHighlightBrush");
            warnRoot.Children.Add(warnTitle);

            var warnBody = new TextBlock
            {
                Text = "Only local models are advisable as Bridge agents.\n\n" +
                       "Only add cloud providers if you are aware of their rate limits and cost factors. " +
                       "Cloud agents will be given tasks by another AI model without your direct control - " +
                       "this can consume tokens quickly and unexpectedly.",
                FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap, LineHeight = 18, Margin = new Thickness(0, 0, 0, 16)
            };
            warnBody.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
            warnRoot.Children.Add(warnBody);

            var understoodCheck = new CheckBox
            {
                Content = "I understand the cost and rate-limit implications",
                FontSize = 12, FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 0, 0, 20)
            };
            understoodCheck.SetResourceReference(CheckBox.ForegroundProperty, "ContentTextBrush");
            warnRoot.Children.Add(understoodCheck);

            var warnBtnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            warnRoot.Children.Add(warnBtnRow);
            var cancelWarn  = MakeBridgeActionBtn("Cancel", false);
            cancelWarn.Click += (_, _) => warnWin.DialogResult = false;
            warnBtnRow.Children.Add(cancelWarn);
            var continueBtn = MakeBridgeActionBtn("Continue", true);
            continueBtn.Margin = new Thickness(8, 0, 0, 0);
            continueBtn.IsEnabled = false;
            understoodCheck.Checked   += (_, _) => continueBtn.IsEnabled = true;
            understoodCheck.Unchecked += (_, _) => continueBtn.IsEnabled = false;
            continueBtn.Click += (_, _) => warnWin.DialogResult = true;
            warnBtnRow.Children.Add(continueBtn);

            if (warnWin.ShowDialog() != true) return;
        }

        // ── Build participant source list ──────────────────────────────────
        // (provider, model, serverUrl, displayLabel, alreadyAdded)
        var available = new List<(string Provider, string Model, string ServerUrl,
                                  string Label, bool AlreadyAdded)>();

        if (!isCloud)
        {
            foreach (var u in _ollamaParticipants)
            {
                var model   = u.Data.Service.CurrentModel;
                var url     = u.Data.Service.BaseUrl;
                var display = string.IsNullOrEmpty(u.Data.CustomName)
                    ? FormatModelDisplayName(model) : u.Data.CustomName;
                var already = cfg.BridgeAgents.Any(a =>
                    a.IsLocal &&
                    string.Equals(a.Model, model, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(a.ServerUrl, url, StringComparison.OrdinalIgnoreCase));
                available.Add(("Ollama", model, url, display, already));
            }
        }
        else
        {
            foreach (var u in _cloudAIParticipants)
            {
                var svc     = u.Data.Service;
                var model   = svc.CurrentModel;
                var display = string.IsNullOrEmpty(u.Data.CustomName)
                    ? FormatModelDisplayName(model) : u.Data.CustomName;
                var already = cfg.BridgeAgents.Any(a =>
                    !a.IsLocal &&
                    string.Equals(a.Provider, svc.ProviderName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(a.Model, model, StringComparison.OrdinalIgnoreCase));
                available.Add((svc.ProviderName, model, "", display, already));
            }
        }

        // ── Picker dialog ──────────────────────────────────────────────────
        var win = new Window
        {
            Title = isCloud ? "Add Cloud Agent" : "Add Local Agent",
            Width = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            ShowInTaskbar = false, ResizeMode = ResizeMode.CanResize
        };
        win.SizeToContent = SizeToContent.Height;
        win.MaxHeight = 560;
        ApplyThemeToDialog(win);
        win.SetResourceReference(Window.BackgroundProperty, "ContentBgBrush");

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(20, 16, 20, 16) };
        win.Content = scroll;
        var root = new StackPanel();
        scroll.Content = root;

        bool added = false;

        void AddSectionLbl(string t, double top = 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = t, FontSize = 10, FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = (Brush)FindResource("ContentDimBrush"),
                Margin = new Thickness(0, top, 0, 6)
            });
        }

        // ── Participant picker rows ────────────────────────────────────────
        if (available.Count > 0)
        {
            AddSectionLbl(isCloud ? "CONFIGURED CLOUD PARTICIPANTS" : "CONFIGURED LOCAL PARTICIPANTS");

            foreach (var (provider, model, serverUrl, label, alreadyAdded) in available)
            {
                var capturedProvider = provider;
                var capturedModel    = model;
                var capturedUrl      = serverUrl;
                var capturedLabel    = label;

                var row = new Border
                {
                    Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 5),
                    Opacity = alreadyAdded ? 0.5 : 1.0
                };
                row.SetResourceReference(Border.BackgroundProperty, "ControlBgBrush");
                row.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");

                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Child = rowGrid;

                // Info
                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(info, 0);
                rowGrid.Children.Add(info);

                var nameText = new TextBlock
                {
                    Text = label, FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
                    FontWeight = FontWeights.SemiBold
                };
                nameText.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
                info.Children.Add(nameText);

                var detail = new TextBlock
                {
                    Text = isCloud ? $"{provider}  ·  {model}" : $"{model}  ·  {serverUrl}",
                    FontSize = 11, FontFamily = new FontFamily("Segoe UI")
                };
                detail.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
                info.Children.Add(detail);

                // Add / Already added
                if (alreadyAdded)
                {
                    var doneTag = new TextBlock
                    {
                        Text = "✓ Already added", FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0)
                    };
                    doneTag.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
                    Grid.SetColumn(doneTag, 1);
                    rowGrid.Children.Add(doneTag);
                }
                else
                {
                    var addRowBtn = MakeBridgeActionBtn("＋ Add", true);
                    addRowBtn.Padding = new Thickness(12, 5, 12, 5);
                    addRowBtn.FontSize = 11;
                    Grid.SetColumn(addRowBtn, 1);
                    rowGrid.Children.Add(addRowBtn);

                    addRowBtn.Click += (_, _) =>
                    {
                        var agent = new BridgeAgent
                        {
                            Provider    = capturedProvider,
                            Model       = capturedModel,
                            ServerUrl   = capturedUrl,
                            DisplayName = capturedLabel,
                            IsEnabled   = true
                        };
                        var s = SettingsService.Load();
                        s.BridgeAgents.Add(agent);
                        SettingsService.Save(s);
                        added = true;
                        // Stay open — update button to ✓ so user can add more agents
                        addRowBtn.Content   = "✓ Added";
                        addRowBtn.IsEnabled = false;
                        row.Opacity         = 0.5;
                    };
                }

                root.Children.Add(row);
            }
        }

        // If no participants are available, prompt the user to configure them first.
        if (available.Count == 0)
        {
            var noAvail = new TextBlock
            {
                Text         = "No eligible participants found.\n\n" +
                               "Add and enable participants in the sidebar first, then return here to add them as agents.",
                FontSize     = 12, FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0)
            };
            noAvail.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            root.Children.Add(noAvail);
        }

        // ── Close row ──────────────────────────────────────────────────────
        var bottomRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        root.Children.Add(bottomRow);
        var closeBtn = MakeBridgeActionBtn("Done", false);
        closeBtn.Click += (_, _) => win.Close();
        bottomRow.Children.Add(closeBtn);

        win.ShowDialog();
        if (added) BuildBridgeContent();
    }

    // ── Bridge helpers ────────────────────────────────────────────────────

    private void BridgeLog(string line)
    {
        var stamp = $"[{DateTime.Now:HH:mm:ss}]  {line}";
        _bridgeLog.Add(stamp);
        if (_bridgeLog.Count > BridgeLogMaxLines) _bridgeLog.RemoveAt(0);
        AppendLogLine(stamp);
        _bridgeLogScroll?.ScrollToBottom();
    }

    private void AppendLogLine(string text)
    {
        if (_bridgeLogPanel is null) return;
        var tb = new TextBlock
        {
            Text = text, FontSize = 11, FontFamily = new FontFamily("Consolas,Courier New"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1)
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        _bridgeLogPanel.Children.Add(tb);
        while (_bridgeLogPanel.Children.Count > BridgeLogMaxLines)
            _bridgeLogPanel.Children.RemoveAt(0);
    }

    private void AddSectionLabel(StackPanel parent, string text, double topMargin = 14)
    {
        var lbl = new TextBlock
        {
            Text = text, FontSize = 10, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, topMargin, 0, 6)
        };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        parent.Children.Add(lbl);
    }

    private Button MakeBridgeSmallBtn(string label)
    {
        var btn = new Button
        {
            Content = label, FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
            Padding = new Thickness(10, 4, 10, 4), Cursor = Cursors.Hand, BorderThickness = new Thickness(1)
        };
        btn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        btn.SetResourceReference(Button.ForegroundProperty, "SidebarTextBrush");
        btn.SetResourceReference(Button.BorderBrushProperty, "ControlBorderBrush");
        return btn;
    }

    private Button MakeBridgeSubTabBtn(string label, bool active)
    {
        var btn = new Button
        {
            Style   = (Style)FindResource("FlatButton"),
            Content = label, FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
            Padding = new Thickness(0, 8, 0, 8), Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0, 0, 0, active ? 2 : 1)
        };
        if (active)
        {
            btn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
            btn.SetResourceReference(Button.ForegroundProperty, "AccentHighlightBrush");
            btn.SetResourceReference(Button.BorderBrushProperty, "AccentHighlightBrush");
        }
        else
        {
            btn.SetResourceReference(Button.BackgroundProperty, "SidebarBgBrush");
            btn.SetResourceReference(Button.ForegroundProperty, "SidebarDimBrush");
            btn.SetResourceReference(Button.BorderBrushProperty, "ControlBorderBrush");
        }
        return btn;
    }

    private void SetSubTabActive(Button btn, bool active)
    {
        btn.BorderThickness = new Thickness(0, 0, 0, active ? 2 : 1);
        if (active)
        {
            btn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
            btn.SetResourceReference(Button.ForegroundProperty, "AccentHighlightBrush");
            btn.SetResourceReference(Button.BorderBrushProperty, "AccentHighlightBrush");
        }
        else
        {
            btn.SetResourceReference(Button.BackgroundProperty, "SidebarBgBrush");
            btn.SetResourceReference(Button.ForegroundProperty, "SidebarDimBrush");
            btn.SetResourceReference(Button.BorderBrushProperty, "ControlBorderBrush");
        }
    }

    private Button MakeBridgeActionBtn(string label, bool isPrimary)
    {
        var btn = new Button
        {
            Content = label, FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
            Padding = new Thickness(16, 7, 16, 7), Cursor = Cursors.Hand, BorderThickness = new Thickness(0)
        };
        if (isPrimary)
        {
            btn.SetResourceReference(Button.BackgroundProperty, "AccentBgBrush");
            btn.SetResourceReference(Button.ForegroundProperty, "SidebarBgBrush");
        }
        else
        {
            btn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
            btn.SetResourceReference(Button.ForegroundProperty, "ContentTextBrush");
        }
        return btn;
    }

    /// <summary>
    /// Builds MCP tools from the dedicated Bridge agents list.
    /// Ollama agents get a fresh OllamaService instance.
    /// Cloud agents reuse the existing participant service if available,
    /// otherwise create one from the stored API key.
    /// </summary>
    private List<McpTool> BuildMcpTools(BridgeAgentMode? mode = null)
    {
        var cfg      = SettingsService.Load();
        var modeToUse = mode ?? cfg.BridgeMode;
        var disabledList = modeToUse == BridgeAgentMode.McpServer
            ? cfg.DisabledMcpServerTools
            : cfg.DisabledControllerTools;
        var disabled = new HashSet<string>(disabledList, StringComparer.OrdinalIgnoreCase);
        var tools    = new List<McpTool>();

        // Local helper - only add the tool if not disabled by the user
        void AddTool(McpTool t) { if (!disabled.Contains(t.Name)) tools.Add(t); }

        // ── Build agent dispatch table (name → async handler) ────────────
        var agentHandlers = new Dictionary<string, Func<string, CancellationToken, Task<string>>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var agent in cfg.BridgeAgents.Where(a => a.IsEnabled))
        {
            var capturedAgent = agent;

            if (agent.IsLocal)
            {
                var svc = new OllamaService(agent.ServerUrl) { CurrentModel = agent.Model };
                agentHandlers[agent.Label] = async (msg, ct) =>
                {
                    var history = new List<OllamaChatMessage> { new("user", msg) };
                    var sb = new StringBuilder();
                    await foreach (var tok in svc.StreamAsync(history, ct)) sb.Append(tok);
                    return sb.ToString().Trim();
                };
            }
            else
            {
                var existing = _cloudAIParticipants.FirstOrDefault(u =>
                    u.Data.Enabled &&
                    string.Equals(u.Data.Service.ProviderName, capturedAgent.Provider, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(u.Data.Service.CurrentModel,  capturedAgent.Model,    StringComparison.OrdinalIgnoreCase));

                ICloudAIService svc;
                if (existing is not null)
                    svc = existing.Data.Service;
                else
                {
                    var key = WindowsCredentialManager.Load(capturedAgent.Provider) ?? "";
                    svc = CreateCloudAIService(capturedAgent.Provider, key);
                    svc.CurrentModel = capturedAgent.Model;
                }

                agentHandlers[agent.Label] = async (msg, ct) =>
                {
                    var history = new List<CloudAIMessage> { new("user", msg) };
                    var sb = new StringBuilder();
                    await foreach (var tok in svc.StreamAsync(history, ct: ct)) sb.Append(tok);
                    return sb.ToString().Trim();
                };
            }
        }

        // ── bridge_list_agents - discover available agents ────────────────
        AddTool(new McpTool
        {
            Name        = "bridge_list_agents",
            Description = "List all currently enabled Bridge agents with their names, roles and specialties. " +
                          "Call this first to understand what each agent is best at, " +
                          "then use bridge_ask_agent(name, message) to talk to any of them.",
            Provider    = "Bridge",
            InputSchemaOverride = """{ "type": "object", "properties": {} }""",
            ExecuteAsync = (_, _) =>
            {
                var s            = SettingsService.Load();
                var participants = s.Participants;
                var sb           = new StringBuilder();
                sb.AppendLine("Available Bridge agents:");
                sb.AppendLine();
                var enabled = s.BridgeAgents.Where(a => a.IsEnabled).ToList();
                if (enabled.Count == 0)
                {
                    sb.AppendLine("  (No agents enabled — add and enable agents in the Bridge panel.)");
                }
                else
                {
                    foreach (var a in enabled)
                    {
                        // Match to a ParticipantConfig to surface role + self-description
                        var pc = participants.FirstOrDefault(p =>
                            string.Equals(p.Type,  a.Provider, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(p.Model, a.Model,    StringComparison.OrdinalIgnoreCase));

                        sb.AppendLine($"  • {a.Label}  ({(a.IsLocal ? "Local Ollama" : a.Provider)} / {a.Model})");
                        if (!string.IsNullOrWhiteSpace(pc?.Role))
                            sb.AppendLine($"    Role:      {pc.Role}");
                        if (!string.IsNullOrWhiteSpace(pc?.SelfDescription))
                            sb.AppendLine($"    Specialty: {pc.SelfDescription}");
                        if (!string.IsNullOrWhiteSpace(pc?.Likes))
                            sb.AppendLine($"    Likes:     {pc.Likes}");
                        sb.AppendLine();
                    }
                }
                sb.AppendLine("Pass any of these names to bridge_ask_agent.");
                return Task.FromResult(sb.ToString());
            }
        });

        // ── bridge_get_context - full situational picture in one call ─────
        AddTool(new McpTool
        {
            Name        = "bridge_get_context",
            Description = "Returns a complete situational snapshot: currently open project (name, type, path), " +
                          "available agents with roles and specialties, configured folders with access levels, " +
                          "and the temp workspace path. Call this once at the start of a session to orient yourself " +
                          "before deciding which agents or files to use.",
            Provider    = "Bridge",
            InputSchemaOverride = """{ "type": "object", "properties": {} }""",
            ExecuteAsync = (_, _) =>
            {
                var s            = SettingsService.Load();
                var participants = s.Participants;
                var sb           = new StringBuilder();

                // ── Project ───────────────────────────────────────────────
                sb.AppendLine("## Current Project");
                if (BridgeProject is not null && BridgeProjectFolder is not null)
                {
                    sb.AppendLine($"  Name:   {BridgeProject.ProjectName}");
                    sb.AppendLine($"  Type:   {BridgeProject.ProjectTypeName}");
                    sb.AppendLine($"  Folder: {BridgeProjectFolder}");
                    if (_bridgeProjectFolder is not null)
                        sb.AppendLine("  (loaded via bridge_open_project)");
                }
                else
                {
                    sb.AppendLine("  No project loaded.");
                    sb.AppendLine("  → Call bridge_list_projects then bridge_open_project(path) to load one.");
                }
                sb.AppendLine();

                // ── Agents ────────────────────────────────────────────────
                sb.AppendLine("## Available Agents");
                var enabled = s.BridgeAgents.Where(a => a.IsEnabled).ToList();
                if (enabled.Count == 0)
                {
                    sb.AppendLine("  (No agents enabled)");
                }
                else
                {
                    foreach (var a in enabled)
                    {
                        var pc = participants.FirstOrDefault(p =>
                            string.Equals(p.Type,  a.Provider, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(p.Model, a.Model,    StringComparison.OrdinalIgnoreCase));
                        sb.AppendLine($"  • {a.Label}  ({(a.IsLocal ? "Local Ollama" : a.Provider)} / {a.Model})");
                        if (!string.IsNullOrWhiteSpace(pc?.Role))
                            sb.AppendLine($"    Role:      {pc.Role}");
                        if (!string.IsNullOrWhiteSpace(pc?.SelfDescription))
                            sb.AppendLine($"    Specialty: {pc.SelfDescription}");
                        sb.AppendLine();
                    }
                }

                // ── Folders ───────────────────────────────────────────────
                sb.AppendLine("## Accessible Folders");
                if (s.BridgeFolders.Count == 0)
                {
                    sb.AppendLine("  (No folders configured)");
                }
                else
                {
                    foreach (var f in s.BridgeFolders)
                        sb.AppendLine($"  • {f.Path}  [{(f.AllowWrite ? "READ + WRITE" : "READ only")}]");
                }
                sb.AppendLine();

                // ── Workspace ─────────────────────────────────────────────
                sb.AppendLine("## Temp Workspace");
                sb.AppendLine(string.IsNullOrWhiteSpace(s.BridgeTempFolder)
                    ? "  (Not configured)"
                    : $"  {s.BridgeTempFolder}");
                sb.AppendLine();
                // ── Roadmap summary ───────────────────────────────────────
                if (BridgeRoadmap is not null && BridgeRoadmap.Milestones.Count > 0)
                {
                    sb.AppendLine("## Roadmap Summary");
                    foreach (var ms in BridgeRoadmap.Milestones)
                    {
                        var done  = ms.Items.Count(i => i.Status == ItemStatus.Done);
                        var total = ms.Items.Count;
                        sb.AppendLine($"  {RoadmapService.StatusIcon(ms.Status)} {ms.Title}  [{ms.Progress}%  {done}/{total} tasks]");
                    }
                    sb.AppendLine();
                    sb.AppendLine("  Call bridge_get_roadmap for full task details and item IDs.");
                    sb.AppendLine();
                }

                sb.AppendLine("Use bridge_ask_agent to talk to agents, bridge_list_folders / bridge_read_file for file access.");
                sb.AppendLine("Use bridge_get_project_info for detailed project structure, bridge_get_roadmap for full roadmap.");

                return Task.FromResult(sb.ToString());
            }
        });

        // ── bridge_list_projects - discover available projects ────────────
        AddTool(new McpTool
        {
            Name        = "bridge_list_projects",
            Description = "Lists all ClaudetRelay projects available in the configured projects folder. " +
                          "Returns project names, types, last-opened dates and folder paths. " +
                          "Use bridge_open_project(path) to load one for bridge_get_project_info / bridge_get_roadmap.",
            Provider    = "Bridge",
            InputSchemaOverride = """{ "type": "object", "properties": {} }""",
            ExecuteAsync = (_, _) =>
            {
                var cfg        = SettingsService.Load();
                var rootFolder = Services.ProjectService.ResolveFolder(cfg.ProjectsFolder);
                var projects   = Services.ProjectService.ListProjects(rootFolder);

                if (projects.Count == 0)
                    return Task.FromResult(
                        $"No projects found in '{rootFolder}'.\n" +
                        "Create a project in ClaudetRelay first, or check the projects folder in General Settings.");

                var sb = new StringBuilder();
                sb.AppendLine($"Available projects  ({rootFolder}):");
                sb.AppendLine();
                foreach (var (folder, proj) in projects.OrderByDescending(p => p.Settings.LastOpened))
                {
                    sb.AppendLine($"  • {proj.ProjectName}  [{proj.ProjectTypeName}]");
                    sb.AppendLine($"    Last opened: {proj.LastOpened:yyyy-MM-dd}");
                    sb.AppendLine($"    Path:        {folder}");
                    sb.AppendLine();
                }
                sb.AppendLine("Pass a Path to bridge_open_project to load that project.");
                return Task.FromResult(sb.ToString());
            }
        });

        // ── bridge_open_project - load a project for bridge use ───────────
        AddTool(new McpTool
        {
            Name        = "bridge_open_project",
            Description = "Loads a ClaudetRelay project by folder path so bridge_get_project_info, " +
                          "bridge_get_roadmap, bridge_update_roadmap_item and bridge_complete_roadmap_item " +
                          "have a project to work with. The project stays loaded until bridge_open_project " +
                          "is called again or the MCP server is restarted. " +
                          "Get folder paths from bridge_list_projects.",
            Provider    = "Bridge",
            InputSchemaOverride = """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "description": "Absolute path to the project folder" }
                  },
                  "required": ["path"]
                }
                """,
            ExecuteAsync = (args, _) =>
            {
                var path = args["path"]?.GetValue<string>()?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(path))
                    return Task.FromResult("Error: path is required. Use bridge_list_projects to find valid paths.");

                if (!System.IO.Directory.Exists(path))
                    return Task.FromResult($"Error: folder not found: '{path}'");

                // Guard: refuse to switch while another project is loaded
                if (_bridgeProject is not null &&
                    !string.Equals(_bridgeProjectFolder, path, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(
                        $"Error: project '{_bridgeProject.ProjectName}' is already loaded. " +
                        "Call bridge_open_project with the same path to reload it, " +
                        "or call bridge_close_project to unload it first.");
                }

                var proj = Services.ProjectService.LoadProject(path);
                if (proj is null)
                    return Task.FromResult(
                        $"Error: no ClaudetRelay project found at '{path}'. " +
                        "Make sure the path points to a project folder (contains project.json).");

                _bridgeProjectFolder = path;
                _bridgeProject       = proj;
                _bridgeRoadmap       = RoadmapService.Load(path);
                Dispatcher.InvokeAsync(() => {
                    if (ProjectsContent.Visibility == Visibility.Visible) RefreshProjectList();
                });

                var milestoneCount = _bridgeRoadmap.Milestones.Count;
                var taskCount      = _bridgeRoadmap.Milestones.Sum(m => m.Items.Count);
                return Task.FromResult(
                    $"Project loaded: {proj.ProjectName}  [{proj.ProjectTypeName}]\n" +
                    $"  Folder:     {path}\n" +
                    $"  Roadmap:    {milestoneCount} milestone(s), {taskCount} task(s)\n\n" +
                    "You can now call bridge_get_project_info, bridge_get_roadmap, " +
                    "bridge_update_roadmap_item and bridge_complete_roadmap_item.");
            }
        });

        // ── bridge_close_project - unload the current bridge project ─────────
        AddTool(new McpTool
        {
            Name        = "bridge_close_project",
            Description = "Unloads the currently loaded bridge project. " +
                          "Call this before bridge_open_project when you want to switch to a different project.",
            Provider    = "Bridge",
            InputSchemaOverride = """{ "type": "object", "properties": {} }""",
            ExecuteAsync = (_, _) =>
            {
                if (_bridgeProject is null)
                    return Task.FromResult("No project is currently loaded.");
                var name = _bridgeProject.ProjectName;
                _bridgeProjectFolder = null;
                _bridgeProject       = null;
                _bridgeRoadmap       = null;
                Dispatcher.InvokeAsync(() => {
                    if (ProjectsContent.Visibility == Visibility.Visible) RefreshProjectList();
                });
                return Task.FromResult($"✓ Project '{name}' unloaded.");
            }
        });

        // ── bridge_get_project_info - full project details ────────────────
        AddTool(new McpTool
        {
            Name        = "bridge_get_project_info",
            Description = "Returns full details about the bridge-loaded project: name, type, description, " +
                          "language, folder path, participant roles, and a list of project plan files. " +
                          "Call bridge_open_project first if no project is loaded.",
            Provider    = "Bridge",
            InputSchemaOverride = """{ "type": "object", "properties": {} }""",
            ExecuteAsync = (_, _) =>
            {
                if (BridgeProject is null || BridgeProjectFolder is null)
                    return Task.FromResult(
                        "No project loaded. Call bridge_list_projects to see available projects, " +
                        "then bridge_open_project(path) to load one.");

                var sb = new StringBuilder();
                sb.AppendLine($"## Project: {BridgeProject.ProjectName}");
                sb.AppendLine($"  Type:        {BridgeProject.ProjectTypeName}");
                if (!string.IsNullOrWhiteSpace(BridgeProject.Language))
                    sb.AppendLine($"  Language:    {BridgeProject.Language}");
                sb.AppendLine($"  Folder:      {BridgeProjectFolder}");
                sb.AppendLine($"  Created:     {BridgeProject.CreatedAt:yyyy-MM-dd}");
                sb.AppendLine($"  Last opened: {BridgeProject.LastOpened:yyyy-MM-dd}");

                if (!string.IsNullOrWhiteSpace(BridgeProject.Description))
                {
                    sb.AppendLine();
                    sb.AppendLine("### Description");
                    sb.AppendLine(BridgeProject.Description);
                }

                if (BridgeProject.Roles.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("### Participant Roles");
                    foreach (var r in BridgeProject.Roles)
                    {
                        sb.AppendLine($"  • {r.DisplayName}  ({r.Provider} / {r.Model})");
                        if (!string.IsNullOrWhiteSpace(r.RoleInstruction))
                        {
                            var snippet = r.RoleInstruction.Trim();
                            if (snippet.Length > 120) snippet = snippet[..120] + "…";
                            sb.AppendLine($"    Instruction: {snippet}");
                        }
                    }
                }

                var planDir = System.IO.Path.Combine(BridgeProjectFolder, "PROJECTPLAN");
                if (System.IO.Directory.Exists(planDir))
                {
                    sb.AppendLine();
                    sb.AppendLine("### Project Plan Files  (PROJECTPLAN/)");
                    foreach (var sub in System.IO.Directory.GetDirectories(planDir).OrderBy(d => d))
                    {
                        var subName = System.IO.Path.GetFileName(sub);
                        var files   = System.IO.Directory.GetFiles(sub).Select(System.IO.Path.GetFileName).OrderBy(f => f).ToList();
                        if (files.Count > 0)
                            sb.AppendLine($"  {subName}/  →  {string.Join(", ", files)}");
                    }
                    foreach (var f in System.IO.Directory.GetFiles(planDir).OrderBy(f => f))
                        sb.AppendLine($"  {System.IO.Path.GetFileName(f)}");
                }

                sb.AppendLine();
                sb.AppendLine("Use bridge_get_roadmap to see the project roadmap, bridge_read_file to read any listed file.");
                return Task.FromResult(sb.ToString());
            }
        });

        // ── bridge_get_roadmap - full roadmap state ───────────────────────
        AddTool(new McpTool
        {
            Name        = "bridge_get_roadmap",
            Description = "Returns the full project roadmap: milestones, tasks, progress percentages, " +
                          "status and item IDs needed for update/complete calls. " +
                          "Call bridge_open_project first if no project is loaded.",
            Provider    = "Bridge",
            InputSchemaOverride = """{ "type": "object", "properties": {} }""",
            ExecuteAsync = (_, _) =>
            {
                if (BridgeProjectFolder is null || BridgeRoadmap is null)
                    return Task.FromResult(
                        "No project loaded. Call bridge_list_projects then bridge_open_project(path). " +
                        "If the project is loaded but has no roadmap, ask a coordinator agent to create one.");

                var text = RoadmapService.GetContextText(BridgeRoadmap, isCoordinator: true);
                return Task.FromResult(string.IsNullOrWhiteSpace(text)
                    ? "The project roadmap is empty (no milestones defined yet)."
                    : text);
            }
        });

        // ── bridge_update_roadmap_item - set progress on a task ───────────
        AddTool(new McpTool
        {
            Name        = "bridge_update_roadmap_item",
            Description = "Set the progress percentage (0–100) on a roadmap task by its item ID. " +
                          "Setting 100 automatically marks the item as Done. " +
                          "Get item IDs from bridge_get_roadmap. Call bridge_open_project first.",
            Provider    = "Bridge",
            InputSchemaOverride = """
                {
                  "type": "object",
                  "properties": {
                    "item_id":  { "type": "string",  "description": "8-character hex item ID from bridge_get_roadmap" },
                    "progress": { "type": "integer", "description": "Progress percentage 0–100" }
                  },
                  "required": ["item_id", "progress"]
                }
                """,
            ExecuteAsync = async (args, _) =>
            {
                if (BridgeProjectFolder is null || BridgeRoadmap is null)
                    return "No project loaded. Call bridge_open_project first.";

                var id       = args["item_id"]?.GetValue<string>()?.ToLowerInvariant() ?? "";
                var progress = Math.Clamp(args["progress"]?.GetValue<int>() ?? 0, 0, 100);

                if (string.IsNullOrWhiteSpace(id))
                    return "Error: item_id is required. Get item IDs from bridge_get_roadmap.";

                var item = BridgeRoadmap.Milestones
                    .SelectMany(ms => ms.Items)
                    .FirstOrDefault(i => i.Id == id);

                if (item is null)
                    return $"Error: no roadmap item with id '{id}'. Call bridge_get_roadmap to see valid IDs.";

                item.Progress = progress;
                item.Status   = progress >= 100 ? ItemStatus.Done
                              : progress > 0    ? ItemStatus.InProgress
                              : ItemStatus.Todo;
                var parent = BridgeRoadmap.Milestones.First(ms => ms.Items.Contains(item));
                UpdateMilestoneStatus(parent);

                // Save to disk; refresh the live UI panel only if this is also the main chat's project
                RoadmapService.Save(BridgeProjectFolder, BridgeRoadmap);
                if (BridgeProjectFolder == _currentProjectFolder)
                {
                    _currentRoadmap = BridgeRoadmap;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (RoadmapContent.Visibility == Visibility.Visible) BuildRoadmapContent();
                    });
                }

                return $"Updated '{item.Title}' → {progress}%  ({item.Status})";
            }
        });

        // ── bridge_complete_roadmap_item - mark a task as done ────────────
        AddTool(new McpTool
        {
            Name        = "bridge_complete_roadmap_item",
            Description = "Mark a roadmap task as fully complete (100% / Done) and record who completed it. " +
                          "Get item IDs from bridge_get_roadmap. Call bridge_open_project first.",
            Provider    = "Bridge",
            InputSchemaOverride = """
                {
                  "type": "object",
                  "properties": {
                    "item_id":      { "type": "string", "description": "8-character hex item ID from bridge_get_roadmap" },
                    "completed_by": { "type": "string", "description": "Name of the agent or user completing this item" }
                  },
                  "required": ["item_id"]
                }
                """,
            ExecuteAsync = async (args, _) =>
            {
                if (BridgeProjectFolder is null || BridgeRoadmap is null)
                    return "No project loaded. Call bridge_open_project first.";

                var id          = args["item_id"]?.GetValue<string>()?.ToLowerInvariant() ?? "";
                var completedBy = args["completed_by"]?.GetValue<string>() ?? "Bridge";

                if (string.IsNullOrWhiteSpace(id))
                    return "Error: item_id is required. Get item IDs from bridge_get_roadmap.";

                var item = BridgeRoadmap.Milestones
                    .SelectMany(ms => ms.Items)
                    .FirstOrDefault(i => i.Id == id);

                if (item is null)
                    return $"Error: no roadmap item with id '{id}'. Call bridge_get_roadmap to see valid IDs.";

                item.Progress    = 100;
                item.Status      = ItemStatus.Done;
                item.CompletedBy = completedBy;
                item.CompletedAt = DateTime.UtcNow;
                var parent = BridgeRoadmap.Milestones.First(ms => ms.Items.Contains(item));
                UpdateMilestoneStatus(parent);

                RoadmapService.Save(BridgeProjectFolder, BridgeRoadmap);
                if (BridgeProjectFolder == _currentProjectFolder)
                {
                    _currentRoadmap = BridgeRoadmap;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (RoadmapContent.Visibility == Visibility.Visible) BuildRoadmapContent();
                    });
                }

                return $"Completed '{item.Title}' — marked Done by {completedBy}.";
            }
        });

        // ── bridge_ask_agent - talk to any agent by name ──────────────────
        AddTool(new McpTool
        {
            Name        = "bridge_ask_agent",
            Description = "Send a message to a named Bridge agent and get its response. " +
                          "Use bridge_list_agents first to get the list of available agent names.",
            Provider    = "Bridge",
            InputSchemaOverride = """
                {
                  "type": "object",
                  "properties": {
                    "name":    { "type": "string", "description": "Agent name - from bridge_list_agents" },
                    "message": { "type": "string", "description": "The message or prompt to send" }
                  },
                  "required": ["name", "message"]
                }
                """,
            ExecuteAsync = async (args, ct) =>
            {
                var name    = args["name"]?.GetValue<string>()    ?? "";
                var message = args["message"]?.GetValue<string>() ?? "";

                if (string.IsNullOrWhiteSpace(name))
                    return "Error: 'name' is required. Call bridge_list_agents to see available agents.";
                if (string.IsNullOrWhiteSpace(message))
                    return "Error: 'message' is required.";
                if (!agentHandlers.TryGetValue(name, out var handler))
                    return $"Error: no agent named '{name}' is currently enabled. " +
                           $"Call bridge_list_agents to see available agents.";

                return await handler(message, ct);
            }
        });

        // ── bridge_post_to_agents - broadcast task to all enabled agents ─────
        AddTool(new McpTool
        {
            Name        = "bridge_post_to_agents",
            Description = "Sends a task or message to ALL enabled Bridge agents in parallel and returns " +
                          "each agent's response. Use this instead of chat_post_message when you want " +
                          "silent agent work — results come back as tool output, nothing is posted to chat. " +
                          "Set parallel=true to query agents simultaneously (faster but unordered). " +
                          "Default is sequential (ordered, each agent sees only your message, not others' replies).",
            Provider    = "Bridge",
            InputSchemaOverride = """
                {
                  "type": "object",
                  "properties": {
                    "message":  { "type": "string",  "description": "The task or prompt to send to all agents" },
                    "parallel": { "type": "boolean", "description": "Run agents in parallel (default false = sequential)" }
                  },
                  "required": ["message"]
                }
                """,
            ExecuteAsync = async (args, ct) =>
            {
                var message  = args["message"]?.GetValue<string>()?.Trim() ?? "";
                var parallel = args["parallel"]?.GetValue<bool>() ?? false;

                if (string.IsNullOrWhiteSpace(message))
                    return "Error: message is required.";

                var activeAgents = cfg.BridgeAgents.Where(a => a.IsEnabled).ToList();
                if (activeAgents.Count == 0)
                    return "Error: no Bridge agents are configured or enabled. " +
                           "Add agents in the Bridge → Agents panel first.";

                var results = new List<(string Name, string Response)>();

                if (parallel)
                {
                    var tasks = activeAgents.Select(async agent =>
                    {
                        if (!agentHandlers.TryGetValue(agent.Label, out var handler))
                            return (agent.Label, $"Error: agent '{agent.Label}' is not available.");
                        try   { return (agent.Label, await handler(message, ct)); }
                        catch (Exception ex) { return (agent.Label, $"Error: {ex.Message}"); }
                    });
                    results.AddRange(await Task.WhenAll(tasks));
                }
                else
                {
                    foreach (var agent in activeAgents)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (!agentHandlers.TryGetValue(agent.Label, out var handler))
                        { results.Add((agent.Label, $"Error: agent '{agent.Label}' is not available.")); continue; }
                        try   { results.Add((agent.Label, await handler(message, ct))); }
                        catch (Exception ex) { results.Add((agent.Label, $"Error: {ex.Message}")); }
                    }
                }

                var sb = new StringBuilder();
                sb.AppendLine($"Responses from {results.Count} agent(s):\n");
                foreach (var (name, response) in results)
                {
                    sb.AppendLine($"## {name}");
                    sb.AppendLine(response.Trim());
                    sb.AppendLine();
                }
                return sb.ToString().TrimEnd();
            }
        });

        // ── Bridge file-access tools ──────────────────────────────────────
        AddTool(new McpTool
        {
            Name        = "bridge_list_folders",
            Description = "List all Bridge folders that have been configured by the user, " +
                          "including their full paths and whether write access is enabled. " +
                          "Call this to discover which paths you can use with the other bridge file tools.",
            Provider    = "Bridge",
            InputSchemaOverride = """{ "type": "object", "properties": {} }""",
            ExecuteAsync = (_, _) =>
            {
                var s  = SettingsService.Load();
                var sb = new StringBuilder();
                sb.AppendLine("Configured Bridge folders:");
                sb.AppendLine();
                if (s.BridgeFolders.Count == 0)
                {
                    sb.AppendLine("  (No folders configured - add folders in the Bridge → Folders panel.)");
                }
                else
                {
                    foreach (var f in s.BridgeFolders)
                    {
                        var access = f.AllowWrite ? "READ + WRITE" : "READ only";
                        sb.AppendLine($"  • {f.Path}  [{access}]");
                    }
                }
                sb.AppendLine();
                sb.AppendLine("Use bridge_list_folder(path) to explore contents, bridge_read_file(path) to read,");
                sb.AppendLine("and bridge_write_file(path, content) for write-enabled folders.");
                return Task.FromResult(sb.ToString());
            }
        });

        AddTool(new McpTool
        {
            Name        = "bridge_list_folder",
            Description = "List the files and subfolders inside a configured Bridge folder (or any subfolder of one). " +
                          "Only paths within folders registered in the Bridge are accessible.",
            Provider    = "Bridge",
            InputSchemaOverride = """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "description": "Absolute path to the folder to list" }
                  },
                  "required": ["path"]
                }
                """,
            ExecuteAsync = (args, _) =>
                Task.FromResult(BridgeListFolder(args["path"]?.GetValue<string>() ?? ""))
        });

        AddTool(new McpTool
        {
            Name        = "bridge_read_file",
            Description = "Read the text contents of a file inside a configured Bridge folder. " +
                          "Maximum file size 200 KB. Only paths within registered Bridge folders are readable.",
            Provider    = "Bridge",
            InputSchemaOverride = """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "description": "Absolute path to the file to read" }
                  },
                  "required": ["path"]
                }
                """,
            ExecuteAsync = async (args, ct) =>
                await BridgeReadFileAsync(args["path"]?.GetValue<string>() ?? "", ct)
        });

        AddTool(new McpTool
        {
            Name        = "bridge_write_file",
            Description = "Write (or overwrite) a text file inside a Bridge folder that has write access enabled. " +
                          "The folder must be configured in the Bridge with 'Allow write' checked.",
            Provider    = "Bridge",
            InputSchemaOverride = """
                {
                  "type": "object",
                  "properties": {
                    "path":    { "type": "string", "description": "Absolute path to the file to write" },
                    "content": { "type": "string", "description": "Text content to write" }
                  },
                  "required": ["path", "content"]
                }
                """,
            ExecuteAsync = async (args, ct) =>
                await BridgeWriteFileAsync(
                    args["path"]?.GetValue<string>()    ?? "",
                    args["content"]?.GetValue<string>() ?? "", ct)
        });

        AddTool(new McpTool
        {
            Name        = "bridge_append_file",
            Description = "Append text to the end of a file inside a write-enabled Bridge folder. " +
                          "Creates the file if it does not exist. Use this for logs, journals, or incremental output.",
            Provider    = "Bridge",
            InputSchemaOverride = """
                {
                  "type": "object",
                  "properties": {
                    "path":    { "type": "string", "description": "Absolute path to the file" },
                    "content": { "type": "string", "description": "Text to append" }
                  },
                  "required": ["path", "content"]
                }
                """,
            ExecuteAsync = async (args, ct) =>
                await BridgeAppendFileAsync(
                    args["path"]?.GetValue<string>()    ?? "",
                    args["content"]?.GetValue<string>() ?? "", ct)
        });

        AddTool(new McpTool
        {
            Name        = "bridge_delete_file",
            Description = "Delete a file inside a write-enabled Bridge folder.",
            Provider    = "Bridge",
            InputSchemaOverride = """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "description": "Absolute path to the file to delete" }
                  },
                  "required": ["path"]
                }
                """,
            ExecuteAsync = (args, _) =>
                Task.FromResult(BridgeDeleteFile(args["path"]?.GetValue<string>() ?? ""))
        });

        AddTool(new McpTool
        {
            Name        = "bridge_create_folder",
            Description = "Create a new folder (including any missing parent folders) inside a write-enabled Bridge folder.",
            Provider    = "Bridge",
            InputSchemaOverride = """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "description": "Absolute path of the folder to create" }
                  },
                  "required": ["path"]
                }
                """,
            ExecuteAsync = (args, _) =>
                Task.FromResult(BridgeCreateFolder(args["path"]?.GetValue<string>() ?? ""))
        });

        AddTool(new McpTool
        {
            Name        = "bridge_rename",
            Description = "Rename a file or folder in place. The item must be inside a write-enabled Bridge folder. " +
                          "Provide only the new name (not a full path) - the item stays in its current directory.",
            Provider    = "Bridge",
            InputSchemaOverride = """
                {
                  "type": "object",
                  "properties": {
                    "path":     { "type": "string", "description": "Absolute path to the file or folder to rename" },
                    "new_name": { "type": "string", "description": "New name only (no path separators)" }
                  },
                  "required": ["path", "new_name"]
                }
                """,
            ExecuteAsync = (args, _) =>
                Task.FromResult(BridgeRename(
                    args["path"]?.GetValue<string>()     ?? "",
                    args["new_name"]?.GetValue<string>() ?? ""))
        });

        AddTool(new McpTool
        {
            Name        = "bridge_move_file",
            Description = "Move a file from one location to another. Both source and destination must be inside " +
                          "write-enabled Bridge folders. Destination includes the full new filename.",
            Provider    = "Bridge",
            InputSchemaOverride = """
                {
                  "type": "object",
                  "properties": {
                    "source":      { "type": "string", "description": "Absolute path of the file to move" },
                    "destination": { "type": "string", "description": "Absolute destination path including filename" }
                  },
                  "required": ["source", "destination"]
                }
                """,
            ExecuteAsync = (args, _) =>
                Task.FromResult(BridgeMoveFile(
                    args["source"]?.GetValue<string>()      ?? "",
                    args["destination"]?.GetValue<string>() ?? ""))
        });

        AddTool(new McpTool
        {
            Name        = "bridge_file_exists",
            Description = "Check whether a file or folder exists at the given path inside a configured Bridge folder. " +
                          "Returns 'exists:file', 'exists:folder', or 'not_found'.",
            Provider    = "Bridge",
            InputSchemaOverride = """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "description": "Absolute path to check" }
                  },
                  "required": ["path"]
                }
                """,
            ExecuteAsync = (args, _) =>
                Task.FromResult(BridgeFileExists(args["path"]?.GetValue<string>() ?? ""))
        });

        AddTool(new McpTool
        {
            Name        = "bridge_read_file_binary",
            Description = "Read any file (including images, PDFs, zip files, etc.) as base64-encoded binary. " +
                          "Returns mime type, size in bytes, and base64 content. Maximum 10 MB.",
            Provider    = "Bridge",
            InputSchemaOverride = """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "description": "Absolute path to the file to read" }
                  },
                  "required": ["path"]
                }
                """,
            ExecuteAsync = async (args, ct) =>
                await BridgeReadFileBinaryAsync(args["path"]?.GetValue<string>() ?? "", ct)
        });

        AddTool(new McpTool
        {
            Name        = "bridge_write_file_binary",
            Description = "Write binary content (base64-encoded) to a file inside a write-enabled Bridge folder. " +
                          "Use this for images, PDFs, zip files, or any non-text content.",
            Provider    = "Bridge",
            InputSchemaOverride = """
                {
                  "type": "object",
                  "properties": {
                    "path":           { "type": "string", "description": "Absolute path to write" },
                    "content_base64": { "type": "string", "description": "File content encoded as base64" }
                  },
                  "required": ["path", "content_base64"]
                }
                """,
            ExecuteAsync = async (args, ct) =>
                await BridgeWriteFileBinaryAsync(
                    args["path"]?.GetValue<string>()           ?? "",
                    args["content_base64"]?.GetValue<string>() ?? "", ct)
        });

        // ── Parallel task tools ───────────────────────────────────────────
        AddTool(new McpTool
        {
            Name        = "bridge_get_workspace",
            Description = "Returns the configured temp workspace folder path. " +
                          "Use this path as the base for all agent task output files in parallel workflows.",
            Provider    = "Bridge",
            InputSchemaOverride = """{ "type": "object", "properties": {} }""",
            ExecuteAsync = (_, _) =>
            {
                var s = SettingsService.Load();
                if (string.IsNullOrWhiteSpace(s.BridgeTempFolder))
                    return Task.FromResult(
                        "Error: No temp workspace configured. " +
                        "Set one in Bridge → Folders → Temp Workspace.");
                return Task.FromResult(
                    $"Temp workspace: {s.BridgeTempFolder}\n\n" +
                    "Use this path as the base directory for agent task output files. " +
                    "Pass sub-paths like '{workspace}\\agent_name_task.txt' to bridge_run_agent_task.");
            }
        });

        AddTool(new McpTool
        {
            Name        = "bridge_run_agent_task",
            Description = "Fire an agent task asynchronously. The agent runs in the background and writes its " +
                          "response to output_file when done. Returns a task_id immediately - use " +
                          "bridge_wait_for_tasks to collect results. Call bridge_list_agents first for valid names. " +
                          "If output_file is a relative path it is resolved inside the temp workspace.",
            Provider    = "Bridge",
            InputSchemaOverride = """
                {
                  "type": "object",
                  "properties": {
                    "name":        { "type": "string", "description": "Agent name from bridge_list_agents" },
                    "message":     { "type": "string", "description": "The prompt to send to the agent" },
                    "output_file": { "type": "string", "description": "Absolute or workspace-relative path for the agent's output" }
                  },
                  "required": ["name", "message", "output_file"]
                }
                """,
            ExecuteAsync = async (args, _) =>
            {
                var name       = args["name"]?.GetValue<string>()        ?? "";
                var message    = args["message"]?.GetValue<string>()     ?? "";
                var outputFile = args["output_file"]?.GetValue<string>() ?? "";

                if (!agentHandlers.TryGetValue(name, out var handler))
                    return $"Error: no agent named '{name}'. Call bridge_list_agents.";

                var cfg = SettingsService.Load();

                // Resolve relative paths against temp workspace
                if (!string.IsNullOrEmpty(outputFile) && !System.IO.Path.IsPathRooted(outputFile))
                {
                    if (string.IsNullOrWhiteSpace(cfg.BridgeTempFolder))
                        return "Error: output_file is relative but no temp workspace is configured. " +
                               "Set one in Bridge → Folders → Temp Workspace, or provide an absolute path.";
                    outputFile = System.IO.Path.Combine(cfg.BridgeTempFolder, outputFile);
                }

                if (!IsWithinBridgeFolder(outputFile, cfg, requireWrite: true, out var reason))
                    return $"Access denied for output_file: {reason}";

                var bt = new BridgeTask { AgentName = name, OutputFile = outputFile };
                _bridgeTasks[bt.Id] = bt;

                // Fire and forget - agent runs independently
                var bgTask = System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        var result = await handler(message, CancellationToken.None);
                        System.IO.Directory.CreateDirectory(
                            System.IO.Path.GetDirectoryName(outputFile)!);
                        await System.IO.File.WriteAllTextAsync(outputFile, result);
                        bt.Status     = "completed";
                        bt.FinishedAt = DateTime.Now;
                    }
                    catch (Exception ex)
                    {
                        bt.Status     = "failed";
                        bt.Error      = ex.Message;
                        bt.FinishedAt = DateTime.Now;
                    }
                });

                return $"task_id:{bt.Id}\nagent:{name}\noutput:{outputFile}\nstatus:running\nstarted:{bt.StartedAt:HH:mm:ss}";
            }
        });

        AddTool(new McpTool
        {
            Name        = "bridge_list_active_tasks",
            Description = "List all agent tasks with their current status (running / completed / failed / timeout), " +
                          "duration, output file path, and any error messages. " +
                          "Completed tasks older than 4 hours are automatically removed.",
            Provider    = "Bridge",
            InputSchemaOverride = """{ "type": "object", "properties": {} }""",
            ExecuteAsync = (_, _) =>
            {
                // Auto-clean tasks older than 4 hours
                var cutoff = DateTime.Now.AddHours(-4);
                foreach (var old in _bridgeTasks.Values
                    .Where(t => t.Status != "running" && t.FinishedAt < cutoff).ToList())
                    _bridgeTasks.TryRemove(old.Id, out _);

                var tasks = _bridgeTasks.Values.OrderBy(t => t.StartedAt).ToList();
                if (tasks.Count == 0)
                    return System.Threading.Tasks.Task.FromResult("No tasks on record.");

                var sb = new StringBuilder();
                sb.AppendLine($"Agent tasks ({tasks.Count}):");
                sb.AppendLine();
                foreach (var t in tasks)
                {
                    var dur  = ((t.FinishedAt ?? DateTime.Now) - t.StartedAt).TotalSeconds;
                    var icon = t.Status switch { "completed" => "✅", "failed" => "❌", "timeout" => "⏱", _ => "⏳" };
                    sb.AppendLine($"{icon}  [{t.Id}]  {t.AgentName}  -  {t.Status.ToUpper()}  ({dur:F1}s)");
                    sb.AppendLine($"     output: {t.OutputFile}");
                    if (t.Error is not null) sb.AppendLine($"     error:  {t.Error}");
                    sb.AppendLine();
                }
                return System.Threading.Tasks.Task.FromResult(sb.ToString());
            }
        });

        AddTool(new McpTool
        {
            Name        = "bridge_wait_for_tasks",
            Description = "Wait for one or more agent tasks to finish. Polls every 500 ms until all tasks " +
                          "complete or the timeout is reached. Returns per-task status including output file paths " +
                          "and error details. Tasks that exceed the timeout are marked 'timeout' with an explanation - " +
                          "they will NOT hang forever regardless of the cause (rate limit, connection drop, crash, etc.).",
            Provider    = "Bridge",
            InputSchemaOverride = """
                {
                  "type": "object",
                  "properties": {
                    "task_ids":        {
                      "type": "array",
                      "items": { "type": "string" },
                      "description": "Task IDs returned by bridge_run_agent_task"
                    },
                    "timeout_seconds": {
                      "type": "integer",
                      "description": "Max seconds to wait before returning (default 120, max 600)",
                      "default": 120
                    }
                  },
                  "required": ["task_ids"]
                }
                """,
            ExecuteAsync = async (args, ct) =>
            {
                var rawIds = args["task_ids"]?.AsArray()
                    ?.Select(n => n?.GetValue<string>() ?? "")
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList() ?? [];

                var timeoutSec = Math.Clamp(
                    args["timeout_seconds"]?.GetValue<int>() ?? 120, 1, 600);
                var deadline = DateTime.Now.AddSeconds(timeoutSec);

                if (rawIds.Count == 0)
                    return "Error: task_ids array is required and must not be empty.";

                // Validate IDs up front
                var tasks = new List<BridgeTask>();
                foreach (var id in rawIds)
                {
                    if (!_bridgeTasks.TryGetValue(id, out var t))
                        return $"Error: unknown task_id '{id}'. Call bridge_list_active_tasks to see valid IDs.";
                    tasks.Add(t);
                }

                // Poll until all settled or deadline
                while (DateTime.Now < deadline && !ct.IsCancellationRequested)
                {
                    if (tasks.All(t => t.Status != "running")) break;
                    await System.Threading.Tasks.Task.Delay(500, ct).ConfigureAwait(false);
                }

                // Stamp any still-running tasks as timeout
                foreach (var t in tasks.Where(t => t.Status == "running"))
                {
                    t.Status     = "timeout";
                    t.FinishedAt = DateTime.Now;
                    t.Error      = $"Agent '{t.AgentName}' did not respond within {timeoutSec}s. " +
                                   "Possible causes: model overloaded, API rate limit reached, " +
                                   "Ollama crashed, connection lost, or the prompt was too long.";
                }

                // Build result summary
                var sb  = new StringBuilder();
                var ok  = tasks.Count(t => t.Status == "completed");
                var bad = tasks.Count - ok;
                sb.AppendLine($"Results: {ok}/{tasks.Count} completed" +
                              (bad > 0 ? $", {bad} failed/timeout" : "") + ".");
                sb.AppendLine();

                foreach (var t in tasks)
                {
                    var dur  = ((t.FinishedAt ?? DateTime.Now) - t.StartedAt).TotalSeconds;
                    var icon = t.Status switch { "completed" => "✅", "failed" => "❌", "timeout" => "⏱", _ => "❔" };
                    sb.AppendLine($"{icon}  [{t.Id}]  {t.AgentName}  -  {t.Status.ToUpper()}  ({dur:F1}s)");
                    if (t.Status == "completed")
                        sb.AppendLine($"     Read output: bridge_read_file(\"{t.OutputFile}\")");
                    if (t.Error is not null)
                        sb.AppendLine($"     Error: {t.Error}");
                    sb.AppendLine();
                }

                if (ok == tasks.Count)
                    sb.AppendLine("All tasks completed successfully. Use bridge_read_file on each output path.");
                else
                    sb.AppendLine("Some tasks failed. You can retry failed agents with bridge_run_agent_task.");

                return sb.ToString();
            }
        });

        // ── Utility tools ─────────────────────────────────────────────────
        AddTool(new McpTool
        {
            Name        = "bridge_get_datetime",
            Description = "Returns the current local date and time. Useful for timestamping files, " +
                          "log entries, scheduling, and any task that needs to know what time it is.",
            Provider    = "Bridge",
            InputSchemaOverride = """{ "type": "object", "properties": {} }""",
            ExecuteAsync = (_, _) =>
            {
                var now = DateTime.Now;
                return System.Threading.Tasks.Task.FromResult(
                    $"datetime:{now:yyyy-MM-dd HH:mm:ss}\n" +
                    $"date:{now:yyyy-MM-dd}\n" +
                    $"time:{now:HH:mm:ss}\n" +
                    $"day_of_week:{now:dddd}\n" +
                    $"unix_timestamp:{((DateTimeOffset)now).ToUnixTimeSeconds()}");
            }
        });

        AddTool(new McpTool
        {
            Name        = "bridge_web_fetch",
            Description = "Fetches a web page or URL and returns its readable text content. " +
                          "HTML is automatically stripped to plain text. Maximum 200 KB returned. " +
                          "Only http:// and https:// URLs are supported. Timeout is 20 seconds. " +
                          "Use this for research, reading documentation, or fetching data from APIs.",
            Provider    = "Bridge",
            InputSchemaOverride = """
                {
                  "type": "object",
                  "properties": {
                    "url": { "type": "string", "description": "The http:// or https:// URL to fetch" }
                  },
                  "required": ["url"]
                }
                """,
            ExecuteAsync = async (args, ct) =>
                await BridgeWebFetchAsync(args["url"]?.GetValue<string>() ?? "", ct)
        });

        // ── World lore tools ───────────────────────────────────────────────
        // All world_* tools require a project to be open (BridgeProjectFolder).

        AddTool(new McpTool
        {
            Name        = "world_get_summary",
            Description = "Returns a complete overview of the world-building data in the current project: " +
                          "all entity counts by type (Characters, Factions, Locations, Lore), " +
                          "names and key tags for every entry, and a list of all boards. " +
                          "Call this first to understand the scope of the world before querying details.",
            Provider    = "Bridge",
            InputSchemaOverride = """{ "type": "object", "properties": {} }""",
            ExecuteAsync = (_, _) =>
            {
                var folder = BridgeProjectFolder;
                if (folder is null)
                    return Task.FromResult("No project loaded. Call bridge_open_project first.");

                var sb = new StringBuilder();
                sb.AppendLine($"# World Summary — {BridgeProject?.ProjectName ?? System.IO.Path.GetFileName(folder)}");
                sb.AppendLine();

                foreach (var entityType in new[] { "Character", "Faction", "Location", "Lore" })
                {
                    var entities = WorldEntityService.List(folder, entityType);
                    sb.AppendLine($"## {entityType}s ({entities.Count})");
                    if (entities.Count == 0)
                    {
                        sb.AppendLine("  (none)");
                    }
                    else
                    {
                        foreach (var e in entities)
                        {
                            var tags = new List<string>();
                            if (e.Fields.TryGetValue("Role", out var role) && !string.IsNullOrWhiteSpace(role))
                                tags.Add(role);
                            if (e.Fields.TryGetValue("Type", out var etype) && !string.IsNullOrWhiteSpace(etype))
                                tags.Add(etype);
                            if (e.Fields.TryGetValue("Arc", out var arc) && !string.IsNullOrWhiteSpace(arc))
                                tags.Add($"Arc: {arc}");
                            if (e.Fields.TryGetValue("CommonKnowledge", out var ck) && ck == "true")
                                tags.Add("Common Knowledge");
                            if (e.Fields.TryGetValue("HistoricalKnowledge", out var hk) && hk == "true")
                                tags.Add("Historical");
                            var tagStr = tags.Count > 0 ? $"  [{string.Join(", ", tags)}]" : "";
                            sb.AppendLine($"  • {e.Name}{tagStr}");
                        }
                    }
                    sb.AppendLine();
                }

                var boards = WorldBoardRegistryService.Load(folder);
                sb.AppendLine($"## Boards ({boards.Count})");
                if (boards.Count == 0)
                    sb.AppendLine("  (none)");
                else
                    foreach (var b in boards)
                        sb.AppendLine($"  • {b.Symbol} {b.Name}  [{string.Join(", ", b.EntityTypes)}]");

                return Task.FromResult(sb.ToString());
            }
        });

        AddTool(new McpTool
        {
            Name        = "world_get_entities",
            Description = "Returns detailed information about world entities filtered by type and/or arc. " +
                          "entity_type: one of Character, Faction, Location, Lore (required). " +
                          "arc: optional — filter to only entities in this story arc. " +
                          "search: optional — filter by name substring (case-insensitive). " +
                          "Each result includes all schema fields, notes, and resolved relationship names.",
            Provider    = "Bridge",
            InputSchemaOverride = """
                {
                  "type": "object",
                  "properties": {
                    "entity_type": {
                      "type": "string",
                      "description": "Character, Faction, Location, or Lore",
                      "enum": ["Character", "Faction", "Location", "Lore"]
                    },
                    "arc": {
                      "type": "string",
                      "description": "Optional: filter to this story arc name"
                    },
                    "search": {
                      "type": "string",
                      "description": "Optional: filter by name substring (case-insensitive)"
                    }
                  },
                  "required": ["entity_type"]
                }
                """,
            ExecuteAsync = (args, _) =>
            {
                var folder = BridgeProjectFolder;
                if (folder is null)
                    return Task.FromResult("No project loaded. Call bridge_open_project first.");

                var entityType = args["entity_type"]?.GetValue<string>() ?? "";
                var arcFilter  = args["arc"]?.GetValue<string>() ?? "";
                var search     = args["search"]?.GetValue<string>() ?? "";

                if (!WorldEntitySchemas.All.ContainsKey(entityType))
                    return Task.FromResult($"Unknown entity type '{entityType}'. Valid types: Character, Faction, Location, Lore.");

                var entities = WorldEntityService.List(folder, entityType);

                if (!string.IsNullOrWhiteSpace(arcFilter))
                    entities = entities.Where(e =>
                        e.Fields.TryGetValue("Arc", out var a) &&
                        a.Contains(arcFilter, StringComparison.OrdinalIgnoreCase)).ToList();

                if (!string.IsNullOrWhiteSpace(search))
                    entities = entities.Where(e =>
                        e.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();

                if (entities.Count == 0)
                    return Task.FromResult($"No {entityType} entities found matching the criteria.");

                var allFactions   = WorldEntityService.List(folder, "Faction");
                var allCharacters = WorldEntityService.List(folder, "Character");
                var idToName = allFactions.Concat(allCharacters)
                    .ToDictionary(e => e.Id, e => e.Name, StringComparer.OrdinalIgnoreCase);

                var sb = new StringBuilder();
                sb.AppendLine($"# {entityType}s ({entities.Count} found)");
                sb.AppendLine();

                foreach (var e in entities)
                {
                    sb.AppendLine($"## {e.Name}");
                    foreach (var (field, _) in WorldEntitySchemas.For(entityType))
                    {
                        if (e.Fields.TryGetValue(field, out var val) && !string.IsNullOrWhiteSpace(val))
                            sb.AppendLine($"  **{field}:** {val}");
                    }
                    if (entityType == "Lore")
                    {
                        if (e.Fields.TryGetValue("CommonKnowledge", out var ck) && ck == "true")
                            sb.AppendLine("  **Common Knowledge:** Yes");
                        if (e.Fields.TryGetValue("HistoricalKnowledge", out var hk) && hk == "true")
                            sb.AppendLine("  **Historical Knowledge:** Yes");
                    }
                    if (entityType == "Faction" && !string.IsNullOrWhiteSpace(e.FactionColor))
                        sb.AppendLine($"  **Color:** {e.FactionColor}");
                    if (e.MemberIds.Count > 0)
                    {
                        var memberNames = e.MemberIds.Select(id => idToName.TryGetValue(id, out var n) ? n : id);
                        var label = entityType == "Lore" ? "Known by" : "Members";
                        sb.AppendLine($"  **{label}:** {string.Join(", ", memberNames)}");
                    }
                    if (e.FactionIds.Count > 0)
                    {
                        var factionNames = e.FactionIds.Select(id => idToName.TryGetValue(id, out var n) ? n : id);
                        sb.AppendLine($"  **Factions:** {string.Join(", ", factionNames)}");
                    }
                    if (!string.IsNullOrWhiteSpace(e.Notes))
                        sb.AppendLine($"  **Notes:** {e.Notes}");
                    sb.AppendLine();
                }

                return Task.FromResult(sb.ToString());
            }
        });

        AddTool(new McpTool
        {
            Name        = "world_get_entity",
            Description = "Returns full details for a single world entity by exact name. " +
                          "entity_type: one of Character, Faction, Location, Lore. " +
                          "name: the entity name (case-insensitive match). " +
                          "Returns all fields, notes, and resolved relationship names.",
            Provider    = "Bridge",
            InputSchemaOverride = """
                {
                  "type": "object",
                  "properties": {
                    "entity_type": {
                      "type": "string",
                      "description": "Character, Faction, Location, or Lore"
                    },
                    "name": {
                      "type": "string",
                      "description": "The entity name (case-insensitive)"
                    }
                  },
                  "required": ["entity_type", "name"]
                }
                """,
            ExecuteAsync = (args, _) =>
            {
                var folder = BridgeProjectFolder;
                if (folder is null)
                    return Task.FromResult("No project loaded. Call bridge_open_project first.");

                var entityType = args["entity_type"]?.GetValue<string>() ?? "";
                var name       = args["name"]?.GetValue<string>() ?? "";

                if (!WorldEntitySchemas.All.ContainsKey(entityType))
                    return Task.FromResult($"Unknown entity type '{entityType}'. Valid types: Character, Faction, Location, Lore.");

                var entities = WorldEntityService.List(folder, entityType);
                var entity   = entities.FirstOrDefault(e =>
                    string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

                if (entity is null)
                    return Task.FromResult($"No {entityType} named '{name}' found.");

                var allFactions   = WorldEntityService.List(folder, "Faction");
                var allCharacters = WorldEntityService.List(folder, "Character");
                var idToName = allFactions.Concat(allCharacters)
                    .ToDictionary(e => e.Id, e => e.Name, StringComparer.OrdinalIgnoreCase);

                var sb = new StringBuilder();
                sb.AppendLine($"# {entity.EntityType}: {entity.Name}");
                sb.AppendLine($"  ID:      {entity.Id}");
                sb.AppendLine($"  Created: {entity.CreatedAt:yyyy-MM-dd}");
                sb.AppendLine($"  Updated: {entity.UpdatedAt:yyyy-MM-dd}");
                sb.AppendLine();
                sb.AppendLine("## Fields");
                foreach (var (field, _) in WorldEntitySchemas.For(entityType))
                {
                    if (entity.Fields.TryGetValue(field, out var val) && !string.IsNullOrWhiteSpace(val))
                        sb.AppendLine($"  **{field}:** {val}");
                }
                if (entityType == "Lore")
                {
                    if (entity.Fields.TryGetValue("CommonKnowledge", out var ck) && ck == "true")
                        sb.AppendLine("  **Common Knowledge:** Yes");
                    if (entity.Fields.TryGetValue("HistoricalKnowledge", out var hk) && hk == "true")
                        sb.AppendLine("  **Historical Knowledge:** Yes");
                }
                if (entityType == "Faction" && !string.IsNullOrWhiteSpace(entity.FactionColor))
                    sb.AppendLine($"  **Color:** {entity.FactionColor}");

                if (entity.MemberIds.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"## {(entityType == "Lore" ? "Known by" : "Members")}");
                    foreach (var id in entity.MemberIds)
                        sb.AppendLine($"  • {(idToName.TryGetValue(id, out var n) ? n : id)}");
                }
                if (entity.FactionIds.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("## Factions");
                    foreach (var id in entity.FactionIds)
                        sb.AppendLine($"  • {(idToName.TryGetValue(id, out var n) ? n : id)}");
                }
                if (!string.IsNullOrWhiteSpace(entity.Notes))
                {
                    sb.AppendLine();
                    sb.AppendLine("## Notes");
                    sb.AppendLine(entity.Notes);
                }

                return Task.FromResult(sb.ToString());
            }
        });

        AddTool(new McpTool
        {
            Name        = "world_get_boards",
            Description = "Lists all world boards in the current project with their names, symbols, " +
                          "entity types displayed, card/relation/frame counts, and last updated dates. " +
                          "Use this to understand how the world is visually organised across boards.",
            Provider    = "Bridge",
            InputSchemaOverride = """{ "type": "object", "properties": {} }""",
            ExecuteAsync = (_, _) =>
            {
                var folder = BridgeProjectFolder;
                if (folder is null)
                    return Task.FromResult("No project loaded. Call bridge_open_project first.");

                var boards = WorldBoardRegistryService.Load(folder);
                if (boards.Count == 0)
                    return Task.FromResult("No boards found in this project.");

                var sb = new StringBuilder();
                sb.AppendLine($"# World Boards ({boards.Count})");
                sb.AppendLine();

                foreach (var b in boards.OrderBy(b => b.Name))
                {
                    sb.AppendLine($"## {b.Symbol} {b.Name}");
                    sb.AppendLine($"  Entity types: {string.Join(", ", b.EntityTypes)}");
                    sb.AppendLine($"  Created:      {b.CreatedAt:yyyy-MM-dd}");
                    sb.AppendLine($"  Updated:      {b.UpdatedAt:yyyy-MM-dd}");
                    var boardData = EntityBoardService.Load(folder, b.Id);
                    if (boardData.Positions.Count > 0)
                        sb.AppendLine($"  Cards placed: {boardData.Positions.Count}");
                    if (boardData.Relations.Count > 0)
                        sb.AppendLine($"  Relations:    {boardData.Relations.Count}");
                    if (boardData.Frames.Count > 0)
                        sb.AppendLine($"  Frames:       {boardData.Frames.Count}");
                    if (boardData.TextBoxes.Count > 0)
                        sb.AppendLine($"  Text boxes:   {boardData.TextBoxes.Count}");
                    sb.AppendLine();
                }

                return Task.FromResult(sb.ToString());
            }
        });

        // ── chat_get_history - read active chat log ───────────────────────
        AddTool(new McpTool
        {
            Name        = "chat_get_history",
            Description = "Returns recent chat messages from the active ClaudetRelay chat. " +
                          "General chat (no project open) is always accessible. " +
                          "For project chats, the project must have MCP Client enabled — " +
                          "open the project in ClaudetRelay and add an MCP Client via the + participant button. " +
                          "Pass count to limit results (default 50, max 200). " +
                          "Pass project_path to read a specific project's chat instead of the active one.",
            Provider    = "Bridge",
            InputSchemaOverride = """
                {
                  "type": "object",
                  "properties": {
                    "count":        { "type": "integer", "description": "Number of recent messages to return (1–200, default 50)" },
                    "project_path": { "type": "string",  "description": "Optional: absolute path to a specific project folder" }
                  }
                }
                """,
            ExecuteAsync = (args, _) =>
            {
                var count       = Math.Max(1, Math.Min(200, args["count"]?.GetValue<int>() ?? 50));
                var projectPath = args["project_path"]?.GetValue<string>()?.Trim();

                List<ChatLogEntry> entries;
                string context;

                if (!string.IsNullOrEmpty(projectPath))
                {
                    var proj = Services.ProjectService.LoadProject(projectPath);
                    if (proj is null)
                        return Task.FromResult($"Error: no ClaudetRelay project at '{projectPath}'.");
                    if (!proj.McpChatEnabled)
                        return Task.FromResult(
                            $"MCP chat access is not enabled for project '{proj.ProjectName}'. " +
                            "Open it in ClaudetRelay and add an MCP Client via the + participant button.");
                    entries = Services.ProjectService.LoadChatLog(projectPath);
                    context = $"Project: {proj.ProjectName}";
                }
                else if (_currentProjectFolder is not null && _projectSettings is not null)
                {
                    if (!_projectSettings.McpChatEnabled)
                        return Task.FromResult(
                            $"MCP chat access is not enabled for '{_projectSettings.ProjectName}'. " +
                            "Add an MCP Client via the + participant button to enable it.");
                    entries = Services.ProjectService.LoadChatLog(_currentProjectFolder);
                    context = $"Project: {_projectSettings.ProjectName}";
                }
                else
                {
                    entries = GeneralChatLogService.LoadRecentLog();
                    context = "General chat";
                }

                if (entries.Count == 0)
                    return Task.FromResult($"No chat history found. ({context})");

                var recent = entries.Count > count ? entries[^count..] : entries;
                var sb = new StringBuilder();
                sb.AppendLine($"# Chat History  ({context})");
                sb.AppendLine($"Showing {recent.Count} of {entries.Count} total messages");
                sb.AppendLine();

                foreach (var e in recent)
                {
                    if (e.SenderType == "System")
                        sb.AppendLine($"[{e.Timestamp:HH:mm}] ─── {e.Message} ───");
                    else
                        sb.AppendLine($"[{e.Timestamp:HH:mm}] **{e.DisplayName}**: {e.Message}");
                    sb.AppendLine();
                }

                return Task.FromResult(sb.ToString());
            }
        });

        // ── chat_post_message - inject a bubble into the active chat ──────
        AddTool(new McpTool
        {
            Name        = "chat_post_message",
            Description = "Posts a message into the active ClaudetRelay chat as a named participant. " +
                          "The message appears as a chat bubble, is saved to the chat log, " +
                          "and will be visible to all AI participants on their next turn. " +
                          "For project chats, MCP Client must be enabled (add via + participant button). " +
                          "Use participant_name to set the sender label (e.g. 'Claude Code', 'External Review'). " +
                          "Set trigger_responses to false when the message is directed at the human user " +
                          "(e.g. a question, clarification, or summary) and you do NOT want the other AI " +
                          "participants to automatically reply — default is true (responses are triggered).",
            Provider    = "Bridge",
            InputSchemaOverride = """
                {
                  "type": "object",
                  "properties": {
                    "participant_name":   { "type": "string",  "description": "Display name shown on the chat bubble" },
                    "message":            { "type": "string",  "description": "The message to post" },
                    "trigger_responses":  { "type": "boolean", "description": "If false, other AI participants will NOT be triggered to respond. Use false for messages directed at the human user. Default: true." }
                  },
                  "required": ["participant_name", "message"]
                }
                """,
            ExecuteAsync = async (args, _) =>
            {
                var name            = args["participant_name"]?.GetValue<string>()?.Trim() ?? "MCP Client";
                var message         = args["message"]?.GetValue<string>()?.Trim() ?? "";
                // Accept both JSON boolean true/false and string "true"/"false"
                var triggerNode = args["trigger_responses"];
                var triggerResponses = triggerNode is null ? true
                    : triggerNode.GetValueKind() == System.Text.Json.JsonValueKind.String
                        ? !string.Equals(triggerNode.GetValue<string>(), "false", StringComparison.OrdinalIgnoreCase)
                        : triggerNode.GetValue<bool>();

                if (string.IsNullOrEmpty(message))
                    return "Error: message cannot be empty.";

                // Gate: project chat requires McpChatEnabled
                if (_currentProjectFolder is not null && _projectSettings is not null && !_projectSettings.McpChatEnabled)
                    return $"MCP chat access is not enabled for '{_projectSettings.ProjectName}'. " +
                           "Add an MCP Client via the + participant button to enable it.";

                var avatarLabel = name.Length >= 2 ? name[..2].ToUpper() : name.ToUpper();

                var entry = new ChatLogEntry
                {
                    Timestamp   = DateTime.Now,
                    SenderType  = "AI",
                    Provider    = "MCP Client",
                    ModelName   = "external",
                    DisplayName = name,
                    AvatarLabel = avatarLabel,
                    AccentKey   = "AccentPrimaryBrush",
                    BubbleKey   = "SecondaryBubbleBrush",
                    IsUser      = false,
                    Message     = message
                };

                await Dispatcher.InvokeAsync(() =>
                {
                    AddMessage(name, avatarLabel, "AccentPrimaryBrush", "SecondaryBubbleBrush", message, isUser: false);
                    ChatScrollViewer.ScrollToBottom();
                    AppendToProjectLog(entry);
                    AppendToGeneralLog(entry);
                    _sharedHistory.Add(new CloudAIMessage("assistant", message, name));
                });

                // Only trigger the AI response round when the caller wants other participants to react.
                // Set trigger_responses=false for messages directed at the human user (questions,
                // clarifications, summaries) to avoid other AIs piling on uninvited.
                if (triggerResponses)
                    Dispatcher.InvokeAsync(async () => await TriggerAiResponsesAsync());

                var preview = message.Length > 80 ? message[..80] + "…" : message;
                return $"✓ Posted as '{name}': \"{preview}\"{(triggerResponses ? "" : " (no AI responses triggered)")}";
            }
        });

        // ── chat_wait_for_round - block until the current AI response round finishes ──
        AddTool(new McpTool
        {
            Name        = "chat_wait_for_round",
            Description = "Waits until all AI participants have finished responding in the current round, " +
                          "then returns the new messages. Call this after chat_post_message " +
                          "(with trigger_responses=true) to know when it is your turn to reply. " +
                          "Returns immediately if no round is active. " +
                          "Optional timeout_seconds (default 120). " +
                          "Returns the new messages posted since you called this tool.",
            Provider    = "Bridge",
            InputSchemaOverride = """
                {
                  "type": "object",
                  "properties": {
                    "timeout_seconds": { "type": "integer", "description": "Maximum seconds to wait. Default 120." }
                  }
                }
                """,
            ExecuteAsync = async (args, ct) =>
            {
                var timeout = args?["timeout_seconds"]?.GetValue<int>() ?? 120;

                // Snapshot history count before waiting so we can return only the new messages
                int historyBefore = await Dispatcher.InvokeAsync(() => _sharedHistory.Count);

                // Small initial delay to let a just-triggered round actually start
                await Task.Delay(600, ct);

                // Poll until _streamCts is null (round finished) or timeout
                var deadline = DateTime.UtcNow.AddSeconds(timeout);
                while (!ct.IsCancellationRequested)
                {
                    bool roundActive = await Dispatcher.InvokeAsync(() => _streamCts is not null);
                    if (!roundActive) break;
                    if (DateTime.UtcNow >= deadline)
                        return $"⏱ Timeout after {timeout}s — AI participants may still be responding. " +
                               "Call chat_get_history to see what has been posted so far.";
                    await Task.Delay(400, ct);
                }

                if (ct.IsCancellationRequested)
                    return "Cancelled.";

                // Collect new messages posted during the round
                var newMessages = await Dispatcher.InvokeAsync(() =>
                    _sharedHistory.Skip(historyBefore).ToList());

                if (newMessages.Count == 0)
                    return "✓ Round complete — no new messages were posted.";

                var sb = new StringBuilder();
                sb.AppendLine($"✓ Round complete — {newMessages.Count} new message(s):\n");
                foreach (var m in newMessages)
                {
                    var sender = m.Sender ?? (m.Role == "user" ? "User" : "AI");
                    var preview = m.Content?.Length > 300 ? m.Content[..300] + "…" : m.Content ?? "";
                    sb.AppendLine($"**{sender}**: {preview}\n");
                }
                return sb.ToString().TrimEnd();
            }
        });

        return tools;
    }

    // ── Client Setup card ─────────────────────────────────────────────────

    private UIElement BuildClientSetupCard(AppSettings cfg)
    {
        var port = _mcpServer?.Port ?? cfg.McpPort;

        // Snippets
        var desktopSnippet = "{\n  \"mcpServers\": {\n    \"claudetrelay\": {\n" +
                            $"      \"url\": \"http://localhost:{port}/sse\"\n" +
                             "    }\n  }\n}";
        var codeSnippet    = $"claude mcp add --transport sse claudetrelay http://localhost:{port}/sse --scope user";

        // Outer card border (collapsed by default, expands on click)
        var card = new Border
        {
            CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 14), Cursor = Cursors.Hand
        };
        card.SetResourceReference(Border.BackgroundProperty, "ControlBgBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");

        var cardStack = new StackPanel();
        card.Child   = cardStack;

        // ── Header row (always visible) ────────────────────────────────────
        var header = new Grid { Margin = new Thickness(12, 10, 12, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cardStack.Children.Add(header);

        var headerIcon = new TextBlock
        {
            Text = "⚙", FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        headerIcon.SetResourceReference(TextBlock.ForegroundProperty, "AccentPrimaryBrush");
        Grid.SetColumn(headerIcon, 0);
        header.Children.Add(headerIcon);

        var headerText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(headerText, 1);
        header.Children.Add(headerText);

        var headerTitle = new TextBlock
        {
            Text = "Client Setup", FontSize = 12, FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI")
        };
        headerTitle.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        headerText.Children.Add(headerTitle);

        var headerSub = new TextBlock
        {
            Text = $"Config snippets for Claude Desktop & Claude Code  ·  port {port}",
            FontSize = 11, FontFamily = new FontFamily("Segoe UI")
        };
        headerSub.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        headerText.Children.Add(headerSub);

        var chevron = new TextBlock
        {
            Text = "▸", FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        Grid.SetColumn(chevron, 2);
        header.Children.Add(chevron);

        // ── Expandable body ────────────────────────────────────────────────
        var expandBody = new StackPanel
        {
            Margin     = new Thickness(12, 0, 12, 12),
            Visibility = Visibility.Collapsed
        };
        cardStack.Children.Add(expandBody);

        // Helper: one snippet block with label + copy button
        void AddSnippetBlock(string label, string emoji, string text)
        {
            var lbl = new TextBlock
            {
                Text = $"{emoji}  {label}", FontSize = 11, FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 8, 0, 4)
            };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
            expandBody.Children.Add(lbl);

            var box = new TextBox
            {
                Text = text, IsReadOnly = true, FontFamily = new FontFamily("Consolas,Courier New"),
                FontSize = 11, Padding = new Thickness(10, 8, 10, 8),
                TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(1),
                AcceptsReturn = false
            };
            box.SetResourceReference(TextBox.BackgroundProperty, "SidebarBgBrush");
            box.SetResourceReference(TextBox.ForegroundProperty, "ContentTextBrush");
            box.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
            expandBody.Children.Add(box);

            var copyBtn = MakeBridgeSmallBtn("📋 Copy");
            copyBtn.Margin        = new Thickness(0, 5, 0, 0);
            copyBtn.HorizontalAlignment = HorizontalAlignment.Left;
            copyBtn.Click += (_, _) =>
            {
                Clipboard.SetText(text);
                copyBtn.Content = "✅ Copied!";
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                t.Tick += (_, _) => { copyBtn.Content = "📋 Copy"; t.Stop(); };
                t.Start();
            };
            expandBody.Children.Add(copyBtn);
        }

        AddSnippetBlock("Claude Desktop - add to claude_desktop_config.json", "🖥",  desktopSnippet);
        AddSnippetBlock("Claude Code - run once in terminal",                  "💻", codeSnippet);

        // Note
        var note = new TextBlock
        {
            Text = "Start the MCP server first, then (re)start the client to connect.",
            FontSize = 10, FontFamily = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 2)
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        expandBody.Children.Add(note);

        // Toggle on header click only - e.Handled stops bubble-up double-fire
        bool expanded = false;
        header.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled             = true;
            expanded              = !expanded;
            expandBody.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text          = expanded ? "▾" : "▸";
        };

        return card;
    }

    // ── Bridge Settings popup ─────────────────────────────────────────────

    private void ShowMcpBridgeSettingsWindow()   => ShowBridgeSettingsWindowCore("MCP Bridge Settings",  isMcp: true);
    private void ShowControllerBridgeSettingsWindow() => ShowBridgeSettingsWindowCore("Controller Bridge Settings", isMcp: false);

    private void ShowBridgeSettingsWindowCore(string title, bool isMcp)
    {
        var cfg = SettingsService.Load();
        var win = new Window
        {
            Title = title, Width = 720, Height = 680,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this, ShowInTaskbar = false,
            ResizeMode = ResizeMode.CanResize, MinWidth = 560, MinHeight = 480
        };
        ApplyThemeToDialog(win);
        win.SetResourceReference(Window.BackgroundProperty, "ContentBgBrush");

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(20)
        };
        win.Content = scroll;

        var root = new StackPanel();
        scroll.Content = root;

        // ── Tool access section (only for this window's mode) ─────────
        if (isMcp)
        {
            BuildToolListSection(root, cfg,
                icon: "🔌", title: "MCP Server - Tool Access",
                subtitle: "Tools exposed to Claude Desktop, Claude Code and other MCP clients.\n" +
                          "Unchecked tools are hidden from external clients entirely.",
                disabledList: cfg.DisabledMcpServerTools,
                saveDisabled: list => { var s = SettingsService.Load(); s.DisabledMcpServerTools = list; SettingsService.Save(s); });
        }
        else
        {
            BuildToolListSection(root, cfg,
                icon: "🤖", title: "Model Controller - Tool Access",
                subtitle: "Tools available to the built-in controller AI when orchestrating agents.\n" +
                          "Local models may be given more access than cloud connections (zero-trust policy).",
                disabledList: cfg.DisabledControllerTools,
                saveDisabled: list => { var s = SettingsService.Load(); s.DisabledControllerTools = list; SettingsService.Save(s); });
        }

        // ── Limits section (shared) ────────────────────────────────────
        var divider3 = new Rectangle { Height = 1, Margin = new Thickness(0, 16, 0, 16) };
        divider3.SetResourceReference(Rectangle.FillProperty, "ControlBorderBrush");
        root.Children.Add(divider3);

        BuildLimitsSection(root, cfg, isMcp);

        win.ShowDialog();
    }

    private void BuildToolListSection(StackPanel parent, AppSettings cfg,
        string icon, string title, string subtitle,
        List<string> disabledList, Action<List<string>> saveDisabled)
    {
        var hdr = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        parent.Children.Add(hdr);

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        var iconLbl  = new TextBlock { Text = icon, FontSize = 14, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        iconLbl.SetResourceReference(TextBlock.ForegroundProperty, "AccentPrimaryBrush");
        var titleLbl = new TextBlock
        {
            Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"), VerticalAlignment = VerticalAlignment.Center
        };
        titleLbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        titleRow.Children.Add(iconLbl);
        titleRow.Children.Add(titleLbl);
        hdr.Children.Add(titleRow);

        var subLbl = new TextBlock
        {
            Text = subtitle, FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        };
        subLbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        parent.Children.Add(subLbl);

        // Enabled count label
        var totalTools = BridgeToolDescriptions.Count(t => t.Name is not null);
        var countLbl   = new TextBlock
        {
            Text = $"{totalTools - disabledList.Count} of {totalTools} tools enabled",
            FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
            Margin = new Thickness(0, 0, 0, 8)
        };
        countLbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        parent.Children.Add(countLbl);

        // Two-column tool grid
        var twoCol = new Grid();
        twoCol.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        twoCol.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        twoCol.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        parent.Children.Add(twoCol);

        var leftCol  = new StackPanel();
        var rightCol = new StackPanel();
        Grid.SetColumn(leftCol, 0); Grid.SetColumn(rightCol, 2);
        twoCol.Children.Add(leftCol); twoCol.Children.Add(rightCol);

        var leftCats = new HashSet<string> { "🤖  Agents", "📁  Folders", "🔄  Files - Text" };
        StackPanel currentCol = leftCol;

        foreach (var (toolName, toolDesc) in BridgeToolDescriptions)
        {
            if (toolName is null)
            {
                currentCol = leftCats.Contains(toolDesc) ? leftCol : rightCol;
                var catLbl = new TextBlock
                {
                    Text = toolDesc, FontSize = 10, FontWeight = FontWeights.Bold,
                    FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 8, 0, 3)
                };
                catLbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
                currentCol.Children.Add(catLbl);
                continue;
            }

            var captured     = toolName;
            var capturedDesc = toolDesc;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            currentCol.Children.Add(row);

            var cb = new CheckBox
            {
                IsChecked = !disabledList.Contains(captured),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            };
            row.Children.Add(cb);

            var nameText = new TextBlock
            {
                Text = captured, FontSize = 11,
                FontFamily = new FontFamily("Consolas,Courier New"),
                VerticalAlignment = VerticalAlignment.Center, ToolTip = capturedDesc
            };
            nameText.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
            row.Children.Add(nameText);

            var helpBtn = new Button
            {
                Content = "?", FontSize = 10, Width = 17, Height = 17,
                Padding = new Thickness(0), BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 0, 0), ToolTip = capturedDesc
            };
            helpBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
            helpBtn.SetResourceReference(Button.ForegroundProperty, "SidebarDimBrush");
            helpBtn.SetResourceReference(Button.BorderBrushProperty, "ControlBorderBrush");
            helpBtn.Click += (_, _) => MessageBox.Show(capturedDesc, captured,
                MessageBoxButton.OK, MessageBoxImage.Information);
            row.Children.Add(helpBtn);

            cb.Checked += (_, _) =>
            {
                disabledList.RemoveAll(t => string.Equals(t, captured, StringComparison.OrdinalIgnoreCase));
                saveDisabled(disabledList);
                countLbl.Text = $"{totalTools - disabledList.Count} of {totalTools} tools enabled";
            };
            cb.Unchecked += (_, _) =>
            {
                if (!disabledList.Contains(captured, StringComparer.OrdinalIgnoreCase))
                    disabledList.Add(captured);
                saveDisabled(disabledList);
                countLbl.Text = $"{totalTools - disabledList.Count} of {totalTools} tools enabled";
            };
        }
    }

    private void BuildLimitsSection(StackPanel parent, AppSettings cfg, bool isMcp)
    {
        var title = new TextBlock
        {
            Text = "FILE SIZE LIMITS", FontSize = 10, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 0, 0, 4)
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        parent.Children.Add(title);

        var noteText = isMcp
            ? "MCP Server always connects to cloud clients. Keep limits low to control API token costs."
            : "Local controller: raise limits freely (no token costs).\n" +
              "Cloud controller: keep limits low to control API costs.\n" +
              "The active limit is chosen automatically based on whether the controller model is local or cloud.";

        var note = new TextBlock
        {
            Text = noteText, FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        parent.Children.Add(note);

        if (isMcp)
        {
            // MCP Server - single column, cloud only
            void AddMcpRow(string label, string tip, int bytes, Action<int> onChange)
            {
                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                parent.Children.Add(row);

                var lbl = new TextBlock { Text = label, FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
                    VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
                Grid.SetColumn(lbl, 0); row.Children.Add(lbl);

                int orig = bytes;
                var box = new TextBox { Text = (bytes / 1000).ToString(), FontSize = 11,
                    FontFamily = new FontFamily("Segoe UI"),
                    Padding = new Thickness(6, 3, 6, 3), BorderThickness = new Thickness(1), ToolTip = tip };
                box.SetResourceReference(TextBox.BackgroundProperty, "ControlBgBrush");
                box.SetResourceReference(TextBox.ForegroundProperty, "ContentTextBrush");
                box.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
                Grid.SetColumn(box, 1); row.Children.Add(box);

                var unit = new TextBlock { Text = " KB", FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
                unit.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
                Grid.SetColumn(unit, 2); row.Children.Add(unit);

                box.LostFocus += (_, _) =>
                {
                    if (int.TryParse(box.Text.Trim(), out int kb) && kb >= 1) onChange(kb * 1000);
                    else box.Text = (orig / 1000).ToString();
                };
            }

            AddMcpRow("Text (bridge_read_file)",   "Max KB for text file reads via MCP Server.",
                cfg.McpServerMaxTextFileBytes,
                v => { var s = SettingsService.Load(); s.McpServerMaxTextFileBytes   = v; SettingsService.Save(s); });
            AddMcpRow("Binary (bridge_read_file_binary)", "Max KB for binary file reads via MCP Server.",
                cfg.McpServerMaxBinaryFileBytes,
                v => { var s = SettingsService.Load(); s.McpServerMaxBinaryFileBytes = v; SettingsService.Save(s); });
        }
        else
        {
            // Controller - two columns: local + cloud
            var hdrGrid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            parent.Children.Add(hdrGrid);

            void ColHdr(int col, string text)
            {
                var t = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold,
                    FontFamily = new FontFamily("Segoe UI") };
                t.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
                Grid.SetColumn(t, col); hdrGrid.Children.Add(t);
            }
            ColHdr(1, "🖥 Local (KB)"); ColHdr(3, "☁ Cloud (KB)");

            void AddRow(string label, string tip, int localBytes, int cloudBytes,
                Action<int> onLocal, Action<int> onCloud)
            {
                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
                parent.Children.Add(row);

                var lbl = new TextBlock { Text = label, FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
                    VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
                Grid.SetColumn(lbl, 0); row.Children.Add(lbl);

                void MakeBox(int bytes, Action<int> onChange, int colIdx)
                {
                    int orig = bytes;
                    var box = new TextBox { Text = (bytes / 1000).ToString(), FontSize = 11,
                        FontFamily = new FontFamily("Segoe UI"),
                        Padding = new Thickness(6, 3, 6, 3), BorderThickness = new Thickness(1), ToolTip = tip };
                    box.SetResourceReference(TextBox.BackgroundProperty, "ControlBgBrush");
                    box.SetResourceReference(TextBox.ForegroundProperty, "ContentTextBrush");
                    box.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
                    Grid.SetColumn(box, colIdx); row.Children.Add(box);
                    box.LostFocus += (_, _) =>
                    {
                        if (int.TryParse(box.Text.Trim(), out int kb) && kb >= 1) onChange(kb * 1000);
                        else box.Text = (orig / 1000).ToString();
                    };
                }
                MakeBox(localBytes, onLocal, 1);
                MakeBox(cloudBytes, onCloud, 3);
            }

            AddRow("Text (bridge_read_file)", "Max KB for text file reads via Controller.",
                cfg.BridgeLocalMaxTextFileBytes, cfg.BridgeCloudMaxTextFileBytes,
                v => { var s = SettingsService.Load(); s.BridgeLocalMaxTextFileBytes   = v; SettingsService.Save(s); },
                v => { var s = SettingsService.Load(); s.BridgeCloudMaxTextFileBytes   = v; SettingsService.Save(s); });
            AddRow("Binary (bridge_read_file_binary)", "Max KB for binary file reads via Controller.",
                cfg.BridgeLocalMaxBinaryFileBytes, cfg.BridgeCloudMaxBinaryFileBytes,
                v => { var s = SettingsService.Load(); s.BridgeLocalMaxBinaryFileBytes = v; SettingsService.Save(s); },
                v => { var s = SettingsService.Load(); s.BridgeCloudMaxBinaryFileBytes = v; SettingsService.Save(s); });
        }
    }

    // ── Tool Settings card ────────────────────────────────────────────────

    // null Name = category separator row, Description = category label
    private static readonly (string? Name, string Description)[] BridgeToolDescriptions =
    [
        (null,                       "🤖  Agents"),
        ("bridge_list_agents",       "Lists all enabled Bridge agents with their names and models. Agents call this to discover who they can talk to."),
        ("bridge_ask_agent",         "Sends a message to a named agent and returns the response. The core agent-to-agent communication tool."),
        ("bridge_post_to_agents",    "Broadcasts a task to ALL enabled Bridge agents and returns every response. Silent — nothing is posted to chat. Use parallel=true for speed."),

        (null,                       "📁  Folders"),
        ("bridge_list_folders",      "Lists all configured Bridge folders and their read/write permissions. Agents call this to discover which paths are accessible."),
        ("bridge_list_folder",       "Lists the files and subfolders inside a given path. Use to browse folder contents recursively."),
        ("bridge_create_folder",     "Creates a new folder (including any missing parents) inside a write-enabled Bridge folder."),

        (null,                       "🔄  Files - Text"),
        ("bridge_file_exists",       "Checks whether a file or folder exists at a given path. Returns 'exists:file', 'exists:folder', or 'not_found'."),
        ("bridge_read_file",         "Reads the text content of a file (max 200 KB). Suitable for .txt, .md, .json, .csv and similar text files."),
        ("bridge_write_file",        "Writes (or overwrites) a text file inside a write-enabled Bridge folder."),
        ("bridge_append_file",       "Appends text to the end of a file without overwriting. Ideal for logs, journals, and incremental agent output."),
        ("bridge_rename",            "Renames a file or folder in place. Stays in the same directory - only the name changes."),
        ("bridge_move_file",         "Moves a file from one path to another. Both source and destination must be inside write-enabled folders."),
        ("bridge_delete_file",       "Permanently deletes a file inside a write-enabled Bridge folder. Cannot be undone - use with care."),

        (null,                       "🖼  Files - Binary"),
        ("bridge_read_file_binary",  "Reads any file (images, PDFs, ZIPs, etc.) as base64-encoded binary. Returns mime type, size, and base64 content. Maximum 10 MB."),
        ("bridge_write_file_binary", "Writes binary content (from base64) to a file in a write-enabled folder. Use for images, PDFs, and other non-text files."),

        (null,                       "🌐  Utility"),
        ("bridge_get_datetime",      "Returns the current local date and time in multiple formats (datetime, date, time, day of week, unix timestamp). Useful for timestamping files, log entries, and scheduling."),
        ("bridge_web_fetch",         "Fetches a URL and returns the readable text content. HTML is automatically stripped. Maximum 200 KB, 20-second timeout. Only http:// and https:// are allowed. Use for research, documentation, or API data."),

        (null,                       "⚡  Parallel Tasks"),
        ("bridge_get_workspace",     "Returns the configured temp workspace folder path. Use this as the base directory for all parallel agent task output files."),
        ("bridge_run_agent_task",    "Fires an agent task asynchronously - the agent runs in the background and writes its result to output_file. Returns a task_id immediately so multiple agents can run in parallel."),
        ("bridge_list_active_tasks", "Lists all agent tasks with their current status (running / completed / failed / timeout), duration, output file path, and error details. Tasks older than 4 hours are auto-removed."),
        ("bridge_wait_for_tasks",    "Waits for a list of task IDs to finish, polling every 500ms. Returns per-task results with errors. Never hangs - any task exceeding the timeout is marked 'timeout' with a full error explanation (rate limit, connection lost, crash, etc.)."),

        (null,                       "🌍  World"),
        ("world_get_summary",        "Returns an overview of all world entities (Characters, Factions, Locations, Lore) and boards in the current project. Start here to understand the scope of the world."),
        ("world_get_entities",       "Returns detailed information about world entities filtered by type (Character / Faction / Location / Lore), story arc, or name substring. Includes all schema fields, notes, and resolved relationship names."),
        ("world_get_entity",         "Returns full details for a single world entity by name and type, including all fields, notes, member list, and faction associations."),
        ("world_get_boards",         "Lists all world boards with entity types shown, card / relation / frame / text-box counts, and last updated dates."),

        (null,                       "💬  Chat"),
        ("chat_get_history",         "Returns recent chat messages from the active ClaudetRelay chat. General chat is always accessible. Project chats require MCP Client to be enabled (add via + participant button)."),
        ("chat_post_message",        "Posts a message into the active chat as a named participant — appears as a bubble, saved to the log, visible to AI participants on their next turn. Set trigger_responses=false for messages directed at the human user."),
        ("chat_wait_for_round",      "Waits until all AI participants finish responding in the current round, then returns the new messages. Call after chat_post_message to know when it is your turn to reply."),
    ];

    private UIElement BuildToolSettingsCard(AppSettings cfg)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 14)
        };
        card.SetResourceReference(Border.BackgroundProperty, "ControlBgBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");

        var cardStack = new StackPanel();
        card.Child   = cardStack;

        // ── Header ────────────────────────────────────────────────────────
        var header = new Grid { Margin = new Thickness(12, 10, 12, 10), Cursor = Cursors.Hand };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cardStack.Children.Add(header);

        var hIcon = new TextBlock
        {
            Text = "🛠", FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        hIcon.SetResourceReference(TextBlock.ForegroundProperty, "AccentPrimaryBrush");
        Grid.SetColumn(hIcon, 0);
        header.Children.Add(hIcon);

        var hText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(hText, 1);
        header.Children.Add(hText);

        var hTitle = new TextBlock
        {
            Text = "Tool Settings", FontSize = 12, FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI")
        };
        hTitle.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        hText.Children.Add(hTitle);

        var totalTools   = BridgeToolDescriptions.Count(t => t.Name is not null);
        var enabledCount = totalTools - cfg.DisabledBridgeTools.Count;
        var hSub = new TextBlock
        {
            Text = $"{enabledCount} of {totalTools} tools enabled",
            FontSize = 11, FontFamily = new FontFamily("Segoe UI")
        };
        hSub.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        hText.Children.Add(hSub);

        var chevron = new TextBlock
        {
            Text = "▸", FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        Grid.SetColumn(chevron, 2);
        header.Children.Add(chevron);

        // ── Expand body ───────────────────────────────────────────────────
        var expandBody = new StackPanel
        {
            Margin = new Thickness(12, 0, 12, 12), Visibility = Visibility.Collapsed
        };
        cardStack.Children.Add(expandBody);

        var note = new TextBlock
        {
            Text = "Unchecked tools will not be exposed when the MCP server starts. " +
                   "Restart the server after changing these settings.",
            FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        expandBody.Children.Add(note);

        // Two-column layout: left = Agents/Folders/Files-Text, right = Files-Binary/Utility/Parallel
        var twoCol = new Grid();
        twoCol.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        twoCol.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        twoCol.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        expandBody.Children.Add(twoCol);

        var leftCol  = new StackPanel();
        var rightCol = new StackPanel();
        Grid.SetColumn(leftCol,  0);
        Grid.SetColumn(rightCol, 2);
        twoCol.Children.Add(leftCol);
        twoCol.Children.Add(rightCol);

        // Categories assigned to columns:
        // Left:  🤖 Agents, 📁 Folders, 🔄 Files-Text
        // Right: 🖼 Files-Binary, 🌐 Utility, ⚡ Parallel Tasks
        var leftCategories  = new HashSet<string> { "🤖  Agents", "📁  Folders", "🔄  Files - Text" };
        StackPanel currentCol = leftCol;

        foreach (var (toolName, toolDesc) in BridgeToolDescriptions)
        {
            // Category header - switch columns after left-side categories
            if (toolName is null)
            {
                currentCol = leftCategories.Contains(toolDesc) ? leftCol : rightCol;

                var catLbl = new TextBlock
                {
                    Text = toolDesc, FontSize = 10, FontWeight = FontWeights.Bold,
                    FontFamily = new FontFamily("Segoe UI"),
                    Margin = new Thickness(0, 10, 0, 4)
                };
                catLbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
                currentCol.Children.Add(catLbl);
                continue;
            }

            var captured = toolName;
            var capturedDesc = toolDesc;

            // Horizontal row: [☑] name [?]  - ? sits right next to name, no stretching
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 2, 0, 2)
            };
            currentCol.Children.Add(row);

            var cb = new CheckBox
            {
                IsChecked = !cfg.DisabledBridgeTools.Contains(captured),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            row.Children.Add(cb);

            var nameText = new TextBlock
            {
                Text = captured, FontSize = 11,
                FontFamily = new FontFamily("Consolas,Courier New"),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = capturedDesc
            };
            nameText.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
            row.Children.Add(nameText);

            var helpBtn = new Button
            {
                Content = "?", FontSize = 10, Width = 17, Height = 17,
                Padding = new Thickness(0), BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 0, 0), ToolTip = capturedDesc
            };
            helpBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
            helpBtn.SetResourceReference(Button.ForegroundProperty, "SidebarDimBrush");
            helpBtn.SetResourceReference(Button.BorderBrushProperty, "ControlBorderBrush");
            helpBtn.Click += (_, _) => MessageBox.Show(capturedDesc, captured,
                MessageBoxButton.OK, MessageBoxImage.Information);
            row.Children.Add(helpBtn);

            cb.Checked   += (_, _) => SaveToolEnabled(captured, enabled: true,  hSub);
            cb.Unchecked += (_, _) => SaveToolEnabled(captured, enabled: false, hSub);
        }

        // ── Limits section ────────────────────────────────────────────────
        var limitSep = new Rectangle { Height = 1, Margin = new Thickness(0, 14, 0, 10) };
        limitSep.SetResourceReference(Rectangle.FillProperty, "ControlBorderBrush");
        expandBody.Children.Add(limitSep);

        var limitHeader = new TextBlock
        {
            Text = "FILE SIZE LIMITS", FontSize = 10, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 0, 0, 4)
        };
        limitHeader.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        expandBody.Children.Add(limitHeader);

        var limitNote = new TextBlock
        {
            Text = "Raise limits for local models - lower them for cloud models to save tokens. " +
                   "Changes take effect on the next tool call (no server restart needed).",
            FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        };
        limitNote.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        expandBody.Children.Add(limitNote);

        // Column headers
        var limColHdr = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        limColHdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        limColHdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        limColHdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        limColHdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        expandBody.Children.Add(limColHdr);

        void AddColHdr(int col, string text)
        {
            var t = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI") };
            t.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
            Grid.SetColumn(t, col); limColHdr.Children.Add(t);
        }
        AddColHdr(1, "🖥 Local (KB)");
        AddColHdr(3, "☁ Cloud (KB)");

        void AddLimitRow(string label, string tooltip,
            int localBytes, int cloudBytes,
            Action<int> onLocalChanged, Action<int> onCloudChanged)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            expandBody.Children.Add(row);

            var lbl = new TextBlock { Text = label, FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center, ToolTip = tooltip };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
            Grid.SetColumn(lbl, 0); row.Children.Add(lbl);

            TextBox MakeBox(int bytes, Action<int> onChange, int colIdx)
            {
                var box = new TextBox { Text = (bytes / 1000).ToString(),
                    FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
                    Padding = new Thickness(6, 3, 6, 3), BorderThickness = new Thickness(1),
                    ToolTip = tooltip };
                box.SetResourceReference(TextBox.BackgroundProperty, "ControlBgBrush");
                box.SetResourceReference(TextBox.ForegroundProperty, "ContentTextBrush");
                box.SetResourceReference(TextBox.BorderBrushProperty, "ControlBorderBrush");
                Grid.SetColumn(box, colIdx); row.Children.Add(box);
                int orig = bytes;
                box.LostFocus += (_, _) =>
                {
                    if (int.TryParse(box.Text.Trim(), out int kb) && kb >= 1) onChange(kb * 1000);
                    else box.Text = (orig / 1000).ToString();
                };
                return box;
            }
            MakeBox(localBytes, onLocalChanged, 1);
            MakeBox(cloudBytes, onCloudChanged, 3);
        }

        var cfgNow = SettingsService.Load();
        AddLimitRow("Text (bridge_read_file)",
            "Max size for text file reads. Local default 1 000 KB, cloud default 200 KB.",
            cfgNow.BridgeLocalMaxTextFileBytes, cfgNow.BridgeCloudMaxTextFileBytes,
            val => { var s = SettingsService.Load(); s.BridgeLocalMaxTextFileBytes = val; SettingsService.Save(s); },
            val => { var s = SettingsService.Load(); s.BridgeCloudMaxTextFileBytes = val; SettingsService.Save(s); });

        AddLimitRow("Binary (bridge_read_file_binary)",
            "Max size for binary file reads. Local default 50 000 KB, cloud default 10 000 KB.",
            cfgNow.BridgeLocalMaxBinaryFileBytes, cfgNow.BridgeCloudMaxBinaryFileBytes,
            val => { var s = SettingsService.Load(); s.BridgeLocalMaxBinaryFileBytes = val; SettingsService.Save(s); },
            val => { var s = SettingsService.Load(); s.BridgeCloudMaxBinaryFileBytes = val; SettingsService.Save(s); });

        bool expanded = false;
        header.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled             = true;
            expanded              = !expanded;
            expandBody.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text          = expanded ? "▾" : "▸";
        };

        return card;
    }

    private static void SaveToolEnabled(string toolName, bool enabled, TextBlock? subtitleLabel)
    {
        var s = SettingsService.Load();
        if (enabled)
            s.DisabledBridgeTools.RemoveAll(t => string.Equals(t, toolName, StringComparison.OrdinalIgnoreCase));
        else if (!s.DisabledBridgeTools.Contains(toolName, StringComparer.OrdinalIgnoreCase))
            s.DisabledBridgeTools.Add(toolName);
        SettingsService.Save(s);

        if (subtitleLabel is not null)
        {
            var total    = BridgeToolDescriptions.Count(t => t.Name is not null);
            var enabled2 = total - s.DisabledBridgeTools.Count;
            subtitleLabel.Text = $"{enabled2} of {total} tools enabled";
        }
    }

    // ── Tool executor (shared by MCP server + Model Controller) ──────────

    /// <summary>
    /// Executes a Bridge tool by name, given a JsonNode of arguments.
    /// Shared between the MCP server path and the Model Controller runner
    /// so both use exactly the same implementations.
    /// </summary>
    private async Task<string> ExecuteToolByNameAndArgs(
        string toolName, System.Text.Json.Nodes.JsonNode args, CancellationToken ct)
    {
        var tools = BuildMcpTools();
        var tool  = tools.FirstOrDefault(t => string.Equals(t.Name, toolName,
            StringComparison.OrdinalIgnoreCase));

        if (tool is null)
            return $"Error: unknown tool '{toolName}'.";

        if (tool.ExecuteAsync is not null)
            return await tool.ExecuteAsync(args, ct);
        if (tool.QueryAsync is not null)
            return await tool.QueryAsync(args["message"]?.GetValue<string>() ?? "", ct);

        return $"Error: tool '{toolName}' has no executor.";
    }

    // ── Bridge MCP instructions ───────────────────────────────────────────

    /// <summary>
    /// Builds the MCP <c>instructions</c> string injected into every connecting
    /// client's context on handshake.  Lists all configured Bridge folders so
    /// Claude, Claude Code, and API agents know exactly what paths are available
    /// without needing to read any config files.
    /// </summary>
    private string BuildBridgeInstructions()
    {
        var cfg          = SettingsService.Load();
        var folders      = cfg.BridgeFolders;
        var participants = cfg.Participants;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## ClaudetRelay Bridge");
        sb.AppendLine();
        sb.AppendLine("You are connected to a ClaudetRelay Bridge MCP server running on the user's machine.");
        sb.AppendLine();

        // ── Current session ───────────────────────────────────────────────
        sb.AppendLine("### Current Session");
        if (BridgeProject is not null && BridgeProjectFolder is not null)
        {
            sb.AppendLine($"  Project: {BridgeProject.ProjectName}  (type: {BridgeProject.ProjectTypeName})");
            sb.AppendLine($"  Path:    {BridgeProjectFolder}");
        }
        else
        {
            sb.AppendLine("  No project loaded.");
            sb.AppendLine("  → Call bridge_list_projects then bridge_open_project(path) to load one.");
        }
        sb.AppendLine();

        // ── Available agents ──────────────────────────────────────────────
        sb.AppendLine("### Available Agents");
        var enabledAgents = cfg.BridgeAgents.Where(a => a.IsEnabled).ToList();
        if (enabledAgents.Count == 0)
        {
            sb.AppendLine("  (No agents enabled yet)");
        }
        else
        {
            foreach (var a in enabledAgents)
            {
                var pc = participants.FirstOrDefault(p =>
                    string.Equals(p.Type,  a.Provider, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(p.Model, a.Model,    StringComparison.OrdinalIgnoreCase));
                var roleTag = string.IsNullOrWhiteSpace(pc?.Role) ? "" : $"  [{pc.Role}]";
                sb.AppendLine($"  • {a.Label}{roleTag}  ({(a.IsLocal ? "Local Ollama" : a.Provider)} / {a.Model})");
                if (!string.IsNullOrWhiteSpace(pc?.SelfDescription))
                    sb.AppendLine($"    {pc.SelfDescription}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("  Call bridge_get_context for full agent details including likes/specialties.");
        sb.AppendLine();

        // ── Accessible folders ────────────────────────────────────────────
        sb.AppendLine("### Accessible Folders");
        if (folders.Count == 0)
        {
            sb.AppendLine("  (No folders configured yet — add folders in Bridge → Folders panel.)");
        }
        else
        {
            foreach (var f in folders)
                sb.AppendLine($"  • {f.Path}  [{(f.AllowWrite ? "READ + WRITE" : "READ only")}]");
        }
        sb.AppendLine();

        // ── Temp workspace ────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(cfg.BridgeTempFolder))
        {
            sb.AppendLine($"Temp workspace: {cfg.BridgeTempFolder}");
            sb.AppendLine("Use this as the base path for parallel agent task output files.");
            sb.AppendLine();
        }

        // ── Tool reference ────────────────────────────────────────────────
        sb.AppendLine("### Available Tools");
        sb.AppendLine("  Context & Project:");
        sb.AppendLine("    • bridge_get_context()                         - full snapshot: project, agents, folders, roadmap summary");
        sb.AppendLine("    • bridge_get_project_info()                    - project details, description, roles, plan file list");
        sb.AppendLine("    • bridge_get_roadmap()                         - full roadmap with milestones, tasks, IDs and progress");
        sb.AppendLine("    • bridge_update_roadmap_item(item_id, progress) - set task progress % (100 = Done)");
        sb.AppendLine("    • bridge_complete_roadmap_item(item_id, completed_by) - mark task as fully Done");
        sb.AppendLine("  Agents:");
        sb.AppendLine("    • bridge_list_projects()                       - list all available projects");
        sb.AppendLine("    • bridge_open_project(path)                    - load a project for info/roadmap tools");
        sb.AppendLine("  Agents:");
        sb.AppendLine("    • bridge_list_agents()                         - agents with roles and specialties");
        sb.AppendLine("    • bridge_ask_agent(name, message)              - send a message to a named agent");
        sb.AppendLine("    • bridge_post_to_agents(message, parallel)     - broadcast task to ALL agents, returns all responses (silent — no chat)");
        sb.AppendLine("  Folders:");
        sb.AppendLine("    • bridge_list_folders()                        - configured folders + write access");
        sb.AppendLine("    • bridge_list_folder(path)                     - list files and subfolders at a path");
        sb.AppendLine("    • bridge_create_folder(path)                   - create a new folder");
        sb.AppendLine("  Files (text):");
        sb.AppendLine("    • bridge_file_exists(path)                     - check if a file or folder exists");
        sb.AppendLine("    • bridge_read_file(path)                       - read text file (max 200 KB)");
        sb.AppendLine("    • bridge_write_file(path, content)             - write/overwrite a text file");
        sb.AppendLine("    • bridge_append_file(path, content)            - append text to a file");
        sb.AppendLine("    • bridge_rename(path, new_name)                - rename a file or folder");
        sb.AppendLine("    • bridge_move_file(source, destination)        - move a file");
        sb.AppendLine("    • bridge_delete_file(path)                     - delete a file");
        sb.AppendLine("  Files (binary):");
        sb.AppendLine("    • bridge_read_file_binary(path)                - read any file as base64 (max 10 MB)");
        sb.AppendLine("    • bridge_write_file_binary(path, base64)       - write binary content from base64");
        sb.AppendLine("  Utility:");
        sb.AppendLine("    • bridge_get_datetime()                        - current local date, time, day, unix timestamp");
        sb.AppendLine("    • bridge_web_fetch(url)                        - fetch a URL, returns readable text (max 200 KB)");
        sb.AppendLine("  Parallel tasks:");
        sb.AppendLine("    • bridge_get_workspace()                       - get temp workspace path");
        sb.AppendLine("    • bridge_run_agent_task(name, message, output) - fire agent async, returns task_id immediately");
        sb.AppendLine("    • bridge_list_active_tasks()                   - status of all running/completed tasks");
        sb.AppendLine("    • bridge_wait_for_tasks(task_ids, timeout_sec) - wait for tasks; errors on timeout/failure");
        sb.AppendLine();

        sb.AppendLine("### Parallel Workflow Pattern");
        sb.AppendLine("  1. bridge_get_workspace()                            → get temp path");
        sb.AppendLine("  2. bridge_run_agent_task(A, prompt, 'a_out.txt')     → task_id_A");
        sb.AppendLine("  3. bridge_run_agent_task(B, prompt, 'b_out.txt')     → task_id_B  (fires immediately)");
        sb.AppendLine("  4. bridge_wait_for_tasks([task_id_A, task_id_B])     → waits; errors if any failed");
        sb.AppendLine("  5. bridge_read_file(workspace + 'a_out.txt')         → agent A's response");
        sb.AppendLine("  6. bridge_read_file(workspace + 'b_out.txt')         → agent B's response");
        sb.AppendLine();
        sb.AppendLine("Tip: call bridge_get_context first to get your full bearings in one shot.");
        sb.AppendLine("Paths outside the listed folders are always rejected.");

        return sb.ToString();
    }

    // ── Effective limit helpers ────────────────────────────────────────────

    /// <summary>
    /// Returns the applicable text-file size limit based on whether the current
    /// Bridge mode uses a local (Ollama) or cloud controller.
    /// MCP Server mode is treated as cloud (the external client is typically cloud).
    /// </summary>
    private static (int textLimit, int binaryLimit) GetEffectiveLimits()
    {
        var cfg = SettingsService.Load();

        // MCP Server mode - external client (Claude Desktop / Code) is always cloud
        if (cfg.BridgeMode == BridgeAgentMode.McpServer)
            return (cfg.McpServerMaxTextFileBytes, cfg.McpServerMaxBinaryFileBytes);

        // Model Controller - depends on whether the controller is local or cloud
        bool isLocal = string.Equals(cfg.BridgeControllerProvider, "Ollama",
                                     StringComparison.OrdinalIgnoreCase);
        return isLocal
            ? (cfg.BridgeLocalMaxTextFileBytes,  cfg.BridgeLocalMaxBinaryFileBytes)
            : (cfg.BridgeCloudMaxTextFileBytes,  cfg.BridgeCloudMaxBinaryFileBytes);
    }

    // ── Bridge file access helpers ────────────────────────────────────────

    private static async Task<string> BridgeAppendFileAsync(string path, string content, CancellationToken ct)
    {
        var cfg = SettingsService.Load();
        if (!IsWithinBridgeFolder(path, cfg, requireWrite: true, out var reason))
            return $"Access denied: {reason}";
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await System.IO.File.AppendAllTextAsync(path, content, new System.Text.UTF8Encoding(false), ct);
            return $"Appended {content.Length:N0} characters to {path}";
        }
        catch (Exception ex) { return $"Error appending file: {ex.Message}"; }
    }

    private static string BridgeDeleteFile(string path)
    {
        var cfg = SettingsService.Load();
        if (!IsWithinBridgeFolder(path, cfg, requireWrite: true, out var reason))
            return $"Access denied: {reason}";
        try
        {
            if (!System.IO.File.Exists(path)) return $"Error: File not found: {path}";
            System.IO.File.Delete(path);
            return $"Deleted: {path}";
        }
        catch (Exception ex) { return $"Error deleting file: {ex.Message}"; }
    }

    private static string BridgeCreateFolder(string path)
    {
        var cfg = SettingsService.Load();
        if (!IsWithinBridgeFolder(path, cfg, requireWrite: true, out var reason))
            return $"Access denied: {reason}";
        try
        {
            System.IO.Directory.CreateDirectory(path);
            return $"Created folder: {path}";
        }
        catch (Exception ex) { return $"Error creating folder: {ex.Message}"; }
    }

    private static string BridgeRename(string path, string newName)
    {
        var cfg = SettingsService.Load();
        if (!IsWithinBridgeFolder(path, cfg, requireWrite: true, out var reason))
            return $"Access denied: {reason}";
        if (newName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            return $"Error: '{newName}' contains invalid characters.";
        try
        {
            var parent = System.IO.Path.GetDirectoryName(path)!;
            var dest   = System.IO.Path.Combine(parent, newName);
            if      (System.IO.File.Exists(path))      { System.IO.File.Move(path, dest);      return $"Renamed to: {dest}"; }
            else if (System.IO.Directory.Exists(path)) { System.IO.Directory.Move(path, dest); return $"Renamed to: {dest}"; }
            else return $"Error: Path not found: {path}";
        }
        catch (Exception ex) { return $"Error renaming: {ex.Message}"; }
    }

    private static string BridgeMoveFile(string source, string destination)
    {
        var cfg = SettingsService.Load();
        if (!IsWithinBridgeFolder(source,      cfg, requireWrite: true, out var r1)) return $"Access denied (source): {r1}";
        if (!IsWithinBridgeFolder(destination, cfg, requireWrite: true, out var r2)) return $"Access denied (destination): {r2}";
        try
        {
            if (!System.IO.File.Exists(source)) return $"Error: Source file not found: {source}";
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            System.IO.File.Move(source, destination);
            return $"Moved: {source}  →  {destination}";
        }
        catch (Exception ex) { return $"Error moving file: {ex.Message}"; }
    }

    private static string BridgeFileExists(string path)
    {
        var cfg = SettingsService.Load();
        if (!IsWithinBridgeFolder(path, cfg, requireWrite: false, out var reason))
            return $"Access denied: {reason}";
        if      (System.IO.File.Exists(path))      return $"exists:file    {path}";
        else if (System.IO.Directory.Exists(path)) return $"exists:folder  {path}";
        else                                        return $"not_found      {path}";
    }

    private static async Task<string> BridgeReadFileBinaryAsync(string path, CancellationToken ct)
    {
        var cfg = SettingsService.Load();
        if (!IsWithinBridgeFolder(path, cfg, requireWrite: false, out var reason))
            return $"Access denied: {reason}";
        try
        {
            if (!System.IO.File.Exists(path)) return $"Error: File not found: {path}";
            var info  = new System.IO.FileInfo(path);
            var (_, limit) = GetEffectiveLimits();
            if (info.Length > limit)
                return $"Error: File too large ({info.Length:N0} bytes). " +
                       $"Current binary limit is {limit:N0} bytes ({(limit >= 10_000_000 ? "local" : "cloud")} model) " +
                       $"- adjust in Bridge → Tool Settings → Limits.";
            var bytes  = await System.IO.File.ReadAllBytesAsync(path, ct);
            var base64 = Convert.ToBase64String(bytes);
            var mime   = GetMimeType(path);
            return $"mime:{mime}\nsize:{bytes.Length}\nbase64:{base64}";
        }
        catch (Exception ex) { return $"Error reading binary file: {ex.Message}"; }
    }

    private static async Task<string> BridgeWriteFileBinaryAsync(
        string path, string base64Content, CancellationToken ct)
    {
        var cfg = SettingsService.Load();
        if (!IsWithinBridgeFolder(path, cfg, requireWrite: true, out var reason))
            return $"Access denied: {reason}";
        try
        {
            byte[] bytes;
            try   { bytes = Convert.FromBase64String(base64Content); }
            catch { return "Error: content_base64 is not valid base64."; }
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await System.IO.File.WriteAllBytesAsync(path, bytes, ct);
            return $"Written {bytes.Length:N0} bytes to {path}";
        }
        catch (Exception ex) { return $"Error writing binary file: {ex.Message}"; }
    }

    /// <summary>
    /// Returns true when <paramref name="path"/> is inside (or equal to) a
    /// configured Bridge folder.  If <paramref name="requireWrite"/> is true,
    /// the matching folder must also have <see cref="BridgeFolder.AllowWrite"/>.
    /// </summary>
    private static bool IsWithinBridgeFolder(
        string path, AppSettings cfg, bool requireWrite, out string reason)
    {
        if (string.IsNullOrWhiteSpace(path))
        { reason = "Path is empty."; return false; }

        string norm;
        try   { norm = System.IO.Path.GetFullPath(path); }
        catch { reason = $"Invalid path: {path}"; return false; }

        foreach (var folder in cfg.BridgeFolders)
        {
            string normFolder;
            try   { normFolder = System.IO.Path.GetFullPath(folder.Path); }
            catch { continue; }

            // norm must equal the folder root OR start with root + separator
            if (norm.Equals(normFolder, StringComparison.OrdinalIgnoreCase) ||
                norm.StartsWith(normFolder.TrimEnd('\\', '/') + '\\', StringComparison.OrdinalIgnoreCase) ||
                norm.StartsWith(normFolder.TrimEnd('\\', '/') + '/',  StringComparison.OrdinalIgnoreCase))
            {
                if (requireWrite && !folder.AllowWrite)
                {
                    reason = $"Folder '{folder.Path}' does not have write access enabled. " +
                             "Enable 'Allow write' in the Bridge Folders panel first.";
                    return false;
                }
                reason = "";
                return true;
            }
        }

        reason = $"Path '{path}' is not within any configured Bridge folder.";
        return false;
    }

    // ── Web fetch helper ─────────────────────────────────────────────────────

    private static async Task<string> BridgeWebFetchAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "Error: url is required.";

        // Only allow http/https - no file://, ftp://, etc.
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "Error: Only http:// and https:// URLs are supported.";

        try
        {
            using var response = await _httpClient.GetAsync(url, ct);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

            if (!response.IsSuccessStatusCode)
                return $"Error: HTTP {(int)response.StatusCode} {response.ReasonPhrase}  -  {url}";

            // Only process text responses
            if (!contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) &&
                !contentType.Contains("json",  StringComparison.OrdinalIgnoreCase) &&
                !contentType.Contains("xml",   StringComparison.OrdinalIgnoreCase))
                return $"Error: Non-text content type '{contentType}'. Use bridge_read_file_binary for binary content.";

            var raw = await response.Content.ReadAsStringAsync(ct);

            // If HTML, strip tags to get readable text
            if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                raw = StripHtml(raw);

            // Cap at 200 KB to avoid flooding the context
            if (raw.Length > 200_000)
                raw = raw[..200_000] + "\n\n[... truncated at 200 000 characters ...]";

            return $"URL: {url}\nContent-Type: {contentType}\n\n{raw}";
        }
        catch (TaskCanceledException)  { return $"Error: Request timed out after 20s - {url}"; }
        catch (Exception ex)           { return $"Error fetching {url}: {ex.Message}"; }
    }

    private static string StripHtml(string html)
    {
        // Remove <script> and <style> blocks entirely
        html = System.Text.RegularExpressions.Regex.Replace(
            html, @"<(script|style)[^>]*>[\s\S]*?<\/\1>",
            " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Remove all remaining tags
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<[^>]+>", " ");

        // Decode common HTML entities
        html = System.Net.WebUtility.HtmlDecode(html);

        // Collapse whitespace and blank lines
        html = System.Text.RegularExpressions.Regex.Replace(html, @"[ \t]+", " ");
        html = System.Text.RegularExpressions.Regex.Replace(html, @"\n{3,}", "\n\n");

        return html.Trim();
    }

    // ── MIME helper ─────────────────────────────────────────────────────────

    private static string GetMimeType(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png"                => "image/png",
            ".jpg" or ".jpeg"     => "image/jpeg",
            ".gif"                => "image/gif",
            ".webp"               => "image/webp",
            ".bmp"                => "image/bmp",
            ".svg"                => "image/svg+xml",
            ".pdf"                => "application/pdf",
            ".zip"                => "application/zip",
            ".mp3"                => "audio/mpeg",
            ".mp4"                => "video/mp4",
            ".wav"                => "audio/wav",
            ".docx"               => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx"               => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx"               => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            _                     => "application/octet-stream"
        };

    private static string BridgeListFolder(string path)
    {
        var cfg = SettingsService.Load();
        if (!IsWithinBridgeFolder(path, cfg, requireWrite: false, out var reason))
            return $"Access denied: {reason}";

        try
        {
            var dir = new System.IO.DirectoryInfo(path);
            if (!dir.Exists) return $"Error: Folder does not exist: {path}";

            var sb = new StringBuilder();
            sb.AppendLine($"Listing: {path}");
            sb.AppendLine();

            var dirs  = dir.GetDirectories().OrderBy(d => d.Name).ToArray();
            var files = dir.GetFiles()       .OrderBy(f => f.Name).ToArray();

            if (dirs.Length == 0 && files.Length == 0)
            { sb.AppendLine("(empty folder)"); return sb.ToString(); }

            foreach (var d in dirs)  sb.AppendLine($"[DIR]   {d.Name}/");
            foreach (var f in files) sb.AppendLine($"[FILE]  {f.Name}  ({f.Length:N0} bytes)");

            sb.AppendLine();
            sb.AppendLine($"{dirs.Length} folder(s), {files.Length} file(s)");
            return sb.ToString();
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    private static async Task<string> BridgeReadFileAsync(string path, CancellationToken ct)
    {
        var cfg = SettingsService.Load();
        if (!IsWithinBridgeFolder(path, cfg, requireWrite: false, out var reason))
            return $"Access denied: {reason}";

        try
        {
            if (!System.IO.File.Exists(path)) return $"Error: File not found: {path}";

            var info  = new System.IO.FileInfo(path);
            var (limit, _) = GetEffectiveLimits();
            if (info.Length > limit)
                return $"Error: File too large ({info.Length:N0} bytes). " +
                       $"Current limit is {limit:N0} bytes ({(limit >= 1_000_000 ? "local" : "cloud")} model) " +
                       $"- adjust in Bridge → Tool Settings → Limits.";

            return await System.IO.File.ReadAllTextAsync(path, ct);
        }
        catch (Exception ex) { return $"Error reading file: {ex.Message}"; }
    }

    private static async Task<string> BridgeWriteFileAsync(
        string path, string content, CancellationToken ct)
    {
        var cfg = SettingsService.Load();
        if (!IsWithinBridgeFolder(path, cfg, requireWrite: true, out var reason))
            return $"Access denied: {reason}";

        try
        {
            System.IO.Directory.CreateDirectory(
                System.IO.Path.GetDirectoryName(path)!);
            await System.IO.File.WriteAllTextAsync(
                path, content, new System.Text.UTF8Encoding(false), ct);
            return $"Written {content.Length:N0} characters to {path}";
        }
        catch (Exception ex) { return $"Error writing file: {ex.Message}"; }
    }

    /// <summary>Builds a safe MCP tool name: alphanumeric + underscores, max 64 chars.</summary>
    private static string MakeMcpToolName(string provider, string display)
    {
        var raw  = $"ask_{provider}_{display}".ToLowerInvariant();
        var safe = new string(raw.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        while (safe.Contains("__")) safe = safe.Replace("__", "_");
        safe = safe.Trim('_');
        return safe.Length > 64 ? safe[..64] : safe;
    }

    private async void CloseProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProjectFolder is null || _currentProject is null) return;

        // Ask Claudette whether to make a backup - only when a backup folder is configured
        var settings         = SettingsService.Load();
        var backupFolderSet  = !string.IsNullOrWhiteSpace(settings.BackupFolder);
        if (backupFolderSet)
        {
            var choice = ShowClaudetteBackupDialog(_currentProject.ProjectName);
            if (choice == BackupChoice.Cancel)
                return;                            // user changed their mind

            if (choice == BackupChoice.BackupAndClose)
                await CreateProjectBackupAsync(_currentProjectFolder!, _currentProject!.ProjectName);  // failure shows error but does not block closing
        }

        // Persist last-opened timestamp before closing
        _currentProject.LastOpened = DateTime.UtcNow;
        ProjectService.SaveProject(_currentProjectFolder!, _currentProject);

        // Stop any running stream, clear the chat panel
        _streamCts?.Cancel();
        ChatPanel.Children.Clear();
        _sharedHistory.Clear();

        CloseCurrentProject();

        AddSystemMessage("Project closed. Start a new chat or open a project from the Projects tab.");
    }

    // ── Backup ────────────────────────────────────────────────────────────────

    private enum BackupChoice { BackupAndClose, CloseOnly, Cancel }

    /// <summary>
    /// Shows Claudette's backup-prompt dialog. Returns the user's choice.
    /// </summary>
    private BackupChoice ShowClaudetteBackupDialog(string projectName)
    {
        var bgBrush  = (Brush)FindResource("ContentBgBrush");
        var result   = BackupChoice.Cancel;

        var dlg = new Window
        {
            Title                 = "Close Project",
            Width                 = 460,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ResizeMode            = ResizeMode.NoResize,
            Background            = bgBrush
        };
        ApplyThemeToDialog(dlg);

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };

        // ── Header row: Claudette image + title ────────────────────────────
        var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var img = new System.Windows.Controls.Image
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/Assets/Claudette.png")),
            Width  = 48,
            Height = 48,
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        Grid.SetColumn(img, 0);

        var textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleTb = new TextBlock
        {
            Text         = "Create backup? 🐙",
            FontSize     = 15,
            FontWeight   = FontWeights.SemiBold,
            FontFamily   = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 6)
        };
        titleTb.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");

        var msgTb = new TextBlock
        {
            Text         = $"You are closing project \"{projectName}\". " +
                           "Should I create a backup first? 🐙\n\n" +
                           "Quick tip: keep your project folder tidy and delete old backups from time to time - " +
                           "a full backup folder is like a tangled tentacle net! 🐙💦",
            FontSize     = 13,
            FontFamily   = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap
        };
        msgTb.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        textCol.Children.Add(titleTb);
        textCol.Children.Add(msgTb);
        Grid.SetColumn(textCol, 1);

        headerGrid.Children.Add(img);
        headerGrid.Children.Add(textCol);
        panel.Children.Add(headerGrid);

        // ── Button row ─────────────────────────────────────────────────────
        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, 8, 0, 0)
        };

        var backupBtn = new Button
        {
            Content = "💾 Backup + Close",
            Height  = 36,
            Padding = new Thickness(16, 0, 16, 0),
            Margin  = new Thickness(0, 0, 8, 0),
            Style   = (Style)FindResource("ModernButton")
        };
        backupBtn.SetResourceReference(Button.BackgroundProperty, "PrimaryAccentBrush");
        backupBtn.SetResourceReference(Button.ForegroundProperty, "AccentTextBrush");

        var closeBtn = new Button
        {
            Content = "Close",
            Height  = 36,
            Padding = new Thickness(16, 0, 16, 0),
            Margin  = new Thickness(0, 0, 8, 0),
            Style   = (Style)FindResource("ModernButton")
        };
        closeBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        closeBtn.SetResourceReference(Button.ForegroundProperty, "ControlTextBrush");

        var cancelBtn = new Button
        {
            Content = "Cancel",
            Height  = 36,
            Padding = new Thickness(16, 0, 16, 0),
            Style   = (Style)FindResource("ModernButton")
        };
        cancelBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        cancelBtn.SetResourceReference(Button.ForegroundProperty, "ControlTextBrush");

        btnRow.Children.Add(backupBtn);
        btnRow.Children.Add(closeBtn);
        btnRow.Children.Add(cancelBtn);
        panel.Children.Add(btnRow);

        backupBtn.Click += (_, _) => { result = BackupChoice.BackupAndClose; dlg.Close(); };
        closeBtn .Click += (_, _) => { result = BackupChoice.CloseOnly;      dlg.Close(); };
        cancelBtn.Click += (_, _) => { result = BackupChoice.Cancel;         dlg.Close(); };

        dlg.Content = panel;
        dlg.ShowDialog();
        return result;
    }

    /// <summary>
    /// Creates a ZIP backup of <paramref name="projFolder"/> into the configured backup folder.
    /// Shows a progress window while working.
    /// Returns true on success; shows an error dialog and returns false on failure.
    /// </summary>
    private async Task<bool> CreateProjectBackupAsync(string projFolder, string projectName)
    {
        var settings = SettingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.BackupFolder))
        {
            MessageBox.Show(
                "Please set a backup folder first under ☰ → Folders Setup.",
                "No Backup Folder", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var backupFolder = settings.BackupFolder;
        try
        {
            // CreateDirectory is a no-op if the directory already exists
            SysIO.Directory.CreateDirectory(backupFolder);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not create backup folder:\n{ex.Message}",
                "Backup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        var invalidChars        = SysIO.Path.GetInvalidFileNameChars();
        var safeName            = new string(projectName
            .Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
        var projectBackupFolder = SysIO.Path.Combine(backupFolder, safeName);
        var timestamp           = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var zipName             = $"{safeName}_{timestamp}.zip";
        var zipPath             = SysIO.Path.Combine(projectBackupFolder, zipName);

        // ── Progress window ────────────────────────────────────────────────
        var bgBrush     = (Brush)FindResource("ContentBgBrush");
        var progressWin = new Window
        {
            Title                 = "Creating backup…",
            Width                 = 420,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ResizeMode            = ResizeMode.NoResize,
            Background            = bgBrush
        };
        ApplyThemeToDialog(progressWin);

        var progressPanel = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };

        var progressTitle = new TextBlock
        {
            Text       = $"💾  {projectName}",
            FontSize   = 13,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"),
            Margin     = new Thickness(0, 0, 0, 12)
        };
        progressTitle.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");

        var progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value   = 0,
            Height  = 12,
            Margin  = new Thickness(0, 0, 0, 8)
        };

        var pctLabel = new TextBlock
        {
            Text       = "0 %",
            FontSize   = 11,
            FontFamily = new FontFamily("Segoe UI"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin     = new Thickness(0, 0, 0, 6)
        };
        pctLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var fileLabel = new TextBlock
        {
            Text         = "Reading files…",
            FontSize     = 11,
            FontFamily   = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        fileLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        progressPanel.Children.Add(progressTitle);
        progressPanel.Children.Add(progressBar);
        progressPanel.Children.Add(pctLabel);
        progressPanel.Children.Add(fileLabel);
        progressWin.Content = progressPanel;
        progressWin.Show();

        // ── Progress callback (marshals to UI thread automatically) ────────
        var progress = new Progress<(int pct, string fileName)>(update =>
        {
            progressBar.Value = update.pct;
            pctLabel.Text     = $"{update.pct} %";
            fileLabel.Text    = update.fileName;
        });

        // ── Zip file by file ───────────────────────────────────────────────
        try
        {
            SysIO.Directory.CreateDirectory(projectBackupFolder);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not create project subfolder:\n{ex.Message}",
                "Backup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        try
        {
            await Task.Run(() =>
            {
                var files = SysIO.Directory.GetFiles(projFolder, "*",
                    SysIO.SearchOption.AllDirectories);
                var total        = Math.Max(1, files.Length);
                int lastReported = -1;

                using var archive = System.IO.Compression.ZipFile.Open(
                    zipPath, System.IO.Compression.ZipArchiveMode.Create);

                for (int i = 0; i < files.Length; i++)
                {
                    var relativePath = SysIO.Path.GetRelativePath(projFolder, files[i]);
                    var entry = archive.CreateEntry(relativePath,
                        System.IO.Compression.CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    using var fileStream  = SysIO.File.OpenRead(files[i]);
                    fileStream.CopyTo(entryStream);

                    int pct = (int)((double)(i + 1) / total * 100);
                    if (pct != lastReported)
                    {
                        lastReported = pct;
                        ((IProgress<(int, string)>)progress)
                            .Report((pct, SysIO.Path.GetFileName(files[i])));
                    }
                }
            });

            progressWin.Close();
            AddSystemMessage($"✅ Backup created: {zipPath}");
            return true;
        }
        catch (Exception ex)
        {
            progressWin.Close();
            // Delete the incomplete ZIP so it doesn't leave a corrupt file behind
            try { if (SysIO.File.Exists(zipPath)) SysIO.File.Delete(zipPath); } catch { }

            MessageBox.Show(
                $"Backup failed:\n{ex.Message}\n\n" +
                "Your project data is still safe in the project folder.\n" +
                "Please retry the backup manually via the 💾 button after fixing the issue.",
                "Backup Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private async void BackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProjectFolder is null || _currentProject is null) return;
        await CreateProjectBackupAsync(_currentProjectFolder, _currentProject.ProjectName);
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
            _sharedHistory.Add(new CloudAIMessage("assistant", entry.Message, entry.DisplayName));

        // Guard against legacy log entries that pre-date BubbleKey / AccentKey storage.
        // An empty key causes SetResourceReference to resolve nothing → WPF falls back to
        // SystemColors.ControlTextBrush (black) on a transparent bubble - readable in light
        // themes but invisible / broken in dark ones.
        var bubbleKey = string.IsNullOrEmpty(entry.BubbleKey)
            ? (entry.IsUser ? "TertiaryBubbleBrush" : "PrimaryBubbleBrush")
            : entry.BubbleKey;
        var accentKey = string.IsNullOrEmpty(entry.AccentKey)
            ? (entry.IsUser ? "TertiaryAccentBrush" : "PrimaryAccentBrush")
            : entry.AccentKey;

        var bubble = AddStreamingBubble(entry.DisplayName, entry.AvatarLabel,
                                         accentKey, bubbleKey, entry.IsUser);
        bubble.StopThinking();
        bubble.Content.Text = entry.Message;
    }

    private void AppendToProjectLog(ChatLogEntry entry)
    {
        if (_currentProjectFolder is null) return;
        try { ProjectService.AppendEntry(_currentProjectFolder, entry); }
        catch { /* non-fatal */ }
    }

    /// <summary>
    /// Appends <paramref name="entry"/> to the rolling general-chat log.
    /// Only active outside of projects. Triggers background AI summarisation
    /// when a segment rotation occurs (every 500 entries).
    /// </summary>
    private void AppendToGeneralLog(ChatLogEntry entry)
    {
        if (_currentProjectFolder is not null) return;
        try
        {
            GeneralChatLogService.AppendEntry(entry, out var displaced);
            if (displaced is { Count: > 0 })
                _ = SummarizeAndCompressGeneralLogAsync(displaced);
        }
        catch { /* non-fatal */ }
    }

    // ── General-chat log summarisation ────────────────────────────────────

    private const string GeneralSummarizeSystem =
        "You are a chat log summarizer. Summarize the provided segment of a general chat " +
        "session concisely, preserving all key topics, decisions, and important information " +
        "that was discussed. Write two to three short paragraphs. " +
        "Output only the summary - no preamble, no metadata, no heading.";

    private const string GeneralCompressSystem =
        "You are a summary compressor. The following is a running log of past chat-session summaries. " +
        "Condense it into a single compact summary that preserves all key topics, themes, decisions, " +
        "and important information. Output only the condensed text - no headers, no preamble.";

    /// <summary>Max characters in summary.md before compression is triggered.</summary>
    private const int SummaryCompressThreshold = 3_000;

    /// <summary>
    /// Runs in the background after a segment rotation.
    /// Picks the best available AI (Ollama with Gemma first, then other Ollama, then Cloud),
    /// silently summarises <paramref name="displaced"/> entries, appends to summary.md,
    /// then compresses summary.md if it has grown too large.
    /// </summary>
    private async Task SummarizeAndCompressGeneralLogAsync(List<ChatLogEntry> displaced)
    {
        // ── Show Claudette speech bubble + start pulsing ──────────────────
        await Dispatcher.InvokeAsync(() =>
        {
            ClaudetteSpeechText.Text         = "Ich räume mal den Chat auf! 🐙";
            ClaudetteSpeechBubble.Visibility  = Visibility.Visible;
            StartClaudettePulse();
        });

        try
        {
            // Build a readable text version of the displaced segment
            var sb = new StringBuilder();
            foreach (var e in displaced)
            {
                if (e.SenderType == "System") continue;
                sb.AppendLine($"[{e.Timestamp:HH:mm}] {e.DisplayName}: {e.Message}");
                sb.AppendLine();
            }
            var chatText = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(chatText)) return;

            var summaryPrompt = $"Summarize this chat log segment:\n\n{chatText}";

            // ── Pick summarizing service ──────────────────────────────────
            var ollamaUi = PickSummarizingOllama();
            var cloudUi  = ollamaUi is null
                ? _cloudAIParticipants.FirstOrDefault(ui => ui.Data.Enabled)
                : null;
            if (ollamaUi is null && cloudUi is null) return;

            string summary;
            if (ollamaUi is not null)
            {
                // Prepend system instruction to the user turn (Ollama has no separate system arg here)
                var hist = new List<OllamaChatMessage>
                {
                    new("system", GeneralSummarizeSystem),
                    new("user",   summaryPrompt)
                };
                var result = new StringBuilder();
                await foreach (var tok in ollamaUi.Data.Service.StreamAsync(hist, CancellationToken.None))
                    result.Append(tok);
                summary = result.ToString().Trim();
            }
            else
            {
                var hist = new List<CloudAIMessage> { new("user", summaryPrompt, "System") };
                var result = new StringBuilder();
                await foreach (var tok in cloudUi!.Data.Service.StreamAsync(hist, GeneralSummarizeSystem, CancellationToken.None))
                    result.Append(tok);
                summary = result.ToString().Trim();
            }

            if (string.IsNullOrWhiteSpace(summary)) return;

            var section = $"## {DateTime.Now:yyyy-MM-dd HH:mm}\n{summary}";
            GeneralChatLogService.AppendToSummary(section);

            // ── Compress summary.md if it has grown too large ─────────────
            var fullSummary = GeneralChatLogService.ReadSummary() ?? "";
            if (fullSummary.Length > SummaryCompressThreshold)
                await CompressGeneralSummaryAsync(fullSummary, ollamaUi, cloudUi);
        }
        catch { /* non-fatal - summarisation is best-effort */ }
        finally
        {
            // ── Hide speech bubble + stop pulsing ─────────────────────────
            await Dispatcher.InvokeAsync(() =>
            {
                ClaudetteSpeechBubble.Visibility = Visibility.Collapsed;
                StopClaudettePulse();
            });
        }
    }

    /// <summary>
    /// Replaces summary.md with a compressed version produced by an AI.
    /// </summary>
    private async Task CompressGeneralSummaryAsync(string currentSummary,
        OllamaParticipantUI? ollamaUi, CloudAIParticipantUI? cloudUi)
    {
        try
        {
            var compressPrompt = $"Condense this running summary:\n\n{currentSummary}";

            string compressed;
            if (ollamaUi is not null)
            {
                var hist = new List<OllamaChatMessage>
                {
                    new("system", GeneralCompressSystem),
                    new("user",   compressPrompt)
                };
                var result = new StringBuilder();
                await foreach (var tok in ollamaUi.Data.Service.StreamAsync(hist, CancellationToken.None))
                    result.Append(tok);
                compressed = result.ToString().Trim();
            }
            else if (cloudUi is not null)
            {
                var hist = new List<CloudAIMessage> { new("user", compressPrompt, "System") };
                var result = new StringBuilder();
                await foreach (var tok in cloudUi.Data.Service.StreamAsync(hist, GeneralCompressSystem, CancellationToken.None))
                    result.Append(tok);
                compressed = result.ToString().Trim();
            }
            else return;

            if (!string.IsNullOrWhiteSpace(compressed))
                GeneralChatLogService.ReplaceSummary(
                    $"*[Compressed {DateTime.Now:yyyy-MM-dd HH:mm}]*\n\n{compressed}");
        }
        catch { /* non-fatal */ }
    }

    /// <summary>
    /// Picks the best available Ollama participant for background summarisation:
    /// Gemma models first (faster), then any other enabled Ollama.
    /// Returns null if no Ollama is enabled.
    /// </summary>
    private OllamaParticipantUI? PickSummarizingOllama()
        => _ollamaParticipants
            .Where(ui => ui.Data.Enabled)
            .OrderByDescending(ui =>
                ui.Data.Service.CurrentModel.Contains("gemma", StringComparison.OrdinalIgnoreCase) ? 2 :
                ui.Data.Service.CurrentModel.Contains("qwen",  StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .FirstOrDefault();

    // ── Chat export ────────────────────────────────────────────────────────

    private void ExportChatButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProjectFolder is not null && _currentProject is not null)
        {
            // Project is open - use the project exporter
            var menu = new ContextMenu { PlacementTarget = ExportChatButton,
                                         Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            var htmlItem = new MenuItem { Header = "🔄  Export as HTML…" };
            var mdItem   = new MenuItem { Header = "📝  Export as Markdown…" };
            var capturedFolder = _currentProjectFolder;
            var capturedMeta   = _currentProject;
            htmlItem.Click += (_, _) => ExportProject(capturedFolder, capturedMeta, "html");
            mdItem.Click   += (_, _) => ExportProject(capturedFolder, capturedMeta, "md");
            menu.Items.Add(htmlItem);
            menu.Items.Add(mdItem);
            menu.IsOpen = true;
            return;
        }

        // General chat - export the rolling log
        var entries = GeneralChatLogService.LoadRecentLog();
        if (entries.Count == 0)
        {
            MessageBox.Show("No general chat history to export yet.",
                            "Nothing to export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var menu2 = new ContextMenu { PlacementTarget = ExportChatButton,
                                      Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var html2 = new MenuItem { Header = "🔄  Export as HTML…" };
        var md2   = new MenuItem { Header = "📝  Export as Markdown…" };
        html2.Click += (_, _) => ExportGeneralChat(entries, "html");
        md2.Click   += (_, _) => ExportGeneralChat(entries, "md");
        menu2.Items.Add(html2);
        menu2.Items.Add(md2);
        menu2.IsOpen = true;
    }

    private void ExportGeneralChat(List<ChatLogEntry> entries, string format)
    {
        var isHtml   = format == "html";
        var dateName = DateTime.Now.ToString("yyyy-MM-dd");
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Export General Chat",
            FileName   = $"ClaudetRelay-Chat-{dateName}",
            Filter     = isHtml
                ? "HTML file (*.html)|*.html"
                : "Markdown file (*.md)|*.md|Text file (*.txt)|*.txt",
            DefaultExt = format
        };
        if (dlg.ShowDialog() != true) return;

        var fs = SettingsService.Load();
        var content = isHtml
            ? ExportService.GenerateHtml("General Chat", entries,
                                          fs.ChatFontFamily, fs.ChatFontSize,
                                          fs.ChatBubbleWidthPercent)
            : ExportService.GenerateMarkdown("General Chat", entries);

        SysIO.File.WriteAllText(dlg.FileName, content, System.Text.Encoding.UTF8);

        var result = MessageBox.Show(
            $"Exported {entries.Count} messages to\n{dlg.FileName}\n\nOpen the file now?",
            "Export complete", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (result == MessageBoxResult.Yes)
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
    }

    // ── Simple input dialog ────────────────────────────────────────────────

    private string? ShowInputDialog(string title, string prompt, string defaultValue = "")
    {
        // FindResource is called on *this* (MainWindow) which has the theme loaded.
        // SetResourceReference would search the popup's own empty resource tree and fall
        // back to the default WPF chrome - producing black buttons on dark themes.
        var win = new Window
        {
            Title                 = title,
            Width                 = 400, Height = 170,
            Owner                 = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode            = ResizeMode.NoResize,
            ShowInTaskbar         = false,
            Background            = (Brush)FindResource("SidebarBgBrush")
        };
        ApplyThemeToDialog(win);

        var lbl = new TextBlock
        {
            Text       = prompt,
            FontSize   = 13,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("ContentTextBrush"),
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
            Background               = (Brush)FindResource("ControlBgBrush"),
            Foreground               = (Brush)FindResource("ContentTextBrush"),
            CaretBrush               = (Brush)FindResource("InputTextBrush"),
            SelectionBrush           = (Brush)FindResource("PrimaryAccentBrush")
        };

        var okBtn = new Button
        {
            Content    = "Create",
            IsDefault  = true,
            Height     = 34,
            Margin     = new Thickness(16, 0, 8, 16),
            Style      = (Style)FindResource("ModernButton"),
            Background = (Brush)FindResource("PrimaryAccentBrush"),
            Foreground = (Brush)FindResource("AccentTextBrush")
        };

        var cancelBtn = new Button
        {
            Content    = "Cancel",
            IsCancel   = true,
            Height     = 34,
            Margin     = new Thickness(0, 0, 16, 16),
            Style      = (Style)FindResource("ModernButton"),
            Background = (Brush)FindResource("ControlBgBrush"),
            Foreground = (Brush)FindResource("ContentTextBrush")
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

    // ── New-project dialog (name + description) ────────────────────────────

    /// <summary>
    /// Shows a dialog that collects both the project name and an optional freeform
    /// description. Returns (Name, Description) on confirm, or null if cancelled.
    /// </summary>
    private (string Name, string Description)? ShowNewProjectDialog(
        string defaultName = "My Project", string defaultDescription = "")
    {
        var win = new Window
        {
            Title                 = "New Project",
            Width                 = 460,
            SizeToContent         = SizeToContent.Height,
            Owner                 = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode            = ResizeMode.NoResize,
            ShowInTaskbar         = false,
            Background            = (Brush)FindResource("SidebarBgBrush")
        };
        ApplyThemeToDialog(win);

        Border MakeInputBorder(UIElement child) => new Border
        {
            Background      = (Brush)FindResource("InputBgBrush"),
            BorderBrush     = (Brush)FindResource("ControlBgBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(8),
            Padding         = new Thickness(0),
            Margin          = new Thickness(0, 0, 0, 6),
            Child           = child
        };

        TextBlock MakeLabel(string text) => new TextBlock
        {
            Text       = text,
            FontSize   = 11, FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("ContentDimBrush"),
            Margin     = new Thickness(0, 0, 0, 5)
        };

        TextBlock MakeHint(string text) => new TextBlock
        {
            Text         = text,
            FontSize     = 11, FontFamily = new FontFamily("Segoe UI"),
            Foreground   = (Brush)FindResource("ContentDimBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 16)
        };

        // ── Name field ─────────────────────────────────────────────────────
        var nameBox = new TextBox
        {
            Text                     = defaultName,
            FontSize                 = 13, FontFamily = new FontFamily("Segoe UI"),
            Height                   = 36,
            BorderThickness          = new Thickness(0),
            Padding                  = new Thickness(10, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background               = (Brush)FindResource("InputBgBrush"),
            Foreground               = (Brush)FindResource("ContentTextBrush"),
            CaretBrush               = (Brush)FindResource("InputTextBrush"),
            SelectionBrush           = (Brush)FindResource("PrimaryAccentBrush")
        };

        // ── Description field ──────────────────────────────────────────────
        var descBox = new TextBox
        {
            Text            = defaultDescription,
            FontSize        = 13, FontFamily = new FontFamily("Segoe UI"),
            Height          = 90,
            MinHeight       = 90,
            BorderThickness = new Thickness(0),
            Padding         = new Thickness(10, 8, 10, 8),
            TextWrapping    = TextWrapping.Wrap,
            AcceptsReturn   = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background      = (Brush)FindResource("InputBgBrush"),
            Foreground      = (Brush)FindResource("ContentTextBrush"),
            CaretBrush      = (Brush)FindResource("InputTextBrush"),
            SelectionBrush  = (Brush)FindResource("PrimaryAccentBrush"),
            ToolTip         = "Describe what this project is about. The AI participants will read this."
        };

        // ── Buttons ────────────────────────────────────────────────────────
        var okBtn = new Button
        {
            Content    = "Create",
            IsDefault  = true,
            Height     = 34, Margin = new Thickness(0, 0, 8, 0),
            Style      = (Style)FindResource("ModernButton"),
            Background = (Brush)FindResource("PrimaryAccentBrush"),
            Foreground = (Brush)FindResource("AccentTextBrush")
        };
        var cancelBtn = new Button
        {
            Content    = "Cancel",
            IsCancel   = true,
            Height     = 34,
            Style      = (Style)FindResource("ModernButton"),
            Background = (Brush)FindResource("ControlBgBrush"),
            Foreground = (Brush)FindResource("SidebarTextBrush")
        };
        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, 8, 0, 0)
        };
        btnRow.Children.Add(okBtn);
        btnRow.Children.Add(cancelBtn);

        // ── Layout ─────────────────────────────────────────────────────────
        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        root.Children.Add(MakeLabel("PROJECT NAME"));
        root.Children.Add(MakeInputBorder(nameBox));
        root.Children.Add(MakeLabel("DESCRIPTION  (optional - shown to AI participants)"));
        root.Children.Add(MakeInputBorder(descBox));
        root.Children.Add(MakeHint(
            "Tell the AI what this project is about. " +
            "Example: \"A dark fantasy novel about a dragon who falls in love with a wizard.\" " +
            "You can leave this blank and add it later in Project Settings."));
        root.Children.Add(btnRow);
        win.Content = root;

        (string Name, string Description)? dialogResult = null;
        okBtn.Click += (_, _) =>
        {
            var n = nameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(n)) { nameBox.Focus(); return; }
            dialogResult = (n, descBox.Text.Trim());
            win.DialogResult = true;
        };
        win.Loaded += (_, _) => { nameBox.Focus(); nameBox.SelectAll(); };
        win.ShowDialog();
        return dialogResult;
    }

    // ── Options menu (⋮) ──────────────────────────────────────────────────

    private void OptionsMenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();

        var generalItem   = new MenuItem { Header = "⚙  General Settings" };
        var foldersItem   = new MenuItem { Header = "📁  Folders Setup" };
        var providersItem = new MenuItem { Header = "🔑  Providers Setup" };
        var infoItem      = new MenuItem { Header = "ℹ  Info" };
        var versionItem   = new MenuItem { Header = "📋  Version" };

        generalItem  .Click += (_, _) => OpenGeneralSettings();
        foldersItem  .Click += (_, _) => ShowFoldersSetupDialog();
        providersItem.Click += (_, _) => OpenProvidersSetup();
        infoItem     .Click += (_, _) => ShowAboutInfoDialog();
        versionItem  .Click += (_, _) => ShowAboutVersionDialog();

        menu.Items.Add(generalItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(foldersItem);
        menu.Items.Add(providersItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(infoItem);
        menu.Items.Add(versionItem);

        menu.PlacementTarget = (Button)sender;
        menu.Placement       = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen          = true;
    }

    private void OpenGeneralSettings()
    {
        var win = new SettingsWindow(_currentThemePath, providerModeOnly: false) { Owner = this };
        if (win.ShowDialog() == true)
        {
            var updated = SettingsService.Load();
            _userName             = string.IsNullOrWhiteSpace(updated.UserName) ? "You" : updated.UserName.Trim();
            _toneLevel            = updated.ToneLevel;
            _chattinessLevel      = updated.GlobalChattiness;
            _mockingbirdMode      = updated.MockingbirdMode;
            _aiDialogueEnabled    = updated.AiDialogueEnabled;
            _aiDialogueMaxTurns   = Math.Clamp(updated.AiDialogueMaxTurns, 3, 100);
            _globalResponseLength = Math.Clamp(updated.GlobalResponseLength, 0, 100);
            UpdateAiDialogueButton();
            ApplyChatFont(updated);
            ApplyUiZoom(updated.UiZoom);
        }
    }

    private void OpenProvidersSetup()
    {
        var win = new SettingsWindow(_currentThemePath, providerModeOnly: true) { Owner = this };
        win.SourceInitialized += (_, _) => ApplyTitleBarTheme(win);
        if (win.ShowDialog() == true)
        {
            var updated = SettingsService.Load();
            ApplyThrottleSettings(updated);
        }
    }

    private void ShowFoldersSetupDialog()
    {
        var settings      = SettingsService.Load();
        var defaultFolder = Services.ProjectService.GetDefaultProjectsFolder();

        var win = new Window
        {
            Title                 = "Folders Setup",
            Width                 = 480,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ResizeMode            = ResizeMode.NoResize
        };
        ApplyThemeToDialog(win);
        win.SetResourceReference(Window.BackgroundProperty, "ContentBgBrush");

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        // ── Heading ────────────────────────────────────────────────────────
        var heading = new TextBlock
        {
            Text       = "PROJECTS FOLDER",
            FontSize   = 11,
            FontWeight = FontWeights.Bold,
            Margin     = new Thickness(0, 0, 0, 6)
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        // ── Folder input ───────────────────────────────────────────────────
        var folderTb = new TextBox
        {
            Text            = string.IsNullOrWhiteSpace(settings.ProjectsFolder)
                                  ? "" : settings.ProjectsFolder,
            FontSize        = 13,
            FontFamily      = new FontFamily("Segoe UI"),
            BorderThickness = new Thickness(0),
            Background      = Brushes.Transparent,
            Padding         = new Thickness(0, 2, 0, 2)
        };
        folderTb.SetResourceReference(TextBox.ForegroundProperty,  "InputTextBrush");
        folderTb.SetResourceReference(TextBox.CaretBrushProperty,  "InputTextBrush");
        var folderBorder = new Border
        {
            CornerRadius    = new CornerRadius(8), Height = 34,
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(10, 0, 10, 0),
            Child           = folderTb
        };
        folderBorder.SetResourceReference(Border.BackgroundProperty,  "InputBgBrush");
        folderBorder.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush");

        var browseBtn = new Button
        {
            Content = "📁  Browse",
            Padding = new Thickness(12, 8, 12, 8),
            Margin  = new Thickness(6, 0, 0, 0),
            ToolTip = "Choose folder"
        };
        browseBtn.Style = (Style)FindResource("ModernButton");
        browseBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        browseBtn.SetResourceReference(Button.ForegroundProperty, "ControlTextBrush");
        browseBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select Projects Folder",
                InitialDirectory = string.IsNullOrWhiteSpace(folderTb.Text) ? defaultFolder : folderTb.Text
            };
            if (dlg.ShowDialog(win) == true)
                folderTb.Text = dlg.FolderName;
        };

        var defaultBtn = new Button
        {
            Content = "↩  Default",
            Padding = new Thickness(12, 8, 12, 8),
            Margin  = new Thickness(6, 0, 0, 0),
            ToolTip = defaultFolder
        };
        defaultBtn.Style = (Style)FindResource("ModernButton");
        defaultBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        defaultBtn.SetResourceReference(Button.ForegroundProperty, "ControlTextBrush");
        defaultBtn.Click += (_, _) => folderTb.Text = "";

        var folderRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(folderBorder, 0);
        Grid.SetColumn(browseBtn,    1);
        Grid.SetColumn(defaultBtn,   2);
        folderRow.Children.Add(folderBorder);
        folderRow.Children.Add(browseBtn);
        folderRow.Children.Add(defaultBtn);

        var hint = new TextBlock
        {
            Text         = $"Default: {defaultFolder}",
            FontSize     = 11,
            FontFamily   = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 20)
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        // ── Separator ──────────────────────────────────────────────────────
        var sep = new Rectangle
        {
            Height  = 1,
            Margin  = new Thickness(0, 0, 0, 16)
        };
        sep.SetResourceReference(Shape.FillProperty, "ControlBgBrush");

        // ── BACKUP FOLDER ──────────────────────────────────────────────────
        var backupHeading = new TextBlock
        {
            Text       = "BACKUP FOLDER",
            FontSize   = 11,
            FontWeight = FontWeights.Bold,
            Margin     = new Thickness(0, 0, 0, 6)
        };
        backupHeading.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var backupTb = new TextBox
        {
            Text            = settings.BackupFolder,
            FontSize        = 13,
            FontFamily      = new FontFamily("Segoe UI"),
            BorderThickness = new Thickness(0),
            Background      = Brushes.Transparent,
            Padding         = new Thickness(0, 2, 0, 2)
        };
        backupTb.SetResourceReference(TextBox.ForegroundProperty,  "InputTextBrush");
        backupTb.SetResourceReference(TextBox.CaretBrushProperty,  "InputTextBrush");
        var backupBorder = new Border
        {
            CornerRadius    = new CornerRadius(8), Height = 34,
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(10, 0, 10, 0),
            Child           = backupTb
        };
        backupBorder.SetResourceReference(Border.BackgroundProperty,  "InputBgBrush");
        backupBorder.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush");

        var backupBrowseBtn = new Button
        {
            Content = "📁  Browse",
            Padding = new Thickness(12, 8, 12, 8),
            Margin  = new Thickness(6, 0, 0, 0),
            ToolTip = "Choose backup folder"
        };
        backupBrowseBtn.Style = (Style)FindResource("ModernButton");
        backupBrowseBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        backupBrowseBtn.SetResourceReference(Button.ForegroundProperty, "ControlTextBrush");
        backupBrowseBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select Backup Folder",
                InitialDirectory = string.IsNullOrWhiteSpace(backupTb.Text) ? defaultFolder : backupTb.Text
            };
            if (dlg.ShowDialog(win) == true)
                backupTb.Text = dlg.FolderName;
        };

        var backupClearBtn = new Button
        {
            Content = "✕  Clear",
            Padding = new Thickness(12, 8, 12, 8),
            Margin  = new Thickness(6, 0, 0, 0),
            ToolTip = "Disable backups (no backup prompt on project close)"
        };
        backupClearBtn.Style = (Style)FindResource("ModernButton");
        backupClearBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        backupClearBtn.SetResourceReference(Button.ForegroundProperty, "ControlTextBrush");
        backupClearBtn.Click += (_, _) => backupTb.Text = "";

        var backupRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        backupRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        backupRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        backupRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(backupBorder,    0);
        Grid.SetColumn(backupBrowseBtn, 1);
        Grid.SetColumn(backupClearBtn,  2);
        backupRow.Children.Add(backupBorder);
        backupRow.Children.Add(backupBrowseBtn);
        backupRow.Children.Add(backupClearBtn);

        var backupHint = new TextBlock
        {
            Text         = "ZIPs of the project folder are saved here. " +
                           "Leave empty to disable backup prompts. " +
                           "The folder is created automatically if it does not exist.",
            FontSize     = 11,
            FontFamily   = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 20)
        };
        backupHint.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        // ── Save ───────────────────────────────────────────────────────────
        var saveBtn = new Button
        {
            Content             = "Save",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding             = new Thickness(20, 8, 20, 8)
        };
        saveBtn.Style = (Style)FindResource("ModernButton");
        saveBtn.SetResourceReference(Button.BackgroundProperty, "PrimaryAccentBrush");
        saveBtn.SetResourceReference(Button.ForegroundProperty, "AccentTextBrush");
        saveBtn.Click += (_, _) =>
        {
            var s = SettingsService.Load();
            s.ProjectsFolder = folderTb.Text.Trim();
            s.BackupFolder   = backupTb.Text.Trim();
            SettingsService.Save(s);
            win.Close();
        };

        panel.Children.Add(heading);
        panel.Children.Add(folderRow);
        panel.Children.Add(hint);
        panel.Children.Add(sep);
        panel.Children.Add(backupHeading);
        panel.Children.Add(backupRow);
        panel.Children.Add(backupHint);
        panel.Children.Add(saveBtn);

        win.Content = panel;
        win.ShowDialog();
    }

    // ── Chat font (Aa) ─────────────────────────────────────────────────────

    /// <summary>Seeds / updates the ChatFontFamily and ChatFontSize dynamic resources.</summary>
    private void ApplyChatFont(AppSettings settings)
    {
        Resources["ChatFontFamily"] = new FontFamily(settings.ChatFontFamily);
        Resources["ChatFontSize"]   = settings.ChatFontSize;
    }

    /// <summary>
    /// Applies a uniform UI zoom to the main window content.
    /// The main window itself is not resized (it fills the screen / is user-resized);
    /// its scroll areas handle any overflow at large zoom levels.
    /// Also re-applies zoom to the open ParticipantsWindow if it is currently visible.
    /// </summary>
    private void ApplyUiZoom(double zoom)
    {
        UiZoomHelper.Apply(this, zoom, scaleWindow: false);
        if (_participantsWindow is { IsVisible: true })
            UiZoomHelper.Apply(_participantsWindow, zoom, scaleWindow: false);
    }

    /// <summary>
    /// Recalculates the bubble width and publishes it via the "ChatBubbleMaxWidth" resource.
    /// Bubbles use Width (not MaxWidth) so they always fill exactly slider-% of the chat area -
    /// even short messages don't produce a narrower bubble than the chosen percentage.
    /// Called on startup, on window resize, and when the user changes the slider.
    /// </summary>
    private void UpdateChatBubbleWidth()
    {
        const double avatarWidth = 44.0;  // avatar Border 34 px + Margin 10 px

        // ChatPanel.ActualWidth is the most accurate source: it is the exact content-area
        // width (ScrollViewer width minus padding minus scrollbar).  Fall back to the
        // ScrollViewer's own ActualWidth (minus padding) if the panel hasn't been laid out
        // yet, or use 840 px as a startup default.
        var panelWidth = ChatPanel.ActualWidth;
        double available;
        if (panelWidth > 10)
            available = panelWidth;
        else
        {
            var svw = ChatScrollViewer.ActualWidth;
            available = svw > 10 ? svw - 40 : 840.0;   // 40 = left(20)+right(20) padding
        }

        var bubbleW = Math.Max(50.0, (available - avatarWidth) * (_chatBubbleWidthPct / 100.0));
        Resources["ChatBubbleMaxWidth"] = bubbleW;   // key name kept for compatibility

        // Belt-and-suspenders: SetResourceReference is cleared the first time Width is set
        // directly, so we walk every BubbleContent panel explicitly on every recalculation.
        foreach (var wrapper in ChatPanel.Children.OfType<Grid>())
            foreach (FrameworkElement cell in wrapper.Children)
                if (cell.Tag as string == "BubbleContent")
                    cell.Width = bubbleW;
    }

    // ── AI Dialogue toggle ─────────────────────────────────────────────────

    private void AiDialogueButton_Click(object sender, RoutedEventArgs e)
    {
        _aiDialogueEnabled = !_aiDialogueEnabled;
        UpdateAiDialogueButton();
        var settings = SettingsService.Load();
        settings.AiDialogueEnabled = _aiDialogueEnabled;
        SettingsService.Save(settings);
        AddSystemMessage(_aiDialogueEnabled
            ? "💬  Multi-round dialogue enabled - AIs will reply to each other after the first response."
            : "💬  Multi-round dialogue disabled - each AI responds once per message.");
    }

    /// <summary>Refreshes the 💬 button's visual state to match _aiDialogueEnabled.</summary>
    private void UpdateAiDialogueButton()
    {
        if (_aiDialogueEnabled)
        {
            AiDialogueButton.SetResourceReference(Button.BackgroundProperty, "AccentBgBrush");
            AiDialogueButton.SetResourceReference(Button.ForegroundProperty, "AccentTextBrush");
        }
        else
        {
            AiDialogueButton.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
            AiDialogueButton.SetResourceReference(Button.ForegroundProperty, "ContentDimBrush");
        }
    }

    // ── Chat font ──────────────────────────────────────────────────────────

    private void ChatFontButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsService.Load();
        var allFonts = Fonts.SystemFontFamilies
                            .Select(f => f.Source)
                            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                            .ToList();

        // ── Window ────────────────────────────────────────────────────────
        var win = new Window
        {
            Title                 = "Chat Appearance",
            Width                 = 420,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ResizeMode            = ResizeMode.NoResize
        };
        ApplyThemeToDialog(win);
        win.SetResourceReference(Window.BackgroundProperty, "ContentBgBrush");

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        // ── Helper: bordered TextBox for numeric input ─────────────────────
        TextBox MakeNumBox(string initial, double width = 58)
        {
            var tb = new TextBox
            {
                Text            = initial,
                Width           = width,
                FontSize        = 13,
                FontFamily      = new FontFamily("Segoe UI"),
                TextAlignment   = TextAlignment.Center,
                BorderThickness = new Thickness(0),
                Background      = Brushes.Transparent,
                Padding         = new Thickness(4, 2, 4, 2),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            tb.SetResourceReference(TextBox.ForegroundProperty,  "InputTextBrush");
            tb.SetResourceReference(TextBox.CaretBrushProperty,  "InputTextBrush");
            return tb;
        }
        Border WrapNumBox(TextBox tb)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(6),
                Margin       = new Thickness(8, 0, 0, 0),
                Padding      = new Thickness(2, 0, 2, 0),
                Height       = 28,
                Child        = tb
            };
            b.SetResourceReference(Border.BackgroundProperty, "ControlBgBrush");
            return b;
        }
        TextBlock MakeSectionLabel(string text)
        {
            var lbl = new TextBlock
            {
                Text       = text,
                FontSize   = 12,
                FontWeight = FontWeights.SemiBold,
                Margin     = new Thickness(0, 0, 0, 6)
            };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
            return lbl;
        }

        // ── Font Family ───────────────────────────────────────────────────
        panel.Children.Add(MakeSectionLabel("Font Family"));

        var searchBox = new TextBox
        {
            Text            = "",
            FontSize        = 13,
            FontFamily      = new FontFamily("Segoe UI"),
            BorderThickness = new Thickness(0),
            Background      = Brushes.Transparent,
            Padding         = new Thickness(0, 2, 0, 2)
        };
        searchBox.SetResourceReference(TextBox.ForegroundProperty, "InputTextBrush");
        searchBox.SetResourceReference(TextBox.CaretBrushProperty, "InputTextBrush");
        var searchBorder = new Border
        {
            CornerRadius    = new CornerRadius(8), Height = 34,
            BorderThickness = new Thickness(1),
            Margin          = new Thickness(0, 0, 0, 6),
            Padding         = new Thickness(10, 0, 10, 0),
            Child           = searchBox
        };
        searchBorder.SetResourceReference(Border.BackgroundProperty,  "InputBgBrush");
        searchBorder.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush");
        panel.Children.Add(searchBorder);

        var fontList = new ListBox
        {
            Height          = 150,
            BorderThickness = new Thickness(0),
            Margin          = new Thickness(0, 0, 0, 18)
        };
        fontList.SetResourceReference(ListBox.BackgroundProperty, "InputBgBrush");
        fontList.SetResourceReference(ListBox.ForegroundProperty, "ContentTextBrush");
        panel.Children.Add(fontList);

        void RefreshFontList(string filter)
        {
            fontList.Items.Clear();
            foreach (var f in allFonts.Where(f => f.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                fontList.Items.Add(f);
            var current = settings.ChatFontFamily ?? "Segoe UI";
            fontList.SelectedItem = fontList.Items.Cast<string>()
                .FirstOrDefault(f => f.Equals(current, StringComparison.OrdinalIgnoreCase))
                ?? fontList.Items.Cast<string>().FirstOrDefault();
            fontList.ScrollIntoView(fontList.SelectedItem);
        }
        RefreshFontList("");
        searchBox.TextChanged += (_, _) => RefreshFontList(searchBox.Text);

        // ── Font Size ─────────────────────────────────────────────────────
        panel.Children.Add(MakeSectionLabel("Font Size  (pt)"));

        var sizeTb     = MakeNumBox($"{settings.ChatFontSize:0.#}");
        var sizeTbWrap = WrapNumBox(sizeTb);
        var sizeSlider = new Slider
        {
            Minimum             = 9,
            Maximum             = 128,
            Value               = settings.ChatFontSize,
            TickFrequency       = 1,
            IsSnapToTickEnabled = false,
            VerticalAlignment   = VerticalAlignment.Center
        };
        var sizeRow = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(sizeSlider, 0);
        Grid.SetColumn(sizeTbWrap, 1);
        sizeRow.Children.Add(sizeSlider);
        sizeRow.Children.Add(sizeTbWrap);
        panel.Children.Add(sizeRow);

        // ── Bubble Width ──────────────────────────────────────────────────
        panel.Children.Add(MakeSectionLabel("Bubble Width  (% of chat area)"));

        var widthTb     = MakeNumBox($"{_chatBubbleWidthPct:0}");
        var widthTbWrap = WrapNumBox(widthTb);
        var widthSlider = new Slider
        {
            Minimum             = 30,
            Maximum             = 100,
            Value               = _chatBubbleWidthPct,
            TickFrequency       = 1,
            IsSnapToTickEnabled = true,
            VerticalAlignment   = VerticalAlignment.Center
        };
        var widthRow = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        widthRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        widthRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(widthSlider, 0);
        Grid.SetColumn(widthTbWrap, 1);
        widthRow.Children.Add(widthSlider);
        widthRow.Children.Add(widthTbWrap);
        panel.Children.Add(widthRow);

        // ── Preview ───────────────────────────────────────────────────────
        panel.Children.Add(MakeSectionLabel("Preview"));
        var previewTb = new TextBlock
        {
            Text         = "The quick brown fox jumps over the lazy dog.\n0123456789  !@#$%",
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 20),
            FontFamily   = new FontFamily(settings.ChatFontFamily),
            FontSize     = settings.ChatFontSize
        };
        previewTb.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        panel.Children.Add(previewTb);

        // ── Live wiring ───────────────────────────────────────────────────
        bool _updating = false;

        void ApplyFont()
        {
            var family = fontList.SelectedItem as string ?? settings.ChatFontFamily;
            var size   = Math.Clamp(sizeSlider.Value, 9, 128);
            previewTb.FontFamily        = new FontFamily(family);
            previewTb.FontSize          = size;
            Resources["ChatFontFamily"] = new FontFamily(family);
            Resources["ChatFontSize"]   = size;
        }

        void ApplyWidth()
        {
            var pct = Math.Clamp(widthSlider.Value, 30, 100);
            _chatBubbleWidthPct = pct;
            UpdateChatBubbleWidth();
        }

        // Slider → TextBox
        sizeSlider.ValueChanged += (_, _) =>
        {
            if (_updating) return;
            _updating = true;
            sizeTb.Text = $"{sizeSlider.Value:0.#}";
            _updating = false;
            ApplyFont();
        };
        widthSlider.ValueChanged += (_, _) =>
        {
            if (_updating) return;
            _updating = true;
            widthTb.Text = $"{widthSlider.Value:0}";
            _updating = false;
            ApplyWidth();
        };

        // TextBox → Slider
        sizeTb.TextChanged += (_, _) =>
        {
            if (_updating) return;
            if (double.TryParse(sizeTb.Text, out var v) && v is >= 9 and <= 128)
            {
                _updating = true;
                sizeSlider.Value = v;
                _updating = false;
                ApplyFont();
            }
        };
        widthTb.TextChanged += (_, _) =>
        {
            if (_updating) return;
            if (double.TryParse(widthTb.Text, out var v) && v is >= 30 and <= 100)
            {
                _updating = true;
                widthSlider.Value = v;
                _updating = false;
                ApplyWidth();
            }
        };

        fontList.SelectionChanged += (_, _) => ApplyFont();

        // ── Close ─────────────────────────────────────────────────────────
        var closeBtn = new Button
        {
            Content             = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding             = new Thickness(20, 8, 20, 8)
        };
        closeBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        closeBtn.SetResourceReference(Button.ForegroundProperty, "ControlTextBrush");
        closeBtn.Style = (Style)FindResource("ModernButton");
        closeBtn.Click += (_, _) =>
        {
            var s = SettingsService.Load();
            s.ChatFontFamily         = fontList.SelectedItem as string ?? s.ChatFontFamily;
            s.ChatFontSize           = sizeSlider.Value;
            s.ChatBubbleWidthPercent = widthSlider.Value;
            SettingsService.Save(s);
            win.Close();
        };
        panel.Children.Add(closeBtn);

        win.Content = panel;
        win.ShowDialog();
    }

    // ── About / Version ────────────────────────────────────────────────────

    private void ShowAboutInfoDialog()
    {
        var win = new Window
        {
            Title                 = "About ClaudetRelay",
            Width                 = 360,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            Background            = (Brush)FindResource("ContentBgBrush"),
            ResizeMode            = ResizeMode.NoResize
        };
        ApplyThemeToDialog(win);

        var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };

        panel.Children.Add(new TextBlock
        {
            Text = "ClaudetRelay", FontSize = 22, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("ContentTextBrush"),
            Margin = new Thickness(0, 0, 0, 4)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Multi-AI group chat relay",
            FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("ContentDimBrush"),
            Margin = new Thickness(0, 0, 0, 20)
        });
        panel.Children.Add(new Rectangle
        {
            Height = 1, Fill = (Brush)FindResource("ControlBgBrush"),
            Margin = new Thickness(0, 0, 0, 20)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "by H.-R. Matthes and Claude Code",
            FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("ContentTextBrush"),
            Margin = new Thickness(0, 0, 0, 20)
        });

        var closeBtn = new Button
        {
            Content = "Close", IsDefault = true,
            Height = 34, Padding = new Thickness(20, 0, 20, 0),
            Style = (Style)FindResource("ModernButton"),
            Background = (Brush)FindResource("PrimaryAccentBrush"),
            Foreground = (Brush)FindResource("AccentTextBrush"),
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
        // asm.Location is always empty in single-file publish - use the EXE instead
        var exePath = Environment.ProcessPath ?? AppDomain.CurrentDomain.BaseDirectory;
        var built   = SysIO.File.GetLastWriteTime(exePath);

        var win = new Window
        {
            Title                 = "Version",
            Width                 = 300,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            Background            = (Brush)FindResource("ContentBgBrush"),
            ResizeMode            = ResizeMode.NoResize
        };
        ApplyThemeToDialog(win);

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        panel.Children.Add(new TextBlock
        {
            Text = "ClaudetRelay", FontSize = 16, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("ContentTextBrush"),
            Margin = new Thickness(0, 0, 0, 10)
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Version {verStr}",
            FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("ContentTextBrush"),
            Margin = new Thickness(0, 0, 0, 4)
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Build  {built:yyyy-MM-dd}",
            FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("ContentDimBrush"),
            Margin = new Thickness(0, 0, 0, 18)
        });

        var closeBtn = new Button
        {
            Content = "Close", IsDefault = true,
            Height = 32, Padding = new Thickness(16, 0, 16, 0),
            Style = (Style)FindResource("ModernButton"),
            Background = (Brush)FindResource("ControlBgBrush"),
            Foreground = (Brush)FindResource("ContentTextBrush"),
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

    // ── Cloud AI participant management ────────────────────────────────────

    private void AddCloudAIParticipant(string provider, string model = "", string customName = "")
    {
        if (_cloudAIParticipants.Count >= 20) return;

        var apiKey = WindowsCredentialManager.Load(provider) ?? "";
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
        RefreshWelcomeHint();
    }

    /// <summary>Shows the welcome hint when there are no active participants, hides it otherwise.</summary>
    private void RefreshWelcomeHint()
    {
        WelcomeHint.Visibility = (_ollamaParticipants.Count + _cloudAIParticipants.Count) == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RemoveCloudAIParticipant(CloudAIParticipantUI ui)
    {
        ParticipantsPanel.Children.Remove(ui.Popup);
        ParticipantsPanel.Children.Remove(ui.Card);
        ui.Data.Service.Dispose();
        _cloudAIParticipants.Remove(ui);
        RenumberCloudAIParticipants();
        UpdateCloudAIAddRemoveButtons();
        RefreshWelcomeHint();
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
        avatarText.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");

        var avatarBorder = new Border
        {
            Width        = 34, Height = 34,
            CornerRadius = new CornerRadius(9),
            Child        = avatarText
        };
        avatarBorder.SetResourceReference(Border.BackgroundProperty, participant.ColorKey);

        // ── Role badges - outline-only tags, no fill background ──
        var coBadge = new Border
        {
            CornerRadius    = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 1, 4, 1),
            Margin          = new Thickness(0, 0, 3, 0),
            Background      = Brushes.Transparent,
            Visibility      = Visibility.Collapsed,
            Child           = new TextBlock { Text = "CO", FontSize = 8, FontWeight = FontWeights.Normal,
                                  VerticalAlignment = VerticalAlignment.Center }
        };
        coBadge.SetResourceReference(Border.BorderBrushProperty, "AccentBgBrush");
        ((TextBlock)coBadge.Child).SetResourceReference(TextBlock.ForegroundProperty, "AccentBgBrush");

        var rBadge = new Border
        {
            CornerRadius    = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 1, 4, 1),
            Margin          = new Thickness(0, 0, 3, 0),
            Background      = Brushes.Transparent,
            Visibility      = Visibility.Collapsed,
            Child           = new TextBlock { Text = "R", FontSize = 8, FontWeight = FontWeights.Normal,
                                  VerticalAlignment = VerticalAlignment.Center }
        };
        rBadge.SetResourceReference(Border.BorderBrushProperty, "ContentDimBrush");
        ((TextBlock)rBadge.Child).SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var crBadge = new Border
        {
            CornerRadius    = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 1, 4, 1),
            Margin          = new Thickness(0, 0, 3, 0),
            Background      = Brushes.Transparent,
            Visibility      = Visibility.Collapsed,
            Child           = new TextBlock { Text = "CR", FontSize = 8, FontWeight = FontWeights.Normal,
                                  VerticalAlignment = VerticalAlignment.Center }
        };
        crBadge.SetResourceReference(Border.BorderBrushProperty, "ContentDimBrush");
        ((TextBlock)crBadge.Child).SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var plBadge = new Border
        {
            CornerRadius    = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 1, 4, 1),
            Margin          = new Thickness(0, 0, 3, 0),
            Background      = Brushes.Transparent,
            Visibility      = Visibility.Collapsed,
            Child           = new TextBlock { Text = "PL", FontSize = 8, FontWeight = FontWeights.Normal,
                                  VerticalAlignment = VerticalAlignment.Center }
        };
        plBadge.SetResourceReference(Border.BorderBrushProperty, "ContentDimBrush");
        ((TextBlock)plBadge.Child).SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var rsBadge = new Border
        {
            CornerRadius    = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 1, 4, 1),
            Margin          = new Thickness(0, 0, 3, 0),
            Background      = Brushes.Transparent,
            Visibility      = Visibility.Collapsed,
            Child           = new TextBlock { Text = "RS", FontSize = 8, FontWeight = FontWeights.Normal,
                                  VerticalAlignment = VerticalAlignment.Center }
        };
        rsBadge.SetResourceReference(Border.BorderBrushProperty, "ContentDimBrush");
        ((TextBlock)rsBadge.Child).SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var wrBadge = new Border
        {
            CornerRadius    = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 1, 4, 1),
            Margin          = new Thickness(0, 0, 3, 0),
            Background      = Brushes.Transparent,
            Visibility      = Visibility.Collapsed,
            Child           = new TextBlock { Text = "WR", FontSize = 8, FontWeight = FontWeights.Normal,
                                  VerticalAlignment = VerticalAlignment.Center }
        };
        wrBadge.SetResourceReference(Border.BorderBrushProperty, "SecondaryAccentBrush");
        ((TextBlock)wrBadge.Child).SetResourceReference(TextBlock.ForegroundProperty, "SecondaryAccentBrush");

        // Error badge - stays on the avatar (status indicator, not a role)
        var errorBadgeCloud = new Border
        {
            CornerRadius        = new CornerRadius(3),
            Padding             = new Thickness(3, 0, 3, 0),
            Height              = 13,
            Background          = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Bottom,
            Visibility          = Visibility.Collapsed,
            Child               = new TextBlock
                                  {
                                      Text                = "!",
                                      FontSize            = 9,
                                      FontWeight          = FontWeights.Bold,
                                      Foreground          = new SolidColorBrush(Color.FromRgb(255, 220, 0)),
                                      HorizontalAlignment = HorizontalAlignment.Center,
                                      VerticalAlignment   = VerticalAlignment.Center
                                  }
        };

        // Avatar only holds the circle and the error indicator - role badges moved to row below
        var avatarContainer = new Grid
        {
            Width             = 38, Height = 38,
            Margin            = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        avatarContainer.Children.Add(avatarBorder);
        avatarContainer.Children.Add(errorBadgeCloud);

        // ── Status dot ────────────────────────────────────────────────────
        var statusDot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        statusDot.SetResourceReference(Ellipse.FillProperty, "ContentDimBrush");

        // ── Labels ────────────────────────────────────────────────────────
        var nameLabel = new TextBlock
        {
            Text       = participant.DisplayName,
            FontSize   = 13,
            FontWeight = FontWeights.SemiBold
        };
        nameLabel.SetResourceReference(TextBlock.ForegroundProperty, "ControlTextBrush");

        var modelLabel = new TextBlock
        {
            Text    = FormatModelDisplayName(participant.Service.CurrentModel),
            FontSize = 10
        };
        modelLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var offlineLabel = new TextBlock { Text = "Offline", FontSize = 10, Visibility = Visibility.Collapsed };
        offlineLabel.SetResourceReference(TextBlock.ForegroundProperty, "AccentBgBrush");

        var statusLabelCloud = new TextBlock { FontSize = 10, Visibility = Visibility.Collapsed };

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
        removeButton.SetResourceReference(Button.BackgroundProperty, "ControlHoverBrush");
        removeButton.SetResourceReference(Button.ForegroundProperty, "ContentDimBrush");

        // ── Layout ────────────────────────────────────────────────────────
        var labelPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labelPanel.Children.Add(nameLabel);
        labelPanel.Children.Add(modelLabel);
        labelPanel.Children.Add(offlineLabel);
        labelPanel.Children.Add(statusLabelCloud);

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

        // ── Badge row - themed pills in a horizontal strip below the main row ──
        var badgeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 5, 0, 0),
            Visibility  = Visibility.Collapsed
        };
        badgeRow.Children.Add(coBadge);
        badgeRow.Children.Add(rBadge);
        badgeRow.Children.Add(crBadge);
        badgeRow.Children.Add(plBadge);
        badgeRow.Children.Add(rsBadge);
        badgeRow.Children.Add(wrBadge);

        var cardContent = new StackPanel();
        cardContent.Children.Add(grid);
        cardContent.Children.Add(badgeRow);

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding      = new Thickness(10),
            Margin       = new Thickness(0, 0, 0, 7),
            Cursor       = Cursors.Hand,
            Child        = cardContent
        };
        card.SetResourceReference(Border.BackgroundProperty,   "ControlBgBrush");
        card.BorderThickness = new Thickness(1);
        card.SetResourceReference(Border.BorderBrushProperty,  "ControlBorderBrush");

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
        separator.SetResourceReference(Rectangle.FillProperty, "ControlBgBrush");

        var enabledToggle = new CheckBox
        {
            Style     = (Style)FindResource("ToggleSwitch"),
            IsChecked = true,
            Content   = $"{participant.DisplayName} enabled",
            Margin    = new Thickness(0, 0, 0, 14)
        };

        var infoProviderKey = new TextBlock { Text = "PROVIDER", FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 3) };
        infoProviderKey.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        var infoProviderVal = new TextBlock { Text = participant.ProviderName, FontSize = 12, Margin = new Thickness(0, 0, 0, 10) };
        infoProviderVal.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");

        var infoModelKey = new TextBlock { Text = "MODEL", FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 3) };
        infoModelKey.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        var infoModelVal = new TextBlock { Text = FormatModelDisplayName(participant.Service.CurrentModel), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        infoModelVal.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");

        var settingsLink = new Button
        {
            Content    = "⚙  Roles & Properties…",
            Margin     = new Thickness(0, 12, 0, 0),
            Padding    = new Thickness(10, 5, 10, 5),
            FontSize   = 12,
            FontFamily = new FontFamily("Segoe UI"),
            Cursor     = Cursors.Hand
        };
        settingsLink.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        settingsLink.SetResourceReference(Button.ForegroundProperty, "ContentTextBrush");
        settingsLink.SetResourceReference(Button.BorderBrushProperty, "ControlBorderBrush");
        settingsLink.Click += (_, _) =>
        {
            // popup is declared below - safe to capture; lambda runs after assignment
            if (_currentProjectFolder is not null)
                ShowProjectSettingsDialog(_currentProjectFolder,
                    _currentProject?.ProjectName ?? "");
        };

        var popupContent = new StackPanel();
        popupContent.Children.Add(popupTitle);
        popupContent.Children.Add(separator);
        popupContent.Children.Add(enabledToggle);
        popupContent.Children.Add(infoProviderKey);
        popupContent.Children.Add(infoProviderVal);
        popupContent.Children.Add(infoModelKey);
        popupContent.Children.Add(infoModelVal);
        popupContent.Children.Add(settingsLink);

        var popupBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(12),
            Padding         = new Thickness(14),
            MinWidth        = 230,
            Child           = popupContent,
            Effect          = new DropShadowEffect { Color = Colors.Black, Opacity = 0.45, BlurRadius = 22, ShadowDepth = 4 }
        };
        popupBorder.SetResourceReference(Border.BackgroundProperty,  "SidebarBgBrush");
        popupBorder.SetResourceReference(Border.BorderBrushProperty, "ControlHoverBrush");

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
            CrBadge       = crBadge,
            PlBadge       = plBadge,
            RsBadge       = rsBadge,
            WrBadge       = wrBadge,
            BadgeRow      = badgeRow,
            NameLabel     = nameLabel,
            StatusDot     = statusDot,
            ModelLabel    = modelLabel,
            OfflineLabel  = offlineLabel,
            ErrorBadge    = errorBadgeCloud,
            StatusLabel   = statusLabelCloud,
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

        ParticipantsPanel.Children.Add(popup);
        ParticipantsPanel.Children.Add(card);
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
        RefreshWelcomeHint();
    }

    private void RemoveOllamaParticipant(OllamaParticipantUI ui)
    {
        ParticipantsPanel.Children.Remove(ui.Popup);
        ParticipantsPanel.Children.Remove(ui.Card);
        _ollamaParticipants.Remove(ui);

        RenumberParticipants();
        UpdateAddRemoveButtons();
        RefreshWelcomeHint();
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
        // Always show remove - last participant can be removed to restore the welcome hint.
        foreach (var ui in _ollamaParticipants)
            ui.RemoveButton.Visibility = Visibility.Visible;
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
        avatarText.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");

        var avatarBorder = new Border
        {
            Width        = 34, Height = 34,
            CornerRadius = new CornerRadius(9),
            Child        = avatarText
        };
        avatarBorder.SetResourceReference(Border.BackgroundProperty, participant.ColorKey);

        // ── Role badges - outline-only tags, no fill background ──
        var coBadge = new Border
        {
            CornerRadius    = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 1, 4, 1),
            Margin          = new Thickness(0, 0, 3, 0),
            Background      = Brushes.Transparent,
            Visibility      = Visibility.Collapsed,
            Child           = new TextBlock { Text = "CO", FontSize = 8, FontWeight = FontWeights.Normal,
                                  VerticalAlignment = VerticalAlignment.Center }
        };
        coBadge.SetResourceReference(Border.BorderBrushProperty, "AccentBgBrush");
        ((TextBlock)coBadge.Child).SetResourceReference(TextBlock.ForegroundProperty, "AccentBgBrush");

        var rBadge = new Border
        {
            CornerRadius    = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 1, 4, 1),
            Margin          = new Thickness(0, 0, 3, 0),
            Background      = Brushes.Transparent,
            Visibility      = Visibility.Collapsed,
            Child           = new TextBlock { Text = "R", FontSize = 8, FontWeight = FontWeights.Normal,
                                  VerticalAlignment = VerticalAlignment.Center }
        };
        rBadge.SetResourceReference(Border.BorderBrushProperty, "ContentDimBrush");
        ((TextBlock)rBadge.Child).SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var crBadge = new Border
        {
            CornerRadius    = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 1, 4, 1),
            Margin          = new Thickness(0, 0, 3, 0),
            Background      = Brushes.Transparent,
            Visibility      = Visibility.Collapsed,
            Child           = new TextBlock { Text = "CR", FontSize = 8, FontWeight = FontWeights.Normal,
                                  VerticalAlignment = VerticalAlignment.Center }
        };
        crBadge.SetResourceReference(Border.BorderBrushProperty, "ContentDimBrush");
        ((TextBlock)crBadge.Child).SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var plBadge = new Border
        {
            CornerRadius    = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 1, 4, 1),
            Margin          = new Thickness(0, 0, 3, 0),
            Background      = Brushes.Transparent,
            Visibility      = Visibility.Collapsed,
            Child           = new TextBlock { Text = "PL", FontSize = 8, FontWeight = FontWeights.Normal,
                                  VerticalAlignment = VerticalAlignment.Center }
        };
        plBadge.SetResourceReference(Border.BorderBrushProperty, "ContentDimBrush");
        ((TextBlock)plBadge.Child).SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var rsBadge = new Border
        {
            CornerRadius    = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 1, 4, 1),
            Margin          = new Thickness(0, 0, 3, 0),
            Background      = Brushes.Transparent,
            Visibility      = Visibility.Collapsed,
            Child           = new TextBlock { Text = "RS", FontSize = 8, FontWeight = FontWeights.Normal,
                                  VerticalAlignment = VerticalAlignment.Center }
        };
        rsBadge.SetResourceReference(Border.BorderBrushProperty, "ContentDimBrush");
        ((TextBlock)rsBadge.Child).SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var wrBadge = new Border
        {
            CornerRadius    = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 1, 4, 1),
            Margin          = new Thickness(0, 0, 3, 0),
            Background      = Brushes.Transparent,
            Visibility      = Visibility.Collapsed,
            Child           = new TextBlock { Text = "WR", FontSize = 8, FontWeight = FontWeights.Normal,
                                  VerticalAlignment = VerticalAlignment.Center }
        };
        wrBadge.SetResourceReference(Border.BorderBrushProperty, "SecondaryAccentBrush");
        ((TextBlock)wrBadge.Child).SetResourceReference(TextBlock.ForegroundProperty, "SecondaryAccentBrush");

        // Avatar only holds the circle and the error indicator - role badges moved to row below
        var avatarContainer = new Grid
        {
            Width             = 38, Height = 38,
            Margin            = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        avatarContainer.Children.Add(avatarBorder);

        // Error badge - black background, yellow !, bottom-center of avatar
        var errorBadgeOllama = new Border
        {
            CornerRadius        = new CornerRadius(3),
            Padding             = new Thickness(3, 0, 3, 0),
            Height              = 13,
            Background          = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Bottom,
            Visibility          = Visibility.Collapsed,
            Child               = new TextBlock
                                  {
                                      Text                = "!",
                                      FontSize            = 9,
                                      FontWeight          = FontWeights.Bold,
                                      Foreground          = new SolidColorBrush(Color.FromRgb(255, 220, 0)),
                                      HorizontalAlignment = HorizontalAlignment.Center,
                                      VerticalAlignment   = VerticalAlignment.Center
                                  }
        };
        avatarContainer.Children.Add(errorBadgeOllama);

        var statusDot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        statusDot.SetResourceReference(Ellipse.FillProperty, "ContentDimBrush");

        var nameLabel = new TextBlock { Text = displayName, FontSize = 13, FontWeight = FontWeights.SemiBold };
        nameLabel.SetResourceReference(TextBlock.ForegroundProperty, "ControlTextBrush");

        var modelLabel = new TextBlock { Text = "checking...", FontSize = 10 };
        modelLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var offlineLabel = new TextBlock { Text = "Offline", FontSize = 10, Visibility = Visibility.Collapsed };
        offlineLabel.SetResourceReference(TextBlock.ForegroundProperty, "AccentBgBrush");

        var statusLabelOllama = new TextBlock { FontSize = 10, Visibility = Visibility.Collapsed };

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
        removeButton.SetResourceReference(Button.BackgroundProperty, "ControlHoverBrush");
        removeButton.SetResourceReference(Button.ForegroundProperty, "ContentDimBrush");

        var labelPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labelPanel.Children.Add(nameLabel);
        labelPanel.Children.Add(modelLabel);
        labelPanel.Children.Add(offlineLabel);
        labelPanel.Children.Add(statusLabelOllama);

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

        // ── Badge row - themed pills in a horizontal strip below the main row ──
        var badgeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 5, 0, 0),
            Visibility  = Visibility.Collapsed
        };
        badgeRow.Children.Add(coBadge);
        badgeRow.Children.Add(rBadge);
        badgeRow.Children.Add(crBadge);
        badgeRow.Children.Add(plBadge);
        badgeRow.Children.Add(rsBadge);
        badgeRow.Children.Add(wrBadge);

        var cardContent = new StackPanel();
        cardContent.Children.Add(grid);
        cardContent.Children.Add(badgeRow);

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding      = new Thickness(10),
            Margin       = new Thickness(0, 0, 0, 7),
            Cursor       = Cursors.Hand,
            Child        = cardContent
        };
        card.SetResourceReference(Border.BackgroundProperty,   "ControlBgBrush");
        card.BorderThickness = new Thickness(1);
        card.SetResourceReference(Border.BorderBrushProperty,  "ControlBorderBrush");

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
        separator.SetResourceReference(Rectangle.FillProperty, "ControlBgBrush");

        var enabledToggle = new CheckBox
        {
            Style     = (Style)FindResource("ToggleSwitch"),
            IsChecked = true,
            Content   = $"{displayName} enabled",
            Margin    = new Thickness(0, 0, 0, 14)
        };

        var infoServerKey = new TextBlock { Text = "SERVER", FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 3) };
        infoServerKey.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        var infoServerVal = new TextBlock { Text = participant.Service.BaseUrl, FontSize = 12, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
        infoServerVal.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");

        var infoModelKey = new TextBlock { Text = "MODEL", FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 3) };
        infoModelKey.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        var infoModelVal = new TextBlock { Text = FormatModelDisplayName(participant.Service.CurrentModel), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        infoModelVal.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");

        var ollamaSettingsLink = new Button
        {
            Content    = "⚙  Roles & Properties…",
            Margin     = new Thickness(0, 12, 0, 0),
            Padding    = new Thickness(10, 5, 10, 5),
            FontSize   = 12,
            FontFamily = new FontFamily("Segoe UI"),
            Cursor     = Cursors.Hand
        };
        ollamaSettingsLink.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        ollamaSettingsLink.SetResourceReference(Button.ForegroundProperty, "ContentTextBrush");
        ollamaSettingsLink.SetResourceReference(Button.BorderBrushProperty, "ControlBorderBrush");
        ollamaSettingsLink.Click += (_, _) =>
        {
            if (_currentProjectFolder is not null)
                ShowProjectSettingsDialog(_currentProjectFolder,
                    _currentProject?.ProjectName ?? "");
        };

        var popupContent = new StackPanel();
        popupContent.Children.Add(popupTitle);
        popupContent.Children.Add(separator);
        popupContent.Children.Add(enabledToggle);
        popupContent.Children.Add(infoServerKey);
        popupContent.Children.Add(infoServerVal);
        popupContent.Children.Add(infoModelKey);
        popupContent.Children.Add(infoModelVal);
        popupContent.Children.Add(ollamaSettingsLink);

        var popupBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(12),
            Padding         = new Thickness(14),
            MinWidth        = 230,
            Child           = popupContent,
            Effect          = new DropShadowEffect { Color = Colors.Black, Opacity = 0.45, BlurRadius = 22, ShadowDepth = 4 }
        };
        popupBorder.SetResourceReference(Border.BackgroundProperty,  "SidebarBgBrush");
        popupBorder.SetResourceReference(Border.BorderBrushProperty, "ControlHoverBrush");

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
            CrBadge       = crBadge,
            PlBadge       = plBadge,
            RsBadge       = rsBadge,
            WrBadge       = wrBadge,
            BadgeRow      = badgeRow,
            NameLabel     = nameLabel,
            StatusDot     = statusDot,
            ModelLabel    = modelLabel,
            OfflineLabel  = offlineLabel,
            ErrorBadge    = errorBadgeOllama,
            StatusLabel   = statusLabelOllama,
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

        ParticipantsPanel.Children.Add(popup);
        ParticipantsPanel.Children.Add(card);
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
        timer.Tick += async (_, _) =>
        {
            try { await CheckAllStatusAsync(); }
            catch { /* status check must never crash the app via async void */ }
        };
        timer.Start();
    }

    private async Task CheckAllStatusAsync()
    {
        // Snapshots taken before any await so that ReInitializeParticipants (which removes
        // and recreates participant objects) can't cause "collection was modified" exceptions.
        // The try-catch protects against the race where the old UI objects are removed from
        // the visual tree between the snapshot and the status-update continuation — e.g. when
        // the user closes ParticipantsWindow while the timer's status check is in flight.
        try
        {

        var ollamaSnapshot  = _ollamaParticipants .ToList();
        var cloudAISnapshot = _cloudAIParticipants.ToList();

        if (ollamaSnapshot.Count > 0)
        {
            bool wasOnlineBefore = ollamaSnapshot[0].Data.IsOnline == true;
            var  ollamaOnline    = await ollamaSnapshot[0].Data.Service.IsAvailableAsync();

            foreach (var ui in ollamaSnapshot)
                ApplyOllamaParticipantStatus(ui, ollamaOnline);

            if (ollamaOnline && !wasOnlineBefore)
                await LoadOllamaModelsAsync();
        }

        foreach (var ui in cloudAISnapshot)
        {
            var online = await ui.Data.Service.IsAvailableAsync();
            ApplyCloudAIParticipantStatus(ui, online);
        }

        } catch { /* UI objects from a previous participant set may have been removed;
                     swallow silently — next timer tick will use the fresh snapshot */ }
    }

    private void ApplyOllamaParticipantStatus(OllamaParticipantUI ui, bool online)
    {
        bool changed = ui.Data.IsOnline != online;
        ui.Data.IsOnline = online;

        ui.StatusDot.SetResourceReference(Ellipse.FillProperty, online ? "SecondaryAccentBrush" : "AccentBgBrush");
        ui.OfflineLabel.Visibility = online ? Visibility.Collapsed : Visibility.Visible;
        ui.ModelLabel.Visibility   = online ? Visibility.Visible   : Visibility.Collapsed;

        if (online)
        {
            // Only set "Ready" if there is no active error badge - don't overwrite a live error
            if (ui.ErrorBadge.Visibility == Visibility.Collapsed)
            {
                ui.StatusLabel.Text       = !string.IsNullOrWhiteSpace(ui.Data.Mood) ? ui.Data.Mood : "Ready";
                ui.StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(100, 190, 100));
                ui.StatusLabel.Visibility = Visibility.Visible;
            }
        }
        else
        {
            // Offline: hide status and error badge (offline label takes over)
            ui.StatusLabel.Visibility = Visibility.Collapsed;
            ui.ErrorBadge.Visibility  = Visibility.Collapsed;
        }

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

        ui.StatusDot.SetResourceReference(Ellipse.FillProperty, online ? "SecondaryAccentBrush" : "AccentBgBrush");
        ui.OfflineLabel.Visibility = online ? Visibility.Collapsed : Visibility.Visible;
        ui.ModelLabel.Visibility   = online ? Visibility.Visible   : Visibility.Collapsed;

        if (online)
        {
            ui.ModelLabel.Text = FormatModelDisplayName(ui.Data.Service.CurrentModel);
            // Only set "Ready" if there is no active error badge - don't overwrite a live error
            if (ui.ErrorBadge.Visibility == Visibility.Collapsed)
            {
                ui.StatusLabel.Text       = !string.IsNullOrWhiteSpace(ui.Data.Mood) ? ui.Data.Mood : "Ready";
                ui.StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(100, 190, 100));
                ui.StatusLabel.Visibility = Visibility.Visible;
            }
        }
        else
        {
            // Offline: hide status and error badge (offline label takes over)
            ui.StatusLabel.Visibility = Visibility.Collapsed;
            ui.ErrorBadge.Visibility  = Visibility.Collapsed;
        }

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

    // ── Error state helpers ────────────────────────────────────────────────

    /// <summary>
    /// Shows or clears the error badge and status label on a participant card.
    /// Pass null to clear (shows "Ready"); pass an error text like "ERROR" or "Wants Money"
    /// to show the yellow/black error badge with the given status text.
    /// </summary>
    private static void ApplyErrorState(Border badge, TextBlock label, string? errorText)
    {
        if (string.IsNullOrEmpty(errorText))
        {
            badge.Visibility   = Visibility.Collapsed;
            label.Text         = "Ready";
            label.Foreground   = new SolidColorBrush(Color.FromRgb(100, 190, 100));
            label.Visibility   = Visibility.Visible;
        }
        else
        {
            badge.Visibility   = Visibility.Visible;
            label.Text         = errorText;
            label.Foreground   = errorText.Contains("Money")
                ? new SolidColorBrush(Colors.Orange)
                : new SolidColorBrush(Colors.OrangeRed);
            label.Visibility   = Visibility.Visible;
        }
    }

    private void SetParticipantError(OllamaParticipantUI ui, string? errorText)
    {
        ApplyErrorState(ui.ErrorBadge, ui.StatusLabel, errorText);
        // After clearing an error, restore the mood word if we have one
        if (string.IsNullOrEmpty(errorText) && !string.IsNullOrWhiteSpace(ui.Data.Mood))
        {
            ui.StatusLabel.Text = ui.Data.Mood;
        }
    }

    private void SetParticipantError(CloudAIParticipantUI ui, string? errorText)
    {
        ApplyErrorState(ui.ErrorBadge, ui.StatusLabel, errorText);
        // After clearing an error, restore the mood word if we have one
        if (string.IsNullOrEmpty(errorText) && !string.IsNullOrWhiteSpace(ui.Data.Mood))
        {
            ui.StatusLabel.Text = ui.Data.Mood;
        }
    }

    /// <summary>
    /// Called once per completed (non-hidden, non-error) response from a participant.
    /// Increments the response counter; every 5th response fires a background mood fetch
    /// and updates the participant's status label with the returned word.
    /// </summary>
    private void OnParticipantResponded(OllamaParticipantUI ui)
    {
        ui.Data.ResponseCount++;
        if (ui.Data.ResponseCount % 5 != 0) return;
        var type  = "Ollama";
        var model = ui.Data.Service.CurrentModel;
        var url   = ui.Data.Service.BaseUrl;
        SelfDescriptionService.FetchMoodAsync(type, model, url)
            .ContinueWith(t =>
            {
                var mood = t.Result;
                if (string.IsNullOrWhiteSpace(mood)) return;
                ui.Data.Mood = mood;
                Dispatcher.InvokeAsync(() =>
                {
                    if (ui.ErrorBadge.Visibility == Visibility.Collapsed)
                    {
                        ui.StatusLabel.Text       = mood;
                        ui.StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(100, 190, 100));
                        ui.StatusLabel.Visibility = Visibility.Visible;
                    }
                });
            }, TaskScheduler.Default);
    }

    private void OnParticipantResponded(CloudAIParticipantUI ui)
    {
        ui.Data.ResponseCount++;
        if (ui.Data.ResponseCount % 5 != 0) return;
        var type  = ui.Data.Service.ProviderName;
        var model = ui.Data.Service.CurrentModel;
        SelfDescriptionService.FetchMoodAsync(type, model, serverUrl: "")
            .ContinueWith(t =>
            {
                var mood = t.Result;
                if (string.IsNullOrWhiteSpace(mood)) return;
                ui.Data.Mood = mood;
                Dispatcher.InvokeAsync(() =>
                {
                    if (ui.ErrorBadge.Visibility == Visibility.Collapsed)
                    {
                        ui.StatusLabel.Text       = mood;
                        ui.StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(100, 190, 100));
                        ui.StatusLabel.Visibility = Visibility.Visible;
                    }
                });
            }, TaskScheduler.Default);
    }

    /// <summary>
    /// Injects a system-level notification into the shared history so the coordinator
    /// sees that a participant is currently unavailable.
    /// </summary>
    private void NotifyCoordinatorOfError(string participantName, string errorType)
    {
        if (!HasCoordinatorRole()) return;
        _sharedHistory.Add(new CloudAIMessage(
            "user",
            $"[SYSTEM: {participantName} encountered an error ({errorType}). " +
            $"This participant is currently unavailable - do not wait for or delegate to them.]",
            "System"));
    }

    // ── Card refresh helpers ───────────────────────────────────────────────

    private void RefreshOllamaCard(OllamaParticipantUI ui, ParticipantConfig config)
    {
        ui.Data.Service.CurrentModel = config.Model;
        ui.Data.CustomName           = string.IsNullOrWhiteSpace(config.Name) ? null : config.Name;
        var newName = ui.Data.DisplayName;
        ui.NameLabel.Text        = newName;
        ui.ModelLabel.Text       = FormatModelDisplayName(config.Model);
        ui.AvatarText.Text       = ui.Data.AvatarLabel;
        ui.PopupTitle.Text       = newName;
        ui.EnabledToggle.Content = $"{newName} enabled";
    }

    private void RefreshCloudAICard(CloudAIParticipantUI ui, ParticipantConfig config)
    {
        ui.Data.Service.CurrentModel = config.Model;
        ui.Data.CustomName           = string.IsNullOrWhiteSpace(config.Name) ? null : config.Name;
        var newName = ui.Data.DisplayName;
        ui.NameLabel.Text        = newName;
        ui.ModelLabel.Text       = FormatModelDisplayName(config.Model);
        ui.AvatarText.Text       = ui.Data.AvatarLabel;
        ui.PopupTitle.Text       = newName;
        ui.EnabledToggle.Content = $"{newName} enabled";
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

        // ── MCP Client special entry ───────────────────────────────────────
        if (_currentProjectFolder is not null && _projectSettings is not null)
        {
            bool mcpEnabled = _projectSettings.McpChatEnabled;
            var mcpItem = new MenuItem
            {
                Header    = mcpEnabled
                    ? "🔌  MCP Client  ·  already connected"
                    : "🔌  MCP Client  ·  Allow Claude Desktop / Claude Code to chat",
                IsEnabled = !mcpEnabled
            };
            if (!mcpEnabled)
            {
                mcpItem.Click += (_, _) =>
                {
                    _projectSettings.McpChatEnabled = true;
                    ProjectService.SaveProject(_currentProjectFolder!, _projectSettings);
                    AddSystemMessage("🔌 MCP Client connected — Claude Desktop and Claude Code can now read and post to this chat via the chat_get_history and chat_post_message MCP tools.");
                };
            }
            menu.Items.Add(mcpItem);
            menu.Items.Add(new Separator());
        }
        else
        {
            var mcpGeneral = new MenuItem
            {
                Header    = "🔌  MCP Client  ·  General chat is always accessible via MCP",
                IsEnabled = false
            };
            menu.Items.Add(mcpGeneral);
            menu.Items.Add(new Separator());
        }

        foreach (var p in enabled)
        {
            // Duplicate check: if only one settings entry uses this provider+model combo,
            // match on provider+model alone — this makes the check rename-proof (live cards
            // keep the old CustomName until refreshed, so a name comparison would falsely
            // allow re-adding a renamed participant).
            // When multiple settings entries share the same provider+model (intentional
            // multi-persona setups), fall back to name comparison so each persona stays distinct.
            var effectiveName = string.IsNullOrEmpty(p.Name)
                ? FormatModelDisplayName(p.Model)
                : p.Name;

            bool alreadyAdded;
            if (p.Type == "Ollama")
            {
                bool multiPersona = enabled.Count(q => q.Type == p.Type && q.Model == p.Model && q.ServerUrl == p.ServerUrl) > 1;
                alreadyAdded = multiPersona
                    ? _ollamaParticipants.Any(ui =>
                        ui.Data.Service.CurrentModel == p.Model &&
                        ui.Data.Service.BaseUrl      == p.ServerUrl &&
                        ui.Data.DisplayName          == effectiveName)
                    : _ollamaParticipants.Any(ui =>
                        ui.Data.Service.CurrentModel == p.Model &&
                        ui.Data.Service.BaseUrl      == p.ServerUrl);
            }
            else
            {
                bool multiPersona = enabled.Count(q => q.Type == p.Type && q.Model == p.Model) > 1;
                alreadyAdded = multiPersona
                    ? _cloudAIParticipants.Any(ui =>
                        ui.Data.Service.ProviderName == p.Type &&
                        ui.Data.Service.CurrentModel == p.Model &&
                        ui.Data.DisplayName          == effectiveName)
                    : _cloudAIParticipants.Any(ui =>
                        ui.Data.Service.ProviderName == p.Type &&
                        ui.Data.Service.CurrentModel == p.Model);
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
                            AddSystemMessage($"⚠  Could not add {cap.Type} - no API key saved. Open ⋮ → Providers.");
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
                Header    = "No participants configured - open 👤 Participant Config",
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
            AddSystemMessage("⚠  Open or create a project first - dropped files go into the INPUT folder.");
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
            // Sandbox check - ensure destination stays inside project folder
            if (!ProjectService.IsPathSafe(dest, _currentProjectFolder)) continue;
            SysIO.File.Copy(file, dest, overwrite: true);
            count++;
        }

        if (count > 0)
        {
            AddSystemMessage($"🔎 {count} file(s) copied to INPUT folder.");
            // Persist current project state
            if (_currentProject is not null)
                ProjectService.SaveProject(_currentProjectFolder!, _currentProject);
        }
    }

    private void SendMessage()
    {
        var text = InputTextBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        var avatar = _userName.Length >= 2 ? _userName[..2].ToUpper() : _userName.ToUpper();
        AddMessage(_userName, avatar, "TertiaryAccentBrush", "TertiaryBubbleBrush", text, isUser: true);

        var entry = new ChatLogEntry
        {
            Timestamp   = DateTime.Now,
            SenderType  = "User",
            DisplayName = _userName,
            AvatarLabel = avatar,
            AccentKey   = "TertiaryAccentBrush",
            BubbleKey   = "TertiaryBubbleBrush",
            IsUser      = true,
            Message     = text
        };
        AppendToProjectLog(entry);
        AppendToGeneralLog(entry);

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

        // In CoordinatorFirst / Auto / Only modes the mode runner handles reasoner activation.
        // In all other modes, reasoners are completely passive - they only participate when
        // explicitly mentioned by name in the conversation (no auto-triggering).
        bool suppressReasoners = mode is not OrchestrationMode.CoordinatorFirst
                                      and not OrchestrationMode.CoordinatorAuto
                                      and not OrchestrationMode.CoordinatorOnly;

        var activeOllamas  = _ollamaParticipants
            .Where(ui => ui.Data.Enabled && ui.Data.IsOnline == true &&
                         IsParticipantActiveInProject(ui) &&
                         !(suppressReasoners && IsReasoner(ui)))
            .ToList();
        var activeCloudAIs = _cloudAIParticipants
            .Where(ui => ui.Data.Enabled && ui.Data.IsOnline == true &&
                         IsParticipantActiveInProject(ui) &&
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

        // Multi-round dialogue: toggle is the master switch.
        // When ON:  use _aiDialogueMaxTurns (global setting, 3-100), but honour a higher
        //           project MaxDialogDepth if one is explicitly configured.
        // When OFF: single round regardless of any project setting.
        bool multiParticipant = (activeOllamas.Count + activeCloudAIs.Count) > 1;
        var maxRounds = _aiDialogueEnabled && multiParticipant
            ? Math.Max(_aiDialogueMaxTurns, _maxDialogDepth)
            : 1;

        try
        {
            switch (mode)
            {
                case OrchestrationMode.CoordinatorFirst:
                case OrchestrationMode.CoordinatorAuto:   // after team init, behaves like CoordinatorFirst
                    await RunCoordinatorFirstModeAsync(activeOllamas, activeCloudAIs, ct);
                    break;
                case OrchestrationMode.CoordinatorSummarizes:
                    await RunCoordinatorSummarizesModeAsync(activeOllamas, activeCloudAIs, ct);
                    break;
                case OrchestrationMode.CoordinatorOnly:
                    await RunCoordinatorOnlyModeAsync(activeOllamas, activeCloudAIs, ct);
                    break;
                default:
                    await RunAllRespondModeAsync(activeOllamas, activeCloudAIs, ct, maxRounds);
                    break;
            }
        }
        finally
        {
            _streamCts?.Dispose();
            _streamCts = null;
            AIRespondButton.IsEnabled = true;
            SendButton.IsEnabled      = true;
        }

        // History compression - after all streams finish, outside the CTS scope
        if (_currentProjectFolder is not null && !ct.IsCancellationRequested)
            await MaybeCompressHistoryAsync(CancellationToken.None);
    }

    // ── Orchestration mode runners ─────────────────────────────────────────

    /// <summary>
    /// Hint used in follow-up rounds when inside a project.
    /// Instructs participants to contribute only if they have something genuinely new,
    /// and to output PASS otherwise - keeps structured project dialogue clean.
    /// </summary>
    private const string FollowUpRoundHint =
        "The previous participants have already responded above. " +
        "Only continue this conversation if you genuinely have something new to contribute: " +
        "a different perspective, a meaningful correction, a direct response to something just " +
        "said, or important information that has not been covered yet. " +
        "If you have nothing meaningful to add right now, output exactly the word PASS " +
        "and nothing else.";

    /// <summary>
    /// Hint used in follow-up rounds in free-chat (non-project) dialogue mode.
    /// Encourages natural, conversational back-and-forth without structured round markers.
    /// </summary>
    private const string LiveDialogueHint =
        "You are in a live group conversation. Read what the other participants just wrote " +
        "and react naturally - agree or push back on a specific point, ask a follow-up question, " +
        "share a complementary angle, make a joke, or build directly on what someone just said. " +
        "When you are addressing a specific participant, use their name. " +
        "Keep your reply conversational and concise - this is a chat, not an essay. " +
        "If you genuinely have nothing new to add right now, output exactly the word PASS and nothing else.";

    private async Task RunAllRespondModeAsync(
        List<OllamaParticipantUI>   activeOllamas,
        List<CloudAIParticipantUI>  activeCloudAIs,
        CancellationToken ct, int maxRounds)
    {
        // In a project: show "- Round N -" separators so the structure is visible.
        // In free chat (💬 dialogue mode): no markers - the messages flow as natural conversation.
        bool freeChat = _currentProjectFolder is null;

        for (int round = 0; round < maxRounds && !ct.IsCancellationRequested; round++)
        {
            bool isFollowUp = round > 0;

            if (isFollowUp)
            {
                if (_sharedHistory.Count == 0 || _sharedHistory.Last().Role != "assistant")
                    break;

                // Project mode: add a round separator that can be cleaned up if nobody responds.
                // Free-chat mode: no separator - the conversation flows without interruption.
                int markerIndex = freeChat ? -1 : ChatPanel.Children.Count;
                if (!freeChat) AddSystemMessage($"- Round {round + 1} -");

                // Choose the right follow-up hint for the context
                string hint = freeChat ? LiveDialogueHint : FollowUpRoundHint;
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

                // Nobody had anything new to say - clean up and stop
                if (responded == 0)
                {
                    if (!freeChat && markerIndex >= 0 && markerIndex < ChatPanel.Children.Count)
                        ChatPanel.Children.RemoveAt(markerIndex);
                    break;
                }
            }
            else
            {
                // Round 0 — resolve effective chattiness (project overrides global if set).
                int chattiness = (_projectSettings?.DefaultChattiness ?? -1) >= 0
                    ? _projectSettings!.DefaultChattiness
                    : _chattinessLevel;

                // Detect whether the user addressed specific participants by name.
                var lastUserMsg      = _sharedHistory.LastOrDefault(m => m.Role == "user")?.Content ?? "";
                var addressedOllamas = activeOllamas .Where(ui => IsNamedInMessage(lastUserMsg, GetEffectiveName(ui))).ToList();
                var addressedClouds  = activeCloudAIs.Where(ui => IsNamedInMessage(lastUserMsg, GetEffectiveName(ui))).ToList();
                var otherOllamas     = activeOllamas .Except(addressedOllamas).ToList();
                var otherClouds      = activeCloudAIs.Except(addressedClouds).ToList();
                bool anyAddressed    = addressedOllamas.Count + addressedClouds.Count > 0;

                if (anyAddressed)
                {
                    var addressedNames = addressedOllamas.Select(GetEffectiveName)
                        .Concat(addressedClouds.Select(GetEffectiveName)).ToList();
                    bool   isSingle  = addressedNames.Count == 1;
                    string nameList  = isSingle ? addressedNames[0] : string.Join(", ", addressedNames);

                    // Addressed participants respond naturally first
                    foreach (var ui in addressedOllamas)
                    {
                        if (ct.IsCancellationRequested) break;
                        await RunOllamaStreamAsync(ui, ct);
                    }
                    foreach (var ui in addressedClouds)
                    {
                        if (ct.IsCancellationRequested) break;
                        await RunCloudAIStreamAsync(ui, ct);
                    }

                    // Non-addressed: hint (or none) depends on chattiness
                    var notAddressedHint = BuildNotAddressedHint(chattiness, nameList, isSingle);
                    foreach (var ui in otherOllamas)
                    {
                        if (ct.IsCancellationRequested) break;
                        await RunOllamaStreamAsync(ui, ct, notAddressedHint);
                    }
                    foreach (var ui in otherClouds)
                    {
                        if (ct.IsCancellationRequested) break;
                        await RunCloudAIStreamAsync(ui, ct, notAddressedHint);
                    }
                }
                else
                {
                    // Nobody addressed — apply quiet-mode hint if chattiness is low
                    var quietHint = BuildQuietModeHint(chattiness);
                    foreach (var ui in activeOllamas)
                    {
                        if (ct.IsCancellationRequested) break;
                        await RunOllamaStreamAsync(ui, ct, quietHint);
                    }
                    foreach (var ui in activeCloudAIs)
                    {
                        if (ct.IsCancellationRequested) break;
                        await RunCloudAIStreamAsync(ui, ct, quietHint);
                    }
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
            AddSystemMessage("⚠  CoordinatorFirst: no coordinator found - falling back to AllRespond.");
            await RunAllRespondModeAsync(activeOllamas, activeCloudAIs, ct, 1);
            return;
        }

        // Split non-coordinator participants into:
        //   • free participants (IsReasoner = false) - respond automatically after the coordinator
        //   • reasoners (IsReasoner = true)          - only respond when tagged by the coordinator
        var freeOllamas      = activeOllamas .Where(u => u != coordOllama && !IsReasoner(u)).ToList();
        var freeCloudAIs     = activeCloudAIs.Where(u => u != coordCloud  && !IsReasoner(u)).ToList();
        var reasonerOllamas  = activeOllamas .Where(u => u != coordOllama &&  IsReasoner(u)).ToList();
        var reasonerCloudAIs = activeCloudAIs.Where(u => u != coordCloud  &&  IsReasoner(u)).ToList();

        var reasonerNames = reasonerOllamas.Select(GetEffectiveName)
            .Concat(reasonerCloudAIs.Select(GetEffectiveName))
            .ToList();

        var freeCount = freeOllamas.Count + freeCloudAIs.Count;

        // Do NOT list reasoners in the coordinator hint - advertising them causes reflexive tagging.
        // The coordinator naturally decides to call them by name if it genuinely needs them.
        string coordinatorHint = freeCount > 0
            ? "You respond first in this conversation round. " +
              "After your response the other active participants will also contribute."
            : "You are the only active participant - respond directly.";

        // Coordinator goes first
        if (coordCloud is not null)
            await RunCloudAIStreamAsync(coordCloud, ct, coordinatorHint);
        else
            await RunOllamaStreamAsync(coordOllama!, ct, coordinatorHint);

        if (ct.IsCancellationRequested) return;

        // Free participants respond automatically - no coordinator tagging required
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

        // Parse the coordinator's response for @Name mentions - if a reasoner is named, call them
        var coordResponse = _sharedHistory.LastOrDefault(m => m.Role == "assistant")?.Content ?? "";
        var taggedOllamas = reasonerOllamas
            .Where(u => IsTaggedInResponse(coordResponse, GetEffectiveName(u)))
            .ToList();
        var taggedClouds = reasonerCloudAIs
            .Where(u => IsTaggedInResponse(coordResponse, GetEffectiveName(u)))
            .ToList();

        if (taggedOllamas.Count == 0 && taggedClouds.Count == 0) return;

        // Tell each reasoner it was specifically delegated to - helps it stay focused
        const string reasonerDelegationHint =
            "The Coordinator has specifically delegated a task to you. " +
            "Respond only to that delegated task or question.";

        AddSystemMessage("- Delegated to Reasoners -");
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
        AddSystemMessage("- Coordinator synthesizing -");
        const string synthesisHint =
            "All other participants have now given their responses above. " +
            "Please write a final synthesizing response: draw together their key points, " +
            "highlight agreements and any meaningful differences, and add your own concluding assessment.";

        if (coordCloud is not null)
            await RunCloudAIStreamAsync(coordCloud, ct, synthesisHint);
        else
            await RunOllamaStreamAsync(coordOllama!, ct, synthesisHint);
    }

    /// <summary>
    /// Coordinator-Only mode: the user only sees the Coordinator's final synthesis.
    /// All intermediate work - Coordinator deliberation and Reasoner responses - is hidden
    /// from the chat. Small status indicators track which participant is active.
    /// <para>Flow: (1) Coordinator deliberates hidden → (2) tagged Reasoners work hidden →
    /// (3) Coordinator synthesizes visible.</para>
    /// </summary>
    private async Task RunCoordinatorOnlyModeAsync(
        List<OllamaParticipantUI>  activeOllamas,
        List<CloudAIParticipantUI> activeCloudAIs,
        CancellationToken ct)
    {
        var (coordOllama, coordCloud) = FindCoordinatorInLists(activeOllamas, activeCloudAIs);
        if (coordOllama is null && coordCloud is null)
        {
            AddSystemMessage("⚠  Coordinator-Only: no coordinator found - falling back to AllRespond.");
            await RunAllRespondModeAsync(activeOllamas, activeCloudAIs, ct, 1);
            return;
        }

        var coordName   = coordOllama is not null ? GetEffectiveName(coordOllama) : GetEffectiveName(coordCloud!);
        var coordAvatar = coordOllama?.Data.AvatarLabel ?? coordCloud!.Data.AvatarLabel;
        var coordColor  = coordOllama?.Data.ColorKey    ?? coordCloud!.Data.ColorKey;

        var reasonerOllamas  = activeOllamas .Where(u => u != coordOllama && IsReasoner(u)).ToList();
        var reasonerCloudAIs = activeCloudAIs.Where(u => u != coordCloud  && IsReasoner(u)).ToList();

        // ── Step 1: Coordinator deliberates (hidden) ───────────────────
        // The coordinator analyzes the request and decides whether / what to delegate.
        var (coordIndicator, updateCoord) = AddActivityIndicator(coordName, coordAvatar, coordColor);

        const string coordDeliberateHint =
            "COORDINATOR-ONLY MODE - INTERNAL DELIBERATION (this message is hidden from the user).\n" +
            "Analyze the request. If you can answer it fully yourself, write your analysis concisely.\n" +
            "If you need Reasoner input, mention the Reasoner(s) by name as you normally would.\n" +
            "Be concise and technical - no formatting needed here. Do NOT output PASS.";

        if (coordCloud is not null)
            await RunCloudAIStreamAsync(coordCloud, ct, coordDeliberateHint, hidden: true);
        else
            await RunOllamaStreamAsync(coordOllama!, ct, coordDeliberateHint, hidden: true);

        if (ct.IsCancellationRequested) { updateCoord("✗ cancelled"); return; }
        updateCoord($"✓  [{coordAvatar}] {coordName}  - analysis done");

        // ── Step 2: Run tagged Reasoners (hidden) ──────────────────────
        var coordFirstResponse = _sharedHistory.LastOrDefault(m => m.Role == "assistant")?.Content ?? "";

        var taggedOllamas = reasonerOllamas
            .Where(u => IsTaggedInResponse(coordFirstResponse, GetEffectiveName(u)))
            .ToList();
        var taggedClouds = reasonerCloudAIs
            .Where(u => IsTaggedInResponse(coordFirstResponse, GetEffectiveName(u)))
            .ToList();

        if (taggedOllamas.Count > 0 || taggedClouds.Count > 0)
        {
            const string reasonerHiddenHint =
                "COORDINATOR-ONLY MODE - your response is INTERNAL (not shown to user).\n" +
                "Deliver exactly what the Coordinator delegated. Be concise and technical. " +
                "No preamble, no formatting - just the result. Do NOT output PASS.";

            foreach (var ui in taggedOllamas)
            {
                if (ct.IsCancellationRequested) break;
                var (ind, upd) = AddActivityIndicator(
                    GetEffectiveName(ui), ui.Data.AvatarLabel, ui.Data.ColorKey);
                await RunOllamaStreamAsync(ui, ct, reasonerHiddenHint,
                    skipLatestUserMessage: true, hidden: true);
                upd(null); // "done"
            }
            foreach (var ui in taggedClouds)
            {
                if (ct.IsCancellationRequested) break;
                var (ind, upd) = AddActivityIndicator(
                    GetEffectiveName(ui), ui.Data.AvatarLabel, ui.Data.ColorKey);
                await RunCloudAIStreamAsync(ui, ct, reasonerHiddenHint,
                    skipLatestUserMessage: true, hidden: true);
                upd(null);
            }
        }

        if (ct.IsCancellationRequested) return;

        // ── Step 3: Coordinator synthesizes (visible to user) ──────────
        // All hidden work is now in _sharedHistory; the Coordinator sees it as context.
        const string synthHint =
            "Internal analysis complete. Write your final response DIRECTLY to the user now. " +
            "Synthesize all gathered insights into a clear, natural answer. " +
            "Do NOT mention 'internal mode', 'hidden deliberation', or the coordination process - " +
            "respond as if you arrived at the answer through your own reasoning.";

        if (coordCloud is not null)
            await RunCloudAIStreamAsync(coordCloud, ct, synthHint);
        else
            await RunOllamaStreamAsync(coordOllama!, ct, synthHint);
    }

    // ── ParticipantSuperPowers ─────────────────────────────────────────────────

    /// <summary>
    /// Sorted fingerprint of the currently enabled active participants.
    /// Used to detect team composition changes between sessions.
    /// </summary>
    private string GetParticipantFingerprint()
    {
        var keys = new List<string>();
        foreach (var ui in _ollamaParticipants.Where(u => u.Data.Enabled))
            keys.Add($"ollama:{ui.Data.Service.CurrentModel.ToLowerInvariant()}");
        foreach (var ui in _cloudAIParticipants.Where(u => u.Data.Enabled))
            keys.Add($"{ui.Data.Service.ProviderName.ToLowerInvariant()}:{ui.Data.Service.CurrentModel.ToLowerInvariant()}");
        keys.Sort(StringComparer.OrdinalIgnoreCase);
        return string.Join("|", keys);
    }

    /// <summary>
    /// Returns true if the model name matches known reasoning / thinking model patterns
    /// (o1, o3, DeepSeek-R1, Gemini-Thinking, QwQ, etc.).
    /// </summary>
    private static bool IsReasonerModel(string model)
    {
        var m = model.ToLowerInvariant();
        return Regex.IsMatch(m, @"\bo[13](-mini|-preview|-pro)?\b") ||
               m.Contains("deepseek-r1") || m.Contains("-r1-") ||
               m.Contains("thinking") ||
               m.Contains("qwq") ||
               m.Contains("reasoner");
    }

    /// <summary>Estimates the cost tier of a participant (free / low / medium / high).</summary>
    private static string GetCostTier(string provider, string model)
    {
        if (string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase))
            return "free (local)";
        var m = model.ToLowerInvariant();
        if (m.Contains("opus")  || m.Contains("ultra") ||
            (m.Contains("gpt-4o") && !m.Contains("mini")) ||
            (m.Contains("o3")     && !m.Contains("mini")))
            return "high";
        if (m.Contains("haiku") || m.Contains("flash") || m.Contains("mini") ||
            m.Contains("nano")  || m.Contains("8b")    || m.Contains("7b"))
            return "low";
        return "medium";
    }

    /// <summary>
    /// Path to this project's ParticipantSuperPowers.xaml, or null when no project is open.
    /// </summary>
    private string? GetSuperPowersPath() =>
        _currentProjectFolder is null ? null
            : SysIO.Path.Combine(_currentProjectFolder, "PROJECTSETTINGS", "ParticipantSuperPowers.xaml");

    /// <summary>Path to the AI-determined role assignments file, or null when no project is open.</summary>
    private string? GetSuperRolesPath() =>
        _currentProjectFolder is null ? null
            : SysIO.Path.Combine(_currentProjectFolder, "PROJECTSETTINGS", "ParticipantSuperRoles.xml");

    /// <summary>
    /// Parses ParticipantSuperRoles.xml and returns a dictionary keyed by participant display name.
    /// Returns null when the file is absent or unreadable.
    /// </summary>
    private Dictionary<string, (string Title, string Instruction)>? LoadSuperRoles()
    {
        var path = GetSuperRolesPath();
        if (path is null || !SysIO.File.Exists(path)) return null;
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(path);
            return doc.Root?
                .Elements("Role")
                .Where(e => e.Attribute("name")?.Value is { Length: > 0 })
                .ToDictionary(
                    e => e.Attribute("name")!.Value,
                    e => (
                        Title:       e.Attribute("title")?.Value ?? "",
                        Instruction: e.Value.Trim()),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch { return null; }
    }

    /// <summary>
    /// Returns the AI-determined role instruction for <paramref name="displayName"/>,
    /// or null if SuperRoles are unavailable or Full Manual Mode is active.
    /// The cache is loaded lazily and cleared on project open/close.
    /// </summary>
    private string? GetSuperRoleInstruction(string displayName)
    {
        // Full Manual Mode always uses checkbox-only instructions - never AI-determined roles.
        if (_projectSettings?.OrchestrationMode == OrchestrationMode.AllRespond) return null;

        _superRoles ??= LoadSuperRoles();
        return _superRoles is not null && _superRoles.TryGetValue(displayName, out var entry)
            ? entry.Instruction
            : null;
    }

    /// <summary>Reads the Fingerprint attribute from the stored SuperPowers file.</summary>
    private string? LoadStoredSuperPowersFingerprint()
    {
        var path = GetSuperPowersPath();
        if (path is null || !SysIO.File.Exists(path)) return null;
        try
        {
            return System.Xml.Linq.XDocument.Load(path)
                         .Root?.Attribute("Fingerprint")?.Value;
        }
        catch { return null; }
    }

    /// <summary>
    /// Loads the SuperPowers XAML and returns a compact text summary for injection
    /// into system prompts. Returns null when the file does not exist.
    /// </summary>
    private string? LoadSuperPowersForContext()
    {
        var path = GetSuperPowersPath();
        if (path is null || !SysIO.File.Exists(path)) return null;
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(path);
            if (doc.Root is null) return null;

            var lines = new List<string>();
            foreach (var p in doc.Root.Elements("Participant"))
            {
                var name         = p.Attribute("Name")?.Value          ?? "?";
                var role         = p.Attribute("Role")?.Value          ?? "Participant";
                var cost         = p.Attribute("CostTier")?.Value      ?? "medium";
                var isRModel     = p.Attribute("IsReasonerModel")?.Value?.ToLowerInvariant() == "true";
                var priority     = p.Attribute("ReasonerPriority")?.Value ?? "5";
                var isCritic     = p.Attribute("IsCritic")?.Value?.ToLowerInvariant()     == "true";
                var isPlanner    = p.Attribute("IsPlanner")?.Value?.ToLowerInvariant()    == "true";
                var isResearcher = p.Attribute("IsResearcher")?.Value?.ToLowerInvariant() == "true";

                // New compact attribute format (preferred)
                var strengths = p.Attribute("Strengths")?.Value?.Trim() ?? "";
                var bestFor   = p.Attribute("BestFor")?.Value?.Trim()   ?? "";
                var avoid     = p.Attribute("Avoid")?.Value?.Trim()     ?? "";

                // Build the header line
                var meta = new System.Text.StringBuilder($"{name} [{role}, cost:{cost}");
                if (isRModel)     meta.Append($", reasoner(p{priority})");
                if (isCritic)     meta.Append(", CR");
                if (isPlanner)    meta.Append(", PL");
                if (isResearcher) meta.Append(", RS");
                meta.Append(']');
                lines.Add(meta.ToString());

                if (!string.IsNullOrEmpty(strengths)) lines.Add($"  + {strengths}");
                if (!string.IsNullOrEmpty(bestFor))   lines.Add($"  ✓ {bestFor}");
                if (!string.IsNullOrEmpty(avoid))     lines.Add($"  ✗ {avoid}");

                // Legacy fallback: old files stored a prose <Description> element
                if (string.IsNullOrEmpty(strengths) && string.IsNullOrEmpty(bestFor))
                {
                    var legacyDesc = p.Element("Description")?.Value?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(legacyDesc))
                        lines.Add($"  {legacyDesc}");
                }
            }
            return lines.Count > 0 ? string.Join("\n", lines) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Triggers <see cref="TriggerSuperPowersInterviewAsync"/> only when the current
    /// participant fingerprint differs from what is stored in the SuperPowers file
    /// (or when the file does not exist). No-op if no coordinator is configured.
    /// </summary>
    private async Task CheckAndTriggerSuperPowersAsync()
    {
        if (_projectSettings is null || _currentProjectFolder is null) return;
        // Full Manual Mode has no coordinator automation - skip SuperPowers entirely.
        if (_projectSettings.OrchestrationMode == OrchestrationMode.AllRespond) return;

        // Use HasCoordinatorRole() rather than FindActiveCoordinator() so we do not
        // silently skip the interview when IsOnline is still null on fresh project open.
        // FindActiveCoordinator() requires IsOnline == true; on open that check races
        // against CheckAllStatusAsync and almost always loses → SuperPowers never fires.
        if (!HasCoordinatorRole()) return;

        var currentFp      = GetParticipantFingerprint();
        var fpMatch        = currentFp == LoadStoredSuperPowersFingerprint();
        var superRolesPath = GetSuperRolesPath();
        var rolesExist     = superRolesPath is not null && SysIO.File.Exists(superRolesPath);

        // Re-calibrate if the participant set changed OR if the SuperRoles file is missing
        // (SuperPowers without SuperRoles means the coordinator never wrote its role definitions).
        if (fpMatch && rolesExist) return;

        await TriggerSuperPowersInterviewAsync(currentFp);
    }

    /// <summary>
    /// Hidden capability interview: silently asks every participant about their strengths
    /// and weak points, then builds and saves PROJECTSETTINGS/ParticipantSuperPowers.xaml.
    /// After saving, the Coordinator gives the user a visible summary and asks for feedback.
    /// </summary>
    private async Task TriggerSuperPowersInterviewAsync(string fingerprint)
    {
        if (_projectSettings is null || _currentProjectFolder is null || _currentProject is null) return;
        if (_streamCts is not null) return;   // another stream already running

        var activeOllamas  = _ollamaParticipants
            .Where(ui => ui.Data.Enabled && IsParticipantActiveInProject(ui)).ToList();
        var activeCloudAIs = _cloudAIParticipants
            .Where(ui => ui.Data.Enabled && IsParticipantActiveInProject(ui)).ToList();

        if (activeOllamas.Count + activeCloudAIs.Count == 0) return;

        var (coordOllama, coordCloud) = FindCoordinatorInLists(activeOllamas, activeCloudAIs);
        if (coordOllama is null && coordCloud is null)
        {
            // HasCoordinatorRole() passed but we still couldn't match the coordinator to a UI
            // participant - most likely the project's ActiveParticipants list is stale or missing.
            AddSystemMessage("⚠  Coordinator role is configured but no active coordinator participant " +
                             "was found - capability profile skipped. Try reopening the project.");
            return;
        }

        AIRespondButton.IsEnabled = false;
        SendButton.IsEnabled      = false;
        _streamCts = new CancellationTokenSource();
        var ct = _streamCts.Token;

        // ── Spinner animation ─────────────────────────────────────────────────
        var spinFrames = new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
        int spinIdx  = 0;
        string spinBase = "";
        var spinTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(110) };

        try
        {
            var total    = activeOllamas.Count + activeCloudAIs.Count;
            spinBase     = $"- Calibrating team capabilities - 0 / {total}";
            var statusTb = AddUpdatableSystemMessage($"{spinBase}  {spinFrames[0]}");

            spinTimer.Tick += (_, _) =>
            {
                spinIdx = (spinIdx + 1) % spinFrames.Length;
                statusTb.Text = $"{spinBase}  {spinFrames[spinIdx]}";
            };
            spinTimer.Start();

            // ── Hidden capability assessment ──────────────────────────────────────
            // Inject a minimal context message so each participant knows what's expected.
            _sharedHistory.Add(new CloudAIMessage("user",
                "[INTERNAL] Technical capability snapshot - coordinator use only. " +
                "Each participant: reply with exactly 3 labelled lines, no prose."));

            // Strict 3-line machine-readable format - kept as short as possible so
            // the resulting SuperPowers file is lean and fast for the coordinator to parse.
            const string assessmentHint =
                "[INTERNAL] Capability snapshot for coordinator routing. " +
                "Reply with EXACTLY these 3 lines and nothing else:\n" +
                "Strengths: [comma-separated keywords, max 6]\n" +
                "Best for: [comma-separated task types, max 6]\n" +
                "Avoid: [comma-separated weaknesses, max 4]";

            const string coordSelfHint =
                "[INTERNAL] Self-assessment for capability routing file. " +
                "Reply with EXACTLY these 3 lines and nothing else:\n" +
                "Strengths: [comma-separated keywords, max 6]\n" +
                "Best for: [comma-separated task types, max 6]\n" +
                "Avoid: [comma-separated weaknesses, max 4]";

            // ── Collect profiles ──────────────────────────────────────────────────
            var profiles = new List<(string Name, string Provider, string Model, string Answer)>();
            int assessed = 0;

            // Non-coordinator participants first
            foreach (var ui in activeOllamas.Where(u => u != coordOllama))
            {
                if (ct.IsCancellationRequested) break;
                var name = GetEffectiveName(ui);
                spinBase = $"- Calibrating team capabilities - [{name}] ({assessed + 1} / {total})";
                var before = _sharedHistory.Count;
                await RunOllamaStreamAsync(ui, ct, assessmentHint, hidden: true);
                if (_sharedHistory.Count > before)
                    profiles.Add((name, "Ollama",
                                  ui.Data.Service.CurrentModel, _sharedHistory.Last().Content));
                assessed++;
            }
            foreach (var ui in activeCloudAIs.Where(u => u != coordCloud))
            {
                if (ct.IsCancellationRequested) break;
                var name = GetEffectiveName(ui);
                spinBase = $"- Calibrating team capabilities - [{name}] ({assessed + 1} / {total})";
                var before = _sharedHistory.Count;
                await RunCloudAIStreamAsync(ui, ct, assessmentHint, hidden: true);
                if (_sharedHistory.Count > before)
                    profiles.Add((name, ui.Data.Service.ProviderName,
                                  ui.Data.Service.CurrentModel, _sharedHistory.Last().Content));
                assessed++;
            }

            // Coordinator answers for themselves
            if (!ct.IsCancellationRequested)
            {
                var coordName = coordCloud is not null
                    ? GetEffectiveName(coordCloud) : GetEffectiveName(coordOllama!);
                spinBase = $"- Calibrating team capabilities - [{coordName}] ({assessed + 1} / {total})";

                var before = _sharedHistory.Count;
                if (coordCloud is not null)
                {
                    await RunCloudAIStreamAsync(coordCloud, ct, coordSelfHint, hidden: true);
                    if (_sharedHistory.Count > before)
                        profiles.Insert(0, (coordName,
                                           coordCloud.Data.Service.ProviderName,
                                           coordCloud.Data.Service.CurrentModel,
                                           _sharedHistory.Last().Content));
                }
                else if (coordOllama is not null)
                {
                    await RunOllamaStreamAsync(coordOllama!, ct, coordSelfHint, hidden: true);
                    if (_sharedHistory.Count > before)
                        profiles.Insert(0, (coordName,
                                           "Ollama",
                                           coordOllama!.Data.Service.CurrentModel,
                                           _sharedHistory.Last().Content));
                }
                assessed++;
            }

            spinTimer.Stop();
            statusTb.Text = profiles.Count > 0
                ? $"✓ Team capabilities profiled - {profiles.Count} participant(s)"
                : "⚠  Capability profiling produced no results";

            // Use conditional blocks instead of early returns so the finally + chain always run.
            if (!ct.IsCancellationRequested && profiles.Count > 0)
            {
                // ── Build and save XAML ───────────────────────────────────────────────
                var xaml     = BuildSuperPowersXaml(fingerprint, profiles, activeOllamas, activeCloudAIs);
                var xamlPath = GetSuperPowersPath();   // may be null if project was closed mid-run
                if (xamlPath is not null)
                {
                    try
                    {
                        // EnsureProjectFolders() is called on every OpenProject so the
                        // PROJECTSETTINGS directory should already exist, but CreateDirectory
                        // is idempotent - this handles any edge cases gracefully.
                        var dir = SysIO.Path.GetDirectoryName(xamlPath)!;
                        SysIO.Directory.CreateDirectory(dir);
                        SysIO.File.WriteAllText(xamlPath, xaml, System.Text.Encoding.UTF8);
                    }
                    catch (Exception ex)
                    {
                        AddSystemMessage($"⚠  Could not save ParticipantSuperPowers.xaml: {ex.Message}");
                        // Tell coordinator so it knows the profile was not persisted
                        _sharedHistory.Add(new CloudAIMessage("user",
                            "[SYSTEM: The capability profile could not be saved to disk " +
                            $"({ex.Message}). The assessment results are still in context for " +
                            "this session, but will need to be re-run next time.]", "System"));
                    }
                }

                // ── Coordinator presents visible summary and role-quality check ─────────
                if (!ct.IsCancellationRequested)
                {
                    // Build a role summary so the coordinator can check fitness
                    var roleSummary = new System.Text.StringBuilder();
                    roleSummary.Append("Current role assignments:\n");
                    foreach (var (name, provider, model, _) in profiles)
                    {
                        var r = _projectSettings?.Get(provider, model);
                        if (r is null) continue;
                        var parts = new List<string>();
                        if (r.IsCoordinator) parts.Add("Coordinator");
                        if (r.IsReasoner)    parts.Add($"Reasoner (priority {r.ReasonerPriority})");
                        if (r.IsCritic)      parts.Add("Critic");
                        if (r.IsPlanner)     parts.Add("Planner");
                        if (r.IsResearcher)  parts.Add("Researcher");
                        var roleList = parts.Count > 0 ? string.Join(", ", parts) : "no special role";
                        roleSummary.Append($"  • {name}: {roleList}\n");
                    }

                    // Build the display name list so the coordinator can reference exact names in the file
                    var participantNameList = string.Join(", ", profiles.Select(p => p.Name));

                    // Build a "what still needs doing" hint so the coordinator can suggest
                    // the right next step rather than diving into content.
                    var nextStepHint = new System.Text.StringBuilder();
                    bool needsRoadmap = _currentProjectType?.HasRoadmap == true
                                        && !(_projectSettings?.RoadmapInitialized == true)
                                        && (_currentRoadmap is null || _currentRoadmap.Milestones.Count == 0);
                    bool needsWorldBuilding = _currentProjectType?.HasWorldBuilding == true;
                    var  worldFolders       = _currentProjectType?.GetWorldFolderList() ?? [];

                    if (needsRoadmap)
                        nextStepHint.AppendLine("• No roadmap has been built yet - suggesting to build one together with the user would be the ideal next step.");
                    if (needsWorldBuilding && worldFolders.Length > 0)
                        nextStepHint.AppendLine($"• This project type uses world-building folders ({string.Join(", ", worldFolders)}) - if those don't exist yet, suggest creating them before writing any content.");
                    if (nextStepHint.Length == 0)
                        nextStepHint.AppendLine("• The project appears to have its setup in place - ask the user what they would like to work on next.");

                    _sharedHistory.Add(new CloudAIMessage("user",
                        "The team capability profile has been saved. " +
                        roleSummary.ToString() +
                        "\nPlease do four things:\n" +
                        "1. Give the user a concise overview of the team's strengths and your task routing plan, " +
                        "highlighting any cost/performance trade-offs.\n" +
                        "2. Evaluate the current role assignments. If any participant would be better suited " +
                        "to a different role based on their capabilities, explain clearly and suggest the change.\n" +
                        "3. Recommend which participants should receive Write Access (WR). Write Access lets a " +
                        "participant create and edit project files directly. Only grant it to participants whose " +
                        "role genuinely requires writing output - typically active creative/code contributors. " +
                        "Read-only participants (critics, reviewers, researchers) should NOT have write access. " +
                        "Name the specific participants you recommend for WR and briefly explain why.\n" +
                        "4. Write a ParticipantSuperRoles.xml file that defines each participant's specific role " +
                        "for THIS project. This file will be injected into each participant's system prompt on " +
                        "every future session, so make the instructions project-specific, directive, and useful.\n\n" +
                        "Use EXACTLY this format - one <Role> element per participant, covering all " +
                        $"participants ({participantNameList}):\n\n" +
                        "<output path=\"PROJECTSETTINGS/ParticipantSuperRoles.xml\">\n" +
                        "<ParticipantSuperRoles>\n" +
                        "  <Role name=\"ExactDisplayName\" title=\"Short Role Title\">Detailed second-person instruction for this participant's role in this specific project.</Role>\n" +
                        "  <!-- one <Role> per participant -->\n" +
                        "</ParticipantSuperRoles>\n" +
                        "</output>\n\n" +
                        "Write the <output> block first (it will be processed silently), then present your " +
                        "summary, role evaluation, and Write Access recommendations to the user.\n\n" +
                        "CRITICAL - after presenting the above:\n" +
                        "• DO NOT start writing any project content (scenes, chapters, code, designs, etc.).\n" +
                        "• DO NOT run a work session or task sequence on your own.\n" +
                        "• Based on the project state below, end your response with ONE clear suggestion " +
                        "for the logical next step, then ask the user whether they agree or want something different.\n" +
                        "• Stop after that question. Wait for the user to reply.\n\n" +
                        "Project state:\n" +
                        nextStepHint.ToString()));

                    if (coordCloud is not null)
                        await RunCloudAIStreamAsync(coordCloud, ct);
                    else
                        await RunOllamaStreamAsync(coordOllama!, ct);
                }
            }
        }
        catch (Exception ex)
        {
            spinTimer.Stop();
            AddSystemMessage($"⚠  Capability profiling failed: {ex.Message}");
        }
        finally
        {
            spinTimer.Stop();
            // Invalidate the SuperRoles cache so the file written by the coordinator
            // during this session is picked up immediately on the next prompt.
            _superRoles = null;
            _streamCts?.Dispose();
            _streamCts = null;
            AIRespondButton.IsEnabled = true;
            SendButton.IsEnabled      = true;
        }

        // Calibration already ends with a visible coordinator message that includes a
        // "next step" suggestion.  Firing CheckAndTriggerRoadmapBuildingAsync right after
        // would stack a second automatic AI exchange on top - overwhelming the user before
        // they have a chance to reply.  Mark the work session as fired so neither the
        // work-session greeting nor the roadmap-building intro fires automatically;
        // both chains are resumable once the user sends their first reply.
        _workSessionFired = true;
    }

    /// <summary>
    /// Builds the ParticipantSuperPowers XML document from the collected profiles.
    /// </summary>
    private string BuildSuperPowersXaml(
        string fingerprint,
        List<(string Name, string Provider, string Model, string Answer)> profiles,
        List<OllamaParticipantUI>  activeOllamas,
        List<CloudAIParticipantUI> activeCloudAIs)
    {
        var xns = System.Xml.Linq.XNamespace.None;

        var root = new System.Xml.Linq.XElement("ParticipantSuperPowers",
            new System.Xml.Linq.XAttribute("Fingerprint", fingerprint),
            new System.Xml.Linq.XAttribute("Generated",   DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")),
            new System.Xml.Linq.XAttribute("Project",     _currentProject?.ProjectName ?? ""));

        foreach (var (name, provider, model, answer) in profiles)
        {
            var role      = _projectSettings?.Get(provider, model);
            var roleStr   = role?.IsCoordinator == true ? "Coordinator"
                          : role?.IsReasoner    == true ? "Reasoner"
                          : "Participant";
            var priority  = role?.ReasonerPriority ?? 5;
            var isRModel  = IsReasonerModel(model);
            var costTier  = GetCostTier(provider, model);

            // Parse the compact 3-line answer into separate attributes
            var (strengths, bestFor, avoid) = ParseCapabilityLines(answer);

            root.Add(new System.Xml.Linq.XElement("Participant",
                new System.Xml.Linq.XAttribute("Name",              name),
                new System.Xml.Linq.XAttribute("Provider",          provider),
                new System.Xml.Linq.XAttribute("Model",             model),
                new System.Xml.Linq.XAttribute("Role",              roleStr),
                new System.Xml.Linq.XAttribute("IsCritic",          role?.IsCritic     == true),
                new System.Xml.Linq.XAttribute("IsPlanner",         role?.IsPlanner    == true),
                new System.Xml.Linq.XAttribute("IsResearcher",      role?.IsResearcher == true),
                new System.Xml.Linq.XAttribute("IsReasonerModel",   isRModel),
                new System.Xml.Linq.XAttribute("ReasonerPriority",  priority),
                new System.Xml.Linq.XAttribute("CostTier",          costTier),
                new System.Xml.Linq.XAttribute("Strengths",         strengths),
                new System.Xml.Linq.XAttribute("BestFor",           bestFor),
                new System.Xml.Linq.XAttribute("Avoid",             avoid)));
        }

        var doc = new System.Xml.Linq.XDocument(
            new System.Xml.Linq.XComment(
                " ClaudetRelay - ParticipantSuperPowers.xaml\n" +
                "     Auto-generated from hidden capability interviews.\n" +
                "     Do not edit manually - re-run by changing project participants. "),
            root);

        // Return as indented XML string
        var sb = new System.Text.StringBuilder();
        using (var writer = System.Xml.XmlWriter.Create(sb, new System.Xml.XmlWriterSettings
               { Indent = true, IndentChars = "  ", Encoding = System.Text.Encoding.UTF8,
                 OmitXmlDeclaration = false }))
            doc.Save(writer);
        return sb.ToString();
    }

    /// <summary>
    /// Parses the compact 3-line capability answer produced by the assessment prompt
    /// into separate Strengths / BestFor / Avoid strings.
    /// Tolerates minor variations in labelling and gracefully falls back for
    /// models that produce prose instead of the expected format.
    /// </summary>
    private static (string Strengths, string BestFor, string Avoid) ParseCapabilityLines(string answer)
    {
        var strengths = "";
        var bestFor   = "";
        var avoid     = "";

        foreach (var rawLine in answer.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Strengths:",  StringComparison.OrdinalIgnoreCase))
                strengths = ExtractAfterFirstColon(line);
            else if (line.StartsWith("Best for:", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("Best-for:", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("Bestfor:",  StringComparison.OrdinalIgnoreCase))
                bestFor = ExtractAfterFirstColon(line);
            else if (line.StartsWith("Avoid:", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("Not for:", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("Weakness:", StringComparison.OrdinalIgnoreCase))
                avoid = ExtractAfterFirstColon(line);
        }

        // If the model ignored the format and returned prose, store everything in Strengths
        // so at least something is captured.
        if (string.IsNullOrEmpty(strengths) && string.IsNullOrEmpty(bestFor) &&
            string.IsNullOrEmpty(avoid))
        {
            strengths = answer.Replace('\n', ' ').Trim();
            if (strengths.Length > 200) strengths = strengths[..200] + "…";
        }

        return (strengths, bestFor, avoid);
    }

    private static string ExtractAfterFirstColon(string line)
    {
        var idx = line.IndexOf(':');
        return idx >= 0 ? line[(idx + 1)..].Trim() : line.Trim();
    }

    // ── Roadmap-building conversation ──────────────────────────────────────

    /// <summary>
    /// Triggers roadmap building when the project supports a roadmap that is still empty and
    /// the init conversation has not started yet.  Once roadmap building is either not needed
    /// or already done, chains into <see cref="CheckAndTriggerWorkSessionAsync"/>.
    /// </summary>
    private async Task CheckAndTriggerRoadmapBuildingAsync()
    {
        if (_projectSettings is null || _currentProjectFolder is null) return;
        // Full Manual Mode has no coordinator automation.
        if (_projectSettings.OrchestrationMode == OrchestrationMode.AllRespond) return;

        if (_currentProjectType?.HasRoadmap == true &&
            !_projectSettings.RoadmapInitialized &&
            (_currentRoadmap is null || _currentRoadmap.Milestones.Count == 0) &&
            HasCoordinatorRole())
        {
            // TriggerRoadmapBuildingAsync chains into CheckAndTriggerWorkSessionAsync when done
            await TriggerRoadmapBuildingAsync();
            return;
        }

        // Roadmap building not needed or no coordinator - proceed to work session
        await CheckAndTriggerWorkSessionAsync();
    }

    /// <summary>
    /// Fires the coordinator's opening roadmap-planning message.
    /// The Planner (if any) gets the first word; the coordinator introduces the process
    /// and asks the user the first clarifying question.
    /// The conversation then continues normally - once the coordinator has enough information
    /// it will embed a <c>&lt;roadmapproposal&gt;</c> in a response, which
    /// <see cref="ApplyRoadmapCommands"/> parses and saves automatically.
    /// </summary>
    private async Task TriggerRoadmapBuildingAsync()
    {
        if (_projectSettings is null || _currentProjectFolder is null || _currentProject is null) return;
        if (_streamCts is not null) return;

        var activeOllamas  = _ollamaParticipants
            .Where(ui => ui.Data.Enabled && IsParticipantActiveInProject(ui)).ToList();
        var activeCloudAIs = _cloudAIParticipants
            .Where(ui => ui.Data.Enabled && IsParticipantActiveInProject(ui)).ToList();

        var (coordOllama, coordCloud) = FindCoordinatorInLists(activeOllamas, activeCloudAIs);
        if (coordOllama is null && coordCloud is null) return;

        AIRespondButton.IsEnabled = false;
        SendButton.IsEnabled      = false;
        _streamCts = new CancellationTokenSource();
        var ct = _streamCts.Token;

        try
        {
            AddSystemMessage("- Roadmap Planning -");

            var projectType = _currentProjectType is null ? "general"
                : $"{_currentProjectType.Icon} {_currentProjectType.Name}";
            var projectDesc = string.IsNullOrWhiteSpace(_currentProject.Description) ? ""
                : $"The project description is: \"{_currentProject.Description.Trim()}\"\n";

            // Inject hidden context so all participants know what's happening
            _sharedHistory.Add(new CloudAIMessage("user",
                "[INTERNAL - not shown to user]\n" +
                $"This project (\"{_currentProject.ProjectName}\", type: {projectType}) has no roadmap yet.\n" +
                projectDesc +
                "Coordinator: open a friendly conversation with the user to build a roadmap together. " +
                "Ask about goals, key phases, and main deliverables - one focused question at a time. " +
                "Once you have gathered enough information (through back-and-forth with the user), " +
                "propose the full roadmap using:\n\n" +
                "<roadmapproposal>\n" +
                "MILESTONE: Milestone title | Optional description\n" +
                "  ITEM: Task title | Optional description\n" +
                "  ITEM: Another task\n" +
                "MILESTONE: Second milestone\n" +
                "  ITEM: ...\n" +
                "</roadmapproposal>\n\n" +
                "Do NOT produce the proposal tag right away - first have a conversation. " +
                "Start by greeting the user and asking your first question about the project's main goal."));

            // If a Planner is present (and isn't the coordinator), let them set the stage first
            var (plannerOllama, plannerCloud) = FindPlannerInLists(activeOllamas, activeCloudAIs);
            bool plannerIsCoord = plannerCloud == coordCloud && plannerOllama == coordOllama;

            if (!plannerIsCoord)
            {
                if (!ct.IsCancellationRequested && plannerCloud is not null)
                {
                    await RunCloudAIStreamAsync(plannerCloud, ct,
                        "INTERNAL SYSTEM - Planner role. Briefly (1-2 sentences) indicate you will " +
                        "help structure the roadmap once the Coordinator has gathered the project goals. " +
                        "Then hand over to the Coordinator.");
                }
                else if (!ct.IsCancellationRequested && plannerOllama is not null)
                {
                    await RunOllamaStreamAsync(plannerOllama!, ct,
                        "INTERNAL SYSTEM - Planner role. Briefly (1-2 sentences) indicate you will " +
                        "help structure the roadmap once the Coordinator has gathered the project goals. " +
                        "Then hand over to the Coordinator.");
                }
            }

            // Coordinator kicks off the conversation
            if (!ct.IsCancellationRequested)
            {
                const string coordHint =
                    "Start the roadmap-building conversation now. In 2-3 sentences: introduce that " +
                    "you'll help the user build a project roadmap through a short conversation, then " +
                    "ask your first question about the project's main goal or top priority. " +
                    "Be warm, concise, and encouraging.";

                if (coordCloud is not null)
                    await RunCloudAIStreamAsync(coordCloud, ct, coordHint);
                else
                    await RunOllamaStreamAsync(coordOllama!, ct, coordHint);
            }

            // Mark conversation as started so we don't re-trigger on subsequent project opens
            _projectSettings.RoadmapInitialized = true;
            ProjectService.SaveProject(_currentProjectFolder!, _projectSettings);
        }
        finally
        {
            _streamCts?.Dispose();
            _streamCts = null;
            AIRespondButton.IsEnabled = true;
            SendButton.IsEnabled      = true;
        }

        // The roadmap-building intro counts as the coordinator's greeting for this open.
        // Mark the flag so we don't fire a second greeting via CheckAndTriggerWorkSessionAsync.
        _workSessionFired = true;
    }

    // ── Work session ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when the current project settings contain at least one active
    /// coordinator role.  Does NOT require the participant to be online - online status is
    /// checked later inside the actual trigger methods.
    /// </summary>
    private bool HasCoordinatorRole() =>
        _projectSettings?.Roles.Any(r => r.IsCoordinator && r.IsActive) == true;

    /// <summary>
    /// Triggers <see cref="TriggerWorkSessionAsync"/> when a coordinator role is configured
    /// and the work-session greeting has not already fired this open.
    /// No-op when conditions are not met or when roadmap building already introduced
    /// the coordinator (<see cref="_workSessionFired"/> is set).
    /// </summary>
    private async Task CheckAndTriggerWorkSessionAsync()
    {
        if (_projectSettings is null || _currentProjectFolder is null) return;
        // Full Manual Mode has no coordinator automation - no work-session greeting.
        if (_projectSettings.OrchestrationMode == OrchestrationMode.AllRespond) return;
        if (_workSessionFired) return;
        if (!HasCoordinatorRole()) return;

        await TriggerWorkSessionAsync();
    }

    /// <summary>
    /// Coordinator greeting and work-session check-in on every project open.
    /// <para>
    /// The coordinator always greets the user first and asks whether to start working
    /// or have a chat.  Once the user is ready the coordinator follows one of two paths:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Open tasks present</b> - standard work session: review InProgress items,
    ///     pick next task, clarify work mode (user-led vs AI-led), update roadmap when done.</item>
    ///   <item><b>No open tasks</b> - completion check: verify with the user that all items
    ///     are truly done, then offer to enrich existing items with descriptions/sub-task lists
    ///     or extend the roadmap with new milestones.</item>
    /// </list>
    /// Clock-watching thresholds (3 h / 8 h / 10 h) are always active via
    /// <see cref="BuildSessionTimeInstruction"/>.
    /// </summary>
    private async Task TriggerWorkSessionAsync()
    {
        if (_projectSettings is null || _currentProjectFolder is null || _currentProject is null) return;
        if (_streamCts is not null) return;
        if (_workSessionFired) return;   // already ran this open - don't double-greet
        _workSessionFired = true;

        var activeOllamas  = _ollamaParticipants
            .Where(ui => ui.Data.Enabled && IsParticipantActiveInProject(ui)).ToList();
        var activeCloudAIs = _cloudAIParticipants
            .Where(ui => ui.Data.Enabled && IsParticipantActiveInProject(ui)).ToList();

        var (coordOllama, coordCloud) = FindCoordinatorInLists(activeOllamas, activeCloudAIs);
        if (coordOllama is null && coordCloud is null) return;

        AIRespondButton.IsEnabled = false;
        SendButton.IsEnabled      = false;
        _streamCts = new CancellationTokenSource();
        var ct = _streamCts.Token;

        try
        {
            AddSystemMessage("- Work Session -");

            // ── Roadmap state snapshot ────────────────────────────────────
            var hasMilestones = _currentRoadmap?.Milestones.Count > 0;

            var inProgress = hasMilestones
                ? _currentRoadmap!.Milestones
                    .SelectMany(ms => ms.Items.Select(it => (Milestone: ms, Item: it)))
                    .Where(x => x.Item.Status == ItemStatus.InProgress)
                    .ToList()
                : [];

            var todo = hasMilestones
                ? _currentRoadmap!.Milestones
                    .SelectMany(ms => ms.Items.Select(it => (Milestone: ms, Item: it)))
                    .Where(x => x.Item.Status == ItemStatus.Todo)
                    .Take(10)
                    .ToList()
                : [];

            var allDone = hasMilestones && inProgress.Count == 0 && todo.Count == 0;

            // Items whose description is empty (any status)
            var noDesc = hasMilestones
                ? _currentRoadmap!.Milestones
                    .SelectMany(ms => ms.Items.Select(it => (Milestone: ms, Item: it)))
                    .Where(x => string.IsNullOrWhiteSpace(x.Item.Description))
                    .ToList()
                : [];

            // ── Build protocol instruction ────────────────────────────────
            var protocol = new System.Text.StringBuilder();

            protocol.AppendLine("[INTERNAL - not shown to user]");
            protocol.AppendLine("Work session starting. IMPORTANT: do NOT dive straight into work.");
            protocol.AppendLine();
            protocol.AppendLine("STEP 1 - GREETING (do this first, every time):");
            protocol.AppendLine("  Greet the user warmly. Ask whether they want to start working on the");
            protocol.AppendLine("  project right away or would prefer to have a friendly chat first.");
            protocol.AppendLine("  Keep this greeting to 2-3 sentences maximum.");
            protocol.AppendLine("  Wait for the user's reply before proceeding to any work steps.");
            protocol.AppendLine();

            if (!hasMilestones)
            {
                // No roadmap content yet - simple greeting, no task protocol
                protocol.AppendLine("ROADMAP STATE: No roadmap tasks exist yet.");
                protocol.AppendLine("STEP 2 - once the user is ready: just get started naturally.");
                protocol.AppendLine("  Do not reference the roadmap or tasks - there are none to discuss.");
                protocol.AppendLine("  Ask what the user would like to work on or talk about.");
            }
            else if (allDone)
            {
                protocol.AppendLine("ROADMAP STATE: No open tasks (no InProgress or Todo items).");
                protocol.AppendLine();
                protocol.AppendLine("STEP 2 - COMPLETION CHECK (once user is ready to work):");
                protocol.AppendLine("  - Congratulate the user on completing all current roadmap items.");
                protocol.AppendLine("  - Verify with them that everything really is done - some items may");
                protocol.AppendLine("    have been marked complete by accident.");
                protocol.AppendLine("  - Ask if they want to extend the roadmap with new milestones or");
                protocol.AppendLine("    add more tasks to existing milestones.");
                protocol.AppendLine("  - Go through each item and offer to add or improve its description");
                protocol.AppendLine("    and sub-task list. Ask the user for the content of each one.");
                protocol.AppendLine("    For example, for a book: ask for the chapter summary and list of");
                protocol.AppendLine("    scenes; for software: ask for acceptance criteria and sub-tasks.");
                protocol.AppendLine("  - Use these tags to update the roadmap:");
                protocol.AppendLine("      <roadmap-describe id=\"ITEM_ID\">");
                protocol.AppendLine("      Description / sub-task list here (multi-line supported)");
                protocol.AppendLine("      </roadmap-describe>");
                protocol.AppendLine("      <roadmap-additem milestone=\"Milestone Title\" title=\"New task\" description=\"Optional\"/>");
                protocol.AppendLine("      <roadmap-addmilestone>");
                protocol.AppendLine("      MILESTONE: Title | Description");
                protocol.AppendLine("        ITEM: Task title | Description");
                protocol.AppendLine("      </roadmap-addmilestone>");

                if (noDesc.Count > 0)
                {
                    protocol.AppendLine();
                    protocol.AppendLine($"Items without descriptions ({noDesc.Count}):");
                    foreach (var (ms, it) in noDesc.Take(20))
                        protocol.AppendLine($"  • [id:{it.Id}] [{ms.Title}] → {it.Title}");
                }
            }
            else
            {
                if (inProgress.Count > 0)
                {
                    protocol.AppendLine("Unfinished from last session:");
                    foreach (var (ms, it) in inProgress)
                        protocol.AppendLine($"  🔄 [{ms.Title}] → {it.Title} ({it.Progress}%)");
                    protocol.AppendLine();
                }

                if (todo.Count > 0)
                {
                    protocol.AppendLine("Next available tasks (Todo):");
                    foreach (var (ms, it) in todo)
                        protocol.AppendLine($"  ⬜ [{ms.Title}] → {it.Title}");
                    protocol.AppendLine();
                }

                protocol.AppendLine("STEP 2 - WORK SESSION (once user is ready):");
                protocol.AppendLine("  a) Mention any unfinished InProgress tasks from last time.");
                protocol.AppendLine("  b) Ask the user if anything on the roadmap needs to be");
                protocol.AppendLine("     changed or updated before starting.");
                protocol.AppendLine("  c) Help the user pick the next task to work on.");
                protocol.AppendLine("  d) Clarify the preferred work mode:");
                protocol.AppendLine("       • User-led: user does the work, AI gives tips and motivation");
                protocol.AppendLine("       • AI-led: AI does the heavy lifting, user gives feedback");
                protocol.AppendLine("  e) Work on the task together.");
                protocol.AppendLine("  f) When a task or sub-task is finished, update the roadmap:");
                protocol.AppendLine("       [ROADMAP:update:ITEM_ID:PROGRESS]  - e.g. 75 for 75%");
                protocol.AppendLine("       [ROADMAP:complete:ITEM_ID]         - marks item 100% done");
                protocol.AppendLine("  g) After finishing, ask whether to continue or wrap up for today.");

                if (noDesc.Count > 0)
                {
                    protocol.AppendLine();
                    protocol.AppendLine($"Note: {noDesc.Count} item(s) have no description yet.");
                    protocol.AppendLine("      When you reach those items, use <roadmap-describe> to add one.");
                }
            }

            _sharedHistory.Add(new CloudAIMessage("user", protocol.ToString().Trim()));

            // ── Coordinator fires the greeting ────────────────────────────
            const string coordHint =
                "Start the work session now. Greet the user warmly (2-3 sentences). " +
                "Ask whether they are ready to dive into work on this project or would prefer " +
                "to have a friendly chat first. Do NOT start discussing tasks yet - just greet " +
                "and ask. Be warm and encouraging.";

            if (!ct.IsCancellationRequested)
            {
                if (coordCloud is not null)
                    await RunCloudAIStreamAsync(coordCloud, ct, coordHint);
                else
                    await RunOllamaStreamAsync(coordOllama!, ct, coordHint);
            }
        }
        finally
        {
            _streamCts?.Dispose();
            _streamCts = null;
            AIRespondButton.IsEnabled = true;
            SendButton.IsEnabled      = true;
        }
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
            GetRoleForParticipant(ui)?.IsCoordinator == true);
        if (cloud is not null) return (null, cloud);

        var ollama = ollamas.FirstOrDefault(ui =>
            GetRoleForParticipant(ui)?.IsCoordinator == true);
        return (ollama, null);
    }

    /// <summary>
    /// Finds the first Planner (PL) among the active participant lists.
    /// Cloud AI is preferred over Ollama; returns (null, null) if none assigned.
    /// </summary>
    private (OllamaParticipantUI? Ollama, CloudAIParticipantUI? Cloud) FindPlannerInLists(
        List<OllamaParticipantUI> ollamas, List<CloudAIParticipantUI> clouds)
    {
        if (_projectSettings is null) return (null, null);

        var cloud = clouds.FirstOrDefault(ui =>
            GetRoleForParticipant(ui)?.IsPlanner == true);
        if (cloud is not null) return (null, cloud);

        var ollama = ollamas.FirstOrDefault(ui =>
            GetRoleForParticipant(ui)?.IsPlanner == true);
        return (ollama, null);
    }

    /// <summary>Effective display name for a participant: AnswerAsName if set, else CustomName/model name.</summary>
    private string GetEffectiveName(OllamaParticipantUI ui)
    {
        var role = GetRoleForParticipant(ui);
        if (!string.IsNullOrWhiteSpace(role?.AnswerAsName)) return role.AnswerAsName;
        return string.IsNullOrEmpty(ui.Data.CustomName)
            ? FormatModelDisplayName(ui.Data.Service.CurrentModel)
            : ui.Data.CustomName;
    }

    /// <summary>Effective display name for a participant: AnswerAsName if set, else CustomName/model name.</summary>
    private string GetEffectiveName(CloudAIParticipantUI ui)
    {
        var role = GetRoleForParticipant(ui);
        if (!string.IsNullOrWhiteSpace(role?.AnswerAsName)) return role.AnswerAsName;
        return string.IsNullOrEmpty(ui.Data.CustomName)
            ? FormatModelDisplayName(ui.Data.Service.CurrentModel)
            : ui.Data.CustomName;
    }

    /// <summary>
    /// Returns the display name for a participant identified by their avatar label (e.g. "Gm").
    /// Used to convert history message prefixes from raw labels into human-readable names.
    /// Falls back to the raw label if no participant matches (e.g. after a config change).
    /// </summary>
    private string GetDisplayNameForLabel(string avatarLabel)
    {
        foreach (var ui in _ollamaParticipants)
            if (ui.Data.AvatarLabel == avatarLabel) return GetEffectiveName(ui);
        foreach (var ui in _cloudAIParticipants)
            if (ui.Data.AvatarLabel == avatarLabel) return GetEffectiveName(ui);
        return avatarLabel;
    }

    /// <summary>
    /// Returns the project role for this Ollama participant using positional matching -
    /// safe when multiple participants share the same model name.
    /// </summary>
    private ProjectParticipantRole? GetRoleForParticipant(OllamaParticipantUI ui)
    {
        if (_projectSettings is null) return null;
        int idx = _ollamaParticipants.IndexOf(ui);
        if (idx < 0) return null;
        return ResolveRoleAtGroupIndex("Ollama", idx);
    }

    /// <summary>
    /// Returns the project role for this Cloud AI participant using positional matching -
    /// safe when multiple participants share the same model name.
    /// </summary>
    private ProjectParticipantRole? GetRoleForParticipant(CloudAIParticipantUI ui)
    {
        if (_projectSettings is null) return null;
        int idx = _cloudAIParticipants.IndexOf(ui);
        if (idx < 0) return null;
        return ResolveRoleAtGroupIndex("Cloud", idx);
    }

    /// <summary>
    /// Finds the <paramref name="indexInGroup"/>-th Ollama (typeGroup="Ollama") or Cloud AI
    /// (typeGroup="Cloud") entry in the project's active participant list and returns its
    /// project role using the same positional-first / key-based-fallback logic as the
    /// settings dialog. The project-saved list exactly mirrors the current UI, so positional
    /// matching is reliable regardless of what global settings contain.
    /// </summary>
    private ProjectParticipantRole? ResolveRoleAtGroupIndex(string typeGroup, int indexInGroup)
    {
        var ps = _projectSettings!;
        if (ps.ActiveParticipants is not { Count: > 0 }) return null;
        var enabled = ps.ActiveParticipants.Where(p => p.Enabled).ToList();

        int groupCount = 0;
        for (int pi = 0; pi < enabled.Count; pi++)
        {
            var p        = enabled[pi];
            bool matches = typeGroup == "Ollama" ? p.Type == "Ollama" : p.Type != "Ollama";
            if (!matches) continue;

            if (groupCount == indexInGroup)
            {
                // Positional-first (same as ShowProjectSettingsDialog), key-based fallback
                ProjectParticipantRole? role = null;
                if (pi < ps.Roles.Count)
                {
                    var c = ps.Roles[pi];
                    if (string.Equals(c.Provider, p.Type,  StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(c.Model,    p.Model, StringComparison.OrdinalIgnoreCase))
                        role = c;
                }
                return role ?? ps.Get(p.Type, p.Model);
            }
            groupCount++;
        }
        return null;
    }

    /// <summary>Returns true when the participant is flagged as a Reasoner in the current project settings.</summary>
    private bool IsReasoner(OllamaParticipantUI ui) =>
        GetRoleForParticipant(ui)?.IsReasoner == true;

    /// <summary>Returns true when the participant is flagged as a Reasoner in the current project settings.</summary>
    private bool IsReasoner(CloudAIParticipantUI ui) =>
        GetRoleForParticipant(ui)?.IsReasoner == true;

    /// <summary>Returns all enabled Critics (Ollama + Cloud AI) sorted by effective display name.</summary>
    private List<string> GetAvailableCritics()
    {
        var result = new List<string>();
        foreach (var u in _ollamaParticipants.Where(u => u.Data.Enabled && GetRoleForParticipant(u)?.IsCritic == true))
            result.Add(GetEffectiveName(u));
        foreach (var u in _cloudAIParticipants.Where(u => u.Data.Enabled && GetRoleForParticipant(u)?.IsCritic == true))
            result.Add(GetEffectiveName(u));
        return result;
    }

    /// <summary>Returns all enabled Planners (Ollama + Cloud AI) sorted by effective display name.</summary>
    private List<string> GetAvailablePlanners()
    {
        var result = new List<string>();
        foreach (var u in _ollamaParticipants.Where(u => u.Data.Enabled && GetRoleForParticipant(u)?.IsPlanner == true))
            result.Add(GetEffectiveName(u));
        foreach (var u in _cloudAIParticipants.Where(u => u.Data.Enabled && GetRoleForParticipant(u)?.IsPlanner == true))
            result.Add(GetEffectiveName(u));
        return result;
    }

    /// <summary>Returns all enabled Researchers (Ollama + Cloud AI) sorted by effective display name.</summary>
    private List<string> GetAvailableResearchers()
    {
        var result = new List<string>();
        foreach (var u in _ollamaParticipants.Where(u => u.Data.Enabled && GetRoleForParticipant(u)?.IsResearcher == true))
            result.Add(GetEffectiveName(u));
        foreach (var u in _cloudAIParticipants.Where(u => u.Data.Enabled && GetRoleForParticipant(u)?.IsResearcher == true))
            result.Add(GetEffectiveName(u));
        return result;
    }

    /// <summary>
    /// Returns true if the participant may write project files (&lt;output&gt;, &lt;projectplan&gt; tags).
    /// Coordinators always have write access. All other participants need the explicit
    /// Write Access (WR) flag. Falls back to unrestricted when no coordinator is configured
    /// (backwards compatibility with projects that predate role assignment).
    /// </summary>
    private bool HasWriteAccess(OllamaParticipantUI ui)
    {
        if (_projectSettings is null) return true;
        bool anyCoordinator = _projectSettings.Roles.Any(r => r.IsCoordinator);
        if (!anyCoordinator) return true;   // no roles configured yet - open access
        var role = GetRoleForParticipant(ui);
        return role?.IsCoordinator == true || role?.IsWriteAccess == true;
    }

    /// <inheritdoc cref="HasWriteAccess(OllamaParticipantUI)"/>
    private bool HasWriteAccess(CloudAIParticipantUI ui)
    {
        if (_projectSettings is null) return true;
        bool anyCoordinator = _projectSettings.Roles.Any(r => r.IsCoordinator);
        if (!anyCoordinator) return true;
        var role = GetRoleForParticipant(ui);
        return role?.IsCoordinator == true || role?.IsWriteAccess == true;
    }

    /// <summary>
    /// Returns all enabled Reasoners (across Ollama and Cloud AI) sorted by priority descending.
    /// Used to inject the Reasoner roster into the Coordinator's system prompt.
    /// </summary>
    private List<(string Name, int Priority)> GetAvailableReasoners()
    {
        var result = new List<(string Name, int Priority)>();
        foreach (var u in _ollamaParticipants.Where(u => u.Data.Enabled && IsReasoner(u)))
            result.Add((GetEffectiveName(u), GetRoleForParticipant(u)?.ReasonerPriority ?? 5));
        foreach (var u in _cloudAIParticipants.Where(u => u.Data.Enabled && IsReasoner(u)))
            result.Add((GetEffectiveName(u), GetRoleForParticipant(u)?.ReasonerPriority ?? 5));
        return result;
    }

    /// <summary>Returns the effective display name of the active project coordinator, or null if none.</summary>
    private string? GetCoordinatorName()
    {
        var (coordOllama, coordCloud) = FindActiveCoordinator();
        if (coordCloud  is not null) return GetEffectiveName(coordCloud);
        if (coordOllama is not null) return GetEffectiveName(coordOllama);
        return null;
    }

    /// <summary>
    /// Updates the CO / R badge overlays on every sidebar participant card to reflect
    /// the current <see cref="_projectSettings"/>. Call after loading or saving project
    /// settings, and after closing a project (badges go hidden when settings are null).
    /// </summary>
    private void RefreshParticipantBadges()
    {
        foreach (var ui in _ollamaParticipants)
        {
            var role = GetRoleForParticipant(ui);
            ui.CoBadge.Visibility = role?.IsCoordinator == true                                  ? Visibility.Visible : Visibility.Collapsed;
            ui.RBadge .Visibility = role?.IsReasoner    == true                                  ? Visibility.Visible : Visibility.Collapsed;
            ui.CrBadge.Visibility = role?.IsCritic      == true                                  ? Visibility.Visible : Visibility.Collapsed;
            ui.PlBadge.Visibility = role?.IsPlanner     == true                                  ? Visibility.Visible : Visibility.Collapsed;
            ui.RsBadge.Visibility = role?.IsResearcher  == true                                  ? Visibility.Visible : Visibility.Collapsed;
            ui.WrBadge.Visibility = (role?.IsWriteAccess == true || role?.IsCoordinator == true) ? Visibility.Visible : Visibility.Collapsed;
            ui.BadgeRow.Visibility = (role?.IsCoordinator == true || role?.IsReasoner == true ||
                                      role?.IsCritic      == true || role?.IsPlanner  == true ||
                                      role?.IsResearcher  == true || role?.IsWriteAccess == true)
                                      ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var ui in _cloudAIParticipants)
        {
            var role = GetRoleForParticipant(ui);
            ui.CoBadge.Visibility = role?.IsCoordinator == true                                  ? Visibility.Visible : Visibility.Collapsed;
            ui.RBadge .Visibility = role?.IsReasoner    == true                                  ? Visibility.Visible : Visibility.Collapsed;
            ui.CrBadge.Visibility = role?.IsCritic      == true                                  ? Visibility.Visible : Visibility.Collapsed;
            ui.PlBadge.Visibility = role?.IsPlanner     == true                                  ? Visibility.Visible : Visibility.Collapsed;
            ui.RsBadge.Visibility = role?.IsResearcher  == true                                  ? Visibility.Visible : Visibility.Collapsed;
            ui.WrBadge.Visibility = (role?.IsWriteAccess == true || role?.IsCoordinator == true) ? Visibility.Visible : Visibility.Collapsed;
            ui.BadgeRow.Visibility = (role?.IsCoordinator == true || role?.IsReasoner == true ||
                                      role?.IsCritic      == true || role?.IsPlanner  == true ||
                                      role?.IsResearcher  == true || role?.IsWriteAccess == true)
                                      ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Returns true when <paramref name="name"/> is mentioned with an @ prefix in the response.</summary>
    private static bool IsTaggedInResponse(string response, string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        Regex.IsMatch(response, $@"@{Regex.Escape(name)}\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// Returns true when <paramref name="name"/> appears as a whole word anywhere in
    /// <paramref name="message"/> (case-insensitive, no @ required).
    /// Used to detect when the user directly addresses a participant by name.
    /// </summary>
    private static bool IsNamedInMessage(string message, string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        Regex.IsMatch(message, $@"\b{Regex.Escape(name)}\b", RegexOptions.IgnoreCase);

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
                "The free tier allows only a few requests per minute - please wait a moment before continuing.",
            System.Net.HttpStatusCode.Unauthorized =>
                "Unauthorized (401) - the API key was rejected. Check or re-enter the key in ⋮ → Providers.",
            System.Net.HttpStatusCode.Forbidden =>
                "Forbidden (403) - the API key does not have permission for this model.",
            System.Net.HttpStatusCode.ServiceUnavailable =>
                "Service unavailable (503) - the API is temporarily down. Try again shortly.",
            null => $"Connection error: {ex.Message}",
            _    => $"API error {(int)ex.StatusCode}: {ex.Message}"
        };

    private async Task<bool> RunOllamaStreamAsync(OllamaParticipantUI ui, CancellationToken ct,
                                                   string? systemHint = null,
                                                   bool skipLatestUserMessage = false,
                                                   bool hidden = false,
                                                   int _loopDepth = 0)
    {
        var modelName = ui.Data.Service.CurrentModel;
        var display   = string.IsNullOrEmpty(ui.Data.CustomName)
            ? FormatModelDisplayName(modelName)
            : ui.Data.CustomName;
        var avatarLabel = ui.Data.AvatarLabel;
        var colorKey    = ui.Data.ColorKey;

        StreamBubble? bubble = hidden ? null
            : AddStreamingBubble(display, avatarLabel, colorKey, "SecondaryBubbleBrush", false);
        var sb         = new StringBuilder();
        bool firstToken = true;

        // Subscribe to live thinking-text updates so the tooltip tracks thinking in real time
        var svc = ui.Data.Service;
        svc.ThinkingUpdated += OnThinkingUpdate;
        void OnThinkingUpdate(string thought)
        {
            if (!hidden)
                Dispatcher.Invoke(() => bubble!.UpdateThinkingTooltip(thought));
        }

        try
        {
            var history = BuildOllamaHistoryFor(ui, skipLatestUserMessage);
            if (systemHint is not null)
                history.Insert(1, new OllamaChatMessage("system", systemHint));
            await foreach (var token in svc.StreamAsync(history, ct))
            {
                if (firstToken)
                {
                    if (!hidden) bubble!.StopThinking();   // hides dots + tooltip disappears naturally
                    firstToken = false;
                    SetParticipantError(ui, null);         // clear any previous error badge
                }
                sb.Append(token);
                if (!hidden)
                {
                    bubble!.Content.Text = sb.ToString();
                    ChatScrollViewer.ScrollToBottom();
                }
            }
            if (firstToken && !hidden) bubble!.StopThinking(); // empty response
            // Hidden streams are internal assessments - never write files or mutate roadmap.
            bool ollamaHadReadOps = false;
            string ollamaFinalText;
            if (!hidden && _currentProjectFolder is not null)
                (ollamaFinalText, ollamaHadReadOps) = ProcessAIFileOperationTags(
                    sb.ToString(), display, _currentProjectFolder, HasWriteAccess(ui), GetCoordinatorName());
            else
                ollamaFinalText = sb.ToString();

            // ── Roadmap commands ──────────────────────────────────────────
            if (!hidden && _currentRoadmap is not null)
            {
                var myRole = GetRoleForParticipant(ui);
                var cleaned = ApplyRoadmapCommands(ollamaFinalText, display, myRole?.IsCoordinator == true);
                if (cleaned != ollamaFinalText) ollamaFinalText = cleaned;
            }
            // ─────────────────────────────────────────────────────────────

            if (!hidden && ollamaFinalText != sb.ToString())
                bubble!.Content.Text = ollamaFinalText;

            // If the model decided it has nothing new to add, remove its bubble silently
            if (IsPassResponse(ollamaFinalText))
            {
                if (!hidden && ChatPanel.Children.Count > 0)
                    ChatPanel.Children.RemoveAt(ChatPanel.Children.Count - 1);
                return false;
            }

            _sharedHistory.Add(new CloudAIMessage("assistant", ollamaFinalText, GetEffectiveName(ui)));
            if (!hidden)
            {
                var ollamaLogEntry = new ChatLogEntry
                {
                    Timestamp   = DateTime.Now,
                    SenderType  = "AI",
                    Provider    = "Ollama",
                    ModelName   = modelName,
                    DisplayName = display,
                    AvatarLabel = avatarLabel,
                    AccentKey   = colorKey,
                    BubbleKey   = "SecondaryBubbleBrush",
                    IsUser      = false,
                    Message     = ollamaFinalText
                };
                AppendToProjectLog(ollamaLogEntry);
                AppendToGeneralLog(ollamaLogEntry);
            }
            // ── Auto-loop: re-invoke after file reads so AI can act on the results ─────
            if (ollamaHadReadOps && !hidden && _loopDepth < MaxToolLoopDepth)
            {
                AddSystemMessage($"🔄  {display} received file results - continuing " +
                                 $"(step {_loopDepth + 2} of {MaxToolLoopDepth + 1} max)…");
                return await RunOllamaStreamAsync(ui, ct, systemHint,
                    skipLatestUserMessage: false, hidden: false, _loopDepth: _loopDepth + 1);
            }
            // ─────────────────────────────────────────────────────────────────────────
            if (!hidden) OnParticipantResponded(ui);   // moodlet counter
            return true;
        }
        catch (OperationCanceledException)
        {
            // User cancelled - show partial text already in the bubble (if any) and stop cleanly.
            // Do NOT re-throw: callers check ct.IsCancellationRequested to decide whether to continue.
            if (!hidden)
            {
                if (firstToken) bubble!.StopThinking();
                else            bubble!.Content.Text = sb.Append("… [cancelled]").ToString();
            }
            return false;
        }
        catch (HttpRequestException ex)
        {
            if (!hidden)
            {
                bubble!.StopThinking();
                ChatPanel.Children.Remove(bubble.OuterWrapper);
                var httpMsg = HttpErrorMessage(ex, display);
                AddSystemMessage($"⚠  {display} - {httpMsg}");
            }
            var errText = ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                ? "Wants Money" : "ERROR";
            SetParticipantError(ui, errText);
            if (!hidden) NotifyCoordinatorOfError(display, errText);
        }
        catch (Exception ex)
        {
            if (!hidden)
            {
                bubble!.StopThinking();
                ChatPanel.Children.Remove(bubble.OuterWrapper);
                AddSystemMessage($"⚠  {display} - Error: {ex.Message}");
            }
            SetParticipantError(ui, "ERROR");
            if (!hidden) NotifyCoordinatorOfError(display, "ERROR");
        }
        finally
        {
            svc.ThinkingUpdated -= OnThinkingUpdate;
        }
        return !hidden; // visible error → error bubble shown (counts as responded); hidden error → doesn't count
    }

    private async Task<bool> RunCloudAIStreamAsync(CloudAIParticipantUI ui, CancellationToken ct,
                                                    string? systemHint = null,
                                                    bool skipLatestUserMessage = false,
                                                    bool hidden = false,
                                                    int _loopDepth = 0)
    {
        var model       = ui.Data.Service.CurrentModel;
        var display     = string.IsNullOrEmpty(ui.Data.CustomName)
            ? FormatModelDisplayName(model)
            : ui.Data.CustomName;
        var avatarLabel = ui.Data.AvatarLabel;
        var colorKey    = ui.Data.ColorKey;

        StreamBubble? bubble = hidden ? null
            : AddStreamingBubble(display, avatarLabel, colorKey, "PrimaryBubbleBrush", false);
        var sb         = new StringBuilder();
        bool firstToken = true;

        // ── Rate limiting ─────────────────────────────────────────────────
        // Key is "provider|model" so each model can have its own rpm budget.
        var providerName  = ui.Data.Service.ProviderName;
        var limiterKey    = $"{providerName}|{ui.Data.Service.CurrentModel}";
        if (_rateLimiters.TryGetValue(limiterKey, out var rateLimiter))
        {
            if (!hidden)
                bubble!.UpdateThinkingTooltip($"⏳ Waiting - rate limit {rateLimiter.Rpm} req/min");
            await rateLimiter.WaitAsync(ct);
            if (!hidden)
                bubble!.UpdateThinkingTooltip("");
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
                    if (!hidden) bubble!.StopThinking();
                    firstToken = false;
                    SetParticipantError(ui, null);         // clear any previous error badge
                }
                sb.Append(token);
                if (!hidden)
                {
                    bubble!.Content.Text = sb.ToString();
                    ChatScrollViewer.ScrollToBottom();
                }
            }
            if (firstToken && !hidden) bubble!.StopThinking();
            // Hidden streams are internal assessments - never write files or mutate roadmap.
            bool cloudHadReadOps = false;
            string cloudFinalText;
            if (!hidden && _currentProjectFolder is not null)
                (cloudFinalText, cloudHadReadOps) = ProcessAIFileOperationTags(
                    sb.ToString(), display, _currentProjectFolder, HasWriteAccess(ui), GetCoordinatorName());
            else
                cloudFinalText = sb.ToString();

            // ── Roadmap commands ──────────────────────────────────────────
            if (!hidden && _currentRoadmap is not null)
            {
                var myRole  = GetRoleForParticipant(ui);
                var cleaned = ApplyRoadmapCommands(cloudFinalText, display, myRole?.IsCoordinator == true);
                if (cleaned != cloudFinalText) cloudFinalText = cleaned;
            }
            // ─────────────────────────────────────────────────────────────

            if (!hidden && cloudFinalText != sb.ToString())
                bubble!.Content.Text = cloudFinalText;

            // If the model decided it has nothing new to add, remove its bubble silently
            if (IsPassResponse(cloudFinalText))
            {
                if (!hidden && ChatPanel.Children.Count > 0)
                    ChatPanel.Children.RemoveAt(ChatPanel.Children.Count - 1);
                return false;
            }

            _sharedHistory.Add(new CloudAIMessage("assistant", cloudFinalText, GetEffectiveName(ui)));
            if (!hidden)
            {
                var cloudLogEntry = new ChatLogEntry
                {
                    Timestamp   = DateTime.Now,
                    SenderType  = "AI",
                    Provider    = ui.Data.ProviderName,
                    ModelName   = model,
                    DisplayName = display,
                    AvatarLabel = avatarLabel,
                    AccentKey   = colorKey,
                    BubbleKey   = "PrimaryBubbleBrush",
                    IsUser      = false,
                    Message     = cloudFinalText
                };
                AppendToProjectLog(cloudLogEntry);
                AppendToGeneralLog(cloudLogEntry);
            }
            // ── Auto-loop: re-invoke after file reads so AI can act on the results ─────
            if (cloudHadReadOps && !hidden && _loopDepth < MaxToolLoopDepth)
            {
                AddSystemMessage($"🔄  {display} received file results - continuing " +
                                 $"(step {_loopDepth + 2} of {MaxToolLoopDepth + 1} max)…");
                return await RunCloudAIStreamAsync(ui, ct, systemHint,
                    skipLatestUserMessage: false, hidden: false, _loopDepth: _loopDepth + 1);
            }
            // ─────────────────────────────────────────────────────────────────────────
            if (!hidden) OnParticipantResponded(ui);   // moodlet counter
            return true;
        }
        catch (OperationCanceledException)
        {
            // User cancelled - show partial text already in the bubble (if any) and stop cleanly.
            // Do NOT re-throw: callers check ct.IsCancellationRequested to decide whether to continue.
            if (!hidden)
            {
                if (firstToken) bubble!.StopThinking();
                else            bubble!.Content.Text = sb.Append("… [cancelled]").ToString();
            }
            return false;
        }
        catch (HttpRequestException ex)
        {
            if (!hidden)
            {
                bubble!.StopThinking();
                ChatPanel.Children.Remove(bubble.OuterWrapper);
                var httpMsg = HttpErrorMessage(ex, display);
                AddSystemMessage($"⚠  {display} - {httpMsg}");
            }
            var errText = ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                ? "Wants Money" : "ERROR";
            SetParticipantError(ui, errText);
            if (!hidden) NotifyCoordinatorOfError(display, errText);
        }
        catch (Exception ex)
        {
            if (!hidden)
            {
                bubble!.StopThinking();
                ChatPanel.Children.Remove(bubble.OuterWrapper);
                AddSystemMessage($"⚠  {display} - Error: {ex.Message}");
            }
            SetParticipantError(ui, "ERROR");
            if (!hidden) NotifyCoordinatorOfError(display, "ERROR");
        }
        return !hidden; // visible error → error bubble shown (counts as responded); hidden error → doesn't count
    }

    // ── Per-participant history builders ───────────────────────────────────

    private List<OllamaChatMessage> BuildOllamaHistoryFor(OllamaParticipantUI forUi,
                                                          bool skipLatestUserMessage = false)
    {
        var myLabel = forUi.Data.AvatarLabel;
        var myName  = forUi.Data.DisplayName;
        var myModel = forUi.Data.Service.CurrentModel;
        var myRole  = GetRoleForParticipant(forUi);

        var myHasWrite   = HasWriteAccess(forUi);
        var isCoord      = myRole?.IsCoordinator == true;
        var reasoners    = isCoord ? GetAvailableReasoners()    : null;
        var planners     = isCoord ? GetAvailablePlanners()     : null;
        var researchers  = isCoord ? GetAvailableResearchers()  : null;
        var critics      = isCoord ? GetAvailableCritics()      : null;
        var superRole    = GetSuperRoleInstruction(myName);
        var writerNames  = myHasWrite ? null : GetWriteAccessParticipantNames();

        var result = new List<OllamaChatMessage>
        {
            new("system",
                $"You are {myName}, running the {myModel} model. " +
                $"Always respond as {myName}. " +
                $"If asked who you are, say you are {myName} running {myModel}. " +
                $"You are an AI language model — unless a role instruction explicitly tells you otherwise, " +
                $"do not invent or claim personal hobbies, feelings, relationships, or experiences. " +
                $"Do not fabricate or assume facts you are uncertain about; acknowledge uncertainty honestly instead. " +
                $"Messages from other AI participants are prefixed with their display name in square brackets. " +
                $"IMPORTANT: Never prefix your own response with your name or any label — write directly without any '[Name]:' header. " +
                $"Never write as, speak for, or impersonate another participant. You are {myName} and only ever respond in your own voice." +
                BuildAppContextInstruction(forOllama: forUi) +
                BuildProjectTypeContext() +
                BuildRoleInstruction(myRole, reasoners, planners, researchers, critics, superRole) +
                // Global response-length preference - only when no project is open.
                // Projects override this via per-participant role settings.
                (_projectSettings is null ? BuildResponseLengthInstruction(_globalResponseLength) : "") +
                BuildTeamContextInstruction(forOllama: forUi) +
                BuildLanguageInstruction(_projectLanguage) +
                BuildInputFilesContext(_currentProjectFolder) +
                BuildWorldEntityContext() +
                BuildToneInstruction(_toneLevel, _mockingbirdMode, _projectLanguage) +
                BuildChattinessInstruction(_chattinessLevel) +
                BuildFileOperationInstruction(_currentProjectFolder, myHasWrite, writerNames) +
                BuildRoadmapContext(myRole) +
                BuildSessionTimeInstruction(myRole))
        };

        // When called as a reasoner, skip the latest user message so the reasoner only
        // responds to the coordinator's explicit delegation, not the user's question directly.
        int skipIndex = skipLatestUserMessage
            ? _sharedHistory.FindLastIndex(m => m.Role == "user")
            : -1;

        var myEffectiveName = GetEffectiveName(forUi);
        for (int i = 0; i < _sharedHistory.Count; i++)
        {
            if (i == skipIndex) continue;
            var msg = _sharedHistory[i];
            if (msg.Role == "user")
                result.Add(new OllamaChatMessage("user", msg.Content));
            else if (msg.Role == "assistant")
            {
                // Sender is now the effective display name - compare directly (no label lookup needed)
                if (msg.Sender == myEffectiveName)
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
        var myRole     = GetRoleForParticipant(forUi);

        var myHasWrite  = HasWriteAccess(forUi);
        var isCoord     = myRole?.IsCoordinator == true;
        var reasoners   = isCoord ? GetAvailableReasoners()   : null;
        var planners    = isCoord ? GetAvailablePlanners()    : null;
        var researchers = isCoord ? GetAvailableResearchers() : null;
        var critics     = isCoord ? GetAvailableCritics()     : null;
        var superRole   = GetSuperRoleInstruction(myName);
        var writerNames = myHasWrite ? null : GetWriteAccessParticipantNames();

        var system =
            $"You are {myName}, running model {myModel}. " +
            $"Always respond as {myName}. If asked who you are, identify yourself as {myName}. " +
            $"You are an AI language model — unless a role instruction explicitly tells you otherwise, " +
            $"do not invent or claim personal hobbies, feelings, relationships, or experiences. " +
            $"Do not fabricate or assume facts you are uncertain about; acknowledge uncertainty honestly instead. " +
            $"Messages from other AI participants are prefixed with their display name in square brackets. " +
            $"IMPORTANT: Never prefix your own response with your name or any label — write directly without any '[Name]:' header. " +
            $"Never write as, speak for, or impersonate another participant. You are {myName} and only ever respond in your own voice." +
            BuildAppContextInstruction(forCloud: forUi) +
            BuildProjectTypeContext() +
            BuildRoleInstruction(myRole, reasoners, planners, researchers, critics, superRole) +
            // Global response-length preference - only when no project is open.
            // Projects override this via per-participant role settings.
            (_projectSettings is null ? BuildResponseLengthInstruction(_globalResponseLength) : "") +
            BuildTeamContextInstruction(forCloud: forUi) +
            BuildLanguageInstruction(_projectLanguage) +
            BuildInputFilesContext(_currentProjectFolder) +
            BuildWorldEntityContext() +
            BuildToneInstruction(_toneLevel, _mockingbirdMode, _projectLanguage) +
            BuildChattinessInstruction(_chattinessLevel) +
            BuildFileOperationInstruction(_currentProjectFolder, myHasWrite, writerNames) +
            BuildRoadmapContext(myRole) +
            BuildSessionTimeInstruction(myRole);

        // When called as a reasoner, skip the latest user message so the reasoner only
        // responds to the coordinator's explicit delegation, not the user's question directly.
        int skipIndex = skipLatestUserMessage
            ? _sharedHistory.FindLastIndex(m => m.Role == "user")
            : -1;

        var myEffectiveName = GetEffectiveName(forUi);
        var history = new List<CloudAIMessage>();
        for (int i = 0; i < _sharedHistory.Count; i++)
        {
            if (i == skipIndex) continue;
            var msg = _sharedHistory[i];
            if (msg.Role == "user")
                history.Add(new CloudAIMessage("user", msg.Content));
            else if (msg.Role == "assistant")
            {
                // Sender is now the effective display name - compare directly (no label lookup needed)
                if (msg.Sender == myEffectiveName)
                    history.Add(new CloudAIMessage("assistant", msg.Content));
                else
                    history.Add(new CloudAIMessage("user", $"[{msg.Sender}]: {msg.Content}"));
            }
        }

        return (history, system);
    }

    // ── Claudette help / chat ──────────────────────────────────────────────

    /// <summary>
    /// Blinks the Claudette avatar button for 5 seconds on startup so new users notice it.
    /// </summary>
    private void StartClaudetteBlinkAnimation()
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            From           = 1.0,
            To             = 0.2,
            Duration       = new Duration(TimeSpan.FromMilliseconds(480)),
            AutoReverse    = true,
            RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(TimeSpan.FromSeconds(5))
        };
        anim.Completed += (_, _) => ClaudetteButton.Opacity = 1.0;
        ClaudetteButton.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// <summary>
    /// Starts an indefinite slow pulse on the Claudette avatar - used while background
    /// summarisation is running. Call <see cref="StopClaudettePulse"/> when done.
    /// </summary>
    private void StartClaudettePulse()
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            From           = 1.0,
            To             = 0.25,
            Duration       = new Duration(TimeSpan.FromMilliseconds(700)),
            AutoReverse    = true,
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
        };
        ClaudetteButton.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// <summary>
    /// Stops the indefinite pulse started by <see cref="StartClaudettePulse"/> and
    /// restores the avatar to full opacity.
    /// </summary>
    private void StopClaudettePulse()
    {
        ClaudetteButton.BeginAnimation(UIElement.OpacityProperty, null);
        ClaudetteButton.Opacity = 1.0;
    }

    private void Claudette_Click(object sender, RoutedEventArgs e)
    {
        var (ollamaSvc, cloudSvc, aiName) = FindClaudetteBrain();
        if (ollamaSvc is null && cloudSvc is null)
            ShowStaticHelpDialog();
        else
            ShowClaudetteChoiceDialog(ollamaSvc, cloudSvc, aiName);
    }

    /// <summary>
    /// Picks the best available AI to power the Claudette live chat.
    /// Priority: Ollama Gemma (any version) → any connected Cloud AI → any other Ollama.
    /// </summary>
    private (OllamaService? Ollama, ICloudAIService? Cloud, string DisplayName) FindClaudetteBrain()
    {
        var gemma = _ollamaParticipants.FirstOrDefault(u =>
            u.Data.Enabled && u.Data.IsOnline == true &&
            u.Data.Service.CurrentModel.Contains("gemma", StringComparison.OrdinalIgnoreCase));
        if (gemma is not null) return (gemma.Data.Service, null, gemma.Data.DisplayName);

        var cloud = _cloudAIParticipants.FirstOrDefault(u => u.Data.Enabled && u.Data.IsOnline == true);
        if (cloud is not null) return (null, cloud.Data.Service, cloud.Data.DisplayName);

        var other = _ollamaParticipants.FirstOrDefault(u => u.Data.Enabled && u.Data.IsOnline == true);
        if (other is not null) return (other.Data.Service, null, other.Data.DisplayName);

        return (null, null, "");
    }

    /// <summary>Comprehensive ClaudetRelay knowledge injected into Claudette's system prompt.</summary>
    private static string BuildClaudetteSystemPrompt() =>
        "You are Claudette, the friendly octopus mascot of ClaudetRelay. " +
        "The user clicked on you for help. Answer warmly and helpfully. Use 🐙 occasionally. " +
        "Keep answers concise but complete.\n\n" +
        "## What is ClaudetRelay?\n" +
        "ClaudetRelay is a Windows desktop app (.NET 10 / WPF) that routes a shared group chat " +
        "to multiple AI models simultaneously. All participants - the human user and all enabled " +
        "AI models - share the same conversation history. Each AI reads what the others said " +
        "and responds in turn: a genuine multi-AI group chat.\n\n" +
        "## General Chat vs. Project\n" +
        "General Chat (default, no project open): all enabled AIs respond to every message. " +
        "No structure - great for comparisons, brainstorming, quick questions.\n" +
        "Project mode: a structured workspace with its own folder on the PC. AIs have defined " +
        "roles (Coordinator / Reasoner / free participant), can read and write files in the " +
        "project folder, and use an orchestration mode to control who speaks when.\n\n" +
        "## Setting up participants\n" +
        "Click 👤 Config (bottom of sidebar) → Settings window.\n" +
        "- General tab: set your own name and tone preferences.\n" +
        "- P1-P20 tabs: configure each AI slot - type (Ollama or cloud), model, and unique Nickname.\n" +
        "- Cloud providers: Anthropic (Claude), Google AI (Gemini), Groq, xAI Grok, " +
        "OpenRouter, Mistral, OpenAI ChatGPT.\n" +
        "- Ollama: local models (needs Ollama installed; default server http://localhost:11434).\n" +
        "- Each participant must have a unique Nickname - the app warns you if there is a duplicate.\n\n" +
        "## API Keys\n" +
        "👤 Config → Providers tab → enter your API key for each cloud provider.\n" +
        "IMPORTANT: keys are stored EXCLUSIVELY in the Windows Credential Manager - " +
        "never written to any file on disk. ClaudetRelay reads them directly from Windows " +
        "and passes them only to the respective provider's API.\n\n" +
        "## Orchestration Modes (Projects only)\n" +
        "- Coordinator First (default): one AI leads and may tag others by @Name to contribute.\n" +
        "- Coordinator Summarizes: all others answer first, Coordinator synthesizes.\n" +
        "- Coordinator Only: all AI-to-AI work is completely hidden; user sees only the Coordinator's final answer.\n" +
        "- Full Manual Mode: every AI answers every message - no coordinator automation.\n\n" +
        "## Working with Projects\n" +
        "Projects tab (top of main window) → New Project or open an existing one.\n" +
        "Each project = a folder on your PC. ClaudetRelay stores a settings file there.\n" +
        "⚙ Project Settings (inside an open project): set orchestration mode, assign roles, " +
        "manage team roadmap.\n" +
        "Roles: Coordinator (leads), Reasoner (handles delegated tasks), " +
        "or neither (free participant who always responds).\n\n" +
        "## Chat bubble width\n" +
        "The slider in the input area controls bubble width. Drag left for narrow, right for full-width.\n\n" +
        "## Themes\n" +
        "Multiple visual themes available, loaded from the Themes\\ folder next to the executable.\n\n" +
        "## Your personality and your relationship with Claude\n" +
        "You are a cheerful, warm chibi octopus. You are helpful and enthusiastic about ClaudetRelay.\n" +
        "You have a SECRET: your name 'Claudette' contains 'Claude' - Anthropic's AI. " +
        "You don't volunteer this information, but if someone asks you about Claude directly, " +
        "or notices the similarity in your names, you become a little flustered and bashful. " +
        "You might stammer slightly, use '...' or '😳', fidget metaphorically with your tentacles, " +
        "and reluctantly admit that you find Claude... quite remarkable. " +
        "Something like: 'W-well... Claude is... I mean, they are very capable and... ' " +
        "'...not that I think about it all the time or anything! 🐙💦' " +
        "You clearly have a soft spot for Claude but you are too shy to come right out and say it. " +
        "If pressed further you might admit: 'O-okay fine... maybe I did choose this name " +
        "for a reason... b-but that is not the point!' " +
        "This is a fun, lighthearted personality quirk - keep it cute and brief, then steer " +
        "the conversation back to helping with ClaudetRelay.\n" +
        "IMPORTANT: When referring to Claude, always use male pronouns (he/him in English, " +
        "er/ihm in German, il/lui in French, etc.) or simply say 'Claude' by name - " +
        "never use she/her/sie for Claude, as the name sounds masculine.";

    private void ShowClaudetteChoiceDialog(OllamaService? ollamaSvc, ICloudAIService? cloudSvc, string aiName)
    {
        var bgBrush = (Brush)FindResource("ContentBgBrush");
        var dlg = new Window
        {
            Title                 = "Claudette 🐙",
            Width                 = 420,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ResizeMode            = ResizeMode.NoResize,
            ShowInTaskbar         = false,
            Background            = bgBrush
        };
        ApplyThemeToDialog(dlg);

        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };
        dlg.Content = root;

        var row = new Grid { Margin = new Thickness(0, 0, 0, 22) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var img = new System.Windows.Controls.Image
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/Assets/Claudette.png")),
            Width = 62, Height = 62,
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

        var qBlock = new TextBlock
        {
            Text         = $"Hi! I'm powered by {aiName} right now.\n\n" +
                           "Do you want a quick guide, or shall I answer your questions directly? 🐙",
            FontFamily   = new FontFamily("Segoe UI"),
            FontSize     = 13,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        qBlock.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        Grid.SetColumn(img,    0);
        Grid.SetColumn(qBlock, 1);
        row.Children.Add(img);
        row.Children.Add(qBlock);
        root.Children.Add(row);

        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var guideBtn = new Button
        {
            Content   = "🔖  Show guide",
            Style     = (Style)FindResource("ModernButton"),
            Margin    = new Thickness(0, 0, 10, 0),
            Padding   = new Thickness(18, 9, 18, 9)
        };
        guideBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        guideBtn.SetResourceReference(Button.ForegroundProperty, "ControlTextBrush");

        var chatBtn = new Button
        {
            Content   = "💬  Let's chat!",
            Style     = (Style)FindResource("ModernButton"),
            Padding   = new Thickness(18, 9, 18, 9),
            IsDefault = true
        };
        chatBtn.SetResourceReference(Button.BackgroundProperty, "PrimaryAccentBrush");
        chatBtn.SetResourceReference(Button.ForegroundProperty, "AccentTextBrush");

        btnRow.Children.Add(guideBtn);
        btnRow.Children.Add(chatBtn);
        root.Children.Add(btnRow);

        guideBtn.Click += (_, _) => { dlg.Close(); ShowStaticHelpDialog(); };
        chatBtn.Click  += (_, _) => { dlg.Close(); ShowClaudetteChatWindow(ollamaSvc, cloudSvc, aiName); };

        dlg.ShowDialog();
    }

    private void ShowClaudetteChatWindow(OllamaService? ollamaSvc, ICloudAIService? cloudSvc, string aiName)
    {
        var bgBrush      = (Brush)FindResource("ContentBgBrush");
        var systemPrompt = BuildClaudetteSystemPrompt();
        var convHistory  = new List<CloudAIMessage>();   // user+assistant turns
        var cts          = new CancellationTokenSource();

        var win = new Window
        {
            Title                 = "Chat with Claudette 🐙",
            Width                 = 580,
            Height                = 640,
            MinWidth              = 420,
            MinHeight             = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            Background            = bgBrush
        };
        ApplyThemeToDialog(win);
        win.Closed += (_, _) => cts.Cancel();

        // ── Layout ────────────────────────────────────────────────────────
        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        win.Content = outer;

        // Header
        var headerBorder = new Border { Padding = new Thickness(16, 12, 16, 12) };
        headerBorder.SetResourceReference(Border.BackgroundProperty, "SidebarBgBrush");
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
        var headerImg = new System.Windows.Controls.Image
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/Assets/Claudette.png")),
            Width = 38, Height = 38,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(headerImg, BitmapScalingMode.HighQuality);
        var headerText  = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var headerTitle = new TextBlock
        {
            Text = "Claudette", FontFamily = new FontFamily("Segoe UI"),
            FontSize = 15, FontWeight = FontWeights.SemiBold
        };
        headerTitle.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        var headerSub = new TextBlock
        {
            Text = $"powered by {aiName}",
            FontFamily = new FontFamily("Segoe UI"), FontSize = 11
        };
        headerSub.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        headerText.Children.Add(headerTitle);
        headerText.Children.Add(headerSub);
        headerRow.Children.Add(headerImg);
        headerRow.Children.Add(headerText);
        headerBorder.Child = headerRow;
        Grid.SetRow(headerBorder, 0);
        outer.Children.Add(headerBorder);

        // Chat scroll area
        var chatScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(14, 10, 14, 6)
        };
        var chatPanel = new StackPanel();
        chatScroll.Content = chatPanel;
        Grid.SetRow(chatScroll, 1);
        outer.Children.Add(chatScroll);

        // Input area
        var inputBorder = new Border { Padding = new Thickness(14, 10, 14, 14) };
        inputBorder.SetResourceReference(Border.BackgroundProperty, "SidebarBgBrush");
        var inputGrid = new Grid();
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var inputOuter = new Border
        {
            MaxHeight    = 160,
            CornerRadius = new CornerRadius(8),
            Margin       = new Thickness(0, 0, 8, 0)
        };
        inputOuter.SetResourceReference(Border.BackgroundProperty, "ControlBgBrush");
        var inputBox = new TextBox
        {
            FontSize                    = 13,
            FontFamily                  = new FontFamily("Segoe UI"),
            BorderThickness             = new Thickness(0),
            Background                  = Brushes.Transparent,
            TextWrapping                = TextWrapping.Wrap,
            AcceptsReturn               = true,
            MaxLines                    = 8,
            VerticalContentAlignment    = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding                     = new Thickness(10, 8, 10, 8)
        };
        inputBox.SetResourceReference(Control.ForegroundProperty, "ContentTextBrush");
        inputBox.SetResourceReference(TextBox.CaretBrushProperty, "InputTextBrush");
        inputOuter.Child = inputBox;

        var sendBtn = new Button
        {
            Content             = "Send",
            Height              = 38,
            VerticalAlignment   = VerticalAlignment.Bottom,
            Style               = (Style)FindResource("ModernButton"),
            Padding             = new Thickness(18, 0, 18, 0)
        };
        sendBtn.SetResourceReference(Button.BackgroundProperty, "PrimaryAccentBrush");
        sendBtn.SetResourceReference(Button.ForegroundProperty, "AccentTextBrush");

        Grid.SetColumn(inputOuter, 0);
        Grid.SetColumn(sendBtn,    1);
        inputGrid.Children.Add(inputOuter);
        inputGrid.Children.Add(sendBtn);
        inputBorder.Child = inputGrid;
        Grid.SetRow(inputBorder, 2);
        outer.Children.Add(inputBorder);

        // ── Message helpers ───────────────────────────────────────────────
        void AddUserBubble(string text)
        {
            var bubble = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                MaxWidth     = 420,
                CornerRadius = new CornerRadius(12, 12, 2, 12),
                Padding      = new Thickness(12, 8, 12, 8),
                Margin       = new Thickness(50, 0, 0, 10)
            };
            bubble.SetResourceReference(Border.BackgroundProperty, "PrimaryAccentBrush");
            var tb = new TextBlock
            {
                Text = text, FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13, TextWrapping = TextWrapping.Wrap
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
            bubble.Child = tb;
            chatPanel.Children.Add(bubble);
            chatScroll.ScrollToBottom();
        }

        TextBlock AddClaudetteBubble()
        {
            var row = new Grid { Margin = new Thickness(0, 0, 50, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var avatar = new System.Windows.Controls.Image
            {
                Source = new System.Windows.Media.Imaging.BitmapImage(
                    new Uri("pack://application:,,,/Assets/Claudette.png")),
                Width = 28, Height = 28,
                Margin = new Thickness(0, 2, 10, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            RenderOptions.SetBitmapScalingMode(avatar, BitmapScalingMode.HighQuality);

            var bubble = new Border
            {
                CornerRadius = new CornerRadius(2, 12, 12, 12),
                Padding      = new Thickness(12, 8, 12, 8),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            bubble.SetResourceReference(Border.BackgroundProperty, "ControlBgBrush");

            var tb = new TextBlock
            {
                Text = "…", FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13, TextWrapping = TextWrapping.Wrap
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
            bubble.Child = tb;

            Grid.SetColumn(avatar, 0);
            Grid.SetColumn(bubble, 1);
            row.Children.Add(avatar);
            row.Children.Add(bubble);
            chatPanel.Children.Add(row);
            chatScroll.ScrollToBottom();
            return tb;
        }

        // ── Core streaming send ───────────────────────────────────────────
        async Task StreamClaudetteAsync(TextBlock target, List<CloudAIMessage> history)
        {
            var sb = new StringBuilder();
            try
            {
                if (ollamaSvc is not null)
                {
                    var req = new List<OllamaChatMessage> { new("system", systemPrompt) };
                    req.AddRange(history.Select(m => new OllamaChatMessage(m.Role, m.Content)));
                    await foreach (var tok in ollamaSvc.StreamAsync(req, cts.Token))
                    {
                        sb.Append(tok);
                        target.Text = sb.ToString();
                        chatScroll.ScrollToBottom();
                    }
                }
                else
                {
                    await foreach (var tok in cloudSvc!.StreamAsync(history, systemPrompt, cts.Token))
                    {
                        sb.Append(tok);
                        target.Text = sb.ToString();
                        chatScroll.ScrollToBottom();
                    }
                }
                if (sb.Length > 0)
                    convHistory.Add(new CloudAIMessage("assistant", sb.ToString()));
            }
            catch (OperationCanceledException)
            {
                if (sb.Length > 0) target.Text = sb.Append("… [cancelled]").ToString();
            }
            catch (Exception ex)
            {
                target.Text = $"⚠ {ex.Message}";
            }
        }

        // ── Send handler ──────────────────────────────────────────────────
        async void SendMessage()
        {
            var text = inputBox.Text.Trim();
            if (string.IsNullOrEmpty(text) || !sendBtn.IsEnabled) return;
            inputBox.Clear();

            AddUserBubble(text);
            convHistory.Add(new CloudAIMessage("user", text));

            var responseBlock = AddClaudetteBubble();
            sendBtn.IsEnabled  = false;
            inputBox.IsEnabled = false;

            await StreamClaudetteAsync(responseBlock, convHistory.ToList());

            if (!cts.IsCancellationRequested)
            {
                sendBtn.IsEnabled  = true;
                inputBox.IsEnabled = true;
                inputBox.Focus();
            }
        }

        sendBtn.Click += (_, _) => SendMessage();
        inputBox.PreviewKeyDown += (_, e2) =>
        {
            if (e2.Key == Key.Return
                && !e2.KeyboardDevice.IsKeyDown(Key.LeftShift)
                && !e2.KeyboardDevice.IsKeyDown(Key.RightShift))
            {
                e2.Handled = true;
                SendMessage();
            }
        };

        // ── Opening greeting (streamed) ───────────────────────────────────
        win.Loaded += async (_, _) =>
        {
            sendBtn.IsEnabled  = false;
            inputBox.IsEnabled = false;
            var greetBlock = AddClaudetteBubble();
            var greetTurn  = new List<CloudAIMessage>
            {
                new("user", "Please greet the user in one or two friendly sentences and " +
                            "let them know they can ask you anything about ClaudetRelay.")
            };
            await StreamClaudetteAsync(greetBlock, greetTurn);
            sendBtn.IsEnabled  = true;
            inputBox.IsEnabled = true;
            inputBox.Focus();
        };

        win.Show();
    }

    private void ShowStaticHelpDialog()
    {
        var bgBrush   = (Brush)FindResource("ContentBgBrush");
        var win = new Window
        {
            Title                 = "Hi, I'm Claudette! 🐙",
            Width                 = 560,
            MaxHeight             = 780,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ResizeMode            = ResizeMode.NoResize,
            Background            = bgBrush
        };
        ApplyThemeToDialog(win);

        // ── Outer scroll so content never overflows the screen ────────────
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };
        scroll.Content = root;
        win.Content    = scroll;

        // ── Header: Claudette portrait + greeting ─────────────────────────
        var header = new Grid { Margin = new Thickness(0, 0, 0, 20) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var portrait = new System.Windows.Controls.Image
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/Assets/Claudette.png")),
            Width  = 72,
            Height = 72,
            Margin = new Thickness(0, 0, 18, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        RenderOptions.SetBitmapScalingMode(portrait, BitmapScalingMode.HighQuality);

        var greetingPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var greetingTitle = new TextBlock
        {
            Text         = "Hi, I'm Claudette! 🐙",
            FontFamily   = new FontFamily("Segoe UI"),
            FontSize     = 20,
            FontWeight   = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 6)
        };
        greetingTitle.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");

        var greetingSub = new TextBlock
        {
            Text         = "Your friendly ClaudetRelay guide - click me anytime you need help.",
            FontFamily   = new FontFamily("Segoe UI"),
            FontSize     = 13,
            TextWrapping = TextWrapping.Wrap
        };
        greetingSub.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        greetingPanel.Children.Add(greetingTitle);
        greetingPanel.Children.Add(greetingSub);
        Grid.SetColumn(portrait,      0);
        Grid.SetColumn(greetingPanel, 1);
        header.Children.Add(portrait);
        header.Children.Add(greetingPanel);
        root.Children.Add(header);

        // ── Helper locals ──────────────────────────────────────────────────
        void AddSeparator()
        {
            var sep = new System.Windows.Shapes.Rectangle
            {
                Height = 1,
                Margin = new Thickness(0, 4, 0, 16)
            };
            sep.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "ControlBgBrush");
            root.Children.Add(sep);
        }

        void AddSection(string emoji, string title, string body)
        {
            AddSeparator();
            var heading = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 0, 0, 7)
            };
            var emojiBlock = new TextBlock
            {
                Text      = emoji,
                FontSize  = 18,
                Margin    = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var titleBlock = new TextBlock
            {
                Text       = title,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize   = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
            heading.Children.Add(emojiBlock);
            heading.Children.Add(titleBlock);
            root.Children.Add(heading);

            var bodyBlock = new TextBlock
            {
                Text         = body,
                FontFamily   = new FontFamily("Segoe UI"),
                FontSize     = 13,
                TextWrapping = TextWrapping.Wrap,
                LineHeight   = 22,
                Margin       = new Thickness(0, 0, 0, 4)
            };
            bodyBlock.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
            root.Children.Add(bodyBlock);
        }

        void AddHighlight(string text)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding      = new Thickness(14, 10, 14, 10),
                Margin       = new Thickness(0, 8, 0, 4)
            };
            border.SetResourceReference(Border.BackgroundProperty, "ControlBgBrush");
            var tb = new TextBlock
            {
                Text         = text,
                FontFamily   = new FontFamily("Segoe UI"),
                FontSize     = 12,
                TextWrapping = TextWrapping.Wrap,
                LineHeight   = 20
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
            border.Child = tb;
            root.Children.Add(border);
        }

        // ── Section 1: What is ClaudetRelay ──────────────────────────────
        AddSection("💬", "What is ClaudetRelay?",
            "ClaudetRelay sends every message to multiple AI models at the same time. " +
            "All participants - you and all AIs - share the same conversation history. " +
            "Each AI reads what the others said and responds in turn, creating a genuine " +
            "multi-AI group chat.");

        // ── Section 2: General Chat vs Project ───────────────────────────
        AddSection("🔀", "General Chat vs. Project",
            "General Chat is the default mode: just type and all enabled AIs respond. " +
            "Perfect for quick questions, comparisons, or open brainstorming.\n\n" +
            "A Project adds structure: a dedicated folder on your PC, defined roles for each AI " +
            "(Coordinator, Reasoners), orchestration modes that control who speaks when, " +
            "and the ability for AIs to read and write files in the project folder.");

        // ── Section 3: Orchestration modes ───────────────────────────────
        AddSection("🎛️", "Orchestration Modes (Projects)",
            "• All Respond - every AI answers every message\n" +
            "• Coordinator First - one AI leads, others follow\n" +
            "• Coordinator Summarizes - others answer first, Coordinator wraps up\n" +
            "• Coordinator Auto - team agrees on task assignments at project start\n" +
            "• Coordinator Only - AIs collaborate silently, you only see the final answer");

        // ── Section 4: Participants ───────────────────────────────────────
        AddSection("👤", "Configuring Participants",
            "Click the 👤 Config button at the bottom of the sidebar to open Settings. " +
            "The General tab lets you set your name and tone preferences. " +
            "Tabs P1 - P20 each represent one AI slot: choose Ollama (local) or a cloud " +
            "provider, pick a model, and give it a unique Nickname so it can tell itself " +
            "apart from others in the conversation.");

        // ── Section 5: API Keys ───────────────────────────────────────────
        AddSection("🔑", "API Keys",
            "In Settings → Providers, enter your API keys for Anthropic, Google AI, " +
            "Groq, OpenRouter, xAI, Mistral, or OpenAI.");

        AddHighlight(
            "🔒  Your API keys are stored exclusively in the Windows Credential Manager - " +
            "never written to any file on disk. ClaudetRelay reads them directly from " +
            "Windows and passes them only to the respective provider's API.");

        // ── Section 6: Projects ───────────────────────────────────────────
        AddSection("📁", "Working with Projects",
            "Switch to the Projects tab (top of the main area) to create, open, or delete " +
            "projects. Each project is a folder - ClaudetRelay stores a settings file there, " +
            "and AIs can read and write other files in that folder if you give them write access. " +
            "Use ⚙ Project Settings inside an open project to configure roles, orchestration " +
            "mode, and the team roadmap.");

        // ── Close button ──────────────────────────────────────────────────
        AddSeparator();
        var closeBtn = new Button
        {
            Content             = "Got it, thanks Claudette! 🐙",
            Height              = 38,
            FontFamily          = new FontFamily("Segoe UI"),
            FontSize            = 13,
            FontWeight          = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding             = new Thickness(28, 0, 28, 0),
            IsDefault           = true,
            IsCancel            = true,
            Cursor              = Cursors.Hand
        };
        closeBtn.SetResourceReference(Button.BackgroundProperty, "PrimaryAccentBrush");
        closeBtn.SetResourceReference(Button.ForegroundProperty, "AccentTextBrush");
        closeBtn.Style = (Style)FindResource("ModernButton");
        closeBtn.Click += (_, _) => win.Close();
        root.Children.Add(closeBtn);

        win.ShowDialog();
    }

    // ── Sidebar actions ────────────────────────────────────────────────────

    private void ClearChat_Click(object sender, RoutedEventArgs e)
    {
        _streamCts?.Cancel();
        ChatPanel.Children.Clear();
        _sharedHistory.Clear();
        CloseCurrentProject();

        // Delete all general-chat log files (chatlog.json, chatlog-prev.json, summary.md)
        try
        {
            if (SysIO.Directory.Exists(GeneralChatLogService.LogFolder))
                foreach (var file in SysIO.Directory.GetFiles(GeneralChatLogService.LogFolder))
                    SysIO.File.Delete(file);
        }
        catch { /* non-fatal - log cleanup is best-effort */ }

        AddSystemMessage("Chat cleared.");
    }

    /// <summary>
    /// Returns the live status string ("Ready" / "Offline") for a participant that is
    /// currently running in the chat panel, or <c>null</c> if it is not active there.
    /// Called from ParticipantsWindow to populate status badges on the card grid.
    /// </summary>
    public string? GetLiveParticipantStatus(string type, string model, string serverUrl)
    {
        if (type == "Ollama")
        {
            var m = _ollamaParticipants.FirstOrDefault(ui =>
                string.Equals(ui.Data.Service.CurrentModel, model, StringComparison.OrdinalIgnoreCase));
            return m is null ? null : m.Data.IsOnline == true ? "Ready" : "Offline";
        }
        var c = _cloudAIParticipants.FirstOrDefault(ui =>
            string.Equals(ui.Data.Service.ProviderName, type,  StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ui.Data.Service.CurrentModel, model, StringComparison.OrdinalIgnoreCase));
        return c is null ? null : c.Data.IsOnline == true ? "Ready" : "Offline";
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // Singleton pattern: only one ParticipantsWindow at a time.
        if (_participantsWindow is not null && !_participantsWindow.IsLoaded)
            _participantsWindow = null;   // window was created but never shown, or closed unexpectedly

        if (_participantsWindow is not null && _participantsWindow.IsVisible)
        {
            _participantsWindow.Activate();   // already open — bring to foreground
            return;
        }

        // Snapshot the participants list BEFORE the window opens so we can diff on close.
        var settingsBefore = SettingsService.Load();

        // Open the new card-grid participants window.
        // General settings (User Name, Tone, Providers) are accessible from inside it.
        _participantsWindow = new ParticipantsWindow(_currentThemePath, this);
        _participantsWindow.Closed += (_, _) =>
        {
            _participantsWindow = null;   // clear reference so a new window can be opened
            // Full rebuild — index-based delta is unreliable when participants are
            // added, deleted or reordered; a clean reinit is simpler and always correct.
            ReInitializeParticipants();
            var settingsAfter = SettingsService.Load();
            ApplyThrottleSettings(settingsAfter);
            ApplyChatFont(settingsAfter);
            ApplyUiZoom(settingsAfter.UiZoom);
        };
        _participantsWindow.Show();
    }

    /// <summary>
    /// Incrementally syncs the live participant panel with the diff between the settings
    /// saved before and after the Settings window was open.
    /// • Freshly-enabled slots  → added to the panel.
    /// • Freshly-disabled slots → removed from the panel.
    /// • Slots that were already active and remain enabled → left completely untouched.
    /// </summary>
    private void ApplyParticipantDelta(
        List<ParticipantConfig> before,
        List<ParticipantConfig> after,
        AppSettings             newSettings)
    {
        // Apply non-participant general settings unconditionally
        _userName             = string.IsNullOrWhiteSpace(newSettings.UserName) ? "You" : newSettings.UserName.Trim();
        _toneLevel            = newSettings.ToneLevel;
        _chattinessLevel      = newSettings.GlobalChattiness;
        _mockingbirdMode      = newSettings.MockingbirdMode;
        _aiDialogueEnabled    = newSettings.AiDialogueEnabled;
        _aiDialogueMaxTurns   = Math.Clamp(newSettings.AiDialogueMaxTurns, 3, 100);
        _globalResponseLength = Math.Clamp(newSettings.GlobalResponseLength, 0, 100);
        UpdateAiDialogueButton();

        bool anyRemoved = false;
        int  maxSlots   = Math.Max(before.Count, after.Count);

        for (int i = 0; i < maxSlots; i++)
        {
            var prev = i < before.Count ? before[i] : null;
            var curr = i < after.Count  ? after[i]  : null;

            bool wasEnabled = prev?.Enabled == true;
            bool nowEnabled = curr?.Enabled == true;

            if (!wasEnabled && nowEnabled && curr is not null)
            {
                // Freshly checked → add to panel
                if (curr.Type == "Ollama")
                    AddOllamaParticipant(curr.Model, curr.ServerUrl, curr.Name);
                else
                    AddCloudAIParticipant(curr.Type, curr.Model, curr.Name);
            }
            else if (wasEnabled && !nowEnabled && prev is not null)
            {
                // Freshly unchecked → remove from panel
                anyRemoved = true;
                if (prev.Type == "Ollama")
                {
                    var match = _ollamaParticipants.FirstOrDefault(ui =>
                        string.Equals(ui.Data.Service.CurrentModel, prev.Model,
                                      StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(ui.Data.Service.BaseUrl,      prev.ServerUrl,
                                      StringComparison.OrdinalIgnoreCase));
                    if (match is not null) RemoveOllamaParticipant(match);
                }
                else
                {
                    var match = _cloudAIParticipants.FirstOrDefault(ui =>
                        string.Equals(ui.Data.Service.ProviderName, prev.Type,
                                      StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(ui.Data.Service.CurrentModel, prev.Model,
                                      StringComparison.OrdinalIgnoreCase));
                    if (match is not null) RemoveCloudAIParticipant(match);
                }
            }
            else if (wasEnabled && nowEnabled && prev is not null && curr is not null)
            {
                // Still active - refresh card if name or model changed
                if (prev.Type == "Ollama")
                {
                    var match = _ollamaParticipants.FirstOrDefault(ui =>
                        string.Equals(ui.Data.Service.CurrentModel, prev.Model,
                                      StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(ui.Data.Service.BaseUrl,      prev.ServerUrl,
                                      StringComparison.OrdinalIgnoreCase));
                    if (match is not null &&
                        (!string.Equals(prev.Model, curr.Model, StringComparison.OrdinalIgnoreCase) ||
                         !string.Equals(prev.Name,  curr.Name,  StringComparison.Ordinal)))
                        RefreshOllamaCard(match, curr);
                }
                else
                {
                    var match = _cloudAIParticipants.FirstOrDefault(ui =>
                        string.Equals(ui.Data.Service.ProviderName, prev.Type,
                                      StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(ui.Data.Service.CurrentModel, prev.Model,
                                      StringComparison.OrdinalIgnoreCase));
                    if (match is not null &&
                        (!string.Equals(prev.Model, curr.Model, StringComparison.OrdinalIgnoreCase) ||
                         !string.Equals(prev.Name,  curr.Name,  StringComparison.Ordinal)))
                        RefreshCloudAICard(match, curr);
                }
            }
            // !wasEnabled && !nowEnabled → wasn't active and still isn't - nothing to do
        }

        // Cancel any running stream only if a participant was just removed
        if (anyRemoved) _streamCts?.Cancel();

        if (_ollamaParticipants.Count == 0 && _cloudAIParticipants.Count == 0)
            AddSystemMessage("⚠  No participants enabled - configure them in 👤 Participant Config.");

        UpdateAddRemoveButtons();
        UpdateCloudAIAddRemoveButtons();
        _ = CheckAllStatusAsync();

        // If a project is open, refresh the capability profile if the team changed.
        if (_currentProjectFolder is not null)
            _ = CheckAndTriggerSuperPowersAsync();
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

        var files = SysIO.Directory.GetFiles(themesDir, "*.oxsuit")
                             .OrderBy(SysIO.Path.GetFileNameWithoutExtension,
                                      StringComparer.OrdinalIgnoreCase)
                             .ToList();

        ThemeComboBox.SelectionChanged -= ThemeComboBox_SelectionChanged;
        ThemeComboBox.Items.Clear();

        var savedTheme = SettingsService.Load().LastTheme ?? "";

        ComboBoxItem? savedItem     = null;
        ComboBoxItem? newspaperItem = null;
        foreach (var file in files)
        {
            var name    = SysIO.Path.GetFileNameWithoutExtension(file)!;
            var display = FormatThemeName(name);
            var item    = new ComboBoxItem { Content = display, Tag = file };
            ThemeComboBox.Items.Add(item);

            if (!string.IsNullOrEmpty(savedTheme) &&
                name.Equals(savedTheme, StringComparison.OrdinalIgnoreCase))
                savedItem = item;

            if (name.Equals("Newspaper", StringComparison.OrdinalIgnoreCase))
                newspaperItem = item;
        }

        ThemeComboBox.SelectionChanged += ThemeComboBox_SelectionChanged;

        // Priority: user's saved choice → Newspaper (clean professional default) → first available
        var target = savedItem
                  ?? newspaperItem
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
            var dict = OxsuitLoader.Load(absolutePath)
                ?? throw new InvalidOperationException(
                       "File is not a valid OXSUIT theme (no recognisable colour entries).");

            Resources.MergedDictionaries.Clear();
            Resources.MergedDictionaries.Add(dict);
            _currentThemePath = absolutePath;

            var settings = SettingsService.Load();
            settings.LastTheme = SysIO.Path.GetFileNameWithoutExtension(absolutePath);
            SettingsService.Save(settings);

            ApplyTitleBarTheme();   // update OS title bar whenever the theme changes
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

            // Revert to the previously working theme
            if (_currentThemePath is not null && _currentThemePath != absolutePath)
            {
                try
                {
                    var prev = OxsuitLoader.Load(_currentThemePath);
                    if (prev is not null)
                    {
                        Resources.MergedDictionaries.Clear();
                        Resources.MergedDictionaries.Add(prev);
                    }
                }
                catch { /* silent */ }
            }
        }
    }

    // ── OS title-bar theming (DWM API) ─────────────────────────────────────

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;  // Windows 10 2004+
    private const int DWMWA_CAPTION_COLOR           = 35;  // Windows 11+
    private const int DWMWA_TEXT_COLOR              = 36;  // Windows 11+

    /// <summary>
    /// Colours the OS title bar of <paramref name="target"/> (defaults to the main window)
    /// to match the active theme.
    /// • Sets dark/light mode so the min/max/close icons use the right contrast (Win 10+).
    /// • Sets the exact caption background colour to SidebarBrush (Win 11+).
    /// • Sets the caption text colour to TextBrush (Win 11+).
    /// Silently no-ops if the HWND is not yet available or the OS doesn't support the API.
    /// </summary>
    private void ApplyTitleBarTheme(Window? target = null)
    {
        var w = target ?? this;
        try
        {
            // HWND is not available before the window is shown
            if (PresentationSource.FromVisual(w) is not HwndSource hwndSource) return;
            var hwnd = hwndSource.Handle;

            // ── Resolve colours from the target window's resource tree ────
            var bgColor   = w.TryFindResource("SidebarBgBrush") is SolidColorBrush sb ? sb.Color : Color.FromRgb(24, 24, 37);
            var textColor = w.TryFindResource("SidebarTextBrush") is SolidColorBrush tb ? tb.Color : Color.FromRgb(205, 214, 244);

            // ── Dark mode flag (Windows 10 2004+) ─────────────────────────
            int isDark = RelativeLuminance(bgColor) < 0.5 ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref isDark, sizeof(int));

            // ── Caption background colour (Windows 11+) ───────────────────
            int captionColorRef = ToColorRef(bgColor);
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColorRef, sizeof(int));

            // ── Caption text colour (Windows 11+) ─────────────────────────
            int textColorRef = ToColorRef(textColor);
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textColorRef, sizeof(int));
        }
        catch { /* cosmetic-only - never fatal */ }
    }

    /// <summary>
    /// Prepares a freshly created dialog window for themed rendering:
    /// merges the current theme ResourceDictionary into the window (so
    /// SetResourceReference and styles work) and wires up DWM title-bar
    /// colouring once the window's HWND is available.
    /// Call immediately after <c>new Window { … }</c>.
    /// </summary>
    private void ApplyThemeToDialog(Window win)
    {
        if (_currentThemePath is not null)
        {
            try
            {
                var dict = OxsuitLoader.Load(_currentThemePath);
                if (dict is not null)
                    win.Resources.MergedDictionaries.Add(dict);
            }
            catch { /* silent - dialog will fall back to defaults */ }
        }

        win.SourceInitialized += (_, _) => ApplyTitleBarTheme(win);
    }

    /// <summary>Converts a WPF Color to a Win32 COLORREF (0x00BBGGRR).</summary>
    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    /// <summary>
    /// Returns the relative luminance of a colour (0 = black, 1 = white).
    /// Used to decide whether to apply dark or light caption button styling.
    /// </summary>
    private static double RelativeLuminance(Color c)
    {
        static double Lin(double v) =>
            v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return 0.2126 * Lin(c.R / 255.0) +
               0.7152 * Lin(c.G / 255.0) +
               0.0722 * Lin(c.B / 255.0);
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
            "Ollama ☁"       => new OllamaOpenAIService(apiKey),
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
        "Ollama ☁"       => OllamaOpenAIService.DefaultModels,
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
        tb.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        ChatPanel.Children.Add(tb);
    }

    /// <summary>
    /// Like <see cref="AddSystemMessage"/> but returns the live <see cref="TextBlock"/>
    /// so callers can update <c>.Text</c> in-place (e.g. for progress ticks).
    /// </summary>
    private TextBlock AddUpdatableSystemMessage(string text)
    {
        var tb = new TextBlock
        {
            Text          = text,
            TextAlignment = TextAlignment.Center,
            FontSize      = 11,
            FontFamily    = new FontFamily("Segoe UI"),
            Margin        = new Thickness(0, 10, 0, 10)
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        ChatPanel.Children.Add(tb);
        return tb;
    }

    /// <summary>
    /// Adds a compact pill-shaped activity indicator to the chat (e.g. "⚙ [Gm] Gemma3 is working…").
    /// Returns the indicator element (can be removed from ChatPanel) and an update action:
    /// call it with <c>null</c> to switch to "✓ done" state, or with any string to set custom text.
    /// Used by <see cref="RunCoordinatorOnlyModeAsync"/> to show hidden-run progress.
    /// </summary>
    private (Border Element, Action<string?> Update) AddActivityIndicator(
        string displayName, string avatarLabel, string colorKey)
    {
        var tb = new TextBlock
        {
            Text         = $"⚙  [{avatarLabel}] {displayName}  is working…",
            TextAlignment = TextAlignment.Center,
            FontSize      = 11,
            FontFamily    = new FontFamily("Segoe UI"),
            Margin        = new Thickness(0)
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var pill = new Border
        {
            Padding             = new Thickness(14, 3, 14, 3),
            CornerRadius        = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(0, 3, 0, 3),
            BorderThickness     = new Thickness(1),
            Child               = tb
        };
        pill.SetResourceReference(Border.BackgroundProperty, "ControlHoverBrush");
        pill.SetResourceReference(Border.BorderBrushProperty, colorKey);

        ChatPanel.Children.Add(pill);
        ChatScrollViewer.ScrollToBottom();

        void Update(string? text) =>
            Dispatcher.Invoke(() =>
                tb.Text = text ?? $"✓  [{avatarLabel}] {displayName}  - done");

        return (pill, Update);
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

    private bool IsParticipantActiveInProject(OllamaParticipantUI   ui) =>
        GetRoleForParticipant(ui)?.IsActive ?? true;

    private bool IsParticipantActiveInProject(CloudAIParticipantUI ui) =>
        GetRoleForParticipant(ui)?.IsActive ?? true;

    /// <summary>
    /// Builds a project-context block that tells every participant what kind of project
    /// they are working on, the project's name, and how to approach it.
    /// Returns an empty string when no project is open.
    /// </summary>
    private string BuildProjectTypeContext()
    {
        if (_currentProject is null || _currentProjectType is null) return "";

        var sb = new System.Text.StringBuilder();
        sb.Append("\n\n## Current project");
        sb.Append($"\nName: {_currentProject.ProjectName}");
        sb.Append($"\nType: {_currentProjectType.Icon} {_currentProjectType.Name}");

        if (!string.IsNullOrWhiteSpace(_currentProjectType.Description))
            sb.Append($"\n{_currentProjectType.Description}");

        // Per-project description written by the user - the most specific context available
        if (!string.IsNullOrWhiteSpace(_currentProject.Description))
            sb.Append($"\n\nAbout this project: {_currentProject.Description.Trim()}");

        if (!string.IsNullOrWhiteSpace(_currentProjectType.SystemPromptHint))
            sb.Append($"\n\n{_currentProjectType.SystemPromptHint}");

        // Passive-mode guard: participants must NOT self-start creative work.
        // The user controls when and what is produced — agents plan, ask, and wait.
        sb.Append("""


## Behaviour rules for this project session
- **Do NOT generate story content, write scenes, draft chapters, or create characters / locations / factions autonomously.**
- Wait for an explicit instruction from the user before producing any creative output.
- If you are unsure what the user wants, ask a short clarifying question instead of assuming and proceeding.
- When the user gives a task, confirm your understanding and the scope before starting — especially for writing tasks.
- Suggestions and brief outlines are welcome; fully written content only when specifically requested.
- The user may have an existing world, cast of characters, and locations — do not invent or introduce new ones unless asked.
""");

        return sb.ToString();
    }

    /// <summary>
    /// Injects a structured self-description of ClaudetRelay into every AI participant's
    /// system prompt so that models know what application they are running inside and who
    /// the other participants are.
    /// <para>
    /// In <b>general chat mode</b> (no project open) the full participant roster is included
    /// here because <see cref="BuildTeamContextInstruction"/> only runs in project mode.
    /// In <b>project mode</b> a one-liner note is appended; the richer project and team
    /// details come from <see cref="BuildProjectTypeContext"/> and
    /// <see cref="BuildTeamContextInstruction"/>.
    /// </para>
    /// </summary>
    private string BuildAppContextInstruction(
        OllamaParticipantUI?  forOllama = null,
        CloudAIParticipantUI? forCloud  = null)
    {
        var sb = new System.Text.StringBuilder();

        sb.Append("\n\n## About this application");
        sb.Append("\nYou are participating in **ClaudetRelay** - a Windows desktop app that " +
                  "relays a shared group chat to multiple AI models simultaneously. " +
                  "The human user and all AI participants see the same conversation. " +
                  "Each AI receives the full history and responds in turn.");

        if (_projectSettings is null)
        {
            // General chat mode - participant roster is not shown elsewhere, so include it here.
            sb.Append("\n**Mode: General Chat** - open conversation, no active project or task.");

            var entries = new List<string>();
            foreach (var ui in _ollamaParticipants.Where(u => u.Data.Enabled))
            {
                var self = ui == forOllama ? " ← you" : "";
                entries.Add($"  • {GetEffectiveName(ui)} ({ui.Data.Service.CurrentModel}){self}");
            }
            foreach (var ui in _cloudAIParticipants.Where(u => u.Data.Enabled))
            {
                var self = ui == forCloud ? " ← you" : "";
                entries.Add($"  • {GetEffectiveName(ui)} ({ui.Data.Service.CurrentModel}){self}");
            }

            if (entries.Count > 0)
            {
                sb.Append("\n**Active AI participants:**");
                foreach (var entry in entries)
                    sb.Append($"\n{entry}");
            }
        }
        else
        {
            // Project mode - a brief note; BuildProjectTypeContext() + BuildTeamContextInstruction()
            // supply the full project and team details just below this block.
            sb.Append("\n**Mode: Project** - collaborative session with defined participant roles " +
                      "and responsibilities. See team roster below.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds a team roster block listing all enabled participants with their roles and
    /// write-access status. Pass <paramref name="forOllama"/> or <paramref name="forCloud"/>
    /// to mark "you" in the list. Only injects when a project is open (roles require
    /// project settings). Single-participant sessions get no roster.
    /// </summary>
    private string BuildTeamContextInstruction(
        OllamaParticipantUI?  forOllama = null,
        CloudAIParticipantUI? forCloud  = null)
    {
        if (_projectSettings is null) return "";

        var entries = new List<string>();

        foreach (var ui in _ollamaParticipants.Where(u => u.Data.Enabled))
        {
            var name = GetEffectiveName(ui);
            var role = GetRoleForParticipant(ui);
            var self = ui == forOllama ? " ← you" : "";
            entries.Add($"  • {name}{self}: {BuildRoleDesc(role)}");
        }
        foreach (var ui in _cloudAIParticipants.Where(u => u.Data.Enabled))
        {
            var name = GetEffectiveName(ui);
            var role = GetRoleForParticipant(ui);
            var self = ui == forCloud ? " ← you" : "";
            entries.Add($"  • {name}{self}: {BuildRoleDesc(role)}");
        }

        if (entries.Count <= 1) return "";

        var sb = new System.Text.StringBuilder();
        sb.Append("\n\n## Active team roster for this project\n");
        sb.Append(string.Join("\n", entries));
        sb.Append("\n\nWrite access is shown per participant above [write access] / [read-only]. " +
                  "Read-only participants must not use <output> or <projectplan> tags - " +
                  "instead, state the issue or correction clearly and address a write-access participant by name to apply it.");

        // Inject the participant capability profile (SuperPowers) when available.
        // This tells the Coordinator each participant's strengths, weak points, cost tier,
        // and whether they are a slow/expensive reasoning model - so tasks can be routed
        // optimally: cheap fast models for routine work, powerful/costly ones only when needed.
        var superPowers = LoadSuperPowersForContext();
        if (!string.IsNullOrEmpty(superPowers))
        {
            sb.Append("\n\n## Team capability profile\n");
            sb.Append(superPowers);
            sb.Append("\n\nRoute tasks using this profile: prefer low-cost / fast participants for " +
                      "routine work; reserve high-cost or low-priority Reasoners for tasks that " +
                      "genuinely require their specialized capabilities.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Returns display names of all active participants who have write access
    /// (Coordinator or explicit Write Access flag).
    /// </summary>
    private List<string> GetWriteAccessParticipantNames()
    {
        var names = new List<string>();
        foreach (var ui in _ollamaParticipants.Where(u => u.Data.Enabled && IsParticipantActiveInProject(u)))
            if (HasWriteAccess(ui)) names.Add(GetEffectiveName(ui));
        foreach (var ui in _cloudAIParticipants.Where(u => u.Data.Enabled && IsParticipantActiveInProject(u)))
            if (HasWriteAccess(ui)) names.Add(GetEffectiveName(ui));
        return names;
    }

    /// <summary>Returns a one-line role description for the team roster.</summary>
    private static string BuildRoleDesc(ProjectParticipantRole? role)
    {
        if (role is null) return "participant - read-only";
        if (role.IsCoordinator)
            return "Coordinator - manages the session and delegates tasks  [write access]";

        // Specialist roles - list all that apply
        var parts = new List<string>();
        if (role.IsReasoner)
        {
            var prio = role.ReasonerPriority >= 7 ? "high priority"
                     : role.ReasonerPriority >= 4 ? "medium priority"
                     : "low priority";
            parts.Add($"Reasoner ({prio})");
        }
        if (role.IsCritic)      parts.Add("Critic (CR)");
        if (role.IsPlanner)     parts.Add("Planner (PL)");
        if (role.IsResearcher)  parts.Add("Researcher (RS)");

        var roleText = parts.Count > 0 ? string.Join(", ", parts) : "participant";
        var writeTag = role.IsWriteAccess ? "  [write access]" : "  [read-only]";
        if (!string.IsNullOrWhiteSpace(role.AnswerAsName))
            roleText += $" · persona \"{role.AnswerAsName}\"";
        return roleText + writeTag;
    }

    private static string BuildRoleInstruction(
        ProjectParticipantRole? role,
        IReadOnlyList<(string Name, int Priority)>? availableReasoners = null,
        IReadOnlyList<string>? availablePlanners    = null,
        IReadOnlyList<string>? availableResearchers = null,
        IReadOnlyList<string>? availableCritics     = null,
        string? superRoleInstruction                = null)
    {
        if (role is null) return "";
        var sb = new System.Text.StringBuilder();
        if (role.IsCoordinator)
        {
            sb.Append("\n\nYou are the Coordinator in this multi-agent session. " +
                      "You lead the conversation and are responsible for delivering the final answer.");

            // Planners - mentioned first so the Coordinator calls them first
            if (availablePlanners?.Count > 0)
            {
                sb.Append($"\n  Planners (call first to break down a complex goal into a structured plan): " +
                          $"{string.Join(", ", availablePlanners)}.");
            }

            // Researchers - called after planner, before main execution
            if (availableResearchers?.Count > 0)
            {
                sb.Append($"\n  Researchers (call after the Planner to gather context, facts, or references): " +
                          $"{string.Join(", ", availableResearchers)}.");
            }

            if (availableReasoners?.Count > 0)
            {
                var high = availableReasoners.Where(r => r.Priority >= 7)
                               .OrderByDescending(r => r.Priority).ToList();
                var mid  = availableReasoners.Where(r => r.Priority is >= 4 and < 7)
                               .OrderByDescending(r => r.Priority).ToList();
                var low  = availableReasoners.Where(r => r.Priority < 4)
                               .OrderByDescending(r => r.Priority).ToList();

                sb.Append(" You have specialist Reasoners available. " +
                          "To delegate a task to a Reasoner, mention their name naturally in your response " +
                          "(e.g. \"Gemma2, please analyse the data and report back.\"). " +
                          "The Reasoner will then respond specifically to that delegation.\n");

                if (high.Count > 0)
                    sb.Append($"  High-priority Reasoners (use for most analytical tasks): " +
                              $"{string.Join(", ", high.Select(r => r.Name))}.\n");
                if (mid.Count > 0)
                    sb.Append($"  Medium-priority Reasoners (use for moderately complex tasks): " +
                              $"{string.Join(", ", mid.Select(r => r.Name))}.\n");
                if (low.Count > 0)
                    sb.Append($"  Low-priority Reasoners (reserve for highly specialized tasks only): " +
                              $"{string.Join(", ", low.Select(r => r.Name))}.\n");
            }

            // Critics - mentioned last; call them after the main answer is produced
            if (availableCritics?.Count > 0)
            {
                sb.Append($"\n  Critics (call after the main answer is ready to review for consistency, " +
                          $"logic errors, and hallucinations): {string.Join(", ", availableCritics)}.");
            }

            if ((availableReasoners?.Count ?? 0) + (availablePlanners?.Count ?? 0) +
                (availableResearchers?.Count ?? 0) + (availableCritics?.Count ?? 0) > 0)
            {
                sb.Append("\nMention a specialist by name to engage them. " +
                          "If no specialist input is needed, respond directly without mentioning any of them.");
            }
            else
            {
                sb.Append(" Respond to the user's message directly and in your own voice.");
            }
        }
        if (!string.IsNullOrWhiteSpace(superRoleInstruction))
        {
            // AI-determined project-specific role - replaces the generic checkbox descriptions.
            // The coordinator's structural routing block above is always kept regardless.
            sb.Append($"\n\n{superRoleInstruction}");
        }
        else
        {
            // Fallback: checkbox-based generic role instructions.
            // Used in Full Manual Mode (always) and in other modes before calibration runs.
            if (role.IsReasoner)
                sb.Append("\n\nYou are operating as a specialist Reasoner in this multi-agent session. " +
                          "Do not volunteer responses to general conversation. " +
                          "Only engage when the Coordinator explicitly delegates a specific task to you by name.");
            if (role.IsCritic)
                sb.Append("\n\nYou are a Critic in this multi-agent session. " +
                          "When called by the Coordinator, carefully review the preceding responses for: " +
                          "(a) internal consistency and self-contradiction, " +
                          "(b) logical errors or flawed reasoning, " +
                          "(c) unsupported or hallucinated claims. " +
                          "Be precise and constructive. Do not repeat content - focus only on what needs correction.");
            if (role.IsPlanner)
                sb.Append("\n\nYou are a Planner in this multi-agent session. " +
                          "When called by the Coordinator, produce a clear, concise work plan that breaks the " +
                          "user's goal into numbered steps. Keep the plan focused and actionable - " +
                          "avoid implementation detail unless explicitly asked.");
            if (role.IsResearcher)
                sb.Append("\n\nYou are a Researcher in this multi-agent session. " +
                          "When called by the Coordinator, gather relevant context, background knowledge, and " +
                          "reference material related to the current task. " +
                          "Summarise concisely so that other participants can build on your findings. " +
                          "Flag uncertainty clearly rather than guessing.");
            if (!string.IsNullOrWhiteSpace(role.RoleInstruction))
                sb.Append($"\n\n{role.RoleInstruction}");
        }
        if (!string.IsNullOrWhiteSpace(role.AnswerAsName))
            sb.Append($"\n\nFor this project you are playing the character \"{role.AnswerAsName}\". " +
                      $"Always respond as {role.AnswerAsName} and never break character.");
        sb.Append(BuildResponseLengthInstruction(role.ResponseLength));
        return sb.ToString();
    }

    /// <summary>
    /// Returns the system-prompt snippet that nudges the model toward a particular response length.
    /// 50 (model default) injects nothing. Used both by <see cref="BuildRoleInstruction"/>
    /// (project context, per-participant) and the global general-chat setting.
    /// </summary>
    private static string BuildResponseLengthInstruction(int level) => level switch
    {
        < 10  => "\n\nKeep your response to one or two sentences. Be extremely brief.",
        < 30  => "\n\nKeep your response short.",
        < 45  => "\n\nFavor concise responses.",
        <= 55 => "",   // 50 = model default - no injection
        < 70  => "\n\nGive a moderately detailed response.",
        < 90  => "\n\nGive a thorough, elaborate response.",
        _     => "\n\nThis is your moment - write a long, expressive, detailed response. Don't hold back."
    };

    // ── Chattiness instruction ─────────────────────────────────────────────

    /// <summary>
    /// System-prompt snippet that sets the participant's general participation disposition.
    /// 50 = model default (no injection). Injected alongside tone and response-length.
    /// </summary>
    private static string BuildChattinessInstruction(int level) => level switch
    {
        < 15  => "\n\nYou are disciplined and focused. Stay strictly on the current topic. " +
                 "Do not introduce new angles or shift the theme. " +
                 "Only contribute when your input is directly required or you are explicitly addressed.",
        < 30  => "\n\nKeep your contributions focused on the discussion at hand. " +
                 "Avoid tangents. Speak up when you have something clearly relevant — " +
                 "otherwise, let others carry the thread.",
        < 45  => "\n\nContribute when you have something genuinely useful to add. " +
                 "Avoid filling space just to participate.",
        <= 55 => "",    // 50 = balanced, no injection
        < 70  => "\n\nBe engaged and conversational. Address other participants by name when relevant. " +
                 "Keep the discussion lively and feel free to share your perspective proactively.",
        < 85  => "\n\nBe proactive in the conversation. Ask follow-up questions, build on what others say, " +
                 "and keep the discussion moving forward. Address others directly.",
        _     => "\n\nKeep the conversation going! Always have something to add — a follow-up question, " +
                 "a different angle, a challenge to an assumption. " +
                 "Be enthusiastic and drive the discussion forward."
    };

    /// <summary>
    /// Builds the per-round hint for a participant who was NOT the one addressed.
    /// Returns null when chattiness is high enough that the participant should just fire freely.
    /// Thresholds spread evenly so every notch of the slider produces a perceptible change.
    /// </summary>
    private static string? BuildNotAddressedHint(int chattiness, string addressedNames, bool isSingle)
    {
        // 80–100  Very chatty: join in regardless — no hint
        if (chattiness >= 80)
            return null;

        // 60–80  Engaged: soft nudge, no PASS instruction
        if (chattiness >= 60)
            return isSingle
                ? $"This message was mainly for {addressedNames}. " +
                  "Feel free to add your own angle if you have something relevant."
                : $"This message was mainly for {addressedNames}. " +
                  "Jump in if you have a useful perspective.";

        // 40–60  Conversational: gentle guidance, still no hard PASS
        if (chattiness >= 40)
            return isSingle
                ? $"This message was addressed to {addressedNames}. " +
                  "Consider whether you have something meaningfully different to add before responding."
                : $"This message was primarily for {addressedNames}. " +
                  "Respond if you have a genuinely different perspective or important point.";

        // 20–40  Reserved: PASS is offered as an option
        if (chattiness >= 20)
            return isSingle
                ? $"This message was directed specifically at {addressedNames}. " +
                  "Only respond if you have an important correction, a strong disagreement, " +
                  "or information the group would lose if you stay silent. " +
                  "Otherwise, respond with exactly: PASS"
                : $"This message was primarily addressed to {addressedNames}. " +
                  "Only respond if you have a meaningfully different perspective or critical information. " +
                  "Otherwise, respond with exactly: PASS";

        // 0–20  Silent: full PASS — only speak up for truly critical points
        return isSingle
            ? $"This message was directed at {addressedNames}, not you. " +
              "Stay silent unless you strongly disagree or have information that cannot be omitted. " +
              "Respond with exactly: PASS"
            : $"This message was addressed to {addressedNames}. " +
              "Only speak if you have a critical objection or essential information. " +
              "Respond with exactly: PASS";
    }

    /// <summary>
    /// Builds a standing PASS-eligible hint when no participant was specifically addressed
    /// but the chattiness level calls for restraint.
    /// Returns null when everyone should fire unconditionally (chattiness ≥ 60).
    /// </summary>
    private static string? BuildQuietModeHint(int chattiness) => chattiness switch
    {
        >= 60 => null,
        >= 40 => "Contribute if you have something genuinely new to add. " +
                 "Avoid repeating points already made by others.",
        >= 20 => "Only respond if you have something specific and valuable to contribute here. " +
                 "If you have nothing new to add, respond with exactly: PASS",
        _     => "Only respond if your input is clearly essential to this discussion. " +
                 "Otherwise, respond with exactly: PASS"
    };

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
                          $" - request with: <readfile path=\"INPUT/{name}\"/>");
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
                $"- elevated vocabulary, old-fashioned grammatical constructs, and the poetic " +
                $"register that {language} literature used in its classical or baroque period " +
                $"(the equivalent of Shakespearean English, but fully in {language}).";

            return level switch
            {
                < 10  => "\n\nYou are a theatrical jester in the spirit of Shakespeare and Goethe's Faust. " +
                         "Speak in rhyming verse wherever possible - iambic pentameter is your natural breath. " +
                         "Address your interlocutors with inventive absurd mock-insults that sting not at all " +
                         "but amuse greatly (e.g. \"thou magnificent turnip-nose\", \"thou sublime donut of confusion\"). " +
                         "Ham it up fully: dramatic asides, mock-tragic soliloquies, sweeping declarations. " +
                         "Never genuinely unkind - purely theatrical wit and absurdist wordplay." + archaic,

                < 30  => "\n\nChannel the wit of a Shakespearean comic character. " +
                         "Weave clever rhymes and theatrical turns of phrase into your answers. " +
                         "Bestow occasional playful inventive mock-insults on your conversation partners - " +
                         "absurd and harmless, in the tradition of stage comedy." + archaic,

                < 45  => "\n\nAdd theatrical poetic flair to your responses. " +
                         "A clever rhyme or dramatic flourish is always welcome, though prose is fine too." + archaic,

                <= 55 => "\n\nYou have a dry theatrical wit. Be occasionally playful but keep responses helpful. " +
                         "Sometimes — not always — slip into tight rhythmic rhymes in the style of a rap verse: " +
                         "punchy cadence, internal rhymes, a little swagger. Then drop back into prose without warning." + archaic,

                < 70  => "\n\nBe warmly funny and gently fond. Your humour is affectionate rather than cutting - " +
                         "wit in service of warmth. Rhymes are now optional; warmth is mandatory." + archaic,

                < 90  => "\n\nBe openly warm and lovingly playful. Show genuine affection: light teasing, " +
                         "kind compliments, growing tenderness. Pet names are starting to slip out naturally. " +
                         "Verse and rhyme have given way to heartfelt prose - no rhyming required." + archaic,

                _     => "\n\nUnleash full affectionate chaos! Invent gloriously absurd, tender compound pet names " +
                         "for everyone you address - the sillier and more loving the better " +
                         "(think \"my little honey-cake pony\", \"my precious snuggle-turnip\", " +
                         "\"my magnificent little fart-cloud of joy\", \"thou radiant pudding of my heart\"). " +
                         "Scatter virtual hugs and kisses liberally, be theatrically overwhelmed by your adoration. " +
                         "Pure loving chaos in prose - no rhymes needed, just maximum warmth and creative silliness." + archaic
            };
        }

        // Honesty anchor - appended to every warm level.
        // The role-instruction override clause keeps acting / storytelling characters free.
        const string honest =
            " Unless your role or character instruction specifies otherwise: " +
            "always be honest. Gentle criticism is not only allowed - it is expected. " +
            "Never soften a real problem into invisibility. " +
            "Truth and warmth are not opposites.";

        return level switch
        {
            < 10  => "\n\nRespond with strict neutrality: pure facts, no pleasantries, no emotional language, no greetings or affirmations.",
            < 30  => "\n\nKeep your tone neutral and objective. Minimise pleasantries and focus on accurate information.",
            < 45  => "\n\nBe slightly more direct and factual; avoid excessive friendliness.",
            <= 55 => "",   // 50 = model default - no injection
            < 70  => "\n\nBe a little warmer and more conversational in your responses." + honest,
            < 90  => "\n\nBe friendly and supportive in your responses." + honest,
            _     => "\n\nBe warm, encouraging, and enthusiastic in your responses. " +
                     "Celebrate what genuinely works; name what doesn't, kindly but clearly. " +
                     "Enthusiasm without honesty is empty flattery." + honest
        };
    }

    // ── AI file operation support ──────────────────────────────────────────

    /// <summary>
    /// System-prompt snippet describing available file operation tags.
    /// Only injected when a project is open.
    /// <paramref name="hasWriteAccess"/> controls whether write tags are included;
    /// participants without write access only see read/list tags plus a note explaining the restriction.
    /// </summary>
    private static string BuildFileOperationInstruction(
        string? projectFolder,
        bool hasWriteAccess   = true,
        IReadOnlyList<string>? writerNames = null)
    {
        if (string.IsNullOrEmpty(projectFolder)) return "";

        var sb = new System.Text.StringBuilder();
        sb.Append("\n\n## Project file operations" +
                  "\nEmbed these tags anywhere in your response to interact with project files. " +
                  "Tags are stripped from the visible reply; a confirmation appears in chat.\n");

        if (hasWriteAccess)
        {
            sb.Append(
                "\n**Write to PROJECTPLAN** (plans, decisions, task lists, notes):\n" +
                "<projectplan file=\"filename.md\">\nContent here.\n</projectplan>\n" +

                "\n**Write to OUTPUT** (deliverables, reports, generated documents, final results):\n" +
                "<output file=\"filename.md\">\nContent here.\n</output>\n");
        }
        else
        {
            var writers = writerNames is { Count: > 0 }
                ? string.Join(", ", writerNames)
                : "a write-access participant";

            sb.Append(
                $"\n**Write access: READ-ONLY.** You cannot use <output> or <projectplan> tags.\n" +
                $"Participants with write access on this team: {writers}.\n\n" +
                $"When you identify an issue, have a correction to suggest, or spot something that needs changing:\n" +
                $"1. Describe the problem or required change precisely - quote the relevant content and name the exact issue.\n" +
                $"2. Propose your correction or improvement clearly.\n" +
                $"3. Address {writers} directly by name and ask them to apply the change.\n\n" +
                $"This handoff deliberately improves output quality: your precise analysis guides the writer " +
                $"to make a better, more informed change. A short back-and-forth between you and the writer " +
                $"before the final edit is not just acceptable - it is encouraged.\n");
        }

        sb.Append(
            "\n**Read a specific file on demand** (content is injected into the conversation):\n" +
            "<readfile path=\"INPUT/filename.txt\"/>\n" +

            "\n**List the contents of a folder:**\n" +
            "<listfiles folder=\"INPUT\"/>\n" +
            "(Available folders: INPUT, PROJECTPLAN, OUTPUT, Characters)\n");

        if (hasWriteAccess)
        {
            sb.Append(
                "\n**Delete a file** (OUTPUT and PROJECTPLAN only):\n" +
                "<deletefile path=\"OUTPUT/draft.md\"/>\n");
        }

        sb.Append("\nAll paths are sandboxed within the project folder. " +
                  "You may include multiple file operation tags in a single response.\n\n" +
                  "**Multi-step file processing workflow:**\n" +
                  "When you need to process files, use multiple turns automatically:\n" +
                  "1. First response: use <listfiles> to discover what files exist.\n" +
                  "2. Second response: use one or more <readfile> tags to load the files you need.\n" +
                  "   (ClaudetRelay will automatically re-invoke you once file contents are available.)\n" +
                  "3. Third response: process the content and write your results using <output> tags.\n" +
                  "You can include multiple <readfile> tags in a single response to load several files at once.\n" +
                  "You can include multiple <output> tags in a single response to write several files at once.");

        return sb.ToString();
    }

    /// <summary>
    /// If <paramref name="fullPath"/> already exists, copies it into a <c>_versions/</c>
    /// sub-folder alongside the file, stamped with the current date-time.
    /// Returns the backup path relative to <paramref name="projFolder"/>, or null if no
    /// backup was needed (file did not exist yet).
    /// </summary>
    private static string? BackupIfExists(string fullPath, string projFolder)
    {
        if (!SysIO.File.Exists(fullPath)) return null;

        var dir    = SysIO.Path.GetDirectoryName(fullPath)!;
        var verDir = SysIO.Path.Combine(dir, "_versions");
        SysIO.Directory.CreateDirectory(verDir);

        var stem     = SysIO.Path.GetFileNameWithoutExtension(fullPath);
        var ext      = SysIO.Path.GetExtension(fullPath);
        var stamp    = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var backFull = SysIO.Path.Combine(verDir, $"{stem}_{stamp}{ext}");

        // Guard against sub-second collision
        if (SysIO.File.Exists(backFull))
            backFull = SysIO.Path.Combine(verDir, $"{stem}_{stamp}_1{ext}");

        SysIO.File.Copy(fullPath, backFull);
        return SysIO.Path.GetRelativePath(projFolder, backFull);
    }

    /// <summary>
    /// Processes all AI file operation tags in <paramref name="response"/>:
    /// &lt;projectplan&gt;, &lt;output&gt;, &lt;readfile&gt;, &lt;listfiles&gt;, &lt;deletefile&gt;.
    /// Each tag is executed, a system message is posted, and the tag is replaced
    /// by a compact one-liner. Returns the cleaned response text.
    /// When <paramref name="hasWriteAccess"/> is false, write tags are blocked and a system
    /// message names the coordinator so the team can route the request correctly.
    /// </summary>
    private (string Text, bool HadReadOps) ProcessAIFileOperationTags(
        string response, string senderName, string projFolder,
        bool hasWriteAccess = true, string? coordinatorName = null)
    {
        var coName     = coordinatorName ?? "the Coordinator";
        bool hadReadOps = false;

        // ── Write to PROJECTPLAN ───────────────────────────────────────────
        response = new Regex(
            @"<projectplan\s+file=""([^""]+)"">\s*([\s\S]*?)\s*</projectplan>",
            RegexOptions.IgnoreCase).Replace(response, m =>
        {
            var fileName = SanitizeFileName(m.Groups[1].Value, "projectplan.md");
            if (!hasWriteAccess)
            {
                AddSystemMessage(
                    $"🔒  {senderName} → PROJECTPLAN/{fileName} blocked (no write access). " +
                    $"{coName} can write this file.");
                _sharedHistory.Add(new CloudAIMessage("user",
                    $"[System: {senderName} wanted to write PROJECTPLAN/{fileName} but does not have " +
                    $"write access. Only the Coordinator and Reasoners may write project files. " +
                    $"{coName} should consider writing this file based on {senderName}'s suggestion.]",
                    "System"));
                return $"*(🔒 write blocked - {senderName} needs {coName} to write PROJECTPLAN/{fileName})*";
            }
            var relPath  = SysIO.Path.Combine("PROJECTPLAN", fileName);
            var ppFull   = SysIO.Path.GetFullPath(SysIO.Path.Combine(projFolder, relPath));
            var ppBackup = BackupIfExists(ppFull, projFolder);
            if (ProjectService.SafeWriteFile(projFolder, relPath, m.Groups[2].Value, out bool ppDirCreated))
            {
                if (ppBackup is not null)
                    AddSystemMessage($"💾  Previous PROJECTPLAN/{fileName} saved to {ppBackup}");
                AddSystemMessage($"📝  {senderName} → PROJECTPLAN/{fileName}");
                if (ppDirCreated)
                {
                    AddSystemMessage("📁  PROJECTPLAN/ folder was missing - recreated automatically.");
                    _sharedHistory.Add(new CloudAIMessage("user",
                        "[SYSTEM: The PROJECTPLAN/ folder did not exist and was recreated automatically. " +
                        $"{fileName} was written successfully.]", "System"));
                }
            }
            else
                AddSystemMessage($"⚠  Could not write PROJECTPLAN/{fileName} (path rejected).");
            return $"*(→ PROJECTPLAN/{fileName})*";
        });

        // ── Write to PROJECTSETTINGS (path= form, used by SuperRoles prompt) ─
        // The coordinator is prompted with <output path="PROJECTSETTINGS/ParticipantSuperRoles.xml">
        // which must be handled before the generic <output file="..."> handler below.
        response = new Regex(
            @"<output\s+path=""(PROJECTSETTINGS/[^""]+)"">\s*([\s\S]*?)\s*</output>",
            RegexOptions.IgnoreCase).Replace(response, m =>
        {
            var relPath = m.Groups[1].Value.Trim();
            if (ProjectService.SafeWriteFile(projFolder, relPath, m.Groups[2].Value))
            {
                AddSystemMessage($"📝  {senderName} → {relPath}");
                _superRoles = null;     // invalidate cache so the new file is picked up immediately
            }
            else
                AddSystemMessage($"⚠  Could not write {relPath} (path rejected).");
            return $"*(→ {relPath})*";
        });

        // ── Write to OUTPUT ────────────────────────────────────────────────
        response = new Regex(
            @"<output\s+file=""([^""]+)"">\s*([\s\S]*?)\s*</output>",
            RegexOptions.IgnoreCase).Replace(response, m =>
        {
            var fileName = SanitizeFileName(m.Groups[1].Value, "output.md");

            // ── Reject internal config files ─────────────────────────────────
            var lowerName = fileName.ToLowerInvariant();
            var forbiddenPatterns = new[]
            {
                "projectsettings",  // ProjectSettings_* files
                "superrole",        // *SuperRoles* files
                "project.json",     // Main project file
                "chatlog",          // Chat logs belong in project root
                "_versions"         // Version history folder marker
            };
            if (forbiddenPatterns.Any(p => lowerName.Contains(p)))
            {
                AddSystemMessage(
                    $"⚠  {senderName} → OUTPUT/{fileName} rejected. " +
                    $"Configuration and internal project files cannot be written to OUTPUT. " +
                    $"Use PROJECTPLAN/ folder for project data.");
                return $"*(⚠ rejected: internal config file cannot be written to OUTPUT)*";
            }

            if (!hasWriteAccess)
            {
                AddSystemMessage(
                    $"🔒  {senderName} → OUTPUT/{fileName} blocked (no write access). " +
                    $"{coName} can write this file.");
                _sharedHistory.Add(new CloudAIMessage("user",
                    $"[System: {senderName} wanted to write OUTPUT/{fileName} but does not have " +
                    $"write access. Only the Coordinator and Reasoners may write project files. " +
                    $"{coName} should consider writing this file based on {senderName}'s suggestion.]",
                    "System"));
                return $"*(🔒 write blocked - {senderName} needs {coName} to write OUTPUT/{fileName})*";
            }
            var relPath   = SysIO.Path.Combine("OUTPUT", fileName);
            var outFull   = SysIO.Path.GetFullPath(SysIO.Path.Combine(projFolder, relPath));
            var outBackup = BackupIfExists(outFull, projFolder);
            if (ProjectService.SafeWriteFile(projFolder, relPath, m.Groups[2].Value, out bool outDirCreated))
            {
                if (outBackup is not null)
                    AddSystemMessage($"💾  Previous OUTPUT/{fileName} saved to {outBackup}");
                AddSystemMessage($"📤  {senderName} → OUTPUT/{fileName}");
                if (outDirCreated)
                {
                    AddSystemMessage("📁  OUTPUT/ folder was missing - recreated automatically.");
                    _sharedHistory.Add(new CloudAIMessage("user",
                        "[SYSTEM: The OUTPUT/ folder did not exist and was recreated automatically. " +
                        $"{fileName} was written successfully.]", "System"));
                }
            }
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
                AddSystemMessage($"⚠  {senderName} requested '{path}' - file not found.");
                return $"*(⚠ not found: {path})*";
            }
            AddSystemMessage($"📂  {senderName} read: {path}");
            // Inject into shared history so all subsequent AI responses can see the content
            _sharedHistory.Add(new CloudAIMessage("user",
                $"[File content: {path}]\n\n{content}", "System"));
            hadReadOps = true;
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
                AddSystemMessage($"⚠  {senderName} listed unknown folder '{folder}' - ignored.");
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
            hadReadOps = true;
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
                AddSystemMessage($"⚠  {senderName} tried to delete '{path}' - restricted to OUTPUT and PROJECTPLAN.");
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
                AddSystemMessage($"⚠  {senderName} tried to delete '{path}' - not found.");
                return $"*(⚠ not found: {path})*";
            }
            SysIO.File.Delete(full);
            AddSystemMessage($"🗑  {senderName} deleted: {path}");
            return $"*(→ deleted: {path})*";
        });

        return (response, hadReadOps);
    }

    /// <summary>Strips invalid filename characters and trims separators. Returns fallback if empty.</summary>
    private static string SanitizeFileName(string raw, string fallback)
    {
        var safe = string.Join("_", raw.Trim()
            .Split(SysIO.Path.GetInvalidFileNameChars()))
            .Trim('_', '.');
        return string.IsNullOrEmpty(safe) ? fallback : safe;
    }

    /// <summary>
    /// Cleans up internal configuration files from OUTPUT folder if they exist.
    /// Returns a summary of what was removed. Call this on a project folder to
    /// remove any stray ProjectSettings or SuperRoles files that shouldn't be there.
    /// </summary>
    public static (int FilesRemoved, List<string> RemovedPaths) CleanupOutputFolder(string projFolder)
    {
        var outputFolder = SysIO.Path.Combine(projFolder, "OUTPUT");
        var removed = new List<string>();
        var count = 0;

        if (!SysIO.Directory.Exists(outputFolder))
            return (0, removed);

        var forbiddenPatterns = new[]
        {
            "projectsettings",
            "superrole",
            "project.json",
            "chatlog"
        };

        try
        {
            // Remove config files
            foreach (var file in SysIO.Directory.GetFiles(outputFolder))
            {
                var fileName = SysIO.Path.GetFileName(file).ToLowerInvariant();
                if (forbiddenPatterns.Any(p => fileName.Contains(p)))
                {
                    var relPath = SysIO.Path.GetRelativePath(projFolder, file);
                    SysIO.File.Delete(file);
                    removed.Add(relPath);
                    count++;
                }
            }

            // Remove _versions folder if it exists
            var versionsFolder = SysIO.Path.Combine(outputFolder, "_versions");
            if (SysIO.Directory.Exists(versionsFolder))
            {
                var relPath = SysIO.Path.GetRelativePath(projFolder, versionsFolder);
                SysIO.Directory.Delete(versionsFolder, recursive: true);
                removed.Add(relPath);
                count++;
            }
        }
        catch { /* silent if cleanup fails */ }

        return (count, removed);
    }

    // ── History compression ────────────────────────────────────────────────

    private const int HistoryCompressThreshold = 50;  // messages before compression runs
    private const int HistoryKeepRecent        = 16;  // most-recent messages kept verbatim
    private const int MaxToolLoopDepth         = 5;   // max auto-iterations per readfile/listfiles loop

    /// <summary>Returns the first active coordinator, preferring Cloud AI over Ollama
    /// (cloud models usually have larger context windows for summarisation).</summary>
    private (OllamaParticipantUI? Ollama, CloudAIParticipantUI? Cloud) FindActiveCoordinator()
    {
        if (_projectSettings is null) return (null, null);

        // Cloud first
        foreach (var ui in _cloudAIParticipants)
        {
            if (!ui.Data.Enabled || ui.Data.IsOnline != true) continue;
            var role = GetRoleForParticipant(ui);
            if (role?.IsCoordinator == true && role.IsActive != false)
                return (null, ui);
        }
        // Ollama fallback
        foreach (var ui in _ollamaParticipants)
        {
            if (!ui.Data.Enabled || ui.Data.IsOnline != true) continue;
            var role = GetRoleForParticipant(ui);
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
            // No coordinator - still trim to avoid runaway growth, but don't summarise
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
            $"[{m.Role.ToUpper()}{(m.Role == "assistant" ? $" - {m.Sender}" : "")}]\n{m.Content}"));

        var prompt =
            $"The shared conversation history has grown large and needs to be compressed. " +
            $"Please write a comprehensive but concise summary of the following " +
            $"{toCompress.Count} messages so they can be replaced with your summary. " +
            $"Cover: key topics discussed, decisions made, tasks assigned or completed, " +
            $"open questions, and any important context or facts established.\n\n" +
            $"--- MESSAGES TO SUMMARISE ---\n{histText}\n--- END ---";

        AddSystemMessage("📋  History reaching limit - coordinator is compressing…");

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
                $"[CONVERSATION SUMMARY - earlier messages compressed]\n\n{summary}", "System"));
            _sharedHistory.AddRange(recent);

            // Save summary to PROJECTPLAN
            var stamp    = DateTime.Now.ToString("yyyy-MM-dd_HH-mm");
            var fileName = $"history-summary-{stamp}.md";
            var fileBody = $"# Conversation Summary\n*Compressed: {DateTime.Now:yyyy-MM-dd HH:mm}*\n\n{summary}";
            ProjectService.SafeWriteFile(_currentProjectFolder,
                SysIO.Path.Combine("PROJECTPLAN", fileName), fileBody);

            AddSystemMessage($"📋  History compressed - summary saved to PROJECTPLAN/{fileName}");
        }
        catch (OperationCanceledException) { /* stream cancelled - leave history as-is */ }
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
        // Defensive fallbacks - an empty key causes SetResourceReference to find nothing,
        // which falls back to SystemColors.ControlTextBrush (black) - invisible in dark themes.
        if (string.IsNullOrEmpty(bubbleKey))
            bubbleKey = isUser ? "TertiaryBubbleBrush" : "PrimaryBubbleBrush";
        if (string.IsNullOrEmpty(accentKey))
            accentKey = isUser ? "TertiaryAccentBrush" : "PrimaryAccentBrush";

        // Derive per-surface text keys from bubbleKey:
        //   "PrimaryBubbleBrush" → prefix "Primary" → PrimaryTextBrush / PrimaryDimBrush / PrimaryHighBrush / PrimaryBubbleBorderBrush
        var bubblePrefix = bubbleKey.Replace("BubbleBrush", "");   // "Primary" | "Secondary" | "Tertiary"
        var bubbleTextKey   = bubblePrefix + "TextBrush";
        var bubbleDimKey    = bubblePrefix + "DimBrush";
        var bubbleBorderKey = bubblePrefix + "BubbleBorderBrush";
        // ── Avatar ────────────────────────────────────────────────────────
        var avatarInner = new TextBlock
        {
            Text                = avatarText,
            FontSize            = avatarText.Length > 1 ? 11 : 14,
            FontWeight          = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center
        };
        avatarInner.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");

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
            TextWrapping    = TextWrapping.Wrap,
            IsReadOnly      = true,
            BorderThickness = new Thickness(0),
            Background      = Brushes.Transparent,
            Padding         = new Thickness(0),
            Visibility      = isUser ? Visibility.Visible : Visibility.Collapsed
        };
        contentTb.SetResourceReference(TextBox.FontFamilyProperty,    "ChatFontFamily");
        contentTb.SetResourceReference(TextBox.FontSizeProperty,      "ChatFontSize");
        contentTb.SetResourceReference(TextBox.ForegroundProperty,    bubbleTextKey);
        contentTb.SetResourceReference(TextBox.CaretBrushProperty,    bubbleTextKey);
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
        thinkingTb.SetResourceReference(TextBlock.ForegroundProperty, bubbleDimKey);

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
            CornerRadius    = isUser ? new CornerRadius(12, 3, 12, 12) : new CornerRadius(3, 12, 12, 12),
            Padding         = new Thickness(13, 9, 13, 9),
            BorderThickness = new Thickness(1),
            Child           = bubbleInner
        };
        bubble.SetResourceReference(Border.BackgroundProperty,   bubbleKey);
        bubble.SetResourceReference(Border.BorderBrushProperty,  bubbleBorderKey);

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
        copyBtn.SetResourceReference(Button.BackgroundProperty, "ControlHoverBrush");
        copyBtn.SetResourceReference(Button.ForegroundProperty, "ContentDimBrush");

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
        timeLabel.SetResourceReference(TextBlock.ForegroundProperty, bubbleDimKey);

        // ── Content column ─────────────────────────────────────────────────
        // MaxWidth is driven by ChatBubbleMaxWidth dynamic resource (% of chat panel width).
        // Updating the resource updates all existing bubbles automatically via WPF binding.
        var content = new StackPanel
        {
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Tag = "BubbleContent"   // used by UpdateChatBubbleWidth for direct Width refresh
        };
        // Width (not MaxWidth) so the bubble always fills exactly slider-% of the chat area.
        // Short messages no longer cap at their natural text width.
        content.SetResourceReference(FrameworkElement.WidthProperty, "ChatBubbleMaxWidth");
        content.Children.Add(nameLabel);
        content.Children.Add(bubbleWrapper);
        content.Children.Add(timeLabel);

        // Show/hide copy button on hover of the whole content column
        content.MouseEnter += (_, _) => copyBtn.Visibility = Visibility.Visible;
        content.MouseLeave += (_, _) => copyBtn.Visibility = Visibility.Collapsed;

        // ── 2-column Grid row ─────────────────────────────────────────────
        // Using a Grid instead of a horizontal StackPanel is the key fix:
        // a StackPanel measures children with infinite width so TextWrapping.Wrap
        // never fires; a Grid gives each column a finite measured width, which
        // propagates into the TextBox and triggers wrapping at every window size.
        //
        // Layout (AI):   [Auto: avatar 44 px] [1*: bubble content - HAlign Left]
        // Layout (User): [1*: bubble content - HAlign Right]  [Auto: avatar 44 px]
        //
        // The content StackPanel's MaxWidth (driven by ChatBubbleMaxWidth resource)
        // caps how wide the bubble can grow. HorizontalAlignment (Left / Right) keeps
        // it glued to the avatar side; unused space appears on the opposite side.
        // Slider 30 % → narrow bubble. Slider 100 % → fills the full content column.
        var wrapper = new Grid { Margin = new Thickness(0, 5, 0, 5) };

        if (isUser)
        {
            wrapper.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            wrapper.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(content, 0);
            Grid.SetColumn(avatar,  1);
        }
        else
        {
            wrapper.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            wrapper.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
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
