namespace OpenWire.Core.Models;

/// <summary>Aggregated usage grouped by a remote host (domain or IP).</summary>
public sealed class HostUsage
{
    /// <summary>Resolved host name if available, otherwise the raw IP.</summary>
    public string Host { get; set; } = string.Empty;

    public string RemoteAddress { get; set; } = string.Empty;

    public long BytesIn { get; set; }
    public long BytesOut { get; set; }
    public long Total => BytesIn + BytesOut;

    public GeoInfo Geo { get; set; } = GeoInfo.Unknown;

    public int ConnectionCount { get; set; }

    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>Distinct apps that talked to this host.</summary>
    public int AppCount { get; set; }
}

/// <summary>Aggregated usage grouped by traffic type (application / protocol bucket).</summary>
public sealed class TrafficTypeUsage
{
    /// <summary>Bucket name, e.g. "Web (HTTPS)", "DNS", "Streaming", "Other".</summary>
    public string TypeName { get; set; } = string.Empty;

    public long BytesIn { get; set; }
    public long BytesOut { get; set; }
    public long Total => BytesIn + BytesOut;

    /// <summary>Fraction of the overall total (0..1) for the pie/bar chart.</summary>
    public double Fraction { get; set; }
}
