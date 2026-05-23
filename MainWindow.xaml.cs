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
        public required OllamaService Service    { get; init; }
        public int      Position   { get; set; }
        public bool     Enabled    { get; set; } = true;
        public bool?    IsOnline   { get; set; }
        public string?  CustomName { get; set; }

        public string ColorKey    => Position switch { 2 => "AccentBrush", 3 => "ClaudeBrush", _ => "OllamaBrush" };
        public string AvatarLabel => $"O{Position}";
        public string DisplayName => string.IsNullOrEmpty(CustomName)
            ? $"O{Position} · Ollama"
            : CustomName;
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
        public required Button            RemoveButton  { get; init; }
    }

    private sealed class CloudAIParticipant
    {
        public required ICloudAIService Service    { get; init; }
        public int      Position   { get; set; }
        public bool     Enabled    { get; set; } = true;
        public bool?    IsOnline   { get; set; }
        public string?  CustomName { get; set; }

        public string ColorKey => Position switch { 2 => "AccentBrush", 3 => "OllamaBrush", _ => "ClaudeBrush" };

        public string AvatarLabel => Service.ProviderName switch
        {
            "Anthropic"      => "An",
            "Google AI"      => "Gm",
            "Groq"           => "Gq",
            "OpenRouter"     => "OR",
            "Mistral"        => "Mi",
            "xAI Grok"       => "xG",
            "OpenAI ChatGPT" => "GP",
            _                => Service.ProviderName.Length >= 2
                                    ? Service.ProviderName[..2]
                                    : Service.ProviderName
        };

        public string ProviderName => Service.ProviderName;

        public string DisplayName => string.IsNullOrEmpty(CustomName)
            ? Service.ProviderName
            : CustomName;
    }

    private sealed class CloudAIParticipantUI
    {
        public required CloudAIParticipant Data          { get; init; }
        public required Border             Card          { get; init; }
        public required Border             AvatarBorder  { get; init; }
        public required TextBlock          AvatarText    { get; init; }
        public required TextBlock          NameLabel     { get; init; }
        public required Ellipse            StatusDot     { get; init; }
        public required TextBlock          ModelLabel    { get; init; }
        public required TextBlock          OfflineLabel  { get; init; }
        public required Popup              Popup         { get; init; }
        public required TextBlock          PopupTitle    { get; init; }
        public required CheckBox           EnabledToggle { get; init; }
        public required Button             RemoveButton  { get; init; }
    }

    // ── State ──────────────────────────────────────────────────────────────
    private readonly List<CloudAIParticipantUI>  _cloudAIParticipants   = [];
    private readonly List<OllamaParticipantUI>   _ollamaParticipants    = [];
    private readonly List<CloudAIMessage>        _sharedHistory         = [];
    private CancellationTokenSource?             _streamCts;
    private List<string>                         _availableOllamaModels = [];
    private string?                              _currentThemePath;
    private string                               _userName              = "You";

    // ──────────────────────────────────────────────────────────────────────

    public MainWindow()
    {
        InitializeComponent();
        LoadThemesIntoComboBox();
        Loaded += async (_, _) =>
        {
            InitializeServices();
            AddSystemMessage("Chat started  ·  configure participants in ⚙ Settings.");
            InputTextBox.Focus();
            await CheckAllStatusAsync();
            StartStatusTimer();
        };
    }

    // ── Initialization ─────────────────────────────────────────────────────

    private void InitializeServices()
    {
        var settings = SettingsService.Load();

        // One-time migration: move legacy ClaudeApiKey → Windows Credential Manager
        if (!string.IsNullOrWhiteSpace(settings.ClaudeApiKey))
        {
            WindowsCredentialManager.Save("Anthropic", settings.ClaudeApiKey);
            settings.ClaudeApiKey = "";
            SettingsService.Save(settings);
        }

        // Create participants from settings
        bool anyAdded = false;
        foreach (var p in settings.Participants.Where(p => p.Enabled))
        {
            if (p.Type == "Ollama")
            {
                if (_ollamaParticipants.Count < 3)
                {
                    AddOllamaParticipant(p.Model, p.ServerUrl, p.Name);
                    anyAdded = true;
                }
            }
            else
            {
                if (_cloudAIParticipants.Count < 3)
                {
                    AddCloudAIParticipant(p.Type, p.Model, p.Name);
                    anyAdded = true;
                }
            }
        }

        // Fallback: no participants configured → add default Ollama
        if (!anyAdded)
        {
            AddOllamaParticipant(settings.OllamaModel);
            AddSystemMessage("ℹ  No participants configured — open ⚙ Settings to set them up.");
        }

        // User display name
        _userName = string.IsNullOrWhiteSpace(settings.UserName) ? "You" : settings.UserName.Trim();
    }

    // ── Re-initialize after Settings save ─────────────────────────────────

    private void ReInitializeParticipants()
    {
        _streamCts?.Cancel();

        // Remove Cloud AI cards
        foreach (var ui in _cloudAIParticipants.ToList())
        {
            CloudAICardsPanel.Children.Remove(ui.Popup);
            CloudAICardsPanel.Children.Remove(ui.Card);
            ui.Data.Service.Dispose();
        }
        _cloudAIParticipants.Clear();

        // Remove Ollama cards
        foreach (var ui in _ollamaParticipants.ToList())
        {
            OllamaCardsPanel.Children.Remove(ui.Popup);
            OllamaCardsPanel.Children.Remove(ui.Card);
        }
        _ollamaParticipants.Clear();
        _availableOllamaModels.Clear();

        // Re-add from settings
        var settings = SettingsService.Load();
        _userName = string.IsNullOrWhiteSpace(settings.UserName) ? "You" : settings.UserName.Trim();

        foreach (var p in settings.Participants.Where(p => p.Enabled))
        {
            if (p.Type == "Ollama" && _ollamaParticipants.Count < 3)
                AddOllamaParticipant(p.Model, p.ServerUrl, p.Name);
            else if (p.Type != "Ollama" && _cloudAIParticipants.Count < 3)
                AddCloudAIParticipant(p.Type, p.Model, p.Name);
        }

        if (_ollamaParticipants.Count == 0 && _cloudAIParticipants.Count == 0)
            AddSystemMessage("⚠  No participants enabled — configure them in ⚙ Settings.");

        UpdateAddRemoveButtons();
        UpdateCloudAIAddRemoveButtons();
        _ = CheckAllStatusAsync();
    }

    // ── Cloud AI participant management ────────────────────────────────────

    private void AddCloudAIParticipant(string provider, string model = "", string customName = "")
    {
        if (_cloudAIParticipants.Count >= 3) return;
        if (_cloudAIParticipants.Any(ui => ui.Data.Service.ProviderName == provider)) return;

        var apiKey = WindowsCredentialManager.Load(provider);
        if (string.IsNullOrWhiteSpace(apiKey)) return;

        var service = CreateCloudAIService(provider, apiKey);
        if (!string.IsNullOrEmpty(model)) service.CurrentModel = model;

        var participant = new CloudAIParticipant
        {
            Service    = service,
            Position   = _cloudAIParticipants.Count + 1,
            CustomName = string.IsNullOrWhiteSpace(customName) ? null : customName
        };
        BuildCloudAICard(participant);
        UpdateCloudAIAddRemoveButtons();
    }

    private void RemoveCloudAIParticipant(CloudAIParticipantUI ui)
    {
        CloudAICardsPanel.Children.Remove(ui.Popup);
        CloudAICardsPanel.Children.Remove(ui.Card);
        ui.Data.Service.Dispose();
        _cloudAIParticipants.Remove(ui);
        RenumberCloudAIParticipants();
        UpdateCloudAIAddRemoveButtons();
    }

    private void RenumberCloudAIParticipants()
    {
        for (int i = 0; i < _cloudAIParticipants.Count; i++)
        {
            var ui = _cloudAIParticipants[i];
            ui.Data.Position = i + 1;
            ui.AvatarBorder.SetResourceReference(Border.BackgroundProperty, ui.Data.ColorKey);
        }
    }

    private void UpdateCloudAIAddRemoveButtons()
    {
        AddCloudAIButton.Visibility = _cloudAIParticipants.Count < 3
            ? Visibility.Visible : Visibility.Collapsed;

        foreach (var ui in _cloudAIParticipants)
            ui.RemoveButton.Visibility = Visibility.Visible;
    }

    private void BuildCloudAICard(CloudAIParticipant participant)
    {
        // ── Avatar ────────────────────────────────────────────────────────
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

        // ── Status dot ────────────────────────────────────────────────────
        var statusDot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        statusDot.SetResourceReference(Ellipse.FillProperty, "SubtextBrush");

        // ── Labels ────────────────────────────────────────────────────────
        var nameLabel = new TextBlock
        {
            Text       = participant.DisplayName,
            FontSize   = 13,
            FontWeight = FontWeights.SemiBold
        };
        nameLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var modelLabel = new TextBlock { Text = participant.Service.CurrentModel, FontSize = 10 };
        modelLabel.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");

        var offlineLabel = new TextBlock { Text = "Offline", FontSize = 10, Visibility = Visibility.Collapsed };
        offlineLabel.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");

        // ── Remove button ─────────────────────────────────────────────────
        var removeButton = new Button
        {
            Content           = "✕",
            Width             = 22, Height = 22,
            FontSize          = 10,
            BorderThickness   = new Thickness(0),
            Cursor            = Cursors.Hand,
            Padding           = new Thickness(0),
            Visibility        = Visibility.Visible,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip           = "Remove participant",
            Style             = (Style)FindResource("ModernButton")
        };
        removeButton.SetResourceReference(Button.BackgroundProperty, "SurfaceBrush");
        removeButton.SetResourceReference(Button.ForegroundProperty, "SubtextBrush");

        // ── Layout ────────────────────────────────────────────────────────
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

        // ── Popup ─────────────────────────────────────────────────────────
        var popupTitle = new TextBlock
        {
            Text       = participant.DisplayName,
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
            Content   = $"{participant.DisplayName} enabled",
            Margin    = new Thickness(0, 0, 0, 14)
        };

        // ── Read-only info rows ───────────────────────────────────────────
        var infoProviderKey = new TextBlock { Text = "PROVIDER", FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 3) };
        infoProviderKey.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");
        var infoProviderVal = new TextBlock { Text = participant.ProviderName, FontSize = 12, Margin = new Thickness(0, 0, 0, 10) };
        infoProviderVal.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var infoModelKey = new TextBlock { Text = "MODEL", FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 3) };
        infoModelKey.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");
        var infoModelVal = new TextBlock { Text = participant.Service.CurrentModel, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        infoModelVal.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var popupContent = new StackPanel();
        popupContent.Children.Add(popupTitle);
        popupContent.Children.Add(separator);
        popupContent.Children.Add(enabledToggle);
        popupContent.Children.Add(infoProviderKey);
        popupContent.Children.Add(infoProviderVal);
        popupContent.Children.Add(infoModelKey);
        popupContent.Children.Add(infoModelVal);

        var popupBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(12),
            Padding         = new Thickness(14),
            MinWidth        = 230,
            Child           = popupContent,
            Effect          = new DropShadowEffect { Color = Colors.Black, Opacity = 0.45, BlurRadius = 22, ShadowDepth = 4 }
        };
        popupBorder.SetResourceReference(Border.BackgroundProperty,  "SidebarBrush");
        popupBorder.SetResourceReference(Border.BorderBrushProperty, "SurfaceBrush");

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

        // ── UI record ─────────────────────────────────────────────────────
        var ui = new CloudAIParticipantUI
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
            RemoveButton  = removeButton
        };

        // ── Events ────────────────────────────────────────────────────────
        card.MouseLeftButtonDown += (_, _) =>
        {
            enabledToggle.IsChecked = ui.Data.Enabled;
            infoModelVal.Text       = ui.Data.Service.CurrentModel;
            popup.IsOpen            = !popup.IsOpen;
        };

        enabledToggle.Checked   += (_, _) => OnCloudAIEnabledChanged(ui, true);
        enabledToggle.Unchecked += (_, _) => OnCloudAIEnabledChanged(ui, false);

        removeButton.Click += (_, _) => RemoveCloudAIParticipant(ui);

        CloudAICardsPanel.Children.Add(popup);
        CloudAICardsPanel.Children.Add(card);
        _cloudAIParticipants.Add(ui);
    }

    private void OnCloudAIEnabledChanged(CloudAIParticipantUI ui, bool enabled)
    {
        ui.Data.Enabled = enabled;
        double op = (enabled && ui.Data.IsOnline == true) ? 1.0 : 0.6;
        AnimateStatusChange(ui.Card, op);
    }

    // ── Ollama participant management ──────────────────────────────────────

    private void AddOllamaParticipant(string model = "llama3.2",
                                      string serverUrl = "http://localhost:11434",
                                      string customName = "")
    {
        if (_ollamaParticipants.Count >= 3) return;

        var participant = new OllamaParticipant
        {
            Service    = new OllamaService(serverUrl) { CurrentModel = model },
            Position   = _ollamaParticipants.Count + 1,
            CustomName = string.IsNullOrWhiteSpace(customName) ? null : customName
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

            var displayName = ui.Data.DisplayName;
            ui.NameLabel .Text = displayName;
            ui.PopupTitle.Text = displayName;
            ui.PopupTitle.SetResourceReference(TextBlock.ForegroundProperty, ui.Data.ColorKey);
            ui.EnabledToggle.Content = $"{displayName} enabled";
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
        var pos         = participant.Position;
        var displayName = participant.DisplayName;

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

        var statusDot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        statusDot.SetResourceReference(Ellipse.FillProperty, "SubtextBrush");

        var nameLabel = new TextBlock { Text = displayName, FontSize = 13, FontWeight = FontWeights.SemiBold };
        nameLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var modelLabel = new TextBlock { Text = "checking...", FontSize = 10 };
        modelLabel.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");

        var offlineLabel = new TextBlock { Text = "Offline", FontSize = 10, Visibility = Visibility.Collapsed };
        offlineLabel.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");

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
        removeButton.SetResourceReference(Button.BackgroundProperty, "SurfaceBrush");
        removeButton.SetResourceReference(Button.ForegroundProperty, "SubtextBrush");

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

        // Popup
        var popupTitle = new TextBlock
        {
            Text       = displayName,
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
            Content   = $"{displayName} enabled",
            Margin    = new Thickness(0, 0, 0, 14)
        };

        var infoServerKey = new TextBlock { Text = "SERVER", FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 3) };
        infoServerKey.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");
        var infoServerVal = new TextBlock { Text = participant.Service.BaseUrl, FontSize = 12, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
        infoServerVal.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var infoModelKey = new TextBlock { Text = "MODEL", FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 3) };
        infoModelKey.SetResourceReference(TextBlock.ForegroundProperty, "SubtextBrush");
        var infoModelVal = new TextBlock { Text = participant.Service.CurrentModel, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        infoModelVal.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var popupContent = new StackPanel();
        popupContent.Children.Add(popupTitle);
        popupContent.Children.Add(separator);
        popupContent.Children.Add(enabledToggle);
        popupContent.Children.Add(infoServerKey);
        popupContent.Children.Add(infoServerVal);
        popupContent.Children.Add(infoModelKey);
        popupContent.Children.Add(infoModelVal);

        var popupBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(12),
            Padding         = new Thickness(14),
            MinWidth        = 230,
            Child           = popupContent,
            Effect          = new DropShadowEffect { Color = Colors.Black, Opacity = 0.45, BlurRadius = 22, ShadowDepth = 4 }
        };
        popupBorder.SetResourceReference(Border.BackgroundProperty,  "SidebarBrush");
        popupBorder.SetResourceReference(Border.BorderBrushProperty, "SurfaceBrush");

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
            RemoveButton  = removeButton
        };

        card.MouseLeftButtonDown += (_, _) =>
        {
            enabledToggle.IsChecked = ui.Data.Enabled;
            infoModelVal.Text       = ui.Data.Service.CurrentModel;
            popup.IsOpen            = !popup.IsOpen;
        };

        enabledToggle.Checked   += (_, _) => OnOllamaEnabledChanged(ui, true);
        enabledToggle.Unchecked += (_, _) => OnOllamaEnabledChanged(ui, false);

        removeButton.Click += (_, _) => RemoveOllamaParticipant(ui);

        OllamaCardsPanel.Children.Add(popup);
        OllamaCardsPanel.Children.Add(card);
        _ollamaParticipants.Add(ui);
    }

    private void OnOllamaEnabledChanged(OllamaParticipantUI ui, bool enabled)
    {
        ui.Data.Enabled = enabled;
        double op = (enabled && ui.Data.IsOnline == true) ? 1.0 : 0.6;
        AnimateStatusChange(ui.Card, op);
    }

    // ── Status ─────────────────────────────────────────────────────────────

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
            bool wasOnlineBefore = _ollamaParticipants[0].Data.IsOnline == true;
            var  ollamaOnline    = await _ollamaParticipants[0].Data.Service.IsAvailableAsync();

            foreach (var ui in _ollamaParticipants)
                ApplyOllamaParticipantStatus(ui, ollamaOnline);

            if (ollamaOnline && !wasOnlineBefore)
                await LoadOllamaModelsAsync();
        }

        foreach (var ui in _cloudAIParticipants)
        {
            var online = await ui.Data.Service.IsAvailableAsync();
            ApplyCloudAIParticipantStatus(ui, online);
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
            if (_ollamaParticipants.Count > 0 && _ollamaParticipants[0] == ui)
                AddSystemMessage(online ? "✓  Ollama is online." : "⚠  Ollama is offline.");
        }
        else
        {
            ui.Card.Opacity = targetOpacity;
        }
    }

    private void ApplyCloudAIParticipantStatus(CloudAIParticipantUI ui, bool online)
    {
        bool changed = ui.Data.IsOnline != online;
        ui.Data.IsOnline = online;

        ui.StatusDot.SetResourceReference(Ellipse.FillProperty, online ? "OllamaBrush" : "AccentBrush");
        ui.OfflineLabel.Visibility = online ? Visibility.Collapsed : Visibility.Visible;
        ui.ModelLabel.Visibility   = online ? Visibility.Visible   : Visibility.Collapsed;

        if (online)
            ui.ModelLabel.Text = ui.Data.Service.CurrentModel;

        double targetOpacity = (online && ui.Data.Enabled) ? 1.0 : 0.6;

        if (changed)
        {
            AnimateStatusChange(ui.Card, targetOpacity);
            AddSystemMessage(online
                ? $"✓  {ui.Data.DisplayName} is online."
                : $"⚠  {ui.Data.DisplayName} is offline.");
        }
        else
        {
            ui.Card.Opacity = targetOpacity;
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

    private async Task LoadOllamaModelsAsync()
    {
        if (_ollamaParticipants.Count == 0) return;
        try
        {
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

    // ── Add Cloud AI popup handlers ────────────────────────────────────────

    private void AddCloudAIButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cloudAIParticipants.Count >= 3) return;

        var active    = _cloudAIParticipants.Select(ui => ui.Data.Service.ProviderName).ToHashSet();
        var available = new[] { "Anthropic", "OpenAI ChatGPT", "Google AI", "Groq", "xAI Grok", "OpenRouter", "Mistral" }
            .Where(p => !active.Contains(p) && WindowsCredentialManager.Load(p) is not null)
            .ToList();

        if (available.Count == 0)
        {
            AddSystemMessage("ℹ  No more Cloud AI providers with API keys. Configure them in ⚙ Settings.");
            return;
        }

        AddCloudAIProviderCombo.SelectionChanged -= AddCloudAIProviderCombo_SelectionChanged;
        AddCloudAIProviderCombo.Items.Clear();
        foreach (var p in available)
            AddCloudAIProviderCombo.Items.Add(new ComboBoxItem { Content = p });
        AddCloudAIProviderCombo.SelectedIndex = 0;
        AddCloudAIProviderCombo.SelectionChanged += AddCloudAIProviderCombo_SelectionChanged;

        PopulateAddCloudAIModelCombo(available[0]);
        AddCloudAIPopup.IsOpen = true;
    }

    private void AddCloudAIProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var provider = (AddCloudAIProviderCombo.SelectedItem as ComboBoxItem)?.Content?.ToString();
        if (provider is not null)
            PopulateAddCloudAIModelCombo(provider);
    }

    private void PopulateAddCloudAIModelCombo(string provider)
    {
        var models = GetDefaultModelsForProvider(provider);
        AddCloudAIModelCombo.Items.Clear();
        foreach (var m in models)
            AddCloudAIModelCombo.Items.Add(new ComboBoxItem { Content = m });
        if (AddCloudAIModelCombo.Items.Count > 0)
            AddCloudAIModelCombo.SelectedIndex = 0;
    }

    private void ConfirmAddCloudAI_Click(object sender, RoutedEventArgs e)
    {
        var provider = (AddCloudAIProviderCombo.SelectedItem as ComboBoxItem)?.Content?.ToString();
        var model    = (AddCloudAIModelCombo.SelectedItem    as ComboBoxItem)?.Content?.ToString() ?? "";

        if (provider is null) return;

        AddCloudAIPopup.IsOpen = false;
        AddCloudAIParticipant(provider, model);

        if (_cloudAIParticipants.Count > 0)
        {
            var ui = _cloudAIParticipants[^1];
            _ = Task.Run(async () =>
            {
                var online = await ui.Data.Service.IsAvailableAsync();
                Dispatcher.Invoke(() => ApplyCloudAIParticipantStatus(ui, online));
            });
        }
    }

    // ── Sidebar buttons ────────────────────────────────────────────────────

    private void AddOllamaButton_Click(object sender, RoutedEventArgs e)
        => AddOllamaParticipant();

    // ── Input ──────────────────────────────────────────────────────────────

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

        var avatar = _userName.Length >= 2 ? _userName[..2].ToUpper() : _userName.ToUpper();
        AddMessage(_userName, avatar, "UserBrush", "UserBubbleBrush", text, isUser: true);
        _sharedHistory.Add(new CloudAIMessage("user", text, "User"));

        InputTextBox.Clear();
        InputTextBox.Focus();
        _ = TriggerAiResponsesAsync();
    }

    // ── AI responses ───────────────────────────────────────────────────────

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
        var activeOllamas  = _ollamaParticipants .Where(ui => ui.Data.Enabled && ui.Data.IsOnline == true).ToList();
        var activeCloudAIs = _cloudAIParticipants.Where(ui => ui.Data.Enabled && ui.Data.IsOnline == true).ToList();

        if (activeOllamas.Count == 0 && activeCloudAIs.Count == 0)
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
            foreach (var ui in activeCloudAIs)
            {
                if (ct.IsCancellationRequested) break;
                await RunCloudAIStreamAsync(ui, ct);
            }
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
        var display = string.IsNullOrEmpty(ui.Data.CustomName)
            ? $"O{ui.Data.Position} · {ui.Data.Service.CurrentModel}"
            : ui.Data.CustomName;

        var bubbleTb = AddStreamingBubble(display, ui.Data.AvatarLabel, ui.Data.ColorKey, "OllamaBubbleBrush", false);
        var sb = new StringBuilder();
        try
        {
            var history = BuildOllamaHistoryFor(ui);
            await foreach (var token in ui.Data.Service.StreamAsync(history, ct))
            {
                sb.Append(token);
                bubbleTb.Text = sb.ToString();
                ChatScrollViewer.ScrollToBottom();
            }
            _sharedHistory.Add(new CloudAIMessage("assistant", sb.ToString(), ui.Data.AvatarLabel));
        }
        catch (OperationCanceledException)
        {
            bubbleTb.Text = sb.Append(" [cancelled]").ToString();
            throw;
        }
        catch (HttpRequestException ex)
        {
            bubbleTb.Text = $"Connection error: {ex.Message}";
            AddSystemMessage($"⚠  {display} unreachable.");
        }
        catch (Exception ex)
        {
            bubbleTb.Text = $"Error: {ex.Message}";
        }
    }

    private async Task RunCloudAIStreamAsync(CloudAIParticipantUI ui, CancellationToken ct)
    {
        var model   = ui.Data.Service.CurrentModel;
        var display = string.IsNullOrEmpty(ui.Data.CustomName)
            ? $"{ui.Data.ProviderName} · {model}"
            : ui.Data.CustomName;

        var bubbleTb = AddStreamingBubble(display, ui.Data.AvatarLabel, ui.Data.ColorKey, "ClaudeBubbleBrush", false);
        var sb = new StringBuilder();
        try
        {
            var (history, system) = BuildCloudAIHistoryFor(ui);
            await foreach (var token in ui.Data.Service.StreamAsync(history, system, ct))
            {
                sb.Append(token);
                bubbleTb.Text = sb.ToString();
                ChatScrollViewer.ScrollToBottom();
            }
            _sharedHistory.Add(new CloudAIMessage("assistant", sb.ToString(), ui.Data.AvatarLabel));
        }
        catch (OperationCanceledException)
        {
            bubbleTb.Text = sb.Append(" [cancelled]").ToString();
            throw;
        }
        catch (HttpRequestException ex)
        {
            bubbleTb.Text = $"Connection error: {ex.Message}";
            AddSystemMessage($"⚠  {display} unreachable.");
        }
        catch (Exception ex)
        {
            bubbleTb.Text = $"Error: {ex.Message}";
        }
    }

    // ── Per-participant history builders ───────────────────────────────────

    private List<OllamaChatMessage> BuildOllamaHistoryFor(OllamaParticipantUI forUi)
    {
        var myLabel = forUi.Data.AvatarLabel;
        var myName  = forUi.Data.DisplayName;
        var myModel = forUi.Data.Service.CurrentModel;

        var result = new List<OllamaChatMessage>
        {
            new("system",
                $"You are {myName} (ID: {myLabel}), running the {myModel} model. " +
                $"You are one of several participants in a relay group chat (human + multiple AI models). " +
                $"Always respond as {myName}. " +
                $"If asked who you are, say you are {myName} running {myModel}. " +
                $"Messages from other AI participants are prefixed with their ID in square brackets.")
        };

        foreach (var msg in _sharedHistory)
        {
            if (msg.Role == "user")
                result.Add(new OllamaChatMessage("user", msg.Content));
            else if (msg.Role == "assistant")
            {
                if (msg.Sender == myLabel)
                    result.Add(new OllamaChatMessage("assistant", msg.Content));
                else
                    result.Add(new OllamaChatMessage("user", $"[{msg.Sender}]: {msg.Content}"));
            }
        }

        return result;
    }

    private (List<CloudAIMessage> History, string System) BuildCloudAIHistoryFor(CloudAIParticipantUI forUi)
    {
        var myLabel = forUi.Data.AvatarLabel;
        var myName  = forUi.Data.DisplayName;
        var myModel = forUi.Data.Service.CurrentModel;

        var otherOllamas = _ollamaParticipants
            .Where(ui => ui.Data.Enabled)
            .Select(ui => $"{ui.Data.AvatarLabel} ({ui.Data.DisplayName})");
        var otherCloud = _cloudAIParticipants
            .Where(ui => ui != forUi && ui.Data.Enabled)
            .Select(ui => $"{ui.Data.AvatarLabel} ({ui.Data.DisplayName})");

        var others    = otherOllamas.Concat(otherCloud).ToList();
        var othersNote = others.Count > 0
            ? $" Other AI participants: {string.Join(", ", others)}."
            : "";

        var system =
            $"You are {myName} (ID: {myLabel}), running model {myModel}. " +
            $"You are participating in a relay group chat with a human user and other AI models.{othersNote} " +
            $"Always respond as {myName}. If asked who you are, identify yourself as {myName}.";

        var history = new List<CloudAIMessage>();
        foreach (var msg in _sharedHistory)
        {
            if (msg.Role == "user")
                history.Add(new CloudAIMessage("user", msg.Content));
            else if (msg.Role == "assistant")
            {
                if (msg.Sender == myLabel)
                    history.Add(new CloudAIMessage("assistant", msg.Content));
                else
                    history.Add(new CloudAIMessage("user", $"[{msg.Sender}]: {msg.Content}"));
            }
        }

        return (history, system);
    }

    // ── Sidebar actions ────────────────────────────────────────────────────

    private void ClearChat_Click(object sender, RoutedEventArgs e)
    {
        _streamCts?.Cancel();
        ChatPanel.Children.Clear();
        _sharedHistory.Clear();
        AddSystemMessage("Chat cleared.");
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_currentThemePath) { Owner = this };
        if (win.ShowDialog() == true)
            ReInitializeParticipants();
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeComboBox.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag?.ToString() is string path)
            ApplyTheme(path);
    }

    private void LoadThemesIntoComboBox()
    {
        var themesDir = SysIO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Themes");
        if (!SysIO.Directory.Exists(themesDir)) return;

        var files = SysIO.Directory.GetFiles(themesDir, "*.xaml")
                             .OrderBy(SysIO.Path.GetFileNameWithoutExtension,
                                      StringComparer.OrdinalIgnoreCase)
                             .ToList();

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

    private void ApplyTheme(string absolutePath)
    {
        try
        {
            var dict = new ResourceDictionary { Source = new Uri(absolutePath) };
            Resources.MergedDictionaries.Clear();
            Resources.MergedDictionaries.Add(dict);
            _currentThemePath = absolutePath;

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

            if (_currentThemePath is not null && _currentThemePath != absolutePath)
            {
                try
                {
                    var prev = new ResourceDictionary { Source = new Uri(_currentThemePath) };
                    Resources.MergedDictionaries.Clear();
                    Resources.MergedDictionaries.Add(prev);
                }
                catch { /* silent */ }
            }
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

    // ── Service factories ──────────────────────────────────────────────────

    private static ICloudAIService CreateCloudAIService(string provider, string apiKey) =>
        provider switch
        {
            "Google AI"      => new GoogleAIService(apiKey),
            "Groq"           => new GroqService(apiKey),
            "OpenRouter"     => new OpenRouterService(apiKey),
            "Mistral"        => new MistralService(apiKey),
            "xAI Grok"       => new XAIGrokService(apiKey),
            "OpenAI ChatGPT" => new OpenAIService(apiKey),
            _                => new AnthropicService(apiKey)
        };

    private static string[] GetDefaultModelsForProvider(string provider) => provider switch
    {
        "Anthropic"      => AnthropicService.DefaultModels,
        "Google AI"      => GoogleAIService.DefaultModels,
        "Groq"           => GroqService.DefaultModels,
        "OpenRouter"     => OpenRouterService.DefaultModels,
        "Mistral"        => MistralService.DefaultModels,
        "xAI Grok"       => XAIGrokService.DefaultModels,
        "OpenAI ChatGPT" => OpenAIService.DefaultModels,
        _                => AnthropicService.DefaultModels
    };

    // ── Message rendering ──────────────────────────────────────────────────

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

        var contentTb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize     = 13,
            FontFamily   = new FontFamily("Segoe UI")
        };
        contentTb.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var bubble = new Border
        {
            CornerRadius = isUser ? new CornerRadius(12, 3, 12, 12) : new CornerRadius(3, 12, 12, 12),
            Padding      = new Thickness(13, 9, 13, 9),
            Child        = contentTb
        };
        bubble.SetResourceReference(Border.BackgroundProperty, bubbleKey);

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
}
