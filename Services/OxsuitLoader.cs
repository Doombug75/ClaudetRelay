namespace ClaudetRelay.Services;

using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

/// <summary>
/// Loads a native OXSUIT 1.0 theme file (.oxsuit) into a WPF <see cref="ResourceDictionary"/>.
/// Parses the <c>&lt;oxsuit&gt;</c> XML format, maps short key names to WPF resource keys,
/// and converts <b>#RRGGBBAA</b> colour values to WPF's <b>#AARRGGBB</b> convention.
/// </summary>
public static class OxsuitLoader
{
    // ── OXSUIT 1.0 short key → WPF resource key ──────────────────────────────

    private static readonly Dictionary<string, string> s_keyMap =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // Content surface
        ["ContentBg"]        = "ContentBgBrush",
        ["ContentBorder"]    = "ContentBorderBrush",
        ["ContentText"]      = "ContentTextBrush",
        ["ContentHigh"]      = "ContentHighBrush",
        ["ContentDim"]       = "ContentDimBrush",

        // Sidebar surface
        ["SidebarBg"]        = "SidebarBgBrush",
        ["SidebarBorder"]    = "SidebarBorderBrush",
        ["SidebarText"]      = "SidebarTextBrush",
        ["SidebarHigh"]      = "SidebarHighBrush",
        ["SidebarDim"]       = "SidebarDimBrush",

        // Control surface
        ["ControlBg"]        = "ControlBgBrush",
        ["ControlBorder"]    = "ControlBorderBrush",
        ["ControlText"]      = "ControlTextBrush",
        ["ControlHigh"]      = "ControlHighBrush",
        ["ControlDim"]       = "ControlDimBrush",
        ["ControlHover"]     = "ControlHoverBrush",

        // Input surface
        ["InputBg"]          = "InputBgBrush",
        ["InputBorder"]      = "InputBorderBrush",
        ["InputText"]        = "InputTextBrush",
        ["InputHigh"]        = "InputHighBrush",
        ["InputDim"]         = "InputDimBrush",

        // Accent
        ["AccentBg"]         = "AccentBgBrush",
        ["AccentText"]       = "AccentTextBrush",
        ["AccentHighlight"]  = "AccentHighlightBrush",
        ["PrimaryAccent"]    = "PrimaryAccentBrush",
        ["SecondaryAccent"]  = "SecondaryAccentBrush",
        ["TertiaryAccent"]   = "TertiaryAccentBrush",

        // Primary bubble slot
        // OXSUIT "Bg" maps to ClaudetRelay "Bubble" (historical naming difference)
        ["PrimaryBg"]        = "PrimaryBubbleBrush",
        ["PrimaryBorder"]    = "PrimaryBubbleBorderBrush",
        ["PrimaryText"]      = "PrimaryTextBrush",
        ["PrimaryHigh"]      = "PrimaryHighBrush",
        ["PrimaryDim"]       = "PrimaryDimBrush",

        // Secondary bubble slot
        ["SecondaryBg"]      = "SecondaryBubbleBrush",
        ["SecondaryBorder"]  = "SecondaryBubbleBorderBrush",
        ["SecondaryText"]    = "SecondaryTextBrush",
        ["SecondaryHigh"]    = "SecondaryHighBrush",
        ["SecondaryDim"]     = "SecondaryDimBrush",

        // Tertiary bubble slot
        ["TertiaryBg"]       = "TertiaryBubbleBrush",
        ["TertiaryBorder"]   = "TertiaryBubbleBorderBrush",
        ["TertiaryText"]     = "TertiaryTextBrush",
        ["TertiaryHigh"]     = "TertiaryHighBrush",
        ["TertiaryDim"]      = "TertiaryDimBrush",
    };

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads the .oxsuit file at <paramref name="path"/> into a
    /// <see cref="ResourceDictionary"/> ready for use by WPF.
    /// Returns <c>null</c> if the file does not exist, cannot be parsed,
    /// or contains no recognisable theme entries.
    /// </summary>
    public static ResourceDictionary? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return LoadNativeXml(File.ReadAllText(path, Encoding.UTF8));
        }
        catch { return null; }
    }

    /// <summary>
    /// Returns a friendly display name for an .oxsuit file.
    /// Reads the <c>name</c> attribute from the XML root if present;
    /// otherwise falls back to the filename without extension.
    /// </summary>
    public static string GetDisplayName(string path)
    {
        try
        {
            var doc      = XDocument.Load(path);
            var nameAttr = doc.Root?.Attribute("name")?.Value;
            if (!string.IsNullOrWhiteSpace(nameAttr)) return nameAttr;
        }
        catch { /* fall through */ }
        return Path.GetFileNameWithoutExtension(path);
    }

    // ── Private loader ────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a native OXSUIT XML v1.0 file and builds a ResourceDictionary.
    /// Colour format: #RRGGBB (opaque) or #RRGGBBAA (alpha <b>last</b> — opposite of WPF's #AARRGGBB).
    /// </summary>
    private static ResourceDictionary? LoadNativeXml(string xmlText)
    {
        var doc  = XDocument.Parse(xmlText);
        var root = doc.Root;
        if (root?.Name.LocalName != "oxsuit") return null;

        var result = new ResourceDictionary();

        var colors = root.Element("colors");
        if (colors is not null)
        {
            foreach (var el in colors.Elements("color"))
            {
                var oxKey = el.Attribute("key")?.Value;
                var value = el.Attribute("value")?.Value;
                if (string.IsNullOrWhiteSpace(oxKey) || string.IsNullOrWhiteSpace(value)) continue;

                var wpfKey = MapKey(oxKey);
                try
                {
                    var brush = new SolidColorBrush(ParseHexOxsuit(value));
                    brush.Freeze();
                    result[wpfKey] = brush;
                }
                catch { /* skip unparseable colour values */ }
            }
        }

        return result.Count > 0 ? ApplyFallbacks(result) : null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps an OXSUIT short key name to the WPF resource key name.
    /// Falls back to appending "Brush" for unrecognised keys.
    /// </summary>
    private static string MapKey(string oxKey) =>
        s_keyMap.TryGetValue(oxKey, out var wpf) ? wpf : oxKey + "Brush";

    /// <summary>
    /// Applies the same legacy-compatibility fallbacks used for .xaml themes so that
    /// OXSUIT files which omit some keys still render correctly in ClaudetRelay.
    /// </summary>
    private static ResourceDictionary ApplyFallbacks(ResourceDictionary dict)
    {
        if (!dict.Contains("ControlTextBrush") && dict.Contains("ContentTextBrush"))
            dict["ControlTextBrush"] = dict["ContentTextBrush"];
        if (!dict.Contains("SidebarTextBrush") && dict.Contains("ContentTextBrush"))
            dict["SidebarTextBrush"] = dict["ContentTextBrush"];
        if (!dict.Contains("InputBgBrush") && dict.Contains("ControlBgBrush"))
            dict["InputBgBrush"] = dict["ControlBgBrush"];
        return dict;
    }

    /// <summary>
    /// Parses a hex colour string in OXSUIT native format.
    /// <list type="bullet">
    ///   <item>#RGB</item>
    ///   <item>#RRGGBB</item>
    ///   <item>#RRGGBBAA — alpha byte is <b>last</b> (unlike WPF's #AARRGGBB)</item>
    /// </list>
    /// </summary>
    private static Color ParseHexOxsuit(string hex)
    {
        hex = hex.TrimStart('#');
        return hex.Length switch
        {
            3 => Color.FromRgb(
                     Convert.ToByte(new string(hex[0], 2), 16),
                     Convert.ToByte(new string(hex[1], 2), 16),
                     Convert.ToByte(new string(hex[2], 2), 16)),
            6 => Color.FromRgb(
                     Convert.ToByte(hex[..2], 16),
                     Convert.ToByte(hex[2..4], 16),
                     Convert.ToByte(hex[4..6], 16)),
            8 => Color.FromArgb(
                     Convert.ToByte(hex[6..8], 16),   // alpha is last in OXSUIT (#RRGGBBAA)
                     Convert.ToByte(hex[..2], 16),
                     Convert.ToByte(hex[2..4], 16),
                     Convert.ToByte(hex[4..6], 16)),
            _ => Colors.Magenta
        };
    }
}
