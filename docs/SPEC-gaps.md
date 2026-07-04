Verified against GlassWire's official user guide, features page, and forum manual. Below are the gaps — features, screens, settings, alerts, and UX behaviors that GlassWire actually ships but the OpenWire spec omits or under-specifies. Grouped and ranked by impact, ready to fold in.

## Major missing features (whole subsystems)

- **Data Plan / data-limit tracking** — completely absent from the desktop product (spec only leaves a stray "stopwatch" icon and punts it to the mobile companion). GlassWire desktop has a full Data Plan subsystem: set a limit (MB/GB), set the **billing-cycle reset day** (calendar picker for non-1st cycles), threshold alerts (e.g. warn at 90%), **recurring/repeat alerts**, **overage alert**, roaming toggle, **data rollover**, **zero-rated apps** (excluded from count), **multiple plans** (incl. **Wi‑Fi‑only plans**), and **metered-connection awareness**. Needs: a Settings → Data Plan section, a remaining-data / days-left gauge on the Usage screen, and a **"Data limit exceeded / approaching limit" alert type** (GlassWire's "Bandwidth Average Monitor"/overage alert) — none of which exist in the spec.
- **Incognito mode** — missing entirely. Global Incognito (stop recording graph/usage history) from the app menu, **plus per-app Incognito** ("Add to Incognito" so that app's host/data is never logged). This is distinct from firewall blocking and from snooze.
- **Clear history** — no "delete graph/usage history" action anywhere (Settings → General in GlassWire). Spec has retention config but no manual purge.
- **App-lock / password protection** — GlassWire's "Enable admin account password request" (require the OS admin password to open GlassWire or change settings). Spec only uses a password for *remote* access, not for local GUI/settings protection.

## Graph / Usage screen gaps

- **Single-app graph drill-down** — clicking an app should filter the graph to just that app's up/down history. Spec allows click-to-breakdown but not "graph this one app."
- **External vs Local traffic toggle** on Usage — GlassWire's Usage table has an external/local filter; spec omits it.
- **Network-type / adapter awareness** — no per-interface or Wi‑Fi/Ethernet/VPN/mobile/metered breakdown or filtering anywhere (relevant to Data Plan and to Usage).
- **"Traffic Types" classification is undefined** — spec lists the view mode but never says how traffic types are derived (port→service mapping, e.g. HTTP/HTTPS/DNS/FTP) or enumerates them.
- **"Publishers" view depends on Authenticode extraction** — spec stores `publisher` but never specifies signature/publisher verification (also feeds Score and a signed/unsigned indicator).
- **Snapshot save/share** — GlassWire's camera snapshot saves an image and can share it; spec mentions the icon but not the output/share action.
- **Host "search online" / whois lookup** — right-click a host to look it up online; not specified.

## Firewall screen gaps

- **Per-app "More"/context menu** — GlassWire's per-app menu (Add to Incognito, VirusTotal Analyze, host-level block, "search online", etc.). Spec only has hover‑X reset; the richer per-row menu is missing.
- **Ask-to-Connect prompt queue** — when the mode is first enabled many apps prompt at once; behavior for queuing/stacking multiple simultaneous prompts is unspecified.
- **Ask-to-Connect as an actionable toast** (Allow/Deny buttons in the Windows notification, not only an in-app modal) is not addressed.

## Alerts screen / alert-type gaps

- **Missing alert types:** "Data-limit / bandwidth overage" (data plan), and GlassWire's **"Security Setting changed" monitor** (a network-security setting was altered). 
- **"New version of app" vs "Application Info changed"** — GlassWire treats an app being *updated to a new version* distinctly; spec collapses everything into Application Info Monitor.
- **Snooze granularity** — spec only has a global 24h snooze; GlassWire also exposes per-type / per-monitor notification toggles feeding snooze, plus the app-menu "Snooze Alerts." Worth noting multiple snooze durations.
- **Alert sort options** — sort by date / app / type (spec has by-App/by-Type filter but not sort ordering explicitly).

## Settings gaps (General & misc.)

- **Speed units toggle: bytes vs bits per second** (GlassWire "Bandwidth Speed Units"). Absent.
- **"Use system time format"** option. Absent.
- **"Start in idle mode" / idle-detection threshold** config (drives the graph's idle shading and "while you were away"). Absent — idle detection is used but never configured.
- **Send alerts to Windows Event Log** integration. Absent.
- **Language / localization selection** — GlassWire is heavily localized; spec has no i18n at all.
- **About / version / check-for-updates / update channel** — no About section, version display, or update mechanism (for an OSS build, GitHub-releases update flow + third-party license acknowledgements). Absent.
- **Reset-to-defaults** (settings and/or Windows Firewall reset) — GlassWire offers this at install/repair; spec only has "clean up OpenWire rules."
- **Database backup / restore** workflow — spec makes the data dir configurable but has no backup/restore path.

## Tray & window-behavior gaps

- **System-tray icon + right-click tray menu** — GlassWire's tray icon shows live up/down activity and offers a right-click menu (show, settings, run-minimized, exit). Spec says "run in tray" but never specs the tray icon, its live indicator, or its menu.
- **Close-to-tray vs Exit / "run minimized on boot"** behavior, and window size/position persistence — unspecified.
- **App menu (hamburger) contents** — spec lists only Themes/Mini Viewer/Settings; GlassWire's menu also has Hide/Minimize, Snooze Alerts, Incognito, Language, Mobile link, Help/Forum, About, Exit. Flesh out the menu.

## Cross-cutting UX / architecture completeness

- **First-run onboarding** — initial UAC/admin elevation, service-install handshake, GeoIP/OUI/Recog data provisioning, first LAN scan consent, and the expected flood of "New app" prompts. Not described.
- **Service-unavailable / IPC-lost GUI state** — given the two-process split, the GUI needs empty/error/reconnecting states when the service is down or the pipe drops; not specified.
- **Empty / loading / permission-denied states** for each screen (blank Things list before first scan is the only one mentioned).
- **Accessibility & input** — no keyboard navigation, screen-reader labeling, high-DPI/multi-monitor, or reduced-motion handling (the animated graph needs a reduced-motion fallback).
- **Metered-connection detection** (Windows) — used by GlassWire to auto-limit background transfer and feed the metered profile; unspecified.
- **Remote NAT traversal guidance** — GlassWire documents port-forwarding and tunneling (Ngrok/Hamachi) for remote monitoring across NAT; spec only gives host/port/password.
- **VirusTotal false-positive reporting** link (minor) and the file-upload queue UX are omitted.

Sources:
- [GlassWire user guide](https://www.glasswire.com/userguide/)
- [GlassWire features](https://www.glasswire.com/features/)
- [GlassWire Menu | Settings | Client | General (forum manual)](https://forum.glasswire.com/t/glasswire-menu-settings-client-general/4324)
- [GlassWire 2.1.152 — Incognito Apps](https://www.glasswire.com/blog/2019/02/22/glasswire-2-1-152-now-with-incognito-apps/)
- [GlassWire Android data plan help](https://www.glasswire.com/android-help/)