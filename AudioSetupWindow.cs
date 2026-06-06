using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private readonly List<(string Label, string Key)> _devices = [];
    private ComboBox? _deviceCombo;
    private Slider?   _volumeSlider;

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

        BuildUI();
    }

    private void BuildUI()
    {
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };
        Content  = root;

        var s = SettingsService.Load();

        // ── Output device ──────────────────────────────────────────────────
        root.Children.Add(SectionHeading("🔊  " + Properties.Loc.S("Audio_OutputDevice")));

        _devices.Add((Properties.Loc.S("Audio_DefaultDevice"), ""));
        for (int i = 0; i < WaveOut.DeviceCount; i++)
        {
            var name = WaveOut.GetCapabilities(i).ProductName;
            _devices.Add((name, name));
        }

        _deviceCombo = new ComboBox
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 12,
            Margin     = new Thickness(0, 0, 0, 20),
        };
        _deviceCombo.SetResourceReference(ForegroundProperty, "ContentTextBrush");
        _deviceCombo.SetResourceReference(BackgroundProperty, "ControlBgBrush");

        foreach (var (label, _) in _devices)
            _deviceCombo.Items.Add(label);

        var savedKey = s.AudioOutputDevice;
        var idx = string.IsNullOrEmpty(savedKey)
            ? 0
            : _devices.FindIndex(d => d.Key == savedKey);
        _deviceCombo.SelectedIndex = idx < 0 ? 0 : idx;

        root.Children.Add(_deviceCombo);

        // ── Volume ─────────────────────────────────────────────────────────
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

        var pctLabel = new TextBlock
        {
            Text              = "%",
            FontFamily        = new FontFamily("Segoe UI"),
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(4, 0, 0, 0),
        };
        pctLabel.SetResourceReference(ForegroundProperty, "ContentDimBrush");

        var volRow = new Grid { Margin = new Thickness(0, 0, 0, 24) };
        volRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        volRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        volRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_volumeSlider, 0);
        Grid.SetColumn(volTb,         1);
        Grid.SetColumn(pctLabel,      2);
        volRow.Children.Add(_volumeSlider);
        volRow.Children.Add(volTb);
        volRow.Children.Add(pctLabel);
        root.Children.Add(volRow);

        // Live two-way binding between slider and text box
        bool updating = false;
        _volumeSlider.ValueChanged += (_, _) =>
        {
            if (updating) return;
            updating = true;
            volTb.Text = $"{(int)_volumeSlider.Value}";
            updating   = false;
            VoiceOutputService.Volume = (float)_volumeSlider.Value / 100f;
        };
        volTb.TextChanged += (_, _) =>
        {
            if (updating) return;
            if (!int.TryParse(volTb.Text, out var v) || v is < 0 or > 100) return;
            updating               = true;
            _volumeSlider.Value    = v;
            updating               = false;
            VoiceOutputService.Volume = v / 100f;
        };

        // ── Close ──────────────────────────────────────────────────────────
        var closeBtn = MakeBtn(Properties.Loc.S("Btn_Close"), isPrimary: true);
        closeBtn.Click += (_, _) => { SaveSettings(); DialogResult = true; };
        root.Children.Add(closeBtn);
    }

    private void SaveSettings()
    {
        var s = SettingsService.Load();

        if (_deviceCombo is not null)
        {
            var i = _deviceCombo.SelectedIndex;
            s.AudioOutputDevice = i >= 0 && i < _devices.Count ? _devices[i].Key : "";
            VoiceOutputService.DeviceNumber = FindDeviceNumber(s.AudioOutputDevice);
        }

        if (_volumeSlider is not null)
        {
            s.AudioVolume = _volumeSlider.Value / 100.0;
            VoiceOutputService.Volume = (float)s.AudioVolume;
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

    // ── Helpers ────────────────────────────────────────────────────────────

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
