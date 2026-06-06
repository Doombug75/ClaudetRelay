using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudetRelay.Services;
using System.Diagnostics;

namespace ClaudetRelay;

/// <summary>
/// Download and manage Sherpa-onnx ASR (speech recognition) model packs.
/// Opened from Voice Recognition Settings → Manage ASR Models.
/// Mirrors the pattern of VoiceModelManagerWindow for TTS models.
/// </summary>
public sealed class AsrModelManagerWindow : Window
{
    private readonly string? _themePath;
    private TextBox? _folderBox;

    private static string DefaultAsrFolder =>
        Path.Combine(AppContext.BaseDirectory, "ASR");

    private static readonly AsrModelInfo[] CuratedModels =
    [
        // Whisper — multilingual (includes German), recommended
        new("sherpa-onnx-whisper-tiny",
            "Whisper", "EN/DE/+98 languages · tiny",  "★★★",   78,
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-whisper-tiny.tar.bz2"),
        new("sherpa-onnx-whisper-base",
            "Whisper", "EN/DE/+98 languages · base",  "★★★★",  142,
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-whisper-base.tar.bz2"),
        new("sherpa-onnx-whisper-small",
            "Whisper", "EN/DE/+98 languages · small", "★★★★★", 466,
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-whisper-small.tar.bz2"),
        // SenseVoice — fast, EN/ZH/JA/KO only
        new("sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17",
            "SenseVoice", "EN/ZH/JA/KO · fast",       "★★★★",  130,
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17.tar.bz2"),
    ];

    private record AsrModelInfo(
        string Name, string Type, string Description,
        string Quality, int SizeMb, string DownloadUrl);

    public AsrModelManagerWindow(string? themePath)
    {
        _themePath = themePath;

        Title                 = Properties.Loc.S("Asr_ManageModels");
        Width                 = 640;
        SizeToContent         = SizeToContent.Height;
        MaxHeight             = 700;
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
        SourceInitialized += (_, _) => ApplyTitleBar();

        BuildUI();
    }

    private void ApplyTitleBar()
    {
        if (TryFindResource("SidebarBgBrush") is not SolidColorBrush bgBrush) return;
        if (TryFindResource("SidebarTextBrush") is not SolidColorBrush textBrush) return;
        try
        {
            var hwnd   = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int isDark = RelLuminance(bgBrush.Color) < 0.5 ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref isDark, 4);
            int colRef = bgBrush.Color.R | (bgBrush.Color.G << 8) | (bgBrush.Color.B << 16);
            DwmSetWindowAttribute(hwnd, 35, ref colRef, 4);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int val, int size);

    private static double RelLuminance(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double Linearize(double v) => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return 0.2126 * Linearize(r) + 0.7152 * Linearize(g) + 0.0722 * Linearize(b);
    }

    private void BuildUI()
    {
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var root   = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };
        scroll.Content = root;
        Content        = scroll;

        var isDE = System.Globalization.CultureInfo.CurrentUICulture
                         .TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase);

        // ── Intro ──────────────────────────────────────────────────────────
        var introTb = new TextBlock
        {
            Text = isDE
                ? "ASR-Modelle laufen vollständig offline auf deinem PC — keine Cloud, keine Internetverbindung " +
                  "während der Erkennung nötig. Wähle einen Ordner, lade ein Modell herunter und wähle " +
                  "es anschließend in den Spracherkennungs-Einstellungen aus.\n\n" +
                  "Für Deutsch empfehlen wir Whisper tiny oder base."
                : "ASR models run fully offline on your PC — no cloud or internet connection needed during " +
                  "recognition. Pick a folder, download a model, then select it in Voice Recognition Settings.\n\n" +
                  "For German, use Whisper tiny or base (recommended).",
            FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin   = new Thickness(0, 0, 0, 16)
        };
        introTb.SetResourceReference(ForegroundProperty, "ContentDimBrush");
        root.Children.Add(introTb);

        // ── Model folder ──────────────────────────────────────────────────
        root.Children.Add(SectionHeading("🗂  " + Properties.Loc.S("Asr_ModelFolder")));

        var s = SettingsService.Load();
        var folderRow = new Grid { Margin = new Thickness(0, 0, 0, 20) };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _folderBox = new TextBox
        {
            Text            = string.IsNullOrEmpty(s.AsrModelsFolder) ? DefaultAsrFolder : s.AsrModelsFolder,
            FontFamily      = new FontFamily("Segoe UI"), FontSize = 12,
            Padding         = new Thickness(8, 6, 8, 6),
            BorderThickness = new Thickness(1)
        };
        _folderBox.SetResourceReference(ForegroundProperty, "InputTextBrush");
        _folderBox.SetResourceReference(BackgroundProperty, "ControlBgBrush");
        _folderBox.SetResourceReference(BorderBrushProperty, "ControlBorderBrush");
        Grid.SetColumn(_folderBox, 0);
        folderRow.Children.Add(_folderBox);

        var browseBtn = MakeBtn("  📁  ");
        browseBtn.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(browseBtn, 1);
        browseBtn.Click += (_, _) =>
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog
            { Description = Properties.Loc.S("Asr_ModelFolder"), SelectedPath = _folderBox.Text, ShowNewFolderButton = true };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _folderBox.Text = dlg.SelectedPath;
                SaveFolder();
            }
        };
        folderRow.Children.Add(browseBtn);
        root.Children.Add(folderRow);

        // ── Model catalogue ────────────────────────────────────────────────
        root.Children.Add(SectionHeading("🎙  " + Properties.Loc.S("Audio_CuratedModels")));

        string? lastType = null;
        foreach (var m in CuratedModels)
        {
            if (m.Type != lastType)
            {
                var typeHdr = new TextBlock
                {
                    Text       = m.Type == "Whisper"
                        ? (isDE ? "🌍  Whisper  (mehrsprachig, empfohlen für Deutsch)" : "🌍  Whisper  (multilingual, recommended for European languages)")
                        : (isDE ? "⚡  SenseVoice  (schnell, nur EN/ZH/JA/KO)" : "⚡  SenseVoice  (fast, EN/ZH/JA/KO only)"),
                    FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Margin     = new Thickness(0, 8, 0, 4)
                };
                typeHdr.SetResourceReference(ForegroundProperty, "SidebarDimBrush");
                root.Children.Add(typeHdr);
                lastType = m.Type;
            }
            root.Children.Add(BuildModelRow(m));
        }

        // ── Close ──────────────────────────────────────────────────────────
        root.Children.Add(new Separator { Margin = new Thickness(0, 16, 0, 8) });
        var closeBtn = MakeBtn(Properties.Loc.S("Btn_Close"), isPrimary: true);
        closeBtn.Margin = new Thickness(0, 8, 0, 0);
        closeBtn.Click += (_, _) => { SaveFolder(); DialogResult = true; };
        root.Children.Add(closeBtn);
    }

    private UIElement BuildModelRow(AsrModelInfo m)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info   = new StackPanel();
        var nameTb = new TextBlock
        { Text = m.Name, FontFamily = new FontFamily("Segoe UI"), FontSize = 12, FontWeight = FontWeights.SemiBold };
        nameTb.SetResourceReference(ForegroundProperty, "ContentTextBrush");
        var descTb = new TextBlock
        { Text = $"{m.Description}  {m.Quality}  ·  ~{m.SizeMb} MB", FontFamily = new FontFamily("Segoe UI"), FontSize = 11 };
        descTb.SetResourceReference(ForegroundProperty, "ContentDimBrush");
        info.Children.Add(nameTb);
        info.Children.Add(descTb);
        Grid.SetColumn(info, 0);
        row.Children.Add(info);

        var statusLbl = new TextBlock
        { FontFamily = new FontFamily("Segoe UI"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        statusLbl.SetResourceReference(ForegroundProperty, "ContentDimBrush");
        Grid.SetColumn(statusLbl, 1);
        row.Children.Add(statusLbl);

        var actionBtn = MakeBtn("");
        Grid.SetColumn(actionBtn, 2);
        row.Children.Add(actionBtn);

        void RefreshRow()
        {
            var folder    = _folderBox?.Text.Trim() ?? DefaultAsrFolder;
            var installed = Directory.Exists(Path.Combine(folder, m.Name));
            statusLbl.Text    = installed ? "✓  " + Properties.Loc.S("Audio_Installed") : "";
            actionBtn.Content = installed ? Properties.Loc.S("Audio_Remove") : "⬇  " + Properties.Loc.S("Audio_Install");
        }
        RefreshRow();

        actionBtn.Click += async (_, _) =>
        {
            var folder   = _folderBox?.Text.Trim() ?? DefaultAsrFolder;
            var modelDir = Path.Combine(folder, m.Name);

            if (Directory.Exists(modelDir))
            {
                if (MessageBox.Show($"{Properties.Loc.S("Audio_ConfirmRemove")} \"{m.Name}\"?",
                        Properties.Loc.S("Asr_ManageModels"),
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
            var spinChars = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";
            int spinIdx   = 0;
            var spinTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            spinTimer.Tick += (_, _) => actionBtn.Content = spinChars[spinIdx++ % spinChars.Length].ToString();
            spinTimer.Start();

            try
            {
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                    SaveFolder();
                }
                var tmp = Path.Combine(Path.GetTempPath(), Path.GetFileName(m.DownloadUrl));
                using var http = new HttpClient();
                var bytes = await http.GetByteArrayAsync(m.DownloadUrl);
                await File.WriteAllBytesAsync(tmp, bytes);
                await Task.Run(() => ExtractTarBz2(tmp, folder));
                try { File.Delete(tmp); } catch { }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Download failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                spinTimer.Stop();
                actionBtn.IsEnabled = true;
                RefreshRow();
            }
        };

        return row;
    }

    private static void ExtractTarBz2(string archivePath, string destFolder)
    {
        Directory.CreateDirectory(destFolder);
        using var proc = Process.Start(new ProcessStartInfo("tar")
        {
            Arguments             = $"-xjf \"{archivePath}\" -C \"{destFolder}\"",
            UseShellExecute       = false,
            CreateNoWindow        = true,
            RedirectStandardError = true,
        })!;
        proc.WaitForExit(120_000);
    }

    private void SaveFolder()
    {
        var s = SettingsService.Load();
        s.AsrModelsFolder = _folderBox?.Text.Trim() ?? "";
        SettingsService.Save(s);
    }

    private TextBlock SectionHeading(string text)
    {
        var tb = new TextBlock
        { Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        tb.SetResourceReference(ForegroundProperty, "ContentTextBrush");
        return tb;
    }

    private Button MakeBtn(string label, bool isPrimary = false)
    {
        var btn = new Button
        { Content = label, FontFamily = new FontFamily("Segoe UI"), FontSize = 12, Padding = new Thickness(12, 6, 12, 6), BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Right };
        if (isPrimary) { btn.SetResourceReference(BackgroundProperty, "PrimaryAccentBrush"); btn.SetResourceReference(ForegroundProperty, "AccentTextBrush"); }
        else           { btn.SetResourceReference(BackgroundProperty, "ControlBgBrush");      btn.SetResourceReference(ForegroundProperty, "ContentTextBrush"); }
        btn.SetResourceReference(BorderBrushProperty, "ControlBorderBrush");
        return btn;
    }
}
