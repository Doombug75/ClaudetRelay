using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClaudetRelay.Properties;
using ClaudetRelay.Services;

namespace ClaudetRelay;

public partial class MainWindow
{
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

        // Create participants from settings (welcome hint handles the no-participants state)
        foreach (var p in settings.Participants.Where(p => p.Enabled))
        {
            if (p.Type == "Ollama")
                AddOllamaParticipant(p.Model, p.ServerUrl, p.Name, p.OllamaNumCtx, p.OllamaNumPredict);
            else
                AddCloudAIParticipant(p.Type, p.Model, p.Name, p.ServerUrl, p.CloudMaxTokens);
        }

        // User display name & tone
        _userName          = string.IsNullOrWhiteSpace(settings.UserName) ? "You" : settings.UserName.Trim();
        _toneLevel         = settings.ToneLevel;
        _chattinessLevel   = settings.GlobalChattiness;
        _mockingbirdMode   = settings.MockingbirdMode;
        _buccaneeerMode    = settings.BuccaneerMode;

        // AI dialogue toggle + depth
        _aiDialogueEnabled    = settings.AiDialogueEnabled;
        _aiDialogueMaxTurns   = Math.Clamp(settings.AiDialogueMaxTurns, 3, 100);
        _globalResponseLength = Math.Clamp(settings.GlobalResponseLength, 0, 100);
        _uiLanguageName       = UiLanguageCodeToName(settings.Language ?? "");
        UpdateAiDialogueButton();

        // Rate limiters
        ApplyThrottleSettings(settings);

        // Show welcome hint when nothing is configured yet
        RefreshWelcomeHint();
    }

    /// <summary>
    /// Rebuilds the per-provider rate-limiter table from saved settings.
    /// Call once on startup and again whenever the settings window is saved.
    /// </summary>
    private void ApplyThrottleSettings(AppSettings settings)
    {
        _rateLimiters.Clear();

        // Per-participant rate limits keyed by "type|model".
        // When two participants share the same type+model the most permissive limit wins
        // (they share an API budget anyway — the tighter one would block both).
        foreach (var p in settings.Participants.Where(p => p.RpmEnabled && p.Rpm >= 1))
        {
            var key = $"{p.Type}|{p.Model}";
            if (!_rateLimiters.TryGetValue(key, out var existing) || existing.Rpm < p.Rpm)
                _rateLimiters[key] = new ProviderRateLimiter(p.Rpm);
        }
    }

    // ── Re-initialize after Settings save ─────────────────────────────────

    private void ReInitializeParticipants()
    {
        _streamCts?.Cancel();

        // Remove Cloud AI cards
        foreach (var ui in _cloudAIParticipants.ToList())
        {
            ParticipantsPanel.Children.Remove(ui.Popup);
            ParticipantsPanel.Children.Remove(ui.Card);
            ui.Data.Service.Dispose();
        }
        _cloudAIParticipants.Clear();

        // Remove Ollama cards
        foreach (var ui in _ollamaParticipants.ToList())
        {
            ParticipantsPanel.Children.Remove(ui.Popup);
            ParticipantsPanel.Children.Remove(ui.Card);
        }
        _ollamaParticipants.Clear();
        _availableOllamaModels.Clear();

        // Re-add from settings
        var settings = SettingsService.Load();
        _userName             = string.IsNullOrWhiteSpace(settings.UserName) ? "You" : settings.UserName.Trim();
        _toneLevel            = settings.ToneLevel;
        _chattinessLevel      = settings.GlobalChattiness;
        _mockingbirdMode      = settings.MockingbirdMode;
        _aiDialogueEnabled    = settings.AiDialogueEnabled;
        _aiDialogueMaxTurns   = Math.Clamp(settings.AiDialogueMaxTurns, 3, 100);
        _globalResponseLength = Math.Clamp(settings.GlobalResponseLength, 0, 100);
        _uiLanguageName       = UiLanguageCodeToName(settings.Language ?? "");
        UpdateAiDialogueButton();

        foreach (var p in settings.Participants.Where(p => p.Enabled))
        {
            if (p.Type == "Ollama")
                AddOllamaParticipant(p.Model, p.ServerUrl, p.Name, p.OllamaNumCtx, p.OllamaNumPredict);
            else
                AddCloudAIParticipant(p.Type, p.Model, p.Name, p.ServerUrl, p.CloudMaxTokens);
        }

        UpdateAddRemoveButtons();
        UpdateCloudAIAddRemoveButtons();
        RefreshWelcomeHint();
        _ = CheckAllStatusAsync();
    }

    // ── Per-project participant persistence ────────────────────────────────

    /// <summary>
    /// Snapshots the current live participants into the open project's settings file.
    /// Safe to call when no project is open (no-op).
    /// </summary>
    private void SaveProjectParticipants()
    {
        if (_currentProjectFolder is null || _projectSettings is null) return;

        var saved = new List<ParticipantConfig>();

        // Walk the unified panel in visual (slot) order so the saved list always
        // matches top-to-bottom card order, regardless of participant type.
        foreach (FrameworkElement child in ParticipantsPanel.Children)
        {
            var cloud = _cloudAIParticipants.FirstOrDefault(u => ReferenceEquals(u.Card, child));
            if (cloud is not null)
            {
                saved.Add(new ParticipantConfig
                {
                    Name      = cloud.Data.CustomName ?? "",
                    Type      = cloud.Data.Service.ProviderName,
                    Model     = cloud.Data.Service.CurrentModel,
                    ServerUrl = "",
                    Enabled   = cloud.Data.Enabled
                });
                continue;
            }

            var ollama = _ollamaParticipants.FirstOrDefault(u => ReferenceEquals(u.Card, child));
            if (ollama is not null)
            {
                saved.Add(new ParticipantConfig
                {
                    Name      = ollama.Data.CustomName ?? "",
                    Type      = "Ollama",
                    Model     = ollama.Data.Service.CurrentModel,
                    ServerUrl = ollama.Data.Service.BaseUrl,
                    Enabled   = ollama.Data.Enabled
                });
            }
        }

        _projectSettings.ActiveParticipants = saved;
        try { ProjectService.SaveProject(_currentProjectFolder!, _projectSettings); }
        catch { /* non-fatal - settings will re-save next time */ }
    }

    // ── Chat-state snapshot / restore ─────────────────────────────────────────
    // Used to preserve in-session "removed from chat" and "deactivated in chat"
    // states across a ParticipantsWindow open/close cycle.

    private record ChatParticipantState(string Type, string Model, string ServerUrl, bool IsEnabled);

    /// <summary>
    /// Captures the current live participant state: which participants are in the
    /// chat panel and whether each is enabled. Call BEFORE ReInitializeParticipants().
    /// </summary>
    private List<ChatParticipantState> SnapshotChatStates()
    {
        var states = new List<ChatParticipantState>();
        foreach (var ui in _cloudAIParticipants)
            states.Add(new ChatParticipantState(
                ui.Data.Service.ProviderName,
                ui.Data.Service.CurrentModel,
                "",
                ui.Data.Enabled));
        foreach (var ui in _ollamaParticipants)
            states.Add(new ChatParticipantState(
                "Ollama",
                ui.Data.Service.CurrentModel,
                ui.Data.Service.BaseUrl,
                ui.Data.Enabled));
        return states;
    }

    /// <summary>
    /// Re-applies a previously snapshotted chat state after ReInitializeParticipants()
    /// has rebuilt the panel from global settings.
    /// • Participants not in the snapshot (removed from chat) are removed again.
    /// • Participants that were disabled are disabled again.
    /// </summary>
    private void RestoreChatStates(List<ChatParticipantState> states)
    {
        // Remove participants that weren't in the chat before the reinit
        foreach (var ui in _cloudAIParticipants.ToList())
        {
            bool wasPresent = states.Any(s =>
                s.Type  == ui.Data.Service.ProviderName &&
                s.Model == ui.Data.Service.CurrentModel);
            if (!wasPresent) RemoveCloudAIParticipant(ui);
        }
        foreach (var ui in _ollamaParticipants.ToList())
        {
            bool wasPresent = states.Any(s =>
                s.Type      == "Ollama" &&
                s.Model     == ui.Data.Service.CurrentModel &&
                s.ServerUrl == ui.Data.Service.BaseUrl);
            if (!wasPresent) RemoveOllamaParticipant(ui);
        }

        // Re-apply disabled state for participants that were deactivated in-chat
        foreach (var state in states.Where(s => !s.IsEnabled))
        {
            if (state.Type == "Ollama")
            {
                var ui = _ollamaParticipants.FirstOrDefault(u =>
                    u.Data.Service.CurrentModel == state.Model &&
                    u.Data.Service.BaseUrl      == state.ServerUrl);
                if (ui is not null) OnOllamaEnabledChanged(ui, false);
            }
            else
            {
                var ui = _cloudAIParticipants.FirstOrDefault(u =>
                    u.Data.Service.ProviderName == state.Type &&
                    u.Data.Service.CurrentModel == state.Model);
                if (ui is not null) OnCloudAIEnabledChanged(ui, false);
            }
        }
    }

    /// <summary>
    /// Clears all current participants and re-adds from the saved list.
    /// Falls back to global settings if nothing from the list can be added.
    /// </summary>
    private void ReInitializeParticipantsFrom(List<ParticipantConfig> saved)
    {
        _streamCts?.Cancel();

        // Remove Cloud AI
        foreach (var ui in _cloudAIParticipants.ToList())
        {
            ParticipantsPanel.Children.Remove(ui.Popup);
            ParticipantsPanel.Children.Remove(ui.Card);
            ui.Data.Service.Dispose();
        }
        _cloudAIParticipants.Clear();

        // Remove Ollama
        foreach (var ui in _ollamaParticipants.ToList())
        {
            ParticipantsPanel.Children.Remove(ui.Popup);
            ParticipantsPanel.Children.Remove(ui.Card);
        }
        _ollamaParticipants.Clear();
        _availableOllamaModels.Clear();

        // Re-add from saved list
        foreach (var p in saved)
        {
            if (p.Type == "Ollama")
                AddOllamaParticipant(p.Model, p.ServerUrl, p.Name, p.OllamaNumCtx, p.OllamaNumPredict);
            else
                AddCloudAIParticipant(p.Type, p.Model, p.Name, p.ServerUrl, p.CloudMaxTokens);
        }

        // Apply saved enabled/disabled state (add functions default to enabled=true)
        foreach (var p in saved)
        {
            if (p.Enabled) continue; // already enabled - skip
            if (p.Type == "Ollama")
            {
                var match = _ollamaParticipants.FirstOrDefault(ui =>
                    ui.Data.Service.CurrentModel == p.Model &&
                    ui.Data.Service.BaseUrl      == p.ServerUrl);
                if (match is not null)
                {
                    match.Data.Enabled  = false;
                    match.Card.Opacity  = 0.45;
                }
            }
            else
            {
                var match = _cloudAIParticipants.FirstOrDefault(ui =>
                    ui.Data.Service.ProviderName == p.Type &&
                    ui.Data.Service.CurrentModel == p.Model);
                if (match is not null)
                {
                    match.Data.Enabled  = false;
                    match.Card.Opacity  = 0.45;
                }
            }
        }

        // Fallback: if nothing was restored (e.g. all API keys gone), use global settings
        if (_ollamaParticipants.Count == 0 && _cloudAIParticipants.Count == 0)
        {
            var settings = SettingsService.Load();
            foreach (var p in settings.Participants.Where(p => p.Enabled))
            {
                if (p.Type == "Ollama")
                    AddOllamaParticipant(p.Model, p.ServerUrl, p.Name, p.OllamaNumCtx, p.OllamaNumPredict);
                else
                    AddCloudAIParticipant(p.Type, p.Model, p.Name, p.ServerUrl, p.CloudMaxTokens);
            }
        }

        UpdateAddRemoveButtons();
        UpdateCloudAIAddRemoveButtons();
        RefreshWelcomeHint();
        _ = CheckAllStatusAsync();
    }

    /// <summary>
    /// Applies IsActive flags from ProjectParticipantRole list to the live sidebar cards.
    /// Used on first project open before any ActiveParticipants snapshot exists, so that
    /// participants unchecked in Project Settings start as disabled in the live session.
    /// </summary>
    /// <summary>
    /// Applies <see cref="ProjectParticipantRole.IsActive"/> to the live card grid.
    /// Inactive roles → participant deactivated in chat.
    /// Active roles   → participant re-enabled (so toggling IsActive on→off→on works correctly).
    /// </summary>
    private void ApplyRoleActiveStatesToParticipants(List<ProjectParticipantRole> roles)
    {
        foreach (var role in roles)
        {
            var cloudMatch = _cloudAIParticipants.FirstOrDefault(ui =>
                (string.IsNullOrEmpty(role.DisplayName) ||
                 string.Equals(ui.Data.CustomName, role.DisplayName, StringComparison.OrdinalIgnoreCase)) &&
                string.Equals(ui.Data.Service.ProviderName, role.Provider, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(ui.Data.Service.CurrentModel, role.Model, StringComparison.OrdinalIgnoreCase));
            if (cloudMatch is not null)
            {
                OnCloudAIEnabledChanged(cloudMatch, role.IsActive);
                continue;
            }
            var ollamaMatch = _ollamaParticipants.FirstOrDefault(ui =>
                (string.IsNullOrEmpty(role.DisplayName) ||
                 string.Equals(ui.Data.CustomName, role.DisplayName, StringComparison.OrdinalIgnoreCase)) &&
                string.Equals(ui.Data.Service.CurrentModel, role.Model, StringComparison.OrdinalIgnoreCase));
            if (ollamaMatch is not null)
                OnOllamaEnabledChanged(ollamaMatch, role.IsActive);
        }
    }

    /// <summary>
    /// Re-applies the project-specific enabled/disabled states to current live participants
    /// without removing and re-adding cards.  Call after ReInitializeParticipants() when
    /// a project is open, so that project-level disabled states are not wiped by the global
    /// participant refresh.
    /// </summary>
    private void ApplyProjectParticipantEnabledStates(List<ParticipantConfig> saved)
    {
        foreach (var p in saved)
        {
            if (p.Enabled) continue; // default is enabled — only need to disable
            if (p.Type == "Ollama")
            {
                var match = _ollamaParticipants.FirstOrDefault(ui =>
                    ui.Data.Service.CurrentModel == p.Model &&
                    ui.Data.Service.BaseUrl      == p.ServerUrl);
                if (match is not null)
                {
                    match.Data.Enabled  = false;
                    match.Card.Opacity  = 0.45;
                    if (match.EnabledToggle is not null)
                        match.EnabledToggle.IsChecked = false;
                }
            }
            else
            {
                var match = _cloudAIParticipants.FirstOrDefault(ui =>
                    ui.Data.Service.ProviderName == p.Type &&
                    ui.Data.Service.CurrentModel == p.Model);
                if (match is not null)
                {
                    match.Data.Enabled  = false;
                    match.Card.Opacity  = 0.45;
                    if (match.EnabledToggle is not null)
                        match.EnabledToggle.IsChecked = false;
                }
            }
        }
    }

    private void AddCloudAIParticipant(string provider, string model = "", string customName = "", string serverUrl = "", int cloudMaxTokens = 0)
    {
        if (_cloudAIParticipants.Count >= 20) return;

        var apiKey   = WindowsCredentialManager.Load(provider) ?? "";
        bool needsKey = provider is not "Ollama ☁" // Ollama ☁ uses key, but vLLM/LM Studio may not
            && provider is not "vLLM" and not "LM Studio";
        if (needsKey && string.IsNullOrWhiteSpace(apiKey)) return;

        var service = CreateCloudAIService(provider, apiKey, serverUrl);
        if (!string.IsNullOrEmpty(model)) service.CurrentModel = model;
        if (cloudMaxTokens > 0) service.MaxTokens = cloudMaxTokens;

        var participant = new CloudAIParticipant
        {
            Service    = service,
            Position   = _cloudAIParticipants.Count + 1,
            CustomName = string.IsNullOrWhiteSpace(customName) ? null : customName
        };
        BuildCloudAICard(participant);
        UpdateCloudAIAddRemoveButtons();
        RefreshWelcomeHint();
    }

    /// <summary>Shows the welcome hint when there are no active participants, hides it otherwise.</summary>
    private void RefreshWelcomeHint()
    {
        WelcomeHint.Visibility = (_ollamaParticipants.Count + _cloudAIParticipants.Count) == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RemoveCloudAIParticipant(CloudAIParticipantUI ui)
    {
        ParticipantsPanel.Children.Remove(ui.Popup);
        ParticipantsPanel.Children.Remove(ui.Card);
        ui.Data.Service.Dispose();
        _cloudAIParticipants.Remove(ui);
        RenumberCloudAIParticipants();
        UpdateCloudAIAddRemoveButtons();
        RefreshWelcomeHint();
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
        avatarText.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");

        var avatarBorder = new Border
        {
            Width        = 34, Height = 34,
            CornerRadius = new CornerRadius(9),
            Child        = avatarText
        };
        avatarBorder.SetResourceReference(Border.BackgroundProperty, participant.ColorKey);

        // ── Role badges - outline-only tags, no fill background ──
        var coBadge = new Border
        {
            CornerRadius    = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 1, 4, 1),
            Margin          = new Thickness(0, 0, 3, 0),
            Background      = Brushes.Transparent,
            Visibility      = Visibility.Collapsed,
            Child           = new TextBlock { Text = "CO", FontSize = 8, FontWeight = FontWeights.Normal,
                                  VerticalAlignment = VerticalAlignment.Center }
        };
        coBadge.SetResourceReference(Border.BorderBrushProperty, "AccentBgBrush");
        ((TextBlock)coBadge.Child).SetResourceReference(TextBlock.ForegroundProperty, "AccentBgBrush");

        var rBadge = new Border
        {
            CornerRadius    = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 1, 4, 1),
            Margin          = new Thickness(0, 0, 3, 0),
            Background      = Brushes.Transparent,
            Visibility      = Visibility.Collapsed,
            Child           = new TextBlock { Text = "R", FontSize = 8, FontWeight = FontWeights.Normal,
                                  VerticalAlignment = VerticalAlignment.Center }
        };
        rBadge.SetResourceReference(Border.BorderBrushProperty, "ContentDimBrush");
        ((TextBlock)rBadge.Child).SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var crBadge = new Border
        {
            CornerRadius    = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 1, 4, 1),
            Margin          = new Thickness(0, 0, 3, 0),
            Background      = Brushes.Transparent,
            Visibility      = Visibility.Collapsed,
            Child           = new TextBlock { Text = "CR", FontSize = 8, FontWeight = FontWeights.Normal,
                                  VerticalAlignment = VerticalAlignment.Center }
        };
        crBadge.SetResourceReference(Border.BorderBrushProperty, "ContentDimBrush");
        ((TextBlock)crBadge.Child).SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var plBadge = new Border
        {
            CornerRadius    = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 1, 4, 1),
            Margin          = new Thickness(0, 0, 3, 0),
            Background      = Brushes.Transparent,
            Visibility      = Visibility.Collapsed,
            Child           = new TextBlock { Text = "PL", FontSize = 8, FontWeight = FontWeights.Normal,
                                  VerticalAlignment = VerticalAlignment.Center }
        };
        plBadge.SetResourceReference(Border.BorderBrushProperty, "ContentDimBrush");
        ((TextBlock)plBadge.Child).SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var rsBadge = new Border
        {
            CornerRadius    = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 1, 4, 1),
            Margin          = new Thickness(0, 0, 3, 0),
            Background      = Brushes.Transparent,
            Visibility      = Visibility.Collapsed,
            Child           = new TextBlock { Text = "RS", FontSize = 8, FontWeight = FontWeights.Normal,
                                  VerticalAlignment = VerticalAlignment.Center }
        };
        rsBadge.SetResourceReference(Border.BorderBrushProperty, "ContentDimBrush");
        ((TextBlock)rsBadge.Child).SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var wrBadge = new Border
        {
            CornerRadius    = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 1, 4, 1),
            Margin          = new Thickness(0, 0, 3, 0),
            Background      = Brushes.Transparent,
            Visibility      = Visibility.Collapsed,
            Child           = new TextBlock { Text = "WR", FontSize = 8, FontWeight = FontWeights.Normal,
                                  VerticalAlignment = VerticalAlignment.Center }
        };
        wrBadge.SetResourceReference(Border.BorderBrushProperty, "SecondaryAccentBrush");
        ((TextBlock)wrBadge.Child).SetResourceReference(TextBlock.ForegroundProperty, "SecondaryAccentBrush");

        // Error badge - stays on the avatar (status indicator, not a role)
        var errorBadgeCloud = new Border
        {
            CornerRadius        = new CornerRadius(3),
            Padding             = new Thickness(3, 0, 3, 0),
            Height              = 13,
            Background          = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Bottom,
            Visibility          = Visibility.Collapsed,
            Child               = new TextBlock
                                  {
                                      Text                = "!",
                                      FontSize            = 9,
                                      FontWeight          = FontWeights.Bold,
                                      Foreground          = new SolidColorBrush(Color.FromRgb(255, 220, 0)),
                                      HorizontalAlignment = HorizontalAlignment.Center,
                                      VerticalAlignment   = VerticalAlignment.Center
                                  }
        };

        // Avatar only holds the circle and the error indicator - role badges moved to row below
        var avatarContainer = new Grid
        {
            Width             = 38, Height = 38,
            Margin            = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        avatarContainer.Children.Add(avatarBorder);
        avatarContainer.Children.Add(errorBadgeCloud);

        // ── Status dot ────────────────────────────────────────────────────
        var statusDot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        statusDot.SetResourceReference(Ellipse.FillProperty, "ContentDimBrush");

        // ── Labels ────────────────────────────────────────────────────────
        var nameLabel = new TextBlock
        {
            Text       = participant.DisplayName,
            FontSize   = 13,
            FontWeight = FontWeights.SemiBold
        };
        nameLabel.SetResourceReference(TextBlock.ForegroundProperty, "ControlTextBrush");

        var modelLabel = new TextBlock
        {
            Text    = FormatModelDisplayName(participant.Service.CurrentModel),
            FontSize = 10
        };
        modelLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var offlineLabel = new TextBlock { Text = "Offline", FontSize = 10, Visibility = Visibility.Collapsed };
        offlineLabel.SetResourceReference(TextBlock.ForegroundProperty, "AccentBgBrush");

        var statusLabelCloud = new TextBlock { FontSize = 10, Visibility = Visibility.Collapsed };

        var stopButtonCloud = new Button
        {
            Content           = "⏹",
            Width             = 16, Height = 16,
            FontSize          = 8,
            BorderThickness   = new Thickness(0),
            Cursor            = Cursors.Hand,
            Padding           = new Thickness(0),
            Visibility        = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(5, 0, 0, 0),
            ToolTip           = "Stop this model",
            Style             = (Style)FindResource("ModernButton")
        };
        stopButtonCloud.SetResourceReference(Button.BackgroundProperty, "AccentBgBrush");
        stopButtonCloud.SetResourceReference(Button.ForegroundProperty, "AccentTextBrush");

        // ── Layout ────────────────────────────────────────────────────────
        // Status row: "Thinking..." label + per-card stop button inline
        var statusRowCloud = new StackPanel { Orientation = Orientation.Horizontal };
        statusRowCloud.Children.Add(statusLabelCloud);
        statusRowCloud.Children.Add(stopButtonCloud);

        var labelPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labelPanel.Children.Add(nameLabel);
        labelPanel.Children.Add(modelLabel);
        labelPanel.Children.Add(offlineLabel);
        labelPanel.Children.Add(statusRowCloud);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(avatarContainer, 0);
        Grid.SetColumn(labelPanel,      1);
        Grid.SetColumn(statusDot,       2);

        grid.Children.Add(avatarContainer);
        grid.Children.Add(labelPanel);
        grid.Children.Add(statusDot);

        // ── Badge row - themed pills in a horizontal strip below the main row ──
        var badgeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 5, 0, 0),
            Visibility  = Visibility.Collapsed
        };
        badgeRow.Children.Add(coBadge);
        badgeRow.Children.Add(rBadge);
        badgeRow.Children.Add(crBadge);
        badgeRow.Children.Add(plBadge);
        badgeRow.Children.Add(rsBadge);
        badgeRow.Children.Add(wrBadge);

        var cardContent = new StackPanel();
        cardContent.Children.Add(grid);
        cardContent.Children.Add(badgeRow);

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding      = new Thickness(10),
            Margin       = new Thickness(0, 0, 0, 7),
            Cursor       = Cursors.Hand,
            Child        = cardContent
        };
        card.SetResourceReference(Border.BackgroundProperty,   "ControlBgBrush");
        card.BorderThickness = new Thickness(1);
        card.SetResourceReference(Border.BorderBrushProperty,  "ControlBorderBrush");

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
        separator.SetResourceReference(Rectangle.FillProperty, "ControlBgBrush");

        var enabledToggle = new CheckBox
        {
            Style     = (Style)FindResource("ToggleSwitch"),
            IsChecked = true,
            Content   = $"{participant.DisplayName} enabled",
            Margin    = new Thickness(0, 0, 0, 14)
        };

        var infoProviderKey = new TextBlock { Text = "PROVIDER", FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 3) };
        infoProviderKey.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        var infoProviderVal = new TextBlock { Text = participant.ProviderName, FontSize = 12, Margin = new Thickness(0, 0, 0, 10) };
        infoProviderVal.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");

        var infoModelKey = new TextBlock { Text = "MODEL", FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 3) };
        infoModelKey.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        var infoModelVal = new TextBlock { Text = FormatModelDisplayName(participant.Service.CurrentModel), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        infoModelVal.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");

        var settingsLink = new Button
        {
            Content    = "⚙  Roles & Properties…",
            Margin     = new Thickness(0, 12, 0, 0),
            Padding    = new Thickness(10, 5, 10, 5),
            FontSize   = 12,
            FontFamily = new FontFamily("Segoe UI"),
            Cursor     = Cursors.Hand,
            Visibility = Visibility.Collapsed   // shown only when a project is open
        };
        settingsLink.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        settingsLink.SetResourceReference(Button.ForegroundProperty, "ContentTextBrush");
        settingsLink.SetResourceReference(Button.BorderBrushProperty, "ControlBorderBrush");
        settingsLink.Click += (_, _) =>
        {
            if (_currentProjectFolder is not null)
                ShowProjectSettingsDialog(_currentProjectFolder,
                    _currentProject?.ProjectName ?? "");
        };

        var popupRemoveSep = new Rectangle { Height = 1, Margin = new Thickness(0, 10, 0, 10) };
        popupRemoveSep.SetResourceReference(Rectangle.FillProperty, "ControlBgBrush");

        var removeButton = new Button
        {
            Content    = "👋  " + Properties.Loc.S("Participant_RemoveFromChat"),
            Margin     = new Thickness(0, 0, 0, 0),
            Padding    = new Thickness(10, 5, 10, 5),
            FontSize   = 12,
            FontFamily = new FontFamily("Segoe UI"),
            Cursor     = Cursors.Hand
        };
        removeButton.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        removeButton.SetResourceReference(Button.ForegroundProperty, "AccentBgBrush");
        removeButton.SetResourceReference(Button.BorderBrushProperty, "ControlBorderBrush");

        var popupContent = new StackPanel();
        popupContent.Children.Add(popupTitle);
        popupContent.Children.Add(separator);
        popupContent.Children.Add(enabledToggle);
        popupContent.Children.Add(infoProviderKey);
        popupContent.Children.Add(infoProviderVal);
        popupContent.Children.Add(infoModelKey);
        popupContent.Children.Add(infoModelVal);
        popupContent.Children.Add(settingsLink);
        popupContent.Children.Add(popupRemoveSep);
        popupContent.Children.Add(removeButton);

        var popupBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(12),
            Padding         = new Thickness(14),
            MinWidth        = 230,
            Child           = popupContent,
            Effect          = new DropShadowEffect { Color = Colors.Black, Opacity = 0.45, BlurRadius = 22, ShadowDepth = 4 }
        };
        popupBorder.SetResourceReference(Border.BackgroundProperty,  "SidebarBgBrush");
        popupBorder.SetResourceReference(Border.BorderBrushProperty, "ControlHoverBrush");

        var popup = new Popup
        {
            PlacementTarget    = card,
            Placement          = PlacementMode.Right,
            HorizontalOffset   = 10,
            VerticalOffset     = -8,
            StaysOpen          = true,
            AllowsTransparency = true,
            Child              = popupBorder
        };

        var ui = new CloudAIParticipantUI
        {
            Data          = participant,
            Card          = card,
            AvatarBorder  = avatarBorder,
            AvatarText    = avatarText,
            CoBadge       = coBadge,
            RBadge        = rBadge,
            CrBadge       = crBadge,
            PlBadge       = plBadge,
            RsBadge       = rsBadge,
            WrBadge       = wrBadge,
            BadgeRow      = badgeRow,
            NameLabel     = nameLabel,
            StatusDot     = statusDot,
            ModelLabel    = modelLabel,
            OfflineLabel  = offlineLabel,
            ErrorBadge    = errorBadgeCloud,
            StatusLabel   = statusLabelCloud,
            Popup         = popup,
            PopupTitle    = popupTitle,
            EnabledToggle = enabledToggle,
            RemoveButton  = removeButton,
            StopButton    = stopButtonCloud
        };

        void OpenCloudPopup()
        {
            _suppressEnabledToggle     = true;
            enabledToggle.IsChecked    = ui.Data.Enabled;
            _suppressEnabledToggle     = false;
            infoModelVal.Text          = FormatModelDisplayName(ui.Data.Service.CurrentModel);
            settingsLink.Visibility    = _currentProjectFolder is not null ? Visibility.Visible : Visibility.Collapsed;
            popup.IsOpen               = !popup.IsOpen;
        }

        card.MouseLeftButtonDown  += (_, _) => OpenCloudPopup();
        card.MouseRightButtonUp   += (_, e) => { OpenCloudPopup(); e.Handled = true; };

        enabledToggle.Checked   += (_, _) => { if (!_suppressEnabledToggle) OnCloudAIEnabledChanged(ui, true);  };
        enabledToggle.Unchecked += (_, _) => { if (!_suppressEnabledToggle) OnCloudAIEnabledChanged(ui, false); };

        removeButton.Click    += (_, _) => { popup.IsOpen = false; RemoveCloudAIParticipant(ui); };
        stopButtonCloud.Click += (_, _) => ui.ActiveCts?.Cancel();

        ParticipantsPanel.Children.Add(popup);
        ParticipantsPanel.Children.Add(card);
        _cloudAIParticipants.Add(ui);
    }

    private void OnCloudAIEnabledChanged(CloudAIParticipantUI ui, bool enabled)
    {
        ui.Data.Enabled = enabled;
        double op = enabled && ui.Data.IsOnline == true ? 1.0 : enabled ? 0.6 : 0.45;
        AnimateStatusChange(ui.Card, op);

        if (!enabled)
        {
            ui.StatusLabel.Text       = Properties.Loc.S("Status_Deactivated");
            ui.StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
            ui.StatusLabel.Visibility = Visibility.Visible;
        }
        else if (ui.Data.IsOnline == true)
        {
            ui.StatusLabel.Text       = !string.IsNullOrWhiteSpace(ui.Data.Mood) ? ui.Data.Mood : "Ready";
            ui.StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(100, 190, 100));
            ui.StatusLabel.Visibility = Visibility.Visible;
        }
        else
        {
            ui.StatusLabel.Visibility = Visibility.Collapsed;
        }

        SortParticipantsByEnabledState();
    }

    // ── Participant sort ───────────────────────────────────────────────────────
    /// <summary>Re-orders the ParticipantsPanel so disabled cards sink to the bottom.</summary>
    private void SortParticipantsByEnabledState()
    {
        // Collect (popup, card) pairs in order: enabled first, disabled last.
        // Each participant owns exactly two consecutive children: popup then card.
        var cloudPairs = _cloudAIParticipants
            .Select(p => (popup: (UIElement)p.Popup, card: (UIElement)p.Card, enabled: p.Data.Enabled));
        var ollamaPairs = _ollamaParticipants
            .Select(p => (popup: (UIElement)p.Popup, card: (UIElement)p.Card, enabled: p.Data.Enabled));

        var sorted = cloudPairs.Concat(ollamaPairs)
            .OrderByDescending(x => x.enabled)  // enabled = true sorts first
            .ToList();

        ParticipantsPanel.Children.Clear();
        foreach (var (popup, card, _) in sorted)
        {
            ParticipantsPanel.Children.Add(popup);
            ParticipantsPanel.Children.Add(card);
        }
    }

    // ── Ollama participant management ──────────────────────────────────────

    private void AddOllamaParticipant(string model = "llama3.2",
                                      string serverUrl = "http://localhost:11434",
                                      string customName = "",
                                      int numCtx = 8192,
                                      int numPredict = 2048)
    {
        if (_ollamaParticipants.Count >= 20) return;

        var participant = new OllamaParticipant
        {
            Service    = new OllamaService(serverUrl) { CurrentModel = model, NumCtx = numCtx, NumPredict = numPredict },
            Position   = _ollamaParticipants.Count + 1,
            CustomName = string.IsNullOrWhiteSpace(customName) ? null : customName
        };
        BuildOllamaCard(participant);
        UpdateAddRemoveButtons();
        RefreshWelcomeHint();
    }

    private void RemoveOllamaParticipant(OllamaParticipantUI ui)
    {
        ParticipantsPanel.Children.Remove(ui.Popup);
        ParticipantsPanel.Children.Remove(ui.Card);
        _ollamaParticipants.Remove(ui);

        RenumberParticipants();
        UpdateAddRemoveButtons();
        RefreshWelcomeHint();
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
        // Always show remove - last participant can be removed to restore the welcome hint.
        foreach (var ui in _ollamaParticipants)
            ui.RemoveButton.Visibility = Visibility.Visible;
    }

    private void BuildOllamaCard(OllamaParticipant participant)
    {
        var displayName = participant.DisplayName;

        var avatarText = new TextBlock
        {
            Text                = participant.AvatarLabel,
            FontSize            = 11,
            FontWeight          = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center
        };
        avatarText.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");

        var avatarBorder = new Border
        {
            Width        = 34, Height = 34,
            CornerRadius = new CornerRadius(9),
            Child        = avatarText
        };
        avatarBorder.SetResourceReference(Border.BackgroundProperty, participant.ColorKey);

        // ── Role badges - outline-only tags, no fill background ──
        var coBadge = new Border
        {
            CornerRadius    = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 1, 4, 1),
            Margin          = new Thickness(0, 0, 3, 0),
            Background      = Brushes.Transparent,
            Visibility      = Visibility.Collapsed,
            Child           = new TextBlock { Text = "CO", FontSize = 8, FontWeight = FontWeights.Normal,
                                  VerticalAlignment = VerticalAlignment.Center }
        };
        coBadge.SetResourceReference(Border.BorderBrushProperty, "AccentBgBrush");
        ((TextBlock)coBadge.Child).SetResourceReference(TextBlock.ForegroundProperty, "AccentBgBrush");

        var rBadge = new Border
        {
            CornerRadius    = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 1, 4, 1),
            Margin          = new Thickness(0, 0, 3, 0),
            Background      = Brushes.Transparent,
            Visibility      = Visibility.Collapsed,
            Child           = new TextBlock { Text = "R", FontSize = 8, FontWeight = FontWeights.Normal,
                                  VerticalAlignment = VerticalAlignment.Center }
        };
        rBadge.SetResourceReference(Border.BorderBrushProperty, "ContentDimBrush");
        ((TextBlock)rBadge.Child).SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var crBadge = new Border
        {
            CornerRadius    = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 1, 4, 1),
            Margin          = new Thickness(0, 0, 3, 0),
            Background      = Brushes.Transparent,
            Visibility      = Visibility.Collapsed,
            Child           = new TextBlock { Text = "CR", FontSize = 8, FontWeight = FontWeights.Normal,
                                  VerticalAlignment = VerticalAlignment.Center }
        };
        crBadge.SetResourceReference(Border.BorderBrushProperty, "ContentDimBrush");
        ((TextBlock)crBadge.Child).SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var plBadge = new Border
        {
            CornerRadius    = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 1, 4, 1),
            Margin          = new Thickness(0, 0, 3, 0),
            Background      = Brushes.Transparent,
            Visibility      = Visibility.Collapsed,
            Child           = new TextBlock { Text = "PL", FontSize = 8, FontWeight = FontWeights.Normal,
                                  VerticalAlignment = VerticalAlignment.Center }
        };
        plBadge.SetResourceReference(Border.BorderBrushProperty, "ContentDimBrush");
        ((TextBlock)plBadge.Child).SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var rsBadge = new Border
        {
            CornerRadius    = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 1, 4, 1),
            Margin          = new Thickness(0, 0, 3, 0),
            Background      = Brushes.Transparent,
            Visibility      = Visibility.Collapsed,
            Child           = new TextBlock { Text = "RS", FontSize = 8, FontWeight = FontWeights.Normal,
                                  VerticalAlignment = VerticalAlignment.Center }
        };
        rsBadge.SetResourceReference(Border.BorderBrushProperty, "ContentDimBrush");
        ((TextBlock)rsBadge.Child).SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var wrBadge = new Border
        {
            CornerRadius    = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 1, 4, 1),
            Margin          = new Thickness(0, 0, 3, 0),
            Background      = Brushes.Transparent,
            Visibility      = Visibility.Collapsed,
            Child           = new TextBlock { Text = "WR", FontSize = 8, FontWeight = FontWeights.Normal,
                                  VerticalAlignment = VerticalAlignment.Center }
        };
        wrBadge.SetResourceReference(Border.BorderBrushProperty, "SecondaryAccentBrush");
        ((TextBlock)wrBadge.Child).SetResourceReference(TextBlock.ForegroundProperty, "SecondaryAccentBrush");

        // Avatar only holds the circle and the error indicator - role badges moved to row below
        var avatarContainer = new Grid
        {
            Width             = 38, Height = 38,
            Margin            = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        avatarContainer.Children.Add(avatarBorder);

        // Error badge - black background, yellow !, bottom-center of avatar
        var errorBadgeOllama = new Border
        {
            CornerRadius        = new CornerRadius(3),
            Padding             = new Thickness(3, 0, 3, 0),
            Height              = 13,
            Background          = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Bottom,
            Visibility          = Visibility.Collapsed,
            Child               = new TextBlock
                                  {
                                      Text                = "!",
                                      FontSize            = 9,
                                      FontWeight          = FontWeights.Bold,
                                      Foreground          = new SolidColorBrush(Color.FromRgb(255, 220, 0)),
                                      HorizontalAlignment = HorizontalAlignment.Center,
                                      VerticalAlignment   = VerticalAlignment.Center
                                  }
        };
        avatarContainer.Children.Add(errorBadgeOllama);

        var statusDot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        statusDot.SetResourceReference(Ellipse.FillProperty, "ContentDimBrush");

        var nameLabel = new TextBlock { Text = displayName, FontSize = 13, FontWeight = FontWeights.SemiBold };
        nameLabel.SetResourceReference(TextBlock.ForegroundProperty, "ControlTextBrush");

        var modelLabel = new TextBlock { Text = "checking...", FontSize = 10 };
        modelLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var offlineLabel = new TextBlock { Text = "Offline", FontSize = 10, Visibility = Visibility.Collapsed };
        offlineLabel.SetResourceReference(TextBlock.ForegroundProperty, "AccentBgBrush");

        var statusLabelOllama = new TextBlock { FontSize = 10, Visibility = Visibility.Collapsed };

        var stopButtonOllama = new Button
        {
            Content           = "⏹",
            Width             = 16, Height = 16,
            FontSize          = 8,
            BorderThickness   = new Thickness(0),
            Cursor            = Cursors.Hand,
            Padding           = new Thickness(0),
            Visibility        = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(5, 0, 0, 0),
            ToolTip           = "Stop this model",
            Style             = (Style)FindResource("ModernButton")
        };
        stopButtonOllama.SetResourceReference(Button.BackgroundProperty, "AccentBgBrush");
        stopButtonOllama.SetResourceReference(Button.ForegroundProperty, "AccentTextBrush");

        // Status row: "Thinking..." label + per-card stop button inline
        var statusRowOllama = new StackPanel { Orientation = Orientation.Horizontal };
        statusRowOllama.Children.Add(statusLabelOllama);
        statusRowOllama.Children.Add(stopButtonOllama);

        var labelPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labelPanel.Children.Add(nameLabel);
        labelPanel.Children.Add(modelLabel);
        labelPanel.Children.Add(offlineLabel);
        labelPanel.Children.Add(statusRowOllama);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(avatarContainer, 0);
        Grid.SetColumn(labelPanel,      1);
        Grid.SetColumn(statusDot,       2);

        grid.Children.Add(avatarContainer);
        grid.Children.Add(labelPanel);
        grid.Children.Add(statusDot);

        // ── Badge row - themed pills in a horizontal strip below the main row ──
        var badgeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 5, 0, 0),
            Visibility  = Visibility.Collapsed
        };
        badgeRow.Children.Add(coBadge);
        badgeRow.Children.Add(rBadge);
        badgeRow.Children.Add(crBadge);
        badgeRow.Children.Add(plBadge);
        badgeRow.Children.Add(rsBadge);
        badgeRow.Children.Add(wrBadge);

        var cardContent = new StackPanel();
        cardContent.Children.Add(grid);
        cardContent.Children.Add(badgeRow);

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding      = new Thickness(10),
            Margin       = new Thickness(0, 0, 0, 7),
            Cursor       = Cursors.Hand,
            Child        = cardContent
        };
        card.SetResourceReference(Border.BackgroundProperty,   "ControlBgBrush");
        card.BorderThickness = new Thickness(1);
        card.SetResourceReference(Border.BorderBrushProperty,  "ControlBorderBrush");

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
        separator.SetResourceReference(Rectangle.FillProperty, "ControlBgBrush");

        var enabledToggle = new CheckBox
        {
            Style     = (Style)FindResource("ToggleSwitch"),
            IsChecked = true,
            Content   = $"{displayName} enabled",
            Margin    = new Thickness(0, 0, 0, 14)
        };

        var infoServerKey = new TextBlock { Text = "SERVER", FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 3) };
        infoServerKey.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        var infoServerVal = new TextBlock { Text = participant.Service.BaseUrl, FontSize = 12, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
        infoServerVal.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");

        var infoModelKey = new TextBlock { Text = "MODEL", FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 3) };
        infoModelKey.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        var infoModelVal = new TextBlock { Text = FormatModelDisplayName(participant.Service.CurrentModel), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        infoModelVal.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");

        var ollamaSettingsLink = new Button
        {
            Content    = "⚙  Roles & Properties…",
            Margin     = new Thickness(0, 12, 0, 0),
            Padding    = new Thickness(10, 5, 10, 5),
            FontSize   = 12,
            FontFamily = new FontFamily("Segoe UI"),
            Cursor     = Cursors.Hand,
            Visibility = Visibility.Collapsed   // shown only when a project is open
        };
        ollamaSettingsLink.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        ollamaSettingsLink.SetResourceReference(Button.ForegroundProperty, "ContentTextBrush");
        ollamaSettingsLink.SetResourceReference(Button.BorderBrushProperty, "ControlBorderBrush");
        ollamaSettingsLink.Click += (_, _) =>
        {
            if (_currentProjectFolder is not null)
                ShowProjectSettingsDialog(_currentProjectFolder,
                    _currentProject?.ProjectName ?? "");
        };

        var ollamaPopupRemoveSep = new Rectangle { Height = 1, Margin = new Thickness(0, 10, 0, 10) };
        ollamaPopupRemoveSep.SetResourceReference(Rectangle.FillProperty, "ControlBgBrush");

        var removeButton = new Button
        {
            Content    = "👋  " + Properties.Loc.S("Participant_RemoveFromChat"),
            Margin     = new Thickness(0, 0, 0, 0),
            Padding    = new Thickness(10, 5, 10, 5),
            FontSize   = 12,
            FontFamily = new FontFamily("Segoe UI"),
            Cursor     = Cursors.Hand
        };
        removeButton.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        removeButton.SetResourceReference(Button.ForegroundProperty, "AccentBgBrush");
        removeButton.SetResourceReference(Button.BorderBrushProperty, "ControlBorderBrush");

        var popupContent = new StackPanel();
        popupContent.Children.Add(popupTitle);
        popupContent.Children.Add(separator);
        popupContent.Children.Add(enabledToggle);
        popupContent.Children.Add(infoServerKey);
        popupContent.Children.Add(infoServerVal);
        popupContent.Children.Add(infoModelKey);
        popupContent.Children.Add(infoModelVal);
        popupContent.Children.Add(ollamaSettingsLink);
        popupContent.Children.Add(ollamaPopupRemoveSep);
        popupContent.Children.Add(removeButton);

        var popupBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(12),
            Padding         = new Thickness(14),
            MinWidth        = 230,
            Child           = popupContent,
            Effect          = new DropShadowEffect { Color = Colors.Black, Opacity = 0.45, BlurRadius = 22, ShadowDepth = 4 }
        };
        popupBorder.SetResourceReference(Border.BackgroundProperty,  "SidebarBgBrush");
        popupBorder.SetResourceReference(Border.BorderBrushProperty, "ControlHoverBrush");

        var popup = new Popup
        {
            PlacementTarget    = card,
            Placement          = PlacementMode.Right,
            HorizontalOffset   = 10,
            VerticalOffset     = -8,
            StaysOpen          = true,
            AllowsTransparency = true,
            Child              = popupBorder
        };

        var ui = new OllamaParticipantUI
        {
            Data          = participant,
            Card          = card,
            AvatarBorder  = avatarBorder,
            AvatarText    = avatarText,
            CoBadge       = coBadge,
            RBadge        = rBadge,
            CrBadge       = crBadge,
            PlBadge       = plBadge,
            RsBadge       = rsBadge,
            WrBadge       = wrBadge,
            BadgeRow      = badgeRow,
            NameLabel     = nameLabel,
            StatusDot     = statusDot,
            ModelLabel    = modelLabel,
            OfflineLabel  = offlineLabel,
            ErrorBadge    = errorBadgeOllama,
            StatusLabel   = statusLabelOllama,
            Popup         = popup,
            PopupTitle    = popupTitle,
            EnabledToggle = enabledToggle,
            RemoveButton  = removeButton,
            StopButton    = stopButtonOllama
        };

        void OpenOllamaPopup()
        {
            _suppressEnabledToggle        = true;
            enabledToggle.IsChecked       = ui.Data.Enabled;
            _suppressEnabledToggle        = false;
            infoModelVal.Text             = FormatModelDisplayName(ui.Data.Service.CurrentModel);
            ollamaSettingsLink.Visibility = _currentProjectFolder is not null ? Visibility.Visible : Visibility.Collapsed;
            popup.IsOpen                  = !popup.IsOpen;
        }

        card.MouseLeftButtonDown += (_, _) => OpenOllamaPopup();
        card.MouseRightButtonUp  += (_, e) => { OpenOllamaPopup(); e.Handled = true; };

        enabledToggle.Checked   += (_, _) => { if (!_suppressEnabledToggle) OnOllamaEnabledChanged(ui, true);  };
        enabledToggle.Unchecked += (_, _) => { if (!_suppressEnabledToggle) OnOllamaEnabledChanged(ui, false); };

        removeButton.Click     += (_, _) => { popup.IsOpen = false; RemoveOllamaParticipant(ui); };
        stopButtonOllama.Click += (_, _) => ui.ActiveCts?.Cancel();

        ParticipantsPanel.Children.Add(popup);
        ParticipantsPanel.Children.Add(card);
        _ollamaParticipants.Add(ui);
    }

    private void OnOllamaEnabledChanged(OllamaParticipantUI ui, bool enabled)
    {
        ui.Data.Enabled = enabled;
        double op = enabled && ui.Data.IsOnline == true ? 1.0 : enabled ? 0.6 : 0.45;
        AnimateStatusChange(ui.Card, op);

        if (!enabled)
        {
            ui.StatusLabel.Text       = Properties.Loc.S("Status_Deactivated");
            ui.StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
            ui.StatusLabel.Visibility = Visibility.Visible;
        }
        else if (ui.Data.IsOnline == true)
        {
            ui.StatusLabel.Text       = !string.IsNullOrWhiteSpace(ui.Data.Mood) ? ui.Data.Mood : "Ready";
            ui.StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(100, 190, 100));
            ui.StatusLabel.Visibility = Visibility.Visible;
        }
        else
        {
            ui.StatusLabel.Visibility = Visibility.Collapsed;
        }

        SortParticipantsByEnabledState();
    }

    // ── Status ─────────────────────────────────────────────────────────────

    private void StartStatusTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += async (_, _) =>
        {
            try { await CheckAllStatusAsync(); }
            catch { /* status check must never crash the app via async void */ }
        };
        timer.Start();
    }

    private async Task CheckAllStatusAsync()
    {
        // Snapshots taken before any await so that ReInitializeParticipants (which removes
        // and recreates participant objects) can't cause "collection was modified" exceptions.
        // The try-catch protects against the race where the old UI objects are removed from
        // the visual tree between the snapshot and the status-update continuation — e.g. when
        // the user closes ParticipantsWindow while the timer's status check is in flight.
        try
        {

        var ollamaSnapshot  = _ollamaParticipants .ToList();
        var cloudAISnapshot = _cloudAIParticipants.ToList();

        if (ollamaSnapshot.Count > 0)
        {
            bool wasOnlineBefore = ollamaSnapshot[0].Data.IsOnline == true;
            var  ollamaOnline    = await ollamaSnapshot[0].Data.Service.IsAvailableAsync();

            foreach (var ui in ollamaSnapshot)
                ApplyOllamaParticipantStatus(ui, ollamaOnline);

            if (ollamaOnline && !wasOnlineBefore)
                await LoadOllamaModelsAsync();
        }

        foreach (var ui in cloudAISnapshot)
        {
            var online = await ui.Data.Service.IsAvailableAsync();
            ApplyCloudAIParticipantStatus(ui, online);
        }

        } catch { /* UI objects from a previous participant set may have been removed;
                     swallow silently — next timer tick will use the fresh snapshot */ }
    }

    private void ApplyOllamaParticipantStatus(OllamaParticipantUI ui, bool online)
    {
        bool changed = ui.Data.IsOnline != online;
        ui.Data.IsOnline = online;

        ui.StatusDot.SetResourceReference(Ellipse.FillProperty, online ? "SecondaryAccentBrush" : "AccentBgBrush");
        ui.OfflineLabel.Visibility = online ? Visibility.Collapsed : Visibility.Visible;
        ui.ModelLabel.Visibility   = online ? Visibility.Visible   : Visibility.Collapsed;

        if (online)
        {
            // Only set "Ready" if there is no active error badge - don't overwrite a live error
            if (ui.ErrorBadge.Visibility == Visibility.Collapsed)
            {
                ui.StatusLabel.Text       = !string.IsNullOrWhiteSpace(ui.Data.Mood) ? ui.Data.Mood : "Ready";
                ui.StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(100, 190, 100));
                ui.StatusLabel.Visibility = Visibility.Visible;
            }
        }
        else
        {
            // Offline: hide status and error badge (offline label takes over)
            ui.StatusLabel.Visibility = Visibility.Collapsed;
            ui.ErrorBadge.Visibility  = Visibility.Collapsed;
        }

        double targetOpacity = online && ui.Data.Enabled ? 1.0 : !ui.Data.Enabled ? 0.45 : 0.6;

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

        ui.StatusDot.SetResourceReference(Ellipse.FillProperty, online ? "SecondaryAccentBrush" : "AccentBgBrush");
        ui.OfflineLabel.Visibility = online ? Visibility.Collapsed : Visibility.Visible;
        ui.ModelLabel.Visibility   = online ? Visibility.Visible   : Visibility.Collapsed;

        if (online)
        {
            ui.ModelLabel.Text = FormatModelDisplayName(ui.Data.Service.CurrentModel);
            // Only set "Ready" if there is no active error badge - don't overwrite a live error
            if (ui.ErrorBadge.Visibility == Visibility.Collapsed)
            {
                ui.StatusLabel.Text       = !string.IsNullOrWhiteSpace(ui.Data.Mood) ? ui.Data.Mood : "Ready";
                ui.StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(100, 190, 100));
                ui.StatusLabel.Visibility = Visibility.Visible;
            }
        }
        else
        {
            // Offline: hide status and error badge (offline label takes over)
            ui.StatusLabel.Visibility = Visibility.Collapsed;
            ui.ErrorBadge.Visibility  = Visibility.Collapsed;
        }

        double targetOpacity = online && ui.Data.Enabled ? 1.0 : !ui.Data.Enabled ? 0.45 : 0.6;

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

    // ── Error state helpers ────────────────────────────────────────────────

    /// <summary>
    /// Shows or clears the error badge and status label on a participant card.
    /// Pass null to clear (shows "Ready"); pass an error text like "ERROR" or "Wants Money"
    /// to show the yellow/black error badge with the given status text.
    /// </summary>
    private static void ApplyErrorState(Border badge, TextBlock label, string? errorText)
    {
        if (string.IsNullOrEmpty(errorText))
        {
            badge.Visibility   = Visibility.Collapsed;
            label.Text         = "Ready";
            label.Foreground   = new SolidColorBrush(Color.FromRgb(100, 190, 100));
            label.Visibility   = Visibility.Visible;
        }
        else
        {
            badge.Visibility   = Visibility.Visible;
            label.Text         = errorText;
            label.Foreground   = errorText.Contains("Money")
                ? new SolidColorBrush(Colors.Orange)
                : new SolidColorBrush(Colors.OrangeRed);
            label.Visibility   = Visibility.Visible;
        }
    }

    private void SetParticipantError(OllamaParticipantUI ui, string? errorText)
    {
        ApplyErrorState(ui.ErrorBadge, ui.StatusLabel, errorText);
        // After clearing an error, restore the mood word if we have one
        if (string.IsNullOrEmpty(errorText) && !string.IsNullOrWhiteSpace(ui.Data.Mood))
        {
            ui.StatusLabel.Text = ui.Data.Mood;
        }
    }

    private void SetParticipantError(CloudAIParticipantUI ui, string? errorText)
    {
        ApplyErrorState(ui.ErrorBadge, ui.StatusLabel, errorText);
        // After clearing an error, restore the mood word if we have one
        if (string.IsNullOrEmpty(errorText) && !string.IsNullOrWhiteSpace(ui.Data.Mood))
        {
            ui.StatusLabel.Text = ui.Data.Mood;
        }
    }

    /// <summary>
    /// Called once per completed (non-hidden, non-error) response from a participant.
    /// Increments the response counter; every 5th response fires a background mood fetch
    /// and updates the participant's status label with the returned word.
    /// </summary>
    private void OnParticipantResponded(OllamaParticipantUI ui)
    {
        ui.Data.ResponseCount++;
        if (ui.Data.ResponseCount % 5 != 0) return;
        var type  = "Ollama";
        var model = ui.Data.Service.CurrentModel;
        var url   = ui.Data.Service.BaseUrl;
        SelfDescriptionService.FetchMoodAsync(type, model, url)
            .ContinueWith(t =>
            {
                var mood = t.Result;
                if (string.IsNullOrWhiteSpace(mood)) return;
                ui.Data.Mood = mood;
                Dispatcher.InvokeAsync(() =>
                {
                    if (ui.ErrorBadge.Visibility == Visibility.Collapsed)
                    {
                        ui.StatusLabel.Text       = mood;
                        ui.StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(100, 190, 100));
                        ui.StatusLabel.Visibility = Visibility.Visible;
                    }
                });
            }, TaskScheduler.Default);
    }

    private void OnParticipantResponded(CloudAIParticipantUI ui)
    {
        ui.Data.ResponseCount++;
        if (ui.Data.ResponseCount % 5 != 0) return;
        var type  = ui.Data.Service.ProviderName;
        var model = ui.Data.Service.CurrentModel;
        SelfDescriptionService.FetchMoodAsync(type, model, serverUrl: "")
            .ContinueWith(t =>
            {
                var mood = t.Result;
                if (string.IsNullOrWhiteSpace(mood)) return;
                ui.Data.Mood = mood;
                Dispatcher.InvokeAsync(() =>
                {
                    if (ui.ErrorBadge.Visibility == Visibility.Collapsed)
                    {
                        ui.StatusLabel.Text       = mood;
                        ui.StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(100, 190, 100));
                        ui.StatusLabel.Visibility = Visibility.Visible;
                    }
                });
            }, TaskScheduler.Default);
    }

    /// <summary>
    /// Injects a system-level notification into the shared history so the coordinator
    /// sees that a participant is currently unavailable.
    /// </summary>
    private void NotifyCoordinatorOfError(string participantName, string errorType)
    {
        if (!HasCoordinatorRole()) return;
        _sharedHistory.Add(new CloudAIMessage(
            "user",
            $"[SYSTEM: {participantName} encountered an error ({errorType}). " +
            $"This participant is currently unavailable - do not wait for or delegate to them.]",
            "System"));
    }

    // ── Card refresh helpers ───────────────────────────────────────────────

    private void RefreshOllamaCard(OllamaParticipantUI ui, ParticipantConfig config)
    {
        ui.Data.Service.CurrentModel = config.Model;
        ui.Data.CustomName           = string.IsNullOrWhiteSpace(config.Name) ? null : config.Name;
        var newName = ui.Data.DisplayName;
        ui.NameLabel.Text        = newName;
        ui.ModelLabel.Text       = FormatModelDisplayName(config.Model);
        ui.AvatarText.Text       = ui.Data.AvatarLabel;
        ui.PopupTitle.Text       = newName;
        ui.EnabledToggle.Content = $"{newName} enabled";
    }

    private void RefreshCloudAICard(CloudAIParticipantUI ui, ParticipantConfig config)
    {
        ui.Data.Service.CurrentModel = config.Model;
        ui.Data.CustomName           = string.IsNullOrWhiteSpace(config.Name) ? null : config.Name;
        var newName = ui.Data.DisplayName;
        ui.NameLabel.Text        = newName;
        ui.ModelLabel.Text       = FormatModelDisplayName(config.Model);
        ui.AvatarText.Text       = ui.Data.AvatarLabel;
        ui.PopupTitle.Text       = newName;
        ui.EnabledToggle.Content = $"{newName} enabled";
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
                    ui.ModelLabel.Text = FormatModelDisplayName(ui.Data.Service.CurrentModel);
                    // Also refresh avatar text
                    ui.AvatarText.Text = ui.Data.AvatarLabel;
                    ui.NameLabel .Text = ui.Data.DisplayName;
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

    // ── Add Participant dropdown ───────────────────────────────────────────

    private void AddParticipantButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsService.Load();
        var enabled  = settings.Participants
            .Where(p => p.Enabled && !string.IsNullOrEmpty(p.Model))
            .ToList();

        var menu = new ContextMenu { FontFamily = new FontFamily("Segoe UI"), FontSize = 13 };

        // ── MCP Client special entry ───────────────────────────────────────
        if (_currentProjectFolder is not null && _projectSettings is not null)
        {
            bool mcpEnabled = _projectSettings.McpChatEnabled;
            var mcpItem = new MenuItem
            {
                Header    = mcpEnabled
                    ? "🔌  MCP Client  ·  already connected"
                    : "🔌  MCP Client  ·  Allow Claude Desktop / Claude Code to chat",
                IsEnabled = !mcpEnabled
            };
            if (!mcpEnabled)
            {
                mcpItem.Click += (_, _) =>
                {
                    _projectSettings.McpChatEnabled = true;
                    ProjectService.SaveProject(_currentProjectFolder!, _projectSettings);
                    AddSystemMessage("🔌 MCP Client connected — Claude Desktop and Claude Code can now read and post to this chat via the chat_get_history and chat_post_message MCP tools.");
                };
            }
            menu.Items.Add(mcpItem);
            menu.Items.Add(new Separator());
        }
        else
        {
            var mcpGeneral = new MenuItem
            {
                Header    = "🔌  MCP Client  ·  General chat is always accessible via MCP",
                IsEnabled = false
            };
            menu.Items.Add(mcpGeneral);
            menu.Items.Add(new Separator());
        }

        foreach (var p in enabled)
        {
            // Duplicate check: if only one settings entry uses this provider+model combo,
            // match on provider+model alone — this makes the check rename-proof (live cards
            // keep the old CustomName until refreshed, so a name comparison would falsely
            // allow re-adding a renamed participant).
            // When multiple settings entries share the same provider+model (intentional
            // multi-persona setups), fall back to name comparison so each persona stays distinct.
            var effectiveName = string.IsNullOrEmpty(p.Name)
                ? FormatModelDisplayName(p.Model)
                : p.Name;

            bool alreadyAdded;
            if (p.Type == "Ollama")
            {
                bool multiPersona = enabled.Count(q => q.Type == p.Type && q.Model == p.Model && q.ServerUrl == p.ServerUrl) > 1;
                alreadyAdded = multiPersona
                    ? _ollamaParticipants.Any(ui =>
                        ui.Data.Service.CurrentModel == p.Model &&
                        ui.Data.Service.BaseUrl      == p.ServerUrl &&
                        ui.Data.DisplayName          == effectiveName)
                    : _ollamaParticipants.Any(ui =>
                        ui.Data.Service.CurrentModel == p.Model &&
                        ui.Data.Service.BaseUrl      == p.ServerUrl);
            }
            else
            {
                bool multiPersona = enabled.Count(q => q.Type == p.Type && q.Model == p.Model) > 1;
                alreadyAdded = multiPersona
                    ? _cloudAIParticipants.Any(ui =>
                        ui.Data.Service.ProviderName == p.Type &&
                        ui.Data.Service.CurrentModel == p.Model &&
                        ui.Data.DisplayName          == effectiveName)
                    : _cloudAIParticipants.Any(ui =>
                        ui.Data.Service.ProviderName == p.Type &&
                        ui.Data.Service.CurrentModel == p.Model);
            }

            bool hasKey = p.Type == "Ollama"
                || !string.IsNullOrWhiteSpace(WindowsCredentialManager.Load(p.Type));

            var displayName = string.IsNullOrWhiteSpace(p.Name)
                ? FormatModelDisplayName(p.Model)
                : p.Name;

            var typeIcon = p.Type == "Ollama" ? "🦙" : "☁️";
            var suffix   = alreadyAdded ? "  · already in chat"
                         : !hasKey      ? "  · ⚠ no API key"
                         : "";
            var item = new MenuItem
            {
                Header    = $"{typeIcon}  {displayName}  ·  {p.Model}{suffix}",
                IsEnabled = !alreadyAdded && hasKey
            };

            if (!alreadyAdded && hasKey)
            {
                var cap = p;
                item.Click += (_, _) =>
                {
                    if (cap.Type == "Ollama")
                    {
                        AddOllamaParticipant(cap.Model, cap.ServerUrl, cap.Name, cap.OllamaNumCtx, cap.OllamaNumPredict);
                        _ = CheckAllStatusAsync();
                    }
                    else
                    {
                        var countBefore = _cloudAIParticipants.Count;
                        AddCloudAIParticipant(cap.Type, cap.Model, cap.Name, cap.ServerUrl, cap.CloudMaxTokens);
                        if (_cloudAIParticipants.Count == countBefore)
                        {
                            AddSystemMessage($"⚠  Could not add {cap.Type} - no API key saved. Open ⋮ → Providers.");
                            return;
                        }
                        var ui = _cloudAIParticipants[^1];
                        _ = Task.Run(async () =>
                        {
                            var online = await ui.Data.Service.IsAvailableAsync();
                            Dispatcher.Invoke(() => ApplyCloudAIParticipantStatus(ui, online));
                        });
                    }
                };
            }
            menu.Items.Add(item);
        }

        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header    = "No participants configured - open 👤 Participant Config",
                IsEnabled = false
            });
        }

        menu.PlacementTarget = AddParticipantButton;
        menu.Placement       = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen          = true;
    }

    private void ApplyParticipantDelta(
        List<ParticipantConfig> before,
        List<ParticipantConfig> after,
        AppSettings             newSettings)
    {
        // Apply non-participant general settings unconditionally
        _userName             = string.IsNullOrWhiteSpace(newSettings.UserName) ? "You" : newSettings.UserName.Trim();
        _toneLevel            = newSettings.ToneLevel;
        _chattinessLevel      = newSettings.GlobalChattiness;
        _mockingbirdMode      = newSettings.MockingbirdMode;
        _aiDialogueEnabled    = newSettings.AiDialogueEnabled;
        _aiDialogueMaxTurns   = Math.Clamp(newSettings.AiDialogueMaxTurns, 3, 100);
        _globalResponseLength = Math.Clamp(newSettings.GlobalResponseLength, 0, 100);
        UpdateAiDialogueButton();

        bool anyRemoved = false;
        int  maxSlots   = Math.Max(before.Count, after.Count);

        for (int i = 0; i < maxSlots; i++)
        {
            var prev = i < before.Count ? before[i] : null;
            var curr = i < after.Count  ? after[i]  : null;

            bool wasEnabled = prev?.Enabled == true;
            bool nowEnabled = curr?.Enabled == true;

            if (!wasEnabled && nowEnabled && curr is not null)
            {
                // Freshly checked → add to panel
                if (curr.Type == "Ollama")
                    AddOllamaParticipant(curr.Model, curr.ServerUrl, curr.Name, curr.OllamaNumCtx, curr.OllamaNumPredict);
                else
                    AddCloudAIParticipant(curr.Type, curr.Model, curr.Name, curr.ServerUrl, curr.CloudMaxTokens);
            }
            else if (wasEnabled && !nowEnabled && prev is not null)
            {
                // Freshly unchecked → remove from panel
                anyRemoved = true;
                if (prev.Type == "Ollama")
                {
                    var match = _ollamaParticipants.FirstOrDefault(ui =>
                        string.Equals(ui.Data.Service.CurrentModel, prev.Model,
                                      StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(ui.Data.Service.BaseUrl,      prev.ServerUrl,
                                      StringComparison.OrdinalIgnoreCase));
                    if (match is not null) RemoveOllamaParticipant(match);
                }
                else
                {
                    var match = _cloudAIParticipants.FirstOrDefault(ui =>
                        string.Equals(ui.Data.Service.ProviderName, prev.Type,
                                      StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(ui.Data.Service.CurrentModel, prev.Model,
                                      StringComparison.OrdinalIgnoreCase));
                    if (match is not null) RemoveCloudAIParticipant(match);
                }
            }
            else if (wasEnabled && nowEnabled && prev is not null && curr is not null)
            {
                // Still active - refresh card if name or model changed
                if (prev.Type == "Ollama")
                {
                    var match = _ollamaParticipants.FirstOrDefault(ui =>
                        string.Equals(ui.Data.Service.CurrentModel, prev.Model,
                                      StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(ui.Data.Service.BaseUrl,      prev.ServerUrl,
                                      StringComparison.OrdinalIgnoreCase));
                    if (match is not null &&
                        (!string.Equals(prev.Model, curr.Model, StringComparison.OrdinalIgnoreCase) ||
                         !string.Equals(prev.Name,  curr.Name,  StringComparison.Ordinal)))
                        RefreshOllamaCard(match, curr);
                }
                else
                {
                    var match = _cloudAIParticipants.FirstOrDefault(ui =>
                        string.Equals(ui.Data.Service.ProviderName, prev.Type,
                                      StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(ui.Data.Service.CurrentModel, prev.Model,
                                      StringComparison.OrdinalIgnoreCase));
                    if (match is not null &&
                        (!string.Equals(prev.Model, curr.Model, StringComparison.OrdinalIgnoreCase) ||
                         !string.Equals(prev.Name,  curr.Name,  StringComparison.Ordinal)))
                        RefreshCloudAICard(match, curr);
                }
            }
            // !wasEnabled && !nowEnabled → wasn't active and still isn't - nothing to do
        }

        // Cancel any running stream only if a participant was just removed
        if (anyRemoved) _streamCts?.Cancel();

        if (_ollamaParticipants.Count == 0 && _cloudAIParticipants.Count == 0)
            AddSystemMessage("⚠  No participants enabled - configure them in 👤 Participant Config.");

        UpdateAddRemoveButtons();
        UpdateCloudAIAddRemoveButtons();
        _ = CheckAllStatusAsync();

        // If a project is open, refresh the capability profile if the team changed.
        if (_currentProjectFolder is not null)
            _ = CheckAndTriggerSuperPowersAsync();
    }

    // ── Card thinking-pulse animations ─────────────────────────────────────

    /// <summary>
    /// Starts a pulsing glow on the participant's avatar to indicate the model is actively
    /// generating a response. Pair every call with <see cref="StopCardPulse"/>.
    /// </summary>
    /// <summary>
    /// Starts the pulsing glow animation on a participant's avatar card.
    /// Also updates the status label to show "Busy..." while generating.
    /// </summary>
    private void StartCardPulse(Border avatarBorder, TextBlock? statusLabel = null, Button? stopButton = null)
    {
        // Show "Thinking..." status and per-card stop button
        if (statusLabel != null)
        {
            statusLabel.Text       = Properties.Loc.S("Status_Thinking");
            statusLabel.Visibility = Visibility.Visible;
        }
        if (stopButton != null)
            stopButton.Visibility = Visibility.Visible;

        var glow = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color       = System.Windows.Media.Colors.White,
            Direction   = 0,
            ShadowDepth = 0,
            Opacity     = 0.0,
            BlurRadius  = 10
        };
        avatarBorder.Effect = glow;

        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            From           = 0.0,
            To             = 0.9,
            Duration       = new Duration(TimeSpan.FromMilliseconds(620)),
            AutoReverse    = true,
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            EasingFunction = new System.Windows.Media.Animation.SineEase
                             { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
        };
        glow.BeginAnimation(
            System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, anim);
    }

    /// <summary>
    /// Removes the thinking-pulse glow from the avatar border.
    /// Also clears the "Busy..." status label if provided.
    /// </summary>
    private void StopCardPulse(Border avatarBorder, TextBlock? statusLabel = null, Button? stopButton = null)
    {
        avatarBorder.Effect = null;

        // Clear "Thinking..." status and hide per-card stop button
        if (statusLabel != null)
            statusLabel.Text = "";
        if (stopButton != null)
            stopButton.Visibility = Visibility.Collapsed;
    }
}
