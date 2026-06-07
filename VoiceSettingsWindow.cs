using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudetRelay.Services;

namespace ClaudetRelay;

/// <summary>
/// TTS backend selector with per-backend configuration panels.
/// Switching a radio button applies the backend live; settings are persisted on close.
/// </summary>
public sealed class VoiceSettingsWindow : Window
{
    private readonly string? _themePath;

    private RadioButton? _rbWindows, _rbSherpa, _rbVoicevox;
    private StackPanel?  _sherpaPanel, _voicevoxPanel;
    private TextBox?     _folderBox, _portBox, _maxCharsBox;
    private CheckBox?    _interruptChk;

    private static bool IsDE =>
        string.Equals(System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
                      "de", StringComparison.OrdinalIgnoreCase);

    public VoiceSettingsWindow(string? themePath)
    {
        _themePath = themePath;

        Title                 = Properties.Loc.S("Audio_VoiceSettings");
        Width                 = 520;
        SizeToContent         = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode            = ResizeMode.NoResize;
        ShowInTaskbar         = false;
        SetResourceReference(BackgroundProperty, "ContentBgBrush");

        if (themePath is not null)
        {
            try
            {
                var dict = OxsuitLoader.Load(themePath);
                if (dict is not null) Resources.MergedDictionaries.Add(dict);
            }
            catch { }
        }
        SourceInitialized += (_, _) => ParticipantsWindow.TryApplyTitleBarTo(this);

        BuildUI();
        UiZoomHelper.Apply(this, UiZoomHelper.FromSettings());
    }

    private void BuildUI()
    {
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };
        Content  = root;

        var s = SettingsService.Load();

        root.Children.Add(SectionHeading("🎤  " + Properties.Loc.S("Audio_VoiceBackendTitle")));

        // ── Windows TTS ────────────────────────────────────────────────────
        _rbWindows = MakeRadio(Properties.Loc.S("Audio_BackendWindows"));
        _rbWindows.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(_rbWindows);
        root.Children.Add(HintText(IsDE
            ? "Integrierte Windows-Stimmen — offline verfügbar, keine Einrichtung erforderlich."
            : "Built-in Windows voices — works offline, no setup required."));

        // ── Sherpa-onnx ────────────────────────────────────────────────────
        _rbSherpa = MakeRadio(Properties.Loc.S("Audio_BackendSherpa"));
        _rbSherpa.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(_rbSherpa);
        root.Children.Add(HintText(IsDE
            ? "Hochwertige neuronale Offline-Stimmen. Lade Modelle über den Manager herunter."
            : "High-quality offline neural voices. Download models with the Manager below."));

        _sherpaPanel = new StackPanel
        {
            Margin     = new Thickness(20, 6, 0, 4),
            Visibility = Visibility.Collapsed,
        };
        root.Children.Add(_sherpaPanel);

        // Sherpa folder row
        var folderRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _sherpaPanel.Children.Add(folderRow);

        _folderBox = new TextBox
        {
            Text = string.IsNullOrEmpty(s.SherpaModelFolder)
                ? Path.Combine(AppContext.BaseDirectory, "Voices")
                : s.SherpaModelFolder,
            FontFamily      = new FontFamily("Segoe UI"),
            FontSize        = 12,
            Padding         = new Thickness(8, 6, 8, 6),
            BorderThickness = new Thickness(1),
        };
        _folderBox.SetResourceReference(ForegroundProperty, "InputTextBrush");
        _folderBox.SetResourceReference(BackgroundProperty, "ControlBgBrush");
        _folderBox.SetResourceReference(BorderBrushProperty, "ControlBorderBrush");
        Grid.SetColumn(_folderBox, 0);
        folderRow.Children.Add(_folderBox);

        var browseBtn = MakeBtn("  📁  ");
        browseBtn.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(browseBtn, 1);
        browseBtn.Click += (_, _) =>
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description         = Properties.Loc.S("Audio_ModelFolder"),
                SelectedPath        = _folderBox.Text,
                ShowNewFolderButton = true,
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                _folderBox.Text = dlg.SelectedPath;
        };
        folderRow.Children.Add(browseBtn);

        // Manage models button
        var manageBtn = MakeBtn("🎙  " + Properties.Loc.S("Audio_ManageModels"));
        manageBtn.HorizontalAlignment = HorizontalAlignment.Left;
        manageBtn.Click += (_, _) =>
        {
            SaveSettings();
            var mgr = new VoiceModelManagerWindow(_themePath) { Owner = this };
            mgr.ShowDialog();
            // Sync folder box if the manager created / changed the folder
            var s2 = SettingsService.Load();
            if (_folderBox is not null && !string.IsNullOrEmpty(s2.SherpaModelFolder))
                _folderBox.Text = s2.SherpaModelFolder;
        };
        _sherpaPanel.Children.Add(manageBtn);

        // ── VOICEVOX ───────────────────────────────────────────────────────
        _rbVoicevox = MakeRadio(Properties.Loc.S("Audio_BackendVoicevox"));
        _rbVoicevox.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(_rbVoicevox);
        root.Children.Add(HintText(IsDE
            ? "Anime-inspirierte Stimmen über eine lokal laufende VOICEVOX-Installation."
            : "Anime-inspired voices via a locally running VOICEVOX installation."));

        _voicevoxPanel = new StackPanel
        {
            Margin     = new Thickness(20, 6, 0, 4),
            Visibility = Visibility.Collapsed,
            Orientation = Orientation.Horizontal,
        };
        root.Children.Add(_voicevoxPanel);

        var portLbl = new TextBlock
        {
            Text              = Properties.Loc.S("Audio_VoicevoxPort") + "  ",
            FontFamily        = new FontFamily("Segoe UI"),
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        portLbl.SetResourceReference(ForegroundProperty, "ContentTextBrush");
        _voicevoxPanel.Children.Add(portLbl);

        _portBox = new TextBox
        {
            Text            = s.VoicevoxPort.ToString(),
            Width           = 70,
            FontFamily      = new FontFamily("Segoe UI"),
            FontSize        = 12,
            Padding         = new Thickness(6, 4, 6, 4),
            BorderThickness = new Thickness(1),
        };
        _portBox.SetResourceReference(ForegroundProperty, "InputTextBrush");
        _portBox.SetResourceReference(BackgroundProperty, "ControlBgBrush");
        _portBox.SetResourceReference(BorderBrushProperty, "ControlBorderBrush");
        _voicevoxPanel.Children.Add(_portBox);

        // ── Initial state ──────────────────────────────────────────────────
        var backend = s.VoiceBackend ?? "Windows";
        if      (backend.Equals("Sherpa",   StringComparison.OrdinalIgnoreCase)) _rbSherpa.IsChecked   = true;
        else if (backend.Equals("Voicevox", StringComparison.OrdinalIgnoreCase)) _rbVoicevox.IsChecked = true;
        else                                                                       _rbWindows.IsChecked  = true;

        UpdatePanels();

        _rbWindows .Checked += (_, _) => { UpdatePanels(); ApplyBackend(); };
        _rbSherpa  .Checked += (_, _) => { UpdatePanels(); ApplyBackend(); };
        _rbVoicevox.Checked += (_, _) => { UpdatePanels(); ApplyBackend(); };

        // ── Playback behaviour ─────────────────────────────────────────────
        root.Children.Add(new Separator { Margin = new Thickness(0, 20, 0, 16) });
        root.Children.Add(SectionHeading("⚙  " + (IsDE ? "Wiedergabe-Verhalten" : "Playback Behaviour")));

        var interruptChk = new CheckBox
        {
            Content    = IsDE
                ? "Aktuelle Wiedergabe bei neuer Nachricht unterbrechen"
                : "Stop current speech when a new message arrives",
            IsChecked  = s.VoiceInterruptOnNewMessage,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 12,
            Margin     = new Thickness(0, 0, 0, 2),
        };
        interruptChk.SetResourceReference(ForegroundProperty, "ContentTextBrush");
        root.Children.Add(interruptChk);
        root.Children.Add(HintText(IsDE
            ? "Ein: jede neue Nachricht unterbricht die laufende Ausgabe.\nAus: Nachrichten werden in die Warteschlange eingereiht — ideal für Story-Sitzungen."
            : "On: each new bubble immediately interrupts the previous one.\nOff: messages queue up — perfect for story mode where you want every response read aloud in sequence."));

        var maxRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 12, 0, 2),
        };
        root.Children.Add(maxRow);

        var maxLabel = new TextBlock
        {
            Text              = (IsDE ? "Max. Zeichen pro Nachricht: " : "Max characters per message: "),
            FontFamily        = new FontFamily("Segoe UI"),
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        maxLabel.SetResourceReference(ForegroundProperty, "ContentTextBrush");
        maxRow.Children.Add(maxLabel);

        var maxBox = new TextBox
        {
            Text            = s.VoiceSpeechMaxChars.ToString(),
            Width           = 60,
            FontFamily      = new FontFamily("Segoe UI"),
            FontSize        = 12,
            TextAlignment   = TextAlignment.Center,
            Padding         = new Thickness(6, 3, 6, 3),
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        maxBox.SetResourceReference(ForegroundProperty, "InputTextBrush");
        maxBox.SetResourceReference(BackgroundProperty, "ControlBgBrush");
        maxBox.SetResourceReference(BorderBrushProperty, "ControlBorderBrush");
        maxBox.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsAsciiDigit);
        maxRow.Children.Add(maxBox);

        var maxSuffix = new TextBlock
        {
            Text              = " " + (IsDE ? "Zeichen" : "chars"),
            FontFamily        = new FontFamily("Segoe UI"),
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(4, 0, 0, 0),
        };
        maxSuffix.SetResourceReference(ForegroundProperty, "ContentDimBrush");
        maxRow.Children.Add(maxSuffix);

        root.Children.Add(HintText(IsDE
            ? "Längere Nachrichten werden vor der TTS-Ausgabe gekürzt. Bereich: 100–5000. Standard: 700."
            : "Messages longer than this are truncated before TTS. Range 100–5000. Default: 700."));

        // Capture for SaveSettings
        _interruptChk = interruptChk;
        _maxCharsBox  = maxBox;

        // ── Separator + Close ──────────────────────────────────────────────
        root.Children.Add(new Separator { Margin = new Thickness(0, 20, 0, 12) });

        var closeBtn = MakeBtn(Properties.Loc.S("Btn_Close"), isPrimary: true);
        closeBtn.HorizontalAlignment = HorizontalAlignment.Right;
        closeBtn.Click += (_, _) => { SaveSettings(); DialogResult = true; };
        root.Children.Add(closeBtn);
    }

    private void UpdatePanels()
    {
        if (_sherpaPanel   is not null) _sherpaPanel  .Visibility = _rbSherpa?.IsChecked   == true ? Visibility.Visible : Visibility.Collapsed;
        if (_voicevoxPanel is not null) _voicevoxPanel.Visibility = _rbVoicevox?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyBackend()
    {
        var folder = _folderBox?.Text.Trim() ?? SettingsService.Load().SherpaModelFolder;
        var port   = int.TryParse(_portBox?.Text.Trim(), out var p) ? p : SettingsService.Load().VoicevoxPort;

        VoiceOutputService.ActiveBackend = SelectedKey() switch
        {
            "Sherpa"   => new SherpaOnnxTtsBackend(folder),
            "Voicevox" => new VoicevoxTtsBackend(port),
            _          => new WindowsTtsBackend(),
        };
    }

    private void SaveSettings()
    {
        var s = SettingsService.Load();
        s.VoiceBackend = SelectedKey();

        if (_folderBox is not null)
            s.SherpaModelFolder = _folderBox.Text.Trim();

        if (_portBox is not null &&
            int.TryParse(_portBox.Text.Trim(), out var port) &&
            port is >= 1 and <= 65535)
            s.VoicevoxPort = port;

        s.VoiceInterruptOnNewMessage = _interruptChk?.IsChecked ?? s.VoiceInterruptOnNewMessage;

        if (_maxCharsBox is not null &&
            int.TryParse(_maxCharsBox.Text.Trim(), out var maxChars) &&
            maxChars is >= 100 and <= 5000)
            s.VoiceSpeechMaxChars = maxChars;

        SettingsService.Save(s);
    }

    private string SelectedKey()
    {
        if (_rbSherpa  ?.IsChecked == true) return "Sherpa";
        if (_rbVoicevox?.IsChecked == true) return "Voicevox";
        return "Windows";
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private TextBlock SectionHeading(string text)
    {
        var tb = new TextBlock
        {
            Text       = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 12,
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, 0, 0, 4),
        };
        tb.SetResourceReference(ForegroundProperty, "ContentTextBrush");
        return tb;
    }

    private TextBlock HintText(string text)
    {
        var tb = new TextBlock
        {
            Text         = text,
            FontFamily   = new FontFamily("Segoe UI"),
            FontSize     = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(20, 2, 0, 0),
        };
        tb.SetResourceReference(ForegroundProperty, "ContentDimBrush");
        return tb;
    }

    private RadioButton MakeRadio(string label)
    {
        var rb = new RadioButton
        {
            Content    = label,
            GroupName  = "Backend",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 12,
        };
        rb.SetResourceReference(ForegroundProperty, "ContentTextBrush");
        return rb;
    }

    private Button MakeBtn(string label, bool isPrimary = false)
    {
        var btn = new Button
        {
            Content         = label,
            FontFamily      = new FontFamily("Segoe UI"),
            FontSize        = 12,
            Padding         = new Thickness(12, 6, 12, 6),
            BorderThickness = new Thickness(1),
            Cursor          = System.Windows.Input.Cursors.Hand,
        };
        if (isPrimary)
        {
            btn.SetResourceReference(BackgroundProperty, "PrimaryAccentBrush");
            btn.SetResourceReference(ForegroundProperty, "AccentTextBrush");
        }
        else
        {
            btn.SetResourceReference(BackgroundProperty, "ControlBgBrush");
            btn.SetResourceReference(ForegroundProperty, "ContentTextBrush");
        }
        btn.SetResourceReference(BorderBrushProperty, "ControlBorderBrush");
        return btn;
    }
}
