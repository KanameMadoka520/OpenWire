using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace OpenWire.App.Controls;

/// <summary>One country's projected polygon(s) for the choropleth map.</summary>
internal sealed class CountryShape
{
    public string Iso = "";
    public string Name = "";
    public Geometry Geo = Geometry.Empty;
    public Rect Bounds;      // in projection space [0,360]x[0,180]
    public Point Centroid;   // in projection space
}

/// <summary>
/// World country polygons, generated from Natural Earth 1:110m (public domain). All
/// coordinates are in an equirectangular [0,360]x[0,180] space (x = lon+180, y = 90-lat).
/// Regenerate via scratchpad/gen_worldmap2.py.
/// </summary>
internal static class WorldGeo
{
    private static List<CountryShape>? _shapes;

    public static IReadOnlyList<CountryShape> Shapes => _shapes ??= Load();

    private static List<CountryShape> Load()
    {
        var list = new List<CountryShape>();
        try
        {
            var asm = typeof(WorldGeo).Assembly;
            var name = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith("WorldCountries.txt", StringComparison.OrdinalIgnoreCase));
            if (name is null) return list;

            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var parts = line.Split('\t');
                if (parts.Length < 5) continue;
                var cxy = parts[2].Split(',');
                var b = parts[3].Split(',');
                var g = Geometry.Parse(parts[4]);
                g.Freeze();
                list.Add(new CountryShape
                {
                    Iso = parts[0],
                    Name = parts[1],
                    Geo = g,
                    Centroid = new Point(D(cxy[0]), D(cxy[1])),
                    Bounds = new Rect(D(b[0]), D(b[1]), D(b[2]) - D(b[0]), D(b[3]) - D(b[1])),
                });
            }
        }
        catch { /* map unavailable */ }
        return list;
    }

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
}
