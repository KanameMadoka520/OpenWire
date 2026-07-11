using System.Runtime.InteropServices;

namespace OpenWire.Service.Native;

/// <summary>
/// Reads overall internet-connectivity state from the Windows Network List Manager
/// (NLM) COM service — the same source the taskbar network icon uses. Consumed by the
/// internet-access monitor to detect when the machine gains or loses internet
/// reachability. Bound late (IDispatch) so no interface vtable has to be pinned; every
/// call is guarded and returns null when NLM cannot be queried, so an unavailable
/// service never false-alarms.
/// </summary>
internal static class NetworkListManagerInterop
{
    // CLSID_NetworkListManager (netlistmgr.h).
    private static readonly Guid ClsidNetworkListManager = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    /// <summary>
    /// Current connectivity as a canonical string "internet|connected|flags"
    /// (e.g. "1|1|4103"), or null if the NLM service could not be queried.
    /// </summary>
    public static string? ReadState()
    {
        object? nlm = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidNetworkListManager);
            if (type is null) return null;
            nlm = Activator.CreateInstance(type);
            if (nlm is null) return null;

            // INetworkListManager is a dual interface; late-bind by name.
            dynamic m = nlm;
            bool internet = (bool)m.IsConnectedToInternet;   // VARIANT_BOOL -> bool
            bool connected = (bool)m.IsConnected;            // connected to any network (may be local-only)
            int connectivity = (int)m.GetConnectivity();     // NLM_CONNECTIVITY flags
            return $"{(internet ? 1 : 0)}|{(connected ? 1 : 0)}|{connectivity}";
        }
        catch
        {
            return null;   // NLM unavailable / access denied → no observation, no alarm
        }
        finally
        {
            if (nlm is not null && Marshal.IsComObject(nlm))
                Marshal.FinalReleaseComObject(nlm);
        }
    }

    /// <summary>Whether a canonical state string reports the internet reachable.</summary>
    public static bool IsInternetUp(string state)
        => state.Length > 0 && state[0] == '1';
}
