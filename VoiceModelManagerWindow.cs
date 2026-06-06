using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudetRelay.Services;
using System.Diagnostics;

namespace ClaudetRelay;

/// <summary>
/// Lets the user download and manage Sherpa-onnx voice model packs,
/// configure the model folder and VOICEVOX port, and view backend instructions.
/// Opened from the Audio section of the ⋮ options menu.
/// </summary>
public sealed class VoiceModelManagerWindow : Window
{
    private readonly string? _themePath;
    private TextBlock?  _sherpaStatusTb;
    private TextBlock?  _voicevoxStatusTb;
    private TextBox?    _folderBox;
    private TextBox?    _portBox;

    // ── Default folder: <exe dir>/Voices ──────────────────────────────────
    private static string DefaultVoicesFolder =>
        Path.Combine(AppContext.BaseDirectory, "Voices");

    // ── Curated voice model catalogue ─────────────────────────────────────

    private static readonly VoiceModelInfo[] CuratedModels =
    [
        new("de_DE-thorsten-high",           "DE", "Male · natural",     "★★★★", 67, "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-de_DE-thorsten-high.tar.bz2"),
        new("de_DE-thorsten_emotional-medium","DE", "Male · expressive",  "★★★★", 73, "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-de_DE-thorsten_emotional-medium.tar.bz2"),
        new("de_DE-eva_k-x_low",             "DE", "Female",             "★★★",  34, "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-de_DE-eva_k-x_low.tar.bz2"),
        new("en_US-ryan-high",               "EN", "Male · warm",        "★★★★", 68, "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-en_US-ryan-high.tar.bz2"),
        new("en_US-hfc_female-medium",       "EN", "Female · clear",     "★★★★", 46, "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-en_US-hfc_female-medium.tar.bz2"),
        new("en_US-lessac-high",             "EN", "Male · neutral",     "★★★★", 67, "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-en_US-lessac-high.tar.bz2"),
        new("en_GB-alba-medium",             "EN", "Female · British",   "★★★",  44, "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-en_GB-alba-medium.tar.bz2"),
    ];

    private record VoiceModelInfo(
        string Name, string Lang, string Description,
        string Quality, int SizeMb, string DownloadUrl);

    // ── Constructor ────────────────────────────────────────────────────────

    public VoiceModelManagerWindow(string? themePath)
    {
        _themePath = themePath;

        Title                 = Properties.Loc.S("Audio_VoiceModels");
        Width                 = 640;
        SizeToContent         = SizeToContent.Height;
        MaxHeight             = 800;
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

        BuildUI();
        _ = RefreshStatusAsync();
    }

    // ── UI ─────────────────────────────────────────────────────────────────

    private void BuildUI()
    {
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var root   = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };
        scroll.Content = root;
        Content        = scroll;

        // ── Model folder ──────────────────────────────────────────────────
        root.Children.Add(SectionHeading("🗂  " + Properties.Loc.S("Audio_ModelFolder")));

        var folderRow = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.Children.Add(folderRow);

        var s = SettingsService.Load();
        _folderBox = new TextBox
        {
            Text        = string.IsNullOrEmpty(s.SherpaModelFolder) ? DefaultVoicesFolder : s.SherpaModelFolder,
            FontFamily  = new FontFamily("Segoe UI"),
            FontSize    = 12,
            Padding     = new Thickness(8, 6, 8, 6),
        };
        _folderBox.SetResourceReference(ForegroundProperty, "InputTextBrush");
        _folderBox.SetResourceReference(BackgroundProperty, "ControlBgBrush");
        _folderBox.SetResourceReference(BorderBrushProperty, "ControlBorderBrush");
        _folderBox.BorderThickness = new Thickness(1);
        Grid.SetColumn(_folderBox, 0);
        folderRow.Children.Add(_folderBox);

        var browseBtn = MakeBtn("  📁  ");
        browseBtn.Margin = new Thickness(8, 0, 0, 0);
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
            {
                _folderBox.Text = dlg.SelectedPath;
                SaveFolderAndPort();
            }
        };
        folderRow.Children.Add(browseBtn);

        // Sherpa-onnx status
        _sherpaStatusTb = StatusLine("");
        root.Children.Add(_sherpaStatusTb);

        // ── Voice model catalogue ──────────────────────────────────────────
        root.Children.Add(SectionHeading("🎙  " + Properties.Loc.S("Audio_CuratedModels")));

        string? lastLang = null;
        foreach (var m in CuratedModels)
        {
            if (m.Lang != lastLang)
            {
                root.Children.Add(LangHeading(m.Lang == "DE" ? "🇩🇪  Deutsch" : "🇬🇧  English"));
                lastLang = m.Lang;
            }
            root.Children.Add(BuildModelRow(m));
        }

        root.Children.Add(new Separator { Margin = new Thickness(0, 16, 0, 16) });

        // ── VOICEVOX ──────────────────────────────────────────────────────
        root.Children.Add(SectionHeading("🎭  VOICEVOX"));

        var vvRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        vvRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        vvRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        vvRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.Children.Add(vvRow);

        var portLbl = new TextBlock
        {
            Text              = Properties.Loc.S("Audio_VoicevoxPort") + "  ",
            FontFamily        = new FontFamily("Segoe UI"),
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        portLbl.SetResourceReference(ForegroundProperty, "ContentTextBrush");
        Grid.SetColumn(portLbl, 0);
        vvRow.Children.Add(portLbl);

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
        Grid.SetColumn(_portBox, 1);
        vvRow.Children.Add(_portBox);

        var vvDownloadBtn = MakeBtn("⬇  " + Properties.Loc.S("Audio_DownloadVoicevox"));
        vvDownloadBtn.Margin = new Thickness(12, 0, 0, 0);
        vvDownloadBtn.Click += (_, _) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://voicevox.hiroshiba.jp/") { UseShellExecute = true });
        Grid.SetColumn(vvDownloadBtn, 2);
        vvRow.Children.Add(vvDownloadBtn);

        _voicevoxStatusTb = StatusLine("");
        root.Children.Add(_voicevoxStatusTb);

        root.Children.Add(new Separator { Margin = new Thickness(0, 16, 0, 8) });

        // ── In-app instructions ────────────────────────────────────────────
        root.Children.Add(BuildInstructions());

        // ── Save / Close ───────────────────────────────────────────────────
        var closeBtn = MakeBtn(Properties.Loc.S("Btn_Close"), isPrimary: true);
        closeBtn.Margin = new Thickness(0, 16, 0, 0);
        closeBtn.Click += (_, _) => { SaveFolderAndPort(); DialogResult = true; };
        root.Children.Add(closeBtn);
    }

    private UIElement BuildModelRow(VoiceModelInfo m)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Name + description
        var info = new StackPanel();
        var nameTb = new TextBlock
        {
            Text       = m.Name,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 12,
            FontWeight = FontWeights.SemiBold,
        };
        nameTb.SetResourceReference(ForegroundProperty, "ContentTextBrush");
        var descTb = new TextBlock
        {
            Text       = $"{m.Description}  {m.Quality}  ·  ~{m.SizeMb} MB",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 11,
        };
        descTb.SetResourceReference(ForegroundProperty, "ContentDimBrush");
        info.Children.Add(nameTb);
        info.Children.Add(descTb);
        Grid.SetColumn(info, 0);
        row.Children.Add(info);

        // Status label (Installed / –)
        var statusLbl = new TextBlock
        {
            FontFamily        = new FontFamily("Segoe UI"),
            FontSize          = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(12, 0, 12, 0),
        };
        statusLbl.SetResourceReference(ForegroundProperty, "ContentDimBrush");
        Grid.SetColumn(statusLbl, 1);
        row.Children.Add(statusLbl);

        // Install / Remove button
        var actionBtn = MakeBtn("");
        Grid.SetColumn(actionBtn, 2);
        row.Children.Add(actionBtn);

        void RefreshRow()
        {
            var s      = SettingsService.Load();
            var folder = s.SherpaModelFolder;
            var installed = !string.IsNullOrEmpty(folder) &&
                            Directory.Exists(Path.Combine(folder, m.Name));

            statusLbl.Text = installed
                ? "✓  " + Properties.Loc.S("Audio_Installed")
                : "";
            actionBtn.Content = installed
                ? Properties.Loc.S("Audio_Remove")
                : "⬇  " + Properties.Loc.S("Audio_Install");
        }

        actionBtn.Click += async (_, _) =>
        {
            // Always read from the live text box so unsaved edits are respected.
            var folder = _folderBox?.Text.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(folder))
                folder = DefaultVoicesFolder;

            var modelDir = Path.Combine(folder, m.Name);
            if (Directory.Exists(modelDir))
            {
                var confirmMsg = $"{Properties.Loc.S("Audio_ConfirmRemove")} \"{m.Name}\"?";
                if (MessageBox.Show(
                    confirmMsg,
                    Properties.Loc.S("Audio_VoiceModels"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    Directory.Delete(modelDir, recursive: true);
                    RefreshRow();
                }
                return;
            }

            // ── Download + extract ─────────────────────────────────────────
            actionBtn.IsEnabled = false;
            actionBtn.Content   = "⠋";
            var spinChars       = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";
            int spinIdx         = 0;
            var spinTimer       = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(100) };
            spinTimer.Tick += (_, _) =>
            {
                spinIdx = (spinIdx + 1) % spinChars.Length;
                actionBtn.Content = spinChars[spinIdx].ToString();
            };
            spinTimer.Start();

            try
            {
                // Create and persist the folder if it doesn't exist yet.
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                    var cfg = SettingsService.Load();
                    cfg.SherpaModelFolder = folder;
                    SettingsService.Save(cfg);
                    if (_folderBox is not null) _folderBox.Text = folder;
                }
                var tmpFile = Path.Combine(Path.GetTempPath(), Path.GetFileName(m.DownloadUrl));

                using var http   = new HttpClient();
                var bytes        = await http.GetByteArrayAsync(m.DownloadUrl);
                await File.WriteAllBytesAsync(tmpFile, bytes);

                await Task.Run(() => ExtractTarBz2(tmpFile, folder));
                try { File.Delete(tmpFile); } catch { }

                // Rename extracted folder to expected model name if needed
                var extracted = Directory.GetDirectories(folder)
                    .FirstOrDefault(d => Path.GetFileName(d).Contains(m.Name.Split('-')[0]));
                if (extracted is not null && !string.Equals(Path.GetFileName(extracted), m.Name))
                    Directory.Move(extracted, modelDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Download failed:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                spinTimer.Stop();
                actionBtn.IsEnabled = true;
                RefreshRow();
            }
        };

        RefreshRow();
        return row;
    }

    private static void ExtractTarBz2(string archivePath, string destFolder)
    {
        // tar.exe is built into Windows 10 v1803+ — no external install needed.
        Directory.CreateDirectory(destFolder);
        using var proc = Process.Start(new ProcessStartInfo("tar")
        {
            Arguments             = $"-xjf \"{archivePath}\" -C \"{destFolder}\"",
            UseShellExecute       = false,
            CreateNoWindow        = true,
            RedirectStandardError = true,
        })!;
        proc.WaitForExit(60_000);
    }

    private UIElement BuildInstructions()
    {
        var panel = new StackPanel();

        var lang   = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var isDE   = string.Equals(lang, "de", StringComparison.OrdinalIgnoreCase);

        var sherpaHeading = SectionHeading("ℹ  Sherpa-onnx");
        panel.Children.Add(sherpaHeading);

        var sherpaTxt = new TextBlock
        {
            Text         = isDE
                ? "Sherpa-onnx-Stimmen laufen vollständig offline — keine Internetverbindung nötig. " +
                  "Wähle oben einen Modellordner, lade ein oder mehrere Modelle herunter, " +
                  "dann wähle im ⋮-Menü unter Audio → Backend den Eintrag \"Sherpa-onnx\".\n\n" +
                  "Jedes Modell belegt je nach Qualitätsstufe ca. 30–80 MB auf der Festplatte. " +
                  "Selbst heruntergeladene VITS-Modelle (kompatibel mit Piper TTS) können " +
                  "einfach als Unterordner abgelegt werden und erscheinen automatisch in der Stimmenliste."
                : "Sherpa-onnx voices run fully offline — no internet connection required. " +
                  "Pick a model folder above, download one or more voice models, " +
                  "then select Audio → Backend → Sherpa-onnx from the ⋮ menu.\n\n" +
                  "Each model uses ~30–80 MB depending on quality. " +
                  "Custom VITS models (Piper TTS-compatible) can be placed as subdirectories " +
                  "and will appear automatically in the voice picker.",
            FontFamily   = new FontFamily("Segoe UI"),
            FontSize     = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 4, 0, 16),
        };
        sherpaTxt.SetResourceReference(ForegroundProperty, "ContentDimBrush");
        panel.Children.Add(sherpaTxt);

        var vvHeading = SectionHeading("ℹ  VOICEVOX");
        panel.Children.Add(vvHeading);

        var vvTxt = new TextBlock
        {
            Text         = isDE
                ? "VOICEVOX ist eine kostenlose, separate Anwendung mit anime-inspirierten Charakterstimmen. " +
                  "Einmal installiert und gestartet, erscheinen seine Stimmen automatisch in der Stimmenliste. " +
                  "Klicke auf den Download-Button, um die offizielle VOICEVOX-Website zu öffnen.\n\n" +
                  "Kompatible Alternativen (AivisSpeech, COEIROINK) funktionieren ebenfalls, " +
                  "solange sie auf demselben Port laufen."
                : "VOICEVOX is a free, separate application with anime-inspired character voices. " +
                  "Once installed and running, its voices appear automatically in the voice picker. " +
                  "Click the Download button above to open the official VOICEVOX website.\n\n" +
                  "Compatible alternatives (AivisSpeech, COEIROINK) also work as long as " +
                  "they run on the same port.",
            FontFamily   = new FontFamily("Segoe UI"),
            FontSize     = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 4, 0, 0),
        };
        vvTxt.SetResourceReference(ForegroundProperty, "ContentDimBrush");
        panel.Children.Add(vvTxt);

        return panel;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private async Task RefreshStatusAsync()
    {
        var s         = SettingsService.Load();
        var sherpaOk  = Directory.Exists(s.SherpaModelFolder) &&
                        new SherpaOnnxTtsBackend(s.SherpaModelFolder).GetVoices().Count > 0;
        var vvOk      = await new VoicevoxTtsBackend(s.VoicevoxPort).IsAvailableAsync();

        if (_sherpaStatusTb is not null)
            _sherpaStatusTb.Text = sherpaOk
                ? "✓  " + Properties.Loc.S("Audio_SherpaReady")
                : "○  " + Properties.Loc.S("Audio_SherpaNoModels");

        if (_voicevoxStatusTb is not null)
            _voicevoxStatusTb.Text = vvOk
                ? "✓  " + Properties.Loc.S("Audio_VoicevoxRunning")
                : "○  " + Properties.Loc.S("Audio_VoicevoxNotRunning");
    }

    private void SaveFolderAndPort()
    {
        var s = SettingsService.Load();
        if (_folderBox is not null) s.SherpaModelFolder = _folderBox.Text.Trim();
        if (_portBox   is not null &&
            int.TryParse(_portBox.Text.Trim(), out var port) &&
            port is >= 1 and <= 65535)
            s.VoicevoxPort = port;
        SettingsService.Save(s);
    }

    private TextBlock StatusLine(string text)
    {
        var tb = new TextBlock
        {
            Text       = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 11,
            Margin     = new Thickness(0, 2, 0, 10),
        };
        tb.SetResourceReference(ForegroundProperty, "ContentDimBrush");
        return tb;
    }

    private TextBlock SectionHeading(string text)
    {
        var tb = new TextBlock
        {
            Text       = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 12,
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, 10, 0, 4),
        };
        tb.SetResourceReference(ForegroundProperty, "ContentTextBrush");
        return tb;
    }

    private TextBlock LangHeading(string text)
    {
        var tb = new TextBlock
        {
            Text       = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 11,
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, 8, 0, 2),
        };
        tb.SetResourceReference(ForegroundProperty, "ContentDimBrush");
        return tb;
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
