using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudetRelay.Models;
using ClaudetRelay.Services;

namespace ClaudetRelay;

/// <summary>
/// Card-based list of user-defined voice commands.
/// Each card shows the command name, type badge, action, and enable toggle.
/// Add / Edit opens VoiceCommandEditorDialog.
/// Changes are saved to SettingsService on close.
/// </summary>
public sealed class VoiceCommandsWindow : Window
{
    private readonly string?                      _themePath;
    private readonly List<VoiceCommand>           _commands;
    private readonly Func<float[], Task<string?>>? _transcribeFunc;
    private readonly Func<string>?                 _getDiagnostics;
    private StackPanel?                            _cardPanel;

    public VoiceCommandsWindow(string? themePath, Func<float[], Task<string?>>? transcribeFunc = null, Func<string>? getDiagnostics = null)
    {
        _themePath      = themePath;
        _transcribeFunc = transcribeFunc;
        _getDiagnostics = getDiagnostics;
        _commands       = SettingsService.Load().VoiceCommands;

        Title                 = Properties.Loc.S("VoiceCmds_WindowTitle");
        Width                 = 600;
        Height                = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode            = ResizeMode.CanResize;
        ShowInTaskbar         = false;
        SetResourceReference(BackgroundProperty, "ContentBgBrush");

        if (themePath is not null)
            try { var d = OxsuitLoader.Load(themePath); if (d is not null) Resources.MergedDictionaries.Add(d); }
            catch { }

        SourceInitialized += (_, _) => ParticipantsWindow.TryApplyTitleBarTo(this);
        BuildUI();
        UiZoomHelper.Apply(this, UiZoomHelper.FromSettings());
    }

    internal Panel BuildTabContent()
    {
        var root = new Grid();
        PopulateContent(root);
        return root;
    }

    // ── UI ─────────────────────────────────────────────────────────────────

    private void BuildUI()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;
        PopulateContent(root);
    }

    private void PopulateContent(Grid root)
    {
        // ── Header ─────────────────────────────────────────────────────────
        var header = new Border
        {
            Padding = new Thickness(20, 16, 20, 12),
        };
        header.SetResourceReference(Border.BackgroundProperty, "SidebarBgBrush");

        var headerRow = new Grid();
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleBlock = new TextBlock
        {
            Text       = Properties.Loc.S("VoiceCmds_Header"),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 15,
            FontWeight = FontWeights.SemiBold,
        };
        titleBlock.SetResourceReference(ForegroundProperty, "ContentTextBrush");
        Grid.SetColumn(titleBlock, 0);
        headerRow.Children.Add(titleBlock);

        var addBtn = AccentBtn("＋  " + Properties.Loc.S("VoiceCmds_AddCommand"));
        addBtn.Click += OnAddCommand;
        Grid.SetColumn(addBtn, 1);
        headerRow.Children.Add(addBtn);

        header.Child = headerRow;
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // ── Hint ───────────────────────────────────────────────────────────
        var hint = new TextBlock
        {
            Text         = Properties.Loc.S("VoiceCmds_Hint"),
            FontFamily   = new FontFamily("Segoe UI"),
            FontSize     = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(20, 10, 20, 0),
        };
        hint.SetResourceReference(ForegroundProperty, "ContentDimBrush");

        // ── Noise-distinctness tip ─────────────────────────────────────────
        var noiseTip = new TextBlock
        {
            Text         = Properties.Loc.S("VoiceCmds_NoiseTip"),
            FontFamily   = new FontFamily("Segoe UI"),
            FontSize     = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(20, 6, 20, 0),
        };
        noiseTip.SetResourceReference(ForegroundProperty, "ContentDimBrush");

        // ── Card scroll area ───────────────────────────────────────────────
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0),
        };

        var cardWrapper = new StackPanel { Margin = new Thickness(20, 0, 20, 12) };
        cardWrapper.Children.Add(hint);
        cardWrapper.Children.Add(noiseTip);

        _cardPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        cardWrapper.Children.Add(_cardPanel);
        scroll.Content = cardWrapper;

        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        // ── Footer ─────────────────────────────────────────────────────────
        var footer = new Border
        {
            Padding = new Thickness(20, 10, 20, 14),
        };
        footer.SetResourceReference(Border.BackgroundProperty, "SidebarBgBrush");

        var footerRow = new Grid();
        footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (_getDiagnostics is not null)
        {
            var diagBtn = new Button
            {
                Content             = Properties.Loc.S("VoiceCmds_Diagnostics"),
                FontFamily          = new FontFamily("Segoe UI"),
                FontSize            = 12,
                Padding             = new Thickness(12, 5, 12, 5),
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor              = System.Windows.Input.Cursors.Hand,
            };
            diagBtn.SetResourceReference(StyleProperty, "ModernButton");
            diagBtn.Click += (_, _) => ShowDiagnosticsWindow(_getDiagnostics(), this);
            Grid.SetColumn(diagBtn, 0);
            footerRow.Children.Add(diagBtn);
        }

        var closeBtn = AccentBtn(Properties.Loc.S("VoiceCmds_Close"));
        closeBtn.HorizontalAlignment = HorizontalAlignment.Right;
        closeBtn.Click += (_, _) => { Save(); Close(); };
        Grid.SetColumn(closeBtn, 2);
        footerRow.Children.Add(closeBtn);
        footer.Child = footerRow;

        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        RefreshCards();
    }

    // ── Cards ──────────────────────────────────────────────────────────────

    private void RefreshCards()
    {
        if (_cardPanel is null) return;
        _cardPanel.Children.Clear();

        if (_commands.Count == 0)
        {
            var empty = new TextBlock
            {
                Text         = Properties.Loc.S("VoiceCmds_Empty"),
                FontFamily   = new FontFamily("Segoe UI"),
                FontSize     = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 16, 0, 0),
            };
            empty.SetResourceReference(ForegroundProperty, "ContentDimBrush");
            _cardPanel.Children.Add(empty);
            return;
        }

        foreach (var cmd in _commands)
            _cardPanel.Children.Add(BuildCard(cmd));
    }

    private UIElement BuildCard(VoiceCommand cmd)
    {
        var card = new Border
        {
            Margin          = new Thickness(0, 0, 0, 8),
            Padding         = new Thickness(14, 10, 14, 10),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(6),
        };
        card.SetResourceReference(Border.BackgroundProperty, "ControlBgBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // ── Left: info ────────────────────────────────────────────────────
        var info = new StackPanel();
        Grid.SetColumn(info, 0);

        // Row 1: name + type badge + action
        var row1 = new WrapPanel { Orientation = Orientation.Horizontal };

        var nameText = new TextBlock
        {
            Text       = cmd.Name,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 13,
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        nameText.SetResourceReference(ForegroundProperty, "ContentTextBrush");
        row1.Children.Add(nameText);

        row1.Children.Add(TypeBadge(cmd.Type));

        var effectiveAction = cmd.Type == VoiceCommandType.Noise ? cmd.DefaultAction : cmd.Action;
        var actionText = new TextBlock
        {
            Text              = "→  " + ActionLabel(effectiveAction),
            FontFamily        = new FontFamily("Segoe UI"),
            FontSize          = 11,
            Margin            = new Thickness(8, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        actionText.SetResourceReference(ForegroundProperty, "ContentDimBrush");
        row1.Children.Add(actionText);

        if (effectiveAction == VoiceCommandAction.InsertCharacter)
        {
            var charBox = new TextBox
            {
                Text              = cmd.InsertCharacterValue,
                Width             = 90,
                FontSize          = 12,
                Padding           = new Thickness(4, 1, 4, 1),
                Margin            = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip           = Properties.Loc.S("VoiceCmd_InsertCharTooltip"),
            };
            charBox.SetResourceReference(ForegroundProperty, "InputTextBrush");
            charBox.SetResourceReference(BackgroundProperty, "ControlBgBrush");
            charBox.SetResourceReference(BorderBrushProperty, "ControlBorderBrush");
            charBox.TextChanged += (_, _) => { cmd.InsertCharacterValue = charBox.Text; Save(); };
            row1.Children.Add(charBox);
        }

        info.Children.Add(row1);

        // Row 2: phrase / noise summary
        var detail = new TextBlock
        {
            FontFamily   = new FontFamily("Segoe UI"),
            FontSize     = 11,
            Margin       = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        detail.SetResourceReference(ForegroundProperty, "ContentDimBrush");

        if (cmd.Type == VoiceCommandType.Phrase)
            detail.Text = "\"" + cmd.Phrase + "\"";
        else
        {
            var parts = new System.Collections.Generic.List<string> { NoiseSummary(cmd) };
            if (cmd.PhraseActions.Count > 0)
                parts.Add(string.Format(Properties.Loc.S("VoiceCmds_PhraseCommandCount"), cmd.PhraseActions.Count));
            detail.Text = string.Join("  |  ", parts);
        }

        info.Children.Add(detail);
        grid.Children.Add(info);

        // ── Right: controls ───────────────────────────────────────────────
        var controls = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(controls, 1);

        // Enable toggle
        var toggle = new CheckBox
        {
            IsChecked         = cmd.Enabled,
            ToolTip           = Properties.Loc.S("VoiceCmds_EnabledTooltip"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 8, 0),
        };
        toggle.Checked   += (_, _) => { cmd.Enabled = true;  Save(); };
        toggle.Unchecked += (_, _) => { cmd.Enabled = false; Save(); };
        controls.Children.Add(toggle);

        var editBtn = SmallBtn("✏");
        editBtn.ToolTip = Properties.Loc.S("VoiceCmds_EditTooltip");
        editBtn.Click  += (_, _) => OnEditCommand(cmd);
        editBtn.Margin  = new Thickness(0, 0, 4, 0);
        controls.Children.Add(editBtn);

        var delBtn = SmallBtn("🗑");
        delBtn.ToolTip = Properties.Loc.S("VoiceCmds_DeleteTooltip");
        delBtn.Click  += (_, _) => OnDeleteCommand(cmd);
        controls.Children.Add(delBtn);

        grid.Children.Add(controls);
        card.Child = grid;
        return card;
    }

    // ── Actions ────────────────────────────────────────────────────────────

    private void OnAddCommand(object sender, RoutedEventArgs e)
    {
        var cmd = new VoiceCommand { Name = Properties.Loc.S("VoiceCmds_NewCommandName") };
        var dlg = new VoiceCommandEditorDialog(_themePath, cmd, _transcribeFunc) { Owner = Window.GetWindow(this) ?? this };
        if (dlg.ShowDialog() == true)
        {
            _commands.Add(cmd);
            Save();
            RefreshCards();
        }
    }

    private void OnEditCommand(VoiceCommand cmd)
    {
        var dlg = new VoiceCommandEditorDialog(_themePath, cmd, _transcribeFunc) { Owner = Window.GetWindow(this) ?? this };
        if (dlg.ShowDialog() == true)
        {
            Save();
            RefreshCards();
        }
    }

    private void OnDeleteCommand(VoiceCommand cmd)
    {
        var msg = string.Format(Properties.Loc.S("VoiceCmds_DeleteConfirm"), cmd.Name);
        if (MessageBox.Show(msg, Properties.Loc.S("VoiceCmds_DeleteTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _commands.Remove(cmd);
            Save();
            RefreshCards();
        }
    }

    private void Save()
    {
        var s = SettingsService.Load();
        s.VoiceCommands = _commands;
        SettingsService.Save(s);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string NoiseSummary(VoiceCommand cmd)
    {
        int recorded = cmd.NoiseSamples.Count(s => s is not null);
        return string.Format(Properties.Loc.S("VoiceCmds_References"), recorded);
    }

    private static string ActionLabel(VoiceCommandAction a) => a switch
    {
        VoiceCommandAction.None               => Properties.Loc.S("VoiceCmd_ActionNone"),
        VoiceCommandAction.NewLine            => Properties.Loc.S("VoiceCmd_ActionNewLine"),
        VoiceCommandAction.DeleteLastWord     => Properties.Loc.S("VoiceCmd_ActionDeleteLastWord"),
        VoiceCommandAction.DeleteLastSentence => Properties.Loc.S("VoiceCmd_ActionDeleteLastSentence"),
        VoiceCommandAction.DeleteAll          => Properties.Loc.S("VoiceCmd_ActionDeleteAll"),
        VoiceCommandAction.Send               => Properties.Loc.S("VoiceCmd_ActionSend"),
        VoiceCommandAction.Undo               => Properties.Loc.S("VoiceCmd_ActionUndo"),
        VoiceCommandAction.InsertCharacter    => Properties.Loc.S("VoiceCmd_ActionInsertCharacter"),
        _                                     => a.ToString(),
    };

    private static UIElement TypeBadge(VoiceCommandType type)
    {
        string label = type switch
        {
            VoiceCommandType.Phrase => Properties.Loc.S("VoiceCmd_TypePhrase"),
            VoiceCommandType.Noise  => Properties.Loc.S("VoiceCmd_TypeNoise"),
            _                       => type.ToString(),
        };
        var border = new Border
        {
            Padding         = new Thickness(6, 1, 6, 1),
            CornerRadius    = new CornerRadius(10),
            Margin          = new Thickness(0, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        border.SetResourceReference(Border.BackgroundProperty, "PrimaryAccentBrush");

        var tb = new TextBlock
        {
            Text       = label,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 10,
            FontWeight = FontWeights.SemiBold,
        };
        tb.SetResourceReference(ForegroundProperty, "AccentTextBrush");
        border.Child = tb;
        return border;
    }

    private static void ShowDiagnosticsWindow(string text, Window owner)
    {
        var win = new Window
        {
            Title                 = Properties.Loc.S("VoiceCmds_Diagnostics"),
            Width                 = 560,
            Height                = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = owner,
            ShowInTaskbar         = false,
            ResizeMode            = ResizeMode.CanResize,
        };
        foreach (var dict in owner.Resources.MergedDictionaries)
            win.Resources.MergedDictionaries.Add(dict);
        win.SetResourceReference(BackgroundProperty, "ContentBgBrush");

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var tb = new TextBox
        {
            Text                = text,
            IsReadOnly          = true,
            AcceptsReturn       = true,
            TextWrapping        = TextWrapping.NoWrap,
            FontFamily          = new FontFamily("Consolas, Courier New"),
            FontSize            = 11,
            Margin              = new Thickness(12),
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness     = new Thickness(0),
        };
        tb.SetResourceReference(ForegroundProperty, "ContentTextBrush");
        tb.SetResourceReference(BackgroundProperty, "ContentBgBrush");
        Grid.SetRow(tb, 0);
        grid.Children.Add(tb);

        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(12, 0, 12, 12),
        };
        Grid.SetRow(btnRow, 1);

        var copyBtn = new Button
        {
            Content    = Properties.Loc.S("VoiceCmds_DiagCopy"),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 12,
            Padding    = new Thickness(14, 5, 14, 5),
            Margin     = new Thickness(0, 0, 8, 0),
            Cursor     = System.Windows.Input.Cursors.Hand,
        };
        copyBtn.SetResourceReference(StyleProperty, "ModernButton");
        copyBtn.Click += (_, _) =>
        {
            Clipboard.SetText(tb.Text);
            copyBtn.Content = Properties.Loc.S("VoiceCmds_DiagCopied");
        };

        var closeBtn = new Button
        {
            Content    = Properties.Loc.S("VoiceCmds_Close"),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 12,
            Padding    = new Thickness(14, 5, 14, 5),
            Cursor     = System.Windows.Input.Cursors.Hand,
            IsCancel   = true,
        };
        closeBtn.SetResourceReference(StyleProperty, "ModernButton");
        closeBtn.Click += (_, _) => win.Close();

        btnRow.Children.Add(copyBtn);
        btnRow.Children.Add(closeBtn);
        grid.Children.Add(btnRow);

        win.Content = grid;
        win.ShowDialog();
    }

    private Button AccentBtn(string label)
    {
        var b = new Button
        {
            Content         = label,
            FontFamily      = new FontFamily("Segoe UI"),
            FontSize        = 12,
            Padding         = new Thickness(12, 6, 12, 6),
            BorderThickness = new Thickness(1),
            Cursor          = System.Windows.Input.Cursors.Hand,
        };
        b.SetResourceReference(BackgroundProperty, "PrimaryAccentBrush");
        b.SetResourceReference(ForegroundProperty, "AccentTextBrush");
        b.SetResourceReference(BorderBrushProperty, "ControlBorderBrush");
        return b;
    }

    private Button SmallBtn(string label)
    {
        var b = new Button
        {
            Content         = label,
            FontFamily      = new FontFamily("Segoe UI"),
            FontSize        = 12,
            Padding         = new Thickness(6, 3, 6, 3),
            BorderThickness = new Thickness(1),
            Cursor          = System.Windows.Input.Cursors.Hand,
        };
        b.SetResourceReference(BackgroundProperty, "ControlBgBrush");
        b.SetResourceReference(ForegroundProperty, "ContentTextBrush");
        b.SetResourceReference(BorderBrushProperty, "ControlBorderBrush");
        return b;
    }
}
