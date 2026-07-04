# OpenWire — Authoritative Feature & UI Specification

> **Single source of truth for the build.** OpenWire is an open-source, clean-room reimplementation of GlassWire for Windows: a per-process network monitor, application firewall, LAN device scanner, and network-security alert engine. This document specifies the product, architecture, every screen, the security suite, every alert type, the visual design system, and the (dissolved) feature tiering.

---

## 1. Product Overview & Philosophy

### 1.1 What OpenWire Is
OpenWire is a Windows desktop application that answers three questions continuously and visually:

1. **"What is my computer sending and receiving right now, and what sent it?"** — a live, animated bandwidth graph with per-process attribution and full historical playback (the "Network Time Machine").
2. **"Should this app be allowed to talk to the network?"** — a bidirectional application firewall built on the Windows Firewall, with per-app / per-direction allow/block and an *Ask-to-Connect* gate.
3. **"Is anything suspicious happening on or around my machine?"** — a network-security alert engine (new devices, DNS changes, RDP sessions, rogue Wi-Fi, suspicious hosts, etc.).

### 1.2 Core Philosophy
- **Driverless-first.** Unlike GlassWire (which ships a signed kernel driver `gwdrv.sys`), OpenWire captures per-process traffic with **ETW** (Event Tracing for Windows) so it needs **no kernel driver and no EV code-signing certificate**. A high-fidelity WFP-callout driver is an explicit *stretch goal*, not a requirement.
- **Transparent enforcement.** OpenWire never drops packets in a private driver. It **delegates blocking to the Windows Defender Firewall** via the Windows Firewall COM API — exactly as GlassWire does — so rules are visible in Windows, survive reboots, and persist when the GUI is closed. We accept the honest limitation this implies (see §2.5).
- **Everything free, forever.** GlassWire paywalls its most useful capabilities (Ask-to-Connect, extended history, remote monitoring, Evil-Twin/ARP/RDP alerts, VirusTotal, GlassWire Score, Firewall Profiles). OpenWire ships the **entire feature surface with no tiers, no accounts, no telemetry**. §7 maps each GlassWire tier boundary for reference only.
- **Privileged service / thin GUI split.** Only a background Windows service touches admin-only surfaces (ETW session, firewall API, ProgramData writes). The GUI is an ordinary unprivileged process that talks to the service over local IPC — the same channel used for remote-machine monitoring.
- **Forensic, not just live.** Every screen is time-travel-capable. History is stored locally in SQLite with multi-resolution rollups so you can zoom from a live 5-minute rate view out to months of volume history cheaply.
- **Clean-room.** OpenWire is built only from observed external behavior and public documentation. GlassWire's undocumented internals (exact driver type, IPC wire format) are treated as *recommendations*, never copied.

### 1.3 Non-Goals
- No cloud backend, no license server, no user telemetry.
- No deep-packet inspection of payloads / TLS interception.
- Webcam/mic monitoring is **not** in the shipping product (Windows removed the reliable detection surface; see §4). Documented historically only.

### 1.4 Target Platform
- Windows 10 and Windows 11 (x64, ARM64).
- .NET 8+ for both service and GUI.
- GUI framework: **WPF or WinUI 3** (primary recommendation) or **Avalonia** if cross-platform reuse is later desired.

---

## 2. Architecture

OpenWire is a **two-process product** mirroring GlassWire's `GWCtlSrv.exe` (service) + `GlassWire.exe` (GUI) split.

```
┌──────────────────────────────┐         ┌──────────────────────────────────────┐
│  OpenWire.Gui.exe            │  gRPC/  │  OpenWireService (LocalSystem)         │
│  (per-user, unprivileged)    │◄──named─►│  Microsoft.Extensions.Hosting worker  │
│  WPF / WinUI 3               │  pipe / │                                        │
│  - all screens               │  loopback│  ┌── Capture (ETW TraceEventSession)  │
│  - no admin rights           │  TCP    │  ├── Connection table (IPHLPAPI poll)  │
│  - talks only to the service │         │  ├── Firewall (INetFwPolicy2 COM)      │
└──────────────────────────────┘         │  ├── GeoIP (MaxMind.GeoIP2)            │
                                          │  ├── Reverse DNS (System.Net.Dns)      │
                                          │  ├── LAN scanner (ARP+probes+Recog)    │
                                          │  ├── Alert engine                      │
                                          │  └── Storage (SQLite WAL rollups)      │
                                          └──────────────────────────────────────┘
                                                          │
                                             C:\ProgramData\OpenWire\
                                             ├── GeoLite2-Country.mmdb
                                             ├── openwire.db  (config, rules, devices, alerts)
                                             └── stats\  (time-partitioned rollup DBs)
```

### 2.1 Background Service (`OpenWireService`)
- **Host:** `Microsoft.Extensions.Hosting` `BackgroundService` (worker-service template). Registered as a Windows Service.
- **Account:** `LocalSystem` — required to open a real-time kernel ETW session, call the Windows Firewall COM API, and write to `%ProgramData%`.
- **Responsibilities:** all capture, connection enumeration, GeoIP, reverse DNS, firewall programming, LAN scanning, alert evaluation, SQLite persistence, and serving the IPC contract to one-or-more GUIs (local or remote).
- **Lifetime:** starts at boot; capture and firewall enforcement continue with the GUI closed.

### 2.2 GUI (`OpenWire.Gui`)
- Ordinary interactive user process. **Never** calls privileged APIs directly.
- Renders all screens, issues commands (block app, change mode, rename device, toggle alert) over IPC, and subscribes to streaming updates (live graph, new-connection events, alerts).
- Can point at a **remote** service (see §4 remote monitoring) using the identical contract.

### 2.3 IPC
- **Recommended:** **gRPC** with a `.proto` contract, transported over a **named pipe** for the local case and **loopback/remote TCP** for multi-PC monitoring. gRPC gives typed contracts and **server-streaming** ideal for the live graph feed.
- **Simpler alternatives:** `System.IO.Pipes` named pipe carrying `protobuf-net`/`MessagePack` frames, or SignalR/WebSocket on loopback.
- **Security:** authenticate the channel; restrict the pipe ACL so only the intended interactive user can issue firewall-mutating commands. Remote endpoints require a shared password (see §4).
- *(GlassWire's actual wire format is undocumented; protobuf here is a recommendation, not a replicated fact.)*

### 2.4 Per-Process Traffic Capture (driverless)
**Primary (driverless, no signing):** ETW.
- Use **`Microsoft.Diagnostics.Tracing.TraceEvent`** (PerfView's engine).
- Open an **elevated real-time `TraceEventSession`** (service is `LocalSystem`).
- Enable the kernel network provider (`KernelTraceEventProvider.Keywords.NetworkTCPIP`) and subscribe to `KernelTraceEventParser` events: **`TcpIpSend` / `TcpIpRecv` / `UdpIpSend` / `UdpIpRecv`**, each carrying **ProcessID + byte count** → per-process cumulative byte deltas that drive the graph and usage tables.
- Alternatively/additionally parse the modern **`Microsoft-Windows-TCPIP`** manifest provider for send/recv with PID.

**Fallback (no ETW, no driver):** poll `GetExtendedTcpTable`/`GetExtendedUdpTable` (§2.6) on a timer to snapshot active per-PID connections. Undercounts short-lived flows and cannot give exact byte totals — use only where ETW is unavailable.

**Stretch (high fidelity):** a **KMDF WFP callout driver** registered at `FWPM_LAYER_ALE_FLOW_ESTABLISHED` / `FWPM_LAYER_STREAM` doing per-flow byte accounting keyed by PID (reference: `pia-foss/desktop-windows-wfp-callout`). Requires an EV code-signing cert + attestation signing — hence not the default.

### 2.5 Firewall Enforcement (Windows Firewall, delegated)
- **Confirmed GlassWire design, replicated:** blocking is done by **programming Windows Defender Firewall**, not by a private packet filter.
- Interop: `FirewallAPI.dll` / `NetFwTypeLib` COM.
  - `INetFwPolicy2` via `Type.GetTypeFromProgID("HNetCfg.FwPolicy2")`.
  - `INetFwRule` via `Type.GetTypeFromProgID("HNetCfg.FwRule")`.
  - Set `Name`, `ApplicationName` (full exe path), `Action = NET_FW_ACTION_BLOCK`, `Direction = NET_FW_RULE_DIR_IN/OUT`, `Protocol`, `Enabled = true`, then `fwPolicy2.Rules.Add(rule)`.
  - Per-endpoint / "block once" semantics via `RemoteAddresses` / `RemotePorts`.
- **Must run in the service** (admin), never the GUI.
- **Consequences to design for (GlassWire documents all of these):**
  - Rules **persist across reboots** and with the GUI closed.
  - OpenWire-created rules can **linger** in Windows Firewall — provide a "clean up OpenWire rules" maintenance action and namespace all rule names with an `OpenWire:` prefix + a stable GUID tag so they're identifiable and removable.
  - Because enforcement is rule-based (not an inline callout), a brand-new app **may emit a few packets before the block rule lands**. True first-packet blocking requires the WFP-callout stretch goal. Document this in-product for Ask-to-Connect.

### 2.6 Connection → PID Enumeration
- P/Invoke **`iphlpapi!GetExtendedTcpTable`** with `TCP_TABLE_OWNER_PID_ALL` → `MIB_TCPROW_OWNER_PID` rows (local addr/port, remote addr/port, state, `OwningPid`); IPv6 variant over `AF_INET6` (`MIB_TCP6ROW_OWNER_PID`).
- Same with **`GetExtendedUdpTable`** + `UDP_TABLE_OWNER_PID`.
- Poll on a ~1 s timer; **diff** against the previous snapshot to detect **new connections** → feeds Ask-to-Connect prompts and the "new app connected" alert.
- Map `OwningPid` → image path via `QueryFullProcessImageName` / `System.Diagnostics.Process`.
- Correlate these remote `IP:port` pairs with the ETW byte counts to attribute bytes to concrete endpoints for GeoIP + reverse DNS.

### 2.7 IP → Country GeoIP
- Bundle **MaxMind GeoLite2 Country** (`GeoLite2-Country.mmdb`) in `%ProgramData%\OpenWire`.
- Use **`MaxMind.GeoIP2`** (typed models) over **`MaxMind.Db`** (raw `.mmdb` reader).
- Construct **one long-lived `DatabaseReader`** and reuse it (construction parses metadata and is expensive; reads are thread-safe). Call `reader.Country(ip)` per lookup.
- **Skip** RFC1918 / loopback / link-local ranges → label **"Local"**.
- Refresh weekly (MaxMind republishes Tuesdays) via GeoIP Update; requires a free GeoLite2 license key. Make the GeoIP DB path/source **configurable** (a known GlassWire user request).

### 2.8 Reverse DNS / Host Resolution
- Simple path: **`System.Net.Dns.GetHostEntryAsync(ipAddress)`** (async — PTR lookups block/time out) → `IPHostEntry.HostName`.
- Wrap in a **bounded, TTL'd concurrent cache** (CDNs reuse IPs) with capped concurrency.
- Advanced path: **`DnsClient.NET`** `LookupClient.QueryReverse` for custom resolver, explicit timeouts, no OS-cache quirks.
- Unresolved IPs are labeled by **GeoIP country** instead.

### 2.9 LAN Device Discovery & Fingerprinting
- **Enumerate ARP/neighbor table:** `iphlpapi GetIpNetTable2`/`GetIpNetTable`, or `SendARP`, for IP↔MAC.
- **Vendor:** resolve MAC **OUI** (first 3 bytes) against a bundled IEEE OUI/`mac-vendors` list → "Apple", "Amazon", "Ubiquiti", etc. (Note the OUI maps to the NIC chip vendor, so labels can be misleading — hence user relabeling in §3.5.)
- **Deep fingerprinting (stretch → recommended):** run active-probe banners through **Rapid7 Recog** XML fingerprints (BSD-2): mDNS/Bonjour (`_services._dns-sd`), SSDP/UPnP device descriptors, NBNS/LLMNR names, HTTP `Server` headers, SNMP `sysDescr`. Recog turns banners into device type/model/vendor. A .NET consumer can load the Recog XML and apply the regexes directly (reference: `runZeroInc/recog-go`).
- **Liveness / join-leave:** ping-sweep / TCP-connect the subnet.

### 2.10 Storage (SQLite, multi-resolution rollups)
- Engine: **`Microsoft.Data.Sqlite`** with **`PRAGMA journal_mode=WAL`** (concurrent GUI reads while the service writes; matches GlassWire's `-wal`/`-shm` sidecars).
- Location: `C:\ProgramData\OpenWire` (all-users, survives GUI restarts); **configurable** data directory.
- **Databases / tables:**
  - `openwire.db` — config, firewall rules mirror, firewall profiles, device inventory, alert log, per-app metadata.
  - `stats\` — **time-partitioned rollup DBs** mirroring GlassWire's `glasswire_stats_1sec_<epoch>.db` / `glasswire_stats_30sec_*.db` design:
    - **Hot 1-second table:** per-second, per-process byte deltas (in/out).
    - Background rollup job aggregates → **30 s → hourly → daily** tables, then **prunes** fine-grained partitions.
    - **Partition by time** (per-file or per-table keyed by Unix epoch) so pruning old history is a cheap partition/file drop.
- This 1 s/30 s split is what lets the graph zoom from live rate to month volume cheaply and keeps disk bounded.

### 2.11 Data Model (core entities)
- **Process/App:** `AppId`, image path, display name, icon, publisher, version, VirusTotal state, OpenWire Score.
- **Flow/Connection:** `AppId`, local `IP:port`, remote `IP:port`, protocol, state, first-seen, bytes in/out, resolved host, country.
- **StatBucket:** `AppId`, timestamp, resolution, bytesIn, bytesOut.
- **FirewallRule:** `AppId`, direction, action, scope (endpoint/port), profileId, enabled.
- **Profile:** `ProfileId`, name, active flag, rule set.
- **Device:** `mac`, `ip`, vendor, deviceType, hostName, infoString, customName, customIcon, description, firstSeen, lastSeen, online.
- **Alert:** `id`, type, timestamp, `AppId`/host/device ref, severity, message, read/unread, snoozed.

---

## 3. Screens

OpenWire uses GlassWire's 3.x top-level navigation with clean-room names. Classic GlassWire names are shown in parentheses for mapping.

**Top navigation tabs:** `Traffic` (Traffic Monitor) · `Firewall` (Protect) · `Usage` · `Alerts` (Log Analysis) · `Things` (Network Scanner) · `Settings`

> Note: GlassWire 3.x folds Graph + Usage into a single "Traffic Monitor" tab with Graph/Usage/Map sub-views. OpenWire keeps **Graph** and **Usage** as distinct primary screens for clarity (per this spec's required screen list) but treats **Map** as a sub-view of the Graph/Traffic screen.

A persistent **top status/navigation bar** spans all screens:
- **Left:** the OpenWire app menu (hamburger) — Themes/Skins, Show Mini Viewer, Settings.
- **Center/right:** the main tab strip.
- **Right:** the **machine selector** (local machine or a remote host/server) and a global **alerts badge** ("New" counter).

---

### 3.1 Graph Screen (Traffic Monitor → Graph)

#### Purpose
The flagship landing screen: a live, animated, filled-area chart of all bandwidth over time, split into incoming (download) vs outgoing (upload). It doubles as a forensic **Network Time Machine** — scrub back through history, click any spike to pause and reveal which apps/hosts caused it, and see security alerts plotted on the timeline.

#### Layout — three stacked horizontal regions (+ on-demand fourth)
```
┌───────────────────────────────────────────────────────────────────────┐
│ CONTROL/STATUS STRIP                                                    │
│  [All][Apps][Traffic][Publishers][Country]        [5m][3h][24h][W][M]   │
│  (view-mode filter, top-LEFT)              (time-range presets, top-RIGHT)│
├───────────────────────────────────────────────────────────────────────┤
│  6 Kb/s ↤ scale/current-speed (UPPER-LEFT, printed on canvas)  [⏸ 📷]   │
│                                                                         │
│                    LARGE REAL-TIME FILLED-AREA GRAPH                     │
│               (auto-scales vertically; animates continuously)           │
│                                                                         │
│  (↑) outgoing   (↓) incoming     ← direction-legend circles, LOWER-LEFT │
├───────────────────────────────────────────────────────────────────────┤
│ TIMELINE SCRUBBER  [◀handle═══════════handle▶]   •  •   •  ← alert dots │
├───────────────────────────────────────────────────────────────────────┤
│ (ON CLICK) APP/HOST BREAKDOWN — icons + names + up/down usage, sorted   │
└───────────────────────────────────────────────────────────────────────┘
```

#### Full Feature List
1. **Real-time area graph** — live, continuously animating filled-area/line chart. Auto-scales vertically to current peak; new activity enters and advances over time.
2. **Incoming vs outgoing (two-color)** — two directions as stacked/overlaid filled areas in two distinct skin colors; overlap blends. Legend is the two arrow circles.
3. **Up/Down arrow direction indicators** — two small colored circles at the graph's **lower-left**: a circle with an **UP arrow** (outgoing/upload) and one with a **DOWN arrow** (incoming/download). Serve as color legend **and** live up/down speed counters. ("Up-arrow color = outgoing color on the graph; down-arrow color = incoming.")
4. **Upper-left scale / current-speed readout** — printed on the canvas (e.g. `6 Kb/s`, `3 Mb/s`, or a peak like `~20 KB`). **Units change with time range:** 5-min view → rate (B/s, KB/s); 3 h / 24 h / week / month → volume (MB). Window resize rewidths time buckets (spikes split/merge) and recalculates the scale value.
5. **Time-range selector (top-right):** `5 minutes` · `3 hours` · `24 hours` · `Week` · `Month`. 5 min = fast real-time rate; 24 h/week/month = slow-moving MB volume. **"Unlimited" is not a preset** — it refers to how far back the scrubber reaches, bounded by retention (OpenWire = configurable, default unlimited).
6. **Timeline scrubber / dual sliders (Network Time Machine)** — a mini-timeline strip along the **bottom** with two draggable handles (left/right). Grab and move to zoom/pan the main graph to any past window. Combine with the range presets: pick a range, then narrow onto a date/time to investigate a spike or alert.
7. **Click-a-spike to reveal apps & hosts** — **left-click** anywhere (or set the vertical inspection line on a spike and click) to **pause** the graph and drop a **vertical marker**; a breakdown slides in **below** the graph showing app + host icons/names involved at that moment, **sorted by data usage**. Entries expand to show which hosts each app talked to; hosts are auto-resolved and shown with **country flag**. **Right-click = resume** live graph. Hovering surfaces a **pause + snapshot** icon at the canvas top-right.
8. **View-mode filter (top-left): `All` · `Apps` · `Traffic` · `Publishers` · `Country`** — re-segments the same throughput. All = total; Apps = per-application bands; Traffic = per traffic type/protocol (FTP/HTTP…); Publishers = per software publisher; Country = per destination country.
9. **Alert markers on the timeline** — small dots on the bottom timeline are OpenWire alerts (new app, first connection, security events). Click a dot to jump to that event in temporal context.
10. **Idle / away shaded sections** — dark/grey shaded regions stamped with a **clock icon** mark periods when the computer was idle/away during that activity (distinguishes background from active use).
11. **Machine selector (remote host)** — switch which computer's traffic is graphed (local or a remote PC/server). Re-points the entire graph to that host's up/down traffic.
12. **Map sub-view** — interactive world map of live & historic connections by country with **animated connection lines**. (Graph/Usage/Map are switchable within the Traffic area.)
13. **Themes / skins** — change incoming/outgoing colors & overall skin (fixes the two-tone-blend readability complaint). See §6.
14. **Mini Viewer** — small always-on-top desktop mini-graph. Toggle transparency/position via a filter icon; drag corners to resize.

#### Controls / Widgets
- View-mode filter buttons · time-range preset buttons · large graph canvas · upper-left scale readout · two direction-legend circles · bottom timeline strip with two slider handles · alert dots · idle grey blocks with clock icon · hover pause/snapshot icon · vertical inspection line · click-driven app/host breakdown list (icons + names + per-direction usage + country flags) · machine selector · Graph/Usage/Map sub-view switch.

#### Data Displayed
- Live incoming & outgoing bandwidth over time; current up/down rate + auto-scaled peak; per-app / per-traffic-type / per-publisher / per-country composition; which apps + hosts caused a specific spike; resolved host names with country; security alerts positioned in time; idle/away periods; history back as far as retention allows.

#### Interactions
- **Left-click** = inspect/pause + marker + breakdown. **Right-click** = resume live.
- **Drag** the two bottom sliders = zoom/pan history.
- **Click** a preset = change range (and rate↔volume units).
- **Click** a view-mode button = re-segment bands.
- **Click** an alert dot = jump to event.
- **Hover** canvas = reveal pause/snapshot.
- **Switch** machine selector = re-point graph to another host.

---

### 3.2 Firewall Screen (Protect)

#### Purpose
See every app using the network and control its access. A dense table with per-app allow/block split into incoming and outgoing, showing the remote hosts each app connects to, exposing a global firewall **mode** pull-down plus named **profiles**.

#### Layout
```
┌───────────────────────────────────────────────────────────────────────┐
│ [Mode ▼: Off / Click to Block / Ask to Connect / Block all]            │
│                       [Profile ▼: Home ▾]  [+ Create]     [🔍 search]   │
│ [ Master firewall: ON/OFF ]                                             │
├──────┬─────────────┬────────────┬──────────┬──────────┬──────┬─────────┤
│ 🔥   │ App (icon+  │ Hosts      │ Graph    │ In Conn. │ Out  │ VirusTot│
│      │ name)       │ (flags)    │ (spark)  │ [Allow▾] │[Allow▾]│ (opt) │
├──────┼─────────────┼────────────┼──────────┼──────────┼──────┼─────────┤
│  ●   │ chrome.exe  │ 3 hosts 🇺🇸│ ▁▂▅▂     │ Allow    │ Allow│ 0/72   │
│  🔥  │ sketchy.exe │ 1 host  🇷🇺│ ▁▁█      │ Blocked  │Block │ 8/72 ⚠ │
└──────┴─────────────┴────────────┴──────────┴──────────┴──────┴─────────┘
```

#### Full Feature List
1. **Firewall mode pull-down (top-left):** `Off` / `Click to Block` / `Ask to Connect` / `Block all`.
   - **Off** — firewall disabled; all apps connect freely; OpenWire relinquishes control of Windows Firewall.
   - **Click to Block** (default) — everything connects by default; you manually block via the flame icon. In this mode **incoming is blocked, outgoing allowed** by default.
   - **Ask to Connect** — OpenWire prompts Allow/Deny the first time a new app tries to connect.
   - **Block all** — lockdown: instantly cuts off all connectivity (for stepping away). Blocks persist across app updates. *(There is no separate padlock button — "lockdown" is this mode value.)*
2. **Master firewall on/off toggle** — block rules only apply when the master toggle is on.
3. **Per-app allow/block via flame (fire) icon** — each row has a flame; click to block (icon lights up/colored = blocked), click again to unblock; instantaneous. OpenWire's own components default to Allowed and are protected. *(The flame is the single block control — there is no per-app padlock.)*
4. **Separate In / Out control** — independent `In Connections` and `Out Connections` dropdowns per app, each `Allow` or `Blocked` (block inbound only, outbound only, or both). Requires master toggle on.
5. **Ask-to-Connect prompt** — when a new/unknown app actually **sends traffic** (not merely launches), a dialog asks **Allow / Deny**. Extended options where supported: **Allow once / Allow always / Block once / Block always**. Apps already allowed before the mode was enabled are not re-prompted; certain system apps are exempt to keep Windows working. On first enabling, expect many "New" prompts that taper off. **Honest caveat (§2.5):** because enforcement is a Windows Firewall rule (not an inline callout), a few first packets may escape before the block lands.
6. **Hosts column** — external servers/domains/IPs each app contacts, annotated with **country flags**. Click an app/host to drill into per-host traffic.
7. **Per-app activity sparkline (Graph column)** — network activity over time; turns **grey when the device is idle** (spot unexpected overnight activity).
8. **Firewall Profiles** — create/name/save multiple profiles (e.g. `Home`, `Surfing`, a locked-down metered profile), each holding its own rule set; switch **manually** for public Wi-Fi vs home. "Create" offers a **"use current firewall rules"** starting point. *(Auto-switch by detected network is a documented GlassWire non-feature on desktop; OpenWire may implement it as a stretch goal — see §4/§7.)* Known quirks to handle: deleting an inactive app removes it from all profiles; blocking in one profile can surface an app in another; switching slows with very large block lists.
9. **VirusTotal column (optional, off by default)** — scans each app file against VirusTotal; shows detection ratio + link to the report. Requires a user-supplied VirusTotal API key.
10. **Search / find app** — search icon → type to filter a long list.
11. **Per-row reset (X on hover)** — hover a row, click **X** to delete/reset an app's decision; it then behaves as never-seen and will prompt again under Ask-to-Connect.

#### Controls / Widgets
- Mode pull-down · master toggle · profile selector + Create · search icon · app table rows (icon + name) · Hosts column (flags) · Graph sparkline column · In/Out `Allow/Blocked` dropdowns · flame icon · optional VirusTotal column · hover X · Ask-to-Connect dialog (Allow/Deny [/once/always]).

#### Data Displayed
- App name + icon; remote hosts/IPs/domains with country flags; In allow/block state; Out allow/block state; block status (flame color); per-app activity graph; traffic in/out volume; download/upload speed; app version; **OpenWire Score / VirusTotal** result (optional).

#### Interactions
- **Click flame** = toggle block. **Change In/Out dropdown** = set direction policy. **Change mode** = global behavior. **Create/switch profile** = swap rule sets (manual). **Enable VirusTotal** = per-app scan on hover "Analyze". **Hover + X** = reset app. **Ask-to-Connect** = modal Allow/Deny on new outbound flow.

---

### 3.3 Usage Screen (Traffic Monitor → Usage)

#### Purpose
Answer "which apps / hosts / traffic types / countries consumed the most bandwidth" over a chosen window, with resolved hosts and countries — the tabular complement to the Graph's visualization.

#### Layout
```
┌───────────────────────────────────────────────────────────────────────┐
│ [Apps | Traffic Types | Hosts | Countries]        [Day | Week | Month | Custom]│
├───────────────────────────────────────────────────────────────────────┤
│  Rank  Name (icon/flag)      Host / country      ▼ Down    ▲ Up   Total │
│  1     chrome.exe            a-lb.example.net 🇺🇸  1.2 GB   240 MB 1.44 GB│
│  2     backup.exe            s3.amazonaws.com 🇺🇸  0 B      3.1 GB 3.1 GB │
│   └─ (expand) → hosts contacted by this app, per-host in/out           │
└───────────────────────────────────────────────────────────────────────┘
```

#### Full Feature List
1. **Breakdown-dimension switch:** `Apps` · `Traffic Types` · `Hosts` · `Countries`.
2. **Time-window selector:** `Day` · `Week` · `Month` · **Custom** (date range).
3. **Sortable columns:** name, download volume, upload volume, total; sorted by data usage by default.
4. **Resolved hosts + GeoIP country** — reverse-DNS names with country flags; raw IP shown alongside where unresolved.
5. **Expandable rows** — expand an app to see the hosts it talked to (per-host in/out); expand a host to see which apps used it.
6. **Machine selector aware** — reflects the currently selected local/remote machine.
7. **Export** (OpenWire addition) — export the current view to CSV/JSON.

#### Data Displayed
- Per-app cumulative bytes sent/received over day/week/month/custom; per-traffic-type volume; per-host volume with resolved name + country; per-country totals; process image path/PID on expand.

#### Interactions
- Switch dimension; switch/set time window; click a column header to sort; expand/collapse rows; click a host to inspect; export.

---

### 3.4 Alerts Screen (Log Analysis)

#### Purpose
Chronological, forensic list of every OpenWire event — new apps, first connections, and the full security-detection set — filterable and drill-downable. The deep-forensics counterpart to the Graph's timeline dots.

#### Layout
```
┌───────────────────────────────────────────────────────────────────────┐
│ Filter: [ By App ▾ | By Type ▾ ]    [Mark all read] [Snooze 24h] [Clear]│
├───────────────────────────────────────────────────────────────────────┤
│ ● 14:02  New Application       chrome_updater.exe → 3 hosts   [scan][x] │
│ ● 13:55  New network device    "Amazon" 10.0.0.42 (aa:bb..)   [name]    │
│   09:31  DNS server changed     8.8.8.8 → 1.1.1.1                       │
│   02:10  RDP Connection         from 203.0.113.9 🇩🇪                     │
│   01:44  WiFi Evil Twin         SSID "HomeNet" new BSSID                │
└───────────────────────────────────────────────────────────────────────┘
```

#### Full Feature List
1. **Chronological alert list** — newest first; each row: timestamp, alert type, initiating program/host/service/device, short message, small app/device icon.
2. **Switchable grouping/filter:** by **App** or by **Type**.
3. **Unread badges** — red "new" badge/dot on unread alerts; global "New" counter in the top bar drops as OpenWire learns normal behavior.
4. **Per-alert drill-down** — click an alert to see full detail: the host/service/program that initiated the connection, file path, and context. Security alerts link into the relevant screen (e.g. a device alert → Things row; a suspicious-host alert → Usage/host detail).
5. **Per-alert actions** — for app alerts: **virus-scan** button (VirusTotal), **allow/block** shortcut, delete/reset (**X**). For device alerts: **name device** shortcut.
6. **Snooze** — snooze notifications (24 h) globally.
7. **"While you were away" summary** — a recap entry summarizing network activity that occurred while the user was gone.
8. **Timeline correlation** — every alert corresponds to a dot on the Graph timeline; selecting one can jump to that moment on the Graph.

#### Data Displayed
- Timestamp; alert type; initiating program/host/service/device; app icon; file path (on drill-down); severity/read state; snooze state.

#### Interactions
- Toggle by-App/by-Type filter; click to expand; act (scan/allow/block/name/delete); mark read; snooze; clear; jump to Graph.

*(Complete alert-type enumeration is in §5.)*

---

### 3.5 Things Screen (Network Scanner)

#### Purpose
Local-network device monitor — "Who's on your Wi-Fi or Network?" Scans the LAN and lists every discoverable device (PCs, phones, smart plugs, cameras, printers, routers, TVs, IoT), with vendor/IP/MAC/type/host-name/first-seen, keeps history of devices ever seen (including offline), lets you rename/re-icon them, and alerts on join/leave.

#### Layout
```
┌───────────────────────────────────────────────────────────────────────┐
│ [Scan]   Filter: [All ▾ | Available]   [i network info]   scan-speed ◁──▷│
├──────┬───────────────┬───────────────┬────────┬──────┬───────────┬──────┤
│ icon │ Name (custom) │ Network name  │ Info   │ IP   │ MAC       │ First│
├──────┼───────────────┼───────────────┼────────┼──────┼───────────┼──────┤
│ 🖨    │ HP – Printer  │ HPA1B2C3      │ ...    │ .12  │ aa:bb:..  │ 6/28 │
│ 📷    │ Front Cam     │ ipcam-04      │ ...    │ .40  │ cc:dd:..  │ 6/30 │
│                                              (hover row → ⋯ menu)       │
└──────┴───────────────┴───────────────┴────────┴──────┴───────────┴──────┘
```

#### Full Feature List
1. **LAN device discovery (Scan)** — discovers devices on the same segment. On first use the list may be blank until **Scan** is pressed; OpenWire auto-scans once on install. What's visible depends on topology (router isolation limits results to your LAN).
2. **Scheduled/auto re-scan** — interval set in Settings → General ("Things interval scan", default ~30 min, adjustable e.g. to 5 min).
3. **Scan-speed slider** — left = slower but more accurate (more threads/time), right = very fast (may miss devices); option to disable specific scan protocols to avoid conflicts/false positives with enterprise security software.
4. **Per-device info display** — vendor (MAC OUI lookup), IP, MAC, device-type icon, network/host (DNS) name, an **Info** identifier string, **first-seen / last-scanned** date, and online/offline availability. Columns are **hover-sortable**.
5. **Device-type identification & fingerprinting** — vendor from OUI (can be misleading — a PlayStation may show as "Liteon technology"); host/DNS name from the network; enhanced heuristics + Recog fingerprints recognize specific device types/models (routers, cameras, etc.).
6. **Custom naming, description & icon** — override auto-detected identity: custom name + description + device-type icon (e.g. "Hewlett Packard – Printer"). Persist per device. *(Ensure custom labels appear in alert text — a documented GlassWire shortcoming to fix.)*
7. **New/unknown device alerts ("Things Monitor")** — raises **"New network device"** ("A new device just joined your network or WiFi."). Configurable to notify only for **new unknown devices** (recommended) or for **every join/leave** (noisier).
8. **Device history — offline/old devices ("All" view)** — keeps a record of every device ever seen; the **All** filter shows current + offline history; **Available** narrows to online-now. Offline devices remain with last-known details.
9. **Local network info panel (`i` icon)** — your own connection details: current IP(s), your MAC, Gateway, DNS servers.
10. **Open device in browser** — connect to a device via its IP directly from the UI (router/camera admin pages).
11. **Forget device** — remove/clear a device entry (per-row `⋯` menu).

#### Controls / Widgets
- Scan button · All/Available filter · `i` network-info icon · scan-speed slider · device table (leading type icon + columns) · hover `⋯` menu (device name + description, change icon, open in browser, forget device) · sortable headers.

#### Data Displayed
- Manufacturer/vendor (OUI); LAN IP; MAC; device type + icon; custom label; description; network/host name; Info string; first-seen/last-scanned; online status; local self-info (own IP(s)/MAC/Gateway/DNS).

#### Interactions
- Press Scan; set filter; adjust scan-speed; hover a row → `⋯` → rename/redescribe/re-icon/open-in-browser/forget; sort columns; open `i` panel; receive/act on join/leave alerts.

---

### 3.6 Settings Screen

#### Purpose
Central configuration for the service and GUI: firewall behavior, alert/monitor toggles, scanning, GeoIP, remote access, storage, themes, and the mini viewer.

#### Sections & Options

**General**
- Data directory / DB + log location (default `%ProgramData%\OpenWire`; configurable).
- **Things interval scan** — auto re-scan interval (default ~30 min).
- Scan-speed / threads slider; disable specific scan protocols.
- History retention window (OpenWire default: **unlimited**; configurable cap).
- Update interval / refresh rate for live views.
- Start with Windows; run in tray; persistent speed-meter notification.

**Firewall**
- Firewall **mode**: Off / Click to Block / Ask to Connect / Block all.
- Master firewall toggle.
- Default direction behavior (Click-to-Block: In blocked, Out allowed).
- **Firewall Profiles**: create, name, switch (manual; auto-switch-by-network is a stretch goal).
- "Clean up OpenWire-created Windows Firewall rules" maintenance action (§2.5).

**Security (alert/monitor toggles)** — each monitor has **two independent switches: enable the monitor** and **raise a desktop notification**:
- New Application Monitor
- Things Monitor (notify me / report only new unknown devices / alert on every join+leave)
- Network Scanner (alert when a new unknown device joins)
- DNS Server Settings Monitor
- Proxy Monitor
- Internet Access Monitor
- ARP Spoofing Monitor *(stretch/feasibility-flagged, §4)*
- Evil Twin (Wi-Fi) Monitor *(stretch/feasibility-flagged)*
- RDP Connection Detection
- Suspicious Hosts / Known-threat detection
- System File Changed Monitor
- Device List Change Monitor
- Application Info Monitor
- Remote Client Connection alert
- *(Camera & Mic monitor — documented historically, disabled/removed; see §4.)*

**VirusTotal**
- Enable manual file analysis by VirusTotal.
- Automatically analyze all apps with network activity.
- Personal VirusTotal API key field.

**GeoIP**
- Bundled `GeoLite2-Country.mmdb` path (configurable/swappable).
- GeoLite2 license key + weekly update toggle.

**Remote — Server List (client side)**
- Add remote machines: **Name + IP/hostname + password** (default port `:7010`; append `:XXXX` for custom port). Machine appears in the top-bar machine selector.

**Remote — Remote Access (endpoint side)**
- Enable "Remote Access" → **Unlock** (OS admin password) → allow-access toggle → set access password → optional custom port (default `:7010`). Raises a "Remote Client Connection" alert on connect.

**Appearance**
- **Themes / skins** — change incoming vs outgoing graph colors and overall look (fix two-tone readability; e.g. a "blue & peach" bordered skin). See §6.
- Dark theme (default) / light theme.
- **Show Mini Viewer** — desktop always-on-top mini-graph; transparency/position filter; drag corners to resize.

**Alerts**
- Global snooze (24 h).
- Notification style (toast / in-app only).

---

## 4. Security-Feature Suite

Each capability is tagged **Driverless-feasible**, **Needs driver**, or **Stretch goal**, with the enforcement/detection mechanism.

| # | Feature | Detection mechanism (OpenWire) | Feasibility |
|---|---------|-------------------------------|-------------|
| 1 | **ARP Spoofing Detection** | Monitor the ARP/neighbor table (`GetIpNetTable2`) for the gateway IP mapping to a changed/duplicate MAC, or two IPs sharing one MAC (MITM). Compare device MACs. | **Driverless-feasible** (passive ARP-table polling). Continuous passive ARP sniffing for higher fidelity is a **stretch goal**. |
| 2 | **DNS Server Settings Monitor** | Record configured DNS servers per adapter (IP Helper / registry / `GetAdapterAddresses`); diff on change → alert. Guard against the known false-positive of re-firing when IPs are actually unchanged. | **Driverless-feasible.** |
| 3 | **Proxy Monitor** | Watch WinINET/WinHTTP proxy settings for changes. | **Driverless-feasible.** |
| 4 | **Internet Access Monitor** | Watch NLM (Network List Manager) connectivity state changes. | **Driverless-feasible.** |
| 5 | **Evil-Twin (duplicate Wi-Fi) Detection** | Use the Native Wifi API (`wlanapi.dll` — `WlanGetNetworkBssList`/scan) to enumerate nearby BSSIDs; alert when a **new BSSID/hardware appears advertising your current SSID**, or when your network **suddenly loses its password/encryption**. | **Driverless-feasible** on machines with a Wi-Fi adapter (Native Wifi API). No driver required. Marked **stretch** where no Wi-Fi radio is present. |
| 6 | **Webcam & Microphone Monitor** | *Historically:* alert when a network app accessed the camera/mic. | **Removed / not shipping.** Windows API changes removed a reliable detection surface (GlassWire itself dropped it). Documented for parity; **not in the product**. A best-effort registry-based "app used camera/mic recently" indicator is a low-priority **stretch goal**, explicitly not a real-time monitor. |
| 7 | **RDP Connection Detection** | Detect new inbound RDP sessions in real time: watch TCP 3389 (and configured RDP port) in the connection table for new inbound established flows, and/or subscribe to Windows Security/TerminalServices event logs. | **Driverless-feasible.** |
| 8 | **Suspicious Hosts / Known-threat detection** | Match remote IPs/domains from the connection table against a bundled/updatable known-malicious host list; alert on contact. | **Driverless-feasible** (list-based). Threat-feed updating is optional. |
| 9 | **System File Changed / Device List Change / Application Info monitors** | Watch network-related system files, the OS network-device list, and app metadata for malware-style tampering; alert on change. | **Driverless-feasible.** |
| 10 | **VirusTotal scanning** | Hash network-active executables; send hash first, upload full file only if unknown; show detection ratio + report link. Manual or auto mode; user-supplied API key. | **Driverless-feasible** (requires network + user API key). |
| 11 | **OpenWire Score** (GlassWire Score analog) | Per-app 1–5 reputation. GlassWire aggregates cross-userbase prevalence/sentiment. **OpenWire has no telemetry backend**, so Score is computed from **local signals + optional community/open reputation datasets** (prevalence heuristics, signed-publisher trust, VirusTotal ratio). | **Driverless-feasible**, but **reduced fidelity stretch goal** (no proprietary global userbase). Clearly label as heuristic. |
| 12 | **Anomaly Detection** | Flag when an app's traffic is unusually high/low vs its own historical baseline (OpenWire computes locally, since there's no cross-userbase average). Shown as an `i`-icon left/right slider next to up/down values. | **Driverless-feasible** (local baseline). Cross-userbase comparison is out of scope. |
| 13 | **New Application Monitor** | Diff the per-PID connection table; first time an unknown app sends traffic → alert (feeds Ask-to-Connect). | **Driverless-feasible.** |
| 14 | **New Network Device Monitor (Things)** | LAN scan join/leave detection (§2.9). | **Driverless-feasible.** |
| 15 | **Remote server / multi-PC monitoring** | Install OpenWire on each endpoint; one GUI connects to many services over the IPC contract (name + IP/host + password, default `:7010`). Raises "Remote Client Connection" alert. | **Driverless-feasible.** OpenWire = **unlimited** remote connections (no tier cap). |
| 16 | **Ask-to-Connect firewall gate** | New outbound flow detected via ETW/`GetExtendedTcpTable`; hold/prompt; persist Allow/Block as a Windows Firewall rule. | **Driverless-feasible**, with the **first-packet caveat** (§2.5). True first-packet block = **needs WFP-callout driver (stretch)**. |
| 17 | **Block all / Lock Down** | Mode value that writes block-all Windows Firewall rules. | **Driverless-feasible.** |
| 18 | **Auto-switch Firewall Profiles by network** | Detect active network (SSID/gateway) and swap profiles automatically. | **Stretch goal** (GlassWire does not do this on desktop). |
| 19 | **High-fidelity per-packet capture / first-packet block** | WFP callout driver at ALE/stream layers. | **Needs driver + EV cert (stretch).** |
| 20 | **Mobile companion (Android)** | Separate free app: live Wi-Fi/mobile data graph, data-plan limits, per-app firewall via on-device `VpnService`, new-app alerts, Wi-Fi vs mobile profiles. Does **not** carry desktop threat detections. | **Out of scope for v1 (roadmap/stretch).** |

**Feasibility summary:** the overwhelming majority of GlassWire's security suite is **fully achievable driverless in .NET**. The only items that genuinely need a kernel driver are (a) exact per-packet byte accounting beyond ETW granularity and (b) inline first-packet blocking — both explicitly **stretch goals**. Webcam/mic is intentionally omitted. OpenWire Score is degraded (no proprietary global userbase) and labeled heuristic.

---

## 5. Complete Alert-Type Enumeration

Every alert has: a **type**, an **enable toggle**, a separate **desktop-notification toggle**, a **timeline dot** on the Graph, and a **row** in the Alerts screen. Severity and default-on state noted.

| Alert Type | Trigger / meaning | Default | Notes |
|------------|-------------------|---------|-------|
| **New Application Monitor** | An unknown app accesses the network for the first time. | On | Feeds Ask-to-Connect; count tapers as behavior is learned. |
| **New Network Device** | "A new device just joined your network or WiFi." | On | Configurable: only new-unknown vs every join/leave. |
| **Device List Change** | The set of network devices on your system changed. | On | Malware-tamper indicator. |
| **DNS Server Settings Changed** | Configured DNS server IP(s) changed. | On | Guard the known false-positive re-fire. |
| **Proxy Monitor** | Proxy configuration used/changed. | On | |
| **Internet Access Monitor** | Internet-access/connectivity state changed. | On | |
| **ARP Spoofing** | Possible ARP-table poisoning / MITM on the LAN. | On (if feasible) | Guidance: compare device MACs. |
| **WiFi Evil Twin Alert** | New Wi-Fi hardware/BSSID appears advertising your current SSID, or your network suddenly loses its password. | On (Wi-Fi present) | Can fire in batches; guidance: check nearby AP MACs. |
| **RDP Connection Detection** | New inbound RDP session to this PC/server, in real time. | On | Especially relevant for remote servers. |
| **Suspicious Hosts** | Your machine contacted a known-malicious host/IP/domain. | On | List-based. |
| **System File Changed** | A network-related system file changed (possible malware). | On | |
| **Application Info Monitor** | An app's info/metadata changed (possibly malware-caused). | On | |
| **Camera & Mic Monitor** | *(Historical)* A network app accessed the webcam/mic. | Removed | Not shipping; documented for parity only. |
| **Remote Client Connection** | Someone connected to this machine's OpenWire service remotely. | On | Fired on remote-access connect. |
| **While You Were Away** | Summary recap of network activity that occurred while the user was idle/away. | On | Aggregate/digest entry. |
| **Anomaly Detection** | An app's traffic is abnormally high/low vs its local baseline. | On | Slider indicator on Firewall/Security detail. |
| **VirusTotal Detection** | A network-active app flagged malicious by VirusTotal. | Off (needs key) | Shows detection ratio + report link. |

---

## 6. Visual Design System

### 6.1 Design Language
Dark-themed, data-dense **command-console** aesthetic dominated by the large animated area graph. Flat surfaces, restrained chrome, crisp mono-ish numerics for readings, small country-flag and app-icon glyphs, and status conveyed by color-state (lit flame, grey idle blocks). Skins are user-swappable to solve the two-tone-overlap readability complaint.

### 6.2 Color Palette (exact-feel hex)

**Dark theme — base surfaces**
| Token | Hex | Use |
|-------|-----|-----|
| `--bg-app` | `#12161C` | App background (deep slate) |
| `--bg-panel` | `#1A1F27` | Panels, table backgrounds |
| `--bg-elevated` | `#222834` | Cards, dropdowns, dialogs |
| `--bg-graph` | `#0E1218` | Graph canvas (darkest, for contrast) |
| `--border` | `#2C3440` | Hairline separators, table grid |
| `--border-strong` | `#3A4453` | Focus outlines, active tab underline |

**Text**
| Token | Hex | Use |
|-------|-----|-----|
| `--text-primary` | `#E6EAF0` | Primary text |
| `--text-secondary` | `#9AA4B2` | Secondary/labels |
| `--text-muted` | `#5E6B7A` | Disabled, idle timestamps |

**Graph directional colors (default skin — "Teal & Amber", bordered for readability)**
| Token | Hex | Use |
|-------|-----|-----|
| `--in-fill` | `#2FB8C6` (teal) | Incoming/download fill |
| `--in-line` | `#5FE3EF` | Incoming border line |
| `--out-fill` | `#E8A13A` (amber) | Outgoing/upload fill |
| `--out-line` | `#FFC46B` | Outgoing border line |
| `--overlap-blend` | `~#8FAE7E` | Where fills overlap (blended) |

> The default ships **with border lines** on each area so the two directions stay distinct (unlike GlassWire's criticized borderless two-tone blend). Alternate bundled skins: **"Blue & Peach"** (`#4A8CFF` / `#FFB59A`), **"Classic GlassWire"** (two-tone, borderless, overlap → orange band), **"Mono Green/Red"** (`#3CCB7F` in / `#F0554E` out), and a **Light** variant.

**Status / semantic**
| Token | Hex | Use |
|-------|-----|-----|
| `--flame-blocked` | `#F0554E` (red-orange) | Lit flame = blocked |
| `--flame-idle` | `#4A5461` (grey) | Unlit flame = allowed |
| `--alert-new` | `#FF4D4D` | Unread alert badge / "New" counter |
| `--alert-dot` | `#FFC46B` | Timeline alert dots |
| `--idle-shade` | `#171B22` @ 70% | Idle/away grey blocks (with clock icon) |
| `--success` | `#3CCB7F` | Allowed / safe / VT clean |
| `--warning` | `#E8A13A` | Anomaly / caution |
| `--danger` | `#F0554E` | Suspicious host / VT hit |
| `--accent` | `#4A8CFF` | Primary buttons, links, active selection |
| `--flag-ring` | `#2C3440` | Country-flag border |

### 6.3 Typography
- **UI sans-serif:** Segoe UI Variable (Windows-native) → fallback Inter / system-ui.
- **Numeric readouts (graph scale, speeds, byte totals):** a tabular-figures font — **Cascadia Mono** or Segoe UI with `font-variant-numeric: tabular-nums` — so digits don't jitter as values animate.
- **Scale:** Display (graph scale readout) 22–28px semibold · H1 tab titles 18px semibold · Table headers 12px uppercase, `letter-spacing: 0.04em`, `--text-secondary` · Body/rows 13–14px · Captions/timestamps 11–12px `--text-muted`.
- **Weights:** 400 body, 600 emphasis/headers, 700 the live-speed readout.

### 6.4 Iconography
- **App icons:** the real extracted `.exe` icon per row.
- **Direction legend:** two filled circles — up-arrow (outgoing color), down-arrow (incoming color) — doubling as live counters.
- **Device-type glyphs (Things):** computer, laptop, phone, tablet, printer, camera, router, switch, smart-plug/outlet, TV, speaker, game-console, NAS, generic-IoT; user-swappable.
- **Country flags:** small rounded-rect flag chips next to hosts/countries with a `--flag-ring` border.
- **State icons:** flame (block), clock (idle block), pause + snapshot (graph hover), shield (firewall/security), stopwatch (data-plan/interval), `i` (info), `⋯` (per-row menu), 🔍 (search), gear (settings), star ×5 (OpenWire Score).
- Line-icon style, 1.5px stroke, 16/20/24px grid; monochrome tinted by state color.

### 6.5 Layout Proportions
- **Top nav/status bar:** fixed height **56px**.
- **Graph screen vertical split:** control strip **~48px**, graph canvas **~60–65%** of remaining height, timeline scrubber **~64px**, on-click breakdown panel slides up to **~30%** (overlaying/pushing the canvas).
- **Tables (Firewall/Usage/Things/Alerts):** header row **40px**, data rows **44–48px**, comfortable 12–16px horizontal cell padding, hairline `--border` grid.
- **Content max density:** tables scroll internally; the app body never scrolls horizontally — wide tables get their own `overflow-x: auto` region.
- **Dialogs (Ask-to-Connect):** compact modal ~420px wide, app icon + name + hosts + Allow/Deny (and once/always).
- **Corner radius:** 6px cards/dropdowns, 4px buttons, 3px flag chips. **Spacing scale:** 4 / 8 / 12 / 16 / 24 / 32.

### 6.6 Signature Animated Graph Spec
- **Type:** stacked/overlaid **filled-area** chart, incoming + outgoing, each with a 1.5px top border line (default skin).
- **Auto-scaling:** Y-axis auto-scales to the current visible peak; the **scale/current-speed value floats at the upper-left**, printed on the canvas. Rescale is **eased** (200–300ms) so the axis doesn't snap jarringly.
- **Units switch by range:** 5-min → rate (B/s, KB/s, Mb/s); 3h/24h/week/month → volume (MB). Recompute on range change and on window resize (buckets re-width; spikes visibly split/merge).
- **Advance/animation:** new samples enter at the right edge; the series scrolls left at the sampling cadence (1s hot resolution). Interpolate between samples for smoothness (~60fps target; throttle to the update-interval setting).
- **Idle blocks:** stretches where the machine was idle render as `--idle-shade` grey bands stamped with a small clock icon.
- **Inspection:** on hover, reveal pause + snapshot at top-right. On **left-click**, freeze the animation, drop a **vertical inspection line** at the clicked timestamp, and slide up the app/host breakdown (sorted by data). **Right-click** resumes live.
- **Timeline scrubber:** below the canvas, a full-history mini-strip with **two draggable handles** (left/right) that pan/zoom the main window; **alert dots** (`--alert-dot`) and idle shading render on this strip too.
- **View-mode recolor:** in `Apps`/`Traffic`/`Publishers`/`Country` modes the single up/down pair is replaced by per-category stacked bands using a categorical palette (see §6.7).
- **Performance:** render with GPU-accelerated 2D (Direct2D/SkiaSharp/Win2D); cap redraw to visible range; decimate points beyond pixel resolution.

### 6.7 Categorical Palette (per-app / per-country bands)
Ordered, colorblind-considerate sequence for stacked composition modes:
`#4A8CFF` · `#2FB8C6` · `#3CCB7F` · `#E8A13A` · `#F0554E` · `#A97BFF` · `#F06CC0` · `#8FD14F` · `#6BC7E8` · `#C9A24B` (then cycle with reduced luminance).

### 6.8 Dark Theme (default) & Light Theme
- **Dark is default** (matches GlassWire's signature look). All tokens above are the dark set.
- **Light theme** overrides: `--bg-app #F5F7FA`, `--bg-panel #FFFFFF`, `--bg-graph #FFFFFF`, `--text-primary #1A1F27`, `--text-secondary #55606E`, `--border #E1E6EC`; directional colors keep the same hues but slightly deepened for contrast on white.
- **Skin/theme switching** lives in the app menu → Themes and in Settings → Appearance; changes graph in/out colors and overall surface set live.

### 6.9 Mini Viewer
- Small always-on-top window (~280×140px), just the live area graph + up/down readouts, transparent background option, drag-corner resize, filter icon for transparency/position.

---

## 7. Feature Tiering (GlassWire mapping — OpenWire = all free)

OpenWire ships **every feature with no tiers**. The table records where GlassWire drew paywalls (historical Basic $39 / Pro $69 / Elite $99 with 3 / 10 / unlimited remote connections; current GlassWire collapsed to Free + Premium) purely for reference and parity checking.

| Feature | GlassWire Free/Basic | GlassWire Pro | GlassWire Elite | OpenWire |
|---------|----------------------|---------------|-----------------|----------|
| Live traffic graph | ✅ Free | ✅ | ✅ | **Free** |
| Current-usage table | ✅ Free | ✅ | ✅ | **Free** |
| Graph history retention | Free ~1 month (older builds ~3 days) | 6 months | Unlimited / "forever" | **Free, unlimited (configurable)** |
| Basic firewall on/block toggles | ✅ Free | ✅ | ✅ | **Free** |
| Bidirectional In/Out control | ✅ Free | ✅ | ✅ | **Free** |
| Per-app flame block | ✅ Free | ✅ | ✅ | **Free** |
| **Ask to Connect** mode | ❌ Paid | ✅ | ✅ | **Free** |
| **Block all / Lock Down** mode | ❌ Paid | ✅ | ✅ | **Free** |
| **Firewall Profiles** (multiple) | ❌ Paid | ✅ | ✅ | **Free** |
| Auto-switch profiles by network | Not on desktop (any tier) | — | — | **Free (stretch)** |
| Things / Network Scanner list | ✅ Free (limited notifications) | ✅ | ✅ | **Free** |
| **Things Monitor** (repeat scan + join/leave alerts) | ❌ Paid | ✅ | ✅ | **Free** |
| VirusTotal column/scanning | ❌ Paid | ✅ | ✅ | **Free (user API key)** |
| **GlassWire/OpenWire Score** | ❌ Paid | ✅ | ✅ | **Free (heuristic)** |
| **Anomaly Detection** | ❌ Paid | ✅ | ✅ | **Free (local baseline)** |
| **Evil Twin** Wi-Fi alert | ❌ Paid | ✅ | ✅ | **Free (Wi-Fi present)** |
| **ARP Spoofing** alert | ❌ Paid | ✅ | ✅ | **Free (feasibility-flagged)** |
| **RDP Connection Detection** | ❌ Paid | ✅ | ✅ | **Free** |
| DNS / Proxy / Internet-access monitors | ✅ Free | ✅ | ✅ | **Free** |
| Suspicious Hosts / System-file / Device-list / App-info monitors | ❌ Paid | ✅ | ✅ | **Free** |
| Webcam/Mic monitor | Paid (removed from all builds) | — | — | **Not shipping** |
| **Remote / multi-PC monitoring** | Free = 1 remote; Basic ~3 | ~10 | Unlimited | **Free, unlimited** |
| Extra skins / themes | Basic theme free | Extra skins | Extra skins | **All free** |
| **Mini Viewer** | ❌ Paid | ✅ | ✅ | **Free** |

---

## Appendix A — Recommended .NET Dependencies
- **ETW capture:** `Microsoft.Diagnostics.Tracing.TraceEvent`
- **Connection table / ARP / firewall:** P/Invoke `iphlpapi.dll` (`GetExtendedTcpTable`, `GetExtendedUdpTable`, `GetIpNetTable2`), COM interop `NetFwTypeLib` (`HNetCfg.FwPolicy2` / `HNetCfg.FwRule`)
- **Wi-Fi scan (Evil Twin):** P/Invoke `wlanapi.dll` (`WlanGetNetworkBssList`)
- **GeoIP:** `MaxMind.GeoIP2` + `MaxMind.Db` (bundle `GeoLite2-Country.mmdb`)
- **Reverse DNS:** `System.Net.Dns` or `DnsClient.NET`
- **Device fingerprinting:** Rapid7 **Recog** XML fingerprints (+ IEEE OUI/mac-vendors list)
- **Storage:** `Microsoft.Data.Sqlite` (WAL, multi-resolution rollups)
- **Service host:** `Microsoft.Extensions.Hosting` (worker service, LocalSystem)
- **IPC:** gRPC (`Grpc.Net`) over named pipe / loopback+remote TCP, or `System.IO.Pipes` + `protobuf-net`/MessagePack
- **GUI:** WPF or WinUI 3 (Avalonia optional); Direct2D/SkiaSharp/Win2D for the animated graph
- **VirusTotal:** REST v3 with user-supplied API key (hash-first, upload-if-unknown)

## Appendix B — On-Disk Layout (target)
```
C:\ProgramData\OpenWire\
├── GeoLite2-Country.mmdb
├── openwire.db            (+ -wal, -shm)   config · rules · profiles · devices · alerts · app metadata
├── stats\
│   ├── openwire_stats_1sec_<unixepoch>.db   (+ -wal, -shm)   hot per-second per-process deltas
│   ├── openwire_stats_30sec_<unixepoch>.db  (+ -wal, -shm)   coarser rollup
│   ├── openwire_stats_hour_<unixepoch>.db
│   └── openwire_stats_day_<unixepoch>.db
└── logs\
```

## Appendix C — Clean-Room & Honesty Notes
- GlassWire's kernel driver type (WFP callout vs NDIS LWF) and its IPC wire format are **undocumented**; OpenWire's choices (ETW capture, gRPC/protobuf IPC) are independent designs, not reverse-engineered artifacts.
- Firewall enforcement being delegated to Windows Firewall is a **confirmed** GlassWire fact and is intentionally replicated for transparency; its first-packet limitation is disclosed to users.
- OpenWire Score cannot match GlassWire's proprietary global-userbase reputation; it is a **local + open-data heuristic**, labeled as such.
- Webcam/Mic detection is intentionally **omitted** (no reliable Windows API).