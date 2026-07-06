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

/// <summary>Per-process resource usage row for the Hardware screen's process table.</summary>
public sealed class ProcessResourceRow
{
    public int Pid { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>CPU utilisation across all cores, 0..100.</summary>
    public double CpuPercent { get; set; }

    /// <summary>GPU utilisation, 0..100 (0 if unavailable).</summary>
    public double GpuPercent { get; set; }

    /// <summary>Working set (physical memory), bytes.</summary>
    public long MemoryBytes { get; set; }

    /// <summary>Disk I/O rate (read + write), bytes/sec.</summary>
    public double DiskBytesPerSec { get; set; }
}

/// <summary>One physical resource (CPU / memory / a GPU / a disk) for the Hardware
/// screen's "hardware resource usage" list.</summary>
public sealed class HardwareResourceRow
{
    /// <summary>Category: "cpu" · "memory" · "gpu" · "disk". Drives the row icon.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Display name, e.g. "AMD Ryzen 9 9950X3D" or "NVIDIA GeForce RTX 5090".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Secondary line, e.g. a GPU index "GPU 4" or a disk "SSD · Fixed".</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>Utilisation 0..100, or null when a component reports no usage figure.</summary>
    public double? Percent { get; set; }
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

    /// <summary>Top processes by resource use, for the Hardware process table.</summary>
    public List<ProcessResourceRow> Processes { get; set; } = new();

    /// <summary>Physical components (CPU, memory, each GPU, each disk) for the
    /// "hardware resource usage" list.</summary>
    public List<HardwareResourceRow> Resources { get; set; } = new();
}

/// <summary>Where the engine keeps its database, and how much space it uses.</summary>
public sealed class StorageInfo
{
    /// <summary>Directory that holds openwire.db (+ WAL/SHM).</summary>
    public string DataDirectory { get; set; } = string.Empty;

    /// <summary>Total bytes of the database files (db + wal + shm).</summary>
    public long DatabaseBytes { get; set; }

    /// <summary>Free space on the volume holding the data directory.</summary>
    public long FreeBytes { get; set; }

    /// <summary>Oldest retained minute-history timestamp, if any.</summary>
    public DateTimeOffset? OldestRecord { get; set; }

    /// <summary>True when a relocation was staged and needs an engine restart to take effect.</summary>
    public bool RestartRequired { get; set; }
}

/// <summary>What a clear-data request should remove.</summary>
public enum ClearDataMode
{
    /// <summary>Drop per-minute history but keep daily rollups + settings, then compact.</summary>
    MinuteHistory,
    /// <summary>Drop all traffic/usage/device history (keep settings + firewall), then compact.</summary>
    AllHistory,
}
