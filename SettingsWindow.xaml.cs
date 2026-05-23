using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ClaudetRelay.Services;

namespace ClaudetRelay;

public partial class SettingsWindow : Window
{
    // ── Nested types ───────────────────────────────────────────────────────

    private sealed class ParticipantForm
    {
        public required int         SlotIndex        { get; init; }
        public required TabItem     Tab              { get; init; }
        public required CheckBox    EnabledCheck     { get; init; }
        public required TextBox     NameBox          { get; init; }
        public required ComboBox    TypeCombo        { get; init; }
        // Ollama section
        public required Border      OllamaSection    { get; init; }
        public required TextBox     ServerUrlBox     { get; init; }
        public required ComboBox    OllamaModelCombo { get; init; }
        public required TextBlock   OllamaTestLabel  { get; init; }
        // Cloud AI section
        public required Border      CloudAISection   { get; init; }
        public required PasswordBox ApiKeyBox        { get; init; }
        public required TextBox     ApiKeyTextBox    { get; init; }
        // ApiKeyOuter is the Border wrapping ApiKeyBox; its Visibility tells us which mode is active
        public required Border      ApiKeyOuter      { get; init; }
        public required TextBlock   ApiKeyHintLabel  { get; init; }
        public required ComboBox    CloudModelCombo  { get; init; }
        public required TextBlock   CloudTestLabel   { get; init; }
        // Role section
        public required CheckBox    CoordinatorCheck  { get; init; }
        public required CheckBox    ReasonerCheck     { get; init; }
        public required Slider      PrioritySlider    { get; init; }
        public required Border      PrioritySection   { get; init; }

        public string CurrentProvider =>
            (TypeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Ollama";

        // Password mode is active when ApiKeyOuter (the PasswordBox wrapper) is visible
        public string CurrentApiKey =>
            ApiKeyOuter.Visibility == Visibility.Visible
                ? ApiKeyBox.Password
                : ApiKeyTextBox.Text;
    }

    // ── State ──────────────────────────────────────────────────────────────

    private readonly List<ParticipantForm> _forms = [];
    private TextBox  _userNameBox       = null!;
    private TextBox  _projectsFolderBox = null!;
    private Slider   _toneSlider        = null!;

    // ── Constructor ────────────────────────────────────────────────────────

    public SettingsWindow(string? currentThemePath)
    {
        if (currentThemePath is not null)
        {
            try
            {
                Resources.MergedDictionaries.Add(
                    new ResourceDictionary { Source = new Uri(currentThemePath) });
            }
            catch { /* no theme – window still usable */ }
        }

        InitializeComponent();

        var settings = SettingsService.Load();

        // "General" tab is always first
        BuildGeneralTab(settings);

        // P1–P8
        for (int i = 0; i < 8; i++)
        {
            var config = i < settings.Participants.Count
                ? settings.Participants[i]
                : new ParticipantConfig();
            BuildTab(i, config);
        }

        // Auto-test all participants once the window is fully rendered
        Loaded += async (_, _) => await AutoTestAllAsync();
    }

    // ── Input helpers ──────────────────────────────────────────────────────
    // Same pattern as MainWindow's InputTextBox: transparent inner control +
    // outer Border that supplies the background and rounded corners.

    /// <summary>Returns a 36 px rounded input: (outer Border for layout, inner TextBox for data).</summary>
    private (Border Outer, TextBox Input) MakeTextInput(string text = "")
    {
        var tb = new TextBox { Style = (Style)FindResource("STextBox"), Text = text };
        // Local values beat any system-theme setter
        tb.FontSize   = 14;
        tb.FontFamily = new FontFamily("Segoe UI");
        tb.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        tb.SetResourceReference(TextBox.CaretBrushProperty, "TextBrush");

        var outer = new Border { Height = 36, CornerRadius = new CornerRadius(8) };
        outer.SetResourceReference(Border.BackgroundProperty, "InputBrush");
        outer.Child = tb;
        return (outer, tb);
    }

    /// <summary>Returns a 36 px rounded password input: (outer Border, inner PasswordBox).</summary>
    private (Border Outer, PasswordBox Input) MakePasswordInput()
    {
        var pb = new PasswordBox { Style = (Style)FindResource("SPasswordBox") };
        pb.FontSize   = 14;
        pb.FontFamily = new FontFamily("Segoe UI");
        pb.SetResourceReference(Control.ForegroundProperty, "TextBrush");

        var outer = new Border { Height = 36, CornerRadius = new CornerRadius(8) };
        outer.SetResourceReference(Border.BackgroundProperty, "InputBrush");
        outer.Child = pb;
        return (outer, pb);
    }

    // ── General tab ────────────────────────────────────────────────────────

    private void BuildGeneralTab(AppSettings settings)
    {
        var nameLabel = new TextBlock { Style = (Style)FindResource("SLabel"), Text = "YOUR NAME" };

        var (userNameOuter, userNameInput) = MakeTextInput(
            string.IsNullOrWhiteSpace(settings.UserName) ? "You" : settings.UserName);
        _userNameBox          = userNameInput;
        userNameOuter.Margin  = new Thickness(0, 0, 0, 6);

        var nameHint = new TextBlock
        {
            Text         = "Shown on your own chat bubbles",
            FontSize     = 11,
            FontFamily   = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 18)
        };
        nameHint.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");

        // ── PROJECTS FOLDER ────────────────────────────────────────────────
        var folderLabel = new TextBlock
        {
            Style  = (Style)FindResource("SLabel"),
            Text   = "PROJECTS FOLDER",
            Margin = new Thickness(0, 0, 0, 6)
        };

        var defaultFolder = ProjectService.GetDefaultProjectsFolder();

        var (folderOuter, folderBox) = MakeTextInput(
            string.IsNullOrWhiteSpace(settings.ProjectsFolder) ? "" : settings.ProjectsFolder);
        _projectsFolderBox = folderBox;

        var browseFolderBtn = new Button
        {
            Content = "📁 Browse",
            Style   = (Style)FindResource("SButtonSecondary"),
            Height  = 36,
            Margin  = new Thickness(6, 0, 0, 0),
            ToolTip = "Open a folder picker"
        };
        browseFolderBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select Projects Folder",
                InitialDirectory = string.IsNullOrWhiteSpace(folderBox.Text)
                    ? defaultFolder
                    : folderBox.Text
            };
            if (dlg.ShowDialog(this) == true)
                folderBox.Text = dlg.FolderName;
        };

        var defaultFolderBtn = new Button
        {
            Content  = "↩ Default",
            Style    = (Style)FindResource("SButtonSecondary"),
            Height   = 36,
            Margin   = new Thickness(6, 0, 0, 0),
            ToolTip  = defaultFolder
        };
        defaultFolderBtn.Click += (_, _) => folderBox.Text = "";

        var folderGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        folderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(folderOuter,      0);
        Grid.SetColumn(browseFolderBtn,  1);
        Grid.SetColumn(defaultFolderBtn, 2);
        folderGrid.Children.Add(folderOuter);
        folderGrid.Children.Add(browseFolderBtn);
        folderGrid.Children.Add(defaultFolderBtn);

        var folderHint = new TextBlock
        {
            Text         = $"Default: {defaultFolder}",
            FontSize     = 11,
            FontFamily   = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap
        };
        folderHint.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");

        // ── RESPONSE TONE ──────────────────────────────────────────────────
        var settings2 = SettingsService.Load(); // fresh read for ToneLevel
        var toneLabel = new TextBlock
        {
            Style  = (Style)FindResource("SLabel"),
            Text   = "RESPONSE TONE",
            Margin = new Thickness(0, 18, 0, 6)
        };

        var toneValueLabel = new TextBlock
        {
            FontSize   = 12,
            FontFamily = new FontFamily("Segoe UI"),
            Text       = FormatToneLabel(settings2.ToneLevel),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin     = new Thickness(0, 0, 0, 4)
        };
        toneValueLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var toneSlider = new Slider
        {
            Minimum               = 0,
            Maximum               = 100,
            Value                 = settings2.ToneLevel,
            TickFrequency         = 10,
            IsSnapToTickEnabled   = false,
            Margin                = new Thickness(0, 0, 0, 4)
        };
        _toneSlider = toneSlider;
        toneSlider.ValueChanged += (_, e) =>
            toneValueLabel.Text = FormatToneLabel((int)e.NewValue);

        var toneRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        toneRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toneRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toneRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var toneLeft = MakeHintText("Neutral");
        var toneRight = MakeHintText("Freundlich");
        Grid.SetColumn(toneLeft,   0);
        Grid.SetColumn(toneSlider, 1);
        Grid.SetColumn(toneRight,  2);
        toneRow.Children.Add(toneLeft);
        toneRow.Children.Add(toneSlider);
        toneRow.Children.Add(toneRight);

        var toneHint = MakeHintText("0 = streng neutral  ·  50 = Modell-Standard (kein Eingriff)  ·  100 = sehr freundlich");

        var root = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        root.Children.Add(nameLabel);
        root.Children.Add(userNameOuter);
        root.Children.Add(nameHint);
        root.Children.Add(folderLabel);
        root.Children.Add(folderGrid);
        root.Children.Add(folderHint);
        root.Children.Add(toneLabel);
        root.Children.Add(toneValueLabel);
        root.Children.Add(toneRow);
        root.Children.Add(toneHint);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root
        };
        var tab = new TabItem { Header = "General", Content = scroll };
        ParticipantsTabControl.Items.Add(tab);
    }

    // ── Auto-test on open ──────────────────────────────────────────────────

    private async Task AutoTestAllAsync()
    {
        var tasks = _forms.Select(async form =>
        {
            if (form.CurrentProvider == "Ollama")
                await TestOllamaAsync(form);
            else if (!string.IsNullOrEmpty(form.CurrentApiKey))
                await TestCloudAIAsync(form);
        });
        await Task.WhenAll(tasks);
    }

    // ── Tab builder ────────────────────────────────────────────────────────

    private void BuildTab(int index, ParticipantConfig config)
    {
        bool isOllama  = config.Type == "Ollama";
        var  tabHeader = string.IsNullOrWhiteSpace(config.Name) ? $"P{index + 1}" : config.Name;

        // ── Enable checkbox ───────────────────────────────────────────────
        var enabledCheck = new CheckBox
        {
            Style     = (Style)FindResource("SToggle"),
            IsChecked = config.Enabled,
            Content   = "Enable this participant",
            Margin    = new Thickness(0, 0, 0, 14)
        };

        // ── Name + Type row ───────────────────────────────────────────────
        var nameLabel = new TextBlock { Style = (Style)FindResource("SLabel"), Text = "NAME" };
        var (nameBoxOuter, nameBox) = MakeTextInput(config.Name);

        var typeLabel = new TextBlock { Style = (Style)FindResource("SLabel"), Text = "TYPE" };
        var typeCombo = new ComboBox { Style = (Style)FindResource("SComboBox") };
        foreach (var t in new[] { "Ollama", "Anthropic", "OpenAI ChatGPT", "Google AI", "Groq", "xAI Grok", "OpenRouter", "Mistral" })
            typeCombo.Items.Add(new ComboBoxItem { Content = t });
        SelectComboByContent(typeCombo, config.Type);

        var nameCol = new StackPanel { Margin = new Thickness(0, 0, 8, 14) };
        nameCol.Children.Add(nameLabel);
        nameCol.Children.Add(nameBoxOuter);

        var typeCol = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        typeCol.Children.Add(typeLabel);
        typeCol.Children.Add(typeCombo);

        var nameTypeGrid = new Grid();
        nameTypeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameTypeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(nameCol, 0);
        Grid.SetColumn(typeCol, 1);
        nameTypeGrid.Children.Add(nameCol);
        nameTypeGrid.Children.Add(typeCol);

        // ── ROLE section ─────────────────────────────────────────────────────
        var roleLabel = new TextBlock
        {
            Style  = (Style)FindResource("SLabel"),
            Text   = "ROLE",
            Margin = new Thickness(0, 0, 0, 6)
        };

        var coordinatorCheck = new CheckBox
        {
            Style     = (Style)FindResource("SToggle"),
            IsChecked = config.IsCoordinator,
            Content   = "Coordinator",
            Margin    = new Thickness(0, 0, 0, 4),
            ToolTip   = "Receives every user message first and decides who should respond.\n" +
                        "Only one Coordinator should be active per project.\n" +
                        "The Coordinator can delegate tasks to Reasoners or handle them directly."
        };

        var reasonerCheck = new CheckBox
        {
            Style     = (Style)FindResource("SToggle"),
            IsChecked = config.IsReasoner,
            Content   = "Reasoner",
            Margin    = new Thickness(0, 0, 0, 8),
            ToolTip   = "Executes specialised tasks delegated by the Coordinator.\n" +
                        "Multiple Reasoners can be active; higher priority is preferred first.\n" +
                        "Reasoners respond only when called by the Coordinator or the user."
        };

        var priorityLabel = new TextBlock
        {
            Style  = (Style)FindResource("SLabel"),
            Text   = "REASONER PRIORITY",
            Margin = new Thickness(0, 0, 0, 4)
        };

        var prioritySlider = new Slider
        {
            Minimum             = 1,
            Maximum             = 10,
            Value               = config.ReasonerPriority,
            TickFrequency       = 1,
            IsSnapToTickEnabled = true,
            Margin              = new Thickness(0, 0, 0, 4),
            ToolTip             = "1 = lowest priority, 10 = highest priority.\n" +
                                  "The Coordinator prefers higher-priority Reasoners for complex tasks."
        };
        var priorityValueLabel = new TextBlock
        {
            FontSize   = 12,
            FontFamily = new FontFamily("Segoe UI"),
            Text       = $"Priority: {(int)config.ReasonerPriority}",
            Margin     = new Thickness(0, 0, 0, 8)
        };
        priorityValueLabel.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");
        prioritySlider.ValueChanged += (_, e) =>
            priorityValueLabel.Text = $"Priority: {(int)e.NewValue}";

        var priorityContent = new StackPanel();
        priorityContent.Children.Add(priorityLabel);
        priorityContent.Children.Add(prioritySlider);
        priorityContent.Children.Add(priorityValueLabel);

        var prioritySection = new Border
        {
            Visibility = config.IsReasoner ? Visibility.Visible : Visibility.Collapsed,
            Child      = priorityContent
        };

        reasonerCheck.Checked   += (_, _) => prioritySection.Visibility = Visibility.Visible;
        reasonerCheck.Unchecked += (_, _) => prioritySection.Visibility = Visibility.Collapsed;

        var roleStack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        roleStack.Children.Add(roleLabel);
        roleStack.Children.Add(coordinatorCheck);
        roleStack.Children.Add(reasonerCheck);
        roleStack.Children.Add(prioritySection);

        // Separator
        var sep = new Rectangle { Style = (Style)FindResource("SSep") };

        // ── OLLAMA SECTION ────────────────────────────────────────────────

        var serverLabel = new TextBlock { Style = (Style)FindResource("SLabel"), Text = "SERVER URL" };

        var (serverUrlOuter, serverUrlBox) = MakeTextInput(
            string.IsNullOrEmpty(config.ServerUrl) ? "http://localhost:11434" : config.ServerUrl);

        var localhostBtn = new Button
        {
            Content = "↩ Localhost",
            Style   = (Style)FindResource("SButtonSecondary"),
            Margin  = new Thickness(6, 0, 0, 0),
            Height  = 36
        };

        var serverGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        serverGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        serverGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(serverUrlOuter, 0);
        Grid.SetColumn(localhostBtn,   1);
        serverGrid.Children.Add(serverUrlOuter);
        serverGrid.Children.Add(localhostBtn);

        var ollamaModelLabel = new TextBlock { Style = (Style)FindResource("SLabel"), Text = "MODEL" };
        var ollamaModelCombo = new ComboBox
        {
            Style  = (Style)FindResource("SComboBox"),
            Margin = new Thickness(0, 0, 0, 10)
        };
        if (!string.IsNullOrEmpty(config.Model) && isOllama)
            ollamaModelCombo.Items.Add(new ComboBoxItem { Content = config.Model, IsSelected = true });

        var ollamaHint = new TextBlock
        {
            Text         = "Test connection to load available models",
            FontSize     = 11,
            FontFamily   = new FontFamily("Segoe UI"),
            Margin       = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap
        };
        ollamaHint.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");

        var ollamaTestBtn = new Button
        {
            Content = "Test Connection",
            Style   = (Style)FindResource("SButtonSecondary"),
            Margin  = new Thickness(0, 0, 10, 0)
        };
        var ollamaTestLabel = new TextBlock
        {
            FontSize          = 13,
            FontFamily        = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center
        };
        ollamaTestLabel.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");

        var ollamaTestRow = new StackPanel { Orientation = Orientation.Horizontal };
        ollamaTestRow.Children.Add(ollamaTestBtn);
        ollamaTestRow.Children.Add(ollamaTestLabel);

        var ollamaContent = new StackPanel();
        ollamaContent.Children.Add(serverLabel);
        ollamaContent.Children.Add(serverGrid);
        ollamaContent.Children.Add(ollamaModelLabel);
        ollamaContent.Children.Add(ollamaModelCombo);
        ollamaContent.Children.Add(ollamaHint);
        ollamaContent.Children.Add(ollamaTestRow);

        var ollamaSection = new Border
        {
            Visibility = isOllama ? Visibility.Visible : Visibility.Collapsed,
            Child      = ollamaContent
        };

        // ── CLOUD AI SECTION ──────────────────────────────────────────────

        var apiKeyLabel = new TextBlock { Style = (Style)FindResource("SLabel"), Text = "API KEY" };

        // Password mode (default visible) and show-key mode (initially collapsed)
        var (apiKeyOuter, apiKeyBox)         = MakePasswordInput();
        var (apiKeyTextOuter, apiKeyTextBox) = MakeTextInput();
        apiKeyTextOuter.Visibility = Visibility.Collapsed;

        var showHideBtn = new Button
        {
            Content  = "👁",
            Width    = 36,
            Height   = 36,
            FontSize = 15,
            Margin   = new Thickness(6, 0, 0, 0),
            Style    = (Style)FindResource("SButtonSecondary"),
            ToolTip  = "Show / hide key"
        };

        // Pre-load API key from Credential Manager for the current provider
        if (!isOllama)
        {
            var existingKey      = WindowsCredentialManager.Load(config.Type) ?? "";
            apiKeyBox.Password   = existingKey;
            apiKeyTextBox.Text   = existingKey;
        }

        var apiKeyGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        apiKeyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        apiKeyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(apiKeyOuter,     0);
        Grid.SetColumn(apiKeyTextOuter, 0);   // same cell — they toggle
        Grid.SetColumn(showHideBtn,     1);
        apiKeyGrid.Children.Add(apiKeyOuter);
        apiKeyGrid.Children.Add(apiKeyTextOuter);
        apiKeyGrid.Children.Add(showHideBtn);

        var apiKeyHint = new TextBlock
        {
            FontSize     = 11,
            FontFamily   = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 12)
        };
        apiKeyHint.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");
        UpdateApiKeyHint(apiKeyHint, config.Type);

        var cloudModelLabel = new TextBlock { Style = (Style)FindResource("SLabel"), Text = "MODEL" };
        var cloudModelCombo = new ComboBox
        {
            Style  = (Style)FindResource("SComboBox"),
            Margin = new Thickness(0, 0, 0, 10)
        };
        PopulateCloudModelCombo(cloudModelCombo, config.Type, config.Model);

        var cloudTestBtn = new Button
        {
            Content = "Test Connection",
            Style   = (Style)FindResource("SButtonSecondary"),
            Margin  = new Thickness(0, 0, 10, 0)
        };
        var cloudTestLabel = new TextBlock
        {
            FontSize          = 13,
            FontFamily        = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center
        };
        cloudTestLabel.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");

        var cloudTestRow = new StackPanel { Orientation = Orientation.Horizontal };
        cloudTestRow.Children.Add(cloudTestBtn);
        cloudTestRow.Children.Add(cloudTestLabel);

        var cloudContent = new StackPanel();
        cloudContent.Children.Add(apiKeyLabel);
        cloudContent.Children.Add(apiKeyGrid);
        cloudContent.Children.Add(apiKeyHint);
        cloudContent.Children.Add(cloudModelLabel);
        cloudContent.Children.Add(cloudModelCombo);
        cloudContent.Children.Add(cloudTestRow);

        var cloudAISection = new Border
        {
            Visibility = isOllama ? Visibility.Collapsed : Visibility.Visible,
            Child      = cloudContent
        };

        // ── Assemble tab content ───────────────────────────────────────────
        var root = new StackPanel();
        root.Children.Add(enabledCheck);
        root.Children.Add(nameTypeGrid);
        root.Children.Add(roleStack);
        root.Children.Add(sep);
        root.Children.Add(ollamaSection);
        root.Children.Add(cloudAISection);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root
        };

        var tab = new TabItem { Header = tabHeader, Content = scroll };
        ParticipantsTabControl.Items.Add(tab);

        // ── Build form record ─────────────────────────────────────────────
        var form = new ParticipantForm
        {
            SlotIndex        = index,
            Tab              = tab,
            EnabledCheck     = enabledCheck,
            NameBox          = nameBox,
            TypeCombo        = typeCombo,
            OllamaSection    = ollamaSection,
            ServerUrlBox     = serverUrlBox,
            OllamaModelCombo = ollamaModelCombo,
            OllamaTestLabel  = ollamaTestLabel,
            CloudAISection   = cloudAISection,
            ApiKeyBox        = apiKeyBox,
            ApiKeyTextBox    = apiKeyTextBox,
            ApiKeyOuter      = apiKeyOuter,
            ApiKeyHintLabel  = apiKeyHint,
            CloudModelCombo  = cloudModelCombo,
            CloudTestLabel   = cloudTestLabel,
            CoordinatorCheck = coordinatorCheck,
            ReasonerCheck    = reasonerCheck,
            PrioritySlider   = prioritySlider,
            PrioritySection  = prioritySection
        };
        _forms.Add(form);

        // ── Events ────────────────────────────────────────────────────────

        nameBox.TextChanged += (_, _) =>
        {
            var h = string.IsNullOrWhiteSpace(form.NameBox.Text)
                ? $"P{index + 1}"
                : form.NameBox.Text.Trim();
            form.Tab.Header = h;
        };

        typeCombo.SelectionChanged += (_, _) =>
        {
            ApplySectionVisibility(form);
            var provider = form.CurrentProvider;
            UpdateApiKeyHint(form.ApiKeyHintLabel, provider);
            if (provider != "Ollama")
            {
                var key                  = WindowsCredentialManager.Load(provider) ?? "";
                form.ApiKeyBox.Password  = key;
                form.ApiKeyTextBox.Text  = key;
                PopulateCloudModelCombo(form.CloudModelCombo, provider, "");
            }
        };

        // Toggle password / plain-text view using the OUTER borders
        showHideBtn.Click += (_, _) =>
        {
            if (apiKeyOuter.Visibility == Visibility.Visible)   // currently showing password dots
            {
                apiKeyTextBox.Text         = apiKeyBox.Password;
                apiKeyOuter.Visibility     = Visibility.Collapsed;
                apiKeyTextOuter.Visibility = Visibility.Visible;
                apiKeyTextBox.Focus();
                apiKeyTextBox.CaretIndex   = apiKeyTextBox.Text.Length;
            }
            else                                                 // currently showing plain text
            {
                apiKeyBox.Password         = apiKeyTextBox.Text;
                apiKeyTextOuter.Visibility = Visibility.Collapsed;
                apiKeyOuter.Visibility     = Visibility.Visible;
            }
        };

        apiKeyBox.PasswordChanged += (_, _) => apiKeyTextBox.Text  = apiKeyBox.Password;
        apiKeyTextBox.TextChanged  += (_, _) => apiKeyBox.Password = apiKeyTextBox.Text;

        localhostBtn.Click += (_, _) => serverUrlBox.Text = "http://localhost:11434";

        ollamaTestBtn.Click += async (_, _) => await TestOllamaAsync(form);
        cloudTestBtn.Click  += async (_, _) => await TestCloudAIAsync(form);
    }

    // ── Section visibility helper ──────────────────────────────────────────

    private static void ApplySectionVisibility(ParticipantForm form)
    {
        bool isOllama = form.CurrentProvider == "Ollama";
        form.OllamaSection .Visibility = isOllama ? Visibility.Visible   : Visibility.Collapsed;
        form.CloudAISection.Visibility = isOllama ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── Test buttons ───────────────────────────────────────────────────────

    private async Task TestOllamaAsync(ParticipantForm form)
    {
        form.OllamaTestLabel.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");
        form.OllamaTestLabel.Text = "Testing…";

        try
        {
            var url = form.ServerUrlBox.Text.Trim();
            if (string.IsNullOrEmpty(url)) url = "http://localhost:11434";

            using var svc = new OllamaService(url);
            var ok = await svc.IsAvailableAsync();

            form.OllamaTestLabel.SetResourceReference(TextBlock.ForegroundProperty,
                ok ? "OllamaBrush" : "AccentBrush");
            form.OllamaTestLabel.Text = ok ? "Online ✓" : "Offline ✗";

            if (ok)
            {
                try
                {
                    var models = await svc.GetModelsAsync();
                    if (models.Count > 0)
                    {
                        var current = (form.OllamaModelCombo.SelectedItem as ComboBoxItem)
                                        ?.Content?.ToString() ?? "";
                        form.OllamaModelCombo.Items.Clear();
                        foreach (var m in models)
                        {
                            var item = new ComboBoxItem { Content = m };
                            form.OllamaModelCombo.Items.Add(item);
                            if (m == current) form.OllamaModelCombo.SelectedItem = item;
                        }
                        if (form.OllamaModelCombo.SelectedItem is null &&
                            form.OllamaModelCombo.Items.Count > 0)
                            form.OllamaModelCombo.SelectedIndex = 0;
                    }
                }
                catch { /* keep list as-is */ }
            }
        }
        catch (Exception ex)
        {
            form.OllamaTestLabel.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            form.OllamaTestLabel.Text = $"Error: {ex.Message}";
        }
    }

    private async Task TestCloudAIAsync(ParticipantForm form)
    {
        form.CloudTestLabel.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");
        form.CloudTestLabel.Text = "Testing…";

        try
        {
            var apiKey = form.CurrentApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                form.CloudTestLabel.Text = "⚠  Enter an API key first.";
                return;
            }

            using var svc = BuildCloudAIService(form.CurrentProvider, apiKey);
            var ok = await svc.IsAvailableAsync();

            form.CloudTestLabel.SetResourceReference(TextBlock.ForegroundProperty,
                ok ? "OllamaBrush" : "AccentBrush");
            form.CloudTestLabel.Text = ok ? "Connected ✓" : "Failed ✗  (check your key)";

            if (ok)
            {
                try
                {
                    var models = await svc.GetModelsAsync();
                    if (models.Count > 0)
                    {
                        var current = (form.CloudModelCombo.SelectedItem as ComboBoxItem)
                                        ?.Content?.ToString() ?? "";
                        form.CloudModelCombo.Items.Clear();
                        foreach (var m in models)
                        {
                            var item = new ComboBoxItem { Content = m };
                            form.CloudModelCombo.Items.Add(item);
                            if (m == current) form.CloudModelCombo.SelectedItem = item;
                        }
                        if (form.CloudModelCombo.SelectedItem is null &&
                            form.CloudModelCombo.Items.Count > 0)
                            form.CloudModelCombo.SelectedIndex = 0;
                    }
                }
                catch { /* keep default list */ }
            }
        }
        catch (Exception ex)
        {
            form.CloudTestLabel.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            form.CloudTestLabel.Text = $"Error: {ex.Message}";
        }
    }

    // ── Save All ───────────────────────────────────────────────────────────

    private void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsService.Load();

        // General settings
        var userName = _userNameBox.Text.Trim();
        settings.UserName = string.IsNullOrEmpty(userName) ? "You" : userName;

        settings.ProjectsFolder = _projectsFolderBox.Text.Trim();
        settings.ToneLevel      = (int)_toneSlider.Value;

        settings.Participants.Clear();

        foreach (var form in _forms)
        {
            var isOllama = form.CurrentProvider == "Ollama";

            var model = isOllama
                ? (form.OllamaModelCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? ""
                : (form.CloudModelCombo   .SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

            var serverUrl = form.ServerUrlBox.Text.Trim();
            if (string.IsNullOrEmpty(serverUrl)) serverUrl = "http://localhost:11434";

            settings.Participants.Add(new ParticipantConfig
            {
                Name             = form.NameBox.Text.Trim(),
                Type             = form.CurrentProvider,
                Model            = model,
                ServerUrl        = serverUrl,
                Enabled          = form.EnabledCheck.IsChecked == true,
                IsCoordinator    = form.CoordinatorCheck.IsChecked == true,
                IsReasoner       = form.ReasonerCheck.IsChecked   == true,
                ReasonerPriority = (int)form.PrioritySlider.Value
            });

            // Persist Cloud AI API key to Windows Credential Manager
            if (!isOllama && !string.IsNullOrWhiteSpace(form.CurrentApiKey))
                WindowsCredentialManager.Save(form.CurrentProvider, form.CurrentApiKey);
        }

        // Keep legacy fields in sync for any code that still reads them
        var firstOllama = settings.Participants.FirstOrDefault(p => p.Type == "Ollama" && p.Enabled);
        if (firstOllama is not null)
        {
            settings.OllamaBaseUrl = firstOllama.ServerUrl;
            settings.OllamaModel   = firstOllama.Model;
        }
        var firstCloud = settings.Participants.FirstOrDefault(p => p.Type != "Ollama" && p.Enabled);
        if (firstCloud is not null)
        {
            settings.SelectedProvider   = firstCloud.Type;
            settings.SelectedCloudModel = firstCloud.Model;
        }

        SettingsService.Save(settings);
        SaveStatusLabel.Text = "Saved ✓";
        DialogResult = true;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static void SelectComboByContent(ComboBox combo, string value)
    {
        foreach (ComboBoxItem item in combo.Items)
            if (item.Content?.ToString() == value)
            {
                combo.SelectedItem = item;
                return;
            }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static void UpdateApiKeyHint(TextBlock hint, string provider)
    {
        hint.Text = provider switch
        {
            "Anthropic"      => "Get your key at console.anthropic.com",
            "Google AI"      => "Free tier at aistudio.google.com — no credit card required!",
            "Groq"           => "Free tier at console.groq.com — no credit card required!",
            "OpenRouter"     => "Get your key at openrouter.ai/keys",
            "Mistral"        => "Get your key at console.mistral.ai",
            "xAI Grok"       => "Get your key at console.x.ai — requires credit card",
            "OpenAI ChatGPT" => "Get your key at platform.openai.com — requires credit card",
            _                => ""
        };
    }

    private static string FormatToneLabel(int v) => v switch
    {
        < 10  => "Streng neutral",
        < 30  => "Neutral",
        < 45  => "Leicht neutral",
        <= 55 => "Modell-Standard",
        < 70  => "Leicht freundlich",
        < 90  => "Freundlich",
        _     => "Sehr freundlich"
    };

    private TextBlock MakeHintText(string text) => new TextBlock
    {
        Text         = text,
        FontSize     = 11,
        FontFamily   = new FontFamily("Segoe UI"),
        TextWrapping = TextWrapping.Wrap,
        Margin       = new Thickness(0, 0, 0, 4),
        Foreground   = (Brush)(TryFindResource("SubtextBrush") ?? Brushes.Gray)
    };

    private void PopulateCloudModelCombo(ComboBox combo, string provider, string selectedModel)
    {
        var models = GetDefaultModels(provider);
        combo.Items.Clear();
        foreach (var m in models)
        {
            var item = new ComboBoxItem { Content = m };
            combo.Items.Add(item);
            if (m == selectedModel) combo.SelectedItem = item;
        }
        if (combo.SelectedItem is null && combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private static string[] GetDefaultModels(string provider) => provider switch
    {
        "Anthropic"      => AnthropicService.DefaultModels,
        "Google AI"      => GoogleAIService.DefaultModels,
        "Groq"           => GroqService.DefaultModels,
        "OpenRouter"     => OpenRouterService.DefaultModels,
        "Mistral"        => MistralService.DefaultModels,
        "xAI Grok"       => XAIGrokService.DefaultModels,
        "OpenAI ChatGPT" => OpenAIService.DefaultModels,
        _                => []
    };

    private static ICloudAIService BuildCloudAIService(string provider, string apiKey) =>
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
}
