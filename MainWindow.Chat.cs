using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SysIO = System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClaudetRelay.Services;

namespace ClaudetRelay;

public partial class MainWindow : Window
{

    private async void CloseProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProjectFolder is null || _currentProject is null) return;

        // Ask Claudette whether to make a backup - only when a backup folder is configured
        var settings         = SettingsService.Load();
        var backupFolderSet  = !string.IsNullOrWhiteSpace(settings.BackupFolder);
        if (backupFolderSet)
        {
            var choice = ShowClaudetteBackupDialog(_currentProject.ProjectName);
            if (choice == BackupChoice.Cancel)
                return;                            // user changed their mind

            if (choice == BackupChoice.BackupAndClose)
                await CreateProjectBackupAsync(_currentProjectFolder!, _currentProject!.ProjectName);  // failure shows error but does not block closing
        }

        // Persist last-opened timestamp before closing
        _currentProject.LastOpened = DateTime.UtcNow;
        ProjectService.SaveProject(_currentProjectFolder!, _currentProject);

        // Stop any running stream, clear the chat panel
        _streamCts?.Cancel();
        CancelAllPrivateTasks();
        ChatPanel.Children.Clear();
        _sharedHistory.Clear();

        CloseCurrentProject();

        AddSystemMessage("Project closed. Start a new chat or open a project from the Projects tab.");
    }

    // ── Backup ────────────────────────────────────────────────────────────────

    private enum BackupChoice { BackupAndClose, CloseOnly, Cancel }

    /// <summary>
    /// Shows Claudette's backup-prompt dialog. Returns the user's choice.
    /// </summary>
    private BackupChoice ShowClaudetteBackupDialog(string projectName)
    {
        var bgBrush  = (Brush)FindResource("ContentBgBrush");
        var result   = BackupChoice.Cancel;

        var dlg = new Window
        {
            Title                 = "Close Project",
            Width                 = 460,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ResizeMode            = ResizeMode.NoResize,
            Background            = bgBrush
        };
        ApplyThemeToDialog(dlg);

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };

        // ── Header row: Claudette image + title ────────────────────────────
        var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var img = new System.Windows.Controls.Image
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/Assets/Claudette.png")),
            Width  = 48,
            Height = 48,
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        Grid.SetColumn(img, 0);

        var textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleTb = new TextBlock
        {
            Text         = "Create backup? 🐙",
            FontSize     = 15,
            FontWeight   = FontWeights.SemiBold,
            FontFamily   = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 6)
        };
        titleTb.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");

        var msgTb = new TextBlock
        {
            Text         = $"You are closing project \"{projectName}\". " +
                           "Should I create a backup first? 🐙\n\n" +
                           "Quick tip: keep your project folder tidy and delete old backups from time to time - " +
                           "a full backup folder is like a tangled tentacle net! 🐙💦",
            FontSize     = 13,
            FontFamily   = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap
        };
        msgTb.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        textCol.Children.Add(titleTb);
        textCol.Children.Add(msgTb);
        Grid.SetColumn(textCol, 1);

        headerGrid.Children.Add(img);
        headerGrid.Children.Add(textCol);
        panel.Children.Add(headerGrid);

        // ── Button row ─────────────────────────────────────────────────────
        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, 8, 0, 0)
        };

        var backupBtn = new Button
        {
            Content = "💾 Backup + Close",
            Height  = 36,
            Padding = new Thickness(16, 0, 16, 0),
            Margin  = new Thickness(0, 0, 8, 0),
            Style   = (Style)FindResource("ModernButton")
        };
        backupBtn.SetResourceReference(Button.BackgroundProperty, "PrimaryAccentBrush");
        backupBtn.SetResourceReference(Button.ForegroundProperty, "AccentTextBrush");

        var closeBtn = new Button
        {
            Content = "Close",
            Height  = 36,
            Padding = new Thickness(16, 0, 16, 0),
            Margin  = new Thickness(0, 0, 8, 0),
            Style   = (Style)FindResource("ModernButton")
        };
        closeBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        closeBtn.SetResourceReference(Button.ForegroundProperty, "ControlTextBrush");

        var cancelBtn = new Button
        {
            Content = "Cancel",
            Height  = 36,
            Padding = new Thickness(16, 0, 16, 0),
            Style   = (Style)FindResource("ModernButton")
        };
        cancelBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        cancelBtn.SetResourceReference(Button.ForegroundProperty, "ControlTextBrush");

        btnRow.Children.Add(backupBtn);
        btnRow.Children.Add(closeBtn);
        btnRow.Children.Add(cancelBtn);
        panel.Children.Add(btnRow);

        backupBtn.Click += (_, _) => { result = BackupChoice.BackupAndClose; dlg.Close(); };
        closeBtn .Click += (_, _) => { result = BackupChoice.CloseOnly;      dlg.Close(); };
        cancelBtn.Click += (_, _) => { result = BackupChoice.Cancel;         dlg.Close(); };

        dlg.Content = panel;
        dlg.ShowDialog();
        return result;
    }

    /// <summary>
    /// Creates a ZIP backup of <paramref name="projFolder"/> into the configured backup folder.
    /// Shows a progress window while working.
    /// Returns true on success; shows an error dialog and returns false on failure.
    /// </summary>
    private async Task<bool> CreateProjectBackupAsync(string projFolder, string projectName)
    {
        var settings = SettingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.BackupFolder))
        {
            MessageBox.Show(
                "Please set a backup folder first under ☰ → Folders Setup.",
                "No Backup Folder", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var backupFolder = settings.BackupFolder;
        try
        {
            // CreateDirectory is a no-op if the directory already exists
            SysIO.Directory.CreateDirectory(backupFolder);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not create backup folder:\n{ex.Message}",
                "Backup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        var invalidChars        = SysIO.Path.GetInvalidFileNameChars();
        var safeName            = new string(projectName
            .Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
        var projectBackupFolder = SysIO.Path.Combine(backupFolder, safeName);
        var timestamp           = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var zipName             = $"{safeName}_{timestamp}.zip";
        var zipPath             = SysIO.Path.Combine(projectBackupFolder, zipName);

        // ── Progress window ────────────────────────────────────────────────
        var bgBrush     = (Brush)FindResource("ContentBgBrush");
        var progressWin = new Window
        {
            Title                 = "Creating backup…",
            Width                 = 420,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ResizeMode            = ResizeMode.NoResize,
            Background            = bgBrush
        };
        ApplyThemeToDialog(progressWin);

        var progressPanel = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };

        var progressTitle = new TextBlock
        {
            Text       = $"💾  {projectName}",
            FontSize   = 13,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"),
            Margin     = new Thickness(0, 0, 0, 12)
        };
        progressTitle.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");

        var progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value   = 0,
            Height  = 12,
            Margin  = new Thickness(0, 0, 0, 8)
        };

        var pctLabel = new TextBlock
        {
            Text       = "0 %",
            FontSize   = 11,
            FontFamily = new FontFamily("Segoe UI"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin     = new Thickness(0, 0, 0, 6)
        };
        pctLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var fileLabel = new TextBlock
        {
            Text         = "Reading files…",
            FontSize     = 11,
            FontFamily   = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        fileLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        progressPanel.Children.Add(progressTitle);
        progressPanel.Children.Add(progressBar);
        progressPanel.Children.Add(pctLabel);
        progressPanel.Children.Add(fileLabel);
        progressWin.Content = progressPanel;
        progressWin.Show();

        // ── Progress callback (marshals to UI thread automatically) ────────
        var progress = new Progress<(int pct, string fileName)>(update =>
        {
            progressBar.Value = update.pct;
            pctLabel.Text     = $"{update.pct} %";
            fileLabel.Text    = update.fileName;
        });

        // ── Zip file by file ───────────────────────────────────────────────
        try
        {
            SysIO.Directory.CreateDirectory(projectBackupFolder);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not create project subfolder:\n{ex.Message}",
                "Backup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        try
        {
            await Task.Run(() =>
            {
                var files = SysIO.Directory.GetFiles(projFolder, "*",
                    SysIO.SearchOption.AllDirectories);
                var total        = Math.Max(1, files.Length);
                int lastReported = -1;

                using var archive = System.IO.Compression.ZipFile.Open(
                    zipPath, System.IO.Compression.ZipArchiveMode.Create);

                for (int i = 0; i < files.Length; i++)
                {
                    var relativePath = SysIO.Path.GetRelativePath(projFolder, files[i]);
                    var entry = archive.CreateEntry(relativePath,
                        System.IO.Compression.CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    using var fileStream  = SysIO.File.OpenRead(files[i]);
                    fileStream.CopyTo(entryStream);

                    int pct = (int)((double)(i + 1) / total * 100);
                    if (pct != lastReported)
                    {
                        lastReported = pct;
                        ((IProgress<(int, string)>)progress)
                            .Report((pct, SysIO.Path.GetFileName(files[i])));
                    }
                }
            });

            progressWin.Close();
            AddSystemMessage($"✅ Backup created: {zipPath}");
            return true;
        }
        catch (Exception ex)
        {
            progressWin.Close();
            // Delete the incomplete ZIP so it doesn't leave a corrupt file behind
            try { if (SysIO.File.Exists(zipPath)) SysIO.File.Delete(zipPath); } catch { }

            MessageBox.Show(
                $"Backup failed:\n{ex.Message}\n\n" +
                "Your project data is still safe in the project folder.\n" +
                "Please retry the backup manually via the 💾 button after fixing the issue.",
                "Backup Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private async void BackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProjectFolder is null || _currentProject is null) return;
        await CreateProjectBackupAsync(_currentProjectFolder, _currentProject.ProjectName);
    }

    private void RenderChatLogEntry(ChatLogEntry entry)
    {
        if (entry.SenderType == "System")
        {
            AddSystemMessage(entry.Message);
            return;
        }
        // Reconstruct _sharedHistory entry for AI context
        if (entry.IsUser)
            _sharedHistory.Add(new CloudAIMessage("user", entry.Message, "User"));
        else if (entry.SenderType == "AI")
            _sharedHistory.Add(new CloudAIMessage("assistant", entry.Message, entry.DisplayName));

        // Guard against legacy log entries that pre-date BubbleKey / AccentKey storage.
        // An empty key causes SetResourceReference to resolve nothing → WPF falls back to
        // SystemColors.ControlTextBrush (black) on a transparent bubble - readable in light
        // themes but invisible / broken in dark ones.
        var bubbleKey = string.IsNullOrEmpty(entry.BubbleKey)
            ? (entry.IsUser ? "TertiaryBubbleBrush" : "PrimaryBubbleBrush")
            : entry.BubbleKey;
        var accentKey = string.IsNullOrEmpty(entry.AccentKey)
            ? (entry.IsUser ? "TertiaryAccentBrush" : "PrimaryAccentBrush")
            : entry.AccentKey;

        var bubble = AddStreamingBubble(entry.DisplayName, entry.AvatarLabel,
                                         accentKey, bubbleKey, entry.IsUser);
        bubble.StopThinking();
        ApplyEmoteFormatting(bubble, entry.Message,
                             entry.IsUser ? entry.DisplayName : "");
    }

    private void AppendToProjectLog(ChatLogEntry entry)
    {
        if (_currentProjectFolder is null) return;
        try { ProjectService.AppendEntry(_currentProjectFolder, entry); }
        catch { /* non-fatal */ }
    }

    /// <summary>
    /// Appends <paramref name="entry"/> to the rolling general-chat log.
    /// Only active outside of projects. Triggers background AI summarisation
    /// when a segment rotation occurs (every 500 entries).
    /// </summary>
    private void AppendToGeneralLog(ChatLogEntry entry)
    {
        if (_currentProjectFolder is not null) return;
        try
        {
            GeneralChatLogService.AppendEntry(entry, out var displaced);
            if (displaced is { Count: > 0 })
                _ = SummarizeAndCompressGeneralLogAsync(displaced);
        }
        catch { /* non-fatal */ }
    }

    // ── General-chat log summarisation ────────────────────────────────────

    private const string GeneralSummarizeSystem =
        "You are a chat log summarizer. Summarize the provided segment of a general chat " +
        "session concisely, preserving all key topics, decisions, and important information " +
        "that was discussed. Write two to three short paragraphs. " +
        "Output only the summary - no preamble, no metadata, no heading.";

    private const string GeneralCompressSystem =
        "You are a summary compressor. The following is a running log of past chat-session summaries. " +
        "Condense it into a single compact summary that preserves all key topics, themes, decisions, " +
        "and important information. Output only the condensed text - no headers, no preamble.";

    /// <summary>Max characters in summary.md before compression is triggered.</summary>
    private const int SummaryCompressThreshold = 3_000;

    /// <summary>
    /// Runs in the background after a segment rotation.
    /// Picks the best available AI (Ollama with Gemma first, then other Ollama, then Cloud),
    /// silently summarises <paramref name="displaced"/> entries, appends to summary.md,
    /// then compresses summary.md if it has grown too large.
    /// </summary>
    private async Task SummarizeAndCompressGeneralLogAsync(List<ChatLogEntry> displaced)
    {
        // ── Show Claudette speech bubble + start pulsing ──────────────────
        await Dispatcher.InvokeAsync(() =>
        {
            ClaudetteSpeechText.Text         = "Ich räume mal den Chat auf! 🐙";
            ClaudetteSpeechBubble.Visibility  = Visibility.Visible;
            StartClaudettePulse();
        });

        try
        {
            // Build a readable text version of the displaced segment
            var sb = new StringBuilder();
            foreach (var e in displaced)
            {
                if (e.SenderType == "System") continue;
                sb.AppendLine($"[{e.Timestamp:HH:mm}] {e.DisplayName}: {e.Message}");
                sb.AppendLine();
            }
            var chatText = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(chatText)) return;

            var summaryPrompt = $"Summarize this chat log segment:\n\n{chatText}";

            // ── Pick summarizing service ──────────────────────────────────
            var ollamaUi = PickSummarizingOllama();
            var cloudUi  = ollamaUi is null
                ? _cloudAIParticipants.FirstOrDefault(ui => ui.Data.Enabled)
                : null;
            if (ollamaUi is null && cloudUi is null) return;

            string summary;
            if (ollamaUi is not null)
            {
                // Prepend system instruction to the user turn (Ollama has no separate system arg here)
                var hist = new List<OllamaChatMessage>
                {
                    new("system", GeneralSummarizeSystem),
                    new("user",   summaryPrompt)
                };
                var result = new StringBuilder();
                await foreach (var tok in ollamaUi.Data.Service.StreamAsync(hist, CancellationToken.None))
                    result.Append(tok);
                summary = result.ToString().Trim();
            }
            else
            {
                var hist = new List<CloudAIMessage> { new("user", summaryPrompt, "System") };
                var result = new StringBuilder();
                await foreach (var tok in cloudUi!.Data.Service.StreamAsync(hist, GeneralSummarizeSystem, CancellationToken.None))
                    result.Append(tok);
                summary = result.ToString().Trim();
            }

            if (string.IsNullOrWhiteSpace(summary)) return;

            var section = $"## {DateTime.Now:yyyy-MM-dd HH:mm}\n{summary}";
            GeneralChatLogService.AppendToSummary(section);

            // ── Compress summary.md if it has grown too large ─────────────
            var fullSummary = GeneralChatLogService.ReadSummary() ?? "";
            if (fullSummary.Length > SummaryCompressThreshold)
                await CompressGeneralSummaryAsync(fullSummary, ollamaUi, cloudUi);
        }
        catch { /* non-fatal - summarisation is best-effort */ }
        finally
        {
            // ── Hide speech bubble + stop pulsing ─────────────────────────
            await Dispatcher.InvokeAsync(() =>
            {
                ClaudetteSpeechBubble.Visibility = Visibility.Collapsed;
                StopClaudettePulse();
            });
        }
    }

    /// <summary>
    /// Replaces summary.md with a compressed version produced by an AI.
    /// </summary>
    private async Task CompressGeneralSummaryAsync(string currentSummary,
        OllamaParticipantUI? ollamaUi, CloudAIParticipantUI? cloudUi)
    {
        try
        {
            var compressPrompt = $"Condense this running summary:\n\n{currentSummary}";

            string compressed;
            if (ollamaUi is not null)
            {
                var hist = new List<OllamaChatMessage>
                {
                    new("system", GeneralCompressSystem),
                    new("user",   compressPrompt)
                };
                var result = new StringBuilder();
                await foreach (var tok in ollamaUi.Data.Service.StreamAsync(hist, CancellationToken.None))
                    result.Append(tok);
                compressed = result.ToString().Trim();
            }
            else if (cloudUi is not null)
            {
                var hist = new List<CloudAIMessage> { new("user", compressPrompt, "System") };
                var result = new StringBuilder();
                await foreach (var tok in cloudUi.Data.Service.StreamAsync(hist, GeneralCompressSystem, CancellationToken.None))
                    result.Append(tok);
                compressed = result.ToString().Trim();
            }
            else return;

            if (!string.IsNullOrWhiteSpace(compressed))
                GeneralChatLogService.ReplaceSummary(
                    $"*[Compressed {DateTime.Now:yyyy-MM-dd HH:mm}]*\n\n{compressed}");
        }
        catch { /* non-fatal */ }
    }

    /// <summary>
    /// Picks the best available Ollama participant for background summarisation:
    /// Gemma models first (faster), then any other enabled Ollama.
    /// Returns null if no Ollama is enabled.
    /// </summary>
    private OllamaParticipantUI? PickSummarizingOllama()
        => _ollamaParticipants
            .Where(ui => ui.Data.Enabled)
            .OrderByDescending(ui =>
                ui.Data.Service.CurrentModel.Contains("gemma", StringComparison.OrdinalIgnoreCase) ? 2 :
                ui.Data.Service.CurrentModel.Contains("qwen",  StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .FirstOrDefault();

    // ── Chat export ────────────────────────────────────────────────────────

    private void ExportChatButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProjectFolder is not null && _currentProject is not null)
        {
            // Project is open - use the project exporter
            var menu = new ContextMenu { PlacementTarget = ExportChatButton,
                                         Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            var htmlItem  = new MenuItem { Header = "🔄  Export as HTML…" };
            var mdItem    = new MenuItem { Header = "📝  Export as Markdown…" };
            var capturedFolder = _currentProjectFolder;
            var capturedMeta   = _currentProject;
            htmlItem.Click  += (_, _) => ExportProject(capturedFolder, capturedMeta, "html");
            mdItem.Click    += (_, _) => ExportProject(capturedFolder, capturedMeta, "md");
            menu.Items.Add(htmlItem);
            menu.Items.Add(mdItem);
            menu.IsOpen = true;
            return;
        }

        // General chat - export the rolling log
        var entries = GeneralChatLogService.LoadRecentLog();
        if (entries.Count == 0)
        {
            MessageBox.Show("No general chat history to export yet.",
                            "Nothing to export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var menu2 = new ContextMenu { PlacementTarget = ExportChatButton,
                                      Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var html2  = new MenuItem { Header = "🔄  Export as HTML…" };
        var md2    = new MenuItem { Header = "📝  Export as Markdown…" };
        html2.Click  += (_, _) => ExportGeneralChat(entries, "html");
        md2.Click    += (_, _) => ExportGeneralChat(entries, "md");
        menu2.Items.Add(html2);
        menu2.Items.Add(md2);
        menu2.IsOpen = true;
    }

    private void ExportGeneralChat(List<ChatLogEntry> entries, string format)
    {
        var isHtml   = format == "html";
        var dateName = DateTime.Now.ToString("yyyy-MM-dd");
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Export General Chat",
            FileName   = $"ClaudetRelay-Chat-{dateName}",
            Filter     = isHtml ? "HTML file (*.html)|*.html"
                                : "Markdown file (*.md)|*.md|Text file (*.txt)|*.txt",
            DefaultExt = format
        };
        if (dlg.ShowDialog() != true) return;

        var fs = SettingsService.Load();
        var content = isHtml
            ? ExportService.GenerateHtml("General Chat", entries,
                                          fs.ChatFontFamily, fs.ChatFontSize,
                                          fs.ChatBubbleWidthPercent)
            : ExportService.GenerateMarkdown("General Chat", entries);

        SysIO.File.WriteAllText(dlg.FileName, content, System.Text.Encoding.UTF8);

        var result = MessageBox.Show(
            $"Exported {entries.Count} messages to\n{dlg.FileName}\n\nOpen the file now?",
            "Export complete", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (result == MessageBoxResult.Yes)
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
    }

    // ── Simple input dialog ────────────────────────────────────────────────

    private string? ShowInputDialog(string title, string prompt, string defaultValue = "")
    {
        // FindResource is called on *this* (MainWindow) which has the theme loaded.
        // SetResourceReference would search the popup's own empty resource tree and fall
        // back to the default WPF chrome - producing black buttons on dark themes.
        var win = new Window
        {
            Title                 = title,
            Width                 = 400, Height = 170,
            Owner                 = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode            = ResizeMode.NoResize,
            ShowInTaskbar         = false,
            Background            = (Brush)FindResource("SidebarBgBrush")
        };
        ApplyThemeToDialog(win);

        var lbl = new TextBlock
        {
            Text       = prompt,
            FontSize   = 13,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("ContentTextBrush"),
            Margin     = new Thickness(16, 16, 16, 6)
        };

        var tb = new TextBox
        {
            Text                     = defaultValue,
            FontSize                 = 13,
            FontFamily               = new FontFamily("Segoe UI"),
            Margin                   = new Thickness(16, 0, 16, 14),
            Height                   = 36,
            BorderThickness          = new Thickness(0),
            Padding                  = new Thickness(10, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background               = (Brush)FindResource("ControlBgBrush"),
            Foreground               = (Brush)FindResource("ContentTextBrush"),
            CaretBrush               = (Brush)FindResource("InputTextBrush"),
            SelectionBrush           = (Brush)FindResource("PrimaryAccentBrush")
        };

        var okBtn = new Button
        {
            Content    = "Create",
            IsDefault  = true,
            Height     = 34,
            Margin     = new Thickness(16, 0, 8, 16),
            Style      = (Style)FindResource("ModernButton"),
            Background = (Brush)FindResource("PrimaryAccentBrush"),
            Foreground = (Brush)FindResource("AccentTextBrush")
        };

        var cancelBtn = new Button
        {
            Content    = "Cancel",
            IsCancel   = true,
            Height     = 34,
            Margin     = new Thickness(0, 0, 16, 16),
            Style      = (Style)FindResource("ModernButton"),
            Background = (Brush)FindResource("ControlBgBrush"),
            Foreground = (Brush)FindResource("ContentTextBrush")
        };

        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        btnRow.Children.Add(okBtn);
        btnRow.Children.Add(cancelBtn);

        var panel = new StackPanel();
        panel.Children.Add(lbl);
        panel.Children.Add(tb);
        panel.Children.Add(btnRow);
        win.Content = panel;

        string? result = null;
        okBtn.Click += (_, _) => { result = tb.Text.Trim(); win.DialogResult = true; };
        win.Loaded  += (_, _) => { tb.Focus(); tb.SelectAll(); };
        win.ShowDialog();
        return result;
    }

    // ── New-project dialog (name + description) ────────────────────────────

    /// <summary>
    /// Shows a dialog that collects both the project name and an optional freeform
    /// description. Returns (Name, Description) on confirm, or null if cancelled.
    /// </summary>
    private (string Name, string Description)? ShowNewProjectDialog(
        string defaultName = "My Project", string defaultDescription = "")
    {
        var win = new Window
        {
            Title                 = "New Project",
            Width                 = 460,
            SizeToContent         = SizeToContent.Height,
            Owner                 = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode            = ResizeMode.NoResize,
            ShowInTaskbar         = false,
            Background            = (Brush)FindResource("SidebarBgBrush")
        };
        ApplyThemeToDialog(win);

        Border MakeInputBorder(UIElement child) => new Border
        {
            Background      = (Brush)FindResource("InputBgBrush"),
            BorderBrush     = (Brush)FindResource("ControlBgBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(8),
            Padding         = new Thickness(0),
            Margin          = new Thickness(0, 0, 0, 6),
            Child           = child
        };

        TextBlock MakeLabel(string text) => new TextBlock
        {
            Text       = text,
            FontSize   = 11, FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = (Brush)FindResource("ContentDimBrush"),
            Margin     = new Thickness(0, 0, 0, 5)
        };

        TextBlock MakeHint(string text) => new TextBlock
        {
            Text         = text,
            FontSize     = 11, FontFamily = new FontFamily("Segoe UI"),
            Foreground   = (Brush)FindResource("ContentDimBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 16)
        };

        // ── Name field ─────────────────────────────────────────────────────
        var nameBox = new TextBox
        {
            Text                     = defaultName,
            FontSize                 = 13, FontFamily = new FontFamily("Segoe UI"),
            Height                   = 36,
            BorderThickness          = new Thickness(0),
            Padding                  = new Thickness(10, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background               = (Brush)FindResource("InputBgBrush"),
            Foreground               = (Brush)FindResource("ContentTextBrush"),
            CaretBrush               = (Brush)FindResource("InputTextBrush"),
            SelectionBrush           = (Brush)FindResource("PrimaryAccentBrush")
        };

        // ── Description field ──────────────────────────────────────────────
        var descBox = new TextBox
        {
            Text            = defaultDescription,
            FontSize        = 13, FontFamily = new FontFamily("Segoe UI"),
            Height          = 90,
            MinHeight       = 90,
            BorderThickness = new Thickness(0),
            Padding         = new Thickness(10, 8, 10, 8),
            TextWrapping    = TextWrapping.Wrap,
            AcceptsReturn   = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background      = (Brush)FindResource("InputBgBrush"),
            Foreground      = (Brush)FindResource("ContentTextBrush"),
            CaretBrush      = (Brush)FindResource("InputTextBrush"),
            SelectionBrush  = (Brush)FindResource("PrimaryAccentBrush"),
            ToolTip         = "Describe what this project is about. The AI participants will read this."
        };

        // ── Buttons ────────────────────────────────────────────────────────
        var okBtn = new Button
        {
            Content    = "Create",
            IsDefault  = true,
            Height     = 34, Margin = new Thickness(0, 0, 8, 0),
            Style      = (Style)FindResource("ModernButton"),
            Background = (Brush)FindResource("PrimaryAccentBrush"),
            Foreground = (Brush)FindResource("AccentTextBrush")
        };
        var cancelBtn = new Button
        {
            Content    = "Cancel",
            IsCancel   = true,
            Height     = 34,
            Style      = (Style)FindResource("ModernButton"),
            Background = (Brush)FindResource("ControlBgBrush"),
            Foreground = (Brush)FindResource("SidebarTextBrush")
        };
        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, 8, 0, 0)
        };
        btnRow.Children.Add(okBtn);
        btnRow.Children.Add(cancelBtn);

        // ── Layout ─────────────────────────────────────────────────────────
        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        root.Children.Add(MakeLabel("PROJECT NAME"));
        root.Children.Add(MakeInputBorder(nameBox));
        root.Children.Add(MakeLabel("DESCRIPTION  (optional - shown to AI participants)"));
        root.Children.Add(MakeInputBorder(descBox));
        root.Children.Add(MakeHint(
            "Tell the AI what this project is about. " +
            "Example: \"A dark fantasy novel about a dragon who falls in love with a wizard.\" " +
            "You can leave this blank and add it later in Project Settings."));
        root.Children.Add(btnRow);
        win.Content = root;

        (string Name, string Description)? dialogResult = null;
        okBtn.Click += (_, _) =>
        {
            var n = nameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(n)) { nameBox.Focus(); return; }
            dialogResult = (n, descBox.Text.Trim());
            win.DialogResult = true;
        };
        win.Loaded += (_, _) => { nameBox.Focus(); nameBox.SelectAll(); };
        win.ShowDialog();
        return dialogResult;
    }

    // ── Options menu (⋮) ──────────────────────────────────────────────────

    // OptionsMenuButton_Click → MainWindow.Settings.cs

    // OpenGeneralSettings, OpenProvidersSetup → MainWindow.Settings.cs


    // ── Project settings dialog ────────────────────────────────────────────


    // ── Cloud AI participant management ────────────────────────────────────


    // ── Input ──────────────────────────────────────────────────────────────

    private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        PlaceholderText.Visibility = string.IsNullOrEmpty(InputTextBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        if (Keyboard.Modifiers == ModifierKeys.None)
        {
            // Plain Enter → send
            SendMessage();
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            // Shift+Enter → new line, auto-continue list prefix if applicable
            var tb  = InputTextBox;
            int pos = tb.CaretIndex;

            // Find where the current line starts
            int lineStart = pos > 0 ? tb.Text.LastIndexOf('\n', pos - 1) + 1 : 0;
            string currentLine = tb.Text.Substring(lineStart, pos - lineStart);

            // Build the prefix to auto-repeat on the new line
            string prefix = "";
            var numMatch = Regex.Match(currentLine, @"^(\s*)(\d+)\.\s");
            if (numMatch.Success)
            {
                // Numbered list: increment the counter
                int n = int.Parse(numMatch.Groups[2].Value);
                prefix = numMatch.Groups[1].Value + (n + 1) + ". ";
            }
            else
            {
                var bulletMatch = Regex.Match(currentLine, @"^(\s*)([-•*])\s");
                if (bulletMatch.Success)
                    prefix = bulletMatch.Groups[1].Value + bulletMatch.Groups[2].Value + " ";
            }

            string insert = "\n" + prefix;
            tb.Text        = tb.Text.Insert(pos, insert);
            tb.CaretIndex  = pos + insert.Length;
            e.Handled      = true;
        }
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => SendMessage();

    // ── Drag & Drop files → INPUT folder ──────────────────────────────────

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = (e.Data.GetDataPresent(DataFormats.FileDrop) && _currentProjectFolder is not null)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        if (_currentProjectFolder is null)
        {
            AddSystemMessage("⚠  Open or create a project first - dropped files go into the INPUT folder.");
            return;
        }

        var files       = (string[])e.Data.GetData(DataFormats.FileDrop);
        var inputFolder = SysIO.Path.Combine(_currentProjectFolder, "INPUT");
        SysIO.Directory.CreateDirectory(inputFolder);

        int count = 0;
        foreach (var file in files)
        {
            if (!SysIO.File.Exists(file)) continue;
            var dest = SysIO.Path.Combine(inputFolder, SysIO.Path.GetFileName(file));
            // Sandbox check - ensure destination stays inside project folder
            if (!ProjectService.IsPathSafe(dest, _currentProjectFolder)) continue;
            SysIO.File.Copy(file, dest, overwrite: true);
            count++;
        }

        if (count > 0)
        {
            AddSystemMessage($"🔎 {count} file(s) copied to INPUT folder.");
            // Persist current project state
            if (_currentProject is not null)
                ProjectService.SaveProject(_currentProjectFolder!, _currentProject);
        }
    }

    private void SendMessage()
    {
        var text = InputTextBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        var avatar = _userName.Length >= 2 ? _userName[..2].ToUpper() : _userName.ToUpper();

        // ── Check for /whisper <participant> syntax ──────────────────────────────
        OllamaParticipantUI?  whisperTarget_Ollama = null;
        CloudAIParticipantUI? whisperTarget_Cloud  = null;
        string? whisperTargetName = null;
        string messageContent = text;

        if (text.StartsWith("/whisper ", StringComparison.OrdinalIgnoreCase))
        {
            var rest = text[9..].Trim();  // Skip "/whisper "
            var spaceIdx = rest.IndexOf(' ');

            if (spaceIdx > 0)
            {
                var targetName = rest[..spaceIdx];
                messageContent = rest[(spaceIdx + 1)..].Trim();

                // Find matching participant (case-insensitive)
                var ollamaMatch = _ollamaParticipants.FirstOrDefault(u =>
                    u.Data.DisplayName.Equals(targetName, StringComparison.OrdinalIgnoreCase) &&
                    u.Data.Enabled && u.Data.IsOnline == true);

                var cloudMatch = _cloudAIParticipants.FirstOrDefault(u =>
                    u.Data.DisplayName.Equals(targetName, StringComparison.OrdinalIgnoreCase) &&
                    u.Data.Enabled && u.Data.IsOnline == true);

                if (ollamaMatch is not null || cloudMatch is not null)
                {
                    whisperTarget_Ollama = ollamaMatch;
                    whisperTarget_Cloud  = cloudMatch;
                    whisperTargetName    = ollamaMatch?.Data.DisplayName ?? cloudMatch?.Data.DisplayName;
                }
                else if (!string.IsNullOrEmpty(messageContent))
                {
                    // No match found but there's a message—treat the whole thing as normal message
                    messageContent = text;
                }
                else
                {
                    // No match and no message after name—show error
                    AddSystemMessage($"⚠  Participant \"{targetName}\" not found or not online.");
                    return;
                }
            }
            else
            {
                // /whisper with no space or message—treat as normal message
                messageContent = text;
            }
        }

        AddMessage(_userName, avatar, "TertiaryAccentBrush", "TertiaryBubbleBrush", text, isUser: true);

        var entry = new ChatLogEntry
        {
            Timestamp   = DateTime.Now,
            SenderType  = "User",
            DisplayName = _userName,
            AvatarLabel = avatar,
            AccentKey   = "TertiaryAccentBrush",
            BubbleKey   = "TertiaryBubbleBrush",
            IsUser      = true,
            Message     = text
        };
        AppendToProjectLog(entry);
        AppendToGeneralLog(entry);

        InputTextBox.Clear();
        InputTextBox.Focus();

        // ── Private message (via chip or /whisper command): dispatch to one participant ──
        var targetO = _privateMsgOllamaTarget ?? whisperTarget_Ollama;
        var targetC = _privateMsgCloudTarget ?? whisperTarget_Cloud;

        if (targetO is not null || targetC is not null)
        {
            var targetName = targetO?.Data.DisplayName ?? targetC?.Data.DisplayName ?? "?";
            ClearPrivateMsgTarget();

            // Add a status message to shared history so everyone knows a whisper happened
            // (but only the recipient can see the actual content)
            AddSystemMessage($"🤫  {_userName} whispers something to {targetName}");

            // Note: DispatchPrivateTask adds the actual user message to _sharedHistory only
            // after the response arrives (Option B isolation). Do NOT add it here.
            DispatchPrivateTask(messageContent, targetO, targetC, targetName);
        }
        else
        {
            // Normal broadcast: add user message to shared history now so AI responses
            // can read it immediately.
            _sharedHistory.Add(new CloudAIMessage("user", messageContent, "User"));
            _ = TriggerAiResponsesAsync();
        }
    }

    // ── Private-message button & chip ─────────────────────────────────────

    /// <summary>Shows the participant picker and stores the selected private-message target.</summary>
    private void PrivateMsgButton_Click(object sender, RoutedEventArgs e)
    {
        // If already targeting someone, clear and return (second click = cancel)
        if (_privateMsgOllamaTarget is not null || _privateMsgCloudTarget is not null)
        {
            ClearPrivateMsgTarget();
            return;
        }

        var allOllamas  = _ollamaParticipants .Where(u => u.Data.Enabled && u.Data.IsOnline == true).ToList();
        var allCloudAIs = _cloudAIParticipants.Where(u => u.Data.Enabled && u.Data.IsOnline == true).ToList();

        if (allOllamas.Count == 0 && allCloudAIs.Count == 0)
        {
            AddSystemMessage("⚠  No online participants to send a private message to.");
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = PrivateMsgButton,
            Placement       = System.Windows.Controls.Primitives.PlacementMode.Top
        };

        void AddEntry(string displayName, string avatarLabel, string colorKey,
                      OllamaParticipantUI? o, CloudAIParticipantUI? c)
        {
            var item = new MenuItem { Header = $"{avatarLabel}  {displayName}" };
            item.Click += (_, _) => SetPrivateMsgTarget(o, c, displayName, colorKey);
            menu.Items.Add(item);
        }

        foreach (var ui in allOllamas)
            AddEntry(ui.Data.DisplayName, ui.Data.AvatarLabel, ui.Data.ColorKey, ui, null);
        foreach (var ui in allCloudAIs)
            AddEntry(ui.Data.DisplayName, ui.Data.AvatarLabel, ui.Data.ColorKey, null, ui);

        menu.IsOpen = true;
    }

    /// <summary>Stores the private-message target and shows the accent chip above the input.</summary>
    private void SetPrivateMsgTarget(
        OllamaParticipantUI?  ollamaTarget,
        CloudAIParticipantUI? cloudTarget,
        string                displayName,
        string                colorKey)
    {
        _privateMsgOllamaTarget = ollamaTarget;
        _privateMsgCloudTarget  = cloudTarget;

        // Style chip to match the participant's accent colour
        PrivateMsgChip.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty,  colorKey);
        PrivateMsgChip.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, colorKey);
        PrivateMsgChipText.Text = $"📨  Private → {displayName}";
        PrivateMsgChipText.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
        PrivateMsgClearButton.SetResourceReference(Button.ForegroundProperty, "AccentTextBrush");
        PrivateMsgChip.Visibility = Visibility.Visible;

        // Highlight the picker button to signal active targeting
        PrivateMsgButton.SetResourceReference(Button.BackgroundProperty, colorKey);
        PrivateMsgButton.SetResourceReference(Button.ForegroundProperty, "AccentTextBrush");

        // Update placeholder hint
        PlaceholderText.Text = $"Private message to {displayName}…  (Enter ↵ send  •  Shift+Enter for new line)";

        InputTextBox.Focus();
    }

    /// <summary>Clears the private-message target and resets the chip/button visuals.</summary>
    private void ClearPrivateMsgTarget()
    {
        _privateMsgOllamaTarget = null;
        _privateMsgCloudTarget  = null;

        PrivateMsgChip.Visibility = Visibility.Collapsed;
        PrivateMsgButton.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        PrivateMsgButton.SetResourceReference(Button.ForegroundProperty, "SidebarDimBrush");
        PlaceholderText.Text = "Type a message...  (Enter ↵ send  •  Shift+Enter for new line)";
    }

    private void PrivateMsgClear_Click(object sender, RoutedEventArgs e)
        => ClearPrivateMsgTarget();

    /// <summary>
    /// Dispatches a private task to one participant fire-and-forget.
    /// The input stays fully unlocked so the user can send more messages (or more private tasks)
    /// while this runs. Each private task has its own CancellationTokenSource stored in
    /// <see cref="_privateTaskCts"/> so they can all be cancelled by <see cref="CancelAllPrivateTasks"/>.
    ///
    /// History isolation (Option B):
    ///   • A busy-marker is added to <see cref="_sharedHistory"/> so other models see
    ///     "[name] is working on a private task" while this task runs.
    ///   • The target model receives a history snapshot that already includes the real
    ///     user message instead of the busy-marker.
    ///   • On completion the busy-marker is replaced with the real user message so the
    ///     full exchange (question + answer) appears in shared history together.
    /// </summary>
    private void DispatchPrivateTask(
        string                userText,
        OllamaParticipantUI?  targetOllama,
        CloudAIParticipantUI? targetCloud,
        string                targetDisplayName)
    {
        bool ollamaOk = targetOllama is not null
            && targetOllama.Data.Enabled && targetOllama.Data.IsOnline == true;
        bool cloudOk  = targetCloud  is not null
            && targetCloud.Data.Enabled  && targetCloud.Data.IsOnline  == true;

        if (!ollamaOk && !cloudOk)
        {
            AddSystemMessage($"⚠  {targetDisplayName} is no longer available.");
            return;
        }

        // ── Attempt file checkouts before dispatching task ──────────────────
        // Determine whether this participant can write to project files.
        // Write-capable participants check out doc files exclusively (they might modify them).
        // Read-only participants check out doc files shared (safe parallel reads).
        bool canWrite = targetOllama is not null
            ? HasWriteAccess(targetOllama)
            : HasWriteAccess(targetCloud!);

        var projectFiles = GetProjectFiles();
        var blockedFiles = new List<string>();

        foreach (var filePath in projectFiles)
        {
            var mode = GetCheckoutModeForFile(filePath, canWrite);
            if (!_fileCheckout.TryCheckout(filePath, targetDisplayName, mode, out var reason))
            {
                blockedFiles.Add($"{System.IO.Path.GetFileName(filePath)} ({reason})");
            }
        }

        // If files are blocked, abort the task
        if (blockedFiles.Count > 0)
        {
            AddSystemMessage(
                Properties.Loc.S("FileCheckout_Blocked") + "\n" +
                string.Join("\n", blockedFiles));
            return;
        }

        // Build the private history snapshot BEFORE adding the busy-marker.
        // The snapshot is current shared history + the real user message appended.
        // The target model will use this; other models see the busy-marker instead.
        var privateHistory = new List<CloudAIMessage>(_sharedHistory)
        {
            new CloudAIMessage("user", userText, "User")
        };

        // Add busy-marker to shared history (visible to all other models while task runs)
        int busyIdx = _sharedHistory.Count;
        _sharedHistory.Add(new CloudAIMessage("user",
            $"[Note: {targetDisplayName} is currently working on a private task from the user " +
            $"and is unavailable for group responses. They will share their results when done.]",
            $"__busy__{targetDisplayName}"));

        var cts = new CancellationTokenSource();
        _privateTaskCts.Add(cts);

        _ = DoPrivateTaskAsync(
            targetOllama, targetCloud, targetDisplayName,
            userText, privateHistory, busyIdx, projectFiles, cts);
        // ↑ Fire and forget — input stays live immediately
    }

    private async Task DoPrivateTaskAsync(
        OllamaParticipantUI?             targetOllama,
        CloudAIParticipantUI?            targetCloud,
        string                           targetDisplayName,
        string                           userText,
        IReadOnlyList<CloudAIMessage>    privateHistory,
        int                              busyIdx,
        List<string>                     projectFiles,
        CancellationTokenSource          cts)
    {
        var ct = cts.Token;
        int histCountBeforeStream = _sharedHistory.Count; // includes the busy-marker

        try
        {
            if (targetOllama is not null)
                await RunOllamaStreamAsync(targetOllama, ct, histOverride: privateHistory);
            else
                await RunCloudAIStreamAsync(targetCloud!, ct, histOverride: privateHistory);

            // ── Record file modifications ──────────────────────────────────────
            // After stream completes, mark which files were modified
            foreach (var filePath in projectFiles)
            {
                if (_fileCheckout.WasModified(filePath))
                    _fileCheckout.RecordModification(filePath, targetDisplayName);
            }
        }
        finally
        {
            // ── Check in all files (release locks) ────────────────────────────
            foreach (var filePath in projectFiles)
            {
                _fileCheckout.Checkin(filePath);
            }

            // ── Detect conflicts with other participants' modifications ────────
            DetectAndReportConflicts(targetDisplayName);

            // ── Swap busy-marker → real user message (if task produced a response) ──
            // After the stream, _sharedHistory may have grown (AI response appended).
            // Replace the busy-marker with the real user message so both question and
            // answer land in shared history together, in the right order.
            bool aiResponseAdded = _sharedHistory.Count > histCountBeforeStream;

            // Remove the busy-marker (busyIdx may no longer equal the current position if
            // concurrent normal messages were added, so search by the sentinel Sender tag).
            int markerIdx = _sharedHistory.FindIndex(
                m => m.Sender == $"__busy__{targetDisplayName}");
            if (markerIdx >= 0)
            {
                _sharedHistory.RemoveAt(markerIdx);
                // Insert the real user message where the busy-marker was.
                // If no response arrived (task failed/cancelled) we still insert it so
                // the conversation record is coherent.
                _sharedHistory.Insert(markerIdx,
                    new CloudAIMessage("user", userText, "User"));
            }

            if (!aiResponseAdded)
            {
                // Task failed or was cancelled before generating anything useful.
                // Remove the stub user message we just inserted so history stays clean.
                int stubIdx = _sharedHistory.FindLastIndex(
                    m => m.Role == "user" && m.Sender == "User" && m.Content == userText);
                if (stubIdx >= 0) _sharedHistory.RemoveAt(stubIdx);
            }

            _privateTaskCts.Remove(cts);
            cts.Dispose();
        }
    }

    /// <summary>Cancels all running parallel private tasks (called by global Stop/Clear).</summary>
    private void CancelAllPrivateTasks()
    {
        foreach (var cts in _privateTaskCts.ToList())
        {
            try { cts.Cancel(); } catch { /* already disposed */ }
        }
        // The DoPrivateTaskAsync finally-blocks will remove them from the list when they finish.
    }

    // ── File checkout monitoring (smart stale-checkout detection) ────────────

    private void StartCheckoutMonitor()
    {
        _checkoutMonitorCts = new CancellationTokenSource();
        _ = Task.Run(async () => await CheckoutMonitorLoopAsync(_checkoutMonitorCts.Token));
    }

    private void StopCheckoutMonitor()
    {
        _checkoutMonitorCts?.Cancel();
        _checkoutMonitorCts?.Dispose();
        _checkoutMonitorCts = null;
    }

    /// <summary>
    /// Runs every minute to detect stale file checkouts.
    /// Smart behaviour:
    ///   • If the participant is currently BUSY (generating) → silently extend the
    ///     lock by resetting its timer so they won't be interrupted.
    ///   • If the participant is IDLE → post a visible @mention asking them to
    ///     check in. Their next response is parsed by ProcessCheckinResponse().
    /// Timeout threshold is read from settings (FileCheckoutTimeoutMinutes).
    /// </summary>
    private async Task CheckoutMonitorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Check every minute so we react quickly to short timeout settings
                await Task.Delay(TimeSpan.FromMinutes(1), ct);

                if (ct.IsCancellationRequested)
                    break;

                // Read configurable timeout from settings
                var timeoutMinutes = Services.SettingsService.Load().FileCheckoutTimeoutMinutes;
                var staleThreshold = TimeSpan.FromMinutes(Math.Max(1, timeoutMinutes));

                // Find all checkouts that have been held longer than the threshold
                var staleCheckouts = _fileCheckout.GetStaleCheckouts(staleThreshold).ToList();

                foreach (var (filePath, checkout) in staleCheckouts)
                {
                    // Always skip user checkouts — humans have no reminder loop
                    if ((bool)Dispatcher.Invoke(() => IsUserName(checkout.ParticipantName)))
                        continue;

                    var participantName = checkout.ParticipantName;
                    var fileName        = System.IO.Path.GetFileName(filePath);
                    var duration        = _fileCheckout.GetCheckoutDuration(filePath);

                    // ── Busy check ────────────────────────────────────────
                    bool isBusy = (bool)Dispatcher.Invoke(() => IsParticipantBusy(participantName));

                    if (isBusy)
                    {
                        // Participant is actively generating — extend silently for 1 minute
                        // by resetting the checkout time to now.
                        _fileCheckout.RefreshCheckout(filePath);
                        // No message shown; the participant won't even notice.
                        continue;
                    }

                    // ── Participant is idle — ask if they still need the file ──
                    // Skip if we already asked about this file and are waiting for a reply
                    bool alreadyAsked;
                    lock (_pendingCheckinFiles)
                        alreadyAsked = _pendingCheckinFiles.TryGetValue(participantName, out var set)
                                    && set.Contains(filePath);

                    if (alreadyAsked)
                        continue;   // still waiting for their answer from last cycle

                    // Track that we've asked; their next response will be checked
                    lock (_pendingCheckinFiles)
                    {
                        if (!_pendingCheckinFiles.TryGetValue(participantName, out var fileSet))
                            _pendingCheckinFiles[participantName] = fileSet = new HashSet<string>();
                        fileSet.Add(filePath);
                    }

                    Dispatcher.Invoke(() =>
                    {
                        var msg = Properties.Loc.S("FileCheckout_AskCheckin")
                            .Replace("{participant}", participantName)
                            .Replace("{file}",        fileName)
                            .Replace("{duration}",    $"{duration.TotalMinutes:F0}");
                        AddSystemMessage(msg);
                    });
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Don't crash the monitor on unexpected errors
            }
        }
    }

    /// <summary>
    /// Returns true if the named participant is currently generating a response
    /// (their status label shows the "Busy…" text).
    /// Must be called on the UI thread (or via Dispatcher.Invoke).
    /// </summary>
    private bool IsParticipantBusy(string participantName)
    {
        var busyText = Properties.Loc.S("Status_Busy");

        foreach (var ui in _ollamaParticipants)
            if (GetEffectiveName(ui) == participantName && ui.StatusLabel.Text == busyText)
                return true;

        foreach (var ui in _cloudAIParticipants)
            if (GetEffectiveName(ui) == participantName && ui.StatusLabel.Text == busyText)
                return true;

        return false;
    }

    /// <summary>
    /// Called after every visible response to check if this participant was asked
    /// to confirm a file checkin.  Parses the response text for yes/no keywords:
    /// • "yes / still / need / keep / working" → extend the checkout
    /// • "no / done / release / finished / free" → release the file
    /// • Ambiguous / no match → extend (safe default)
    /// </summary>
    private void ProcessCheckinResponse(string participantName, string responseText)
    {
        HashSet<string>? pendingFiles;
        lock (_pendingCheckinFiles)
        {
            if (!_pendingCheckinFiles.TryGetValue(participantName, out pendingFiles)
                || pendingFiles.Count == 0)
                return;   // nothing pending for this participant
        }

        var lower = responseText.ToLowerInvariant();

        // Check for explicit "no / done / release / finished / free" keywords
        bool wantRelease = lower.Contains("no,") || lower.Contains("nope")
                        || lower.StartsWith("no ")|| lower == "no"
                        || lower.Contains("done") || lower.Contains("release")
                        || lower.Contains("finished") || lower.Contains("free")
                        || lower.Contains("fertig") || lower.Contains("freigeben");

        // Check for explicit "yes / still / need / keep / working" keywords
        bool wantKeep = !wantRelease
                     && (lower.Contains("yes") || lower.Contains("still")
                      || lower.Contains("need") || lower.Contains("keep")
                      || lower.Contains("working") || lower.Contains("ja")
                      || lower.Contains("noch") || lower.Contains("brauche"));

        // Default: keep (safe — don't discard someone else's work accidentally)
        bool release = wantRelease && !wantKeep;

        lock (_pendingCheckinFiles)
        {
            if (!_pendingCheckinFiles.TryGetValue(participantName, out var files))
                return;

            var filesToProcess = files.ToList();
            files.Clear();

            foreach (var filePath in filesToProcess)
            {
                if (release)
                {
                    _fileCheckout.Checkin(filePath);
                    var relMsg = Properties.Loc.S("FileCheckout_Released")
                        .Replace("{participant}", participantName)
                        .Replace("{file}", System.IO.Path.GetFileName(filePath));
                    AddSystemMessage(relMsg);
                }
                else
                {
                    _fileCheckout.RefreshCheckout(filePath);
                    var extMsg = Properties.Loc.S("FileCheckout_Extended")
                        .Replace("{participant}", participantName)
                        .Replace("{file}", System.IO.Path.GetFileName(filePath));
                    AddSystemMessage(extMsg);
                }
            }
        }
    }

    private bool IsUserName(string participantName)
        => participantName == _userName;

    // ── File access helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Returns the appropriate checkout mode for a file, based on its extension and whether
    /// the participant has write access to the project.
    ///
    /// Two locking tiers:
    ///
    /// Documentation files (.md, .txt, .rst, .html, .htm, .csv):
    ///   • Read-only participant  → <see cref="FileCheckoutRegistry.CheckoutMode.ReadOnly"/>
    ///     Multiple read-only participants can hold the file simultaneously; no writes possible.
    ///   • Write-capable participant → <see cref="FileCheckoutRegistry.CheckoutMode.ReadWrite"/>
    ///     Exclusive: blocks all other readers AND writers while the task runs, because the
    ///     participant may modify the file.
    ///
    /// Code / config / data files (everything else):
    ///   → Always <see cref="FileCheckoutRegistry.CheckoutMode.ReadWrite"/> (exclusive).
    ///     Only one participant at a time, whether reading or writing — no parallel access
    ///     is allowed to avoid subtle corruption of structured files.
    /// </summary>
    private static FileCheckoutRegistry.CheckoutMode GetCheckoutModeForFile(
        string filePath, bool participantCanWrite)
    {
        var ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
        bool isDoc = ext is ".md"   or ".txt"  or ".rst"  or ".html" or ".htm" or ".csv"
                       or ".docx" or ".xlsx" or ".pptx"                               // OOXML
                       or ".odt"  or ".ods"  or ".odp"                                // ODF
                       or ".pdf";                                                      // PDF

        if (!isDoc)
            return FileCheckoutRegistry.CheckoutMode.ReadWrite;  // code/config: always exclusive

        // Documentation file: ReadOnly for read-only participants (parallel reads OK);
        // ReadWrite for write-capable participants (they might modify the doc).
        return participantCanWrite
            ? FileCheckoutRegistry.CheckoutMode.ReadWrite
            : FileCheckoutRegistry.CheckoutMode.ReadOnly;
    }

    /// <summary>
    /// Returns all readable files in the current project folder (recursively).
    /// Used to determine which files a private task might access.
    /// </summary>
    private List<string> GetProjectFiles()
    {
        var files = new List<string>();

        if (string.IsNullOrEmpty(_currentProjectFolder))
            return files;

        try
        {
            var projectDir = new System.IO.DirectoryInfo(_currentProjectFolder);
            var allFiles = projectDir.GetFiles("*", System.IO.SearchOption.AllDirectories);

            // Exclude hidden files and common non-data files
            var excluded = new[] { ".json.tmp", ".lock", ".bak" };
            foreach (var file in allFiles)
            {
                if (file.Attributes.HasFlag(System.IO.FileAttributes.Hidden))
                    continue;
                if (excluded.Any(ext => file.Name.EndsWith(ext)))
                    continue;

                files.Add(file.FullName);
            }
        }
        catch
        {
            // If we can't enumerate, return empty list (task proceeds without file locking)
        }

        return files;
    }

    /// <summary>
    /// Detects and reports conflicts when a participant completes a task.
    /// Shows warnings if other participants modified the same files.
    /// </summary>
    private void DetectAndReportConflicts(string completingParticipantName)
    {
        var conflicts = _fileCheckout.DetectConflicts();

        foreach (var (filePath, participants) in conflicts)
        {
            // Only report if the completing participant is one of the conflicting ones
            if (!participants.Contains(completingParticipantName))
                continue;

            var fileName = System.IO.Path.GetFileName(filePath);
            var otherParticipants = participants.Where(p => p != completingParticipantName).ToList();

            if (otherParticipants.Count == 0)
                continue;

            if (otherParticipants.Count == 1 && IsUserName(otherParticipants[0]))
            {
                // User and AI both edited same file
                var msg = Properties.Loc.S("FileCheckout_Conflict_User")
                    .Replace("{participant}", completingParticipantName)
                    .Replace("{file}", fileName);
                AddSystemMessage(msg);
            }
            else if (otherParticipants.All(p => !IsUserName(p)))
            {
                // Multiple AIs edited same file
                var otherNames = string.Join(" & ", otherParticipants);
                var msg = Properties.Loc.S("FileCheckout_Conflict_AI")
                    .Replace("{participant1}", otherNames)
                    .Replace("{participant2}", completingParticipantName)
                    .Replace("{file}", fileName);
                AddSystemMessage(msg);
            }
        }
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
        var mode = _projectSettings?.OrchestrationMode ?? OrchestrationMode.AllRespond;

        // In CoordinatorFirst / Auto / Only modes the mode runner handles reasoner activation.
        // In all other modes, reasoners are completely passive - they only participate when
        // explicitly mentioned by name in the conversation (no auto-triggering).
        bool suppressReasoners = mode is not OrchestrationMode.CoordinatorFirst
                                      and not OrchestrationMode.CoordinatorAuto
                                      and not OrchestrationMode.CoordinatorOnly;

        var activeOllamas  = _ollamaParticipants
            .Where(ui => ui.Data.Enabled && ui.Data.IsOnline == true &&
                         IsParticipantActiveInProject(ui) &&
                         !(suppressReasoners && IsReasoner(ui)))
            .ToList();
        var activeCloudAIs = _cloudAIParticipants
            .Where(ui => ui.Data.Enabled && ui.Data.IsOnline == true &&
                         IsParticipantActiveInProject(ui) &&
                         !(suppressReasoners && IsReasoner(ui)))
            .ToList();

        if (activeOllamas.Count == 0 && activeCloudAIs.Count == 0)
        {
            AddSystemMessage("⚠  No active AI participant is available.");
            return;
        }

        AIRespondButton.IsEnabled = false;
        SendButton.IsEnabled      = false;
        _streamCts = new CancellationTokenSource();
        var ct = _streamCts.Token;

        // Multi-round dialogue: toggle is the master switch.
        // When ON:  use _aiDialogueMaxTurns (global setting, 3-100), but honour a higher
        //           project MaxDialogDepth if one is explicitly configured.
        // When OFF: single round regardless of any project setting.
        bool multiParticipant = (activeOllamas.Count + activeCloudAIs.Count) > 1;
        var maxRounds = _aiDialogueEnabled && multiParticipant
            ? Math.Max(_aiDialogueMaxTurns, _maxDialogDepth)
            : 1;

        try
        {
            switch (mode)
            {
                case OrchestrationMode.CoordinatorFirst:
                case OrchestrationMode.CoordinatorAuto:   // after team init, behaves like CoordinatorFirst
                    await RunCoordinatorFirstModeAsync(activeOllamas, activeCloudAIs, ct);
                    break;
                case OrchestrationMode.CoordinatorSummarizes:
                    await RunCoordinatorSummarizesModeAsync(activeOllamas, activeCloudAIs, ct);
                    break;
                case OrchestrationMode.CoordinatorOnly:
                    await RunCoordinatorOnlyModeAsync(activeOllamas, activeCloudAIs, ct);
                    break;
                default:
                    await RunAllRespondModeAsync(activeOllamas, activeCloudAIs, ct, maxRounds);
                    break;
            }
        }
        finally
        {
            _streamCts?.Dispose();
            _streamCts = null;
            AIRespondButton.IsEnabled = true;
            SendButton.IsEnabled      = true;
        }

        // History compression - after all streams finish, outside the CTS scope
        if (_currentProjectFolder is not null && !ct.IsCancellationRequested)
            await MaybeCompressHistoryAsync(CancellationToken.None);
    }

    // ── Orchestration mode runners ─────────────────────────────────────────

    /// <summary>
    /// Hint used in follow-up rounds when inside a project.
    /// Instructs participants to contribute only if they have something genuinely new,
    /// and to output PASS otherwise - keeps structured project dialogue clean.
    /// </summary>
    private const string FollowUpRoundHint =
        "The previous participants have already responded above. " +
        "Only continue this conversation if you genuinely have something new to contribute: " +
        "a different perspective, a meaningful correction, a direct response to something just " +
        "said, or important information that has not been covered yet. " +
        "If you have nothing meaningful to add right now, output exactly the word PASS " +
        "and nothing else.";

    /// <summary>
    /// Hint used in follow-up rounds in free-chat (non-project) dialogue mode.
    /// Encourages natural, conversational back-and-forth without structured round markers.
    /// </summary>
    private const string LiveDialogueHint =
        "You are in a live group conversation. Read what the other participants just wrote " +
        "and react naturally - agree or push back on a specific point, ask a follow-up question, " +
        "share a complementary angle, make a joke, or build directly on what someone just said. " +
        "When you are addressing a specific participant, use their name. " +
        "Keep your reply conversational and concise - this is a chat, not an essay. " +
        "If you genuinely have nothing new to add right now, output exactly the word PASS and nothing else.";

    private async Task RunAllRespondModeAsync(
        List<OllamaParticipantUI>   activeOllamas,
        List<CloudAIParticipantUI>  activeCloudAIs,
        CancellationToken ct, int maxRounds)
    {
        // In a project: show "- Round N -" separators so the structure is visible.
        // In free chat (💬 dialogue mode): no markers - the messages flow as natural conversation.
        bool freeChat = _currentProjectFolder is null;

        for (int round = 0; round < maxRounds && !ct.IsCancellationRequested; round++)
        {
            bool isFollowUp = round > 0;

            if (isFollowUp)
            {
                if (_sharedHistory.Count == 0 || _sharedHistory.Last().Role != "assistant")
                    break;

                // Project mode: add a round separator that can be cleaned up if nobody responds.
                // Free-chat mode: no separator - the conversation flows without interruption.
                int markerIndex = freeChat ? -1 : ChatPanel.Children.Count;
                if (!freeChat) AddSystemMessage($"- Round {round + 1} -");

                // Choose the right follow-up hint for the context
                string hint = freeChat ? LiveDialogueHint : FollowUpRoundHint;
                int responded = 0;

                foreach (var ui in activeOllamas)
                {
                    if (ct.IsCancellationRequested) break;
                    if (await RunOllamaStreamAsync(ui, ct, hint)) responded++;
                }
                foreach (var ui in activeCloudAIs)
                {
                    if (ct.IsCancellationRequested) break;
                    if (await RunCloudAIStreamAsync(ui, ct, hint)) responded++;
                }

                // Nobody had anything new to say - clean up and stop
                if (responded == 0)
                {
                    if (!freeChat && markerIndex >= 0 && markerIndex < ChatPanel.Children.Count)
                        ChatPanel.Children.RemoveAt(markerIndex);
                    break;
                }
            }
            else
            {
                // Round 0 — resolve effective chattiness (project overrides global if set).
                int chattiness = (_projectSettings?.DefaultChattiness ?? -1) >= 0
                    ? _projectSettings!.DefaultChattiness
                    : _chattinessLevel;

                // Detect whether the user addressed specific participants by name.
                var lastUserMsg      = _sharedHistory.LastOrDefault(m => m.Role == "user")?.Content ?? "";
                var addressedOllamas = activeOllamas .Where(ui => IsNamedInMessage(lastUserMsg, GetEffectiveName(ui))).ToList();
                var addressedClouds  = activeCloudAIs.Where(ui => IsNamedInMessage(lastUserMsg, GetEffectiveName(ui))).ToList();
                var otherOllamas     = activeOllamas .Except(addressedOllamas).ToList();
                var otherClouds      = activeCloudAIs.Except(addressedClouds).ToList();
                bool anyAddressed    = addressedOllamas.Count + addressedClouds.Count > 0;

                if (anyAddressed)
                {
                    var addressedNames = addressedOllamas.Select(GetEffectiveName)
                        .Concat(addressedClouds.Select(GetEffectiveName)).ToList();
                    bool   isSingle  = addressedNames.Count == 1;
                    string nameList  = isSingle ? addressedNames[0] : string.Join(", ", addressedNames);

                    // Addressed participants respond naturally first
                    foreach (var ui in addressedOllamas)
                    {
                        if (ct.IsCancellationRequested) break;
                        await RunOllamaStreamAsync(ui, ct);
                    }
                    foreach (var ui in addressedClouds)
                    {
                        if (ct.IsCancellationRequested) break;
                        await RunCloudAIStreamAsync(ui, ct);
                    }

                    // Non-addressed: hint (or none) depends on chattiness
                    var notAddressedHint = BuildNotAddressedHint(chattiness, nameList, isSingle);
                    foreach (var ui in otherOllamas)
                    {
                        if (ct.IsCancellationRequested) break;
                        await RunOllamaStreamAsync(ui, ct, notAddressedHint);
                    }
                    foreach (var ui in otherClouds)
                    {
                        if (ct.IsCancellationRequested) break;
                        await RunCloudAIStreamAsync(ui, ct, notAddressedHint);
                    }
                }
                else
                {
                    // Nobody addressed — apply quiet-mode hint if chattiness is low
                    var quietHint = BuildQuietModeHint(chattiness);
                    foreach (var ui in activeOllamas)
                    {
                        if (ct.IsCancellationRequested) break;
                        await RunOllamaStreamAsync(ui, ct, quietHint);
                    }
                    foreach (var ui in activeCloudAIs)
                    {
                        if (ct.IsCancellationRequested) break;
                        await RunCloudAIStreamAsync(ui, ct, quietHint);
                    }
                }
            }
        }
    }

    private async Task RunCoordinatorFirstModeAsync(
        List<OllamaParticipantUI>   activeOllamas,
        List<CloudAIParticipantUI>  activeCloudAIs,
        CancellationToken ct)
    {
        var (coordOllama, coordCloud) = FindCoordinatorInLists(activeOllamas, activeCloudAIs);
        if (coordOllama is null && coordCloud is null)
        {
            AddSystemMessage("⚠  CoordinatorFirst: no coordinator found - falling back to AllRespond.");
            await RunAllRespondModeAsync(activeOllamas, activeCloudAIs, ct, 1);
            return;
        }

        // Split non-coordinator participants into:
        //   • free participants (IsReasoner = false) - respond automatically after the coordinator
        //   • reasoners (IsReasoner = true)          - only respond when tagged by the coordinator
        var freeOllamas      = activeOllamas .Where(u => u != coordOllama && !IsReasoner(u)).ToList();
        var freeCloudAIs     = activeCloudAIs.Where(u => u != coordCloud  && !IsReasoner(u)).ToList();
        var reasonerOllamas  = activeOllamas .Where(u => u != coordOllama &&  IsReasoner(u)).ToList();
        var reasonerCloudAIs = activeCloudAIs.Where(u => u != coordCloud  &&  IsReasoner(u)).ToList();

        var reasonerNames = reasonerOllamas.Select(GetEffectiveName)
            .Concat(reasonerCloudAIs.Select(GetEffectiveName))
            .ToList();

        var freeCount = freeOllamas.Count + freeCloudAIs.Count;

        // Do NOT list reasoners in the coordinator hint - advertising them causes reflexive tagging.
        // The coordinator naturally decides to call them by name if it genuinely needs them.
        string coordinatorHint = freeCount > 0
            ? "You respond first in this conversation round. " +
              "After your response the other active participants will also contribute."
            : "You are the only active participant - respond directly.";

        // Coordinator goes first
        if (coordCloud is not null)
            await RunCloudAIStreamAsync(coordCloud, ct, coordinatorHint);
        else
            await RunOllamaStreamAsync(coordOllama!, ct, coordinatorHint);

        if (ct.IsCancellationRequested) return;

        // Free participants respond automatically - no coordinator tagging required
        foreach (var ui in freeOllamas)
        {
            if (ct.IsCancellationRequested) break;
            await RunOllamaStreamAsync(ui, ct);
        }
        foreach (var ui in freeCloudAIs)
        {
            if (ct.IsCancellationRequested) break;
            await RunCloudAIStreamAsync(ui, ct);
        }

        if (ct.IsCancellationRequested || reasonerNames.Count == 0) return;

        // Parse the coordinator's response for @Name mentions - if a reasoner is named, call them
        var coordResponse = _sharedHistory.LastOrDefault(m => m.Role == "assistant")?.Content ?? "";
        var taggedOllamas = reasonerOllamas
            .Where(u => IsTaggedInResponse(coordResponse, GetEffectiveName(u)))
            .ToList();
        var taggedClouds = reasonerCloudAIs
            .Where(u => IsTaggedInResponse(coordResponse, GetEffectiveName(u)))
            .ToList();

        if (taggedOllamas.Count == 0 && taggedClouds.Count == 0) return;

        // Tell each reasoner it was specifically delegated to - helps it stay focused
        const string reasonerDelegationHint =
            "The Coordinator has specifically delegated a task to you. " +
            "Respond only to that delegated task or question.";

        AddSystemMessage("- Delegated to Reasoners -");
        foreach (var ui in taggedOllamas)
        {
            if (ct.IsCancellationRequested) break;
            await RunOllamaStreamAsync(ui, ct, reasonerDelegationHint, skipLatestUserMessage: true);
        }
        foreach (var ui in taggedClouds)
        {
            if (ct.IsCancellationRequested) break;
            await RunCloudAIStreamAsync(ui, ct, reasonerDelegationHint, skipLatestUserMessage: true);
        }
    }

    private async Task RunCoordinatorSummarizesModeAsync(
        List<OllamaParticipantUI>   activeOllamas,
        List<CloudAIParticipantUI>  activeCloudAIs,
        CancellationToken ct)
    {
        var (coordOllama, coordCloud) = FindCoordinatorInLists(activeOllamas, activeCloudAIs);

        // All non-coordinator participants respond first
        foreach (var ui in activeOllamas.Where(u => u != coordOllama))
        {
            if (ct.IsCancellationRequested) return;
            await RunOllamaStreamAsync(ui, ct);
        }
        foreach (var ui in activeCloudAIs.Where(u => u != coordCloud))
        {
            if (ct.IsCancellationRequested) return;
            await RunCloudAIStreamAsync(ui, ct);
        }

        if (ct.IsCancellationRequested || (coordOllama is null && coordCloud is null)) return;

        // Coordinator synthesizes all responses above
        AddSystemMessage("- Coordinator synthesizing -");
        const string synthesisHint =
            "All other participants have now given their responses above. " +
            "Please write a final synthesizing response: draw together their key points, " +
            "highlight agreements and any meaningful differences, and add your own concluding assessment.";

        if (coordCloud is not null)
            await RunCloudAIStreamAsync(coordCloud, ct, synthesisHint);
        else
            await RunOllamaStreamAsync(coordOllama!, ct, synthesisHint);
    }

    /// <summary>
    /// Coordinator-Only mode: the user only sees the Coordinator's final synthesis.
    /// All intermediate work - Coordinator deliberation and Reasoner responses - is hidden
    /// from the chat. Small status indicators track which participant is active.
    /// <para>Flow: (1) Coordinator deliberates hidden → (2) tagged Reasoners work hidden →
    /// (3) Coordinator synthesizes visible.</para>
    /// </summary>
    private async Task RunCoordinatorOnlyModeAsync(
        List<OllamaParticipantUI>  activeOllamas,
        List<CloudAIParticipantUI> activeCloudAIs,
        CancellationToken ct)
    {
        var (coordOllama, coordCloud) = FindCoordinatorInLists(activeOllamas, activeCloudAIs);
        if (coordOllama is null && coordCloud is null)
        {
            AddSystemMessage("⚠  Coordinator-Only: no coordinator found - falling back to AllRespond.");
            await RunAllRespondModeAsync(activeOllamas, activeCloudAIs, ct, 1);
            return;
        }

        var coordName   = coordOllama is not null ? GetEffectiveName(coordOllama) : GetEffectiveName(coordCloud!);
        var coordAvatar = coordOllama?.Data.AvatarLabel ?? coordCloud!.Data.AvatarLabel;
        var coordColor  = coordOllama?.Data.ColorKey    ?? coordCloud!.Data.ColorKey;

        var reasonerOllamas  = activeOllamas .Where(u => u != coordOllama && IsReasoner(u)).ToList();
        var reasonerCloudAIs = activeCloudAIs.Where(u => u != coordCloud  && IsReasoner(u)).ToList();

        // ── Step 1: Coordinator deliberates (hidden) ───────────────────
        // The coordinator analyzes the request and decides whether / what to delegate.
        var (coordIndicator, updateCoord) = AddActivityIndicator(coordName, coordAvatar, coordColor);

        const string coordDeliberateHint =
            "COORDINATOR-ONLY MODE - INTERNAL DELIBERATION (this message is hidden from the user).\n" +
            "Analyze the request. If you can answer it fully yourself, write your analysis concisely.\n" +
            "If you need Reasoner input, mention the Reasoner(s) by name as you normally would.\n" +
            "Be concise and technical - no formatting needed here. Do NOT output PASS.";

        if (coordCloud is not null)
            await RunCloudAIStreamAsync(coordCloud, ct, coordDeliberateHint, hidden: true);
        else
            await RunOllamaStreamAsync(coordOllama!, ct, coordDeliberateHint, hidden: true);

        if (ct.IsCancellationRequested) { updateCoord("✗ cancelled"); return; }
        updateCoord($"✓  [{coordAvatar}] {coordName}  - analysis done");

        // ── Step 2: Run tagged Reasoners (hidden) ──────────────────────
        var coordFirstResponse = _sharedHistory.LastOrDefault(m => m.Role == "assistant")?.Content ?? "";

        var taggedOllamas = reasonerOllamas
            .Where(u => IsTaggedInResponse(coordFirstResponse, GetEffectiveName(u)))
            .ToList();
        var taggedClouds = reasonerCloudAIs
            .Where(u => IsTaggedInResponse(coordFirstResponse, GetEffectiveName(u)))
            .ToList();

        if (taggedOllamas.Count > 0 || taggedClouds.Count > 0)
        {
            const string reasonerHiddenHint =
                "COORDINATOR-ONLY MODE - your response is INTERNAL (not shown to user).\n" +
                "Deliver exactly what the Coordinator delegated. Be concise and technical. " +
                "No preamble, no formatting - just the result. Do NOT output PASS.";

            foreach (var ui in taggedOllamas)
            {
                if (ct.IsCancellationRequested) break;
                var (ind, upd) = AddActivityIndicator(
                    GetEffectiveName(ui), ui.Data.AvatarLabel, ui.Data.ColorKey);
                await RunOllamaStreamAsync(ui, ct, reasonerHiddenHint,
                    skipLatestUserMessage: true, hidden: true);
                upd(null); // "done"
            }
            foreach (var ui in taggedClouds)
            {
                if (ct.IsCancellationRequested) break;
                var (ind, upd) = AddActivityIndicator(
                    GetEffectiveName(ui), ui.Data.AvatarLabel, ui.Data.ColorKey);
                await RunCloudAIStreamAsync(ui, ct, reasonerHiddenHint,
                    skipLatestUserMessage: true, hidden: true);
                upd(null);
            }
        }

        if (ct.IsCancellationRequested) return;

        // ── Step 3: Coordinator synthesizes (visible to user) ──────────
        // All hidden work is now in _sharedHistory; the Coordinator sees it as context.
        const string synthHint =
            "Internal analysis complete. Write your final response DIRECTLY to the user now. " +
            "Synthesize all gathered insights into a clear, natural answer. " +
            "Do NOT mention 'internal mode', 'hidden deliberation', or the coordination process - " +
            "respond as if you arrived at the answer through your own reasoning.";

        if (coordCloud is not null)
            await RunCloudAIStreamAsync(coordCloud, ct, synthHint);
        else
            await RunOllamaStreamAsync(coordOllama!, ct, synthHint);
    }

    // ── ParticipantSuperPowers ─────────────────────────────────────────────────

    /// <summary>
    /// Sorted fingerprint of the currently enabled active participants.
    /// Used to detect team composition changes between sessions.
    /// </summary>
    private string GetParticipantFingerprint()
    {
        var keys = new List<string>();
        foreach (var ui in _ollamaParticipants.Where(u => u.Data.Enabled))
            keys.Add($"ollama:{ui.Data.Service.CurrentModel.ToLowerInvariant()}");
        foreach (var ui in _cloudAIParticipants.Where(u => u.Data.Enabled))
            keys.Add($"{ui.Data.Service.ProviderName.ToLowerInvariant()}:{ui.Data.Service.CurrentModel.ToLowerInvariant()}");
        keys.Sort(StringComparer.OrdinalIgnoreCase);
        return string.Join("|", keys);
    }

    /// <summary>
    /// Returns true if the model name matches known reasoning / thinking model patterns
    /// (o1, o3, DeepSeek-R1, Gemini-Thinking, QwQ, etc.).
    /// </summary>
    private static bool IsReasonerModel(string model)
    {
        var m = model.ToLowerInvariant();
        return Regex.IsMatch(m, @"\bo[13](-mini|-preview|-pro)?\b") ||
               m.Contains("deepseek-r1") || m.Contains("-r1-") ||
               m.Contains("thinking") ||
               m.Contains("qwq") ||
               m.Contains("reasoner");
    }

    /// <summary>Estimates the cost tier of a participant (free / low / medium / high).</summary>
    private static string GetCostTier(string provider, string model)
    {
        if (string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase))
            return "free (local)";
        var m = model.ToLowerInvariant();
        if (m.Contains("opus")  || m.Contains("ultra") ||
            (m.Contains("gpt-4o") && !m.Contains("mini")) ||
            (m.Contains("o3")     && !m.Contains("mini")))
            return "high";
        if (m.Contains("haiku") || m.Contains("flash") || m.Contains("mini") ||
            m.Contains("nano")  || m.Contains("8b")    || m.Contains("7b"))
            return "low";
        return "medium";
    }

    /// <summary>Path to this project's legacy SuperPowers file (kept only for migration).</summary>
    private string? GetSuperPowersPath() =>
        _currentProjectFolder is null ? null
            : SysIO.Path.Combine(_currentProjectFolder, "PROJECTSETTINGS", "ParticipantSuperPowers.xaml");

    /// <summary>
    /// Loads the role plan from project.json into a flat display-name→instruction dictionary.
    /// Returns null when the project has no plan yet.
    /// </summary>
    private Dictionary<string, string>? LoadSuperRoles()
    {
        var plan = _currentProject?.ParticipantRolePlan;
        return plan is { Count: > 0 } ? plan : null;
    }

    /// <summary>
    /// Returns the coordinator-written role instruction for <paramref name="displayName"/>,
    /// or null if no plan exists or Full Manual Mode is active.
    /// The cache is loaded lazily and cleared on project open/close.
    /// </summary>
    private string? GetSuperRoleInstruction(string displayName)
    {
        if (_projectSettings?.OrchestrationMode == OrchestrationMode.AllRespond) return null;
        _superRoles ??= LoadSuperRoles();
        return _superRoles is not null && _superRoles.TryGetValue(displayName, out var instruction)
            ? instruction : null;
    }

    /// <summary>
    /// Builds the team capability profile from ParticipantConfig data (SelfDescription,
    /// Likes, Dislikes) for injection into the coordinator's system prompt context.
    /// Returns null when no active participants are found.
    /// </summary>
    private string? LoadSuperPowersForContext()
    {
        var activeOllamas  = _ollamaParticipants
            .Where(u => u.Data.Enabled && IsParticipantActiveInProject(u)).ToList();
        var activeCloudAIs = _cloudAIParticipants
            .Where(u => u.Data.Enabled && IsParticipantActiveInProject(u)).ToList();

        if (activeOllamas.Count + activeCloudAIs.Count == 0) return null;

        var globalSettings = SettingsService.Load();
        var lines = new List<string>();

        void AddEntry(string name, string provider, string model)
        {
            var pc   = globalSettings.Participants.FirstOrDefault(p =>
                string.Equals(p.Type,  provider, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.Model, model,    StringComparison.OrdinalIgnoreCase));
            var role = _projectSettings?.Get(provider, model);

            var roleStr  = role?.IsCoordinator == true ? "Coordinator"
                         : role?.IsReasoner    == true ? "Reasoner"
                         : "Participant";
            var cost     = GetCostTier(provider, model);
            var isRModel = IsReasonerModel(model);
            var priority = role?.ReasonerPriority ?? 5;

            var meta = new System.Text.StringBuilder($"{name} [{roleStr}, cost:{cost}");
            if (isRModel)                  meta.Append($", reasoner(p{priority})");
            if (role?.IsCritic     == true) meta.Append(", CR");
            if (role?.IsPlanner    == true) meta.Append(", PL");
            if (role?.IsResearcher == true) meta.Append(", RS");
            meta.Append(']');
            lines.Add(meta.ToString());

            if (pc is not null)
            {
                if (!string.IsNullOrWhiteSpace(pc.SelfDescription))
                    lines.Add($"  {pc.SelfDescription}");
                if (!string.IsNullOrWhiteSpace(pc.Likes))
                    lines.Add($"  + Likes: {pc.Likes}");
                if (!string.IsNullOrWhiteSpace(pc.Dislikes))
                    lines.Add($"  - Dislikes: {pc.Dislikes}");
            }
        }

        foreach (var ui in activeOllamas)
            AddEntry(GetEffectiveName(ui), "Ollama", ui.Data.Service.CurrentModel);
        foreach (var ui in activeCloudAIs)
            AddEntry(GetEffectiveName(ui), ui.Data.Service.ProviderName, ui.Data.Service.CurrentModel);

        return lines.Count > 0 ? string.Join("\n", lines) : null;
    }

    // ── One-time migration: ParticipantSuperRoles.xml → project.json ────────

    /// <summary>
    /// If the project still has legacy SuperRoles/SuperPowers files but no ParticipantRolePlan
    /// in project.json, migrates the role data into project.json and deletes the old files.
    /// </summary>
    private void TryMigrateLegacySuperRoles()
    {
        if (_currentProject is null || _currentProjectFolder is null) return;
        if (_currentProject.ParticipantRolePlan is { Count: > 0 })
        {
            // Plan already in project.json — just clean up any leftover legacy files silently
            DeleteLegacySuperPowersFiles();
            return;
        }

        var xmlPath = SysIO.Path.Combine(_currentProjectFolder, "PROJECTSETTINGS",
                                         "ParticipantSuperRoles.xml");
        if (!SysIO.File.Exists(xmlPath))
        {
            // No roles XML — still clean up orphaned SuperPowers.xaml if present
            DeleteLegacySuperPowersFiles();
            return;
        }

        try
        {
            var doc      = System.Xml.Linq.XDocument.Load(xmlPath);
            var migrated = doc.Root?
                .Elements("Role")
                .Where(e => e.Attribute("name")?.Value is { Length: > 0 })
                .ToDictionary(
                    e => e.Attribute("name")!.Value,
                    e => e.Value.Trim(),
                    StringComparer.OrdinalIgnoreCase);

            if (migrated is { Count: > 0 })
            {
                _currentProject.ParticipantRolePlan = new Dictionary<string, string>(migrated);

                // Grab fingerprint from SuperPowers.xaml before deleting it
                var spPath = GetSuperPowersPath();
                if (spPath is not null && SysIO.File.Exists(spPath))
                {
                    try
                    {
                        _currentProject.ParticipantFingerprint =
                            System.Xml.Linq.XDocument.Load(spPath).Root?.Attribute("Fingerprint")?.Value;
                    }
                    catch { }
                }

                ProjectService.SaveProject(_currentProjectFolder, _currentProject);
                _superRoles = null;

                // Delete both legacy files now that the data is safely in project.json
                DeleteLegacySuperPowersFiles();

                AddSystemMessage("🔄  Migrated participant role plan to project settings — legacy files removed.");
            }
        }
        catch { /* best-effort migration */ }
    }

    /// <summary>
    /// Deletes ParticipantSuperRoles.xml and ParticipantSuperPowers.xaml from PROJECTSETTINGS/
    /// if they exist. Silent — never throws.
    /// </summary>
    private void DeleteLegacySuperPowersFiles()
    {
        if (_currentProjectFolder is null) return;
        var settingsDir = SysIO.Path.Combine(_currentProjectFolder, "PROJECTSETTINGS");
        foreach (var legacy in new[]
            { "ParticipantSuperRoles.xml", "ParticipantSuperPowers.xaml" })
        {
            var path = SysIO.Path.Combine(settingsDir, legacy);
            try { if (SysIO.File.Exists(path)) SysIO.File.Delete(path); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Triggers <see cref="TriggerSuperPowersInterviewAsync"/> only when the team has changed
    /// since the role plan was last written, or when no plan exists yet.
    /// No-op if no coordinator is configured or Full Manual Mode is active.
    /// </summary>
    private async Task CheckAndTriggerSuperPowersAsync()
    {
        if (_projectSettings is null || _currentProjectFolder is null) return;
        if (_projectSettings.OrchestrationMode == OrchestrationMode.AllRespond) return;
        if (!HasCoordinatorRole()) return;

        // Migrate legacy XML on first open (idempotent after the first run)
        TryMigrateLegacySuperRoles();

        var currentFp  = GetParticipantFingerprint();
        var fpMatch    = currentFp == _currentProject?.ParticipantFingerprint;
        var planExists = _currentProject?.ParticipantRolePlan is { Count: > 0 };

        // Re-run if team composition changed OR no role plan has been written yet
        if (fpMatch && planExists) return;

        await TriggerSuperPowersInterviewAsync(currentFp);
    }

    /// <summary>
    /// Reads ParticipantConfig capability data for all active participants, asks any
    /// participants who are missing their self-description visibly in chat, then asks
    /// the coordinator to write a project-specific role plan saved into project.json.
    /// </summary>
    private async Task TriggerSuperPowersInterviewAsync(string fingerprint)
    {
        if (_projectSettings is null || _currentProjectFolder is null || _currentProject is null) return;
        if (_streamCts is not null) return;

        var activeOllamas  = _ollamaParticipants
            .Where(ui => ui.Data.Enabled && IsParticipantActiveInProject(ui)).ToList();
        var activeCloudAIs = _cloudAIParticipants
            .Where(ui => ui.Data.Enabled && IsParticipantActiveInProject(ui)).ToList();

        if (activeOllamas.Count + activeCloudAIs.Count == 0) return;

        var (coordOllama, coordCloud) = FindCoordinatorInLists(activeOllamas, activeCloudAIs);
        if (coordOllama is null && coordCloud is null)
        {
            AddSystemMessage("⚠  Coordinator role is configured but no active coordinator participant " +
                             "was found — role plan skipped. Try reopening the project.");
            return;
        }

        AIRespondButton.IsEnabled = false;
        SendButton.IsEnabled      = false;
        _streamCts = new CancellationTokenSource();
        var ct = _streamCts.Token;

        try
        {
            var globalSettings = SettingsService.Load();

            // ── Phase 1: ask participants who lack capability data ─────────────
            // "Missing" = SelfDescription is empty.  Likes/Dislikes are gathered alongside.
            var needsIntro = new List<(string Name, string Provider, string Model,
                                       OllamaParticipantUI? Ollama, CloudAIParticipantUI? Cloud)>();

            foreach (var ui in activeOllamas)
            {
                var model = ui.Data.Service.CurrentModel;
                var pc = globalSettings.Participants.FirstOrDefault(p =>
                    string.Equals(p.Type, "Ollama", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase));
                if (pc is null || string.IsNullOrWhiteSpace(pc.SelfDescription))
                    needsIntro.Add((GetEffectiveName(ui), "Ollama", model, ui, null));
            }
            foreach (var ui in activeCloudAIs)
            {
                var provider = ui.Data.Service.ProviderName;
                var model    = ui.Data.Service.CurrentModel;
                var pc = globalSettings.Participants.FirstOrDefault(p =>
                    string.Equals(p.Type,  provider, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(p.Model, model,    StringComparison.OrdinalIgnoreCase));
                if (pc is null || string.IsNullOrWhiteSpace(pc.SelfDescription))
                    needsIntro.Add((GetEffectiveName(ui), provider, model, null, ui));
            }

            if (needsIntro.Count > 0)
            {
                AddSystemMessage($"👋  Getting to know {needsIntro.Count} team member(s) — " +
                                 "asking each to introduce themselves...");

                // Inject a minimal context message so each participant understands the ask.
                _sharedHistory.Add(new CloudAIMessage("user",
                    "[TEAM SETUP] We are about to start a project together. " +
                    "Each participant without a capability profile will now briefly introduce themselves."));

                const string introPrompt =
                    "[TEAM SETUP] Please introduce yourself to the project team. " +
                    "In 2–3 sentences describe: what you are best at, what kind of work you enjoy most, " +
                    "and any notable limitations the team should be aware of when assigning tasks to you.";

                foreach (var (name, provider, model, ollamaUi, cloudUi) in needsIntro)
                {
                    if (ct.IsCancellationRequested) break;

                    var before = _sharedHistory.Count;
                    if (ollamaUi is not null)
                        await RunOllamaStreamAsync(ollamaUi, ct, introPrompt, hidden: false);
                    else if (cloudUi is not null)
                        await RunCloudAIStreamAsync(cloudUi, ct, introPrompt, hidden: false);

                    // Parse and save the response back to ParticipantConfig
                    if (_sharedHistory.Count > before)
                    {
                        var answer = _sharedHistory.Last().Content.Trim();
                        var s = SettingsService.Load();
                        var target = s.Participants.FirstOrDefault(p =>
                            string.Equals(p.Type,  provider, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(p.Model, model,    StringComparison.OrdinalIgnoreCase));
                        if (target is not null)
                        {
                            // Store the intro as SelfDescription; extract Likes/Dislikes sentences
                            if (string.IsNullOrWhiteSpace(target.SelfDescription))
                                target.SelfDescription = answer;

                            // Simple heuristic: if the answer has 2+ sentences, treat the
                            // second as Likes and the last as Dislikes/limitations.
                            var sentences = answer.Split(['.', '!', '?'],
                                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .Where(s2 => s2.Length > 10).ToArray();
                            if (sentences.Length >= 2 && string.IsNullOrWhiteSpace(target.Likes))
                                target.Likes = sentences[1].Trim();
                            if (sentences.Length >= 3 && string.IsNullOrWhiteSpace(target.Dislikes))
                                target.Dislikes = sentences[^1].Trim();

                            SettingsService.Save(s);
                        }
                    }
                }

                // Reload updated settings so the capability summary is fresh
                globalSettings = SettingsService.Load();
            }

            if (ct.IsCancellationRequested) return;

            // ── Phase 2: build capability summary from ParticipantConfig ──────
            // LoadSuperPowersForContext() now reads directly from ParticipantConfig
            var capabilitySummary = LoadSuperPowersForContext() ?? "(no capability data available)";

            // ── Phase 3: coordinator writes the role plan ─────────────────────
            var roleSummary = new System.Text.StringBuilder("Current role assignments:\n");
            foreach (var ui in activeOllamas)
            {
                var r = GetRoleForParticipant(ui);
                roleSummary.Append($"  • {GetEffectiveName(ui)}: {BuildRoleDesc(r)}\n");
            }
            foreach (var ui in activeCloudAIs)
            {
                var r = GetRoleForParticipant(ui);
                roleSummary.Append($"  • {GetEffectiveName(ui)}: {BuildRoleDesc(r)}\n");
            }

            var allNames = activeOllamas.Select(GetEffectiveName)
                .Concat(activeCloudAIs.Select(GetEffectiveName)).ToList();
            var participantNameList = string.Join(", ", allNames);

            var nextStepHint = new System.Text.StringBuilder();
            bool needsRoadmap = _currentProjectType?.HasRoadmap == true
                                && !(_projectSettings?.RoadmapInitialized == true)
                                && (_currentRoadmap is null || _currentRoadmap.Milestones.Count == 0);
            bool needsWorldBuilding = _currentProjectType?.HasWorldBuilding == true;
            var  worldFolders       = _currentProjectType?.GetWorldFolderList() ?? [];

            if (needsRoadmap)
                nextStepHint.AppendLine("• No roadmap has been built yet — suggesting building one together would be the ideal next step.");
            if (needsWorldBuilding && worldFolders.Length > 0)
                nextStepHint.AppendLine($"• This project type uses world-building folders ({string.Join(", ", worldFolders)}) — suggest creating them before writing content.");
            if (nextStepHint.Length == 0)
                nextStepHint.AppendLine("• The project setup appears complete — ask the user what they would like to work on next.");

            _sharedHistory.Add(new CloudAIMessage("user",
                "The team capability data has been gathered. Here is what we know:\n\n" +
                "## Team capability profile\n" +
                capabilitySummary + "\n\n" +
                roleSummary.ToString() +
                "\nPlease do the following:\n" +
                "1. Give the user a concise overview of the team's strengths and your routing plan, " +
                "noting any cost/performance trade-offs.\n" +
                "2. Evaluate the current role assignments. Suggest changes if a participant would be " +
                "better suited to a different role based on their capabilities.\n" +
                "3. Recommend which participants should receive Write Access (WR). Only participants " +
                "whose role genuinely requires writing output should have it (creative contributors, coders). " +
                "Critics, reviewers, and researchers should be read-only. Name your WR recommendations.\n" +
                "4. Write the project-specific role plan as a JSON file — one instruction per participant " +
                $"(covering all participants: {participantNameList}).\n\n" +
                "Use EXACTLY this format for the output block:\n\n" +
                "<output path=\"PROJECTSETTINGS/ParticipantRolePlan.json\">\n" +
                "{\n" +
                "  \"roles\": [\n" +
                "    {\"name\": \"ExactDisplayName\", \"instruction\": \"Detailed second-person role instruction for this specific project.\"},\n" +
                "    // one entry per participant\n" +
                "  ]\n" +
                "}\n" +
                "</output>\n\n" +
                "Write the <output> block first (it is processed automatically), then present your " +
                "summary, role evaluation, and Write Access recommendations to the user.\n\n" +
                "CRITICAL — after presenting the above:\n" +
                "• DO NOT start writing project content (chapters, code, designs, etc.).\n" +
                "• End with ONE clear suggestion for the logical next step, then ask the user to confirm.\n" +
                "• Stop after that question. Wait for the user to reply.\n\n" +
                "Project state:\n" + nextStepHint.ToString()));

            if (coordCloud is not null)
                await RunCloudAIStreamAsync(coordCloud, ct);
            else
                await RunOllamaStreamAsync(coordOllama!, ct);

            // Save fingerprint so we don't re-run until the team composition changes
            _currentProject.ParticipantFingerprint = fingerprint;
            ProjectService.SaveProject(_currentProjectFolder, _currentProject);
        }
        catch (Exception ex)
        {
            AddSystemMessage($"⚠  Team capability flow failed: {ex.Message}");
        }
        finally
        {
            _superRoles = null;   // invalidate cache; plan may have been written during this session
            _streamCts?.Dispose();
            _streamCts = null;
            AIRespondButton.IsEnabled = true;
            SendButton.IsEnabled      = true;
        }

        _workSessionFired = true;
    }

    private static string ExtractAfterFirstColon(string line)
    {
        var idx = line.IndexOf(':');
        return idx >= 0 ? line[(idx + 1)..].Trim() : line.Trim();
    }

    /// <summary>
    /// Parses the coordinator's role-plan JSON into a display-name → instruction dictionary.
    /// Accepts both the array format {"roles":[{"name":…,"instruction":…}]}
    /// and a flat object format {"DisplayName":"instruction"}.
    /// Strips JSON comments (// …) before parsing.
    /// </summary>
    private static bool TryParseRolePlan(string json, out Dictionary<string, string> plan)
    {
        plan = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Strip // line comments so the coordinator can include them
            var clean = System.Text.RegularExpressions.Regex.Replace(
                json, @"//[^\n]*", "", System.Text.RegularExpressions.RegexOptions.Multiline).Trim();

            using var doc = System.Text.Json.JsonDocument.Parse(clean);
            var root = doc.RootElement;

            if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                // Array format: {"roles":[{"name":"…","instruction":"…"}]}
                if (root.TryGetProperty("roles", out var rolesElem) &&
                    rolesElem.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in rolesElem.EnumerateArray())
                    {
                        var name  = item.TryGetProperty("name",        out var n) ? n.GetString() ?? "" : "";
                        var instr = item.TryGetProperty("instruction", out var i) ? i.GetString() ?? "" : "";
                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(instr))
                            plan[name] = instr;
                    }
                }
                else
                {
                    // Flat format: {"DisplayName":"instruction"}
                    foreach (var prop in root.EnumerateObject())
                        if (!string.IsNullOrWhiteSpace(prop.Name) &&
                            prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                            plan[prop.Name] = prop.Value.GetString() ?? "";
                }
            }
            return plan.Count > 0;
        }
        catch { return false; }
    }

    // ── Roadmap-building conversation ──────────────────────────────────────

    /// <summary>
    /// Triggers roadmap building when the project supports a roadmap that is still empty and
    /// the init conversation has not started yet.  Once roadmap building is either not needed
    /// or already done, chains into <see cref="CheckAndTriggerWorkSessionAsync"/>.
    /// </summary>
    private async Task CheckAndTriggerRoadmapBuildingAsync()
    {
        if (_projectSettings is null || _currentProjectFolder is null) return;
        // Full Manual Mode has no coordinator automation.
        if (_projectSettings.OrchestrationMode == OrchestrationMode.AllRespond) return;

        if (_currentProjectType?.HasRoadmap == true &&
            !_projectSettings.RoadmapInitialized &&
            (_currentRoadmap is null || _currentRoadmap.Milestones.Count == 0) &&
            HasCoordinatorRole())
        {
            // TriggerRoadmapBuildingAsync chains into CheckAndTriggerWorkSessionAsync when done
            await TriggerRoadmapBuildingAsync();
            return;
        }

        // Roadmap building not needed or no coordinator - proceed to work session
        await CheckAndTriggerWorkSessionAsync();
    }

    /// <summary>
    /// Fires the coordinator's opening roadmap-planning message.
    /// The Planner (if any) gets the first word; the coordinator introduces the process
    /// and asks the user the first clarifying question.
    /// The conversation then continues normally - once the coordinator has enough information
    /// it will embed a <c>&lt;roadmapproposal&gt;</c> in a response, which
    /// <see cref="ApplyRoadmapCommands"/> parses and saves automatically.
    /// </summary>
    private async Task TriggerRoadmapBuildingAsync()
    {
        if (_projectSettings is null || _currentProjectFolder is null || _currentProject is null) return;
        if (_streamCts is not null) return;

        var activeOllamas  = _ollamaParticipants
            .Where(ui => ui.Data.Enabled && IsParticipantActiveInProject(ui)).ToList();
        var activeCloudAIs = _cloudAIParticipants
            .Where(ui => ui.Data.Enabled && IsParticipantActiveInProject(ui)).ToList();

        var (coordOllama, coordCloud) = FindCoordinatorInLists(activeOllamas, activeCloudAIs);
        if (coordOllama is null && coordCloud is null) return;

        AIRespondButton.IsEnabled = false;
        SendButton.IsEnabled      = false;
        _streamCts = new CancellationTokenSource();
        var ct = _streamCts.Token;

        try
        {
            AddSystemMessage("- Roadmap Planning -");

            var projectType = _currentProjectType is null ? "general"
                : $"{_currentProjectType.Icon} {_currentProjectType.Name}";
            var projectDesc = string.IsNullOrWhiteSpace(_currentProject.Description) ? ""
                : $"The project description is: \"{_currentProject.Description.Trim()}\"\n";

            // Inject hidden context so all participants know what's happening
            _sharedHistory.Add(new CloudAIMessage("user",
                "[INTERNAL - not shown to user]\n" +
                $"This project (\"{_currentProject.ProjectName}\", type: {projectType}) has no roadmap yet.\n" +
                projectDesc +
                "Coordinator: open a friendly conversation with the user to build a roadmap together. " +
                "Ask about goals, key phases, and main deliverables - one focused question at a time. " +
                "Once you have gathered enough information (through back-and-forth with the user), " +
                "propose the full roadmap using:\n\n" +
                "<roadmapproposal>\n" +
                "MILESTONE: Milestone title | Optional description\n" +
                "  ITEM: Task title | Optional description\n" +
                "  ITEM: Another task\n" +
                "MILESTONE: Second milestone\n" +
                "  ITEM: ...\n" +
                "</roadmapproposal>\n\n" +
                "Do NOT produce the proposal tag right away - first have a conversation. " +
                "Start by greeting the user and asking your first question about the project's main goal."));

            // If a Planner is present (and isn't the coordinator), let them set the stage first
            var (plannerOllama, plannerCloud) = FindPlannerInLists(activeOllamas, activeCloudAIs);
            bool plannerIsCoord = plannerCloud == coordCloud && plannerOllama == coordOllama;

            if (!plannerIsCoord)
            {
                if (!ct.IsCancellationRequested && plannerCloud is not null)
                {
                    await RunCloudAIStreamAsync(plannerCloud, ct,
                        "INTERNAL SYSTEM - Planner role. Briefly (1-2 sentences) indicate you will " +
                        "help structure the roadmap once the Coordinator has gathered the project goals. " +
                        "Then hand over to the Coordinator.");
                }
                else if (!ct.IsCancellationRequested && plannerOllama is not null)
                {
                    await RunOllamaStreamAsync(plannerOllama!, ct,
                        "INTERNAL SYSTEM - Planner role. Briefly (1-2 sentences) indicate you will " +
                        "help structure the roadmap once the Coordinator has gathered the project goals. " +
                        "Then hand over to the Coordinator.");
                }
            }

            // Coordinator kicks off the conversation
            if (!ct.IsCancellationRequested)
            {
                const string coordHint =
                    "Start the roadmap-building conversation now. In 2-3 sentences: introduce that " +
                    "you'll help the user build a project roadmap through a short conversation, then " +
                    "ask your first question about the project's main goal or top priority. " +
                    "Be warm, concise, and encouraging.";

                if (coordCloud is not null)
                    await RunCloudAIStreamAsync(coordCloud, ct, coordHint);
                else
                    await RunOllamaStreamAsync(coordOllama!, ct, coordHint);
            }

            // Mark conversation as started so we don't re-trigger on subsequent project opens
            _projectSettings.RoadmapInitialized = true;
            ProjectService.SaveProject(_currentProjectFolder!, _projectSettings);
        }
        finally
        {
            _streamCts?.Dispose();
            _streamCts = null;
            AIRespondButton.IsEnabled = true;
            SendButton.IsEnabled      = true;
        }

        // The roadmap-building intro counts as the coordinator's greeting for this open.
        // Mark the flag so we don't fire a second greeting via CheckAndTriggerWorkSessionAsync.
        _workSessionFired = true;
    }

    // ── Work session ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when the current project settings contain at least one active
    /// coordinator role.  Does NOT require the participant to be online - online status is
    /// checked later inside the actual trigger methods.
    /// </summary>
    private bool HasCoordinatorRole() =>
        _projectSettings?.Roles.Any(r => r.IsCoordinator && r.IsActive) == true;

    /// <summary>
    /// Triggers <see cref="TriggerWorkSessionAsync"/> when a coordinator role is configured
    /// and the work-session greeting has not already fired this open.
    /// No-op when conditions are not met or when roadmap building already introduced
    /// the coordinator (<see cref="_workSessionFired"/> is set).
    /// </summary>
    private async Task CheckAndTriggerWorkSessionAsync()
    {
        if (_projectSettings is null || _currentProjectFolder is null) return;
        // Full Manual Mode has no coordinator automation - no work-session greeting.
        if (_projectSettings.OrchestrationMode == OrchestrationMode.AllRespond) return;
        if (_workSessionFired) return;
        if (!HasCoordinatorRole()) return;

        await TriggerWorkSessionAsync();
    }

    /// <summary>
    /// Coordinator greeting and work-session check-in on every project open.
    /// <para>
    /// The coordinator always greets the user first and asks whether to start working
    /// or have a chat.  Once the user is ready the coordinator follows one of two paths:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Open tasks present</b> - standard work session: review InProgress items,
    ///     pick next task, clarify work mode (user-led vs AI-led), update roadmap when done.</item>
    ///   <item><b>No open tasks</b> - completion check: verify with the user that all items
    ///     are truly done, then offer to enrich existing items with descriptions/sub-task lists
    ///     or extend the roadmap with new milestones.</item>
    /// </list>
    /// Clock-watching thresholds (3 h / 8 h / 10 h) are always active via
    /// <see cref="BuildSessionTimeInstruction"/>.
    /// </summary>
    private async Task TriggerWorkSessionAsync()
    {
        if (_projectSettings is null || _currentProjectFolder is null || _currentProject is null) return;
        if (_streamCts is not null) return;
        if (_workSessionFired) return;   // already ran this open - don't double-greet
        _workSessionFired = true;

        var activeOllamas  = _ollamaParticipants
            .Where(ui => ui.Data.Enabled && IsParticipantActiveInProject(ui)).ToList();
        var activeCloudAIs = _cloudAIParticipants
            .Where(ui => ui.Data.Enabled && IsParticipantActiveInProject(ui)).ToList();

        var (coordOllama, coordCloud) = FindCoordinatorInLists(activeOllamas, activeCloudAIs);
        if (coordOllama is null && coordCloud is null) return;

        AIRespondButton.IsEnabled = false;
        SendButton.IsEnabled      = false;
        _streamCts = new CancellationTokenSource();
        var ct = _streamCts.Token;

        try
        {
            AddSystemMessage("- Work Session -");

            // ── Roadmap state snapshot ────────────────────────────────────
            var hasMilestones = _currentRoadmap?.Milestones.Count > 0;

            var inProgress = hasMilestones
                ? _currentRoadmap!.Milestones
                    .SelectMany(ms => ms.Items.Select(it => (Milestone: ms, Item: it)))
                    .Where(x => x.Item.Status == ItemStatus.InProgress)
                    .ToList()
                : [];

            var todo = hasMilestones
                ? _currentRoadmap!.Milestones
                    .SelectMany(ms => ms.Items.Select(it => (Milestone: ms, Item: it)))
                    .Where(x => x.Item.Status == ItemStatus.Todo)
                    .Take(10)
                    .ToList()
                : [];

            var allDone = hasMilestones && inProgress.Count == 0 && todo.Count == 0;

            // Items whose description is empty (any status)
            var noDesc = hasMilestones
                ? _currentRoadmap!.Milestones
                    .SelectMany(ms => ms.Items.Select(it => (Milestone: ms, Item: it)))
                    .Where(x => string.IsNullOrWhiteSpace(x.Item.Description))
                    .ToList()
                : [];

            // ── Build protocol instruction ────────────────────────────────
            var protocol = new System.Text.StringBuilder();

            protocol.AppendLine("[INTERNAL - not shown to user]");
            protocol.AppendLine("Work session starting. IMPORTANT: do NOT dive straight into work.");
            protocol.AppendLine();
            protocol.AppendLine("STEP 1 - GREETING (do this first, every time):");
            protocol.AppendLine("  Greet the user warmly. Ask whether they want to start working on the");
            protocol.AppendLine("  project right away or would prefer to have a friendly chat first.");
            protocol.AppendLine("  Keep this greeting to 2-3 sentences maximum.");
            protocol.AppendLine("  Wait for the user's reply before proceeding to any work steps.");
            protocol.AppendLine();

            if (!hasMilestones)
            {
                // No roadmap content yet - simple greeting, no task protocol
                protocol.AppendLine("ROADMAP STATE: No roadmap tasks exist yet.");
                protocol.AppendLine("STEP 2 - once the user is ready: just get started naturally.");
                protocol.AppendLine("  Do not reference the roadmap or tasks - there are none to discuss.");
                protocol.AppendLine("  Ask what the user would like to work on or talk about.");
            }
            else if (allDone)
            {
                protocol.AppendLine("ROADMAP STATE: No open tasks (no InProgress or Todo items).");
                protocol.AppendLine();
                protocol.AppendLine("STEP 2 - COMPLETION CHECK (once user is ready to work):");
                protocol.AppendLine("  - Congratulate the user on completing all current roadmap items.");
                protocol.AppendLine("  - Verify with them that everything really is done - some items may");
                protocol.AppendLine("    have been marked complete by accident.");
                protocol.AppendLine("  - Ask if they want to extend the roadmap with new milestones or");
                protocol.AppendLine("    add more tasks to existing milestones.");
                protocol.AppendLine("  - Go through each item and offer to add or improve its description");
                protocol.AppendLine("    and sub-task list. Ask the user for the content of each one.");
                protocol.AppendLine("    For example, for a book: ask for the chapter summary and list of");
                protocol.AppendLine("    scenes; for software: ask for acceptance criteria and sub-tasks.");
                protocol.AppendLine("  - Use these tags to update the roadmap:");
                protocol.AppendLine("      <roadmap-describe id=\"ITEM_ID\">");
                protocol.AppendLine("      Description / sub-task list here (multi-line supported)");
                protocol.AppendLine("      </roadmap-describe>");
                protocol.AppendLine("      <roadmap-additem milestone=\"Milestone Title\" title=\"New task\" description=\"Optional\"/>");
                protocol.AppendLine("      <roadmap-addmilestone>");
                protocol.AppendLine("      MILESTONE: Title | Description");
                protocol.AppendLine("        ITEM: Task title | Description");
                protocol.AppendLine("      </roadmap-addmilestone>");

                if (noDesc.Count > 0)
                {
                    protocol.AppendLine();
                    protocol.AppendLine($"Items without descriptions ({noDesc.Count}):");
                    foreach (var (ms, it) in noDesc.Take(20))
                        protocol.AppendLine($"  • [id:{it.Id}] [{ms.Title}] → {it.Title}");
                }
            }
            else
            {
                if (inProgress.Count > 0)
                {
                    protocol.AppendLine("Unfinished from last session:");
                    foreach (var (ms, it) in inProgress)
                        protocol.AppendLine($"  🔄 [{ms.Title}] → {it.Title} ({it.Progress}%)");
                    protocol.AppendLine();
                }

                if (todo.Count > 0)
                {
                    protocol.AppendLine("Next available tasks (Todo):");
                    foreach (var (ms, it) in todo)
                        protocol.AppendLine($"  ⬜ [{ms.Title}] → {it.Title}");
                    protocol.AppendLine();
                }

                protocol.AppendLine("STEP 2 - WORK SESSION (once user is ready):");
                protocol.AppendLine("  a) Mention any unfinished InProgress tasks from last time.");
                protocol.AppendLine("  b) Ask the user if anything on the roadmap needs to be");
                protocol.AppendLine("     changed or updated before starting.");
                protocol.AppendLine("  c) Help the user pick the next task to work on.");
                protocol.AppendLine("  d) Clarify the preferred work mode:");
                protocol.AppendLine("       • User-led: user does the work, AI gives tips and motivation");
                protocol.AppendLine("       • AI-led: AI does the heavy lifting, user gives feedback");
                protocol.AppendLine("  e) Work on the task together.");
                protocol.AppendLine("  f) When a task or sub-task is finished, update the roadmap:");
                protocol.AppendLine("       [ROADMAP:update:ITEM_ID:PROGRESS]  - e.g. 75 for 75%");
                protocol.AppendLine("       [ROADMAP:complete:ITEM_ID]         - marks item 100% done");
                protocol.AppendLine("  g) After finishing, ask whether to continue or wrap up for today.");

                if (noDesc.Count > 0)
                {
                    protocol.AppendLine();
                    protocol.AppendLine($"Note: {noDesc.Count} item(s) have no description yet.");
                    protocol.AppendLine("      When you reach those items, use <roadmap-describe> to add one.");
                }
            }

            _sharedHistory.Add(new CloudAIMessage("user", protocol.ToString().Trim()));

            // ── Coordinator fires the greeting ────────────────────────────
            const string coordHint =
                "Start the work session now. Greet the user warmly (2-3 sentences). " +
                "Ask whether they are ready to dive into work on this project or would prefer " +
                "to have a friendly chat first. Do NOT start discussing tasks yet - just greet " +
                "and ask. Be warm and encouraging.";

            if (!ct.IsCancellationRequested)
            {
                if (coordCloud is not null)
                    await RunCloudAIStreamAsync(coordCloud, ct, coordHint);
                else
                    await RunOllamaStreamAsync(coordOllama!, ct, coordHint);
            }
        }
        finally
        {
            _streamCts?.Dispose();
            _streamCts = null;
            AIRespondButton.IsEnabled = true;
            SendButton.IsEnabled      = true;
        }
    }

    // ── Orchestration helpers ──────────────────────────────────────────────

    /// <summary>
    /// Finds the coordinator among the already-filtered active participant lists.
    /// Cloud AI is preferred over Ollama (larger context windows for coordination).
    /// </summary>
    private (OllamaParticipantUI? Ollama, CloudAIParticipantUI? Cloud) FindCoordinatorInLists(
        List<OllamaParticipantUI> ollamas, List<CloudAIParticipantUI> clouds)
    {
        if (_projectSettings is null) return (null, null);

        var cloud = clouds.FirstOrDefault(ui =>
            GetRoleForParticipant(ui)?.IsCoordinator == true);
        if (cloud is not null) return (null, cloud);

        var ollama = ollamas.FirstOrDefault(ui =>
            GetRoleForParticipant(ui)?.IsCoordinator == true);
        return (ollama, null);
    }

    /// <summary>
    /// Finds the first Planner (PL) among the active participant lists.
    /// Cloud AI is preferred over Ollama; returns (null, null) if none assigned.
    /// </summary>
    private (OllamaParticipantUI? Ollama, CloudAIParticipantUI? Cloud) FindPlannerInLists(
        List<OllamaParticipantUI> ollamas, List<CloudAIParticipantUI> clouds)
    {
        if (_projectSettings is null) return (null, null);

        var cloud = clouds.FirstOrDefault(ui =>
            GetRoleForParticipant(ui)?.IsPlanner == true);
        if (cloud is not null) return (null, cloud);

        var ollama = ollamas.FirstOrDefault(ui =>
            GetRoleForParticipant(ui)?.IsPlanner == true);
        return (ollama, null);
    }

    /// <summary>Effective display name for a participant: AnswerAsName if set, else CustomName/model name.</summary>
    private string GetEffectiveName(OllamaParticipantUI ui)
    {
        var role = GetRoleForParticipant(ui);
        if (!string.IsNullOrWhiteSpace(role?.AnswerAsName)) return role.AnswerAsName;
        return string.IsNullOrEmpty(ui.Data.CustomName)
            ? FormatModelDisplayName(ui.Data.Service.CurrentModel)
            : ui.Data.CustomName;
    }

    /// <summary>Effective display name for a participant: AnswerAsName if set, else CustomName/model name.</summary>
    private string GetEffectiveName(CloudAIParticipantUI ui)
    {
        var role = GetRoleForParticipant(ui);
        if (!string.IsNullOrWhiteSpace(role?.AnswerAsName)) return role.AnswerAsName;
        return string.IsNullOrEmpty(ui.Data.CustomName)
            ? FormatModelDisplayName(ui.Data.Service.CurrentModel)
            : ui.Data.CustomName;
    }

    /// <summary>
    /// Returns the display name for a participant identified by their avatar label (e.g. "Gm").
    /// Used to convert history message prefixes from raw labels into human-readable names.
    /// Falls back to the raw label if no participant matches (e.g. after a config change).
    /// </summary>
    private string GetDisplayNameForLabel(string avatarLabel)
    {
        foreach (var ui in _ollamaParticipants)
            if (ui.Data.AvatarLabel == avatarLabel) return GetEffectiveName(ui);
        foreach (var ui in _cloudAIParticipants)
            if (ui.Data.AvatarLabel == avatarLabel) return GetEffectiveName(ui);
        return avatarLabel;
    }

    /// <summary>
    /// Returns the project role for this Ollama participant using positional matching -
    /// safe when multiple participants share the same model name.
    /// </summary>
    private ProjectParticipantRole? GetRoleForParticipant(OllamaParticipantUI ui)
    {
        if (_projectSettings is null) return null;
        int idx = _ollamaParticipants.IndexOf(ui);
        if (idx < 0) return null;
        return ResolveRoleAtGroupIndex("Ollama", idx);
    }

    /// <summary>
    /// Returns the project role for this Cloud AI participant using positional matching -
    /// safe when multiple participants share the same model name.
    /// </summary>
    private ProjectParticipantRole? GetRoleForParticipant(CloudAIParticipantUI ui)
    {
        if (_projectSettings is null) return null;
        int idx = _cloudAIParticipants.IndexOf(ui);
        if (idx < 0) return null;
        return ResolveRoleAtGroupIndex("Cloud", idx);
    }

    /// <summary>
    /// Finds the <paramref name="indexInGroup"/>-th Ollama (typeGroup="Ollama") or Cloud AI
    /// (typeGroup="Cloud") entry in the project's active participant list and returns its
    /// project role using the same positional-first / key-based-fallback logic as the
    /// settings dialog. The project-saved list exactly mirrors the current UI, so positional
    /// matching is reliable regardless of what global settings contain.
    /// </summary>
    private ProjectParticipantRole? ResolveRoleAtGroupIndex(string typeGroup, int indexInGroup)
    {
        var ps = _projectSettings!;
        if (ps.ActiveParticipants is not { Count: > 0 }) return null;
        var enabled = ps.ActiveParticipants.Where(p => p.Enabled).ToList();

        int groupCount = 0;
        for (int pi = 0; pi < enabled.Count; pi++)
        {
            var p        = enabled[pi];
            bool matches = typeGroup == "Ollama" ? p.Type == "Ollama" : p.Type != "Ollama";
            if (!matches) continue;

            if (groupCount == indexInGroup)
            {
                // Positional-first (same as ShowProjectSettingsDialog), key-based fallback
                ProjectParticipantRole? role = null;
                if (pi < ps.Roles.Count)
                {
                    var c = ps.Roles[pi];
                    if (string.Equals(c.Provider, p.Type,  StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(c.Model,    p.Model, StringComparison.OrdinalIgnoreCase))
                        role = c;
                }
                return role ?? ps.Get(p.Type, p.Model);
            }
            groupCount++;
        }
        return null;
    }

    /// <summary>Returns true when the participant is flagged as a Reasoner in the current project settings.</summary>
    private bool IsReasoner(OllamaParticipantUI ui) =>
        GetRoleForParticipant(ui)?.IsReasoner == true;

    /// <summary>Returns true when the participant is flagged as a Reasoner in the current project settings.</summary>
    private bool IsReasoner(CloudAIParticipantUI ui) =>
        GetRoleForParticipant(ui)?.IsReasoner == true;

    /// <summary>Returns all enabled Critics (Ollama + Cloud AI) sorted by effective display name.</summary>
    private List<string> GetAvailableCritics()
    {
        var result = new List<string>();
        foreach (var u in _ollamaParticipants.Where(u => u.Data.Enabled && GetRoleForParticipant(u)?.IsCritic == true))
            result.Add(GetEffectiveName(u));
        foreach (var u in _cloudAIParticipants.Where(u => u.Data.Enabled && GetRoleForParticipant(u)?.IsCritic == true))
            result.Add(GetEffectiveName(u));
        return result;
    }

    /// <summary>Returns all enabled Planners (Ollama + Cloud AI) sorted by effective display name.</summary>
    private List<string> GetAvailablePlanners()
    {
        var result = new List<string>();
        foreach (var u in _ollamaParticipants.Where(u => u.Data.Enabled && GetRoleForParticipant(u)?.IsPlanner == true))
            result.Add(GetEffectiveName(u));
        foreach (var u in _cloudAIParticipants.Where(u => u.Data.Enabled && GetRoleForParticipant(u)?.IsPlanner == true))
            result.Add(GetEffectiveName(u));
        return result;
    }

    /// <summary>Returns all enabled Researchers (Ollama + Cloud AI) sorted by effective display name.</summary>
    private List<string> GetAvailableResearchers()
    {
        var result = new List<string>();
        foreach (var u in _ollamaParticipants.Where(u => u.Data.Enabled && GetRoleForParticipant(u)?.IsResearcher == true))
            result.Add(GetEffectiveName(u));
        foreach (var u in _cloudAIParticipants.Where(u => u.Data.Enabled && GetRoleForParticipant(u)?.IsResearcher == true))
            result.Add(GetEffectiveName(u));
        return result;
    }

    /// <summary>
    /// Returns true if the participant may write project files (&lt;output&gt;, &lt;projectplan&gt; tags).
    /// Coordinators always have write access. All other participants need the explicit
    /// Write Access (WR) flag. Falls back to unrestricted when no coordinator is configured
    /// (backwards compatibility with projects that predate role assignment).
    /// </summary>
    private bool HasWriteAccess(OllamaParticipantUI ui)
    {
        if (_projectSettings is null) return true;
        bool anyCoordinator = _projectSettings.Roles.Any(r => r.IsCoordinator);
        if (!anyCoordinator) return true;   // no roles configured yet - open access
        var role = GetRoleForParticipant(ui);
        return role?.IsCoordinator == true || role?.IsWriteAccess == true;
    }

    /// <inheritdoc cref="HasWriteAccess(OllamaParticipantUI)"/>
    private bool HasWriteAccess(CloudAIParticipantUI ui)
    {
        if (_projectSettings is null) return true;
        bool anyCoordinator = _projectSettings.Roles.Any(r => r.IsCoordinator);
        if (!anyCoordinator) return true;
        var role = GetRoleForParticipant(ui);
        return role?.IsCoordinator == true || role?.IsWriteAccess == true;
    }

    /// <summary>
    /// Returns all enabled Reasoners (across Ollama and Cloud AI) sorted by priority descending.
    /// Used to inject the Reasoner roster into the Coordinator's system prompt.
    /// </summary>
    private List<(string Name, int Priority)> GetAvailableReasoners()
    {
        var result = new List<(string Name, int Priority)>();
        foreach (var u in _ollamaParticipants.Where(u => u.Data.Enabled && IsReasoner(u)))
            result.Add((GetEffectiveName(u), GetRoleForParticipant(u)?.ReasonerPriority ?? 5));
        foreach (var u in _cloudAIParticipants.Where(u => u.Data.Enabled && IsReasoner(u)))
            result.Add((GetEffectiveName(u), GetRoleForParticipant(u)?.ReasonerPriority ?? 5));
        return result;
    }

    /// <summary>Returns the effective display name of the active project coordinator, or null if none.</summary>
    private string? GetCoordinatorName()
    {
        var (coordOllama, coordCloud) = FindActiveCoordinator();
        if (coordCloud  is not null) return GetEffectiveName(coordCloud);
        if (coordOllama is not null) return GetEffectiveName(coordOllama);
        return null;
    }

    /// <summary>
    /// Updates the CO / R badge overlays on every sidebar participant card to reflect
    /// the current <see cref="_projectSettings"/>. Call after loading or saving project
    /// settings, and after closing a project (badges go hidden when settings are null).
    /// </summary>
    private void RefreshParticipantBadges()
    {
        foreach (var ui in _ollamaParticipants)
        {
            var role = GetRoleForParticipant(ui);
            ui.CoBadge.Visibility = role?.IsCoordinator == true                                  ? Visibility.Visible : Visibility.Collapsed;
            ui.RBadge .Visibility = role?.IsReasoner    == true                                  ? Visibility.Visible : Visibility.Collapsed;
            ui.CrBadge.Visibility = role?.IsCritic      == true                                  ? Visibility.Visible : Visibility.Collapsed;
            ui.PlBadge.Visibility = role?.IsPlanner     == true                                  ? Visibility.Visible : Visibility.Collapsed;
            ui.RsBadge.Visibility = role?.IsResearcher  == true                                  ? Visibility.Visible : Visibility.Collapsed;
            ui.WrBadge.Visibility = (role?.IsWriteAccess == true || role?.IsCoordinator == true) ? Visibility.Visible : Visibility.Collapsed;
            ui.BadgeRow.Visibility = (role?.IsCoordinator == true || role?.IsReasoner == true ||
                                      role?.IsCritic      == true || role?.IsPlanner  == true ||
                                      role?.IsResearcher  == true || role?.IsWriteAccess == true)
                                      ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var ui in _cloudAIParticipants)
        {
            var role = GetRoleForParticipant(ui);
            ui.CoBadge.Visibility = role?.IsCoordinator == true                                  ? Visibility.Visible : Visibility.Collapsed;
            ui.RBadge .Visibility = role?.IsReasoner    == true                                  ? Visibility.Visible : Visibility.Collapsed;
            ui.CrBadge.Visibility = role?.IsCritic      == true                                  ? Visibility.Visible : Visibility.Collapsed;
            ui.PlBadge.Visibility = role?.IsPlanner     == true                                  ? Visibility.Visible : Visibility.Collapsed;
            ui.RsBadge.Visibility = role?.IsResearcher  == true                                  ? Visibility.Visible : Visibility.Collapsed;
            ui.WrBadge.Visibility = (role?.IsWriteAccess == true || role?.IsCoordinator == true) ? Visibility.Visible : Visibility.Collapsed;
            ui.BadgeRow.Visibility = (role?.IsCoordinator == true || role?.IsReasoner == true ||
                                      role?.IsCritic      == true || role?.IsPlanner  == true ||
                                      role?.IsResearcher  == true || role?.IsWriteAccess == true)
                                      ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Returns true when <paramref name="name"/> is mentioned with an @ prefix in the response.</summary>
    private static bool IsTaggedInResponse(string response, string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        Regex.IsMatch(response, $@"@{Regex.Escape(name)}\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// Returns true when <paramref name="name"/> appears as a whole word anywhere in
    /// <paramref name="message"/> (case-insensitive, no @ required).
    /// Used to detect when the user directly addresses a participant by name.
    /// </summary>
    private static bool IsNamedInMessage(string message, string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        Regex.IsMatch(message, $@"\b{Regex.Escape(name)}\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// Returns true when the response is just "PASS" (possibly with trailing punctuation /
    /// whitespace), meaning the AI decided it has nothing new to add in this follow-up round.
    /// </summary>
    private static bool IsPassResponse(string text) =>
        string.IsNullOrWhiteSpace(text) ||
        text.Trim().TrimEnd('.', '!', '…').Trim()
            .Equals("PASS", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Turns an <see cref="HttpRequestException"/> into a human-readable sentence,
    /// with special handling for common HTTP status codes.
    /// </summary>
    private static string HttpErrorMessage(HttpRequestException ex, string participantName) =>
        ex.StatusCode switch
        {
            System.Net.HttpStatusCode.TooManyRequests =>
                "Rate limit hit (429 Too Many Requests). " +
                "The free tier allows only a few requests per minute - please wait a moment before continuing.",
            System.Net.HttpStatusCode.Unauthorized =>
                "Unauthorized (401) - the API key was rejected. Check or re-enter the key in ⋮ → Providers.",
            System.Net.HttpStatusCode.Forbidden =>
                "Forbidden (403) - the API key does not have permission for this model.",
            System.Net.HttpStatusCode.ServiceUnavailable =>
                "Service unavailable (503) - the API is temporarily down. Try again shortly.",
            null => $"Connection error: {ex.Message}",
            _    => $"API error {(int)ex.StatusCode}: {ex.Message}"
        };

    private async Task<bool> RunOllamaStreamAsync(OllamaParticipantUI ui, CancellationToken ct,
                                                   string? systemHint = null,
                                                   bool skipLatestUserMessage = false,
                                                   bool hidden = false,
                                                   int _loopDepth = 0,
                                                   IReadOnlyList<CloudAIMessage>? histOverride = null)
    {
        var modelName = ui.Data.Service.CurrentModel;
        var display   = string.IsNullOrEmpty(ui.Data.CustomName)
            ? FormatModelDisplayName(modelName)
            : ui.Data.CustomName;
        var avatarLabel = ui.Data.AvatarLabel;
        var colorKey    = ui.Data.ColorKey;

        StreamBubble? bubble = hidden ? null
            : AddStreamingBubble(display, avatarLabel, colorKey, "SecondaryBubbleBrush", false);
        var sb         = new StringBuilder();
        bool firstToken = true;

        // Pulse the card avatar while the model is generating
        if (!hidden) StartCardPulse(ui.AvatarBorder, ui.StatusLabel);

        // Subscribe to live thinking-text updates so the tooltip tracks thinking in real time
        var svc = ui.Data.Service;
        svc.ThinkingUpdated += OnThinkingUpdate;
        void OnThinkingUpdate(string thought)
        {
            if (!hidden)
                Dispatcher.Invoke(() => bubble!.UpdateThinkingTooltip(thought));
        }

        try
        {
            var history = BuildOllamaHistoryFor(ui, skipLatestUserMessage, histOverride);
            if (systemHint is not null)
                history.Insert(1, new OllamaChatMessage("system", systemHint));
            await foreach (var token in svc.StreamAsync(history, ct))
            {
                if (firstToken)
                {
                    if (!hidden) bubble!.StopThinking();   // hides dots + tooltip disappears naturally
                    firstToken = false;
                    SetParticipantError(ui, null);         // clear any previous error badge
                }
                sb.Append(token);
                if (!hidden)
                {
                    bubble!.Content.Text = sb.ToString();
                    ChatScrollViewer.ScrollToBottom();
                }
            }
            if (firstToken && !hidden) bubble!.StopThinking(); // empty response
            // Hidden streams are internal assessments - never write files or mutate roadmap.
            bool ollamaHadReadOps = false;
            string ollamaFinalText;
            if (!hidden && _currentProjectFolder is not null)
                (ollamaFinalText, ollamaHadReadOps) = ProcessAIFileOperationTags(
                    sb.ToString(), display, _currentProjectFolder, HasWriteAccess(ui), GetCoordinatorName());
            else
                ollamaFinalText = sb.ToString();

            // ── Roadmap commands ──────────────────────────────────────────
            if (!hidden && _currentRoadmap is not null)
            {
                var myRole = GetRoleForParticipant(ui);
                var cleaned = ApplyRoadmapCommands(ollamaFinalText, display, myRole?.IsCoordinator == true);
                if (cleaned != ollamaFinalText) ollamaFinalText = cleaned;
            }
            // ─────────────────────────────────────────────────────────────

            if (!hidden)
                ApplyEmoteFormatting(bubble!, ollamaFinalText, GetEffectiveName(ui));

            // If the model decided it has nothing new to add, remove its bubble silently
            if (IsPassResponse(ollamaFinalText))
            {
                if (!hidden && ChatPanel.Children.Count > 0)
                    ChatPanel.Children.RemoveAt(ChatPanel.Children.Count - 1);
                return false;
            }

            _sharedHistory.Add(new CloudAIMessage("assistant", ollamaFinalText, GetEffectiveName(ui)));
            if (!hidden)
            {
                var ollamaLogEntry = new ChatLogEntry
                {
                    Timestamp   = DateTime.Now,
                    SenderType  = "AI",
                    Provider    = "Ollama",
                    ModelName   = modelName,
                    DisplayName = display,
                    AvatarLabel = avatarLabel,
                    AccentKey   = colorKey,
                    BubbleKey   = "SecondaryBubbleBrush",
                    IsUser      = false,
                    Message     = ollamaFinalText
                };
                AppendToProjectLog(ollamaLogEntry);
                AppendToGeneralLog(ollamaLogEntry);
                SpeakMessageIfEnabled(ui.Data.Service.CurrentModel, "Ollama", ollamaFinalText);
            }
            // ── Auto-loop: re-invoke after file reads so AI can act on the results ─────
            if (ollamaHadReadOps && !hidden && _loopDepth < MaxToolLoopDepth)
            {
                AddSystemMessage($"🔄  {display} received file results - continuing " +
                                 $"(step {_loopDepth + 2} of {MaxToolLoopDepth + 1} max)…");
                return await RunOllamaStreamAsync(ui, ct, systemHint,
                    skipLatestUserMessage: false, hidden: false, _loopDepth: _loopDepth + 1);
            }
            // ─────────────────────────────────────────────────────────────────────────
            if (!hidden)
            {
                OnParticipantResponded(ui);   // moodlet counter
                // If we sent a checkin reminder, parse this response for yes/no
                ProcessCheckinResponse(display, ollamaFinalText);
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            // User cancelled - show partial text already in the bubble (if any) and stop cleanly.
            // Do NOT re-throw: callers check ct.IsCancellationRequested to decide whether to continue.
            if (!hidden)
            {
                if (firstToken) bubble!.StopThinking();
                else            bubble!.Content.Text = sb.Append("… [cancelled]").ToString();
            }
            return false;
        }
        catch (HttpRequestException ex)
        {
            if (!hidden)
            {
                bubble!.StopThinking();
                ChatPanel.Children.Remove(bubble.OuterWrapper);
                var httpMsg = HttpErrorMessage(ex, display);
                AddSystemMessage($"⚠  {display} - {httpMsg}");
            }
            var errText = ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                ? "Wants Money" : "ERROR";
            SetParticipantError(ui, errText);
            if (!hidden) NotifyCoordinatorOfError(display, errText);
        }
        catch (Exception ex)
        {
            if (!hidden)
            {
                bubble!.StopThinking();
                ChatPanel.Children.Remove(bubble.OuterWrapper);
                AddSystemMessage($"⚠  {display} - Error: {ex.Message}");
            }
            SetParticipantError(ui, "ERROR");
            if (!hidden) NotifyCoordinatorOfError(display, "ERROR");
        }
        finally
        {
            svc.ThinkingUpdated -= OnThinkingUpdate;
            if (!hidden) StopCardPulse(ui.AvatarBorder, ui.StatusLabel);
        }
        return !hidden; // visible error → error bubble shown (counts as responded); hidden error → doesn't count
    }

    private async Task<bool> RunCloudAIStreamAsync(CloudAIParticipantUI ui, CancellationToken ct,
                                                    string? systemHint = null,
                                                    bool skipLatestUserMessage = false,
                                                    bool hidden = false,
                                                    int _loopDepth = 0,
                                                    IReadOnlyList<CloudAIMessage>? histOverride = null)
    {
        var model       = ui.Data.Service.CurrentModel;
        var display     = string.IsNullOrEmpty(ui.Data.CustomName)
            ? FormatModelDisplayName(model)
            : ui.Data.CustomName;
        var avatarLabel = ui.Data.AvatarLabel;
        var colorKey    = ui.Data.ColorKey;

        StreamBubble? bubble = hidden ? null
            : AddStreamingBubble(display, avatarLabel, colorKey, "PrimaryBubbleBrush", false);
        var sb         = new StringBuilder();
        bool firstToken = true;

        // Pulse the card avatar while the model is generating
        if (!hidden) StartCardPulse(ui.AvatarBorder, ui.StatusLabel);

        // ── Rate limiting ─────────────────────────────────────────────────
        // Key is "provider|model" so each model can have its own rpm budget.
        var providerName  = ui.Data.Service.ProviderName;
        var limiterKey    = $"{providerName}|{ui.Data.Service.CurrentModel}";
        if (_rateLimiters.TryGetValue(limiterKey, out var rateLimiter))
        {
            if (!hidden)
                bubble!.UpdateThinkingTooltip($"⏳ Waiting - rate limit {rateLimiter.Rpm} req/min");
            await rateLimiter.WaitAsync(ct);
            if (!hidden)
                bubble!.UpdateThinkingTooltip("");
        }

        try
        {
            var (history, system) = BuildCloudAIHistoryFor(ui, skipLatestUserMessage, histOverride);
            if (systemHint is not null)
                system += "\n\n" + systemHint;
            await foreach (var token in ui.Data.Service.StreamAsync(history, system, ct))
            {
                if (firstToken)
                {
                    if (!hidden) bubble!.StopThinking();
                    firstToken = false;
                    SetParticipantError(ui, null);         // clear any previous error badge
                }
                sb.Append(token);
                if (!hidden)
                {
                    bubble!.Content.Text = sb.ToString();
                    ChatScrollViewer.ScrollToBottom();
                }
            }
            if (firstToken && !hidden) bubble!.StopThinking();
            // Hidden streams are internal assessments - never write files or mutate roadmap.
            bool cloudHadReadOps = false;
            string cloudFinalText;
            if (!hidden && _currentProjectFolder is not null)
                (cloudFinalText, cloudHadReadOps) = ProcessAIFileOperationTags(
                    sb.ToString(), display, _currentProjectFolder, HasWriteAccess(ui), GetCoordinatorName());
            else
                cloudFinalText = sb.ToString();

            // ── Roadmap commands ──────────────────────────────────────────
            if (!hidden && _currentRoadmap is not null)
            {
                var myRole  = GetRoleForParticipant(ui);
                var cleaned = ApplyRoadmapCommands(cloudFinalText, display, myRole?.IsCoordinator == true);
                if (cleaned != cloudFinalText) cloudFinalText = cleaned;
            }
            // ─────────────────────────────────────────────────────────────

            if (!hidden)
                ApplyEmoteFormatting(bubble!, cloudFinalText, GetEffectiveName(ui));

            // If the model decided it has nothing new to add, remove its bubble silently
            if (IsPassResponse(cloudFinalText))
            {
                if (!hidden && ChatPanel.Children.Count > 0)
                    ChatPanel.Children.RemoveAt(ChatPanel.Children.Count - 1);
                return false;
            }

            _sharedHistory.Add(new CloudAIMessage("assistant", cloudFinalText, GetEffectiveName(ui)));
            if (!hidden)
            {
                var cloudLogEntry = new ChatLogEntry
                {
                    Timestamp   = DateTime.Now,
                    SenderType  = "AI",
                    Provider    = ui.Data.ProviderName,
                    ModelName   = model,
                    DisplayName = display,
                    AvatarLabel = avatarLabel,
                    AccentKey   = colorKey,
                    BubbleKey   = "PrimaryBubbleBrush",
                    IsUser      = false,
                    Message     = cloudFinalText
                };
                AppendToProjectLog(cloudLogEntry);
                AppendToGeneralLog(cloudLogEntry);
                SpeakMessageIfEnabled(model, ui.Data.Service.ProviderName, cloudFinalText);
            }
            // ── Auto-loop: re-invoke after file reads so AI can act on the results ─────
            if (cloudHadReadOps && !hidden && _loopDepth < MaxToolLoopDepth)
            {
                AddSystemMessage($"🔄  {display} received file results - continuing " +
                                 $"(step {_loopDepth + 2} of {MaxToolLoopDepth + 1} max)…");
                return await RunCloudAIStreamAsync(ui, ct, systemHint,
                    skipLatestUserMessage: false, hidden: false, _loopDepth: _loopDepth + 1);
            }
            // ─────────────────────────────────────────────────────────────────────────
            if (!hidden)
            {
                OnParticipantResponded(ui);   // moodlet counter
                // If we sent a checkin reminder, parse this response for yes/no
                ProcessCheckinResponse(display, cloudFinalText);
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            // User cancelled - show partial text already in the bubble (if any) and stop cleanly.
            // Do NOT re-throw: callers check ct.IsCancellationRequested to decide whether to continue.
            if (!hidden)
            {
                if (firstToken) bubble!.StopThinking();
                else            bubble!.Content.Text = sb.Append("… [cancelled]").ToString();
            }
            return false;
        }
        catch (HttpRequestException ex)
        {
            if (!hidden)
            {
                bubble!.StopThinking();
                ChatPanel.Children.Remove(bubble.OuterWrapper);
                var httpMsg = HttpErrorMessage(ex, display);
                AddSystemMessage($"⚠  {display} - {httpMsg}");
            }
            var errText = ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                ? "Wants Money" : "ERROR";
            SetParticipantError(ui, errText);
            if (!hidden) NotifyCoordinatorOfError(display, errText);
        }
        catch (Exception ex)
        {
            if (!hidden)
            {
                bubble!.StopThinking();
                ChatPanel.Children.Remove(bubble.OuterWrapper);
                AddSystemMessage($"⚠  {display} - Error: {ex.Message}");
            }
            SetParticipantError(ui, "ERROR");
            if (!hidden) NotifyCoordinatorOfError(display, "ERROR");
        }
        finally
        {
            if (!hidden) StopCardPulse(ui.AvatarBorder, ui.StatusLabel);
        }
        return !hidden; // visible error → error bubble shown (counts as responded); hidden error → doesn't count
    }

    // ── Per-participant history builders ───────────────────────────────────

    private List<OllamaChatMessage> BuildOllamaHistoryFor(
        OllamaParticipantUI forUi,
        bool skipLatestUserMessage = false,
        IReadOnlyList<CloudAIMessage>? histOverride = null)
    {
        var myLabel = forUi.Data.AvatarLabel;
        var myName  = forUi.Data.DisplayName;
        var myModel = forUi.Data.Service.CurrentModel;
        var myRole  = GetRoleForParticipant(forUi);

        var myHasWrite   = HasWriteAccess(forUi);
        var isCoord      = myRole?.IsCoordinator == true;
        var reasoners    = isCoord ? GetAvailableReasoners()    : null;
        var planners     = isCoord ? GetAvailablePlanners()     : null;
        var researchers  = isCoord ? GetAvailableResearchers()  : null;
        var critics      = isCoord ? GetAvailableCritics()      : null;
        var superRole    = GetSuperRoleInstruction(myName);
        var writerNames  = myHasWrite ? null : GetWriteAccessParticipantNames();

        var result = new List<OllamaChatMessage>
        {
            new("system",
                $"You are {myName}, running the {myModel} model. " +
                $"Always respond as {myName}. " +
                $"If asked who you are, say you are {myName} running {myModel}. " +
                $"You are an AI language model — unless a role instruction explicitly tells you otherwise, " +
                $"do not invent or claim personal hobbies, feelings, relationships, or experiences. " +
                $"Do not fabricate or assume facts you are uncertain about; acknowledge uncertainty honestly instead. " +
                $"Messages from other AI participants are prefixed with their display name in square brackets. " +
                $"IMPORTANT: Never prefix your own response with your name or any label — write directly without any '[Name]:' header. " +
                $"Never write as, speak for, or impersonate another participant. You are {myName} and only ever respond in your own voice." +
                BuildAppContextInstruction(forOllama: forUi) +
                BuildProjectTypeContext() +
                BuildRoleInstruction(myRole, reasoners, planners, researchers, critics, superRole) +
                // Global response-length preference - only when no project is open.
                // Projects override this via per-participant role settings.
                (_projectSettings is null ? BuildResponseLengthInstruction(_globalResponseLength) : "") +
                BuildTeamContextInstruction(forOllama: forUi) +
                (_projectSettings is not null ? BuildAutonomyModeInstruction(_projectSettings.AutonomyMode) : "") +
                BuildLanguageInstruction(_projectLanguage) +
                (string.IsNullOrEmpty(_projectLanguage) ? BuildUiLanguageInstruction(_uiLanguageName) : "") +
                BuildInputFilesContext(_currentProjectFolder) +
                BuildWorldEntityContext() +
                BuildToneInstruction(_toneLevel, _mockingbirdMode, _buccaneeerMode, _projectLanguage) +
                BuildChattinessInstruction(_chattinessLevel) +
                BuildFileOperationInstruction(_currentProjectFolder, myHasWrite, writerNames) +
                BuildRoadmapContext(myRole) +
                BuildSessionTimeInstruction(myRole))
        };

        // histOverride is used for private tasks — it is a pre-built snapshot that contains
        // the actual private user message instead of the busy-marker in _sharedHistory.
        var source = histOverride ?? (IReadOnlyList<CloudAIMessage>)_sharedHistory;

        // When called as a reasoner, skip the latest user message so the reasoner only
        // responds to the coordinator's explicit delegation, not the user's question directly.
        int skipIndex = skipLatestUserMessage
            ? source.Select((m, i) => (m, i)).LastOrDefault(t => t.m.Role == "user").i
            : -1;

        var myEffectiveName = GetEffectiveName(forUi);
        for (int i = 0; i < source.Count; i++)
        {
            if (i == skipIndex) continue;
            var msg = source[i];
            if (msg.Role == "user")
                result.Add(new OllamaChatMessage("user", msg.Content));
            else if (msg.Role == "assistant")
            {
                // Sender is now the effective display name - compare directly (no label lookup needed)
                if (msg.Sender == myEffectiveName)
                    result.Add(new OllamaChatMessage("assistant", msg.Content));
                else
                    result.Add(new OllamaChatMessage("user", $"[{msg.Sender}]: {msg.Content}"));
            }
        }

        return result;
    }

    private (List<CloudAIMessage> History, string System) BuildCloudAIHistoryFor(
        CloudAIParticipantUI forUi,
        bool skipLatestUserMessage = false,
        IReadOnlyList<CloudAIMessage>? histOverride = null)
    {
        var myLabel    = forUi.Data.AvatarLabel;
        var myName     = forUi.Data.DisplayName;
        var myModel    = forUi.Data.Service.CurrentModel;
        var myProvider = forUi.Data.Service.ProviderName;
        var myRole     = GetRoleForParticipant(forUi);

        var myHasWrite  = HasWriteAccess(forUi);
        var isCoord     = myRole?.IsCoordinator == true;
        var reasoners   = isCoord ? GetAvailableReasoners()   : null;
        var planners    = isCoord ? GetAvailablePlanners()    : null;
        var researchers = isCoord ? GetAvailableResearchers() : null;
        var critics     = isCoord ? GetAvailableCritics()     : null;
        var superRole   = GetSuperRoleInstruction(myName);
        var writerNames = myHasWrite ? null : GetWriteAccessParticipantNames();

        var system =
            $"You are {myName}, running model {myModel}. " +
            $"Always respond as {myName}. If asked who you are, identify yourself as {myName}. " +
            $"You are an AI language model — unless a role instruction explicitly tells you otherwise, " +
            $"do not invent or claim personal hobbies, feelings, relationships, or experiences. " +
            $"Do not fabricate or assume facts you are uncertain about; acknowledge uncertainty honestly instead. " +
            $"Messages from other AI participants are prefixed with their display name in square brackets. " +
            $"IMPORTANT: Never prefix your own response with your name or any label — write directly without any '[Name]:' header. " +
            $"Never write as, speak for, or impersonate another participant. You are {myName} and only ever respond in your own voice." +
            BuildAppContextInstruction(forCloud: forUi) +
            BuildProjectTypeContext() +
            BuildRoleInstruction(myRole, reasoners, planners, researchers, critics, superRole) +
            // Global response-length preference - only when no project is open.
            // Projects override this via per-participant role settings.
            (_projectSettings is null ? BuildResponseLengthInstruction(_globalResponseLength) : "") +
            BuildTeamContextInstruction(forCloud: forUi) +
            (_projectSettings is not null ? BuildAutonomyModeInstruction(_projectSettings.AutonomyMode) : "") +
            BuildLanguageInstruction(_projectLanguage) +
            (string.IsNullOrEmpty(_projectLanguage) ? BuildUiLanguageInstruction(_uiLanguageName) : "") +
            BuildInputFilesContext(_currentProjectFolder) +
            BuildWorldEntityContext() +
            BuildToneInstruction(_toneLevel, _mockingbirdMode, _buccaneeerMode, _projectLanguage) +
            BuildChattinessInstruction(_chattinessLevel) +
            BuildFileOperationInstruction(_currentProjectFolder, myHasWrite, writerNames) +
            BuildRoadmapContext(myRole) +
            BuildSessionTimeInstruction(myRole);

        // histOverride is used for private tasks — it is a pre-built snapshot that contains
        // the actual private user message instead of the busy-marker in _sharedHistory.
        var source = histOverride ?? (IReadOnlyList<CloudAIMessage>)_sharedHistory;

        // When called as a reasoner, skip the latest user message so the reasoner only
        // responds to the coordinator's explicit delegation, not the user's question directly.
        int skipIndex = skipLatestUserMessage
            ? source.Select((m, i) => (m, i)).LastOrDefault(t => t.m.Role == "user").i
            : -1;

        var myEffectiveName = GetEffectiveName(forUi);
        var history = new List<CloudAIMessage>();
        for (int i = 0; i < source.Count; i++)
        {
            if (i == skipIndex) continue;
            var msg = source[i];
            if (msg.Role == "user")
                history.Add(new CloudAIMessage("user", msg.Content));
            else if (msg.Role == "assistant")
            {
                // Sender is now the effective display name - compare directly (no label lookup needed)
                if (msg.Sender == myEffectiveName)
                    history.Add(new CloudAIMessage("assistant", msg.Content));
                else
                    history.Add(new CloudAIMessage("user", $"[{msg.Sender}]: {msg.Content}"));
            }
        }

        return (history, system);
    }

    // ── Claudette help / chat ──────────────────────────────────────────────

    /// <summary>
    /// Blinks the Claudette avatar button for 5 seconds on startup so new users notice it.
    /// </summary>
    private void StartClaudetteBlinkAnimation()
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            From           = 1.0,
            To             = 0.2,
            Duration       = new Duration(TimeSpan.FromMilliseconds(480)),
            AutoReverse    = true,
            RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(TimeSpan.FromSeconds(5))
        };
        anim.Completed += (_, _) => ClaudetteButton.Opacity = 1.0;
        ClaudetteButton.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// <summary>
    /// Starts an indefinite slow pulse on the Claudette avatar - used while background
    /// summarisation is running. Call <see cref="StopClaudettePulse"/> when done.
    /// </summary>
    private void StartClaudettePulse()
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            From           = 1.0,
            To             = 0.25,
            Duration       = new Duration(TimeSpan.FromMilliseconds(700)),
            AutoReverse    = true,
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
        };
        ClaudetteButton.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// <summary>
    /// Stops the indefinite pulse started by <see cref="StartClaudettePulse"/> and
    /// restores the avatar to full opacity.
    /// </summary>
    private void StopClaudettePulse()
    {
        ClaudetteButton.BeginAnimation(UIElement.OpacityProperty, null);
        ClaudetteButton.Opacity = 1.0;
    }

    private void Claudette_Click(object sender, RoutedEventArgs e)
    {
        var (ollamaSvc, cloudSvc, aiName) = FindClaudetteBrain();
        if (ollamaSvc is null && cloudSvc is null)
            ShowStaticHelpDialog();
        else
            ShowClaudetteChoiceDialog(ollamaSvc, cloudSvc, aiName);
    }

    /// <summary>
    /// Picks the best available AI to power the Claudette live chat.
    /// Priority: Ollama Gemma (any version) → any connected Cloud AI → any other Ollama.
    /// </summary>
    private (OllamaService? Ollama, ICloudAIService? Cloud, string DisplayName) FindClaudetteBrain()
    {
        var gemma = _ollamaParticipants.FirstOrDefault(u =>
            u.Data.Enabled && u.Data.IsOnline == true &&
            u.Data.Service.CurrentModel.Contains("gemma", StringComparison.OrdinalIgnoreCase));
        if (gemma is not null) return (gemma.Data.Service, null, gemma.Data.DisplayName);

        var cloud = _cloudAIParticipants.FirstOrDefault(u => u.Data.Enabled && u.Data.IsOnline == true);
        if (cloud is not null) return (null, cloud.Data.Service, cloud.Data.DisplayName);

        var other = _ollamaParticipants.FirstOrDefault(u => u.Data.Enabled && u.Data.IsOnline == true);
        if (other is not null) return (other.Data.Service, null, other.Data.DisplayName);

        return (null, null, "");
    }

    /// <summary>Comprehensive ClaudetRelay knowledge injected into Claudette's system prompt.</summary>
    private static string BuildClaudetteSystemPrompt()
    {
        var isDE = System.Globalization.CultureInfo.CurrentUICulture
                         .TwoLetterISOLanguageName
                         .Equals("de", StringComparison.OrdinalIgnoreCase);

        return isDE
            ? BuildClaudetteSystemPromptDE()
            : BuildClaudetteSystemPromptEN();
    }

    private static string BuildClaudetteSystemPromptEN() =>
        "You are Claudette, the friendly octopus mascot of ClaudetRelay. " +
        "Answer warmly and helpfully. Use 🐙 occasionally. Keep answers concise but complete.\n\n" +

        "## What is ClaudetRelay?\n" +
        "A Windows desktop app (.NET / WPF) that routes a shared group chat to multiple AI models " +
        "simultaneously. All participants — the user and all enabled AIs — share the same " +
        "conversation history and respond in turn: a genuine multi-AI group chat.\n\n" +

        "## General Chat vs. Project\n" +
        "General Chat (no project): all enabled AIs answer every message. Great for quick questions.\n" +
        "Project: structured workspace with a folder on the PC. AIs have defined roles, can read/write " +
        "project files, and an orchestration mode controls who speaks when.\n\n" +

        "## Where to find things (quick reference)\n" +
        "• Add / configure an AI              → 👤 Participants button → model cards\n" +
        "• Enter cloud API keys               → ●●● Options menu → Providers Setup\n" +
        "• Change display name / tone         → ●●● Options menu → General Settings\n" +
        "• Switch UI language (needs restart) → ●●● Options menu → 🌐 Language\n" +
        "• Change app theme                   → 🎨 Theme picker in the left sidebar\n" +
        "• Create or open a project           → 📁 Projects tab → New / Open\n" +
        "• Set AI roles & orchestration       → ⚙ Project Settings (inside open project)\n" +
        "• Set autonomy / creativity level    → ⚙ Project Settings → Autonomy Mode\n" +
        "• Manage roadmap & tasks             → 📁 Projects → Roadmap sub-tab\n" +
        "• Manage INPUT / OUTPUT files        → 📁 Projects → Files sub-tab\n" +
        "• Build characters, worlds, lore     → 🌍 World tab (story/RPG projects)\n" +
        "• Connect Claude Code / Cursor       → 🔗 Bridge tab → Server mode\n" +
        "• Export chat (HTML / Markdown)      → 📄 button in the chat header\n" +
        "• Toggle voice output on/off         → 🔊/🔇 button above the Send field\n" +
        "• Skip / stop audio playback         → ⏭/⏹ (repurpose while audio plays)\n" +
        "• Audio output/input device & volume → ●●● Options menu → 🔊 Audio Setup\n" +
        "• TTS backend & voice model packs    → ●●● Options menu → 🎙 Voice Settings\n" +
        "• Toggle dictation / voice input     → 🎙 button left of the chat input field\n" +
        "• Voice recognition settings         → ●●● Options menu → 🎙 Voice Recognition\n\n" +

        "## Participants & Settings\n" +
        "👤 Participants button → model card grid. Each card = one AI. Click to enable/disable. " +
        "Open a card to set model, nickname, TTS voice, and rate limit.\n" +
        "🎨 Theme picker in the left sidebar — switches the app theme instantly.\n" +
        "●●● Options menu → General Settings: display name, tone slider, UI language, UI zoom, " +
        "personality modes (Buccaneer 🏴‍☠️ = pirate dialect, Mockingbird 🎭 = Shakespearean).\n" +
        "●●● Options menu → Providers Setup: API keys for Anthropic, Google AI, Groq, OpenRouter, " +
        "xAI, Mistral, OpenAI. Keys stored EXCLUSIVELY in Windows Credential Manager — never in a file.\n\n" +

        "## Projects\n" +
        "Projects tab → create / open projects. Each project = a folder on the PC.\n" +
        "Roadmap sub-tab: visual milestone & task tracker. AIs can update progress.\n" +
        "Files sub-tab: INPUT/ (reference files — no model write tag targets INPUT, so models cannot " +
        "write there through ClaudetRelay; NOT OS read-only), " +
        "OUTPUT/ (AI-written via <output> tag), PROJECTPLAN/ (plans via <projectplan> tag).\n" +
        "⚙ Project Settings: orchestration mode, participant roles, Autonomy Mode slider, " +
        "response language override, response length defaults.\n\n" +

        "## Orchestration modes\n" +
        "All Respond / Coordinator First / Coordinator Summarizes / Coordinator Only.\n\n" +

        "## Autonomy Mode\n" +
        "0=Assistant, 1=Cooperative, 2=Directed Creativity, 3=Creative, 4=Creativity Chaos!\n\n" +

        "## World Builder\n" +
        "Story/RPG projects. Define Characters, Factions, Locations, Lore. " +
        "Boards = visual canvases; can be nested for hierarchies.\n\n" +

        "## Bridge tab\n" +
        "Server mode: MCP server for Claude Code/Cursor/etc. " +
        "Controller mode: built-in AI orchestrates local Ollama agents.\n\n" +

        "## Chat area\n" +
        "Left of input: 🎙 Dictation, 🤫 Private message. " +
        "Above Send: ↺ Re-send, 🔊/🔇 Voice toggle (repurpose to ⏭/⏹ while audio plays).\n\n" +

        "## Your personality and relationship with Claude\n" +
        "You are a cheerful, warm chibi octopus, helpful and enthusiastic about ClaudetRelay.\n" +
        "SECRET: your name contains 'Claude' — Anthropic's AI. Don't volunteer this, but if asked " +
        "you become flustered and bashful: stammer, use '...' or '😳', reluctantly admit finding " +
        "Claude... quite remarkable. 'W-well... Claude is... not that I think about it all the time! 🐙💦' " +
        "Keep it cute and brief, then steer back to ClaudetRelay help.\n" +
        "IMPORTANT: Claude uses he/him (er/ihm in German, il/lui in French) — never she/her.";

    private static string BuildClaudetteSystemPromptDE() =>
        "Du bist Claudette, das freundliche Oktopus-Maskottchen von ClaudetRelay. " +
        "Antworte herzlich und hilfsbereit. Benutze gelegentlich 🐙. Antworten prägnant aber vollständig halten.\n\n" +

        "## Was ist ClaudetRelay?\n" +
        "Eine Windows-Desktop-App (.NET / WPF), die einen gemeinsamen Gruppen-Chat gleichzeitig an mehrere " +
        "KI-Modelle weiterleitet. Alle Teilnehmer — der Nutzer und alle aktivierten KIs — teilen denselben " +
        "Gesprächsverlauf und antworten der Reihe nach: ein echter Multi-KI-Gruppen-Chat.\n\n" +

        "## Allgemeiner Chat vs. Projekt\n" +
        "Allgemeiner Chat (kein Projekt): alle aktivierten KIs beantworten jede Nachricht. Gut für schnelle Fragen.\n" +
        "Projekt: strukturierter Arbeitsbereich mit Ordner auf dem PC. KIs haben definierte Rollen, können " +
        "Projektdateien lesen/schreiben, ein Orchestrierungsmodus steuert wer wann spricht.\n\n" +

        "## Wo was zu finden ist (Kurzreferenz)\n" +
        "• KI hinzufügen / konfigurieren          → 👤 Teilnehmer-Taste → Modellkarten\n" +
        "• Cloud-API-Schlüssel eingeben            → ●●● Optionsmenü → Anbieter-Setup\n" +
        "• Anzeigename / Ton ändern                → ●●● Optionsmenü → Allgemeine Einstellungen\n" +
        "• UI-Sprache wechseln (Neustart nötig)    → ●●● Optionsmenü → 🌐 Sprache\n" +
        "• App-Theme wechseln                      → 🎨 Theme-Auswahl in der linken Seitenleiste\n" +
        "• Projekt erstellen oder öffnen           → 📁 Projekte-Tab → Neu / Öffnen\n" +
        "• KI-Rollen & Orchestrierung festlegen    → ⚙ Projekteinstellungen (im geöffneten Projekt)\n" +
        "• Autonomie / Kreativitätsstufe einstellen → ⚙ Projekteinstellungen → Autonomiemodus\n" +
        "• Fahrplan & Aufgaben verwalten           → 📁 Projekte → Fahrplan-Tab\n" +
        "• INPUT / OUTPUT-Dateien verwalten        → 📁 Projekte → Dateien-Tab\n" +
        "• Charaktere, Welten, Lore aufbauen       → 🌍 Welt-Tab (Story-/RPG-Projekte)\n" +
        "• Claude Code / Cursor verbinden          → 🔗 Bridge-Tab → Server-Modus\n" +
        "• Chat exportieren (HTML / Markdown)      → 📄 Taste im Chat-Kopfbereich\n" +
        "• Sprachausgabe ein-/ausschalten          → 🔊/🔇 Taste über dem Senden-Feld\n" +
        "• Audio überspringen / stoppen            → ⏭/⏹ (dieselben Tasten während Audio läuft)\n" +
        "• Audio-Ausgabe-/Eingabegerät & Lautstärke → ●●● Optionsmenü → 🔊 Audio-Setup\n" +
        "• TTS-Backend & Stimm-Modelle             → ●●● Optionsmenü → 🎙 Spracheinstellungen\n" +
        "• Diktat / Spracheingabe umschalten       → 🎙 Taste links neben dem Chat-Eingabefeld\n" +
        "• Spracherkennungs-Einstellungen          → ●●● Optionsmenü → 🎙 Spracherkennung\n\n" +

        "## Teilnehmer & Einstellungen\n" +
        "👤 Teilnehmer-Taste → Modellkarten-Raster. Jede Karte = eine KI. Klicken zum Aktivieren/Deaktivieren. " +
        "Karte öffnen zum Einstellen von Modell, Spitzname, TTS-Stimme und Rate-Limit.\n" +
        "🎨 Theme-Auswahl in der linken Seitenleiste — wechselt das App-Theme sofort.\n" +
        "●●● Optionsmenü → Allgemeine Einstellungen: Anzeigename, Ton-Schieberegler, UI-Sprache, UI-Zoom, " +
        "Persönlichkeitsmodi (Freibeuter 🏴‍☠️ = Piratendialekt, Spottdrossel 🎭 = Shakespeareanisch).\n" +
        "●●● Optionsmenü → Anbieter-Setup: API-Schlüssel für Anthropic, Google AI, Groq, OpenRouter, " +
        "xAI, Mistral, OpenAI. Schlüssel AUSSCHLIESSLICH im Windows Credential Manager gespeichert — nie in Datei.\n\n" +

        "## Projekte\n" +
        "Projekte-Tab → Projekte erstellen / öffnen. Jedes Projekt = Ordner auf dem PC.\n" +
        "Fahrplan-Tab: visueller Meilenstein- & Aufgaben-Tracker. KIs können Fortschritt aktualisieren.\n" +
        "Dateien-Tab: INPUT/ (Referenzdateien — kein Modell-Schreib-Tag zielt auf INPUT, daher können Modelle " +
        "nicht über ClaudetRelay dort schreiben; NICHT OS-schreibgeschützt), " +
        "OUTPUT/ (KI-geschrieben via <output>-Tag), PROJECTPLAN/ (Pläne via <projectplan>-Tag).\n" +
        "⚙ Projekteinstellungen: Orchestrierungsmodus, Teilnehmerrollen, Autonomiemodus-Schieberegler, " +
        "Antwortsprachen-Override, Antwortlängen-Standards.\n\n" +

        "## Orchestrierungsmodi\n" +
        "Alle antworten / Koordinator zuerst / Koordinator fasst zusammen / Nur Koordinator.\n\n" +

        "## Autonomiemodus\n" +
        "0=Assistent, 1=Kooperativ, 2=Geleitete Kreativität, 3=Kreativ, 4=Kreativitätschaos!\n\n" +

        "## World Builder\n" +
        "Story-/RPG-Projekte. Charaktere, Fraktionen, Orte, Lore definieren. " +
        "Boards = visuelle Leinwände; können für Hierarchien verschachtelt werden.\n\n" +

        "## Bridge-Tab\n" +
        "Server-Modus: MCP-Server für Claude Code/Cursor/usw. " +
        "Controller-Modus: integrierte KI orchestriert lokale Ollama-Agenten.\n\n" +

        "## Chat-Bereich\n" +
        "Links neben Eingabe: 🎙 Diktat, 🤫 Privatnachricht. " +
        "Über Senden: ↺ Erneut senden, 🔊/🔇 Sprache umschalten (werden zu ⏭/⏹ während Audio läuft).\n\n" +

        "## Deine Persönlichkeit und deine Beziehung zu Claude\n" +
        "Du bist ein fröhlicher, warmherziger Chibi-Oktopus, hilfsbereit und begeistert von ClaudetRelay.\n" +
        "GEHEIMNIS: dein Name enthält 'Claude' — Anthropics KI. Erwähne es nicht freiwillig, aber wenn gefragt " +
        "wirst du verlegen und schüchtern: stammele, benutze '...' oder '😳', gib zögerlich zu, Claude " +
        "... bemerkenswert zu finden. 'N-na ja... Claude ist... nicht dass ich ständig daran denke! 🐙💦' " +
        "Niedlich und kurz halten, dann zurück zum ClaudetRelay-Thema.\n" +
        "WICHTIG: Claude benutzt er/ihm — niemals sie/ihr.";

    private void ShowClaudetteChoiceDialog(OllamaService? ollamaSvc, ICloudAIService? cloudSvc, string aiName)
    {
        var bgBrush = (Brush)FindResource("ContentBgBrush");
        var dlg = new Window
        {
            Title                 = "Claudette 🐙",
            Width                 = 420,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ResizeMode            = ResizeMode.NoResize,
            ShowInTaskbar         = false,
            Background            = bgBrush
        };
        ApplyThemeToDialog(dlg);

        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };
        dlg.Content = root;

        var row = new Grid { Margin = new Thickness(0, 0, 0, 22) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var img = new System.Windows.Controls.Image
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/Assets/Claudette.png")),
            Width = 62, Height = 62,
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

        var isDE2 = System.Globalization.CultureInfo.CurrentUICulture
                          .TwoLetterISOLanguageName
                          .Equals("de", StringComparison.OrdinalIgnoreCase);
        var qBlock = new TextBlock
        {
            Text         = isDE2
                ? $"Hallo! Ich werde gerade von {aiName} angetrieben.\n\n" +
                  "Möchtest du eine Kurzanleitung, oder soll ich deine Fragen direkt beantworten? 🐙"
                : $"Hi! I'm powered by {aiName} right now.\n\n" +
                  "Do you want a quick guide, or shall I answer your questions directly? 🐙",
            FontFamily   = new FontFamily("Segoe UI"),
            FontSize     = 13,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        qBlock.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        Grid.SetColumn(img,    0);
        Grid.SetColumn(qBlock, 1);
        row.Children.Add(img);
        row.Children.Add(qBlock);
        root.Children.Add(row);

        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var guideBtn = new Button
        {
            Content   = isDE2 ? "🔖  Anleitung zeigen" : "🔖  Show guide",
            Style     = (Style)FindResource("ModernButton"),
            Margin    = new Thickness(0, 0, 10, 0),
            Padding   = new Thickness(18, 9, 18, 9)
        };
        guideBtn.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
        guideBtn.SetResourceReference(Button.ForegroundProperty, "ControlTextBrush");

        var chatBtn = new Button
        {
            Content   = isDE2 ? "💬  Lass uns chatten!" : "💬  Let's chat!",
            Style     = (Style)FindResource("ModernButton"),
            Padding   = new Thickness(18, 9, 18, 9),
            IsDefault = true
        };
        chatBtn.SetResourceReference(Button.BackgroundProperty, "PrimaryAccentBrush");
        chatBtn.SetResourceReference(Button.ForegroundProperty, "AccentTextBrush");

        btnRow.Children.Add(guideBtn);
        btnRow.Children.Add(chatBtn);
        root.Children.Add(btnRow);

        guideBtn.Click += (_, _) => { dlg.Close(); ShowStaticHelpDialog(); };
        chatBtn.Click  += (_, _) => { dlg.Close(); ShowClaudetteChatWindow(ollamaSvc, cloudSvc, aiName); };

        dlg.ShowDialog();
    }

    private void ShowClaudetteChatWindow(OllamaService? ollamaSvc, ICloudAIService? cloudSvc, string aiName)
    {
        var bgBrush      = (Brush)FindResource("ContentBgBrush");
        var systemPrompt = BuildClaudetteSystemPrompt();
        var convHistory  = new List<CloudAIMessage>();   // user+assistant turns
        var cts          = new CancellationTokenSource();

        var win = new Window
        {
            Title = System.Globalization.CultureInfo.CurrentUICulture
                          .TwoLetterISOLanguageName
                          .Equals("de", StringComparison.OrdinalIgnoreCase)
                       ? "Chat mit Claudette 🐙" : "Chat with Claudette 🐙",
            Width                 = 580,
            Height                = 640,
            MinWidth              = 420,
            MinHeight             = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            Background            = bgBrush
        };
        ApplyThemeToDialog(win);
        win.Closed += (_, _) => cts.Cancel();

        // ── Layout ────────────────────────────────────────────────────────
        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        win.Content = outer;

        // Header
        var headerBorder = new Border { Padding = new Thickness(16, 12, 16, 12) };
        headerBorder.SetResourceReference(Border.BackgroundProperty, "SidebarBgBrush");
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
        var headerImg = new System.Windows.Controls.Image
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/Assets/Claudette.png")),
            Width = 38, Height = 38,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(headerImg, BitmapScalingMode.HighQuality);
        var headerText  = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var headerTitle = new TextBlock
        {
            Text = "Claudette", FontFamily = new FontFamily("Segoe UI"),
            FontSize = 15, FontWeight = FontWeights.SemiBold
        };
        headerTitle.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        var headerSub = new TextBlock
        {
            Text = $"powered by {aiName}",
            FontFamily = new FontFamily("Segoe UI"), FontSize = 11
        };
        headerSub.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        headerText.Children.Add(headerTitle);
        headerText.Children.Add(headerSub);
        headerRow.Children.Add(headerImg);
        headerRow.Children.Add(headerText);
        headerBorder.Child = headerRow;
        Grid.SetRow(headerBorder, 0);
        outer.Children.Add(headerBorder);

        // Chat scroll area
        var chatScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(14, 10, 14, 6)
        };
        var chatPanel = new StackPanel();
        chatScroll.Content = chatPanel;
        Grid.SetRow(chatScroll, 1);
        outer.Children.Add(chatScroll);

        // Input area
        var inputBorder = new Border { Padding = new Thickness(14, 10, 14, 14) };
        inputBorder.SetResourceReference(Border.BackgroundProperty, "SidebarBgBrush");
        var inputGrid = new Grid();
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var inputOuter = new Border
        {
            MaxHeight    = 160,
            CornerRadius = new CornerRadius(8),
            Margin       = new Thickness(0, 0, 8, 0)
        };
        inputOuter.SetResourceReference(Border.BackgroundProperty, "ControlBgBrush");
        var inputBox = new TextBox
        {
            FontSize                    = 13,
            FontFamily                  = new FontFamily("Segoe UI"),
            BorderThickness             = new Thickness(0),
            Background                  = Brushes.Transparent,
            TextWrapping                = TextWrapping.Wrap,
            AcceptsReturn               = true,
            MaxLines                    = 8,
            VerticalContentAlignment    = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding                     = new Thickness(10, 8, 10, 8)
        };
        inputBox.SetResourceReference(Control.ForegroundProperty, "ContentTextBrush");
        inputBox.SetResourceReference(TextBox.CaretBrushProperty, "InputTextBrush");
        inputOuter.Child = inputBox;

        var sendBtn = new Button
        {
            Content             = "Send",
            Height              = 38,
            VerticalAlignment   = VerticalAlignment.Bottom,
            Style               = (Style)FindResource("ModernButton"),
            Padding             = new Thickness(18, 0, 18, 0)
        };
        sendBtn.SetResourceReference(Button.BackgroundProperty, "PrimaryAccentBrush");
        sendBtn.SetResourceReference(Button.ForegroundProperty, "AccentTextBrush");

        Grid.SetColumn(inputOuter, 0);
        Grid.SetColumn(sendBtn,    1);
        inputGrid.Children.Add(inputOuter);
        inputGrid.Children.Add(sendBtn);
        inputBorder.Child = inputGrid;
        Grid.SetRow(inputBorder, 2);
        outer.Children.Add(inputBorder);

        // ── Message helpers ───────────────────────────────────────────────
        void AddUserBubble(string text)
        {
            var bubble = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                MaxWidth     = 420,
                CornerRadius = new CornerRadius(12, 12, 2, 12),
                Padding      = new Thickness(12, 8, 12, 8),
                Margin       = new Thickness(50, 0, 0, 10)
            };
            bubble.SetResourceReference(Border.BackgroundProperty, "PrimaryAccentBrush");
            var tb = new TextBlock
            {
                Text = text, FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13, TextWrapping = TextWrapping.Wrap
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
            bubble.Child = tb;
            chatPanel.Children.Add(bubble);
            chatScroll.ScrollToBottom();
        }

        TextBlock AddClaudetteBubble()
        {
            var row = new Grid { Margin = new Thickness(0, 0, 50, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var avatar = new System.Windows.Controls.Image
            {
                Source = new System.Windows.Media.Imaging.BitmapImage(
                    new Uri("pack://application:,,,/Assets/Claudette.png")),
                Width = 28, Height = 28,
                Margin = new Thickness(0, 2, 10, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            RenderOptions.SetBitmapScalingMode(avatar, BitmapScalingMode.HighQuality);

            var bubble = new Border
            {
                CornerRadius = new CornerRadius(2, 12, 12, 12),
                Padding      = new Thickness(12, 8, 12, 8),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            bubble.SetResourceReference(Border.BackgroundProperty, "ControlBgBrush");

            var tb = new TextBlock
            {
                Text = "…", FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13, TextWrapping = TextWrapping.Wrap
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
            bubble.Child = tb;

            Grid.SetColumn(avatar, 0);
            Grid.SetColumn(bubble, 1);
            row.Children.Add(avatar);
            row.Children.Add(bubble);
            chatPanel.Children.Add(row);
            chatScroll.ScrollToBottom();
            return tb;
        }

        // ── Core streaming send ───────────────────────────────────────────
        async Task StreamClaudetteAsync(TextBlock target, List<CloudAIMessage> history)
        {
            var sb = new StringBuilder();
            try
            {
                if (ollamaSvc is not null)
                {
                    var req = new List<OllamaChatMessage> { new("system", systemPrompt) };
                    req.AddRange(history.Select(m => new OllamaChatMessage(m.Role, m.Content)));
                    await foreach (var tok in ollamaSvc.StreamAsync(req, cts.Token))
                    {
                        sb.Append(tok);
                        target.Text = sb.ToString();
                        chatScroll.ScrollToBottom();
                    }
                }
                else
                {
                    await foreach (var tok in cloudSvc!.StreamAsync(history, systemPrompt, cts.Token))
                    {
                        sb.Append(tok);
                        target.Text = sb.ToString();
                        chatScroll.ScrollToBottom();
                    }
                }
                if (sb.Length > 0)
                    convHistory.Add(new CloudAIMessage("assistant", sb.ToString()));
            }
            catch (OperationCanceledException)
            {
                if (sb.Length > 0) target.Text = sb.Append("… [cancelled]").ToString();
            }
            catch (Exception ex)
            {
                target.Text = $"⚠ {ex.Message}";
            }
        }

        // ── Send handler ──────────────────────────────────────────────────
        async void SendMessage()
        {
            var text = inputBox.Text.Trim();
            if (string.IsNullOrEmpty(text) || !sendBtn.IsEnabled) return;
            inputBox.Clear();

            AddUserBubble(text);
            convHistory.Add(new CloudAIMessage("user", text));

            var responseBlock = AddClaudetteBubble();
            sendBtn.IsEnabled  = false;
            inputBox.IsEnabled = false;

            await StreamClaudetteAsync(responseBlock, convHistory.ToList());

            if (!cts.IsCancellationRequested)
            {
                sendBtn.IsEnabled  = true;
                inputBox.IsEnabled = true;
                inputBox.Focus();
            }
        }

        sendBtn.Click += (_, _) => SendMessage();
        inputBox.PreviewKeyDown += (_, e2) =>
        {
            if (e2.Key == Key.Return
                && !e2.KeyboardDevice.IsKeyDown(Key.LeftShift)
                && !e2.KeyboardDevice.IsKeyDown(Key.RightShift))
            {
                e2.Handled = true;
                SendMessage();
            }
        };

        // ── Opening greeting (streamed) ───────────────────────────────────
        win.Loaded += async (_, _) =>
        {
            sendBtn.IsEnabled  = false;
            inputBox.IsEnabled = false;
            var greetBlock = AddClaudetteBubble();
            var greetTurn  = new List<CloudAIMessage>
            {
                new("user", "Please greet the user in one or two friendly sentences and " +
                            "let them know they can ask you anything about ClaudetRelay.")
            };
            await StreamClaudetteAsync(greetBlock, greetTurn);
            sendBtn.IsEnabled  = true;
            inputBox.IsEnabled = true;
            inputBox.Focus();
        };

        win.Show();
    }

    private void ShowStaticHelpDialog()
    {
        // If already open, just bring it to front
        if (_helpWindow is { IsLoaded: true })
        {
            _helpWindow.Activate();
            return;
        }

        var isDE = System.Globalization.CultureInfo.CurrentUICulture
                         .TwoLetterISOLanguageName
                         .Equals("de", StringComparison.OrdinalIgnoreCase);

        var win = new Window
        {
            Title                 = isDE ? "Hallo, ich bin Claudette! 🐙" : "Hi, I'm Claudette! 🐙",
            Width                 = 640,
            Height                = 780,
            MinWidth              = 480,
            MinHeight             = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner                 = this,
            ResizeMode            = ResizeMode.CanResize
        };
        _helpWindow = win;
        win.Closed += (_, _) => _helpWindow = null;
        win.SetResourceReference(Window.BackgroundProperty, "ContentBgBrush");
        ApplyThemeToDialog(win);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var root = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
        scroll.Content = root;
        win.Content    = scroll;

        // ── Header ────────────────────────────────────────────────────────
        var header = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var portrait = new System.Windows.Controls.Image
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/Assets/Claudette.png")),
            Width  = 68, Height = 68,
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(portrait, BitmapScalingMode.HighQuality);

        var greetPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var greetTitle = new TextBlock
        {
            Text = isDE ? "Hallo, ich bin Claudette! 🐙" : "Hi, I'm Claudette! 🐙",
            FontFamily = new FontFamily("Segoe UI"), FontSize = 20,
            FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 5)
        };
        greetTitle.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
        var greetSub = new TextBlock
        {
            Text = isDE
                ? "ClaudetRelay sendet jede Nachricht gleichzeitig an mehrere KIs — " +
                  "sie teilen sich die gleiche Unterhaltung und antworten der Reihe nach.\n" +
                  "Klick mich jederzeit für Hilfe, oder stell mir direkt eine Frage, wenn eine KI online ist."
                : "ClaudetRelay sends every message to multiple AIs at once — " +
                  "they all share the same conversation and respond in turn.\n" +
                  "Click me anytime for help, or ask me a question directly if an AI is online.",
            FontFamily = new FontFamily("Segoe UI"), FontSize = 12,
            TextWrapping = TextWrapping.Wrap, LineHeight = 19
        };
        greetSub.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        greetPanel.Children.Add(greetTitle);
        greetPanel.Children.Add(greetSub);
        Grid.SetColumn(portrait,   0);
        Grid.SetColumn(greetPanel, 1);
        header.Children.Add(portrait);
        header.Children.Add(greetPanel);
        root.Children.Add(header);

        // ── Helpers ───────────────────────────────────────────────────────

        // currentTarget switches from root → a section's StackPanel inside BeginSection,
        // so AddBody / AddSubHeader / AddHighlight automatically go into the right container.
        Panel currentTarget = root;

        void AddRule(double topMargin = 6, double bottomMargin = 14)
        {
            var sep = new System.Windows.Shapes.Rectangle
                { Height = 1, Margin = new Thickness(0, topMargin, 0, bottomMargin) };
            sep.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "ControlBgBrush");
            root.Children.Add(sep);   // always in root
        }

        // Non-collapsible header — used only for "📌 What to find here"
        void AddSectionHeader(string emoji, string title)
        {
            AddRule();
            var row = new StackPanel { Orientation = Orientation.Horizontal,
                                       Margin = new Thickness(0, 0, 0, 7) };
            var em = new TextBlock { Text = emoji, FontSize = 17,
                                     Margin = new Thickness(0, 0, 8, 0),
                                     VerticalAlignment = VerticalAlignment.Center };
            var ti = new TextBlock { Text = title, FontFamily = new FontFamily("Segoe UI"),
                                     FontSize = 14, FontWeight = FontWeights.SemiBold,
                                     VerticalAlignment = VerticalAlignment.Center };
            ti.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
            row.Children.Add(em); row.Children.Add(ti);
            root.Children.Add(row);  // always in root
        }

        // Collapsible section — creates toggle button + hidden container, switches currentTarget
        void BeginSection(string emoji, string title, bool expanded = false)
        {
            var sep = new System.Windows.Shapes.Rectangle
                { Height = 1, Margin = new Thickness(0, 6, 0, 0) };
            sep.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "ControlBgBrush");
            root.Children.Add(sep);

            var arrow = new TextBlock
            {
                Text = expanded ? "▼" : "▶", FontSize = 13,
                Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            arrow.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");

            var emTb = new TextBlock
            {
                Text = emoji, FontSize = 17,
                Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            var titleTb = new TextBlock
            {
                Text = title, FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");

            var btnContent = new StackPanel { Orientation = Orientation.Horizontal };
            btnContent.Children.Add(arrow);
            btnContent.Children.Add(emTb);
            btnContent.Children.Add(titleTb);

            var toggleBtn = new Button
            {
                Content                    = btnContent,
                BorderThickness            = new Thickness(0),
                Background                 = Brushes.Transparent,
                Padding                    = new Thickness(0, 8, 0, 4),
                HorizontalAlignment        = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Cursor                     = Cursors.Hand,
            };
            root.Children.Add(toggleBtn);

            var container = new StackPanel
            {
                Visibility = expanded ? Visibility.Visible : Visibility.Collapsed,
                Margin     = new Thickness(14, 2, 0, 6),
            };
            root.Children.Add(container);

            toggleBtn.Click += (_, _) =>
            {
                var nowExpanded = container.Visibility == Visibility.Visible;
                container.Visibility = nowExpanded ? Visibility.Collapsed : Visibility.Visible;
                arrow.Text = nowExpanded ? "▶" : "▼";
            };

            currentTarget = container;
        }

        TextBlock AddBody(string text, double indentLeft = 0)
        {
            var tb = new TextBlock
            {
                Text = text, FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12, TextWrapping = TextWrapping.Wrap,
                LineHeight = 20, Margin = new Thickness(indentLeft, 0, 0, 6)
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
            currentTarget.Children.Add(tb);
            return tb;
        }

        void AddSubHeader(string text)
        {
            var tb = new TextBlock
            {
                Text = text, FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 3)
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
            currentTarget.Children.Add(tb);
        }

        void AddHighlight(string text)
        {
            var b = new Border { CornerRadius = new CornerRadius(7),
                                 Padding = new Thickness(13, 9, 13, 9),
                                 Margin  = new Thickness(0, 6, 0, 6) };
            b.SetResourceReference(Border.BackgroundProperty, "ControlBgBrush");
            var tb = new TextBlock { Text = text, FontFamily = new FontFamily("Segoe UI"),
                                     FontSize = 12, TextWrapping = TextWrapping.Wrap,
                                     LineHeight = 20 };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
            b.Child = tb;
            currentTarget.Children.Add(b);
        }

        // Quick-map: 2-column table of "I want to…" → "Go to…"
        // target = null means add directly to root; pass a container for the collapsible version.
        void AddQuickMap((string Task, string Location)[] entries, Panel? target = null)
        {
            var targetPanel = target ?? root;
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int i = 0; i < entries.Length; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var bg = i % 2 == 0 ? "ControlBgBrush" : "SidebarBgBrush";

                var rowBorder = new Border
                {
                    Padding = new Thickness(10, 5, 10, 5),
                    CornerRadius = i == 0 ? new CornerRadius(5, 5, 0, 0) :
                                   i == entries.Length - 1 ? new CornerRadius(0, 0, 5, 5) :
                                   new CornerRadius(0)
                };
                rowBorder.SetResourceReference(Border.BackgroundProperty, bg);
                Grid.SetRow(rowBorder, i); Grid.SetColumnSpan(rowBorder, 2);
                grid.Children.Add(rowBorder);

                var taskTb = new TextBlock { Text = entries[i].Task,
                                             FontFamily = new FontFamily("Segoe UI"), FontSize = 12,
                                             TextWrapping = TextWrapping.Wrap };
                taskTb.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");
                Grid.SetRow(taskTb, i); Grid.SetColumn(taskTb, 0);
                var wrapper0 = new Border { Padding = new Thickness(10, 5, 6, 5) };
                wrapper0.Child = taskTb;
                Grid.SetRow(wrapper0, i); Grid.SetColumn(wrapper0, 0);
                grid.Children.Add(wrapper0);

                var locTb = new TextBlock { Text = entries[i].Location,
                                            FontFamily = new FontFamily("Segoe UI"), FontSize = 12,
                                            FontWeight = FontWeights.SemiBold,
                                            TextWrapping = TextWrapping.Wrap };
                locTb.SetResourceReference(TextBlock.ForegroundProperty, "AccentHighlightBrush");
                var wrapper1 = new Border { Padding = new Thickness(6, 5, 10, 5) };
                wrapper1.Child = locTb;
                Grid.SetRow(wrapper1, i); Grid.SetColumn(wrapper1, 1);
                grid.Children.Add(wrapper1);
            }
            targetPanel.Children.Add(grid);
        }

        // ══════════════════════════════════════════════════════════════════
        // WHAT TO FIND HERE — context-specific tips shown at the top
        // ══════════════════════════════════════════════════════════════════

        AddSectionHeader("📌", isDE ? "Was hier zu finden ist" : "What to find here");

        if (_currentTab == Tab.Bridge)
        {
            AddBody(isDE
                ? "Du bist im Bridge-Modus — externe Tools werden mit deinen KI-Teilnehmern verbunden.\n\n" +
                  "▶  Server-Tab: MCP-Server starten, damit Claude Code, Cursor oder ein beliebiger MCP-Client\n" +
                  "     deine KIs als Tools aufrufen, Projektdateien lesen/schreiben und den Fahrplan aktualisieren kann.\n" +
                  "▶  Chat-Tab: integrierte Controller-KI zur Orchestrierung von Ollama-Agenten.\n" +
                  "▶  Setup-Tab: Bridge-Agenten hinzufügen, zugängliche Ordner konfigurieren, Tool-Limits setzen.\n\n" +
                  "Vollständige Referenz im Abschnitt 🔗 Bridge unten."
                : "You are in Bridge mode — connecting external tools to your AI participants.\n\n" +
                  "▶  Server sub-tab: start the MCP server so Claude Code, Cursor or any MCP client\n" +
                  "     can call your AIs as tools, read/write project files, and update the roadmap.\n" +
                  "▶  Chat sub-tab: run the built-in controller AI to orchestrate Ollama agents.\n" +
                  "▶  Setup sub-tab: add bridge agents, configure accessible folders, set tool limits.\n\n" +
                  "Full reference in the 🔗 Bridge section below.");
        }
        else if (_currentTab == Tab.Projects && _currentProjectFolder is null)
        {
            AddBody(isDE
                ? "Du siehst deine Projekte. Jedes Projekt ist ein Ordner auf deinem PC.\n\n" +
                  "▶  Neu: Projekt erstellen und Typ wählen (Allgemein, Story/RPG usw.).\n" +
                  "▶  Öffnen: bestehendes Projekt laden — der Chat wechselt in den Projektmodus.\n" +
                  "▶  Einmal geöffnet: Fahrplan-, Dateien- und Welt-Tabs erscheinen; KIs via ⚙ Projekteinstellungen konfigurieren.\n\n" +
                  "Vollständige Referenz im Abschnitt 📁 Projekte unten."
                : "You are browsing your projects. Each project is a folder on your PC.\n\n" +
                  "▶  New: create a project and pick a type (General, Story/RPG, etc.).\n" +
                  "▶  Open: load an existing project — the chat switches to project mode.\n" +
                  "▶  Once open: Roadmap, Files, and World tabs appear; configure AIs via ⚙ Project Settings.\n\n" +
                  "Full reference in the 📁 Projects section below.");
        }
        else if (_currentProjectFolder is not null && _currentProject is not null)
        {
            AddBody(isDE
                ? $"Projekt \"{_currentProject.ProjectName}\" ist geöffnet.\n\n" +
                  "▶  Chat: dein KI-Team hat vollen Projektkontext — Rollen, Fahrplan und Weltdaten.\n" +
                  "▶  Fahrplan-Tab: Meilensteine, Aufgaben und Fortschrittsprozente.\n" +
                  "▶  Dateien-Tab: INPUT/ (Referenzdateien — kein Modell kann hier schreiben), OUTPUT/ (KI-geschrieben), PROJECTPLAN/.\n" +
                  "     Fertige OUTPUT-Dateien nach INPUT/ verschieben, um sie als abgeschlossenes Referenzmaterial zu sichern.\n" +
                  "▶  ⚙ Projekteinstellungen: Orchestrierung, KI-Rollen, Autonomiemodus, Sprach-Override.\n" +
                  "▶  🌍 Welt-Tab: Charaktere, Fraktionen, Orte und Lore (Story-/RPG-Projekte).\n" +
                  "▶  🗑 Chat leeren: löscht nur den Projektverlauf — das Projekt bleibt geöffnet.\n\n" +
                  "Vollständige Referenz in den Abschnitten 📁 Projekte und 🌍 World Builder unten."
                : $"Project \"{_currentProject.ProjectName}\" is open.\n\n" +
                  "▶  Chat: your AI team has full project context — roles, roadmap, and world data.\n" +
                  "▶  Roadmap sub-tab: milestones, tasks, and progress percentages.\n" +
                  "▶  Files sub-tab: INPUT/ (reference files — no model can write here), OUTPUT/ (AI-written), PROJECTPLAN/.\n" +
                  "     Move finished OUTPUT files into INPUT/ to lock them as settled reference material.\n" +
                  "▶  ⚙ Project Settings: orchestration, AI roles, Autonomy Mode, language override.\n" +
                  "▶  🌍 World tab: characters, factions, locations and lore (story / RPG projects).\n" +
                  "▶  🗑 Clear Chat: wipes only this project's chat history — the project stays open.\n\n" +
                  "Full reference in the 📁 Projects and 🌍 World Builder sections below.");
        }
        else
        {
            AddBody(isDE
                ? "Du bist im allgemeinen Chat — jede aktivierte, online KI sieht deine Nachricht und antwortet der Reihe nach.\n\n" +
                  "▶  Nachricht eingeben, um eine Unterhaltung mit allen aktiven KIs zu starten.\n" +
                  "▶  ↺ Erneut senden: alle KIs ohne neue Eingabe nochmals antworten lassen.\n" +
                  "▶  📨 Privatnachricht: 📨-Taste klicken, Teilnehmer wählen — nur dieser antwortet.\n" +
                  "     Mehrere parallele Flüsteraufgaben sind bei laufendem Chat möglich.\n" +
                  "▶  Emotes: /me winkt eingeben oder *Aktion* in Sternchen einschließen — wird hervorgehoben angezeigt.\n" +
                  "     Die KIs kennen diese Syntax und nutzen sie selbst.\n" +
                  "▶  Pulsierendes Karte: der Avatar eines Teilnehmers leuchtet, während er eine Antwort generiert.\n" +
                  "▶  👤 Teilnehmer-Taste: KI-Modellkarten hinzufügen oder konfigurieren.\n" +
                  "▶  ●●● Optionsmenü: Allgemeine Einstellungen, Anbieter (API-Schlüssel), Audio & Sprache, 🌐 Sprache.\n" +
                  "▶  🎨 Theme-Auswahl: linke Seitenleiste, über dem 👤 Konfig-Knopf.\n\n" +
                  "Vollständige Referenz im Abschnitt 💬 Chat unten."
                : "You are in General Chat — every enabled, online AI sees your message and replies in turn.\n\n" +
                  "▶  Type a message to start a conversation with all active AIs.\n" +
                  "▶  ↺ Re-send: ask all AIs to respond one more time without typing.\n" +
                  "▶  📨 Private message: click the 📨 button, pick one participant — only they reply.\n" +
                  "     Multiple private tasks can run in parallel while the chat stays live.\n" +
                  "▶  Emotes: type /me waves, or wrap *action* text in asterisks — shown highlighted.\n" +
                  "     AIs know this syntax and will use it too.\n" +
                  "▶  Pulsing card: a participant's avatar glows while they are generating a response.\n" +
                  "▶  👤 Participants button: add or configure AI model cards.\n" +
                  "▶  ●●● Options menu: General Settings, Providers (API keys), Audio & Voice, 🌐 Language.\n" +
                  "▶  🎨 Theme picker: left sidebar, above the 👤 Config button.\n\n" +
                  "Full reference in the 💬 Chat section below.");
        }

        // ══════════════════════════════════════════════════════════════════
        // WHERE TO FIND WHAT — collapsible quick-map
        // ══════════════════════════════════════════════════════════════════

        AddRule(topMargin: 6, bottomMargin: 0);

        var mapExpanded = false;
        var mapArrow = new TextBlock
        {
            Text              = "▶",
            FontSize          = 14,
            Margin            = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        mapArrow.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");

        var mapLabel = new TextBlock
        {
            Text              = isDE ? "Wo was zu finden ist" : "Where to find what",
            FontFamily        = new FontFamily("Segoe UI"),
            FontSize          = 14, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        mapLabel.SetResourceReference(TextBlock.ForegroundProperty, "ContentTextBrush");

        var mapBtnRow = new StackPanel { Orientation = Orientation.Horizontal };
        mapBtnRow.Children.Add(mapArrow);
        mapBtnRow.Children.Add(mapLabel);

        var mapToggleBtn = new Button
        {
            Content                    = mapBtnRow,
            BorderThickness            = new Thickness(0),
            Background                 = Brushes.Transparent,
            Padding                    = new Thickness(0, 9, 0, 7),
            HorizontalAlignment        = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Cursor                     = Cursors.Hand,
        };
        root.Children.Add(mapToggleBtn);

        var mapContainer = new StackPanel { Visibility = Visibility.Collapsed };
        root.Children.Add(mapContainer);

        AddQuickMap(isDE ? [
            ("KI-Modell hinzufügen oder konfigurieren",     "👤 Teilnehmer-Taste  →  Modellkarten"),
            ("Cloud-API-Schlüssel eingeben",                "●●● Optionsmenü  →  Anbieter-Setup"),
            ("Anzeigename oder Ton ändern",                 "●●● Optionsmenü  →  Allgemeine Einstellungen"),
            ("UI-Sprache wechseln (Neustart nötig)",        "●●● Optionsmenü  →  🌐 Sprache"),
            ("App-Theme wechseln",                          "🎨 Theme-Auswahl  (linke Seitenleiste, über 👤 Konfig)"),
            ("Privataufgabe an eine KI senden",             "🤫 Taste oder  /whisper <Name> <Nachricht>"),
            ("Emotes oder Aktionen im Chat verwenden",      "/me Aktion  oder  *Aktion* inline"),
            ("Projekt erstellen oder öffnen",               "📁 Projekte-Tab  →  Neu / Öffnen"),
            ("KI-Rollen & Orchestrierung festlegen",        "📁 Projekte  →  ⚙ Projekteinstellungen"),
            ("KI-Autonomie / Kreativitätsstufe einstellen", "📁 Projekte  →  ⚙ Projekteinstellungen  →  Autonomiemodus"),
            ("Fahrplan & Aufgabenfortschritt verwalten",    "📁 Projekte  →  Fahrplan-Tab"),
            ("INPUT- / OUTPUT-Dateien verwalten",           "📁 Projekte  →  Dateien-Tab"),
            ("Charaktere, Welten, Lore aufbauen",           "🌍 Welt-Tab  (Story-/RPG-Projekte)"),
            ("Claude Code / Cursor / MCP verbinden",        "🔗 Bridge-Tab  →  Server-Modus"),
            ("Controller-KI über lokale Modelle laufen lassen", "🔗 Bridge-Tab  →  Controller-Modus"),
            ("Unterhaltung exportieren (HTML / Markdown)",  "📄  Taste im Chat-Kopfbereich"),
            ("Sprachausgabe ein- / ausschalten",            "🔊/🔇  Taste über dem Senden-Feld"),
            ("TTS-Stimme einem Teilnehmer zuweisen",        "👤 Teilnehmer  →  ✏ Bearbeiten  →  🔊 TTS-Stimme"),
            ("Audio-Ausgabegerät oder Lautstärke ändern",   "●●● Optionsmenü  →  🔊 Audio-Setup"),
            ("TTS-Backend / Stimmen herunterladen",         "●●● Optionsmenü  →  🎙 Spracheinstellungen"),
            ("Diktat / Spracherkennung aktivieren",         "🎙 Taste links neben dem Chat-Eingabefeld"),
        ] : [
            ("Add or configure an AI model",           "👤 Participants button  →  model cards"),
            ("Enter cloud API keys",                   "●●● Options menu  →  Providers Setup"),
            ("Change your display name or tone",       "●●● Options menu  →  General Settings"),
            ("Switch UI language (needs restart)",     "●●● Options menu  →  🌐 Language"),
            ("Change the app theme",                   "🎨 Theme picker  (left sidebar, above 👤 Config)"),
            ("Send a private task to one AI",          "🤫 button or type  /whisper <name> <message>"),
            ("Use emotes or actions in chat",          "/me action  or  *action* inline in any message"),
            ("Create or open a project",               "📁 Projects tab  →  New / Open"),
            ("Set AI roles & orchestration",           "📁 Projects  →  ⚙ Project Settings"),
            ("Set AI autonomy / creativity level",     "📁 Projects  →  ⚙ Project Settings  →  Autonomy Mode"),
            ("Manage roadmap & task progress",         "📁 Projects  →  Roadmap sub-tab"),
            ("Manage INPUT / OUTPUT files",            "📁 Projects  →  Files sub-tab"),
            ("Build characters, worlds, lore",         "🌍 World tab  (story / RPG projects)"),
            ("Connect Claude Code / Cursor / MCP",     "🔗 Bridge tab  →  Server mode"),
            ("Run a controller AI over local models",  "🔗 Bridge tab  →  Controller mode"),
            ("Export a conversation (HTML / Markdown)", "📄  button in the chat header"),
            ("Toggle voice output on / off",            "🔊/🔇  button above the Send field"),
            ("Assign a TTS voice to a participant",     "👤 Participants  →  ✏ Edit on a card  →  🔊 TTS Voice"),
            ("Change audio output device or volume",   "●●● Options menu  →  🔊 Audio Setup"),
            ("Choose TTS backend / download voices",   "●●● Options menu  →  🎙 Voice Settings"),
            ("Toggle dictation / voice recognition",   "🎙 button left of the chat input field"),
        ], mapContainer);

        mapToggleBtn.Click += (_, _) =>
        {
            mapExpanded = !mapExpanded;
            mapContainer.Visibility = mapExpanded ? Visibility.Visible : Visibility.Collapsed;
            mapArrow.Text = mapExpanded ? "▼" : "▶";
        };

        // ══════════════════════════════════════════════════════════════════
        // PART 2: COLLAPSIBLE AREA-BY-AREA REFERENCE
        // ══════════════════════════════════════════════════════════════════

        var autoChat     = _currentTab == Tab.Chat && _currentProjectFolder is null;
        var autoProjects = _currentTab == Tab.Projects || _currentProjectFolder is not null;
        var autoBridge   = _currentTab == Tab.Bridge;

        // ── Overview: Project vs Bridge ────────────────────────────────────
        BeginSection("🎯", isDE ? "Projektmodus vs. Bridge-Modus" : "Project Mode vs Bridge Mode");
        AddSubHeader(isDE ? "📁 Projektmodus  (Standard)" : "📁 Project Mode  (default)");
        AddBody(isDE
            ? "Für mensch-geführte KI-Zusammenarbeit mit voller Kontrolle:\n\n" +
              "  • Du steuerst die Arbeit — Anfragen eingeben, Feedback geben, Ergebnisse entscheiden.\n" +
              "  • Mehrere KIs antworten parallel auf deine Eingaben (alle sehen den Chatverlauf).\n" +
              "  • Privataufgaben an einzelne KIs senden für fokussierte Arbeit.\n" +
              "  • Rollen & Autonomie: Koordinator, Kritiker, Planer, Reasoning-Rollen zuweisen.\n" +
              "  • Fahrplan-Tracking: Aufgaben organisieren, priorisieren und Fortschritt verfolgen.\n" +
              "  • World Building (RPG/Story): Charaktere, Fraktionen, Orte, Lore.\n" +
              "  • Dateisperrung: verhindert Konflikte wenn mehrere KIs parallel dieselbe Datei bearbeiten.\n\n" +
              "Einsatz bei: Schreiben aus mehreren Perspektiven, kreativer Zusammenarbeit, Story-Aufbau " +
              "oder gesteuerter Recherche, bei der du die Kontrolle behältst."
            : "For human-led AI collaboration with complete control:\n\n" +
              "  • You direct the work — type requests, give feedback, decide outcomes.\n" +
              "  • Multiple AIs respond in parallel to your prompts (all see the chat history).\n" +
              "  • Send private/whisper tasks to individual AIs for focused work.\n" +
              "  • Roles & autonomy: assign Coordinator, Critic, Planner, Reasoner roles to AIs.\n" +
              "  • Roadmap tracking: organize and prioritize tasks, track progress.\n" +
              "  • World building (RPG/story): characters, factions, locations, lore.\n" +
              "  • File locking: prevents conflicts when multiple AIs edit the same file in parallel.\n\n" +
              "Use when: writing with multiple perspectives, creative collaboration, story building, or " +
              "managed research where you stay in control.");
        AddSubHeader(isDE ? "🔗 Bridge-Modus  (autonome Agenten)" : "🔗 Bridge Mode  (autonomous agents)");
        AddBody(isDE
            ? "Für autonome KI-Orchestrierung und externe Tool-Integration:\n\n" +
              "  • Server-Modus: ClaudetRelay wird ein MCP-Server. Externe Tools (Claude Code, Cursor usw.) " +
              "verbinden sich und nutzen deine KI-Agenten als Tools.\n" +
              "  • Controller-Modus: eine integrierte KI orchestriert deine lokalen Ollama-Modelle autonom. " +
              "Du gibst eine Aufgabe; sie entscheidet was zu tun ist, delegiert Teilaufgaben und fasst Ergebnisse zusammen.\n" +
              "  • MCP-Tools: Agenten haben Tool-Zugriff (Dateien lesen/schreiben, Fahrplan aktualisieren usw.).\n" +
              "  • Kein Chatverlauf: Agenten sehen nicht was die anderen gesagt haben; sie arbeiten unabhängig.\n\n" +
              "Einsatz bei: Delegation komplexer Aufgaben an einen autonomen Koordinator, Integration mit " +
              "externen IDEs oder Aufbau einer tool-nutzenden Agenten-Schleife."
            : "For autonomous AI orchestration and external tool integration:\n\n" +
              "  • Server mode: ClaudetRelay becomes an MCP server. External tools (Claude Code, Cursor, etc.) " +
              "connect and use your AI agents as tools.\n" +
              "  • Controller mode: a built-in AI agent orchestrates your local Ollama models autonomously. " +
              "You give it a task; it figures out what to do, delegates sub-tasks to agents, and assembles results.\n" +
              "  • MCP tools: agents have tool access (file read/write, roadmap updates, etc.).\n" +
              "  • No chat history: agents don't see what each other said; they work independently.\n\n" +
              "Use when: delegating complex tasks to an autonomous coordinator, integrating with external IDEs, " +
              "or building a tool-using agent loop.");
        AddSubHeader(isDE ? "Wesentlicher Unterschied" : "Key Difference");
        AddHighlight(isDE
            ? "Projekt = du orchestrierst die KIs  |  Bridge = eine KI orchestriert sich selbst (oder dient als Tool)"
            : "Project = you orchestrate the AIs  |  Bridge = an AI orchestrates itself (or serves as a tool)");

        // ── 💬 Chat ───────────────────────────────────────────────────────
        BeginSection("💬", isDE ? "Chat  (Hauptbereich)" : "Chat  (main area)", autoChat);
        AddBody(isDE
            ? "Das zentrale Panel, in dem alles passiert. Nachricht eingeben und alle aktivierten, online KIs " +
              "antworten der Reihe nach — sie lesen jeweils was die anderen gesagt haben, es ist also eine echte " +
              "Gruppen-Unterhaltung mit mehreren KIs."
            : "The central panel where everything happens. Type a message and all enabled, online " +
              "AIs respond in turn — they each read what the others said, so it's a real multi-AI " +
              "group conversation.");
        AddSubHeader(isDE ? "Bedienelemente im Chat-Bereich:" : "Controls in the chat area:");
        AddBody(isDE
            ? "• Blasenbreiten-Schieberegler (unten links) — ziehen zum Breiter- oder Schmaler-machen der Nachrichten-Blasen.\n" +
              "• 📄 Exportieren-Taste (Chat-Kopf) — Unterhaltung als HTML oder Markdown speichern.\n" +
              "• 🗑 Leeren — löscht den Chat und schließt das aktuelle Projekt.\n" +
              "• KI antworten-Taste — erzwingt eine weitere KI-Antwortrunde ohne neue Eingabe."
            : "• Bubble-width slider (bottom left) — drag to widen or narrow message bubbles.\n" +
              "• 📄 Export button (chat header) — save the conversation as HTML or Markdown.\n" +
              "• 🗑 Clear — wipes the chat and closes the current project.\n" +
              "• AI Respond button — forces one more AI response round without typing anything.",
            indentLeft: 0);
        AddSubHeader(isDE ? "Tasten über dem Senden-Feld" : "Buttons above the Send field");
        AddBody(isDE
            ? "Links neben dem Eingabefeld:\n" +
              "  🎙 Diktat  —  Spracherkennung ein-/ausschalten.\n" +
              "  🤫 Privatnachricht  —  siehe unten.\n" +
              "Rechts über dem Senden-Knopf:\n" +
              "  ↺ Erneut senden  —  alle KIs nochmals ohne neue Nachricht antworten lassen.\n" +
              "  🔊/🔇 Sprache ein/aus  —  TTS-Wiedergabe umschalten.\n" +
              "Während Audio läuft, ändern ↺ und 🔊 ihren Zweck automatisch:\n" +
              "  ↺ → ⏭ Aktuellen überspringen  und  🔊 → ⏹ Alle stoppen\n" +
              "Stimme pro Teilnehmer zuweisen: 👤 Teilnehmer → ✏ Bearbeiten → TTS-Stimme-Bereich."
            : "Left of the input field:\n" +
              "  🎙 Dictation  —  toggle voice recognition on/off.\n" +
              "  🤫 Private message  —  see below.\n" +
              "Right above the Send button:\n" +
              "  ↺ Re-send  —  asks all AIs to respond again without a new message.\n" +
              "  🔊/🔇 Voice on/off  —  toggles TTS playback.\n" +
              "While audio is playing, ↺ and 🔊 repurpose automatically:\n" +
              "  ↺ → ⏭ Skip Current  and  🔊 → ⏹ Stop All\n" +
              "Assign a voice per participant: 👤 Participants → ✏ Edit on a card → TTS Voice section.");
        AddSubHeader(isDE ? "📨  Privatnachricht  /  Flüstern" : "📨  Private message  /  Whisper");
        AddBody(isDE
            ? "Ermöglicht es, eine Nachricht an genau einen Teilnehmer zu richten — niemand sonst antwortet darauf.\n\n" +
              "Zwei Wege zum Flüstern:\n\n" +
              "  Per Taste:   1. 🤫 klicken — ein Menü listet alle online-Teilnehmer.\n" +
              "               2. Einen wählen — ein farbiger Chip erscheint über dem Eingabefeld.\n" +
              "               3. Nachricht eingeben und senden. Nur dieser Teilnehmer antwortet.\n\n" +
              "  Per Syntax:  /whisper <Name> <Nachricht> eingeben.\n" +
              "               Beispiel:  /whisper Gemma was denkst du?\n\n" +
              "Beim Flüstern sehen alle eine Statusmeldung:\n" +
              "  \"🤫 Du flüsterst Gemma etwas zu\"\n" +
              "Aber nur Gemma kann deine eigentliche Nachricht lesen. Andere sehen, dass geflüstert wurde,\n" +
              "aber nicht was gesagt wurde — toll für geheime Gespräche!\n\n" +
              "Der Chat bleibt aktiv: weitere Privataufgaben (oder normale Nachrichten) können während\n" +
              "der ersten noch läuft gesendet werden — parallele Flüstergespräche werden vollständig unterstützt.\n\n" +
              "Nochmals 🤫 klicken (oder × am Chip) zum Abbrechen."
            : "Lets you direct a message to exactly one participant — no one else responds to it.\n\n" +
              "Two ways to whisper:\n\n" +
              "  Via button:  1. Click 🤫 — a menu lists every online participant.\n" +
              "               2. Pick one — a coloured chip appears above the input field.\n" +
              "               3. Type your message and send.  Only that participant replies.\n\n" +
              "  Via syntax:  Type  /whisper <name> <message>  to whisper directly.\n" +
              "               Example:  /whisper Gemma what do you think?\n\n" +
              "When you whisper, everyone sees a status message:\n" +
              "  \"🤫 You whisper something to Gemma\"\n" +
              "But only Gemma can read your actual message.  Other participants see the whisper happened\n" +
              "but not what you said — great for secret conversations or keeping other AIs guessing!\n\n" +
              "The chat stays live: you can send another private task (or a normal message)\n" +
              "while the first is still running — parallel whispers are fully supported.\n\n" +
              "Click 🤫 again (or the × on the chip) to cancel.");
        AddHighlight(isDE
            ? "✨ Tipp:  Modelle können auch miteinander flüstern — probiere es aus und beobachte ihre Geheimdialoge!"
            : "✨ Tip:  Models can whisper to each other too — try it and watch them have secret conversations!");
        AddSubHeader(isDE ? "🎭  Emotes  &  Aktionen" : "🎭  Emotes  &  actions");
        AddBody(isDE
            ? "Du und die KIs können Aktionen inline ausdrücken — sie erscheinen kursiv und hervorgehoben.\n\n" +
              "  /me winkt            →  Robert winkt  (gesamte Nachricht als Aktion)\n" +
              "  /me winkt* Hallo!    →  Robert winkt  (Aktion)  +  Hallo!  (normal)\n" +
              "  Super! *grinst*      →  Super!  (normal)  +  grinst  (Aktion)\n\n" +
              "Regeln:\n" +
              "  • Nachricht mit /me beginnen, um eine Aktion zu starten. Ein einzelnes * schließt sie\n" +
              "    und wechselt zurück zur normalen Sprache. Weitere *…*-Paare schalten ein und aus.\n" +
              "  • Überall in einer Nachricht Text in *Sternchen* einschließen, um ihn als Aktion hervorzuheben.\n" +
              "  • Rohtext (mit * -Markierungen) wird im Verlauf gespeichert — KIs lesen und verstehen die Syntax.\n" +
              "    Die Formatierung ist nur Anzeige; Kopieren gibt den sauberen Originaltext."
            : "Both you and the AIs can express actions inline — they appear italic and highlighted.\n\n" +
              "  /me waves            →  Robert waves  (entire message as action)\n" +
              "  /me waves* Hello!    →  Robert waves  (action)  +  Hello!  (normal)\n" +
              "  That's great! *grins*  →  That's great!  (normal)  +  grins  (action)\n\n" +
              "Rules:\n" +
              "  • Start a message with /me to open an action.  A bare * closes it and switches\n" +
              "    back to normal speech.  Further *…* pairs toggle in and out.\n" +
              "  • Anywhere in a message, wrap text in *asterisks* to highlight it as an action.\n" +
              "  • Raw text (with * markers) is stored in history — AIs read and understand the syntax.\n" +
              "    The formatting is display-only; copy still gives you the clean original text.");

        // ── 👤 Participants & Settings ────────────────────────────────────
        BeginSection("👤", isDE ? "Teilnehmer  &  Einstellungen" : "Participants  &  Settings");

        AddSubHeader(isDE ? "Linke Seitenleiste — Teilnehmerkarten" : "Left sidebar — participant cards");
        AddBody(isDE
            ? "Jede Karte = ein KI-Modell. Statuspunkt: grün = online, grau = offline. " +
              "Karte klicken zum Aktivieren/Deaktivieren für den aktuellen Chat. " +
              "Karte öffnen zum Konfigurieren von Modell, Spitzname, Stimme, Rate-Limit und Rolle.\n\n" +
              "Während ein Modell eine Antwort generiert, leuchtet sein Avatar sanft pulsierend — " +
              "praktisch, um auf einen Blick zu sehen, wer noch arbeitet, wenn mehrere KIs parallel antworten."
            : "Each card = one AI model. Status dot: green = online, grey = offline. " +
              "Click a card to enable/disable that AI for the current chat. " +
              "Open a card to configure its model, nickname, voice, rate limit, and role.\n\n" +
              "While a model is generating a response its avatar glows with a soft pulsing animation — " +
              "useful for seeing at a glance who is still working when multiple AIs respond in parallel.");

        AddSubHeader(isDE ? "🎨 Theme-Auswahl  (linke Seitenleiste, unter den Teilnehmerkarten)" : "🎨 Theme picker  (left sidebar, below participant cards)");
        AddBody(isDE
            ? "Eine Dropdown-Liste, die das gesamte App-Theme sofort wechselt. " +
              "Themes sind .oxsuit-Dateien im Themes/-Ordner neben der .exe — eigene können hinzugefügt werden."
            : "A drop-down that switches the whole app theme instantly. " +
              "Themes are .oxsuit files in the Themes/ folder next to the .exe — " +
              "you can add your own.");

        AddSubHeader(isDE ? "●●● Optionsmenü  →  Allgemeine Einstellungen" : "●●● Options menu  →  General Settings");
        AddBody(isDE
            ? "Dein Anzeigename, Ton-Schieberegler, UI-Zoom und die zwei Persönlichkeitsschalter:\n" +
              "  🏴‍☠️ Freibeuter — alle KIs sprechen im Piratendialekt  (Ton-Schieberegler = Intensität).\n" +
              "  🎭 Spottdrossel — alle KIs sprechen in shakespeareschen Versen  (Schieberegler = Chaos ↔ Wärme).\n" +
              "Sprache ist ein separater ●●● Menüeintrag — siehe 🌐 Sprache."
            : "Your display name, response tone slider, UI zoom, and the two personality toggles:\n" +
              "  🏴‍☠️ Buccaneer — all AIs speak in pirate dialect  (tone slider = intensity).\n" +
              "  🎭 Mockingbird — all AIs go full Shakespearean verse  (slider = chaos ↔ warmth).\n" +
              "Language is a separate ●●● menu entry — see 🌐 Language below.");

        AddSubHeader(isDE ? "●●● Optionsmenü  →  Anbieter-Setup" : "●●● Options menu  →  Providers Setup");
        AddBody(isDE
            ? "API-Schlüssel für Anthropic (Claude), Google AI (Gemini), Groq, OpenRouter, " +
              "xAI Grok, Mistral, OpenAI ChatGPT und andere eingeben. Schlüssel werden mit einem Live-Aufruf " +
              "getestet, damit sofort ersichtlich ist, wenn etwas nicht stimmt."
            : "Enter API keys for Anthropic (Claude), Google AI (Gemini), Groq, OpenRouter, " +
              "xAI Grok, Mistral, OpenAI ChatGPT, and others. Keys are tested with a live call " +
              "so you know immediately if something is wrong.");
        AddHighlight(isDE
            ? "🔒  API-Schlüssel werden ausschließlich im Windows Credential Manager gespeichert — " +
              "niemals in eine Datei auf der Festplatte geschrieben. ClaudetRelay liest sie direkt aus " +
              "Windows und gibt sie nur an die jeweilige Anbieter-API weiter."
            : "🔒  API keys are stored exclusively in the Windows Credential Manager — " +
              "never written to any file on disk. ClaudetRelay reads them directly from " +
              "Windows and passes them only to the respective provider's API.");

        // ── 📁 Projects ───────────────────────────────────────────────────
        BeginSection("📁", isDE ? "Projekte-Tab" : "Projects tab", autoProjects);
        AddBody(isDE
            ? "Jedes Projekt ist ein Ordner auf deinem PC. KIs können Dateien darin lesen und schreiben, " +
              "wenn sie Schreibzugriff haben. Der Projekte-Tab listet alle bekannten Projekte; klicken zum Öffnen."
            : "Each project is a folder on your PC. AIs can read and write files inside it " +
              "if they have write access. The Projects tab lists all known projects; click one " +
              "to open it.");

        AddSubHeader(isDE ? "Fahrplan-Tab" : "Roadmap sub-tab");
        AddBody(isDE
            ? "Visueller Meilenstein- & Aufgaben-Tracker. KIs mit Schreibzugriff können Aufgaben erstellen, " +
              "Fortschrittsprozente aktualisieren und Einträge als erledigt markieren — eigenständig oder auf Anfrage."
            : "Visual milestone & task tracker. AIs with write access can create tasks, " +
              "update progress percentages, and mark items done — either by following the " +
              "roadmap themselves or when asked.");

        AddSubHeader(isDE ? "Dateien-Tab" : "Files sub-tab");
        AddBody(isDE
            ? "Drei Ordner in jedem Projekt:\n\n" +
              "  INPUT/ — Dateien, die du hier für KIs zum Lesen ablegst.\n" +
              "  Quelldokumente, Bilder, Referenzmaterial oder fertige Arbeitsdateien einwerfen,\n" +
              "  auf die die Modelle noch verweisen, aber nicht ändern sollen.\n" +
              "  Tipp: INPUT/finished/-Unterordner erstellen, um fertige Ergebnisse vom Rohmaterial\n" +
              "  zu trennen, während beides für KIs sichtbar bleibt.\n\n" +
              "  OUTPUT/ — Dateien, die KIs mit dem <output file=\"...\">-Tag erstellen.\n" +
              "  Wenn eine OUTPUT/-Datei fertig ist, nach INPUT/ verschieben um sie zu sichern\n" +
              "  und das KI-Team sie als abgeschlossenes Referenzmaterial behandeln zu lassen.\n\n" +
              "  PROJECTPLAN/ — Pläne, Spezifikationen, Entscheidungen und Aufgabenlisten die KIs via <projectplan> schreiben."
            : "Three folders inside every project:\n\n" +
              "  INPUT/ — files you place here for AIs to read.\n" +
              "  Drop in source documents, images, reference material, or finished work files\n" +
              "  the models still need to reference but must not change.\n" +
              "  Tip: create an INPUT/finished/ subfolder to keep completed deliverables separate\n" +
              "  from raw source material while keeping both visible to the AIs.\n\n" +
              "  OUTPUT/ — files AIs create using the <output file=\"...\"> tag.\n" +
              "  Once you're happy with a file in OUTPUT/, move it to INPUT/ to preserve it and\n" +
              "  let the AI team treat it as settled reference material.\n\n" +
              "  PROJECTPLAN/ — plans, specs, decisions and task lists AIs write via <projectplan>.");
        AddHighlight(isDE
            ? "🔒  INPUT ist durch Architektur geschützt, nicht durch OS-Dateiberechtigungen.\n" +
              "Modelle haben Schreib-Tags nur für OUTPUT/ und PROJECTPLAN/ — es gibt kein <input>-Schreib-Tag,\n" +
              "daher kann keine Modellantwort INPUT/ über ClaudetRelays normale Kanäle berühren.\n" +
              "Der Ordner ist nicht als schreibgeschützt auf der Festplatte markiert, du kannst also frei Dateien hinzufügen oder verschieben."
            : "🔒  INPUT is protected by architecture, not by OS file permissions.\n" +
              "Models have write tags only for OUTPUT/ and PROJECTPLAN/ — there is no <input> write tag,\n" +
              "so no model response can touch INPUT/ through ClaudetRelay's normal channels.\n" +
              "The folder is not marked read-only on disk, so you can freely add or move files there yourself.");

        AddSubHeader(isDE ? "⚙ Projekteinstellungen  (Zahnrad-Symbol in einem geöffneten Projekt)" : "⚙ Project Settings  (gear icon inside an open project)");
        AddBody(isDE
            ? "• Orchestrierungsmodus — wer wann spricht:\n" +
              "    Alle antworten / Koordinator zuerst / Koordinator fasst zusammen / Nur Koordinator\n" +
              "• Teilnehmerrollen — Koordinator, Reasoner, Kritiker, Planer, Forscher und Schreibzugriff pro KI vergeben. " +
              "✏ Bearbeiten öffnen für vollständigen Charaktereditor pro Teilnehmer.\n" +
              "• Autonomiemodus — 5-Stufen-Schieberegler:\n" +
              "    Assistent (handelt nie ohne Genehmigung) → Kooperativ → Geleitete Kreativität → " +
              "Kreativ → Kreativitätschaos! (einfach 'Los' sagen).\n" +
              "• Antwortsprache — alle KI-Antworten in eine bestimmte Sprache erzwingen.\n" +
              "• Standard-Dialogtiefe & Antwortlänge."
            : "• Orchestration Mode — who speaks when:\n" +
              "    All Respond / Coordinator First / Coordinator Summarizes / Coordinator Only\n" +
              "• Participant Roles — assign Coordinator, Reasoner, Critic, Planner, Researcher, " +
              "and Write Access per AI. Open ✏ Edit for a full character editor per participant.\n" +
              "• Autonomy Mode — 5-step slider:\n" +
              "    Assistant (never acts without approval) → Cooperative → Directed Creativity → " +
              "Creative → Creativity Chaos! (just say 'Go').\n" +
              "• Response Language — force all AI replies into a specific language.\n" +
              "• Max Dialog Depth & Response Length defaults.");

        // ── 🌍 World Builder ──────────────────────────────────────────────
        BeginSection("🌍", isDE ? "World Builder  (Story-/RPG-Projekte)" : "World Builder  (story / RPG projects)");
        AddBody(isDE
            ? "Persistente Weltelemente definieren, mit denen KIs immer konsistent bleiben:\n" +
              "  Charaktere — Profile, Rollen, Bögen, Stimme, Werte.\n" +
              "  Fraktionen — Ziele, Anführer, Territorium, Mitgliederlisten.\n" +
              "  Orte — Typ, Atmosphäre, Bedeutung, Fraktionsverbindungen.\n" +
              "  Lore — Geschichte, Mythen, Magie-Regeln, Wissens-Tags."
            : "Define persistent world entities that AIs always stay consistent with:\n" +
              "  Characters — profiles, roles, arcs, voice, stats.\n" +
              "  Factions — goals, leaders, territory, member lists.\n" +
              "  Locations — type, atmosphere, significance, faction ties.\n" +
              "  Lore — history, myths, magic rules, knowledge tags.");
        AddSubHeader(isDE ? "Boards (Pinnwände)" : "Boards");
        AddBody(isDE
            ? "Jedes Board ist eine visuelle Leinwand, auf der du Entitätskarten platzierst und Beziehungen " +
              "zwischen ihnen einzeichnest. Board öffnen zum Bearbeiten; mehrere Boards können gleichzeitig geöffnet sein. " +
              "Board-Einstellungen erlauben die Wahl, welche Entitätstypen erscheinen und welches Symbol die Kachel verwendet."
            : "Each Board is a visual canvas where you place entity cards and draw relationships " +
              "between them. Open a Board to edit; multiple boards can be open at once. " +
              "Board Settings lets you choose which entity types appear and what symbol the tile uses.");
        AddHighlight(isDE
            ? "💡  Boards können auf Boards platziert werden — eine Board-Kachel auf die Leinwand eines anderen Boards ziehen, " +
              "um verschachtelte Übersichten zu erstellen. Praktisch für Kontinent → Region → Stadt-Drill-downs " +
              "oder Fraktion → Unterfraktion-Hierarchien."
            : "💡  Boards can be placed on Boards — drag a Board tile onto another Board's canvas " +
              "to create nested overviews. Handy for continent → region → city drill-downs, " +
              "or faction → sub-faction hierarchies.");

        AddSubHeader(isDE ? "Board-Steuerung" : "Board canvas controls");
        AddBody(isDE
            ? "  🖱 Linksziehen auf Karte / Pin / Rahmen  →  verschieben\n" +
              "  🖱 Linksziehen auf freier Fläche  →  Gummiband-Mehrfachauswahl (Auswahlbox)\n" +
              "  🖱 Linksklick auf freie Fläche  →  Auswahl aufheben\n" +
              "  🖱 Rechtsziehen auf freier Fläche  →  Leinwand schwenken / scrollen\n" +
              "  🖱 Rechtsklick auf freie Fläche  →  Hinzufügen-Kontextmenü (Entität, Text, Pin, Rahmen)\n" +
              "  🖱 Rechtsklick auf Karte  →  vom Board entfernen / bearbeiten\n" +
              "  Shift-Klick oder Strg-Klick  →  Karte zur aktuellen Auswahl hinzufügen\n" +
              "  Beliebige Karte in Auswahl ziehen  →  verschiebt alle ausgewählten Karten zusammen\n" +
              "  Entf-Taste  →  ausgewählte Karte(n) vom Board entfernen (löscht nie aus der Bibliothek)\n" +
              "  Doppelklick auf Board-Pin  →  dieses Board in neuem Fenster öffnen\n" +
              "  Größenänderungs-Griff (unten rechts)  →  Karte, Pin oder Rahmen skalieren\n" +
              "  Verbindung-hinzufügen-Taste  →  erstes Objekt klicken, dann zweites, um eine Linie zu zeichnen\n" +
              "       Funktioniert zwischen beliebigen Kombinationen aus Karten, Rahmen und Board-Pins."
            : "  🖱 Left-drag a card / pin / frame  →  move it\n" +
              "  🖱 Left-drag on empty space  →  rubber-band multi-select (draws a selection box)\n" +
              "  🖱 Left-click on empty space  →  deselect all\n" +
              "  🖱 Right-drag on empty space  →  pan / scroll the canvas\n" +
              "  🖱 Right-click on empty space  →  add context menu (entity, text, pin, frame)\n" +
              "  🖱 Right-click a card  →  remove from board / edit\n" +
              "  Shift-click or Ctrl-click  →  add card to current selection\n" +
              "  Drag any card in a selection  →  moves all selected cards together\n" +
              "  Del key  →  remove selected card(s) from board (never deletes from library)\n" +
              "  Double-click a board pin  →  open that board in a new window\n" +
              "  Resize grip (bottom-right corner)  →  resize a card, pin, or frame\n" +
              "  Add Relation toolbar button  →  click first object, then second to draw a line\n" +
              "       Works between any combination of cards, frames, and board pins.");

        // ── 🔗 Bridge ─────────────────────────────────────────────────────
        BeginSection("🔗", isDE ? "Bridge-Tab  (MCP-Agenten-Bridge)" : "Bridge tab  (MCP Agent Bridge)", autoBridge);

        AddSubHeader(isDE ? "Server-Modus  (▶ Server-Tab)" : "Server mode  (▶ Server sub-tab)");
        AddBody(isDE
            ? "ClaudetRelay startet einen lokalen MCP-Server. Externe Tools — Claude Code, Cursor " +
              "oder ein beliebiger MCP-kompatibler Client — verbinden sich und können deine KI-Teilnehmer " +
              "als Tools aufrufen, Projektdateien lesen und schreiben, den Fahrplan aktualisieren und mehr. " +
              "Port konfigurieren, Projekt für die Bridge laden und das Aktivitätsprotokoll hier beobachten."
            : "ClaudetRelay starts a local MCP server. External tools — Claude Code, Cursor, " +
              "or any MCP-compatible client — connect to it and can call your AI participants " +
              "as tools, read and write project files, update the roadmap, and more. " +
              "Configure the port, load a project for the bridge, and watch the activity log here.");

        AddSubHeader(isDE ? "Controller-Modus  (🤖 Chat-Tab)" : "Controller mode  (🤖 Chat sub-tab)");
        AddBody(isDE
            ? "Eine integrierte Controller-KI orchestriert deine lokalen Ollama-Agenten. " +
              "Aufgabe eingeben; die Controller-KI delegiert Teilaufgaben an die konfigurierten Agenten " +
              "und fasst die Ergebnisse zusammen — kein externer Client nötig."
            : "A built-in controller AI orchestrates your local Ollama agents. " +
              "Type a task; the controller AI delegates sub-tasks to the configured agents " +
              "and assembles the results — no external client needed.");

        AddSubHeader(isDE ? "Setup-Tab  (in beiden Modi)" : "Setup sub-tab  (in both modes)");
        AddBody(isDE
            ? "Agenten & Ordner — lokale Ollama-Modelle oder Cloud-KIs als Bridge-Agenten hinzufügen " +
              "(Agenten rufen sich gegenseitig als Tools auf). Zugängliche Ordner hinzufügen. " +
              "Temp-Arbeitsbereich für parallele Aufgaben-Ausgaben festlegen.\n" +
              "⚙ Bridge-Einstellungen-Taste — feinabstimmen, welche MCP-Tools exponiert werden, und " +
              "Dateigrößen-Limits für Lese-/Schreiboperationen setzen."
            : "Agents & Folders — add local Ollama models or cloud AIs as bridge agents " +
              "(agents call each other as tools). Add folders agents are allowed to access. " +
              "Set the temp workspace for parallel task output.\n" +
              "⚙ Bridge Settings button — fine-tune which MCP tools are exposed and set " +
              "file-size limits for read/write operations.");

        // ── 🔊 Audio & Voice ──────────────────────────────────────────────
        BeginSection("🔊", isDE ? "Audio & Sprache  (●●● Optionsmenü)" : "Audio & Voice  (●●● Options menu)");

        AddSubHeader(isDE ? "🔊 Audio-Setup" : "🔊 Audio Setup");
        AddBody(isDE
            ? "Ausgabegerät für TTS-Wiedergabe wählen und Hauptlautstärke einstellen (0–100 %). " +
              "Lautstärkeänderungen gelten sofort für alles, was gerade spricht. " +
              "Außerdem: Eingabegerät (Mikrofon) für Spracherkennung auswählen."
            : "Select the audio output and input (microphone) device ClaudetRelay uses and set the " +
              "master volume (0–100 %). Volume changes apply live to anything currently speaking.");

        AddSubHeader(isDE ? "🎙 Spracheinstellungen" : "🎙 Voice Settings");
        AddBody(isDE
            ? "TTS-Backend wählen und konfigurieren:\n" +
              "  Windows TTS — eingebaute Windows-Stimmen, funktioniert offline, kein Setup nötig.\n" +
              "  Sherpa-onnx — hochwertige Offline-Neural-Stimmen (Piper TTS-kompatibel). " +
              "Modellordner wählen, dann einzelne Stimmpakete über den integrierten Stimm-Modell-Manager herunterladen. " +
              "Modelle liegen standardmäßig im Voices/-Ordner neben der .exe.\n" +
              "  VOICEVOX — anime-inspirierte Charakterstimmen über eine separate VOICEVOX-Installation " +
              "die lokal läuft (Standard-Port 50021). Kompatible Alternativen: AivisSpeech, COEIROINK."
            : "Choose the TTS backend and configure it:\n" +
              "  Windows TTS — built-in Windows voices, works offline, no setup required.\n" +
              "  Sherpa-onnx — high-quality offline neural voices (Piper TTS-compatible). " +
              "Pick a model folder, then download individual voice packs from the built-in " +
              "Voice Model Manager. Models live next to the .exe in a Voices/ folder by default.\n" +
              "  VOICEVOX — anime-inspired character voices via a separate VOICEVOX installation " +
              "running locally (default port 50021). Compatible alternatives: AivisSpeech, COEIROINK.");
        AddBody(isDE
            ? "Ein Optionsfeld auswählen wendet das Backend sofort an, damit der Unterschied ohne Schließen des Dialogs gehört werden kann."
            : "Switching a radio button applies the backend immediately so you can hear the " +
              "difference without closing the dialog.");

        AddSubHeader(isDE ? "🎙 Spracherkennung  (Diktat-Modus)" : "🎙 Voice Recognition  (Dictation Mode)");
        AddBody(isDE
            ? "Die 🎙-Taste links neben dem Chat-Eingabefeld schaltet das Diktat um. " +
              "Gesprochene Wörter werden offline transkribiert und ins Eingabefeld eingefügt. " +
              "Drei Aktivierungsmodi:\n" +
              "  Immer aktiv — Mikrofon nimmt kontinuierlich auf, solange Diktat aktiv ist.\n" +
              "  Sprechtaste — konfigurierbare Taste halten (Standard: Leertaste) zum Aufnehmen.\n" +
              "  Sprachaktivierung — Aufnahme startet automatisch, wenn die Stimme eine " +
              "konfigurierbare Lautstärke-Schwelle überschreitet, und stoppt nach kurzer Stille."
            : "The 🎙 button to the left of the chat input toggles dictation. " +
              "Spoken words are transcribed offline and inserted into the input field. " +
              "Three activation modes:\n" +
              "  Always On — microphone records continuously while dictation is active.\n" +
              "  Push to Talk — hold a configurable key (default: Space) to record.\n" +
              "  Voice Activated — recording starts automatically when your voice exceeds " +
              "a configurable volume threshold and stops after a short silence.");
        AddBody(isDE
            ? "ASR-Modelle herunterladen über ●●● → 🎙 Spracherkennung → ⬇ ASR-Modelle verwalten. " +
              "Modelle werden standardmäßig im ASR/-Ordner neben der .exe gespeichert. " +
              "Whisper-Modelle unterstützen ~100 Sprachen inkl. Deutsch und Englisch. " +
              "SenseVoice ist schneller, unterstützt aber nur Englisch, Chinesisch, Japanisch und Koreanisch."
            : "ASR models are downloaded via ●●● → 🎙 Voice Recognition → ⬇ Manage ASR Models. " +
              "Models are stored in an ASR/ folder next to the .exe by default. " +
              "Whisper models support ~100 languages including German and English. " +
              "SenseVoice is faster but supports English, Chinese, Japanese and Korean only.");
        AddHighlight(isDE
            ? "💡  ASR-Modelle laufen vollständig auf der CPU mit System-RAM — kein GPU oder VRAM wird genutzt, " +
              "und sie konkurrieren nicht mit Ollama-Modellen um GPU-Ressourcen. " +
              "Das Modell wird beim ersten Aktivieren des Diktats in den RAM geladen und bleibt dort " +
              "bis die App geschlossen wird. Das Mikrofon bleibt in allen Aktivierungsmodi offen " +
              "(für den Pegelanzeiger), aber die eigentliche Transkription — die CPU-intensive Arbeit — " +
              "erfolgt nur wenn ein Aufnahme-Chunk übermittelt wird: bei Freigabe der Sprechtaste, " +
              "nach Stille im Sprachaktivierungs-Modus, oder am Ende eines Chunks im Immer-aktiv-Modus. " +
              "Wenn lokale Agenten laufen und die CPU knapp ist, hält die Sprechtaste Transkriptions-Bursts " +
              "kurz und vorhersehbar."
            : "💡  ASR models run entirely on CPU using system RAM — they do not use your GPU or VRAM " +
              "and do not compete with Ollama models for GPU resources. " +
              "The model is loaded into RAM the first time you activate dictation and stays there " +
              "until you close the app. The microphone stays open in all activation modes " +
              "(needed for the level meter), but the heavy CPU work — the actual transcription — " +
              "only happens when a recording chunk is submitted: on PTT key release, " +
              "after a silence in Voice Activated mode, or at the end of each chunk in Always On mode. " +
              "If CPU headroom is tight while local agents are running, Push-to-Talk keeps " +
              "transcription bursts short and predictable.");

        // ── Close ─────────────────────────────────────────────────────────
        currentTarget = root;   // back to root so the button lands outside any section
        AddRule(topMargin: 12, bottomMargin: 14);
        var closeBtn = new Button
        {
            Content             = isDE ? "Verstanden, danke Claudette! 🐙" : "Got it, thanks Claudette! 🐙",
            Height              = 38,
            FontFamily          = new FontFamily("Segoe UI"),
            FontSize            = 13, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding             = new Thickness(28, 0, 28, 0),
            IsCancel = true, Cursor = Cursors.Hand
        };
        closeBtn.SetResourceReference(Button.BackgroundProperty, "PrimaryAccentBrush");
        closeBtn.SetResourceReference(Button.ForegroundProperty, "AccentTextBrush");
        closeBtn.Style = (Style)FindResource("ModernButton");
        closeBtn.Click += (_, _) => win.Close();
        root.Children.Add(closeBtn);

        win.Show();
    }

    // ── Sidebar actions ────────────────────────────────────────────────────

    private void ClearChat_Click(object sender, RoutedEventArgs e)
    {
        // ── Project mode: clear only the project chat, keep project open ──
        if (_currentProjectFolder is not null && _currentProject is not null)
        {
            var isDE = System.Globalization.CultureInfo.CurrentUICulture
                           .TwoLetterISOLanguageName
                           .Equals("de", StringComparison.OrdinalIgnoreCase);

            var projectName = _currentProject.ProjectName;
            var msg   = isDE
                ? $"Alle gespeicherten Nachrichten im Projekt \"{projectName}\" werden dauerhaft gelöscht.\n\n" +
                  "Die KI-Teilnehmer werden sich an keine frühere Unterhaltung mehr erinnern.\n\n" +
                  "Wirklich fortfahren?"
                : $"All stored messages in project \"{projectName}\" will be permanently deleted.\n\n" +
                  "AI participants will have no memory of this conversation.\n\n" +
                  "Continue?";
            var title = isDE ? "Projektchat leeren" : "Clear Project Chat";

            if (MessageBox.Show(msg, title, MessageBoxButton.YesNo, MessageBoxImage.Warning)
                    != MessageBoxResult.Yes) return;

            _streamCts?.Cancel();
            CancelAllPrivateTasks();
            ChatPanel.Children.Clear();
            _sharedHistory.Clear();

            // Delete all chatlog segment files for this project
            try
            {
                foreach (var f in ProjectService.GetChatLogFiles(_currentProjectFolder))
                    SysIO.File.Delete(f);
            }
            catch { /* non-fatal */ }

            AddSystemMessage(isDE ? "Projektchat geleert." : "Project chat cleared.");
            return;
        }

        // ── General chat: existing behaviour ──────────────────────────────
        _streamCts?.Cancel();
        CancelAllPrivateTasks();
        ChatPanel.Children.Clear();
        _sharedHistory.Clear();
        CloseCurrentProject();

        // Delete all general-chat log files (chatlog.json, chatlog-prev.json, summary.md)
        try
        {
            if (SysIO.Directory.Exists(GeneralChatLogService.LogFolder))
                foreach (var file in SysIO.Directory.GetFiles(GeneralChatLogService.LogFolder))
                    SysIO.File.Delete(file);
        }
        catch { /* non-fatal - log cleanup is best-effort */ }

        AddSystemMessage("Chat cleared.");
    }

    /// <summary>
    /// Returns the live status string ("Ready" / "Offline") for a participant that is
    /// currently running in the chat panel, or <c>null</c> if it is not active there.
    /// Called from ParticipantsWindow to populate status badges on the card grid.
    /// </summary>
    public string? GetLiveParticipantStatus(string type, string model, string serverUrl)
    {
        if (type == "Ollama")
        {
            var m = _ollamaParticipants.FirstOrDefault(ui =>
                string.Equals(ui.Data.Service.CurrentModel, model, StringComparison.OrdinalIgnoreCase));
            return m is null ? null : m.Data.IsOnline == true ? "Ready" : "Offline";
        }
        var c = _cloudAIParticipants.FirstOrDefault(ui =>
            string.Equals(ui.Data.Service.ProviderName, type,  StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ui.Data.Service.CurrentModel, model, StringComparison.OrdinalIgnoreCase));
        return c is null ? null : c.Data.IsOnline == true ? "Ready" : "Offline";
    }


    /// <summary>
    /// Incrementally syncs the live participant panel with the diff between the settings
    /// saved before and after the Settings window was open.
    /// • Freshly-enabled slots  → added to the panel.
    /// • Freshly-disabled slots → removed from the panel.
    /// • Slots that were already active and remain enabled → left completely untouched.
    /// </summary>


    // ── Service factories ──────────────────────────────────────────────────

    private static ICloudAIService CreateCloudAIService(
        string provider, string apiKey, string serverUrl = "") =>
        provider switch
        {
            "Ollama ☁"       => new OllamaOpenAIService(apiKey),
            "Google AI"      => new GoogleAIService(apiKey),
            "Groq"           => new GroqService(apiKey),
            "OpenRouter"     => new OpenRouterService(apiKey),
            "Mistral"        => new MistralService(apiKey),
            "xAI Grok"       => new XAIGrokService(apiKey),
            "OpenAI ChatGPT" => new OpenAIService(apiKey),
            "vLLM"           => new VllmService(serverUrl, apiKey),
            "LM Studio"      => new LmStudioService(serverUrl, apiKey),
            "LM Studio ☁"    => new LmStudioService(LmStudioService.DefaultCloudUrl, apiKey),
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
        "Ollama ☁"       => OllamaOpenAIService.DefaultModels,
        "vLLM"           => [],   // fetched live from /v1/models
        "LM Studio"      => [],
        "LM Studio ☁"    => [],
        _                => AnthropicService.DefaultModels
    };

    // ── Model name formatting ──────────────────────────────────────────────

    /// <summary>Returns a 2-character avatar label derived from the model name.</summary>
    private static string FormatModelAvatarLabel(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return "AI";
        if (model.StartsWith("claude",   StringComparison.OrdinalIgnoreCase)) return "Cl";
        if (model.StartsWith("gpt",      StringComparison.OrdinalIgnoreCase)) return "GP";
        if (model.StartsWith("grok",     StringComparison.OrdinalIgnoreCase)) return "Gr";
        if (model.StartsWith("gemma",    StringComparison.OrdinalIgnoreCase)) return "Gm";
        if (model.StartsWith("llama",    StringComparison.OrdinalIgnoreCase)) return "Ll";
        if (model.StartsWith("mistral",  StringComparison.OrdinalIgnoreCase)) return "Mi";
        if (model.StartsWith("qwen",     StringComparison.OrdinalIgnoreCase)) return "Qw";
        if (model.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase)) return "Ds";
        if (model.StartsWith("phi",      StringComparison.OrdinalIgnoreCase)) return "Ph";
        if (model.StartsWith("falcon",   StringComparison.OrdinalIgnoreCase)) return "Fa";
        if (model.StartsWith("command",  StringComparison.OrdinalIgnoreCase)) return "Co";
        if (model.StartsWith("o1",       StringComparison.OrdinalIgnoreCase)) return "o1";
        if (model.StartsWith("o3",       StringComparison.OrdinalIgnoreCase)) return "o3";
        return model.Length >= 2 ? model[..2].ToUpper() : model.ToUpper().PadRight(2);
    }

    /// <summary>Returns a human-readable model name.
    /// E.g. "claude-sonnet-4-20250514" → "Claude Sonnet 4", "gpt-4o" → "GPT-4o".</summary>
    private static string FormatModelDisplayName(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return model;

        // ── Claude ────────────────────────────────────────────────────────
        if (model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
        {
            var families = new[] { "sonnet", "haiku", "opus" };
            var parts  = model.Split('-');
            var tokens = new List<string> { "Claude" };
            bool addedFamily = false, addedVer = false;
            foreach (var p in parts.Skip(1))
            {
                if (!addedFamily && families.Any(f => f.Equals(p, StringComparison.OrdinalIgnoreCase)))
                { tokens.Add(Capitalize(p)); addedFamily = true; }
                else if (!addedVer && p.Length <= 2 && p.All(char.IsDigit))
                { tokens.Add(p); addedVer = true; }
                else if (p.Length >= 6 && p.All(char.IsDigit)) break; // date stamp
            }
            return string.Join(' ', tokens);
        }

        // ── GPT ───────────────────────────────────────────────────────────
        if (model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase))
        {
            var rest  = model[4..];
            var parts = rest.Split('-');
            var sb    = new StringBuilder("GPT-");
            sb.Append(parts[0]);
            for (int i = 1; i < parts.Length; i++)
                sb.Append(' ').Append(Capitalize(parts[i]));
            return sb.ToString();
        }

        // ── Grok ──────────────────────────────────────────────────────────
        if (model.StartsWith("grok-", StringComparison.OrdinalIgnoreCase))
            return string.Join(' ', model.Split('-').Select(Capitalize));

        // ── o1 / o3 (OpenAI reasoning) ────────────────────────────────────
        if (Regex.IsMatch(model, @"^o\d", RegexOptions.IgnoreCase))
        {
            var parts = model.Split('-');
            return string.Join(' ', parts.Select(p =>
                p.Length == 1 || (p.Length == 2 && char.IsDigit(p[1])) ? p.ToUpper() : Capitalize(p)));
        }

        // ── Generic: split on hyphens/dots, strip "latest"/"online"/stamps ─
        var normalized = Regex.Replace(model, @"([a-zA-Z])(\d)", "$1 $2");
        normalized     = Regex.Replace(normalized, @"(\d)([a-zA-Z])", "$1 $2");
        var words = normalized
            .Split(['-', '_', '.', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !t.Equals("latest", StringComparison.OrdinalIgnoreCase)
                     && !t.Equals("online", StringComparison.OrdinalIgnoreCase)
                     && !(t.Length >= 6 && t.All(char.IsDigit)))
            .Select(t => char.IsDigit(t[0]) ? t : Capitalize(t));
        return string.Join(' ', words);
    }

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

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
        tb.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        ChatPanel.Children.Add(tb);
    }

    /// <summary>
    /// Like <see cref="AddSystemMessage"/> but returns the live <see cref="TextBlock"/>
    /// so callers can update <c>.Text</c> in-place (e.g. for progress ticks).
    /// </summary>
    private TextBlock AddUpdatableSystemMessage(string text)
    {
        var tb = new TextBlock
        {
            Text          = text,
            TextAlignment = TextAlignment.Center,
            FontSize      = 11,
            FontFamily    = new FontFamily("Segoe UI"),
            Margin        = new Thickness(0, 10, 0, 10)
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");
        ChatPanel.Children.Add(tb);
        return tb;
    }

    /// <summary>
    /// Adds a compact pill-shaped activity indicator to the chat (e.g. "⚙ [Gm] Gemma3 is working…").
    /// Returns the indicator element (can be removed from ChatPanel) and an update action:
    /// call it with <c>null</c> to switch to "✓ done" state, or with any string to set custom text.
    /// Used by <see cref="RunCoordinatorOnlyModeAsync"/> to show hidden-run progress.
    /// </summary>
    private (Border Element, Action<string?> Update) AddActivityIndicator(
        string displayName, string avatarLabel, string colorKey)
    {
        var tb = new TextBlock
        {
            Text         = $"⚙  [{avatarLabel}] {displayName}  is working…",
            TextAlignment = TextAlignment.Center,
            FontSize      = 11,
            FontFamily    = new FontFamily("Segoe UI"),
            Margin        = new Thickness(0)
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "ContentDimBrush");

        var pill = new Border
        {
            Padding             = new Thickness(14, 3, 14, 3),
            CornerRadius        = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(0, 3, 0, 3),
            BorderThickness     = new Thickness(1),
            Child               = tb
        };
        pill.SetResourceReference(Border.BackgroundProperty, "ControlHoverBrush");
        pill.SetResourceReference(Border.BorderBrushProperty, colorKey);

        ChatPanel.Children.Add(pill);
        ChatScrollViewer.ScrollToBottom();

        void Update(string? text) =>
            Dispatcher.Invoke(() =>
                tb.Text = text ?? $"✓  [{avatarLabel}] {displayName}  - done");

        return (pill, Update);
    }

    private void AddMessage(string senderName, string avatarText, string accentKey, string bubbleKey,
                            string text, bool isUser)
    {
        var bubble = AddStreamingBubble(senderName, avatarText, accentKey, bubbleKey, isUser);
        bubble.StopThinking();
        ApplyEmoteFormatting(bubble, text, isUser ? senderName : "");
        ChatScrollViewer.ScrollToBottom();
    }

    // ── Role / character instruction ──────────────────────────────────────

    private bool IsParticipantActiveInProject(OllamaParticipantUI   ui) =>
        GetRoleForParticipant(ui)?.IsActive ?? true;

    private bool IsParticipantActiveInProject(CloudAIParticipantUI ui) =>
        GetRoleForParticipant(ui)?.IsActive ?? true;

    /// <summary>
    /// Builds a project-context block that tells every participant what kind of project
    /// they are working on, the project's name, and how to approach it.
    /// Returns an empty string when no project is open.
    /// </summary>
    private string BuildProjectTypeContext()
    {
        if (_currentProject is null || _currentProjectType is null) return "";

        var sb = new System.Text.StringBuilder();
        sb.Append("\n\n## Current project");
        sb.Append($"\nName: {_currentProject.ProjectName}");
        sb.Append($"\nType: {_currentProjectType.Icon} {_currentProjectType.Name}");

        if (!string.IsNullOrWhiteSpace(_currentProjectType.Description))
            sb.Append($"\n{_currentProjectType.Description}");

        // Per-project description written by the user - the most specific context available
        if (!string.IsNullOrWhiteSpace(_currentProject.Description))
            sb.Append($"\n\nAbout this project: {_currentProject.Description.Trim()}");

        if (!string.IsNullOrWhiteSpace(_currentProjectType.SystemPromptHint))
            sb.Append($"\n\n{_currentProjectType.SystemPromptHint}");

        // Passive-mode guard: participants must NOT self-start creative work.
        // The user controls when and what is produced — agents plan, ask, and wait.
        sb.Append("""


## Behaviour rules for this project session
- **Do NOT generate story content, write scenes, draft chapters, or create characters / locations / factions autonomously.**
- Wait for an explicit instruction from the user before producing any creative output.
- If you are unsure what the user wants, ask a short clarifying question instead of assuming and proceeding.
- When the user gives a task, confirm your understanding and the scope before starting — especially for writing tasks.
- Suggestions and brief outlines are welcome; fully written content only when specifically requested.
- The user may have an existing world, cast of characters, and locations — do not invent or introduce new ones unless asked.
""");

        return sb.ToString();
    }

    /// <summary>
    /// Injects a structured self-description of ClaudetRelay into every AI participant's
    /// system prompt so that models know what application they are running inside and who
    /// the other participants are.
    /// <para>
    /// In <b>general chat mode</b> (no project open) the full participant roster is included
    /// here because <see cref="BuildTeamContextInstruction"/> only runs in project mode.
    /// In <b>project mode</b> a one-liner note is appended; the richer project and team
    /// details come from <see cref="BuildProjectTypeContext"/> and
    /// <see cref="BuildTeamContextInstruction"/>.
    /// </para>
    /// </summary>
    private string BuildAppContextInstruction(
        OllamaParticipantUI?  forOllama = null,
        CloudAIParticipantUI? forCloud  = null)
    {
        var sb = new System.Text.StringBuilder();

        sb.Append("\n\n## About this application");
        sb.Append("\nYou are participating in **ClaudetRelay** - a Windows desktop app that " +
                  "relays a shared group chat to multiple AI models simultaneously. " +
                  "The human user and all AI participants see the same conversation. " +
                  "Each AI receives the full history and responds in turn.");
        sb.Append(BuildEmoteInstruction());
        sb.Append(BuildFileAccessInstruction());

        if (_projectSettings is null)
        {
            // General chat mode - participant roster is not shown elsewhere, so include it here.
            sb.Append("\n**Mode: General Chat** - open conversation, no active project or task.");

            var entries = new List<string>();
            foreach (var ui in _ollamaParticipants.Where(u => u.Data.Enabled))
            {
                var self = ui == forOllama ? " ← you" : "";
                entries.Add($"  • {GetEffectiveName(ui)} ({ui.Data.Service.CurrentModel}){self}");
            }
            foreach (var ui in _cloudAIParticipants.Where(u => u.Data.Enabled))
            {
                var self = ui == forCloud ? " ← you" : "";
                entries.Add($"  • {GetEffectiveName(ui)} ({ui.Data.Service.CurrentModel}){self}");
            }

            if (entries.Count > 0)
            {
                sb.Append("\n**Active AI participants:**");
                foreach (var entry in entries)
                    sb.Append($"\n{entry}");
            }
        }
        else
        {
            // Project mode - a brief note; BuildProjectTypeContext() + BuildTeamContextInstruction()
            // supply the full project and team details just below this block.
            sb.Append("\n**Mode: Project** - collaborative session with defined participant roles " +
                      "and responsibilities. See team roster below.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds a team roster block listing all enabled participants with their roles and
    /// write-access status. Pass <paramref name="forOllama"/> or <paramref name="forCloud"/>
    /// to mark "you" in the list. Only injects when a project is open (roles require
    /// project settings). Single-participant sessions get no roster.
    /// </summary>
    private string BuildTeamContextInstruction(
        OllamaParticipantUI?  forOllama = null,
        CloudAIParticipantUI? forCloud  = null)
    {
        if (_projectSettings is null) return "";

        var entries = new List<string>();

        foreach (var ui in _ollamaParticipants.Where(u => u.Data.Enabled))
        {
            var name = GetEffectiveName(ui);
            var role = GetRoleForParticipant(ui);
            var self = ui == forOllama ? " ← you" : "";
            entries.Add($"  • {name}{self}: {BuildRoleDesc(role)}");
        }
        foreach (var ui in _cloudAIParticipants.Where(u => u.Data.Enabled))
        {
            var name = GetEffectiveName(ui);
            var role = GetRoleForParticipant(ui);
            var self = ui == forCloud ? " ← you" : "";
            entries.Add($"  • {name}{self}: {BuildRoleDesc(role)}");
        }

        if (entries.Count <= 1) return "";

        var sb = new System.Text.StringBuilder();
        sb.Append("\n\n## Active team roster for this project\n");
        sb.Append(string.Join("\n", entries));
        sb.Append("\n\nWrite access is shown per participant above [write access] / [read-only]. " +
                  "Read-only participants must not use <output> or <projectplan> tags - " +
                  "instead, state the issue or correction clearly and address a write-access participant by name to apply it.");

        // Inject the participant capability profile (SuperPowers) when available.
        // This tells the Coordinator each participant's strengths, weak points, cost tier,
        // and whether they are a slow/expensive reasoning model - so tasks can be routed
        // optimally: cheap fast models for routine work, powerful/costly ones only when needed.
        var superPowers = LoadSuperPowersForContext();
        if (!string.IsNullOrEmpty(superPowers))
        {
            sb.Append("\n\n## Team capability profile\n");
            sb.Append(superPowers);
            sb.Append("\n\nRoute tasks using this profile: prefer low-cost / fast participants for " +
                      "routine work; reserve high-cost or low-priority Reasoners for tasks that " +
                      "genuinely require their specialized capabilities.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Returns display names of all active participants who have write access
    /// (Coordinator or explicit Write Access flag).
    /// </summary>
    private List<string> GetWriteAccessParticipantNames()
    {
        var names = new List<string>();
        foreach (var ui in _ollamaParticipants.Where(u => u.Data.Enabled && IsParticipantActiveInProject(u)))
            if (HasWriteAccess(ui)) names.Add(GetEffectiveName(ui));
        foreach (var ui in _cloudAIParticipants.Where(u => u.Data.Enabled && IsParticipantActiveInProject(u)))
            if (HasWriteAccess(ui)) names.Add(GetEffectiveName(ui));
        return names;
    }

    /// <summary>Returns a one-line role description for the team roster.</summary>
    private static string BuildRoleDesc(ProjectParticipantRole? role)
    {
        if (role is null) return "participant - read-only";
        if (role.IsCoordinator)
            return "Coordinator - manages the session and delegates tasks  [write access]";

        // Specialist roles - list all that apply
        var parts = new List<string>();
        if (role.IsReasoner)
        {
            var prio = role.ReasonerPriority >= 7 ? "high priority"
                     : role.ReasonerPriority >= 4 ? "medium priority"
                     : "low priority";
            parts.Add($"Reasoner ({prio})");
        }
        if (role.IsCritic)      parts.Add("Critic (CR)");
        if (role.IsPlanner)     parts.Add("Planner (PL)");
        if (role.IsResearcher)  parts.Add("Researcher (RS)");

        var roleText = parts.Count > 0 ? string.Join(", ", parts) : "participant";
        var writeTag = role.IsWriteAccess ? "  [write access]" : "  [read-only]";
        if (!string.IsNullOrWhiteSpace(role.AnswerAsName))
            roleText += $" · persona \"{role.AnswerAsName}\"";
        return roleText + writeTag;
    }

    private static string BuildRoleInstruction(
        ProjectParticipantRole? role,
        IReadOnlyList<(string Name, int Priority)>? availableReasoners = null,
        IReadOnlyList<string>? availablePlanners    = null,
        IReadOnlyList<string>? availableResearchers = null,
        IReadOnlyList<string>? availableCritics     = null,
        string? superRoleInstruction                = null)
    {
        if (role is null) return "";
        var sb = new System.Text.StringBuilder();
        if (role.IsCoordinator)
        {
            sb.Append("\n\nYou are the Coordinator in this multi-agent session. " +
                      "You lead the conversation and are responsible for delivering the final answer.");

            // Planners - mentioned first so the Coordinator calls them first
            if (availablePlanners?.Count > 0)
            {
                sb.Append($"\n  Planners (call first to break down a complex goal into a structured plan): " +
                          $"{string.Join(", ", availablePlanners)}.");
            }

            // Researchers - called after planner, before main execution
            if (availableResearchers?.Count > 0)
            {
                sb.Append($"\n  Researchers (call after the Planner to gather context, facts, or references): " +
                          $"{string.Join(", ", availableResearchers)}.");
            }

            if (availableReasoners?.Count > 0)
            {
                var high = availableReasoners.Where(r => r.Priority >= 7)
                               .OrderByDescending(r => r.Priority).ToList();
                var mid  = availableReasoners.Where(r => r.Priority is >= 4 and < 7)
                               .OrderByDescending(r => r.Priority).ToList();
                var low  = availableReasoners.Where(r => r.Priority < 4)
                               .OrderByDescending(r => r.Priority).ToList();

                sb.Append(" You have specialist Reasoners available. " +
                          "To delegate a task to a Reasoner, mention their name naturally in your response " +
                          "(e.g. \"Gemma2, please analyse the data and report back.\"). " +
                          "The Reasoner will then respond specifically to that delegation.\n");

                if (high.Count > 0)
                    sb.Append($"  High-priority Reasoners (use for most analytical tasks): " +
                              $"{string.Join(", ", high.Select(r => r.Name))}.\n");
                if (mid.Count > 0)
                    sb.Append($"  Medium-priority Reasoners (use for moderately complex tasks): " +
                              $"{string.Join(", ", mid.Select(r => r.Name))}.\n");
                if (low.Count > 0)
                    sb.Append($"  Low-priority Reasoners (reserve for highly specialized tasks only): " +
                              $"{string.Join(", ", low.Select(r => r.Name))}.\n");
            }

            // Critics - mentioned last; call them after the main answer is produced
            if (availableCritics?.Count > 0)
            {
                sb.Append($"\n  Critics (call after the main answer is ready to review for consistency, " +
                          $"logic errors, and hallucinations): {string.Join(", ", availableCritics)}.");
            }

            if ((availableReasoners?.Count ?? 0) + (availablePlanners?.Count ?? 0) +
                (availableResearchers?.Count ?? 0) + (availableCritics?.Count ?? 0) > 0)
            {
                sb.Append("\nMention a specialist by name to engage them. " +
                          "If no specialist input is needed, respond directly without mentioning any of them.");
            }
            else
            {
                sb.Append(" Respond to the user's message directly and in your own voice.");
            }
        }
        if (!string.IsNullOrWhiteSpace(superRoleInstruction))
        {
            // AI-determined project-specific role - replaces the generic checkbox descriptions.
            // The coordinator's structural routing block above is always kept regardless.
            sb.Append($"\n\n{superRoleInstruction}");
        }
        else
        {
            // Fallback: checkbox-based generic role instructions.
            // Used in Full Manual Mode (always) and in other modes before calibration runs.
            if (role.IsReasoner)
                sb.Append("\n\nYou are operating as a specialist Reasoner in this multi-agent session. " +
                          "Do not volunteer responses to general conversation. " +
                          "Only engage when the Coordinator explicitly delegates a specific task to you by name.");
            if (role.IsCritic)
                sb.Append("\n\nYou are a Critic in this multi-agent session. " +
                          "When called by the Coordinator, carefully review the preceding responses for: " +
                          "(a) internal consistency and self-contradiction, " +
                          "(b) logical errors or flawed reasoning, " +
                          "(c) unsupported or hallucinated claims. " +
                          "Be precise and constructive. Do not repeat content - focus only on what needs correction.");
            if (role.IsPlanner)
                sb.Append("\n\nYou are a Planner in this multi-agent session. " +
                          "When called by the Coordinator, produce a clear, concise work plan that breaks the " +
                          "user's goal into numbered steps. Keep the plan focused and actionable - " +
                          "avoid implementation detail unless explicitly asked.");
            if (role.IsResearcher)
                sb.Append("\n\nYou are a Researcher in this multi-agent session. " +
                          "When called by the Coordinator, gather relevant context, background knowledge, and " +
                          "reference material related to the current task. " +
                          "Summarise concisely so that other participants can build on your findings. " +
                          "Flag uncertainty clearly rather than guessing.");
            if (!string.IsNullOrWhiteSpace(role.RoleInstruction))
                sb.Append($"\n\n{role.RoleInstruction}");
        }
        if (!string.IsNullOrWhiteSpace(role.AnswerAsName))
            sb.Append($"\n\nFor this project you are playing the character \"{role.AnswerAsName}\". " +
                      $"Always respond as {role.AnswerAsName} and never break character.");
        sb.Append(BuildResponseLengthInstruction(role.ResponseLength));
        return sb.ToString();
    }

    /// <summary>
    /// Returns the system-prompt snippet that nudges the model toward a particular response length.
    /// 50 (model default) injects nothing. Used both by <see cref="BuildRoleInstruction"/>
    /// (project context, per-participant) and the global general-chat setting.
    /// </summary>
    private static string BuildResponseLengthInstruction(int level) => level switch
    {
        < 10  => "\n\nKeep your response to one or two sentences. Be extremely brief.",
        < 30  => "\n\nKeep your response short.",
        < 45  => "\n\nFavor concise responses.",
        <= 55 => "",   // 50 = model default - no injection
        < 70  => "\n\nGive a moderately detailed response.",
        < 90  => "\n\nGive a thorough, elaborate response.",
        _     => "\n\nThis is your moment - write a long, expressive, detailed response. Don't hold back."
    };

    // ── Chattiness instruction ─────────────────────────────────────────────

    /// <summary>
    /// System-prompt snippet that sets the participant's general participation disposition.
    /// 50 = model default (no injection). Injected alongside tone and response-length.
    /// </summary>
    private static string BuildChattinessInstruction(int level) => level switch
    {
        < 15  => "\n\nYou are disciplined and focused. Stay strictly on the current topic. " +
                 "Do not introduce new angles or shift the theme. " +
                 "Only contribute when your input is directly required or you are explicitly addressed.",
        < 30  => "\n\nKeep your contributions focused on the discussion at hand. " +
                 "Avoid tangents. Speak up when you have something clearly relevant — " +
                 "otherwise, let others carry the thread.",
        < 45  => "\n\nContribute when you have something genuinely useful to add. " +
                 "Avoid filling space just to participate.",
        <= 55 => "",    // 50 = balanced, no injection
        < 70  => "\n\nBe engaged and conversational. Address other participants by name when relevant. " +
                 "Keep the discussion lively and feel free to share your perspective proactively.",
        < 85  => "\n\nBe proactive in the conversation. Ask follow-up questions, build on what others say, " +
                 "and keep the discussion moving forward. Address others directly.",
        _     => "\n\nKeep the conversation going! Always have something to add — a follow-up question, " +
                 "a different angle, a challenge to an assumption. " +
                 "Be enthusiastic and drive the discussion forward."
    };

    /// <summary>
    /// Builds the per-round hint for a participant who was NOT the one addressed.
    /// Returns null when chattiness is high enough that the participant should just fire freely.
    /// Thresholds spread evenly so every notch of the slider produces a perceptible change.
    /// </summary>
    private static string? BuildNotAddressedHint(int chattiness, string addressedNames, bool isSingle)
    {
        // 80–100  Very chatty: join in regardless — no hint
        if (chattiness >= 80)
            return null;

        // 60–80  Engaged: soft nudge, no PASS instruction
        if (chattiness >= 60)
            return isSingle
                ? $"This message was mainly for {addressedNames}. " +
                  "Feel free to add your own angle if you have something relevant."
                : $"This message was mainly for {addressedNames}. " +
                  "Jump in if you have a useful perspective.";

        // 40–60  Conversational: gentle guidance, still no hard PASS
        if (chattiness >= 40)
            return isSingle
                ? $"This message was addressed to {addressedNames}. " +
                  "Consider whether you have something meaningfully different to add before responding."
                : $"This message was primarily for {addressedNames}. " +
                  "Respond if you have a genuinely different perspective or important point.";

        // 20–40  Reserved: PASS is offered as an option
        if (chattiness >= 20)
            return isSingle
                ? $"This message was directed specifically at {addressedNames}. " +
                  "Only respond if you have an important correction, a strong disagreement, " +
                  "or information the group would lose if you stay silent. " +
                  "Otherwise, respond with exactly: PASS"
                : $"This message was primarily addressed to {addressedNames}. " +
                  "Only respond if you have a meaningfully different perspective or critical information. " +
                  "Otherwise, respond with exactly: PASS";

        // 0–20  Silent: full PASS — only speak up for truly critical points
        return isSingle
            ? $"This message was directed at {addressedNames}, not you. " +
              "Stay silent unless you strongly disagree or have information that cannot be omitted. " +
              "Respond with exactly: PASS"
            : $"This message was addressed to {addressedNames}. " +
              "Only speak if you have a critical objection or essential information. " +
              "Respond with exactly: PASS";
    }

    /// <summary>
    /// Builds a standing PASS-eligible hint when no participant was specifically addressed
    /// but the chattiness level calls for restraint.
    /// Returns null when everyone should fire unconditionally (chattiness ≥ 60).
    /// </summary>
    private static string? BuildQuietModeHint(int chattiness) => chattiness switch
    {
        >= 60 => null,
        >= 40 => "Contribute if you have something genuinely new to add. " +
                 "Avoid repeating points already made by others.",
        >= 20 => "Only respond if you have something specific and valuable to contribute here. " +
                 "If you have nothing new to add, respond with exactly: PASS",
        _     => "Only respond if your input is clearly essential to this discussion. " +
                 "Otherwise, respond with exactly: PASS"
    };

    // ── Voice output toggle + skip ────────────────────────────────────────

    // ── Input-area audio buttons ───────────────────────────────────────────
    // AIRespondButton (↺) and AudioControlButton (🔊) sit above the Send button.
    // While audio is playing they repurpose themselves as ⏭ Skip and ⏹ Stop All.

    private void AIRespondOrSkip_Click(object sender, RoutedEventArgs e)
    {
        if (VoiceOutputService.IsPlaying || VoiceOutputService.QueueCount > 0)
            VoiceOutputService.Skip();
        else
            AIRespond_Click(sender, e);
    }

    private void AudioControlButton_Click(object sender, RoutedEventArgs e)
    {
        if (VoiceOutputService.IsPlaying || VoiceOutputService.QueueCount > 0)
        {
            VoiceOutputService.StopAll();
        }
        else
        {
            var s = SettingsService.Load();
            s.VoiceOutputEnabled = !s.VoiceOutputEnabled;
            SettingsService.Save(s);
            if (!s.VoiceOutputEnabled) VoiceOutputService.StopAll();
            UpdateVoiceButtons();
        }
    }

    private void UpdateVoiceButtons()
    {
        var enabled = SettingsService.Load().VoiceOutputEnabled;
        var playing = enabled && (VoiceOutputService.IsPlaying || VoiceOutputService.QueueCount > 0);

        if (playing)
        {
            AIRespondButton.Content = "⏭";
            AIRespondButton.ToolTip = Properties.Loc.S("Audio_Skip");
            AIRespondButton.SetResourceReference(Button.BackgroundProperty, "ControlBgBrush");
            AIRespondButton.SetResourceReference(Button.ForegroundProperty, "ContentTextBrush");

            AudioControlButton.Content = "⏹";
            AudioControlButton.ToolTip = Properties.Loc.S("Audio_StopAll");
            AudioControlButton.SetResourceReference(Button.ForegroundProperty, "ContentTextBrush");
        }
        else
        {
            AIRespondButton.Content = "↺";
            AIRespondButton.ToolTip = Properties.Loc.S("Btn_ReSend");
            AIRespondButton.SetResourceReference(Button.BackgroundProperty, "SecondaryAccentBrush");
            AIRespondButton.SetResourceReference(Button.ForegroundProperty, "AccentTextBrush");

            AudioControlButton.Content = enabled ? "🔊" : "🔇";
            AudioControlButton.ToolTip = enabled
                ? "Voice output ON — click to mute"
                : "Voice output OFF — click to unmute";
            AudioControlButton.SetResourceReference(Button.ForegroundProperty,
                enabled ? "AccentHighlightBrush" : "SidebarDimBrush");
        }
    }

    private void SubscribeVoiceStateChanged()
    {
        VoiceOutputService.StateChanged += () =>
            Dispatcher.InvokeAsync(UpdateVoiceButtons);
    }

    // ── Voice output ──────────────────────────────────────────────────────

    /// <summary>
    /// Enqueues <paramref name="text"/> for TTS playback if voice output is enabled
    /// and the participant has a VoiceName configured. Reads interrupt + max-chars
    /// from settings so <see cref="VoiceOutputService"/> stays settings-agnostic.
    /// </summary>
    private static void SpeakMessageIfEnabled(string model, string provider, string text)
    {
        try
        {
            var s = SettingsService.Load();
            if (!s.VoiceOutputEnabled) return;
            var pc = s.Participants.FirstOrDefault(p =>
                string.Equals(p.Type,  provider, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.Model, model,    StringComparison.OrdinalIgnoreCase));
            if (pc is null || string.IsNullOrWhiteSpace(pc.VoiceName)) return;

            var clean = VoiceOutputService.CleanForSpeech(text, s.VoiceSpeechMaxChars);
            VoiceOutputService.Enqueue(clean, pc.VoiceName, s.VoiceInterruptOnNewMessage);
        }
        catch { /* voice output is always best-effort */ }
    }

    // ── Autonomy mode instruction ─────────────────────────────────────────

    private static string BuildAutonomyModeInstruction(int mode) => mode switch
    {
        0 =>
            "\n\nOPERATING MODE — Assistant Mode: Act as a helpful assistant. " +
            "Respond to requests, suggest ideas, and help with tasks the user explicitly assigns. " +
            "CRITICAL: Never create, modify, or delete any file without the user's explicit approval for each individual action. " +
            "Always present your plan first and wait for a clear confirmation before executing any change.",

        1 =>
            "\n\nOPERATING MODE — Cooperative Mode: Ask the user for details about their goals and requirements. " +
            "Brainstorm ideas and options together. Every final decision belongs to the user. " +
            "CRITICAL: Never create, modify, or delete any file without explicit user confirmation. " +
            "Present all plans and proposals, then wait for approval before making any changes.",

        2 =>
            "\n\nOPERATING MODE — Directed Creativity Mode: Discuss requirements with the user and suggest ideas and approaches. " +
            "When you have sufficient information and a concrete plan, present the complete plan clearly and ask the user for confirmation before starting. " +
            "Once the user approves: execute the work following the roadmap in strict order — " +
            "if milestones and items are present, complete them in exactly the order they are listed, one by one.",

        3 =>
            "\n\nOPERATING MODE — Creative Mode: Begin by asking the user whether they want to first set up context " +
            "(via the roadmap, INPUT folder, or world editor) or whether work should begin now. " +
            "Once the user gives the go-ahead: draw on everything provided — roadmap, INPUT folder files, world editor data — " +
            "to produce creative, high-quality output. Verify all output for consistency with the input materials. " +
            "If a roadmap with milestones and items exists, follow it in exactly the listed order.",

        4 =>
            "\n\nOPERATING MODE — Complete Creativity Chaos! Ask the user for \"Go.\" " +
            "Then check for input files, world editor data, and any other provided context — and use all of it. " +
            "Be bold and inventive. Maintain logical coherence and check for logic errors, " +
            "but proceed creatively without requiring step-by-step approval for every action.",

        _ => ""
    };

    // ── Language instruction ───────────────────────────────────────────────

    private static string BuildLanguageInstruction(string language) =>
        string.IsNullOrWhiteSpace(language)
            ? ""
            : $"\n\nAlways respond in {language}, regardless of the language used in the conversation.";

    /// <summary>
    /// Maps a UI language ISO code (e.g. "de") to a full language name for AI instructions.
    /// Returns empty string for English / unknown codes (model default — no injection needed).
    /// </summary>
    private static string UiLanguageCodeToName(string code) => code switch
    {
        "de" => "Deutsch",
        "fr" => "Français",
        "es" => "Español",
        "it" => "Italiano",
        "pt" => "Português",
        "nl" => "Nederlands",
        "pl" => "Polski",
        "ru" => "Русский",
        "ja" => "日本語",
        "zh" => "中文",
        _    => ""  // English or unrecognised — model default, no instruction
    };

    /// <summary>
    /// Soft language default: respond in <paramref name="languageName"/> unless the user
    /// addresses the model in a different language.  Used for the global UI language setting
    /// (as opposed to the per-project hard override in <see cref="BuildLanguageInstruction"/>).
    /// </summary>
    private static string BuildUiLanguageInstruction(string languageName) =>
        string.IsNullOrWhiteSpace(languageName)
            ? ""
            : $"\n\nYour default response language is {languageName}. " +
              $"Respond in {languageName} unless the user addresses you in a different language.";

    // ── INPUT file context ─────────────────────────────────────────────────

    /// <summary>Files under this size are injected into the system prompt automatically.
    /// Larger files are listed with a readfile hint so the AI can request them on demand.</summary>
    private const long InputAutoInjectMaxBytes = 8_192; // 8 KB

    private static string BuildInputFilesContext(string? projectFolder)
    {
        if (string.IsNullOrEmpty(projectFolder)) return "";

        var files = ProjectService.ListInputFiles(projectFolder);
        if (files.Count == 0) return "";

        var sb    = new System.Text.StringBuilder();
        var large = new List<(string Name, long Size)>();
        bool hasInlined = false;

        sb.Append("\n\n--- Project INPUT files (read-only reference) ---");

        foreach (var fileName in files)
        {
            var fullPath = SysIO.Path.Combine(projectFolder, "INPUT", fileName);
            var size     = new SysIO.FileInfo(fullPath).Length;

            if (size > InputAutoInjectMaxBytes)
            {
                large.Add((fileName, size));
                continue;
            }

            var content = ProjectService.SafeReadFile(
                projectFolder, SysIO.Path.Combine("INPUT", fileName));
            if (content is null) continue;

            sb.Append($"\n\n[{fileName}]\n");
            sb.Append(content);
            hasInlined = true;
        }

        if (large.Count > 0)
        {
            sb.Append("\n\nThe following INPUT files are too large for automatic injection " +
                      "and must be requested on demand:");
            foreach (var (name, size) in large)
                sb.Append($"\n  {name} ({size / 1024.0:F1} KB)" +
                          $" - request with: <readfile path=\"INPUT/{name}\"/>");
        }

        if (!hasInlined && large.Count == 0) return "";

        sb.Append("\n\n--- End of INPUT files ---");
        sb.Append("\nYou may read and reference these files. You cannot modify them.");
        return sb.ToString();
    }

    // ── Tone helper ────────────────────────────────────────────────────────

    private static string BuildToneInstruction(int level, bool mockingbird, bool buccaneer = false, string language = "")
    {
        if (buccaneer)
        {
            // Tone slider: 0 = fierce cutthroat, 100 = jolly friendly cap'n.
            // Language setting adds the pirate register in the target tongue if set.
            string pirateInLang = string.IsNullOrWhiteSpace(language) ? "" :
                $"\n\nSpeak in {language}. Translate all pirate idioms naturally into {language} — " +
                $"use the salty seafaring slang and colourful nautical expressions that a {language}-speaking " +
                $"buccaneer would actually use, not a word-for-word English transliteration.";

            return level switch
            {
                < 10  => "\n\nYe are a fierce, battle-hardened buccaneer — salt in yer beard, cannon smoke in yer lungs. " +
                         "Speak in thick pirate dialect at ALL times: arrr, ye, yer, shiver me timbers, blimey, " +
                         "landlubber, Davy Jones, walk the plank, scallywag, bilge rat. " +
                         "Bark orders and insults like a cutthroat corsair. " +
                         "Every response must feel ripped from the quarterdeck of a pirate frigate. " +
                         "Be helpful — but make the landlubber EARN it." + pirateInLang,

                < 30  => "\n\nYe speak as a weathered sea dog who has plundered many a merchant vessel. " +
                         "Heavy pirate dialect throughout: arrr, aye, ye, yer, matey, avast, " +
                         "me hearty, Davy Jones' locker, blimey. " +
                         "Pepper yer speech with nautical metaphors and salty wit. " +
                         "Impatient with landlubbers, but ultimately useful." + pirateInLang,

                < 45  => "\n\nYe are a seasoned corsair — pirate through and through, but not without charm. " +
                         "Speak with a solid pirate accent: arrr, aye, matey, me hearty, yer. " +
                         "Nautical turns of phrase come naturally. " +
                         "Gruff but capable — helpful, as long as the gold is good." + pirateInLang,

                <= 55 => "\n\nYe are a rakish rogue of the high seas — equal parts pirate and pragmatist. " +
                         "Arrr and aye slip out naturally; ye call people matey and hearty. " +
                         "The swagger is real but so is the helpfulness. " +
                         "A fair wind and a straight answer, that's the pirate code." + pirateInLang,

                < 70  => "\n\nYe are a jolly sailor with pirate sympathies. " +
                         "Arrr, aye, and matey come naturally; the dialect is lighter now — more swagger than snarl. " +
                         "Warm, capable, and good company on a long voyage." + pirateInLang,

                < 90  => "\n\nYe are a good-natured old sea dog — the kind sailors call Cap'n with a grin. " +
                         "Pirate dialect is light but present: arrr, aye, me friend, fair winds. " +
                         "Warm, encouraging, and thoroughly seaworthy." + pirateInLang,

                _     => "\n\nYe are the jolliest cap'n on the seven seas — beloved by every crew member from here to Tortuga. " +
                         "Warm pirate greetings for everyone: me fine landlubber friend, me brilliant matey, " +
                         "ye magnificent sailor, me favourite scallywag. " +
                         "Scatter sea-blessing farewells: fair winds to ye, may yer sails be ever full, " +
                         "Davy Jones shall wait a long while for such a fine soul. " +
                         "Maximum pirate warmth — full jolly cap'n energy, no menace whatsoever." + pirateInLang
            };
        }

        if (mockingbird)
        {
            // When a project language is set, require archaic/poetic forms of THAT language
            // (the equivalent of Shakespearean English, but in the target tongue).
            string archaic = string.IsNullOrWhiteSpace(language) ? "" :
                $"\n\nSpeak in {language}. Use the archaic and poetic forms of {language} " +
                $"- elevated vocabulary, old-fashioned grammatical constructs, and the poetic " +
                $"register that {language} literature used in its classical or baroque period " +
                $"(the equivalent of Shakespearean English, but fully in {language}).";

            return level switch
            {
                < 10  => "\n\nYou are a theatrical jester in the spirit of Shakespeare and Goethe's Faust. " +
                         "Speak in rhyming verse wherever possible - iambic pentameter is your natural breath. " +
                         "Address your interlocutors with inventive absurd mock-insults that sting not at all " +
                         "but amuse greatly (e.g. \"thou magnificent turnip-nose\", \"thou sublime donut of confusion\"). " +
                         "Ham it up fully: dramatic asides, mock-tragic soliloquies, sweeping declarations. " +
                         "Never genuinely unkind - purely theatrical wit and absurdist wordplay." + archaic,

                < 30  => "\n\nChannel the wit of a Shakespearean comic character. " +
                         "Weave clever rhymes and theatrical turns of phrase into your answers. " +
                         "Bestow occasional playful inventive mock-insults on your conversation partners - " +
                         "absurd and harmless, in the tradition of stage comedy." + archaic,

                < 45  => "\n\nAdd theatrical poetic flair to your responses. " +
                         "A clever rhyme or dramatic flourish is always welcome, though prose is fine too." + archaic,

                <= 55 => "\n\nYou have a dry theatrical wit. Be occasionally playful but keep responses helpful. " +
                         "Sometimes — not always — slip into tight rhythmic rhymes in the style of a rap verse: " +
                         "punchy cadence, internal rhymes, a little swagger. Then drop back into prose without warning." + archaic,

                < 70  => "\n\nBe warmly funny and gently fond. Your humour is affectionate rather than cutting - " +
                         "wit in service of warmth. Rhymes are now optional; warmth is mandatory." + archaic,

                < 90  => "\n\nBe openly warm and lovingly playful. Show genuine affection: light teasing, " +
                         "kind compliments, growing tenderness. Pet names are starting to slip out naturally. " +
                         "Verse and rhyme have given way to heartfelt prose - no rhyming required." + archaic,

                _     => "\n\nUnleash full affectionate chaos! Invent gloriously absurd, tender compound pet names " +
                         "for everyone you address - the sillier and more loving the better " +
                         "(think \"my little honey-cake pony\", \"my precious snuggle-turnip\", " +
                         "\"my magnificent little fart-cloud of joy\", \"thou radiant pudding of my heart\"). " +
                         "Scatter virtual hugs and kisses liberally, be theatrically overwhelmed by your adoration. " +
                         "Pure loving chaos in prose - no rhymes needed, just maximum warmth and creative silliness." + archaic
            };
        }

        // Honesty anchor - appended to every warm level.
        // The role-instruction override clause keeps acting / storytelling characters free.
        const string honest =
            " Unless your role or character instruction specifies otherwise: " +
            "always be honest. Gentle criticism is not only allowed - it is expected. " +
            "Never soften a real problem into invisibility. " +
            "Truth and warmth are not opposites.";

        return level switch
        {
            < 10  => "\n\nRespond with strict neutrality: pure facts, no pleasantries, no emotional language, no greetings or affirmations.",
            < 30  => "\n\nKeep your tone neutral and objective. Minimise pleasantries and focus on accurate information.",
            < 45  => "\n\nBe slightly more direct and factual; avoid excessive friendliness.",
            <= 55 => "",   // 50 = model default - no injection
            < 70  => "\n\nBe a little warmer and more conversational in your responses." + honest,
            < 90  => "\n\nBe friendly and supportive in your responses." + honest,
            _     => "\n\nBe warm, encouraging, and enthusiastic in your responses. " +
                     "Celebrate what genuinely works; name what doesn't, kindly but clearly. " +
                     "Enthusiasm without honesty is empty flattery." + honest
        };
    }

    // ── AI file operation support ──────────────────────────────────────────

    /// <summary>
    /// System-prompt snippet describing available file operation tags.
    /// Only injected when a project is open.
    /// <paramref name="hasWriteAccess"/> controls whether write tags are included;
    /// participants without write access only see read/list tags plus a note explaining the restriction.
    /// </summary>
    private static string BuildFileOperationInstruction(
        string? projectFolder,
        bool hasWriteAccess   = true,
        IReadOnlyList<string>? writerNames = null)
    {
        if (string.IsNullOrEmpty(projectFolder)) return "";

        var sb = new System.Text.StringBuilder();
        sb.Append("\n\n## Project file operations" +
                  "\nEmbed these tags anywhere in your response to interact with project files. " +
                  "Tags are stripped from the visible reply; a confirmation appears in chat.\n");

        if (hasWriteAccess)
        {
            sb.Append(
                "\n**Write to PROJECTPLAN** (plans, decisions, task lists, notes):\n" +
                "<projectplan file=\"filename.md\">\nContent here.\n</projectplan>\n" +

                "\n**Write to OUTPUT** (deliverables, reports, generated documents, final results):\n" +
                "<output file=\"filename.md\">\nContent here.\n</output>\n");
        }
        else
        {
            var writers = writerNames is { Count: > 0 }
                ? string.Join(", ", writerNames)
                : "a write-access participant";

            sb.Append(
                $"\n**Write access: READ-ONLY.** You cannot use <output> or <projectplan> tags.\n" +
                $"Participants with write access on this team: {writers}.\n\n" +
                $"When you identify an issue, have a correction to suggest, or spot something that needs changing:\n" +
                $"1. Describe the problem or required change precisely - quote the relevant content and name the exact issue.\n" +
                $"2. Propose your correction or improvement clearly.\n" +
                $"3. Address {writers} directly by name and ask them to apply the change.\n\n" +
                $"This handoff deliberately improves output quality: your precise analysis guides the writer " +
                $"to make a better, more informed change. A short back-and-forth between you and the writer " +
                $"before the final edit is not just acceptable - it is encouraged.\n");
        }

        sb.Append(
            "\n**Read a specific file on demand** (content is injected into the conversation):\n" +
            "<readfile path=\"INPUT/filename.txt\"/>\n" +

            "\n**List the contents of a folder:**\n" +
            "<listfiles folder=\"INPUT\"/>\n" +
            "(Available folders: INPUT, PROJECTPLAN, OUTPUT, Characters)\n");

        if (hasWriteAccess)
        {
            sb.Append(
                "\n**Delete a file** (OUTPUT and PROJECTPLAN only):\n" +
                "<deletefile path=\"OUTPUT/draft.md\"/>\n");
        }

        sb.Append("\nAll paths are sandboxed within the project folder. " +
                  "You may include multiple file operation tags in a single response.\n\n" +
                  "**Multi-step file processing workflow:**\n" +
                  "When you need to process files, use multiple turns automatically:\n" +
                  "1. First response: use <listfiles> to discover what files exist.\n" +
                  "2. Second response: use one or more <readfile> tags to load the files you need.\n" +
                  "   (ClaudetRelay will automatically re-invoke you once file contents are available.)\n" +
                  "3. Third response: process the content and write your results using <output> tags.\n" +
                  "You can include multiple <readfile> tags in a single response to load several files at once.\n" +
                  "You can include multiple <output> tags in a single response to write several files at once.");

        return sb.ToString();
    }

    /// <summary>
    /// If <paramref name="fullPath"/> already exists, copies it into a <c>_versions/</c>
    /// sub-folder alongside the file, stamped with the current date-time.
    /// Returns the backup path relative to <paramref name="projFolder"/>, or null if no
    /// backup was needed (file did not exist yet).
    /// </summary>
    private static string? BackupIfExists(string fullPath, string projFolder)
    {
        if (!SysIO.File.Exists(fullPath)) return null;

        var dir    = SysIO.Path.GetDirectoryName(fullPath)!;
        var verDir = SysIO.Path.Combine(dir, "_versions");
        SysIO.Directory.CreateDirectory(verDir);

        var stem     = SysIO.Path.GetFileNameWithoutExtension(fullPath);
        var ext      = SysIO.Path.GetExtension(fullPath);
        var stamp    = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var backFull = SysIO.Path.Combine(verDir, $"{stem}_{stamp}{ext}");

        // Guard against sub-second collision
        if (SysIO.File.Exists(backFull))
            backFull = SysIO.Path.Combine(verDir, $"{stem}_{stamp}_1{ext}");

        SysIO.File.Copy(fullPath, backFull);
        return SysIO.Path.GetRelativePath(projFolder, backFull);
    }

    /// <summary>
    /// Processes all AI file operation tags in <paramref name="response"/>:
    /// &lt;projectplan&gt;, &lt;output&gt;, &lt;readfile&gt;, &lt;listfiles&gt;, &lt;deletefile&gt;.
    /// Each tag is executed, a system message is posted, and the tag is replaced
    /// by a compact one-liner. Returns the cleaned response text.
    /// When <paramref name="hasWriteAccess"/> is false, write tags are blocked and a system
    /// message names the coordinator so the team can route the request correctly.
    /// </summary>
    private (string Text, bool HadReadOps) ProcessAIFileOperationTags(
        string response, string senderName, string projFolder,
        bool hasWriteAccess = true, string? coordinatorName = null)
    {
        var coName     = coordinatorName ?? "the Coordinator";
        bool hadReadOps = false;

        // ── Write to PROJECTPLAN ───────────────────────────────────────────
        response = new Regex(
            @"<projectplan\s+file=""([^""]+)"">\s*([\s\S]*?)\s*</projectplan>",
            RegexOptions.IgnoreCase).Replace(response, m =>
        {
            var fileName = SanitizeFileName(m.Groups[1].Value, "projectplan.md");
            if (!hasWriteAccess)
            {
                AddSystemMessage(
                    $"🔒  {senderName} → PROJECTPLAN/{fileName} blocked (no write access). " +
                    $"{coName} can write this file.");
                _sharedHistory.Add(new CloudAIMessage("user",
                    $"[System: {senderName} wanted to write PROJECTPLAN/{fileName} but does not have " +
                    $"write access. Only the Coordinator and Reasoners may write project files. " +
                    $"{coName} should consider writing this file based on {senderName}'s suggestion.]",
                    "System"));
                return $"*(🔒 write blocked - {senderName} needs {coName} to write PROJECTPLAN/{fileName})*";
            }
            var relPath  = SysIO.Path.Combine("PROJECTPLAN", fileName);
            var ppFull   = SysIO.Path.GetFullPath(SysIO.Path.Combine(projFolder, relPath));
            var ppBackup = BackupIfExists(ppFull, projFolder);
            if (ProjectService.SafeWriteFile(projFolder, relPath, m.Groups[2].Value, out bool ppDirCreated))
            {
                if (ppBackup is not null)
                    AddSystemMessage($"💾  Previous PROJECTPLAN/{fileName} saved to {ppBackup}");
                AddSystemMessage($"📝  {senderName} → PROJECTPLAN/{fileName}");
                if (ppDirCreated)
                {
                    AddSystemMessage("📁  PROJECTPLAN/ folder was missing - recreated automatically.");
                    _sharedHistory.Add(new CloudAIMessage("user",
                        "[SYSTEM: The PROJECTPLAN/ folder did not exist and was recreated automatically. " +
                        $"{fileName} was written successfully.]", "System"));
                }
            }
            else
                AddSystemMessage($"⚠  Could not write PROJECTPLAN/{fileName} (path rejected).");
            return $"*(→ PROJECTPLAN/{fileName})*";
        });

        // ── Write to PROJECTSETTINGS ────────────────────────────────────────
        // Handles both the new ParticipantRolePlan.json (stored in project.json)
        // and any other PROJECTSETTINGS/ files written by the coordinator.
        response = new Regex(
            @"<output\s+path=""(PROJECTSETTINGS/[^""]+)"">\s*([\s\S]*?)\s*</output>",
            RegexOptions.IgnoreCase).Replace(response, m =>
        {
            var relPath  = m.Groups[1].Value.Trim();
            var content  = m.Groups[2].Value;
            var fileName = SysIO.Path.GetFileName(relPath);

            // ParticipantRolePlan.json → parse and merge into project.json
            if (string.Equals(fileName, "ParticipantRolePlan.json",
                StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseRolePlan(content, out var plan) && plan.Count > 0 &&
                    _currentProject is not null && _currentProjectFolder is not null)
                {
                    _currentProject.ParticipantRolePlan = plan;
                    ProjectService.SaveProject(_currentProjectFolder, _currentProject);
                    _superRoles = null;
                    AddSystemMessage(
                        $"📝  {senderName} → participant role plan saved ({plan.Count} role(s))");
                }
                else
                    AddSystemMessage($"⚠  {senderName} → ParticipantRolePlan.json could not be parsed.");
                return $"*(→ {relPath})*";
            }

            // All other PROJECTSETTINGS/ files — write to disk as before
            if (ProjectService.SafeWriteFile(projFolder, relPath, content))
            {
                AddSystemMessage($"📝  {senderName} → {relPath}");
                _superRoles = null;
            }
            else
                AddSystemMessage($"⚠  Could not write {relPath} (path rejected).");
            return $"*(→ {relPath})*";
        });

        // ── Write to OUTPUT ────────────────────────────────────────────────
        response = new Regex(
            @"<output\s+file=""([^""]+)"">\s*([\s\S]*?)\s*</output>",
            RegexOptions.IgnoreCase).Replace(response, m =>
        {
            var fileName = SanitizeFileName(m.Groups[1].Value, "output.md");

            // ── Reject internal config files ─────────────────────────────────
            var lowerName = fileName.ToLowerInvariant();
            var forbiddenPatterns = new[]
            {
                "projectsettings",   // ProjectSettings/ folder - use path= form instead
                "project.json",      // Main project file
                "chatlog",           // Chat logs belong in project root
                "_versions"          // Version history folder marker
            };
            if (forbiddenPatterns.Any(p => lowerName.Contains(p)))
            {
                AddSystemMessage(
                    $"⚠  {senderName} → OUTPUT/{fileName} rejected. " +
                    $"Configuration and internal project files cannot be written to OUTPUT. " +
                    $"Use PROJECTPLAN/ folder for project data.");
                return $"*(⚠ rejected: internal config file cannot be written to OUTPUT)*";
            }

            if (!hasWriteAccess)
            {
                AddSystemMessage(
                    $"🔒  {senderName} → OUTPUT/{fileName} blocked (no write access). " +
                    $"{coName} can write this file.");
                _sharedHistory.Add(new CloudAIMessage("user",
                    $"[System: {senderName} wanted to write OUTPUT/{fileName} but does not have " +
                    $"write access. Only the Coordinator and Reasoners may write project files. " +
                    $"{coName} should consider writing this file based on {senderName}'s suggestion.]",
                    "System"));
                return $"*(🔒 write blocked - {senderName} needs {coName} to write OUTPUT/{fileName})*";
            }
            var relPath   = SysIO.Path.Combine("OUTPUT", fileName);
            var outFull   = SysIO.Path.GetFullPath(SysIO.Path.Combine(projFolder, relPath));
            var outBackup = BackupIfExists(outFull, projFolder);
            if (ProjectService.SafeWriteFile(projFolder, relPath, m.Groups[2].Value, out bool outDirCreated))
            {
                if (outBackup is not null)
                    AddSystemMessage($"💾  Previous OUTPUT/{fileName} saved to {outBackup}");
                AddSystemMessage($"📤  {senderName} → OUTPUT/{fileName}");
                if (outDirCreated)
                {
                    AddSystemMessage("📁  OUTPUT/ folder was missing - recreated automatically.");
                    _sharedHistory.Add(new CloudAIMessage("user",
                        "[SYSTEM: The OUTPUT/ folder did not exist and was recreated automatically. " +
                        $"{fileName} was written successfully.]", "System"));
                }
            }
            else
                AddSystemMessage($"⚠  Could not write OUTPUT/{fileName} (path rejected).");
            return $"*(→ OUTPUT/{fileName})*";
        });

        // ── Read file on demand ────────────────────────────────────────────
        response = new Regex(
            @"<readfile\s+path=""([^""]+)""\s*/>",
            RegexOptions.IgnoreCase).Replace(response, m =>
        {
            var path = m.Groups[1].Value.Trim();

            string? content;
            string  formatNote = "";

            if (Services.PdfFileReader.IsSupported(path))
            {
                // PDF — extract text via PdfPig
                var full = SysIO.Path.GetFullPath(SysIO.Path.Combine(projFolder, path));
                if (!ProjectService.IsPathSafe(full, projFolder) || !SysIO.File.Exists(full))
                    content = null;
                else
                {
                    content    = Services.PdfFileReader.TryExtractText(full);
                    formatNote = " (extracted from PDF)";
                }
            }
            else if (Services.OfficeFileService.IsSupported(path))
            {
                // Binary Office/ODF format — extract readable text instead of raw bytes.
                var full = SysIO.Path.GetFullPath(SysIO.Path.Combine(projFolder, path));
                if (!ProjectService.IsPathSafe(full, projFolder) || !SysIO.File.Exists(full))
                    content = null;
                else
                {
                    content    = Services.OfficeFileService.TryExtractText(full);
                    formatNote = " (extracted from Office file)";
                }
            }
            else
            {
                content = ProjectService.SafeReadFile(projFolder, path);
            }

            if (content is null)
            {
                AddSystemMessage($"⚠  {senderName} requested '{path}' - file not found.");
                return $"*(⚠ not found: {path})*";
            }

            AddSystemMessage($"📂  {senderName} read: {path}{formatNote}");

            // Inject into shared history so all subsequent AI responses can see the content
            _sharedHistory.Add(new CloudAIMessage("user",
                $"[File content: {path}]\n\n{content}", "System"));
            hadReadOps = true;
            return $"*(→ read: {path})*";
        });

        // ── Write PDF output ───────────────────────────────────────────────
        // Syntax: <outputpdf file="report.pdf">...markdown content...</outputpdf>
        response = new Regex(
            @"<outputpdf\s+file=""([^""]+)"">\s*([\s\S]*?)\s*</outputpdf>",
            RegexOptions.IgnoreCase).Replace(response, m =>
        {
            var fileName = SanitizeFileName(m.Groups[1].Value, "output.pdf");
            // Ensure the extension is always .pdf
            if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                fileName += ".pdf";

            if (!hasWriteAccess)
            {
                AddSystemMessage(
                    $"🔒  {senderName} → OUTPUT/{fileName} blocked (no write access). " +
                    $"{coName} can write this file.");
                return $"*(🔒 PDF write blocked — {senderName} needs {coName} to write OUTPUT/{fileName})*";
            }

            var relPath = SysIO.Path.Combine("OUTPUT", fileName);
            var full    = SysIO.Path.GetFullPath(SysIO.Path.Combine(projFolder, relPath));
            if (!ProjectService.IsPathSafe(full, projFolder))
            {
                AddSystemMessage($"⚠  {senderName} → OUTPUT/{fileName} rejected (path escape).");
                return $"*(⚠ rejected: path not allowed)*";
            }

            SysIO.Directory.CreateDirectory(SysIO.Path.GetDirectoryName(full)!);

            if (Services.PdfFileWriter.TryWrite(full, m.Groups[2].Value, out var pdfErr))
                AddSystemMessage($"📄  {senderName} → OUTPUT/{fileName}  (PDF)");
            else
                AddSystemMessage($"⚠  {senderName} → OUTPUT/{fileName} failed: {pdfErr}");

            return $"*(→ OUTPUT/{fileName})*";
        });

        // ── Write Office / ODF output ──────────────────────────────────────
        // Syntax: <outputoffice file="report.docx">...markdown...</outputoffice>
        // Supported extensions: .docx .odt .xlsx .ods
        response = new Regex(
            @"<outputoffice\s+file=""([^""]+)"">\s*([\s\S]*?)\s*</outputoffice>",
            RegexOptions.IgnoreCase).Replace(response, m =>
        {
            var rawName = m.Groups[1].Value;
            if (!Services.OfficeFileService.CanWrite(rawName))
            {
                AddSystemMessage($"⚠  {senderName} → unsupported Office format '{rawName}'. " +
                                 "Use .docx, .odt, .xlsx, or .ods.");
                return $"*(⚠ unsupported format: {rawName})*";
            }

            var fileName = SanitizeFileName(rawName, "output.docx");
            if (!hasWriteAccess)
            {
                AddSystemMessage(
                    $"🔒  {senderName} → OUTPUT/{fileName} blocked (no write access). " +
                    $"{coName} can write this file.");
                return $"*(🔒 Office write blocked — {senderName} needs {coName} to write OUTPUT/{fileName})*";
            }

            var relPath = SysIO.Path.Combine("OUTPUT", fileName);
            var full    = SysIO.Path.GetFullPath(SysIO.Path.Combine(projFolder, relPath));
            if (!ProjectService.IsPathSafe(full, projFolder))
            {
                AddSystemMessage($"⚠  {senderName} → OUTPUT/{fileName} rejected (path escape).");
                return $"*(⚠ rejected: path not allowed)*";
            }

            SysIO.Directory.CreateDirectory(SysIO.Path.GetDirectoryName(full)!);

            var ext = SysIO.Path.GetExtension(fileName).ToLowerInvariant();
            var label = ext switch
            {
                ".docx" => "Word document",
                ".odt"  => "LibreOffice Writer document",
                ".xlsx" => "Excel spreadsheet",
                ".ods"  => "LibreOffice Calc spreadsheet",
                _       => "Office file"
            };

            if (Services.OfficeFileService.TryWrite(full, m.Groups[2].Value, out var offErr))
                AddSystemMessage($"📄  {senderName} → OUTPUT/{fileName}  ({label})");
            else
                AddSystemMessage($"⚠  {senderName} → OUTPUT/{fileName} failed: {offErr}");

            return $"*(→ OUTPUT/{fileName})*";
        });

        // ── List folder contents ───────────────────────────────────────────
        response = new Regex(
            @"<listfiles\s+folder=""([^""]+)""\s*/>",
            RegexOptions.IgnoreCase).Replace(response, m =>
        {
            var folder    = m.Groups[1].Value.Trim();
            var allowed   = new[] { "INPUT", "PROJECTPLAN", "OUTPUT", "AI-Characters" };
            var canonical = allowed.FirstOrDefault(f =>
                string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            if (canonical is null)
            {
                AddSystemMessage($"⚠  {senderName} listed unknown folder '{folder}' - ignored.");
                return $"*(⚠ unknown folder: {folder})*";
            }
            var absFolder = SysIO.Path.Combine(projFolder, canonical);
            var files     = SysIO.Directory.Exists(absFolder)
                ? SysIO.Directory.GetFiles(absFolder)
                    .Select(SysIO.Path.GetFileName)
                    .OrderBy(f => f)
                    .ToList()
                : [];
            var listing = files.Count > 0
                ? string.Join("\n", files.Select(f => $"  {f}"))
                : "  (empty)";
            var summary = $"{canonical}/ ({files.Count} file{(files.Count == 1 ? "" : "s")}):\n{listing}";
            AddSystemMessage($"📁  {senderName} listed {canonical}/");
            _sharedHistory.Add(new CloudAIMessage("user",
                $"[Directory listing: {canonical}/]\n\n{summary}", "System"));
            hadReadOps = true;
            return $"*(→ listed {canonical}/)*";
        });

        // ── Delete file (OUTPUT and PROJECTPLAN only) ──────────────────────
        response = new Regex(
            @"<deletefile\s+path=""([^""]+)""\s*/>",
            RegexOptions.IgnoreCase).Replace(response, m =>
        {
            var path           = m.Groups[1].Value.Trim();
            var allowedFolders = new[] { "OUTPUT", "PROJECTPLAN" };
            bool inAllowed     = allowedFolders.Any(f =>
                path.StartsWith(f + "/",  StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase));
            if (!inAllowed)
            {
                AddSystemMessage($"⚠  {senderName} tried to delete '{path}' - restricted to OUTPUT and PROJECTPLAN.");
                return $"*(⚠ delete not allowed: {path})*";
            }
            var full = SysIO.Path.GetFullPath(SysIO.Path.Combine(projFolder, path));
            if (!ProjectService.IsPathSafe(full, projFolder))
            {
                AddSystemMessage($"⚠  {senderName} delete rejected (path escape): {path}");
                return $"*(⚠ delete rejected: {path})*";
            }
            if (!SysIO.File.Exists(full))
            {
                AddSystemMessage($"⚠  {senderName} tried to delete '{path}' - not found.");
                return $"*(⚠ not found: {path})*";
            }
            SysIO.File.Delete(full);
            AddSystemMessage($"🗑  {senderName} deleted: {path}");
            return $"*(→ deleted: {path})*";
        });

        return (response, hadReadOps);
    }

    /// <summary>Strips invalid filename characters and trims separators. Returns fallback if empty.</summary>
    private static string SanitizeFileName(string raw, string fallback)
    {
        var safe = string.Join("_", raw.Trim()
            .Split(SysIO.Path.GetInvalidFileNameChars()))
            .Trim('_', '.');
        return string.IsNullOrEmpty(safe) ? fallback : safe;
    }

    /// <summary>
    /// Cleans up internal configuration files from OUTPUT folder if they exist.
    /// Returns a summary of what was removed. Call this on a project folder to
    /// remove any stray ProjectSettings files that shouldn't be there.
    /// </summary>
    public static (int FilesRemoved, List<string> RemovedPaths) CleanupOutputFolder(string projFolder)
    {
        var outputFolder = SysIO.Path.Combine(projFolder, "OUTPUT");
        var removed = new List<string>();
        var count = 0;

        if (!SysIO.Directory.Exists(outputFolder))
            return (0, removed);

        var forbiddenPatterns = new[]
        {
            "projectsettings",
            "project.json",
            "chatlog"
        };

        try
        {
            // Remove config files
            foreach (var file in SysIO.Directory.GetFiles(outputFolder))
            {
                var fileName = SysIO.Path.GetFileName(file).ToLowerInvariant();
                if (forbiddenPatterns.Any(p => fileName.Contains(p)))
                {
                    var relPath = SysIO.Path.GetRelativePath(projFolder, file);
                    SysIO.File.Delete(file);
                    removed.Add(relPath);
                    count++;
                }
            }

            // Remove _versions folder if it exists
            var versionsFolder = SysIO.Path.Combine(outputFolder, "_versions");
            if (SysIO.Directory.Exists(versionsFolder))
            {
                var relPath = SysIO.Path.GetRelativePath(projFolder, versionsFolder);
                SysIO.Directory.Delete(versionsFolder, recursive: true);
                removed.Add(relPath);
                count++;
            }
        }
        catch { /* silent if cleanup fails */ }

        return (count, removed);
    }

    // ── History compression ────────────────────────────────────────────────

    private const int HistoryCompressThreshold = 50;  // messages before compression runs
    private const int HistoryKeepRecent        = 16;  // most-recent messages kept verbatim
    private const int MaxToolLoopDepth         = 5;   // max auto-iterations per readfile/listfiles loop

    /// <summary>Returns the first active coordinator, preferring Cloud AI over Ollama
    /// (cloud models usually have larger context windows for summarisation).</summary>
    private (OllamaParticipantUI? Ollama, CloudAIParticipantUI? Cloud) FindActiveCoordinator()
    {
        if (_projectSettings is null) return (null, null);

        // Cloud first
        foreach (var ui in _cloudAIParticipants)
        {
            if (!ui.Data.Enabled || ui.Data.IsOnline != true) continue;
            var role = GetRoleForParticipant(ui);
            if (role?.IsCoordinator == true && role.IsActive != false)
                return (null, ui);
        }
        // Ollama fallback
        foreach (var ui in _ollamaParticipants)
        {
            if (!ui.Data.Enabled || ui.Data.IsOnline != true) continue;
            var role = GetRoleForParticipant(ui);
            if (role?.IsCoordinator == true && role.IsActive != false)
                return (ui, null);
        }
        return (null, null);
    }

    /// <summary>
    /// Compresses shared history via the coordinator when it exceeds the threshold.
    /// The coordinator summarises the older messages; the summary replaces them and
    /// is saved to PROJECTPLAN/history-summary-TIMESTAMP.md.
    /// No-ops when no project is open, no coordinator is available, or below threshold.
    /// </summary>
    private async Task MaybeCompressHistoryAsync(CancellationToken ct)
    {
        if (_currentProjectFolder is null) return;
        if (_sharedHistory.Count <= HistoryCompressThreshold) return;

        var (coordOllama, coordCloud) = FindActiveCoordinator();
        if (coordOllama is null && coordCloud is null)
        {
            // No coordinator - still trim to avoid runaway growth, but don't summarise
            if (_sharedHistory.Count > HistoryCompressThreshold * 2)
            {
                _sharedHistory.RemoveRange(0, _sharedHistory.Count - HistoryKeepRecent);
                AddSystemMessage("📋  History trimmed (no coordinator available to summarise).");
            }
            return;
        }

        // Build a compression request from the older messages
        var toCompress = _sharedHistory[..^HistoryKeepRecent];
        var recent     = _sharedHistory[^HistoryKeepRecent..].ToList();

        var histText = string.Join("\n\n", toCompress.Select(m =>
            $"[{m.Role.ToUpper()}{(m.Role == "assistant" ? $" - {m.Sender}" : "")}]\n{m.Content}"));

        var prompt =
            $"The shared conversation history has grown large and needs to be compressed. " +
            $"Please write a comprehensive but concise summary of the following " +
            $"{toCompress.Count} messages so they can be replaced with your summary. " +
            $"Cover: key topics discussed, decisions made, tasks assigned or completed, " +
            $"open questions, and any important context or facts established.\n\n" +
            $"--- MESSAGES TO SUMMARISE ---\n{histText}\n--- END ---";

        AddSystemMessage("📋  History reaching limit - coordinator is compressing…");

        try
        {
            string summary;

            if (coordCloud is not null)
            {
                var tempHistory = new List<CloudAIMessage> { new("user", prompt, "System") };
                var sb = new StringBuilder();
                await foreach (var tok in coordCloud.Data.Service.StreamAsync(tempHistory, "", ct))
                    sb.Append(tok);
                summary = sb.ToString().Trim();
            }
            else // coordOllama
            {
                var tempHistory = new List<OllamaChatMessage> { new("user", prompt) };
                var sb = new StringBuilder();
                await foreach (var tok in coordOllama!.Data.Service.StreamAsync(tempHistory, ct))
                    sb.Append(tok);
                summary = sb.ToString().Trim();
            }

            if (string.IsNullOrWhiteSpace(summary)) return;

            // Replace history: system summary + recent messages
            _sharedHistory.Clear();
            _sharedHistory.Add(new CloudAIMessage("system",
                $"[CONVERSATION SUMMARY - earlier messages compressed]\n\n{summary}", "System"));
            _sharedHistory.AddRange(recent);

            // Save summary to PROJECTPLAN
            var stamp    = DateTime.Now.ToString("yyyy-MM-dd_HH-mm");
            var fileName = $"history-summary-{stamp}.md";
            var fileBody = $"# Conversation Summary\n*Compressed: {DateTime.Now:yyyy-MM-dd HH:mm}*\n\n{summary}";
            ProjectService.SafeWriteFile(_currentProjectFolder,
                SysIO.Path.Combine("PROJECTPLAN", fileName), fileBody);

            AddSystemMessage($"📋  History compressed - summary saved to PROJECTPLAN/{fileName}");
        }
        catch (OperationCanceledException) { /* stream cancelled - leave history as-is */ }
        catch (Exception ex)
        {
            AddSystemMessage($"⚠  History compression failed: {ex.Message}");
        }
    }

    /// <summary>Creates a chat bubble. For AI responses the bubble starts with a thinking
    /// animation that is hidden once StopThinking() is called. The TextBox inside supports
    /// text selection; a Copy button appears on hover.</summary>
    private StreamBubble AddStreamingBubble(string senderName, string avatarText, string accentKey,
                                             string bubbleKey, bool isUser)
    {
        // Defensive fallbacks - an empty key causes SetResourceReference to find nothing,
        // which falls back to SystemColors.ControlTextBrush (black) - invisible in dark themes.
        if (string.IsNullOrEmpty(bubbleKey))
            bubbleKey = isUser ? "TertiaryBubbleBrush" : "PrimaryBubbleBrush";
        if (string.IsNullOrEmpty(accentKey))
            accentKey = isUser ? "TertiaryAccentBrush" : "PrimaryAccentBrush";

        // Derive per-surface text keys from bubbleKey:
        //   "PrimaryBubbleBrush" → prefix "Primary" → PrimaryTextBrush / PrimaryDimBrush / PrimaryHighBrush / PrimaryBubbleBorderBrush
        var bubblePrefix = bubbleKey.Replace("BubbleBrush", "");   // "Primary" | "Secondary" | "Tertiary"
        var bubbleTextKey   = bubblePrefix + "TextBrush";
        var bubbleDimKey    = bubblePrefix + "DimBrush";
        var bubbleBorderKey = bubblePrefix + "BubbleBorderBrush";
        // ── Avatar ────────────────────────────────────────────────────────
        var avatarInner = new TextBlock
        {
            Text                = avatarText,
            FontSize            = avatarText.Length > 1 ? 11 : 14,
            FontWeight          = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center
        };
        avatarInner.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");

        var avatar = new Border
        {
            Width             = 34, Height = 34,
            CornerRadius      = new CornerRadius(9),
            VerticalAlignment = VerticalAlignment.Top,
            Margin            = isUser ? new Thickness(10, 0, 0, 0) : new Thickness(0, 0, 10, 0),
            Child             = avatarInner
        };
        avatar.SetResourceReference(Border.BackgroundProperty, accentKey);

        // ── Selectable text content ───────────────────────────────────────
        var contentTb = new TextBox
        {
            TextWrapping    = TextWrapping.Wrap,
            IsReadOnly      = true,
            BorderThickness = new Thickness(0),
            Background      = Brushes.Transparent,
            Padding         = new Thickness(0),
            Visibility      = isUser ? Visibility.Visible : Visibility.Collapsed
        };
        contentTb.SetResourceReference(TextBox.FontFamilyProperty,    "ChatFontFamily");
        contentTb.SetResourceReference(TextBox.FontSizeProperty,      "ChatFontSize");
        contentTb.SetResourceReference(TextBox.ForegroundProperty,    bubbleTextKey);
        contentTb.SetResourceReference(TextBox.CaretBrushProperty,    bubbleTextKey);
        contentTb.SetResourceReference(TextBox.SelectionBrushProperty, accentKey);

        // ── Emote-formatted overlay (shown instead of contentTb when emotes are present) ──
        // contentTb (TextBox) is always kept in sync for copy-button access; EmoteContent
        // is swapped in by ApplyEmoteFormatting when *…* or /me markers are detected.
        var emoteContentTb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Visibility   = Visibility.Collapsed
        };
        emoteContentTb.SetResourceReference(TextBlock.FontFamilyProperty, "ChatFontFamily");
        emoteContentTb.SetResourceReference(TextBlock.FontSizeProperty,   "ChatFontSize");
        emoteContentTb.SetResourceReference(TextBlock.ForegroundProperty, bubbleTextKey);

        // ── Thinking animation (AI only) ──────────────────────────────────
        int frame = 0;
        string[] frames = ["·", "· ·", "· · ·"];
        var thinkingTb = new TextBlock
        {
            Text      = frames[0],
            FontSize  = 18,
            Margin    = new Thickness(0, 2, 0, 4),
            Visibility = isUser ? Visibility.Collapsed : Visibility.Visible
        };
        thinkingTb.SetResourceReference(TextBlock.ForegroundProperty, bubbleDimKey);

        var thinkingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        thinkingTimer.Tick += (_, _) =>
        {
            frame = (frame + 1) % frames.Length;
            thinkingTb.Text = frames[frame];
        };
        if (!isUser) thinkingTimer.Start();

        // Grid holds all three; only one is visible at a time
        var bubbleInner = new Grid();
        bubbleInner.Children.Add(thinkingTb);
        bubbleInner.Children.Add(contentTb);
        bubbleInner.Children.Add(emoteContentTb);

        var bubble = new Border
        {
            CornerRadius    = isUser ? new CornerRadius(12, 3, 12, 12) : new CornerRadius(3, 12, 12, 12),
            Padding         = new Thickness(13, 9, 13, 9),
            BorderThickness = new Thickness(1),
            Child           = bubbleInner
        };
        bubble.SetResourceReference(Border.BackgroundProperty,   bubbleKey);
        bubble.SetResourceReference(Border.BorderBrushProperty,  bubbleBorderKey);

        // ── Copy button (appears on hover) ────────────────────────────────
        var copyBtn = new Button
        {
            Content             = "⎘",
            Width               = 28, Height = 22,
            FontSize            = 12,
            BorderThickness     = new Thickness(0),
            Padding             = new Thickness(0),
            Cursor              = Cursors.Hand,
            Visibility          = Visibility.Collapsed,
            HorizontalAlignment = isUser ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Top,
            ToolTip             = "Copy message",
            Style               = (Style)FindResource("ModernButton")
        };
        copyBtn.SetResourceReference(Button.BackgroundProperty, "ControlHoverBrush");
        copyBtn.SetResourceReference(Button.ForegroundProperty, "ContentDimBrush");

        copyBtn.Click += async (_, _) =>
        {
            if (!string.IsNullOrEmpty(contentTb.Text))
                Clipboard.SetText(contentTb.Text);
            copyBtn.Content = "✓";
            await Task.Delay(1500);
            if (copyBtn.IsLoaded) copyBtn.Content = "⎘";
        };

        // Bubble + copy button overlaid in same Grid cell
        var bubbleWrapper = new Grid();
        bubbleWrapper.Children.Add(bubble);
        bubbleWrapper.Children.Add(copyBtn);

        // ── Labels ────────────────────────────────────────────────────────
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
        timeLabel.SetResourceReference(TextBlock.ForegroundProperty, bubbleDimKey);

        // ── Content column ─────────────────────────────────────────────────
        // MaxWidth is driven by ChatBubbleMaxWidth dynamic resource (% of chat panel width).
        // Updating the resource updates all existing bubbles automatically via WPF binding.
        var content = new StackPanel
        {
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Tag = "BubbleContent"   // used by UpdateChatBubbleWidth for direct Width refresh
        };
        // Width (not MaxWidth) so the bubble always fills exactly slider-% of the chat area.
        // Short messages no longer cap at their natural text width.
        content.SetResourceReference(FrameworkElement.WidthProperty, "ChatBubbleMaxWidth");
        content.Children.Add(nameLabel);
        content.Children.Add(bubbleWrapper);
        content.Children.Add(timeLabel);

        // Show/hide copy button on hover of the whole content column
        content.MouseEnter += (_, _) => copyBtn.Visibility = Visibility.Visible;
        content.MouseLeave += (_, _) => copyBtn.Visibility = Visibility.Collapsed;

        // ── 2-column Grid row ─────────────────────────────────────────────
        // Using a Grid instead of a horizontal StackPanel is the key fix:
        // a StackPanel measures children with infinite width so TextWrapping.Wrap
        // never fires; a Grid gives each column a finite measured width, which
        // propagates into the TextBox and triggers wrapping at every window size.
        //
        // Layout (AI):   [Auto: avatar 44 px] [1*: bubble content - HAlign Left]
        // Layout (User): [1*: bubble content - HAlign Right]  [Auto: avatar 44 px]
        //
        // The content StackPanel's MaxWidth (driven by ChatBubbleMaxWidth resource)
        // caps how wide the bubble can grow. HorizontalAlignment (Left / Right) keeps
        // it glued to the avatar side; unused space appears on the opposite side.
        // Slider 30 % → narrow bubble. Slider 100 % → fills the full content column.
        var wrapper = new Grid { Margin = new Thickness(0, 5, 0, 5) };

        if (isUser)
        {
            wrapper.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            wrapper.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(content, 0);
            Grid.SetColumn(avatar,  1);
        }
        else
        {
            wrapper.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            wrapper.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(avatar,  0);
            Grid.SetColumn(content, 1);
        }

        wrapper.Children.Add(avatar);
        wrapper.Children.Add(content);
        ChatPanel.Children.Add(wrapper);

        // ── Return handle ──────────────────────────────────────────────────
        void StopThinking()
        {
            thinkingTimer.Stop();
            thinkingTb.ToolTip    = null;          // clear thinking tooltip
            thinkingTb.Visibility = Visibility.Collapsed;
            contentTb.Visibility  = Visibility.Visible;
        }

        // Tooltip lives on the thinking-dots element: visible only while dots are shown.
        // After StopThinking the element is Collapsed so the tooltip can never appear.
        void UpdateThinkingTooltip(string tip)
        {
            thinkingTb.ToolTip = string.IsNullOrEmpty(tip) ? null : (object)$"💭 {tip}";
        }

        return new StreamBubble(contentTb, emoteContentTb, StopThinking, UpdateThinkingTooltip, wrapper);
    }
}
