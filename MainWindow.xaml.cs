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
using System.Linq;
using ClaudetRelay.Models;
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
        /// <summary>Thin context-window usage bar at the bottom of the card. Updated after each response.</summary>
        public Border?                    ContextBar       { get; set; }
        /// <summary>Popup label showing "X / Y tokens (Z%)" for the last response. Updated after each response.</summary>
        public TextBlock?                 PopupContextVal  { get; set; }
        /// <summary>Popup label showing accumulated session token totals. Updated after each response.</summary>
        public TextBlock?                 PopupSessionVal  { get; set; }
        /// <summary>Running total of input tokens sent this session.</summary>
        public int                        SessionInputTokens  { get; set; }
        /// <summary>Running total of output tokens received this session.</summary>
        public int                        SessionOutputTokens { get; set; }
        /// <summary>Small token counter shown top-right of card during generation. Null until card is built.</summary>
        public TextBlock?                 TokenCountLabel { get; set; }
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
        /// <summary>Thin context-window usage bar at the bottom of the card. Updated after each response.</summary>
        public Border?                     ContextBar          { get; set; }
        /// <summary>Popup label showing "X / Y tokens (Z%)" for the last response.</summary>
        public TextBlock?                  PopupContextVal     { get; set; }
        /// <summary>Popup label showing accumulated session token totals.</summary>
        public TextBlock?                  PopupSessionVal     { get; set; }
        /// <summary>Running total of input tokens sent this session.</summary>
        public int                         SessionInputTokens  { get; set; }
        /// <summary>Running total of output tokens received this session.</summary>
        public int                         SessionOutputTokens { get; set; }
        /// <summary>Small token counter shown top-right of card during generation. Null until card is built.</summary>
        public TextBlock?                  TokenCountLabel { get; set; }
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
    // Temporary char counts captured immediately before each provider StreamAsync call,
    // used to calibrate the per-participant chars-per-token ratio after the response.
    private int _sentCharsOllama;
    private int _sentCharsCloud;

    /// <summary>
    /// One semaphore per Ollama base URL — ensures only one model streams at a time
    /// per Ollama instance (Ollama is single-threaded; parallel requests queue up and
    /// can produce empty/corrupted responses). Cloud AI participants are unaffected.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.SemaphoreSlim>
        _ollamaServerSemaphores = new(StringComparer.OrdinalIgnoreCase);
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
    private int                                  _maxFileOpDepth        = 0;
    private bool                                 _aiDialogueEnabled     = false;
    private int                                  _aiDialogueMaxTurns    = 10;
    private int                                  _globalResponseLength  = 50;
    private ProjectSettings?                     _projectSettings;
    private ParticipantsWindow?                  _participantsWindow;
    private Window?                              _helpWindow;
    // ── Dictation / voice recognition ────────────────────────────────────
    private readonly DictationService            _dictation    = new();
    private readonly NoiseCommandMatcher         _noiseMatcher = new();
    private bool                                 _dictationActive      = false;
    private bool                                 _dictationModelLoaded = false;
    private bool                                 _dictationRunning     = false;
    private string                               _loadedAsrModelType   = "";
    private string                               _loadedAsrModelName   = "";
    // Combo-command state: noise fired, waiting for phrase within timeout (streaming mode)
    private VoiceCommand?  _pendingComboCommand;
    private DateTime       _comboDeadline;
    // Recently-fired noise: lets ASR chunks strip the noise's filter words
    private VoiceCommand?  _lastFiredNoise;
    private DateTime       _lastFiredNoiseAt;
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
            var settings = SettingsService.Load(out bool settingsCorrupt);
            if (settingsCorrupt)
                PromptCorruptFile(SettingsService.FilePath, "App Settings");

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
                // Render all bubbles visually, but only inject recent messages into
                // _sharedHistory — capped both by message count AND by estimated token
                // budget so small models (e.g. Gemma 2B with 2k ctx) don't start full.
                // Token estimate: 1 token ≈ 4 chars; we claim at most 40% of the
                // smallest active context window for restored history.
                int smallestCtx = _ollamaParticipants
                    .Where(u => u.Data.Enabled && u.Data.Service.NumCtx > 0)
                    .Select(u => u.Data.Service.NumCtx)
                    .DefaultIfEmpty(8192)
                    .Min();
                int charBudget = (int)(smallestCtx * 4 * 0.40);   // 40% of ctx in chars
                const int MaxHistoryMessages = 40;

                // Walk from newest to oldest, accumulating chars until budget exhausted
                int charsUsed = 0;
                int contextStart = savedLog.Count; // assume nothing fits, then expand
                for (int i = savedLog.Count - 1; i >= 0; i--)
                {
                    var msgLen = (savedLog[i].Message?.Length ?? 0) + (savedLog[i].RawMessage?.Length ?? 0);
                    if (charsUsed + msgLen > charBudget || savedLog.Count - i > MaxHistoryMessages)
                        break;
                    charsUsed += msgLen;
                    contextStart = i;
                }

                for (int i = 0; i < savedLog.Count; i++)
                {
                    var entry = savedLog[i];
                    if (i < contextStart)
                        RenderChatLogEntryVisualOnly(entry);  // bubble only, no history
                    else
                        RenderChatLogEntry(entry);            // bubble + history
                }
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
            if (SettingsService.Load().DictationEnabled)
                _ = LoadDictationAsync();   // restore last active state; instant first press
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
        Closing += (_, e) =>
        {
            StopCheckoutMonitor();
            _streamCts?.Cancel();
            CancelAllPrivateTasks();
            VoiceOutputService.StopAll();
            _dictation.Deactivate();
            _dictation.Dispose();
            if (_mcpServer?.IsRunning == true) { _mcpServer.Stop(); _mcpServer.Dispose(); _mcpServer = null; }
        };
        // ONNX Runtime keeps native threads alive even after Dispose — force-exit so
        // the process actually terminates instead of hanging in native thread pools.
        Closed += (_, _) => Environment.Exit(0);
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
        // ── Output chain: ordered text + noise commands ───────────────────
        var chain = new OutputChain();
        _dictation.OutputChain    = chain;
        _noiseMatcher.OutputChain = chain;

        chain.EntryReady += entry => Dispatcher.Invoke(() => OnChainEntryReady(entry));

        // ── Noise command matcher ─────────────────────────────────────────
        ReloadVoiceCommands();
        _dictation.RawAudioAvailable += samples => _noiseMatcher.ProcessSamples(samples);
        _noiseMatcher.CommandFired   += cmd =>
        {
            // Set suppress immediately on the audio thread so the very next batch
            // doesn't re-arm from the noise resonance tail before Dispatcher runs.
            _dictation.SuppressRearm();
            Dispatcher.Invoke(() =>
            {
                // Commit speech recorded before this noise (trim clip so ASR doesn't
                // transcribe it; noise command execution is handled via the output chain).
                _dictation.CommitAndRearm(_noiseMatcher.LastMatchedClipSamples);

                // Strip any ASR transcription of the noise already in the text box
                StripNoiseFilterWords(cmd);
                _lastFiredNoise   = cmd;
                _lastFiredNoiseAt = DateTime.UtcNow;
            });
        };

        // ── ASR text (streaming mode only — batch goes through the chain) ─
        _dictation.TextAvailable += text =>
            Dispatcher.Invoke(() =>
            {
                if (!_dictation.IsStreamingMode) return; // batch handled by chain
                _noiseMatcher.NotifyAsrOutput();
                text = ProcessPhraseCommands(text);
                if (!string.IsNullOrWhiteSpace(text)) AppendTextToInput(text);
            });

        _dictation.StateChanged += state =>
            Dispatcher.Invoke(() => UpdateMicButton(state));

        // Show a spinning "working" cursor over the input box while ASR jobs are in flight.
        _dictation.PendingCountChanged += count =>
            Dispatcher.Invoke(() =>
                InputTextBox.Cursor = count > 0 ? System.Windows.Input.Cursors.AppStarting : null);

        // Wire PTT key: listen on main window PreviewKeyDown/Up.
        // When focus is in a TextBox we allow the key through so you can still type
        // (holding Space while in the chat box types spaces AND records — both work).
        // When focus is anywhere else (buttons, panels, etc.) we suppress the key so
        // it never clicks a focused button or shifts focus to the chat input.
        PreviewKeyDown += (_, e) =>
        {
            if (!_dictationActive) return;
            var s = SettingsService.Load();
            if (s.AsrActivationMode != "PushToTalk") return;
            if (!IsPttKeyMatch(e, s)) return;
            _dictation.PttDown();
            e.Handled = Keyboard.FocusedElement is not System.Windows.Controls.TextBox;
        };
        PreviewKeyUp += (_, e) =>
        {
            if (!_dictationActive) return;
            var s = SettingsService.Load();
            if (s.AsrActivationMode != "PushToTalk") return;
            if (!IsPttKeyMatch(e, s)) return;
            _dictation.PttUp();
            e.Handled = Keyboard.FocusedElement is not System.Windows.Controls.TextBox;
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

    // ── Dictation power button (⏻) ─────────────────────────────────────────
    // Loads / unloads the model and opens / closes the microphone.
    // Use this to free RAM when you don't need dictation.

    private async void DictationPowerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dictationModelLoaded)
        {
            // Unload: stop recording, release mic, free model RAM
            if (_dictation.IsRecording) _dictation.FinalizeRecording();
            _dictation.Deactivate();
            _dictationActive      = false;
            _dictationModelLoaded = false;
            _dictationRunning     = false;
            UpdateDictationPower(loaded: false);
            UpdateMicButton(DictationState.Idle);
            var s = SettingsService.Load(); s.DictationEnabled = false; SettingsService.Save(s);
        }
        else
        {
            await LoadDictationAsync();
        }
    }

    /// <summary>
    /// Loads the ASR model and opens the microphone.
    /// Called by the power button and automatically at startup.
    /// Also call after Voice Recognition settings are saved to hot-swap the model.
    /// </summary>
    public async Task LoadDictationAsync()
    {
        var s = SettingsService.Load();
        if (string.IsNullOrEmpty(s.AsrModelName) || string.IsNullOrEmpty(s.AsrModelsFolder))
            return;

        Dispatcher.Invoke(() =>
        {
            UpdateDictationPower(loading: true);
            if (MicButton is not null) MicButton.IsEnabled = false;
        });

        var modelFolder   = System.IO.Path.Combine(s.AsrModelsFolder, s.AsrModelName);
        var selectedType  = (s.AsrModelType ?? "whisper").ToLowerInvariant();
        var detectedType  = DictationService.DetectModelTypeFromFolder(modelFolder);

        if (detectedType is not null && !string.Equals(detectedType, selectedType, StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.Invoke(() =>
            {
                UpdateDictationPower(loaded: false);
                UpdateMicButton(DictationState.Idle);
                MessageBox.Show(
                    string.Format(Properties.Loc.S("Asr_MismatchBody"), selectedType, detectedType),
                    Properties.Loc.S("Asr_MismatchTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            });
            return;
        }

        var loaded = await Task.Run(() => _dictation.LoadModel(selectedType, modelFolder));

        if (!loaded)
        {
            Dispatcher.Invoke(() => { UpdateDictationPower(loaded: false); UpdateMicButton(DictationState.Idle); });
            return;
        }

        // Legacy "VoiceActivated" setting falls through to AlwaysOn (modes were merged).
        var mode   = s.AsrActivationMode switch
        {
            "PushToTalk" => DictationActivationMode.PushToTalk,
            _            => DictationActivationMode.AlwaysOn
        };
        _dictation.Configure(
            mode,
            s.VoiceActivationThreshold,
            DictationService.FindInputDeviceNumber(s.AudioInputDevice),
            s.VoiceSilenceMs);
        _dictation.MicBoost = AudioSetupWindow.QuadraticBoost(Math.Clamp(s.AudioInputBoost, 0, 300));

        // Open the mic in standby; AlwaysOn arms voice detection, PushToTalk arms the key.
        _dictation.Activate(startRecordingChunk: false);
        _dictationActive      = true;
        _dictationModelLoaded = true;
        _loadedAsrModelType   = selectedType;
        _loadedAsrModelName   = s.AsrModelName ?? "";
        var sOn = SettingsService.Load(); sOn.DictationEnabled = true; SettingsService.Save(sOn);

        Dispatcher.Invoke(() =>
        {
            UpdateDictationPower(loaded: true);
            UpdateMicButton(_dictation.State);
        });
    }

    // ── Dictation mic button (🎙) ──────────────────────────────────────────
    // Controls recording only — power must be on first.
    // AlwaysOn:   press = start a manual chunk, press again = stop + transcribe
    //             (hands-free voice detection also auto-starts chunks)
    // PushToTalk: not used here — PTT key handles everything

    private void MicButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_dictationModelLoaded) return;

        // Warn if settings changed since the model was loaded
        var cur = SettingsService.Load();
        var typeMismatch  = !string.Equals(_loadedAsrModelType, cur.AsrModelType,  StringComparison.OrdinalIgnoreCase);
        var modelMismatch = !string.Equals(_loadedAsrModelName, cur.AsrModelName, StringComparison.OrdinalIgnoreCase);
        if (typeMismatch || modelMismatch)
        {
            var what = typeMismatch ? $"type changed to \"{cur.AsrModelType}\"" : $"model changed to \"{cur.AsrModelName}\"";
            MessageBox.Show(
                $"ASR settings have changed ({what}) but the old model is still loaded.\n\nTurn the power off and on again to reload.",
                "ASR model mismatch", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_dictationRunning)
        {
            _dictationRunning = false;
            _dictation.FinalizeRecording();
        }
        else
        {
            _dictationRunning = true;
            _dictation.StartRecordingChunk();
        }
    }

    /// <summary>Called by AudioSetupWindow when the user moves the boost slider.</summary>
    public void ApplyMicBoost(float gain) => _dictation.MicBoost = gain;

    // ── Voice Commands ────────────────────────────────────────────────────

    public void ReloadVoiceCommands()
    {
        var cmds = SettingsService.Load().VoiceCommands;
        _noiseMatcher.UpdateCommands(cmds);
    }

    /// <summary>Delegate exposed so VoiceCommandsWindow can offer auto-detect.</summary>
    public Task<string?> TranscribeSampleAsync(float[] samples)
        => _dictation.TranscribeSampleAsync(samples);

    /// <summary>Returns a multi-line diagnostic string about the current noise matcher state.</summary>
    public string GetNoiseDiagnostics()
    {
        var cmds = SettingsService.Load().VoiceCommands;
        var noises = cmds.Where(c => c.Type == VoiceCommandType.Noise).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"Noise commands in settings: {noises.Count}");
        foreach (var c in noises)
        {
            int loaded = c.NoiseSamples.Count(s => s is not null);
            sb.AppendLine($"  [{(c.Enabled ? "ON" : "off")}] {c.Name}  samples={loaded}/3");
        }
        sb.AppendLine();
        sb.AppendLine($"Last clip: {_noiseMatcher.LastClipInfo}");
        return sb.ToString();
    }

    /// <summary>
    /// Called on each ASR text chunk. Handles:
    ///  1. Pending combo: strip filter words from start, match phrase mappings.
    ///  2. Plain Phrase commands: case-insensitive contains match.
    /// Returns the text with matched phrases stripped (may be empty).
    /// </summary>
    private string ProcessPhraseCommands(string text)
    {
        // ── Filter words from recently-fired noise (noise fired before ASR output) ──
        // Handles the case where the noise fires, executes its action, and THEN the
        // ASR segment that contained the noise arrives with the filter word still in it.
        if (_lastFiredNoise is { } recent
            && DateTime.UtcNow - _lastFiredNoiseAt < TimeSpan.FromSeconds(4))
        {
            _lastFiredNoise = null;
            text = StripFilterWordsFromStart(text, recent.NoiseFilterWords);
            // Also strip from tail in case model put it at the end instead of start
            foreach (var filter in recent.NoiseFilterWords.Where(w => !string.IsNullOrWhiteSpace(w)))
            {
                if (text.EndsWith(filter, StringComparison.OrdinalIgnoreCase))
                {
                    text = text[..^filter.Length].TrimEnd();
                    break;
                }
            }
        }

        // ── Pending combo (noise already fired) ───────────────────────────
        if (_pendingComboCommand is not null)
        {
            var combo = _pendingComboCommand;
            if (DateTime.UtcNow <= _comboDeadline)
            {
                _pendingComboCommand = null;
                // Strip noise filter words that leaked into the start of this chunk
                text = StripFilterWordsFromStart(text, combo.NoiseFilterWords);

                foreach (var mapping in combo.PhraseActions)
                {
                    if (TryMatchAndStripPhrase(ref text, mapping.Phrase, mustStart: true))
                    {
                        ExecuteVoiceAction(mapping.Action, mapping.InsertCharacterValue);
                        return text;
                    }
                }
                // No phrase matched — fall through to plain phrase check,
                // and also fire default action if set
                if (combo.DefaultAction != VoiceCommandAction.None)
                    ExecuteVoiceAction(combo.DefaultAction, combo.InsertCharacterValue);
            }
            else
            {
                _pendingComboCommand = null; // deadline expired
            }
        }

        // ── Plain Phrase commands ──────────────────────────────────────────
        var phraseCmds = SettingsService.Load().VoiceCommands
            .Where(c => c.Enabled && c.Type == VoiceCommandType.Phrase
                     && !string.IsNullOrWhiteSpace(c.Phrase));

        foreach (var cmd in phraseCmds)
        {
            if (cmd.Action == VoiceCommandAction.InsertCharacter)
            {
                // In-place replacement: swap the spoken word(s) for the stored value
                // wherever they occur in the sentence (e.g. censoring "Scheiße" → "<Zensiert>").
                // Does not break — multiple replacement commands can apply to one utterance.
                TryReplacePhrase(ref text, cmd.Phrase, cmd.InsertCharacterValue);
            }
            else if (TryMatchAndStripPhrase(ref text, cmd.Phrase))
            {
                ExecuteVoiceAction(cmd.Action, cmd.InsertCharacterValue);
                break;
            }
        }

        return text;
    }

    // ── Output chain processor ────────────────────────────────────────────

    private VoiceCommand? _pendingPhraseCommand; // noise cmd waiting to see if next text contains its phrase

    private void OnChainEntryReady(Models.OutputChainEntry entry)
    {
        if (entry.Kind == Models.ChainEntryKind.Text)
        {
            _noiseMatcher.NotifyAsrOutput();
            var text = ProcessPhraseCommands(entry.Text ?? "");

            if (_pendingPhraseCommand != null)
            {
                // A noise command with phrase actions was waiting — check if this text
                // matches one of its phrase triggers.
                var matched = _pendingPhraseCommand.PhraseActions
                    .FirstOrDefault(pa => ContainsPhraseWords(text, pa.Phrase));
                if (matched != null)
                {
                    // Phrase found: execute the phrase action and suppress the text
                    var pendingCmd = _pendingPhraseCommand;
                    _pendingPhraseCommand = null;
                    ExecuteVoiceAction(matched.Action, matched.InsertCharacterValue);
                    return;
                }
                // No phrase match: drop the noise command, output the text as-is
                _pendingPhraseCommand = null;
            }

            if (!string.IsNullOrWhiteSpace(text)) AppendTextToInput(text);
        }
        else // Command
        {
            var cmd = entry.Command!;
            StripNoiseFilterWords(cmd);
            _lastFiredNoise   = cmd;
            _lastFiredNoiseAt = DateTime.UtcNow;

            if (cmd.PhraseActions.Count > 0)
            {
                // Wait for the next text entry to decide which phrase action to take
                _pendingPhraseCommand = cmd;
            }
            else if (cmd.DefaultAction != VoiceCommandAction.None)
            {
                ExecuteVoiceAction(cmd.DefaultAction, cmd.InsertCharacterValue);
            }
        }
    }

    private void AppendTextToInput(string text)
    {
        var current = InputTextBox.Text;
        if (string.IsNullOrWhiteSpace(current))
            InputTextBox.Text = text;
        else if (current.EndsWith('\n'))
            InputTextBox.Text = current + text;
        else
            InputTextBox.Text = current.TrimEnd() + " " + text;
        InputTextBox.CaretIndex = InputTextBox.Text.Length;
        InputTextBox.Focus();
    }

    /// <summary>
    /// Removes noise filter words/phrases that appear at the TAIL of the text box
    /// (the ASR transcription of the noise sound itself).
    /// </summary>
    private void StripNoiseFilterWords(VoiceCommand cmd)
    {
        if (cmd.NoiseFilterWords.Count == 0) return;
        var text = InputTextBox.Text;
        if (string.IsNullOrEmpty(text)) return;

        // Try each filter entry against the tail of the text, longest first
        var filters = cmd.NoiseFilterWords
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .OrderByDescending(w => w.Length);

        foreach (var filter in filters)
        {
            if (text.EndsWith(filter, StringComparison.OrdinalIgnoreCase))
            {
                InputTextBox.Text = text[..^filter.Length].TrimEnd();
                InputTextBox.CaretIndex = InputTextBox.Text.Length;
                return;
            }
        }
    }

    /// <summary>
    /// Strips noise filter words/phrases from the START of a text chunk
    /// (noise transcription that leaked into the next ASR segment).
    /// </summary>
    private static string StripFilterWordsFromStart(string text, IEnumerable<string> filters)
    {
        var ordered = filters
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .OrderByDescending(w => w.Length);

        foreach (var filter in ordered)
        {
            if (text.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
                return text[filter.Length..].TrimStart();
        }
        return text;
    }

    // ── Punctuation-tolerant phrase matching ────────────────────────────────
    // The ASR model often attaches punctuation to command words ("slash.",
    // "(slash)", "send!", ", new line"). These helpers compare on whole-word
    // boundaries after stripping punctuation so the command is still recognised.

    /// <summary>Lowercases and removes everything that isn't a letter or digit.</summary>
    private static string NormalizeToken(string token)
    {
        var sb = new System.Text.StringBuilder(token.Length);
        foreach (char c in token)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static readonly char[] _wordSep = { ' ', '\t', '\n', '\r' };

    /// <summary>Splits a phrase into normalised, punctuation-free lowercase words.</summary>
    private static string[] PhraseWords(string phrase) =>
        phrase.Split(_wordSep, StringSplitOptions.RemoveEmptyEntries)
              .Select(NormalizeToken)
              .Where(w => w.Length > 0)
              .ToArray();

    /// <summary>
    /// Splits a phrase field into its alternatives (semicolon-separated), each as a
    /// word array. So "Scheisse;Scheiße;scheisse" yields three alternatives that all
    /// trigger the same command. Sorted longest-first so a multi-word alternative isn't
    /// pre-empted by a shorter one sharing its first word.
    /// </summary>
    private static List<string[]> PhraseAlternatives(string phrase) =>
        phrase.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
              .Select(PhraseWords)
              .Where(w => w.Length > 0)
              .OrderByDescending(w => w.Length)
              .ToList();

    /// <summary>True if <paramref name="phraseWords"/> matches the run of normalised
    /// tokens starting at index <paramref name="i"/>.</summary>
    private static bool PhraseMatchesAt(string[] normTokens, string[] phraseWords, int i)
    {
        if (i + phraseWords.Length > normTokens.Length) return false;
        for (int j = 0; j < phraseWords.Length; j++)
            if (normTokens[i + j] != phraseWords[j]) return false;
        return true;
    }

    /// <summary>
    /// Searches <paramref name="text"/> for <paramref name="phrase"/> as a contiguous
    /// run of whole words, ignoring case and any punctuation attached to the words.
    /// If found, removes those words (with their punctuation) from <paramref name="text"/>
    /// and returns true. When <paramref name="mustStart"/> is set, only matches at the
    /// very first word.
    /// </summary>
    private static bool TryMatchAndStripPhrase(ref string text, string phrase, bool mustStart = false)
    {
        var alts = PhraseAlternatives(phrase);
        if (alts.Count == 0) return false;

        var tokens     = text.Split(_wordSep, StringSplitOptions.RemoveEmptyEntries);
        var normTokens = tokens.Select(NormalizeToken).ToArray();

        int limit = mustStart ? 1 : tokens.Length;
        for (int i = 0; i < limit; i++)
        {
            foreach (var phraseWords in alts)
            {
                if (!PhraseMatchesAt(normTokens, phraseWords, i)) continue;
                var remaining = tokens.Take(i).Concat(tokens.Skip(i + phraseWords.Length));
                text = string.Join(" ", remaining).Trim();
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Replaces every whole-word occurrence of <paramref name="phrase"/> in
    /// <paramref name="text"/> with <paramref name="replacement"/> in place (case- and
    /// punctuation-tolerant). Used by phrase commands with the InsertCharacter action so
    /// a spoken word is swapped for a value where it occurs — e.g. censoring. Returns
    /// true if at least one occurrence was replaced.
    /// </summary>
    private static bool TryReplacePhrase(ref string text, string phrase, string replacement)
    {
        var alts = PhraseAlternatives(phrase);
        if (alts.Count == 0) return false;

        var tokens     = text.Split(_wordSep, StringSplitOptions.RemoveEmptyEntries);
        var normTokens = tokens.Select(NormalizeToken).ToArray();

        var output = new List<string>(tokens.Length);
        bool any = false;
        for (int i = 0; i < tokens.Length; )
        {
            int matchedLen = 0;
            foreach (var phraseWords in alts)
                if (PhraseMatchesAt(normTokens, phraseWords, i)) { matchedLen = phraseWords.Length; break; }

            if (matchedLen > 0)
            {
                if (!string.IsNullOrEmpty(replacement)) output.Add(replacement);
                any = true;
                i += matchedLen;
            }
            else
            {
                output.Add(tokens[i]);
                i++;
            }
        }
        if (any) text = string.Join(" ", output).Trim();
        return any;
    }

    /// <summary>Punctuation/case-tolerant whole-word containment test (no mutation).</summary>
    private static bool ContainsPhraseWords(string text, string phrase)
    {
        var copy = text;
        return TryMatchAndStripPhrase(ref copy, phrase);
    }

    private void ExecuteVoiceAction(VoiceCommandAction action, string insertCharValue = "")
    {
        switch (action)
        {
            case VoiceCommandAction.NewLine:
                var pos = InputTextBox.CaretIndex;
                InputTextBox.Text = InputTextBox.Text.Insert(pos, "\n");
                InputTextBox.CaretIndex = pos + 1;
                break;

            case VoiceCommandAction.DeleteLastWord:
                var t = InputTextBox.Text.TrimEnd();
                var lastSpace = t.LastIndexOfAny(new[] { ' ', '\n' });
                InputTextBox.Text = lastSpace >= 0 ? t[..(lastSpace + 1)] : "";
                InputTextBox.CaretIndex = InputTextBox.Text.Length;
                break;

            case VoiceCommandAction.DeleteLastSentence:
                var ts = InputTextBox.Text.TrimEnd();
                // Find last sentence-ending punctuation or newline
                int cut = -1;
                for (int i = ts.Length - 1; i >= 0; i--)
                {
                    if (ts[i] is '.' or '!' or '?' or '\n') { cut = i; break; }
                }
                InputTextBox.Text = cut > 0 ? ts.Substring(0, cut + 1).TrimEnd() : "";
                InputTextBox.CaretIndex = InputTextBox.Text.Length;
                break;

            case VoiceCommandAction.DeleteAll:
                InputTextBox.Text = "";
                break;

            case VoiceCommandAction.Send:
                // Simulate send button click — reuse existing send logic
                SendButton?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                break;

            case VoiceCommandAction.Undo:
                InputTextBox.Undo();
                break;

            case VoiceCommandAction.InsertCharacter:
                if (!string.IsNullOrEmpty(insertCharValue))
                {
                    var caret = InputTextBox.CaretIndex;
                    InputTextBox.Text = InputTextBox.Text.Insert(caret, insertCharValue);
                    InputTextBox.CaretIndex = caret + insertCharValue.Length;
                }
                break;
        }
    }

    private void UpdateDictationPower(bool loaded = false, bool loading = false)
    {
        if (DictationPowerButton is null) return;
        if (loading)
        {
            DictationPowerButton.Content   = "⏳";
            DictationPowerButton.IsEnabled = false;
            DictationPowerButton.SetResourceReference(ForegroundProperty, "ContentDimBrush");
            DictationPowerButton.SetResourceReference(BackgroundProperty, "ControlBgBrush");
        }
        else if (loaded)
        {
            DictationPowerButton.Content   = "⏻";
            DictationPowerButton.IsEnabled = true;
            DictationPowerButton.SetResourceReference(ForegroundProperty, "AccentTextBrush");
            DictationPowerButton.SetResourceReference(BackgroundProperty, "PrimaryAccentBrush");
        }
        else
        {
            DictationPowerButton.Content   = "⏻";
            DictationPowerButton.IsEnabled = true;
            DictationPowerButton.SetResourceReference(ForegroundProperty, "SidebarDimBrush");
            DictationPowerButton.SetResourceReference(BackgroundProperty, "ControlBgBrush");
        }
        if (MicButton is not null) MicButton.IsEnabled = loaded && !loading;
    }

    private void UpdateMicButton(DictationState state)
    {
        if (MicButton is null) return;
        switch (state)
        {
            case DictationState.Idle:
                MicButton.Content = "🎙";
                MicButton.ToolTip = Properties.Loc.S("Asr_MicBtn_Idle");
                MicButton.SetResourceReference(BackgroundProperty, "ControlBgBrush");
                MicButton.SetResourceReference(ForegroundProperty, "SidebarDimBrush");
                break;
            case DictationState.Listening:
                // Mic open — waiting for manual press, voice trigger, or PTT key
                MicButton.Content = "🎙";
                MicButton.ToolTip = Properties.Loc.S("Asr_MicBtn_Ready");
                MicButton.SetResourceReference(BackgroundProperty, "ControlBgBrush");
                MicButton.SetResourceReference(ForegroundProperty, "PrimaryAccentBrush");
                break;
            case DictationState.Recording:
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
