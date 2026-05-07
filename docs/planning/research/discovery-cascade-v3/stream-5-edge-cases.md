---
name: Stream 5 — Network and System Edge Cases for LAN Discovery on Windows
date: 2026-05-06
stream: discovery-cascade-v3 / stream 5 (edge cases)
research_question: |
  Enumerate the network and system edge cases that complicate "find a LAN
  device" on Windows. For each: what fails, why, what the user experience is,
  what the cascade should do. Produce a known-issues matrix the cascade design
  can audit against.
audience: cascade architects designing JJ Flex's multi-rung discovery (cached
  IP / TCP probe / ARP / mDNS / SSDP / NetBIOS / LLMNR / manual entry).
out_of_scope: SmartLink (cloud) discovery — this stream is LAN only. WAN /
  remote operation gets a separate stream.
---

# Stream 5 — Edge cases for LAN discovery on Windows

## 1. Executive summary

The five most impactful edge cases — the ones most likely to silently break
the cascade or, worse, return a *wrong* answer the cascade then trusts:

1. **Tailscale "swoops up" UDP broadcasts.** When the user has a Tailscale
   subnet router on the same machine, broadcast and multicast traffic gets
   captured by the WireGuard adapter and never reaches the LAN. This is by
   design and is documented behavior. Any rung that depends on UDP broadcast
   (the original Flex discovery, SSDP M-SEARCH, LLMNR) is at risk on machines
   running Tailscale, which is almost guaranteed on Noel's setup and common
   in the broader user base.

2. **Multi-NIC interface selection is silent and metric-driven.** A laptop
   with both Wi-Fi and Ethernet up will route broadcasts and TCP probes to
   *one* interface based on Windows' route metric, not user intent. The
   "wrong" interface (Ethernet to office LAN, Wi-Fi to ham shack) happily
   answers "no radio here" when the radio is one switch port away on the
   other side. Cascade must enumerate interfaces and probe each independently
   — cannot rely on a single default-route socket.

3. **Virtual adapters poison the ARP table.** Hyper-V vEthernet, WSL2's
   bridge, Docker Desktop, VirtualBox host-only adapters, and VPN clients
   all create entries in `GetIpNetTable2`. The cascade's ARP-scrape rung
   must filter by interface LUID against `GetAdaptersAddresses` operational
   state and adapter type — a naive "iterate all ARP entries" returns
   thousands of false positives on a developer laptop and a few on a normal
   one.

4. **APIPA fallback (169.254.x.x) is real on Flex hardware.** FlexRadio
   community threads document 6700 and 6600M power-up cycles where the radio
   gets an APIPA address until the router-renewal cycle catches up. The
   cascade's subnet probe assumes the radio is on the *user's* /24. If the
   radio is on 169.254/16 and the laptop is on 192.168.1/24, no rung except
   "scan link-local /16" will find it. Worth a dedicated last-resort rung
   that runs only on explicit user invocation because 65,536 hosts is slow
   even at high parallelism.

5. **No-silent-failure rule applies to discovery too.** Per project memory
   (no-silent-keystrokes principle), a discovery rung that fails must
   *announce* its failure with enough context for a screen-reader user to
   choose the next action — not just "discovery failed, retrying." Each
   rung's failure-mode catalog below names the user-visible string the
   cascade should speak. This is non-negotiable for accessibility parity.

A sixth honorable mention: **Wi-Fi NIC ARP-offload + sleep/wake** combine to
produce a state where the laptop "knows" an IP it can no longer reach, which
will mislead the cached-IP rung specifically. Treated in detail in §3.20.

---

## 2. Cascade rung shorthand

The matrix below references rungs by short name. For traceability:

- **R0 — Cached own:** read JJFlex's own connection cache.
- **R1 — Cached scrape:** read SmartSDR's `autoConnectV2.xml` (and
  equivalents from other Flex apps).
- **R2 — TCP probe:** open TCP 4992 (or product-specific port) against each
  /24 host on each non-virtual interface.
- **R3 — ARP read:** enumerate `GetIpNetTable2` and probe MAC OUIs / known
  patterns.
- **R4 — mDNS:** browse `_smartsdr._tcp.local` (or whatever Flex advertises;
  needs confirmation — see open questions).
- **R5 — SSDP:** UDP 1900 M-SEARCH multicast.
- **R6 — NetBIOS:** name-service queries (port 137).
- **R7 — LLMNR:** UDP 5355 multicast name resolution.
- **R8 — Manual:** UI prompt for user-entered IP.

Rung numbering is not commitment to ordering — that's Stream 3's territory.
Used here for reference compactness only.

---

## 3. Edge case matrix

### 3.1 Multi-NIC machines (Wi-Fi + Ethernet simultaneously)

- **Affected rungs:** R2 (subnet probe scoped to wrong /24), R3 (ARP table
  is per-interface but scrape rung may not filter), R4/R5/R7 (multicast
  exits via metric-winning interface only by default).
- **Failure mode:** Windows picks the lower-metric interface for outbound
  broadcasts and multicasts. If Ethernet is plugged into a corporate LAN
  with metric 25 and Wi-Fi is on the ham shack network with metric 50, all
  discovery traffic exits Ethernet. The radio on Wi-Fi never sees a probe.
- **User-visible result:** "No radio found" with no hint that it's an
  interface-selection problem. Power user might check `route print`; a blind
  ham new to the app has no diagnostic path.
- **Cascade should:** enumerate interfaces via
  `NetworkInterface.GetAllNetworkInterfaces()`, filter to operational +
  non-virtual + has-IPv4-unicast, and run R2/R4/R5/R7 *per interface* with
  socket bound to the interface's local IP. Report per-interface results in
  the failure announcement so the user (or a helping ham on a phone call)
  can identify "oh, you scanned the office network not the shack network."
- **Trace capture:** for each interface — Name, Description, Type
  (Wireless80211 / Ethernet / Tunnel / etc.), Status, Speed, IPv4 address
  + mask, Gateway, Metric, MAC OUI, whether discovery rung was attempted,
  per-rung result. Adapter-type filter list also captured (which adapters
  were skipped and why).

### 3.2 USB-Ethernet adapters

- **Affected rungs:** all the multi-NIC ones (§3.1). USB adapters often
  appear as a *second* Ethernet interface that wasn't there at last launch.
- **Failure mode:** USB adapter unplugged means the cached interface index
  is now invalid. USB adapter plugged in mid-session (after JJFlex started)
  means a viable interface that the cascade snapshot doesn't know about.
- **User-visible result:** "Worked yesterday, doesn't today" or "I plugged
  in my dock and now it can't find my radio."
- **Cascade should:** re-enumerate interfaces at the start of *every*
  discovery attempt (not at app startup). Subscribe to
  `NetworkChange.NetworkAddressChanged` to invalidate snapshot caches when
  a new interface appears.
- **Trace capture:** adapter list with timestamps; "interfaces added /
  removed since last attempt" delta.

### 3.3 Hyper-V virtual switches

- **Affected rungs:** R2 (probes phantom /24s), R3 (ARP table contains
  Hyper-V default switch entries that look like real hosts).
- **Failure mode:** Hyper-V's "Default Switch" creates a /20 in the
  172.16-31 range with NAT. Subnet probe will dutifully scan 4,096
  addresses inside the host. ARP table contains entries for VMs that
  aren't reachable from the radio. Worse: Hyper-V default switch IP changes
  every reboot, so cached state is unstable.
- **User-visible result:** discovery takes 30+ seconds longer than it
  should because R2 is grinding through phantom /24s. May find "a host" on
  a Hyper-V network that responds to a port probe but is not a Flex.
- **Cascade should:** filter interfaces by `NetworkInterfaceType` and by
  description regex. Skip anything matching: `vEthernet \(*`, `Hyper-V*`,
  `WSL*`, `Docker*`, `VirtualBox Host-Only*`, `VMware*`, `Tailscale*`,
  `OpenVPN*`, `WireGuard*`, `TAP-Windows*`, `NordVPN*`, `ExpressVPN*`,
  `Mullvad*`, `Cisco AnyConnect*`. Whitelist by physical MAC OUI of known
  NIC vendors as a second-level filter only when the description regex
  is ambiguous.
- **Trace capture:** the full skip list with reason per skipped adapter.

### 3.4 WSL2 vEthernet

- **Affected rungs:** R2, R3 — same family as Hyper-V (WSL2 uses Hyper-V's
  virtualization layer).
- **Failure mode:** WSL2 creates `vEthernet (WSL)` with a
  reboot-changing /20. Same spurious-host problem as §3.3. Additional
  wrinkle: WSL2 distros running their own services may answer port probes
  for ports the user didn't expect.
- **Cascade should:** included in the skip-list of §3.3.
- **Trace capture:** as §3.3.

### 3.5 Docker Desktop adapters

- **Affected rungs:** R2, R3.
- **Failure mode:** Docker Desktop installs `Docker Desktop` adapter and
  may also install `vEthernet (DockerNAT)` on older versions. Container
  bridge networks (`172.17/16`) appear in routing tables. A misconfigured
  filter could send R2 probes into the container-bridge /16.
- **Cascade should:** skip-list (§3.3).
- **Trace capture:** as §3.3.

### 3.6 Tailscale tailnet adapter (PRIORITY EDGE CASE)

- **Affected rungs:** R2, R4, R5, R7 — *and* the original UDP broadcast
  Flex discovery if it runs at all.
- **Failure mode:** Tailscale documents that subnet routers "swoop up" UDP
  broadcasts; any broadcast the OS sends on a non-Tailscale interface can
  still be intercepted by the Tailscale interface depending on routing
  table state. Multicast over the tailnet is explicitly unsupported
  (Tailscale GitHub issues #6184, #11134). On Windows, Tailscale's
  acceptance of subnet routes is on by default.
- **What this means concretely:** if the Flex radio's IP (e.g. 192.168.1.50)
  falls inside a subnet that another tailnet node is advertising as a
  subnet route, the laptop's route to 192.168.1.50 may go via Tailscale
  (over WireGuard, via the relay or via direct UDP) instead of via the LAN
  Wi-Fi/Ethernet interface. TCP and UDP unicast still work — possibly with
  added latency — but broadcast / multicast discovery silently fails.
- **User-visible result:** if the user has a tailnet at home with a subnet
  router, JJFlex on a laptop *at the same LAN* may route to the radio via
  Tailscale unnecessarily. If the user disables "Use Tailscale subnets"
  per the Tailscale docs, behavior reverts. But many users won't know to
  do this.
- **Cascade should:**
  - Detect Tailscale presence: look for `Tailscale` adapter description, or
    process running, or `100.64.0.0/10` IP on any local interface.
  - If detected, **bias toward direct unicast rungs** (R0, R1, R2 with
    explicit interface binding, R3) over broadcast/multicast rungs.
  - Bind discovery sockets explicitly to the LAN interface IP, not 0.0.0.0
    — this forces traffic out the chosen interface and prevents Tailscale
    interception for unicast probes.
  - Surface a one-line educational message in the failure UI: "Tailscale
    detected. Discovery is using direct probes. If your radio is reachable
    via Tailscale only, use Manual IP (Ctrl+M)."
  - Do NOT attempt to disable Tailscale subnet acceptance programmatically.
    User-owned setting.
- **Trace capture:** Tailscale presence (yes/no/unknown), tailnet adapter
  IP, route table snapshot for the radio's expected /24, whether sockets
  bound to interface IP succeeded.

### 3.7 Other VPN adapters (NordVPN / ExpressVPN / Mullvad / commercial)

- **Affected rungs:** R2, R4, R5, R7.
- **Failure mode:** Most commercial VPNs install a TUN/TAP adapter and set
  the default route to 0.0.0.0/0 via the VPN. All non-LAN-bound traffic
  goes through the tunnel. If the cascade sends a broadcast to
  255.255.255.255 without explicit interface binding, it goes to the VPN
  server, where it dies. Some VPNs offer "split tunneling" or "LAN access"
  which fixes this; many disable LAN access by default for security.
- **User-visible result:** "I started my VPN to do work and now JJFlex
  can't find my radio."
- **Cascade should:** detect commercial VPN adapter by description, warn
  the user, but still attempt rungs with explicit interface binding to the
  physical NIC. If the VPN adapter is the *only* operational interface
  (rare but possible on a corporate-VPN-required laptop), report that
  state clearly: "All your network connections go through your VPN. The
  radio is probably not reachable until you enable Local Area Network
  access in your VPN settings."
- **Trace capture:** VPN adapter list, default-route interface, whether
  any non-VPN interface has IPv4.

### 3.8 IPv6 (non-coverage)

- **Affected rungs:** none, by design.
- **Failure mode:** N/A.
- **Note:** FlexRadio community has confirmed (multiple threads through
  2026) that the 6000 series, 8000 series, Maestro, and discovery /
  SmartLink protocols do not support IPv6. The radio listens on IPv4 only.
  This stream's recommendation: do NOT implement IPv6 rungs (no IPv6
  neighbor cache scrape, no IPv6 mDNS, no IPv6 multicast). Document this
  decision so a future contributor doesn't waste time on IPv6 rungs that
  have no target.
- **Re-evaluation trigger:** if Flex announces IPv6 firmware support,
  re-open this section.

### 3.9 APIPA / link-local 169.254.x.x

- **Affected rungs:** R0/R1 (cache won't have a link-local address from a
  prior successful connect), R2 (probes user's normal /24, not 169.254/16).
- **Failure mode:** FlexRadio community confirms 6700, 6600M, and other
  models occasionally fail DHCP at power-on and self-assign in 169.254/16.
  Windows continues to send DHCP DISCOVER every 5 minutes from the laptop
  side, but the *radio* may stay on its APIPA address until the user
  power-cycles it.
- **User-visible result:** "I just turned my radio on and JJFlex can't see
  it. Yesterday it worked."
- **Cascade should:**
  - Add a dedicated APIPA-recovery rung that scans 169.254/16 *only when
    explicitly invoked by the user* (not in the default cascade — too slow,
    too noisy). Surface it as "Look for radio in DHCP-failure mode" in the
    manual-recovery menu.
  - When this rung is invoked, sample sparsely first: probe a random 1024
    addresses in 169.254/16 to see if anything answers, then narrow.
  - In the diagnostic message after a normal cascade fails, hint:
    "If you just powered on the radio and it didn't get an IP from your
    router, try Tools > Recovery > Find Radio in DHCP-Failure Mode."
- **Trace capture:** local interface link-local addresses; whether
  recovery rung was invoked; how many probes sent; results.

### 3.10 Mobile hotspot (laptop tethered to phone, radio on phone hotspot)

- **Affected rungs:** R2 mostly; broadcast rungs may or may not work
  depending on phone OS.
- **Failure mode:** Phone hotspots typically use a small subnet
  (192.168.43/24 on Android, 172.20.10/28 on iOS). iOS in particular uses
  /28 — only 14 usable addresses — and *isolates clients by default*
  (similar to AP isolation). Two devices on iOS hotspot may not be able to
  see each other.
- **User-visible result:** "I'm at field day, I have my radio on my phone
  hotspot, JJFlex can't find it."
- **Cascade should:**
  - Detect small subnet (mask /28 or smaller) and warn: "You're on a small
    network — possibly a phone hotspot. iOS hotspots block device-to-device
    traffic by default. If you're on Android, check Personal Hotspot >
    Allow other devices."
  - Still attempt R2 across the full subnet; for /28 it's 14 hosts so
    instant.
  - Try R0/R1 first since cache may have the IP from last hotspot session.
- **Trace capture:** subnet size, gateway IP, gateway MAC OUI (helps
  identify Android vs iOS hotspot).

### 3.11 NAT-behind-NAT / CGNAT / 100.64.0.0/10

- **Affected rungs:** none for LAN discovery directly. CGNAT is a
  WAN-side problem (affects SmartLink). Mentioned for completeness.
- **Failure mode:** CGNAT (carrier-grade NAT, RFC 6598 100.64/10) prevents
  unsolicited inbound. SmartLink uses outbound from the radio so it works
  through CGNAT, but if the user expects to reach the radio over an
  arbitrary VPN they set up, CGNAT will defeat them.
- **User-visible result:** WAN-side connection failures, not LAN
  discovery failures.
- **Cascade should:** out of scope for this stream. Tag for Stream 8
  (SmartLink edge cases) if such a stream exists.
- **Trace capture:** if WAN IP enumeration is ever added, note CGNAT range
  hits.

### 3.12 Corporate firewalls — outbound 4992 blocked

- **Affected rungs:** R2 (cannot complete TCP handshake — radio is
  unreachable on the listening port).
- **Failure mode:** corporate egress filtering blocks non-standard ports.
  TCP 4992 will fail with timeout (silent) or RST (loud). Some firewalls
  also drop SYN to non-standard ports without responding.
- **User-visible result:** R2 returns "no responses" but the radio is
  there. Other rungs may succeed (mDNS responder is on the radio's local
  network and the corporate firewall is between the laptop's network and
  the radio's network, depending on topology).
- **Cascade should:** distinguish "RST received" (port closed, host alive)
  from "timeout" (host unreachable, possibly firewalled). RST on TCP 4992
  for *all* hosts in the /24 strongly suggests a firewall. Surface:
  "Connections to port 4992 were rejected. A firewall may be blocking
  JJFlex from reaching your radio."
- **Trace capture:** per-host R2 result classified as connected /
  refused / timeout / network-unreachable.

### 3.13 Corporate firewalls — IDS/IPS treats subnet probe as port scan

- **Affected rungs:** R2 (the loud one). Less so R3 (passive).
- **Failure mode:** an IDS may flag rapid SYN to many hosts on one port as
  a horizontal port scan. Some IPS will block the source IP for minutes to
  hours after detection. JJFlex may then lose all network access until the
  IPS unblocks.
- **User-visible result:** "JJFlex broke my internet" — corporate IT
  contacts the user about scanning the network.
- **Cascade should:**
  - On networks where the user has not explicitly opted in, throttle R2
    to ~5 probes/sec instead of saturating.
  - Default to "fast mode" only on networks marked Private in Windows
    network profile (heuristic: home network).
  - On Public network profile, use slow mode and prefer non-probe rungs
    (R0, R1, R3, R4, R5).
  - Surface a one-time consent dialog the *first* time R2 runs on a new
    network: "JJFlex needs to send a small number of network probes to find
    your radio on this network. Some workplace networks treat this as
    suspicious activity. Continue?"
  - Do NOT use SYN-scan-style raw sockets. Use full TCP connect() to be
    socially appropriate (trades stealth for lower IDS-trip risk).
- **Trace capture:** per-network subnet, probe count, probe rate, network
  profile (Public/Private/Domain), consent given (yes/no/never-asked).

### 3.14 DHCP lease changes mid-session — cache stale

- **Affected rungs:** R0, R1.
- **Failure mode:** the cached IP from yesterday is no longer the radio's
  IP today. Common after router reboot, DHCP scope reset, or long absence.
- **User-visible result:** R0/R1 try the cached IP, get no response, fall
  through to R2/etc. Time penalty: the connect timeout (~5-30s depending
  on settings).
- **Cascade should:**
  - R0/R1 timeout aggressively (1-2 seconds — if the cached IP is right,
    a TCP handshake on a healthy LAN takes <100ms; a 1-2s window is plenty
    of headroom for Wi-Fi).
  - On R0/R1 failure, *immediately* fall through. Don't retry the cached
    IP later in the cascade.
  - On a successful discovery via a non-cache rung that finds the radio at
    a *different* IP than cached, update the cache atomically.
  - Keep the last 3-5 historical IPs in the cache (not just the most
    recent), and probe all of them in parallel during R0. If the radio's
    IP rotates between two reserved DHCP ranges (uncommon but possible),
    this catches the rotation.
- **Trace capture:** cached IP at start of attempt, wall-clock age of the
  cache, whether cached IP was reached, whether discovery succeeded at a
  different IP, before/after IP comparison.

### 3.15 Radio rebooted / power-cycled / router rebooted

- **Affected rungs:** R0, R1.
- **Failure mode:** sub-case of §3.14. Radio usually keeps its DHCP-assigned
  IP across radio reboots if the lease is still valid. Router reboots can
  invalidate leases, especially on consumer routers where the DHCP table
  doesn't persist.
- **User-visible result:** as §3.14.
- **Cascade should:** as §3.14. Additionally, when discovery succeeds at
  a *new* IP and the cached IP was reachable in the last N successful
  attempts, log a "DHCP IP changed" event for diagnostic context.
- **Trace capture:** DHCP-IP-change event with old/new pair and elapsed
  time since last successful connect.

### 3.16 Multiple FlexRadio products on the same LAN — disambiguation

- **Affected rungs:** all of them — if the cascade finds *a* Flex but not
  *the user's* Flex.
- **Failure mode:** household with two Flexes (e.g. Don's 6300 plus
  somebody else's 6700). R3 (ARP) returns multiple Flex MACs. R5 (SSDP) /
  R4 (mDNS) responses come back from multiple radios. R2 finds two open
  4992 ports.
- **User-visible result:** worst case — JJFlex connects to the wrong
  radio. Surprise QSO interruption. Bad day.
- **Cascade should:**
  - Every rung must filter by the user's preferred radio identity:
    serial number first, station name second, model third.
  - The cache (R0/R1) stores the serial-IP mapping. The connect is gated
    on serial match: if the IP responds with a different serial, treat as
    cache miss and continue cascade.
  - On a successful discovery of multiple radios where exactly one matches
    the user's serial, connect to that one.
  - On a successful discovery of multiple radios where *none* matches the
    user's serial, surface a chooser dialog: "Found 2 radios but none
    match your saved radio. Choose one (this will update your saved radio)
    or cancel."
  - On a discovery that finds zero matching plus N non-matching radios,
    surface: "Your radio (serial XXXX) was not found, but JJFlex did see
    other Flex radios on this network: [list]. Is your radio powered on
    and on the same network?"
- **Trace capture:** every Flex-looking endpoint discovered with serial,
  model, IP, response time; user-preferred serial; match outcome.

### 3.17 Wi-Fi roaming / band-steering / 2.4GHz vs 5GHz

- **Affected rungs:** R2 (subnet may be the same logically but client
  isolation between bands could be enabled), broadcast rungs in general.
- **Failure mode:** modern APs steer clients between 2.4 and 5GHz radios.
  Both radios usually bridge to the same VLAN, so it's transparent. But
  some "guest mode" or "smart connect" configurations put the bands in
  separate VLANs or enable client-isolation per radio. Result: laptop on
  5GHz cannot see radio on 2.4GHz even if both have 192.168.1/24 IPs.
- **User-visible result:** "It works when I sit in my shack, doesn't work
  from the kitchen" — because the laptop hops APs and ends up on a
  different VLAN.
- **Cascade should:** can't fix this from the client side. Add to the
  diagnostic explainer: "If discovery works in some rooms but not others,
  your Wi-Fi access points may be steering you to a different network than
  your radio. Try connecting your laptop to the same Wi-Fi band the radio
  uses (often 2.4GHz)."
- **Trace capture:** Wi-Fi BSSID at time of discovery (helps user-side
  debugging); link speed (gives band hint — 5GHz typically negotiates
  higher).

### 3.18 AP isolation / client isolation

- **Affected rungs:** all unicast and broadcast — total isolation means
  laptop cannot reach radio via any IP-layer method.
- **Failure mode:** Wi-Fi AP has client isolation enabled, blocking
  station-to-station unicast. Some APs also drop broadcast / multicast
  between clients (others let GTK-encrypted broadcasts through for ARP /
  DHCP). Hotel Wi-Fi, conference Wi-Fi, public-library Wi-Fi commonly
  enable this.
- **User-visible result:** discovery fails entirely. Manual IP entry also
  fails because connect to known IP times out.
- **Cascade should:**
  - Detect: if R2 returns 100% timeouts (not refused) for a /24 *and* the
    laptop is on Wi-Fi *and* the network profile is Public, suspect AP
    isolation.
  - Surface: "This Wi-Fi network appears to block device-to-device
    communication, which is common on hotel and public Wi-Fi. JJFlex
    cannot reach your radio through this network."
  - Suggest user solutions: tether to phone hotspot (with the §3.10 caveat
    for iOS), or use SmartLink if configured.
- **Trace capture:** R2 timeout-to-refused ratio for the /24, network
  profile, gateway reachable (yes/no), DNS reachable (yes/no — helps
  distinguish "no Internet" from "isolated from peers").

### 3.19 IPv4 broadcast suppression (commercial Wi-Fi / managed switches)

- **Affected rungs:** R5 (broadcast-style multicast), R7 (LLMNR multicast),
  legacy Flex broadcast discovery.
- **Failure mode:** managed switches and enterprise APs can suppress
  broadcast traffic to reduce airtime. Some suppress *all* broadcast;
  others rate-limit; others allow ARP/DHCP only.
- **User-visible result:** broadcast rungs silently return zero responses.
- **Cascade should:** treat broadcast rung silence as low-confidence —
  always continue to unicast probe rungs even if a broadcast rung "completes"
  with empty results. Don't report "discovery complete" when only the
  broadcast rungs ran.
- **Trace capture:** per-broadcast-rung response count; per-multicast-rung
  response count; total time spent on broadcast/multicast vs unicast.

### 3.20 Multicast filtering (corporate / hotel networks block mDNS, SSDP)

- **Affected rungs:** R4, R5.
- **Failure mode:** multicast 224.0.0.251 (mDNS) and 239.255.255.250 (SSDP)
  filtered at the AP or first-hop switch. Some networks have IGMP snooping
  misconfigured, dropping all multicast that isn't IGMP-joined.
- **User-visible result:** R4 / R5 return zero. Same as §3.19 from user
  perspective.
- **Cascade should:** as §3.19. Additionally, R4/R5 can detect their own
  partial-success state by sending a self-test multicast (e.g. mDNS query
  for `_services._dns-sd._udp.local`) and seeing if the laptop's own
  responder echoes. If multicast loops back locally but no peers respond,
  remote multicast is filtered.
- **Trace capture:** multicast self-test result, per-multicast-group
  response count.

### 3.21 Windows Public vs Private network profile

- **Affected rungs:** R4, R5, R6, R7 — Windows Defender Firewall blocks
  inbound mDNS, SSDP, NetBIOS, LLMNR by default on Public networks.
- **Failure mode:** if Windows has classified the user's home Wi-Fi as
  Public (a common accident — it asks once and many users click the wrong
  answer or never see the prompt), the firewall silently blocks the
  responses to discovery queries. The query goes out fine; the response
  doesn't arrive at JJFlex because the firewall drops it.
- **User-visible result:** discovery silently underperforms. No error,
  just empty results from the affected rungs.
- **Cascade should:**
  - On startup or first-discovery, query the active network profile via
    `NetworkInformation.GetConnectionProfiles()` (UWP API) or the
    `INetworkListManager` COM interface.
  - If active profile is Public and the user is attempting LAN discovery,
    surface: "Windows has marked this network as Public, which limits
    JJFlex's ability to find your radio. If this is your home network, you
    can mark it Private in Windows Settings > Network & Internet."
  - Do NOT change the profile programmatically — user-owned setting.
  - During discovery, attempt to add temporary Windows Firewall exceptions
    for JJFlex inbound (requires elevation; do not request elevation
    silently — surface the request once, with an explanation).
- **Trace capture:** active network profile (Public/Private/Domain),
  Defender Firewall state per profile, JJFlex firewall rules present
  (yes/no/partial).

### 3.22 First-launch vs returning-user

- **Affected rungs:** R0/R1 don't apply on first launch. Cascade ordering
  may differ.
- **Failure mode:** not a failure per se — a UX consideration. First-launch
  user has no cache; running R0/R1 is wasted time.
- **User-visible result:** if cascade always starts with R0/R1, first-launch
  user waits an extra second before "real" discovery starts.
- **Cascade should:** detect cache empty and skip directly to broadcast +
  ARP rungs in parallel. Speak a different status message: "Searching for
  your radio for the first time..." (vs returning-user "Connecting to
  your radio...").
- **Trace capture:** is-first-launch flag, number of cache entries, cache
  hit (yes/no).

### 3.23 Headless / background / startup-script launch

- **Affected rungs:** R8 (manual IP entry) cannot apply.
- **Failure mode:** JJFlex launched non-interactively (Task Scheduler, Run
  key, post-login script) cannot prompt the user. If all automated rungs
  fail, the cascade has nothing to fall back to.
- **User-visible result:** application starts in a "no radio" state
  silently. Screen-reader user opening the app later finds it disconnected
  with no explanation of what happened.
- **Cascade should:**
  - Detect headless launch by checking command-line args for an explicit
    `--auto-connect` flag, or by checking interactive-session state via
    `Environment.UserInteractive`.
  - In headless mode, run the full automated cascade with extended timeouts
    (network may not be ready at boot). On total failure, log to trace and
    set the status to "Auto-connect failed at startup, please connect
    manually" — surfaceable on the Home screen with diagnostic detail when
    the user opens the app.
  - Optional Sprint 30+ feature: retry headless cascade every N minutes
    until a radio is found, configurable.
- **Trace capture:** launch context (interactive / headless), elapsed
  time from app start to first rung, retry count if applicable.

### 3.24 Slow / high-latency networks (satellite, mobile broadband, weak Wi-Fi)

- **Affected rungs:** all timeouts are tuned for sub-100ms RTT typical of
  LAN. Satellite RTT is 600-800ms; weak Wi-Fi / 5G is 100-300ms.
- **Failure mode:** R2 timeout of 1s misses radios that would respond at
  900ms. Cascade reports "no radio" when the radio is alive and reachable.
- **User-visible result:** the user with a slow link cannot connect. Worse
  on a portable setup where the slow link is the only option.
- **Cascade should:**
  - Adaptive timeout: send a baseline ping to the gateway first to
    estimate RTT, then set per-rung timeout to max(default, gateway_rtt * 5).
  - Per-network learned timeout: persist the last successful discovery
    timing per network (BSSID or gateway MAC). On return to the same
    network, start with the learned timing.
  - User override: a power-user setting "Slow network mode" that
    multiplies all timeouts by 4-8x.
- **Trace capture:** gateway RTT estimate, per-rung effective timeout,
  per-rung actual response times.

### 3.25 Power management — Wi-Fi NIC asleep at app startup

- **Affected rungs:** all rungs run before the NIC fully wakes.
- **Failure mode:** Modern Standby + ARP Offload can leave the laptop in a
  state where the OS thinks the NIC is up but the NIC firmware is just
  resuming. Outbound packets queue or drop for the first 1-3 seconds.
- **User-visible result:** "First time after wake doesn't work, second
  time works."
- **Cascade should:**
  - Wait for `NetworkInterface.OperationalStatus == Up` AND a brief
    settle period (1s) AND a successful gateway ARP resolution before
    starting discovery.
  - If discovery fails entirely on first attempt and was launched within
    5 seconds of a `NetworkAvailabilityChanged` event, retry once
    automatically with a 2-second delay before reporting failure.
- **Trace capture:** time since last `NetworkAvailabilityChanged`, time
  since interface OperStatus came up, gateway ARP success/fail.

### 3.26 Power management — ARP offload returns stale entries

- **Affected rungs:** R3 (ARP scrape) — and indirectly R0/R1 if cache
  trusts a recently-seen IP.
- **Failure mode:** Intel and other NIC drivers' ARP Offload feature
  maintains the laptop's IP-to-MAC mappings during sleep. After wake,
  entries may exist for hosts that have since moved, gone away, or
  changed IP. The radio that was at 192.168.1.50 yesterday may have
  rebooted and be at 192.168.1.55 now, but the ARP table still shows
  the old mapping.
- **User-visible result:** R3 returns "found radio at 192.168.1.50",
  cascade attempts connect, connect times out (because nothing is at .50
  anymore). Then it falls through to slower rungs.
- **Cascade should:** treat R3 hits as *hints*, not authoritative.
  Always probe before trusting. Mark ARP-sourced IPs as "needs
  verification" in the cascade pipeline. Don't update R0 cache from R3
  alone — only from a successful TCP handshake + identity match.
- **Trace capture:** R3 hit count, R3 hits that survived verification,
  R3 hits that failed verification (stale), age of ARP entry where
  available.

### 3.27 Multiple Windows users on same machine — per-user vs per-machine cache

- **Affected rungs:** R0 (own cache).
- **Failure mode:** if the cache is per-user (`%AppData%`), a second user
  on the same machine has no cache and pays the full first-launch cost.
  If the cache is per-machine (`%ProgramData%`), it's shared but may
  conflict if both users have different preferred radios.
- **User-visible result:** depends on architecture decision.
- **Cascade should:** decision is in Stream 1 (cache architecture). For
  this stream's purposes: be aware that R0 hit-rate depends on per-user
  vs per-machine choice. Recommendation: per-user cache (matches existing
  JJFlex AppData pattern, avoids cross-user privacy leakage of "what
  radios this user owns").
- **Trace capture:** cache file path, cache user scope.

### 3.28 Antivirus / endpoint-protection blocks raw sockets

- **Affected rungs:** depends on what AV considers raw — R3 if it intercepts
  IP Helper API, multicast rungs if it intercepts multicast joins, R2 if it
  flags the probe pattern.
- **Failure mode:** Symantec Endpoint Protection, McAfee, CrowdStrike,
  SentinelOne, and others can flag UDP broadcast bursts, port-scan-like
  TCP patterns, or rapid socket creation. Some quarantine the offending
  process. Some just block the socket calls silently with WSA errors.
- **User-visible result:** discovery silently fails. May trigger a
  user-visible AV popup (which the screen reader user might miss).
- **Cascade should:** handle every socket exception — never let one bubble
  up to crash a rung. Aggregate WSA error codes per rung and surface
  patterns: "Some network operations were blocked, possibly by your
  antivirus or endpoint protection software."
- **Trace capture:** WSA error code per failure, per-rung error
  classification, exception count.

### 3.29 IPv4 multicast TTL 1 — multicast bounded to local segment

- **Affected rungs:** R4, R5.
- **Failure mode:** mDNS and SSDP both use TTL 1 by design — multicast
  doesn't cross routers. If the radio is on a different VLAN routed via
  a layer-3 switch (common in larger home setups with managed network gear),
  multicast discovery rungs will not find it. Subnet probe (R2) will, if
  routing is configured; broadcast rungs will not.
- **User-visible result:** "Worked at my apartment, doesn't work at my new
  house with the fancier network."
- **Cascade should:** documented behavior; can't be fixed client-side.
  Diagnostic: if R2 finds the radio on a different /24 than the laptop's
  primary, surface: "Your radio is on a different network from your
  computer — they're connected through a router. JJFlex found it but
  some discovery methods (mDNS, SSDP) only work on the same network."
- **Trace capture:** laptop /24 vs radio /24 comparison; routing-via-gateway
  (yes/no — heuristic from R2 timing).

### 3.30 IGMP snooping misconfigurations

- **Affected rungs:** R4, R5.
- **Failure mode:** managed switches with IGMP snooping enabled but no
  IGMP querier configured will eventually drop all multicast (because
  there are no active group memberships from the snooper's perspective).
  Common in home setups where someone enabled "smart switching" without
  understanding it.
- **User-visible result:** as §3.20 / §3.29.
- **Cascade should:** as §3.20 — always continue to unicast rungs.
- **Trace capture:** as §3.20.

### 3.31 NIC promiscuous mode unavailable / blocked

- **Affected rungs:** none directly — JJFlex should not require promiscuous
  mode. Mentioned to flag: do *not* design rungs that require pcap, raw
  sockets requiring elevation, or NDIS-level features. Stay in
  user-mode-socket territory.
- **Failure mode:** N/A (don't go here).
- **Note:** if a future rung is proposed that requires Npcap or elevated
  raw sockets, reject — too high a friction tax (per the friction-tax
  principle in project memory). Discovery must work for an unprivileged
  installed application.

### 3.32 Network-name change (SSID change) — ambient wrong-network connection

- **Affected rungs:** all of them, but the *result* is misleading.
- **Failure mode:** user's laptop auto-connected to a *different* network
  than expected (neighbor's open Wi-Fi, captive portal, hotel after a
  forced reauth). Discovery runs against the wrong network and finds
  nothing — but the user expected to be on the home network.
- **User-visible result:** "JJFlex says it can't find my radio, but my
  radio is right here!"
- **Cascade should:** surface the SSID and gateway IP in the failure
  diagnostic so the user (or a helping ham on the phone) can verify the
  laptop is on the expected network.
- **Trace capture:** active SSID, gateway IP, gateway MAC, gateway RTT.

### 3.33 IPv4 fragmentation / MTU mismatches

- **Affected rungs:** R2 minimal effect (small SYN), R5 SSDP can fragment
  if response is large.
- **Failure mode:** rare on modern equipment, but possible on satellite
  uplinks or PPPoE links with low MTU. Multicast responses larger than
  ~1400 bytes may fragment, and some networks drop fragments.
- **User-visible result:** broadcast/multicast rungs return *partial*
  responses (some radios discovered, others missed) without indication.
- **Cascade should:** keep all probe payloads under 1400 bytes. For SSDP
  M-SEARCH, that's natural. For mDNS queries, fine. For self-identifying
  payloads JJFlex sends, stay small.
- **Trace capture:** rung-payload-size per rung type.

### 3.34 Loopback interface noise

- **Affected rungs:** R3 if it doesn't filter the loopback.
- **Failure mode:** GetIpNetTable2 may include loopback entries
  (127.0.0.1 mapped to localhost MAC, etc.). Probing 127.0.0.1 for a Flex
  radio is silly but harmless — until JJFlex itself happens to be running
  a service on a port that overlaps and answers the probe.
- **Cascade should:** explicitly skip loopback in interface enumeration.
- **Trace capture:** filtered-interface list with reason.

### 3.35 IPv4 169.254.169.254 cloud metadata service

- **Affected rungs:** R2 if probing link-local /16 in §3.9.
- **Failure mode:** JJFlex probably never runs in a cloud VM, but if it
  does (e.g. a hosted Windows VM for a remote shack), 169.254.169.254 is
  the standard cloud metadata endpoint. Probing it does nothing harmful
  but wastes a probe.
- **Cascade should:** skip 169.254.169.254 in any link-local probe rung.
- **Trace capture:** N/A — too niche to capture per-attempt.

### 3.36 Captive portal interception

- **Affected rungs:** all unicast — captive portal proxies HTTP / HTTPS
  but typically not arbitrary TCP ports. R2 to port 4992 may pass through
  cleanly; or it may be intercepted with a TCP RST.
- **Failure mode:** hotel / coffee-shop captive portal not yet completed.
  All HTTP redirects to portal; non-HTTP TCP may be blocked entirely.
- **User-visible result:** "I'm at a hotel and JJFlex can't find anything."
- **Cascade should:** detect captive portal: HTTP GET to a known endpoint
  (e.g. `http://www.msftncsi.com/ncsi.txt`) and check for redirect. If
  captive, surface: "You're connected to a network that requires login in
  a web browser. Complete the login, then try again."
- **Trace capture:** captive-portal-detected flag (yes/no/unknown).

### 3.37 Network-stack reset / WinSock corruption

- **Affected rungs:** all — sockets fail at creation.
- **Failure mode:** corrupted Winsock catalog returns errors on socket()
  calls. Rare but happens.
- **User-visible result:** every rung fails at socket creation. JJFlex
  may crash if not defensive.
- **Cascade should:** wrap socket creation in try/except; on Winsock
  catalog errors specifically, surface: "Windows networking appears to be
  in a broken state. Try `netsh winsock reset` from an admin command
  prompt and reboot."
- **Trace capture:** WSA startup result, socket creation error rate per
  rung.

### 3.38 Browser/screen-reader focus interaction with manual-entry rung

- **Affected rungs:** R8.
- **Failure mode:** R8 is a UI prompt. If the cascade auto-launches the
  prompt, focus may be stolen from another active dialog (autoConnect
  modal, etc.). Per the dialog-escape rule in project memory, R8 must be
  Escape-closable, fully labeled for screen readers, and predictable in
  focus behavior.
- **User-visible result:** if R8 is broken accessibility-wise, the user
  is stuck — cascade has exhausted all automatic rungs and the manual
  fallback is unusable.
- **Cascade should:** R8 must:
  - Be Escape-closable (per project memory project_dialog_escape_rule).
  - Announce its purpose on open ("Enter your radio's IP address. Press
    Escape to cancel.").
  - Validate input live (announce "valid IP" / "not a valid IP format").
  - Remember last-entered IP across launches as a default.
  - Provide a "Use my last successful IP (192.168.1.50)" suggestion if
    cache exists.
- **Trace capture:** R8 invoked (yes/no), R8 outcome (entered / cancelled
  / timed-out).

### 3.39 Time-of-day / DHCP-renewal timing

- **Affected rungs:** R0, R1.
- **Failure mode:** if the cache is consulted during the radio's DHCP
  renewal window (every ~12 hours typically), the radio may be briefly
  unreachable as the renewal completes.
- **User-visible result:** rare hiccup. Probably indistinguishable from a
  brief Wi-Fi blip.
- **Cascade should:** the §3.14 retry/refresh logic covers this — no
  special handling needed.
- **Trace capture:** none beyond §3.14.

### 3.40 IPv4 limited-broadcast vs subnet-broadcast differences

- **Affected rungs:** any broadcast rung.
- **Failure mode:** Windows routes 255.255.255.255 differently from
  192.168.1.255 (subnet-directed broadcast). On multi-NIC systems,
  255.255.255.255 may go out the metric-winning interface only;
  subnet-directed broadcast is more predictable but requires knowing the
  /24 in advance.
- **Cascade should:** for any rung that sends broadcast, prefer
  subnet-directed broadcast (192.168.1.255) computed from the
  bound-interface's IP and mask. Fall back to 255.255.255.255 only when
  subnet-directed yields no responses.
- **Trace capture:** broadcast type used per rung, response count per
  type.

### 3.41 IPv6 link-local on FlexRadio Maestro / future hardware (forward-looking)

- **Affected rungs:** none today (per §3.8). Re-evaluate if Flex announces
  IPv6.

### 3.42 Bluetooth PAN / phone-to-laptop IP networking

- **Affected rungs:** all.
- **Failure mode:** Bluetooth Personal Area Network adapter shows up as
  another interface. Has its own /24 typically. Same multi-NIC issues as
  §3.1 apply.
- **Cascade should:** included in interface enumeration. Filter by
  adapter description if undesired (Bluetooth NICs are typically named
  `Bluetooth Network Connection`).
- **Trace capture:** as §3.1.

### 3.43 Ham-radio-specific: SO2R or two-radio operating

- **Affected rungs:** all.
- **Failure mode:** SO2R operator (single op, two radios) may have two
  Flexes — JJFlex needs to disambiguate, see §3.16. Plus the user may
  intentionally want JJFlex to control different radios in different app
  instances.
- **Cascade should:** support per-instance radio preference (multi-instance
  JJFlex already exists per project memory — `JJFlexRadio2Trace.txt`).
  Each instance has its own preferred-radio serial; cascade per-instance
  filters as §3.16.
- **Trace capture:** instance ID, instance's preferred serial.

### 3.44 Ham-radio-specific: contest weekend network load

- **Affected rungs:** R2 (timeouts), broadcast rungs (collisions).
- **Failure mode:** during a major contest, the home network may be
  saturated (logging software, audio streaming, video chat with the team).
  Probes time out; the radio is fine, the network is just busy.
- **Cascade should:** adaptive timeout (§3.24) handles this. Additionally,
  if multiple consecutive discovery attempts exhibit high RTT, surface a
  one-time hint: "Your network seems busy. JJFlex is being patient."
- **Trace capture:** RTT trend across recent attempts.

### 3.45 Accessibility-specific: failure-message verbosity

- **Affected rungs:** all — meta concern about how failure is announced.
- **Failure mode:** screen-reader user gets a torrent of "rung X failed,
  rung Y failed..." announcements with no actionable summary. Or the
  opposite: "discovery failed" with no useful detail.
- **User-visible result:** poor accessibility outcome — user can't
  troubleshoot independently.
- **Cascade should:**
  - During an active cascade, announce only at coarse milestones: "Looking
    for your radio" → (if takes >2s) "Checking other discovery methods" →
    (if final) "Found at 192.168.1.50" or "Could not find your radio.
    Press F1 for help."
  - On final failure, provide a structured spoken diagnostic available on
    demand: "Your laptop is on the network 'HomeWiFi'. Your gateway is
    192.168.1.1. JJFlex tried six discovery methods. None found your
    radio. Press 1 for what to try next, press 2 for technical details,
    press Escape to dismiss."
  - The structured diagnostic is the screen-reader analog of the visual
    "advanced details" expander in modern Windows error dialogs. Without
    it, accessibility parity is not achieved.
- **Trace capture:** announcement events with timing and verbosity level.

### 3.46 Accessibility-specific: cannot self-diagnose router

- **Affected rungs:** all — meta concern.
- **Failure mode:** sighted user with discovery failure can walk to the
  router, look at the LED status, plug into a different port, etc. A
  blind ham may have no independent way to check whether the router is
  even powered on, especially if living alone or remote.
- **User-visible result:** discovery failure is more disabling for blind
  users because the recovery actions assume sight.
- **Cascade should:**
  - Provide audible network-state check the user can invoke independently:
    "Your gateway 192.168.1.1 is reachable, but JJFlex still can't find
    your radio." vs "Your gateway 192.168.1.1 is not responding — your
    Wi-Fi may be down or the router may be off."
  - Where possible, give the user *information they can act on* before
    needing to ask a sighted helper. "Your radio was last seen 3 hours
    ago at 192.168.1.50. Right now, nothing answers at that address.
    The radio may be powered off."
  - Avoid suggesting actions the user cannot perform alone unless
    explicitly framed as "if someone is available to help."
- **Trace capture:** gateway-reachable, DNS-reachable, last-known-radio-IP
  reachable.

---

## 4. Cascade architectural recommendations

Synthesizing across the matrix, the cascade design should incorporate:

### 4.1 Interface enumeration is the foundation

- Re-enumerate at the start of every discovery attempt (§3.2).
- Filter by adapter type and description (§3.3 skip-list).
- Detect Tailscale and other commercial VPNs (§3.6, §3.7) — adjust rung
  priority but don't disable rungs.
- Bind discovery sockets explicitly to interface IP, never 0.0.0.0 (§3.6,
  §3.40).
- Run per-interface rungs in parallel — a quad-NIC dev laptop should not
  be 4x slower than a single-NIC laptop.

### 4.2 Timeout strategy

- Cache hits (R0/R1): 1-2 seconds aggressive.
- Active probes (R2/R3): adaptive based on gateway RTT × 5, with floor 1s
  and ceiling 10s (§3.24).
- Multicast rungs (R4/R5): 3 seconds absolute (mDNS/SSDP responders are
  expected to be fast; if they don't answer in 3s they probably can't
  reach us).
- Per-network learned timing: persist post-success timing as the next
  default for that network (§3.24).
- Total cascade ceiling: 30 seconds default, user-configurable up to 5
  minutes (per the stuck-modal-escape design in project memory — escape
  must always work, and 5 minutes is the auto-cancel ceiling).

### 4.3 Parallel vs serial

- R0 + R1 (cache reads) — parallel, both complete in <50ms typically.
- R0/R1 results → R3 (ARP read, also fast) — parallel with cache.
- After ~2 seconds with no result → fan out R2 + R4 + R5 + R7 in
  parallel, per-interface.
- R6 (NetBIOS) and recovery rungs (link-local /16 scan) only on user
  request, not in default cascade.
- R8 (manual) only when all automated rungs exhaust *and* user is
  interactive (§3.23).

### 4.4 Identity verification before trust

- Every rung result is a *candidate* until TCP handshake + identity
  exchange confirms serial number matches user's preferred radio (§3.16).
- ARP-sourced IPs (R3) marked as "needs-verification" stronger than
  cache-sourced IPs (R0) which are stronger than broadcast-discovered
  IPs (R5/R4).
- On any candidate, attempt TCP connect + identity probe in parallel —
  don't wait for all rungs to finish before validating.

### 4.5 Cascade does not hide rung-level failure

- Per the no-silent-keystrokes principle in project memory, the cascade
  must not silently drop rung failures. Each rung's outcome is captured
  in the trace and surfaced in the structured diagnostic (§3.45) when the
  user requests it.
- The cascade may aggregate verbose details into "tried N methods, found
  no radios" for the default announcement, but must never lie about what
  it tried.

### 4.6 Error-message catalog (proposed strings)

These are the user-visible strings the cascade should speak / display per
failure class. They map onto the matrix entries:

| Class | String (proposed) |
|---|---|
| All rungs returned empty | "Your radio was not found on this network. Press F1 for help, or press M to enter the IP address manually." |
| Tailscale detected, broadcast rungs likely suppressed | "Tailscale is active. JJFlex used direct probes to find your radio. If your radio is reachable only through Tailscale, press M to enter its IP." |
| Public network profile blocking discovery | "Windows has marked this network as Public, which limits some discovery methods. If this is your home network, mark it Private in Windows Settings to improve discovery." |
| Gateway unreachable | "Your network gateway is not responding. Check your Wi-Fi connection." |
| Captive portal | "You appear to be on a network that requires browser login. Complete the login, then try again." |
| Multiple radios found, none match preferred | "JJFlex found other radios on this network, but not yours (serial XXXX). Is your radio powered on?" |
| AP isolation suspected | "This Wi-Fi network appears to block device-to-device communication. JJFlex cannot reach your radio through this network." |
| Headless / no UI for manual entry | logged to trace + status: "Auto-connect failed at startup. Open JJFlex to retry." |
| WSA / network stack error | "Windows networking returned an error. Restart Windows and try again." |
| Cached IP no longer valid | (silent — fall through to next rung; eventual success message is the answer) |
| Discovery in progress, slow network | "JJFlex is being patient — your network is slow." (only after 5s) |

The full catalog should live alongside the cascade implementation as a
.resx file (per the centralized-utterances localization-prep memo) so
strings can be tested and translated.

### 4.7 First-launch UX is different

- First-launch (no cache): announce "Searching for your radio for the
  first time" instead of "Connecting." Set user expectation correctly.
  (§3.22)

### 4.8 Headless launch — distinct mode

- Headless detection per §3.23.
- Extended timeouts (network may be initializing at boot).
- On total failure: log + status update, do not block app start.

### 4.9 Per-network state

- Persist per-network (key: SSID + gateway-MAC composite):
  - Best-performing rung from prior successes.
  - Effective timeout from prior successes.
  - Detected pathologies (Public profile, suspected AP isolation, slow
    link, captive portal seen before).
- Use this to prime the next discovery on that network.
- Bound to ~50 entries with LRU eviction to avoid unbounded growth.

---

## 5. Diagnostic capture recommendations

Per project memory (project_trace_persistence_design), every connection
attempt's trace is preserved in the manifest-driven archive. The discovery
cascade is a sub-phase of the connection attempt and should contribute a
structured discovery section to that trace.

### 5.1 Per-attempt header (capture once)

- App version and build number.
- Discovery cascade implementation version (so traces from different
  releases can be diffed).
- Wall-clock timestamp.
- Trigger: app-start / user-initiated / auto-reconnect / etc.
- Launch context: interactive / headless.
- User's preferred radio: serial, model, station name, last-known IP, age
  of last-known IP.
- Cascade configuration: timeouts, enabled rungs, slow-network mode
  on/off, consent flags.

### 5.2 Environment snapshot (capture once at start of cascade)

- Active network interfaces with full detail (name, description, type,
  status, speed, IPv4 unicast addresses + masks, gateway, metric, MAC OUI).
- Filtered-out interfaces with skip reason ("matches vEthernet pattern",
  "loopback", "operational status not Up", etc.).
- Active network profile per interface (Public / Private / Domain).
- Active SSID and BSSID where applicable.
- Gateway IP per interface.
- Tailscale presence (yes / no / unknown), tailnet IP if present.
- Other VPN adapter presence list.
- Windows Defender Firewall state per profile.
- JJFlex's own firewall rules present (yes / no / partial).
- Cache state: number of entries, oldest/newest, size in bytes.
- Per-network learned state for the active network (if any).

### 5.3 Per-rung capture

For each rung attempted:

- Rung identifier and version.
- Start timestamp, end timestamp, elapsed.
- Per-interface attempt records (rung X may run on N interfaces in
  parallel).
- Probe count sent.
- Response count received.
- Per-response: IP, MAC if available, response payload classification
  (looks-like-Flex, doesn't-look-like-Flex, ambiguous), serial extracted
  if available.
- Error classification: success / partial / timeout / refused /
  network-unreachable / WSA-error-code / blocked-by-policy.
- Outcome: contributed-candidates (count) / no-candidates / errored.

### 5.4 Verification phase capture

For each candidate IP that progressed to verification:

- Source rung(s).
- TCP connect timing.
- Identity exchange result (serial received, model, firmware).
- Match decision: matches-preferred / matches-other / no-match /
  identity-exchange-failed.
- Final disposition: connected / rejected / fell-through.

### 5.5 Outcome capture

- Final outcome: connected / failed-no-radio / failed-radio-found-but-not-mine /
  failed-network-error / cancelled-by-user / cancelled-by-timeout.
- Total elapsed.
- User-visible message displayed.
- If failed: which user-action paths were offered (manual entry / retry /
  help link).

### 5.6 Privacy considerations

Per project memory (project_no_silent_phone_home), traces stay local. If
traces are ever transmitted (crash bundle upload to data.jjflexible.radio
receiver), strip:

- SSID and BSSID (wireless network identification).
- Gateway MAC OUI (geo-correlatable).
- Public IP if ever captured.
- Tailscale tailnet device names (private network membership).

Strip-on-upload, not strip-on-capture — the local trace must retain
diagnostic richness for the user's own debugging. Stripping happens at
the bundle-build stage in the crash receiver flow.

### 5.7 Trace structure

Recommend a single JSON object per attempt, appended to a
discovery-specific log (separate from the main trace, joined by
attempt-ID). JSON survives evolution and is grep-able. Compresses well in
LZMA2 (the planned trace persistence backing).

---

## 6. Open questions

These need answers before the cascade ships, but are out of scope for
Stream 5:

1. **Does Flex hardware advertise via mDNS?** Need confirmation. If yes,
   what's the service type (`_smartsdr._tcp.local`? `_flexradio._tcp.local`?
   something else)? Stream 4 (rung implementation) needs this. If no, R4
   is dead weight.

2. **Does Flex hardware advertise via SSDP?** Same question for R5. FlexRadio
   community threads suggest the discovery is a custom UDP broadcast on
   port 4992-ish, not standard SSDP, but a passive listen on
   239.255.255.250 may catch it if the radio also speaks UPnP (some
   network gear does).

3. **What's the autoConnectV2.xml exact path and schema?** Couldn't find
   public documentation in this stream's research. Need to enumerate
   `%LOCALAPPDATA%\FlexRadio Systems\` and similar paths on a machine with
   SmartSDR installed. R1 (cache scrape) implementation depends on this.

4. **What other Flex apps cache IPs we could scrape?** SmartSDR for
   Windows, SmartSDR CAT, DAX, SmartSDR for Mac (irrelevant here),
   3rd-party apps like FlDigi-Flex, N1MM+, WriteLog. The cache scrape
   surface area to consider.

5. **What's the right consent UX for first-time R2 on a new network?**
   §3.13 proposes a consent dialog. Is that overkill for home users? Is
   skipping it reasonable abuse risk for corporate users? Needs UX
   decision separate from this stream.

6. **Should JJFlex add itself to Windows Firewall on install?** §3.21
   notes that Public-profile networks block inbound responses to discovery.
   Adding rules on install (with elevation) would help, but may surprise
   users. Tradeoff to decide separately.

7. **Should the cascade self-test on startup?** A "send a multicast to
   myself, expect echo" loopback test could detect blocked-multicast
   environments before any user-facing failure. Adds startup time. Needs
   weighing.

8. **What's the right cache size and eviction policy?** §3.14 suggests
   keeping last 3-5 IPs per radio. Is more useful? Less? Per-radio LRU?

9. **Per-network state — privacy implication of remembering networks?**
   §4.9 proposes persisting per-SSID state. SSIDs could be sensitive
   (workplace networks, friend's-house networks). Acceptable tradeoff
   given local-only storage? Surface in privacy-relevant docs?

10. **Slow-network mode default on cellular?** §3.24 notes that slow
    networks need extended timeouts. Could JJFlex auto-detect cellular
    via `NetworkInterfaceType.Wwan` and default to slow mode? Yes
    probably — flag for Stream 4 implementation.

11. **Should the recovery rung (link-local /16 scan, §3.9) be exposed in
    the menu by default, or hidden behind a "Show advanced recovery"
    toggle?** Friction-tax says expose it; UI clutter says hide it.

12. **What's the correct behavior when the cascade succeeds but at a
    *different* radio than the user's preferred?** §3.16 proposes a
    chooser dialog. Is this the right friction level? Or should it be
    a notification ("Found Don's other radio. Click here to switch")?

13. **NetBIOS rung (R6) — does Flex hardware respond to NetBIOS name
    queries?** Almost certainly no. Probably dead weight; consider
    dropping from the cascade entirely.

14. **What latency budget for "good UX"?** A returning user with cache
    should connect in under 1 second. A first-launch user under 5
    seconds. A failed cascade should report failure under 15 seconds
    default. Are these the right targets?

---

## Sources

- [Tailscale subnet routers documentation](https://tailscale.com/docs/features/subnet-routers)
- [Tailscale: Using Tailscale SubNet Routing and Flex Radio](https://community.flexradio.com/discussion/8031301/using-tailscale-subnet-routing-and-flex-radio)
- [Tailscale issue #6184 — export devices responding to mDNS for subnet routing](https://github.com/tailscale/tailscale/issues/6184)
- [Tailscale issue #11134 — support for general purpose multicast](https://github.com/tailscale/tailscale/issues/11134)
- [Tailscale MagicDNS docs](https://tailscale.com/docs/features/magicdns)
- [Tailscale client preferences](https://tailscale.com/kb/1072/client-preferences)
- [FlexRadio Community: IPv6 support](https://community.flexradio.com/discussion/7310824/ipv6-support)
- [FlexRadio Community: IPv6 and CGNAT — how do we future-proof remote Flex access](https://community.flexradio.com/discussion/8032151/ipv6-and-cgnat-how-do-we-future-proof-remote-flex-access)
- [FlexRadio Community: 6300 Maestro discovery / subnet limitations](https://community.flexradio.com/discussion/7347714/radios-in-other-subnets-why-cant-smartsdr-for-windows-maestro-be-used-without-auto-discovery)
- [FlexRadio Community: 6600M link-local network issues](https://community.flexradio.com/discussion/8025660/new-6600m-user-network-communications-issues-everything-link-local)
- [FlexRadio Community: DHCP issues](https://community.flexradio.com/discussion/6572279/dhcp-issues)
- [Microsoft Learn: Automatic Metric for IPv4 routes](https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/automatic-metric-for-ipv4-routes)
- [Microsoft Learn: Configure the Order of Network Interfaces](https://learn.microsoft.com/en-us/windows-server/networking/technologies/network-subsystem/net-sub-interface-metric)
- [Microsoft Learn: GetIpNetTable2 function](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-getipnettable2)
- [Microsoft Learn: MIB_IPNET_ROW2 structure](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/ns-netioapi-mib_ipnet_row2)
- [Microsoft Learn: Power management on network adapter](https://learn.microsoft.com/en-us/troubleshoot/windows-client/networking/power-management-on-network-adapter)
- [Microsoft Learn: Wi-Fi power management for modern standby platforms](https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/wi-fi-power-management-for-modern-standby-platforms)
- [Microsoft Learn: Windows 10 firewall and network discovery](https://learn.microsoft.com/en-us/answers/questions/3934748/windows-10-firewall-and-network-discovery)
- [Microsoft Tech Community: mDNS in the Enterprise](https://techcommunity.microsoft.com/t5/networking-blog/mdns-in-the-enterprise/ba-p/3275777)
- [Microsoft Q&A: 24h2 killed network discovery](https://learn.microsoft.com/en-us/answers/questions/2151284/24h2-killed-network-discovery)
- [Microsoft Q&A: WSL2 Hyper-V virtual switch ARP entries](https://learn.microsoft.com/en-us/answers/questions/3954216/2-br0-entries-in-arp-table-but-no-bridge-connectio)
- [microsoft/WSL issue #5835 — specify Hyper-V virtual switch network adapter for wsl2](https://github.com/microsoft/WSL/issues/5835)
- [microsoft/WSL issue #11263 — adding internal HyperV network adapter breaks WSL networking](https://github.com/microsoft/WSL/issues/11263)
- [Cisco Meraki: Wireless Client Isolation](https://documentation.meraki.com/Wireless/Operate_and_Maintain/How_Tos/Firewall_and_Traffic_Shaping/Wireless_Client_Isolation)
- [Mist: Isolation and Filtering](https://www.mist.com/documentation/isolation/)
- [SANS Institute: Wi-Fi Client Isolation — What Security Teams Actually Need to Know](https://www.sans.org/blog/airsnitch-wi-fi-client-isolation-what-security-teams-actually-need-know)
- [Pulse Security: Bypassing WiFi Client Isolation](https://pulsesecurity.co.nz/articles/bypassing-wifi-client-isolation)
- [Ubiquiti: Managing Broadcast Traffic with UniFi](https://help.ui.com/hc/en-us/articles/27384925962647-Managing-Broadcast-Traffic-with-UniFi)
- [Wikipedia: Carrier-grade NAT](https://en.wikipedia.org/wiki/Carrier-grade_NAT)
- [Cloudflare: Detecting CGN to reduce collateral damage](https://blog.cloudflare.com/detecting-cgn-to-reduce-collateral-damage/)
- [OneUptime: How to Fix IPv4 Getting a 169.254.x.x APIPA Address](https://oneuptime.com/blog/post/2026-03-20-fix-apipa-169-254-address/view)
- [IT trip: Fix 169.254.x.x APIPA on Wi-Fi (DHCP not assigning IP)](https://en.ittrip.xyz/windows/troubleshooting/fix-apipa-dhcp-wifi)
- [Nmap: Subverting Intrusion Detection Systems](https://nmap.org/book/subvert-ids.html)
- [Cisco Secure Firewall: Port Scan Detection](https://secure.cisco.com/secure-firewall/docs/port-scan-detection)
- [Fortinet: What Is A Port Scan?](https://www.fortinet.com/resources/cyberglossary/what-is-port-scan)
- [dotnet/runtime issue #83525 — UdpClient inconsistent behaviour between Windows and Linux](https://github.com/dotnet/runtime/issues/83525)
- [Microsoft Learn: UdpClient.Connect Method](https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.udpclient.connect?view=net-8.0)
- [Microsoft Learn: Socket.Bind(EndPoint) Method](https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.socket.bind?view=net-7.0)
- [whizz-tech: Domain vs Private Network Profile — Fix Discovery on Windows](https://whizz-tech.com/support/printers/domain-vs-private-network-profile-breaks-discovery/)
- [MCSI Library: Network Profiles in Windows](https://library.mosse-institute.com/articles/2023/08/public-vs-private-network/public-vs-private-network.html)
- [woshub: Wi-Fi Disconnects After Sleep or Hibernation on Windows](https://woshub.com/internet-or-wifi-disconnected-after-sleep/)
