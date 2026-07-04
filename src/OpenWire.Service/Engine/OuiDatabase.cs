using OpenWire.Core.Models;

namespace OpenWire.Service.Engine;

/// <summary>
/// Resolves a MAC OUI (first three octets) to a hardware vendor, and guesses a
/// device kind from the vendor. Ships with a curated set of common vendors; a full
/// Wireshark-style <c>manuf</c> file dropped in the data directory overrides/extends it.
/// </summary>
public sealed class OuiDatabase
{
    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    public OuiDatabase(string? extraManufPath = null)
    {
        foreach (var (prefix, vendor) in BuiltIn)
            _map[prefix] = vendor;

        if (!string.IsNullOrEmpty(extraManufPath) && File.Exists(extraManufPath))
            LoadManufFile(extraManufPath);
    }

    public string Lookup(string mac)
    {
        if (string.IsNullOrEmpty(mac) || mac.Length < 8) return string.Empty;
        var prefix = mac[..8].ToUpperInvariant(); // "AA:BB:CC"
        return _map.TryGetValue(prefix, out var vendor) ? vendor : string.Empty;
    }

    public DeviceKind GuessKind(string vendor, string hostName)
    {
        string v = vendor.ToLowerInvariant();
        string h = hostName.ToLowerInvariant();

        if (Contains(v, "hikvision", "dahua", "amcrest", "reolink", "axis comm", "wyze")) return DeviceKind.Camera;
        if (Contains(v, "hewlett", "canon", "epson", "brother", "lexmark", "xerox", "kyocera")) return DeviceKind.Printer;
        if (Contains(v, "nintendo", "sony interactive", "microsoft") && Contains(h, "xbox", "playstation", "switch")) return DeviceKind.GameConsole;
        if (Contains(v, "nintendo")) return DeviceKind.GameConsole;
        if (Contains(v, "espressif", "tuya", "sonoff", "shelly", "tp-link", "philips lighting", "signify")) return DeviceKind.SmartHome;
        if (Contains(v, "amazon") && Contains(h, "echo", "fire", "kindle")) return DeviceKind.SmartHome;
        if (Contains(v, "roku", "vizio") || Contains(h, "tv", "bravia", "roku")) return DeviceKind.Television;
        if (Contains(v, "apple") && Contains(h, "iphone", "ipad")) return DeviceKind.Phone;
        if (Contains(v, "apple")) return DeviceKind.Computer;
        if (Contains(v, "samsung", "xiaomi", "huawei", "oneplus", "google") && Contains(h, "phone", "android", "pixel")) return DeviceKind.Phone;
        if (Contains(v, "raspberry", "synology", "qnap")) return DeviceKind.Server;
        if (Contains(v, "ubiquiti", "cisco", "netgear", "d-link", "asustek", "tp-link", "zyxel", "mikrotik", "arris", "technicolor")) return DeviceKind.Router;
        if (Contains(v, "dell", "lenovo", "intel", "asustek", "gigabyte", "micro-star", "hon hai", "foxconn", "azurewave", "liteon")) return DeviceKind.Computer;
        return DeviceKind.Unknown;
    }

    private static bool Contains(string haystack, params string[] needles)
        => needles.Any(n => haystack.Contains(n, StringComparison.Ordinal));

    private void LoadManufFile(string path)
    {
        try
        {
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var parts = line.Split(new[] { '\t', ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                var prefix = parts[0].Replace('-', ':').Trim();
                if (prefix.Length < 8) continue;
                _map[prefix[..8].ToUpperInvariant()] = parts[1].Split('\t', '#')[0].Trim();
            }
        }
        catch { /* best effort */ }
    }

    // A compact, high-hit-rate vendor set. Not exhaustive — drop a manuf file for full coverage.
    private static readonly (string Prefix, string Vendor)[] BuiltIn =
    {
        ("3C:22:FB", "Apple"), ("A4:83:E7", "Apple"), ("F0:18:98", "Apple"), ("AC:BC:32", "Apple"),
        ("D0:03:4B", "Apple"), ("90:B0:ED", "Apple"), ("F4:0F:24", "Apple"),
        ("FC:FB:FB", "Cisco"), ("00:1B:0D", "Cisco"), ("00:0C:29", "VMware"), ("00:50:56", "VMware"),
        ("00:15:5D", "Microsoft"), ("7C:1E:52", "Microsoft"), ("00:17:FA", "Microsoft"),
        ("B8:27:EB", "Raspberry Pi Foundation"), ("DC:A6:32", "Raspberry Pi Trading"), ("E4:5F:01", "Raspberry Pi Trading"),
        ("18:FE:34", "Espressif"), ("24:0A:C4", "Espressif"), ("A0:20:A6", "Espressif"), ("30:AE:A4", "Espressif"),
        ("EC:FA:BC", "Espressif"), ("7C:9E:BD", "Espressif"),
        ("50:02:91", "Espressif"), ("CC:50:E3", "Espressif"),
        ("FC:65:DE", "Amazon"), ("F0:27:2D", "Amazon"), ("44:65:0D", "Amazon"), ("34:D2:70", "Amazon"),
        ("68:37:E9", "Amazon"), ("A0:02:DC", "Amazon"),
        ("F4:F5:E8", "Google"), ("54:60:09", "Google"), ("1C:F2:9A", "Google"), ("48:D6:D5", "Google"),
        ("D8:6C:63", "Google"), ("30:FD:38", "Google"),
        ("00:17:88", "Philips Lighting (Signify)"), ("EC:B5:FA", "Philips Lighting (Signify)"),
        ("50:C7:BF", "TP-Link"), ("F4:F2:6D", "TP-Link"), ("AC:84:C6", "TP-Link"), ("98:DA:C4", "TP-Link"),
        ("C0:06:C3", "TP-Link"), ("60:32:B1", "TP-Link"),
        ("2C:AA:8E", "Samsung"), ("BC:14:85", "Samsung"), ("5C:0A:5B", "Samsung"), ("00:12:FB", "Samsung"),
        ("8C:77:12", "Samsung"), ("FC:A1:3E", "Samsung"),
        ("00:9E:C8", "Xiaomi"), ("28:6C:07", "Xiaomi"), ("64:09:80", "Xiaomi"), ("F0:B4:29", "Xiaomi"),
        ("34:CE:00", "Xiaomi"), ("50:8F:4C", "Xiaomi"),
        ("00:E0:4C", "Realtek"), ("52:54:00", "QEMU/KVM"),
        ("00:1A:11", "Google"), ("00:24:E4", "Withings"),
        ("00:04:20", "Sony"), ("FC:0F:E6", "Sony Interactive"), ("00:D9:D1", "Sony Interactive"),
        ("7C:BB:8A", "Nintendo"), ("98:B6:E9", "Nintendo"), ("A4:C0:E1", "Nintendo"), ("00:1F:C5", "Nintendo"),
        ("00:24:BE", "Nintendo"), ("58:BD:A3", "Nintendo"),
        ("44:D9:E7", "Ubiquiti"), ("FC:EC:DA", "Ubiquiti"), ("74:AC:B9", "Ubiquiti"), ("E0:63:DA", "Ubiquiti"),
        ("00:1D:7E", "Cisco-Linksys"), ("C0:C1:C0", "Cisco-Linksys"),
        ("A0:63:91", "Netgear"), ("9C:D3:6D", "Netgear"), ("20:E5:2A", "Netgear"), ("04:A1:51", "Netgear"),
        ("1C:BD:B9", "D-Link"), ("78:54:2E", "D-Link"), ("C8:BE:19", "D-Link"),
        ("2C:56:DC", "ASUSTek"), ("50:46:5D", "ASUSTek"), ("AC:22:0B", "ASUSTek"), ("04:D4:C4", "ASUSTek"),
        ("F8:32:E4", "ASUSTek"), ("38:D5:47", "ASUSTek"),
        ("28:56:5A", "Hewlett Packard"), ("70:5A:0F", "Hewlett Packard"), ("3C:D9:2B", "Hewlett Packard"),
        ("9C:B6:54", "Hewlett Packard"), ("00:1F:29", "Hewlett Packard"),
        ("00:1B:63", "Canon"), ("2C:9E:FC", "Canon"), ("00:00:48", "Epson"), ("64:EB:8C", "Seiko Epson"),
        ("00:80:77", "Brother"), ("00:1B:A9", "Brother"), ("30:05:5C", "Brother"),
        ("44:19:B6", "Hikvision"), ("BC:AD:28", "Hikvision"), ("C0:56:E3", "Hikvision"),
        ("3C:EF:8C", "Dahua"), ("90:02:A9", "Dahua"),
        ("D4:3D:7E", "Micro-Star (MSI)"), ("00:16:CB", "Apple"), ("F8:E4:3B", "Dell"),
        ("B0:83:FE", "Dell"), ("18:66:DA", "Dell"), ("00:14:22", "Dell"), ("D0:67:E5", "Dell"),
        ("E4:54:E8", "Dell"), ("54:BF:64", "Dell"),
        ("54:EE:75", "Wistron"), ("48:5F:99", "Cloud Network"), ("00:1E:C2", "Apple"),
        ("00:24:D7", "Intel"), ("34:41:5D", "Intel"), ("94:65:9C", "Intel"), ("A0:A8:CD", "Intel"),
        ("00:1F:3C", "Intel"), ("7C:B0:C2", "Intel"), ("50:76:AF", "Intel"),
        ("E4:70:B8", "Intel"), ("D8:F8:83", "Intel"),
        ("00:0A:F7", "Broadcom"), ("00:10:18", "Broadcom"),
        ("00:26:AB", "Seiko Epson"), ("00:1E:8F", "Canon"),
    };
}
