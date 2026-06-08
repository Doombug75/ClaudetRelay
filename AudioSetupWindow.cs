using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClaudetRelay.Services;
using NAudio.Wave;

namespace ClaudetRelay;

/// <summary>
/// Hardware audio configuration: output device selector and master volume slider.
/// Changes apply immediately (live preview) and are saved on close.
/// </summary>
public sealed class AudioSetupWindow : Window
{
    private readonly string? _themePath;
    private readonly List<(string Label, string Key)> _outDevices = [];
    private readonly List<(string Label, string Key)> _inDevices  = [];
    private ComboBox?   _deviceCombo;
    private ComboBox?   _inputCombo;
    private Slider?     _volumeSlider;
    private Slider?     _boostSlider;

    // Mic test
    private Canvas?     _micCanvas;
    private Rectangle?  _micFill;
    private WaveInEvent?     _testWaveIn;
    private DispatcherTimer? _micTimer;
    private float            _micLevel;
    private readonly List<byte> _recBuf = [];
    private bool        _recActive;
    private CancellationTokenSource? _recCts;

    public AudioSetupWindow(string? themePath)
    {
        _themePath = themePath;

        Title                 = Properties.Loc.S("Audio_AudioSetup");
        Width                 = 480;
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

        OverrideSystemColorsForCombos();
        BuildUI();
        UiZoomHelper.Apply(this, UiZoomHelper.FromSettings());
    }

    /// <summary>
    /// Overrides WPF SystemColor brush keys at the window level so the default
    /// ComboBox template (which uses SystemColors.Window, not Background) picks
    /// up the current theme colours.
    /// </summary>
    private void OverrideSystemColorsForCombos()
    {
        if (TryFindResource("ControlBgBrush")    is Brush bg)
        {
            Resources[SystemColors.WindowBrushKey]       = bg;
            Resources[SystemColors.ControlBrushKey]      = bg;
            Resources[SystemColors.ControlLightBrushKey] = bg;
        }
        if (TryFindResource("ContentTextBrush")  is Brush fg)
        {
            Resources[SystemColors.WindowTextBrushKey]   = fg;
            Resources[SystemColors.ControlTextBrushKey]  = fg;
        }
        if (TryFindResource("ControlBorderBrush") is Brush border)
            Resources[SystemColors.ActiveBorderBrushKey] = border;
    }

    private void BuildUI()
    {
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };
        Content  = root;

        var s = SettingsService.Load();

        // ══════════════════════════════════════════════════════════════════
        // OUTPUT DEVICE
        // ══════════════════════════════════════════════════════════════════
        root.Children.Add(SectionHeading("🔊  " + Properties.Loc.S("Audio_OutputDevice")));

        _outDevices.Add((Properties.Loc.S("Audio_DefaultDevice"), ""));
        for (int i = 0; i < WaveOut.DeviceCount; i++)
        {
            var name = WaveOut.GetCapabilities(i).ProductName;
            _outDevices.Add((name, name));
        }

        _deviceCombo = MakeCombo();
        foreach (var (label, _) in _outDevices)
            _deviceCombo.Items.Add(label);

        var savedOut = s.AudioOutputDevice;
        var outIdx   = string.IsNullOrEmpty(savedOut) ? 0
                       : _outDevices.FindIndex(d => d.Key == savedOut);
        _deviceCombo.SelectedIndex = outIdx < 0 ? 0 : outIdx;
        root.Children.Add(_deviceCombo);

        // ── Output volume ──────────────────────────────────────────────────
        root.Children.Add(SectionHeading("🔉  " + Properties.Loc.S("Audio_Volume")));

        var volPct = (int)Math.Clamp(s.AudioVolume * 100, 0, 100);

        var volTb = new TextBox
        {
            Text            = $"{volPct}",
            Width           = 46,
            FontFamily      = new FontFamily("Segoe UI"),
            FontSize        = 12,
            TextAlignment   = TextAlignment.Center,
            Padding         = new Thickness(4),
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        volTb.SetResourceReference(ForegroundProperty, "InputTextBrush");
        volTb.SetResourceReference(BackgroundProperty, "ControlBgBrush");
        volTb.SetResourceReference(BorderBrushProperty, "ControlBorderBrush");

        _volumeSlider = new Slider
        {
            Minimum             = 0,
            Maximum             = 100,
            Value               = volPct,
            TickFrequency       = 10,
            IsSnapToTickEnabled = false,
            VerticalAlignment   = VerticalAlignment.Center,
        };

        var volPctLbl = new TextBlock
        {
            Text              = "%",
            FontFamily        = new FontFamily("Segoe UI"),
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(4, 0, 0, 0),
        };
        volPctLbl.SetResourceReference(ForegroundProperty, "ContentDimBrush");

        var volRow = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        volRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        volRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        volRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_volumeSlider, 0);
        Grid.SetColumn(volTb,         1);
        Grid.SetColumn(volPctLbl,     2);
        volRow.Children.Add(_volumeSlider);
        volRow.Children.Add(volTb);
        volRow.Children.Add(volPctLbl);
        root.Children.Add(volRow);

        bool volUpdating = false;
        _volumeSlider.ValueChanged += (_, _) =>
        {
            if (volUpdating) return;
            volUpdating = true;
            volTb.Text  = $"{(int)_volumeSlider.Value}";
            volUpdating = false;
            VoiceOutputService.Volume = ToAudioGain(_volumeSlider.Value, 100.0);
        };
        volTb.TextChanged += (_, _) =>
        {
            if (volUpdating) return;
            if (!int.TryParse(volTb.Text, out var v) || v is < 0 or > 100) return;
            volUpdating            = true;
            _volumeSlider.Value    = v;
            volUpdating            = false;
            VoiceOutputService.Volume = ToAudioGain(v, 100.0);
        };

        // ── Speaker test ───────────────────────────────────────────────────
        var speakerTestBtn = MakeBtn(Properties.Loc.S("Audio_TestSpeaker"));
        speakerTestBtn.HorizontalAlignment = HorizontalAlignment.Left;
        speakerTestBtn.Margin = new Thickness(0, 0, 0, 24);
        root.Children.Add(speakerTestBtn);

        speakerTestBtn.Click += (_, _) =>
        {
            var outDevIdx = _deviceCombo?.SelectedIndex ?? 0;
            var outDevNum = (outDevIdx > 0 && outDevIdx - 1 < WaveOut.DeviceCount)
                                ? outDevIdx - 1 : 0;
            var vol = ToAudioGain(_volumeSlider?.Value ?? 80.0, 100.0);

            Task.Run(() =>
            {
                const int sr = 44100;

                // Imperial March — main theme, two phrases (~104 BPM)
                // Speedy march feel: GGG/DDD/Gb/G = quarter (q)
                // Slow/cinematic feel: those same notes doubled (2q)
                // Eb–Bb pair is always quarter + eighth (q + e)
                //   q=577ms  e=288ms  h=1154ms
                const int q  = 577;   // quarter
                const int e  = 288;   // eighth
                const int h  = 1154;  // half
                // swap these two lines to switch between fast and slow version:
                const int gq = q;     // fast: q  |  slow: q*2
                // (frequency Hz, duration ms)
                (float freq, int ms)[] notes =
                [
                    (392.0f, gq), (392.0f, gq), (392.0f, gq),    // G   G   G
                    (311.1f,   q), (466.2f,   e),                  // Eb  Bb/8
                    (392.0f, gq), (311.1f,   q), (466.2f,   e),   // G   Eb  Bb/8
                    (392.0f,   h),                                  // G   (half)

                    (587.3f, gq), (587.3f, gq), (587.3f, gq),    // D'  D'  D'
                    (622.3f,   q), (466.2f,   e),                  // Eb' Bb/8
                    (370.0f, gq), (311.1f,   q), (466.2f,   e),   // Gb  Eb  Bb/8
                    (392.0f,   h),                                  // G   (half)
                ];

                // Build PCM: each note gets a 30 ms silent gap for articulation
                const int gapMs    = 30;
                const int attackMs = 8;
                var allSamples = new System.Collections.Generic.List<short>(sr * 12);

                foreach (var (freq, durMs) in notes)
                {
                    int body = Math.Max(1, (int)(sr * (durMs - gapMs) / 1000.0));
                    int gap  = (int)(sr * gapMs / 1000.0);
                    int atk  = sr * attackMs / 1000;

                    for (int i = 0; i < body; i++)
                    {
                        float env = i < atk
                            ? (float)i / atk
                            : i > body * 0.88f
                                ? 1f - (i - body * 0.88f) / (body * 0.12f)
                                : 1f;
                        float s = MathF.Sin(2f * MathF.PI * freq * i / sr) * vol * env;
                        allSamples.Add((short)(Math.Clamp(s, -1f, 1f) * 32767f));
                    }
                    for (int i = 0; i < gap; i++)
                        allSamples.Add(0);
                }

                var buf = new byte[allSamples.Count * 2];
                for (int i = 0; i < allSamples.Count; i++)
                {
                    buf[i * 2]     = (byte)( allSamples[i]        & 0xFF);
                    buf[i * 2 + 1] = (byte)((allSamples[i] >> 8)  & 0xFF);
                }

                using var ms  = new MemoryStream(buf);
                using var raw = new RawSourceWaveStream(ms, new WaveFormat(sr, 16, 1));
                using var wo  = new WaveOutEvent { DeviceNumber = outDevNum };
                wo.Init(raw);
                wo.Play();
                while (wo.PlaybackState == PlaybackState.Playing)
                    Thread.Sleep(20);
            });
        };

        // ══════════════════════════════════════════════════════════════════
        // INPUT DEVICE / MICROPHONE
        // ══════════════════════════════════════════════════════════════════
        root.Children.Add(new Separator { Margin = new Thickness(0, 0, 0, 16) });
        root.Children.Add(SectionHeading("🎙  " + Properties.Loc.S("Audio_InputDevice")));

        _inDevices.Add((Properties.Loc.S("Audio_DefaultDevice"), ""));
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var name = WaveInEvent.GetCapabilities(i).ProductName;
            _inDevices.Add((name, name));
        }

        _inputCombo = MakeCombo();
        foreach (var (label, _) in _inDevices)
            _inputCombo.Items.Add(label);

        var savedIn = s.AudioInputDevice;
        var inIdx   = string.IsNullOrEmpty(savedIn) ? 0
                      : _inDevices.FindIndex(d => d.Key == savedIn);
        _inputCombo.SelectedIndex = inIdx < 0 ? 0 : inIdx;
        root.Children.Add(_inputCombo);

        // ── Microphone boost ───────────────────────────────────────────────
        root.Children.Add(SectionHeading("🎚  " + Properties.Loc.S("Audio_MicBoost")));

        var boostPct = Math.Clamp(s.AudioInputBoost, 0, 300);
        var boostTb  = new TextBox
        {
            Text            = $"{boostPct}",
            Width           = 46,
            FontFamily      = new FontFamily("Segoe UI"),
            FontSize        = 12,
            TextAlignment   = TextAlignment.Center,
            Padding         = new Thickness(4),
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        boostTb.SetResourceReference(ForegroundProperty, "InputTextBrush");
        boostTb.SetResourceReference(BackgroundProperty, "ControlBgBrush");
        boostTb.SetResourceReference(BorderBrushProperty, "ControlBorderBrush");

        _boostSlider = new Slider
        {
            Minimum             = 0,
            Maximum             = 300,
            Value               = boostPct,
            TickFrequency       = 50,
            IsSnapToTickEnabled = false,
            VerticalAlignment   = VerticalAlignment.Center,
        };

        var boostPctLbl = new TextBlock
        {
            Text              = "%",
            FontFamily        = new FontFamily("Segoe UI"),
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(4, 0, 0, 0),
        };
        boostPctLbl.SetResourceReference(ForegroundProperty, "ContentDimBrush");

        var boostRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        boostRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        boostRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        boostRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_boostSlider,  0);
        Grid.SetColumn(boostTb,       1);
        Grid.SetColumn(boostPctLbl,   2);
        boostRow.Children.Add(_boostSlider);
        boostRow.Children.Add(boostTb);
        boostRow.Children.Add(boostPctLbl);
        root.Children.Add(boostRow);

        var boostHint = new TextBlock
        {
            Text         = Properties.Loc.S("Audio_MicBoostHint"),
            FontFamily   = new FontFamily("Segoe UI"),
            FontSize     = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 2, 0, 14),
        };
        boostHint.SetResourceReference(ForegroundProperty, "ContentDimBrush");
        root.Children.Add(boostHint);

        bool boostUpdating = false;
        _boostSlider.ValueChanged += (_, _) =>
        {
            if (boostUpdating) return;
            boostUpdating = true;
            boostTb.Text  = $"{(int)_boostSlider.Value}";
            boostUpdating = false;
        };
        boostTb.TextChanged += (_, _) =>
        {
            if (boostUpdating) return;
            if (!int.TryParse(boostTb.Text, out var v) || v is < 0 or > 300) return;
            boostUpdating      = true;
            _boostSlider.Value = v;
            boostUpdating      = false;
        };

        // ── Mic level meter ────────────────────────────────────────────────
        _micCanvas = new Canvas
        {
            Height       = 14,
            Margin       = new Thickness(0, 0, 0, 10),
            ClipToBounds = true,
        };
        _micCanvas.SetResourceReference(Canvas.BackgroundProperty, "ControlBgBrush");

        _micFill = new Rectangle
        {
            Height = 14,
            Width  = 0,
            Fill   = new LinearGradientBrush(
                         new GradientStopCollection
                         {
                             new GradientStop(Colors.LimeGreen,  0.00),
                             new GradientStop(Colors.YellowGreen,0.60),
                             new GradientStop(Colors.Orange,     0.85),
                             new GradientStop(Colors.Red,        1.00),
                         },
                         startPoint: new System.Windows.Point(0, 0),
                         endPoint:   new System.Windows.Point(1, 0)),
        };
        Canvas.SetLeft(_micFill, 0);
        Canvas.SetTop (_micFill, 0);
        _micCanvas.Children.Add(_micFill);
        _micCanvas.SizeChanged += (_, _) =>
        {
            if (_micFill is not null)
                _micFill.Width = _micCanvas.ActualWidth * _micLevel;
        };
        root.Children.Add(_micCanvas);

        // ── Mic test status label ──────────────────────────────────────────
        var testStatusLbl = new TextBlock
        {
            Text         = "",
            FontFamily   = new FontFamily("Segoe UI"),
            FontSize     = 11,
            Margin       = new Thickness(0, 0, 0, 8),
            Visibility   = Visibility.Collapsed,
        };
        testStatusLbl.SetResourceReference(ForegroundProperty, "PrimaryAccentBrush");
        root.Children.Add(testStatusLbl);

        // ── Mic test button ────────────────────────────────────────────────
        var micTestBtn = MakeBtn(Properties.Loc.S("Audio_TestMicStart"));
        micTestBtn.HorizontalAlignment = HorizontalAlignment.Left;
        micTestBtn.Margin = new Thickness(0, 0, 0, 6);
        root.Children.Add(micTestBtn);

        var testHint = new TextBlock
        {
            Text         = Properties.Loc.S("Audio_TestHint"),
            FontFamily   = new FontFamily("Segoe UI"),
            FontSize     = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 4, 0, 20),
        };
        testHint.SetResourceReference(ForegroundProperty, "ContentDimBrush");
        root.Children.Add(testHint);

        // Level meter timer — decays _micLevel each tick for smooth fall-off,
        // then maps through sqrt curve so quiet signals are still visible
        _micTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _micTimer.Tick += (_, _) =>
        {
            _micLevel *= 0.75f;   // decay when no fresh audio
            if (_micFill is not null && _micCanvas is not null)
                _micFill.Width = _micCanvas.ActualWidth * ToMeterDisplay(_micLevel);
        };
        _micTimer.Start();

        // ── Mic test click logic ───────────────────────────────────────────
        micTestBtn.Click += async (_, _) =>
        {
            if (_recActive)
            {
                StopMicTest();
                micTestBtn.Content       = Properties.Loc.S("Audio_TestMicStart");
                testStatusLbl.Text       = Properties.Loc.S("Audio_TestPlaying");
                testStatusLbl.Visibility = Visibility.Visible;

                await PlaybackRecordingAsync();

                testStatusLbl.Visibility = Visibility.Collapsed;
            }
            else
            {
                _recBuf.Clear();
                _recActive = true;
                _recCts    = new CancellationTokenSource();
                micTestBtn.Content       = Properties.Loc.S("Audio_TestMicStop");
                testStatusLbl.Text       = Properties.Loc.S("Audio_TestRecording");
                testStatusLbl.Visibility = Visibility.Visible;

                var inDevIdx = _inputCombo?.SelectedIndex ?? 0;
                var inDevNum = (inDevIdx > 0 && inDevIdx - 1 < WaveInEvent.DeviceCount)
                                   ? inDevIdx - 1 : 0;
                var boostVal = MathF.Pow((float)(_boostSlider?.Value ?? 100.0) / 100f, 2f);

                _testWaveIn = new WaveInEvent
                {
                    DeviceNumber       = inDevNum,
                    WaveFormat         = new WaveFormat(44100, 16, 1),
                    BufferMilliseconds = 50,
                };
                _testWaveIn.DataAvailable += (_, e) =>
                {
                    // Compute peak per buffer (fixes per-sample decay collapsing to zero)
                    float peak = 0f;
                    for (int i = 0; i < e.BytesRecorded; i += 2)
                    {
                        short sample  = MemoryMarshal.Read<short>(e.Buffer.AsSpan(i));
                        float f       = Math.Clamp(sample / 32768f * boostVal, -1f, 1f);
                        short boosted = (short)(f * 32767f);
                        _recBuf.Add((byte)(boosted & 0xFF));
                        _recBuf.Add((byte)((boosted >> 8) & 0xFF));
                        if (MathF.Abs(f) > peak) peak = MathF.Abs(f);
                    }
                    // Smooth hold: only raise level, let timer decay it
                    if (peak > _micLevel) _micLevel = peak;
                };

                _testWaveIn.StartRecording();

                // Auto-stop after 20 s
                var cts = _recCts;
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(20_000, cts.Token); }
                    catch (OperationCanceledException) { return; }
                    Dispatcher.Invoke(() => micTestBtn.RaiseEvent(
                        new System.Windows.RoutedEventArgs(Button.ClickEvent)));
                });
            }
        };

        // ── Close ──────────────────────────────────────────────────────────
        var closeBtn = MakeBtn(Properties.Loc.S("Btn_Close"), isPrimary: true);
        closeBtn.Click += (_, _) => { StopMicTest(); SaveSettings(); DialogResult = true; };
        root.Children.Add(closeBtn);

        Closing += (_, _) => { _micTimer?.Stop(); StopMicTest(); };
    }

    // ── Mic test helpers ───────────────────────────────────────────────────

    private void StopMicTest()
    {
        _recCts?.Cancel();
        _recCts?.Dispose();
        _recCts = null;

        if (_testWaveIn is not null)
        {
            try { _testWaveIn.StopRecording(); } catch { }
            _testWaveIn.Dispose();
            _testWaveIn = null;
        }

        _recActive = false;
        _micLevel  = 0f;
    }

    private async Task PlaybackRecordingAsync()
    {
        if (_recBuf.Count == 0) return;

        var data      = _recBuf.ToArray();
        _recBuf.Clear();

        var outIdx    = _deviceCombo?.SelectedIndex ?? 0;
        var outDevNum = (outIdx > 0 && outIdx - 1 < WaveOut.DeviceCount)
                            ? outIdx - 1 : 0;
        var vol       = ToAudioGain(_volumeSlider?.Value ?? 80.0, 100.0);

        await Task.Run(() =>
        {
            using var ms  = new MemoryStream(data);
            using var raw = new RawSourceWaveStream(ms, new WaveFormat(44100, 16, 1));
            using var wo  = new WaveOutEvent { DeviceNumber = outDevNum, Volume = vol };
            wo.Init(raw);
            wo.Play();
            while (wo.PlaybackState == PlaybackState.Playing)
                Thread.Sleep(20);
        });
    }

    private void SaveSettings()
    {
        var s = SettingsService.Load();

        if (_deviceCombo is not null)
        {
            var i = _deviceCombo.SelectedIndex;
            s.AudioOutputDevice = i >= 0 && i < _outDevices.Count ? _outDevices[i].Key : "";
            VoiceOutputService.DeviceNumber = FindDeviceNumber(s.AudioOutputDevice);
        }

        if (_inputCombo is not null)
        {
            var i = _inputCombo.SelectedIndex;
            s.AudioInputDevice = i >= 0 && i < _inDevices.Count ? _inDevices[i].Key : "";
        }

        if (_volumeSlider is not null)
        {
            s.AudioVolume = _volumeSlider.Value / 100.0;
            VoiceOutputService.Volume = (float)s.AudioVolume;
        }

        if (_boostSlider is not null)
        {
            s.AudioInputBoost = (int)Math.Clamp(_boostSlider.Value, 0, 300);
            // Apply the same quadratic curve to the live DictationService instance
            // (MainWindow will re-apply on next load; this covers the current session)
            if (Application.Current?.MainWindow is MainWindow mw)
                mw.ApplyMicBoost(ToAudioGain(_boostSlider.Value, 300.0));
        }

        SettingsService.Save(s);
    }

    /// <summary>
    /// Maps a saved device name back to its NAudio device index.
    /// Returns 0 (OS default) if the device is not found or the name is empty.
    /// </summary>
    internal static int FindDeviceNumber(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName)) return 0;
        for (int i = 0; i < WaveOut.DeviceCount; i++)
            if (WaveOut.GetCapabilities(i).ProductName == deviceName)
                return i;
        return 0;
    }

    // ── Audio curve helpers ────────────────────────────────────────────────

    /// <summary>
    /// Maps a linear slider value (0-100) to a 0-1 audio gain using a quadratic
    /// curve so equal slider movement = equal perceived loudness change.
    /// </summary>
    private static float ToAudioGain(double value, double max)
        => MathF.Pow((float)(value / max), 2f) * (float)(max / 100.0);

    /// <summary>
    /// Maps a saved AudioInputBoost integer (0-300, where 100 = unity gain = 1.0×)
    /// through a quadratic curve centred on 100.
    /// 100 → 1.0×,  200 → 4.0×,  300 → 9.0×,  50 → 0.25×
    /// </summary>
    public static float QuadraticBoost(int sliderValue)
        => MathF.Pow(Math.Clamp(sliderValue, 0, 300) / 100f, 2f);

    /// <summary>
    /// Maps a raw linear RMS/peak level (0-1) to a display width fraction using a
    /// square-root curve — spreads quiet signals across more of the bar so they're
    /// visible instead of barely flickering at the left edge.
    /// </summary>
    private static float ToMeterDisplay(float linearLevel)
        => MathF.Sqrt(Math.Clamp(linearLevel, 0f, 1f));

    // ── Helpers ────────────────────────────────────────────────────────────

    private ComboBox MakeCombo()
    {
        var cb = new ComboBox { Margin = new Thickness(0, 0, 0, 20) };
        // Use the app-wide ModernComboBox style which has a proper themed template
        cb.SetResourceReference(StyleProperty, "ModernComboBox");
        return cb;
    }

    private TextBlock SectionHeading(string text)
    {
        var tb = new TextBlock
        {
            Text       = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 12,
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, 0, 0, 8),
        };
        tb.SetResourceReference(ForegroundProperty, "ContentTextBrush");
        return tb;
    }

    private Button MakeBtn(string label, bool isPrimary = false)
    {
        var btn = new Button
        {
            Content             = label,
            FontFamily          = new FontFamily("Segoe UI"),
            FontSize            = 12,
            Padding             = new Thickness(16, 7, 16, 7),
            BorderThickness     = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            Cursor              = System.Windows.Input.Cursors.Hand,
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
