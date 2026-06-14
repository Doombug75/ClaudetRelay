using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudetRelay.Models;
using ClaudetRelay.Services;
using NAudio.Wave;

namespace ClaudetRelay;

/// <summary>
/// Modal dialog for creating or editing a single VoiceCommand.
/// Phrase type: phrase text + action.
/// Noise type:  3-slot recording, filter words, auto-detect, default action,
///              optional phrase→action mapping list.
/// </summary>
public sealed class VoiceCommandEditorDialog : Window
{
    private readonly string?                     _themePath;
    private readonly VoiceCommand                _cmd;
    private readonly Func<float[], Task<string?>>? _transcribeFunc;

    // ── Controls — always visible ──────────────────────────────────────────
    private TextBox?    _nameBox;
    private ComboBox?   _typeCombo;

    // ── Phrase panel ───────────────────────────────────────────────────────
    private StackPanel? _phrasePanel;
    private TextBox?    _phraseBox;
    private ComboBox?   _actionCombo;            // single action for Phrase type
    private TextBox?    _insertCharPhraseBox;    // visible when Phrase action = InsertCharacter

    // ── Noise panel ────────────────────────────────────────────────────────
    private StackPanel? _noisePanel;
    private TextBox?    _filterWordsBox;
    private Button?     _autoDetectBtn;
    private ComboBox?   _defaultActionCombo;
    private TextBox?    _insertCharNoiseBox;     // visible when DefaultAction = InsertCharacter
    private StackPanel? _phraseActionsPanel;

    // phrase→action rows (for Noise type)
    private readonly List<(TextBox PhraseBox, ComboBox ActionCombo, TextBox CharBox)> _phraseRows = new();

    // ── Recording / playback ───────────────────────────────────────────────
    private readonly float[]?[]  _samples       = new float[]?[3];
    private readonly Button[]    _recBtns       = new Button[3];
    private readonly Button[]    _playBtns      = new Button[3];
    private readonly TextBlock[] _slotLabels    = new TextBlock[3];
    private WaveInEvent?  _waveIn;
    private List<float>   _captureBuf    = new();
    private int           _recordingSlot = -1;
    private WaveOutEvent? _waveOut;
    private int           _playingSlot   = -1;

    // <param name="transcribeFunc">
    //   If non-null and a model is loaded, the Auto-detect button becomes active.
    //   Returns the ASR text for the given PCM samples (null = no model).
    // </param>
    public VoiceCommandEditorDialog(
        string?                      themePath,
        VoiceCommand                 cmd,
        Func<float[], Task<string?>>? transcribeFunc = null)
    {
        _themePath      = themePath;
        _cmd            = cmd;
        _transcribeFunc = transcribeFunc;

        // Deep-copy noise samples so Cancel works
        for (int i = 0; i < 3; i++)
            _samples[i] = cmd.NoiseSamples[i] is { } s ? (float[])s.Clone() : null;

        Title                 = Properties.Loc.S("VoiceCmdEditor_Title");
        Width                 = 540;
        SizeToContent         = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode            = ResizeMode.NoResize;
        ShowInTaskbar         = false;
        SetResourceReference(BackgroundProperty, "ContentBgBrush");

        if (themePath is not null)
            try { var d = OxsuitLoader.Load(themePath); if (d is not null) Resources.MergedDictionaries.Add(d); }
            catch { }

        SourceInitialized += (_, _) => ParticipantsWindow.TryApplyTitleBarTo(this);
        Closed += (_, _) => CleanupAudio();

        BuildUI();
        UiZoomHelper.Apply(this, UiZoomHelper.FromSettings());
    }

    // ── UI ─────────────────────────────────────────────────────────────────

    private void BuildUI()
    {
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };
        Content = root;

        // Name
        root.Children.Add(Label(Properties.Loc.S("VoiceCmdEditor_NameLabel")));
        _nameBox = Input(_cmd.Name);
        root.Children.Add(_nameBox);

        // Type
        root.Children.Add(Label(Properties.Loc.S("VoiceCmdEditor_TypeLabel")));
        _typeCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 10) };
        _typeCombo.Items.Add(Properties.Loc.S("VoiceCmdEditor_TypePhrase"));
        _typeCombo.Items.Add(Properties.Loc.S("VoiceCmdEditor_TypeNoise"));
        _typeCombo.SelectedIndex = _cmd.Type == VoiceCommandType.Noise ? 1 : 0;
        StyleCombo(_typeCombo);
        root.Children.Add(_typeCombo);

        // ── Phrase panel ──────────────────────────────────────────────────
        _phrasePanel = new StackPanel();

        _phrasePanel.Children.Add(Label(Properties.Loc.S("VoiceCmdEditor_PhraseLabel")));
        _phraseBox = Input(_cmd.Phrase);
        _phrasePanel.Children.Add(_phraseBox);

        _phrasePanel.Children.Add(Label(Properties.Loc.S("VoiceCmdEditor_ActionLabel")));
        _actionCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var a in Enum.GetValues<VoiceCommandAction>())
            if (a != VoiceCommandAction.None)
                _actionCombo.Items.Add(ActionLabel(a));
        // map existing action to combo index (skip None)
        _actionCombo.SelectedIndex = Math.Max(0, (int)_cmd.Action - 1);
        StyleCombo(_actionCombo);
        _phrasePanel.Children.Add(_actionCombo);

        var insertCharPhraseLabel = Label(Properties.Loc.S("VoiceCmd_InsertCharLabel"));
        _insertCharPhraseBox = Input(_cmd.InsertCharacterValue);
        var insertCharPhraseVis = _cmd.Action == VoiceCommandAction.InsertCharacter
            ? Visibility.Visible : Visibility.Collapsed;
        insertCharPhraseLabel.Visibility = insertCharPhraseVis;
        _insertCharPhraseBox.Visibility  = insertCharPhraseVis;
        _phrasePanel.Children.Add(insertCharPhraseLabel);
        _phrasePanel.Children.Add(_insertCharPhraseBox);
        _actionCombo.SelectionChanged += (_, _) =>
        {
            var sel = (VoiceCommandAction)(_actionCombo.SelectedIndex + 1);
            var vis = sel == VoiceCommandAction.InsertCharacter ? Visibility.Visible : Visibility.Collapsed;
            insertCharPhraseLabel.Visibility = vis;
            _insertCharPhraseBox.Visibility  = vis;
        };

        root.Children.Add(_phrasePanel);

        // ── Noise panel ───────────────────────────────────────────────────
        _noisePanel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

        // -- Noise references --
        _noisePanel.Children.Add(SectionHeader(Properties.Loc.S("VoiceCmdEditor_NoiseRefs")));
        for (int i = 0; i < 3; i++)
            _noisePanel.Children.Add(BuildSlotRow(i));

        // -- Filter words --
        _noisePanel.Children.Add(SectionHeader(Properties.Loc.S("VoiceCmdEditor_FilterHeader")));

        var filterHint = new TextBlock
        {
            Text         = Properties.Loc.S("VoiceCmdEditor_FilterHint"),
            FontFamily   = new FontFamily("Segoe UI"),
            FontSize     = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 4),
        };
        filterHint.SetResourceReference(ForegroundProperty, "ContentDimBrush");
        _noisePanel.Children.Add(filterHint);

        var filterRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _filterWordsBox = Input(string.Join("; ", _cmd.NoiseFilterWords));
        _filterWordsBox.Margin = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(_filterWordsBox, 0);
        filterRow.Children.Add(_filterWordsBox);

        _autoDetectBtn = Btn(Properties.Loc.S("VoiceCmdEditor_AutoDetect"));
        _autoDetectBtn.Padding         = new Thickness(8, 4, 8, 4);
        _autoDetectBtn.VerticalAlignment = VerticalAlignment.Center;
        bool canDetect = _transcribeFunc is not null && _samples.Any(s => s is not null);
        _autoDetectBtn.Opacity = canDetect ? 1.0 : 0.35;
        _autoDetectBtn.Cursor  = canDetect
            ? System.Windows.Input.Cursors.Hand
            : System.Windows.Input.Cursors.Arrow;
        _autoDetectBtn.Click += OnAutoDetect;
        Grid.SetColumn(_autoDetectBtn, 1);
        filterRow.Children.Add(_autoDetectBtn);

        _noisePanel.Children.Add(filterRow);

        // -- Default action --
        _noisePanel.Children.Add(SectionHeader(Properties.Loc.S("VoiceCmdEditor_DefaultAction")));

        _defaultActionCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var a in Enum.GetValues<VoiceCommandAction>())
            _defaultActionCombo.Items.Add(ActionLabel(a));
        _defaultActionCombo.SelectedIndex = (int)_cmd.DefaultAction;
        StyleCombo(_defaultActionCombo);
        _noisePanel.Children.Add(_defaultActionCombo);

        var insertCharNoiseLabel = Label(Properties.Loc.S("VoiceCmd_InsertCharLabel"));
        _insertCharNoiseBox = Input(_cmd.InsertCharacterValue);
        var insertCharNoiseVis = _cmd.DefaultAction == VoiceCommandAction.InsertCharacter
            ? Visibility.Visible : Visibility.Collapsed;
        insertCharNoiseLabel.Visibility = insertCharNoiseVis;
        _insertCharNoiseBox.Visibility  = insertCharNoiseVis;
        _noisePanel.Children.Add(insertCharNoiseLabel);
        _noisePanel.Children.Add(_insertCharNoiseBox);
        _defaultActionCombo.SelectionChanged += (_, _) =>
        {
            var sel = (VoiceCommandAction)_defaultActionCombo.SelectedIndex;
            var vis = sel == VoiceCommandAction.InsertCharacter ? Visibility.Visible : Visibility.Collapsed;
            insertCharNoiseLabel.Visibility = vis;
            _insertCharNoiseBox.Visibility  = vis;
        };

        // -- Phrase→action mappings --
        _noisePanel.Children.Add(SectionHeader(Properties.Loc.S("VoiceCmdEditor_PhraseActions")));

        var phraseHint = new TextBlock
        {
            Text         = Properties.Loc.S("VoiceCmdEditor_PhraseActionsHint"),
            FontFamily   = new FontFamily("Segoe UI"),
            FontSize     = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 4),
        };
        phraseHint.SetResourceReference(ForegroundProperty, "ContentDimBrush");
        _noisePanel.Children.Add(phraseHint);

        _phraseActionsPanel = new StackPanel();
        _noisePanel.Children.Add(_phraseActionsPanel);

        // Populate existing phrase→action rows
        foreach (var mapping in _cmd.PhraseActions)
            AddPhraseRow(mapping.Phrase, mapping.Action, mapping.InsertCharacterValue);

        var addPhraseBtn = Btn(Properties.Loc.S("VoiceCmdEditor_AddPhrase"));
        addPhraseBtn.HorizontalAlignment = HorizontalAlignment.Left;
        addPhraseBtn.Margin              = new Thickness(0, 4, 0, 0);
        addPhraseBtn.Click              += (_, _) => AddPhraseRow("", VoiceCommandAction.NewLine);
        _noisePanel.Children.Add(addPhraseBtn);

        root.Children.Add(_noisePanel);

        // ── Footer ────────────────────────────────────────────────────────
        root.Children.Add(new Separator { Margin = new Thickness(0, 16, 0, 12) });

        var btnRow = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancelBtn = Btn(Properties.Loc.S("VoiceCmdEditor_Cancel"));
        cancelBtn.Margin = new Thickness(0, 0, 8, 0);
        cancelBtn.Click += (_, _) => { DialogResult = false; };

        var okBtn = Btn(Properties.Loc.S("VoiceCmdEditor_Save"), isPrimary: true);
        okBtn.Click += OnSave;

        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(okBtn);
        root.Children.Add(btnRow);

        _typeCombo.SelectionChanged += (_, _) => UpdatePanels();
        UpdatePanels();
    }

    // ── Phrase-action rows (Noise type) ────────────────────────────────────

    private void AddPhraseRow(string phrase, VoiceCommandAction action, string charValue = "")
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var phraseBox = Input(phrase);
        phraseBox.Margin = new Thickness(0, 0, 6, 0);
        phraseBox.ToolTip = Properties.Loc.S("VoiceCmdEditor_PhraseRowTooltip");
        Grid.SetColumn(phraseBox, 0);
        row.Children.Add(phraseBox);

        var actionCombo = new ComboBox { Margin = new Thickness(0, 0, 6, 0) };
        foreach (var a in Enum.GetValues<VoiceCommandAction>())
            if (a != VoiceCommandAction.None)
                actionCombo.Items.Add(ActionLabel(a));
        actionCombo.SelectedIndex = Math.Max(0, (int)action - 1);
        StyleCombo(actionCombo);
        Grid.SetColumn(actionCombo, 1);
        row.Children.Add(actionCombo);

        var charBox = Input(charValue);
        charBox.Width      = 60;
        charBox.Margin     = new Thickness(0, 0, 6, 0);
        charBox.ToolTip    = Properties.Loc.S("VoiceCmd_InsertCharTooltip");
        charBox.Visibility = action == VoiceCommandAction.InsertCharacter ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(charBox, 2);
        row.Children.Add(charBox);

        actionCombo.SelectionChanged += (_, _) =>
        {
            var sel = (VoiceCommandAction)(actionCombo.SelectedIndex + 1);
            charBox.Visibility = sel == VoiceCommandAction.InsertCharacter ? Visibility.Visible : Visibility.Collapsed;
        };

        var removeBtn = Btn("✕");
        removeBtn.Padding = new Thickness(6, 3, 6, 3);
        removeBtn.VerticalAlignment = VerticalAlignment.Center;
        removeBtn.Click += (_, _) =>
        {
            _phraseRows.RemoveAll(r => r.PhraseBox == phraseBox);
            _phraseActionsPanel?.Children.Remove(row);
        };
        Grid.SetColumn(removeBtn, 3);
        row.Children.Add(removeBtn);

        _phraseActionsPanel?.Children.Add(row);
        _phraseRows.Add((phraseBox, actionCombo, charBox));
    }

    // ── Panel visibility ───────────────────────────────────────────────────

    private void UpdatePanels()
    {
        bool isNoise = (_typeCombo?.SelectedIndex ?? 0) == 1;
        if (_phrasePanel is not null)
            _phrasePanel.Visibility = isNoise ? Visibility.Collapsed : Visibility.Visible;
        if (_noisePanel is not null)
            _noisePanel.Visibility = isNoise ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Noise slot rows ────────────────────────────────────────────────────

    private UIElement BuildSlotRow(int idx)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 4, 0, 4),
        };

        var lbl = new TextBlock
        {
            Text              = $"{idx + 1}.",
            Width             = 20,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily        = new FontFamily("Segoe UI"),
            FontSize          = 12,
        };
        lbl.SetResourceReference(ForegroundProperty, "ContentTextBrush");
        row.Children.Add(lbl);

        _slotLabels[idx] = new TextBlock
        {
            Width             = 160,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily        = new FontFamily("Segoe UI"),
            FontSize          = 11,
            Margin            = new Thickness(4, 0, 8, 0),
        };
        _slotLabels[idx].SetResourceReference(ForegroundProperty, "ContentDimBrush");
        row.Children.Add(_slotLabels[idx]);
        UpdateSlotLabel(idx);

        int capturedIdx = idx;

        var recBtn = Btn("⏺");
        recBtn.ToolTip = Properties.Loc.S("VoiceCmdEditor_RecordTooltip");
        recBtn.Margin  = new Thickness(0, 0, 4, 0);
        recBtn.Click  += (_, _) => ToggleRecord(capturedIdx);
        _recBtns[idx]  = recBtn;
        row.Children.Add(recBtn);

        var playBtn = Btn("▶");
        playBtn.ToolTip = Properties.Loc.S("VoiceCmdEditor_PlayBackTooltip");
        playBtn.Opacity = _samples[idx] is not null ? 1.0 : 0.35;
        playBtn.Cursor  = _samples[idx] is not null
            ? System.Windows.Input.Cursors.Hand
            : System.Windows.Input.Cursors.Arrow;
        playBtn.Click  += (_, _) => PlaySlot(capturedIdx);
        _playBtns[idx]  = playBtn;
        row.Children.Add(playBtn);

        return row;
    }

    // ── Auto-detect ────────────────────────────────────────────────────────

    private async void OnAutoDetect(object sender, RoutedEventArgs e)
    {
        if (_transcribeFunc is null) return;
        var recorded = _samples.Where(s => s is not null).ToArray();
        if (recorded.Length == 0) return;

        _autoDetectBtn!.IsEnabled = false;
        var origLabel = _autoDetectBtn.Content;
        _autoDetectBtn.Content = Properties.Loc.S("VoiceCmdEditor_Detecting");

        var existing = (_filterWordsBox?.Text ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.ToLowerInvariant())
            .ToHashSet();

        var newWords  = new List<string>();
        int nullCount = 0;

        foreach (var sample in recorded)
        {
            try
            {
                var result = await _transcribeFunc(sample!);
                if (result is null) { nullCount++; continue; }
                if (!string.IsNullOrWhiteSpace(result))
                {
                    var word = result.Trim();
                    if (!existing.Contains(word.ToLowerInvariant()))
                    {
                        existing.Add(word.ToLowerInvariant());
                        newWords.Add(word);
                    }
                }
            }
            catch { nullCount++; }
        }

        _autoDetectBtn.Content   = origLabel;
        _autoDetectBtn.IsEnabled = true;

        // All samples returned null → no model loaded
        if (nullCount == recorded.Length)
        {
            MessageBox.Show(
                Properties.Loc.S("VoiceCmdEditor_NoModel"),
                Properties.Loc.S("VoiceCmdEditor_NoModelTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (newWords.Count > 0 && _filterWordsBox is not null)
        {
            var current = _filterWordsBox.Text.Trim().TrimEnd(';');
            _filterWordsBox.Text = current.Length > 0
                ? current + "; " + string.Join("; ", newWords)
                : string.Join("; ", newWords);
        }
    }

    // ── Recording ──────────────────────────────────────────────────────────

    private void ToggleRecord(int slot)
    {
        if (_recordingSlot == slot) { StopRecording(); return; }
        if (_recordingSlot >= 0)      StopRecording();
        StartRecording(slot);
    }

    private void StartRecording(int slot)
    {
        if (_playingSlot >= 0) StopPlayback();
        _recordingSlot = slot;
        _captureBuf.Clear();

        _waveIn = new WaveInEvent
        {
            WaveFormat         = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 80,
        };
        _waveIn.DataAvailable += (_, ev) =>
        {
            for (int i = 0; i < ev.BytesRecorded; i += 2)
            {
                short s = (short)(ev.Buffer[i] | (ev.Buffer[i + 1] << 8));
                _captureBuf.Add(s / 32768f);
            }
        };
        _waveIn.StartRecording();

        _recBtns[slot].Content = "⏹";
        _recBtns[slot].SetResourceReference(BackgroundProperty, "PrimaryAccentBrush");
        _slotLabels[slot].Text = Properties.Loc.S("VoiceCmdEditor_Recording");
        _playBtns[slot].Opacity = 0.35;
        _playBtns[slot].Cursor  = System.Windows.Input.Cursors.Arrow;
    }

    private void StopRecording()
    {
        if (_recordingSlot < 0) return;
        int slot = _recordingSlot;
        _recordingSlot = -1;

        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _waveIn = null;

        if (_captureBuf.Count > 800)   // >50 ms at 16 kHz
            _samples[slot] = _captureBuf.ToArray();
        _captureBuf.Clear();

        Dispatcher.Invoke(() =>
        {
            _recBtns[slot].Content = "⏺";
            _recBtns[slot].SetResourceReference(BackgroundProperty, "ControlBgBrush");
            bool hasData = _samples[slot] is not null;
            _playBtns[slot].Opacity = hasData ? 1.0 : 0.35;
            _playBtns[slot].Cursor  = hasData
                ? System.Windows.Input.Cursors.Hand
                : System.Windows.Input.Cursors.Arrow;
            UpdateSlotLabel(slot);
            RefreshAutoDetectButton();
        });
    }

    private void RefreshAutoDetectButton()
    {
        if (_autoDetectBtn is null) return;
        bool canDetect = _transcribeFunc is not null && _samples.Any(s => s is not null);
        _autoDetectBtn.Opacity = canDetect ? 1.0 : 0.35;
        _autoDetectBtn.Cursor  = canDetect
            ? System.Windows.Input.Cursors.Hand
            : System.Windows.Input.Cursors.Arrow;
    }

    // ── Playback ───────────────────────────────────────────────────────────

    private void PlaySlot(int slot)
    {
        if (_samples[slot] is null) return;
        if (_playingSlot >= 0)  StopPlayback();
        if (_recordingSlot >= 0) StopRecording();

        _playingSlot = slot;
        _playBtns[slot].Content = "⏹";

        var pcm   = _samples[slot]!;
        var bytes = new byte[pcm.Length * 2];
        for (int i = 0; i < pcm.Length; i++)
        {
            short s = (short)(Math.Clamp(pcm[i], -1f, 1f) * 32767f);
            bytes[i * 2]     = (byte)(s & 0xFF);
            bytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }

        var ms  = new System.IO.MemoryStream(bytes);
        var rs  = new NAudio.Wave.RawSourceWaveStream(ms, new WaveFormat(16000, 16, 1));
        _waveOut = new WaveOutEvent();
        _waveOut.Init(rs);
        _waveOut.PlaybackStopped += (_, _) => Dispatcher.Invoke(() =>
        {
            _playBtns[slot].Content = "▶";
            _playingSlot = -1;
            _waveOut?.Dispose();
            _waveOut = null;
        });
        _waveOut.Play();
    }

    private void StopPlayback()
    {
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;
        if (_playingSlot >= 0)
        {
            int s = _playingSlot;
            _playingSlot = -1;
            _playBtns[s].Content = "▶";
        }
    }

    private void CleanupAudio()
    {
        if (_recordingSlot >= 0) StopRecording();
        if (_playingSlot  >= 0) StopPlayback();
    }

    // ── Save ───────────────────────────────────────────────────────────────

    private void OnSave(object sender, RoutedEventArgs e)
    {
        CleanupAudio();

        var name = _nameBox?.Text.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show(Properties.Loc.S("VoiceCmdEditor_NameRequired"),
                Properties.Loc.S("VoiceCmdEditor_NameRequiredTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool isNoise = (_typeCombo?.SelectedIndex ?? 0) == 1;

        if (!isNoise)
        {
            // Phrase type
            var phrase = _phraseBox?.Text.Trim() ?? "";
            if (string.IsNullOrEmpty(phrase))
            {
                MessageBox.Show(
                    Properties.Loc.S("VoiceCmdEditor_PhraseRequired"),
                    Properties.Loc.S("VoiceCmdEditor_PhraseRequiredTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _cmd.Name   = name;
            _cmd.Type   = VoiceCommandType.Phrase;
            _cmd.Phrase = phrase;
            // action combo skips None, so index 0 = NewLine (enum index 1)
            _cmd.Action = (VoiceCommandAction)((_actionCombo?.SelectedIndex ?? 0) + 1);
            _cmd.InsertCharacterValue = _insertCharPhraseBox?.Text ?? "";
        }
        else
        {
            // Noise type
            if (!_samples.Any(s => s is not null))
            {
                MessageBox.Show(
                    Properties.Loc.S("VoiceCmdEditor_NoRecording"),
                    Properties.Loc.S("VoiceCmdEditor_NoRecordingTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _cmd.Name = name;
            _cmd.Type = VoiceCommandType.Noise;
            for (int i = 0; i < 3; i++)
                _cmd.NoiseSamples[i] = _samples[i];

            // Filter words
            _cmd.NoiseFilterWords = (_filterWordsBox?.Text ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(w => !string.IsNullOrEmpty(w))
                .ToList();

            // Default action
            _cmd.DefaultAction = (VoiceCommandAction)(_defaultActionCombo?.SelectedIndex ?? 0);
            _cmd.InsertCharacterValue = _insertCharNoiseBox?.Text ?? "";

            // Phrase→action mappings
            _cmd.PhraseActions = _phraseRows
                .Select(r => new PhraseActionMapping
                {
                    Phrase               = r.PhraseBox.Text.Trim(),
                    Action               = (VoiceCommandAction)(r.ActionCombo.SelectedIndex + 1),
                    InsertCharacterValue = r.CharBox.Text,
                })
                .Where(m => !string.IsNullOrEmpty(m.Phrase))
                .ToList();
        }

        DialogResult = true;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private void UpdateSlotLabel(int slot)
    {
        if (_samples[slot] is { } s)
        {
            float dur = (float)s.Length / 16000f;
            _slotLabels[slot].Text = $"{Properties.Loc.S("VoiceCmdEditor_Recorded")}  ({dur:F2} s)";
        }
        else
        {
            _slotLabels[slot].Text = Properties.Loc.S("VoiceCmdEditor_NotRecorded");
        }
    }

    private static string ActionLabel(VoiceCommandAction a) => a switch
    {
        VoiceCommandAction.None               => Properties.Loc.S("VoiceCmdEditor_ActionNoneImmediate"),
        VoiceCommandAction.NewLine            => Properties.Loc.S("VoiceCmd_ActionNewLine"),
        VoiceCommandAction.DeleteLastWord     => Properties.Loc.S("VoiceCmd_ActionDeleteLastWord"),
        VoiceCommandAction.DeleteLastSentence => Properties.Loc.S("VoiceCmd_ActionDeleteLastSentence"),
        VoiceCommandAction.DeleteAll          => Properties.Loc.S("VoiceCmd_ActionDeleteAll"),
        VoiceCommandAction.Send               => Properties.Loc.S("VoiceCmd_ActionSend"),
        VoiceCommandAction.Undo               => Properties.Loc.S("VoiceCmd_ActionUndo"),
        VoiceCommandAction.InsertCharacter    => Properties.Loc.S("VoiceCmd_ActionInsertCharacter"),
        _                                     => a.ToString(),
    };

    private TextBlock SectionHeader(string text)
    {
        var tb = new TextBlock
        {
            Text       = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 11,
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, 10, 0, 2),
        };
        tb.SetResourceReference(ForegroundProperty, "ContentTextBrush");
        return tb;
    }

    private TextBlock Label(string text)
    {
        var tb = new TextBlock
        {
            Text         = text,
            FontFamily   = new FontFamily("Segoe UI"),
            FontSize     = 11,
            Margin       = new Thickness(0, 8, 0, 2),
            TextWrapping = TextWrapping.Wrap,
        };
        tb.SetResourceReference(ForegroundProperty, "ContentDimBrush");
        return tb;
    }

    private TextBox Input(string text)
    {
        var tb = new TextBox
        {
            Text            = text,
            FontFamily      = new FontFamily("Segoe UI"),
            FontSize        = 12,
            Padding         = new Thickness(8, 6, 8, 6),
            BorderThickness = new Thickness(1),
            Margin          = new Thickness(0, 0, 0, 4),
        };
        tb.SetResourceReference(ForegroundProperty, "InputTextBrush");
        tb.SetResourceReference(BackgroundProperty, "ControlBgBrush");
        tb.SetResourceReference(BorderBrushProperty, "ControlBorderBrush");
        return tb;
    }

    private void StyleCombo(ComboBox cb)
    {
        cb.FontFamily      = new FontFamily("Segoe UI");
        cb.FontSize        = 12;
        cb.Padding         = new Thickness(8, 6, 8, 6);
        cb.BorderThickness = new Thickness(1);
        cb.SetResourceReference(StyleProperty,       "ModernComboBox");
        cb.SetResourceReference(ForegroundProperty,  "InputTextBrush");
        cb.SetResourceReference(BackgroundProperty,  "ControlBgBrush");
        cb.SetResourceReference(BorderBrushProperty, "ControlBorderBrush");
    }

    private Button Btn(string label, bool isPrimary = false)
    {
        var b = new Button
        {
            Content         = label,
            FontFamily      = new FontFamily("Segoe UI"),
            FontSize        = 12,
            Padding         = new Thickness(10, 5, 10, 5),
            BorderThickness = new Thickness(1),
            Cursor          = System.Windows.Input.Cursors.Hand,
        };
        if (isPrimary)
        {
            b.SetResourceReference(BackgroundProperty, "PrimaryAccentBrush");
            b.SetResourceReference(ForegroundProperty, "AccentTextBrush");
        }
        else
        {
            b.SetResourceReference(BackgroundProperty, "ControlBgBrush");
            b.SetResourceReference(ForegroundProperty, "ContentTextBrush");
        }
        b.SetResourceReference(BorderBrushProperty, "ControlBorderBrush");
        return b;
    }
}
