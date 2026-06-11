using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using ClaudetRelay.Services;

namespace ClaudetRelay;

/// <summary>
/// Combined Audio &amp; Voice settings window — three tabs:
///   Tab 1: Audio Setup   (device, volume, mic boost)
///   Tab 2: Voice Output  (TTS backend, Sherpa folder, VOICEVOX port)
///   Tab 3: Voice Recognition (ASR model, activation mode, silence delay)
/// </summary>
public sealed class AudioAndVoiceSettingsWindow : Window
{
    private readonly AudioSetupWindow              _audioWin;
    private readonly VoiceSettingsWindow           _voiceWin;
    private readonly VoiceRecognitionSettingsWindow _asrWin;

    public AudioAndVoiceSettingsWindow(
        string?          themePath,
        DictationService dictation,
        Action<Window>?  applyTheme = null,
        int              initialTab = 0)
    {
        Title                 = Properties.Loc.S("Menu_AudioAndVoice");
        Width                 = 560;
        SizeToContent         = SizeToContent.Height;
        MaxHeight             = 860;
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

        // Build child windows (no Show — we only use their content + save methods)
        _audioWin = new AudioSetupWindow(themePath);
        _voiceWin = new VoiceSettingsWindow(themePath);
        _asrWin   = new VoiceRecognitionSettingsWindow(themePath, dictation);

        // Share this window's resources with child instances so brushes resolve
        _audioWin.Resources.MergedDictionaries.Clear();
        _voiceWin.Resources.MergedDictionaries.Clear();
        _asrWin  .Resources.MergedDictionaries.Clear();
        foreach (var d in Resources.MergedDictionaries)
        {
            _audioWin.Resources.MergedDictionaries.Add(d);
            _voiceWin.Resources.MergedDictionaries.Add(d);
            _asrWin  .Resources.MergedDictionaries.Add(d);
        }

        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var tabs = new TabControl { Margin = new Thickness(0) };
        ApplyTabStyle(tabs);

        // ── Tab 1: Audio Setup ────────────────────────────────────────────
        var audioScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(20, 16, 20, 16)
        };
        audioScroll.Content = _audioWin.BuildTabContent();
        tabs.Items.Add(MakeTab(Properties.Loc.S("AudioVoice_TabAudio"), audioScroll));

        // ── Tab 2: Voice Output ───────────────────────────────────────────
        var voiceScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(20, 16, 20, 16)
        };
        voiceScroll.Content = _voiceWin.BuildTabContent();
        tabs.Items.Add(MakeTab(Properties.Loc.S("AudioVoice_TabVoice"), voiceScroll));

        // ── Tab 3: Voice Recognition ──────────────────────────────────────
        var asrScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(20, 16, 20, 16)
        };
        asrScroll.Content = _asrWin.BuildTabContent();
        tabs.Items.Add(MakeTab(Properties.Loc.S("AudioVoice_TabAsr"), asrScroll));

        tabs.SelectedIndex = Math.Clamp(initialTab, 0, 2);

        Grid.SetRow(tabs, 0);
        outer.Children.Add(tabs);

        // ── OK / Cancel ───────────────────────────────────────────────────
        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(20, 10, 20, 16)
        };
        var cancelBtn = MakeBtn(Properties.Loc.S("Btn_Cancel"), false);
        cancelBtn.Margin = new Thickness(0, 0, 8, 0);
        cancelBtn.Click += (_, _) => { DialogResult = false; Close(); };

        var okBtn = MakeBtn(Properties.Loc.S("Btn_OK"), true);
        okBtn.Click += (_, _) =>
        {
            _audioWin.Save();
            _voiceWin.Save();
            _asrWin  .Save();
            DialogResult = true;
            Close();
        };

        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(okBtn);
        Grid.SetRow(btnRow, 1);
        outer.Children.Add(btnRow);

        Content = outer;
        UiZoomHelper.Apply(this, UiZoomHelper.FromSettings());

        // The ASR window subscribes to LevelChanged and starts its timer in its constructor.
        // Since we never Show() it, its Closed event never fires — clean up manually.
        Closed += (_, _) =>
        {
            _asrWin.StopMeterTimer();
            _asrWin.DetachFromDictation();
        };
    }

    // ── Tab factory ────────────────────────────────────────────────────────

    private static TabItem MakeTab(string header, UIElement content)
    {
        var tb = new TextBlock
        {
            Text       = header,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize   = 12,
            Padding    = new Thickness(6, 2, 6, 2)
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        return new TabItem { Header = tb, Content = content };
    }

    private void ApplyTabStyle(TabControl tabs)
    {
        const string xaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style x:Key='CombinedTabControl' TargetType='TabControl'>
    <Setter Property='Background'       Value='{DynamicResource ContentBgBrush}'/>
    <Setter Property='BorderThickness'  Value='0'/>
    <Setter Property='Padding'          Value='0'/>
  </Style>
  <Style x:Key='CombinedTabItem' TargetType='TabItem'>
    <Setter Property='Background'  Value='{DynamicResource ControlBgBrush}'/>
    <Setter Property='Foreground'  Value='{DynamicResource ContentTextBrush}'/>
    <Setter Property='BorderThickness' Value='0'/>
    <Setter Property='Padding'     Value='10,6,10,6'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='TabItem'>
          <Border x:Name='Bd' Background='{TemplateBinding Background}'
                  BorderThickness='0' CornerRadius='4,4,0,0'
                  Margin='2,4,2,0' Padding='{TemplateBinding Padding}'>
            <ContentPresenter ContentSource='Header'
                              HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsSelected' Value='True'>
              <Setter TargetName='Bd' Property='Background'
                      Value='{DynamicResource ContentBgBrush}'/>
            </Trigger>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='Bd' Property='Background'
                      Value='{DynamicResource ControlHoverBrush}'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";
        try
        {
            var rd = (ResourceDictionary)XamlReader.Parse(xaml);
            tabs.Style              = (Style)rd["CombinedTabControl"];
            tabs.ItemContainerStyle = (Style)rd["CombinedTabItem"];
        }
        catch { }
    }

    // ── Button helper ──────────────────────────────────────────────────────

    private Button MakeBtn(string label, bool isPrimary)
    {
        var btn = new Button
        {
            Content   = label,
            MinWidth  = 88,
            Height    = 34,
            Padding   = new Thickness(16, 0, 16, 0),
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize  = 12,
            Cursor    = System.Windows.Input.Cursors.Hand
        };
        if (TryFindResource("ModernButton") is Style s) btn.Style = s;
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
        return btn;
    }
}
