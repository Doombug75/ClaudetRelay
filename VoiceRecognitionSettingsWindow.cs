using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClaudetRelay.Services;
using NAudio.Wave;

namespace ClaudetRelay;

/// <summary>
/// Voice Recognition settings window.
/// Covers: ASR model selection, activation mode (AlwaysOn / PushToTalk / VoiceActivated),
/// push-to-talk key picker, and a live level meter with draggable threshold marker.
/// </summary>
public sealed class VoiceRecognitionSettingsWindow : Window
{
    private readonly string?          _themePath;
    private readonly DictationService _dictation;

    // UI refs
    private ComboBox?   _modelCombo;
    private ComboBox?   _typeCombo;
    private TextBox?    _folderBox;
    private RadioButton? _rbAlways, _rbPtt, _rbVoice;
    private StackPanel? _pttPanel;
    private StackPanel? _voicePanel;
    private TextBlock?  _pttKeyLabel;
    private CheckBox?   _cbCtrl, _cbShift, _cbAlt;
    private Canvas?     _meterCanvas;
    private Rectangle?  _meterFill;
    private Line?       _thresholdLine;
    private DispatcherTimer? _meterTimer;
    private float       _currentLevel;
    private float       _threshold;
    private bool        _recordingKey;

    public VoiceRecognitionSettingsWindow(string? themePath, DictationService dictation,
                                           Action<Window>? applyTheme = null)
    {
        _themePath  = themePath;
        _dictation  = dictation;

        Title                 = Properties.Loc.S("Asr_WindowTitle");
        Width                 = 520;
        SizeToContent         = SizeToContent.Height;
        MinWidth              = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode            = ResizeMode.NoResize;
        ShowInTaskbar         = false;
        SetResourceReference(BackgroundProperty, "ContentBgBrush");

        if (applyTheme is not null)
        {
            // Use the caller's full ApplyThemeToDialog (loads resources + title bar colouring)
            applyTheme(this);
        }
        else if (themePath is not null)
        {
            try
            {
                var dict = OxsuitLoader.Load(themePath);
                if (dict is not null) Resources.MergedDictionaries.Add(dict);
            }
            catch { }
        }

        BuildUI();

        // Subscribe to level events for the meter
        _dictation.LevelChanged += OnLevelChanged;

        Closed += (_, _) =>
        {
            _dictation.LevelChanged -= OnLevelChanged;
            _meterTimer?.Stop();
        };

        // Refresh meter every 50 ms via dispatcher timer (UI thread safe)
        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _meterTimer.Tick += (_, _) => UpdateMeter();
        _meterTimer.Start();
    }

    private void OnLevelChanged(float rms) => _currentLevel = rms;

    // ── UI construction ────────────────────────────────────────────────────

    private void BuildUI()
    {
        var s = SettingsService.Load();
        _threshold = s.VoiceActivationThreshold;

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };
        scroll.Content = root;
        Content = scroll;

        // ── Model section ──────────────────────────────────────────────────
        root.Children.Add(Heading(Properties.Loc.S("Asr_ModelSection")));

        // Folder row
        root.Children.Add(SmallLabel(Properties.Loc.S("Asr_ModelFolder")));
        var folderRow = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _folderBox = new TextBox
        {
            Text            = s.AsrModelsFolder,
            FontFamily      = new FontFamily("Segoe UI"),
            FontSize        = 12,
            Padding         = new Thickness(6, 4, 6, 4),
            BorderThickness = new Thickness(1),
            IsReadOnly      = true
        };
        _folderBox.SetResourceReference(ForegroundProperty,  "ContentTextBrush");
        _folderBox.SetResourceReference(BackgroundProperty,  "ControlBgBrush");
        _folderBox.SetResourceReference(BorderBrushProperty, "ControlBorderBrush");
        Grid.SetColumn(_folderBox, 0);

        var browseBtn = MakeBtn(Properties.Loc.S("Asr_BrowseFolder"), false);
        browseBtn.Margin = new Thickness(6, 0, 0, 0);
        browseBtn.Click += (_, _) =>
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog
            { Description = Properties.Loc.S("Asr_ModelFolder"), SelectedPath = _folderBox.Text };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _folderBox.Text = dlg.SelectedPath;
                RefreshModelList(dlg.SelectedPath);
            }
        };
        Grid.SetColumn(browseBtn, 1);
        folderRow.Children.Add(_folderBox);
        folderRow.Children.Add(browseBtn);
        root.Children.Add(folderRow);

        // Model combo
        root.Children.Add(SmallLabel(Properties.Loc.S("Asr_ModelName")));
        _modelCombo = MakeCombo();
        _modelCombo.Margin = new Thickness(0, 0, 0, 4);
        RefreshModelList(s.AsrModelsFolder);
        root.Children.Add(_modelCombo);

        // Model type combo
        root.Children.Add(SmallLabel(Properties.Loc.S("Asr_ModelType")));
        _typeCombo = MakeCombo();
        _typeCombo.Items.Add(Properties.Loc.S("Asr_TypeWhisper"));
        _typeCombo.Items.Add(Properties.Loc.S("Asr_TypeSenseVoice"));
        _typeCombo.SelectedIndex = s.AsrModelType.ToLower() == "sense_voice" ? 1 : 0;
        _typeCombo.Margin = new Thickness(0, 0, 0, 4);
        root.Children.Add(_typeCombo);

        // Type hint text (updates when selection changes)
        var typeHint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 10,
            Margin = new Thickness(0, 0, 0, 14)
        };
        typeHint.SetResourceReference(ForegroundProperty, "ContentDimBrush");
        void UpdateTypeHint() =>
            typeHint.Text = _typeCombo.SelectedIndex == 1
                ? Properties.Loc.S("Asr_TypeSenseVoiceHint")
                : Properties.Loc.S("Asr_TypeWhisperHint");
        _typeCombo.SelectionChanged += (_, _) => UpdateTypeHint();
        UpdateTypeHint();
        root.Children.Add(typeHint);

        // Download hint + manage button
        var downloadHint = new TextBlock
        {
            Text = Properties.Loc.S("Asr_DownloadHint"),
            TextWrapping = TextWrapping.Wrap, FontSize = 10,
            Margin = new Thickness(0, 0, 0, 6)
        };
        downloadHint.SetResourceReference(ForegroundProperty, "ContentDimBrush");
        root.Children.Add(downloadHint);

        var manageBtn = MakeBtn(Properties.Loc.S("Asr_ManageModels"), false);
        manageBtn.HorizontalAlignment = HorizontalAlignment.Left;
        manageBtn.Margin = new Thickness(0, 0, 0, 20);
        manageBtn.Click += (_, _) =>
        {
            var w = new AsrModelManagerWindow(_themePath) { Owner = this };
            w.ShowDialog();
            RefreshModelList(_folderBox?.Text ?? "");
        };
        root.Children.Add(manageBtn);

        // ── Activation mode ────────────────────────────────────────────────
        root.Children.Add(Heading(Properties.Loc.S("Asr_ActivationSection")));

        _rbAlways = MakeRadio(Properties.Loc.S("Asr_AlwaysOn"),       "ActivationMode");
        _rbPtt    = MakeRadio(Properties.Loc.S("Asr_PushToTalk"),     "ActivationMode");
        _rbVoice  = MakeRadio(Properties.Loc.S("Asr_VoiceActivated"), "ActivationMode");

        switch (s.AsrActivationMode)
        {
            case "PushToTalk":     _rbPtt.IsChecked   = true; break;
            case "VoiceActivated": _rbVoice.IsChecked = true; break;
            default:               _rbAlways.IsChecked = true; break;
        }

        root.Children.Add(_rbAlways);
        root.Children.Add(_rbPtt);
        root.Children.Add(_rbVoice);

        // ── PTT key panel ──────────────────────────────────────────────────
        _pttPanel = new StackPanel { Margin = new Thickness(20, 6, 0, 16) };

        root.Children.Add(_pttPanel);

        _pttPanel.Children.Add(SmallLabel(Properties.Loc.S("Asr_PttKey")));

        var modRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        _cbCtrl  = new CheckBox { Content = Properties.Loc.S("Asr_PttCtrl"),  IsChecked = s.PushToTalkCtrl,  Margin = new Thickness(0,0,12,0) };
        _cbShift = new CheckBox { Content = Properties.Loc.S("Asr_PttShift"), IsChecked = s.PushToTalkShift, Margin = new Thickness(0,0,12,0) };
        _cbAlt   = new CheckBox { Content = Properties.Loc.S("Asr_PttAlt"),   IsChecked = s.PushToTalkAlt };
        foreach (var cb in new[] { _cbCtrl, _cbShift, _cbAlt })
            cb.SetResourceReference(CheckBox.ForegroundProperty, "ContentTextBrush");
        modRow.Children.Add(_cbCtrl);
        modRow.Children.Add(_cbShift);
        modRow.Children.Add(_cbAlt);
        _pttPanel.Children.Add(modRow);

        var keyRow = new StackPanel { Orientation = Orientation.Horizontal };
        _pttKeyLabel = new TextBlock
        {
            Text              = FormatKey(s.PushToTalkKey),
            FontFamily        = new FontFamily("Segoe UI"),
            FontSize          = 13, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 10, 0),
            MinWidth          = 80
        };
        _pttKeyLabel.SetResourceReference(ForegroundProperty, "ContentTextBrush");

        var recordBtn = MakeBtn(Properties.Loc.S("Asr_PttRecord"), false);
        recordBtn.FontSize = 11;
        recordBtn.Padding  = new Thickness(8, 4, 8, 4);
        recordBtn.Click   += (_, _) => StartKeyRecording(recordBtn);
        recordBtn.PreviewKeyDown += OnRecordKeyDown;
        recordBtn.LostFocus += (_, _) => _recordingKey = false;

        keyRow.Children.Add(_pttKeyLabel);
        keyRow.Children.Add(recordBtn);
        _pttPanel.Children.Add(keyRow);

        // ── Voice activated panel ──────────────────────────────────────────
        _voicePanel = new StackPanel { Margin = new Thickness(20, 6, 0, 16) };
        root.Children.Add(_voicePanel);

        _voicePanel.Children.Add(SmallLabel(Properties.Loc.S("Asr_MicLevel")));

        // Level meter canvas — 300×20 px, three-zone color bar + threshold marker
        _meterCanvas = new Canvas
        {
            Width  = double.NaN,
            Height = 24,
            Margin = new Thickness(0, 4, 0, 4),
            ClipToBounds = true
        };

        // Coloured background: red → green → yellow/orange gradient
        var bg = new Rectangle
        {
            Height = 20,
            VerticalAlignment = VerticalAlignment.Top,
            Fill = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromRgb(210, 50,  50),  0.00),
                    new GradientStop(Color.FromRgb(80,  200, 80),  0.30),
                    new GradientStop(Color.FromRgb(80,  200, 80),  0.65),
                    new GradientStop(Color.FromRgb(230, 180, 20),  0.80),
                    new GradientStop(Color.FromRgb(230, 80,  10),  1.00),
                },
                new Point(0, 0), new Point(1, 0))
        };
        Canvas.SetTop(bg, 2);
        _meterCanvas.Children.Add(bg);

        // Black mask overlay (fills from right, shrinks as level rises)
        _meterFill = new Rectangle
        {
            Height = 20,
            HorizontalAlignment = HorizontalAlignment.Right,
            Fill    = new SolidColorBrush(Color.FromArgb(200, 20, 20, 20)),
            Opacity = 0.85
        };
        Canvas.SetTop(_meterFill, 2);
        _meterCanvas.Children.Add(_meterFill);

        // Threshold line (draggable)
        _thresholdLine = new Line
        {
            Y1 = 0, Y2 = 24,
            StrokeThickness = 2,
            Stroke  = Brushes.White,
            Cursor  = Cursors.SizeWE
        };
        _meterCanvas.Children.Add(_thresholdLine);

        // Wire up size changes to resize bg and mask
        _meterCanvas.SizeChanged += (_, _) => PositionMeterChildren();

        // Drag threshold line
        bool dragging = false;
        _thresholdLine.MouseLeftButtonDown += (_, e) => { dragging = true; _thresholdLine.CaptureMouse(); e.Handled = true; };
        _thresholdLine.MouseMove += (_, e) =>
        {
            if (!dragging) return;
            var x = Math.Clamp(e.GetPosition(_meterCanvas).X, 0, _meterCanvas.ActualWidth);
            _threshold = (float)(x / _meterCanvas.ActualWidth);
            PositionMeterChildren();
        };
        _thresholdLine.MouseLeftButtonUp += (_, e) => { dragging = false; _thresholdLine.ReleaseMouseCapture(); };

        _voicePanel.Children.Add(_meterCanvas);

        var thintTb = new TextBlock
        {
            Text = Properties.Loc.S("Asr_ThresholdHint"),
            FontSize = 10, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        };
        thintTb.SetResourceReference(ForegroundProperty, "ContentDimBrush");
        _voicePanel.Children.Add(thintTb);

        // Show/hide panels based on radio selection
        void UpdatePanels()
        {
            _pttPanel.Visibility   = _rbPtt?.IsChecked   == true ? Visibility.Visible : Visibility.Collapsed;
            _voicePanel.Visibility = _rbVoice?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }
        _rbAlways.Checked += (_, _) => UpdatePanels();
        _rbPtt   .Checked += (_, _) => UpdatePanels();
        _rbVoice .Checked += (_, _) => UpdatePanels();
        UpdatePanels();

        // ── Buttons ────────────────────────────────────────────────────────
        var sep = new System.Windows.Shapes.Rectangle { Height = 1, Margin = new Thickness(0, 16, 0, 16) };
        sep.SetResourceReference(System.Windows.Shapes.Rectangle.FillProperty, "ControlBorderBrush");
        root.Children.Add(sep);

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancelBtn = MakeBtn(Properties.Loc.S("Btn_Cancel"), false);
        cancelBtn.Margin = new Thickness(0, 0, 8, 0);
        cancelBtn.Click += (_, _) => Close();

        var okBtn = MakeBtn(Properties.Loc.S("Btn_OK"), true);
        okBtn.Click += (_, _) => { SaveAndClose(); };

        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(okBtn);
        root.Children.Add(btnRow);
    }

    // ── Level meter update (called on dispatcher timer) ────────────────────

    private void UpdateMeter()
    {
        if (_meterCanvas is null || _meterFill is null) return;
        PositionMeterChildren();
    }

    private void PositionMeterChildren()
    {
        if (_meterCanvas is null) return;
        var w = _meterCanvas.ActualWidth;
        if (w <= 0) return;

        // Resize the gradient bg to full width
        foreach (var child in _meterCanvas.Children)
        {
            if (child is Rectangle r && r != _meterFill)
            {
                r.Width = w;
                Canvas.SetLeft(r, 0);
            }
        }

        // Position black mask: fills right side, width = (1 - level) * w
        if (_meterFill is not null)
        {
            var levelW = Math.Clamp(_currentLevel * 3.5f, 0f, 1f); // scale RMS (0.3 typical max) to 0-1
            _meterFill.Width = Math.Max(0, w * (1 - levelW));
            Canvas.SetLeft(_meterFill, w * levelW);
        }

        // Position threshold line
        if (_thresholdLine is not null)
        {
            var tx = Math.Clamp(_threshold * 3.5f, 0f, 1f) * w;
            Canvas.SetLeft(_thresholdLine, tx);
        }
    }

    // ── Key recording ──────────────────────────────────────────────────────

    private void StartKeyRecording(Button btn)
    {
        _recordingKey = true;
        btn.Focus();
    }

    private void OnRecordKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recordingKey) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        // Ignore pure modifier presses
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;

        if (_pttKeyLabel is not null)
            _pttKeyLabel.Text = FormatKey(key.ToString());

        _recordingKey = false;
        e.Handled = true;
    }

    private static string FormatKey(string keyName) => keyName switch
    {
        "Space"  => "Space",
        "Return" => "Enter",
        _        => keyName
    };

    // ── Model list refresh ─────────────────────────────────────────────────

    private void RefreshModelList(string folder)
    {
        if (_modelCombo is null) return;
        var prev = _modelCombo.Text;
        _modelCombo.Items.Clear();
        _modelCombo.Items.Add(Properties.Loc.S("Asr_NoModel"));

        if (Directory.Exists(folder))
        {
            foreach (var dir in Directory.GetDirectories(folder))
                _modelCombo.Items.Add(System.IO.Path.GetFileName(dir));
        }

        var s = SettingsService.Load();
        var match = _modelCombo.Items.Cast<string>()
            .FirstOrDefault(i => i == (string.IsNullOrEmpty(prev) ? s.AsrModelName : prev));
        _modelCombo.SelectedItem = match ?? _modelCombo.Items[0];
    }

    // ── Save ───────────────────────────────────────────────────────────────

    private void SaveAndClose()
    {
        var s = SettingsService.Load();

        s.AsrModelsFolder = _folderBox?.Text ?? "";
        s.AsrModelName    = _modelCombo?.SelectedIndex > 0 ? _modelCombo.SelectedItem?.ToString() ?? "" : "";
        s.AsrModelType    = _typeCombo?.SelectedIndex == 1 ? "sense_voice" : "whisper";

        if (_rbPtt?.IsChecked   == true) s.AsrActivationMode = "PushToTalk";
        else if (_rbVoice?.IsChecked == true) s.AsrActivationMode = "VoiceActivated";
        else s.AsrActivationMode = "AlwaysOn";

        s.PushToTalkKey   = _pttKeyLabel?.Text ?? "Space";
        s.PushToTalkCtrl  = _cbCtrl?.IsChecked  == true;
        s.PushToTalkShift = _cbShift?.IsChecked == true;
        s.PushToTalkAlt   = _cbAlt?.IsChecked   == true;
        s.VoiceActivationThreshold = _threshold;

        SettingsService.Save(s);
        Close();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private TextBlock Heading(string text)
    {
        var tb = new TextBlock
        {
            Text = text, FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        tb.SetResourceReference(ForegroundProperty, "ContentTextBrush");
        return tb;
    }

    private TextBlock SmallLabel(string text)
    {
        var tb = new TextBlock
        {
            Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
            Margin = new Thickness(0, 6, 0, 3)
        };
        tb.SetResourceReference(ForegroundProperty, "SidebarDimBrush");
        return tb;
    }

    private ComboBox MakeCombo()
    {
        var cb = new ComboBox { FontFamily = new FontFamily("Segoe UI"), FontSize = 12,
                                Margin = new Thickness(0, 0, 0, 10) };
        cb.SetResourceReference(ForegroundProperty,  "ContentTextBrush");
        cb.SetResourceReference(BackgroundProperty,  "ControlBgBrush");
        cb.SetResourceReference(BorderBrushProperty, "ControlBorderBrush");
        return cb;
    }

    private RadioButton MakeRadio(string label, string group)
    {
        var rb = new RadioButton
        {
            Content = label, GroupName = group,
            FontFamily = new FontFamily("Segoe UI"), FontSize = 12,
            Margin = new Thickness(0, 3, 0, 3)
        };
        rb.SetResourceReference(RadioButton.ForegroundProperty, "ContentTextBrush");
        return rb;
    }

    private Button MakeBtn(string label, bool primary)
    {
        var btn = new Button
        {
            Content = label, FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12, Padding = new Thickness(16, 7, 16, 7),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };
        if (primary)
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
