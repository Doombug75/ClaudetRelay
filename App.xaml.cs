using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ClaudetRelay.Services;

namespace ClaudetRelay;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // ── Apply UI language from settings (must be first — before any UI is created) ──
        var langCode = SettingsService.Load().Language;
        if (!string.IsNullOrWhiteSpace(langCode))
        {
            try
            {
                var culture = new CultureInfo(langCode);
                Thread.CurrentThread.CurrentCulture   = culture;
                Thread.CurrentThread.CurrentUICulture = culture;
                CultureInfo.DefaultThreadCurrentCulture   = culture;
                CultureInfo.DefaultThreadCurrentUICulture = culture;
            }
            catch (CultureNotFoundException) { /* unknown code — silently stay English */ }
        }

        // ── Dependency checks — run BEFORE any theme resources are loaded ──
        // All dialogs here use only SystemColors / system fonts so they look
        // native regardless of which (if any) theme the user has selected.

        // Require Windows 10 build 17763 (October 2018 Update) or later.
        // Earlier builds lack DWM title-bar colour APIs and several modern controls.
        if (!CheckWindowsVersion(out var osVersion))
        {
            ShowDependencyError(
                title:       "Windows 10 required",
                detail:      $"ClaudetRelay requires Windows 10 (build 17763 — October 2018 Update) " +
                             $"or later.\n\nDetected: {osVersion}",
                downloadUrl: "https://www.microsoft.com/windows/get-windows-10");
            Shutdown(-1);
            return;
        }

        // Require .NET 10 Desktop Runtime (WPF).
        // If the runtime is missing entirely the native apphost will catch it
        // before this code runs; this check catches roll-forward onto a mismatched
        // major version or a pure CLI/ASP.NET runtime without the Desktop workload.
        if (!CheckDotNetRuntime(out var runtimeDesc))
        {
            ShowDependencyError(
                title:       ".NET 10 Desktop Runtime required",
                detail:      $"ClaudetRelay requires the .NET 10 Desktop Runtime (WPF).\n\n" +
                             $"Detected runtime: {runtimeDesc}\n\n" +
                             "Please install the correct runtime and restart the application.",
                downloadUrl: "https://dotnet.microsoft.com/download/dotnet/10.0");
            Shutdown(-1);
            return;
        }

        base.OnStartup(e);
    }

    // ── Checks ─────────────────────────────────────────────────────────────

    private static bool CheckWindowsVersion(out string description)
    {
        var v = Environment.OSVersion;
        description = $"Windows {v.Version.Major}.{v.Version.Minor} (build {v.Version.Build})";

        // Windows 10 October 2018 Update = build 17763
        return v.Platform == PlatformID.Win32NT
            && (v.Version.Major > 10
                || (v.Version.Major == 10 && v.Version.Build >= 17763));
    }

    private static bool CheckDotNetRuntime(out string description)
    {
        description = RuntimeInformation.FrameworkDescription; // e.g. ".NET 10.0.0"
        return Environment.Version.Major >= 10;
    }

    // ── Error dialog ───────────────────────────────────────────────────────

    /// <summary>
    /// Shows a dependency-error dialog that uses only system colours and the
    /// system UI font — never any of the app's custom theme resources.
    /// </summary>
    private static void ShowDependencyError(string title, string detail, string? downloadUrl)
    {
        var win = new Window
        {
            Title                 = $"ClaudetRelay — {title}",
            WindowStyle           = WindowStyle.ToolWindow,
            ResizeMode            = ResizeMode.NoResize,
            Width                 = 500,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background            = SystemColors.WindowBrush,
            ShowInTaskbar         = true,
            Topmost               = true
        };

        var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 20) };

        // ── Icon + heading row ────────────────────────────────────────────
        var headingRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 0, 0, 10)
        };

        var icon = new TextBlock
        {
            Text              = "⚠",
            FontSize          = 28,
            Foreground        = Brushes.DarkOrange,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 12, 0)
        };

        var headingText = new TextBlock
        {
            Text                = title,
            FontFamily          = new FontFamily("Segoe UI"),
            FontSize            = 15,
            FontWeight          = FontWeights.SemiBold,
            Foreground          = SystemColors.WindowTextBrush,
            TextWrapping        = TextWrapping.Wrap,
            VerticalAlignment   = VerticalAlignment.Center
        };

        headingRow.Children.Add(icon);
        headingRow.Children.Add(headingText);
        panel.Children.Add(headingRow);

        // ── Detail text ───────────────────────────────────────────────────
        var detailTb = new TextBlock
        {
            Text         = detail,
            FontFamily   = new FontFamily("Segoe UI"),
            FontSize     = 12,
            Foreground   = SystemColors.WindowTextBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, downloadUrl is null ? 20 : 12)
        };
        panel.Children.Add(detailTb);

        // ── Download link (if provided) ───────────────────────────────────
        if (downloadUrl is not null)
        {
            var linkTb = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize   = 12,
                Margin     = new Thickness(0, 0, 0, 20)
            };
            var link = new Hyperlink { NavigateUri = new Uri(downloadUrl) };
            link.RequestNavigate += (_, args) =>
                Process.Start(new ProcessStartInfo(args.Uri.ToString()) { UseShellExecute = true });
            link.Inlines.Add(downloadUrl);
            linkTb.Inlines.Add("Download: ");
            linkTb.Inlines.Add(link);
            panel.Children.Add(linkTb);
        }

        // ── Close button ──────────────────────────────────────────────────
        var closeBtn = new Button
        {
            Content             = "Close",
            Width               = 88,
            Height              = 30,
            FontFamily          = new FontFamily("Segoe UI"),
            FontSize            = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault           = true,
            IsCancel            = true
        };
        closeBtn.Click += (_, _) => win.Close();
        panel.Children.Add(closeBtn);

        win.Content = panel;
        win.ShowDialog();
    }
}
