using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace OpenWire.Core.Ipc;

/// <summary>
/// Factory for the named-pipe endpoints. The engine runs elevated but the UI runs
/// as a normal user, so the server pipe is created with an ACL that lets any
/// authenticated user connect.
/// </summary>
[SupportedOSPlatform("windows")]
public static class IpcTransport
{
    /// <summary>The well-known local pipe name the engine listens on.</summary>
    public const string PipeName = "OpenWire.Engine";

    public static NamedPipeServerStream CreateServerStream()
    {
        var security = new PipeSecurity();

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // The creating identity needs FullControl (which includes CreateNewInstance) to
        // open the accept loop's subsequent pipe instances. When elevated the
        // Administrators rule covers this; when the engine runs as a normal user, grant
        // the current user explicitly — otherwise the 2nd instance fails with Access
        // Denied and the accept loop busy-spins.
        try
        {
            using var me = WindowsIdentity.GetCurrent();
            if (me.User is not null)
                security.AddAccessRule(new PipeAccessRule(
                    me.User, PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        catch { /* fall back to the rules above */ }

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: security);
    }

    public static NamedPipeClientStream CreateClientStream(string server = ".")
        => new(server, PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
}
