# Changelog

All notable changes to OpenWire are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/), and this project adheres to
[Semantic Versioning](https://semver.org/).

## [1.0.1] — 2026-07-10

A first-principles security, privacy and performance hardening pass over the whole
stack (Core / elevated Service / GUI / IPC / SQLite / ETW / Windows Firewall). 19
focused changes; no page layouts changed and no features were removed.

### Security

- **Named-pipe privilege boundary.** The elevated engine now reads the connecting
  client's PID/SID from the OS pipe handle (never from client-supplied JSON),
  restricts the pipe ACL to the current user, and binds to the exact parent app
  process it launched; the app reciprocally verifies the service's image path and
  elevation. Cross-user and pipe-squatting connections are rejected.
- **No high-privilege auto-start proxy.** Removed the elevated task that could
  register an arbitrary executable to auto-start; login startup is now a plain
  per-user `Run` entry, with the legacy task migrated and cleaned up.
- **Hardened IPC deserialization.** Explicit derived-type allowlist; rejection of
  unknown fields, integer-enum smuggling, null destructive fields, over-deep JSON
  and out-of-range requests.
- **IPC framing limits.** 4-byte length prefix, 8 MiB per-frame cap,
  connection/handshake/write timeouts, client and outbound-queue caps, and
  fast-fail on truncated input.
- **SQLite vulnerability pinned.** Pinned the native `e_sqlite3` runtime to 3.53.3
  to close a memory-corruption issue pulled in transitively.
- **Data-directory lockdown.** Tightened DACLs on the data directory / DB / WAL /
  SHM; rejects UNC, device, network-drive, volume-root and reparse paths; the
  connection string is built structurally.
- **Secrets encrypted at rest.** The VirusTotal API key is now stored with DPAPI
  (CurrentUser); any existing plaintext is migrated on first load.
- **Atomic database migration.** Uses a consistent snapshot + `quick_check` before
  atomically swapping the protected database pointer.
- **Firewall rule isolation.** OpenWire's own rules carry a fixed owner + SHA-256
  marker and are replaced generationally — OpenWire never deletes or edits your
  pre-existing firewall rules.
- **Firewall "Off" truly disarms.** Turning the firewall off now removes every
  strictly-matched OpenWire rule and re-verifies the expected set; a failed
  reconcile no longer reports healthy. Lockdown / block-all stays **manual and
  reversible**.
- **CSV export formula-injection neutralised.** Cells starting with `= + - @`,
  tab, CR or LF are quoted before RFC-4180 escaping.
- **Crash log privatised.** The UI crash log moved to
  `%LOCALAPPDATA%\OpenWire\Logs` with an explicit DACL, reparse rejection,
  1 MiB × 3 rotation and throttling.
- **Bounded DNS reverse-resolution.** No more unbounded task pile-up on bursts of
  unknown IPs; adds a short negative cache.

### Performance

- **Single-transaction minute writes** — per-minute traffic, app, host, daily
  rollups and country observations commit in one transaction (~78 ms → ~5.6 ms on
  a synthetic 50-app / 500-host minute), with frozen-batch backoff and idempotent
  batch IDs.
- **Idle-first sampling** — hardware samplers start at idle rate (1500 ms) with the
  process sweep paused; full-rate 250 ms sampling only while the Hardware page is
  on screen.
- **Single-flight hardware polling** — page switches, initial load and the timer no
  longer stack IPC requests.
- **Incremental hardware history** — history transfers as deltas (~176 KB full
  response → ~467 B typical), with clock-skew detection and backward compatibility
  for older clients.
- **Zero-allocation ETW hot path** — address classification uses `stackalloc`
  (was ~64/80 MB allocated over 2M classifications); local endpoints no longer
  consume remote-attribution capacity.
- **Status bar off the per-second DB scan** — total bytes and unread alerts refresh
  on change with a 30 s fallback, backed by a new `alerts(ack)` index.

### Notes

A full threat-model report accompanies this release. Several items remain open by
design and are called out honestly for a future major pass: a fully-trusted
elevation chain (protected install dir + Authenticode/WinVerifyTrust or an SCM
service), true pre-connect blocking via a WFP ALE callout, and migration to
.NET 10 LTS.

## [1.0.0] — 2026-07-06

Initial public release. A clean-room, open-source network monitor + application
firewall for Windows:

- Live per-process bandwidth graph (ETW capture, no kernel driver).
- Bidirectional application firewall with per-network profiles and *Ask to connect*.
- 2D world map and 3D globe of connection geography.
- Usage analytics by app / host / traffic type / country, with anomaly detection.
- Alert engine, LAN device scanner, live hardware charts.
- Four live-switchable themes and English / Simplified / Traditional Chinese UI.
- Optional VirusTotal integration. Local-only: no account, no cloud, no telemetry.

[1.0.1]: https://github.com/KanameMadoka520/OpenWire/releases/tag/v1.0.1
[1.0.0]: https://github.com/KanameMadoka520/OpenWire/releases/tag/v1.0.0
