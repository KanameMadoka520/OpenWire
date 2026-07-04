using System.Collections.Concurrent;
using System.Runtime.Versioning;
using OpenWire.Core.Ipc;
using OpenWire.Service.Engine;

namespace OpenWire.Service.Ipc;

/// <summary>
/// Named-pipe server that exposes the engine to one or more UI clients. Each
/// connection runs a request/response loop; connections that subscribe also receive
/// the engine's streamed events (live ticks, alerts, device changes, status).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IpcServer : IAsyncDisposable
{
    private sealed class Client
    {
        public required IpcChannel Channel;
        public volatile bool Subscribed;
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
        var client = new Client { Channel = channel };
        _clients[id] = client;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var request = await channel.ReceiveAsync(ct).ConfigureAwait(false);
                if (request is null) break;

                IpcMessage? response = await DispatchAsync(request, client, ct).ConfigureAwait(false);
                if (response is not null)
                {
                    response.CorrelationId = request.CorrelationId;
                    await channel.SendAsync(response, ct).ConfigureAwait(false);
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
            channel.Dispose();
        }
    }

    private async Task<IpcMessage?> DispatchAsync(IpcMessage request, Client client, CancellationToken ct)
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

                case GetConnectionsRequest:
                    return new ConnectionsResponse { Connections = _engine.GetConnections() };

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

                case GetAlertsRequest a:
                    return new AlertsResponse { Alerts = _engine.GetAlerts(a.Limit) };

                case AckAlertRequest ack:
                    _engine.AckAlert(ack.AlertId, ack.All);
                    return new OkResponse();

                case GetDevicesRequest d:
                    if (d.Rescan) await _engine.RescanDevicesAsync(ct).ConfigureAwait(false);
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

                default:
                    return new ErrorResponse { Error = $"Unknown request: {request.GetType().Name}" };
            }
        }
        catch (Exception ex)
        {
            return new ErrorResponse { Error = ex.Message };
        }
    }

    private void OnEngineEvent(IpcMessage evt)
    {
        foreach (var kv in _clients)
        {
            var client = kv.Value;
            if (!client.Subscribed) continue;
            _ = SafeSendAsync(client, evt);
        }
    }

    private static async Task SafeSendAsync(Client client, IpcMessage evt)
    {
        try { await client.Channel.SendAsync(evt).ConfigureAwait(false); }
        catch { /* connection went away; its read loop will clean up */ }
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
