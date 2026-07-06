using System.IO;
using System.Windows;

namespace OpenWire.App.Services;

/// <summary>
/// Chooses and applies the UI language (English / Simplified Chinese / Traditional
/// Chinese). Mirrors <see cref="ThemeManager"/>: the choice is stored in a small
/// local file read synchronously at startup, and the string dictionary is merged
/// before any window is created. Switching persists the choice and rebuilds the
/// main window in place (no app restart) — see App.ReskinMainWindow.
/// </summary>
public static class LangManager
{
    private static readonly Dictionary<string, string> Langs = new()
    {
        ["English"] = "Strings.en.xaml",
        ["SimplifiedChinese"] = "Strings.zh-Hans.xaml",
        ["TraditionalChinese"] = "Strings.zh-Hant.xaml",
    };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenWire", "lang.txt");

    public static string Read()
    {
        try
        {
            var l = File.ReadAllText(FilePath).Trim();
            if (Langs.ContainsKey(l)) return l;
        }
        catch { /* not set yet */ }
        return "English";
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

    /// <summary>Merge the string dictionary for <paramref name="lang"/> into the app's
    /// resources. Call before the theme dictionaries so skins could override strings.</summary>
    public static void Apply(Application app, string lang)
    {
        string file = Langs.GetValueOrDefault(lang, Langs["English"]);
        app.Resources.MergedDictionaries.Add(
            new ResourceDictionary { Source = new Uri($"Lang/{file}", UriKind.Relative) });
    }

    /// <summary>Persist the language and rebuild the main window with the new strings.</summary>
    public static void Switch(string lang)
    {
        if (!Langs.ContainsKey(lang)) return;
        Save(lang);
        (Application.Current as App)?.ReskinMainWindow();
    }
}
