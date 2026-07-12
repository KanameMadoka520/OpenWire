using System.Text.Json.Serialization;

namespace OpenWire.Core.Ipc;

/// <summary>
/// Base type for every message exchanged between the OpenWire engine (service)
/// and the UI over the named pipe. Serialized polymorphically via a "$type"
/// discriminator so a single stream can carry requests, responses and events.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
// --- Requests (UI -> engine) ---
[JsonDerivedType(typeof(HelloRequest), "hello")]
[JsonDerivedType(typeof(GetStatusRequest), "getStatus")]
[JsonDerivedType(typeof(GetGraphRequest), "getGraph")]
[JsonDerivedType(typeof(SubscribeLiveRequest), "subscribeLive")]
[JsonDerivedType(typeof(GetUsageRequest), "getUsage")]
[JsonDerivedType(typeof(GetInsightsRequest), "getInsights")]
[JsonDerivedType(typeof(GetConnectionsRequest), "getConnections")]
[JsonDerivedType(typeof(GetHardwareRequest), "getHardware")]
[JsonDerivedType(typeof(GetFirewallRequest), "getFirewall")]
[JsonDerivedType(typeof(SetFirewallModeRequest), "setFirewallMode")]
[JsonDerivedType(typeof(SetLockdownRequest), "setLockdown")]
[JsonDerivedType(typeof(SetAppBlockedRequest), "setAppBlocked")]
[JsonDerivedType(typeof(SaveFirewallProfileRequest), "saveFwProfile")]
[JsonDerivedType(typeof(DeleteFirewallProfileRequest), "deleteFwProfile")]
[JsonDerivedType(typeof(ActivateFirewallProfileRequest), "activateFwProfile")]
[JsonDerivedType(typeof(ResolveAppDecisionRequest), "resolveAppDecision")]
[JsonDerivedType(typeof(GetAlertsRequest), "getAlerts")]
[JsonDerivedType(typeof(AckAlertRequest), "ackAlert")]
[JsonDerivedType(typeof(GetDevicesRequest), "getDevices")]
[JsonDerivedType(typeof(RenameDeviceRequest), "renameDevice")]
[JsonDerivedType(typeof(ForgetDeviceRequest), "forgetDevice")]
[JsonDerivedType(typeof(GetSettingsRequest), "getSettings")]
[JsonDerivedType(typeof(SetSettingsRequest), "setSettings")]
[JsonDerivedType(typeof(GetBlocklistStatusRequest), "getBlocklistStatus")]
[JsonDerivedType(typeof(RefreshBlocklistsRequest), "refreshBlocklists")]
[JsonDerivedType(typeof(GetGeoIpStatusRequest), "getGeoIpStatus")]
[JsonDerivedType(typeof(UpdateGeoIpRequest), "updateGeoIp")]
[JsonDerivedType(typeof(SetUiActiveRequest), "setUiActive")]
[JsonDerivedType(typeof(GetStorageInfoRequest), "getStorageInfo")]
[JsonDerivedType(typeof(SetStorageLocationRequest), "setStorageLocation")]
[JsonDerivedType(typeof(ClearDataRequest), "clearData")]
// --- Responses (engine -> UI, correlated) ---
[JsonDerivedType(typeof(HelloResponse), "helloResp")]
[JsonDerivedType(typeof(StatusResponse), "statusResp")]
[JsonDerivedType(typeof(GraphResponse), "graphResp")]
[JsonDerivedType(typeof(UsageResponse), "usageResp")]
[JsonDerivedType(typeof(InsightsResponse), "insightsResp")]
[JsonDerivedType(typeof(ConnectionsResponse), "connectionsResp")]
[JsonDerivedType(typeof(HardwareResponse), "hardwareResp")]
[JsonDerivedType(typeof(FirewallResponse), "firewallResp")]
[JsonDerivedType(typeof(AlertsResponse), "alertsResp")]
[JsonDerivedType(typeof(DevicesResponse), "devicesResp")]
[JsonDerivedType(typeof(SettingsResponse), "settingsResp")]
[JsonDerivedType(typeof(BlocklistStatusResponse), "blocklistStatusResp")]
[JsonDerivedType(typeof(GeoIpStatusResponse), "geoIpStatusResp")]
[JsonDerivedType(typeof(StorageInfoResponse), "storageInfoResp")]
[JsonDerivedType(typeof(OkResponse), "okResp")]
[JsonDerivedType(typeof(ErrorResponse), "errorResp")]
// --- Events (engine -> UI, unsolicited) ---
[JsonDerivedType(typeof(LiveTickEvent), "liveTick")]
[JsonDerivedType(typeof(AlertRaisedEvent), "alertRaised")]
[JsonDerivedType(typeof(FirewallPromptEvent), "firewallPrompt")]
[JsonDerivedType(typeof(DeviceChangedEvent), "deviceChanged")]
[JsonDerivedType(typeof(StatusChangedEvent), "statusChanged")]
public abstract class IpcMessage
{
    /// <summary>
    /// Ties a response back to its request. Null on unsolicited events.
    /// </summary>
    public string? CorrelationId { get; set; }
}
