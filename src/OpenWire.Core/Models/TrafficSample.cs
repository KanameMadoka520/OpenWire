namespace OpenWire.Core.Models;

/// <summary>
/// A single point on the traffic graph: the number of bytes that flowed in and
/// out of the machine during a short interval ending at <see cref="Time"/>.
/// </summary>
public sealed class TrafficSample
{
    public DateTimeOffset Time { get; set; }

    /// <summary>Bytes received during the interval.</summary>
    public long BytesIn { get; set; }

    /// <summary>Bytes sent during the interval.</summary>
    public long BytesOut { get; set; }

    public long Total => BytesIn + BytesOut;

    public TrafficSample() { }

    public TrafficSample(DateTimeOffset time, long bytesIn, long bytesOut)
    {
        Time = time;
        BytesIn = bytesIn;
        BytesOut = bytesOut;
    }
}

/// <summary>
/// A contiguous run of <see cref="TrafficSample"/>s at a fixed cadence, used to
/// feed the graph for a given <see cref="GraphRange"/>.
/// </summary>
public sealed class TrafficSeries
{
    public GraphRange Range { get; set; }

    /// <summary>Spacing between samples, in seconds.</summary>
    public int IntervalSeconds { get; set; } = 1;

    public List<TrafficSample> Samples { get; set; } = new();

    /// <summary>Peak per-interval throughput (bytes) across the series, for graph scaling.</summary>
    public long PeakBytes { get; set; }
}
