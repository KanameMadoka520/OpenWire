using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace OpenWire.App.Controls;

/// <summary>
/// One country, kept as raw geographic rings so it can be projected two ways:
/// equirectangular (the flat map) and orthographic (the 3D globe). All the derived
/// shapes are precomputed once at load.
/// </summary>
internal sealed class CountryShape
{
    public string Iso = "";
    public string Name = "";

    /// <summary>Exterior rings in raw geographic space (X = longitude, Y = latitude).</summary>
    public IReadOnlyList<Point[]> Rings = Array.Empty<Point[]>();

    /// <summary>Same rings, edge-densified, for a smooth globe silhouette.</summary>
    public IReadOnlyList<Point[]> GlobeRings = Array.Empty<Point[]>();

    /// <summary>Equirectangular geometry in projection space [0,360]x[0,180] (flat map + hit-test).</summary>
    public Geometry Geo = Geometry.Empty;

    /// <summary>Bounds of <see cref="Geo"/> in projection space.</summary>
    public Rect Bounds;

    /// <summary>Label anchor in projection space (equirectangular).</summary>
    public Point Centroid;

    /// <summary>Label anchor in raw geographic space (lon,lat) — used to aim the globe.</summary>
    public Point CentroidGeo;

    /// <summary>Ray-cast point-in-polygon test in raw geographic space.</summary>
    public bool ContainsGeo(double lon, double lat)
    {
        foreach (var ring in Rings)
        {
            bool inside = false;
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            {
                double xi = ring[i].X, yi = ring[i].Y, xj = ring[j].X, yj = ring[j].Y;
                if (((yi > lat) != (yj > lat)) &&
                    (lon < (xj - xi) * (lat - yi) / (yj - yi) + xi))
                    inside = !inside;
            }
            if (inside) return true;
        }
        return false;
    }
}

/// <summary>
/// World country polygons from Natural Earth 1:110m (public domain), stored as raw
/// lon/lat rings. Equirectangular projection is x = lon+180, y = 90-lat.
/// Regenerate via scratchpad/gen_worldmap3.py.
/// </summary>
internal static class WorldGeo
{
    private const double GlobeStepDeg = 4.0; // densify long edges to this many degrees for the globe

    private static List<CountryShape>? _shapes;
    public static IReadOnlyList<CountryShape> Shapes => _shapes ??= Load();

    public static Point ProjectFlat(Point lonLat) => new(lonLat.X + 180, 90 - lonLat.Y);

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
                if (parts.Length < 4) continue;
                var rings = ParseRings(parts[3]);
                if (rings.Count == 0) continue;

                var cxy = parts[2].Split(',');
                var centroidGeo = new Point(D(cxy[0]), D(cxy[1]));

                list.Add(new CountryShape
                {
                    Iso = parts[0],
                    Name = parts[1],
                    Rings = rings,
                    GlobeRings = rings.ConvertAll(Densify),
                    Geo = BuildFlatGeometry(rings, out var bounds),
                    Bounds = bounds,
                    CentroidGeo = centroidGeo,
                    Centroid = ProjectFlat(centroidGeo),
                });
            }
        }
        catch { /* map unavailable — control just draws nothing */ }
        return list;
    }

    private static List<Point[]> ParseRings(string field)
    {
        var rings = new List<Point[]>();
        foreach (var ringStr in field.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pairs = ringStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (pairs.Length < 3) continue;
            var pts = new Point[pairs.Length];
            for (int i = 0; i < pairs.Length; i++)
            {
                int comma = pairs[i].IndexOf(',');
                pts[i] = new Point(D(pairs[i][..comma]), D(pairs[i][(comma + 1)..]));
            }
            rings.Add(pts);
        }
        return rings;
    }

    private static Geometry BuildFlatGeometry(List<Point[]> rings, out Rect bounds)
    {
        double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
        var sg = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var ctx = sg.Open())
        {
            foreach (var ring in rings)
            {
                var first = ProjectFlat(ring[0]);
                ctx.BeginFigure(first, true, true);
                var line = new Point[ring.Length - 1];
                for (int i = 1; i < ring.Length; i++)
                {
                    var p = ProjectFlat(ring[i]);
                    line[i - 1] = p;
                }
                ctx.PolyLineTo(line, true, false);
                foreach (var raw in ring)
                {
                    var p = ProjectFlat(raw);
                    if (p.X < minx) minx = p.X; if (p.X > maxx) maxx = p.X;
                    if (p.Y < miny) miny = p.Y; if (p.Y > maxy) maxy = p.Y;
                }
            }
        }
        sg.Freeze();
        bounds = new Rect(minx, miny, maxx - minx, maxy - miny);
        return sg;
    }

    /// <summary>Insert intermediate points on long edges so the ring bends nicely on a sphere.</summary>
    private static Point[] Densify(Point[] ring)
    {
        var outPts = new List<Point>(ring.Length * 2);
        for (int i = 0; i < ring.Length; i++)
        {
            var a = ring[i];
            outPts.Add(a);
            var b = ring[(i + 1) % ring.Length];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            int steps = (int)(len / GlobeStepDeg);
            for (int s = 1; s < steps; s++)
            {
                double t = (double)s / steps;
                outPts.Add(new Point(a.X + dx * t, a.Y + dy * t));
            }
        }
        return outPts.ToArray();
    }

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
}
