using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudetRelay.Services;

namespace ClaudetRelay;

public partial class SettingsWindow : Window
{
    private bool _suppressProviderEvent = false;

    public SettingsWindow(string? currentThemePath)
    {
        // Apply the current theme before InitializeComponent so DynamicResource resolves
        if (currentThemePath is not null)
        {
            try
            {
                Resources.MergedDictionaries.Add(
                    new ResourceDictionary { Source = new Uri(currentThemePath) });
            }
            catch { /* no theme – window still usable */ }
        }

        InitializeComponent();
        LoadSettings();
    }

    // ── Load current state into controls ──────────────────────────────────

    private void LoadSettings()
    {
        var s = SettingsService.Load();

        // Participants tab
        CloudAIEnabledCheck.IsChecked          = s.CloudAIEnabled;
        OllamaInstanceCombo.SelectedIndex       = Math.Clamp(s.OllamaInstanceCount - 1, 0, 2);
        SelectComboByContent(ParticipantsProviderCombo, s.SelectedProvider);

        // Ollama tab
        OllamaUrlBox.Text = s.OllamaBaseUrl;

        // Cloud AI tab – suppress SelectionChanged during initial population
        _suppressProviderEvent = true;
        SelectComboByContent(ProviderCombo, s.SelectedProvider);
        _suppressProviderEvent = false;

        LoadApiKeyForProvider(s.SelectedProvider);
        UpdateApiKeyHint(s.SelectedProvider);
        PopulateModelCombo(s.SelectedProvider, s.SelectedCloudModel);
    }

    private static void SelectComboByContent(ComboBox combo, string value)
    {
        foreach (ComboBoxItem item in combo.Items)
            if (item.Content?.ToString() == value)
            {
                combo.SelectedItem = item;
                return;
            }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private void LoadApiKeyForProvider(string provider)
    {
        var key = WindowsCredentialManager.Load(provider) ?? "";
        ApiKeyBox.Password    = key;
        ApiKeyTextBox.Text    = key;
    }

    private void UpdateApiKeyHint(string provider)
    {
        ApiKeyHint.Text = provider switch
        {
            "Anthropic"  => "Get your key at console.anthropic.com",
            "Google AI"  => "Free tier at aistudio.google.com — no credit card required!",
            "Groq"       => "Free tier at console.groq.com — no credit card required!",
            "OpenRouter" => "Get your key at openrouter.ai/keys",
            "Mistral"    => "Get your key at console.mistral.ai",
            _            => ""
        };
    }

    private void PopulateModelCombo(string provider, string selectedModel)
    {
        var models = GetDefaultModels(provider);
        CloudModelCombo.Items.Clear();
        foreach (var m in models)
        {
            var item = new ComboBoxItem { Content = m };
            CloudModelCombo.Items.Add(item);
            if (m == selectedModel) CloudModelCombo.SelectedItem = item;
        }
        if (CloudModelCombo.SelectedItem is null && CloudModelCombo.Items.Count > 0)
            CloudModelCombo.SelectedIndex = 0;
    }

    private static string[] GetDefaultModels(string provider) => provider switch
    {
        "Anthropic"  => AnthropicService.DefaultModels,
        "Google AI"  => GoogleAIService.DefaultModels,
        "Groq"       => GroqService.DefaultModels,
        "OpenRouter" => OpenRouterService.DefaultModels,
        "Mistral"    => MistralService.DefaultModels,
        _            => AnthropicService.DefaultModels
    };

    private string CurrentProvider =>
        (ProviderCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Anthropic";

    private string CurrentApiKey =>
        ApiKeyBox.Visibility == Visibility.Visible
            ? ApiKeyBox.Password
            : ApiKeyTextBox.Text;

    // ── Events ──────────────────────────────────────────────────────────────

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProviderEvent) return;
        var name = CurrentProvider;
        LoadApiKeyForProvider(name);
        UpdateApiKeyHint(name);
        PopulateModelCombo(name, "");
        TestResultLabel.Text = "";
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        => ApiKeyTextBox.Text = ApiKeyBox.Password;

    private void ApiKeyTextBox_TextChanged(object sender, TextChangedEventArgs e)
        => ApiKeyBox.Password = ApiKeyTextBox.Text;

    private void ShowHide_Click(object sender, RoutedEventArgs e)
    {
        if (ApiKeyBox.Visibility == Visibility.Visible)
        {
            ApiKeyTextBox.Text       = ApiKeyBox.Password;
            ApiKeyBox.Visibility     = Visibility.Collapsed;
            ApiKeyTextBox.Visibility = Visibility.Visible;
            ApiKeyTextBox.Focus();
            ApiKeyTextBox.CaretIndex = ApiKeyTextBox.Text.Length;
        }
        else
        {
            ApiKeyBox.Password       = ApiKeyTextBox.Text;
            ApiKeyTextBox.Visibility = Visibility.Collapsed;
            ApiKeyBox.Visibility     = Visibility.Visible;
        }
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        TestResultLabel.Foreground = (Brush)FindResource("SubtextBrush");
        TestResultLabel.Text = "Testing…";

        try
        {
            var apiKey = CurrentApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                TestResultLabel.Text = "⚠  Enter an API key first.";
                return;
            }

            using var svc = BuildService(CurrentProvider, apiKey);
            var ok = await svc.IsAvailableAsync();

            TestResultLabel.Foreground = ok
                ? (Brush)FindResource("OllamaBrush")
                : (Brush)FindResource("AccentBrush");
            TestResultLabel.Text = ok ? "Connected ✓" : "Failed ✗  (check your key)";

            if (ok)
            {
                try
                {
                    var models = await svc.GetModelsAsync();
                    if (models.Count > 0)
                    {
                        var current = (CloudModelCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
                        CloudModelCombo.Items.Clear();
                        foreach (var m in models)
                        {
                            var item = new ComboBoxItem { Content = m };
                            CloudModelCombo.Items.Add(item);
                            if (m == current) CloudModelCombo.SelectedItem = item;
                        }
                        if (CloudModelCombo.SelectedItem is null && CloudModelCombo.Items.Count > 0)
                            CloudModelCombo.SelectedIndex = 0;
                    }
                }
                catch { /* keep default list */ }
            }
        }
        catch (Exception ex)
        {
            TestResultLabel.Foreground = (Brush)FindResource("AccentBrush");
            TestResultLabel.Text = $"Error: {ex.Message}";
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private async void TestOllama_Click(object sender, RoutedEventArgs e)
    {
        TestOllamaButton.IsEnabled = false;
        OllamaTestLabel.Foreground = (Brush)FindResource("SubtextBrush");
        OllamaTestLabel.Text = "Testing…";
        try
        {
            using var svc = new OllamaService(OllamaUrlBox.Text.Trim());
            var ok = await svc.IsAvailableAsync();
            OllamaTestLabel.Foreground = ok
                ? (Brush)FindResource("OllamaBrush")
                : (Brush)FindResource("AccentBrush");
            OllamaTestLabel.Text = ok ? "Online ✓" : "Offline ✗";
        }
        catch
        {
            OllamaTestLabel.Foreground = (Brush)FindResource("AccentBrush");
            OllamaTestLabel.Text = "Offline ✗";
        }
        finally { TestOllamaButton.IsEnabled = true; }
    }

    private void SaveCloudAI_Click(object sender, RoutedEventArgs e)
    {
        var provider = CurrentProvider;
        var apiKey   = CurrentApiKey;
        var model    = (CloudModelCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

        if (!string.IsNullOrWhiteSpace(apiKey))
            WindowsCredentialManager.Save(provider, apiKey);

        var s = SettingsService.Load();
        s.SelectedProvider   = provider;
        s.SelectedCloudModel = model;
        SettingsService.Save(s);

        // Keep Participants tab in sync
        SelectComboByContent(ParticipantsProviderCombo, provider);

        DialogResult = true;
    }

    private void SaveOllama_Click(object sender, RoutedEventArgs e)
    {
        var url = OllamaUrlBox.Text.Trim();
        if (string.IsNullOrEmpty(url)) url = "http://localhost:11434";

        var s = SettingsService.Load();
        s.OllamaBaseUrl = url;
        SettingsService.Save(s);

        OllamaTestLabel.Text = "Saved ✓";
        DialogResult = true;
    }

    private void SaveParticipants_Click(object sender, RoutedEventArgs e)
    {
        var s = SettingsService.Load();
        s.CloudAIEnabled      = CloudAIEnabledCheck.IsChecked == true;
        s.OllamaInstanceCount = OllamaInstanceCombo.SelectedIndex + 1;
        s.SelectedProvider    = (ParticipantsProviderCombo.SelectedItem as ComboBoxItem)
                                    ?.Content?.ToString() ?? s.SelectedProvider;
        SettingsService.Save(s);
        DialogResult = true;
    }

    // ── Factory ─────────────────────────────────────────────────────────────

    private static ICloudAIService BuildService(string provider, string apiKey) => provider switch
    {
        "Google AI"  => new GoogleAIService(apiKey),
        "Groq"       => new GroqService(apiKey),
        "OpenRouter" => new OpenRouterService(apiKey),
        "Mistral"    => new MistralService(apiKey),
        _            => new AnthropicService(apiKey)
    };
}
