using System.Net;
using System.Runtime.InteropServices;

namespace OpenWire.Service.Native;

/// <summary>P/Invoke for the system ARP (IP-to-physical-address) table.</summary>
internal static class ArpInterop
{
    [DllImport("iphlpapi.dll")]
    private static extern int GetIpNetTable(IntPtr pIpNetTable, ref int pdwSize, bool bOrder);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_IPNETROW
    {
        public int dwIndex;
        public int dwPhysAddrLen;
        public byte mac0;
        public byte mac1;
        public byte mac2;
        public byte mac3;
        public byte mac4;
        public byte mac5;
        public byte mac6;
        public byte mac7;
        public int dwAddr;
        public int dwType; // 1=other 2=invalid 3=dynamic 4=static
    }

    internal readonly record struct ArpEntry(IPAddress Address, string Mac, int Type);

    public static List<ArpEntry> GetArpTable()
    {
        var result = new List<ArpEntry>();
        int size = 0;
        GetIpNetTable(IntPtr.Zero, ref size, false);
        if (size <= 0) return result;

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buffer, ref size, false) != 0) return result;

            int count = Marshal.ReadInt32(buffer);
            IntPtr rowPtr = buffer + 4;
            int rowSize = Marshal.SizeOf<MIB_IPNETROW>();

            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_IPNETROW>(rowPtr);
                rowPtr += rowSize;

                if (row.dwPhysAddrLen != 6) continue;
                if (row.dwType is 2) continue; // invalid

                var macBytes = new[] { row.mac0, row.mac1, row.mac2, row.mac3, row.mac4, row.mac5 };
                // Skip broadcast / all-zero / multicast MACs.
                if (macBytes.All(b => b == 0)) continue;
                if (macBytes.All(b => b == 0xFF)) continue;
                if ((macBytes[0] & 0x01) != 0) continue; // multicast bit

                string mac = string.Join(":", macBytes.Select(b => b.ToString("X2")));
                var ip = new IPAddress(BitConverter.GetBytes(row.dwAddr));
                result.Add(new ArpEntry(ip, mac, row.dwType));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }
}
