using System.Collections.Generic;
using System.IO;
using System.Text;
using OpenWire.App.Services;
using OpenWire.Core.Util;

namespace OpenWire.App.Util;

/// <summary>
/// Localized country/region display names in the app's own languages (English /
/// Simplified Chinese / Traditional Chinese) — never a region's native name.
///
/// <para><see cref="System.Globalization.RegionInfo.DisplayName"/> can't be used: on
/// Windows it returns the NATIVE name regardless of the UI culture (e.g. "الجزائر"
/// for DZ, "Deutschland" for DE), so the map would show names a Chinese- or
/// English-speaking user can't read. Instead we carry a compact CLDR table
/// (ISO2 → en / zh-Hans / zh-Hant, embedded as CountryNames.txt) and pick the
/// column for the active UI language.</para>
///
/// Keeps the one-China convention: Taiwan / Hong Kong / Macao render as regions of
/// China in every language via the L.Common.ChinaRegionFmt template — e.g. "中国台湾"
/// (Simplified), "中國台灣" (Traditional), "Taiwan, China" (English).
/// </summary>
public static class CountryName
{
    // ISO2 -> [en, zh-Hans, zh-Hant]. Loaded once; language is chosen per lookup.
    private static readonly Dictionary<string, string[]> Names = Load();

    /// <summary>Localized name for a raw ISO code ("US", "TW", ...) or a display code
    /// ("CN-TW"). Falls back to <paramref name="fallback"/> then the code itself, but
    /// never to the region's native-language name.</summary>
    public static string Localized(string? iso, string? fallback = null)
    {
        var raw = (iso ?? "").ToUpperInvariant();
        if (raw.StartsWith("CN-")) raw = raw[3..];
        if (string.IsNullOrEmpty(raw)) return fallback ?? "";

        string? local = LocalName(raw);

        // One-China: SARs / Taiwan re-wrap as a region of China in the UI language.
        if (raw is "TW" or "HK" or "MO")
            return string.Format(Loc.S("L.Common.ChinaRegionFmt"), local ?? raw);

        return local ?? fallback ?? raw;
    }

    /// <summary>Column index for the active UI language (0=en, 1=zh-Hans, 2=zh-Hant).</summary>
    private static int Col() => LangManager.Current switch
    {
        "SimplifiedChinese" => 1,
        "TraditionalChinese" => 2,
        _ => 0,
    };

    /// <summary>Table name in the active language, falling back to English then null.</summary>
    private static string? LocalName(string iso2)
    {
        if (!Names.TryGetValue(iso2, out var cols)) return null;
        int c = Col();
        string v = c < cols.Length ? cols[c] : "";
        if (string.IsNullOrEmpty(v)) v = cols[0]; // no localized form → English
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private static Dictionary<string, string[]> Load()
    {
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var asm = typeof(CountryName).Assembly;
            var res = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith("CountryNames.txt", StringComparison.OrdinalIgnoreCase));
            if (res is null) return map;

            using var stream = asm.GetManifestResourceStream(res)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var p = line.Split('\t');
                if (p.Length < 2 || p[0].Length == 0) continue;
                map[p[0]] = new[]
                {
                    p[1],
                    p.Length > 2 && p[2].Length > 0 ? p[2] : p[1],
                    p.Length > 3 && p[3].Length > 0 ? p[3] : p[1],
                };
            }
        }
        catch { /* table unavailable — callers fall back to their own name */ }
        return map;
    }
}
