using System.Net.Http;
using SysIO = System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClaudetRelay.Services;

namespace ClaudetRelay;

public partial class MainWindow : Window
{
    // ── Nested types ───────────────────────────────────────────────────────

    private sealed class OllamaParticipant
    {
        public required OllamaService Service  { get; init; }
        public int   Position { get; set; }
        public bool  Enabled  { get; set; } = true;
        public bool? IsOnline { get; set; }   // null = noch unbekannt

        public string ColorKey    => Position switch { 2 => "AccentBrush", 3 => "ClaudeBrush", _ => "OllamaBrush" };
        public string AvatarLabel => $"O{Position}";
    }

    private sealed class OllamaParticipantUI
    {
        public required OllamaParticipant Data          { get; init; }
        public required Border            Card          { get; init; }
        public required Border            AvatarBorder  { get; init; }
        public required TextBlock         AvatarText    { get; init; }
        public required TextBlock         NameLabel     { get; init; }
        public required Ellipse           StatusDot     { get; init; }
        public required TextBlock         ModelLabel    { get; init; }
        public required TextBlock         OfflineLabel  { get; init; }
        public required Popup             Popup         { get; init; }
        public required TextBlock         PopupTitle    { get; init; }
        public required CheckBox          EnabledToggle { get; init; }
        public required ListBox           ModelList     { get; init; }
        public required Button            RemoveButton  { get; init; }
    }

    // ── Dienste ────────────────────────────────────────────────────────────
    private ClaudeService?                     _claude;
    private readonly List<OllamaParticipantUI> _ollamaParticipants    = [];
    private readonly List<OllamaChatMessage>   _sharedHistory         = [];
    private readonly List<ClaudeChatMessage>   _claudeHistory         = [];
    private CancellationTokenSource?           _streamCts;
    private List<string>                       _availableOllamaModels = [];

    // ── Claude-Status ──────────────────────────────────────────────────────
    private bool? _isClaudeOnline;
    private bool  _claudeEnabled = true;

    private static readonly string[] ClaudeModels =
    [
        "claude-sonnet-4-20250514",
        "claude-haiku-4-5-20251001",
        "claude-opus-4-20250514",
    ];

    // ──────────────────────────────────────────────────────────────────────

    // ── Theme-State ────────────────────────────────────────────────────────
    private string? _currentThemePath;

    public MainWindow()
    {
        InitializeComponent();
        LoadThemesIntoComboBox();   // Load theme before first render
        Loaded += async (_, _) =>
        {
            InitializeServices();
            PopulateClaudeModelList();
            AddSystemMessage("Chat started  ·  Claude, Ollama and You are connected.");
            InputTextBox.Focus();
            await CheckAllStatusAsync();
            StartStatusTimer();
        };
    }

    // ── Initialisierung ────────────────────────────────────────────────────

    private void InitializeServices()
    {
        var settings = SettingsService.Load();

        // Erster Ollama-Teilnehmer
        AddOllamaParticipant(settings.OllamaModel);

        // Claude
        if (!string.IsNullOrWhiteSpace(settings.ClaudeApiKey))
        {
            _claude = new ClaudeService(settings.ClaudeApiKey);
        }
        else
        {
            ClaudeModelLabel.Text = "No API key";
            ClaudeStatusDot.SetResourceReference(Ellipse.FillProperty, "AccentBrush");
            ClaudeCard.Opacity    = 0.6;
            ClaudeEnabledToggle.IsChecked = false;
            ClaudeEnabledToggle.IsEnabled = false;
            AddSystemMessage("ℹ  No Claude API key in Settings/settings.json — Claude will be skipped.");
        }
    }

    private void PopulateClaudeModelList()
    {
        ClaudeModelList.Items.Clear();
        foreach (var m in ClaudeModels)
            ClaudeModelList.Items.Add(m);
        ClaudeModelList.SelectedItem = _claude?.CurrentModel ?? ClaudeModels[0];
    }

    private async Task LoadOllamaModelsAsync()
    {
        if (_ollamaParticipants.Count == 0) return;
        try
        {
            // Alle Teilnehmer nutzen denselben Ollama-Endpunkt → einmal laden
            var models = await _ollamaParticipants[0].Data.Service.GetModelsAsync();
            _availableOllamaModels = models;

            foreach (var ui in _ollamaParticipants)
            {
                if (models.Count > 0)
                {
                    if (!models.Contains(ui.Data.Service.CurrentModel))
                        ui.Data.Service.CurrentModel = models[0];
                    ui.ModelLabel.Text = ui.Data.Service.CurrentModel;
                }
                else
                {
                    ui.ModelLabel.Text = "no model found";
                }
            }
        }
        catch
        {
            foreach (var ui in _ollamaParticipants)
                ui.ModelLabel.Text = "model list unavailable";
        }
    }

    // ── Ollama-Teilnehmer-Management ───────────────────────────────────────

    private void AddOllamaParticipant(string model = "llama3.2")
    {
        if (_ollamaParticipants.Count >= 3) return;

        var participant = new OllamaParticipant
        {
            Service  = new OllamaService { CurrentModel = model },
            Position = _ollamaParticipants.Count + 1
        };
        BuildOllamaCard(participant);
        UpdateAddRemoveButtons();
    }

    private void RemoveOllamaParticipant(OllamaParticipantUI ui)
    {
        if (_ollamaParticipants.Count <= 1) return;

        OllamaCardsPanel.Children.Remove(ui.Popup);
        OllamaCardsPanel.Children.Remove(ui.Card);
        _ollamaParticipants.Remove(ui);

        RenumberParticipants();
        UpdateAddRemoveButtons();
    }

    private void RenumberParticipants()
    {
        for (int i = 0; i < _ollamaParticipants.Count; i++)
        {
            var ui  = _ollamaParticipants[i];
            var pos = i + 1;
            ui.Data.Position = pos;

            ui.AvatarText.Text = ui.Data.AvatarLabel;
            ui.AvatarBorder.SetResourceReference(Border.BackgroundProperty, ui.Data.ColorKey);

            ui.NameLabel.Text  = $"O{pos} · Ollama";
            ui.PopupTitle.Text = $"O{pos} · Ollama";
            ui.PopupTitle.SetResourceReference(TextBlock.ForegroundProperty, ui.Data.ColorKey);
            ui.EnabledToggle.Content = $"O{pos} enabled";
        }
    }

    private void UpdateAddRemoveButtons()
    {
        AddOllamaButton.Visibility = _ollamaParticipants.Count < 3
            ? Visibility.Visible : Visibility.Collapsed;

        bool showRemove = _ollamaParticipants.Count > 1;
        foreach (var ui in _ollamaParticipants)
            ui.RemoveButton.Visibility = showRemove ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildOllamaCard(OllamaParticipant participant)
    {
        var pos = participant.Position;

        // Avatar
        var avatarText = new TextBlock
        {
            Text                = participant.AvatarLabel,
            FontSize            = 11,
            FontWeight          = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center
        };
        avatarText.SetResourceReference(TextBlock.ForegroundProperty, "SidebarBrush");

        var avatarBorder = new Border
        {
            Width        = 34, Height = 34,
            CornerRadius = new CornerRadius(9),
            Margin       = new Thickness(0, 0, 10, 0),
            Child        = avatarText
        };
        avatarBorder.SetResourceReference(Border.BackgroundProperty, participant.ColorKey);

        // Status-Dot
        var statusDot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        statusDot.SetResourceReference(Ellipse.FillProperty, "SubtextBrush");

        // Labels
        var nameLabel = new TextBlock { Text = $"O{pos} · Ollama", FontSize = 13, FontWeight = FontWeights.SemiBold };
        nameLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var modelLabel = new TextBlock { Text = "checking...", FontSize = 10 };
        modelLabel.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");

        var offlineLabel = new TextBlock { Text = "Offline", FontSize = 10, Visibility = Visibility.Collapsed };
        offlineLabel.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");

        // Remove-Button
        var removeButton = new Button
        {
            Content           = "✕",
            Width             = 22, Height = 22,
            FontSize          = 10,
            BorderThickness   = new Thickness(0),
            Cursor            = Cursors.Hand,
            Padding           = new Thickness(0),
            Visibility        = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip           = "Remove participant",
            Style             = (Style)FindResource("ModernButton")
        };
        removeButton.SetResourceReference(Button.BackgroundProperty,  "SurfaceBrush");
        removeButton.SetResourceReference(Button.ForegroundProperty,  "SubtextBrush");

        // Layout
        var labelPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labelPanel.Children.Add(nameLabel);
        labelPanel.Children.Add(modelLabel);
        labelPanel.Children.Add(offlineLabel);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(avatarBorder, 0);
        Grid.SetColumn(labelPanel,   1);
        Grid.SetColumn(statusDot,    2);
        Grid.SetColumn(removeButton, 3);

        grid.Children.Add(avatarBorder);
        grid.Children.Add(labelPanel);
        grid.Children.Add(statusDot);
        grid.Children.Add(removeButton);

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding      = new Thickness(10),
            Margin       = new Thickness(0, 0, 0, 7),
            Cursor       = Cursors.Hand,
            Child        = grid
        };
        card.SetResourceReference(Border.BackgroundProperty, "InputBrush");

        // ── Popup-Inhalt ───────────────────────────────────────────────────
        var popupTitle = new TextBlock
        {
            Text       = $"O{pos} · Ollama",
            FontSize   = 13,
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, 0, 0, 8)
        };
        popupTitle.SetResourceReference(TextBlock.ForegroundProperty, participant.ColorKey);

        var separator = new Rectangle { Height = 1, Margin = new Thickness(0, 0, 0, 10) };
        separator.SetResourceReference(Rectangle.FillProperty, "InputBrush");

        var enabledToggle = new CheckBox
        {
            Style     = (Style)FindResource("ToggleSwitch"),
            IsChecked = true,
            Content   = $"O{pos} enabled",
            Margin    = new Thickness(0, 0, 0, 14)
        };

        var modelHeader = new TextBlock { Text = "MODEL", FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(2, 0, 0, 6) };
        modelHeader.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");

        var modelList = new ListBox
        {
            Background         = Brushes.Transparent,
            BorderThickness    = new Thickness(0),
            MaxHeight          = 200,
            ItemContainerStyle = (Style)FindResource("ModelListItem")
        };
        ScrollViewer.SetVerticalScrollBarVisibility(modelList, ScrollBarVisibility.Auto);

        var popupContent = new StackPanel();
        popupContent.Children.Add(popupTitle);
        popupContent.Children.Add(separator);
        popupContent.Children.Add(enabledToggle);
        popupContent.Children.Add(modelHeader);
        popupContent.Children.Add(modelList);

        var popupBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(12),
            Padding         = new Thickness(14),
            MinWidth        = 230,
            Child           = popupContent,
            Effect          = new DropShadowEffect { Color = Colors.Black, Opacity = 0.45, BlurRadius = 22, ShadowDepth = 4 }
        };
        popupBorder.SetResourceReference(Border.BackgroundProperty,   "SidebarBrush");
        popupBorder.SetResourceReference(Border.BorderBrushProperty,  "SurfaceBrush");

        var popup = new Popup
        {
            PlacementTarget    = card,
            Placement          = PlacementMode.Right,
            HorizontalOffset   = 10,
            VerticalOffset     = -8,
            StaysOpen          = false,
            AllowsTransparency = true,
            Child              = popupBorder
        };

        // ── UI-Daten ───────────────────────────────────────────────────────
        var ui = new OllamaParticipantUI
        {
            Data          = participant,
            Card          = card,
            AvatarBorder  = avatarBorder,
            AvatarText    = avatarText,
            NameLabel     = nameLabel,
            StatusDot     = statusDot,
            ModelLabel    = modelLabel,
            OfflineLabel  = offlineLabel,
            Popup         = popup,
            PopupTitle    = popupTitle,
            EnabledToggle = enabledToggle,
            ModelList     = modelList,
            RemoveButton  = removeButton
        };

        // ── Events (Closures über ui) ──────────────────────────────────────
        card.MouseLeftButtonDown += (_, _) =>
        {
            modelList.Items.Clear();
            foreach (var m in _availableOllamaModels)
                modelList.Items.Add(m);
            modelList.SelectedItem  = ui.Data.Service.CurrentModel;
            enabledToggle.IsChecked = ui.Data.Enabled;
            popup.IsOpen            = !popup.IsOpen;
        };

        enabledToggle.Checked   += (_, _) => OnOllamaEnabledChanged(ui, true);
        enabledToggle.Unchecked += (_, _) => OnOllamaEnabledChanged(ui, false);

        modelList.SelectionChanged += (_, _) =>
        {
            if (modelList.SelectedItem is string model && model != ui.Data.Service.CurrentModel)
            {
                ui.Data.Service.CurrentModel = model;
                ui.ModelLabel.Text           = model;
            }
        };

        removeButton.Click += (_, _) => RemoveOllamaParticipant(ui);

        // ── Zum Panel hinzufügen ───────────────────────────────────────────
        // Popup als logisches Kind → DynamicResource-Lookup funktioniert
        OllamaCardsPanel.Children.Add(popup);
        OllamaCardsPanel.Children.Add(card);
        _ollamaParticipants.Add(ui);
    }

    // ── Status-Timer ───────────────────────────────────────────────────────

    private void StartStatusTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += async (_, _) => await CheckAllStatusAsync();
        timer.Start();
    }

    private async Task CheckAllStatusAsync()
    {
        if (_ollamaParticipants.Count > 0)
        {
            // Alle Teilnehmer teilen den gleichen Endpunkt → einmal prüfen
            bool wasOnlineBefore = _ollamaParticipants[0].Data.IsOnline == true;
            var  ollamaOnline    = await _ollamaParticipants[0].Data.Service.IsAvailableAsync();

            foreach (var ui in _ollamaParticipants)
                ApplyOllamaParticipantStatus(ui, ollamaOnline);

            if (ollamaOnline && !wasOnlineBefore)
                await LoadOllamaModelsAsync();
        }

        if (_claude is not null)
        {
            var claudeOnline = await _claude.IsAvailableAsync();
            ApplyClaudeStatus(claudeOnline);
        }
    }

    private void ApplyOllamaParticipantStatus(OllamaParticipantUI ui, bool online)
    {
        bool changed = ui.Data.IsOnline != online;
        ui.Data.IsOnline = online;

        ui.StatusDot.SetResourceReference(Ellipse.FillProperty, online ? "OllamaBrush" : "AccentBrush");
        ui.OfflineLabel.Visibility = online ? Visibility.Collapsed : Visibility.Visible;
        ui.ModelLabel.Visibility   = online ? Visibility.Visible   : Visibility.Collapsed;

        double targetOpacity = (online && ui.Data.Enabled) ? 1.0 : 0.6;

        if (changed)
        {
            AnimateStatusChange(ui.Card, targetOpacity);
            // Systemmeldung nur für den ersten Teilnehmer (gemeinsamer Endpunkt)
            if (_ollamaParticipants.Count > 0 && _ollamaParticipants[0] == ui)
                AddSystemMessage(online ? "✓  Ollama is online." : "⚠  Ollama is offline.");
        }
        else
        {
            ui.Card.Opacity = targetOpacity;
        }
    }

    private void ApplyClaudeStatus(bool online)
    {
        bool changed = _isClaudeOnline != online;
        _isClaudeOnline = online;

        ClaudeStatusDot.SetResourceReference(Ellipse.FillProperty, online ? "OllamaBrush" : "AccentBrush");
        ClaudeOfflineLabel.Visibility = online ? Visibility.Collapsed : Visibility.Visible;
        ClaudeModelLabel.Visibility   = online ? Visibility.Visible   : Visibility.Collapsed;

        if (online)
            ClaudeModelLabel.Text = _claude?.CurrentModel ?? ClaudeModels[0];

        double targetOpacity = (online && _claudeEnabled) ? 1.0 : 0.6;

        if (changed)
        {
            AnimateStatusChange(ClaudeCard, targetOpacity);
            AddSystemMessage(online ? "✓  Claude is online." : "⚠  Claude is offline.");
        }
        else
        {
            ClaudeCard.Opacity = targetOpacity;
        }
    }

    private static void AnimateStatusChange(UIElement element, double targetOpacity)
    {
        var kf = new DoubleAnimationUsingKeyFrames();
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(0.25,          KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        kf.KeyFrames.Add(new LinearDoubleKeyFrame(targetOpacity, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400))));
        Storyboard.SetTarget(kf, element);
        Storyboard.SetTargetProperty(kf, new PropertyPath(OpacityProperty));
        var sb = new Storyboard();
        sb.Children.Add(kf);
        sb.Begin();
    }

    private void OnOllamaEnabledChanged(OllamaParticipantUI ui, bool enabled)
    {
        ui.Data.Enabled = enabled;
        double op = (enabled && ui.Data.IsOnline == true) ? 1.0 : 0.6;
        AnimateStatusChange(ui.Card, op);
    }

    // ── Claude-Popup ───────────────────────────────────────────────────────

    private void ClaudeCard_Click(object sender, MouseButtonEventArgs e)
    {
        ClaudeModelList.SelectedItem  = _claude?.CurrentModel ?? ClaudeModels[0];
        ClaudeEnabledToggle.IsChecked = _claudeEnabled;
        ClaudePopup.IsOpen = !ClaudePopup.IsOpen;
    }

    private void ClaudeEnabled_Changed(object sender, RoutedEventArgs e)
    {
        _claudeEnabled = ClaudeEnabledToggle.IsChecked == true;
        double op = (_claudeEnabled && _isClaudeOnline == true) ? 1.0 : 0.6;
        AnimateStatusChange(ClaudeCard, op);
    }

    private void ClaudeModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_claude is null) return;
        if (ClaudeModelList.SelectedItem is string model && model != _claude.CurrentModel)
        {
            _claude.CurrentModel  = model;
            ClaudeModelLabel.Text = model;
        }
    }

    // ── Sidebar-Buttons ────────────────────────────────────────────────────

    private void AddOllamaButton_Click(object sender, RoutedEventArgs e)
        => AddOllamaParticipant();

    // ── Eingabe ────────────────────────────────────────────────────────────

    private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        PlaceholderText.Visibility = string.IsNullOrEmpty(InputTextBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            SendMessage();
            e.Handled = true;
        }
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => SendMessage();

    private void SendMessage()
    {
        var text = InputTextBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        AddMessage("You", "U", "UserBrush", "UserBubbleBrush", text, isUser: true);
        _sharedHistory.Add(new OllamaChatMessage("user", text));
        _claudeHistory.Add(new ClaudeChatMessage("user", text));

        InputTextBox.Clear();
        InputTextBox.Focus();
        _ = TriggerAiResponsesAsync();
    }

    // ── KI-Antworten ───────────────────────────────────────────────────────

    private async void AIRespond_Click(object sender, RoutedEventArgs e)
    {
        if (_sharedHistory.Count == 0)
        {
            AddSystemMessage("Send a message first.");
            return;
        }
        await TriggerAiResponsesAsync();
    }

    private async Task TriggerAiResponsesAsync()
    {
        var activeOllamas = _ollamaParticipants
            .Where(ui => ui.Data.Enabled && ui.Data.IsOnline == true)
            .ToList();
        bool useClaude = _claudeEnabled && _isClaudeOnline == true && _claude is not null;

        if (activeOllamas.Count == 0 && !useClaude)
        {
            AddSystemMessage("⚠  No active AI participant is available.");
            return;
        }

        AIRespondButton.IsEnabled = false;
        SendButton.IsEnabled      = false;
        _streamCts = new CancellationTokenSource();
        var ct = _streamCts.Token;

        try
        {
            foreach (var ui in activeOllamas)
            {
                if (ct.IsCancellationRequested) break;
                await RunOllamaStreamAsync(ui, ct);
            }
            if (useClaude && !ct.IsCancellationRequested)
                await RunClaudeStreamAsync(ct);
        }
        finally
        {
            _streamCts.Dispose();
            _streamCts = null;
            AIRespondButton.IsEnabled = true;
            SendButton.IsEnabled      = true;
        }
    }

    private async Task RunOllamaStreamAsync(OllamaParticipantUI ui, CancellationToken ct)
    {
        var pos      = ui.Data.Position;
        var model    = ui.Data.Service.CurrentModel;
        var sender   = $"O{pos} · {model}";

        var bubbleTb = AddStreamingBubble(sender, ui.Data.AvatarLabel, ui.Data.ColorKey, "OllamaBubbleBrush", false);
        var sb = new StringBuilder();
        try
        {
            await foreach (var token in ui.Data.Service.StreamAsync(_sharedHistory, ct))
            {
                sb.Append(token);
                bubbleTb.Text = sb.ToString();
                ChatScrollViewer.ScrollToBottom();
            }
            // In den gemeinsamen Verlauf schreiben (alle Teilnehmer + Claude sehen dies)
            _sharedHistory.Add(new OllamaChatMessage("assistant", sb.ToString()));
            _claudeHistory.Add(new ClaudeChatMessage("assistant", sb.ToString()));
        }
        catch (OperationCanceledException)
        {
            bubbleTb.Text = sb.Append(" [cancelled]").ToString();
            throw;
        }
        catch (HttpRequestException ex)
        {
            bubbleTb.Text = $"Connection error: {ex.Message}";
            AddSystemMessage($"⚠  {sender} unreachable.");
        }
        catch (Exception ex)
        {
            bubbleTb.Text = $"Error: {ex.Message}";
        }
    }

    private async Task RunClaudeStreamAsync(CancellationToken ct)
    {
        var bubbleTb = AddStreamingBubble("Claude", "C", "ClaudeBrush", "ClaudeBubbleBrush", false);
        var sb = new StringBuilder();
        try
        {
            await foreach (var token in _claude!.StreamAsync(_claudeHistory, ct))
            {
                sb.Append(token);
                bubbleTb.Text = sb.ToString();
                ChatScrollViewer.ScrollToBottom();
            }
            _claudeHistory.Add(new ClaudeChatMessage("assistant", sb.ToString()));
        }
        catch (OperationCanceledException)
        {
            bubbleTb.Text = sb.Append(" [cancelled]").ToString();
            throw;
        }
        catch (HttpRequestException ex)
        {
            bubbleTb.Text = $"Connection error: {ex.Message}";
            AddSystemMessage("⚠  Claude unreachable.");
        }
        catch (Exception ex)
        {
            bubbleTb.Text = $"Error: {ex.Message}";
        }
    }

    // ── Sidebar-Aktionen ───────────────────────────────────────────────────

    private void ClearChat_Click(object sender, RoutedEventArgs e)
    {
        _streamCts?.Cancel();
        ChatPanel.Children.Clear();
        _sharedHistory.Clear();
        _claudeHistory.Clear();
        AddSystemMessage("Chat cleared.");
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) { }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeComboBox.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag?.ToString() is string path)
            ApplyTheme(path);
    }

    /// <summary>Scannt den Themes-Ordner und befüllt die ComboBox.</summary>
    private void LoadThemesIntoComboBox()
    {
        var themesDir = SysIO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Themes");
        if (!SysIO.Directory.Exists(themesDir)) return;

        var files = SysIO.Directory.GetFiles(themesDir, "*.xaml")
                             .OrderBy(SysIO.Path.GetFileNameWithoutExtension,
                                      StringComparer.OrdinalIgnoreCase)
                             .ToList();

        // Suppress SelectionChanged events while populating
        ThemeComboBox.SelectionChanged -= ThemeComboBox_SelectionChanged;
        ThemeComboBox.Items.Clear();

        var savedTheme = SettingsService.Load().LastTheme ?? "";

        ComboBoxItem? savedItem = null;
        ComboBoxItem? mochaItem = null;
        foreach (var file in files)
        {
            var name    = SysIO.Path.GetFileNameWithoutExtension(file)!;
            var display = FormatThemeName(name);
            var item    = new ComboBoxItem { Content = display, Tag = file };
            ThemeComboBox.Items.Add(item);

            if (!string.IsNullOrEmpty(savedTheme) &&
                name.Equals(savedTheme, StringComparison.OrdinalIgnoreCase))
                savedItem = item;

            if (name.Equals("CatppuccinMocha", StringComparison.OrdinalIgnoreCase))
                mochaItem = item;
        }

        ThemeComboBox.SelectionChanged += ThemeComboBox_SelectionChanged;

        // Priority: saved theme → CatppuccinMocha → first available
        var target = savedItem
                  ?? mochaItem
                  ?? (ThemeComboBox.Items.Count > 0 ? (ComboBoxItem)ThemeComboBox.Items[0]! : null);
        if (target is not null)
        {
            ThemeComboBox.SelectedItem = target;
            if (target.Tag?.ToString() is string path)
                ApplyTheme(path);
        }
    }

    /// <summary>
    /// Lädt eine Theme-XAML vom Dateisystem. Bei Fehler: Popup-Meldung,
    /// vorheriges Theme bleibt aktiv.
    /// </summary>
    private void ApplyTheme(string absolutePath)
    {
        try
        {
            var dict = new ResourceDictionary { Source = new Uri(absolutePath) };
            // Erst ersetzen wenn der Load erfolgreich war
            Resources.MergedDictionaries.Clear();
            Resources.MergedDictionaries.Add(dict);
            _currentThemePath = absolutePath;

            // Persist the selected theme name
            var settings = SettingsService.Load();
            settings.LastTheme = SysIO.Path.GetFileNameWithoutExtension(absolutePath);
            SettingsService.Save(settings);
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

            // Restore previous theme
            if (_currentThemePath is not null && _currentThemePath != absolutePath)
            {
                try
                {
                    var prev = new ResourceDictionary { Source = new Uri(_currentThemePath) };
                    Resources.MergedDictionaries.Clear();
                    Resources.MergedDictionaries.Add(prev);
                }
                catch { /* silent – prefer no theme over a crash */ }
            }
        }
    }

    /// <summary>
    /// CamelCase-Dateinamen → lesbarer Anzeigename
    /// z.B. "CatppuccinMocha" → "Catppuccin Mocha"
    ///      "NCRDesolation"   → "NCR Desolation"
    /// </summary>
    private static string FormatThemeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0)
            {
                // Großbuchstabe nach Kleinbuchstabe:  "FooBar" → "Foo Bar"
                if (char.IsUpper(c) && char.IsLower(name[i - 1]))
                    sb.Append(' ');
                // Großbuchstabe nach Großbuchstabe + nächstes Zeichen klein:
                // "NCRFoo" → "NCR Foo"
                else if (char.IsUpper(c) && char.IsUpper(name[i - 1])
                         && i + 1 < name.Length && char.IsLower(name[i + 1]))
                    sb.Append(' ');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ── Nachrichten-Rendering ──────────────────────────────────────────────

    private void AddSystemMessage(string text)
    {
        var tb = new TextBlock
        {
            Text          = text,
            TextAlignment = TextAlignment.Center,
            FontSize      = 11,
            FontFamily    = new FontFamily("Segoe UI"),
            Margin        = new Thickness(0, 10, 0, 10)
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");
        ChatPanel.Children.Add(tb);
    }

    private void AddMessage(string senderName, string avatarText, string accentKey, string bubbleKey,
                            string text, bool isUser)
    {
        var tb = AddStreamingBubble(senderName, avatarText, accentKey, bubbleKey, isUser);
        tb.Text = text;
        ChatScrollViewer.ScrollToBottom();
    }

    private TextBlock AddStreamingBubble(string senderName, string avatarText, string accentKey,
                                          string bubbleKey, bool isUser)
    {
        // Avatar
        var avatarInner = new TextBlock
        {
            Text                = avatarText,
            FontSize            = avatarText.Length > 1 ? 11 : 14,
            FontWeight          = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center
        };
        avatarInner.SetResourceReference(TextBlock.ForegroundProperty, "SidebarBrush");

        var avatar = new Border
        {
            Width             = 34, Height = 34,
            CornerRadius      = new CornerRadius(9),
            VerticalAlignment = VerticalAlignment.Top,
            Margin            = isUser ? new Thickness(10, 0, 0, 0) : new Thickness(0, 0, 10, 0),
            Child             = avatarInner
        };
        avatar.SetResourceReference(Border.BackgroundProperty, accentKey);

        // Bubble
        var contentTb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, FontFamily = new FontFamily("Segoe UI") };
        contentTb.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var bubble = new Border
        {
            CornerRadius = isUser ? new CornerRadius(12, 3, 12, 12) : new CornerRadius(3, 12, 12, 12),
            Padding      = new Thickness(13, 9, 13, 9),
            Child        = contentTb
        };
        bubble.SetResourceReference(Border.BackgroundProperty, bubbleKey);

        // Labels
        var nameLabel = new TextBlock
        {
            Text                = senderName,
            FontSize            = 11,
            FontWeight          = FontWeights.SemiBold,
            Margin              = new Thickness(isUser ? 0 : 3, 0, isUser ? 3 : 0, 3),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };
        nameLabel.SetResourceReference(TextBlock.ForegroundProperty, accentKey);

        var timeLabel = new TextBlock
        {
            Text                = DateTime.Now.ToString("HH:mm"),
            FontSize            = 10,
            Margin              = new Thickness(3, 4, 3, 0),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };
        timeLabel.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");

        var content = new StackPanel { MaxWidth = 580 };
        content.Children.Add(nameLabel);
        content.Children.Add(bubble);
        content.Children.Add(timeLabel);

        var row = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };
        if (isUser) { row.Children.Add(content); row.Children.Add(avatar); }
        else        { row.Children.Add(avatar);  row.Children.Add(content); }

        var wrapper = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        wrapper.Children.Add(row);
        ChatPanel.Children.Add(wrapper);
        return contentTb;
    }

    // ── Hilfsmethoden ──────────────────────────────────────────────────────

    private Brush GetRes(string key) => (Brush)FindResource(key);
}
