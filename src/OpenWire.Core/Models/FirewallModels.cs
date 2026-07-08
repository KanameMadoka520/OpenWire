namespace OpenWire.Core.Models;

/// <summary>Overall firewall status reported by the engine.</summary>
public sealed class FirewallStatus
{
    public FirewallMode Mode { get; set; } = FirewallMode.Off;

    /// <summary>Name of the active firewall profile.</summary>
    public string ActiveProfile { get; set; } = "Default";

    /// <summary>Friendly name of the currently connected network (SSID / adapter).</summary>
    public string NetworkName { get; set; } = string.Empty;

    /// <summary>Opaque fingerprint of the current network, for binding a profile to it.</summary>
    public string NetworkFingerprint { get; set; } = string.Empty;

    public int BlockedAppCount { get; set; }
    public int PendingAppCount { get; set; }

    /// <summary>True if the OpenWire engine is able to write Windows Firewall rules.</summary>
    public bool CanEnforce { get; set; }

    /// <summary>True while a global lock-down (block-all) rule is engaged.</summary>
    public bool LockdownActive { get; set; }
}

/// <summary>A per-application firewall rule / decision.</summary>
public sealed class AppFirewallRule
{
    public string AppId { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;

    public AppFirewallStatus Status { get; set; } = AppFirewallStatus.Allowed;

    public bool BlockIncoming { get; set; }
    public bool BlockOutgoing { get; set; }

    /// <summary>Profile this rule belongs to.</summary>
    public string Profile { get; set; } = "Default";

    public DateTimeOffset Updated { get; set; }
}

/// <summary>
/// A named firewall profile (e.g. "Home", "Work", "Public"). Rules are scoped to a
/// profile so the active set can switch automatically with the connected network.
/// </summary>
public sealed class FirewallProfile
{
    public string Name { get; set; } = "Default";

    /// <summary>Opaque fingerprint of the network (SSID or gateway MAC) that
    /// auto-activates this profile. Empty = never auto-activate.</summary>
    public string AutoActivateOnNetwork { get; set; } = string.Empty;

    /// <summary>Friendly label of the network captured when the profile was created.</summary>
    public string NetworkLabel { get; set; } = string.Empty;

    public FirewallMode Mode { get; set; } = FirewallMode.Off;

    /// <summary>App ids blocked while this profile is active (scoped rule set).</summary>
    public List<string> BlockedAppIds { get; set; } = new();

    public bool IsActive { get; set; }
}
