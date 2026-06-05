using System.Globalization;
using System.Resources;

namespace ClaudetRelay.Properties;

/// <summary>
/// Centralized UI string localization.
/// Call <c>Loc.S("Key")</c> anywhere in the app to get the current-culture translation.
/// Falls back to the key name itself if a string is missing — never throws.
/// </summary>
/// <remarks>
/// Strings live in <c>Properties/Strings.resx</c> (English, neutral fallback)
/// and <c>Properties/Strings.de.resx</c> (German).
/// The active culture is set once at startup from <c>AppSettings.Language</c>
/// — a restart is required for the change to take effect.
/// </remarks>
internal static class Loc
{
    private static readonly ResourceManager _rm =
        new("ClaudetRelay.Properties.Strings", typeof(Loc).Assembly);

    /// <summary>
    /// Returns the localized string for <paramref name="key"/> in the current UI culture.
    /// Falls back to the English (neutral) resource, then to the key itself.
    /// </summary>
    public static string S(string key) =>
        _rm.GetString(key, CultureInfo.CurrentUICulture) ?? key;
}
