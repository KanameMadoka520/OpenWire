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
        public volatile bool WantsUiActive; // this client's last-reported "UI is being viewed" state
        public CancellationTokenSource? Cts;
        public readonly Channel<IpcMessage> Outbound =
            System.Threading.Channels.Channel.CreateBounded<IpcMessage>(
                new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        public void Enqueue(IpcMessage m) => Outbound.Writer.TryWrite(m);
    }

    // Idle-shutdown watchdog: once a client has connected, the engine exits shortly after the last
    // one disconnects, so it never lingers in the background after OpenWire is closed.
    private const int IdleGraceMs = 8000;     // > the app's reconnect floor, with margin
    private const int StartupGraceMs = 45000; // orphan protection: no app ever attached

    private readonly MonitorEngine _engine;
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly int _expectedClientPid;
    private readonly string _authorizedUserSid;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    private readonly bool _exitWhenIdle;
    private readonly Action _requestShutdown;
    private readonly object _watchdogLock = new();
    private System.Threading.Timer? _idleTimer;
    private System.Threading.Timer? _startupTimer;
    private bool _hadClient;
    private volatile bool _disposing;
    private long _connectSeq;                 // bumped after peer authorization, before the client is registered
    private long _idleArmSeq, _startupArmSeq; // the _connectSeq snapshot each timer was armed at

    public IpcServer(
        MonitorEngine engine,
        bool exitWhenIdle,
        Action requestShutdown,
        int expectedClientPid,
        string authorizedUserSid)
    {
        _engine = engine;
        _exitWhenIdle = exitWhenIdle;
        _requestShutdown = requestShutdown;
        _expectedClientPid = expectedClientPid;
        _authorizedUserSid = authorizedUserSid;
        _engine.Events += OnEngineEvent;
    }

    public void Start(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_exitWhenIdle)
        {
            _idleTimer = new System.Threading.Timer(OnIdleTick, null, Timeout.Infinite, Timeout.Infinite);
            _startupTimer = new System.Threading.Timer(OnStartupTick, null, Timeout.Infinite, Timeout.Infinite);
            lock (_watchdogLock)
            {
                _startupArmSeq = Interlocked.Read(ref _connectSeq);
                _startupTimer.Change(StartupGraceMs, Timeout.Infinite);
            }
        }
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            System.IO.Pipes.NamedPipeServerStream? server = null;
            try
            {
                server = IpcTransport.CreateServerStream(_authorizedUserSid);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var accepted = server; server = null; // ownership transfers to the handler
                _ = HandleClientAsync(accepted, ct);
            }
            catch (OperationCanceledException) { server?.Dispose(); break; }
            catch (Exception ex)
            {
                server?.Dispose(); // don't leak the unconnected pipe instance on an accept error
                Console.Error.WriteLine($"[IPC] accept error: {ex.Message}");
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(System.IO.Pipes.NamedPipeServerStream server, CancellationToken ct)
    {
        if (!TryAuthorizeClient(server))
        {
            Console.Error.WriteLine("[IPC] rejected an unauthorized local client.");
            server.Dispose();
            return;
        }

        // Bump only after OS-derived PID/SID authorization succeeds. Unauthorized probes
        // must not cancel the startup watchdog or keep an orphaned engine alive.
        Interlocked.Increment(ref _connectSeq);
        var id = Guid.NewGuid();
        var channel = new IpcChannel(server);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Default idle: the samplers only run at full rate once a client reports the Hardware page is
        // open (the app re-asserts on connect). A version-skewed client that never reports just gets
        // coarse hardware / a paused process sweep — the safe, low-CPU default.
        var client = new Client { Channel = channel, Cts = linked, WantsUiActive = false };
        _clients[id] = client;
        OnClientConnected();

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
            OnClientDisconnected();
            linked.Cancel();                       // stop the writer if the reader ended first
            client.Outbound.Writer.TryComplete();
            try { await writer.ConfigureAwait(false); } catch { }
            channel.Dispose();
        }
    }

    private bool TryAuthorizeClient(System.IO.Pipes.NamedPipeServerStream server)
    {
        if (!IpcPeerIdentity.TryGetClientProcessInfo(server, out var candidate)) return false;
        if (_expectedClientPid > 0 && candidate.ProcessId != _expectedClientPid) return false;
        if (!string.Equals(candidate.UserSid, _authorizedUserSid, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    // ---- idle-shutdown watchdog + UI-active recompute ----

    private void OnClientConnected()
    {
        lock (_watchdogLock)
        {
            if (_disposing) return; // timers may already be disposed during teardown
            _hadClient = true;
            try
            {
                _idleTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _startupTimer?.Change(Timeout.Infinite, Timeout.Infinite); // first connect ends the startup grace
            }
            catch (ObjectDisposedException) { }
        }
        RecomputeUiActive();
    }

    private void OnClientDisconnected()
    {
        if (_exitWhenIdle && !_disposing)
        {
            lock (_watchdogLock)
            {
                if (_hadClient && _clients.IsEmpty)
                {
                    _idleArmSeq = Interlocked.Read(ref _connectSeq);
                    _idleTimer?.Change(IdleGraceMs, Timeout.Infinite);
                }
            }
        }
        RecomputeUiActive();
    }

    private void OnIdleTick(object? _)
    {
        lock (_watchdogLock)
        {
            if (_disposing || !_hadClient || !_clients.IsEmpty) return;
            if (Interlocked.Read(ref _connectSeq) != _idleArmSeq) return; // a client raced in — abort
        }
        _requestShutdown(); // signal-only (catch-all inside)
    }

    private void OnStartupTick(object? _)
    {
        lock (_watchdogLock)
        {
            if (_disposing || _hadClient || !_clients.IsEmpty) return;
            if (Interlocked.Read(ref _connectSeq) != _startupArmSeq) return;
        }
        _requestShutdown(); // orphan protection: spawned but nobody ever attached
    }

    private readonly object _uiActiveLock = new();

    /// <summary>Throttle state is a pure OR over live clients' last-reported active flag, recomputed
    /// on every connect / disconnect / report — never a sticky value a crashed app could strand.
    /// Serialized so two concurrent recomputes can't apply out of order and strand it throttled.</summary>
    private void RecomputeUiActive()
    {
        lock (_uiActiveLock)
        {
            bool anyActive = false;
            foreach (var c in _clients.Values) if (c.WantsUiActive) { anyActive = true; break; }
            try { _engine.SetUiActive(anyActive); } catch { /* engine tearing down */ }
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

                case SetLockdownRequest ld:
                    _engine.SetLockdown(ld.On);
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

                case SetUiActiveRequest ua:
                    client.WantsUiActive = ua.Active;
                    RecomputeUiActive();
                    return null; // fire-and-forget, no reply needed

                case GetAutoStartRequest:
                    return _engine.GetAutoStart();

                case SetAutoStartRequest asr:
                    return _engine.SetAutoStart(asr.Enabled, asr.AppExePath, asr.UserName);

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
        _disposing = true; // make any in-flight watchdog callback a lock-free no-op
        lock (_watchdogLock)
        {
            _idleTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _startupTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        _engine.Events -= OnEngineEvent;
        _cts?.Cancel();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
        }
        // Dispose the timers only after the accept loop has stopped, so a late OnClientConnected
        // can't call Change() on a disposed timer.
        _idleTimer?.Dispose();
        _startupTimer?.Dispose();

        foreach (var kv in _clients) kv.Value.Channel.Dispose();
        _clients.Clear();
    }
}
