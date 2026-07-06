using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Threading.Channels;
using OpenWire.Core.Ipc;
using OpenWire.Service.Engine;

namespace OpenWire.Service.Ipc;

/// <summary>
/// Named-pipe server that exposes the engine to one or more UI clients. Each
/// connection has an independent inbound read loop and a decoupled outbound writer
/// so a slow/large response can never block the reading of further requests
/// (which would otherwise deadlock the full-duplex pipe).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IpcServer : IAsyncDisposable
{
    private sealed class Client
    {
        public required IpcChannel Channel;
        public volatile bool Subscribed;
        public CancellationTokenSource? Cts;
        public readonly Channel<IpcMessage> Outbound =
            System.Threading.Channels.Channel.CreateBounded<IpcMessage>(
                new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        public void Enqueue(IpcMessage m) => Outbound.Writer.TryWrite(m);
    }

    private readonly MonitorEngine _engine;
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public IpcServer(MonitorEngine engine)
    {
        _engine = engine;
        _engine.Events += OnEngineEvent;
    }

    public void Start(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var server = IpcTransport.CreateServerStream();
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                _ = HandleClientAsync(server, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[IPC] accept error: {ex.Message}");
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(System.IO.Pipes.NamedPipeServerStream server, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var channel = new IpcChannel(server);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var client = new Client { Channel = channel, Cts = linked };
        _clients[id] = client;

        var token = linked.Token;
        var writer = Task.Run(() => WriteLoopAsync(client, token), token);

        try
        {
            while (!token.IsCancellationRequested)
            {
                var request = await channel.ReceiveAsync(token).ConfigureAwait(false);
                if (request is null) break;

                IpcMessage? response = Dispatch(request, client, token);
                if (response is not null)
                {
                    response.CorrelationId = request.CorrelationId;
                    client.Enqueue(response);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[IPC] client {id:N} dropped: {ex.Message}");
        }
        finally
        {
            _clients.TryRemove(id, out _);
            linked.Cancel();                       // stop the writer if the reader ended first
            client.Outbound.Writer.TryComplete();
            try { await writer.ConfigureAwait(false); } catch { }
            channel.Dispose();
        }
    }

    /// <summary>Drains a client's outbound queue to its pipe, one message at a time.</summary>
    private static async Task WriteLoopAsync(Client client, CancellationToken ct)
    {
        try
        {
            await foreach (var msg in client.Outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                await client.Channel.SendAsync(msg, ct).ConfigureAwait(false);
        }
        catch { /* connection gone / send failed */ }
        finally
        {
            // If the writer stops (a send threw, or we were cancelled), tear down the
            // whole client so the read loop unblocks and the client sees EOF and
            // reconnects — instead of a live read pipe wedged behind a dead writer.
            try { client.Cts?.Cancel(); } catch { }
        }
    }

    private IpcMessage? Dispatch(IpcMessage request, Client client, CancellationToken ct)
    {
        try
        {
            switch (request)
            {
                case HelloRequest:
                    return new HelloResponse
                    {
                        EngineVersion = _engine.EngineVersion,
                        MachineName = Environment.MachineName,
                        CanEnforceFirewall = _engine.CanEnforceFirewall,
                        GeoIpAvailable = _engine.GeoIpAvailable,
                    };

                case GetStatusRequest:
                    return new StatusResponse { Status = _engine.GetStatus() };

                case GetGraphRequest g:
                    return new GraphResponse { Series = _engine.GetGraph(g.Range) };

                case SubscribeLiveRequest sub:
                    client.Subscribed = sub.Subscribe;
                    return new OkResponse();

                case GetUsageRequest u:
                    return _engine.GetUsage(u.Range, u.GroupBy);

                case GetInsightsRequest gi:
                    return new InsightsResponse { Report = _engine.GetInsights(gi.Range, gi.FromUnix, gi.ToUnix) };

                case GetConnectionsRequest:
                    return new ConnectionsResponse { Connections = _engine.GetConnections() };

                case GetHardwareRequest:
                    return new HardwareResponse { Hardware = _engine.GetHardware() };

                case GetFirewallRequest:
                {
                    var (status, rules, profiles) = _engine.GetFirewall();
                    return new FirewallResponse { Status = status, Rules = rules, Profiles = profiles };
                }

                case SetFirewallModeRequest fm:
                    _engine.SetFirewallMode(fm.Mode);
                    return new OkResponse();

                case SetAppBlockedRequest ab:
                    _engine.SetAppBlocked(ab.AppId, ab.ExecutablePath, ab.BlockIncoming, ab.BlockOutgoing);
                    return new OkResponse();

                case ResolveAppDecisionRequest rd:
                    _engine.ResolveAppDecision(rd.AppId, rd.Allow);
                    return new OkResponse();

                case SaveFirewallProfileRequest sp:
                    _engine.SaveProfile(sp.Profile);
                    return new OkResponse();

                case DeleteFirewallProfileRequest dp:
                    _engine.DeleteProfile(dp.Name);
                    return new OkResponse();

                case ActivateFirewallProfileRequest ap:
                    _engine.ActivateProfile(ap.Name);
                    return new OkResponse();

                case GetAlertsRequest a:
                    return new AlertsResponse { Alerts = _engine.GetAlerts(a.Limit) };

                case AckAlertRequest ack:
                    _engine.AckAlert(ack.AlertId, ack.All);
                    return new OkResponse();

                case GetDevicesRequest d:
                    if (d.Rescan) _ = _engine.RescanDevicesAsync(ct);
                    return new DevicesResponse { Devices = _engine.GetDevices() };

                case RenameDeviceRequest rn:
                    _engine.RenameDevice(rn.DeviceId, rn.Name);
                    return new OkResponse();

                case ForgetDeviceRequest fd:
                    _engine.ForgetDevice(fd.DeviceId);
                    return new OkResponse();

                case GetSettingsRequest:
                    return new SettingsResponse { Settings = _engine.GetSettings() };

                case SetSettingsRequest ss:
                    _engine.SetSettings(ss.Settings);
                    return new OkResponse();

                case GetGeoIpStatusRequest:
                    return _engine.GetGeoIpStatus();

                case UpdateGeoIpRequest:
                    // Downloading is slow; run it off the read loop and enqueue the correlated
                    // reply when it finishes, so further requests aren't blocked meanwhile.
                    RunGeoIpUpdate(request.CorrelationId, client, ct);
                    return null;

                case GetStorageInfoRequest:
                    return new StorageInfoResponse { Storage = _engine.GetStorageInfo() };

                case SetStorageLocationRequest sl:
                    return new StorageInfoResponse { Storage = _engine.RelocateStorage(sl.NewDirectory) };

                case ClearDataRequest cd:
                    return new StorageInfoResponse { Storage = _engine.ClearData(cd.Mode) };

                default:
                    return new ErrorResponse { Error = $"Unknown request: {request.GetType().Name}" };
            }
        }
        catch (Exception ex)
        {
            return new ErrorResponse { Error = ex.Message };
        }
    }

    /// <summary>Runs a GeoIP update off the request read loop and enqueues the correlated reply.</summary>
    private void RunGeoIpUpdate(string? correlationId, Client client, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            IpcMessage resp;
            try
            {
                resp = await _engine.UpdateGeoIpAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var status = _engine.GetGeoIpStatus();
                status.Success = false;
                status.Message = ex.Message;
                resp = status;
            }
            resp.CorrelationId = correlationId;
            client.Enqueue(resp);
        }, ct);
    }

    private void OnEngineEvent(IpcMessage evt)
    {
        foreach (var kv in _clients)
            if (kv.Value.Subscribed)
                kv.Value.Enqueue(evt);
    }

    public async ValueTask DisposeAsync()
    {
        _engine.Events -= OnEngineEvent;
        _cts?.Cancel();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
        }
        foreach (var kv in _clients) kv.Value.Channel.Dispose();
        _clients.Clear();
    }
}
