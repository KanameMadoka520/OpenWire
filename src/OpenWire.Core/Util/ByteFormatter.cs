using System.Globalization;

namespace OpenWire.Core.Util;

/// <summary>Human-readable formatting for byte counts and throughput rates.</summary>
public static class ByteFormatter
{
    private static readonly string[] SizeUnits = { "B", "KB", "MB", "GB", "TB", "PB" };

    /// <summary>Format a byte count like "1.2 GB" (base-1024).</summary>
    public static string Bytes(long bytes)
    {
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < SizeUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string format = unit == 0 ? "0" : value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return value.ToString(format, CultureInfo.InvariantCulture) + " " + SizeUnits[unit];
    }

    /// <summary>Format a throughput rate like "3.4 MB/s".</summary>
    public static string Rate(double bytesPerSecond)
        => Bytes((long)Math.Round(bytesPerSecond)) + "/s";

    /// <summary>Format a rate in bits per second like "27.2 Mbps".</summary>
    public static string BitRate(double bytesPerSecond)
    {
        double bits = bytesPerSecond * 8;
        string[] units = { "bps", "Kbps", "Mbps", "Gbps" };
        int unit = 0;
        while (bits >= 1000 && unit < units.Length - 1)
        {
            bits /= 1000;
            unit++;
        }
        string format = bits >= 100 ? "0" : bits >= 10 ? "0.0" : "0.00";
        return bits.ToString(format, CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
