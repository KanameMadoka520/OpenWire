using System.Net;
using System.Runtime.InteropServices;

namespace OpenWire.Service.Native;

/// <summary>
/// P/Invoke surface for iphlpapi.dll's extended connection tables, which expose
/// the owning process id for every TCP/UDP endpoint (IPv4 and IPv6).
/// </summary>
internal static class IpHlpApiInterop
{
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;

    // TCP_TABLE_CLASS
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    // UDP_TABLE_CLASS
    private const int UDP_TABLE_OWNER_PID = 1;

    private const int NO_ERROR = 0;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, int reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public uint localPort;
        public uint remoteAddr;
        public uint remotePort;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] localAddr;
        public uint localScopeId;
        public uint localPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] remoteAddr;
        public uint remoteScopeId;
        public uint remotePort;
        public uint state;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint localAddr;
        public uint localPort;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] localAddr;
        public uint localScopeId;
        public uint localPort;
        public uint owningPid;
    }

    /// <summary>A single connection-table row in a managed, protocol-neutral form.</summary>
    internal readonly record struct Endpoint(
        bool IsTcp, bool IsIPv6,
        IPAddress LocalAddress, int LocalPort,
        IPAddress RemoteAddress, int RemotePort,
        int State, int ProcessId);

    private static ushort ParsePort(uint netOrderLowWord)
        => (ushort)(((netOrderLowWord & 0xFF) << 8) | ((netOrderLowWord >> 8) & 0xFF));

    public static List<Endpoint> GetAllEndpoints()
    {
        var result = new List<Endpoint>(512);
        ReadTcp(AF_INET, result);
        ReadTcp(AF_INET6, result);
        ReadUdp(AF_INET, result);
        ReadUdp(AF_INET6, result);
        return result;
    }

    private static IntPtr AllocForTable(Func<IntPtr, int, (uint status, int need)> call, out int size)
    {
        size = 0;
        // First call to size the buffer.
        var probe = call(IntPtr.Zero, size);
        size = probe.need;
        if (size <= 0) return IntPtr.Zero;
        return Marshal.AllocHGlobal(size);
    }

    private static void ReadTcp(int af, List<Endpoint> outList)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size <= 0) return;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0) != NO_ERROR)
                return;

            int count = Marshal.ReadInt32(buffer);
            IntPtr rowPtr = buffer + 4;

            if (af == AF_INET)
            {
                int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                for (int i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                    outList.Add(new Endpoint(
                        IsTcp: true, IsIPv6: false,
                        new IPAddress(row.localAddr), ParsePort(row.localPort),
                        new IPAddress(row.remoteAddr), ParsePort(row.remotePort),
                        (int)row.state, (int)row.owningPid));
                    rowPtr += rowSize;
                }
            }
            else
            {
                int rowSize = Marshal.SizeOf<MIB_TCP6ROW_OWNER_PID>();
                for (int i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCP6ROW_OWNER_PID>(rowPtr);
                    outList.Add(new Endpoint(
                        IsTcp: true, IsIPv6: true,
                        new IPAddress(row.localAddr), ParsePort(row.localPort),
                        new IPAddress(row.remoteAddr), ParsePort(row.remotePort),
                        (int)row.state, (int)row.owningPid));
                    rowPtr += rowSize;
                }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void ReadUdp(int af, List<Endpoint> outList)
    {
        int size = 0;
        GetExtendedUdpTable(IntPtr.Zero, ref size, false, af, UDP_TABLE_OWNER_PID, 0);
        if (size <= 0) return;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedUdpTable(buffer, ref size, false, af, UDP_TABLE_OWNER_PID, 0) != NO_ERROR)
                return;

            int count = Marshal.ReadInt32(buffer);
            IntPtr rowPtr = buffer + 4;

            if (af == AF_INET)
            {
                int rowSize = Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();
                for (int i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(rowPtr);
                    outList.Add(new Endpoint(
                        IsTcp: false, IsIPv6: false,
                        new IPAddress(row.localAddr), ParsePort(row.localPort),
                        IPAddress.Any, 0, 0, (int)row.owningPid));
                    rowPtr += rowSize;
                }
            }
            else
            {
                int rowSize = Marshal.SizeOf<MIB_UDP6ROW_OWNER_PID>();
                for (int i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_UDP6ROW_OWNER_PID>(rowPtr);
                    outList.Add(new Endpoint(
                        IsTcp: false, IsIPv6: true,
                        new IPAddress(row.localAddr), ParsePort(row.localPort),
                        IPAddress.IPv6Any, 0, 0, (int)row.owningPid));
                    rowPtr += rowSize;
                }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Map a MIB_TCP_STATE value to its display name.</summary>
    public static string TcpStateName(int state) => state switch
    {
        1 => "Closed",
        2 => "Listen",
        3 => "SynSent",
        4 => "SynReceived",
        5 => "Established",
        6 => "FinWait1",
        7 => "FinWait2",
        8 => "CloseWait",
        9 => "Closing",
        10 => "LastAck",
        11 => "TimeWait",
        12 => "DeleteTcb",
        _ => "-",
    };
}
