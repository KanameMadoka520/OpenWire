using System.Collections;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using OpenWire.Core.Models;

namespace OpenWire.Service.Firewall;

/// <summary>
/// Programs the Windows Defender Firewall via the <c>HNetCfg.FwPolicy2</c> COM API
/// (late-bound, so no interop assembly is required). Every rule OpenWire creates is
/// tagged with the <c>OpenWire</c> group and a stable per-app hash so the full rule
/// set can be listed, toggled and cleaned up. Must run elevated.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FirewallManager
{
    private const string Group = "OpenWire";
    private const int ActionBlock = 0;   // NET_FW_ACTION_BLOCK
    private const int DirIn = 1;         // NET_FW_RULE_DIR_IN
    private const int DirOut = 2;        // NET_FW_RULE_DIR_OUT
    private const int ProfileAll = 0x7FFFFFFF;
    private const int ProtocolAny = 256; // NET_FW_IP_PROTOCOL_ANY

    private readonly object _lock = new();

    private static object CreatePolicy()
        => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;

    private static object CreateRule()
        => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!)!;

    /// <summary>Can OpenWire actually talk to the Windows Firewall (elevated + service present)?</summary>
    public bool CanEnforce()
    {
        try
        {
            _ = CreatePolicy();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Tag(string appId)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(appId.ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 4);
    }

    private static string RuleName(string displayName, string appId, int dir)
        => $"OpenWire: {displayName} [{(dir == DirIn ? "In" : "Out")}] #{Tag(appId)}";

    /// <summary>Apply the desired block state for one application (removes then re-adds).</summary>
    public void SetAppBlocked(string exePath, string appId, string displayName, bool blockIn, bool blockOut)
    {
        lock (_lock)
        {
            dynamic policy = CreatePolicy();
            RemoveByTag(policy, Tag(appId));

            if (blockOut) AddBlockRule(policy, RuleName(displayName, appId, DirOut), exePath, appId, DirOut);
            if (blockIn) AddBlockRule(policy, RuleName(displayName, appId, DirIn), exePath, appId, DirIn);
        }
    }

    public void UnblockApp(string appId)
    {
        lock (_lock)
        {
            dynamic policy = CreatePolicy();
            RemoveByTag(policy, Tag(appId));
        }
    }

    private static void AddBlockRule(dynamic policy, string name, string exePath, string appId, int dir)
    {
        dynamic rule = CreateRule();
        rule.Name = name;
        rule.Description = $"OWID={appId}";
        rule.Grouping = Group;
        rule.ApplicationName = exePath;
        rule.Protocol = ProtocolAny;
        rule.Action = ActionBlock;
        rule.Direction = dir;
        rule.Enabled = true;
        rule.Profiles = ProfileAll;
        rule.InterfaceTypes = "All";
        policy.Rules.Add(rule);
    }

    private static void RemoveByTag(dynamic policy, string tag)
    {
        var toRemove = new List<string>();
        foreach (var obj in (IEnumerable)policy.Rules)
        {
            dynamic r = obj;
            string name;
            try { name = r.Name; } catch { continue; }
            if (name is not null && name.StartsWith("OpenWire:", StringComparison.Ordinal) && name.EndsWith("#" + tag, StringComparison.Ordinal))
                toRemove.Add(name);
        }
        foreach (var name in toRemove)
        {
            try { policy.Rules.Remove(name); } catch { /* already gone */ }
        }
    }

    /// <summary>Global lock-down: block every application in both directions (or lift it).</summary>
    public void SetBlockAll(bool on)
    {
        lock (_lock)
        {
            dynamic policy = CreatePolicy();
            TryRemove(policy, "OpenWire: Lockdown [Out] #ALL");
            TryRemove(policy, "OpenWire: Lockdown [In] #ALL");

            if (on)
            {
                AddLockdownRule(policy, "OpenWire: Lockdown [Out] #ALL", DirOut);
                AddLockdownRule(policy, "OpenWire: Lockdown [In] #ALL", DirIn);
            }
        }
    }

    private static void AddLockdownRule(dynamic policy, string name, int dir)
    {
        dynamic rule = CreateRule();
        rule.Name = name;
        rule.Description = "OWID=lockdown";
        rule.Grouping = Group;
        rule.Protocol = ProtocolAny;
        rule.Action = ActionBlock;
        rule.Direction = dir;
        rule.Enabled = true;
        rule.Profiles = ProfileAll;
        rule.InterfaceTypes = "All";
        policy.Rules.Add(rule);
    }

    /// <summary>Enumerate the per-app firewall state OpenWire is currently enforcing.</summary>
    public List<AppFirewallRule> GetAppRules()
    {
        var byApp = new Dictionary<string, AppFirewallRule>(StringComparer.OrdinalIgnoreCase);

        lock (_lock)
        {
            dynamic policy;
            try { policy = CreatePolicy(); }
            catch { return new List<AppFirewallRule>(); }

            foreach (var obj in (IEnumerable)policy.Rules)
            {
                dynamic r = obj;
                string? grouping = TryGet(() => (string)r.Grouping);
                if (!string.Equals(grouping, Group, StringComparison.Ordinal)) continue;

                string desc = TryGet(() => (string)r.Description) ?? string.Empty;
                if (!desc.StartsWith("OWID=", StringComparison.Ordinal)) continue;
                string appId = desc[5..];
                if (appId is "lockdown") continue;

                string path = TryGet(() => (string)r.ApplicationName) ?? string.Empty;
                int dir = TryGet(() => (int)r.Direction);

                if (!byApp.TryGetValue(appId, out var entry))
                {
                    entry = new AppFirewallRule { AppId = appId, ExecutablePath = path, Status = AppFirewallStatus.Blocked };
                    byApp[appId] = entry;
                }
                if (dir == DirIn) entry.BlockIncoming = true;
                if (dir == DirOut) entry.BlockOutgoing = true;
            }
        }

        return byApp.Values.ToList();
    }

    public bool IsBlockAllActive()
    {
        lock (_lock)
        {
            dynamic policy;
            try { policy = CreatePolicy(); }
            catch { return false; }

            foreach (var obj in (IEnumerable)policy.Rules)
            {
                dynamic r = obj;
                string? name = TryGet(() => (string)r.Name);
                if (name == "OpenWire: Lockdown [Out] #ALL") return true;
            }
            return false;
        }
    }

    /// <summary>Remove every rule OpenWire ever created (maintenance action).</summary>
    public int CleanupAllRules()
    {
        lock (_lock)
        {
            dynamic policy;
            try { policy = CreatePolicy(); }
            catch { return 0; }

            var names = new List<string>();
            foreach (var obj in (IEnumerable)policy.Rules)
            {
                dynamic r = obj;
                string? grouping = TryGet(() => (string)r.Grouping);
                if (string.Equals(grouping, Group, StringComparison.Ordinal))
                {
                    string? name = TryGet(() => (string)r.Name);
                    if (name is not null) names.Add(name);
                }
            }
            foreach (var name in names)
            {
                try { policy.Rules.Remove(name); } catch { /* ignore */ }
            }
            return names.Count;
        }
    }

    private static void TryRemove(dynamic policy, string name)
    {
        try { policy.Rules.Remove(name); } catch { /* not present */ }
    }

    private static T? TryGet<T>(Func<T> getter)
    {
        try { return getter(); }
        catch { return default; }
    }
}
