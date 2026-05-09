---
title: Stream 3 — Third-Party Config Scrape (Discovery Cascade v3)
date: 2026-05-06
stream: 3
research-question: |
  Identify every ham-radio application or networking tool installed on a typical
  JJ Flex user's Windows machine that may have stored a FlexRadio (or any radio)
  IP address on disk, so JJ Flex can read those IPs as additional discovery rungs
  when its own cache is empty (fresh-install case).
status: research-complete
authorized-by: Noel ("don't spare anything")
---

# Stream 3 — Third-Party Config Scrape

## Executive Summary

Five-bullet TL;DR ranked by expected hit-rate on a random JJ Flex user's machine:

- **SmartSDR for Windows is the killer source** — almost every JJ Flex user has it, FlexRadio's official client. Stores XML-format settings under `%APPDATA%\FlexRadio Systems\` (`SSDR.settings`, `CAT.settings`, optional `filter.txt`). The CAT.settings file definitively contains radio identifiers because SmartCAT must remember which radio a virtual COM port maps to. SSDR.settings is the most likely store of last-connected IPs. **Highest priority rung.**
- **PowerSDR mRX** — SmartSDR's predecessor, still installed by long-time Flex owners. Stores `database.xml` under `%APPDATA%\Roaming\FlexRadio Systems\PowerSDR mRX PS\` and similar paths. XML-format, easy to parse. Lower hit-rate than SmartSDR but trivially cheap to check.
- **WSJT-X is the second-biggest win** — most active digital-mode operators have it. `WSJT-X.ini` lives at `%LOCALAPPDATA%\WSJT-X\WSJT-X.ini` (Qt-style INI). When using "Hamlib NET rigctl" or "FlexRadio 6xxx" rig, the network address (often `127.0.0.1:port` for SmartCAT, but sometimes `radio_ip:4992` for DAX-server-based setups) is stored in the `[Configuration]` section.
- **N1MM Logger+** — most popular contest logger. INI lives at `Documents\N1MM Logger+\N1MM Logger.ini`. Configurer settings store TCP CAT IPs/ports for radios. Very common on FlexRadio contest stations.
- **OmniRig + Hamlib + the ecosystem of "everything talks to localhost"** — most digital/logger apps go through SmartCAT or rigctld via `127.0.0.1:port`. Reading those won't give us the radio's LAN IP directly. **The asymmetry that matters: SmartSDR-side files are the IP source of truth; everything downstream points at SmartSDR via loopback.** Don't waste rung budget on apps that only ever store `127.0.0.1`.

**Architectural conclusion:** the `ThirdPartyConfigScrape` rung should be a single rung with a prioritized walk:
1. SmartSDR settings files (highest hit, freshest)
2. PowerSDR mRX (legacy, still surprisingly common)
3. WSJT-X / N1MM / Wave-Flex Integrator (only useful when they store a real LAN IP, not loopback)

Each candidate file is opened, IP-shaped tokens extracted, deduplicated, and probed in parallel by the existing TCP-probe machinery. Any hit becomes a Radio handle via `Radio.CreateFromIp`.

---

## App-by-app findings

For each app the schema is:
- **Path** — where Windows stores the file (with env-var-resolved variants)
- **Format** — XML / JSON / INI / SQLite / proprietary binary
- **IP storage convention** — what key, structure, and identifiers we expect
- **Freshness signals** — last-modified, sequence number, "last connected"
- **License compliance** — read-only access notes (universally permitted)
- **Known issues** — file locks, encryption, version drift

### Tier 1: SmartSDR (FlexRadio's official client)

**Why this is the absolute biggest target:** Almost every JJ Flex user has SmartSDR installed. Even users who switched to JJ Flex because SmartSDR is inaccessible still have SmartSDR on disk. Of every source on this list, SmartSDR is the most likely to hold the answer.

#### SmartSDR for Windows — `SSDR.settings`

- **Path:** `%APPDATA%\FlexRadio Systems\SSDR.settings`
  - Resolved: `C:\Users\<username>\AppData\Roaming\FlexRadio Systems\SSDR.settings`
- **Format:** XML (text). When deleted, SmartSDR regenerates a fresh one on next launch — confirming it's user-state, not vendor-state.
- **IP storage:** Not explicitly documented externally, but this is the only file that SmartSDR can use to remember "the radio I was connected to last" across launches. Almost certainly contains:
  - Last connected radio's serial + nickname + model (radio chooser display)
  - Last connected radio's LAN IP (otherwise SmartSDR couldn't auto-attempt to reconnect)
  - Possibly a recent-radios list; FlexRadio's own UI shows a "previous radio chosen" highlight
- **Freshness signals:** Updated on each successful connection / disconnect — file mtime is a good "last connected" approximation. May contain explicit `LastConnected` timestamp.
- **License compliance:** Read-only is universally allowed. Don't write, don't move, don't lock.
- **Known issues:** Can be locked exclusively while SmartSDR is running; opening with `FileShare.ReadWrite` should still work for read. If the user crashed SmartSDR mid-write, the file may be partially-written XML — defensive parser must tolerate `XmlException`. Format is undocumented and may change between SmartSDR major versions (2.x → 3.x → 4.x). **Treat schema as best-effort: regex-scan for IPv4 patterns in the document if XML parse fails or the schema is unfamiliar.**

#### SmartSDR for Windows — `CAT.settings`

- **Path:** `%APPDATA%\FlexRadio Systems\CAT.settings`
- **Format:** XML (text). Confirmed XML by FlexRadio community: "If the PC crashed, it can corrupt the XML file that SmartCAT uses to define the port parameters."
- **IP storage:** Defines virtual COM port → radio mappings for third-party CAT clients. To do this, each port definition must reference *which radio* (by serial, possibly with cached IP/nickname for display in the CAT panel). The structure includes per-port radio bindings.
- **Freshness signals:** Updated when user adds/removes/edits a CAT port. Less frequently updated than SSDR.settings, but always populated for users who use any external app with FlexRadio.
- **License compliance:** Read-only OK.
- **Known issues:** Lock contention with running SmartCAT process. Schema undocumented; same defensive parse + IPv4 regex fallback applies.

#### SmartSDR for Windows — `DAX.settings`

- **Path:** `%APPDATA%\FlexRadio Systems\DAX.settings` (presence of CAT.settings + SSDR.settings strongly implies a parallel DAX.settings exists; community references "SmartSDR-Win settings files for CAT, DAX & SSDR" but doesn't always name them by extension)
- **Format:** XML (consistent with the family).
- **IP storage:** DAX (Digital Audio eXchange) audio routing must associate each DAX channel with a radio; expected to hold radio serial + IP for the same reason CAT.settings does.
- **Freshness signals:** mtime when DAX config changed.
- **Known issues:** Same family caveats.

#### SmartSDR for Windows — `filter.txt` (optional)

- **Path:** `%APPDATA%\FlexRadio Systems\filter.txt`
- **Format:** Plain text, one serial substring per line. SmartSDR uses it to filter the discovery results.
- **IP storage:** **No IPs** — only serial-substrings. Useful as a *negative* signal: tells us which serials a multi-radio user actually cares about. Not a discovery source on its own; could be used as a hint to prioritize which serials to surface in JJ Flex's radio chooser.
- **Known issues:** Optional file; absent on most installs.

#### SmartSDR for Windows — `*.ssdr_cfg` profile exports

- **Path:** User-selectable, often Desktop or Documents. Default name pattern `SSDR_Config-MM-DD-YYYY_h.mm.PM.ssdr_cfg`.
- **Format:** Proprietary, **encrypted/obfuscated** per FlexRadio community ("the data is encrypted, which is the reason they don't offer an outboard editing tool").
- **IP storage:** Likely contains radio identifiers but unreadable without reverse-engineering the encryption.
- **Recommendation:** **Skip.** Encrypted format + non-deterministic location + decryption would arguably stretch "reading other apps' files" into reverse-engineering territory. Cost-benefit doesn't justify.

#### SmartSDR — Updates folder (firmware)

- **Path:** `C:\ProgramData\FlexRadio Systems\SmartSDR\Updates\`
- **Format:** Per-version firmware archives.
- **IP storage:** No — but file presence tells us which firmware versions the user has cached, useful for chained-updater UI.
- **Recommendation:** Not a discovery source, but worth noting for the firmware-update flow's context.

#### SmartSDR — Crash reports / logs

- **Path:** Not externally documented; likely `%LOCALAPPDATA%\FlexRadio Systems\` or under `%TEMP%`.
- **IP storage:** Crash dumps may contain in-memory radio state including IPs, but parsing minidumps is deeply impractical.
- **Recommendation:** Skip.

### Tier 1: PowerSDR mRX (legacy Flex client)

- **Path:** `%APPDATA%\Roaming\FlexRadio Systems\PowerSDR mRX PS\database.xml`
  - Sibling versioned paths: `...\PowerSDR v2.7.2\`, `...\PowerSDR v2.8.0\`
- **Format:** XML
- **IP storage:** PowerSDR mRX targeted Flex 6000 series via Ethernet — must store the radio IP to connect. Radio identifiers (model, IP, serial) live in `database.xml`.
- **Freshness signals:** mtime; PowerSDR mRX is mostly maintained by KE9NS now and used by long-time Flex owners. Older mtimes still useful — IPs rarely change on a settled LAN.
- **License compliance:** Read-only OK.
- **Known issues:** XML schema undocumented. Same defensive-parse + IPv4-regex fallback. Possibly locked while PowerSDR mRX is running.

### Tier 2: WSJT-X

- **Path:** `%LOCALAPPDATA%\WSJT-X\WSJT-X.ini`
  - Resolved: `C:\Users\<username>\AppData\Local\WSJT-X\WSJT-X.ini`
  - Multi-instance variants: `WSJT-X - ForEW1`, `WSJT-X - ForEW2`, `WSJT-X - <rig-name>`
- **Format:** Qt-style INI (Windows .ini with sections like `[Configuration]`)
- **IP storage:** Within `[Configuration]` section. For Flex setups:
  - `Rig=` — typically `FlexRadio 6xxx` or `Hamlib NET rigctl` or `Flex 6xxx PowerSDR/Thetis`
  - `CATSerialPort=` — SmartCAT virtual COM port name (e.g. `COM10`)
  - `RigCATNetwork=` — when using Hamlib NET rigctl, this is `host:port`. Almost always `127.0.0.1:<port>` for local SmartCAT. **For radio-to-radio remote setups, this can be a real LAN IP — useful when present.**
- **Freshness signals:** mtime updates each time WSJT-X exits cleanly.
- **License compliance:** Read-only OK.
- **Known issues:** When the value is `127.0.0.1:<port>`, it points at SmartCAT, not the radio — useless for our cascade. **Filter loopback addresses out before treating as a candidate.** Multi-instance INIs (per-rig) multiply the file count; walk all `WSJT-X*.ini` siblings.

### Tier 2: N1MM Logger+

- **Path:** `%USERPROFILE%\Documents\N1MM Logger+\N1MM Logger.ini`
- **Format:** INI
- **IP storage:** "Configurer settings are remembered by the program in the N1MM Logger.ini file." For FlexRadio TCP CAT setup, the Configurer dialog stores the IP and TCP port. When TCP port is set on the Flex side and "TCP" selected for the CAT port type, the value is typically `127.0.0.1:<smartcat-port>`. **For remote-PC setups (N1MM on one PC, SmartSDR on another)**, the IP is real and points to the SmartSDR host — still not the radio, but proximate.
- **Freshness signals:** mtime.
- **Known issues:** Same loopback-filter rule as WSJT-X. Documents-folder location means it's *not* hidden — a scrape-and-display feature would be visible to the user if they care.
- **Bonus payload:** N1MM also stores SQLite databases (`Databases\<name>.s3db`) of contest QSO history. **Not a discovery source** — those are on-air QSOs, not radio-management data. Skip.

### Tier 2: Log4OM

- **Path:** `%APPDATA%\Roaming\Log4OM\config.xml` (v1) or `%APPDATA%\Roaming\Log4OM2\user\config.xml` (v2)
  - Backups: `...\Log4OM2\backup\`
- **Format:** XML
- **IP storage:** Log4OM has FlexRadio CAT support via either direct TCP (real IP), SmartCAT (loopback), or Slicemaster (loopback). The XML stores the radio interface configuration including IP/port. Direct-TCP setups will have a real LAN IP.
- **Freshness signals:** mtime; backup directory contains versioned snapshots that can extend reach historically.
- **License compliance:** Read-only OK.
- **Known issues:** XML schema not externally documented; defensive parse + regex.

### Tier 3: Wave-Flex Integrator (TNXQSO)

- **Path:** Electron app — uses `electron-store` and `electron-json-storage`. Default Electron path: `%APPDATA%\Roaming\<app-name>\config.json` (likely `wave-flex-integrator` or similar).
- **Format:** JSON
- **IP storage:** The app communicates *directly* with the FlexRadio API (TCP/IP), not through SmartSDR — so its config **must** store the actual radio LAN IP. **High-quality source when present.**
- **Freshness signals:** mtime; JSON timestamps may be embedded.
- **License compliance:** MIT/community-driven open source. Read-only OK.
- **Known issues:** Lower install base than SmartSDR; only adds value when present. Trivial to check.

### Tier 3: SliceMaster 6000 (K1DBO)

- **Path:** `%LOCALAPPDATA%\K1DBO\slice-master\config.ini`
- **Format:** INI
- **IP storage:** SliceMaster talks to FlexRadios via UDP discovery (no manual IP needed) and primarily stores paths to *third-party launchable apps* (WSJT-X, CW Skimmer, etc.) rather than radio IPs. **Likely no radio IP.** Worth a one-time check; low yield expected.
- **Known issues:** None. Trivially cheap to skip if empty.

### Tier 3: FRStack (mkcmsoftware)

- **Path:** `C:\Program Files (x86)\FRStack4\FRStack.exe.config` (v4) or `\FRStack3\` (v3)
- **Format:** .NET app.config XML
- **IP storage:** `WhiteListedIPAddresses` (comma-separated client IPs allowed to call FRStack's REST API) — **not radio IPs**, but client IPs. Radio identification is done via FlexLib (UDP discovery), so radio IPs are runtime-only, not persisted in the app.config.
- **Recommendation:** **Skip as IP source.** WhiteListedIPAddresses might give us hints about other PCs on the LAN that have talked to a Flex, but that's a deep stretch.
- **Known issues:** Located in Program Files, not user profile — different install path per architecture.

### Tier 3: Ham Radio Deluxe (HRD)

- **Path:** `%APPDATA%\Roaming\HRDLLC\HRD Logbook\` — XML layout files + SQLite logbook DB
- **Format:** XML for layouts; SQLite for logbook
- **IP storage:** HRD's "Rig Control" component can talk to Flex via SmartCAT (loopback) or via direct vendor driver. Profile config is stored in the registry historically; modern versions migrate to AppData. **Most likely loopback.** SQLite logbook holds QSO data, not radio config.
- **Recommendation:** Low priority. Check `HKEY_CURRENT_USER\Software\HRDLLC\` registry tree for radio profile entries; unlikely to yield non-loopback IPs.
- **Known issues:** Commercial product; many users on older versions; format drift across releases.

### Tier 3: DXLab Suite (Commander, DXKeeper, etc.)

- **Path:** **Windows Registry** — `HKEY_CURRENT_USER\Software\DXLab Suite\Commander\`, `...\DXKeeper\`, etc.
  - Workspace export folders contain text dumps of these registry keys.
- **Format:** Registry values (mostly strings); workspace files are text-format registry dumps.
- **IP storage:** Commander is the rig-control component. For Flex setups it usually goes through SmartCAT via virtual COM, so radio IP isn't stored directly. **Loopback-pattern: Commander stores COM port + baud, not IP.**
- **Recommendation:** Low yield for our purposes; registry walk has higher friction than file read. **Skip unless we add a generic registry scrape rung in v3.**
- **Known issues:** Stores in Registry, not files — different access pattern. May trigger AV/EDR heuristics if we walk arbitrary HKCU subtrees.

### Tier 3: fldigi

- **Path:** `%USERPROFILE%\fldigi.files\` (Windows) — contains `fldigi_def.xml`, `fldigi.prefs`, and a `rigs\` subdirectory with `SmartSDR.xml`
- **Format:** XML for `fldigi_def.xml` and `rigs\*.xml`; "prefs" file is text key=value
- **IP storage:** RigCAT XMLs (`rigs\SmartSDR.xml`) define CAT *commands*, not radio location. fldigi typically uses Hamlib or Hamlib NET to talk to the radio; the network address ends up in `fldigi_def.xml` under a Hamlib-related key. Almost always `127.0.0.1:<port>` for local SmartCAT.
- **Recommendation:** Low yield (loopback). Walk `fldigi_def.xml` for IPv4 tokens, drop loopback.
- **Known issues:** Same loopback filter rule.

### Tier 3: JS8Call

- **Path:** `%LOCALAPPDATA%\JS8Call\JS8Call.ini`
- **Format:** INI (Qt-style, same family as WSJT-X)
- **IP storage:** Same Hamlib/CAT pattern as WSJT-X. `[Configuration]` section likely has the same `RigCATNetwork=` style entry.
- **Recommendation:** Walk the same way as WSJT-X. Low marginal cost since the parser is reusable.
- **Known issues:** Same loopback filter.

### Tier 3: JTAlert

- **Path:** Reads WSJT-X.ini directly per HamApps support documentation; doesn't maintain a separate radio-IP store.
- **Recommendation:** **Skip.** JTAlert is a downstream consumer of WSJT-X config, not a separate IP source. Whatever WSJT-X knows, we already got.

### Tier 3: WriteLog

- **Path:** `C:\Windows\writelog.ini` (XP) or `%PROGRAMDATA%\Writelog\writelog.ini` / `%APPDATA%\Roaming\Writelog\writelog.ini` (modern Windows; per-version varies)
- **Format:** INI
- **IP storage:** WriteLog's "Flex-6000 series" rig driver uses a vendor-supplied driver, not a generic CAT pattern. Network address may or may not be stored explicitly; could pull radio info from FlexLib at runtime.
- **Recommendation:** Low priority. Walk `writelog.ini` for IPv4 tokens; drop loopback.
- **Known issues:** Pre-Vista path is in `C:\Windows\` — modern Windows quietly redirects writes, so the real file may live in VirtualStore (`%LOCALAPPDATA%\VirtualStore\Windows\writelog.ini`). Check both.

### Tier 3: N3FJP ACLog

- **Path:** `%USERPROFILE%\Documents\Affirmatech\N3FJP Software\ACLog\`
  - Settings: `Settings.xml` in Program Files area + Documents folder
  - Database: `LogData.mdb` (Access)
- **Format:** XML for settings; .mdb (Access JET) for log
- **IP storage:** ACLog has Flex API integration for direct radio control; settings.xml may carry the radio IP for direct-TCP setups. Often loopback when going through SmartCAT.
- **Recommendation:** Walk Settings.xml for IPv4; drop loopback.
- **Known issues:** Documents folder location, plus possible VirtualStore redirection of Program Files writes.

### Tier 3: OmniRig

- **Path:** `%APPDATA%\Roaming\Afreet\Products\OmniRig\` (per-rig settings)
  - Rig description files: `C:\Program Files (x86)\Afreet\OmniRig\Rigs\<model>.ini`
- **Format:** INI
- **IP storage:** OmniRig was originally serial-port-only. v2 added network rig support. For Flex, OmniRig usually goes through SmartCAT via virtual COM — **loopback pattern, no LAN IP stored.**
- **Recommendation:** Skip. Loopback expected.

### Tier 3: DDUtil v3 (K5FR)

- **Path:** `%APPDATA%\Roaming\K5FR\DDUtil\` or similar K5FR/DDUtil paths
- **Format:** Various; INI most likely
- **IP storage:** DDUtil connects to Flex 6000 via Ethernet — **must** store the radio IP. Older 5000-series support used serial.
- **Recommendation:** Walk for IPv4 tokens; not loopback because DDUtil talks directly to the radio.
- **Known issues:** Schema undocumented; defensive scan.

### Tier 3: SDR Console v3 (SDR-Radio.com)

- **Path:** `%APPDATA%\Roaming\SDR-RADIO.com (V3)\Console\Ident0\` (XML files) **plus** `HKEY_CURRENT_USER\Software\SDR-RADIO.com (V3)\Console\Ident0` (registry)
- **Format:** XML in files; registry strings
- **IP storage:** SDR Console talks to a wide range of SDRs; for FlexRadios it uses the FlexLib API directly. Server lists and identity-specific config are in the XML files; the radio IP is part of the server-list structure.
- **Recommendation:** Medium-low priority — heavy install footprint, low-overlap user base with JJ Flex.

### Tier 3: SmartSDR for Mac (Marcus Roskosch)

- **Recommendation:** Skip on Windows entirely — not present.

### Tier 4: Network/forensics tools (very low yield)

- **Wireshark** — `%APPDATA%\Wireshark\recent` (last opened files), `recent_common`, `preferences`. Capture files default to `%TEMP%`. **Not a normal-user surface.** Hams who pcap their Flex traffic are a tiny minority. Could in theory parse `.pcapng` files for FlexRadio VITA-49 packets but this is far past the cost-benefit line. **Skip.**
- **Bonjour / mDNS resolver** — FlexRadios use VITA-49/UDP discovery, **not** mDNS/zeroconf. Bonjour cache on Windows is in-memory and won't have FlexRadio entries. **Skip.**
- **arp.exe / netstat.exe / GetIpNetTable** — these surface live network state, not persisted config. They belong to Stream 1 (Windows OS primitives), not Stream 3.
- **Windows DNS resolver cache** — would catch hostname resolutions if the user reaches the Flex by hostname. Belongs to Stream 1.

---

## Recommendations for cascade rung inclusion

### Single rung: `ThirdPartyConfigScrapeRung`

One discovery rung walks all known files. Internal walk priority:

**Round 1 — high-value Flex-vendor sources (always check):**
1. `%APPDATA%\FlexRadio Systems\SSDR.settings`
2. `%APPDATA%\FlexRadio Systems\CAT.settings`
3. `%APPDATA%\FlexRadio Systems\DAX.settings`
4. `%APPDATA%\FlexRadio Systems\filter.txt` (serial-list hint, no IPs)

**Round 2 — Flex-vendor legacy:**
5. `%APPDATA%\FlexRadio Systems\PowerSDR mRX PS\database.xml`
6. `%APPDATA%\FlexRadio Systems\PowerSDR v2.*\database.xml` (glob)

**Round 3 — direct-Flex-API third-party (high quality, lower install base):**
7. Wave-Flex Integrator JSON (Electron-store path)
8. DDUtil v3 config (K5FR path)
9. SDR Console v3 XML files

**Round 4 — Hamlib-pattern apps (loopback-filtered):**
10. `%LOCALAPPDATA%\WSJT-X\WSJT-X*.ini` (multi-instance glob)
11. `%LOCALAPPDATA%\JS8Call\JS8Call.ini`
12. `%USERPROFILE%\Documents\N1MM Logger+\N1MM Logger.ini`
13. `%APPDATA%\Roaming\Log4OM*\user\config.xml` and v1 `\Log4OM\config.xml`
14. `%USERPROFILE%\fldigi.files\fldigi_def.xml`
15. `%USERPROFILE%\Documents\Affirmatech\N3FJP Software\ACLog\Settings.xml`
16. `%PROGRAMDATA%\Writelog\writelog.ini` + VirtualStore variant

### Walk algorithm

For each candidate file:
1. `File.Exists` check (cheap; most candidates absent on most machines).
2. Open for read with `FileShare.ReadWrite` (don't fight the running app's lock).
3. **Defensive parse:**
   - If extension is `.xml` or `.json`, try schema-aware parse first.
   - Always fall back to a regex sweep for IPv4 patterns (`\b(?:25[0-5]|2[0-4]\d|[01]?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|[01]?\d?\d)){3}\b`) — schema drift across vendor versions is the rule, not the exception.
4. **Filter loopback addresses** (`127.0.0.0/8`) and obviously non-LAN addresses (`0.0.0.0`, `255.255.255.255`, multicast, link-local `169.254.0.0/16` unless LAN-context demands it).
5. **Aggregate candidates** across all files.
6. **Deduplicate** the candidate IP list.
7. **Probe in parallel** — short TCP probe to port 4992 on each candidate IP, same machinery as `CachedLanIpRung`.
8. First successful probe wins; rung returns `Radio.CreateFromIp(...)`.
9. **Outcome tags:** `success` / `no_files_found` / `no_ips_extracted` / `all_probes_failed` / `parse_error_only` / `disabled_by_user`.

### Why one rung not many

A separate rung per app (`SmartSDRConfigRung`, `WSJTXConfigRung`, `N1MMConfigRung`, etc.) would balloon the chain depth and make the trace noisier. A single rung that walks the prioritized list is:
- One trace line per *attempt set*, not per *app*
- Easier to disable/enable as a single user-facing toggle (consistent with flexibility-principle config exposure)
- Faster overall — file existence checks are microsecond-scale; the actual cost is the TCP probes, which we batch

If a user has zero of these apps, the rung returns `no_files_found` in single-digit milliseconds and the chain moves on to the next rung. The downside is essentially zero.

### Where this rung lives in the chain order

Based on the round-2 design memo's six-rung ordering:

1. CachedLanIp (own cache, last LAN connection) — already shipped
2. CachedWanIp (own cache, last SmartLink connection) — already shipped
3. **ThirdPartyConfigScrape (this rung) — NEW**
4. SubnetProbe (sweep last-known LAN subnet)
5. UdpDiscovery (FlexLib's standard discovery)
6. ManualIp (user-entered fallback)

Rationale for slot 3: own-cache (rungs 1+2) is faster and authoritative when present; subnet probe (rung 4) is wider-net but slower and may trigger AV/IDS; UDP discovery (rung 5) is what's failing in the cases that motivate this rung. Putting third-party scrape at slot 3 makes it the last "intelligent guess" before we resort to brute-force LAN sweeps.

### Concurrency consideration

Once Phase 3 of the design memo (concurrent rungs) lands, third-party config scrape can fire in parallel with subnet probe and UDP discovery — they don't share resources. For the R6 sequential shape, slot 3 is correct.

---

## Privacy considerations

### What we're doing

Reading **read-only** the configuration files of other applications already installed on the user's machine, extracting only IPv4 patterns, and probing the user's own LAN to find their own radio.

### What we're explicitly NOT doing

- Writing to other apps' files
- Copying files off the machine
- Phoning the IP list home (per `project_no_silent_phone_home.md`)
- Reading anything outside the IP-shaped tokens (we don't care about callsigns, log entries, profile names, encryption keys, antenna definitions, etc. — even if they're sitting next to the IPs in the same file)
- Reading SmartSDR's encrypted profile exports (`.ssdr_cfg`)
- Walking arbitrary registry trees (DXLab is the one app that would benefit and we're skipping it)

### Required user disclosure

Per the friction-tax principle, the right default is **enabled by default** with **transparent disclosure**. The user-facing surface should be:

**On first run:**
- A short paragraph in the Welcome / first-run dialog, screen-reader-friendly:
  > "JJ Flexible Radio Access tries hard to find your radio without making you type its IP address. To do this, it may read the settings files left on this computer by SmartSDR, PowerSDR, WSJT-X, N1MM Logger+, and similar ham-radio programs you've installed — but only to extract IP addresses, and only on this computer. Nothing is sent anywhere; no other data is collected. You can disable this in Settings → Connection → Discovery."
- The rung is on by default. Per friction-tax, the conservative-but-helpful default is "do the work for the user."

**In Settings → Connection → Discovery:**
- A toggle: "Read other ham-radio app config files when looking for the radio" (default on)
- Below it, in a disclosure expander: a list of which apps will be checked, with each app individually toggle-able for the user who wants finer control. (Nice-to-have; not blocking.)

**In the trace file:**
- The trace records which file paths were checked, which produced IP candidates, and which IPs were probed. **Never log the surrounding file contents** — only the path, the count of IPv4 tokens found, and the deduplicated candidate list.

### License compliance summary

Reading other applications' configuration files without modification is universally permitted under all relevant licenses and OS conventions. We are not:
- Distributing the data
- Reverse-engineering protected formats (the `.ssdr_cfg` encrypted format is the one we're explicitly skipping)
- Bypassing access controls (everything is in the user's own profile, readable by their own user account)
- Modifying files we don't own

The closest legal-risk question would be SmartSDR's `.ssdr_cfg` encryption — the fact that FlexRadio chose to encrypt that specific format suggests an intent to keep it opaque. Skipping it is the right call on both ethical and effort grounds.

---

## Open questions / follow-up

### Need empirical investigation (overlaps Stream 2 — Noel's runbook)

1. **What is SSDR.settings' actual XML schema?** Cracking open Noel's own SSDR.settings would let us write a schema-aware parser instead of regex-only. Same for CAT.settings and DAX.settings.
2. **Does SSDR.settings store a recent-radios *list*, or only the *last* radio?** Affects whether we get one IP or many candidates per scrape.
3. **Does PowerSDR mRX's database.xml format match SmartSDR-family XML, or is it a separate schema?**
4. **What does Wave-Flex Integrator's electron-store JSON actually look like on disk?** (Path resolution and key naming for `electron-store` defaults to `<appName>/config.json` but verify.)
5. **DDUtil v3** — exact path under `%APPDATA%\Roaming\K5FR\DDUtil\` or `%APPDATA%\Roaming\DDUtil\` and filename(s).

### Architectural follow-ups

1. **Should we maintain a known-app-paths manifest as data, not code?** A JSON/YAML file shipped with JJ Flex that lists `{app_name, path_template, format_hint}` lets us add new apps via a Data Provider update without an app release. Aligns with the data-provider-as-config strategy.
2. **Surface scraped IPs in the radio chooser's "found via" column?** When multiple discovery rungs return the same radio, showing the user "found via SmartSDR config" or "found via your network" is tasteful disclosure — and reinforces that JJ Flex worked harder than SmartSDR to find their radio.
3. **Should we cache the third-party scrape result in our own cache?** Once we successfully find a radio via SmartSDR's SSDR.settings, we should write that to our own LAN cache so the next launch's rung 1a wins. This is a free side-effect and probably the right behaviour (subject to staleness rules from the round-2 memo).
4. **Multi-radio disambiguation** — if SSDR.settings yields three radios and the user has set a preferred-serial in JJ Flex, only probe the matching candidate first. If no preferred serial, probe all in parallel and prompt user via radio chooser when multiple respond.

### Nice-to-have stretch sources (not in v3 baseline)

- **Recent-files-by-extension on the desktop** — `*.ssdr_cfg` files often live there; even though encrypted, presence + filename-date is a freshness hint
- **Hostname-resolution cache** — if `%COMPUTERNAME%-flex` or similar hostnames resolve, that's an IP source (overlaps Stream 1)
- **Windows Credential Manager** — SmartLink credentials live there; not a LAN-IP source
- **SMB shared folder discovery** — long shot

---

## Summary table — the prioritized walk

(Note: rendered as a flat list rather than a markdown table because screen readers handle lists better and this list is short.)

- **`%APPDATA%\FlexRadio Systems\SSDR.settings`** — XML, highest hit-rate, last-connected radio's IP. **Build first.**
- **`%APPDATA%\FlexRadio Systems\CAT.settings`** — XML, very high hit-rate among users who set up SmartCAT. Per-port radio bindings.
- **`%APPDATA%\FlexRadio Systems\DAX.settings`** — XML (inferred), parallel to CAT.settings for DAX users.
- **`%APPDATA%\FlexRadio Systems\PowerSDR mRX PS\database.xml`** — XML, legacy Flex users. Cheap to check.
- **`%APPDATA%\FlexRadio Systems\filter.txt`** — text, serial-substring hints (no IPs but useful for prioritization).
- **`%LOCALAPPDATA%\WSJT-X\WSJT-X*.ini`** — INI, loopback-filter required. Multi-instance glob.
- **`%LOCALAPPDATA%\JS8Call\JS8Call.ini`** — INI, loopback-filter required.
- **`%USERPROFILE%\Documents\N1MM Logger+\N1MM Logger.ini`** — INI, loopback-filter required.
- **`%APPDATA%\Roaming\Log4OM*\user\config.xml`** — XML, can have direct-TCP IPs.
- **`%USERPROFILE%\fldigi.files\fldigi_def.xml`** — XML, loopback-filter required.
- **`%USERPROFILE%\Documents\Affirmatech\N3FJP Software\ACLog\Settings.xml`** — XML.
- **`%PROGRAMDATA%\Writelog\writelog.ini`** + VirtualStore variant — INI.
- **Wave-Flex Integrator electron-store JSON** — JSON, direct-Flex-API user, high quality when present.
- **`%LOCALAPPDATA%\K1DBO\slice-master\config.ini`** — INI, low yield (mostly app paths).
- **DDUtil v3 K5FR path** — INI, direct-Ethernet to Flex (high quality when present, exact path TBD).
- **SDR Console v3 `%APPDATA%\Roaming\SDR-RADIO.com (V3)\Console\Ident0\*.xml`** — XML.

---

## Sources

- [SmartSDR config file location — FlexRadio Community](https://community.flexradio.com/discussion/8028956/where-does-smart-sdr-keep-the-configuration-files)
- [SmartSDR.XML file — FlexRadio Community](https://community.flexradio.com/discussion/5989334/smartsdr-xml-file)
- [Where to find SmartSDR.xml for FlDigi — FlexRadio Community](https://community.flexradio.com/discussion/7989087/where-to-find-smartsdr-xml-for-fldigi)
- [SmartSDR CAT data backup — FlexRadio Community](https://community.flexradio.com/discussion/8006627/is-there-a-way-to-back-up-smartsdr-cat-data)
- [Lost Smart Cat configuration — FlexRadio Community](https://community.flexradio.com/discussion/8023752/lost-smart-cat-configutration)
- [SmartCAT settings lost after computer glitch — FlexRadio Community](https://community.flexradio.com/discussion/7733969/smartcat-settings-lost-after-computer-glitch)
- [SmartSDR profile export format — FlexRadio Helpdesk](https://helpdesk.flexradio.com/hc/en-us/articles/27698982474267-Exporting-and-Importing-your-SmartSDR-Profiles)
- [PowerSDR database location — FlexRadio Knowledgebase](http://kc.flexradio.com/knowledgebasearticle50469.aspx)
- [FlexLib Discovery.cs (FlexLib_Core port)](https://github.com/brianbruff/FlexLib_Core)
- [WSJT-X User Guide (network rig settings)](https://wsjt.sourceforge.io/wsjtx-doc/wsjtx-main-2.7.0.html)
- [Configuring WSJT-X for FLEX-6000/8000 — FlexRadio Helpdesk](https://helpdesk.flexradio.com/hc/en-us/articles/25961619467163-Configuring-WSJT-X-for-the-FLEX-6000-8000-Series)
- [WSJT-X.ini location — FT8 Digital Mode Group](https://groups.io/g/FT8-Digital-Mode/topic/wsjt_x_ini/72585696)
- [N1MM Logger+ file structure — N1MM docs](https://n1mmwp.hamdocs.com/appendices/technical-information/)
- [N1MM Logger+ file locations — groups.io](https://groups.io/g/N1MMLoggerPlus/topic/nmm_file_structure_location/81599900)
- [Log4OM config file location — Log4OM forum](https://forum.log4om.com/viewtopic.php?t=2195)
- [JS8Call config location — RigPi help](https://rigpi.net/help/js8call.html)
- [fldigi configuration files — fldigi wiki](https://sourceforge.net/p/fldigi/wiki/beginners/)
- [How to Configure Fldigi for FLEX-6000/8000 — FlexRadio Helpdesk](https://helpdesk.flexradio.com/hc/en-us/articles/28304136340763-How-to-Configure-Fldigi-for-the-FLEX-6000-8000-Radios)
- [N3FJP ACLog FAQ — n3fjp.com](https://www.n3fjp.com/faq.html)
- [DXLab application settings location — DXLab wiki](https://www.dxlabsuite.com/dxlabwiki/ApplicationSettings)
- [DXLab Launcher registry workspaces](https://www.dxlabsuite.com/Launcher/Help/RegistrySettings.htm)
- [WriteLog config file location — rttycontesting](https://www.rttycontesting.com/tutorials/writelog/clearing-rttyrite/)
- [WriteLog FlexRadio rig driver](https://writelog.com/writelogs-flexradio-rig-driver)
- [OmniRig configuration — Ham Cockpit](https://ve3nea.github.io/HamCockpit/users_guide/omnirig.html)
- [Wave-Flex Integrator package.json — GitHub](https://github.com/tnxqso/wave-flex-integrator/blob/main/package.json)
- [Wave-Flex Integrator README — GitHub](https://github.com/tnxqso/wave-flex-integrator/blob/main/README.md)
- [Slice Master 6000 config file — FlexRadio Community](https://community.flexradio.com/discussion/8006343/slice-master-6000-config-file)
- [FRStack config — FlexRadio Community](https://community.flexradio.com/discussion/8023242/)
- [DDUtil v3 features — k5fr.com](https://k5fr.com/DDUtilV3wiki/index.php?title=Features)
- [SDR Console v3 user settings location — sdr-radio.com](https://www.sdr-radio.com/user-settings)
- [Ham Radio Deluxe logbook databases — HRD support](https://support.hamradiodeluxe.com/support/solutions/articles/51000052690-managing-logbook-databases)
- [Wireshark Windows folders — Wireshark User's Guide](https://www.wireshark.org/docs/wsug_html_chunked/ChWindowsFolder.html)
