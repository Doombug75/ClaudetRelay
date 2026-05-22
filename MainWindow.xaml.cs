using System.Net.Http;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClaudetRelay.Services;

namespace ClaudetRelay;

public enum SenderType { Claude, Ollama, User }

public partial class MainWindow : Window
{
    private readonly OllamaService _ollama = new();
    private readonly List<OllamaChatMessage> _ollamaHistory = [];
    private CancellationTokenSource? _streamCts;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            AddSystemMessage("Chat gestartet  ·  Claude, Ollama und Du sind verbunden.");
            InputTextBox.Focus();
            await LoadModelsAsync();
        };
    }

    // ── Modelle laden ──────────────────────────────────────────────────────

    private async Task LoadModelsAsync()
    {
        OllamaModelLabel.Text = "wird geladen…";
        try
        {
            var models = await _ollama.GetModelsAsync();
            if (models.Count > 0)
            {
                _ollama.CurrentModel = models[0];
                OllamaModelLabel.Text = models[0];
            }
            else
            {
                OllamaModelLabel.Text = "kein Modell gefunden";
                AddSystemMessage("⚠  Ollama erreichbar, aber keine Modelle installiert.");
            }
        }
        catch
        {
            OllamaModelLabel.Text = "nicht erreichbar";
            AddSystemMessage("⚠  Ollama nicht erreichbar — läuft localhost:11434?");
        }
    }

    // ── Eingabe ────────────────────────────────────────────────────────────

    private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        PlaceholderText.Visibility = string.IsNullOrEmpty(InputTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
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

        AddMessage("Du", text, SenderType.User);
        _ollamaHistory.Add(new OllamaChatMessage("user", text));

        InputTextBox.Clear();
        InputTextBox.Focus();

        _ = StreamOllamaResponseAsync();
    }

    // ── Ollama Streaming ───────────────────────────────────────────────────

    private async void AIRespond_Click(object sender, RoutedEventArgs e)
    {
        if (_ollamaHistory.Count == 0 || _ollamaHistory[^1].Role != "user")
        {
            AddSystemMessage("Schreibe zuerst eine Nachricht.");
            return;
        }
        await StreamOllamaResponseAsync();
    }

    private async Task StreamOllamaResponseAsync()
    {
        AIRespondButton.IsEnabled = false;
        SendButton.IsEnabled = false;

        // Leere Bubble sofort einfügen – wird Token für Token befüllt
        var bubbleTb = AddStreamingBubble("Ollama", SenderType.Ollama);
        var sb = new StringBuilder();

        try
        {
            _streamCts = new CancellationTokenSource();

            await foreach (var token in _ollama.StreamAsync(_ollamaHistory, _streamCts.Token))
            {
                sb.Append(token);
                bubbleTb.Text = sb.ToString();
                ChatScrollViewer.ScrollToBottom();
            }

            _ollamaHistory.Add(new OllamaChatMessage("assistant", sb.ToString()));
        }
        catch (OperationCanceledException)
        {
            bubbleTb.Text = sb.Append(" [abgebrochen]").ToString();
        }
        catch (HttpRequestException ex)
        {
            bubbleTb.Text = $"Verbindungsfehler: {ex.Message}";
            AddSystemMessage("⚠  Ollama nicht erreichbar.");
        }
        catch (Exception ex)
        {
            bubbleTb.Text = $"Fehler: {ex.Message}";
        }
        finally
        {
            _streamCts?.Dispose();
            _streamCts = null;
            AIRespondButton.IsEnabled = true;
            SendButton.IsEnabled = true;
        }
    }

    // ── Sidebar-Aktionen ───────────────────────────────────────────────────

    private void ClearChat_Click(object sender, RoutedEventArgs e)
    {
        _streamCts?.Cancel();
        ChatPanel.Children.Clear();
        _ollamaHistory.Clear();
        AddSystemMessage("Chat geleert.");
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // TODO: Einstellungs-Dialog öffnen
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeComboBox.SelectedItem is not ComboBoxItem item) return;

        string path = (item.Tag?.ToString() ?? "Mocha") switch
        {
            "Macchiato" => "Themes/CatpuccinMacchiato.xaml",
            "Frappe"    => "Themes/CatppuccinFrappe.xaml",
            "Latte"     => "Themes/CatppuccinLatte.xaml",
            _           => "Themes/CatppuccinMocha.xaml"
        };

        var dict = new ResourceDictionary { Source = new Uri(path, UriKind.Relative) };
        Resources.MergedDictionaries.Clear();
        Resources.MergedDictionaries.Add(dict);
    }

    // ── Nachrichten-Rendering ──────────────────────────────────────────────

    private void AddSystemMessage(string text)
    {
        ChatPanel.Children.Add(new TextBlock
        {
            Text = text,
            TextAlignment = TextAlignment.Center,
            Foreground = GetRes("SubtextBrush"),
            FontSize = 11,
            FontFamily = new FontFamily("Segoe UI"),
            Margin = new Thickness(0, 10, 0, 10)
        });
    }

    public void AddMessage(string senderName, string text, SenderType type)
    {
        var tb = AddStreamingBubble(senderName, type);
        tb.Text = text;
        ChatScrollViewer.ScrollToBottom();
    }

    // Fügt eine Bubble ohne Text ein und gibt das TextBlock-Innere zurück.
    // Wird sowohl von AddMessage als auch von AIRespond_Click (Streaming) genutzt.
    private TextBlock AddStreamingBubble(string senderName, SenderType type)
    {
        bool isUser = type == SenderType.User;

        var avatar = new Border
        {
            Width = 34, Height = 34,
            CornerRadius = new CornerRadius(9),
            Background = GetRes(AccentKey(type)),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = isUser ? new Thickness(10, 0, 0, 0) : new Thickness(0, 0, 10, 0),
            Child = new TextBlock
            {
                Text = senderName[0].ToString(),
                FontSize = 14, FontWeight = FontWeights.Bold,
                Foreground = GetRes("SidebarBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var contentTb = new TextBlock
        {
            Foreground = GetRes("TextBrush"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            FontFamily = new FontFamily("Segoe UI")
        };

        var bubble = new Border
        {
            Background = GetRes(BubbleKey(type)),
            CornerRadius = isUser
                ? new CornerRadius(12, 3, 12, 12)
                : new CornerRadius(3, 12, 12, 12),
            Padding = new Thickness(13, 9, 13, 9),
            Child = contentTb
        };

        var nameLabel = new TextBlock
        {
            Text = senderName,
            FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = GetRes(AccentKey(type)),
            Margin = new Thickness(isUser ? 0 : 3, 0, isUser ? 3 : 0, 3),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };
        var timeLabel = new TextBlock
        {
            Text = DateTime.Now.ToString("HH:mm"),
            FontSize = 10,
            Foreground = GetRes("SubtextBrush"),
            Margin = new Thickness(3, 4, 3, 0),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };

        var content = new StackPanel { MaxWidth = 580 };
        content.Children.Add(nameLabel);
        content.Children.Add(bubble);
        content.Children.Add(timeLabel);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
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

    private static string AccentKey(SenderType t) => t switch
    {
        SenderType.Claude => "ClaudeBrush",
        SenderType.Ollama => "OllamaBrush",
        _                 => "UserBrush"
    };

    private static string BubbleKey(SenderType t) => t switch
    {
        SenderType.Claude => "ClaudeBubbleBrush",
        SenderType.Ollama => "OllamaBubbleBrush",
        _                 => "UserBubbleBrush"
    };

    private System.Windows.Media.Brush GetRes(string key)
        => (System.Windows.Media.Brush)FindResource(key);
}
