# OpenWire

**An open-source network monitor + application firewall for Windows.**

OpenWire is a clean-room, open-source reimplementation of the desktop experience popularised
by GlassWire: a live, animated bandwidth graph with per-process attribution, a bidirectional
application firewall, a LAN device scanner, and a network-security alert engine — all free,
with no account, no telemetry, and no paywalled features.

> OpenWire is an independent project. It is **not affiliated with, endorsed by, or derived from
> the source code of GlassWire / SecureMix LLC**. It was built from public documentation and
> observed behaviour, using entirely original code and assets.

---

## Highlights

- **Live traffic graph** — a smoothly-animated, auto-scaling filled-area chart of incoming
  (teal) and outgoing (amber) throughput, with time ranges from 5 minutes to a month.
- **Per-process attribution** — see exactly which application is sending and receiving, with
  real icons, publishers and connection counts.
- **Application firewall** — allow/block any app in either direction via the Windows Defender
  Firewall. Modes: *Off*, *Click to block*, *Ask to connect*.
- **Usage breakdown** — totals by application, remote host (with country), or traffic type,
  over day / week / month.
- **Alerts** — new app on the network, new device on the LAN, DNS-server change, inbound RDP,
  data-plan thresholds, and more.
- **Things** — scan the local network and inventory every device (IP, MAC, vendor, type,
  first-seen, online status).
- **Hardware resources** — live CPU / memory / disk / GPU graphs alongside the network views.
- **GlassWire-accurate UI** — light theme, top 5-tab layout, the signature down/up gauge with
  WAN/LAN bars, and green/red firewall pills.
- **Driverless** — captures per-process traffic with **ETW**, not a kernel driver, so there is
  nothing to sign and nothing to install into the kernel.

---

## Architecture

OpenWire is two processes — a privileged background engine and an unprivileged GUI — that talk
over a local named pipe, mirroring GlassWire's service/GUI split.

```
┌─────────────────────────────┐        ┌────────────────────────────────────────┐
│  OpenWire.App  (WPF, user)  │  named │  OpenWire.Service  (elevated)            │
│  - all six screens          │◄─pipe─►│  - ETW per-process capture               │
│  - live graph + tables      │  JSON  │  - IPHLPAPI connection table             │
│  - issues block/scan/ack    │        │  - Windows Firewall (INetFwPolicy2)      │
└─────────────────────────────┘        │  - GeoIP + reverse DNS + LAN scanner     │
                                        │  - alert engine + SQLite history         │
                                        └────────────────────────────────────────┘
                                                        │
                                          %ProgramData%\OpenWire\openwire.db
```

| Project | What it is |
|---------|-----------|
| `OpenWire.Core` | Shared domain models + the polymorphic named-pipe IPC contract. |
| `OpenWire.Service` | The monitoring engine. Runs elevated; does all privileged work. |
| `OpenWire.App` | The WPF UI. Runs as a normal user; talks only to the engine. |

### How it works

| Capability | Mechanism |
|-----------|-----------|
| Per-process bytes | ETW `Microsoft-Windows-Kernel-Network` via `TraceEvent` (needs admin). |
| Global throughput | Interface counters — the driverless fallback when ETW is unavailable. |
| Live connections | `iphlpapi!GetExtendedTcp/UdpTable` (TCP/UDP, IPv4/IPv6) → owning PID. |
| Blocking | Windows Defender Firewall rules via the `HNetCfg.FwPolicy2` COM API. |
| IP → country | MaxMind GeoLite2 (optional; drop-in database). |
| Host names | Reverse DNS with a bounded, TTL'd cache. |
| LAN devices | Subnet ping-sweep + ARP table + MAC-OUI vendor lookup. |
| History | SQLite (WAL) with per-minute rollups (global / app / host). |

See [`docs/SPEC.md`](docs/SPEC.md) for the full feature + UI specification.

---

## Building & running

**Requirements:** Windows 10/11, [.NET SDK 9](https://dotnet.microsoft.com/download).

```powershell
# from the repo root
dotnet build OpenWire.sln -c Release
```

Then launch the engine **as administrator** (for ETW capture + firewall control) and the app
as a normal user:

```powershell
# convenience script: builds, starts the elevated engine (UAC prompt), then the app
./run.ps1
```

Or manually:

```powershell
# 1. engine — right-click ▸ Run as administrator, or:
Start-Process src/OpenWire.Service/bin/Release/net9.0-windows/OpenWire.Service.exe -Verb RunAs

# 2. app
Start-Process src/OpenWire.App/bin/Release/net9.0-windows/OpenWire.App.exe
```

Headless engine self-test (no GUI, no elevation required — prints live rates, top apps and
connections for ~10 seconds):

```powershell
dotnet src/OpenWire.Service/bin/Release/net9.0-windows/OpenWire.Service.dll --selftest
```

> **Without elevation** the engine still shows the global graph, the live connection table, the
> per-app list (from connections), the firewall app list, and the LAN scanner — but it cannot
> record per-app byte totals (ETW) or enforce blocks (firewall). Run it as administrator for the
> full experience.

### Optional: GeoIP country data

Country flags/labels use a MaxMind GeoLite2 database. It is not bundled (MaxMind requires a free
licence). To enable: download `GeoLite2-Country.mmdb` and drop it in `%ProgramData%\OpenWire\`.

### Optional: full device-vendor names

OpenWire ships a small built-in MAC-OUI table. For full coverage, drop a Wireshark-style
`manuf` file at `%ProgramData%\OpenWire\manuf.txt`.

---

## Feature ↔ screen map

The UI follows current GlassWire: a light theme with a top 5-tab bar, and a
bottom dashboard (down/up gauge + WAN/LAN bars + history scrubber) on the
Traffic tab.

| Tab | What it does |
|-----|--------------|
| **Traffic** | Folds three sub-views — the live animated graph (yellow download / pink upload), a 4-column usage breakdown (Apps / Hosts / Traffic types / Countries), and a connection map — sharing the bottom gauge, WAN/LAN bars and timeline scrubber. |
| **Firewall** | Per-app table with green/red **Incoming/Outgoing** pill toggles + version / hosts / down / up, and a firewall mode selector. |
| **Alerts** | Security + activity log grouped by Today / Yesterday / date with NEW badges. |
| **Scanner** | LAN device inventory with scan. |
| **Hardware** | CPU / memory / disk / GPU graphs + a live stat row. |
| **Settings** (gear) | Monitors, resolution, data plan, and engine info. |

---

## Honest limitations

- **First-packet blocking.** Because enforcement is delegated to the Windows Firewall (rule-based,
  not an inline packet callout), a brand-new app may emit a few packets before its block rule
  lands. True first-packet blocking would require a signed WFP callout driver — a deliberate
  non-goal for the driverless build.
- **OpenWire "score" / reputation.** There is no cloud backend, so no cross-userbase reputation.
- **Webcam/mic monitoring** is intentionally omitted (no reliable Windows API).

## Roadmap

- Firewall profiles + auto-switch by network
- VirusTotal file-hash checks (user API key)
- Remote / multi-PC monitoring over the same IPC contract
- ARP-spoofing / evil-twin Wi-Fi detection
- Optional WFP callout driver for exact accounting + first-packet block

---

## License

[MIT](LICENSE).
