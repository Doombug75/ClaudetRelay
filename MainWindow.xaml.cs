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
        TextBox        Content,          // raw text (always populated; used by copy button)
        TextBlock      EmoteContent,     // formatted inlines (shown instead of Content when emotes present)
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
        /// <summary>Per-card ⏹ stop button shown while this model is generating. Null until card is built.</summary>
        public Button?                    StopButton    { get; set; }
        /// <summary>Per-participant linked CTS, active during streaming. Set by RunOllamaStreamAsync; null otherwise.</summary>
        public CancellationTokenSource?   ActiveCts     { get; set; }
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
        /// <summary>Per-card ⏹ stop button shown while this model is generating. Null until card is built.</summary>
        public Button?                     StopButton    { get; set; }
        /// <summary>Per-participant linked CTS, active during streaming. Set by RunCloudAIStreamAsync; null otherwise.</summary>
        public CancellationTokenSource?    ActiveCts     { get; set; }
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
    /// <summary>
    /// Set to true when the ParticipantsWindow is closed while generation is active.
    /// ReInitializeParticipants() is deferred until the current generation completes.
    /// </summary>
    private bool                                         _pendingParticipantReinit;
    private List<string>                         _availableOllamaModels = [];
    private string?                              _currentThemePath;
    private string                               _userName              = "You";
    private int                                  _toneLevel             = 50;
    private int                                  _chattinessLevel       = 50;
    private bool                                 _mockingbirdMode       = false;
    private bool                                 _buccaneeerMode        = false;
    private bool                                 _suppressEnabledToggle = false;
    private double                               _chatBubbleWidthPct    = 78.0;
    private string                               _projectLanguage       = "";
    private string                               _uiLanguageName        = ""; // full name from app settings, e.g. "Deutsch"
    private int                                  _maxDialogDepth        = 1;
    private bool                                 _aiDialogueEnabled     = false;
    private int                                  _aiDialogueMaxTurns    = 10;
    private int                                  _globalResponseLength  = 50;
    private ProjectSettings?                     _projectSettings;
    private ParticipantsWindow?                  _participantsWindow;
    private Window?                              _helpWindow;
    // ── Dictation / voice recognition ────────────────────────────────────
    private readonly DictationService            _dictation = new();
    private bool                                 _dictationActive = false;
    // ── Private-message target (null = broadcast to all) ──────────────────
    private OllamaParticipantUI?                 _privateMsgOllamaTarget;
    private CloudAIParticipantUI?                _privateMsgCloudTarget;
    // ── Running parallel private tasks (each has its own CTS) ─────────────
    private readonly List<CancellationTokenSource> _privateTaskCts = [];

    // ── File checkout registry (prevents conflicts in parallel tasks) ──────
    private readonly FileCheckoutRegistry _fileCheckout = new();
    private CancellationTokenSource? _checkoutMonitorCts;  // for stale checkout monitor loop
    // Maps participant display name → set of file paths they've been asked to check in.
    // When their next response arrives, we parse it and extend or release the lock.
    private readonly Dictionary<string, HashSet<string>> _pendingCheckinFiles = new();

    // ── Project state ──────────────────────────────────────────────────────
    private Tab                        _currentTab = Tab.Chat;
    private string?                    _currentProjectFolder;
    private ProjectSettings?           _currentProject;   // same object as _projectSettings
    private ProjectTypeDefinition?     _currentProjectType;
    private Roadmap?                   _currentRoadmap;
    /// <summary>Cached role plan for the open project. Null = not loaded yet or no plan.</summary>
    private Dictionary<string, string>? _superRoles;   // keyed by display name → role instruction
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
        ApplyLocalization();
        LoadThemesIntoComboBox();

        // Close any open participant popup when clicking anywhere outside of it
        PreviewMouseDown += (_, e) =>
        {
            var allPopups = _cloudAIParticipants.Select(u => u.Popup)
                .Concat(_ollamaParticipants.Select(u => u.Popup));
            foreach (var p in allPopups)
            {
                if (!p.IsOpen) continue;
                // If the click was inside the popup or on its card (PlacementTarget), leave it open
                if (e.OriginalSource is DependencyObject src &&
                    (IsDescendantOf(src, p.Child) || IsDescendantOf(src, p.PlacementTarget as DependencyObject)))
                    continue;
                p.IsOpen = false;
            }
        };
        Loaded += async (_, _) =>
        {
            // ── First-run: prompt for nickname if this is the first launch ──
            // (Must be done after window is loaded so theme resources are available)
            var settings = SettingsService.Load();
            if (settings.UserName == "User")  // unchanged default
            {
                ShowNicknameDialog();
                settings = SettingsService.Load();  // reload in case it was saved
            }

            ApplyTitleBarTheme();                    // colour the OS title bar to match the theme
            UpdateWebBrowsingButton();               // seed opacity/effect for the web browsing toggle
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
            StartCheckoutMonitor();  // Monitor for stale file checkouts

            // ── Dictation service wiring ──────────────────────────────────
            InitDictationService();
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

        // Cleanup on window close
        Closing += (_, _) => StopCheckoutMonitor();
    }

    // ── Initialization ─────────────────────────────────────────────────────

    /// <summary>
    /// Applies the current UI language to every hardcoded string in MainWindow.xaml.
    /// Called once after InitializeComponent() so the culture is already set.
    /// </summary>
    private void ApplyLocalization()
    {
        // ── Sidebar ───────────────────────────────────────────────────────
        MultiAiSubtitle.Text      = Properties.Loc.S("Sidebar_MultiAiChat");
        ParticipantsLabel.Text    = Properties.Loc.S("Sidebar_Participants");
        ThemeLabel.Text           = Properties.Loc.S("Sidebar_Theme");
        SettingsButton.Content    = Properties.Loc.S("Btn_Config");
        ClearChatButton.Content   = Properties.Loc.S("Btn_ClearChat");
        AddParticipantButton.Content = Properties.Loc.S("Btn_AddParticipant");
        SettingsButton.ToolTip    = Properties.Loc.S("ToolTip_Config");
        ClaudetteButton.ToolTip   = Properties.Loc.S("ToolTip_ClaudetteBtn");

        // ── Welcome hint ─────────────────────────────────────────────────
        WelcomeTitle.Text             = Properties.Loc.S("Welcome_Title");
        WelcomeRun_TopRight.Text      = Properties.Loc.S("Welcome_TopRightMenu");
        WelcomeRun_ApiKeys.Text       = Properties.Loc.S("Welcome_ApiKeys");
        WelcomeRun_ConfigBtn.Text     = Properties.Loc.S("Welcome_ConfigBtn");
        WelcomeRun_ConfigModels.Text  = Properties.Loc.S("Welcome_ConfigModels");
        WelcomeRun_ForHelp.Text       = Properties.Loc.S("Welcome_ForHelp");

        // ── Tab bar ───────────────────────────────────────────────────────
        ChatTabButton.Content     = Properties.Loc.S("Nav_Chat");
        ProjectsTabButton.Content = Properties.Loc.S("Nav_Projects");
        BridgeTabButton.Content   = Properties.Loc.S("Nav_Bridge");

        // ── Chat header ───────────────────────────────────────────────────
        ChatHeaderTitle.Text        = Properties.Loc.S("ChatHeader_Title");
        ChatViewButton.Content      = Properties.Loc.S("Btn_ChatView");
        WorldButton.Content         = Properties.Loc.S("Btn_World");
        RoadmapButton.Content       = Properties.Loc.S("Btn_Roadmap");
        FilesButton.Content         = Properties.Loc.S("Btn_Files");
        CloseProjectButton.Content  = Properties.Loc.S("Btn_CloseProject");

        ExportChatButton.ToolTip    = Properties.Loc.S("ToolTip_ExportChat");
        ChatFontButton.ToolTip      = Properties.Loc.S("ToolTip_ChatFont");
        PrivateMsgButton.ToolTip    = Properties.Loc.S("ToolTip_WhisperBtn");
        InitVoiceBackend();
        SubscribeVoiceStateChanged();
        UpdateVoiceButtons();
        AiDialogueButton.ToolTip    = Properties.Loc.S("ToolTip_AiDialogue");
        ChatViewButton.ToolTip      = Properties.Loc.S("ToolTip_ChatView");
        WorldButton.ToolTip         = Properties.Loc.S("ToolTip_World");
        RoadmapButton.ToolTip       = Properties.Loc.S("ToolTip_Roadmap");
        FilesButton.ToolTip         = Properties.Loc.S("ToolTip_Files");
        BackupButton.ToolTip        = Properties.Loc.S("ToolTip_Backup");
        CloseProjectButton.ToolTip  = Properties.Loc.S("ToolTip_CloseProject");
        ProjectSettingsButton.ToolTip = Properties.Loc.S("ToolTip_ProjectSettings");  // from Projects tab section

        // ── Projects panel ────────────────────────────────────────────────
        ProjectsTitle.Text          = Properties.Loc.S("Projects_Title");
        SortLabel.Text              = Properties.Loc.S("Projects_Sort");
        SortAlphabetButton.Content  = Properties.Loc.S("Btn_SortAlphabetical");
        SortLastOpenedButton.Content = Properties.Loc.S("Btn_SortLastOpened");
        RefreshProjectsButton.Content = Properties.Loc.S("Btn_RefreshProjects");
        NewProjectButton.Content    = Properties.Loc.S("Btn_NewProject");
        OpenProjectButton.Content   = Properties.Loc.S("Btn_Open");
        DeleteProjectButton.Content = Properties.Loc.S("Btn_Delete");

        SortAlphabetButton.ToolTip   = Properties.Loc.S("ToolTip_SortAlpha");
        SortLastOpenedButton.ToolTip = Properties.Loc.S("ToolTip_SortLastOpened");
        RefreshProjectsButton.ToolTip = Properties.Loc.S("ToolTip_RefreshProjects");

        // ── Input area ────────────────────────────────────────────────────
        PlaceholderText.Text = Properties.Loc.S("Placeholder_Message");
        SendButton.Content   = Properties.Loc.S("Btn_Send");
        // AIRespondButton content is managed by UpdateVoiceButtons() — no static assignment here
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
        _currentTab = tab;
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



    // ── First-run nickname dialog ──────────────────────────────────────────

    /// <summary>
    /// Shows the first-run dialog asking the user to set their nickname.
    /// Must be called after window is loaded so theme resources are available.
    /// </summary>
    private void ShowNicknameDialog()
    {
        var title      = Properties.Loc.S("FirstRun_Nickname_Title");
        var question   = Properties.Loc.S("FirstRun_Nickname_Question");
        var okLabel    = Properties.Loc.S("FirstRun_Nickname_OK");
        var cancelLabel = Properties.Loc.S("FirstRun_Nickname_Cancel");

        var win = new Window
        {
            Title                 = title,
            WindowStyle           = WindowStyle.SingleBorderWindow,
            ResizeMode            = ResizeMode.NoResize,
            Width                 = 420,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar         = false,
            Owner = this  // Set MainWindow as owner
        };

        // Copy all theme resources from MainWindow
        foreach (var dict in Resources.MergedDictionaries)
            win.Resources.MergedDictionaries.Add(dict);

        // Also apply background from MainWindow theme
        if (TryFindResource("SidebarBgBrush") is Brush bg)
            win.Background = bg;

        var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 20) };

        // ── Heading ────────────────────────────────────────────────────────
        var titleTb = new TextBlock
        {
            Text         = title,
            FontFamily   = new FontFamily("Segoe UI"),
            FontSize     = 15,
            FontWeight   = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 16)
        };
        titleTb.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        panel.Children.Add(titleTb);

        // ── Question ───────────────────────────────────────────────────────
        var questionTb = new TextBlock
        {
            Text         = question,
            FontFamily   = new FontFamily("Segoe UI"),
            FontSize     = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 8)
        };
        questionTb.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        panel.Children.Add(questionTb);

        // ── Text input ─────────────────────────────────────────────────────
        var textBox = new TextBox
        {
            FontFamily       = new FontFamily("Segoe UI"),
            FontSize         = 13,
            Padding          = new Thickness(8, 6, 8, 6),
            Height           = 36,
            Margin           = new Thickness(0, 0, 0, 16),
            BorderThickness  = new Thickness(1),
            IsUndoEnabled    = false
        };
        // Try to use InputBgBrush, but fallback to a visible color if needed
        try
        {
            textBox.SetResourceReference(TextBox.BackgroundProperty,   "InputBgBrush");
            textBox.SetResourceReference(TextBox.ForegroundProperty,   "InputTextBrush");
            textBox.SetResourceReference(TextBox.BorderBrushProperty,  "InputBorderBrush");
            textBox.SetResourceReference(TextBox.CaretBrushProperty,   "InputTextBrush");
        }
        catch
        {
            // Fallback if theme resources are missing
            textBox.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 245, 245, 245));
            textBox.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Black);
            textBox.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray);
            textBox.CaretBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Black);
        }
        panel.Children.Add(textBox);

        // ── Button row ─────────────────────────────────────────────────────
        var buttonPanel = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var okButton = new Button
        {
            Content    = okLabel,
            Width      = 92,
            Height     = 32,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 12,
            IsDefault  = true,
            Margin     = new Thickness(0, 0, 8, 0)
        };
        okButton.SetResourceReference(Button.StyleProperty, "SButton");

        var cancelButton = new Button
        {
            Content    = cancelLabel,
            Width      = 92,
            Height     = 32,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 12,
            IsCancel   = true
        };
        cancelButton.SetResourceReference(Button.StyleProperty, "SButtonSecondary");

        okButton.Click += (_, _) =>
        {
            try
            {
                var name = textBox.Text.Trim();
                if (!string.IsNullOrEmpty(name))
                {
                    var settings = Services.SettingsService.Load();
                    settings.UserName = name;
                    Services.SettingsService.Save(settings);
                    win.DialogResult = true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving nickname: {ex.Message}");
                MessageBox.Show($"Error saving name: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        cancelButton.Click += (_, _) => win.DialogResult = false;

        textBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Return)
            {
                try
                {
                    okButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    e.Handled = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error handling Return key: {ex.Message}");
                    MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        };

        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(cancelButton);
        panel.Children.Add(buttonPanel);

        win.Content = panel;
        win.ShowDialog();

        // Move focus into the textbox once the window is visible
        win.Loaded += (_, _) => textBox.Focus();
    }

    // ── Dictation / Voice Recognition ─────────────────────────────────────

    private void InitDictationService()
    {
        // Wire up events
        _dictation.TextAvailable += text =>
            Dispatcher.Invoke(() =>
            {
                // Append transcribed text to input box with space separator
                var current = InputTextBox.Text;
                InputTextBox.Text = string.IsNullOrWhiteSpace(current)
                    ? text
                    : current.TrimEnd() + " " + text;
                InputTextBox.CaretIndex = InputTextBox.Text.Length;
                InputTextBox.Focus();
            });

        _dictation.StateChanged += state =>
            Dispatcher.Invoke(() => UpdateMicButton(state));

        // Wire PTT key: listen on main window PreviewKeyDown/Up
        PreviewKeyDown += (_, e) =>
        {
            if (!_dictationActive) return;
            var s = SettingsService.Load();
            if (s.AsrActivationMode != "PushToTalk") return;
            if (IsPttKeyMatch(e, s)) { _dictation.PttDown(); e.Handled = false; }
        };
        PreviewKeyUp += (_, e) =>
        {
            if (!_dictationActive) return;
            var s = SettingsService.Load();
            if (s.AsrActivationMode != "PushToTalk") return;
            if (IsPttKeyMatch(e, s)) _dictation.PttUp();
        };
    }

    private static bool IsPttKeyMatch(KeyEventArgs e, AppSettings s)
    {
        if (!Enum.TryParse<Key>(s.PushToTalkKey, out var pttKey)) return false;
        var k = e.Key == Key.System ? e.SystemKey : e.Key;
        if (k != pttKey) return false;
        bool ctrl  = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift)   != 0;
        bool alt   = (Keyboard.Modifiers & ModifierKeys.Alt)     != 0;
        return ctrl == s.PushToTalkCtrl && shift == s.PushToTalkShift && alt == s.PushToTalkAlt;
    }

    private void MicButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dictationActive)
        {
            _dictation.Deactivate();
            _dictationActive = false;
            UpdateMicButton(DictationState.Idle);
        }
        else
        {
            var s = SettingsService.Load();

            // Load/reload model if needed
            if (!string.IsNullOrEmpty(s.AsrModelName) && !string.IsNullOrEmpty(s.AsrModelsFolder))
            {
                var modelFolder = System.IO.Path.Combine(s.AsrModelsFolder, s.AsrModelName);
                _dictation.LoadModel(s.AsrModelType, modelFolder);
            }

            var mode = s.AsrActivationMode switch
            {
                "PushToTalk"     => DictationActivationMode.PushToTalk,
                "VoiceActivated" => DictationActivationMode.VoiceActivated,
                _                => DictationActivationMode.AlwaysOn
            };
            var device = DictationService.FindInputDeviceNumber(s.AudioInputDevice);
            _dictation.Configure(mode, s.VoiceActivationThreshold, device);
            _dictation.Activate();
            _dictationActive = true;
        }
    }

    private void UpdateMicButton(DictationState state)
    {
        if (MicButton is null) return;
        switch (state)
        {
            case DictationState.Idle:
                MicButton.Content    = "🎙";
                MicButton.ToolTip    = Properties.Loc.S("Asr_MicBtn_Idle");
                MicButton.SetResourceReference(BackgroundProperty, "ControlBgBrush");
                MicButton.SetResourceReference(ForegroundProperty, "SidebarDimBrush");
                break;
            case DictationState.Listening:
                MicButton.Content    = "🔴";
                MicButton.ToolTip    = Properties.Loc.S("Asr_MicBtn_On");
                MicButton.Background = new SolidColorBrush(Color.FromArgb(60, 220, 50, 50));
                MicButton.SetResourceReference(ForegroundProperty, "ContentTextBrush");
                break;
            case DictationState.Processing:
                MicButton.Content    = "⏳";
                MicButton.ToolTip    = Properties.Loc.S("Asr_MicBtn_Processing");
                MicButton.Background = new SolidColorBrush(Color.FromArgb(60, 200, 160, 20));
                MicButton.SetResourceReference(ForegroundProperty, "ContentTextBrush");
                break;
        }
    }

    // World-building editor methods live in MainWindow.World.cs

    /// <summary>Returns true if <paramref name="element"/> is <paramref name="ancestor"/> or a visual/logical descendant of it.</summary>
    private static bool IsDescendantOf(DependencyObject? element, DependencyObject? ancestor)
    {
        if (ancestor is null || element is null) return false;
        var current = element;
        while (current is not null)
        {
            if (current == ancestor) return true;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current)
                   ?? LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

}
