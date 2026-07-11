using System.Collections;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using OpenWire.Core.Models;

namespace OpenWire.Service.Firewall;

/// <summary>
/// Programs Windows Defender Firewall through HNetCfg.FwPolicy2. Managed rules carry an
/// unambiguous owner marker and a 128-bit application tag; display names are never identity.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FirewallManager
{
    private const string Group = "OpenWire.Managed.v1";
    private const string LegacyGroup = "OpenWire";
    private const string OwnerPrefix = "OpenWire.ManagedRule/v1;Tag=";
    private const string LockdownTag = "LOCKDOWN";
    private const int ActionBlock = 0;
    private const int DirIn = 1;
    private const int DirOut = 2;
    private const int ProfileAll = 0x7FFFFFFF;
    private const int ProtocolAny = 256;

    private readonly object _lock = new();

    private static object CreatePolicy()
        => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;

    private static object CreateRule()
        => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!)!;

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
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(appId.ToLowerInvariant()));
        return Convert.ToHexString(hash.AsSpan(0, 16));
    }

    private static string OwnedDescription(string tag) => OwnerPrefix + tag;

    private static string RuleName(string displayName, string tag, int dir, string generation)
        => $"OpenWire: {SanitizeDisplayName(displayName)} [{(dir == DirIn ? "In" : "Out")}] #{tag}.{generation}";

    private static string SanitizeDisplayName(string value)
    {
        string cleaned = new(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (cleaned.Length == 0) cleaned = "Application";
        return cleaned.Length <= 96 ? cleaned : cleaned[..96];
    }

    /// <summary>Atomically replace one application's managed block rules.</summary>
    public void SetAppBlocked(string exePath, string appId, string displayName, bool blockIn, bool blockOut)
    {
        lock (_lock)
        {
            object policy = CreatePolicy();
            if (!blockIn && !blockOut)
            {
                RemoveAppRules(policy, appId, exceptNames: null);
                return;
            }

            string tag = Tag(appId);
            string generation = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            var added = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (blockOut)
                {
                    string name = RuleName(displayName, tag, DirOut, generation);
                    AddBlockRule(policy, name, exePath, tag, DirOut);
                    added.Add(name);
                }
                if (blockIn)
                {
                    string name = RuleName(displayName, tag, DirIn, generation);
                    AddBlockRule(policy, name, exePath, tag, DirIn);
                    added.Add(name);
                }
            }
            catch
            {
                RemoveOwnedNames(policy, added, OwnedDescription(tag));
                throw;
            }

            RemoveAppRules(policy, appId, added);
        }
    }

    public void UnblockApp(string appId)
    {
        lock (_lock)
        {
            object policy = CreatePolicy();
            RemoveAppRules(policy, appId, exceptNames: null);
        }
    }

    public int ClearAppRules()
    {
        lock (_lock)
        {
            object policy = CreatePolicy();
            return RemoveWhere(policy, IsOwnedAppRule, exceptNames: null);
        }
    }

    private static void AddBlockRule(object policyObject, string name, string exePath, string tag, int dir)
    {
        dynamic policy = policyObject;
        dynamic rule = CreateRule();
        rule.Name = name;
        rule.Description = OwnedDescription(tag);
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

    private static void RemoveAppRules(object policy, string appId, HashSet<string>? exceptNames)
    {
        string description = OwnedDescription(Tag(appId));
        string legacyDescription = "OWID=" + appId;
        RemoveWhere(policy, ruleObject =>
        {
            dynamic rule = ruleObject;
            string? grouping = TryGet(() => (string)rule.Grouping);
            string? desc = TryGet(() => (string)rule.Description);
            string? name = TryGet(() => (string)rule.Name);
            return (string.Equals(grouping, Group, StringComparison.Ordinal)
                    && string.Equals(desc, description, StringComparison.Ordinal))
                || (string.Equals(grouping, LegacyGroup, StringComparison.Ordinal)
                    && name?.StartsWith("OpenWire:", StringComparison.Ordinal) == true
                    && string.Equals(desc, legacyDescription, StringComparison.OrdinalIgnoreCase));
        }, exceptNames);
    }

    /// <summary>Global lock-down overlay. Both rules are added before the previous generation is removed.</summary>
    public void SetBlockAll(bool on)
    {
        lock (_lock)
        {
            object policy = CreatePolicy();
            if (!on)
            {
                RemoveWhere(policy, IsOwnedLockdownRule, exceptNames: null);
                return;
            }

            string generation = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            var added = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                string outName = $"OpenWire: Lockdown [Out] #{generation}";
                AddLockdownRule(policy, outName, DirOut);
                added.Add(outName);
                string inName = $"OpenWire: Lockdown [In] #{generation}";
                AddLockdownRule(policy, inName, DirIn);
                added.Add(inName);
            }
            catch
            {
                RemoveOwnedNames(policy, added, OwnedDescription(LockdownTag));
                throw;
            }

            RemoveWhere(policy, IsOwnedLockdownRule, added);
        }
    }

    private static void AddLockdownRule(object policyObject, string name, int dir)
    {
        dynamic policy = policyObject;
        dynamic rule = CreateRule();
        rule.Name = name;
        rule.Description = OwnedDescription(LockdownTag);
        rule.Grouping = Group;
        rule.Protocol = ProtocolAny;
        rule.Action = ActionBlock;
        rule.Direction = dir;
        rule.Enabled = true;
        rule.Profiles = ProfileAll;
        rule.InterfaceTypes = "All";
        policy.Rules.Add(rule);
    }

    public List<AppFirewallRule> GetAppRules()
    {
        var byApp = new Dictionary<string, AppFirewallRule>(StringComparer.OrdinalIgnoreCase);

        lock (_lock)
        {
            object policyObject;
            try { policyObject = CreatePolicy(); }
            catch { return new List<AppFirewallRule>(); }
            dynamic policy = policyObject;

            foreach (var obj in (IEnumerable)policy.Rules)
            {
                dynamic rule = obj;
                if (!TryGetOwnedAppId((object)rule, out string appId, out string path)) continue;
                if (!IsEffectiveBlockRule((object)rule)) continue;
                int dir = TryGet(() => (int)rule.Direction);

                if (!byApp.TryGetValue(appId, out var entry))
                {
                    entry = new AppFirewallRule
                    {
                        AppId = appId,
                        ExecutablePath = path,
                        Status = AppFirewallStatus.Blocked,
                    };
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
            object policyObject;
            try { policyObject = CreatePolicy(); }
            catch { return false; }
            dynamic policy = policyObject;

            bool inbound = false, outbound = false;
            foreach (var obj in (IEnumerable)policy.Rules)
            {
                dynamic rule = obj;
                if (!IsOwnedLockdownRule((object)rule) || !IsEffectiveBlockRule((object)rule)) continue;
                int dir = TryGet(() => (int)rule.Direction);
                if (dir == DirIn) inbound = true;
                if (dir == DirOut) outbound = true;
            }
            return inbound && outbound;
        }
    }

    public int CleanupAllRules()
    {
        lock (_lock)
        {
            object policy;
            try { policy = CreatePolicy(); }
            catch { return 0; }
            return RemoveWhere(policy, rule => IsOwnedAppRule(rule) || IsOwnedLockdownRule(rule), exceptNames: null);
        }
    }

    private static bool TryGetOwnedAppId(object ruleObject, out string appId, out string path)
    {
        dynamic rule = ruleObject;
        appId = string.Empty;
        path = TryGet(() => (string)rule.ApplicationName) ?? string.Empty;
        string? grouping = TryGet(() => (string)rule.Grouping);
        string desc = TryGet(() => (string)rule.Description) ?? string.Empty;
        string name = TryGet(() => (string)rule.Name) ?? string.Empty;

        if (string.Equals(grouping, Group, StringComparison.Ordinal)
            && desc.StartsWith(OwnerPrefix, StringComparison.Ordinal)
            && !desc.Equals(OwnedDescription(LockdownTag), StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(path))
        {
            appId = path.ToLowerInvariant();
            return desc.Equals(OwnedDescription(Tag(appId)), StringComparison.Ordinal);
        }

        if (string.Equals(grouping, LegacyGroup, StringComparison.Ordinal)
            && name.StartsWith("OpenWire:", StringComparison.Ordinal)
            && desc.StartsWith("OWID=", StringComparison.Ordinal)
            && !desc.Equals("OWID=lockdown", StringComparison.OrdinalIgnoreCase))
        {
            appId = desc[5..];
            return appId.Length > 0;
        }

        return false;
    }

    private static bool IsOwnedAppRule(object rule)
        => TryGetOwnedAppId(rule, out _, out _);

    private static bool IsOwnedLockdownRule(object ruleObject)
    {
        dynamic rule = ruleObject;
        string? grouping = TryGet(() => (string)rule.Grouping);
        string desc = TryGet(() => (string)rule.Description) ?? string.Empty;
        string name = TryGet(() => (string)rule.Name) ?? string.Empty;
        return (string.Equals(grouping, Group, StringComparison.Ordinal)
                && desc.Equals(OwnedDescription(LockdownTag), StringComparison.Ordinal)
                && name.StartsWith("OpenWire: Lockdown ", StringComparison.Ordinal))
            || (string.Equals(grouping, LegacyGroup, StringComparison.Ordinal)
                && desc.Equals("OWID=lockdown", StringComparison.OrdinalIgnoreCase)
                && name is "OpenWire: Lockdown [Out] #ALL" or "OpenWire: Lockdown [In] #ALL");
    }

    private static bool IsEffectiveBlockRule(object ruleObject)
    {
        dynamic rule = ruleObject;
        bool enabled = TryGet(() => (bool)rule.Enabled);
        int action = TryGet(() => (int)rule.Action);
        int profiles = TryGet(() => (int)rule.Profiles);
        int direction = TryGet(() => (int)rule.Direction);
        return enabled && action == ActionBlock && profiles != 0 && direction is DirIn or DirOut;
    }

    private static int RemoveWhere(object policyObject, Func<object, bool> predicate, HashSet<string>? exceptNames)
    {
        dynamic policy = policyObject;
        var names = new List<string>();
        foreach (var obj in (IEnumerable)policy.Rules)
        {
            dynamic rule = obj;
            string? name = TryGet(() => (string)rule.Name);
            if (name is null || exceptNames?.Contains(name) == true) continue;
            if (predicate((object)rule)) names.Add(name);
        }
        foreach (string name in names)
        {
            try { policy.Rules.Remove(name); } catch { }
        }
        return names.Count;
    }

    private static void RemoveOwnedNames(object policy, IEnumerable<string> names, string description)
    {
        var wanted = names.ToHashSet(StringComparer.Ordinal);
        RemoveWhere(policy, ruleObject =>
        {
            dynamic rule = ruleObject;
            string? grouping = TryGet(() => (string)rule.Grouping);
            string? desc = TryGet(() => (string)rule.Description);
            string? name = TryGet(() => (string)rule.Name);
            return string.Equals(grouping, Group, StringComparison.Ordinal)
                && string.Equals(desc, description, StringComparison.Ordinal)
                && name is not null
                && wanted.Contains(name);
        }, exceptNames: null);
    }

    private static T? TryGet<T>(Func<T> getter)
    {
        try { return getter(); }
        catch { return default; }
    }
}
