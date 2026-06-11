using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using ClaudetRelay.Services;

namespace ClaudetRelay;

/// <summary>
/// Web Access Settings window — lets the user control download permissions,
/// file-extension allow/ask lists, per-domain whitelist, and fetch parameters.
/// Themed, zoom-aware, title-bar-aware.  All labels are fully localised (EN/DE).
/// </summary>
public sealed class WebAccessSettingsWindow : Window
{
    // ── State ──────────────────────────────────────────────────────────────
    private readonly string? _themePath;

    // Download section
    private CheckBox? _allowDownloadsChk;
    private TextBox?  _autoExtBox;
    private TextBox?  _askExtBox;

    // Fetch settings
    private TextBox? _timeoutBox;
    private TextBox? _maxCloudBox;
    private TextBox? _maxLocalBox;

    // Whitelist
    private List<WebWhitelistEntry> _whitelist = [];
    private ListView?               _whitelistView;
    private TextBox?                _newDomainBox;

    // File Reading tab
    private TextBox? _codeExtBox;

    // ── Localisation helpers ───────────────────────────────────────────────
    private static string L(string key) => Properties.Loc.S(key);

    private static readonly string DefaultAutoExt = ".txt;.md;.pdf;.readme";
    private static readonly string DefaultAskExt  =
        ".docx;.doc;.xlsx;.xls;.pptx;.ppt;.odt;.ods;.odp;.rtf";

    // ── Constructor ────────────────────────────────────────────────────────
    public WebAccessSettingsWindow(string? themePath)
    {
        _themePath = themePath;

        Title                 = L("WebAccess_WindowTitle");
        Width                 = 560;
        MinWidth              = 420;
        Height                = 680;
        MinHeight             = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode            = ResizeMode.CanResizeWithGrip;
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

        var s = SettingsService.Load();
        _whitelist = s.WebBrowsing.Whitelist.Select(e => new WebWhitelistEntry
        {
            Domain        = e.Domain,
            IsEnabled     = e.IsEnabled,
            AllowDownloads = e.AllowDownloads,
        }).ToList();

        BuildUI(s.WebBrowsing, s.CodeFileExtensions);
        UiZoomHelper.Apply(this, UiZoomHelper.FromSettings());
    }

    // ── UI ─────────────────────────────────────────────────────────────────
    private void BuildUI(WebBrowsingSettings wb, string codeExts)
    {
        // ── Outer grid: tabs + close button ───────────────────────────────
        var outer = new Grid { Margin = new Thickness(0) };
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = outer;

        var tabs = new TabControl
        {
            BorderThickness = new Thickness(0),
            Padding         = new Thickness(0),
        };
        tabs.SetResourceReference(BackgroundProperty, "ContentBgBrush");
        ApplyTabStyle(tabs);
        Grid.SetRow(tabs, 0);
        outer.Children.Add(tabs);

        // ── Tab 1: Web & Downloads ─────────────────────────────────────────
        tabs.Items.Add(BuildWebTab(wb));

        // ── Tab 2: File Reading ────────────────────────────────────────────
        tabs.Items.Add(BuildFileReadingTab(codeExts));

        // ── Close / Save strip ─────────────────────────────────────────────
        var btnRow = new Border
        {
            Padding         = new Thickness(16, 10, 16, 12),
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        btnRow.SetResourceReference(BorderBrushProperty, "ControlBorderBrush");
        Grid.SetRow(btnRow, 1);
        outer.Children.Add(btnRow);

        var closeBtn = MakeBtn(L("Btn_Close"), isPrimary: true);
        closeBtn.HorizontalAlignment = HorizontalAlignment.Right;
        closeBtn.Click += (_, _) => { SaveSettings(); DialogResult = true; };
        btnRow.Child = closeBtn;
    }

    private TabItem BuildWebTab(WebBrowsingSettings wb)
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var root = new StackPanel { Margin = new Thickness(24, 16, 24, 16) };
        scroll.Content = root;

        // Downloads section
        root.Children.Add(SectionHeading("⬇  " + L("WebAccess_SectionDownloads")));

        _allowDownloadsChk = new CheckBox
        {
            Content    = L("WebAccess_AllowDownloads"),
            IsChecked  = wb.AllowDownloads,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 12,
            Margin     = new Thickness(0, 8, 0, 2),
        };
        _allowDownloadsChk.SetResourceReference(ForegroundProperty, "ContentTextBrush");
        root.Children.Add(_allowDownloadsChk);
        root.Children.Add(HintText(L("WebAccess_AllowDownloadsHint")));

        root.Children.Add(FieldLabel(L("WebAccess_AutoAllowLabel"), topMargin: 14));
        root.Children.Add(ExtRow(
            wb.AutoDownloadExtensions.Count > 0 ? string.Join(";", wb.AutoDownloadExtensions) : DefaultAutoExt,
            DefaultAutoExt, out _autoExtBox));
        root.Children.Add(HintText(L("WebAccess_AutoAllowHint")));

        root.Children.Add(FieldLabel(L("WebAccess_AskLabel"), topMargin: 12));
        root.Children.Add(ExtRow(
            wb.AskDownloadExtensions.Count > 0 ? string.Join(";", wb.AskDownloadExtensions) : DefaultAskExt,
            DefaultAskExt, out _askExtBox));
        root.Children.Add(HintText(L("WebAccess_AskHint")));

        // Fetch settings section
        root.Children.Add(new Separator { Margin = new Thickness(0, 20, 0, 16) });
        root.Children.Add(SectionHeading("⚙  " + L("WebAccess_SectionFetch")));

        root.Children.Add(NumericRow(L("WebAccess_TimeoutLabel"),      L("WebAccess_TimeoutSuffix"), wb.TimeoutSeconds.ToString(), out _timeoutBox,  width: 60));
        root.Children.Add(NumericRow(L("WebAccess_MaxCharsCloudLabel"), L("WebAccess_CharsSuffix"),   wb.MaxCharsCloud.ToString(),  out _maxCloudBox, width: 80));
        root.Children.Add(NumericRow(L("WebAccess_MaxCharsLocalLabel"), L("WebAccess_CharsSuffix"),   wb.MaxCharsLocal.ToString(),  out _maxLocalBox, width: 80));

        // Whitelist section
        root.Children.Add(new Separator { Margin = new Thickness(0, 20, 0, 16) });
        root.Children.Add(SectionHeading("🌐  " + L("WebAccess_SectionWhitelist")));
        root.Children.Add(HintText(L("WebAccess_WhitelistHint")));

        _whitelistView = BuildWhitelistView();
        root.Children.Add(_whitelistView);

        var addRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.Children.Add(addRow);

        _newDomainBox = new TextBox
        {
            FontFamily      = new FontFamily("Segoe UI"),
            FontSize        = 12,
            Padding         = new Thickness(8, 5, 8, 5),
            BorderThickness = new Thickness(1),
        };
        _newDomainBox.SetResourceReference(ForegroundProperty, "InputTextBrush");
        _newDomainBox.SetResourceReference(BackgroundProperty, "ControlBgBrush");
        _newDomainBox.SetResourceReference(BorderBrushProperty, "ControlBorderBrush");
        _newDomainBox.GotFocus  += (_, _) => { if (_newDomainBox.Text == L("WebAccess_AddDomainHint")) { _newDomainBox.Text = ""; _newDomainBox.SetResourceReference(ForegroundProperty, "InputTextBrush"); } };
        _newDomainBox.LostFocus += (_, _) => { if (string.IsNullOrWhiteSpace(_newDomainBox.Text)) { _newDomainBox.Text = L("WebAccess_AddDomainHint"); _newDomainBox.SetResourceReference(ForegroundProperty, "ContentDimBrush"); } };
        _newDomainBox.Text = L("WebAccess_AddDomainHint");
        _newDomainBox.SetResourceReference(ForegroundProperty, "ContentDimBrush");
        _newDomainBox.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Return) AddDomain(); };
        Grid.SetColumn(_newDomainBox, 0);
        addRow.Children.Add(_newDomainBox);

        var addBtn = MakeBtn(L("WebAccess_AddBtn"));
        addBtn.Margin = new Thickness(6, 0, 0, 0);
        addBtn.Click += (_, _) => AddDomain();
        Grid.SetColumn(addBtn, 1);
        addRow.Children.Add(addBtn);

        var removeBtn = MakeBtn(L("WebAccess_RemoveBtn"));
        removeBtn.Margin = new Thickness(6, 0, 0, 0);
        removeBtn.Click += (_, _) => RemoveDomain();
        Grid.SetColumn(removeBtn, 2);
        addRow.Children.Add(removeBtn);

        return new TabItem { Header = L("WebAccess_TabWeb"), Content = scroll };
    }

    private TabItem BuildFileReadingTab(string codeExts)
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var root = new StackPanel { Margin = new Thickness(24, 16, 24, 16) };
        scroll.Content = root;

        root.Children.Add(SectionHeading("💻  " + L("WebAccess_SectionCodeExt")));
        root.Children.Add(HintText(L("WebAccess_CodeExtHint")));

        var defaultExts = new AppSettings().CodeFileExtensions;
        var extRow = ExtRow(
            string.IsNullOrWhiteSpace(codeExts) ? defaultExts : codeExts,
            defaultExts,
            out _codeExtBox,
            lines: 8);
        root.Children.Add(extRow);

        return new TabItem { Header = L("WebAccess_TabFiles"), Content = scroll };
    }

    // ── Whitelist DataGrid ─────────────────────────────────────────────────
    private ListView BuildWhitelistView()
    {
        var grid = new GridView();

        // Enabled checkbox column
        var enabledCol = new GridViewColumn
        {
            Header = L("WebAccess_ColEnabled"),
            Width  = 36,
        };
        var enabledFactory = new FrameworkElementFactory(typeof(CheckBox));
        enabledFactory.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new Binding(nameof(WebWhitelistEntry.IsEnabled)) { Mode = BindingMode.TwoWay });
        enabledFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        enabledCol.CellTemplate = new DataTemplate { VisualTree = enabledFactory };
        grid.Columns.Add(enabledCol);

        // Domain column
        var domainCol = new GridViewColumn
        {
            Header              = L("WebAccess_ColDomain"),
            DisplayMemberBinding = new Binding(nameof(WebWhitelistEntry.Domain)),
            Width               = 240,
        };
        grid.Columns.Add(domainCol);

        // AllowDownloads checkbox column
        var dlCol = new GridViewColumn
        {
            Header = L("WebAccess_ColAllowDl"),
            Width  = 76,
        };
        var dlFactory = new FrameworkElementFactory(typeof(CheckBox));
        dlFactory.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new Binding(nameof(WebWhitelistEntry.AllowDownloads)) { Mode = BindingMode.TwoWay });
        dlFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        dlCol.CellTemplate = new DataTemplate { VisualTree = dlFactory };
        grid.Columns.Add(dlCol);

        var lv = new ListView
        {
            View            = grid,
            ItemsSource     = _whitelist,
            Height          = 160,
            Margin          = new Thickness(0, 8, 0, 0),
            FontFamily      = new FontFamily("Segoe UI"),
            FontSize        = 12,
            BorderThickness = new Thickness(1),
        };
        lv.SetResourceReference(ForegroundProperty,   "ContentTextBrush");
        lv.SetResourceReference(BackgroundProperty,   "ControlBgBrush");
        lv.SetResourceReference(BorderBrushProperty,  "ControlBorderBrush");
        return lv;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private void AddDomain()
    {
        var raw = _newDomainBox?.Text.Trim() ?? "";
        if (string.IsNullOrEmpty(raw) || raw == L("WebAccess_AddDomainHint")) return;

        // Normalise: strip protocol if user typed it
        if (raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) raw = raw[8..];
        if (raw.StartsWith("http://",  StringComparison.OrdinalIgnoreCase)) raw = raw[7..];
        raw = raw.TrimEnd('/');

        if (_whitelist.Any(e => string.Equals(e.Domain, raw, StringComparison.OrdinalIgnoreCase)))
            return;

        _whitelist.Add(new WebWhitelistEntry { Domain = raw, IsEnabled = true });
        RefreshList();

        if (_newDomainBox is not null)
        {
            _newDomainBox.Text = L("WebAccess_AddDomainHint");
            _newDomainBox.SetResourceReference(ForegroundProperty, "ContentDimBrush");
        }
    }

    private void RemoveDomain()
    {
        if (_whitelistView?.SelectedItem is WebWhitelistEntry entry)
        {
            _whitelist.Remove(entry);
            RefreshList();
        }
    }

    private void RefreshList()
    {
        if (_whitelistView is null) return;
        _whitelistView.ItemsSource = null;
        _whitelistView.ItemsSource = _whitelist;
    }

    private void SaveSettings()
    {
        var s  = SettingsService.Load();
        var wb = s.WebBrowsing;

        wb.AllowDownloads = _allowDownloadsChk?.IsChecked ?? wb.AllowDownloads;

        wb.AutoDownloadExtensions = ParseExtList(_autoExtBox?.Text);
        wb.AskDownloadExtensions  = ParseExtList(_askExtBox?.Text);

        if (int.TryParse(_timeoutBox?.Text.Trim(), out var t)   && t  > 0) wb.TimeoutSeconds = t;
        if (int.TryParse(_maxCloudBox?.Text.Trim(), out var mc) && mc > 0) wb.MaxCharsCloud  = mc;
        if (int.TryParse(_maxLocalBox?.Text.Trim(), out var ml) && ml > 0) wb.MaxCharsLocal  = ml;

        wb.Whitelist = _whitelist.ToList();

        if (!string.IsNullOrWhiteSpace(_codeExtBox?.Text))
            s.CodeFileExtensions = _codeExtBox.Text.Trim();

        SettingsService.Save(s);
        Services.ContentFilter.InvalidateCache();
    }

    private static List<string> ParseExtList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Select(e => e.StartsWith('.') ? e : "." + e)
                  .Distinct(StringComparer.OrdinalIgnoreCase)
                  .ToList();
    }

    // ── Tab theming ────────────────────────────────────────────────────────

    private void ApplyTabStyle(TabControl tabs)
    {
        const string xaml = """
            <ResourceDictionary
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

              <ControlTemplate x:Key="TabCtrlTpl" TargetType="TabControl">
                <DockPanel>
                  <Border DockPanel.Dock="Top"
                          Background="{DynamicResource ControlHoverBrush}"
                          Padding="10,4,10,0">
                    <TabPanel IsItemsHost="True"/>
                  </Border>
                  <Border Background="{DynamicResource ContentBgBrush}">
                    <ContentPresenter ContentSource="SelectedContent"/>
                  </Border>
                </DockPanel>
              </ControlTemplate>

              <Style x:Key="TabItemSty" TargetType="TabItem">
                <Setter Property="FontSize"   Value="12"/>
                <Setter Property="FontFamily" Value="Segoe UI"/>
                <Setter Property="Padding"    Value="14,7"/>
                <Setter Property="Foreground" Value="{DynamicResource ContentDimBrush}"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="TabItem">
                      <Border x:Name="Bd" Padding="{TemplateBinding Padding}"
                              CornerRadius="8,8,0,0" Cursor="Hand">
                        <TextBlock x:Name="Tb"
                                   Text="{TemplateBinding Header}"
                                   FontSize="{TemplateBinding FontSize}"
                                   FontFamily="{TemplateBinding FontFamily}"
                                   FontWeight="SemiBold"
                                   Foreground="{TemplateBinding Foreground}"/>
                      </Border>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsSelected" Value="True">
                          <Setter TargetName="Bd" Property="Background"
                                  Value="{DynamicResource ContentBgBrush}"/>
                          <Setter TargetName="Tb" Property="Foreground"
                                  Value="{DynamicResource ContentTextBrush}"/>
                        </Trigger>
                        <MultiTrigger>
                          <MultiTrigger.Conditions>
                            <Condition Property="IsMouseOver" Value="True"/>
                            <Condition Property="IsSelected"  Value="False"/>
                          </MultiTrigger.Conditions>
                          <Setter TargetName="Bd" Property="Background"
                                  Value="{DynamicResource ControlHoverBrush}"/>
                        </MultiTrigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>

            </ResourceDictionary>
            """;

        var dict = (ResourceDictionary)XamlReader.Parse(xaml);
        Resources.MergedDictionaries.Add(dict);
        tabs.Template            = (ControlTemplate)Resources["TabCtrlTpl"];
        tabs.ItemContainerStyle  = (Style)Resources["TabItemSty"];
    }

    // ── Row builders ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds a row: text box (optionally multiline) + Reset button.
    /// The reset button restores <paramref name="defaultText"/>.
    /// When <paramref name="lines"/> &gt; 1 the text box wraps and the Reset button
    /// is top-aligned so it doesn't stretch.
    /// </summary>
    private UIElement ExtRow(string initialText, string defaultText, out TextBox box, int lines = 1)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var multiline = lines > 1;
        var tb = new TextBox
        {
            Text             = initialText,
            FontFamily       = new FontFamily("Segoe UI"),
            FontSize         = 12,
            Padding          = new Thickness(8, 5, 8, 5),
            BorderThickness  = new Thickness(1),
            AcceptsReturn    = false,
            TextWrapping     = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
        };
        if (multiline) tb.MinHeight = lines * 22 + 10;
        tb.SetResourceReference(ForegroundProperty,  "InputTextBrush");
        tb.SetResourceReference(BackgroundProperty,  "ControlBgBrush");
        tb.SetResourceReference(BorderBrushProperty, "ControlBorderBrush");
        Grid.SetColumn(tb, 0);
        row.Children.Add(tb);

        var resetBtn = MakeBtn(L("WebAccess_ResetBtn"));
        resetBtn.Margin            = new Thickness(6, 0, 0, 0);
        resetBtn.VerticalAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        resetBtn.Click += (_, _) => tb.Text = defaultText;
        Grid.SetColumn(resetBtn, 1);
        row.Children.Add(resetBtn);

        box = tb;
        return row;
    }

    /// <summary>
    /// Builds a labelled numeric row: "Label   [box]  suffix".
    /// </summary>
    private UIElement NumericRow(string label, string suffix, string value, out TextBox box, double width = 70)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 8, 0, 0),
        };

        var lbl = new TextBlock
        {
            Text              = label + "  ",
            FontFamily        = new FontFamily("Segoe UI"),
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth          = 200,
        };
        lbl.SetResourceReference(ForegroundProperty, "ContentTextBrush");
        row.Children.Add(lbl);

        var tb = new TextBox
        {
            Text            = value,
            Width           = width,
            FontFamily      = new FontFamily("Segoe UI"),
            FontSize        = 12,
            TextAlignment   = TextAlignment.Right,
            Padding         = new Thickness(6, 3, 6, 3),
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        tb.SetResourceReference(ForegroundProperty,  "InputTextBrush");
        tb.SetResourceReference(BackgroundProperty,  "ControlBgBrush");
        tb.SetResourceReference(BorderBrushProperty, "ControlBorderBrush");
        tb.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsAsciiDigit);
        row.Children.Add(tb);

        var sfx = new TextBlock
        {
            Text              = suffix,
            FontFamily        = new FontFamily("Segoe UI"),
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(4, 0, 0, 0),
        };
        sfx.SetResourceReference(ForegroundProperty, "ContentDimBrush");
        row.Children.Add(sfx);

        box = tb;
        return row;
    }

    private UIElement FieldLabel(string text, double topMargin = 8)
    {
        var tb = new TextBlock
        {
            Text       = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 12,
            Margin     = new Thickness(0, topMargin, 0, 4),
        };
        tb.SetResourceReference(ForegroundProperty, "ContentTextBrush");
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
            Margin       = new Thickness(0, 2, 0, 0),
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
            Padding         = new Thickness(12, 5, 12, 5),
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
