using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using OpenWire.Core.Ipc;
using OpenWire.Core.Util;
using OpenWire.Service.Engine;
using OpenWire.Service.Ipc;

[assembly: SupportedOSPlatform("windows")]

namespace OpenWire.Service;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // WinExe: no console is attached, so setting OutputEncoding throws — ignore
        // it and let the WriteLine banner/logs below no-op. When launched from a
        // terminal (dev / --selftest) a console is present and this succeeds.
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        PrintBanner();

        int parentPid = ParseParentPid(args);
        bool daemon = args.Contains("--daemon");
        bool selfTest = args.Contains("--selftest");

        // Privileged IPC must normally be bound to the exact GUI process that launched
        // the engine. A deliberately unbound long-running engine requires --daemon so
        // the weaker trust mode is never entered by accident.
        if (parentPid == 0 && !daemon && !selfTest)
        {
            Console.Error.WriteLine("OpenWire.Service requires --parent-app <pid>, --daemon, or --selftest.");
            return 2;
        }

        IpcPeerProcessInfo? expectedClient = null;
        if (parentPid > 0)
        {
            if (!IpcPeerIdentity.TryGetProcessInfo(parentPid, out expectedClient)
                || !IsExpectedAppPath(expectedClient.ImagePath))
            {
                Console.Error.WriteLine("The requested parent is not the expected OpenWire.App executable.");
                return 3;
            }
        }

        string authorizedUserSid = expectedClient?.UserSid
            ?? WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Unable to determine the IPC user SID.");

        // One engine per machine owns the named pipe, the ETW session and the database. A second
        // launch (e.g. the app's auto-spawn racing a launcher script) must bow out immediately,
        // before touching any of them. Global\ so it spans sessions.
        using var singleton = new Mutex(true, @"Global\OpenWire.Engine.Singleton", out bool acquired);
        if (!acquired)
        {
            Console.WriteLine("Another OpenWire engine is already running - exiting.");
            return 0;
        }

        string dataDir = ResolveDataDir(args);
        Directory.CreateDirectory(dataDir);
        Console.WriteLine($"Data directory : {dataDir}");
        Console.WriteLine($"Elevated       : {IsElevated()}");
        if (!IsElevated())
            Console.WriteLine("  ! Not elevated - per-app ETW capture and firewall control are disabled (global graph still works).");
        Console.WriteLine();

        // Launch mode. The app spawns us with "--parent-app <pid>" so we die the moment it exits;
        // "--daemon" opts out of the die-with-the-app behaviour for headless/advanced use. Otherwise
        // the idle watchdog is on: the engine exits shortly after the last UI client disconnects, so
        // it never lingers in the background once OpenWire is closed.
        bool exitWhenIdle = !daemon;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        void RequestShutdown() { try { cts.Cancel(); } catch { /* already disposed/cancelled */ } }

        await using var engine = new MonitorEngine(dataDir);
        await engine.StartAsync(cts.Token);

        if (selfTest)
            return await SelfTestAsync(engine, cts.Token);

        // Die immediately if the app that spawned us exits (covers a force-kill or OS-suspend that
        // never cleanly closes the pipe). The idle watchdog below is the normal-close path.
        if (parentPid > 0) WatchParent((int)parentPid, RequestShutdown);

        await using var server = new IpcServer(
            engine,
            exitWhenIdle,
            RequestShutdown,
            parentPid,
            authorizedUserSid);
        server.Start(cts.Token);
        Console.WriteLine($"Listening on named pipe: \\\\.\\pipe\\{OpenWire.Core.Ipc.IpcTransport.PipeName}");
        Console.WriteLine("Engine running. Press Ctrl+C to stop.\n");

        try { await Task.Delay(Timeout.Infinite, cts.Token); }
        catch (OperationCanceledException) { }

        Console.WriteLine("Shutting down...");
        return 0;
    }

    private static int ParseParentPid(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] is "--parent-app" && int.TryParse(args[i + 1], out var pid) && pid > 0) return pid;
        return 0;
    }

    private static bool IsExpectedAppPath(string path)
    {
        string baseDir = AppContext.BaseDirectory;
        string local = Path.Combine(baseDir, "OpenWire.App.exe");
        if (IpcPeerIdentity.PathsEqual(path, local)) return true;

        string config = baseDir.Contains("Release", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        string dev = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..",
            "OpenWire.App", "bin", config, "net9.0-windows", "OpenWire.App.exe"));
        return IpcPeerIdentity.PathsEqual(path, dev);
    }

    /// <summary>Signal shutdown when the given (parent app) process exits.</summary>
    private static void WatchParent(int pid, Action onExit)
    {
        try
        {
            var parent = Process.GetProcessById(pid);
            parent.EnableRaisingEvents = true;
            parent.Exited += (_, _) => onExit();
            if (parent.HasExited) onExit(); // exited between lookup and hookup
        }
        catch (ArgumentException) { onExit(); } // no such process — already gone
        catch { /* best effort; the idle watchdog still covers a clean close */ }
    }

    private static async Task<int> SelfTestAsync(MonitorEngine engine, CancellationToken ct)
    {
        Console.WriteLine("Running self-test for ~10 seconds (generate some network activity)...\n");
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(1000, ct);
            var s = engine.GetStatus();
            Console.WriteLine($"  t+{i + 1,2}s  down {ByteFormatter.Rate(s.DownloadBytesPerSec),12}   up {ByteFormatter.Rate(s.UploadBytesPerSec),12}   apps:{s.ActiveAppCount,3}  conns:{s.ActiveConnectionCount,3}");
        }

        var graph = engine.GetGraph(Core.Models.GraphRange.FiveMinutes);
        Console.WriteLine($"\nGraph (5 min): {graph.Samples.Count} points, peak {ByteFormatter.Bytes(graph.PeakBytes)}/interval");

        var usage = engine.GetUsage(Core.Models.GraphRange.Day, Core.Models.UsageGroupBy.Apps);
        Console.WriteLine("\nTop apps today:");
        foreach (var a in usage.Apps.Take(8))
            Console.WriteLine($"  {a.App.Name,-28} down {ByteFormatter.Bytes(a.BytesIn),10}  up {ByteFormatter.Bytes(a.BytesOut),10}  [{a.FirewallStatus}]");

        var insights = engine.GetInsights(Core.Models.GraphRange.Week);
        Console.WriteLine($"\nInsights (week): {ByteFormatter.Bytes(insights.TotalBytes)} total, " +
                          $"{insights.ActiveApps} apps, {insights.ActiveDays} active day(s), " +
                          $"busiest hour {(insights.BusiestHour < 0 ? "n/a" : insights.BusiestHour + ":00")}, " +
                          $"{insights.Anomalies.Count} anomal(ies)");
        foreach (var hl in insights.Highlights)
            Console.WriteLine($"  • {hl}");
        foreach (var an in insights.Anomalies.Take(5))
            Console.WriteLine($"  ! [{an.Kind}] {an.Title}");

        var conns = engine.GetConnections();
        Console.WriteLine($"\nActive connections: {conns.Count}");
        foreach (var c in conns.Take(10))
            Console.WriteLine($"  {c.AppName,-24} {c.RemoteDisplay}:{c.RemotePort,-5} {c.Geo.CountryCode,-3} [{c.State}]");

        var hw = engine.GetHardware();
        Console.WriteLine($"\nHardware: CPU {hw.CpuPercent:0}%  Mem {ByteFormatter.Bytes(hw.MemoryUsedBytes)} ({hw.MemoryPercent:0}%)  " +
                          $"Disk R {ByteFormatter.Rate(hw.DiskReadBytesPerSec)} W {ByteFormatter.Rate(hw.DiskWriteBytesPerSec)}  " +
                          $"GPU {hw.GpuPercent:0}%  history:{hw.History.Count}");

        Console.WriteLine("\nSelf-test complete.");
        return 0;
    }

    private static string ResolveDataDir(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] is "--data" or "-d")
                return args[i + 1];

        // A datadir.txt pointer (written when the user relocates storage in Settings)
        // overrides the default location, so a moved database is found on restart.
        try
        {
            var pointer = OpenWire.Service.Engine.MonitorEngine.DataDirPointerPath;
            if (File.Exists(pointer))
            {
                var dir = File.ReadAllText(pointer).Trim();
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
            }
        }
        catch { /* fall back to the default */ }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OpenWire");
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static void PrintBanner()
    {
        Console.WriteLine(@"  ___                 _      ___");
        Console.WriteLine(@" / _ \ _ __  ___ _ _ | | /| / (_)_ _ ___");
        Console.WriteLine(@"| (_) | '_ \/ -_) ' \| |/ |/ / | '_/ -_)");
        Console.WriteLine(@" \___/| .__/\___|_||_|__/|__/|_|_| \___|   engine 0.1.0");
        Console.WriteLine(@"      |_|   open-source network monitor + firewall");
        Console.WriteLine();
    }
}
