using System.Runtime.InteropServices;
using System.Text;

namespace OpenWire.Service.Native;

/// <summary>Minimal kernel32 surface for resolving a PID to its image path.</summary>
internal static class ProcessNative
{
    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, int flags, [Out] StringBuilder lpExeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>Return the full image path for a PID, or null if it can't be read.</summary>
    public static string? GetProcessImagePath(int pid)
    {
        if (pid <= 0) return null;

        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;

        try
        {
            int capacity = 1024;
            var sb = new StringBuilder(capacity);
            // The StringBuilder marshaller sets the correct length from the null
            // terminator; the ref size out-value is not reliable, so use ToString().
            if (QueryFullProcessImageNameW(handle, 0, sb, ref capacity))
                return sb.ToString();
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
