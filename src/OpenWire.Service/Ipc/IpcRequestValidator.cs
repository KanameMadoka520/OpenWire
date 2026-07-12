using OpenWire.Core.Ipc;
using OpenWire.Core.Models;

namespace OpenWire.Service.Ipc;

/// <summary>Rejects malformed or resource-amplifying requests before they reach engine state.</summary>
internal static class IpcRequestValidator
{
    private const int MaxShortText = 64;
    private const int MaxPath = 32_767;
    private const int MaxProfiles = 32;
    private const int MaxBlockedAppsPerProfile = 5_000;
    private const int MaxBlocklists = 32;
    private const int MaxQuotas = 500;

    public static bool TryValidate(IpcMessage message, out string error)
    {
        error = string.Empty;
        switch (message)
        {
            case HelloRequest hello:
                return Text(hello.ClientVersion, MaxShortText, allowEmpty: true, "clientVersion", out error);

            case GetStatusRequest or GetConnectionsRequest or GetFirewallRequest
                or GetSettingsRequest or GetGeoIpStatusRequest or UpdateGeoIpRequest
                or GetStorageInfoRequest or GetBlocklistStatusRequest:
                return true;

            case RefreshBlocklistsRequest refresh:
                return refresh.ListId is null
                    || Text(refresh.ListId, MaxShortText, allowEmpty: false, "listId", out error);

            case GetHardwareRequest hardware:
                if (hardware.AfterHistorySequence < 0)
                    return Fail("Hardware history sequence cannot be negative.", out error);
                return hardware.HistoryStreamId is null
                    || Text(hardware.HistoryStreamId, MaxShortText, allowEmpty: true,
                        "historyStreamId", out error);

            case GetGraphRequest graph:
                return Defined(graph.Range, "range", out error);

            case SubscribeLiveRequest:
                return true;

            case GetUsageRequest usage:
                return Defined(usage.Range, "range", out error)
                    && Defined(usage.GroupBy, "groupBy", out error);

            case GetInsightsRequest insights:
                if (!Defined(insights.Range, "range", out error)) return false;
                if (insights.FromUnix == 0 && insights.ToUnix == 0) return true;
                if (insights.FromUnix < 0 || insights.ToUnix <= insights.FromUnix)
                    return Fail("Custom insight bounds must be a positive, increasing interval.", out error);
                if (insights.ToUnix - insights.FromUnix > TimeSpan.FromDays(3650).TotalSeconds)
                    return Fail("Custom insight range is too large.", out error);
                return true;

            case SetFirewallModeRequest firewallMode:
                return Defined(firewallMode.Mode, "mode", out error);

            case SetLockdownRequest lockdown:
                return lockdown.DurationSeconds is >= 0 and <= 86_400
                    || Fail("Lock-down duration must be between 0 and 86400 seconds.", out error);

            case SetAppBlockedRequest app:
                if (!Text(app.AppId, MaxPath, allowEmpty: false, "appId", out error)) return false;
                if (!Text(app.ExecutablePath, MaxPath, allowEmpty: true, "executablePath", out error)) return false;
                if ((app.BlockIncoming || app.BlockOutgoing) && string.IsNullOrWhiteSpace(app.ExecutablePath))
                    return Fail("A full executable path is required when creating a block rule.", out error);
                if (!string.IsNullOrEmpty(app.ExecutablePath) && !Path.IsPathFullyQualified(app.ExecutablePath))
                    return Fail("Executable path must be fully qualified.", out error);
                return true;

            case SaveFirewallProfileRequest save:
                return ValidateProfile(save.Profile, out error);

            case DeleteFirewallProfileRequest delete:
                return Text(delete.Name, MaxShortText, allowEmpty: false, "name", out error);

            case ActivateFirewallProfileRequest activate:
                return Text(activate.Name, MaxShortText, allowEmpty: false, "name", out error);

            case ResolveAppDecisionRequest decision:
                return Text(decision.AppId, MaxPath, allowEmpty: false, "appId", out error);

            case GetAlertsRequest alerts:
                return alerts.Limit is >= 1 and <= 1000
                    || Fail("Alert limit must be between 1 and 1000.", out error);

            case AckAlertRequest ack:
                return ack.All || ack.AlertId > 0
                    || Fail("A positive alert id is required.", out error);

            case GetDevicesRequest:
                return true;

            case RenameDeviceRequest rename:
                return Text(rename.DeviceId, 256, allowEmpty: false, "deviceId", out error)
                    && Text(rename.Name, MaxShortText, allowEmpty: false, "name", out error);

            case ForgetDeviceRequest forget:
                return Text(forget.DeviceId, 256, allowEmpty: false, "deviceId", out error);

            case SetStorageLocationRequest storage:
                return Text(storage.NewDirectory, 1024, allowEmpty: false, "newDirectory", out error);

            case ClearDataRequest clear:
                return Defined(clear.Mode, "mode", out error);

            case SetSettingsRequest settings:
                return ValidateSettings(settings.Settings, out error);

            case SetUiActiveRequest:
                return true;

            default:
                return Fail("This message type is not accepted from IPC clients.", out error);
        }
    }

    private static bool ValidateSettings(AppSettings? settings, out string error)
    {
        if (settings is null) return Fail("Settings are required.", out error);
        if (!Defined(settings.FirewallMode, "firewallMode", out error)) return false;
        if (!Text(settings.Theme, 32, allowEmpty: false, "theme", out error)) return false;
        if (!Text(settings.ActiveProfile, MaxShortText, allowEmpty: false, "activeProfile", out error)) return false;
        if (settings.HistoryRetentionDays is < 0 or > 3650)
            return Fail("History retention must be between 0 and 3650 days.", out error);
        if (!Text(settings.VirusTotalApiKey, 1024, allowEmpty: true, "virusTotalApiKey", out error)) return false;
        if (settings.GeoIpLastUpdateUnix < 0) return Fail("GeoIP update timestamp cannot be negative.", out error);

        if (settings.DataPlan is null) return Fail("Data plan settings are required.", out error);
        if (settings.DataPlan.LimitBytes < 0 || settings.DataPlan.UsedBytes < 0)
            return Fail("Data plan byte values cannot be negative.", out error);
        if (settings.DataPlan.BillingCycleStartDay is < 1 or > 28)
            return Fail("Billing cycle day must be between 1 and 28.", out error);
        if (!double.IsFinite(settings.DataPlan.WarnAtFraction)
            || settings.DataPlan.WarnAtFraction is < 0.1 or > 1.0)
            return Fail("Data plan warning fraction must be between 0.1 and 1.0.", out error);

        if (settings.FirewallProfiles is null
            || settings.FirewallProfiles.Count is < 1 or > MaxProfiles)
            return Fail($"Firewall profiles must contain between 1 and {MaxProfiles} entries.", out error);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in settings.FirewallProfiles)
        {
            if (!ValidateProfile(profile, out error)) return false;
            if (!names.Add(profile.Name)) return Fail("Firewall profile names must be unique.", out error);
        }
        if (!names.Contains("Default")) return Fail("The Default firewall profile is required.", out error);
        if (!names.Contains(settings.ActiveProfile)) return Fail("The active firewall profile does not exist.", out error);

        if (settings.EnabledAlerts is null) return Fail("Alert settings are required.", out error);
        foreach (var kind in settings.EnabledAlerts.Keys)
            if (!Enum.IsDefined(typeof(AlertKind), kind)) return Fail("Alert settings contain an unknown kind.", out error);

        if (settings.Blocklists is null || settings.Blocklists.Count > MaxBlocklists)
            return Fail($"At most {MaxBlocklists} blocklist subscriptions are allowed.", out error);
        var listIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in settings.Blocklists)
        {
            if (list is null) return Fail("Blocklist entries cannot be null.", out error);
            if (!Text(list.Id, MaxShortText, allowEmpty: false, "blocklist.id", out error)) return false;
            if (!Text(list.Name, MaxShortText, allowEmpty: false, "blocklist.name", out error)) return false;
            if (!Text(list.Url, 2048, allowEmpty: false, "blocklist.url", out error)) return false;
            // Require HTTPS: the list body is fed into firewall block rules when enforcement is on,
            // so a plaintext transport would let an on-path attacker inject destinations to cut.
            if (!Uri.TryCreate(list.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return Fail("Blocklist URLs must be absolute https addresses.", out error);
            if (!listIds.Add(list.Id)) return Fail("Blocklist ids must be unique.", out error);
        }

        if (settings.AppQuotas is null || settings.AppQuotas.Count > MaxQuotas)
            return Fail($"At most {MaxQuotas} app quotas are allowed.", out error);
        foreach (var quota in settings.AppQuotas)
        {
            if (quota is null) return Fail("Quota entries cannot be null.", out error);
            if (!Text(quota.AppId, MaxPath, allowEmpty: false, "quota.appId", out error)) return false;
            if (!Text(quota.ExecutablePath, MaxPath, allowEmpty: true, "quota.executablePath", out error)) return false;
            if (!Text(quota.AppName, MaxShortText, allowEmpty: true, "quota.appName", out error)) return false;
            if (quota.LimitBytes <= 0) return Fail("Quota limit must be positive.", out error);
            if (!Defined(quota.Period, "quota.period", out error)) return false;
            if (quota.AutoBlock && !string.IsNullOrEmpty(quota.ExecutablePath)
                && !Path.IsPathFullyQualified(quota.ExecutablePath))
                return Fail("Quota executable path must be fully qualified when auto-block is on.", out error);
        }

        return true;
    }

    private static bool ValidateProfile(FirewallProfile? profile, out string error)
    {
        if (profile is null) return Fail("Firewall profile is required.", out error);
        if (!Text(profile.Name, MaxShortText, allowEmpty: false, "profile.name", out error)) return false;
        if (!Text(profile.AutoActivateOnNetwork, 512, allowEmpty: true, "profile.autoActivateOnNetwork", out error)) return false;
        if (!Text(profile.NetworkLabel, 256, allowEmpty: true, "profile.networkLabel", out error)) return false;
        if (!Defined(profile.Mode, "profile.mode", out error)) return false;
        if (profile.BlockedAppIds is null || profile.BlockedAppIds.Count > MaxBlockedAppsPerProfile)
            return Fail($"A profile may contain at most {MaxBlockedAppsPerProfile} blocked apps.", out error);
        foreach (string appId in profile.BlockedAppIds)
            if (!Text(appId, MaxPath, allowEmpty: false, "profile.blockedAppIds", out error)) return false;
        return true;
    }

    private static bool Defined<TEnum>(TEnum value, string field, out string error)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value)) return Fail($"{field} contains an unknown value.", out error);
        error = string.Empty;
        return true;
    }

    private static bool Text(string? value, int max, bool allowEmpty, string field, out string error)
    {
        if (value is null) return Fail($"{field} cannot be null.", out error);
        if (value.Length > max) return Fail($"{field} exceeds {max} characters.", out error);
        if (!allowEmpty && string.IsNullOrWhiteSpace(value)) return Fail($"{field} is required.", out error);
        if (value.Any(char.IsControl)) return Fail($"{field} contains control characters.", out error);
        error = string.Empty;
        return true;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}
