using System.Windows;

namespace OpenWire.App.Util;

/// <summary>Code-side lookup for localized strings (XAML uses StaticResource).</summary>
public static class Loc
{
    /// <summary>The string resource for <paramref name="key"/>, or the key itself
    /// when missing — a visible-but-harmless fallback.</summary>
    public static string S(string key) => Application.Current?.TryFindResource(key) as string ?? key;
}
