using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ClaudetRelay.Properties;
using ClaudetRelay.Services;

namespace ClaudetRelay;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Catch any unhandled exception — show a readable message instead of silent exit-0
        DispatcherUnhandledException += (_, args) =>
        {
            ShowCrashDialog(args.Exception);
            args.Handled = true;
            Shutdown(-1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                ShowCrashDialog(ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            // Background task failures are logged but don't kill the app
            System.Diagnostics.Debug.WriteLine($"[UnobservedTask] {args.Exception}");
        };

        // ── Apply UI language from settings (must be first — before any UI is created) ──
        //
        // Three-state contract (Language property in settings):
        //   null  — never configured: auto-detect from OS and save if supported
        //   ""    — user explicitly chose English: force "en" so de-DE OS doesn't bleed through
        //   "de"  — user explicitly chose German (or other code): force that culture
        //
        // Supported language codes (must match Strings.XX.resx files that exist):
        var supportedLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "de" };

        var settings = SettingsService.Load();
        if (settings.Language is null)
        {
            // First launch — auto-detect from OS
            var twoLetter = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
            if (supportedLanguages.Contains(twoLetter))
            {
                // OS language is supported — save it so menu reflects the correct choice
                settings.Language = twoLetter;
                SettingsService.Save(settings);
            }
            else
            {
                // OS language not supported — default to English, save "" explicitly
                settings.Language = "";
                SettingsService.Save(settings);
            }
        }

        var langCode = settings.Language;
        {
            var cultureName = langCode.Length == 0 ? "en" : langCode;   // "" → "en"
            try
            {
                var culture = new CultureInfo(cultureName);
                Thread.CurrentThread.CurrentCulture          = culture;
                Thread.CurrentThread.CurrentUICulture        = culture;
                CultureInfo.DefaultThreadCurrentCulture      = culture;
                CultureInfo.DefaultThreadCurrentUICulture    = culture;
            }
            catch (CultureNotFoundException) { /* unknown code — silently keep OS culture */ }
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

    // ── Error dialogs ──────────────────────────────────────────────────────

    private static void ShowCrashDialog(Exception ex)
    {
        try
        {
            var msg = $"{ex.GetType().Name}: {ex.Message}";
            if (ex.InnerException is not null)
                msg += $"\n\nInner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
            msg += $"\n\n{ex.StackTrace}";

            MessageBox.Show(msg, "ClaudetRelay — Unhandled Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // If even the MessageBox fails, at least write to debug output
            System.Diagnostics.Debug.WriteLine($"CRASH: {ex}");
        }
    }

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


    // ── Shared theme helper ────────────────────────────────────────────────

    /// <summary>
    /// Loads the user's saved OXSUIT theme into <paramref name="win"/> and
    /// applies matching DWM title-bar and caption colours.
    /// Safe to call when no theme is saved — falls back to system defaults silently.
    /// </summary>
    private static void ApplyThemeToWindow(Window win)
    {
        // ── Load theme resources ───────────────────────────────────────────
        var lastTheme = SettingsService.Load().LastTheme;
        if (!string.IsNullOrEmpty(lastTheme))
        {
            var themePath = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "Themes", lastTheme + ".oxsuit");
            try
            {
                var dict = OxsuitLoader.Load(themePath);
                if (dict is not null)
                    win.Resources.MergedDictionaries.Add(dict);
            }
            catch { /* no theme — window will use system defaults */ }
        }

        // ── Set window background from theme ───────────────────────────────
        if (win.TryFindResource("SidebarBgBrush") is Brush bg)
            win.Background = bg;

        // ── Apply DWM title-bar and caption colour (Win 10+/Win 11+) ──────
        win.SourceInitialized += (_, _) =>
        {
            try
            {
                if (win.TryFindResource("SidebarBgBrush")   is not SolidColorBrush bgBrush)   return;
                if (win.TryFindResource("SidebarTextBrush") is not SolidColorBrush textBrush) return;

                var hwnd   = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                var isDark = RelLuminance(bgBrush.Color) < 0.5 ? 1 : 0;
                var cr     = bgBrush.Color.R   | (bgBrush.Color.G   << 8) | (bgBrush.Color.B   << 16);
                var tcr    = textBrush.Color.R  | (textBrush.Color.G << 8) | (textBrush.Color.B << 16);
                DwmSetWindowAttribute(hwnd, 20, ref isDark, 4);  // dark mode (Win 10+)
                DwmSetWindowAttribute(hwnd, 35, ref cr,    4);   // caption colour (Win 11+)
                DwmSetWindowAttribute(hwnd, 36, ref tcr,   4);   // caption text (Win 11+)
            }
            catch { }
        };
    }

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int val, int sz);

    private static double RelLuminance(Color c)
    {
        static double L(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return 0.2126 * L(c.R / 255.0) + 0.7152 * L(c.G / 255.0) + 0.0722 * L(c.B / 255.0);
    }
}
