using System.Runtime.Versioning;
using System.Security.Principal;
using OpenWire.Core.Util;
using OpenWire.Service.Engine;
using OpenWire.Service.Ipc;

[assembly: SupportedOSPlatform("windows")]

namespace OpenWire.Service;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        PrintBanner();

        string dataDir = ResolveDataDir(args);
        Directory.CreateDirectory(dataDir);
        Console.WriteLine($"Data directory : {dataDir}");
        Console.WriteLine($"Elevated       : {IsElevated()}");
        if (!IsElevated())
            Console.WriteLine("  ! Not elevated - per-app ETW capture and firewall control are disabled (global graph still works).");
        Console.WriteLine();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        await using var engine = new MonitorEngine(dataDir);
        await engine.StartAsync(cts.Token);

        if (args.Contains("--selftest"))
            return await SelfTestAsync(engine, cts.Token);

        await using var server = new IpcServer(engine);
        server.Start(cts.Token);
        Console.WriteLine($"Listening on named pipe: \\\\.\\pipe\\{OpenWire.Core.Ipc.IpcTransport.PipeName}");
        Console.WriteLine("Engine running. Press Ctrl+C to stop.\n");

        try { await Task.Delay(Timeout.Infinite, cts.Token); }
        catch (OperationCanceledException) { }

        Console.WriteLine("Shutting down...");
        return 0;
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
