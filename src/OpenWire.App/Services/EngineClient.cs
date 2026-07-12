using System.Collections.Concurrent;
using System.IO;
using System.Security;
using System.Windows.Threading;
using OpenWire.Core.Ipc;
using OpenWire.Core.Models;

namespace OpenWire.App.Services;

/// <summary>
/// UI-side client for the OpenWire engine. Maintains a named-pipe connection with
/// auto-reconnect, correlates request/response messages, and raises the engine's
/// streamed events on the UI dispatcher.
/// </summary>
public sealed class EngineClient : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcMessage>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private volatile IpcChannel? _channel;
    private volatile bool _supportsHardwareDelta;

    public bool IsConnected { get; private set; }

    public event Action<bool>? ConnectionChanged;
    public event Action<LiveTickEvent>? LiveTick;
    public event Action<AlertRaisedEvent>? AlertRaised;
    public event Action<DeviceChangedEvent>? DeviceChanged;
    public event Action<StatusChangedEvent>? StatusChanged;
    public event Action<FirewallPromptEvent>? FirewallPrompt;

    public EngineClient(Dispatcher ui) => _ui = ui;

    public void Start() => _ = Task.Run(() => ConnectLoopAsync(_cts.Token));

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            System.IO.Pipes.NamedPipeClientStream? pipe = null;
            IpcChannel? channel = null;
            try
            {
                pipe = IpcTransport.CreateClientStream();
                await pipe.ConnectAsync(2000, ct).ConfigureAwait(false);
                VerifyServer(pipe);
                channel = new IpcChannel(pipe);
                string engineVersion = await PerformHandshakeAsync(channel, ct).ConfigureAwait(false);
                _supportsHardwareDelta = Version.TryParse(engineVersion, out var parsed)
                    && parsed.CompareTo(new Version(0, 1, 1)) >= 0;
                _channel = channel;
                SetConnected(true);

                // Subscribe to the live event stream for this connection.
                await channel.SendAsync(new SubscribeLiveRequest { Subscribe = true }, ct).ConfigureAwait(false);

                await ReceiveLoopAsync(channel, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch { /* engine not up yet */ }
            finally
            {
                _channel = null;
                _supportsHardwareDelta = false;
                // Release the OS pipe handle instead of leaking it to GC finalization on
                // every reconnect cycle. Disposing the channel disposes the pipe; if the
                // connect failed before wrapping, dispose the pipe directly.
                if (channel is not null) channel.Dispose();
                else pipe?.Dispose();
                SetConnected(false);
                FailPending();
            }

            try { await Task.Delay(1500, ct).ConfigureAwait(false); } catch { break; }
        }
    }

    private static void VerifyServer(System.IO.Pipes.NamedPipeClientStream pipe)
    {
        string? expectedPath = EngineLauncher.LocateServiceExe();
        if (expectedPath is null
            || !IpcPeerIdentity.TryGetServerProcessInfo(pipe, out var server)
            || !server.IsElevated
            || !IpcPeerIdentity.PathsEqual(server.ImagePath, expectedPath))
        {
            throw new SecurityException("The named-pipe server is not the trusted elevated OpenWire engine.");
        }
    }

    private static async Task<string> PerformHandshakeAsync(IpcChannel channel, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        string id = Guid.NewGuid().ToString("N");
        await channel.SendAsync(
            new HelloRequest { CorrelationId = id, ClientVersion = "0.1.1" },
            timeout.Token).ConfigureAwait(false);
        var response = await channel.ReceiveAsync(timeout.Token).ConfigureAwait(false);
        if (response is not HelloResponse hello
            || !string.Equals(hello.CorrelationId, id, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(hello.EngineVersion))
        {
            throw new InvalidDataException("The engine did not complete the IPC handshake.");
        }
        return hello.EngineVersion;
    }

    private async Task ReceiveLoopAsync(IpcChannel channel, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var msg = await channel.ReceiveAsync(ct).ConfigureAwait(false);
            if (msg is null) break;

            if (msg.CorrelationId is { } id && _pending.TryRemove(id, out var tcs))
                tcs.TrySetResult(msg);
            else
                DispatchEvent(msg);
        }
    }

    private void DispatchEvent(IpcMessage msg)
    {
        _ui.BeginInvoke(() =>
        {
            switch (msg)
            {
                case LiveTickEvent e: LiveTick?.Invoke(e); break;
                case AlertRaisedEvent e: AlertRaised?.Invoke(e); break;
                case DeviceChangedEvent e: DeviceChanged?.Invoke(e); break;
                case StatusChangedEvent e: StatusChanged?.Invoke(e); break;
                case FirewallPromptEvent e: FirewallPrompt?.Invoke(e); break;
            }
        });
    }

    private void SetConnected(bool value)
    {
        if (IsConnected == value) return;
        IsConnected = value;
        _ui.BeginInvoke(() => ConnectionChanged?.Invoke(value));
    }

    private void FailPending()
    {
        foreach (var kv in _pending)
            kv.Value.TrySetException(new IOException("Engine disconnected."));
        _pending.Clear();
    }

    private async Task<T> RequestAsync<T>(IpcMessage request, TimeSpan? timeout = null, CancellationToken ct = default) where T : IpcMessage
    {
        var channel = _channel ?? throw new InvalidOperationException("Engine not connected.");
        var id = Guid.NewGuid().ToString("N");
        request.CorrelationId = id;

        var tcs = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
        await using var reg = timeoutCts.Token.Register(() =>
        {
            if (_pending.TryRemove(id, out var t)) t.TrySetCanceled();
        });

        await channel.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        var response = await tcs.Task.ConfigureAwait(false);

        if (response is ErrorResponse err) throw new InvalidOperationException(err.Error);
        return (T)response;
    }

    // ---- typed API ----

    public Task<HelloResponse> HelloAsync() => RequestAsync<HelloResponse>(new HelloRequest { ClientVersion = "0.1.1" });
    public Task<StatusResponse> GetStatusAsync() => RequestAsync<StatusResponse>(new GetStatusRequest());
    public Task<GraphResponse> GetGraphAsync(GraphRange range) => RequestAsync<GraphResponse>(new GetGraphRequest { Range = range });
    public Task<UsageResponse> GetUsageAsync(GraphRange range, UsageGroupBy groupBy) => RequestAsync<UsageResponse>(new GetUsageRequest { Range = range, GroupBy = groupBy });
    public Task<InsightsResponse> GetInsightsAsync(GraphRange range) => RequestAsync<InsightsResponse>(new GetInsightsRequest { Range = range });

    /// <summary>Analytics report over a custom [from, to] window (unix seconds).</summary>
    public Task<InsightsResponse> GetInsightsAsync(long fromUnix, long toUnix)
        => RequestAsync<InsightsResponse>(new GetInsightsRequest { FromUnix = fromUnix, ToUnix = toUnix });
    public Task<ConnectionsResponse> GetConnectionsAsync() => RequestAsync<ConnectionsResponse>(new GetConnectionsRequest());
    public Task<HardwareResponse> GetHardwareAsync(
        string historyStreamId,
        long afterHistorySequence,
        bool includeDetails)
    {
        var request = new GetHardwareRequest();
        if (_supportsHardwareDelta)
        {
            request.HistoryStreamId = historyStreamId;
            request.AfterHistorySequence = afterHistorySequence;
            request.OmitDetails = !includeDetails;
        }
        return RequestAsync<HardwareResponse>(request);
    }
    public Task<StorageInfoResponse> GetStorageInfoAsync() => RequestAsync<StorageInfoResponse>(new GetStorageInfoRequest());
    public Task<StorageInfoResponse> SetStorageLocationAsync(string dir) => RequestAsync<StorageInfoResponse>(new SetStorageLocationRequest { NewDirectory = dir });
    public Task<StorageInfoResponse> ClearDataAsync(ClearDataMode mode) => RequestAsync<StorageInfoResponse>(new ClearDataRequest { Mode = mode });
    public Task<FirewallResponse> GetFirewallAsync() => RequestAsync<FirewallResponse>(new GetFirewallRequest());
    public Task<OkResponse> SetFirewallModeAsync(FirewallMode mode) => RequestAsync<OkResponse>(new SetFirewallModeRequest { Mode = mode });
    public Task<OkResponse> SetLockdownAsync(bool on) => RequestAsync<OkResponse>(new SetLockdownRequest { On = on });
    public Task<OkResponse> SetAppBlockedAsync(string appId, string path, bool blockIn, bool blockOut)
        => RequestAsync<OkResponse>(new SetAppBlockedRequest { AppId = appId, ExecutablePath = path, BlockIncoming = blockIn, BlockOutgoing = blockOut });
    public Task<OkResponse> ResolveAppDecisionAsync(string appId, bool allow) => RequestAsync<OkResponse>(new ResolveAppDecisionRequest { AppId = appId, Allow = allow });
    public Task<OkResponse> SaveFirewallProfileAsync(FirewallProfile profile) => RequestAsync<OkResponse>(new SaveFirewallProfileRequest { Profile = profile });
    public Task<OkResponse> DeleteFirewallProfileAsync(string name) => RequestAsync<OkResponse>(new DeleteFirewallProfileRequest { Name = name });
    public Task<OkResponse> ActivateFirewallProfileAsync(string name) => RequestAsync<OkResponse>(new ActivateFirewallProfileRequest { Name = name });
    public Task<AlertsResponse> GetAlertsAsync(int limit = 200) => RequestAsync<AlertsResponse>(new GetAlertsRequest { Limit = limit });
    public Task<OkResponse> AckAlertAsync(long id, bool all = false) => RequestAsync<OkResponse>(new AckAlertRequest { AlertId = id, All = all });
    public Task<DevicesResponse> GetDevicesAsync(bool rescan = false) => RequestAsync<DevicesResponse>(new GetDevicesRequest { Rescan = rescan });
    public Task<OkResponse> RenameDeviceAsync(string id, string name) => RequestAsync<OkResponse>(new RenameDeviceRequest { DeviceId = id, Name = name });
    public Task<OkResponse> ForgetDeviceAsync(string id) => RequestAsync<OkResponse>(new ForgetDeviceRequest { DeviceId = id });
    public Task<SettingsResponse> GetSettingsAsync() => RequestAsync<SettingsResponse>(new GetSettingsRequest());
    public Task<OkResponse> SetSettingsAsync(AppSettings settings) => RequestAsync<OkResponse>(new SetSettingsRequest { Settings = settings });
    /// <summary>Fire-and-forget send — no correlated reply is awaited (used for the UI-active hint).</summary>
    public void Send(IpcMessage message)
    {
        var ch = _channel;
        if (ch is null) return;
        _ = ch.SendAsync(message, _cts.Token);
    }

    public void SetUiActive(bool active) => Send(new SetUiActiveRequest { Active = active });
    public Task<GeoIpStatusResponse> GetGeoIpStatusAsync() => RequestAsync<GeoIpStatusResponse>(new GetGeoIpStatusRequest());
    public Task<BlocklistStatusResponse> GetBlocklistStatusAsync() => RequestAsync<BlocklistStatusResponse>(new GetBlocklistStatusRequest());
    public Task<OkResponse> RefreshBlocklistsAsync(string? listId = null) => RequestAsync<OkResponse>(new RefreshBlocklistsRequest { ListId = listId });

    /// <summary>Trigger an on-demand GeoIP database download (a longer timeout than usual — it
    /// fetches a few MB over the network).</summary>
    public Task<GeoIpStatusResponse> UpdateGeoIpAsync() =>
        RequestAsync<GeoIpStatusResponse>(new UpdateGeoIpRequest(), TimeSpan.FromSeconds(90));

    public void Dispose()
    {
        _cts.Cancel();
        _channel?.Dispose();
        _cts.Dispose();
    }
}
