namespace OpenWire.Core.Models;

/// <summary>One point of system-resource telemetry.</summary>
public sealed class HardwareSample
{
    public DateTimeOffset Time { get; set; }

    /// <summary>CPU utilisation, 0..100.</summary>
    public double CpuPercent { get; set; }

    /// <summary>Committed memory, 0..100.</summary>
    public double MemoryPercent { get; set; }

    /// <summary>Combined disk transfer rate, bytes/sec.</summary>
    public double DiskBytesPerSec { get; set; }

    /// <summary>GPU utilisation, 0..100 (0 if unavailable).</summary>
    public double GpuPercent { get; set; }
}

/// <summary>Current resource readings plus a short rolling history for the graphs.</summary>
public sealed class HardwareSnapshot
{
    public double CpuPercent { get; set; }
    public double MemoryPercent { get; set; }
    public long MemoryUsedBytes { get; set; }
    public long MemoryTotalBytes { get; set; }
    public double DiskReadBytesPerSec { get; set; }
    public double DiskWriteBytesPerSec { get; set; }
    public double GpuPercent { get; set; }
    public long GpuMemoryUsedBytes { get; set; }

    public List<HardwareSample> History { get; set; } = new();
}
