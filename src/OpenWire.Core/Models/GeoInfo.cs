namespace OpenWire.Core.Models;

/// <summary>Geographic origin of a remote endpoint, resolved from a GeoIP database.</summary>
public sealed class GeoInfo
{
    /// <summary>ISO 3166-1 alpha-2 country code (e.g. "US", "DE"). Empty if unknown.</summary>
    public string CountryCode { get; set; } = string.Empty;

    public string CountryName { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public bool IsLocal { get; set; }

    public bool HasCountry => !string.IsNullOrEmpty(CountryCode);

    public static readonly GeoInfo Unknown = new();

    public static GeoInfo Local => new() { IsLocal = true, CountryName = "Local network" };
}
