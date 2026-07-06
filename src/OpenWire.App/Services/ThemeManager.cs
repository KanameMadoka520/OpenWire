using System.Diagnostics;
using System.IO;
using System.Windows;

namespace OpenWire.App.Services;

/// <summary>
/// Chooses and applies the UI skin (Minimal / Pencil / BerryDay / BerryNight). The choice
/// is stored in a small local file so it can be read synchronously at startup, before any
/// window is created, and merged as the first resource dictionaries. Switching persists the
/// choice and restarts the app so the whole visual tree re-skins cleanly (the engine keeps running).
/// </summary>
public static class ThemeManager
{
    private static readonly Dictionary<string, string> Skins = new()
    {
        ["Minimal"] = "Skin.Minimal.xaml",
        ["Pencil"] = "Skin.Pencil.xaml",
        ["BerryDay"] = "Skin.BerryDay.xaml",
        ["BerryNight"] = "Skin.BerryNight.xaml",
    };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenWire", "theme.txt");

    public static string Read()
    {
        try
        {
            var t = File.ReadAllText(FilePath).Trim();
            if (Skins.ContainsKey(t)) return t;
        }
        catch { /* not set yet */ }
        return "Pencil"; // default to the pencil sketchbook
    }

    public static void Save(string name)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, name);
        }
        catch { /* best effort */ }
    }

    /// <summary>Merge the skin dictionaries for <paramref name="theme"/> into the app's
    /// resources. Call once at startup before creating any window.</summary>
    public static void Apply(Application app, string theme)
    {
        string skin = Skins.GetValueOrDefault(theme, Skins["Pencil"]);
        var res = app.Resources.MergedDictionaries;
        void Add(string f) => res.Add(new ResourceDictionary { Source = new Uri($"Theme/{f}", UriKind.Relative) });
        Add("SketchAssets.xaml"); // fonts, geometries (skin-independent) — first
        Add(skin);                // palette + skin parameters
        Add("Controls.xaml");     // control styles (reference the skin)
        Add("Lists.xaml");
    }

    /// <summary>Persist the theme and rebuild the main window with the new skin
    /// (live switch — no app restart, the engine and tray keep running).</summary>
    public static void Switch(string theme)
    {
        if (!Skins.ContainsKey(theme)) return;
        Save(theme);
        (Application.Current as App)?.ReskinMainWindow();
    }
}
