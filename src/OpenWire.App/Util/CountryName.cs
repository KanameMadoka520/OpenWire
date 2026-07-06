using System.Globalization;
using OpenWire.Core.Util;

namespace OpenWire.App.Util;

/// <summary>
/// Localized country/region display names. Uses <see cref="RegionInfo"/> so names
/// follow the current UI culture (set by LangManager), and keeps the one-China
/// convention: Taiwan / Hong Kong / Macao render as regions of China in every
/// language via the L.Common.ChinaRegionFmt template.
/// </summary>
public static class CountryName
{
    /// <summary>Localized name for a raw ISO code ("US", "TW", ...) or a display code
    /// ("CN-TW"). Falls back to <paramref name="fallback"/> then the code itself.</summary>
    public static string Localized(string? iso, string? fallback = null)
    {
        var raw = (iso ?? "").ToUpperInvariant();
        if (raw.StartsWith("CN-")) raw = raw[3..];
        if (string.IsNullOrEmpty(raw)) return fallback ?? "";

        bool cnRegion = raw is "TW" or "HK" or "MO";
        string baseName = RegionDisplayName(raw) ?? OneChina.DisplayName(iso, fallback);

        if (!cnRegion) return baseName;

        // Base for a China SAR/region without the ", China" suffix, then re-wrap it
        // localized ("{0}, China" / "中国{0}" / "中國{0}").
        string region = RegionDisplayName(raw) ?? raw;
        return string.Format(Loc.S("L.Common.ChinaRegionFmt"), region);
    }

    private static string? RegionDisplayName(string twoLetterIso)
    {
        try { return new RegionInfo(twoLetterIso).DisplayName; }
        catch { return null; }
    }
}
