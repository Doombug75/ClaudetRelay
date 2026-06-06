using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ClaudetRelay.Services;

namespace ClaudetRelay;

/// <summary>
/// Card-grid window for managing all participants.
/// Unlimited participant count — replaces the old P1–P20 tab approach.
/// </summary>
public class ParticipantsWindow : Window
{
    // ── State ──────────────────────────────────────────────────────────────

    private readonly string?      _themePath;
    private readonly MainWindow?  _mainWindow;
    private          string       _sortMode    = "slot";
    private          bool         _activeAtTop = false;
    private          WrapPanel?   _cards;

    // Temp storage for rate-limit controls shared between RebuildRpmSection and saveBtn.Click
    private CheckBox? _editRpmChk;
    private TextBox?  _editRpmValueBox;

    // Provider types shown in the dropdown.
    // "Ollama ☁" is the internal type name used by OllamaOpenAIService / CreateCloudAIService.
    private static readonly string[] AllProviders =
    [
        "Ollama",         // Local Ollama — native API, server URL required
        "Ollama ☁",       // Ollama Cloud (api.ollama.com) — API key required, no custom URL
        "vLLM",           // vLLM inference server — server URL required, API key optional
        "LM Studio",      // LM Studio local — server URL required, no API key needed
        "LM Studio ☁",    // LM Studio Cloud — API key required, no custom URL
        "llama.cpp",      // llama.cpp HTTP server — server URL required, no API key needed
        "LocalAI",        // LocalAI — server URL required, no API key needed
        "Jan",            // Jan desktop app server — server URL required, no API key needed
        "text-gen-webui", // oobabooga text-generation-webui — server URL required
        "GPT4All",        // GPT4All local server — server URL required, no API key needed
        "TabbyAPI",       // TabbyAPI (GPTQ/EXL2) — server URL required, API key optional
        "llamafile",      // llamafile (Mozilla) — server URL required, no API key needed
        "KoboldCpp",      // KoboldCpp — server URL required, no API key needed
        "Anthropic",
        "Google AI",
        "Groq",
        "OpenRouter",
        "Mistral",
        "xAI Grok",
        "OpenAI ChatGPT",
        "Together AI",
        "Fireworks AI",
        "DeepSeek",
        "Cerebras",
        "Perplexity AI",
        "DeepInfra",
        "Nvidia NIM",
    ];

    // ── Provider colour palette ────────────────────────────────────────────

    private static Color ProviderColor(string type) => type switch
    {
        "Anthropic"       => Color.FromRgb(212,  89,  42),
        "Google AI"       => Color.FromRgb( 66, 133, 244),
        "Groq"            => Color.FromRgb(249, 115,  22),
        "OpenRouter"      => Color.FromRgb( 99, 102, 241),
        "Mistral"         => Color.FromRgb(100, 116, 139),
        "xAI Grok"        => Color.FromRgb(147,  51, 234),
        "OpenAI ChatGPT"  => Color.FromRgb( 16, 185, 129),
        "Ollama ☁"        => Color.FromRgb( 20, 184, 166),
        "vLLM"            => Color.FromRgb( 34, 197,  94),   // green
        "LM Studio"       => Color.FromRgb(234, 179,   8),   // amber
        "LM Studio ☁"     => Color.FromRgb(202, 138,   4),   // darker amber
        "llama.cpp"       => Color.FromRgb( 20, 184, 166),   // teal
        "LocalAI"         => Color.FromRgb( 14, 165, 233),   // sky blue
        "Jan"             => Color.FromRgb(139,  92, 246),   // violet
        "text-gen-webui"  => Color.FromRgb(101, 163,  13),   // lime
        "GPT4All"         => Color.FromRgb(  5, 150, 105),   // emerald
        "TabbyAPI"        => Color.FromRgb(244,  63,  94),   // rose
        "llamafile"       => Color.FromRgb(234,  88,  12),   // orange
        "KoboldCpp"       => Color.FromRgb(192,  38, 211),   // fuchsia
        "Together AI"     => Color.FromRgb( 99, 179, 237),   // cornflower
        "Fireworks AI"    => Color.FromRgb(252, 129,  74),   // salmon-orange
        "DeepSeek"        => Color.FromRgb( 59, 130, 246),   // blue
        "Cerebras"        => Color.FromRgb( 16, 212, 128),   // mint
        "Perplexity AI"   => Color.FromRgb( 32, 178, 170),   // light-sea-green
        "DeepInfra"       => Color.FromRgb(113,  86, 217),   // slate-purple
        "Nvidia NIM"      => Color.FromRgb(118, 185,   0),   // nvidia green
        _                 => Color.FromRgb( 37,  99, 235),   // blue = Ollama / unknown
    };

    // ── Cloud vs local helpers ─────────────────────────────────────────────

    // Local (URL-based, no API key required): Ollama, vLLM, LM Studio, llama.cpp, etc.
    // Cloud (API key required): everything else including "LM Studio ☁"
    private static bool IsCloud(string provider) => provider is not (
        "Ollama" or "vLLM" or "LM Studio" or
        "llama.cpp" or "LocalAI" or "Jan" or "text-gen-webui" or
        "GPT4All" or "TabbyAPI" or "llamafile" or "KoboldCpp");

    private static string DefaultServerUrl(string provider) => provider switch
    {
        "vLLM"           => Services.VllmService.DefaultUrl,
        "LM Studio"      => Services.LmStudioService.DefaultLocalUrl,
        "llama.cpp"      => Services.LlamaCppService.DefaultUrl,
        "LocalAI"        => Services.LocalAIService.DefaultUrl,
        "Jan"            => Services.JanService.DefaultUrl,
        "text-gen-webui" => Services.TextGenWebUIService.DefaultUrl,
        "GPT4All"        => Services.GPT4AllService.DefaultUrl,
        "TabbyAPI"       => Services.TabbyAPIService.DefaultUrl,
        "llamafile"      => Services.LlamafileService.DefaultUrl,
        "KoboldCpp"      => Services.KoboldCppService.DefaultUrl,
        _                => "http://localhost:11434"
    };

    /// <summary>
    /// Returns the human-readable label for the provider dropdown.
    /// Cloud providers get a ☁ suffix so users can tell at a glance which need an API key.
    /// Providers whose internal name already ends with ☁ are left unchanged.
    /// </summary>
    private static string ProviderDisplayName(string provider)
        => (provider.EndsWith("☁") || !IsCloud(provider)) ? provider : $"{provider} ☁";

    private static string RpmHint(string provider) => provider switch
    {
        "Google AI"      => "Free tier: 2–15 rpm (Pro) · 15–30 rpm (Flash) — aistudio.google.com",
        "Groq"           => "Free tier varies by model — check console.groq.com",
        "Anthropic"      => "Rate limits depend on your usage tier — console.anthropic.com",
        "OpenRouter"     => "Free models have per-model limits — openrouter.ai",
        "Mistral"        => "Free tier is limited — console.mistral.ai",
        "xAI Grok"       => "Rate limits depend on your plan — console.x.ai",
        "OpenAI ChatGPT" => "Rate limits depend on your usage tier — platform.openai.com",
        _                => ""
    };

    // ── Default model lists per provider (populated immediately in the edit dialog) ──

    private static string[] DefaultModels(string provider) => provider switch
    {
        "Anthropic"       => AnthropicService.DefaultModels,
        "Google AI"       => GoogleAIService.DefaultModels,
        "Groq"            => GroqService.DefaultModels,
        "OpenRouter"      => OpenRouterService.DefaultModels,
        "Mistral"         => MistralService.DefaultModels,
        "xAI Grok"        => XAIGrokService.DefaultModels,
        "OpenAI ChatGPT"  => OpenAIService.DefaultModels,
        _                 => []    // Ollama / vLLM / LM Studio variants: fetched live
    };

    // ── Constructor ────────────────────────────────────────────────────────

    public ParticipantsWindow(string? themePath, MainWindow owner)
    {
        _themePath  = themePath;
        _mainWindow = owner;
        Owner       = owner;

        if (themePath is not null)
        {
            try
            {
                var dict = OxsuitLoader.Load(themePath);
                if (dict is not null) Resources.MergedDictionaries.Add(dict);
            }
            catch { }
        }

        var zoom = UiZoomHelper.FromSettings();

        Title                 = Properties.Loc.S("Participants_Title");
        Width                 = 860 * zoom;
        Height                = 680 * zoom;
        MinWidth              = 600;
        MinHeight             = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar         = false;
        ResizeMode            = ResizeMode.CanResize;
        SetResourceReference(BackgroundProperty, "ContentBgBrush");
        SourceInitialized += (_, _) => TryApplyTitleBar();

        // Apply zoom to content so all text, controls and spacing scale uniformly.
        // Window size is already pre-scaled above so we pass scaleWindow=false here.
        Loaded += (_, _) => UiZoomHelper.Apply(this, zoom, scaleWindow: false);

        BuildUI();
    }

    // ── UI construction ────────────────────────────────────────────────────

    private void BuildUI()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Content = root;

        // ── Toolbar ────────────────────────────────────────────────────────
        var toolBorder = new Border { Padding = new Thickness(16, 10, 16, 10) };
        toolBorder.SetResourceReference(Border.BackgroundProperty, "SidebarBgBrush");
        Grid.SetRow(toolBorder, 0);
        root.Children.Add(toolBorder);

        var toolbar = new Grid();
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolBorder.Child = toolbar;

        var addBtn = MakeBtn(Properties.Loc.S("Btn_AddParticipant"), isPrimary: true);
        addBtn.Click += (_, _) =>
        {
            var p = new ParticipantConfig { DateAdded = DateTime.UtcNow };
            if (ShowEditDialog(p, isNew: true))
            {
                var s = SettingsService.Load();
                s.Participants.Add(p);
                SettingsService.Save(s);
                RebuildCards();
            }
        };
        Grid.SetColumn(addBtn, 0);
        toolbar.Children.Add(addBtn);

        // Sort controls
        var sortPanel = new StackPanel
            { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(sortPanel, 2);
        toolbar.Children.Add(sortPanel);

        var sortLbl = new TextBlock
        {
            Text = Properties.Loc.S("Projects_Sort"), FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
        };
        sortLbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        sortPanel.Children.Add(sortLbl);

        void AddSort(string label, string mode)
        {
            var b = MakeSortBtn(label);
            b.Click += (_, _) =>
            {
                _sortMode = mode == "slot" && _sortMode == "slot" ? "slot_desc"
                          : mode == "slot" && _sortMode == "slot_desc" ? "slot"
                          : mode;
                RebuildCards();
            };
            sortPanel.Children.Add(b);
        }
        AddSort(Properties.Loc.S("Participants_SortSlot"),     "slot");
        AddSort(Properties.Loc.S("Participants_SortName"),     "name");
        AddSort(Properties.Loc.S("Participants_SortProvider"), "provider");
        AddSort(Properties.Loc.S("Participants_SortDate"),     "date");

        // "Active at top" toggle
        var activeTopChk = new CheckBox
        {
            Content   = Properties.Loc.S("Participants_ActiveFirst"),
            IsChecked = _activeAtTop,
            FontSize  = 11, FontFamily = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        activeTopChk.SetResourceReference(CheckBox.ForegroundProperty, "SidebarDimBrush");
        activeTopChk.Checked   += (_, _) => { _activeAtTop = true;  RebuildCards(); };
        activeTopChk.Unchecked += (_, _) => { _activeAtTop = false; RebuildCards(); };
        sortPanel.Children.Add(activeTopChk);

        // (General Settings moved to the main window's three-dots menu)

        // Separator
        var sep = new Rectangle { Height = 1 };
        sep.SetResourceReference(Rectangle.FillProperty, "ControlBorderBrush");
        Grid.SetRow(sep, 0);
        root.Children.Add(sep);

        // ── Cards scroll area ──────────────────────────────────────────────
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(16, 14, 16, 16)
        };
        scroll.SetResourceReference(ScrollViewer.BackgroundProperty, "ContentBgBrush");
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        _cards = new WrapPanel { Orientation = Orientation.Horizontal, ItemWidth = 240 };
        scroll.Content = _cards;

        RebuildCards();
    }

    // ── Card grid ──────────────────────────────────────────────────────────

    private void RebuildCards()
    {
        if (_cards is null) return;
        _cards.Children.Clear();

        var settings     = SettingsService.Load();
        var participants = settings.Participants;

        // Build display list: preserve original index for all mutations.
        var indexed = participants.Select((p, i) => (p, slot: i + 1, origIdx: i)).ToList();

        IEnumerable<(ParticipantConfig p, int slot, int origIdx)> ApplySort(
            IEnumerable<(ParticipantConfig p, int slot, int origIdx)> src) => _sortMode switch
        {
            "name"      => src.OrderBy(x => !string.IsNullOrEmpty(x.p.Name) ? x.p.Name : x.p.Type,
                               StringComparer.OrdinalIgnoreCase),
            "provider"  => src.OrderBy(x => x.p.Type, StringComparer.OrdinalIgnoreCase)
                              .ThenBy(x => x.p.Name, StringComparer.OrdinalIgnoreCase),
            "date"      => src.OrderBy(x => x.p.DateAdded),
            "slot_desc" => src.OrderByDescending(x => x.slot),
            _           => src
        };

        List<(ParticipantConfig p, int slot, int origIdx)> sorted;
        if (_activeAtTop)
        {
            // Sort each group independently, then concatenate active → inactive
            var active   = ApplySort(indexed.Where(x =>  x.p.Enabled)).ToList();
            var inactive = ApplySort(indexed.Where(x => !x.p.Enabled)).ToList();
            sorted = [..active, ..inactive];
        }
        else
        {
            sorted = ApplySort(indexed).ToList();
        }

        foreach (var (p, slot, origIdx) in sorted)
        {
            _cards.Children.Add(BuildCard(p, slot, origIdx));

            // Ask the model to describe itself if we have no description and no prior error.
            // (Error = user needs to fix their API key first; don't hammer the API on every rebuild.)
            // For cloud providers, only fetch if an API key is configured (avoid 5-minute timeout hang).
            // On completion, dispatch RebuildCards() back to the UI thread so the card updates
            // immediately without requiring the user to close and reopen the window.
            bool hasApiKey = !IsCloud(p.Type) || WindowsCredentialManager.Load(p.Type) is not null;
            if ((string.IsNullOrEmpty(p.SelfDescription) || string.IsNullOrEmpty(p.Likes)) &&
                string.IsNullOrEmpty(p.LastApiError) &&
                !string.IsNullOrEmpty(p.Model) &&
                hasApiKey)
            {
                SelfDescriptionService
                    .FetchAndSaveAsync(p.Type, p.Model, p.ServerUrl)
                    .ContinueWith(_ => Dispatcher.InvokeAsync(() =>
                    {
                        if (IsLoaded) RebuildCards();
                    }), TaskScheduler.Default);
            }
        }

        if (participants.Count == 0)
        {
            var hint = new TextBlock
            {
                Text = Properties.Loc.S("Participants_NoParticipantsHint"),
                FontSize = 14, FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 20, 0, 0)
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            _cards.Children.Add(hint);
        }
    }

    private Border BuildCard(ParticipantConfig p, int slot, int origIdx)
    {
        var card = new Border
        {
            Width = 228, Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(14, 12, 14, 12),
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1)
        };
        card.SetResourceReference(Border.BackgroundProperty,  "ControlBgBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");

        // ── Inactive dimming ───────────────────────────────────────────────
        card.Opacity = p.Enabled ? 1.0 : 0.45;

        var inner = new StackPanel();
        card.Child = inner;

        // ── Header: #slot | role tag | Active checkbox ─────────────────────
        var headerRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.Children.Add(headerRow);

        var slotBadge = new Border
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 2, 5, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        slotBadge.SetResourceReference(Border.BackgroundProperty, "ControlHoverBrush");
        var slotText = new TextBlock
        {
            Text = $"#{slot}", FontSize = 10, FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI")
        };
        slotText.SetResourceReference(TextBlock.ForegroundProperty, "ControlDimBrush");
        slotBadge.Child = slotText;
        Grid.SetColumn(slotBadge, 0);
        headerRow.Children.Add(slotBadge);

        // Role tag — shown only when explicitly set (never auto-inferred from name)
        var roleText = p.Role;
        if (!string.IsNullOrWhiteSpace(roleText))
        {
            var roleTb = new TextBlock
            {
                Text = roleText, FontSize = 10, FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0)
            };
            roleTb.SetResourceReference(TextBlock.ForegroundProperty, "AccentHighlightBrush");
            Grid.SetColumn(roleTb, 1);
            headerRow.Children.Add(roleTb);
        }

        // Active checkbox
        var toggle = new CheckBox
        {
            Content = Properties.Loc.S("Participants_Active"), IsChecked = p.Enabled,
            FontSize = 10, FontFamily = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Cursor = Cursors.Hand
        };
        toggle.SetResourceReference(CheckBox.ForegroundProperty,
            p.Enabled ? "AccentHighlightBrush" : "SidebarDimBrush");
        toggle.Checked   += (_, _) =>
        {
            if (IsCloud(p.Type) && string.IsNullOrWhiteSpace(WindowsCredentialManager.Load(p.Type)))
            {
                toggle.IsChecked = false;
                MessageBox.Show(
                    $"No API key is stored for {p.Type}.\n\nOpen General Settings and add your key before activating this participant.",
                    "API Key Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            card.Opacity = 1.0;
            toggle.SetResourceReference(CheckBox.ForegroundProperty, "AccentHighlightBrush");
            var s = SettingsService.Load();
            if (origIdx < s.Participants.Count) s.Participants[origIdx].Enabled = true;
            SettingsService.Save(s);
        };
        toggle.Unchecked += (_, _) =>
        {
            card.Opacity = 0.45;
            toggle.SetResourceReference(CheckBox.ForegroundProperty, "SidebarDimBrush");
            var s = SettingsService.Load();
            if (origIdx < s.Participants.Count) s.Participants[origIdx].Enabled = false;
            SettingsService.Save(s);
        };
        Grid.SetColumn(toggle, 2);
        headerRow.Children.Add(toggle);

        // ── Name ────────────────────────────────────────────────────────────
        var displayName = !string.IsNullOrWhiteSpace(p.Name) ? p.Name
                        : !string.IsNullOrWhiteSpace(p.Model) ? p.Model
                        : $"{p.Type} ({slot})";
        var nameTb = new TextBlock
        {
            Text = displayName, FontSize = 13, FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 4)
        };
        nameTb.SetResourceReference(TextBlock.ForegroundProperty, "ControlTextBrush");
        inner.Children.Add(nameTb);

        // ── Provider row: colour dot + name ─────────────────────────────────
        var provRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        var dot = new Ellipse
        {
            Width = 8, Height = 8, Fill = new SolidColorBrush(ProviderColor(p.Type)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
        };
        provRow.Children.Add(dot);
        var provTb = new TextBlock { Text = p.Type, FontSize = 11, FontFamily = new FontFamily("Segoe UI") };
        provTb.SetResourceReference(TextBlock.ForegroundProperty, "ControlDimBrush");
        provRow.Children.Add(provTb);
        inner.Children.Add(provRow);

        // ── Model ────────────────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(p.Model))
        {
            var modelTb = new TextBlock
            {
                Text = p.Model, FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 2)
            };
            modelTb.SetResourceReference(TextBlock.ForegroundProperty, "ControlDimBrush");
            inner.Children.Add(modelTb);
        }

        // Server URL — show for URL-based providers when non-default
        if (!IsCloud(p.Type) && p.Type != "Ollama ☁"
            && !string.IsNullOrWhiteSpace(p.ServerUrl)
            && p.ServerUrl != DefaultServerUrl(p.Type))
        {
            var urlTb = new TextBlock
            {
                Text = p.ServerUrl, FontSize = 10, FontFamily = new FontFamily("Segoe UI"),
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 2)
            };
            urlTb.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            inner.Children.Add(urlTb);
        }

        // ── Status + rate-limit row (same line) ───────────────────────────
        var statusText = GetCardStatus(p);
        var hasRpm     = IsCloud(p.Type) && p.RpmEnabled && p.Rpm >= 1;

        if (statusText is not null || hasRpm)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            inner.Children.Add(row);

            if (statusText is not null)
            {
                var (label, color) = statusText switch
                {
                    "Ready"      => (Properties.Loc.S("Participants_StatusReady"),   Color.FromRgb( 80, 190,  80)),
                    "Offline"    => (Properties.Loc.S("Participants_StatusOffline"), Color.FromRgb(180, 100,  60)),
                    "No API key" => (Properties.Loc.S("Participants_StatusNoKey"),   Color.FromRgb(200, 140,  40)),
                    var e when e.StartsWith("ERROR:") => (e, Color.FromRgb(210,  80,  60)),
                    _            => (statusText,      Color.FromRgb(150, 150, 150))
                };
                var sTb = new TextBlock
                {
                    Text = label, FontSize = 10, FontFamily = new FontFamily("Segoe UI"),
                    Foreground = new SolidColorBrush(color),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(sTb, 0);
                row.Children.Add(sTb);
            }

            if (hasRpm)
            {
                var rpmTb = new TextBlock
                {
                    Text = $"⏱ {p.Rpm} rpm", FontSize = 10, FontFamily = new FontFamily("Segoe UI"),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                rpmTb.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
                Grid.SetColumn(rpmTb, 1);
                row.Children.Add(rpmTb);
            }
        }

        // ── Left-click = model info, right-click = edit / delete ─────────
        card.Cursor = Cursors.Hand;
        card.MouseLeftButtonDown += (_, _) => ShowModelInfo(p);

        var ctxMenu = new ContextMenu();
        var editItem = new MenuItem { Header = Properties.Loc.S("Participants_EditItem") };
        editItem.Click += (_, _) =>
        {
            var s = SettingsService.Load();
            if (origIdx >= s.Participants.Count) return;
            var live = s.Participants[origIdx];
            if (ShowEditDialog(live, isNew: false)) { SettingsService.Save(s); RebuildCards(); }
        };
        var delItem = new MenuItem { Header = Properties.Loc.S("Participants_RemoveItem") };
        delItem.Click += (_, _) =>
        {
            if (MessageBox.Show($"Remove participant '{displayName}'?",
                    "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes) return;
            var s = SettingsService.Load();
            if (origIdx < s.Participants.Count) s.Participants.RemoveAt(origIdx);
            SettingsService.Save(s);
            RebuildCards();
        };
        ctxMenu.Items.Add(editItem);
        ctxMenu.Items.Add(new Separator());
        ctxMenu.Items.Add(delItem);
        card.ContextMenu = ctxMenu;

        return card;
    }

    // ── Edit dialog ────────────────────────────────────────────────────────

    private bool ShowEditDialog(ParticipantConfig p, bool isNew)
    {
        var win = new Window
        {
            Title = isNew ? Properties.Loc.S("Participants_AddTitle") : Properties.Loc.S("Participants_EditTitle"),
            Width = 460, SizeToContent = SizeToContent.Height, MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize
        };
        // Inherit the exact same resource dictionaries that are loaded in this window
        // (works for both .oxsuit and .xaml themes without re-reading the file)
        foreach (var rd in Resources.MergedDictionaries)
            win.Resources.MergedDictionaries.Add(rd);
        win.SetResourceReference(BackgroundProperty, "ContentBgBrush");
        win.SourceInitialized += (_, _) => TryApplyTitleBarTo(win);

        var scroll = new ScrollViewer
            { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(24, 20, 24, 20) };
        win.Content = scroll;
        UiZoomHelper.Apply(win, UiZoomHelper.FromSettings());

        var root = new StackPanel();
        scroll.Content = root;

        // ── Helpers ────────────────────────────────────────────────────────

        TextBlock Lbl(string t)
        {
            var lbl = new TextBlock
            {
                Text = t, FontSize = 11, FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 12, 0, 4)
            };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            return lbl;
        }

        TextBox Tb(string val)
        {
            var tb = new TextBox { Text = val };
            if (win.TryFindResource("ModernTextBox") is Style s) tb.Style = s;
            return tb;
        }

        // ── Name ────────────────────────────────────────────────────────────
        root.Children.Add(Lbl(Properties.Loc.S("Participants_NameField")));
        var nameBox = Tb(p.Name);
        nameBox.ToolTip = "Shown in chat bubbles. Blank = provider + slot number.";
        root.Children.Add(nameBox);

        // ── Provider ────────────────────────────────────────────────────────
        root.Children.Add(Lbl(Properties.Loc.S("Participants_Provider")));
        var provCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 0) };
        if (win.TryFindResource("ModernComboBox") is Style cs) provCombo.Style = cs;
        foreach (var prov in AllProviders)
            provCombo.Items.Add(new ComboBoxItem { Content = ProviderDisplayName(prov), Tag = prov });
        provCombo.SelectedItem = provCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag?.ToString(), p.Type, StringComparison.OrdinalIgnoreCase))
            ?? provCombo.Items[0];
        root.Children.Add(provCombo);

        // Reads the internal provider key (without display ☁ suffix) from the selected ComboBoxItem.
        string ProvKey() => (provCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

        // ── Model (ComboBox + fetch button) ─────────────────────────────────
        root.Children.Add(Lbl(Properties.Loc.S("Participants_Model")));
        var modelRow = new Grid();
        modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.Children.Add(modelRow);

        // Editable ComboBox so user can type anything OR pick from fetched list
        var modelCombo = new ComboBox
        {
            IsEditable = true, Text = p.Model,
            ToolTip = "Type a model name or click ↻ to fetch available models from the provider"
        };
        if (win.TryFindResource("ModernComboBox") is Style mcs) modelCombo.Style = mcs;
        Grid.SetColumn(modelCombo, 0);
        modelRow.Children.Add(modelCombo);

        var fetchBtn = MakeBtn("↻", isPrimary: false);
        fetchBtn.FontSize = 13; fetchBtn.Padding = new Thickness(10, 6, 10, 6);
        fetchBtn.Margin   = new Thickness(6, 0, 0, 0);
        fetchBtn.ToolTip  = "Fetch available models from the provider";
        Grid.SetColumn(fetchBtn, 1);
        modelRow.Children.Add(fetchBtn);

        // Fetch status label
        var fetchStatus = new TextBlock
        {
            FontSize = 10, FontFamily = new FontFamily("Segoe UI"),
            Margin = new Thickness(0, 3, 0, 0)
        };
        fetchStatus.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        root.Children.Add(fetchStatus);

        // Populate default models for cloud providers immediately
        void PopulateDefaults(string provider)
        {
            var defaults = DefaultModels(provider);
            modelCombo.Items.Clear();
            foreach (var m in defaults) modelCombo.Items.Add(m);
            if (defaults.Length > 0 && string.IsNullOrEmpty(modelCombo.Text))
                modelCombo.Text = defaults[0];
        }
        PopulateDefaults(ProvKey());

        // Re-populate when provider changes; auto-fetch for providers with no static defaults
        provCombo.SelectionChanged += (_, _) =>
        {
            fetchStatus.Text = "";
            var prov = ProvKey();
            PopulateDefaults(prov);
            // Providers with no static model list — kick off a live fetch automatically
            if (!IsCloud(prov))
                fetchBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };

        // ── Server URL (URL-based providers: all local inference servers) ──────
        var urlLbl = Lbl(Properties.Loc.S("Participants_ServerUrl"));
        bool NeedsUrl() => !IsCloud(ProvKey())
                        && (provCombo.SelectedItem as string) is not "Ollama ☁";

        urlLbl.Visibility = NeedsUrl() ? Visibility.Visible : Visibility.Collapsed;
        root.Children.Add(urlLbl);

        var initialUrl = !string.IsNullOrWhiteSpace(p.ServerUrl)
            ? p.ServerUrl
            : DefaultServerUrl(ProvKey());
        var urlBox = Tb(initialUrl);
        urlBox.ToolTip    = $"Base URL of the server  (e.g. {DefaultServerUrl(ProvKey())})";
        urlBox.Visibility = NeedsUrl() ? Visibility.Visible : Visibility.Collapsed;
        root.Children.Add(urlBox);

        provCombo.SelectionChanged += (_, _) =>
        {
            var prov2 = ProvKey();
            var vis   = NeedsUrl() ? Visibility.Visible : Visibility.Collapsed;
            urlLbl.Visibility = vis; urlBox.Visibility = vis;

            if (NeedsUrl())
            {
                // Swap to the provider's default URL if it's still showing another provider's default
                var knownDefaults = new[] { "http://localhost:11434",
                    Services.VllmService.DefaultUrl, Services.LmStudioService.DefaultLocalUrl };
                if (knownDefaults.Any(d => string.Equals(urlBox.Text.Trim(), d, StringComparison.OrdinalIgnoreCase)))
                    urlBox.Text = DefaultServerUrl(prov2);
                urlBox.ToolTip = $"Base URL of the {prov2} server  (e.g. {DefaultServerUrl(prov2)})";
            }
        };

        // CancellationTokenSource tied to the dialog lifetime.
        // Cancels any in-flight fetch when the dialog closes so the async void
        // handler never touches UI elements that belong to a dead window.
        var dialogCts = new CancellationTokenSource();
        win.Closed += (_, _) => dialogCts.Cancel();

        // Fetch button: live API call (wired here so urlBox is in scope)
        fetchBtn.Click += async (_, _) =>
        {
            var prov      = ProvKey();
            var serverUrl = urlBox.Text.Trim();
            if (string.IsNullOrEmpty(serverUrl)) serverUrl = "http://localhost:11434";

            // Guard: cloud providers without API keys should not attempt fetch
            if (IsCloud(prov) && prov != "LM Studio" && WindowsCredentialManager.Load(prov) is null)
            {
                fetchStatus.Text = "⚠  No API key configured. Open ⋮ → Providers to set it up.";
                return;
            }

            fetchBtn.IsEnabled = false;
            fetchStatus.Text   = "Fetching models…";

            try
            {
                var ct = dialogCts.Token;
                List<string> models;
                if (prov == "Ollama")
                {
                    models = await new OllamaService(serverUrl).GetModelsAsync(ct);
                }
                else if (NeedsUrl())
                {
                    // All other URL-based OpenAI-compatible local servers
                    // (vLLM, LM Studio, llama.cpp, LocalAI, Jan, text-gen-webui, etc.)
                    models = await new Services.VllmService(serverUrl).GetModelsAsync(ct);
                }
                else
                {
                    var apiKey = WindowsCredentialManager.Load(prov) ?? "";
                    models = prov switch
                    {
                        "Ollama ☁"       => await new OllamaOpenAIService(apiKey).GetModelsAsync(ct),
                        "Anthropic"      => await new AnthropicService(apiKey).GetModelsAsync(ct),
                        "Google AI"      => await new GoogleAIService(apiKey).GetModelsAsync(ct),
                        "OpenRouter"     => await new OpenRouterService(apiKey).GetModelsAsync(ct),
                        "LM Studio ☁"    => await new Services.LmStudioService(Services.LmStudioService.DefaultCloudUrl, apiKey).GetModelsAsync(ct),
                        _                => [.. DefaultModels(prov)]
                    };
                }

                ct.ThrowIfCancellationRequested();   // dialog may have closed during the await

                var current = modelCombo.Text;
                modelCombo.Items.Clear();
                foreach (var m in models) modelCombo.Items.Add(m);
                modelCombo.Text  = current.Length > 0 ? current : models.Count > 0 ? models[0] : "";
                fetchStatus.Text = $"✓  {models.Count} model{(models.Count == 1 ? "" : "s")} found";
            }
            catch (OperationCanceledException) { /* dialog closed — discard results silently */ }
            catch (Exception ex)               { fetchStatus.Text = $"⚠  {ex.Message}"; }
            finally                            { fetchBtn.IsEnabled = true; }
        };

        // Auto-fetch on dialog open for providers that have no static model list
        // Only auto-fetch if: (a) local provider (Ollama/vLLM/LM Studio) OR (b) cloud provider with API key
        var initialProv = ProvKey();
        // Auto-fetch on open: all local servers (no API key needed) + cloud providers that have a key
        bool shouldAutoFetch = !IsCloud(initialProv)
            || (IsCloud(initialProv) && WindowsCredentialManager.Load(initialProv) is not null);
        if (shouldAutoFetch)
            fetchBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        // ── Rate limiting (cloud providers only) ─────────────────────────────
        // Stored per-provider in AppSettings.ProviderThrottle, not per-participant.
        // Read the current throttle for whichever provider is selected.
        var rpmSection = new StackPanel();
        root.Children.Add(rpmSection);

        void RebuildRpmSection()
        {
            rpmSection.Children.Clear();
            var prov = ProvKey();
            if (!IsCloud(prov)) return;

            rpmSection.Children.Add(Lbl(Properties.Loc.S("Participants_RateLimiting")));

            var rpmRow = new StackPanel
                { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            rpmSection.Children.Add(rpmRow);

            var rpmChk = new CheckBox
            {
                Content   = Properties.Loc.S("Participants_LimitRpm"),
                IsChecked = p.RpmEnabled,
                FontSize  = 12, FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center
            };
            rpmChk.SetResourceReference(CheckBox.ForegroundProperty, "ControlTextBrush");
            rpmRow.Children.Add(rpmChk);

            var rpmValueBox = new TextBox
            {
                Text  = p.Rpm.ToString(),
                Width = 50, Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            if (win.TryFindResource("ModernTextBox") is Style rs) rpmValueBox.Style = rs;
            rpmValueBox.PreviewTextInput += (_, e) =>
                e.Handled = !e.Text.All(char.IsAsciiDigit);
            rpmRow.Children.Add(rpmValueBox);

            var rpmSuffix = new TextBlock
            {
                Text = Properties.Loc.S("Participants_RpmSuffix"),
                FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };
            rpmSuffix.SetResourceReference(TextBlock.ForegroundProperty, "ControlDimBrush");
            rpmRow.Children.Add(rpmSuffix);

            var hint = RpmHint(prov);
            if (!string.IsNullOrEmpty(hint))
            {
                var hintTb = new TextBlock
                {
                    Text = hint, FontSize = 10, FontFamily = new FontFamily("Segoe UI"),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0)
                };
                hintTb.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
                rpmSection.Children.Add(hintTb);
            }

            // Store refs so saveBtn.Click can read them
            _editRpmChk      = rpmChk;
            _editRpmValueBox = rpmValueBox;
        }

        RebuildRpmSection();
        provCombo.SelectionChanged += (_, _) => RebuildRpmSection();

        // ── Voice ──────────────────────────────────────────────────────────
        root.Children.Add(Lbl("🔊  TTS Voice"));

        var voiceNames = VoiceOutputService.GetVoiceNames();
        var voiceCombo = new ComboBox { IsEditable = false, Margin = new Thickness(0, 0, 0, 6) };
        if (win.TryFindResource("ModernComboBox") is Style vcs) voiceCombo.Style = vcs;
        voiceCombo.Items.Add("(none — silent)");
        foreach (var vn in voiceNames) voiceCombo.Items.Add(vn);

        var preselect = voiceNames.FirstOrDefault(v =>
            string.Equals(v, p.VoiceName, StringComparison.OrdinalIgnoreCase));
        voiceCombo.SelectedItem  = preselect ?? "(none — silent)";
        root.Children.Add(voiceCombo);

        var voiceRow = new StackPanel
            { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        root.Children.Add(voiceRow);

        var askVoiceBtn  = MakeBtn("🎤  Ask the Model", isPrimary: false);
        askVoiceBtn.FontSize  = 11;
        askVoiceBtn.Padding   = new Thickness(12, 5, 12, 5);
        askVoiceBtn.ToolTip   = "Let the model pick the voice that fits its personality best";
        askVoiceBtn.IsEnabled = voiceNames.Count > 0;
        voiceRow.Children.Add(askVoiceBtn);

        var voiceStatus = new TextBlock
        {
            FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0)
        };
        voiceStatus.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        voiceRow.Children.Add(voiceStatus);

        var spinFrames = new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
        int spinIdx    = 0;
        var voiceSpinTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(110) };
        void UpdateSpinner()
        {
            spinIdx = (spinIdx + 1) % spinFrames.Length;
            voiceStatus.Text = $"{spinFrames[spinIdx]}  Asking…";
        }
        voiceSpinTimer.Tick += (_, _) => UpdateSpinner();
        win.Closed += (_, _) => voiceSpinTimer.Stop();

        askVoiceBtn.Click += async (_, _) =>
        {
            if (voiceNames.Count == 0) return;
            askVoiceBtn.IsEnabled = false;
            voiceStatus.Text      = $"{spinFrames[0]}  Asking…";
            voiceSpinTimer.Start();

            var prov = ProvKey();
            var mdl  = string.IsNullOrWhiteSpace(modelCombo.Text) ? p.Model : modelCombo.Text.Trim();
            var url  = NeedsUrl() ? urlBox.Text.Trim() : "http://localhost:11434";
            if (string.IsNullOrWhiteSpace(url)) url = "http://localhost:11434";

            var chosen = await SelfDescriptionService.FetchPreferredVoiceAsync(
                prov, mdl, url, voiceNames, dialogCts.Token);

            voiceSpinTimer.Stop();
            askVoiceBtn.IsEnabled = true;

            if (chosen is not null)
            {
                voiceCombo.SelectedItem = chosen;
                voiceStatus.Text        = $"✓  Chose: {chosen}";
            }
            else
                voiceStatus.Text = "⚠  No match — pick manually";
        };

        if (voiceNames.Count == 0)
        {
            var noVoiceTb = new TextBlock
            {
                Text = "No TTS voices found. Install Windows voices via System Settings → " +
                       "Time & Language → Speech → Add voices.",
                FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
            };
            noVoiceTb.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            root.Children.Add(noVoiceTb);
        }

        // ── Active ─────────────────────────────────────────────────────────
        root.Children.Add(Lbl(Properties.Loc.S("Participants_StatusSection")));
        var enabledChk = new CheckBox
        {
            Content    = Properties.Loc.S("Participants_ActiveParticipates"),
            IsChecked  = p.Enabled,
            FontSize   = 12, FontFamily = new FontFamily("Segoe UI"),
            Margin     = new Thickness(0, 4, 0, 0)
        };
        enabledChk.SetResourceReference(CheckBox.ForegroundProperty, "ControlTextBrush");
        root.Children.Add(enabledChk);

        // ── Buttons ────────────────────────────────────────────────────────
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };
        root.Children.Add(btnRow);

        var cancelBtn = MakeBtn(Properties.Loc.S("Btn_Cancel"), isPrimary: false);
        cancelBtn.Padding = new Thickness(16, 8, 16, 8);
        cancelBtn.Click  += (_, _) => win.DialogResult = false;
        btnRow.Children.Add(cancelBtn);

        var saveBtn = MakeBtn(isNew ? Properties.Loc.S("Btn_Add") : Properties.Loc.S("Btn_Save"), isPrimary: true);
        saveBtn.Padding = new Thickness(16, 8, 16, 8);
        saveBtn.Margin  = new Thickness(8, 0, 0, 0);
        saveBtn.Click  += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(modelCombo.Text))
            {
                MessageBox.Show(Properties.Loc.S("Participants_PleaseSelectModel"),
                    Properties.Loc.S("Participants_MissingModel"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var savingType = ProvKey();
            if (enabledChk.IsChecked == true && IsCloud(savingType)
                && string.IsNullOrWhiteSpace(WindowsCredentialManager.Load(savingType)))
            {
                MessageBox.Show(
                    $"No API key is stored for {savingType}.\n\nOpen General Settings and add your key, or save this participant as inactive.",
                    Properties.Loc.S("Participants_ApiKeyRequired"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            p.Name      = nameBox.Text.Trim();
            p.Type      = ProvKey();
            p.Model     = modelCombo.Text.Trim();
            p.ServerUrl = NeedsUrl()
                ? (urlBox?.Text.Trim() is { Length: > 0 } u ? u : DefaultServerUrl(p.Type))
                : "";
            p.Enabled      = enabledChk.IsChecked == true;
            p.LastApiError = "";   // allow a fresh fetch attempt after save

            // Voice
            var pickedVoice = voiceCombo.SelectedItem as string ?? "";
            p.VoiceName = pickedVoice.StartsWith("(none", StringComparison.OrdinalIgnoreCase)
                          ? "" : pickedVoice;

            // Write rate-limit values directly into the participant (per-model budget)
            if (IsCloud(p.Type) && _editRpmChk is not null && _editRpmValueBox is not null)
            {
                if (!int.TryParse(_editRpmValueBox.Text.Trim(), out var rpm) || rpm < 1) rpm = 15;
                p.RpmEnabled = _editRpmChk.IsChecked == true;
                p.Rpm        = rpm;
            }
            else
            {
                p.RpmEnabled = false;   // Ollama — no throttle
            }

            win.DialogResult = true;
        };
        btnRow.Children.Add(saveBtn);

        return win.ShowDialog() == true;
    }

    // ── Button helpers ─────────────────────────────────────────────────────

    private Button MakeBtn(string label, bool isPrimary)
    {
        var btn = new Button
        {
            Content = label, FontSize = 12, FontFamily = new FontFamily("Segoe UI"),
            Padding = new Thickness(12, 6, 12, 6), Cursor = Cursors.Hand
        };
        if (TryFindResource("ModernButton") is Style s) btn.Style = s;
        if (isPrimary)
        {
            btn.SetResourceReference(Button.BackgroundProperty, "AccentBgBrush");
            btn.SetResourceReference(Button.ForegroundProperty, "AccentTextBrush");
        }
        else
        {
            btn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
            btn.SetResourceReference(Button.ForegroundProperty, "ControlTextBrush");
        }
        return btn;
    }

    private Button MakeSortBtn(string label)
    {
        var btn = new Button
        {
            Content = label, FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
            Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 4, 0), Cursor = Cursors.Hand
        };
        if (TryFindResource("ModernButton") is Style s) btn.Style = s;
        btn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        btn.SetResourceReference(Button.ForegroundProperty, "ControlDimBrush");
        return btn;
    }

    // ── Status helper ─────────────────────────────────────────────────────

    private string? GetCardStatus(ParticipantConfig p)
    {
        // API error beats everything — shows even on inactive cards
        if (!string.IsNullOrEmpty(p.LastApiError)) return p.LastApiError;

        if (p.Enabled)
            return _mainWindow?.GetLiveParticipantStatus(p.Type, p.Model, p.ServerUrl);
        if (IsCloud(p.Type) && string.IsNullOrWhiteSpace(WindowsCredentialManager.Load(p.Type)))
            return "No API key";
        return null;
    }

    // ── Model info dialog ──────────────────────────────────────────────────

    private void ShowModelInfo(ParticipantConfig p)
    {
        var isOllama = p.Type == "Ollama";
        var win = new Window
        {
            Title  = Properties.Loc.S("ModelInfo_Title"),
            Width  = 400, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner  = this, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize
        };
        // Inherit the exact same resource dictionaries that are loaded in this window
        // (works for both .oxsuit and .xaml themes without re-reading the file)
        foreach (var rd in Resources.MergedDictionaries)
            win.Resources.MergedDictionaries.Add(rd);
        win.SetResourceReference(BackgroundProperty, "ContentBgBrush");
        win.SourceInitialized += (_, _) => TryApplyTitleBarTo(win);

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        win.Content = root;
        UiZoomHelper.Apply(win, UiZoomHelper.FromSettings());

        // Header
        var provDot = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(ProviderColor(p.Type)), Margin = new Thickness(0,0,8,0), VerticalAlignment = VerticalAlignment.Center };
        var headerSp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,4) };
        headerSp.Children.Add(provDot);
        var modelTb = new TextBlock { Text = string.IsNullOrWhiteSpace(p.Model) ? p.Type : p.Model, FontSize = 15, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI") };
        modelTb.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        headerSp.Children.Add(modelTb);
        root.Children.Add(headerSp);

        var provTb = new TextBlock { Text = isOllama ? $"Ollama · {p.ServerUrl}" : p.Type, FontSize = 11, FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0,0,0,16) };
        provTb.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        root.Children.Add(provTb);

        // Specialty — use the model's own words if available, fall back to knowledge base
        var specialty = !string.IsNullOrWhiteSpace(p.SelfDescription)
            ? p.SelfDescription
            : ModelKnowledge.GetDescription(p.Type, p.Model);
        AddInfoSection(root, Properties.Loc.S("ModelInfo_Specialty"), specialty);

        // Technical section
        var ctx = ModelKnowledge.GetContextWindow(p.Type, p.Model);
        var techLines = new List<(string, string)>();
        if (ctx is not null) techLines.Add((Properties.Loc.S("ModelInfo_ContextWindow"), ctx));
        if (!isOllama)
        {
            techLines.Add((Properties.Loc.S("ModelInfo_CostTier"), ModelKnowledge.GetCostTier(p.Type, p.Model)));
        }
        else
        {
            var vram = ModelKnowledge.EstimateVram(p.Model);
            if (vram != "Unknown") techLines.Add((Properties.Loc.S("ModelInfo_EstVram"), vram));
        }
        if (techLines.Count > 0)
            AddInfoTable(root, Properties.Loc.S("ModelInfo_Specs"), techLines);

        // For Ollama: async-populate live details from /api/show
        if (isOllama)
        {
            var livePanel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            root.Children.Add(livePanel);
            var loadingTb = new TextBlock { Text = Properties.Loc.S("ModelInfo_FetchingDetails"), FontSize = 11, FontFamily = new FontFamily("Segoe UI") };
            loadingTb.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            livePanel.Children.Add(loadingTb);

            win.Loaded += async (_, _) =>
            {
                try
                {
                    var info = await new OllamaService(p.ServerUrl).GetModelInfoAsync(p.Model);
                    livePanel.Children.Clear();
                    if (info is null) return;
                    var liveLines = new List<(string, string)>();
                    if (!string.IsNullOrEmpty(info.Family))            liveLines.Add((Properties.Loc.S("ModelInfo_Family"),       info.Family));
                    if (!string.IsNullOrEmpty(info.ParameterSize))     liveLines.Add((Properties.Loc.S("ModelInfo_Parameters"),   info.ParameterSize));
                    if (!string.IsNullOrEmpty(info.QuantizationLevel)) liveLines.Add((Properties.Loc.S("ModelInfo_Quantization"), info.QuantizationLevel));
                    if (!string.IsNullOrEmpty(info.Format))            liveLines.Add((Properties.Loc.S("ModelInfo_Format"),       info.Format));
                    if (liveLines.Count > 0) AddInfoTable(livePanel, Properties.Loc.S("ModelInfo_LiveDetails"), liveLines);
                }
                catch { /* ignore — Ollama might not be running */ }
            };
        }

        // ── Likes / Dislikes ───────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(p.Likes) || !string.IsNullOrWhiteSpace(p.Dislikes))
        {
            if (!string.IsNullOrWhiteSpace(p.Likes))
            {
                var likesTb = new TextBlock
                {
                    FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
                };
                var likesRun = new System.Windows.Documents.Run(p.Likes);
                likesTb.Inlines.Add(new System.Windows.Documents.Run("likes:  ")
                    { FontWeight = FontWeights.SemiBold });
                likesTb.Inlines.Add(likesRun);
                likesTb.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
                root.Children.Add(likesTb);
            }

            if (!string.IsNullOrWhiteSpace(p.Dislikes))
            {
                var dislikesTb = new TextBlock
                {
                    FontSize = 11, FontFamily = new FontFamily("Segoe UI"),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
                };
                dislikesTb.Inlines.Add(new System.Windows.Documents.Run("dislikes:  ")
                    { FontWeight = FontWeights.SemiBold });
                dislikesTb.Inlines.Add(new System.Windows.Documents.Run(p.Dislikes));
                dislikesTb.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
                root.Children.Add(dislikesTb);
            }
        }

        // Close button
        var closeBtn = MakeBtn(Properties.Loc.S("Btn_Close"), isPrimary: false);
        closeBtn.Margin  = new Thickness(0, 20, 0, 0);
        closeBtn.Padding = new Thickness(20, 8, 20, 8);
        closeBtn.HorizontalAlignment = HorizontalAlignment.Right;
        closeBtn.Click  += (_, _) => win.Close();
        root.Children.Add(closeBtn);

        win.ShowDialog();
    }

    private void AddInfoSection(StackPanel root, string label, string text)
    {
        var lbl = new TextBlock { Text = label, FontSize = 10, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 0, 0, 4) };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        root.Children.Add(lbl);
        var tb = new TextBlock { Text = text, FontSize = 12, FontFamily = new FontFamily("Segoe UI"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        root.Children.Add(tb);
    }

    private void AddInfoTable(Panel root, string label, List<(string Key, string Val)> rows)
    {
        var lbl = new TextBlock { Text = label, FontSize = 10, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 0, 0, 4) };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
        root.Children.Add(lbl);
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var (i, row) in rows.Select((r, i) => (i, r)))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keyTb = new TextBlock { Text = row.Key, FontSize = 11, FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 2, 16, 2) };
            keyTb.SetResourceReference(TextBlock.ForegroundProperty, "SidebarDimBrush");
            Grid.SetRow(keyTb, i); Grid.SetColumn(keyTb, 0);
            grid.Children.Add(keyTb);
            var valTb = new TextBlock { Text = row.Val, FontSize = 11, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 2) };
            valTb.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
            Grid.SetRow(valTb, i); Grid.SetColumn(valTb, 1);
            grid.Children.Add(valTb);
        }
        root.Children.Add(grid);
    }

    // ── Model knowledge base ───────────────────────────────────────────────

    private static class ModelKnowledge
    {
        public static string GetDescription(string provider, string model)
        {
            var m = model.ToLowerInvariant();
            return provider switch
            {
                "Anthropic" when m.Contains("opus")    => "Exceptional reasoning, analysis, and nuanced writing. Anthropic's most capable model — best when quality matters most.",
                "Anthropic" when m.Contains("sonnet")  => "Excellent balance of intelligence and speed. Great for coding, writing, and complex tasks at a reasonable cost.",
                "Anthropic" when m.Contains("haiku")   => "Fast and lightweight. Great for simple tasks, classification, and high-volume use cases.",
                "Anthropic"                             => "Claude — Anthropic's AI assistant. Helpful, harmless, and honest.",
                "Google AI" when m.Contains("ultra")   => "Google's most capable Gemini model. Best for advanced research, complex reasoning, and multimodal tasks.",
                "Google AI" when m.Contains("pro")     => "Strong coding, reasoning, and long-context understanding. A solid all-rounder.",
                "Google AI" when m.Contains("flash")   => "Fast and efficient with a massive context window. Great for summarisation and document analysis.",
                "Google AI" when m.Contains("nano")    => "Ultra-fast and lightweight. Designed for quick responses and on-device tasks.",
                "Google AI"                             => "Gemini — Google's AI assistant family.",
                "OpenAI ChatGPT" when m.Contains("o3") || m.Contains("o1") => "Advanced reasoning with extended thinking time. Top pick for math, logic, and hard science problems.",
                "OpenAI ChatGPT" when m.Contains("gpt-4o")                 => "OpenAI's multimodal flagship. Excellent at coding, analysis, image understanding, and natural conversation.",
                "OpenAI ChatGPT" when m.Contains("gpt-4")                  => "Strong instruction following and reasoning. A reliable workhorse for complex tasks.",
                "OpenAI ChatGPT" when m.Contains("gpt-3.5")                => "Fast and affordable. Good for straightforward everyday tasks.",
                "OpenAI ChatGPT"                                            => "OpenAI GPT — versatile AI assistant.",
                "Groq"      => "Hosted on Groq's ultra-fast LPU inference hardware. Great when response speed is critical.",
                "OpenRouter" => "Routed via OpenRouter, giving access to hundreds of models from many providers.",
                "Mistral"   => "Mistral AI — efficient, high-performance European models. Good balance of quality and cost.",
                "xAI Grok"  => "Grok by xAI — real-time knowledge access and a direct conversational style.",
                "Ollama ☁"  => "Ollama Cloud — open models hosted via the Ollama API at api.ollama.com.",
                _ when m.Contains("code") || m.Contains("coder") || m.Contains("codestral") => "Specialised coding model — excels at code generation, debugging, refactoring, and code review.",
                _ when m.Contains("deepseek") => "DeepSeek — strong at coding and mathematical reasoning. Competitive with frontier models.",
                _ when m.Contains("llama")    => "Meta LLaMA — capable open-source general-purpose model. Widely used and well-supported.",
                _ when m.Contains("mistral")  => "Mistral — efficient European model. Good reasoning and instruction following.",
                _ when m.Contains("phi")      => "Microsoft Phi — small but surprisingly capable. Good for resource-constrained setups.",
                _ when m.Contains("gemma")    => "Google Gemma — lightweight open model. Good for conversation and general tasks.",
                _ when m.Contains("qwen")     => "Alibaba Qwen — strong multilingual capabilities and coding performance.",
                _ when m.Contains("nomic") || m.Contains("embed") => "Embedding model — optimised for semantic search, RAG pipelines, and vector similarity.",
                _                             => "Local open-source model running via Ollama."
            };
        }

        public static string? GetContextWindow(string provider, string model)
        {
            var m = model.ToLowerInvariant();
            return provider switch
            {
                "Anthropic"      => "200,000 tokens",
                "Google AI" when m.Contains("1.5") || m.Contains("2.0") || m.Contains("2.5") => "1,000,000+ tokens",
                "Google AI"      => "32,000 tokens",
                "OpenAI ChatGPT" when m.Contains("o3") || m.Contains("o1") || m.Contains("gpt-4o") || m.Contains("gpt-4") => "128,000 tokens",
                "OpenAI ChatGPT" when m.Contains("gpt-3.5") => "16,000 tokens",
                "Groq" when m.Contains("llama-3") || m.Contains("llama3") => "128,000 tokens",
                "Mistral" when m.Contains("large") || m.Contains("medium") => "128,000 tokens",
                "Mistral"        => "32,000 tokens",
                _ when m.Contains("llama3.2") || m.Contains("llama-3.2") || m.Contains("llama3.1") || m.Contains("llama-3.1") => "128,000 tokens",
                _ when m.Contains("llama3")   || m.Contains("llama-3")    => "8,000 tokens",
                _ when m.Contains("qwen2.5")  || m.Contains("phi-3") || m.Contains("phi3") || m.Contains("phi4") => "128,000 tokens",
                _ when m.Contains("mistral")  => "32,000 tokens",
                _ => null
            };
        }

        public static string EstimateVram(string model)
        {
            var m = model.ToLowerInvariant();
            if (m.Contains("405b") || m.Contains("180b")) return "200+ GB  (multi-GPU)";
            if (m.Contains("70b")  || m.Contains("72b"))  return "40–80 GB";
            if (m.Contains("34b")  || m.Contains("32b"))  return "20–40 GB";
            if (m.Contains("30b"))                        return "20–35 GB";
            if (m.Contains("13b")  || m.Contains("14b"))  return "8–16 GB";
            if (m.Contains("11b"))                        return "7–12 GB";
            if (m.Contains("7b")   || m.Contains("8b"))   return "4–8 GB";
            if (m.Contains("3b")   || m.Contains("4b"))   return "2–4 GB";
            if (m.Contains("1.5b") || m.Contains("2b"))   return "1–3 GB";
            if (m.Contains("0.5b") || m.Contains("1b"))   return "1–2 GB";
            return "Unknown";
        }

        public static string GetCostTier(string provider, string model)
        {
            var m = model.ToLowerInvariant();
            return provider switch
            {
                "Anthropic" when m.Contains("opus")   => "High",
                "Anthropic" when m.Contains("sonnet") => "Medium",
                "Anthropic" when m.Contains("haiku")  => "Low",
                "Anthropic"                            => "Medium",
                "Google AI" when m.Contains("ultra")  => "High",
                "Google AI" when m.Contains("pro")    => "Medium",
                "Google AI" when m.Contains("flash")  => "Low",
                "Google AI" when m.Contains("nano")   => "Free / very low",
                "OpenAI ChatGPT" when m.Contains("o3")        => "Very high",
                "OpenAI ChatGPT" when m.Contains("o1")        => "High",
                "OpenAI ChatGPT" when m.Contains("gpt-4o") || m.Contains("gpt-4") => "Medium–high",
                "OpenAI ChatGPT" when m.Contains("gpt-3.5")  => "Low",
                "Groq"      => "Free tier available",
                "OpenRouter" => "Varies by model",
                "Mistral"   => "Low–medium",
                "xAI Grok"  => "Medium",
                "Ollama ☁"  => "Varies by model",
                "Ollama"    => "Free  (runs locally)",
                _           => "—"
            };
        }
    }

    // ── DWM title-bar theming ──────────────────────────────────────────────

    /// <summary>
    /// Applies DWM dark-mode flag + caption colour to any window.
    /// Resolves SidebarBgBrush from the window's own resource tree.
    /// Safe to call from SourceInitialized — HWND is guaranteed to exist by then.
    /// </summary>
    internal static void TryApplyTitleBarTo(Window w)
    {
        try
        {
            if (w.TryFindResource("SidebarBgBrush") is not SolidColorBrush bg) return;
            var hwnd   = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            var isDark = RelLum(bg.Color) < 0.5 ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref isDark, 4);          // dark/light mode (Win 10+)
            var textColor = w.TryFindResource("SidebarTextBrush") is SolidColorBrush tb ? tb.Color : Color.FromRgb(240, 240, 240);
            var cr   = bg.Color.R | (bg.Color.G << 8) | (bg.Color.B << 16);
            var tcr  = textColor.R | (textColor.G << 8) | (textColor.B << 16);
            DwmSetWindowAttribute(hwnd, 35, ref cr,  4);             // caption colour (Win 11+)
            DwmSetWindowAttribute(hwnd, 36, ref tcr, 4);             // caption text colour (Win 11+)
        }
        catch { }
    }

    private void TryApplyTitleBar() => TryApplyTitleBarTo(this);

    // Correct entry point name — the function is DwmSetWindowAttribute, not DwmSet.
    [System.Runtime.InteropServices.DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int val, int sz);

    private static double RelLum(Color c)
    {
        static double L(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return 0.2126 * L(c.R / 255.0) + 0.7152 * L(c.G / 255.0) + 0.0722 * L(c.B / 255.0);
    }
}
