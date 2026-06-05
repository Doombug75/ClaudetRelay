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
    private bool                                 _buccaneeerMode        = false;
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



    // World-building editor methods live in MainWindow.World.cs


}
