using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using ClaudetRelay.Properties;
using ClaudetRelay.Services;

namespace ClaudetRelay;

public partial class SettingsWindow : Window
{
    // ── Nested types ───────────────────────────────────────────────────────

    private sealed class ParticipantForm
    {
        public required int         SlotIndex           { get; init; }
        public required TabItem     Tab                 { get; init; }
        public required CheckBox    EnabledCheck        { get; init; }
        public required TextBox     NameBox             { get; init; }
        public required ComboBox    TypeCombo           { get; init; }
        // Ollama (local) section
        public required Border      OllamaSection       { get; init; }
        public required TextBox     ServerUrlBox        { get; init; }
        public required ComboBox    OllamaModelCombo    { get; init; }
        public required TextBlock   OllamaTestLabel     { get; init; }
        // Cloud AI section
        public required Border      CloudAISection      { get; init; }
        public required ComboBox    CloudModelCombo     { get; init; }
        public required TextBox     CloudModelFilter    { get; init; }  // live search box
        public required TextBlock   CloudTestLabel      { get; init; }
        // Full unfiltered model list — updated by Test button, read by the filter
        public List<string> AllCloudModels { get; set; } = [];
        // Rate-limit row (Cloud AI only)
        public required CheckBox  RpmEnabledCheck  { get; init; }
        public required TextBox   RpmValueBox      { get; init; }
        public required TextBlock RpmHintLabel     { get; init; }
        // Nickname duplicate warning
        public required TextBlock NicknameWarning  { get; init; }

        /// <summary>Returns the canonical provider name (stored in Tag), ignoring any display-only decorations in Content.</summary>
        public string CurrentProvider =>
            (TypeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()
            ?? (TypeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()
            ?? "Ollama";
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
    private TextBox      _userNameBox          = null!;
    private Slider       _toneSlider           = null!;
    private RadioButton  _modeNeutralBtn       = null!;
    private RadioButton  _modeMockingbirdBtn   = null!;
    private RadioButton  _modeBuccaneerBtn     = null!;
    private TextBox      _dialogueTurnsBox     = null!;
    private Slider       _responseLengthSlider = null!;
    private Slider       _chattinessSlider     = null!;
    private Slider       _zoomSlider           = null!;
    private ComboBox     _languageCombo        = null!;
    private CheckBox     _voiceInterruptChk    = null!;
    private TextBox      _voiceMaxBox          = null!;

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
                var dict = OxsuitLoader.Load(currentThemePath);
                if (dict is not null)
                    Resources.MergedDictionaries.Add(dict);
            }
            catch { /* no theme – window still usable */ }
        }

        InitializeComponent();
        SourceInitialized += (_, _) => ApplyTitleBarTheme();

        var settings = SettingsService.Load();

        // Apply UI zoom so the settings window itself respects the current scale setting
        UiZoomHelper.Apply(this, Math.Clamp(settings.UiZoom, 0.5, 3.0));

        if (providerModeOnly)
        {
            // Providers-only view: just the API key tab
            Title                  = $"{Loc.S("Providers_Title")} · ClaudetRelay";
            WindowTitleBlock.Text  = Loc.S("Providers_Title");
            BuildProvidersTab();
        }
        else
        {
            // General settings only — participants are now managed in the card-grid window
            Title                 = $"{Loc.S("Settings_Title")} · ClaudetRelay";
            WindowTitleBlock.Text = Loc.S("Settings_Title");
            SaveAllButton.Content = Loc.S("Btn_SaveAll");
            BuildGeneralTab(settings);
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
        tb.SetResourceReference(Control.ForegroundProperty, "ContentTextBrush");
        tb.SetResourceReference(TextBox.CaretBrushProperty, "InputTextBrush");

        var outer = new Border { Height = 36, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        outer.SetResourceReference(Border.BackgroundProperty,  "InputBgBrush");
        outer.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush");
        outer.Child = tb;
        return (outer, tb);
    }

    /// <summary>Returns a 36 px rounded password input: (outer Border, inner PasswordBox).</summary>
    private (Border Outer, PasswordBox Input) MakePasswordInput()
    {
        var pb = new PasswordBox { Style = (Style)FindResource("SPasswordBox") };
        pb.FontSize   = 14;
        pb.FontFamily = new FontFamily("Segoe UI");
        pb.SetResourceReference(Control.ForegroundProperty, "ContentTextBrush");

        var outer = new Border { Height = 36, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        outer.SetResourceReference(Border.BackgroundProperty,  "InputBgBrush");
        outer.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush");
        outer.Child = pb;
        return (outer, pb);
    }

    // ── General tab ────────────────────────────────────────────────────────

    private void BuildGeneralTab(AppSettings settings)
    {
        var nameLabel = new TextBlock { Style = (Style)FindResource("SLabel"), Text = Loc.S("Settings_YourName") };

        var (userNameOuter, userNameInput) = MakeTextInput(
            string.IsNullOrWhiteSpace(settings.UserName) ? "You" : settings.UserName);
        _userNameBox          = userNameInput;
        userNameOuter.Margin  = new Thickness(0, 0, 0, 6);
        userNameInput.TextChanged += (_, _) => ValidateAllNicknames();

        var nameHint = new TextBlock
        {
            Text         = Loc.S("Settings_NameHint"),
            FontSize     = 11,
            FontFamily   = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 18)
        };
        nameHint.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        // ── RESPONSE TONE ──────────────────────────────────────────────────
        var settings2 = SettingsService.Load(); // fresh read for ToneLevel
        var toneLabel = new TextBlock
        {
            Style  = (Style)FindResource("SLabel"),
            Text   = Loc.S("Settings_ResponseTone"),
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
        toneValueLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");

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
        {
            int v = (int)e.NewValue;
            toneValueLabel.Text = _modeMockingbirdBtn?.IsChecked == true ? FormatToneLabelMockingbird(v)
                                : _modeBuccaneerBtn?.IsChecked   == true ? FormatToneLabelBuccaneer(v)
                                : FormatToneLabel(v);
        };

        var toneRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        toneRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toneRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toneRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var toneLeft = MakeHintText(Loc.S("Settings_ToneNeutral"));
        var toneRight = MakeHintText(Loc.S("Settings_ToneFriendly"));
        Grid.SetColumn(toneLeft,   0);
        Grid.SetColumn(toneSlider, 1);
        Grid.SetColumn(toneRight,  2);
        toneRow.Children.Add(toneLeft);
        toneRow.Children.Add(toneSlider);
        toneRow.Children.Add(toneRight);

        var toneHint = MakeHintText(Loc.S("Settings_ToneHint"));

        // ── PERSONALITY MODE (Neutral / Mockingbird / Buccaneer) ───────────
        var personalityLabel = new TextBlock
        {
            Style  = (Style)FindResource("SLabel"),
            Text   = Loc.S("Settings_PersonalityMode"),
            Margin = new Thickness(0, 14, 0, 6)
        };

        // Helper that applies the correct labels/hints when a mode becomes active
        void ApplyModeLabels(string mode)
        {
            switch (mode)
            {
                case "mockingbird":
                    toneLeft.Text       = "Comedy 🎭";
                    toneRight.Text      = "Loving 💕";
                    toneHint.Text       = "0 = pure comedy & poems  ·  50 = humoristic default  ·  100 = loving pet names & kisses";
                    toneValueLabel.Text = FormatToneLabelMockingbird((int)toneSlider.Value);
                    break;
                case "buccaneer":
                    toneLeft.Text       = "Cutthroat ⚔️";
                    toneRight.Text      = "Jolly Cap'n 🏴‍☠️";
                    toneHint.Text       = "0 = fierce corsair  ·  50 = seafarin' rogue  ·  100 = jolly, friendly cap'n";
                    toneValueLabel.Text = FormatToneLabelBuccaneer((int)toneSlider.Value);
                    break;
                default:
                    toneLeft.Text       = Loc.S("Settings_ToneNeutral");
                    toneRight.Text      = Loc.S("Settings_ToneFriendly");
                    toneHint.Text       = Loc.S("Settings_ToneHint");
                    toneValueLabel.Text = FormatToneLabel((int)toneSlider.Value);
                    break;
            }
        }

        RadioButton MakePersonalityBtn(string label, string toolTip)
        {
            var rb = new RadioButton
            {
                Content     = label,
                GroupName   = "PersonalityMode",
                ToolTip     = toolTip,
                Style       = (Style)FindResource("SPersonalityBtn"),
                Margin      = new Thickness(0, 0, 6, 0)
            };
            return rb;
        }

        var btnNeutral     = MakePersonalityBtn(Loc.S("Settings_ModeNeutral"),     Loc.S("Settings_ModeNeutralTip"));
        var btnMockingbird = MakePersonalityBtn(Loc.S("Settings_ModeMockingbird"), Loc.S("Settings_ModeMockingbirdTip"));
        var btnBuccaneer   = MakePersonalityBtn(Loc.S("Settings_ModeBuccaneer"),   Loc.S("Settings_ModeBuccaneerTip"));

        _modeNeutralBtn     = btnNeutral;
        _modeMockingbirdBtn = btnMockingbird;
        _modeBuccaneerBtn   = btnBuccaneer;

        // Set initial state
        if (settings.BuccaneerMode)       { btnBuccaneer.IsChecked   = true; ApplyModeLabels("buccaneer"); }
        else if (settings.MockingbirdMode) { btnMockingbird.IsChecked = true; ApplyModeLabels("mockingbird"); }
        else                               { btnNeutral.IsChecked     = true; }

        btnNeutral.Checked     += (_, _) => ApplyModeLabels("neutral");
        btnMockingbird.Checked += (_, _) => ApplyModeLabels("mockingbird");
        btnBuccaneer.Checked   += (_, _) => ApplyModeLabels("buccaneer");

        var personalityRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 0, 0, 4)
        };
        personalityRow.Children.Add(btnNeutral);
        personalityRow.Children.Add(btnMockingbird);
        personalityRow.Children.Add(btnBuccaneer);

        // ── AI DIALOGUE TURNS ──────────────────────────────────────────────
        var dialogueSep = new Rectangle { Style = (Style)FindResource("SSep") };

        var dialogueLabel = new TextBlock
        {
            Style  = (Style)FindResource("SLabel"),
            Text   = Loc.S("Settings_AiDialogueTurns"),
            Margin = new Thickness(0, 4, 0, 6)
        };

        var dialogueHint = MakeHintText(Loc.S("Settings_AiDialogueHint"));

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
        dialogueTurnsBox.SetResourceReference(TextBox.BackgroundProperty, "InputBgBrush");
        dialogueTurnsBox.SetResourceReference(TextBox.ForegroundProperty, "InputTextBrush");
        dialogueTurnsBox.SetResourceReference(TextBox.BorderBrushProperty, "InputBgBrush");
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
            Text   = Loc.S("Settings_ResponseLength"),
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
        responseLengthValueLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");

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
        var rlLeft  = MakeHintText(Loc.S("Settings_RL_Left"));
        var rlRight = MakeHintText(Loc.S("Settings_RL_Right"));
        Grid.SetColumn(rlLeft,                 0);
        Grid.SetColumn(responseLengthSlider,   1);
        Grid.SetColumn(rlRight,                2);
        responseLengthRow.Children.Add(rlLeft);
        responseLengthRow.Children.Add(responseLengthSlider);
        responseLengthRow.Children.Add(rlRight);

        var responseLengthHint = MakeHintText(Loc.S("Settings_ResponseLengthHint"));

        // ── Chattiness ──────────────────────────────────────────────────────
        var chattinessSep = new Rectangle { Height = 1, Margin = new Thickness(0, 16, 0, 12) };
        chattinessSep.SetResourceReference(Rectangle.FillProperty, "ControlBorderBrush");

        var chattinessLabel = new TextBlock
        {
            Style  = (Style)FindResource("SLabel"),
            Text   = Loc.S("Settings_Chattiness"),
            Margin = new Thickness(0, 4, 0, 6)
        };

        var chattinessCurrentValue = Math.Clamp(settings.GlobalChattiness, 0, 100);
        var chattinessValueLabel = new TextBlock
        {
            FontSize   = 12, FontFamily = new FontFamily("Segoe UI"),
            Text       = FormatChattinessLabel(chattinessCurrentValue),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin     = new Thickness(0, 0, 0, 4)
        };
        chattinessValueLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");

        var chattinessSlider = new Slider
        {
            Minimum             = 0,
            Maximum             = 100,
            Value               = chattinessCurrentValue,
            TickFrequency       = 10,
            IsSnapToTickEnabled = false,
            Margin              = new Thickness(0, 0, 0, 4)
        };
        _chattinessSlider = chattinessSlider;
        chattinessSlider.ValueChanged += (_, e) =>
            chattinessValueLabel.Text = FormatChattinessLabel((int)e.NewValue);

        var chattinessRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        chattinessRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        chattinessRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        chattinessRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var clLeft  = MakeHintText(Loc.S("Settings_Ch_Left"));
        var clRight = MakeHintText(Loc.S("Settings_Ch_Right"));
        Grid.SetColumn(clLeft,             0);
        Grid.SetColumn(chattinessSlider,   1);
        Grid.SetColumn(clRight,            2);
        chattinessRow.Children.Add(clLeft);
        chattinessRow.Children.Add(chattinessSlider);
        chattinessRow.Children.Add(clRight);

        var chattinessHint = MakeHintText(Loc.S("Settings_ChattinessHint"));

        // ── Language ────────────────────────────────────────────────────────
        var langSep = new Rectangle { Height = 1, Margin = new Thickness(0, 16, 0, 12) };
        langSep.SetResourceReference(Rectangle.FillProperty, "ControlBorderBrush");

        var langLabel = new TextBlock
        {
            Style  = (Style)FindResource("SLabel"),
            Text   = Loc.S("Settings_Language"),
            Margin = new Thickness(0, 0, 0, 6)
        };

        var langCombo = new ComboBox
        {
            FontSize   = 13,
            FontFamily = new FontFamily("Segoe UI"),
            Margin     = new Thickness(0, 0, 0, 4),
            Width      = 180,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        langCombo.Items.Add(new ComboBoxItem { Content = "English", Tag = "" });
        langCombo.Items.Add(new ComboBoxItem { Content = "Deutsch", Tag = "de" });

        // Select current language
        var currentLang = settings.Language ?? "";
        var selectedIndex = 0;
        for (int li = 0; li < langCombo.Items.Count; li++)
        {
            if ((string)((ComboBoxItem)langCombo.Items[li]).Tag == currentLang)
            { selectedIndex = li; break; }
        }
        langCombo.SelectedIndex = selectedIndex;
        _languageCombo = langCombo;

        var langHint = MakeHintText(Loc.S("Settings_LanguageHint"));

        // ── UI Zoom ─────────────────────────────────────────────────────────
        var zoomSep = new Rectangle { Height = 1, Margin = new Thickness(0, 16, 0, 12) };
        zoomSep.SetResourceReference(Rectangle.FillProperty, "ControlBorderBrush");

        var zoomCurrentValue = Math.Clamp(settings.UiZoom, 0.5, 3.0);
        var zoomValueLabel = new TextBlock
        {
            FontSize   = 12, FontFamily = new FontFamily("Segoe UI"),
            Text       = UiZoomHelper.FormatLabel(zoomCurrentValue),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin     = new Thickness(0, 0, 0, 4)
        };
        zoomValueLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");

        var zoomLabel = new TextBlock { Style = (Style)FindResource("SLabel"), Text = Loc.S("Settings_UiZoom") };

        var zoomSlider = new Slider
        {
            Minimum             = 50,
            Maximum             = 300,
            Value               = zoomCurrentValue * 100,
            TickFrequency       = 25,
            IsSnapToTickEnabled = false,
            Margin              = new Thickness(0, 0, 0, 4)
        };
        _zoomSlider = zoomSlider;
        zoomSlider.ValueChanged += (_, e) =>
            zoomValueLabel.Text = UiZoomHelper.FormatLabel(e.NewValue / 100.0);

        var zoomRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        zoomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        zoomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        zoomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var zLeft  = MakeHintText("50%");
        var zRight = MakeHintText("300%");
        Grid.SetColumn(zLeft,       0);
        Grid.SetColumn(zoomSlider,  1);
        Grid.SetColumn(zRight,      2);
        zoomRow.Children.Add(zLeft);
        zoomRow.Children.Add(zoomSlider);
        zoomRow.Children.Add(zRight);

        var zoomHint = MakeHintText(Loc.S("Settings_UiZoomHint"));

        var root = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        root.Children.Add(nameLabel);
        root.Children.Add(userNameOuter);
        root.Children.Add(nameHint);
        root.Children.Add(toneLabel);
        root.Children.Add(toneValueLabel);
        root.Children.Add(toneRow);
        root.Children.Add(toneHint);
        root.Children.Add(personalityLabel);
        root.Children.Add(personalityRow);
        root.Children.Add(dialogueSep);
        root.Children.Add(dialogueLabel);
        root.Children.Add(dialogueHint);
        root.Children.Add(dialogueSliderRow);
        root.Children.Add(responseLengthSep);
        root.Children.Add(responseLengthLabel);
        root.Children.Add(responseLengthValueLabel);
        root.Children.Add(responseLengthRow);
        root.Children.Add(responseLengthHint);
        root.Children.Add(chattinessSep);
        root.Children.Add(chattinessLabel);
        root.Children.Add(chattinessValueLabel);
        root.Children.Add(chattinessRow);
        root.Children.Add(chattinessHint);
        root.Children.Add(langSep);
        root.Children.Add(langLabel);
        root.Children.Add(langCombo);
        root.Children.Add(langHint);
        root.Children.Add(zoomSep);
        root.Children.Add(zoomLabel);
        root.Children.Add(zoomValueLabel);
        root.Children.Add(zoomRow);
        root.Children.Add(zoomHint);

        // ── Voice output settings ───────────────────────────────────────────
        var voiceSep = new Rectangle { Height = 1, Margin = new Thickness(0, 16, 0, 12) };
        voiceSep.SetResourceReference(Rectangle.FillProperty, "ControlBorderBrush");
        root.Children.Add(voiceSep);

        var voiceLabel = new TextBlock
        {
            Text = "🔊  VOICE OUTPUT", FontSize = 11, FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 0, 0, 10)
        };
        voiceLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        root.Children.Add(voiceLabel);

        // Interrupt checkbox
        var voiceInterruptChk = new CheckBox
        {
            Content   = "Stop current speech when a new message arrives",
            IsChecked = settings.VoiceInterruptOnNewMessage,
            FontSize  = 13, FontFamily = new FontFamily("Segoe UI"),
            Margin    = new Thickness(0, 0, 0, 4)
        };
        voiceInterruptChk.SetResourceReference(CheckBox.ForegroundProperty, "ContentTextBrush");
        root.Children.Add(voiceInterruptChk);

        var voiceInterruptHint = MakeHintText(
            "On: each new bubble immediately interrupts the previous one.\n" +
            "Off: messages queue up — perfect for story mode where you want " +
            "every response read aloud in sequence.");
        root.Children.Add(voiceInterruptHint);

        // Max chars row
        var voiceMaxRow = new StackPanel
            { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 4) };
        var voiceMaxLabel = new TextBlock
        {
            Text = "Max characters per message: ", FontSize = 13,
            FontFamily = new FontFamily("Segoe UI"), VerticalAlignment = VerticalAlignment.Center
        };
        voiceMaxLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        voiceMaxRow.Children.Add(voiceMaxLabel);

        var voiceMaxBox = new TextBox
        {
            Text  = settings.VoiceSpeechMaxChars.ToString(),
            Width = 64, FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
            TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(6, 2, 6, 2)
        };
        if (TryFindResource("ModernTextBox") is Style vmbs) voiceMaxBox.Style = vmbs;
        voiceMaxBox.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsAsciiDigit);
        voiceMaxRow.Children.Add(voiceMaxBox);

        var voiceMaxSuffix = new TextBlock
        {
            Text = " chars", FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center
        };
        voiceMaxSuffix.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        voiceMaxRow.Children.Add(voiceMaxSuffix);
        root.Children.Add(voiceMaxRow);

        var voiceMaxHint = MakeHintText(
            "Messages longer than this are truncated before being sent to the TTS engine. " +
            "Range 100–5000. Default: 700.");
        root.Children.Add(voiceMaxHint);

        // Wire save for these two controls (appended to the existing save path)
        _voiceInterruptChk = voiceInterruptChk;
        _voiceMaxBox       = voiceMaxBox;

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root
        };
        var tab = new TabItem { Header = Loc.S("Settings_Title"), Content = scroll };
        ParticipantsTabControl.Items.Add(tab);
    }

    // ── Provider tabs (one per provider, alphabetically sorted) ──────────

    private void BuildProvidersTab()
    {
        // Alphabetical order — easy to scan, easy to extend
        var providers = new[]
        {
            "Anthropic", "Google AI", "Groq", "Mistral",
            "Ollama ☁", "OpenAI ChatGPT", "OpenRouter", "xAI Grok"
        };

        foreach (var provider in providers)
            BuildSingleProviderTab(provider);
    }

    private void BuildSingleProviderTab(string provider)
    {
        // ── Key input (password + plain-text toggle) ──────────────────
        var (keyOuter, keyBox)         = MakePasswordInput();
        var (keyTextOuter, keyTextBox) = MakeTextInput();
        keyTextOuter.Visibility = Visibility.Collapsed;

        var existingKey   = WindowsCredentialManager.Load(provider) ?? "";
        keyBox.Password   = existingKey;
        keyTextBox.Text   = existingKey;

        var showHideBtn = new Button
        {
            Content = Loc.S("Providers_Show"),
            Height  = 36,
            Padding = new Thickness(10, 0, 10, 0),
            Margin  = new Thickness(6, 0, 0, 0),
            Style   = (Style)FindResource("SButtonSecondary"),
            ToolTip = Loc.S("Providers_ShowHideTip")
        };

        // Row: key input + show/hide button
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
            Margin       = new Thickness(0, 0, 0, 10)
        };
        hintText.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        UpdateApiKeyHint(hintText, provider);

        // Test button + status label
        var testBtn = new Button
        {
            Content = Loc.S("Providers_TestConnection"),
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
        testLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var testRow = new StackPanel { Orientation = Orientation.Horizontal };
        testRow.Children.Add(testBtn);
        testRow.Children.Add(testLabel);

        var keyLabel = new TextBlock
        {
            Style  = (Style)FindResource("SLabel"),
            Text   = Loc.S("Providers_ApiKey"),
            Margin = new Thickness(0, 4, 0, 6)
        };

        var root = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        root.Children.Add(keyLabel);
        root.Children.Add(keyGrid);
        root.Children.Add(hintText);
        root.Children.Add(testRow);

        // ── Store form record ─────────────────────────────────────────
        _providerKeyForms.Add(new ProviderKeyForm(provider, keyBox, keyTextBox, keyOuter, testLabel));

        // ── Events ────────────────────────────────────────────────────
        showHideBtn.Click += (_, _) =>
        {
            if (keyOuter.Visibility == Visibility.Visible)   // dots → show plain text
            {
                keyTextBox.Text         = keyBox.Password;
                keyOuter.Visibility     = Visibility.Collapsed;
                keyTextOuter.Visibility = Visibility.Visible;
                showHideBtn.Content     = Loc.S("Providers_Hide");
                keyTextBox.Focus();
                keyTextBox.CaretIndex   = keyTextBox.Text.Length;
            }
            else                                             // plain text → back to dots
            {
                keyBox.Password         = keyTextBox.Text;
                keyTextOuter.Visibility = Visibility.Collapsed;
                keyOuter.Visibility     = Visibility.Visible;
                showHideBtn.Content     = Loc.S("Providers_Show");
            }
        };

        keyBox.PasswordChanged += (_, _) => keyTextBox.Text  = keyBox.Password;
        keyTextBox.TextChanged  += (_, _) => keyBox.Password = keyTextBox.Text;

        var capturedProvider = provider;
        testBtn.Click += async (_, _) =>
        {
            testLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
            testLabel.Text = Loc.S("Providers_Testing");
            try
            {
                var key = GetApiKeyForProvider(capturedProvider);
                if (string.IsNullOrWhiteSpace(key))
                {
                    testLabel.Text = Loc.S("Providers_EnterKeyFirst");
                    return;
                }

                using var svc = BuildCloudAIService(capturedProvider, key);
                var ok = await svc.IsAvailableAsync();
                testLabel.SetResourceReference(TextBlock.ForegroundProperty,
                    ok ? "SecondaryAccentBrush" : "AccentHighlightBrush");
                testLabel.Text = ok ? Loc.S("Providers_Connected") : Loc.S("Providers_Failed");
            }
            catch (Exception ex)
            {
                testLabel.SetResourceReference(TextBlock.ForegroundProperty, "AccentHighlightBrush");
                testLabel.Text = $"Error: {ex.Message}";
            }
        };

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root
        };

        // Short tab header — no need to repeat "ChatGPT" or "Grok" brand names in the tab bar
        var tabHeader = provider switch
        {
            "OpenAI ChatGPT" => "OpenAI",
            "xAI Grok"       => "xAI",
            _                => provider
        };
        ParticipantsTabControl.Items.Add(new TabItem { Header = tabHeader, Content = scroll });
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
        foreach (var t in new[] { "Ollama", "Ollama ☁", "Anthropic", "OpenAI ChatGPT", "Google AI", "Groq", "xAI Grok", "OpenRouter", "Mistral" })
        {
            // "Ollama" is local — no cloud symbol. "Ollama ☁" already has one. All others are cloud.
            var display = t == "Ollama" || t == "Ollama ☁" ? t : $"{t} ☁";
            typeCombo.Items.Add(new ComboBoxItem { Content = display, Tag = t });
        }
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
        ollamaHint.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

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
        ollamaTestLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

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

        // ── Model filter box ──────────────────────────────────────────────
        // Watermark overlay: a hint TextBlock in the same Grid cell as the TextBox.
        // IsHitTestVisible=false lets clicks fall through to the TextBox.
        var cloudModelFilterBox = new TextBox
        {
            Style        = (Style)FindResource("STextBox"),
            ToolTip      = "Type to filter the model list",
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        cloudModelFilterBox.SetResourceReference(TextBox.ForegroundProperty, "ContentTextBrush");
        cloudModelFilterBox.SetResourceReference(TextBox.CaretBrushProperty, "InputTextBrush");

        var cloudModelFilterHint = new TextBlock
        {
            Text                = "🔍  Filter models…",
            FontSize            = 13,
            FontFamily          = new FontFamily("Segoe UI"),
            IsHitTestVisible    = false,
            VerticalAlignment   = VerticalAlignment.Center,
            Margin              = new Thickness(10, 0, 0, 0),
        };
        cloudModelFilterHint.SetResourceReference(TextBlock.ForegroundProperty, "InputDimBrush");

        var filterGrid = new Grid();
        filterGrid.Children.Add(cloudModelFilterBox);
        filterGrid.Children.Add(cloudModelFilterHint);

        var cloudModelFilterOuter = new Border
        {
            Height        = 34,
            CornerRadius  = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Margin        = new Thickness(0, 0, 0, 6),
            Child         = filterGrid,
        };
        cloudModelFilterOuter.SetResourceReference(Border.BackgroundProperty,    "InputBgBrush");
        cloudModelFilterOuter.SetResourceReference(Border.BorderBrushProperty,   "InputBorderBrush");

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
        cloudTestLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var cloudTestRow = new StackPanel { Orientation = Orientation.Horizontal };
        cloudTestRow.Children.Add(cloudTestBtn);
        cloudTestRow.Children.Add(cloudTestLabel);

        var cloudContent = new StackPanel();
        cloudContent.Children.Add(cloudModelLabel);
        cloudContent.Children.Add(cloudModelFilterOuter);
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
        rpmValueTb.SetResourceReference(Control.ForegroundProperty,  "ContentTextBrush");
        rpmValueTb.SetResourceReference(TextBox.CaretBrushProperty,  "InputTextBrush");
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
        rpmValueOuter.SetResourceReference(Border.BackgroundProperty, "InputBgBrush");

        var rpmSuffix = new TextBlock
        {
            Text              = " requests / minute",
            FontSize          = 13,
            FontFamily        = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(8, 0, 0, 0)
        };
        rpmSuffix.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");

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
        rpmHintLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

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
            Visibility = !isOllama ? Visibility.Visible : Visibility.Collapsed,
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
            SlotIndex             = index,
            Tab                   = tab,
            EnabledCheck          = enabledCheck,
            NameBox               = nameBox,
            TypeCombo             = typeCombo,
            OllamaSection         = ollamaSection,
            ServerUrlBox          = serverUrlBox,
            OllamaModelCombo      = ollamaModelCombo,
            OllamaTestLabel       = ollamaTestLabel,
            CloudAISection        = cloudAISection,
            CloudModelCombo       = cloudModelCombo,
            CloudModelFilter      = cloudModelFilterBox,
            CloudTestLabel        = cloudTestLabel,
            RpmEnabledCheck       = rpmCheck,
            RpmValueBox           = rpmValueTb,
            RpmHintLabel          = rpmHintLabel,
            NicknameWarning       = nicknameWarning
        };
        _forms.Add(form);

        // Seed the master model list (filter reads from this; Test replaces it)
        form.AllCloudModels = GetDefaultModels(config.Type).ToList();

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
                // Reset master list to defaults for the new provider, clear filter, refresh combo
                form.AllCloudModels         = GetDefaultModels(provider).ToList();
                form.CloudModelFilter.Text  = "";
                ApplyCloudModelFilter(form);

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

        ollamaTestBtn.Click  += async (_, _) => await TestOllamaAsync(form);
        cloudTestBtn.Click   += async (_, _) => await TestCloudAIAsync(form);

        cloudModelFilterBox.TextChanged += (_, _) =>
        {
            // Toggle the watermark hint
            cloudModelFilterHint.Visibility =
                cloudModelFilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ApplyCloudModelFilter(form);
        };
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

    // ── Model filter helper ────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds <see cref="ParticipantForm.CloudModelCombo"/> from the master model list,
    /// applying the current text in <see cref="ParticipantForm.CloudModelFilter"/>.
    /// Preserves the previously selected model (adds it as a custom entry if it drops out
    /// of the filtered view when the filter is cleared).
    /// </summary>
    private static void ApplyCloudModelFilter(ParticipantForm form)
    {
        var text = form.CloudModelFilter.Text;
        var source = string.IsNullOrWhiteSpace(text)
            ? form.AllCloudModels
            : form.AllCloudModels
                   .Where(m => m.Contains(text, StringComparison.OrdinalIgnoreCase))
                   .ToList();

        var current = (form.CloudModelCombo.SelectedItem as ComboBoxItem)
                          ?.Content?.ToString() ?? "";
        form.CloudModelCombo.Items.Clear();

        foreach (var m in source)
        {
            var item = new ComboBoxItem { Content = m };
            form.CloudModelCombo.Items.Add(item);
            if (m == current) form.CloudModelCombo.SelectedItem = item;
        }

        if (form.CloudModelCombo.SelectedItem is null)
        {
            if (!string.IsNullOrEmpty(current) && string.IsNullOrWhiteSpace(text))
            {
                // Model not in the (full) list — keep it as a custom entry so the
                // user's saved choice is not silently discarded.
                var custom = new ComboBoxItem { Content = current };
                form.CloudModelCombo.Items.Insert(0, custom);
                form.CloudModelCombo.SelectedItem = custom;
            }
            else if (form.CloudModelCombo.Items.Count > 0 && string.IsNullOrWhiteSpace(text))
            {
                form.CloudModelCombo.SelectedIndex = 0;
            }
            // When actively filtering, leave combo unselected — the user is browsing
        }
    }

    // ── Section visibility helper ──────────────────────────────────────────

    private static void ApplySectionVisibility(ParticipantForm form)
    {
        bool isOllama = form.CurrentProvider == "Ollama";
        form.OllamaSection .Visibility = isOllama  ? Visibility.Visible : Visibility.Collapsed;
        form.CloudAISection.Visibility = !isOllama ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Test buttons ───────────────────────────────────────────────────────

    private async Task TestOllamaAsync(ParticipantForm form)
    {
        form.OllamaTestLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        form.OllamaTestLabel.Text = Loc.S("Providers_Testing");

        try
        {
            var url = form.ServerUrlBox.Text.Trim();
            if (string.IsNullOrEmpty(url)) url = "http://localhost:11434";

            using var svc = new OllamaService(url);
            var ok = await svc.IsAvailableAsync();

            form.OllamaTestLabel.SetResourceReference(TextBlock.ForegroundProperty,
                ok ? "SecondaryAccentBrush" : "AccentHighlightBrush");
            form.OllamaTestLabel.Text = ok ? Loc.S("Providers_Online") : Loc.S("Providers_Offline");

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
            form.OllamaTestLabel.SetResourceReference(TextBlock.ForegroundProperty, "AccentHighlightBrush");
            form.OllamaTestLabel.Text = $"Error: {ex.Message}";
        }
    }

    private async Task TestCloudAIAsync(ParticipantForm form)
    {
        form.CloudTestLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        form.CloudTestLabel.Text = Loc.S("Providers_Testing");

        try
        {
            var apiKey = GetApiKeyForProvider(form.CurrentProvider);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                form.CloudTestLabel.Text = Loc.S("Providers_EnterKeyInTab");
                return;
            }

            using var svc = BuildCloudAIService(form.CurrentProvider, apiKey);
            var ok = await svc.IsAvailableAsync();

            form.CloudTestLabel.SetResourceReference(TextBlock.ForegroundProperty,
                ok ? "SecondaryAccentBrush" : "AccentHighlightBrush");
            form.CloudTestLabel.Text = ok ? Loc.S("Providers_Connected") : Loc.S("Providers_Failed");

            if (ok)
            {
                try
                {
                    var models = await svc.GetModelsAsync();
                    if (models.Count > 0)
                    {
                        // Update master list then re-apply any active filter
                        form.AllCloudModels      = models;
                        ApplyCloudModelFilter(form);
                        form.CloudTestLabel.Text = $"{Loc.S("Providers_Connected")}  · {models.Count} models";
                    }
                }
                catch { /* keep default list */ }
            }
        }
        catch (Exception ex)
        {
            form.CloudTestLabel.SetResourceReference(TextBlock.ForegroundProperty, "AccentHighlightBrush");
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
            SaveStatusLabel.Text = Loc.S("Settings_SaveStatus");
            DialogResult = true;
            return;
        }

        // Participants mode: save general settings + participant slots
        var userName = _userNameBox.Text.Trim();
        settings.UserName = string.IsNullOrEmpty(userName) ? "You" : userName;

        settings.ToneLevel            = (int)_toneSlider.Value;
        settings.MockingbirdMode      = _modeMockingbirdBtn.IsChecked == true;
        settings.BuccaneerMode        = _modeBuccaneerBtn.IsChecked   == true;
        settings.Language             = (_languageCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        settings.AiDialogueMaxTurns   = int.TryParse(_dialogueTurnsBox.Text, out var dTurns)
                                        && dTurns is >= 3 and <= 100 ? dTurns : 10;
        settings.GlobalResponseLength = (int)_responseLengthSlider.Value;
        settings.GlobalChattiness     = (int)_chattinessSlider.Value;
        settings.UiZoom               = Math.Clamp(_zoomSlider.Value / 100.0, 0.5, 3.0);
        settings.VoiceInterruptOnNewMessage =
            _voiceInterruptChk?.IsChecked ?? settings.VoiceInterruptOnNewMessage;
        if (int.TryParse(_voiceMaxBox?.Text, out var vmChars) && vmChars is >= 100 and <= 5000)
            settings.VoiceSpeechMaxChars = vmChars;

        settings.Participants.Clear();

        foreach (var form in _forms)
        {
            var isOllama = form.CurrentProvider == "Ollama";

            var model = isOllama
                ? (form.OllamaModelCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? ""
                : (form.CloudModelCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

            var serverUrl = form.ServerUrlBox.Text.Trim();
            if (string.IsNullOrEmpty(serverUrl) && isOllama) serverUrl = "http://localhost:11434";

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
        var firstCloud = settings.Participants.FirstOrDefault(p => p.Type != "Ollama" && p.Type != "Ollama ☁" && p.Enabled);
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
        SaveStatusLabel.Text = Loc.S("Settings_SaveStatus");
        DialogResult = true;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static void SelectComboByContent(ComboBox combo, string value)
    {
        foreach (ComboBoxItem item in combo.Items)
            if ((item.Tag?.ToString() ?? item.Content?.ToString()) == value)
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
            "Ollama ☁"       => "Get your API key from your Ollama cloud provider account.",
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

    private static string FormatToneLabelBuccaneer(int v) => v switch
    {
        < 10  => "Fierce Buccaneer ⚔️",
        < 30  => "Salty Sea Dog",
        < 45  => "Weathered Corsair",
        <= 55 => "Seafarin' Rogue",
        < 70  => "Jolly Sailor",
        < 90  => "Friendly Cap'n",
        _     => "Jolly Cap'n 🏴‍☠️"
    };

    private static string FormatResponseLengthLabel(int v) => v switch
    {
        < 10  => Loc.S("Settings_RL_VeryBrief"),
        < 30  => Loc.S("Settings_RL_Short"),
        < 45  => Loc.S("Settings_RL_Concise"),
        <= 55 => Loc.S("Settings_RL_Default"),
        < 70  => Loc.S("Settings_RL_Moderate"),
        < 90  => Loc.S("Settings_RL_Thorough"),
        _     => Loc.S("Settings_RL_VeryDetailed")
    };

    internal static string FormatChattinessLabel(int v) => v switch
    {
        < 15  => Loc.S("Settings_Ch_Silent"),
        < 30  => Loc.S("Settings_Ch_Reserved"),
        < 45  => Loc.S("Settings_Ch_Focused"),
        <= 55 => Loc.S("Settings_Ch_Balanced"),
        < 70  => Loc.S("Settings_Ch_Conversational"),
        < 85  => Loc.S("Settings_Ch_Engaged"),
        _     => Loc.S("Settings_Ch_VeryChatty")
    };

    private TextBlock MakeHintText(string text) => new TextBlock
    {
        Text         = text,
        FontSize     = 11,
        FontFamily   = new FontFamily("Segoe UI"),
        TextWrapping = TextWrapping.Wrap,
        Margin       = new Thickness(0, 0, 0, 4),
        Foreground   = (Brush)(TryFindResource("ContentDimBrush") ?? Brushes.Gray)
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
        "Ollama ☁"       => OllamaOpenAIService.DefaultModels,
        _                => []
    };

    private static ICloudAIService BuildCloudAIService(string provider, string apiKey) =>
        provider switch
        {
            "Ollama ☁"       => new OllamaOpenAIService(apiKey),
            "Google AI"      => new GoogleAIService(apiKey),
            "Groq"           => new GroqService(apiKey),
            "OpenRouter"     => new OpenRouterService(apiKey),
            "Mistral"        => new MistralService(apiKey),
            "xAI Grok"       => new XAIGrokService(apiKey),
            "OpenAI ChatGPT" => new OpenAIService(apiKey),
            "LM Studio ☁"    => new LmStudioService(LmStudioService.DefaultCloudUrl, apiKey),
            _                => new AnthropicService(apiKey)
        };

    // ── DWM title-bar theming ──────────────────────────────────────────────

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int val, int sz);

    private void ApplyTitleBarTheme()
    {
        try
        {
            if (TryFindResource("SidebarBgBrush")   is not SolidColorBrush bg)   return;
            if (TryFindResource("SidebarTextBrush") is not SolidColorBrush text) return;
            var hwnd   = new WindowInteropHelper(this).Handle;
            var isDark = RelLum(bg.Color) < 0.5 ? 1 : 0;
            var cr     = bg.Color.R   | (bg.Color.G   << 8) | (bg.Color.B   << 16);
            var tcr    = text.Color.R | (text.Color.G << 8) | (text.Color.B << 16);
            DwmSetWindowAttribute(hwnd, 20, ref isDark, 4);   // dark mode flag  (Win 10+)
            DwmSetWindowAttribute(hwnd, 35, ref cr,    4);   // caption colour  (Win 11+)
            DwmSetWindowAttribute(hwnd, 36, ref tcr,   4);   // caption text    (Win 11+)
        }
        catch { }
    }

    private static double RelLum(Color c)
    {
        static double L(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return 0.2126 * L(c.R / 255.0) + 0.7152 * L(c.G / 255.0) + 0.0722 * L(c.B / 255.0);
    }
}
