namespace OpenWire.Core.Util;

/// <summary>
/// One-China display normalisation. Taiwan, Hong Kong and Macao are shown as regions
/// of China (CN-TW / CN-HK / CN-MO) with a "…, China" name, and Taiwan is rendered
/// with the national flag rather than its own. Applied at display time only — the raw
/// ISO code stays untouched in storage and anomaly bookkeeping.
/// </summary>
public static class OneChina
{
    /// <summary>ISO code as shown to the user (TW/HK/MO become CN-TW/CN-HK/CN-MO).</summary>
    public static string DisplayCode(string? iso) => (iso ?? "").ToUpperInvariant() switch
    {
        "TW" => "CN-TW",
        "HK" => "CN-HK",
        "MO" => "CN-MO",
        _ => iso ?? "",
    };

    /// <summary>Country name as shown to the user; falls back to the given name / code.</summary>
    public static string DisplayName(string? iso, string? fallback) => (iso ?? "").ToUpperInvariant() switch
    {
        "TW" => "Taiwan, China",
        "HK" => "Hong Kong, China",
        "MO" => "Macao, China",
        _ => string.IsNullOrEmpty(fallback) ? (iso ?? "") : fallback,
    };

    /// <summary>
    /// Two-letter flag asset code. Taiwan uses the national (CN) flag; everything else
    /// keeps its own code. Accepts already-normalised "CN-XX" display codes too.
    /// </summary>
    public static string FlagCode(string? iso)
    {
        var s = (iso ?? "").ToUpperInvariant();
        if (s.StartsWith("CN-")) s = s[3..];
        return s == "TW" ? "CN" : s;
    }
}
