using System.Globalization;
using OpenWire.App.Services;

namespace OpenWire.App.Util;

/// <summary>Shared date/number presentation helpers, so formatting stays consistent across views.</summary>
public static class AppFormat
{
    /// <summary>The culture matching the app's chosen UI language (not the OS locale) — used for
    /// month names and other localized date parts. Centralized here so the range-label sites
    /// (Traffic graph corner, Analytics custom span, per-day chart headers) can't drift apart.</summary>
    public static CultureInfo Culture =>
        CultureInfo.GetCultureInfo(LangManager.CultureName(LangManager.Current));
}
