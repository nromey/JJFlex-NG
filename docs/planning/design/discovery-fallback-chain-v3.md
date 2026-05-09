---
type: architectural design memo — round 3 (canonical, supersedes round 2)
status: ACK locked 2026-05-08 — build authorized for `track/discovery-chain-full-buildout` against this spec; round-4 update folds in Q1/Q2/Q9 empirical bits as they arrive
draft date: 2026-05-06
ack date: 2026-05-08
supersedes: docs/planning/design/discovery-fallback-chain.md (round 2, ACK'd 2026-05-04)
draft author: Claude
synthesis source: docs/planning/research/discovery-cascade-v3/streams 1, 3, 4, 5, 6, 7
deferred input: docs/planning/research/discovery-cascade-v3/stream-2-flex-internals.md (awaiting hardware time; folds in via round 4 update)
related memories:
  - project_flexlib_4218_discovery_investigation.md
  - project_friction_tax_principle.md
  - project_no_silent_phone_home.md
  - project_flexibility_principle.md
  - project_anti_patterns_from_blindcat.md
  - project_no_silent_keystrokes_rule.md
  - project_dialog_escape_rule.md
  - project_localization_strings_file.md
  - project_jjflex_data_provider.md
  - project_trace_persistence_design.md
  - project_sprint29_crash_reporter_vision.md
  - project_chained_updater_pattern.md
---

# Discovery as a chain, not a gate (round 3)

## Frontmatter

This memo is the canonical specification for the JJ Flex discovery cascade as of 2026-05-06. It supersedes `docs/planning/design/discovery-fallback-chain.md` (round 2). Round 2 framed the cascade as six rungs (1a, 1b, 2, 3, 4, 5). Round 3 doubles the catalog of methods from comprehensive research, formalizes a background `NetworkAddressChanged` watchdog as a separate architectural layer, codifies per-rung diagnostic capture, and bakes in the manual-fallback UX from Stream 6.

Round 2 remains valuable as the foundational reasoning record — its motivations, friction-tax framing, R6-canary narrative, and FlexLib `Radio.CreateFromIp` vendor-patch pattern are unchanged and not re-litigated here. This memo is additive: it broadens the rung catalog, sharpens the architecture, and locks the manual-recovery UX.

`**** ACK` from Noel locks this memo as canonical and authorizes Phase 2+ build-now-ship-later work on `track/discovery-cascade-v3`.

## 1. Context — what changed since round 2

R6 shipped on 2026-05-05 with two of the six round-2 rungs implemented: Rung 1a (Cached LAN IP via `radioConnectionCacheV1.xml`) and Rung 1b (Cached WAN IP for SmartLink users). Rungs 2, 3, 4, 5, plus the diagnostic-capture phase, were explicitly scoped out via the orchestrator-direction file. Code lives at `C:\dev\jjflex-ng\Radios\DiscoveryChain\` (`DiscoveryChain.cs`, `CachedLanIpRung.cs`, `CachedWanIpRung.cs`, `RadioConnectionCache.cs`, `IDiscoveryRung.cs`).

End-to-end empirical validation succeeded on 2026-05-06: the 4.1-line cache-writer backport (build `4.1.16.241`, polish-fixed to `4.1.16.242`) populated `radioConnectionCacheV1.xml` via normal UDP discovery on Don's 4.1 install. R6 then connected via Rung 1a in 296ms — proving the upgrade-path scenario works exactly as round 2 designed it. **The cache-seeded migration path is no longer hypothetical.**

The remaining gap: a fresh-install user on a UDP-broken machine with an empty cache still has no path to connect. Round 2's Rungs 2–5 address this gap but were never specified in implementation detail and were dropped from R6 to keep the diagnostic-build scope tight. Noel authorized round-3 research with the directive "spare nothing — research new options of discovery, even ones we may never use, because friction-tax must not be paid by the user." Six research streams completed on 2026-05-06; one (Stream 2, FlexLib internals) is still awaiting hardware time. This memo synthesizes the six available streams.

The conclusion of the synthesis: **the round-2 six-rung catalog was sound but incomplete.** The cascade should expand to ~10 rungs plus a persistent background watchdog. Several novel-cheap rungs (hosts file, ARP read, TCP table read, hostname-pattern probe, third-party config scrape) are decisive when they hit and cost very little when they miss. The cascade also needs explicit per-network gating, multi-interface enumeration, identity verification, and a screen-reader-first manual-fallback UX.

## 2. Comprehensive method catalog

Every discovery method surfaced in any stream, ranked for v3 inclusion. Hit-rate is qualitative ("rate at which this method finds the radio when it runs against a randomly-chosen JJ Flex install where the radio is in fact reachable"). LOC is rough implementation cost in C# / .NET 10. Priority codes:

- **must** — required for v3 release (ships in 4.2.0 cascade)
- **should** — strong should-have; ship in 4.2.x if not 4.2.0
- **speculative** — promising but evidence-light; build only with empirical evidence justifying it
- **skip** — explicitly ruled out, with rationale captured in §10

| # | Method | Source streams | Hit rate | LOC | Priority |
|---|---|---|---|---|---|
| 1 | Cached LAN IP (`radioConnectionCacheV1.xml`) | round 2, S5 | High (returning users) | shipped | must |
| 2 | Cached WAN IP (SmartLink users) | round 2, S5 | High (SmartLink users) | shipped | must |
| 3 | Cached LAN IP — N-deep history (last 5 IPs) | S5 §3.14, S7 §2.8 | Med-high (multi-network users) | 60 | must |
| 4 | ARP / IPv4 neighbor cache (`GetIpNetTable2`) | S1 §1, S4 Pattern F, S7 §2.21 | Medium-high | 100 | must |
| 5 | TCP connection table (`GetExtendedTcpTable`) | S1 §3, S7 §2.9 | Medium (SmartSDR co-running) | 80 | must |
| 6 | Hosts file scrape | S1 §14, S7 §2.1 | Low-medium, decisive | 30 | must |
| 7 | Third-party config scrape (one rung, walks all apps) | S3 (entire), S7 §2.9 | High cumulative | 300-500 | must |
| 8 | Hostname-pattern probe (DNS+mDNS via OS resolver) | S7 §2.3 | Unknown, free on miss | 50 | must |
| 9 | Reverse-DNS PTR sweep of local /24 | S7 §2.2 | Medium | 80 | should |
| 10 | TCP/4992 subnet probe with ARP pre-filter | round 2 R2, S4 Pattern G, S5 §3.13 | High when subnet correct | 200-300 | must |
| 11 | UDP discovery (FlexLib's standard path) | round 2 R3 | High when wire-broken-radio is rare | already exists | must |
| 12 | DNS resolver cache (`DnsGetCacheDataTable`) | S1 §5 | Low today, forward-compat | 80 | should |
| 13 | Function Discovery cache-only (SSDP+WSD) | S1 §12 | Low today, forward-compat | 150-200 | should |
| 14 | Tailscale / WireGuard peer scrape | S5 §3.6, S7 §2.5 | Low today, growing | 70-100 | should |
| 15 | UPnP-IGD client / port-mapping scrape | S7 §2.4 | Medium | 150 | should |
| 16 | Directed unicast probe to cached IP (UDP/4992) | S4 Pattern C | Unknown — needs FlexLib confirm | 50 | speculative |
| 17 | SmartLink-as-LAN-fallback | round 2 R4 | Universal SmartLink fallback | 100 | must |
| 18 | SmartLink-as-metadata-provider (WAN IP without relay) | S7 §2.19 | High SmartLink + LAN coincident | 150-400 | should |
| 19 | Manual IP entry (with full Stream 6 UX) | round 2 R5, S6 | Universal escape hatch | 600-900 | must |
| 20 | NetworkAddressChanged background watchdog | S7 §2.7 | N/A — multiplier on rungs 1-19 | 120 | must |
| 21 | NLM gating for cached-IP rungs | S1 §8, S5 §3.21 | N/A — pre-filter | 80 | must |
| 22 | Adapter enumeration & filter (skip-list of virtuals) | S5 §3.1–3.7, S4 Pattern E | N/A — pre-filter | 100 | must |
| 23 | NetBIOS name cache (`nbtstat -c`) | S1 §9 | Near-zero | 40 | skip |
| 24 | LLMNR cache enumeration | S1 §10 | N/A — no API | 0 | skip |
| 25 | Wireless BSSID neighbor (`WlanGetAvailableNetworkList`) | S1 §21 | Zero for IP discovery | 0 | skip |
| 26 | WinHTTP/WinINET proxy bypass | S1 §15 | Zero | 0 | skip |
| 27 | `NetServerEnum` (Computer Browser) | S1 §22 | Zero | 0 | skip |
| 28 | Reverse DNS active probe (`Dns.GetHostEntry`) | S1 §23 | Low + sends packets | 0 | skip |
| 29 | SSDP M-SEARCH active probe | S4 §3.5, S7 §2.6 | Near-zero | 0 | skip |
| 30 | WS-Discovery active probe (UDP/3702) | S7 §2.6 | Near-zero | 0 | skip |
| 31 | SNMP public-community sweep | S7 §2.10, §5 | Zero (Flex no SNMP) | 0 | skip |
| 32 | SLP / Service Location Protocol | S7 §2.11, §5 | Zero | 0 | skip |
| 33 | LLDP / CDP receive | S7 §2.12, §5 | Zero, requires Npcap | 0 | skip |
| 34 | ICMP broadcast / subnet-mask request | S7 §2.13, §5 | Zero (deprecated) | 0 | skip |
| 35 | Passive DHCP snooping | S5 §3.6, S7 §2.14 | Requires Npcap+admin | 0 | skip |
| 36 | IPv6 NDP / Router Advertisement listening | S5 §3.8, S7 §2.15 | Zero (Flex IPv4-only) | 0 | skip |
| 37 | Bluetooth-LE / Wi-Fi-Direct browse | S7 §2.16 | Zero (Flex has no BT) | 0 | skip |
| 38 | USB / Device Manager scrape | S7 §2.17 | Zero (Flex Ethernet only) | 0 | skip |
| 39 | Wireshark `.pcap` file scrape | S3 Tier 4, S7 §2.18 | Privacy minefield + ~1% hit | 0 | skip |
| 40 | NTP fingerprinting against candidate IPs | S7 §2.20 | Niche (FLEX-8000 GPSDO only) | 80 | skip |
| 41 | Apple Bonjour Sleep Proxy | S7 §5 | Zero | 0 | skip |
| 42 | Matter / Thread / HomePlug | S7 §5 | Zero | 0 | skip |
| 43 | Generic mDNS service-type fishing (`_services._dns-sd._udp.local`) | S7 §2.22 | Low — fold into hostname-pattern rung | +30 | speculative |
| 44 | PnP-X / `Windows.Devices.Enumeration.Pnp` | S7 §2.23 | Collapses to SSDP+WSD | 0 | skip |
| 45 | Link-local /16 scan (APIPA recovery) | S5 §3.9 | Niche, user-invoked only | 200 | should |
| 46 | Captive-portal detection probe | S5 §3.36 | N/A — diagnostic, not discovery | 60 | should |

The "must" items below are wired into the v3 cascade. The "should" items are queued for 4.2.x. Speculative items are gated on empirical evidence. Skip items are documented in §10 so future contributors don't re-litigate them.

## 3. Recommended cascade composition

The v3 cascade is a layered architecture, not a flat list. From outside in:

- **Background layer** — NetworkAddressChanged watchdog (§5) lives independently of any user-initiated discovery. Re-triggers the cascade when the laptop changes networks.
- **Pre-filter layer** — NLM-gating, adapter enumeration, and identity-verification rules apply to every rung.
- **Concurrent passive layer** — Rungs that read existing state with no network traffic. Run all in parallel.
- **Concurrent active layer** — Rungs that send probes. Run in parallel after passive layer either succeeds or completes.
- **Last-resort layer** — Manual fallback dialog (Stream 6 UX), surfaced only after all automated rungs exhaust.

Within each rung, every successful discovery enters the **identity-verification phase**: TCP handshake + serial-match against the user's preferred radio, before the result is treated as authoritative. This is non-negotiable per Stream 5 §3.16 and §4.4 (avoid auto-connecting to the wrong radio in households with multiple Flexes).

The numbered rungs below run in two batches — passive batch first, active batch second. Within each batch, rungs run concurrently; first valid result wins and cancels the others. Rung numbers correspond to round-2 numbering where possible; new rungs interpolated.

### Rung 1a — Cached LAN IP (SHIPPED in R6)

**Purpose.** When the user has connected to this radio from this machine before on this network, the radio's LAN IP is in `%AppData%\JJFlexRadio\radioConnectionCacheV1.xml`. Probe it directly; skip all discovery work.

**Implementation.** Already shipped. `Radios\DiscoveryChain\CachedLanIpRung.cs` reads the cache, calls `Radio.CreateFromIp(model, serial, name, ip, version)` (FlexLib vendor-patch factory at `Radio.cs:2111`), attempts a TCP/4992 handshake, returns a Radio handle on success. Cache write happens on every successful Connect via `RadioConnectionCache.RecordConnectedRadio(radio)`. The 4.1-line cache-writer backport (build `4.1.16.241+`) seeds `radioConnectionCacheV1.xml` on installs that haven't yet upgraded to 4.2.x.

**LOC estimate.** Already shipped (~250 LOC for `CachedLanIpRung.cs` + `RadioConnectionCache.cs` + `IDiscoveryRung.cs`).

**Hit-rate scenarios.** Returning user on the same network: high (>90%). Returning user on a different network: NLM gate (Rung 21) skips the probe entirely. Fresh install: empty cache, rung returns `cache_empty` instantly.

**Failure modes.** Cache file missing (fresh install — expected, instant fall-through). Cache file present but cached IP no longer responds (radio off, IP changed, Wi-Fi blip). Cached IP responds but with a different serial than expected (DHCP rotation collided with another radio in the household). Each surfaces a distinct outcome tag.

**What gets logged.** Per attempt: cached IP, age of cache entry, NLM network match (yes/no/no-cache), TCP connect timing, identity-exchange result, outcome tag.

### Rung 1b — Cached WAN IP (SHIPPED in R6)

**Purpose.** SmartLink-configured users have a cached external IP from prior SmartLink sessions. Direct TCP/TLS to that IP on `PublicTlsPort` skips a portion of the SmartLink auth round-trip. Especially valuable in NAT-loopback scenarios where the radio is on the same network as the client but local discovery couldn't see it (S7 §2.19 cross-reference).

**Implementation.** Already shipped. `Radios\DiscoveryChain\CachedWanIpRung.cs`. Per round 2's auth-research: the `wan validate handle=<H>` command may still require a SmartLink server round-trip for the per-attempt token. The "constrained case" implementation accepts this; the "best case" (handle informational only) requires a 5-minute live test on a real radio that hasn't been done yet.

**LOC estimate.** Already shipped (~150 LOC).

**Hit-rate scenarios.** SmartLink user on the same WAN as the radio: high. SmartLink user away from the radio: depends on whether the radio's external IP has changed since cache.

**Privacy note.** Cached WAN IPs are LOCAL ONLY. Per `project_no_silent_phone_home.md` they must never appear in trace exports, crash bundles, or Data Provider sync. Add to redaction list when the support-package format is finalized. Cache write code already enforces this (no transmission path exists today).

**What gets logged.** Cached WAN IP (locally), `PublicTlsPort` value, validate-handle outcome, total elapsed.

### Rung 1c — N-deep cached LAN IP history (NEW, must-build)

**Purpose.** Augment Rung 1a with the last 5 IPs we've seen the radio at (not just the most recent). Solves S5 §3.14: if the radio's IP rotates between two reserved DHCP pools, or the user has multiple home networks, multiple cache hits cover those cases. Also solves S7 §2.8: contest stations with home + portable + remote shack environments.

**Implementation approach.** Extend `RadioConnectionCacheEntry` with a `History: List<HistoricalLanIp>` field where each entry has `(ip, network_id_guid, timestamp)`. NLM gate filters the history list to entries whose `network_id_guid` matches the current connected network's GUID. The remaining entries are probed in parallel via `Task.WhenAny` over `TcpClient.ConnectAsync(ip, 4992)`. First successful identity-verified result wins.

**LOC estimate.** ~60 LOC additional to existing cache + rung machinery. Schema bump: `radioConnectionCacheV1.xml` → `radioConnectionCacheV2.xml` (or backward-compatible additive field — preferable to avoid migration).

**Hit-rate scenarios.** Roaming users with stable NLM identity per network: high. Single-home users: zero marginal benefit (still works as Rung 1a does). Cost is essentially zero in single-home case (the history list has length 1).

**Failure modes.** Same as Rung 1a, multiplied by N. Aggressive timeout per-IP (1.5s) so total wall-clock for an empty history fan-out stays under 2s.

**What gets logged.** Number of history entries probed, per-entry outcome, NLM-filtered-out count.

### Rung 1.5a — ARP / neighbor cache read (NEW, must-build)

**Purpose.** Windows already maintains a list of recently-seen LAN IP/MAC pairs. If the radio has communicated with anything on the LAN recently (the gateway, the JJ Flex machine itself, anything), its IP is sitting in the ARP table, free to read. Strictly cheaper than any active probe — no packets sent, no admin needed. (S1 §1, S4 Pattern F, S5 §3.26.)

**Implementation approach.** P/Invoke `GetIpNetTable2(AF_INET, ...)` from `iphlpapi.dll` via `Vanara.PInvoke.IpHlpApi`. Filter by FlexRadio's IEEE-registered MAC OUI prefix (Stream 2 will confirm exact bytes; provisional set per `maclookup.app/vendors/flexradio-systems`). Filter by interface LUID against `GetAdaptersAddresses` to drop loopback and virtual-adapter entries (S5 §3.34, §3.3). Each surviving candidate IP enters identity verification.

**LOC estimate.** ~100 LOC including P/Invoke setup, MAC OUI filter, virtual-adapter filter, and integration with the rung pipeline. Vanara dependency is already a candidate for several other rungs in this design; one-time addition.

**Hit-rate scenarios.** Don's specific case (FlexLib 4.2.18 silent-discovery): the radio is broadcasting at the wire and the gateway is talking to it, so the ARP entry is almost certainly populated. **This rung was intended to cover Don's exact scenario in round 2 but was deferred from R6 — building it for v3 is the highest-ROI pre-fresh-install fix.** Fresh-install user where the radio recently spoke to the LAN: high. User where the radio is off or the LAN segment is firewalled: returns nothing.

**Failure modes.** Stale ARP entries (S5 §3.26). Hyper-V / WSL2 / Docker / VPN entries appearing in the table (S5 §3.3-3.7) — handled via the adapter-LUID filter. Virtual-adapter MAC overlap with FlexRadio OUI — extremely unlikely; flag in trace if it occurs.

**What gets logged.** Number of total ARP entries, count after virtual-adapter filter, count matching FlexRadio OUI, per-IP identity-verification result.

### Rung 1.5b — TCP connection-table read (NEW, must-build)

**Purpose.** If any process on this machine has recently held an open or `TIME_WAIT` socket to remote port 4992 on a local-subnet IP, that's almost certainly a Flex. Catches the SmartSDR-running-side-by-side case (user has SmartSDR open and launches JJ Flex) and the SmartSDR-recently-closed case (`TIME_WAIT` state persists ~120s on Windows defaults). (S1 §3, S7 §2.9.)

**Implementation approach.** P/Invoke `GetExtendedTcpTable` with `TCP_TABLE_OWNER_PID_ALL` from `iphlpapi.dll` via `Vanara.PInvoke.IpHlpApi`. Filter for `dwRemotePort == 4992` AND `dwRemoteAddr` in a local subnet (cross-reference `GetAdaptersAddresses`). Surviving candidate IPs enter identity verification.

**LOC estimate.** ~80 LOC.

**Hit-rate scenarios.** SmartSDR co-running or recently-closed: very high signal, near-zero false-positive rate. Other scenarios: empty result, instant fall-through.

**Failure modes.** Other processes happening to use TCP/4992 to a non-Flex destination — extremely unlikely on a typical home LAN; identity verification catches it.

**What gets logged.** Total TCP connections enumerated, per-PID owner of any candidate, identity-verification result.

### Rung 1.6 — Hosts file scrape (NEW, must-build)

**Purpose.** Users who have manually pinned their radio's IP in `%SystemRoot%\System32\drivers\etc\hosts` (a cargo-cult fix path on FlexRadio forums) are explicitly telling us the IP. Free to read, decisive when it hits. (S1 §14, S7 §2.1.)

**Implementation approach.** `File.ReadAllLines(Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\drivers\etc\hosts"))`. Parse each non-comment line as `<IP> <name1> [<name2> ...]`. Filter:
- IP must be in a private RFC1918 range (`10/8`, `172.16/12`, `192.168/16`) OR `169.254/16`
- Any name on the line contains case-insensitive `flex`, `flexradio`, `smartsdr`, OR matches the cached radio nickname (which we already know from `radioConnectionCacheV1.xml`), OR matches the user's known callsign

Surviving candidates enter identity verification.

**LOC estimate.** ~30 LOC.

**Hit-rate scenarios.** Power-user with manual pinning: decisive. Everyone else: empty result, microsecond-scale fall-through.

**Failure modes.** Stale hosts file entries — identity verification catches.

**What gets logged.** Number of non-comment lines, number matching filters, per-candidate identity-verification result.

### Rung 1.7 — Third-party config scrape (NEW, must-build, single rung, walks all apps)

**Purpose.** Almost every JJ Flex user has SmartSDR installed; SmartSDR's `%APPDATA%\FlexRadio Systems\SSDR.settings` is the most likely source of a working IP on a fresh install. Reading it (with one-time user consent at first run per Stream 3 privacy spec) yields an instant working IP for the migration scenario. The same rung walks PowerSDR mRX, WSJT-X, N1MM Logger+, Log4OM, Wave-Flex Integrator, and ~12 other apps' config files. (Stream 3 entire; S1 §19; S7 §2.9.)

**Implementation approach.** Single rung named `ThirdPartyConfigScrapeRung`. Iterates a prioritized walk:

1. SmartSDR family (`SSDR.settings`, `CAT.settings`, `DAX.settings`, `filter.txt`)
2. PowerSDR mRX (`database.xml` and versioned variants)
3. Direct-Flex-API third-party (Wave-Flex Integrator JSON, DDUtil v3 INI, SDR Console v3 XML)
4. Hamlib-pattern apps (WSJT-X / JS8Call / N1MM / Log4OM / fldigi / N3FJP ACLog / WriteLog) — loopback-filtered

Per-file algorithm: existence check → open with `FileShare.ReadWrite` → defensive XML/JSON parse → fallback to IPv4 regex sweep → filter loopback (`127/8`) and obviously non-LAN addresses → aggregate → deduplicate → identity-verify in parallel.

The known-app-paths list is loaded from a JSON manifest (`thirdparty-cache-paths.json`) shipped in the JJF installer. Per Stream 3 open question 1, this manifest can later be updated via the JJ Flexible Data Provider, letting us add new apps without an app release.

**Privacy.** First-run prompt (Stream 3 §"Required user disclosure"): "JJ Flexible Radio Access tries hard to find your radio without making you type its IP address. To do this, it may read the settings files left on this computer by SmartSDR, PowerSDR, WSJT-X, N1MM Logger+, and similar ham-radio programs you've installed — but only to extract IP addresses, and only on this computer. Nothing is sent anywhere; no other data is collected. You can disable this in Settings → Connection → Discovery." Default: enabled. Trace records paths checked, IP-token counts, never raw file contents.

**LOC estimate.** ~300-500 LOC across one provider per app + dispatcher + manifest loader.

**Hit-rate scenarios.** Cumulative across apps: high. SmartSDR alone covers ~70% of new-user installs.

**Failure modes.** Files locked by running apps (open with `FileShare.ReadWrite`). Schema drift across vendor versions (defensive parse + regex fallback). Encrypted formats (skip `.ssdr_cfg` per Stream 3 §"SmartSDR — `*.ssdr_cfg` profile exports").

**What gets logged.** Per app: path checked, file existed (yes/no), IPv4-token count, candidates contributed. Per candidate: identity-verification result. Outcome tag: `success` / `no_files_found` / `no_ips_extracted` / `all_probes_failed` / `parse_error_only` / `disabled_by_user`.

### Rung 1.8 — Hostname-pattern probe (NEW, must-build)

**Purpose.** Try resolving a small list of plausible hostnames via `Dns.GetHostAddressesAsync(name)` — Windows' resolver transparently chains DNS → mDNS → LLMNR → NetBIOS, so one API call covers four protocols. Free when names don't resolve, decisive when they do. Pairs especially well with users on Tailscale MagicDNS or pfSense local-DNS-with-PTR setups. (S7 §2.3.)

**Implementation approach.** Build candidate-name list from cached metadata:

- Static: `flex.local`, `flexradio.local`, `smartsdr.local`
- Per cached entry: `flex-<serial>.local`, `flex-<nickname>.local`, `<nickname>.local`, `<nickname>.lan`, `<nickname>.home.arpa`, `<nickname>` (no suffix)
- Per user metadata: `<callsign>-flex.local`
- DNS-suffix-search: `flex.<corp.tld>` for each suffix in the user's DHCP-assigned search list

Fan out via `Task.WhenAny` over `Dns.GetHostAddressesAsync(name)` with a 2-second per-name timeout. First resolved address that survives identity verification wins.

**LOC estimate.** ~50 LOC.

**Hit-rate scenarios.** Tailscale + cached nickname: high. pfSense `Register DHCP leases in DNS resolver` + radio with set hostname: high. Default consumer router without local DNS: low.

**Failure modes.** None concerning — name doesn't resolve, instant fall-through.

**What gets logged.** Names attempted, resolutions returned, identity-verification result.

### Rung 1.9 — Reverse-DNS PTR sweep of local /24 (NEW, should-build)

**Purpose.** Many home routers (UniFi, pfSense, OPNsense, OpenWrt with dnsmasq, Pi-hole, ASUS-WRT) auto-populate PTR records from DHCP leases. A parallel `Dns.GetHostEntryAsync` against each of `192.168.1.0` through `192.168.1.255` reveals hostnames containing "flex", "smartsdr", or the cached nickname — without sending any TCP probes. (S7 §2.2.)

**Implementation approach.** Determine local /24 from `GetAdaptersAddresses`. Fan out 256 parallel `Dns.GetHostEntryAsync(ip)` with 1-second per-IP timeout and concurrency cap of 32. Each resolved hostname is pattern-matched (`flex` substring or cached nickname). Survivors enter identity verification.

**LOC estimate.** ~80 LOC.

**Hit-rate scenarios.** UniFi/pfSense home router: high. ISP combo modem: low. Empirical hit-rate is the open question (§12 #5).

**Failure modes.** Slow DNS server amplifies cascade latency — concurrency cap and aggressive per-IP timeout bound it. False positives via hostnames containing "flex" but pointing to non-Flex devices — identity verification catches.

**What gets logged.** IPs queried, names resolved, names matching filter, identity-verification per candidate.

### Rung 2 — TCP/4992 subnet probe with ARP pre-filter (UPDATED from round 2)

**Purpose.** When passive rungs all fail, walk the local subnet actively. For each IP in the local /24, attempt a TCP connection to port 4992 with a short timeout. Any response that completes the FlexLib handshake is a candidate. (Round 2 R2; S4 Pattern G; S5 §3.13.)

**Implementation approach.** Pre-filter using the ARP-cache result from Rung 1.5a: probe ARP-known IPs first (highest probability of success, lowest probe cost), then sweep the rest. For each interface's /24, enumerate hosts; fan out `TcpClient.ConnectAsync(ip, 4992)` with 100ms timeout and concurrency cap of 16. Use `Task.WhenAny` to take the first successful identity-verified result.

**Per-network politeness:** Per Stream 5 §3.13, throttle probes to ~5/sec and prefer non-probe rungs on networks marked `Public` in the Windows network profile. On `Private` networks, full-speed probe. First-time-on-a-new-Public-network shows a one-time consent dialog: "JJFlex needs to send a small number of network probes to find your radio on this network. Some workplace networks treat this as suspicious activity. Continue?"

**Subnet-broadcast preference per Stream 5 §3.40:** Send broadcast probes to subnet-directed broadcast (`192.168.1.255`) computed from the bound interface, fall back to `255.255.255.255` only if subnet-directed yields no responses.

**Per-interface enumeration per Stream 4 Pattern E and Stream 5 §3.1:** Enumerate via `NetworkInterface.GetAllNetworkInterfaces()`, filter to operational + non-virtual (skip-list per Stream 5 §3.3), bind socket explicitly to interface IP (not `0.0.0.0`). Run per-interface in parallel.

**LOC estimate.** ~250-350 LOC including the per-network gating, per-interface enumeration, ARP pre-filter integration, and consent-prompt machinery.

**Hit-rate scenarios.** Subnet correctly identified: high. Multi-NIC machine where Windows picked the wrong default-route interface: high (we cover all interfaces). Tailscale machine: per Stream 5 §3.6, bind to LAN interface explicitly to bypass tailnet swooping.

**Failure modes.** Corporate firewall blocks 4992 outbound (S5 §3.12 — distinguish RST from timeout). IDS/IPS treats sweep as port scan (S5 §3.13 — throttle on Public networks). AP isolation (S5 §3.18 — surfaced in failure UX). Full-100%-timeout on Wi-Fi → diagnostic message "this network appears to block device-to-device communication." Unusually large subnet (S5 §3.13 `subnet_too_large` — bail with diagnostic and suggest manual entry).

**What gets logged.** Per interface: subnet probed, hosts enumerated, probe count, response counts classified `connected` / `refused` / `timeout` / `network-unreachable`. Per response: TCP timing, identity-verification result. Network profile (Public/Private/Domain). Consent decision (yes/no/never-asked).

### Rung 3 — UDP discovery (LOCKED, today's path retained)

**Purpose.** Today's FlexLib UDP-broadcast discovery path. Listens on UDP/4992 for the radio's 1Hz VITA-49 broadcast announcement. (Round 2 R3.)

**Implementation approach.** No changes from current `Discovery.cs`. Per Stream 4 anti-pattern AP-2, even when the FlexLib 4.2.18 silent-discovery investigation closes, this rung stays — we never deprecate a rung that has historically worked for any user.

**Per-interface enumeration per Stream 4 Pattern E:** Bind UDP listener to each operational LAN interface, not just `0.0.0.0`. Tailscale-aware: when Tailscale is detected (S5 §3.6), explicitly avoid binding to the tailnet adapter for broadcast send.

**LOC estimate.** Existing path; ~20-50 LOC for per-interface binding refactor if not already structured that way.

**Hit-rate scenarios.** Working firmware + healthy LAN: high. Don's affected case (FlexLib 4.2.18): the silent-discovery investigation parked at memory entry `project_flexlib_4218_discovery_investigation.md`; chain-not-gate makes this rung's failure non-fatal.

**Failure modes.** Multicast/broadcast filtered (S5 §3.19, §3.20). Multi-NIC default-route mis-selection (S5 §3.1 — covered by per-interface enumeration). Tailscale interception (S5 §3.6 — covered by interface filter).

**What gets logged.** Per interface: socket bind result, listen duration, broadcast-receive count, source IPs of received broadcasts, parse results.

### Rung 4 — SmartLink-as-LAN-fallback (LOCKED from round 2)

**Purpose.** When the user is configured for SmartLink AND none of the LAN-side rungs worked, fall through to SmartLink. Slower (cloud round-trip) but works when LAN is misbehaving. (Round 2 R4; S4 Pattern A.)

**Implementation approach.** Existing SmartLink path with a one-line wrapper: trigger SmartLink connect when all prior rungs return no result AND user has valid SmartLink credentials.

**SmartLink-as-fallback-row UX (NEW from S4 Pattern A):** SmartLink-known radios appear in the radio chooser AS the cascade is running, not only after it gives up. This is the "guaranteed-non-empty UI seed" pattern that FlexRadio's own SmartSDR/Maestro use and is widely the single biggest UX win in the surveyed corpus. Local entries replace SmartLink entries when local discovery later succeeds. Implementation: when SmartLink is configured, populate the radio chooser ListBox with SmartLink radios immediately at cascade start, with a visual/spoken marker "via SmartLink"; LAN-discovered radios with the same serial overwrite the SmartLink row.

**LOC estimate.** ~100 LOC including the fallback-row UX in the radio chooser.

**Hit-rate scenarios.** SmartLink user with online radio: high. Non-SmartLink user: rung skipped silently (not an error per Stream 6 error catalog `wan_cache_no_smartlink`).

**Failure modes.** Per Stream 6 error catalog: `smartlink_offline`, `smartlink_no_radios`, `smartlink_radio_offline`. Each surfaces a distinct user-facing message.

**What gets logged.** SmartLink connection attempt timing, radio list returned, per-radio reachability.

### Rung 4b — SmartLink-as-metadata-provider (NEW, should-build)

**Purpose.** Even without establishing a SmartLink relay, the SmartLink coordinator may return the radio's last-known external IP (`{nickname, public_ip, model, serial}` per the SmartLink-API-Reference April 2026 draft). For users at home with their radio (NAT-loopback scenario), the public IP rolls back to the LAN IP automatically — JJF connects directly without a relay tunnel. (S7 §2.19.)

**Implementation approach.** Add a `--metadata-only` mode to the SmartLink auth code. Returns radio metadata without establishing a relay. The returned `public_ip` enters the same identity-verification phase as any other rung.

**LOC estimate.** ~150-400 LOC depending on how much SmartLink auth refactoring is needed (`AuthFormWebView2.cs` was built for interactive auth).

**Open question.** Per S7 §5 #6, does the SmartLink coordinator return radios that are currently OFFLINE? If yes, even better — IP is correct, just need radio power. Empirical test deferred until 4.2.x sprint.

**What gets logged.** Per-radio metadata returned (locally; never exported), identity-verification result.

### Rung 5 — Manual IP entry (LOCKED from round 2; UX from Stream 6)

**Purpose.** The honest "type your radio's IP here" fallback. Surfaces only after all automated rungs exhaust. (Round 2 R5; Stream 6 entire.)

**Implementation approach.** Per Stream 6 §"Layout proposal A," a single non-resizable WPF dialog (560×620) titled "Find My Radio" with five regions: explanation banner, last-known addresses ListBox, manual IP entry TextBox, action-buttons row (Scan my LAN now / Show me how to find it / Try cascade again), collapsible diagnostic region. Footer: Save and Connect / Cancel. **No wizard.** Tab order, keyboard shortcuts, mnemonic conflict checks all per Stream 6 spec.

**Pre-dialog prompt:** Per Stream 6 §"Top-level prompt," the dialog never opens cold. It is preceded by a "We couldn't find your radio. Would you like help finding it?" plain-language prompt with three buttons: *Help me find it* / *Try again* / *Cancel*. The dialog is the *Help me find it* destination.

**Help text catalog and error catalog:** Per Stream 6 §"Help text catalog" and §"Error catalog" — every user-facing string is specified in those sections, ready to paste into resource files when the centralized-utterances architecture (`project_localization_strings_file.md`) lands.

**Test connection button:** Per Stream 6 §"Region 3" — separate from Save and Connect, performs a quick TCP/4992 handshake against the typed IP, with progress speech and clear pass/fail announcement. Required for screen-reader users who have no visual cue that an address is reachable.

**Forget this address:** Per Stream 6 §"Region 2" — soft-delete with 24-hour undo window. Honors flexibility principle (user controls their own data).

**LOC estimate.** ~600-900 LOC including dialog XAML, code-behind, sub-dialog (`FindIpHelpDialog`), pre-dialog prompt, integration with cascade orchestrator.

**Hit-rate scenarios.** N/A — last-resort dialog; success rate depends entirely on whether the user can find their IP.

**Failure modes.** User can't find IP via any of the help-text paths. Per Stream 6 the dialog provides "Show me how to find it" sub-dialog with plain-language guidance for Fing app, router admin page, sighted helper, FlexRadio support. Also `Test connection` failure pathway covers "address looks valid but doesn't respond."

**What gets logged.** Dialog opened, user actions (selected row / typed IP / pressed Scan / pressed Help), Test result, final outcome.

### Rung 5-recovery — APIPA / link-local /16 scan (NEW, should-build, user-invoked only)

**Purpose.** Per Stream 5 §3.9, FlexRadio community confirms 6700 and 6600M models occasionally fail DHCP at power-on and self-assign in `169.254/16`. The standard cascade can't find them because Rung 2 probes only the user's normal /24. A dedicated link-local /16 scan recovers them, but at 65,536 addresses it's too slow for the default cascade.

**Implementation approach.** Surfaced via the manual fallback dialog as a separate button: "Look for radio in DHCP-failure mode" under "Tools → Recovery" or in the diagnostic region. Sample sparsely first: probe a random 1024 addresses in `169.254/16` to see if anything answers, then narrow if a hit zone is identified. Skip `169.254.169.254` (cloud metadata, S5 §3.35).

**LOC estimate.** ~200 LOC.

**What gets logged.** Probes sent, sampling strategy, hit IPs.

## 4. Background layer — NetworkAddressChanged watchdog (NEW, must-build)

This is **NOT a rung.** It is a persistent background subscription that lives independently of any user-initiated discovery action. Its job: detect when the laptop has joined a new network and re-run the cascade automatically without user intervention. (S7 §2.7.)

The pattern eliminates what Stream 7 estimates is 30-50% of "JJF doesn't see my radio" support requests — the case where the user takes their laptop from home shack to portable site, JJF still tries to reach the home IP, and the user has no idea why discovery stopped working.

### Justification for top-level architectural status

Streams 1, 4, 5, and 7 all converge on this pattern from different angles:

- **Stream 1 §17** documents `NotifyIpInterfaceChange`, `NotifyAddrChange`, and `NotifyUnicastIpAddressChange` as Windows-native primitives.
- **Stream 4 Pattern H** (heartbeat-based passive presence, Sonos/SSDP) reframes mid-session connection loss as a discovery-level event, not a connection-level event.
- **Stream 5 §3.2 (USB-Ethernet adapters)** and §3.25 (NIC sleep/wake) require re-enumeration at every discovery attempt and on `NetworkAvailabilityChanged` events.
- **Stream 7 §2.7** explicitly proposes the watchdog as a multiplier on every other rung.

Burying this pattern as a sub-feature of any single rung would understate its impact. It's a layer.

### Implementation approach

Subscribe to `System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged` at app startup. On event:

1. **Debounce.** Network-change events fire 3-8 times in rapid succession during a typical Wi-Fi reconnect. Wait 2-3 seconds after the last event before acting. (S7 §5 #9.)
2. **Cancel any in-flight cascade.** If a cascade is already running and the network just changed, the in-flight cascade's results are about a stale environment.
3. **Re-fetch NLM identity.** Use `INetworkListManager.GetCurrentConnectivity()` to determine the new network's GUID.
4. **Re-run the cascade.** Pre-filter cached-IP rungs (Rungs 1a, 1c) by NLM GUID match against the new network. If no cached entries match, skip directly to Rungs 1.5a, 1.5b, 1.6, 1.7, 1.8, 1.9, 2 in parallel.
5. **Speak the result.** If the new cascade succeeds: "Now connected to your radio at the new network." If it fails: "JJ Flex couldn't find your radio on this network. Press F2 to enter the radio chooser." Per `project_no_silent_keystrokes_rule.md`, the watchdog never re-runs silently.

**Configurable.** Per `project_flexibility_principle.md` and Stream 5 §4.9, the user can disable the watchdog (Settings → Connection → Discovery → "Re-search for my radio when my network changes"). Default: enabled when JJ Flex is in the foreground, disabled when in background (battery / network-impact preservation per S4 Open Question 4).

### LOC estimate

~120 LOC including event registration, debounce, cancellation token wiring, and integration with the cascade orchestrator.

### What gets logged

Per event: timestamp, prior network GUID, new network GUID, debounce wait, in-flight cascade cancellation count, re-run cascade outcome.

## 5. Architecture

### 5.1 Concurrent vs sequential

The round-2 sequential model (Rung 1a → 1b → 2 → 3 → 4 → 5) is replaced by a two-batch concurrent model:

**Batch 1 — passive layer (concurrent, 1-2 second wall-clock budget).** All passive rungs fire in parallel from a single async context. They share the result aggregator via a thread-safe channel. As soon as any passive rung returns an identity-verified result, the cascade short-circuits. Passive rungs:
- Rung 1a (cached LAN IP)
- Rung 1b (cached WAN IP, SmartLink only)
- Rung 1c (N-deep history)
- Rung 1.5a (ARP read)
- Rung 1.5b (TCP table read)
- Rung 1.6 (hosts file)
- Rung 1.7 (third-party config scrape)
- Rung 1.8 (hostname-pattern probe — DNS resolver call sends one packet but typically resolves from cache; classified passive for orchestration purposes)
- Rung 1.9 (reverse-DNS PTR sweep, optionally — could move to active batch if measured slow)

**Batch 2 — active layer (concurrent, 5-10 second wall-clock budget).** Active probe rungs fire in parallel only after Batch 1 either returns no result OR completes:
- Rung 2 (TCP/4992 subnet probe with ARP pre-filter)
- Rung 3 (UDP discovery)
- Rung 4b (SmartLink-as-metadata-provider)

**Batch 3 — fallback (serial, user-interactive).** If Batch 2 also returns no result:
- Rung 4 (SmartLink-as-LAN-fallback) — automatic if SmartLink configured
- Rung 5 (manual IP entry) — surfaces the Stream 6 "We couldn't find your radio" prompt

**SmartLink-as-fallback-row UX exception:** Per Stream 4 Pattern A, SmartLink radios appear in the chooser AS Batch 1 and Batch 2 are running. This breaks the strict "Rung 4 only after Batch 2 fails" rule for the *displayed list*, but not for the *connection action* — the cascade still privileges LAN-discovered results when they arrive.

### 5.2 Per-rung timeouts

| Rung | Timeout | Rationale |
|---|---|---|
| Rung 1a | 1.5s | TCP handshake on healthy LAN <100ms; 1.5s tolerates Wi-Fi blip |
| Rung 1b | 3s | Internet RTT plus TLS handshake |
| Rung 1c | 1.5s per IP, 2s wall-clock cap | Parallel fan-out |
| Rung 1.5a | 50ms | OS API read |
| Rung 1.5b | 50ms | OS API read |
| Rung 1.6 | 5ms | File read |
| Rung 1.7 | 500ms per file, 2s wall-clock cap | Disk I/O + parse |
| Rung 1.8 | 2s per name, 3s wall-clock cap | DNS chain timeout |
| Rung 1.9 | 1s per IP, concurrency 32, 5s wall-clock cap | DNS load |
| Rung 2 | 100ms per IP, concurrency 16, 5s wall-clock cap | TCP handshake budget |
| Rung 3 | 5s | One full broadcast cycle plus margin |
| Rung 4 | 30s | SmartLink server round-trip + auth |
| Rung 4b | 5s | Metadata-only path |
| Rung 5 | user-controlled | Modal dialog |

**Adaptive timeout per Stream 5 §3.24:** Send a baseline ping to the gateway first to estimate RTT; multiply per-rung timeout by `max(default, gateway_rtt × 5)` for slow-network environments (satellite, weak Wi-Fi, mobile broadband). Per-network learned timing persists in the per-network-state cache.

**Total cascade ceiling:** 30s default, user-configurable up to 5 minutes per `project_stuck_modal_escape_design.md`. Escape always works.

### 5.3 NLM-based network identity gating

Cached-IP rungs (1a, 1b, 1c) are gated by network identity match. Use `INetworkListManager.GetCurrentConnectivity()` and `GetNetworks(NLM_ENUM_NETWORK_CONNECTED)` to get the current network's GUID. Compare against the cached `network_id_guid` per cache entry. Mismatch → skip the rung entirely (per S1 §8 — "without NLM gating, Rung 1a wastes time probing 192.168.1.42 when the user is on a coffee-shop hotspot in 10.0.0.0/24").

Cross-reference via the `NetworkList\Profiles` and `NetworkList\Signatures` registry trees (S1 §16) for `DefaultGatewayMac` confirmation when NLM GUID seems unstable.

NLM gating is also the trigger for the background watchdog (§4): network GUID change implies cached-IP candidates need to be re-evaluated against the new network.

### 5.4 Adapter enumeration & filter

Performed at the start of every cascade attempt (per S5 §3.2 — USB adapters can appear/disappear between attempts).

Algorithm:
1. `NetworkInterface.GetAllNetworkInterfaces()`
2. Filter: `OperationalStatus == Up`, `NetworkInterfaceType` is one of {`Ethernet`, `Wireless80211`, `Tunnel`} (skip `Loopback`, `Unknown`)
3. Description regex skip-list per S5 §3.3: `vEthernet \(*`, `Hyper-V*`, `WSL*`, `Docker*`, `VirtualBox Host-Only*`, `VMware*`, `Tailscale*`, `OpenVPN*`, `WireGuard*`, `TAP-Windows*`, `NordVPN*`, `ExpressVPN*`, `Mullvad*`, `Cisco AnyConnect*`
4. Has at least one IPv4 unicast address that's not `169.254.x.x`
5. Has a non-zero gateway

Surviving interfaces are the LAN-discovery surface. Each rung that touches the network binds explicitly to one of these (per S4 Pattern E; never `0.0.0.0`) and runs per-interface in parallel.

**Tailscale special case (S5 §3.6):** Detect by adapter description `Tailscale*` OR process name `tailscale.exe` running OR any local interface IP in `100.64.0.0/10`. When detected, **do not skip the tailnet adapter for the Tailscale peer scrape rung** (it's the source of truth there) but **do skip it for broadcast/multicast rungs** (Tailscale swoops UDP broadcasts). For unicast rungs, prefer LAN-bound socket explicitly.

### 5.5 Identity verification

Every candidate IP from any rung enters identity verification before the cascade trusts it:

1. TCP connect to candidate IP, port 4992, 2-second timeout
2. FlexLib handshake (`Radio.CreateFromIp(model, serial, name, ip, version)` factory)
3. Compare returned serial against the user's preferred radio serial (from `radioConnectionCacheV1.xml` last-connected entry)
4. **Match** → pass to the connection orchestrator; cancel all other in-flight rungs
5. **Mismatch** → log as "found a different radio" candidate; do not auto-connect; continue cascade
6. **Multiple mismatches** → after cascade exhaustion, surface the Stream 5 §3.16 chooser dialog: "Found 2 radios but none match your saved radio. Choose one (this will update your saved radio) or cancel."
7. **No saved preferred radio (fresh install)** → first identity-verified result is offered to the user via the standard radio chooser

ARP-sourced and TCP-table-sourced IPs are tagged "needs verification, low confidence" — not because they're more likely wrong, but because they're hints, not facts. Cache (Rungs 1a/1b/1c) IPs are "needs verification, medium confidence." UDP-broadcast-discovered IPs are "needs verification, high confidence" (the radio explicitly announced itself). The confidence labels feed into trace and into how the cascade orchestrator orders identity-verification when many candidates arrive at once.

### 5.6 Cancellation semantics

Once the orchestrator has an identity-verified result, ALL other in-flight rungs receive a cancellation token. This is critical for the active layer — a Rung 2 subnet probe that's halfway through a /24 must abort cleanly so we don't keep pounding the network after success.

Cancellation propagates through `CancellationToken` plumbed into every rung's API (the `IDiscoveryRung.RunAsync(IDiscoveryContext, CancellationToken)` method already has this hook).

Cancellation also fires on user action (Cancel button in the Stream 6 dialog), on background watchdog re-trigger (§4), and on cascade-ceiling timeout (§5.2).

### 5.7 Retry behavior

Cascade does not retry rungs within a single attempt. Each rung gets one shot. If the user wants to retry, they press the *Try again* button in the Stream 6 manual-fallback prompt, which re-runs the whole cascade fresh.

The background watchdog (§4) is the retry mechanism for environment changes. The user explicitly retrying a static environment is the manual-fallback dialog's job.

### 5.8 Per-network learned state

Per S5 §4.9, persist per-network (key: NLM GUID, fall back to SSID + gateway-MAC composite):
- Best-performing rung from prior successes
- Effective timeout from prior successes
- Detected pathologies (Public profile, suspected AP isolation, slow link, captive portal seen before)

Bound to ~50 entries with LRU eviction. File: `%AppData%\JJFlexRadio\perNetworkDiscoveryStateV1.xml`. LOCAL ONLY per `project_no_silent_phone_home.md` (SSIDs and gateway-MACs are sensitive).

## 6. Diagnostic capture & privacy

Per Stream 5 §5 and `project_trace_persistence_design.md`, every cascade attempt contributes a structured discovery section to the trace archive. The structure follows Stream 5's recommendation: per-attempt header, environment snapshot, per-rung capture, verification phase capture, outcome capture. Single JSON object per attempt, appended to a discovery-specific log within the LZMA2-compressed manifest-driven archive.

### 6.1 What each rung logs

Detailed in §3 per-rung "What gets logged" entries. Common fields across all rungs:
- Rung identifier and version
- Start timestamp, end timestamp, elapsed ms
- Per-interface attempt records (rung X may run on N interfaces in parallel)
- Probe count sent, response count received
- Per-response: IP, MAC if available, response payload classification, serial extracted if available
- Error classification: success / partial / timeout / refused / network-unreachable / WSA-error-code / blocked-by-policy
- Outcome tag (vocabulary per rung; standardized across rungs)

### 6.2 Routing through trace persistence

Per `project_trace_persistence_design.md`, the discovery cascade is a sub-phase of the connection attempt. Every attempt — success or failure — produces a manifest entry with the discovery JSON embedded. 30-day auto-prune. Local-first with optional NAS mirror.

### 6.3 Privacy redaction per `project_no_silent_phone_home.md`

When traces are bundled for support upload (the user-initiated "Ask for help" flow per `project_sprint29_crash_reporter_vision.md`), strip:

- **WAN IPs** (Rung 1b cache, Rung 4b metadata) — geo-correlatable, can hint at user location
- **SSID and BSSID** — wireless network identification
- **Gateway MAC OUI** — geo-correlatable
- **Tailscale tailnet device names** — private network membership
- **Hosts file content beyond matched candidates** — user's local DNS overrides may include private context
- **Third-party app config file content beyond matched IP candidates** — vendor configs may contain credentials/callsigns/log entries

Strip-on-upload, not strip-on-capture. The local trace must retain diagnostic richness for the user's own debugging. Stripping happens at the bundle-build stage in the crash receiver flow.

### 6.4 No silent phone-home reconfirmation

Per project memory, JJ Flex never sends discovery data, traces, or any other state to any server without per-event explicit user action. The discovery cascade conforms: every rung reads local state or sends LAN-bounded probes. The single exception is Rung 4/4b (SmartLink), which is a user-explicitly-configured cloud relay path.

## 7. Manual fallback UX (Rung 5)

Stream 6's Layout A is the canonical recommendation. Rather than restate Stream 6 in full, this section captures the authoritative cross-references and the structural commitments locked into the v3 design.

### 7.1 Pre-dialog "We couldn't find your radio" prompt

The user-facing entry point. Per Stream 6 §"Top-level prompt":

```
Title: We couldn't find your radio
Body:
  We tried six different ways to reach your radio on your local network and
  none of them worked. This usually means one of three things:

  • Your radio is powered off or still booting up.
  • Your radio is on a different network than this computer.
  • Your radio is on the network but our discovery messages didn't reach it.

  Would you like to:

  [ Help me find it ]   [ Try again ]   [ Cancel ]
```

Speak on open: `"We couldn't find your radio. Three buttons: Help me find it, Try again, Cancel."` at Critical verbosity.

### 7.2 Find My Radio dialog (Stream 6 Layout A)

Single non-resizable WPF `JJFlexDialog` (560×620), titled "Find My Radio." Five regions: explanation banner / last-known-addresses ListBox / manual IP entry TextBox / action buttons (Scan my LAN now, Show me how to find it, Try cascade again) / collapsible diagnostic region. Footer: Save and Connect / Cancel.

**Locked decisions (per Stream 6 §"Executive summary"):**

1. **No wizard.** Layout B (wizard) explicitly rejected — wizards add per-step orientation cost for screen-reader users, and BlindCat590's wizard is one of the documented anti-patterns we don't repeat.
2. **Last-known addresses list is the load-bearing affordance.** Surfaced FIRST, with manual IP entry below. Most users pick a row and never type an octet.
3. **Help text grouped by user-capability ladder, not data source.** "(a) what JJ Flex already knows, (b) what you can ask another device or person, (c) what you'd ask a sighted helper to read for you, (d) what you'd ask FlexRadio support."
4. **Test connection is separate from Save and Connect.** Blind users have no visual cue an address is reachable; explicit Test with progress speech is required.
5. **No silent rung failures, no silent button presses.** Every cascade-failure error from Stream 6's Error catalog is spoken at Critical verbosity AND visible in the diagnostic region. Every button press produces speech feedback per `project_no_silent_keystrokes_rule.md`.

### 7.3 Cross-references to full Stream 6 spec

- Region-by-region spec: Stream 6 §"Region-by-region spec"
- Tab order: Stream 6 §"Tab order"
- Keyboard shortcuts: Stream 6 §"Keyboard shortcuts"
- Help text catalog (every user-facing string): Stream 6 §"Help text catalog"
- Error catalog (per-rung user-friendly failure messages): Stream 6 §"Error catalog"
- First-launch integration: Stream 6 §"First-launch integration recommendation" (the manual fallback is NOT surfaced automatically on first launch — only after auto-discovery actually fails)
- FindIpHelpDialog (sub-dialog with plain-language IP-finding guidance): Stream 6 §"'Show me how to find it' sub-dialog"

### 7.4 Live validation and accessibility

Per Stream 6 §"Region 3" and §"Why no input mask":
- No input masks (accessibility-hostile)
- Live polite validation feedback via `AutomationProperties.LiveSetting=Polite`
- Validation rule order per Stream 6 §"Live validation rules"
- Clipboard auto-paste hint: if the clipboard contains a valid-looking IP, speak "You have an address on your clipboard. Press Ctrl+V in the address field to paste it." (friction-tax win for users who just looked up the IP elsewhere)

## 8. Edge case handling

This section captures the cascade's response to the most impactful edge cases from Stream 5. Full matrix in Stream 5 §3 (46 cases). Not all 46 require explicit cascade behavior; the ones below do.

| Edge case | Cascade behavior |
|---|---|
| **Multi-NIC machines (S5 §3.1)** | Per-interface rung enumeration. Each rung binds to interface IP explicitly. Failure UX names which interface(s) were probed. |
| **USB-Ethernet adapters (S5 §3.2)** | Re-enumerate interfaces at start of every attempt. NetworkAddressChanged watchdog catches mid-session adapter changes. |
| **Hyper-V / WSL2 / Docker / VirtualBox virtual switches (S5 §3.3-3.5)** | Description-regex skip-list. Trace logs which adapters were filtered and why. |
| **Tailscale tailnet adapter (S5 §3.6)** | Detect via process/adapter/IP triad. Bias toward direct unicast rungs. Bind sockets to LAN interface IP explicitly. Surface educational message in failure UX. Tailscale peer scrape rung uses tailnet adapter as the source of truth. |
| **Other commercial VPNs (S5 §3.7)** | Skip-list for adapter enumeration. If VPN is the only operational interface, surface "All your network connections go through your VPN" message. |
| **APIPA / link-local 169.254.x.x (S5 §3.9)** | Dedicated Rung 5-recovery, surfaced via manual fallback dialog only. Never in default cascade. |
| **Mobile hotspot small subnet (S5 §3.10)** | Detect /28 or smaller; surface iOS-vs-Android hotspot guidance. Probe full small subnet (negligible cost). |
| **Corporate firewall blocks 4992 outbound (S5 §3.12)** | Distinguish RST from timeout. RST on all hosts → "A firewall may be blocking JJFlex from reaching your radio." |
| **IDS/IPS treats subnet probe as port scan (S5 §3.13)** | Throttle to ~5 probes/sec on Public networks. One-time consent dialog on first sweep on a new Public network. |
| **DHCP lease changes mid-session (S5 §3.14)** | Aggressive 1.5s timeout on cached-IP rungs. Atomic cache update on successful new-IP discovery. N-deep history (Rung 1c) covers lease rotation. |
| **Multiple FlexRadio products on same LAN (S5 §3.16)** | Identity verification serial-match gates all rungs. Chooser dialog when multiple non-matching radios respond. |
| **Wi-Fi roaming / band-steering (S5 §3.17)** | Out of cascade scope — diagnostic explainer message only ("Try connecting your laptop to the same Wi-Fi band the radio uses"). |
| **AP isolation (S5 §3.18)** | Detect via 100% timeout (not refused) on /24 + Public profile. Surface "This Wi-Fi network appears to block device-to-device communication." Suggest tether to phone hotspot or SmartLink. |
| **Multicast filtering (S5 §3.19, §3.20)** | Cascade does not declare success based only on broadcast/multicast emptiness — always continues to unicast rungs. Multicast self-test (send to self, expect echo) optionally detects local-only-multicast environments. |
| **Windows Public vs Private profile (S5 §3.21)** | NLM query at startup. If Public + LAN discovery, surface educational message. Don't change profile programmatically. |
| **First-launch vs returning user (S5 §3.22)** | Detect cache empty; speak "Searching for your radio for the first time..." vs "Connecting to your radio...". Skip cached-IP rungs entirely. |
| **Headless / startup-script launch (S5 §3.23)** | Detect via `Environment.UserInteractive` + `--auto-connect` flag. Extended timeouts. On total failure: log + status message; don't block app start. Manual fallback rung skipped. |
| **Slow / high-latency networks (S5 §3.24)** | Adaptive timeout via gateway-RTT estimate. Per-network learned timing in §5.8. |
| **ARP offload returns stale entries (S5 §3.26)** | ARP-sourced IPs marked "needs verification, low confidence." Identity verification rejects stale. |
| **Antivirus / endpoint-protection blocks raw sockets (S5 §3.28)** | Wrap all socket exceptions. Aggregate WSA error codes. Surface "Some network operations were blocked, possibly by your antivirus or endpoint protection software." |
| **Multicast TTL 1 — radio on different VLAN (S5 §3.29)** | Diagnostic message when Rung 2 finds radio on different /24 than laptop's primary. |
| **Captive portal interception (S5 §3.36)** | Detection probe (HTTP GET to known endpoint, check for redirect). Surface "You're connected to a network that requires login in a web browser." |
| **Network-name change / wrong-network (S5 §3.32)** | Surface SSID and gateway IP in failure diagnostic. |
| **Loopback interface noise (S5 §3.34)** | Adapter filter excludes loopback explicitly. |
| **Cloud metadata service `169.254.169.254` (S5 §3.35)** | Skip in any link-local probe rung. |
| **WinSock corruption (S5 §3.37)** | Wrap socket creation in try/catch. On Winsock catalog errors, surface "Windows networking appears to be in a broken state. Try `netsh winsock reset` from an admin command prompt and reboot." |
| **Bluetooth PAN (S5 §3.42)** | Included in adapter enumeration; filterable by description regex. |
| **Multiple Windows users on same machine (S5 §3.27)** | Cache is per-user (`%AppData%`). Each user's cache is independent. |
| **Accessibility — failure-message verbosity (S5 §3.45)** | Coarse-milestone announcements during active cascade. Structured spoken diagnostic on demand: "Press 1 for what to try next, press 2 for technical details, press Escape to dismiss." |
| **Accessibility — cannot self-diagnose router (S5 §3.46)** | Audible network-state check ("Your gateway 192.168.1.1 is reachable" vs "not responding"). Information-the-user-can-act-on framing before suggesting sighted-helper actions. |

The remaining edge cases in Stream 5 §3 (3.8 IPv6, 3.11 CGNAT, 3.15 radio reboot, 3.31 NIC promiscuous, 3.33 IPv4 fragmentation, 3.38 R8 focus interaction, 3.39 DHCP renewal timing, 3.40 broadcast addressing, 3.41 IPv6 forward-looking, 3.43 SO2R, 3.44 contest weekend) are either out of scope (IPv6, CGNAT) or covered by the design implicitly (radio reboot = same as cache stale; SO2R = per-instance preferred-serial; contest weekend = adaptive timeout).

## 9. Methods explicitly ruled out

Captured here so future contributors don't re-litigate. Each entry has rationale and the source stream that ruled it out.

### From Stream 1 (passive-read primitives)

- **LLMNR cache enumeration** — no public API exists; LLMNR resolutions surface in regular DNS resolver cache (Rung 1.8) anyway.
- **Wireless BSSID neighbor (`WlanGetAvailableNetworkList`)** — gated behind location permission; zero IP-discovery value.
- **WinHTTP/WinINET proxy bypass** — never contains LAN device IPs.
- **`NetServerEnum` (Computer Browser)** — service deprecated and disabled by default; Flex doesn't participate.
- **`WNetEnumResource` (SMB)** — Flex doesn't expose shares.
- **Reverse DNS active probe (`Dns.GetHostEntry`)** — sends packets on cache miss, not strictly passive.
- **Synchronous `IUPnPDeviceFinder`** — issues SSDP M-SEARCH; Function Discovery cache mode covers passive-read case.

### From Stream 7 (novel methods)

- **SNMP public-community sweep** — Flex firmware does not run an SNMP agent.
- **SLP (Service Location Protocol, RFC 2608)** — dead protocol in consumer/prosumer space.
- **LLDP / CDP receive** — switch-to-switch protocols; Flex doesn't transmit.
- **ICMP broadcast / subnet-mask request** — deprecated by RFC 6633; modern OSes don't respond.
- **Passive DHCP snooping** — requires Npcap + admin; sees only renewal traffic during listen window.
- **IPv6 NDP / Router Advertisement / Neighbor Solicitation** — Flex is IPv4-only as of firmware 4.x.
- **Bluetooth-LE / Wi-Fi-Direct service browse** — Flex has no Bluetooth radio; doesn't speak Wi-Fi Direct.
- **USB / Device Manager scrape** — FLEX-6000/8000 use Ethernet only (FLEX-1500/3000/5000 USB out of scope per Phase 0).
- **Wireshark `.pcap` file scrape** — privacy minefield + ~1% hit rate.
- **NTP fingerprinting (FLEX-8000 GPSDO)** — niche, <5% of users.
- **Apple Bonjour Sleep Proxy** — compound dependency, near-zero hit rate.
- **Matter / Thread / HomePlug** — Flex shows no signs of adopting.
- **PnP-X / `Windows.Devices.Enumeration.Pnp`** — collapses to SSDP+WSD (already covered).
- **Active SSDP M-SEARCH** — Flex doesn't advertise via SSDP.
- **Active WS-Discovery probe (UDP/3702)** — Flex doesn't speak DPWS; WSDAPI .NET 10 path is COM-interop pain for ~zero hit rate.
- **BJNP** — vendor-specific to Brother/Canon.
- **`GetIfTable2`, `GetIpForwardTable2` as discovery sources** — return our own interface stats, not peer info. (They ARE used as scope-derivation inputs — see §5.3 / §5.4.)
- **WMI `Win32_TcpConnection`** — equivalent to `GetExtendedTcpTable` with added latency.
- **Active mDNS browse for `_flex._tcp` specifically** — Flex doesn't currently advertise; if it ever does, Rung 1.8's hostname-pattern probe catches it via OS resolver chain.

### From Stream 3 (third-party config scrape)

- **SmartSDR `.ssdr_cfg` profile exports** — encrypted format; reverse-engineering would stretch "reading other apps' files" past the ethical/effort line.
- **HRD logbook SQLite** — QSO data, not radio config.
- **N1MM contest SQLite databases** — same.
- **DXLab Suite registry walk** — high friction, low yield (most paths loopback).
- **OmniRig** — loopback-only for Flex.
- **JTAlert** — downstream of WSJT-X; whatever WSJT-X knows, we already got.
- **FRStack `app.config` `WhiteListedIPAddresses`** — not radio IPs.
- **Wireshark recent files** — not a normal-user surface; pcap parsing is overkill.
- **Bonjour mDNS resolver cache** — Flex doesn't use mDNS.

## 10. Implementation phasing

Opinionated, not exhaustive.

### 4.2.0 (release-blocking — must build before 4.2.0 ships)

The rungs and architecture pieces required to make a fresh-install JJ Flex on a UDP-broken machine able to connect to a Flex without manual IP entry. This is the chicken-and-egg gap that R6 didn't close.

1. **Rung 1.5a (ARP read)** — highest-ROI fix for Don's specific scenario. ~100 LOC.
2. **Rung 1.5b (TCP table read)** — covers SmartSDR-coexistence case. ~80 LOC.
3. **Rung 1.6 (hosts file scrape)** — trivial cost, decisive when hits. ~30 LOC.
4. **Rung 1.7 (third-party config scrape)** — this is the load-bearing fresh-install rung. SmartSDR alone covers ~70% of new installs. ~300-500 LOC.
5. **Rung 2 (TCP/4992 subnet probe with ARP pre-filter, per-interface, per-network politeness)** — required for fresh installs without any third-party app. ~250-350 LOC.
6. **Adapter enumeration & filter (§5.4)** — load-bearing for Rungs 2, 3, 4b. ~100 LOC.
7. **NLM gating (§5.3)** — load-bearing for Rungs 1a, 1b, 1c. ~80 LOC.
8. **Identity verification (§5.5)** — non-negotiable per Stream 5 §3.16. ~80 LOC.
9. **Concurrent batch orchestration (§5.1)** — wraps Rungs 1a/1b/1c/1.5a/1.5b/1.6/1.7 into Batch 1, Rungs 2/3 into Batch 2. ~150 LOC.
10. **Cancellation semantics (§5.6)** — wired through `CancellationToken`. ~30 LOC.
11. **Per-rung diagnostic capture (§6.1)** — minimum viable trace structure. Routes through `project_trace_persistence_design.md`. ~150 LOC.
12. **Manual fallback dialog (Stream 6 Layout A)** — surfaced after Batch 2 fails. ~600-900 LOC.
13. **NetworkAddressChanged background watchdog (§4)** — eliminates a documented 30-50% of "JFC didn't notice the network changed" support requests. ~120 LOC.
14. **First-run consent prompt for third-party config scrape** — required per Stream 3 privacy spec.

**Total LOC estimate for 4.2.0 release-blocking work:** ~2,300-2,900 LOC across rung implementations, orchestration, and UX. Spread across ~3 sprints if sequenced conservatively; achievable in 1-2 sprints with parallel tracks (cascade core / dialog UX / third-party config scrape providers can fan out cleanly).

### 4.2.x (post-release, not blocking 4.2.0)

15. **Rung 1c (N-deep cached LAN IP history)** — additive to Rung 1a; ~60 LOC.
16. **Rung 1.8 (hostname-pattern probe)** — high-value for Tailscale/pfSense users; ~50 LOC.
17. **Rung 1.9 (reverse-DNS PTR sweep)** — empirically validate hit rate first; ~80 LOC.
18. **Rung 4 (SmartLink-as-LAN-fallback) and SmartLink-as-fallback-row UX** — UX win is high; ~100 LOC.
19. **Rung 4b (SmartLink-as-metadata-provider)** — gated on SmartLink auth refactor scope; ~150-400 LOC.
20. **DNS resolver cache rung (Rung 12)** and **Function Discovery cache rung (Rung 13)** — forward-compatibility insurance; ~80+150 LOC.
21. **Tailscale / WireGuard peer scrape rung (Rung 14)** — strategically aligned with remote-shack audience; ~100 LOC.
22. **UPnP-IGD scrape rung (Rung 15)** — medium hit rate, free `GetExternalIPAddress` benefit; ~150 LOC.
23. **Per-network learned-state cache (§5.8)** — ~100 LOC.
24. **Adaptive timeout via gateway-RTT estimate (§5.2)** — ~80 LOC.
25. **Captive-portal detection probe (§3.36 / §8)** — ~60 LOC.
26. **APIPA / link-local /16 recovery rung (Rung 5-recovery)** — surfaced from manual fallback dialog; ~200 LOC.

### Sprint 30+ / speculative

27. **Directed unicast probe to cached IP** (S4 Pattern C) — gated on Stream 2 confirming FlexLib responds to unicast UDP/4992 probes.
28. **DHCP snooping rung** (S5 §3.35, S7 §2.14 ruled out today) — only revisit if Npcap dependency becomes acceptable for other reasons.
29. **Cloud-registered phone-home alternative** (S4 Pattern N) — user-opt-in radio registration with the JJ Flex Data Provider for a discovery-free reconnect path. Aligns with multi-radio expansion arc.
30. **Generic mDNS service-type fishing** (S7 §2.22) — fold into Rung 1.8 only if empirical evidence shows Flex advertises any mDNS service.
31. **Manifest-driven third-party app paths** (Stream 3 open question 1) — let the JJ Flexible Data Provider update the known-app-paths list without a JJF release.

## 11. Open questions for Noel's round-3 review

Distilled from the streams' open questions plus questions surfaced during synthesis. Numbered for easy reference in the ACK pass.

1. **FlexRadio MAC OUI prefix(es) for ARP filter (Rung 1.5a) — empirical data needed.** Stream 1 §1 and §"Open questions" #1 flag this. Noel's runbook (Stream 2) is best positioned to confirm by reading a known Flex's MAC from `radioConnectionCacheV1.xml` or a Wireshark capture. **Without confirmed OUI bytes, Rung 1.5a is broken.**

2. **SmartSDR `SSDR.settings` schema — empirical data needed.** Stream 3 open question #1. Cracking open Noel's own SSDR.settings would let us write a schema-aware parser instead of regex-only. Optional but improves Rung 1.7 hit rate and trace cleanliness.

3. **Background watchdog default — foreground-only, always-on, or user-configurable?** S5 / S7 split: foreground-only minimizes battery/network impact; always-on maximizes catch rate. Recommend foreground-only default with clear toggle in Settings → Connection → Discovery. Noel's ACK or alternative.

4. **Third-party config scrape consent UX — first-run prompt, blanket on-by-default, or opt-in only?** Stream 3 §"Required user disclosure" recommends on-by-default with first-run disclosure. Noel's friction-tax framing supports this. Confirm.

5. **NLM gating of cached-IP rungs — strict (skip on mismatch) or soft (try anyway with low priority)?** Strict avoids wasted probes on coffee-shop hotspots. Soft handles the case where NLM GUID is unstable across `Forget Network` actions. Recommend strict, with empirical validation that NLM stability is sufficient. Open per S1 #5.

6. **Rung 1c N-deep history — how many IPs to retain per radio?** S5 §3.14 says 3-5; S7 §2.8 says 5-20. Recommend 5 with LRU eviction. Confirm.

7. **Subnet-probe consent dialog on Public networks — show every time, once per network, or never?** S5 §3.13 proposes once per new network. Confirm or alternative.

8. **Rung 4 SmartLink-as-fallback-row UX — show as the cascade runs (Pattern A) or only after cascade exhaustion?** Stream 4 strongly recommends "as the cascade runs" — single biggest UX win in the surveyed corpus. Confirm.

9. **Rung 4b SmartLink-as-metadata-provider — require empirical handle-strict-vs-informational test before building?** Or build the constrained-case version now and upgrade to best-case after live test? Recommend build constrained-case now. Confirm.

10. **Manifest-driven third-party app paths via Data Provider (Stream 3 open question 1) — defer to 4.2.x or fold into 4.2.0 Rung 1.7 build?** Recommend defer. Confirm.

11. **Adaptive timeout via gateway-RTT estimate — opt-in or default?** S5 §3.24 implies default. Concern: extra startup ping every cascade. Recommend default with skip-on-fast-network heuristic (skip the estimate if the first cached-IP probe succeeds in <50ms).

12. **Rung 1.7 walk priority — alphabetical, configurable, or hardcoded SmartSDR-first?** Recommend hardcoded SmartSDR-first per Stream 3 (highest hit rate). Confirm.

13. **Surface "found via" attribution in the radio chooser?** Stream 3 open question #2. "Found via SmartSDR config" / "found via your network" reinforces the cascade-worked-harder-than-SmartSDR narrative. Light cost. Recommend yes.

14. **Per-network learned state — privacy review.** §5.8 / S5 §4.9. SSIDs and gateway-MACs are sensitive. They're LOCAL ONLY but they accumulate. Acceptable per `project_no_silent_phone_home.md`? Confirm.

15. **Default cascade ceiling — 30s, 60s, or longer?** Round 2 doesn't specify; Stream 6 references the stuck-modal-escape design's 5-minute ceiling. Recommend 30s default with stuck-modal-escape's 5-minute upper bound. Confirm.

## 12. Cross-references

### Round-2 memo and source streams

- `docs/planning/design/discovery-fallback-chain.md` — round-2 memo (superseded by this document)
- `docs/planning/research/discovery-cascade-v3/stream-1-windows-os-primitives.md`
- `docs/planning/research/discovery-cascade-v3/stream-3-thirdparty-config-scrape.md`
- `docs/planning/research/discovery-cascade-v3/stream-4-other-sdr-accessibility-patterns.md`
- `docs/planning/research/discovery-cascade-v3/stream-5-edge-cases.md`
- `docs/planning/research/discovery-cascade-v3/stream-6-manual-fallback-ux.md`
- `docs/planning/research/discovery-cascade-v3/stream-7-novel-methods.md`
- *(deferred)* `docs/planning/research/discovery-cascade-v3/stream-2-flex-internals.md` — folds in via round 4 update

### Code locations (already shipped in R6)

- `Radios\DiscoveryChain\IDiscoveryRung.cs` — rung interface
- `Radios\DiscoveryChain\DiscoveryChain.cs` — cascade orchestrator
- `Radios\DiscoveryChain\CachedLanIpRung.cs` — Rung 1a
- `Radios\DiscoveryChain\CachedWanIpRung.cs` — Rung 1b
- `Radios\DiscoveryChain\RadioConnectionCache.cs` — cache read/write
- FlexLib vendor patch: `Radio.cs:2111` — `Radio.CreateFromIp(model, serial, name, ip, version)` factory

### Project memory

- `project_friction_tax_principle.md` — operational principle this design implements at every level
- `project_no_silent_phone_home.md` — constraint on diagnostic-capture rung; WAN-IP redaction rule; first-run disclosure for third-party scrape
- `project_flexibility_principle.md` — user-controllable rung ordering, watchdog enable/disable, third-party scrape per-app toggles
- `project_anti_patterns_from_blindcat.md` — anti-pattern #1 (no in-app explanation when discovery fails) is the failure mode the manual fallback dialog counter-prescribes
- `project_no_silent_keystrokes_rule.md` — every rung's failure announcement, every dialog button press
- `project_dialog_escape_rule.md` — Escape and Alt+F4 always close every cascade-related dialog
- `project_localization_strings_file.md` — destination for every user-facing string captured in Stream 6's catalogs
- `project_jjflex_data_provider.md` — manifest-driven third-party app paths (post-4.2.0 stretch)
- `project_trace_persistence_design.md` — host for per-rung diagnostic capture
- `project_sprint29_crash_reporter_vision.md` — user-consented export path for diagnostic data
- `project_chained_updater_pattern.md` — generalizable parallel pattern (cascade applies the same chain-not-gate principle to discovery that the chained-updater applies to firmware/plugin updates)
- `project_flexlib_4218_discovery_investigation.md` — the active bug whose impact this design dissolves
- `project_smartlink_login_silent_validation_bug.md` — relevant for Stream 6 manual-IP-entry handoff to SmartLink fallback
- `project_jjflexible_home_terminology.md` — "JJ Flexible Home" terminology in failure-state speech

## 13. Round-3 ACK lock — Noel review 2026-05-08

The 15-question review pull-doc (`docs/planning/for-claude/2026-05-06-discovery-cascade-v3-design-pull.md`, processed and deleted 2026-05-08) was ACK'd with the following locked answers. Where Noel added nuance beyond plain "ACK," the build track must honor the nuance, not the recommendation alone.

### Q1 — FlexRadio MAC OUI prefix(es) for Rung 1.5a — ACK with empirical
Don's MAC data was already collected in a prior round; some Dell-component OUI confusion was sorted out. **Action:** Rung 1.5a OUI filter cites Don's empirical bytes (Dell components were actually present in some Flex unit revisions, so the filter must allow that pattern alongside Flex's primary OUI). When the 8600 unboxes, fold its MAC bytes in via round 4.

### Q2 — SmartSDR `SSDR.settings` schema — ACK, deferred for empirical
Don is not a SmartSDR user, so no file from him. **Action:** Rung 1.7 ships with the regex-only fallback path until a SmartSDR-using tester surfaces a real `SSDR.settings`. Don't block the build on this; round 4 folds in schema-aware parsing when data arrives.

### Q3 — Background watchdog default — ACK with all three options surfaced
Foreground-only is the default, but the user-facing toggle exposes all three: foreground-only / always-on / disabled. **Action:** Settings → Connection → Discovery presents three radio buttons (or equivalent screen-reader-friendly control), default selected on foreground-only, with brief descriptive text per option.

### Q4 — Third-party config scrape consent — ACK
On-by-default with first-run disclosure paragraph. Per-app toggles for granular control. No further nuance.

### Q5 — NLM gating of cached-IP rungs — ACK strict, with caveat
Strict gating is fine **as long as it does not constrict the user's ability to obtain an IP or connect for updating purposes.** **Action:** when NLM identity differs and strict gating skips the cached IP, the cascade falls through to the next rung rather than failing the discovery; the user-visible outcome is "we found your radio anyway via [next-rung-source]" not "we refused to use the cached IP." Strict gating is a *prefer-not-to-probe-on-foreign-networks* preference, not a *refuse-to-connect* posture.

### Q6 — Rung 1c N-deep history — ACK
5 IPs per radio, LRU eviction.

### Q7 — Subnet-probe consent on Public networks — ACK, only when needed
Once per new network — **but only ask for consent when subnet scanning is actually about to happen.** **Action:** if an earlier rung resolves the IP, the consent dialog never fires. The dialog is gated on "we're about to do the subnet probe rung," not on "we joined a public network."

### Q8 — Rung 4 SmartLink-as-fallback-row UX — ACK Pattern A
SmartLink rows surface as the cascade runs (not after exhaustion). Stream 4's biggest UX-win finding stands.

### Q9 — Rung 4b SmartLink-as-metadata-provider — ACK constrained-case now
Build the constrained case now; upgrade to best-case after live test. **Action:** A Don-driven test pass for the constrained-vs-best-case empirical input is acceptable; Noel's 8600 doesn't have to unbox to gate the build.

### Q10 — Manifest-driven third-party paths — ACK defer to 4.2.x
Rung 1.7 ships hardcoded list in 4.2.0. Manifest delivery via Data Provider folds in via 4.2.x. **Why this is a 4.2 thing:** the whole cascade is 4.2 release-scope because it materially helps the updater succeed (which is the whole point of the release), so any 4.2.x deferred items still ship in the 4.2 *line*, not slipped to a hypothetical 4.3.

### Q11 — Adaptive timeout via gateway-RTT — ACK
Default with skip-on-fast-network heuristic (skip the estimate if the first cached-IP probe succeeds in <50ms).

### Q12 — Rung 1.7 walk priority — ACK SmartSDR-first, then alphabetical fallback
Hardcoded SmartSDR-first per Stream 3's hit-rate evidence. **Refinement:** if SmartSDR connect-trial fails, walk the remaining apps **alphabetically** as an automatic progression — **not as a user choice.** Last-resort: ask for data (i.e., open the manual fallback dialog). The user never picks the order; the cascade just falls through one cheap source after another until it succeeds or exhausts.

### Q13 — Surface "found via" attribution in chooser — ACK
"Found via SmartSDR config" / "Found via your network" / etc. Reinforces the cascade-worked-harder-than-SmartSDR narrative.

### Q14 — Per-network learned state — ACK with full transparency UX
LOCAL ONLY (never exported per `project_no_silent_phone_home.md`). **Refinements Noel added — these are part of the spec, not optional:**
- Storage format: JSON local settings file (human-readable on disk).
- Settings dialog: **"View collected data"** button → renders the current state in a WebView2 in human-readable form.
- Settings dialog: **"Remove all collected data"** button → requires user consent first; consent dialog explains "if you do this, [bad things] could happen, but **your data doesn't leave** because of the no-phone-home principle." Link from the consent dialog into the help topic that lists JJ Flex's principles.
- **Help integration:** if a "JJ Flex principles" help topic doesn't exist yet, create one. Link to the same content from `www.jjflexible.radio` (public-facing).
- **All help as web with offline fallback:** consider serving help content from the web when reachable, falling back to bundled offline copy when not (contesting / Field Day / no-internet scenarios). This is a broader help-system architectural pivot, separate from this cascade — captured here as Noel-authorized direction for the help track.

### Q15 — Default cascade ceiling — ACK 30s with stuck-tracking + escalation
30s default; stuck-modal-escape's 5-minute upper bound stays. **Refinements Noel added:**
- Track the count of "stuck" cascades per session and per network.
- If stucks recur, the dialog offers to **extend timing** (give it longer next time).
- If stucks recur, the dialog also offers to **submit feedback with traces to the crash reporter** (the plumbing for which is now in place).

### Closing direction — AetherSDR connection methodology
Noel: *"remember to use AetherSDR connection methodology to connecting to SmartSDR with radio payload. Let's get a new clone of AetherSDR github to keep tabs since surgery."*

**Action:** clone the AetherSDR repo as a sibling reference (e.g., `C:\dev\aethersdr-reference` or similar — outside the JJFlex-NG repo, so its commits aren't pulled in). Use AetherSDR's connection code as the reference implementation when designing the SmartLink-with-radio-payload bits of Rung 4 / Rung 4b. **Cloning is gated on Noel's go**; he flagged it as a tracking action since surgery, not an immediate task.

---

## 14. Confirm or revise (legacy review surface)

This section preceded the §13 ACK lock. Retained for history only — the answers above are canonical.

`**** ACK` — design is locked, this document becomes canonical, `track/discovery-chain-full-buildout` spins up for 4.2.0 release-blocking work.

`**** ` (specific changes) — apply changes; round 4 if substantial.
