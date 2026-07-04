using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenWire.Service.Engine;

/// <summary>Identity of the network the machine is currently attached to.</summary>
/// <param name="Name">Friendly label — the Wi-Fi SSID, else the adapter name.</param>
/// <param name="Fingerprint">Stable key for auto-activating a profile
/// (<c>wifi:SSID</c> on wireless, <c>eth:GATEWAY-MAC</c> on wired). Empty if offline.</param>
public readonly record struct NetworkInfo(string Name, string Fingerprint)
{
    public static readonly NetworkInfo None = new(string.Empty, string.Empty);
}

/// <summary>
/// Best-effort detection of the active network. Uses the wlanapi to read the
/// connected Wi-Fi SSID and falls back to the primary gateway adapter (name +
/// gateway MAC via SendARP) for wired / SSID-less connections. Every call is fully
/// guarded — any failure degrades to <see cref="NetworkInfo.None"/>.
/// </summary>
public static class NetworkIdentity
{
    public static NetworkInfo Current()
    {
        try
        {
            var nic = PrimaryInterface();
            if (nic is null) return NetworkInfo.None;

            if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            {
                string? ssid = TryGetSsid();
                if (!string.IsNullOrEmpty(ssid))
                    return new NetworkInfo(ssid!, "wifi:" + ssid);
            }

            string? mac = GatewayMac(nic);
            string label = string.IsNullOrEmpty(nic.Name) ? "Wired network" : nic.Name;
            string fp = mac is not null ? "eth:" + mac : "nic:" + nic.Id;
            return new NetworkInfo(label, fp);
        }
        catch
        {
            return NetworkInfo.None;
        }
    }

    /// <summary>The up, non-loopback adapter that owns a real IPv4 default gateway.</summary>
    private static NetworkInterface? PrimaryInterface()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            var gw = nic.GetIPProperties().GatewayAddresses
                .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                     && !g.Address.Equals(IPAddress.Any));
            if (gw is not null) return nic;
        }
        return null;
    }

    private static string? GatewayMac(NetworkInterface nic)
    {
        var gw = nic.GetIPProperties().GatewayAddresses
            .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
        if (gw is null) return null;

        try
        {
            byte[] mac = new byte[6];
            uint len = 6;
            uint dest = BitConverter.ToUInt32(gw.Address.GetAddressBytes(), 0);
            if (SendARP(dest, 0, mac, ref len) == 0 && len >= 6 && mac.Any(b => b != 0))
                return string.Join('-', mac.Take(6).Select(b => b.ToString("X2")));
        }
        catch { /* SendARP unavailable */ }
        return null;
    }

    // ---------------- wlanapi (SSID of the connected wireless interface) ----------------

    private static string? TryGetSsid()
    {
        IntPtr client = IntPtr.Zero, list = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out client) != 0) return null;
            if (WlanEnumInterfaces(client, IntPtr.Zero, out list) != 0) return null;

            int count = Marshal.ReadInt32(list); // dwNumberOfItems
            IntPtr infoPtr = list + 8;           // skip dwNumberOfItems + dwIndex
            int infoSize = Marshal.SizeOf<WLAN_INTERFACE_INFO>();

            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(infoPtr + i * infoSize);
                if (info.isState != 1) continue; // wlan_interface_state_connected

                var guid = info.InterfaceGuid;
                if (WlanQueryInterface(client, ref guid, WLAN_INTF_OPCODE_CURRENT_CONNECTION,
                        IntPtr.Zero, out _, out IntPtr data, IntPtr.Zero) != 0 || data == IntPtr.Zero)
                    continue;
                try
                {
                    var conn = Marshal.PtrToStructure<WLAN_CONNECTION_ATTRIBUTES_HEAD>(data);
                    int len = (int)Math.Min(conn.dot11SsidLength, 32u);
                    if (len > 0) return Encoding.UTF8.GetString(conn.ucSSID, 0, len);
                }
                finally { WlanFreeMemory(data); }
            }
            return null;
        }
        catch { return null; }
        finally
        {
            if (list != IntPtr.Zero) WlanFreeMemory(list);
            if (client != IntPtr.Zero) WlanCloseHandle(client, IntPtr.Zero);
        }
    }

    private const uint WLAN_INTF_OPCODE_CURRENT_CONNECTION = 7;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_INTERFACE_INFO
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strInterfaceDescription;
        public uint isState;
    }

    // Truncated view of WLAN_CONNECTION_ATTRIBUTES up to the association SSID — the
    // fields past the SSID are not read, so the shorter layout is sufficient.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_CONNECTION_ATTRIBUTES_HEAD
    {
        public uint isState;
        public uint wlanConnectionMode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strProfileName;
        public uint dot11SsidLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] ucSSID;
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid, uint OpCode,
        IntPtr pReserved, out uint pdwDataSize, out IntPtr ppData, IntPtr pWlanOpcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr pMemory);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint macAddrLen);
}
