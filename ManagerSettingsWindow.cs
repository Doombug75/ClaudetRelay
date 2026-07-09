using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudetRelay.Services;

namespace ClaudetRelay;

/// <summary>
/// Manager Settings window.
/// Lets the user pick:
///   - Which participant handles context-window compression
///   - Which participant powers the Claudette live-chat brain (empty = auto-detect)
/// </summary>
public sealed class ManagerSettingsWindow : Window
{
    private ComboBox? _compressionCombo;
    private ComboBox? _brainCombo;
    private ComboBox? _codeGenCombo;
    private TextBlock? _compressionWarning;
    private TextBox?  _structoFoxBox;

    private static bool IsDE =>
        string.Equals(System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
                      "de", StringComparison.OrdinalIgnoreCase);

    public ManagerSettingsWindow(string? themePath, Action<Window>? applyTheme = null)
    {
        Title                 = Properties.Loc.S("Menu_Manager");
        Width                 = 500;
        SizeToContent         = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode            = ResizeMode.NoResize;
        ShowInTaskbar         = false;
        SetResourceReference(BackgroundProperty, "ContentBgBrush");

        if (applyTheme is not null)
            applyTheme(this);
        else if (themePath is not null)
        {
            try
            {
                var dict = OxsuitLoader.Load(themePath);
                if (dict is not null) Resources.MergedDictionaries.Add(dict);
            }
            catch { }
        }

        OverrideSystemColorsForCombos();
        BuildUI();
        UiZoomHelper.Apply(this, UiZoomHelper.FromSettings());
    }

    private void BuildUI()
    {
        var s            = SettingsService.Load();
        var participants = s.Participants.Where(p => p.Enabled).ToList();

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };
        Content  = root;

        // ── Section: Context Compression ──────────────────────────────────
        root.Children.Add(Heading("🗜  " + (IsDE
            ? "Kontext-Komprimierung"
            : "Context Compression")));

        root.Children.Add(BodyText(IsDE
            ? "Wähle welcher Teilnehmer die Zusammenfassung übernimmt, wenn das Kontextfenster zu 80% voll ist. " +
              "Ein Teilnehmer mit einem großen Kontextfenster ist für diese Aufgabe am besten geeignet."
            : "Choose which participant summarises the conversation when the context window reaches 80% capacity. " +
              "A participant with a large context window works best for this role."));

        root.Children.Add(SmallLabel(IsDE ? "Komprimierungs-Teilnehmer:" : "Compression participant:"));
        _compressionCombo = MakeCombo();
        _compressionCombo.Margin = new Thickness(0, 0, 0, 4);

        // Warning shown when the chosen participant has a smaller context than other participants
        _compressionWarning = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize     = 11,
            Margin       = new Thickness(0, 2, 0, 8),
            Visibility   = Visibility.Collapsed
        };
        _compressionWarning.Foreground = new SolidColorBrush(Color.FromRgb(220, 140, 30));

        PopulateCombo(_compressionCombo, participants, s.CompressionParticipantName, includeNone: true);
        _compressionCombo.SelectionChanged += (_, _) => UpdateCompressionWarning(participants);

        root.Children.Add(_compressionCombo);
        root.Children.Add(_compressionWarning);
        UpdateCompressionWarning(participants);

        root.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 16) });

        // ── Section: Claudette Brain ───────────────────────────────────────
        root.Children.Add(Heading("🧠  " + (IsDE
            ? "Claudette-Gehirn"
            : "Claudette Brain")));

        root.Children.Add(BodyText(IsDE
            ? "Claudette ist Claudes hilfreicher Begleiter im Live-Chat-Fenster. Wähle den Teilnehmer, " +
              "der sie antreiben soll. Leer lassen für automatische Erkennung " +
              "(bevorzugt: Gemma → Cloud-KI → beliebiges Ollama-Modell)."
            : "Claudette is Claude's helpful companion in the live chat panel. Choose which participant " +
              "powers her. Leave empty for auto-detection " +
              "(preference order: Gemma → any Cloud AI → any other Ollama)."));

        root.Children.Add(SmallLabel(IsDE ? "Claudette-Gehirn:" : "Claudette brain:"));
        _brainCombo = MakeCombo();
        _brainCombo.Margin = new Thickness(0, 0, 0, 4);

        PopulateCombo(_brainCombo, participants, s.ClaudetteBrainName, includeNone: true);
        root.Children.Add(_brainCombo);
        root.Children.Add(HintText(IsDE
            ? "Auto-Erkennung wählt das erste verfügbare Modell nach obiger Reihenfolge."
            : "Auto-detect picks the first available model in the priority order above."));

        root.Children.Add(new Separator { Margin = new Thickness(0, 16, 0, 16) });

        // ── Section: Code Generator ────────────────────────────────────────
        root.Children.Add(Heading("💻  " + (IsDE ? "Code-Generator" : "Code Generator")));

        root.Children.Add(BodyText(IsDE
            ? "Welcher Teilnehmer im Code-Bereich aus Gerüst, Struktogrammen und Ablaufplänen Code erzeugt " +
              "(z.B. ein spezialisiertes Coder-Modell). Bei „(Jedes Mal fragen)\" wird bei jeder Generierung " +
              "nachgefragt, wer die Aufgabe übernimmt."
            : "Which participant generates code from the skeleton, structograms and flowcharts in the Code section " +
              "(e.g. a specialised coder model). With \"(Ask each time)\" you are prompted on every generation."));

        root.Children.Add(SmallLabel(IsDE ? "Code-Generator:" : "Code generator:"));
        _codeGenCombo = MakeCombo();
        _codeGenCombo.Margin = new Thickness(0, 0, 0, 4);
        PopulateCombo(_codeGenCombo, participants, s.CodeGeneratorName, includeNone: true,
            noneLabel: IsDE ? "(Jedes Mal fragen)" : "(Ask each time)");
        root.Children.Add(_codeGenCombo);

        root.Children.Add(new Separator { Margin = new Thickness(0, 16, 0, 16) });

        // ── Section: StructoFox (external code editor) ─────────────────────
        root.Children.Add(Heading("🦊  StructoFox"));
        root.Children.Add(BodyText(Properties.Loc.S("SF_SettingsBody")));
        _structoFoxBox = new TextBox { Text = s.StructoFoxExePath, Width = 360 };
        _structoFoxBox.SetResourceReference(BackgroundProperty, "InputBgBrush");
        _structoFoxBox.SetResourceReference(ForegroundProperty, "SidebarTextBrush");
        var browseBtn = MakeBtn(Properties.Loc.S("Asr_BrowseFolder"), false);
        browseBtn.Margin = new Thickness(8, 0, 0, 0);
        browseBtn.Click += (_, _) =>
        {
            var pick = new Microsoft.Win32.OpenFileDialog { Title = "StructoFox.exe", Filter = "StructoFox|StructoFox.exe|Executables (*.exe)|*.exe" };
            if (pick.ShowDialog(this) == true && _structoFoxBox is not null) _structoFoxBox.Text = pick.FileName;
        };
        var sfRow = new StackPanel { Orientation = Orientation.Horizontal };
        sfRow.Children.Add(_structoFoxBox);
        sfRow.Children.Add(browseBtn);
        root.Children.Add(sfRow);

        root.Children.Add(new Separator { Margin = new Thickness(0, 16, 0, 16) });

        // ── OK / Cancel ────────────────────────────────────────────────────
        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancelBtn = MakeBtn(Properties.Loc.S("Btn_Cancel"), false);
        cancelBtn.Margin = new Thickness(0, 0, 8, 0);
        cancelBtn.Click += (_, _) => { DialogResult = false; Close(); };
        var okBtn = MakeBtn(Properties.Loc.S("Btn_OK"), true);
        okBtn.Click += (_, _) => { SaveSettings(); DialogResult = true; Close(); };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(okBtn);
        root.Children.Add(btnRow);
    }

    private void PopulateCombo(ComboBox combo, List<ParticipantConfig> participants,
                                string currentValue, bool includeNone, string? noneLabel = null)
    {
        if (includeNone)
            combo.Items.Add(noneLabel ?? (IsDE ? "(Automatisch)" : "(Auto-detect)"));

        foreach (var p in participants)
        {
            var displayName = string.IsNullOrWhiteSpace(p.Name) ? p.Model : p.Name;
            combo.Items.Add(displayName);
        }

        // Select saved value
        if (!string.IsNullOrEmpty(currentValue))
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i] is string s &&
                    s.Equals(currentValue, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
        }
        combo.SelectedIndex = 0;
    }

    private void UpdateCompressionWarning(List<ParticipantConfig> participants)
    {
        if (_compressionWarning is null || _compressionCombo is null) return;

        var chosen = SelectedParticipantName(_compressionCombo, participants);
        if (string.IsNullOrEmpty(chosen))
        {
            _compressionWarning.Visibility = Visibility.Collapsed;
            return;
        }

        var chosenP = participants.FirstOrDefault(p =>
            (string.IsNullOrWhiteSpace(p.Name) ? p.Model : p.Name)
                .Equals(chosen, StringComparison.OrdinalIgnoreCase));

        if (chosenP is null)
        {
            _compressionWarning.Visibility = Visibility.Collapsed;
            return;
        }

        // Only local Ollama models have a configurable context window — cloud models have large contexts
        if (!string.Equals(chosenP.Type, "Ollama", StringComparison.OrdinalIgnoreCase))
        {
            _compressionWarning.Visibility = Visibility.Collapsed;
            return;
        }

        int chosenCtx   = chosenP.OllamaNumCtx > 0 ? chosenP.OllamaNumCtx : 2048;
        int maxOtherCtx = participants
            .Where(p => p != chosenP && string.Equals(p.Type, "Ollama", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.OllamaNumCtx > 0 ? p.OllamaNumCtx : 2048)
            .DefaultIfEmpty(0)
            .Max();

        if (maxOtherCtx > chosenCtx)
        {
            _compressionWarning.Text = IsDE
                ? $"⚠  Dieser Teilnehmer hat ein kleineres Kontextfenster ({chosenCtx:N0} Tokens) als andere " +
                  $"Teilnehmer ({maxOtherCtx:N0} Tokens). Für die Komprimierung ist ein großes Kontextfenster empfohlen."
                : $"⚠  This participant has a smaller context window ({chosenCtx:N0} tokens) than other " +
                  $"participants ({maxOtherCtx:N0} tokens). A large context window is recommended for the compression role.";
            _compressionWarning.Visibility = Visibility.Visible;
        }
        else
        {
            _compressionWarning.Visibility = Visibility.Collapsed;
        }
    }

    private string SelectedParticipantName(ComboBox combo, List<ParticipantConfig> participants)
    {
        if (combo.SelectedIndex <= 0) return ""; // index 0 = auto / none
        var displayName = combo.SelectedItem?.ToString() ?? "";
        var match = participants.FirstOrDefault(p =>
            (string.IsNullOrWhiteSpace(p.Name) ? p.Model : p.Name)
                .Equals(displayName, StringComparison.OrdinalIgnoreCase));
        return match is not null ? (string.IsNullOrWhiteSpace(match.Name) ? match.Model : match.Name) : displayName;
    }

    private void SaveSettings()
    {
        var s    = SettingsService.Load();
        var participants = s.Participants.Where(p => p.Enabled).ToList();

        s.CompressionParticipantName = _compressionCombo is not null
            ? SelectedParticipantName(_compressionCombo, participants)
            : "";

        s.ClaudetteBrainName = _brainCombo is not null
            ? SelectedParticipantName(_brainCombo, participants)
            : "";

        s.CodeGeneratorName = _codeGenCombo is not null
            ? SelectedParticipantName(_codeGenCombo, participants)
            : "";

        if (_structoFoxBox is not null) s.StructoFoxExePath = _structoFoxBox.Text?.Trim() ?? "";

        SettingsService.Save(s);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private TextBlock Heading(string text)
    {
        var tb = new TextBlock
        {
            Text       = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 13,
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, 0, 0, 6)
        };
        tb.SetResourceReference(ForegroundProperty, "ContentTextBrush");
        return tb;
    }

    private TextBlock BodyText(string text)
    {
        var tb = new TextBlock
        {
            Text         = text,
            TextWrapping = TextWrapping.Wrap,
            FontFamily   = new FontFamily("Segoe UI"),
            FontSize     = 11,
            Margin       = new Thickness(0, 0, 0, 10)
        };
        tb.SetResourceReference(ForegroundProperty, "ContentDimBrush");
        return tb;
    }

    private TextBlock SmallLabel(string text)
    {
        var tb = new TextBlock
        {
            Text       = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 11,
            Margin     = new Thickness(0, 4, 0, 3)
        };
        tb.SetResourceReference(ForegroundProperty, "SidebarDimBrush");
        return tb;
    }

    private TextBlock HintText(string text)
    {
        var tb = new TextBlock
        {
            Text         = text,
            TextWrapping = TextWrapping.Wrap,
            FontFamily   = new FontFamily("Segoe UI"),
            FontSize     = 10,
            Margin       = new Thickness(0, 2, 0, 0)
        };
        tb.SetResourceReference(ForegroundProperty, "ContentDimBrush");
        return tb;
    }

    private ComboBox MakeCombo()
    {
        var cb = new ComboBox { Margin = new Thickness(0, 0, 0, 4) };
        cb.SetResourceReference(StyleProperty, "ModernComboBox");
        return cb;
    }

    private Button MakeBtn(string label, bool isPrimary)
    {
        var btn = new Button
        {
            Content         = label,
            FontFamily      = new FontFamily("Segoe UI"),
            FontSize        = 12,
            MinWidth        = 88,
            Height          = 34,
            Padding         = new Thickness(16, 0, 16, 0),
            BorderThickness = new Thickness(1),
            Cursor          = System.Windows.Input.Cursors.Hand
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

    private void OverrideSystemColorsForCombos()
    {
        if (TryFindResource("ControlBgBrush") is Brush bg)
        {
            Resources[SystemColors.WindowBrushKey]       = bg;
            Resources[SystemColors.ControlBrushKey]      = bg;
            Resources[SystemColors.ControlLightBrushKey] = bg;
        }
        if (TryFindResource("ContentTextBrush") is Brush fg)
        {
            Resources[SystemColors.WindowTextBrushKey]  = fg;
            Resources[SystemColors.ControlTextBrushKey] = fg;
        }
        if (TryFindResource("ControlBorderBrush") is Brush border)
            Resources[SystemColors.ActiveBorderBrushKey] = border;
    }
}
