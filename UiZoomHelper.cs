using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClaudetRelay.Services;

namespace ClaudetRelay;

/// <summary>
/// Applies a uniform scale to any WPF Window by setting a ScaleTransform on its
/// root content element via LayoutTransform.  LayoutTransform participates in
/// WPF's measure/arrange pass, so SizeToContent dialogs resize automatically and
/// all text / vector graphics stay crisp at any scale.
///
/// Call <see cref="Apply"/> immediately after the window is constructed but before
/// it is shown.  For the main application window pass scaleWindow=false — the
/// window fills the screen and its ScrollViewers handle overflow at large zoom.
/// For dialogs with a fixed Width, the method scales that dimension so content is
/// never clipped; SizeToContent handles height automatically.
/// </summary>
internal static class UiZoomHelper
{
    /// <summary>
    /// Applies <paramref name="zoom"/> as a uniform LayoutTransform to the window's
    /// content.  A zoom of 1.0 is a no-op (Transform.Identity is used so there is
    /// no overhead).
    /// </summary>
    /// <param name="w">Target window.</param>
    /// <param name="zoom">Scale factor, e.g. 1.5 = 150 %. Clamped to [0.25, 4.0].</param>
    /// <param name="scaleWindow">
    /// When true, also multiplies the window's fixed Width / Height by zoom so content
    /// is never clipped.  Dimensions managed by SizeToContent are left alone — WPF
    /// will measure the scaled content and size that axis automatically.
    /// Pass false for the main window (it should stay at its current size).
    /// </param>
    public static void Apply(Window w, double zoom, bool scaleWindow = true)
    {
        zoom = Math.Clamp(zoom, 0.25, 4.0);

        if (w.Content is not FrameworkElement root) return;

        root.LayoutTransform = Math.Abs(zoom - 1.0) < 0.001
            ? Transform.Identity
            : new ScaleTransform(zoom, zoom);

        if (!scaleWindow) return;

        // Scale only the dimensions that are NOT auto-managed by SizeToContent.
        bool autoW = w.SizeToContent is SizeToContent.Width or SizeToContent.WidthAndHeight;
        bool autoH = w.SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight;

        if (!autoW && !double.IsNaN(w.Width)  && w.Width  > 0) w.Width  *= zoom;
        if (!autoH && !double.IsNaN(w.Height) && w.Height > 0) w.Height *= zoom;
    }

    /// <summary>Loads the current zoom from AppSettings and returns it as a factor (e.g. 1.25).</summary>
    public static double FromSettings() =>
        Math.Clamp(SettingsService.Load().UiZoom, 0.25, 4.0);

    /// <summary>Formats a zoom factor as a percentage string, e.g. 1.5 → "150%".</summary>
    public static string FormatLabel(double zoom) => $"{(int)Math.Round(zoom * 100)}%";
}
