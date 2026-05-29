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
        public required ComboBox    CloudModelCombo  { get; init; }
        public required TextBlock   CloudTestLabel   { get; init; }
        // Rate-limit row (Cloud AI only)
        public required CheckBox  RpmEnabledCheck  { get; init; }
        public required TextBox   RpmValueBox      { get; init; }
        public required TextBlock RpmHintLabel     { get; init; }
        // Nickname duplicate warning
        public required TextBlock NicknameWarning  { get; init; }

        public string CurrentProvider =>
            (TypeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Ollama";
    }

    /// <summary>Holds the key-input controls for one cloud provider on the Providers tab.</summary>
    private sealed record ProviderKeyForm(
        string      Provider,
        PasswordBox KeyBox,
        TextBox     KeyTextBox,
        Border      KeyOuter,      // PasswordBox wrapper — toggles with KeyTextBox
        TextBlock   TestLabel);

    // ── State ──────────────────────────────────────────────────────────────

    private readonly bool                   _providerModeOnly;
    private readonly List<ParticipantForm>  _forms            = [];
    private readonly List<ProviderKeyForm>  _providerKeyForms = [];
    private TextBox   _userNameBox          = null!;
    private Slider    _toneSlider           = null!;
    private CheckBox  _mockingbirdBox       = null!;
    private TextBox   _dialogueTurnsBox     = null!;
    private Slider    _responseLengthSlider = null!;

    // ── Constructor ────────────────────────────────────────────────────────

    /// <param name="currentThemePath">Path to the active theme XAML, or null.</param>
    /// <param name="initialTabIndex">Tab to show on open (ignored in provider mode).</param>
    /// <param name="providerModeOnly">
    /// When true: shows only the Providers tab for API key management.
    /// When false (default): shows General + P1–P20 participant tabs.
    /// </param>
    public SettingsWindow(string? currentThemePath, int initialTabIndex = 0, bool providerModeOnly = false)
    {
        _providerModeOnly = providerModeOnly;

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

        if (providerModeOnly)
        {
            // Providers-only view: just the API key tab
            Title                  = "Providers Setup · ClaudetRelay";
            WindowTitleBlock.Text  = "Providers Setup";
            BuildProvidersTab();
        }
        else
        {
            // Participants view: General settings + all participant slots
            BuildGeneralTab(settings);
            for (int i = 0; i < 20; i++)
            {
                var config = i < settings.Participants.Count
                    ? settings.Participants[i]
                    : new ParticipantConfig();
                BuildTab(i, config, settings);
            }

            if (initialTabIndex > 0 && initialTabIndex < ParticipantsTabControl.Items.Count)
                ParticipantsTabControl.SelectedIndex = initialTabIndex;

            // Auto-test all participants once the window is fully rendered
            Loaded += async (_, _) => await AutoTestAllAsync();
        }
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
        userNameInput.TextChanged += (_, _) => ValidateAllNicknames();

        var nameHint = new TextBlock
        {
            Text         = "Shown on your own chat bubbles",
            FontSize     = 11,
            FontFamily   = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 18)
        };
        nameHint.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");

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
            toneValueLabel.Text = _mockingbirdBox?.IsChecked == true
                ? FormatToneLabelMockingbird((int)e.NewValue)
                : FormatToneLabel((int)e.NewValue);

        var toneRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        toneRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toneRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toneRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var toneLeft = MakeHintText("Neutral");
        var toneRight = MakeHintText("Friendly");
        Grid.SetColumn(toneLeft,   0);
        Grid.SetColumn(toneSlider, 1);
        Grid.SetColumn(toneRight,  2);
        toneRow.Children.Add(toneLeft);
        toneRow.Children.Add(toneSlider);
        toneRow.Children.Add(toneRight);

        var toneHint = MakeHintText("0 = strictly neutral  ·  50 = model default (no change)  ·  100 = very friendly");

        // ── MOCKINGBIRD MODE ───────────────────────────────────────────────
        var mockingbirdCheck = new CheckBox
        {
            Style     = (Style)FindResource("SToggle"),
            IsChecked = settings.MockingbirdMode,
            Content   = "🐦 Mockingbird mode",
            Margin    = new Thickness(0, 10, 0, 4),
            ToolTip   = "Use at own risk."
        };
        _mockingbirdBox = mockingbirdCheck;

        // Apply initial labels if mockingbird was already enabled
        if (settings.MockingbirdMode)
        {
            toneLeft.Text       = "Comedy 🎭";
            toneRight.Text      = "Loving 💕";
            toneHint.Text       = "0 = pure comedy & poems  ·  50 = humoristic default  ·  100 = loving pet names & kisses";
            toneValueLabel.Text = FormatToneLabelMockingbird(settings.ToneLevel);
        }

        mockingbirdCheck.Checked += (_, _) =>
        {
            toneLeft.Text       = "Comedy 🎭";
            toneRight.Text      = "Loving 💕";
            toneHint.Text       = "0 = pure comedy & poems  ·  50 = humoristic default  ·  100 = loving pet names & kisses";
            toneValueLabel.Text = FormatToneLabelMockingbird((int)toneSlider.Value);
        };
        mockingbirdCheck.Unchecked += (_, _) =>
        {
            toneLeft.Text       = "Neutral";
            toneRight.Text      = "Friendly";
            toneHint.Text       = "0 = strictly neutral  ·  50 = model default (no change)  ·  100 = very friendly";
            toneValueLabel.Text = FormatToneLabel((int)toneSlider.Value);
        };

        // ── AI DIALOGUE TURNS ──────────────────────────────────────────────
        var dialogueSep = new Rectangle { Style = (Style)FindResource("SSep") };

        var dialogueLabel = new TextBlock
        {
            Style  = (Style)FindResource("SLabel"),
            Text   = "AI DIALOGUE TURNS",
            Margin = new Thickness(0, 4, 0, 6)
        };

        var dialogueHint = MakeHintText(
            "Maximum number of reply turns when 💬 multi-round dialogue is enabled " +
            "(3 – 100). Participants automatically stop when they have nothing new to add.");

        // Slider + TextBox row (same bidirectional pattern as font-size in Chat Appearance)
        var dialogueTurnsValue = Math.Clamp(settings.AiDialogueMaxTurns, 3, 100);
        var dialogueTurnsSlider = new Slider
        {
            Minimum             = 3,
            Maximum             = 100,
            Value               = dialogueTurnsValue,
            TickFrequency       = 5,
            IsSnapToTickEnabled = false,
            Margin              = new Thickness(0, 4, 0, 4)
        };

        var dialogueTurnsBox = new TextBox
        {
            Text              = dialogueTurnsValue.ToString(),
            Width             = 52,
            Height            = 32,
            FontSize          = 13,
            FontFamily        = new FontFamily("Segoe UI"),
            TextAlignment     = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(8, 0, 0, 0)
        };
        dialogueTurnsBox.SetResourceReference(TextBox.BackgroundProperty, "InputBrush");
        dialogueTurnsBox.SetResourceReference(TextBox.ForegroundProperty, "TextBrush");
        dialogueTurnsBox.SetResourceReference(TextBox.BorderBrushProperty, "InputBrush");
        _dialogueTurnsBox = dialogueTurnsBox;

        bool updatingTurns = false;
        dialogueTurnsSlider.ValueChanged += (_, e) =>
        {
            if (updatingTurns) return;
            updatingTurns = true;
            dialogueTurnsBox.Text = ((int)Math.Round(e.NewValue)).ToString();
            updatingTurns = false;
        };
        dialogueTurnsBox.TextChanged += (_, _) =>
        {
            if (updatingTurns) return;
            if (int.TryParse(dialogueTurnsBox.Text, out var v) && v >= 3 && v <= 100)
            {
                updatingTurns = true;
                dialogueTurnsSlider.Value = v;
                updatingTurns = false;
            }
        };

        // The slider should take all available width; TextBox is fixed-width on the right.
        var dialogueSliderRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        dialogueSliderRow.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dialogueSliderRow.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(dialogueTurnsSlider, 0);
        Grid.SetColumn(dialogueTurnsBox,    1);
        dialogueSliderRow.Children.Add(dialogueTurnsSlider);
        dialogueSliderRow.Children.Add(dialogueTurnsBox);

        // ── RESPONSE LENGTH ────────────────────────────────────────────────
        var responseLengthSep = new Rectangle { Style = (Style)FindResource("SSep") };

        var responseLengthLabel = new TextBlock
        {
            Style  = (Style)FindResource("SLabel"),
            Text   = "RESPONSE LENGTH",
            Margin = new Thickness(0, 4, 0, 6)
        };

        var responseLengthValue = Math.Clamp(settings.GlobalResponseLength, 0, 100);
        var responseLengthValueLabel = new TextBlock
        {
            FontSize   = 12,
            FontFamily = new FontFamily("Segoe UI"),
            Text       = FormatResponseLengthLabel(responseLengthValue),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin     = new Thickness(0, 0, 0, 4)
        };
        responseLengthValueLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var responseLengthSlider = new Slider
        {
            Minimum             = 0,
            Maximum             = 100,
            Value               = responseLengthValue,
            TickFrequency       = 10,
            IsSnapToTickEnabled = false,
            Margin              = new Thickness(0, 0, 0, 4)
        };
        _responseLengthSlider = responseLengthSlider;
        responseLengthSlider.ValueChanged += (_, e) =>
            responseLengthValueLabel.Text = FormatResponseLengthLabel((int)e.NewValue);

        var responseLengthRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        responseLengthRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        responseLengthRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        responseLengthRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var rlLeft  = MakeHintText("Brief");
        var rlRight = MakeHintText("Detailed");
        Grid.SetColumn(rlLeft,                 0);
        Grid.SetColumn(responseLengthSlider,   1);
        Grid.SetColumn(rlRight,                2);
        responseLengthRow.Children.Add(rlLeft);
        responseLengthRow.Children.Add(responseLengthSlider);
        responseLengthRow.Children.Add(rlRight);

        var responseLengthHint = MakeHintText(
            "50 = model default (no instruction injected)  ·  Only applies in general chat — project settings always take priority.");

        var root = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        root.Children.Add(nameLabel);
        root.Children.Add(userNameOuter);
        root.Children.Add(nameHint);
        root.Children.Add(toneLabel);
        root.Children.Add(toneValueLabel);
        root.Children.Add(toneRow);
        root.Children.Add(toneHint);
        root.Children.Add(mockingbirdCheck);
        root.Children.Add(dialogueSep);
        root.Children.Add(dialogueLabel);
        root.Children.Add(dialogueHint);
        root.Children.Add(dialogueSliderRow);
        root.Children.Add(responseLengthSep);
        root.Children.Add(responseLengthLabel);
        root.Children.Add(responseLengthValueLabel);
        root.Children.Add(responseLengthRow);
        root.Children.Add(responseLengthHint);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root
        };
        var tab = new TabItem { Header = "General", Content = scroll };
        ParticipantsTabControl.Items.Add(tab);
    }

    // ── Providers tab ─────────────────────────────────────────────────────

    private void BuildProvidersTab()
    {
        var root = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

        var introHint = new TextBlock
        {
            Text         = "Enter your API key for each provider once. All participants of the same provider share the same key.",
            FontSize     = 12,
            FontFamily   = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 16)
        };
        introHint.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");
        root.Children.Add(introHint);

        foreach (var provider in new[]
            { "Anthropic", "OpenAI ChatGPT", "Google AI", "Groq", "xAI Grok", "OpenRouter", "Mistral" })
        {
            // ── Provider heading ──────────────────────────────────────────
            var heading = new TextBlock
            {
                Style  = (Style)FindResource("SLabel"),
                Text   = provider.ToUpperInvariant(),
                Margin = new Thickness(0, 8, 0, 6)
            };

            // ── Key input (password + plain-text toggle) ──────────────────
            var (keyOuter, keyBox)         = MakePasswordInput();
            var (keyTextOuter, keyTextBox) = MakeTextInput();
            keyTextOuter.Visibility = Visibility.Collapsed;

            var existingKey   = WindowsCredentialManager.Load(provider) ?? "";
            keyBox.Password   = existingKey;
            keyTextBox.Text   = existingKey;

            // Show/Hide toggles between "Show" and "Hide" text — no fixed width needed
            var showHideBtn = new Button
            {
                Content = "Show",
                Height  = 36,
                Padding = new Thickness(10, 0, 10, 0),
                Margin  = new Thickness(6, 0, 0, 0),
                Style   = (Style)FindResource("SButtonSecondary"),
                ToolTip = "Show / hide key"
            };

            // Row 1: key input + show/hide button
            var keyGrid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            keyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            keyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(keyOuter,     0);
            Grid.SetColumn(keyTextOuter, 0);   // same cell — they toggle
            Grid.SetColumn(showHideBtn,  1);
            keyGrid.Children.Add(keyOuter);
            keyGrid.Children.Add(keyTextOuter);
            keyGrid.Children.Add(showHideBtn);

            var hintText = new TextBlock
            {
                FontSize     = 11,
                FontFamily   = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 6)
            };
            hintText.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");
            UpdateApiKeyHint(hintText, provider);

            // Row 2: test button + status label, side by side
            var testBtn = new Button
            {
                Content = "Test Connection",
                Style   = (Style)FindResource("SButtonSecondary"),
                Margin  = new Thickness(0, 0, 10, 0),
                Height  = 32
            };

            var testLabel = new TextBlock
            {
                FontSize          = 12,
                FontFamily        = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center
            };
            testLabel.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");

            var testRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 0, 0, 14)
            };
            testRow.Children.Add(testBtn);
            testRow.Children.Add(testLabel);

            root.Children.Add(heading);
            root.Children.Add(keyGrid);
            root.Children.Add(hintText);
            root.Children.Add(testRow);

            // ── Store form record ─────────────────────────────────────────
            _providerKeyForms.Add(new ProviderKeyForm(provider, keyBox, keyTextBox, keyOuter, testLabel));

            // ── Events ────────────────────────────────────────────────────
            showHideBtn.Click += (_, _) =>
            {
                if (keyOuter.Visibility == Visibility.Visible)   // currently dots → show plain text
                {
                    keyTextBox.Text         = keyBox.Password;
                    keyOuter.Visibility     = Visibility.Collapsed;
                    keyTextOuter.Visibility = Visibility.Visible;
                    showHideBtn.Content     = "Hide";
                    keyTextBox.Focus();
                    keyTextBox.CaretIndex   = keyTextBox.Text.Length;
                }
                else                                             // currently plain text → back to dots
                {
                    keyBox.Password         = keyTextBox.Text;
                    keyTextOuter.Visibility = Visibility.Collapsed;
                    keyOuter.Visibility     = Visibility.Visible;
                    showHideBtn.Content     = "Show";
                }
            };

            keyBox.PasswordChanged += (_, _) => keyTextBox.Text  = keyBox.Password;
            keyTextBox.TextChanged  += (_, _) => keyBox.Password = keyTextBox.Text;

            var capturedProvider = provider;
            testBtn.Click += async (_, _) =>
            {
                testLabel.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");
                testLabel.Text = "Testing…";
                try
                {
                    var key = GetApiKeyForProvider(capturedProvider);
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        testLabel.Text = "⚠  Enter a key first.";
                        return;
                    }
                    using var svc = BuildCloudAIService(capturedProvider, key);
                    var ok = await svc.IsAvailableAsync();
                    testLabel.SetResourceReference(TextBlock.ForegroundProperty,
                        ok ? "OllamaBrush" : "AccentBrush");
                    testLabel.Text = ok ? "Connected ✓" : "Failed ✗  (check your key)";
                }
                catch (Exception ex)
                {
                    testLabel.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                    testLabel.Text = $"Error: {ex.Message}";
                }
            };
        }

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root
        };
        ParticipantsTabControl.Items.Add(new TabItem { Header = "Providers", Content = scroll });
    }

    // ── API-key helper ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the current API key for <paramref name="provider"/> from the Providers tab
    /// (reflecting any unsaved edits), falling back to Credential Manager if the tab
    /// hasn't been built yet.
    /// </summary>
    private string GetApiKeyForProvider(string provider)
    {
        var pkf = _providerKeyForms.FirstOrDefault(f => f.Provider == provider);
        if (pkf is null) return WindowsCredentialManager.Load(provider) ?? "";
        return pkf.KeyOuter.Visibility == Visibility.Visible
            ? pkf.KeyBox.Password
            : pkf.KeyTextBox.Text;
    }

    // ── Auto-test on open ──────────────────────────────────────────────────

    private async Task AutoTestAllAsync()
    {
        var tasks = _forms.Select(async form =>
        {
            if (form.CurrentProvider == "Ollama")
                await TestOllamaAsync(form);
            else if (!string.IsNullOrEmpty(GetApiKeyForProvider(form.CurrentProvider)))
                await TestCloudAIAsync(form);
        });
        await Task.WhenAll(tasks);
    }

    // ── Tab builder ────────────────────────────────────────────────────────

    private void BuildTab(int index, ParticipantConfig config, AppSettings settings)
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
        var nameLabel = new TextBlock { Style = (Style)FindResource("SLabel"), Text = "NICKNAME" };
        var (nameBoxOuter, nameBox) = MakeTextInput(config.Name);

        var nicknameWarning = new TextBlock
        {
            Text         = "⚠ Nickname already used by another participant",
            FontSize     = 11,
            FontFamily   = new FontFamily("Segoe UI"),
            Foreground   = Brushes.OrangeRed,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 3, 0, 0),
            Visibility   = Visibility.Collapsed
        };

        var typeLabel = new TextBlock { Style = (Style)FindResource("SLabel"), Text = "TYPE" };
        var typeCombo = new ComboBox { Style = (Style)FindResource("SComboBox") };
        foreach (var t in new[] { "Ollama", "Anthropic", "OpenAI ChatGPT", "Google AI", "Groq", "xAI Grok", "OpenRouter", "Mistral" })
            typeCombo.Items.Add(new ComboBoxItem { Content = t });
        SelectComboByContent(typeCombo, config.Type);

        var nameCol = new StackPanel { Margin = new Thickness(0, 0, 8, 14) };
        nameCol.Children.Add(nameLabel);
        nameCol.Children.Add(nameBoxOuter);
        nameCol.Children.Add(nicknameWarning);

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
        // API keys are managed in the Providers tab — not per participant.

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
        cloudContent.Children.Add(cloudModelLabel);
        cloudContent.Children.Add(cloudModelCombo);
        cloudContent.Children.Add(cloudTestRow);

        // ── RATE LIMIT ROW ────────────────────────────────────────────────
        var rpmSep = new Rectangle { Style = (Style)FindResource("SSep") };

        var rpmCheck = new CheckBox
        {
            Style             = (Style)FindResource("SToggle"),
            Content           = "Limit to",
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 8, 0)
        };

        // Small numeric input (no wrapper helper — we need a custom width)
        var rpmValueTb = new TextBox { Style = (Style)FindResource("STextBox"), Text = "15" };
        rpmValueTb.FontSize   = 13;
        rpmValueTb.FontFamily = new FontFamily("Segoe UI");
        rpmValueTb.SetResourceReference(Control.ForegroundProperty,  "TextBrush");
        rpmValueTb.SetResourceReference(TextBox.CaretBrushProperty,  "TextBrush");
        // Accept only digits
        rpmValueTb.PreviewTextInput += (_, e) =>
        {
            e.Handled = !e.Text.All(char.IsDigit);
        };

        var rpmValueOuter = new Border
        {
            Width        = 64,
            Height       = 32,
            CornerRadius = new CornerRadius(8),
            Child        = rpmValueTb
        };
        rpmValueOuter.SetResourceReference(Border.BackgroundProperty, "InputBrush");

        var rpmSuffix = new TextBlock
        {
            Text              = " requests / minute",
            FontSize          = 13,
            FontFamily        = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(8, 0, 0, 0)
        };
        rpmSuffix.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var rpmRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 0, 0, 4)
        };
        rpmRow.Children.Add(rpmCheck);
        rpmRow.Children.Add(rpmValueOuter);
        rpmRow.Children.Add(rpmSuffix);

        // Hint label — content set per-provider below and on provider change
        var rpmHintLabel = new TextBlock
        {
            FontSize     = 11,
            FontFamily   = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 4)
        };
        rpmHintLabel.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");

        // Load initial state for current provider
        if (settings.ProviderThrottle.TryGetValue(config.Type, out var initThrottle))
        {
            rpmCheck.IsChecked = initThrottle.Enabled;
            rpmValueTb.Text    = initThrottle.Rpm.ToString();
        }
        UpdateRpmHint(rpmHintLabel, config.Type);

        cloudContent.Children.Add(rpmSep);
        cloudContent.Children.Add(rpmRow);
        cloudContent.Children.Add(rpmHintLabel);

        var cloudAISection = new Border
        {
            Visibility = isOllama ? Visibility.Collapsed : Visibility.Visible,
            Child      = cloudContent
        };

        // ── Assemble tab content ───────────────────────────────────────────
        var root = new StackPanel();
        root.Children.Add(enabledCheck);
        root.Children.Add(nameTypeGrid);
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
            CloudModelCombo  = cloudModelCombo,
            CloudTestLabel   = cloudTestLabel,
            RpmEnabledCheck  = rpmCheck,
            RpmValueBox      = rpmValueTb,
            RpmHintLabel     = rpmHintLabel,
            NicknameWarning  = nicknameWarning
        };
        _forms.Add(form);

        // ── Events ────────────────────────────────────────────────────────

        nameBox.TextChanged += (_, _) =>
        {
            var h = string.IsNullOrWhiteSpace(form.NameBox.Text)
                ? $"P{index + 1}"
                : form.NameBox.Text.Trim();
            form.Tab.Header = h;
            ValidateAllNicknames();
        };

        typeCombo.SelectionChanged += (_, _) =>
        {
            ApplySectionVisibility(form);
            var provider = form.CurrentProvider;
            if (provider != "Ollama")
            {
                PopulateCloudModelCombo(form.CloudModelCombo, provider, "");

                // Refresh RPM controls for the newly selected provider
                if (settings.ProviderThrottle.TryGetValue(provider, out var t))
                {
                    form.RpmEnabledCheck.IsChecked = t.Enabled;
                    form.RpmValueBox.Text          = t.Rpm.ToString();
                }
                else
                {
                    form.RpmEnabledCheck.IsChecked = false;
                    form.RpmValueBox.Text          = "15";
                }
                UpdateRpmHint(form.RpmHintLabel, provider);
            }
        };

        localhostBtn.Click += (_, _) => serverUrlBox.Text = "http://localhost:11434";

        ollamaTestBtn.Click += async (_, _) => await TestOllamaAsync(form);
        cloudTestBtn.Click  += async (_, _) => await TestCloudAIAsync(form);
    }

    // ── Nickname uniqueness validation ────────────────────────────────────

    /// <summary>
    /// Re-validates every participant tab's nickname against all other participants
    /// and the user's own name. Shows or hides the orange-red warning on each tab.
    /// Called from every name-box TextChanged event, including the user-name box.
    /// </summary>
    private void ValidateAllNicknames()
    {
        var userName = _userNameBox?.Text.Trim() ?? "";

        foreach (var f in _forms)
        {
            var name = f.NameBox.Text.Trim();
            bool duplicate = false;

            if (!string.IsNullOrWhiteSpace(name))
            {
                // Duplicate of the user's own name?
                if (!string.IsNullOrWhiteSpace(userName) &&
                    string.Equals(userName, name, StringComparison.OrdinalIgnoreCase))
                {
                    duplicate = true;
                }

                // Duplicate of another participant?
                if (!duplicate)
                {
                    foreach (var other in _forms)
                    {
                        if (other.SlotIndex == f.SlotIndex) continue;
                        var otherName = other.NameBox.Text.Trim();
                        if (!string.IsNullOrWhiteSpace(otherName) &&
                            string.Equals(otherName, name, StringComparison.OrdinalIgnoreCase))
                        {
                            duplicate = true;
                            break;
                        }
                    }
                }
            }

            f.NicknameWarning.Visibility = duplicate ? Visibility.Visible : Visibility.Collapsed;
        }
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
            var apiKey = GetApiKeyForProvider(form.CurrentProvider);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                form.CloudTestLabel.Text = "⚠  Enter an API key in the Providers tab first.";
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
                        if (form.CloudModelCombo.SelectedItem is null)
                        {
                            // Saved model is not in the live list — keep it as a custom entry
                            // so the user's choice is preserved (they can change it manually).
                            if (!string.IsNullOrEmpty(current))
                            {
                                var custom = new ComboBoxItem { Content = current };
                                form.CloudModelCombo.Items.Insert(0, custom);
                                form.CloudModelCombo.SelectedItem = custom;
                            }
                            else if (form.CloudModelCombo.Items.Count > 0)
                                form.CloudModelCombo.SelectedIndex = 0;
                        }
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

        if (_providerModeOnly)
        {
            // Providers-only mode: save API keys and close
            foreach (var pkf in _providerKeyForms)
            {
                var key = pkf.KeyOuter.Visibility == Visibility.Visible
                    ? pkf.KeyBox.Password
                    : pkf.KeyTextBox.Text;
                if (!string.IsNullOrWhiteSpace(key))
                    WindowsCredentialManager.Save(pkf.Provider, key);
            }
            SettingsService.Save(settings);
            SaveStatusLabel.Text = "Saved ✓";
            DialogResult = true;
            return;
        }

        // Participants mode: save general settings + participant slots
        var userName = _userNameBox.Text.Trim();
        settings.UserName = string.IsNullOrEmpty(userName) ? "You" : userName;

        settings.ToneLevel           = (int)_toneSlider.Value;
        settings.MockingbirdMode     = _mockingbirdBox.IsChecked == true;
        settings.AiDialogueMaxTurns  = int.TryParse(_dialogueTurnsBox.Text, out var dTurns)
                                       && dTurns is >= 3 and <= 100 ? dTurns : 10;
        settings.GlobalResponseLength = (int)_responseLengthSlider.Value;

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
                Name      = form.NameBox.Text.Trim(),
                Type      = form.CurrentProvider,
                Model     = model,
                ServerUrl = serverUrl,
                Enabled   = form.EnabledCheck.IsChecked == true
            });

            // API keys are saved from the Providers tab (see above)
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

        // Persist per-provider throttle settings.
        // Multiple participant slots can share the same provider type (e.g. two "Google AI" slots).
        // Merge with "any-enabled-wins": if any slot has the checkbox checked, the setting is
        // saved as enabled.  The RPM value is taken from the first enabled slot found.
        var providerThrottle = new Dictionary<string, ProviderThrottleSettings>();
        foreach (var form in _forms)
        {
            var provider = form.CurrentProvider;
            if (provider == "Ollama") continue;
            if (!int.TryParse(form.RpmValueBox.Text.Trim(), out var rpm) || rpm < 1) rpm = 15;
            var enabled = form.RpmEnabledCheck.IsChecked == true;
            if (!providerThrottle.TryGetValue(provider, out var existing))
            {
                providerThrottle[provider] = new ProviderThrottleSettings { Enabled = enabled, Rpm = rpm };
            }
            else
            {
                providerThrottle[provider] = new ProviderThrottleSettings
                {
                    Enabled = existing.Enabled || enabled,   // any checked = checked
                    Rpm     = enabled ? rpm : existing.Rpm   // prefer rpm from an enabled slot
                };
            }
        }
        foreach (var (provider, ts) in providerThrottle)
            settings.ProviderThrottle[provider] = ts;

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

    private static void UpdateRpmHint(TextBlock hint, string provider)
    {
        hint.Text = provider switch
        {
            "Google AI"      => "Free tier limits: 2–15 rpm (Pro/Ultra), 15–30 rpm (Flash) · aistudio.google.com",
            "Groq"           => "Free tier limits vary by model — check console.groq.com for your model",
            "Anthropic"      => "API rate limits depend on your usage tier — check console.anthropic.com",
            "OpenRouter"     => "Free models have per-model rate limits — check openrouter.ai for details",
            "Mistral"        => "Free tier: limited rpm — check console.mistral.ai for details",
            "xAI Grok"       => "Rate limits depend on your plan — check console.x.ai",
            "OpenAI ChatGPT" => "Rate limits depend on your usage tier — check platform.openai.com",
            _                => ""
        };
    }

    private static string FormatToneLabel(int v) => v switch
    {
        < 10  => "Strictly neutral",
        < 30  => "Neutral",
        < 45  => "Slightly neutral",
        <= 55 => "Model default",
        < 70  => "Slightly friendly",
        < 90  => "Friendly",
        _     => "Very friendly"
    };

    private static string FormatToneLabelMockingbird(int v) => v switch
    {
        < 10  => "Pure comedy 🎭",
        < 30  => "Witty & funny",
        < 45  => "Slightly comedic",
        <= 55 => "Mockingbird default",
        < 70  => "Affectionately funny",
        < 90  => "Lovingly teasing",
        _     => "Affectionate insults 💕"
    };

    private static string FormatResponseLengthLabel(int v) => v switch
    {
        < 10  => "Very brief",
        < 30  => "Short",
        < 45  => "Concise",
        <= 55 => "Model default",
        < 70  => "Moderate",
        < 90  => "Thorough",
        _     => "Very detailed"
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

        if (combo.SelectedItem is null)
        {
            // Saved model is not in the static default list (e.g. a live-fetched model).
            // Add it as a custom entry at the top so the saved value is preserved.
            if (!string.IsNullOrEmpty(selectedModel))
            {
                var custom = new ComboBoxItem { Content = selectedModel };
                combo.Items.Insert(0, custom);
                combo.SelectedItem = custom;
            }
            else if (combo.Items.Count > 0)
                combo.SelectedIndex = 0;
        }
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
