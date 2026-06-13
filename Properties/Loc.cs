using System.Globalization;
using System.IO;
using System.Resources;
using System.Text.Json;

namespace ClaudetRelay.Properties;

/// <summary>
/// Centralized UI string localization.
/// Call <c>Loc.S("Key")</c> anywhere in the app to get the current-culture translation.
/// Falls back to the key name itself if a string is missing — never throws.
/// </summary>
/// <remarks>
/// Strings live in <c>Properties/Strings.resx</c> (English, neutral fallback)
/// and <c>Properties/Strings.de.resx</c> (German).
/// External JSON packs in <c>Languages/&lt;code&gt;.json</c> next to the exe are loaded
/// on top and override individual keys — or supply a full translation for any locale.
/// The active culture is set once at startup from <c>AppSettings.Language</c>
/// — a restart is required for the change to take effect.
/// </remarks>
internal static class Loc
{
    private static readonly ResourceManager _rm =
        new("ClaudetRelay.Properties.Strings", typeof(Loc).Assembly);

    // Overlay loaded from Languages/<code>.json — may be empty if no pack exists.
    private static readonly Dictionary<string, string> _jsonOverlay = LoadJsonOverlay();

    // When a JSON pack is active, .resx fallback uses neutral English so missing
    // keys don't bleed through in whatever the OS language happens to be.
    private static readonly CultureInfo _resFallback =
        _jsonOverlay.Count > 0 ? CultureInfo.InvariantCulture : CultureInfo.CurrentUICulture;

    /// <summary>
    /// Returns the localized string for <paramref name="key"/> in the current UI culture.
    /// JSON overlay is checked first; falls back to .resx, then to the key itself.
    /// </summary>
    public static string S(string key)
    {
        if (_jsonOverlay.TryGetValue(key, out var v)) return v;
        return _rm.GetString(key, _resFallback) ?? key;
    }

    // ── JSON pack loading ──────────────────────────────────────────────────

    private static Dictionary<string, string> LoadJsonOverlay()
    {
        var overlay = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            // Use the saved language code directly — handles custom codes like "martian"
            // that aren't valid CultureInfo names.
            var savedCode = Services.SettingsService.Load().Language;
            if (!string.IsNullOrEmpty(savedCode) && TryLoadPack(savedCode, out var dict))
                return dict;

            // Fall back to the running culture's two-letter code
            var code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
            if (TryLoadPack(code, out dict))
                return dict;

            // Also try full culture name (e.g. "zh-hans")
            var full = CultureInfo.CurrentUICulture.Name.ToLowerInvariant();
            if (full != code && TryLoadPack(full, out dict))
                return dict;
        }
        catch { }
        return overlay;
    }

    private static bool TryLoadPack(string code, out Dictionary<string, string> result)
    {
        result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(code)) return false;
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Languages", code + ".json");
        if (!File.Exists(path)) return false;
        try
        {
            var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // Skip the metadata block
                if (prop.Name == "_language_info") continue;
                if (prop.Value.ValueKind == JsonValueKind.String)
                    result[prop.Name] = prop.Value.GetString()!;
            }
            return result.Count > 0;
        }
        catch { return false; }
    }

    // ── Helpers for the language menu ──────────────────────────────────────

    /// <summary>
    /// Scans the <c>Languages/</c> folder and returns (code, displayName) pairs
    /// for every valid JSON pack found, sorted by display name.
    /// The display name comes from <c>_language_info.language_name</c> if present,
    /// otherwise the filename stem is used.
    /// </summary>
    public static IReadOnlyList<(string Code, string DisplayName)> GetExternalLanguagePacks()
    {
        var result = new List<(string, string)>();
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Languages");
        if (!Directory.Exists(dir)) return result;
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var code = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                if (code == "en") continue; // English is always shown as the built-in default
                var json = File.ReadAllText(file, System.Text.Encoding.UTF8);
                using var doc = JsonDocument.Parse(json);
                string displayName = code;
                if (doc.RootElement.TryGetProperty("_language_info", out var info) &&
                    info.TryGetProperty("language_name", out var nameProp) &&
                    nameProp.ValueKind == JsonValueKind.String)
                {
                    displayName = nameProp.GetString()!;
                }
                result.Add((code, displayName));
            }
            catch { }
        }
        result.Sort((a, b) => string.Compare(a.Item2, b.Item2, StringComparison.CurrentCulture));
        return result;
    }
}
