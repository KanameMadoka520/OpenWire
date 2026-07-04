using System.Net.NetworkInformation;

namespace OpenWire.Service.Engine;

/// <summary>
/// Driverless, unprivileged fallback that reads cumulative per-interface byte
/// counters. Gives an accurate global up/down graph (no per-app attribution) when
/// the ETW kernel session is unavailable (e.g. running without elevation).
/// </summary>
public sealed class InterfaceTrafficMonitor
{
    public (long In, long Out) ReadGlobal()
    {
        long inBytes = 0;
        long outBytes = 0;

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

            try
            {
                var stats = ni.GetIPStatistics();
                inBytes += stats.BytesReceived;
                outBytes += stats.BytesSent;
            }
            catch
            {
                // Some virtual adapters don't expose statistics.
            }
        }

        return (inBytes, outBytes);
    }
}
