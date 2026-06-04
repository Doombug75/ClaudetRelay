using System.IO;
using System.Windows.Media.Imaging;

namespace ClaudetRelay.Services;

/// <summary>
/// Generates and caches small thumbnail images alongside originals in a "_thumbs" subfolder.
/// Thumbnails are loaded into memory instead of full-resolution images, saving significant RAM.
/// </summary>
public static class ThumbnailService
{
    /// <summary>Width of generated thumbnails in pixels.</summary>
    public const int ThumbPx = 200;

    private const string ThumbSubDir = "_thumbs";

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the path to the thumbnail for <paramref name="fullImagePath"/>.
    /// Generates it if it doesn't exist yet.  Returns null if the source doesn't exist.
    /// </summary>
    public static string? EnsureThumb(string? fullImagePath)
    {
        if (string.IsNullOrWhiteSpace(fullImagePath) || !File.Exists(fullImagePath))
            return null;

        var thumbPath = GetThumbPath(fullImagePath);
        if (File.Exists(thumbPath)) return thumbPath;

        try
        {
            GenerateThumb(fullImagePath, thumbPath);
            return thumbPath;
        }
        catch
        {
            // Fall back to the original path – caller will use DecodePixelWidth for memory safety
            return fullImagePath;
        }
    }

    /// <summary>Returns the expected thumbnail path for an original image path (may not exist).</summary>
    public static string GetThumbPath(string fullImagePath)
    {
        var dir  = Path.GetDirectoryName(fullImagePath)!;
        var name = Path.GetFileName(fullImagePath);
        return Path.Combine(dir, ThumbSubDir, name);
    }

    /// <summary>
    /// Deletes the thumbnail for <paramref name="fullImagePath"/> if it exists.
    /// Call this when the original is deleted or replaced.
    /// </summary>
    public static void DeleteThumb(string? fullImagePath)
    {
        if (string.IsNullOrWhiteSpace(fullImagePath)) return;
        var thumb = GetThumbPath(fullImagePath);
        if (File.Exists(thumb)) { try { File.Delete(thumb); } catch { } }
    }

    /// <summary>
    /// Loads a <see cref="BitmapImage"/> memory-efficiently.
    /// Prefers the thumbnail if it exists; otherwise loads the original with
    /// <see cref="BitmapImage.DecodePixelWidth"/> set to <see cref="ThumbPx"/>.
    /// </summary>
    public static BitmapImage? LoadThumb(string? fullImagePath, int decodeWidth = ThumbPx)
    {
        if (string.IsNullOrWhiteSpace(fullImagePath) || !File.Exists(fullImagePath))
            return null;

        var thumbPath = GetThumbPath(fullImagePath);
        var loadPath  = File.Exists(thumbPath) ? thumbPath : fullImagePath;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource        = new Uri(loadPath);
            bmp.DecodePixelWidth = decodeWidth * 2;   // 2× for HiDPI crispness
            bmp.CacheOption      = BitmapCacheOption.OnLoad;
            bmp.CreateOptions    = BitmapCreateOptions.IgnoreImageCache;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    // ── Private ────────────────────────────────────────────────────────────

    private static void GenerateThumb(string sourcePath, string destPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource        = new Uri(sourcePath);
        bmp.DecodePixelWidth = ThumbPx * 2;
        bmp.CacheOption      = BitmapCacheOption.OnLoad;
        bmp.CreateOptions    = BitmapCreateOptions.IgnoreImageCache;
        bmp.EndInit();
        bmp.Freeze();

        BitmapEncoder enc = Path.GetExtension(destPath).ToLowerInvariant() switch
        {
            ".png"  => new PngBitmapEncoder(),
            ".bmp"  => new BmpBitmapEncoder(),
            _       => new JpegBitmapEncoder { QualityLevel = 88 }
        };
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var stream = File.Create(destPath);
        enc.Save(stream);
    }
}
