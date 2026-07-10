using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenWire.Core.Ipc;

/// <summary>Immutable identity facts read from the process that owns one end of a pipe.</summary>
public sealed record IpcPeerProcessInfo(
    int ProcessId,
    string ImagePath,
    string UserSid,
    string UserName,
    bool IsElevated);

/// <summary>
/// Windows-only peer verification for the privileged named-pipe boundary. Process ids come from
/// the pipe handle itself, not from caller-controlled JSON or command-line fields.
/// </summary>
[SupportedOSPlatform("windows")]
public static class IpcPeerIdentity
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int ErrorInsufficientBuffer = 122;

    public static bool TryGetClientProcessInfo(NamedPipeServerStream pipe, out IpcPeerProcessInfo info)
    {
        info = null!;
        return GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid)
            && pid <= int.MaxValue
            && TryGetProcessInfo((int)pid, out info);
    }

    public static bool TryGetServerProcessInfo(NamedPipeClientStream pipe, out IpcPeerProcessInfo info)
    {
        info = null!;
        return GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid)
            && pid <= int.MaxValue
            && TryGetProcessInfo((int)pid, out info);
    }

    public static bool TryGetProcessInfo(int processId, out IpcPeerProcessInfo info)
    {
        info = null!;
        if (processId <= 0) return false;

        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid) return false;

        var path = new StringBuilder(32_768);
        int pathLength = path.Capacity;
        if (!QueryFullProcessImageName(process, 0, path, ref pathLength)) return false;

        if (!OpenProcessToken(process, TokenQuery, out var token)) return false;
        using (token)
        {
            if (!TryReadUser(token, out string sid, out string name)) return false;
            if (!TryReadElevation(token, out bool elevated)) return false;
            info = new IpcPeerProcessInfo(processId, path.ToString(), sid, name, elevated);
            return true;
        }
    }

    public static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadUser(SafeAccessTokenHandle token, out string sid, out string name)
    {
        sid = string.Empty;
        name = string.Empty;

        _ = GetTokenInformation(token, TokenInformationClass.TokenUser, IntPtr.Zero, 0, out int bytes);
        if (bytes <= 0 || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer) return false;

        IntPtr buffer = Marshal.AllocHGlobal(bytes);
        try
        {
            if (!GetTokenInformation(token, TokenInformationClass.TokenUser, buffer, bytes, out _)) return false;
            var tokenUser = Marshal.PtrToStructure<TokenUser>(buffer);
            var userSid = new SecurityIdentifier(tokenUser.User.Sid);
            sid = userSid.Value;
            try { name = userSid.Translate(typeof(NTAccount)).Value; }
            catch (IdentityNotMappedException) { name = sid; }
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryReadElevation(SafeAccessTokenHandle token, out bool elevated)
    {
        elevated = false;
        int bytes = Marshal.SizeOf<TokenElevation>();
        IntPtr buffer = Marshal.AllocHGlobal(bytes);
        try
        {
            if (!GetTokenInformation(token, TokenInformationClass.TokenElevation, buffer, bytes, out _)) return false;
            elevated = Marshal.PtrToStructure<TokenElevation>(buffer).TokenIsElevated != 0;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenUser
    {
        public SidAndAttributes User;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        public int TokenIsElevated;
    }

    private enum TokenInformationClass
    {
        TokenUser = 1,
        TokenElevation = 20,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        int flags,
        StringBuilder imagePath,
        ref int size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        SafeProcessHandle process,
        uint desiredAccess,
        out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle token,
        TokenInformationClass informationClass,
        IntPtr information,
        int informationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
