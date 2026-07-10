using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace OpenWire.Core.Ipc;

/// <summary>
/// Factory for the named-pipe endpoints. The engine runs elevated while the UI stays
/// at medium integrity, so the server grants the one authorized UI account read/write
/// access without granting it the right to create competing server instances.
/// </summary>
[SupportedOSPlatform("windows")]
public static class IpcTransport
{
    /// <summary>The well-known local pipe name the engine listens on.</summary>
    public const string PipeName = "OpenWire.Engine";

    private static int _firstServerInstanceClaimed;

    public static NamedPipeServerStream CreateServerStream(string authorizedUserSid)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Deny));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(authorizedUserSid),
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

        // A non-elevated development engine is not covered by the Administrators rule and
        // therefore needs CreateNewInstance itself. The production engine is elevated, so
        // the UI SID above remains ReadWrite-only and cannot inject a competing server.
        try
        {
            using var me = WindowsIdentity.GetCurrent();
            if (me.User is not null && !new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator))
                security.AddAccessRule(new PipeAccessRule(
                    me.User, PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        catch { /* fall back to the rules above */ }

        bool first = Interlocked.CompareExchange(ref _firstServerInstanceClaimed, 1, 0) == 0;
        try
        {
            return NamedPipeServerStreamAcl.Create(
                PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : 0),
                inBufferSize: 0,
                outBufferSize: 0,
                pipeSecurity: security);
        }
        catch
        {
            // If the first-instance claim failed because another process squatted the
            // name, every retry must retain FirstPipeInstance and keep failing closed.
            if (first) Volatile.Write(ref _firstServerInstanceClaimed, 0);
            throw;
        }
    }

    public static NamedPipeClientStream CreateClientStream(string server = ".")
        => new(
            server,
            PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification,
            HandleInheritability.None);
}
