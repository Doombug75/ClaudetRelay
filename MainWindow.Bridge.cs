using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SysIO = System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using ClaudetRelay.Properties;
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
            Text = Loc.S("Bridge_Mode"), FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
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

        modeRow.Children.Add(MakeModeBtn(Loc.S("Bridge_ModeServer"),     BridgeAgentMode.McpServer));
        modeRow.Children.Add(MakeModeBtn(Loc.S("Bridge_ModeController"), BridgeAgentMode.ModelController));

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

        var serverTabBtn = MakeBridgeSubTabBtn(Loc.S("Bridge_SubTab_Server"), active: true);
        var setupTabBtn  = MakeBridgeSubTabBtn(Loc.S("Bridge_SubTab_Setup"),  active: false);
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
            statusText.Text = Loc.S("Bridge_StatusStopped"); statusText.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
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
            toggleBtn.Content = Loc.S("Bridge_StopServer");
            toggleBtn.SetResourceReference(Button.BackgroundProperty, "ControlHoverBrush");
            toggleBtn.SetResourceReference(Button.ForegroundProperty, "AccentHighlightBrush");
        }
        else
        {
            toggleBtn.Content = Loc.S("Bridge_StartServer");
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
                    MessageBox.Show(Loc.S("Err_InvalidPort"),
                        Loc.S("Dlg_InvalidPort"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var s = SettingsService.Load(); s.McpPort = port; SettingsService.Save(s);
                _mcpServer = new McpServer(port, BuildMcpTools(BridgeAgentMode.McpServer),
                    line => Dispatcher.Invoke(() => BridgeLog(line)),
                    getInstructions: BuildBridgeInstructions);
                try { _mcpServer.Start(); }
                catch (Exception ex)
                {
                    MessageBox.Show($"{Loc.S("Err_McpStartFailed")}\n{ex.Message}",
                        Loc.S("Dlg_BridgeError"), MessageBoxButton.OK, MessageBoxImage.Error);
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
            Text = Loc.S("Lbl_BridgeProject"), FontSize = 10, FontWeight = FontWeights.Bold,
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
            projNameTb.Text = Loc.S("Bridge_NoProjectLoaded");
            projNameTb.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        }
        Grid.SetColumn(projNameTb, 0);
        projRow.Children.Add(projNameTb);

        var loadProjBtn = MakeBridgeSmallBtn(Loc.S("Btn_LoadProject"));
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
                MessageBox.Show(Loc.S("Err_NoProjectsFound"),
                    Loc.S("Dlg_NoProjects"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Simple picker dialog
            var win = new Window
            {
                Title = Loc.S("Dlg_LoadBridgeProject"), Width = 500,
                SizeToContent = SizeToContent.Height, MaxHeight = 600,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, ResizeMode = ResizeMode.NoResize
            };
            ApplyThemeToDialog(win);
            win.SetResourceReference(Window.BackgroundProperty, "ContentBgBrush");

            var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 20) };
            win.Content = panel;
            UiZoomHelper.Apply(win, UiZoomHelper.FromSettings());

            var hdr = new TextBlock
            {
                Text = Loc.S("Bridge_SelectProjectPrompt"),
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
                    Text = $"{Loc.S("Bridge_LastOpened")} {proj.LastOpened:yyyy-MM-dd}  ·  {folder}",
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
                Content = Loc.S("Btn_Cancel"), Padding = new Thickness(16, 7, 16, 7),
                Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand
            };
            if (TryFindResource("ModernButton") is Style mbs) cancelBtn.Style = mbs;
            cancelBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
            cancelBtn.SetResourceReference(Button.ForegroundProperty, "ControlTextBrush");
            cancelBtn.Click += (_, _) => win.DialogResult = false;
            btnRow.Children.Add(cancelBtn);

            var loadBtn = new Button
            {
                Content = Loc.S("Btn_Load"), Padding = new Thickness(16, 7, 16, 7), Cursor = Cursors.Hand
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
            Text = Loc.S("Lbl_ActivityLog"), FontSize = 10, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI"), VerticalAlignment = VerticalAlignment.Center
        };
        logLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        Grid.SetColumn(logLabel, 0);
        logHeaderRow.Children.Add(logLabel);

        var copyLogBtn = MakeBridgeSmallBtn(Loc.S("Bridge_CopyLog"));
        copyLogBtn.ToolTip = Loc.S("Bridge_CopyLogTip");
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

        var mcpSettingsBtn = MakeBridgeSmallBtn(Loc.S("Bridge_McpSettingsBtn"));
        mcpSettingsBtn.FontSize = 12; mcpSettingsBtn.Padding = new Thickness(14, 7, 14, 7);
        mcpSettingsBtn.Margin   = new Thickness(0, 4, 0, 8);
        mcpSettingsBtn.ToolTip  = Loc.S("Bridge_McpSettingsTip");
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

        var chatTabBtn  = MakeBridgeSubTabBtn(Loc.S("Bridge_SubTab_Chat"),  active: true);
        var setupTabBtn = MakeBridgeSubTabBtn(Loc.S("Bridge_SubTab_Setup"), active: false);
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
        AddSectionLabel(chatPanel, Loc.S("Lbl_ControllerParticipant"), topMargin: 0);

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
            Text = Loc.S("Lbl_ControllerChat"), FontSize = 10, FontWeight = FontWeights.Bold,
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

        var ctrlSettingsBtn = MakeBridgeSmallBtn(Loc.S("Bridge_CtrlSettingsBtn"));
        ctrlSettingsBtn.FontSize  = 12;
        ctrlSettingsBtn.Padding   = new Thickness(14, 7, 14, 7);
        ctrlSettingsBtn.Margin    = new Thickness(0, 0, 0, 8);
        ctrlSettingsBtn.ToolTip   = Loc.S("Bridge_CtrlSettingsTip");
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
            Text = Loc.S("Bridge_AgentsFolders"), FontSize = 12, FontWeight = FontWeights.SemiBold,
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
            toggleLbl.Text          = agentsExpanded ? Loc.S("Bridge_AgentsFolders") : Loc.S("Bridge_AgentsFoldersCollapsed");
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
            Text = Loc.S("Lbl_Agents"), FontSize = 10, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI"), VerticalAlignment = VerticalAlignment.Center
        };
        agentsLbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        Grid.SetColumn(agentsLbl, 0);
        agentsHdr.Children.Add(agentsLbl);

        var addBtns = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(addBtns, 1);
        agentsHdr.Children.Add(addBtns);

        var addLocalBtn = MakeBridgeSmallBtn(Loc.S("Bridge_AddLocal"));
        addLocalBtn.ToolTip = Loc.S("Bridge_AddLocalTip");
        addLocalBtn.Click  += (_, _) => ShowAddAgentDialog(isCloud: false, body, cfg);
        addBtns.Children.Add(addLocalBtn);

        var addCloudBtn = MakeBridgeSmallBtn("＋ Cloud");
        addCloudBtn.Margin  = new Thickness(4, 0, 0, 0);
        addCloudBtn.ToolTip = Loc.S("Bridge_AddCloudTip");
        addCloudBtn.Click  += (_, _) => ShowAddAgentDialog(isCloud: true, body, cfg);
        addBtns.Children.Add(addCloudBtn);

        agentsCol.Children.Add(agentsHdr);

        var agentsSub = new TextBlock
        {
            Text = Loc.S("Bridge_ModelsAsTools"), FontSize = 11,
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
            Text = Loc.S("Lbl_Folders"), FontSize = 10, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI"), VerticalAlignment = VerticalAlignment.Center
        };
        foldersLbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        Grid.SetColumn(foldersLbl, 0);
        foldersHdr.Children.Add(foldersLbl);

        var addFolderBtn = MakeBridgeSmallBtn("＋ Add");
        addFolderBtn.ToolTip = Loc.S("Bridge_AddFolderTip");
        Grid.SetColumn(addFolderBtn, 1);
        foldersHdr.Children.Add(addFolderBtn);
        foldersCol.Children.Add(foldersHdr);

        var foldersSub = new TextBlock
        {
            Text = Loc.S("Bridge_ReadablePaths"), FontSize = 11,
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
            Text = Loc.S("Lbl_TempWorkspace"), FontSize = 10, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 0, 0, 4)
        };
        tempLbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        foldersCol.Children.Add(tempLbl);

        var tempSub = new TextBlock
        {
            Text = Loc.S("Bridge_TempWorkspaceHint"),
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
                Text = Loc.S("Bridge_NoAgents"),
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
                Text = Loc.S("Bridge_NoFolders"),
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
                Text = Loc.S("Bridge_AllowWrite"), FontSize = 11,
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
            UiZoomHelper.Apply(warnWin, UiZoomHelper.FromSettings());

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
        UiZoomHelper.Apply(win, UiZoomHelper.FromSettings());
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
                var svc = new OllamaService(agent.ServerUrl) { CurrentModel = agent.Model, NumCtx = agent.OllamaNumCtx, NumPredict = agent.OllamaNumPredict };
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
                    svc = CreateCloudAIService(capturedAgent.Provider, key, capturedAgent.ServerUrl);
                    svc.CurrentModel = capturedAgent.Model;
                    if (capturedAgent.CloudMaxTokens > 0) svc.MaxTokens = capturedAgent.CloudMaxTokens;
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

        // ── Dynamic agent handler factory ────────────────────────────────
        // Always builds a fresh handler from a BridgeAgent so tools are not
        // limited to agents that existed when BuildMcpTools() was first called.
        Func<string, CancellationToken, Task<string>> MakeAgentHandler(BridgeAgent capturedAgent)
        {
            if (capturedAgent.IsLocal)
            {
                var svc        = new OllamaService(capturedAgent.ServerUrl) { CurrentModel = capturedAgent.Model, NumCtx = capturedAgent.OllamaNumCtx, NumPredict = capturedAgent.OllamaNumPredict };
                var serverUrl  = capturedAgent.ServerUrl;
                return async (msg, c) =>
                {
                    // Serialize per Ollama instance — same guard as RunOllamaStreamAsync
                    var sem = _ollamaServerSemaphores.GetOrAdd(serverUrl,
                                  _ => new System.Threading.SemaphoreSlim(1, 1));
                    await sem.WaitAsync(c);
                    try
                    {
                        var history = new List<OllamaChatMessage> { new("user", msg) };
                        var sb2 = new StringBuilder();
                        await foreach (var tok in svc.StreamAsync(history, c)) sb2.Append(tok);
                        return sb2.ToString().Trim();
                    }
                    finally { sem.Release(); }
                };
            }
            else
            {
                var existing = _cloudAIParticipants.FirstOrDefault(u =>
                    u.Data.Enabled &&
                    string.Equals(u.Data.Service.ProviderName, capturedAgent.Provider, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(u.Data.Service.CurrentModel,  capturedAgent.Model,    StringComparison.OrdinalIgnoreCase));
                ICloudAIService dynSvc;
                if (existing is not null)
                    dynSvc = existing.Data.Service;
                else
                {
                    var key = WindowsCredentialManager.Load(capturedAgent.Provider) ?? "";
                    dynSvc = CreateCloudAIService(capturedAgent.Provider, key, capturedAgent.ServerUrl);
                    dynSvc.CurrentModel = capturedAgent.Model;
                    if (capturedAgent.CloudMaxTokens > 0) dynSvc.MaxTokens = capturedAgent.CloudMaxTokens;
                }
                return async (msg, c) =>
                {
                    var history = new List<CloudAIMessage> { new("user", msg) };
                    var sb2 = new StringBuilder();
                    await foreach (var tok in dynSvc.StreamAsync(history, ct: c)) sb2.Append(tok);
                    return sb2.ToString().Trim();
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
                // Resolve dynamically so agents added after server start are found
                var freshCfg  = SettingsService.Load();
                var agentDef  = freshCfg.BridgeAgents.Where(a => a.IsEnabled)
                    .FirstOrDefault(a => string.Equals(a.Label, name, StringComparison.OrdinalIgnoreCase));
                if (agentDef is null)
                    return $"Error: no agent named '{name}' is currently enabled. " +
                           $"Call bridge_list_agents to see available agents.";

                return await MakeAgentHandler(agentDef)(message, ct);
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

                // Reload fresh so agents added after server start are included
                var postCfg      = SettingsService.Load();
                var activeAgents = postCfg.BridgeAgents.Where(a => a.IsEnabled).ToList();
                if (activeAgents.Count == 0)
                    return "Error: no Bridge agents are configured or enabled. " +
                           "Add agents in the Bridge → Agents panel first.";

                var results = new List<(string Name, string Response)>();

                if (parallel)
                {
                    var tasks = activeAgents.Select(async agent =>
                    {
                        var handler = MakeAgentHandler(agent);
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
                        var handler = MakeAgentHandler(agent);
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

                // Resolve dynamically so agents added after server start are found
                var ratCfg    = SettingsService.Load();
                var ratAgent  = ratCfg.BridgeAgents.Where(a => a.IsEnabled)
                    .FirstOrDefault(a => string.Equals(a.Label, name, StringComparison.OrdinalIgnoreCase));
                if (ratAgent is null)
                    return $"Error: no agent named '{name}'. Call bridge_list_agents.";
                var handler = MakeAgentHandler(ratAgent);

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
                {
#pragma warning disable CS4014   // intentional fire-and-forget — dispatcher runs independently
                    Dispatcher.InvokeAsync(async () => await TriggerAiResponsesAsync());
#pragma warning restore CS4014
                }

                var preview = message.Length > 80 ? message[..80] + "…" : message;
                return $"✓ Posted as '{name}': \"{preview}\"{(triggerResponses ? "" : " (no AI responses triggered)")}";
            }
        });

        // ── chat_post_whisper - private task to one participant only ──────────
        AddTool(new McpTool
        {
            Name        = "chat_post_whisper",
            Description = "Sends a private message (whisper) to a single named AI participant. " +
                          "Only the target participant receives and responds to the message — " +
                          "other participants see only a '🤫 whisper' notice and do NOT respond. " +
                          "Use this instead of chat_post_message when you want a one-on-one task " +
                          "without triggering the whole group. " +
                          "The target must be an active, enabled participant by their display name " +
                          "(e.g. 'Qwen', 'Gemma', 'Llama').",
            Provider    = "Bridge",
            InputSchemaOverride = """
                {
                  "type": "object",
                  "properties": {
                    "target":  { "type": "string", "description": "Display name of the participant to whisper to" },
                    "message": { "type": "string", "description": "The private message to send" }
                  },
                  "required": ["target", "message"]
                }
                """,
            ExecuteAsync = async (args, _) =>
            {
                var target  = args["target"]?.GetValue<string>()?.Trim()  ?? "";
                var message = args["message"]?.GetValue<string>()?.Trim() ?? "";

                if (string.IsNullOrWhiteSpace(target))
                    return "Error: 'target' is required — provide the participant's display name.";
                if (string.IsNullOrWhiteSpace(message))
                    return "Error: 'message' cannot be empty.";

                // Gate: project chat requires McpChatEnabled
                if (_currentProjectFolder is not null && _projectSettings is not null && !_projectSettings.McpChatEnabled)
                    return $"MCP chat access is not enabled for '{_projectSettings.ProjectName}'. " +
                           "Add an MCP Client via the + participant button to enable it.";

                string? result = null;
                await Dispatcher.InvokeAsync(() =>
                {
                    // Look up the participant by display name
                    var ollamaTarget = _ollamaParticipants
                        .FirstOrDefault(u => string.Equals(GetEffectiveName(u), target,
                                             StringComparison.OrdinalIgnoreCase));
                    var cloudTarget = ollamaTarget is null
                        ? _cloudAIParticipants.FirstOrDefault(u =>
                              string.Equals(GetEffectiveName(u), target,
                                            StringComparison.OrdinalIgnoreCase))
                        : null;

                    if (ollamaTarget is null && cloudTarget is null)
                    {
                        var available = string.Join(", ",
                            _ollamaParticipants .Select(GetEffectiveName).Concat(
                            _cloudAIParticipants.Select(GetEffectiveName)));
                        result = $"Error: no participant named '{target}' found. " +
                                 $"Available: {available}";
                        return;
                    }

                    var displayName = ollamaTarget is not null
                        ? GetEffectiveName(ollamaTarget)
                        : GetEffectiveName(cloudTarget!);

                    AddSystemMessage($"🤫  MCP whispers something to {displayName}");
                    DispatchPrivateTask(message, ollamaTarget, cloudTarget, displayName);
                    result = $"✓ Whispered to '{displayName}' — only they will respond.";
                });

                return result ?? "Error: whisper dispatch failed.";
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

    private void ShowMcpBridgeSettingsWindow()   => ShowBridgeSettingsWindowCore(Loc.S("BridgeSettings_McpTitle"),  isMcp: true);
    private void ShowControllerBridgeSettingsWindow() => ShowBridgeSettingsWindowCore(Loc.S("BridgeSettings_CtrlTitle"), isMcp: false);

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
        UiZoomHelper.Apply(win, UiZoomHelper.FromSettings());

        var root = new StackPanel();
        scroll.Content = root;

        // ── Tool access section (only for this window's mode) ─────────
        if (isMcp)
        {
            BuildToolListSection(root, cfg,
                icon: "🔌", title: Loc.S("BridgeSettings_McpToolsSection"),
                subtitle: Loc.S("BridgeSettings_McpSubtitle"),
                disabledList: cfg.DisabledMcpServerTools,
                saveDisabled: list => { var s = SettingsService.Load(); s.DisabledMcpServerTools = list; SettingsService.Save(s); });
        }
        else
        {
            BuildToolListSection(root, cfg,
                icon: "🤖", title: Loc.S("BridgeSettings_CtrlToolsSection"),
                subtitle: Loc.S("BridgeSettings_CtrlSubtitle"),
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
            Text = string.Format(Loc.S("BridgeSettings_ToolsEnabled"), totalTools - disabledList.Count, totalTools),
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
                catLbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
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
                countLbl.Text = string.Format(Loc.S("BridgeSettings_ToolsEnabled"), totalTools - disabledList.Count, totalTools);
            };
            cb.Unchecked += (_, _) =>
            {
                if (!disabledList.Contains(captured, StringComparer.OrdinalIgnoreCase))
                    disabledList.Add(captured);
                saveDisabled(disabledList);
                countLbl.Text = string.Format(Loc.S("BridgeSettings_ToolsEnabled"), totalTools - disabledList.Count, totalTools);
            };
        }
    }

    private void BuildLimitsSection(StackPanel parent, AppSettings cfg, bool isMcp)
    {
        var title = new TextBlock
        {
            Text = Loc.S("BridgeSettings_FileLimits"), FontSize = 10, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 0, 0, 4)
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        parent.Children.Add(title);

        var noteText = isMcp
            ? Loc.S("BridgeSettings_McpLimitsNote")
            : Loc.S("BridgeSettings_CtrlLimitsNote");

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
        ("chat_post_whisper",        "Sends a private whisper to a single named participant — only they respond, others just see a 🤫 notice. Use for one-on-one tasks without triggering the whole group."),
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
                catLbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
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
}
