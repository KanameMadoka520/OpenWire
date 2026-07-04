namespace OpenWire.Core.Models;

/// <summary>Overall firewall status reported by the engine.</summary>
public sealed class FirewallStatus
{
    public FirewallMode Mode { get; set; } = FirewallMode.Off;

    /// <summary>Name of the active firewall profile.</summary>
    public string ActiveProfile { get; set; } = "Default";

    public int BlockedAppCount { get; set; }
    public int PendingAppCount { get; set; }

    /// <summary>True if the OpenWire engine is able to write Windows Firewall rules.</summary>
    public bool CanEnforce { get; set; }
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

    /// <summary>Optional network (SSID / gateway MAC) that auto-activates this profile.</summary>
    public string AutoActivateOnNetwork { get; set; } = string.Empty;

    public FirewallMode Mode { get; set; } = FirewallMode.Off;

    public bool IsActive { get; set; }
}
