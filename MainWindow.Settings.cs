using System.Runtime.InteropServices;
using SysIO = System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using ClaudetRelay.Services;

namespace ClaudetRelay;

public partial class MainWindow
{
    // ── Options menu ───────────────────────────────────────────────────────

    private void OptionsMenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();

        var generalItem   = new MenuItem { Header = Properties.Loc.S("Menu_GeneralSettings") };
        var foldersItem   = new MenuItem { Header = Properties.Loc.S("Menu_FoldersSetup") };
        var providersItem = new MenuItem { Header = Properties.Loc.S("Menu_ProvidersSetup") };
        var infoItem      = new MenuItem { Header = Properties.Loc.S("Menu_Info") };
        var versionItem   = new MenuItem { Header = Properties.Loc.S("Menu_Version") };

        generalItem  .Click += (_, _) => OpenGeneralSettings();
        foldersItem  .Click += (_, _) => ShowFoldersSetupDialog();
        providersItem.Click += (_, _) => OpenProvidersSetup();
        infoItem     .Click += (_, _) => ShowAboutInfoDialog();
        versionItem  .Click += (_, _) => ShowAboutVersionDialog();

        // ── Language submenu ───────────────────────────────────────────────
        var storedLang = Services.SettingsService.Load().Language;

        // When null (first launch / OS default), reflect actual running culture
        // so the checkmark lands on the correct language in the menu.
        string effectiveLang;
        if (storedLang is null)
        {
            var twoLetter = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
                                .ToLowerInvariant();
            effectiveLang = twoLetter == "en" ? "" : twoLetter;
        }
        else
        {
            effectiveLang = storedLang;
        }

        var langItem = new MenuItem { Header = "🌐  " + Properties.Loc.S("Menu_Language") };

        void AddLang(string label, string code)
        {
            var item = new MenuItem
            {
                Header      = label,
                IsCheckable = true,
                IsChecked   = string.Equals(effectiveLang, code, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) => SetLanguage(code);
            langItem.Items.Add(item);
        }
        AddLang("English", "");
        AddLang("Deutsch", "de");

        menu.Items.Add(generalItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(foldersItem);
        menu.Items.Add(providersItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(langItem);
        menu.Items.Add(new Separator());
        BuildAudioMenuItems(menu);
        var managerItem = new MenuItem { Header = Properties.Loc.S("Menu_Manager") };
        managerItem.Click += (_, _) => OpenManagerSettings();
        menu.Items.Add(managerItem);
        menu.Items.Add(new Separator());
        BuildWebAccessMenuItem(menu);
        menu.Items.Add(new Separator());
        menu.Items.Add(infoItem);
        menu.Items.Add(versionItem);

        menu.PlacementTarget = (Button)sender;
        menu.Placement       = PlacementMode.Bottom;
        menu.IsOpen          = true;
    }

    private void SetLanguage(string code)
    {
        var s = Services.SettingsService.Load();
        // Compare the stored value directly (no ?? fallback).
        // null (OS default) ≠ "" (explicit English) — switching to English on a
        // non-English OS must still write "" so App.xaml.cs can force the culture.
        if (string.Equals(s.Language, code, StringComparison.OrdinalIgnoreCase)) return;
        s.Language = code;
        Services.SettingsService.Save(s);
        MessageBox.Show(
            Properties.Loc.S("Settings_LanguageHint"),
            "🌐  " + Properties.Loc.S("Menu_Language"),
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BuildAudioMenuItems(ContextMenu menu)
    {
        var item = new MenuItem { Header = Properties.Loc.S("Menu_AudioAndVoice") };
        item.Click += (_, _) => OpenAudioAndVoiceSettings();
        menu.Items.Add(item);
    }

    internal void OpenManagerSettings()
    {
        var win = new ManagerSettingsWindow(_currentThemePath, ApplyThemeToDialog) { Owner = this };
        win.ShowDialog();
    }

    private void BuildWebAccessMenuItem(ContextMenu menu)
    {
        var item = new MenuItem { Header = Properties.Loc.S("WebAccess_MenuTitle") };
        item.Click += (_, _) => OpenWebAccessSettings();
        menu.Items.Add(item);
    }

    internal void OpenWebAccessSettings()
    {
        var win = new WebAccessSettingsWindow(_currentThemePath) { Owner = this };
        win.SourceInitialized += (_, _) => ApplyTitleBarTheme(win);
        win.ShowDialog();
    }

    internal void OpenAudioAndVoiceSettings(int initialTab = 0)
    {
        var win = new AudioAndVoiceSettingsWindow(
            _currentThemePath, _dictation,
            applyTheme: ApplyThemeToDialog,
            initialTab: initialTab)
        { Owner = this };
        win.ShowDialog();

        // Re-initialise dictation after any ASR settings change (same logic as old OpenVoiceRecognitionSettings)
        if (_dictationModelLoaded)
        {
            _dictation.Deactivate();
            _dictationActive      = false;
            _dictationModelLoaded = false;
            UpdateDictationPower(loaded: false);
            UpdateMicButton(Services.DictationState.Idle);
            _ = LoadDictationAsync();
        }
    }

    private void InitVoiceBackend()
    {
        var s = Services.SettingsService.Load();

        Services.VoiceOutputService.Volume       = (float)Math.Clamp(s.AudioVolume, 0.0, 1.0);
        Services.VoiceOutputService.DeviceNumber = AudioSetupWindow.FindDeviceNumber(s.AudioOutputDevice);

        Services.VoiceOutputService.ActiveBackend = s.VoiceBackend switch
        {
            "Sherpa"   => new Services.SherpaOnnxTtsBackend(s.SherpaModelFolder),
            "Voicevox" => new Services.VoicevoxTtsBackend(s.VoicevoxPort),
            _          => new Services.WindowsTtsBackend(),
        };
    }

    internal void OpenAudioSetup()
    {
        var win = new AudioSetupWindow(_currentThemePath) { Owner = this };
        win.SourceInitialized += (_, _) => ApplyTitleBarTheme(win);
        win.ShowDialog();
    }

    internal void OpenVoiceSettings()
    {
        var win = new VoiceSettingsWindow(_currentThemePath) { Owner = this };
        win.SourceInitialized += (_, _) => ApplyTitleBarTheme(win);
        win.ShowDialog();
    }

    internal void OpenVoiceModelManager()
    {
        var win = new VoiceModelManagerWindow(_currentThemePath) { Owner = this };
        win.SourceInitialized += (_, _) => ApplyTitleBarTheme(win);
        win.ShowDialog();
    }

    internal void OpenVoiceRecognitionSettings()
    {
        var win = new VoiceRecognitionSettingsWindow(_currentThemePath, _dictation, ApplyThemeToDialog) { Owner = this };
        win.ShowDialog();

        // Re-initialise the dictation service so any mode / model / device change
        // takes effect immediately (mode change is the most common case — e.g. switching
        // from VoiceActivated to PushToTalk must reconfigure the running service).
        if (_dictationModelLoaded)
        {
            _dictation.Deactivate();
            _dictationActive      = false;
            _dictationModelLoaded = false;
            UpdateDictationPower(loaded: false);
            UpdateMicButton(Services.DictationState.Idle);
            _ = LoadDictationAsync();
        }
    }

    private void OpenGeneralSettings()
    {
        var win = new SettingsWindow(_currentThemePath, providerModeOnly: false) { Owner = this };
        if (win.ShowDialog() == true)
        {
            var updated = SettingsService.Load();
            _userName             = string.IsNullOrWhiteSpace(updated.UserName) ? "You" : updated.UserName.Trim();
            _toneLevel            = updated.ToneLevel;
            _chattinessLevel      = updated.GlobalChattiness;
            _mockingbirdMode      = updated.MockingbirdMode;
            _buccaneeerMode       = updated.BuccaneerMode;
            _aiDialogueEnabled    = updated.AiDialogueEnabled;
            _aiDialogueMaxTurns   = Math.Clamp(updated.AiDialogueMaxTurns, 3, 100);
            _globalResponseLength = Math.Clamp(updated.GlobalResponseLength, 0, 100);
            _uiLanguageName       = UiLanguageCodeToName(updated.Language ?? "");
            UpdateAiDialogueButton();
            ApplyChatFont(updated);
            ApplyUiZoom(updated.UiZoom);
        }
    }

    private void OpenProvidersSetup()
    {
        var win = new SettingsWindow(_currentThemePath, providerModeOnly: true) { Owner = this };
        win.SourceInitialized += (_, _) => ApplyTitleBarTheme(win);
        if (win.ShowDialog() == true)
        {
            var updated = SettingsService.Load();
            ApplyThrottleSettings(updated);
        }
    }

    private void ShowFoldersSetupDialog()
    {
        var settings      = SettingsService.Load();
        var defaultFolder = Services.ProjectService.GetDefaultProjectsFolder();

        var win = new Window
        {
            Title                 = Properties.Loc.S("Folders_Title"),
            Width                 = 480,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ResizeMode            = ResizeMode.NoResize
        };
        ApplyThemeToDialog(win);
        win.SetResourceReference(Window.BackgroundProperty, "ContentBgBrush");

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        // ── Heading ────────────────────────────────────────────────────────
        var heading = new TextBlock
        {
            Text       = Properties.Loc.S("Folders_Projects"),
            FontSize   = 11,
            FontWeight = FontWeights.Bold,
            Margin     = new Thickness(0, 0, 0, 6)
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        // ── Folder input ───────────────────────────────────────────────────
        var folderTb = new TextBox
        {
            Text            = string.IsNullOrWhiteSpace(settings.ProjectsFolder) ? "" : settings.ProjectsFolder,
            FontSize        = 13,
            FontFamily      = new System.Windows.Media.FontFamily("Segoe UI"),
            BorderThickness = new Thickness(0),
            Background      = Brushes.Transparent,
            Padding         = new Thickness(0, 2, 0, 2)
        };
        folderTb.SetResourceReference(TextBox.ForegroundProperty,  "InputTextBrush");
        folderTb.SetResourceReference(TextBox.CaretBrushProperty,  "InputTextBrush");
        var folderBorder = new Border
        {
            CornerRadius    = new CornerRadius(8), Height = 34,
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(10, 0, 10, 0),
            Child           = folderTb
        };
        folderBorder.SetResourceReference(Border.BackgroundProperty,  "InputBgBrush");
        folderBorder.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush");

        var browseBtn = new Button
        {
            Content = Properties.Loc.S("Btn_Browse"),
            Padding = new Thickness(12, 8, 12, 8),
            Margin  = new Thickness(6, 0, 0, 0),
            ToolTip = Properties.Loc.S("Folders_ChooseFolder")
        };
        browseBtn.Style = (Style)FindResource("ModernButton");
        browseBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        browseBtn.SetResourceReference(Button.ForegroundProperty, "ControlTextBrush");
        browseBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = Properties.Loc.S("Folders_SelectProjects"),
                InitialDirectory = string.IsNullOrWhiteSpace(folderTb.Text) ? defaultFolder : folderTb.Text
            };
            if (dlg.ShowDialog(win) == true)
                folderTb.Text = dlg.FolderName;
        };

        var defaultBtn = new Button
        {
            Content = Properties.Loc.S("Btn_Default"),
            Padding = new Thickness(12, 8, 12, 8),
            Margin  = new Thickness(6, 0, 0, 0),
            ToolTip = defaultFolder
        };
        defaultBtn.Style = (Style)FindResource("ModernButton");
        defaultBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        defaultBtn.SetResourceReference(Button.ForegroundProperty, "ControlTextBrush");
        defaultBtn.Click += (_, _) => folderTb.Text = "";

        var folderRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(folderBorder, 0);
        Grid.SetColumn(browseBtn,    1);
        Grid.SetColumn(defaultBtn,   2);
        folderRow.Children.Add(folderBorder);
        folderRow.Children.Add(browseBtn);
        folderRow.Children.Add(defaultBtn);

        var hint = new TextBlock
        {
            Text         = $"{Properties.Loc.S("Folders_Default")}: {defaultFolder}",
            FontSize     = 11,
            FontFamily   = new System.Windows.Media.FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 20)
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        // ── Separator ──────────────────────────────────────────────────────
        var sep = new Rectangle { Height = 1, Margin = new Thickness(0, 0, 0, 16) };
        sep.SetResourceReference(Rectangle.FillProperty, "ControlBgBrush");

        // ── BACKUP FOLDER ──────────────────────────────────────────────────
        var backupHeading = new TextBlock
        {
            Text       = Properties.Loc.S("Folders_Backup"),
            FontSize   = 11,
            FontWeight = FontWeights.Bold,
            Margin     = new Thickness(0, 0, 0, 6)
        };
        backupHeading.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var backupTb = new TextBox
        {
            Text            = settings.BackupFolder,
            FontSize        = 13,
            FontFamily      = new System.Windows.Media.FontFamily("Segoe UI"),
            BorderThickness = new Thickness(0),
            Background      = Brushes.Transparent,
            Padding         = new Thickness(0, 2, 0, 2)
        };
        backupTb.SetResourceReference(TextBox.ForegroundProperty,  "InputTextBrush");
        backupTb.SetResourceReference(TextBox.CaretBrushProperty,  "InputTextBrush");
        var backupBorder = new Border
        {
            CornerRadius    = new CornerRadius(8), Height = 34,
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(10, 0, 10, 0),
            Child           = backupTb
        };
        backupBorder.SetResourceReference(Border.BackgroundProperty,  "InputBgBrush");
        backupBorder.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush");

        var backupBrowseBtn = new Button
        {
            Content = Properties.Loc.S("Btn_Browse"),
            Padding = new Thickness(12, 8, 12, 8),
            Margin  = new Thickness(6, 0, 0, 0),
            ToolTip = Properties.Loc.S("Folders_ChooseBackup")
        };
        backupBrowseBtn.Style = (Style)FindResource("ModernButton");
        backupBrowseBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        backupBrowseBtn.SetResourceReference(Button.ForegroundProperty, "ControlTextBrush");
        backupBrowseBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = Properties.Loc.S("Folders_SelectBackup"),
                InitialDirectory = string.IsNullOrWhiteSpace(backupTb.Text) ? defaultFolder : backupTb.Text
            };
            if (dlg.ShowDialog(win) == true)
                backupTb.Text = dlg.FolderName;
        };

        var backupClearBtn = new Button
        {
            Content = Properties.Loc.S("Btn_ClearX"),
            Padding = new Thickness(12, 8, 12, 8),
            Margin  = new Thickness(6, 0, 0, 0),
            ToolTip = Properties.Loc.S("Folders_BackupClearTip")
        };
        backupClearBtn.Style = (Style)FindResource("ModernButton");
        backupClearBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        backupClearBtn.SetResourceReference(Button.ForegroundProperty, "ControlTextBrush");
        backupClearBtn.Click += (_, _) => backupTb.Text = "";

        var backupRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        backupRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        backupRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        backupRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(backupBorder,    0);
        Grid.SetColumn(backupBrowseBtn, 1);
        Grid.SetColumn(backupClearBtn,  2);
        backupRow.Children.Add(backupBorder);
        backupRow.Children.Add(backupBrowseBtn);
        backupRow.Children.Add(backupClearBtn);

        var backupHint = new TextBlock
        {
            Text         = Properties.Loc.S("Folders_BackupHint"),
            FontSize     = 11,
            FontFamily   = new System.Windows.Media.FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 20)
        };
        backupHint.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        // ── Save ───────────────────────────────────────────────────────────
        var saveBtn = new Button
        {
            Content             = Properties.Loc.S("Btn_Save"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding             = new Thickness(20, 8, 20, 8)
        };
        saveBtn.Style = (Style)FindResource("ModernButton");
        saveBtn.SetResourceReference(Button.BackgroundProperty, "PrimaryAccentBrush");
        saveBtn.SetResourceReference(Button.ForegroundProperty, "AccentTextBrush");
        saveBtn.Click += (_, _) =>
        {
            var s = SettingsService.Load();
            s.ProjectsFolder = folderTb.Text.Trim();
            s.BackupFolder   = backupTb.Text.Trim();
            SettingsService.Save(s);
            win.Close();
        };

        panel.Children.Add(heading);
        panel.Children.Add(folderRow);
        panel.Children.Add(hint);
        panel.Children.Add(sep);
        panel.Children.Add(backupHeading);
        panel.Children.Add(backupRow);
        panel.Children.Add(backupHint);
        panel.Children.Add(saveBtn);

        win.Content = panel;
        UiZoomHelper.Apply(win, UiZoomHelper.FromSettings());
        win.ShowDialog();
    }

    // ── Chat font and appearance ───────────────────────────────────────────

    /// <summary>Seeds / updates the ChatFontFamily and ChatFontSize dynamic resources.</summary>
    private void ApplyChatFont(AppSettings settings)
    {
        Resources["ChatFontFamily"] = new System.Windows.Media.FontFamily(settings.ChatFontFamily);
        Resources["ChatFontSize"]   = settings.ChatFontSize;
    }

    /// <summary>
    /// Applies a uniform UI zoom to the main window content.
    /// Also re-applies zoom to the open ParticipantsWindow if it is currently visible.
    /// </summary>
    private void ApplyUiZoom(double zoom)
    {
        UiZoomHelper.Apply(this, zoom, scaleWindow: false);
        if (_participantsWindow is { IsVisible: true })
            UiZoomHelper.Apply(_participantsWindow, zoom, scaleWindow: false);
        // Push the zoom factor into app resources so ContextMenu / MenuItem styles
        // can scale themselves via DynamicResource binding on their LayoutTransform.
        Application.Current.Resources["UiZoomFactor"] = zoom;
    }

    /// <summary>
    /// Recalculates the bubble width and publishes it via the "ChatBubbleMaxWidth" resource.
    /// Called on startup, on window resize, and when the user changes the slider.
    /// </summary>
    private void UpdateChatBubbleWidth()
    {
        const double avatarWidth = 44.0;  // avatar Border 34 px + Margin 10 px

        var panelWidth = ChatPanel.ActualWidth;
        double available;
        if (panelWidth > 10)
            available = panelWidth;
        else
        {
            var svw = ChatScrollViewer.ActualWidth;
            available = svw > 10 ? svw - 40 : 840.0;
        }

        var bubbleW = Math.Max(50.0, (available - avatarWidth) * (_chatBubbleWidthPct / 100.0));
        Resources["ChatBubbleMaxWidth"] = bubbleW;

        foreach (var wrapper in ChatPanel.Children.OfType<System.Windows.Controls.Grid>())
            foreach (FrameworkElement cell in wrapper.Children)
                if (cell.Tag as string == "BubbleContent")
                    cell.Width = bubbleW;
    }

    // ── AI Dialogue toggle ─────────────────────────────────────────────────

    private void AiDialogueButton_Click(object sender, RoutedEventArgs e)
    {
        _aiDialogueEnabled = !_aiDialogueEnabled;
        UpdateAiDialogueButton();
        var settings = SettingsService.Load();
        settings.AiDialogueEnabled = _aiDialogueEnabled;
        SettingsService.Save(settings);
        AddSystemMessage(_aiDialogueEnabled
            ? "💬  Multi-round dialogue enabled - AIs will reply to each other after the first response."
            : "💬  Multi-round dialogue disabled - each AI responds once per message.");
    }

    /// <summary>Refreshes the 💬 button's visual state to match _aiDialogueEnabled.</summary>
    private void UpdateAiDialogueButton()
    {
        if (_aiDialogueEnabled)
        {
            AiDialogueButton.SetResourceReference(Button.BackgroundProperty, "AccentBgBrush");
            AiDialogueButton.SetResourceReference(Button.ForegroundProperty, "AccentTextBrush");
        }
        else
        {
            AiDialogueButton.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
            AiDialogueButton.SetResourceReference(Button.ForegroundProperty, "ContentDimBrush");
        }
    }

    // ── Chat appearance dialog ─────────────────────────────────────────────

    private void ChatFontButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsService.Load();
        var allFonts = System.Windows.Media.Fonts.SystemFontFamilies
                            .Select(f => f.Source)
                            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                            .ToList();

        var win = new Window
        {
            Title                 = "Chat Appearance",
            Width                 = 420,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ResizeMode            = ResizeMode.NoResize
        };
        ApplyThemeToDialog(win);
        win.SetResourceReference(Window.BackgroundProperty, "ContentBgBrush");

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        TextBox MakeNumBox(string initial, double width = 58)
        {
            var tb = new TextBox
            {
                Text            = initial,
                Width           = width,
                FontSize        = 13,
                FontFamily      = new System.Windows.Media.FontFamily("Segoe UI"),
                TextAlignment   = TextAlignment.Center,
                BorderThickness = new Thickness(0),
                Background      = Brushes.Transparent,
                Padding         = new Thickness(4, 2, 4, 2),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            tb.SetResourceReference(TextBox.ForegroundProperty,  "InputTextBrush");
            tb.SetResourceReference(TextBox.CaretBrushProperty,  "InputTextBrush");
            return tb;
        }
        Border WrapNumBox(TextBox tb)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(6),
                Margin       = new Thickness(8, 0, 0, 0),
                Padding      = new Thickness(2, 0, 2, 0),
                Height       = 28,
                Child        = tb
            };
            b.SetResourceReference(Border.BackgroundProperty, "ControlBgBrush");
            return b;
        }
        TextBlock MakeSectionLabel(string text)
        {
            var lbl = new TextBlock
            {
                Text       = text,
                FontSize   = 12,
                FontWeight = FontWeights.SemiBold,
                Margin     = new Thickness(0, 0, 0, 6)
            };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
            return lbl;
        }

        // ── Font Family ───────────────────────────────────────────────────
        panel.Children.Add(MakeSectionLabel("Font Family"));

        var searchBox = new TextBox
        {
            Text            = "",
            FontSize        = 13,
            FontFamily      = new System.Windows.Media.FontFamily("Segoe UI"),
            BorderThickness = new Thickness(0),
            Background      = Brushes.Transparent,
            Padding         = new Thickness(0, 2, 0, 2)
        };
        searchBox.SetResourceReference(TextBox.ForegroundProperty, "InputTextBrush");
        searchBox.SetResourceReference(TextBox.CaretBrushProperty, "InputTextBrush");
        var searchBorder = new Border
        {
            CornerRadius    = new CornerRadius(8), Height = 34,
            BorderThickness = new Thickness(1),
            Margin          = new Thickness(0, 0, 0, 6),
            Padding         = new Thickness(10, 0, 10, 0),
            Child           = searchBox
        };
        searchBorder.SetResourceReference(Border.BackgroundProperty,  "InputBgBrush");
        searchBorder.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush");
        panel.Children.Add(searchBorder);

        var fontList = new ListBox
        {
            Height          = 150,
            BorderThickness = new Thickness(0),
            Margin          = new Thickness(0, 0, 0, 18)
        };
        fontList.SetResourceReference(ListBox.BackgroundProperty, "InputBgBrush");
        fontList.SetResourceReference(ListBox.ForegroundProperty, "ContentTextBrush");
        panel.Children.Add(fontList);

        void RefreshFontList(string filter)
        {
            fontList.Items.Clear();
            foreach (var f in allFonts.Where(f => f.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                fontList.Items.Add(f);
            var current = settings.ChatFontFamily ?? "Segoe UI";
            fontList.SelectedItem = fontList.Items.Cast<string>()
                .FirstOrDefault(f => f.Equals(current, StringComparison.OrdinalIgnoreCase))
                ?? fontList.Items.Cast<string>().FirstOrDefault();
            fontList.ScrollIntoView(fontList.SelectedItem);
        }
        RefreshFontList("");
        searchBox.TextChanged += (_, _) => RefreshFontList(searchBox.Text);

        // ── Font Size ─────────────────────────────────────────────────────
        panel.Children.Add(MakeSectionLabel("Font Size  (pt)"));

        var sizeTb     = MakeNumBox($"{settings.ChatFontSize:0.#}");
        var sizeTbWrap = WrapNumBox(sizeTb);
        var sizeSlider = new Slider
        {
            Minimum             = 9,
            Maximum             = 128,
            Value               = settings.ChatFontSize,
            TickFrequency       = 1,
            IsSnapToTickEnabled = false,
            VerticalAlignment   = VerticalAlignment.Center
        };
        var sizeRow = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(sizeSlider, 0);
        Grid.SetColumn(sizeTbWrap, 1);
        sizeRow.Children.Add(sizeSlider);
        sizeRow.Children.Add(sizeTbWrap);
        panel.Children.Add(sizeRow);

        // ── Bubble Width ──────────────────────────────────────────────────
        panel.Children.Add(MakeSectionLabel("Bubble Width  (% of chat area)"));

        var widthTb     = MakeNumBox($"{_chatBubbleWidthPct:0}");
        var widthTbWrap = WrapNumBox(widthTb);
        var widthSlider = new Slider
        {
            Minimum             = 30,
            Maximum             = 100,
            Value               = _chatBubbleWidthPct,
            TickFrequency       = 1,
            IsSnapToTickEnabled = true,
            VerticalAlignment   = VerticalAlignment.Center
        };
        var widthRow = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        widthRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        widthRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(widthSlider, 0);
        Grid.SetColumn(widthTbWrap, 1);
        widthRow.Children.Add(widthSlider);
        widthRow.Children.Add(widthTbWrap);
        panel.Children.Add(widthRow);

        // ── Preview ───────────────────────────────────────────────────────
        panel.Children.Add(MakeSectionLabel("Preview"));
        var previewTb = new TextBlock
        {
            Text         = "The quick brown fox jumps over the lazy dog.\n0123456789  !@#$%",
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 20),
            FontFamily   = new System.Windows.Media.FontFamily(settings.ChatFontFamily),
            FontSize     = settings.ChatFontSize
        };
        previewTb.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        panel.Children.Add(previewTb);

        // ── Live wiring ───────────────────────────────────────────────────
        bool _updating = false;

        void ApplyFont()
        {
            var family = fontList.SelectedItem as string ?? settings.ChatFontFamily;
            var size   = Math.Clamp(sizeSlider.Value, 9, 128);
            previewTb.FontFamily        = new System.Windows.Media.FontFamily(family);
            previewTb.FontSize          = size;
            Resources["ChatFontFamily"] = new System.Windows.Media.FontFamily(family);
            Resources["ChatFontSize"]   = size;
        }
        void ApplyWidth()
        {
            var pct = Math.Clamp(widthSlider.Value, 30, 100);
            _chatBubbleWidthPct = pct;
            UpdateChatBubbleWidth();
        }

        sizeSlider.ValueChanged += (_, _) =>
        {
            if (_updating) return;
            _updating = true;
            sizeTb.Text = $"{sizeSlider.Value:0.#}";
            _updating = false;
            ApplyFont();
        };
        widthSlider.ValueChanged += (_, _) =>
        {
            if (_updating) return;
            _updating = true;
            widthTb.Text = $"{widthSlider.Value:0}";
            _updating = false;
            ApplyWidth();
        };
        sizeTb.TextChanged += (_, _) =>
        {
            if (_updating) return;
            if (double.TryParse(sizeTb.Text, out var v) && v is >= 9 and <= 128)
            {
                _updating = true;
                sizeSlider.Value = v;
                _updating = false;
                ApplyFont();
            }
        };
        widthTb.TextChanged += (_, _) =>
        {
            if (_updating) return;
            if (double.TryParse(widthTb.Text, out var v) && v is >= 30 and <= 100)
            {
                _updating = true;
                widthSlider.Value = v;
                _updating = false;
                ApplyWidth();
            }
        };
        fontList.SelectionChanged += (_, _) => ApplyFont();

        var closeBtn = new Button
        {
            Content             = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding             = new Thickness(20, 8, 20, 8)
        };
        closeBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        closeBtn.SetResourceReference(Button.ForegroundProperty, "ControlTextBrush");
        closeBtn.Style = (Style)FindResource("ModernButton");
        closeBtn.Click += (_, _) =>
        {
            var s = SettingsService.Load();
            s.ChatFontFamily         = fontList.SelectedItem as string ?? s.ChatFontFamily;
            s.ChatFontSize           = sizeSlider.Value;
            s.ChatBubbleWidthPercent = widthSlider.Value;
            SettingsService.Save(s);
            win.Close();
        };
        panel.Children.Add(closeBtn);

        win.Content = panel;
        win.ShowDialog();
    }

    // ── About / Version dialogs ────────────────────────────────────────────

    private void ShowAboutInfoDialog()
    {
        var win = new Window
        {
            Title                 = Properties.Loc.S("About_Title"),
            Width                 = 360,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            Background            = (Brush)FindResource("ContentBgBrush"),
            ResizeMode            = ResizeMode.NoResize
        };
        ApplyThemeToDialog(win);

        var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };

        panel.Children.Add(new TextBlock
        {
            Text = "ClaudetRelay", FontSize = 22, FontWeight = FontWeights.Bold,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("ContentTextBrush"),
            Margin = new Thickness(0, 0, 0, 4)
        });
        panel.Children.Add(new TextBlock
        {
            Text = Properties.Loc.S("About_Subtitle"),
            FontSize = 13, FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("ContentDimBrush"),
            Margin = new Thickness(0, 0, 0, 20)
        });
        panel.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Height = 1, Fill = (Brush)FindResource("ControlBgBrush"),
            Margin = new Thickness(0, 0, 0, 20)
        });
        panel.Children.Add(new TextBlock
        {
            Text = Properties.Loc.S("About_Author"),
            FontSize = 13, FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("ContentTextBrush"),
            Margin = new Thickness(0, 0, 0, 20)
        });

        var closeBtn = new Button
        {
            Content = Properties.Loc.S("Btn_Close"), IsDefault = true,
            Height = 34, Padding = new Thickness(20, 0, 20, 0),
            Style = (Style)FindResource("ModernButton"),
            Background = (Brush)FindResource("PrimaryAccentBrush"),
            Foreground = (Brush)FindResource("AccentTextBrush"),
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        closeBtn.Click += (_, _) => win.Close();
        panel.Children.Add(closeBtn);

        win.Content = panel;
        UiZoomHelper.Apply(win, UiZoomHelper.FromSettings());
        win.ShowDialog();
    }

    private void ShowAboutVersionDialog()
    {
        var asm     = System.Reflection.Assembly.GetExecutingAssembly();
        var ver     = asm.GetName().Version;
        var verStr  = ver is not null
            ? ver.Revision > 0
                ? $"{ver.Major}.{ver.Minor}.{ver.Build}.{ver.Revision}"
                : $"{ver.Major}.{ver.Minor}.{ver.Build}"
            : "dev";
        var exePath = Environment.ProcessPath ?? AppDomain.CurrentDomain.BaseDirectory;
        var built   = SysIO.File.GetLastWriteTime(exePath);

        var win = new Window
        {
            Title                 = Properties.Loc.S("Version_Title"),
            Width                 = 300,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            Background            = (Brush)FindResource("ContentBgBrush"),
            ResizeMode            = ResizeMode.NoResize
        };
        ApplyThemeToDialog(win);

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        panel.Children.Add(new TextBlock
        {
            Text = "ClaudetRelay", FontSize = 16, FontWeight = FontWeights.Bold,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("ContentTextBrush"),
            Margin = new Thickness(0, 0, 0, 10)
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{Properties.Loc.S("Version_Label")} {verStr}",
            FontSize = 13, FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("ContentTextBrush"),
            Margin = new Thickness(0, 0, 0, 4)
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{Properties.Loc.S("Version_Build")}  {built:yyyy-MM-dd}",
            FontSize = 12, FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("ContentDimBrush"),
            Margin = new Thickness(0, 0, 0, 18)
        });

        var closeBtn = new Button
        {
            Content = Properties.Loc.S("Btn_Close"), IsDefault = true,
            Height = 32, Padding = new Thickness(16, 0, 16, 0),
            Style = (Style)FindResource("ModernButton"),
            Background = (Brush)FindResource("ControlBgBrush"),
            Foreground = (Brush)FindResource("ContentTextBrush"),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        closeBtn.Click += (_, _) => win.Close();
        panel.Children.Add(closeBtn);

        win.Content = panel;
        UiZoomHelper.Apply(win, UiZoomHelper.FromSettings());
        win.ShowDialog();
    }

    // ── Participants window (Settings button) ──────────────────────────────

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_participantsWindow is not null && !_participantsWindow.IsLoaded)
            _participantsWindow = null;

        if (_participantsWindow is not null && _participantsWindow.IsVisible)
        {
            _participantsWindow.Activate();
            return;
        }

        var settingsBefore = SettingsService.Load();

        _participantsWindow = new ParticipantsWindow(_currentThemePath, this);
        _participantsWindow.Closed += (_, _) =>
        {
            _participantsWindow = null;
            Activate();
            // Snapshot live in-chat state BEFORE teardown so we can restore
            // "removed from chat" and "deactivated in chat" states afterwards.
            // ReInitializeParticipants preserves cards for actively generating
            // participants and sets _pendingParticipantReinit for a deferred
            // full rebuild once their generation completes.
            var chatStates = SnapshotChatStates();
            ReInitializeParticipants();
            RestoreChatStates(chatStates);
            var settingsAfter = SettingsService.Load();
            ApplyThrottleSettings(settingsAfter);
            ApplyChatFont(settingsAfter);
            ApplyUiZoom(settingsAfter.UiZoom);
        };
        _participantsWindow.Show();
    }

    // ── Theme system ───────────────────────────────────────────────────────

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeComboBox.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag?.ToString() is string path)
            ApplyTheme(path);
    }

    // ── Project types ──────────────────────────────────────────────────────

    private void LoadProjectTypes()
    {
        _projectTypes.Clear();
        var typesDir = SysIO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProjectTypes");
        if (SysIO.Directory.Exists(typesDir))
        {
            foreach (var file in SysIO.Directory.GetFiles(typesDir, "*.xaml")
                                                .OrderBy(SysIO.Path.GetFileNameWithoutExtension,
                                                         StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var dict = new ResourceDictionary { Source = new Uri(file) };
                    if (dict["ProjectType"] is ProjectTypeDefinition ptd)
                        _projectTypes.Add(ptd);
                }
                catch { /* skip malformed XAML files */ }
            }
        }

        if (!_projectTypes.Any(t => t.Name.Equals("General", StringComparison.OrdinalIgnoreCase)))
            _projectTypes.Insert(0, new ProjectTypeDefinition());
    }

    /// <summary>Finds the ProjectTypeDefinition for the given type name (case-insensitive).
    /// Falls back to the General definition if not found.</summary>
    private ProjectTypeDefinition ResolveProjectType(string? typeName)
    {
        if (!string.IsNullOrWhiteSpace(typeName))
        {
            var match = _projectTypes.FirstOrDefault(t =>
                t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return _projectTypes.FirstOrDefault(t =>
                   t.Name.Equals("General", StringComparison.OrdinalIgnoreCase))
               ?? new ProjectTypeDefinition();
    }

    private void LoadThemesIntoComboBox()
    {
        var themesDir = SysIO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Themes");
        if (!SysIO.Directory.Exists(themesDir)) return;

        var files = SysIO.Directory.GetFiles(themesDir, "*.oxsuit")
                             .OrderBy(SysIO.Path.GetFileNameWithoutExtension,
                                      StringComparer.OrdinalIgnoreCase)
                             .ToList();

        ThemeComboBox.SelectionChanged -= ThemeComboBox_SelectionChanged;
        ThemeComboBox.Items.Clear();

        var savedTheme = SettingsService.Load().LastTheme ?? "";

        ComboBoxItem? savedItem     = null;
        ComboBoxItem? newspaperItem = null;
        foreach (var file in files)
        {
            var name    = SysIO.Path.GetFileNameWithoutExtension(file)!;
            var display = FormatThemeName(name);
            var item    = new ComboBoxItem { Content = display, Tag = file };
            ThemeComboBox.Items.Add(item);

            if (!string.IsNullOrEmpty(savedTheme) &&
                name.Equals(savedTheme, StringComparison.OrdinalIgnoreCase))
                savedItem = item;

            if (name.Equals("Newspaper", StringComparison.OrdinalIgnoreCase))
                newspaperItem = item;
        }

        ThemeComboBox.SelectionChanged += ThemeComboBox_SelectionChanged;

        var target = savedItem
                  ?? newspaperItem
                  ?? (ThemeComboBox.Items.Count > 0 ? (ComboBoxItem)ThemeComboBox.Items[0]! : null);
        if (target is not null)
        {
            ThemeComboBox.SelectedItem = target;
            if (target.Tag?.ToString() is string path)
                ApplyTheme(path);
        }
    }

    private void ApplyTheme(string absolutePath)
    {
        try
        {
            var dict = OxsuitLoader.Load(absolutePath)
                ?? throw new InvalidOperationException(
                       "File is not a valid OXSUIT theme (no recognisable colour entries).");

            Resources.MergedDictionaries.Clear();
            Resources.MergedDictionaries.Add(dict);
            _currentThemePath = absolutePath;

            var settings = SettingsService.Load();
            settings.LastTheme = SysIO.Path.GetFileNameWithoutExtension(absolutePath);
            SettingsService.Save(settings);

            ApplyTitleBarTheme();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"The theme could not be loaded.\n\n" +
                $"File:    {SysIO.Path.GetFileName(absolutePath)}\n\n" +
                $"Error:   {ex.Message}",
                "Theme Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            if (_currentThemePath is not null && _currentThemePath != absolutePath)
            {
                try
                {
                    var prev = OxsuitLoader.Load(_currentThemePath);
                    if (prev is not null)
                    {
                        Resources.MergedDictionaries.Clear();
                        Resources.MergedDictionaries.Add(prev);
                    }
                }
                catch { /* silent */ }
            }
        }
    }

    // ── OS title-bar theming (DWM API) ─────────────────────────────────────

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR           = 35;
    private const int DWMWA_TEXT_COLOR              = 36;

    /// <summary>
    /// Colours the OS title bar of <paramref name="target"/> to match the active theme.
    /// Silently no-ops if the HWND is not yet available or the OS doesn't support the API.
    /// </summary>
    private void ApplyTitleBarTheme(Window? target = null)
    {
        var w = target ?? this;
        try
        {
            if (PresentationSource.FromVisual(w) is not HwndSource hwndSource) return;
            var hwnd = hwndSource.Handle;

            var bgColor   = w.TryFindResource("SidebarBgBrush")   is SolidColorBrush sb ? sb.Color : Color.FromRgb(24, 24, 37);
            var textColor = w.TryFindResource("SidebarTextBrush") is SolidColorBrush tb ? tb.Color : Color.FromRgb(205, 214, 244);

            int isDark = RelativeLuminance(bgColor) < 0.5 ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref isDark, sizeof(int));

            int captionColorRef = ToColorRef(bgColor);
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColorRef, sizeof(int));

            int textColorRef = ToColorRef(textColor);
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textColorRef, sizeof(int));
        }
        catch { /* cosmetic-only - never fatal */ }
    }

    /// <summary>
    /// Prepares a freshly created dialog window for themed rendering.
    /// Call immediately after <c>new Window { … }</c>.
    /// </summary>
    private void ApplyThemeToDialog(Window win)
    {
        if (_currentThemePath is not null)
        {
            try
            {
                var dict = OxsuitLoader.Load(_currentThemePath);
                if (dict is not null)
                    win.Resources.MergedDictionaries.Add(dict);
            }
            catch { /* silent - dialog will fall back to defaults */ }
        }

        win.SourceInitialized += (_, _) => ApplyTitleBarTheme(win);
    }

    /// <summary>Converts a WPF Color to a Win32 COLORREF (0x00BBGGRR).</summary>
    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    /// <summary>Returns the relative luminance of a colour (0 = black, 1 = white).</summary>
    private static double RelativeLuminance(Color c)
    {
        static double Lin(double v) =>
            v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return 0.2126 * Lin(c.R / 255.0) +
               0.7152 * Lin(c.G / 255.0) +
               0.0722 * Lin(c.B / 255.0);
    }

    /// <summary>
    /// Shown when a settings or project.json file fails to parse.
    /// Offers to open the file in Notepad or silently reset (already done by caller).
    /// </summary>
    internal void PromptCorruptFile(string filePath, string label)
    {
        var msg = $"⚠  The {label} file could not be read and has been reset to defaults.\n\n" +
                  $"File: {filePath}\n\n" +
                  "Would you like to open the file to inspect or recover it?";

        var result = MessageBox.Show(msg, $"Corrupt {label} File",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes && SysIO.File.Exists(filePath))
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(filePath) { UseShellExecute = true });
            }
            catch { /* best effort */ }
        }
    }

    private static string FormatThemeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0)
            {
                if (char.IsUpper(c) && char.IsLower(name[i - 1]))
                    sb.Append(' ');
                else if (char.IsUpper(c) && char.IsUpper(name[i - 1])
                         && i + 1 < name.Length && char.IsLower(name[i + 1]))
                    sb.Append(' ');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
