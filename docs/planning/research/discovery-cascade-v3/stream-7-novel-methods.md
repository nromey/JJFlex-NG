# Stream 7: Novel Discovery Methods

**Stream identifier:** discovery-cascade-v3 / stream-7
**Date:** 2026-05-06
**Author:** Claude (research agent)
**Research question:** Find network discovery methods for "find a known FlexRadio on a LAN" that are NOT already on the cascade list (cached LAN/WAN IP, ARP, GetExtendedTcpTable, DNS resolver cache, third-party config scrape, TCP/4992 subnet probe, mDNS, SSDP, NetBIOS, LLMNR, UDP broadcast, SmartLink, manual entry). Brainstorm broadly, then evaluate feasibility for each. Surprise the team — surface methods we haven't named.

---

## 1. Executive summary

After surveying the LAN-discovery space across IETF RFCs, Microsoft Win32 docs, .NET ecosystem libraries, FlexRadio community forums, and reverse-engineering writeups (AetherSDR, smartsdr-api-docs, miniupnpd, RSSDP, SharpSnmpLib), the methods most worth adding to the cascade — beyond what's already named — are:

- **Hosts file scrape (`%SystemRoot%\System32\drivers\etc\hosts`).** Trivial, instant, no network traffic, no privacy cost, no admin. A user who manually pinned `flex 192.168.1.42` to their hosts file (common cargo-cult fix from FlexRadio forums) is screaming the answer at us. ~30 LOC. Belongs in the cascade.
- **Reverse-DNS PTR sweep of the local /24.** Many home routers/Pi-hole/dnsmasq/UniFi populate PTR records for DHCP leases; a parallel `Dns.GetHostEntryAsync` sweep often reveals hostnames containing "flex" / radio nickname. ~80 LOC, runs in 1-3s on a /24. Privacy: pure outbound DNS, normal traffic.
- **Hostname-pattern probe via DNS / mDNS.** Try `flex.local`, `flexradio.local`, `flex-<serial>.local`, `flex-<callsign>.local`, plus the radio's nickname (which JJF already knows from cache) followed by `.local`, `.lan`, `.home.arpa`. Costs nothing if names don't resolve. Especially powerful when paired with Tailscale MagicDNS / pfSense `flex.localdomain`. ~50 LOC.
- **Router HTTP/UPnP-IGD client list scrape.** UPnP IGD (Internet Gateway Device) on most consumer routers exposes `GetGenericPortMappingEntry` and the connected-clients table. Hit rate is medium (depends on router brand and whether UPnP is enabled), but free if it works. ~150 LOC via `Mono.Nat`. Privacy: outbound to default gateway only.
- **Tailscale / WireGuard peer list scrape.** Growing audience: many remote-shack hams now front-end their station with a mesh VPN. `tailscale status --json` and `wg show all dump` enumerate peer IPs. If the radio is on the tailnet, this is a single-shot answer. ~70 LOC for Tailscale CLI invocation, ~100 LOC if we want the local API socket.
- **WS-Discovery (WSD) on UDP/3702 multicast 239.255.255.250.** Native to Windows since Vista; WSDAPI / Function Discovery exposes a managed-friendly enumeration. FlexRadio almost certainly does NOT advertise via WSD (it's a printer/camera/DPWS protocol), but the cost of *trying* one Probe is ~15ms and ~80 LOC. **Worth including only as an exhaustive defensive measure** — see ruled-out section for the trade-off.
- **Persistent route-change watchdog (`NotifyRouteChange2` / `NotifyUnicastIpAddressChange`).** Not a one-shot rung — a *background* layer that detects "user just plugged into a new network" and re-runs the cascade automatically, killing the worst friction case (user moves laptop and JJF still thinks the radio is on the old subnet). ~120 LOC. Pairs with the cascade rather than competing with it.

Also recommended for *targeted reconnect* but not the cold cascade:

- **TCP keep-alive shadow probe** of the last-known port set (4991, 4992, 4993, 4994, 7000) to a one-shot list of recently-seen IPs from `GetExtendedTcpTable` history.

Methods explicitly ruled out (with rationale below): SNMP public, SLP, LLDP/CDP receive, ICMP broadcast (BSD-style amplification, often filtered), DHCP snooping (requires admin/raw socket), Bluetooth/Wi-Fi-Direct, IPv6 RA (radio doesn't advertise itself this way), Matter/Thread, HomePlug, Wireshark .pcap scraping, USB enumeration, Apple Sleep Proxy, `_workstation._tcp` / generic mDNS service-type fishing, BJNP, PnP-X (which collapses to WSD+SSDP under the hood).

---

## 2. Method-by-method findings

### 2.1 Hosts file scrape (NOVEL — strongly recommended)

**Description.** Read `%SystemRoot%\System32\drivers\etc\hosts` (always world-readable, no admin required), parse non-comment lines as `<IP> <name1> [<name2> ...]`, and search for entries whose name contains `flex`, the user's known radio nickname (which JJF already caches), or the user's callsign. The hosts file is a cargo-cult fix path FlexRadio users frequently take when their router refuses to play nice with discovery — they pin the radio's IP under a friendly name. It's also where corporate/lab users put deterministic shack mappings.

**Feasibility on Windows 10/11 from .NET 10.** Trivial. `File.ReadAllLines(Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\drivers\etc\hosts"))`, parse, done. Always world-readable; only writes need elevation.

**Expected hit rate for FlexRadio.** Low-to-medium (5-15% of installs guess) but extraordinarily high signal when present — when it hits, it's an exact match, not a probabilistic guess.

**LOC cost.** ~30 LOC including parser, comment handling, and matching heuristic.

**Privacy / friction concerns.** Zero. No network traffic. No user opt-in needed. Reads a system file that every Windows process can read.

**Open question for empirical testing.** What pattern does the user write? `flex`, `flexradio`, `<nickname>`, `<callsign>-flex`, `shack`? We should empirically gather a sample of 6-10 user hosts files (Don, Justin, etc.) to tune the regex. Conservative default: case-insensitive substring match for `flex` OR an exact-match against the cached radio nickname.

---

### 2.2 Reverse-DNS PTR sweep of local /24 (NOVEL — strongly recommended)

**Description.** Enumerate the local IPv4 subnet (e.g. 192.168.1.0/24) by issuing parallel reverse-DNS lookups (`Dns.GetHostEntryAsync(ipAddress)`) against each `.0/.1...255` host. Many home routers (Asus, UniFi, pfSense, OPNsense, OpenWrt with dnsmasq, Pi-hole) auto-populate PTR records from DHCP leases, so `192.168.1.42` reverse-resolves to a hostname that often contains "flex", "smartsdr", "shack-radio", or the radio's nickname. This is faster than a full TCP probe sweep and reveals MORE radios than ARP table reads, because it picks up devices that haven't actively talked to the local machine.

**Feasibility on Windows 10/11 from .NET 10.** Native. Uses `System.Net.Dns.GetHostEntryAsync` or [DnsClient.NET](https://dnsclient.michaco.net/) for higher throughput. Concurrent across 256 IPs takes 1-3 seconds with reasonable parallelism limits.

**Expected hit rate for FlexRadio.** Medium. Hit rate depends heavily on router/DHCP-server behavior:
- UniFi: high (PTR auto-populated).
- pfSense/OPNsense with `Register DHCP leases in DNS resolver`: high.
- ASUS-WRT: medium.
- ISP-provided combo modems: low.
Even a single hit is decisive — it tells us not just the IP, but corroborates the radio identity by name.

**LOC cost.** ~80 LOC including subnet enumeration, parallel async DNS, pattern-match scoring.

**Privacy / friction concerns.** Outbound DNS queries to the user's configured DNS server. Looks identical to normal browsing traffic. No leaks.

**Open question for empirical testing.** What's the average response time for a /24 reverse sweep on Don's home network? On a corporate /16 (do we even attempt larger subnets, or hard-cap at /24)? What's the timeout-per-IP that minimizes false-misses without slowing the cascade?

---

### 2.3 Hostname-pattern probe (NOVEL — strongly recommended)

**Description.** Without any probing, simply *try resolving* a small list of plausible hostnames via the OS resolver (which transparently chains DNS → mDNS → LLMNR → NetBIOS):

- `flex.local`, `flexradio.local`, `smartsdr.local`
- `flex-<serial>.local` (we know the serial from cache)
- `flex-<nickname>.local` (we know the nickname from cache)
- `<nickname>.local`, `<nickname>.lan`, `<nickname>.home.arpa`, `<nickname>` (no suffix)
- `<callsign>-flex.local`
- DNS suffix search for the user's domain: `flex.<corp.tld>`

If any of these resolve, we have an IP without needing to broadcast.

**Feasibility on Windows 10/11 from .NET 10.** `Dns.GetHostAddressesAsync(name)` does the right thing automatically — Windows chains through DNS, mDNS, LLMNR, and NetBIOS in priority order. No special code needed for protocol selection.

**Expected hit rate for FlexRadio.** Unknown; needs empirical data. Likely low for the literal strings, but **high once we add the cached nickname** — many users name their radio with a memorable string (`my6500`, `shack-flex`, `<callsign>`) and then rely on `.local` mDNS resolution to reach it. Pairs especially well with Tailscale MagicDNS deployments.

**LOC cost.** ~50 LOC for the candidate-name builder + parallel `GetHostAddressesAsync`.

**Privacy / friction concerns.** None. mDNS lookups are link-local by definition; DNS lookups go to the user's normal resolver.

**Open question for empirical testing.** Does FlexRadio firmware actually advertise itself via mDNS? The community evidence is silent on this — no `_flex._tcp` advertising appears documented. If FlexRadio firmware doesn't advertise, the `.local` paths won't work directly but may still work via dnsmasq/Pi-hole reflection. Need to capture actual mDNS traffic from a real radio (Don, Justin) to confirm.

---

### 2.4 Router UPnP-IGD client / port-mapping scrape (NOVEL — recommended, medium hit rate)

**Description.** Most consumer routers expose UPnP IGD (Internet Gateway Device) on the LAN. Beyond just opening ports, the IGD interface supports:

- `GetExternalIPAddress` — confirms we're behind NAT and gives us our public IP (cheaply usable for SmartLink fallback even without auth).
- `GetGenericPortMappingEntry(index)` iterated until empty — enumerates all current port mappings, which sometimes includes mappings the radio (or SmartSDR) auto-created on user's behalf for SmartLink. The `InternalClient` field is the LAN IP of the device that requested the mapping.
- A few routers also expose `LANHostConfigManagement` / `LayerThreeForwarding` / `WANIPConnection` services that leak DHCP client tables.

**Feasibility on Windows 10/11 from .NET 10.** Use [Mono.Nat](https://github.com/alanmcgovern/Mono.Nat) (MIT-licensed, .NET-Standard-compatible) which already speaks UPnP IGD v1+v2 and NAT-PMP. Discovery via SSDP M-SEARCH for `urn:schemas-upnp-org:device:InternetGatewayDevice:1`. The control URL exposes SOAP endpoints we can hit.

**Expected hit rate for FlexRadio.** Medium. UPnP enabled on ~60% of consumer routers (default-on for most; default-off for security-aware sysadmins). Of those, port mapping enumeration is universal; client-table enumeration is less standardized. Even just `GetExternalIPAddress` is useful as a "behind a NAT?" sanity check that informs other rungs.

**LOC cost.** ~150 LOC if we use Mono.Nat (mostly glue); ~500 LOC if we hand-roll the SSDP+SOAP. Recommend Mono.Nat.

**Privacy / friction concerns.** Outbound traffic to default gateway only. No remote leaks. No admin required. Bonus: if the radio is using UPnP for SmartLink, we may *learn information about SmartLink port mappings* the user didn't know they had — useful as a diagnostic tab future-feature.

**Open question for empirical testing.** Does FlexRadio firmware automatically request UPnP port mappings when SmartLink is enabled? Need to inspect a router's port-mapping table after pairing a radio with SmartLink. If yes, the radio's LAN IP is in `InternalClient`.

---

### 2.5 Tailscale / WireGuard peer list scrape (NOVEL — recommended, growing audience)

**Description.** Hams running remote shacks increasingly use mesh VPNs to front-end their stations. Both Tailscale and WireGuard expose local peer lists:

- **Tailscale:** `C:\Program Files\Tailscale\tailscale.exe status --json` returns a JSON document with every peer's tailnet IP, hostname, OS, online status. Filter for hostnames containing "flex" or matching the cached nickname.
- **WireGuard:** `wireguard.exe /uapi <iface>` (or read `C:\Program Files\WireGuard\Data\Configurations\*.conf.dpapi`) lists peer public keys + allowed-IPs. Less hostname-rich than Tailscale but sufficient if the user named their radio's interface clearly.

If the radio is exposed through a tailnet (which is increasingly common with SDRs running on tailnet-joined Linux gateways or directly tailscale'd routers), this is one shell-out away from a definitive answer.

**Feasibility on Windows 10/11 from .NET 10.** `System.Diagnostics.Process` shell-out. No external NuGet needed. Works whether or not the user is logged into Tailscale (the binary returns gracefully if not). If the binary doesn't exist, we skip the rung in microseconds.

**Expected hit rate for FlexRadio.** Currently low (probably 2-5% of installs), but trending up. **Strategically important** because the users adopting Tailscale/WireGuard are precisely the remote-operation crowd who most need reliable discovery — they're our long-term audience. Including this rung now signals support for their workflow.

**LOC cost.** ~70 LOC for Tailscale (parse JSON), ~100 LOC for WireGuard (parse INI-ish files via DPAPI decrypt or shell-out).

**Privacy / friction concerns.** Reads Tailscale's local status (no internet traffic). Skips silently if the binary doesn't exist. If the user's tailnet is provisioned with sensitive peer names, we read that into memory but never transmit it (consistent with no-silent-phone-home).

**Open question for empirical testing.** When a Flex is on a tailnet behind a subnet router, does Tailscale's MagicDNS expose it as `flex-foo.tail<scale-stable-id>.ts.net`? If yes, the hostname-pattern probe (2.3) actually catches it for free without needing this rung.

---

### 2.6 WS-Discovery / WSDAPI / Function Discovery (NOVEL — defensive, low hit rate)

**Description.** WS-Discovery is the W3C/OASIS multicast discovery protocol that Windows uses natively to find network printers, scanners, IP cameras, and any DPWS-compliant device. Multicast UDP/3702 to `239.255.255.250` (IPv4) or `ff02::c` (IPv6); send a Probe; get back ProbeMatch responses. .NET exposes this via `System.ServiceModel.Discovery.DiscoveryClient` (WCF, full-framework only), or via the underlying `Function Discovery` COM interfaces in WSDAPI for .NET 10.

**Feasibility on Windows 10/11 from .NET 10.** `System.ServiceModel.Discovery` is **NOT available in .NET 10** (it was Full-Framework only). To use WS-Discovery on .NET 10 we'd either P/Invoke into WSDAPI (`IWSDiscoveryProvider`) or hand-roll the SOAP-over-UDP/3702 messages directly. The latter is ~200 LOC; the former is COM-interop pain.

**Expected hit rate for FlexRadio.** Near-zero. FlexRadio firmware almost certainly does not implement DPWS or advertise via WS-Discovery — there's no community evidence of it, and FlexRadio uses its own VITA-49 broadcast on UDP/4992 instead. The only scenario where WSD helps: a third-party gateway/proxy is bridging the radio onto WSD (highly speculative).

**LOC cost.** ~200 LOC hand-rolled, or ~150 LOC via Function Discovery COM interop.

**Privacy / friction concerns.** Multicast on local subnet only. No external traffic. Same noise floor as SSDP.

**Recommendation:** **Marginal — defer.** The cost is non-trivial and the expected hit rate is near-zero. Include only if the team wants to claim "every standard Windows discovery mechanism has been tried" for compliance/marketing. Otherwise spend the LOC on a stronger rung.

**Open question.** Has anyone empirically packet-captured a Flex 6000/8000 and seen ANY traffic on UDP/3702? If no captures exist, we can confidently rule it out.

---

### 2.7 Persistent network-change watchdog (NOVEL — strongly recommended, layer not rung)

**Description.** Not a discovery rung — a *background subscription* that fires when the LAN environment changes. Use one of:

- `NotifyUnicastIpAddressChange` (netioapi.h) — fires when the local machine's IP changes (laptop joined a new Wi-Fi).
- `NotifyRouteChange2` — fires when a routing-table entry changes (default gateway flipped).
- `NotifyIpInterfaceChange` — fires when an interface goes up/down.
- Managed equivalent: `NetworkChange.NetworkAddressChanged` event.

When the watchdog fires, JJF re-runs the discovery cascade automatically and updates the connection UI. This solves the worst real-world friction: user takes laptop from home shack to portable site, JJF still tries to reach the home IP, user has no idea why "JJF can't find the radio."

**Feasibility on Windows 10/11 from .NET 10.** `System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged` is the easy managed path (fires asynchronously on any IP change). For finer-grained route changes, P/Invoke `NotifyRouteChange2`.

**Expected hit rate for FlexRadio.** N/A — this is a force-multiplier on every other rung. Estimated to eliminate ~30-50% of "JJF doesn't see my radio" support requests by triggering automatic re-discovery.

**LOC cost.** ~120 LOC including event registration, debounce, and cascade re-trigger.

**Privacy / friction concerns.** None. No network traffic. Pure local OS event subscription.

**Open question.** What's the right debounce? Network-change events can fire 3-8 times in rapid succession during a Wi-Fi reconnect; we don't want to launch 8 cascade runs. Probably 2-3 second debounce + cancellation of in-flight cascade.

---

### 2.8 Targeted reconnect probe of recent peer-IP history (NOVEL — useful adjacent rung)

**Description.** Augment the existing "cached LAN IP" rung with a *short list* (~5 most recent) of every IP address we've ever seen the radio at, drawn from JJF's own connection log. On each cascade run, fire one TCP-SYN per remembered IP × per known port (4991, 4992, 4993, 4994, 7000) in parallel. First success wins.

This is essentially "cached LAN IP" but with N=5 instead of N=1, and probing all known protocol ports instead of just 4992. The marginal cost is ~10 simultaneous SYNs returning in <100ms.

**Feasibility on Windows 10/11 from .NET 10.** Trivial. `TcpClient.ConnectAsync(ip, port)` × N with `Task.WhenAny` to take the first win.

**Expected hit rate for FlexRadio.** High for users who have multiple network environments (home + portable + remote shack) or who occasionally swap routers.

**LOC cost.** ~60 LOC including history persistence and parallel probe.

**Privacy / friction concerns.** Probes only IPs we've previously connected to. No new endpoints.

**Open question.** How many historical IPs to retain? 5? 20? Infinite with LRU eviction?

---

### 2.9 Reading other Flex-aware app caches (PARTIAL OVERLAP with named "third-party config scrape" — extending it)

**Description.** The named rung lists "SmartSDR, N1MM, WSJT-X" — extend the list significantly. Specific paths and what's there:

- **SmartSDR:** `%APPDATA%\FlexRadio Systems\SSDR.settings` (XML/INI). Contains `STATION` name, recent radios, fixed-IP overrides.
- **SmartSDR CAT:** `%APPDATA%\FlexRadio Systems\CAT.settings`.
- **SmartSDR DAX:** `%APPDATA%\FlexRadio Systems\DAX.settings`.
- **WSJT-X:** `%LOCALAPPDATA%\WSJT-X\WSJT-X.ini` (and `WSJT-X - <ConfigName>` variants for multi-radio). Look for `Configuration\Hamlib\Network` or radio-host fields.
- **N1MM:** `Documents\N1MM Logger+\DigitalDV.ini`, `N1MMLogger.set` — contains rig-control hostnames.
- **DDUtil / Win4K3Suite:** registry under `HKCU\Software\K6JCA` or similar.
- **fldigi:** `%FLDIGI_HOME%\fldigi.prefs` — `RIGCAT/HamlibSettings/...`.
- **JTAlertX:** reads WSJT-X ini, but has its own log of "last seen radio addresses".
- **Log4OM, DXLog, RUMlog, MixW:** all keep CAT/network configs in local files.
- **AetherSDR:** brand new (2026); on Windows it stores config under `%APPDATA%\AetherSDR\` (TBD path; check release zip).
- **FlexLib loaded by ANY other process:** if SmartSDR or Maestro Companion is currently running, querying *its* TCP table for connections to port 4992 reveals the radio IP directly (this is the synergy with `GetExtendedTcpTable` already on the cascade list).

**Feasibility on Windows 10/11 from .NET 10.** All file reads. ~3-5 minutes per app to write a parser.

**Expected hit rate for FlexRadio.** Cumulative across apps: high. Most ham operators have at least 2-3 of these installed.

**LOC cost.** ~30-50 LOC per app × 8-10 apps = ~300-500 LOC total. Cleanly factored as one provider per app. Highly parallel implementation work.

**Privacy / friction concerns.** All world-readable files in user's own profile. No leaks.

**Open question.** Which apps are highest-yield first to implement? Probably SmartSDR, WSJT-X, N1MM, fldigi cover ~85% of the user base. The rest is long-tail.

---

### 2.10 SNMP public-community sweep (NOVEL — RULED OUT, see section 5)

Ruled out — see section 5.

---

### 2.11 SLP / Service Location Protocol (NOVEL — RULED OUT, see section 5)

Ruled out — see section 5.

---

### 2.12 LLDP / CDP receive (NOVEL — RULED OUT, see section 5)

Ruled out — see section 5.

---

### 2.13 ICMP broadcast / subnet-mask request (NOVEL — RULED OUT, see section 5)

Ruled out — see section 5.

---

### 2.14 Passive DHCP snooping (NOVEL — RULED OUT, see section 5)

Ruled out — see section 5.

---

### 2.15 NDP / IPv6 Router-Advertisement listening (NOVEL — RULED OUT, see section 5)

Ruled out — see section 5.

---

### 2.16 Bluetooth-LE / Wi-Fi-Direct service browse (NOVEL — RULED OUT, see section 5)

Ruled out — see section 5.

---

### 2.17 USB-history / Device Manager scrape (NOVEL — RULED OUT, see section 5)

Ruled out — see section 5.

---

### 2.18 Wireshark .pcap file scraping (NOVEL — RULED OUT, see section 5)

Ruled out — see section 5.

---

### 2.19 FlexRadio's own SmartLink server "list my radios" without active SmartLink (NOVEL — recommended forward-looking)

**Description.** Even when SmartLink isn't actively in use, FlexRadio's cloud Auth0+coordinator infrastructure has a record of every radio a user has ever associated with their account. The `SmartLink-API-Reference.pdf` (April 2026 draft) documents the persistent SSL connection to `smartlink.flexradio.com`; it includes a radio-discovery message that returns `{nickname, public_ip, model, serial}` for every registered radio. Critically, the *public_ip* is the radio's last-known external IP — which means even without a SmartLink tunnel, JJF can:

1. Query the SmartLink server with the user's stored Auth0 token.
2. Get the public IP of the radio.
3. Try a direct TCP connection to that public IP on port 4992.
4. If the radio is on the same NAT (user is at home with the radio), the public IP rolls back to the LAN IP automatically and JJF connects.
5. If the user is remote, fall back to SmartLink tunnel as today.

This is "use SmartLink as a metadata provider, not as a connection broker."

**Feasibility on Windows 10/11 from .NET 10.** Already feasible — JJF has SmartLink Auth code (`AuthFormWebView2.cs`). Add a `--metadata-only` mode that doesn't establish the relay, just retrieves the radio list.

**Expected hit rate for FlexRadio.** High for SmartLink-enrolled users (which is most modern users). Even higher in NAT-loopback scenarios where the radio is on the same network as the client but JJF couldn't otherwise discover it (e.g. radio on a separate VLAN with no broadcast bleed-through).

**LOC cost.** ~150 LOC if we already have Auth code; ~400 LOC if we need to factor SmartLink auth out of `AuthFormWebView2.cs` for headless use.

**Privacy / friction concerns.** Requires the user to have logged into SmartLink at least once. NOT a "silent phone-home" because the user explicitly enrolled. Still: this rung should respect the user's SmartLink enable/disable preference.

**Open question.** Does the SmartLink coordinator return radios that are currently OFFLINE? If yes, even-better — the IP is correct, we just need the radio to have power. If no, it only helps when the radio is online (which is fine, that's the only time we'd want to connect anyway).

---

### 2.20 Radio's own NTP server (FLEX-8000 series, NOVEL — niche)

**Description.** FLEX-8000 series radios CAN act as NTP servers when configured with GPSDO. NTP discovery via `ntpq -p` is a thing in the Linux world; on Windows we can issue an NTP request to a candidate IP and check the response's `refid` field for a Flex-distinctive value. Combined with the subnet probe, a single NTP packet to each IP that responds tells us "is this thing a Flex?" without authenticating.

**Feasibility on Windows 10/11 from .NET 10.** ~80 LOC to send NTP queries; off-the-shelf NTP libraries exist (e.g. NodaTime + minor extension).

**Expected hit rate for FlexRadio.** Niche — only FLEX-8000-series with GPSDO configured as NTP. Probably <5% of users. Skip for now.

**LOC cost.** ~80 LOC.

**Privacy / friction concerns.** None — NTP is benign.

**Recommendation:** **Skip.** Hit rate too low to justify; only useful as a confirmation after another rung has located the IP.

---

### 2.21 ARP gratuitous broadcast — passive listening (PARTIAL NOVEL)

**Description.** Beyond reading the ARP table (already on the list), *passively listen* for gratuitous ARP announcements. Every device that boots or roams advertises its MAC↔IP mapping unsolicited. Listening for these requires raw socket access, which on Windows requires admin elevation (or Npcap/WinPcap for unprivileged capture).

**Feasibility on Windows 10/11 from .NET 10.** Requires Npcap (`SharpPcap` NuGet wrapper) + admin or service install. JJF currently runs unprivileged.

**Expected hit rate for FlexRadio.** Medium when the radio just booted; near-zero in steady state.

**LOC cost.** ~150 LOC + Npcap dependency (heavy).

**Privacy / friction concerns.** Requires installing Npcap (user-visible third-party install). Heavy weight for marginal benefit.

**Recommendation:** **Skip for cascade.** The active `GetIpNetTable2` ARP-table read already on the list captures the same data without needing raw sockets. Only revisit if we ever ship a packet-capture diagnostic feature for other reasons.

---

### 2.22 Generic mDNS service-type fishing (`_workstation._tcp`, `_http._tcp`, etc.) (PARTIAL NOVEL)

**Description.** Beyond querying for `_flex._tcp` (whose existence is unconfirmed), broadly query mDNS for common service types and inspect any returned hostnames for "flex" patterns. Common service types: `_workstation._tcp`, `_smb._tcp`, `_http._tcp`, `_ssh._tcp`, `_ipp._tcp`, `_device-info._tcp`. The meta-query `_services._dns-sd._udp.local` enumerates every service type advertised on the network.

**Feasibility on Windows 10/11 from .NET 10.** `Makaretu.Dns.Multicast` (MIT, .NET-Standard) implements mDNS browsing. Send `_services._dns-sd._udp.local` PTR query, get back all advertised types, then resolve each.

**Expected hit rate for FlexRadio.** Unknown. If the radio doesn't advertise mDNS at all (need empirical confirmation), this is zero. If it advertises ANYTHING (e.g. a webserver on `_http._tcp`), we'd find it via the meta-query.

**LOC cost.** ~120 LOC (the mDNS rung that's already on the list largely overlaps; this is a "go broader than `_flex._tcp`" extension).

**Privacy / friction concerns.** Link-local multicast only.

**Recommendation:** **Fold into the existing mDNS rung as a configuration setting** — when the targeted `_flex._tcp` query returns no match, fall back to the meta-query. ~30 LOC additional on top of the already-named mDNS rung.

---

### 2.23 PnP-X / Windows.Devices.Enumeration.Pnp (NOVEL — collapses to existing protocols)

**Description.** PnP-X is Microsoft's umbrella for "make network devices appear as PnP devices in Device Manager." Its discovery mechanisms are *literally* SSDP and WS-Discovery under the hood. `Windows.Devices.Enumeration.DeviceInformation.FindAllAsync()` enumerates discoverable devices.

**Feasibility on Windows 10/11 from .NET 10.** WinRT API; usable from .NET 10 with the Windows SDK projection (`Microsoft.Windows.SDK.NET`).

**Expected hit rate for FlexRadio.** Near-zero; FlexRadio doesn't register as a PnP device.

**LOC cost.** ~60 LOC.

**Recommendation:** **Skip.** PnP-X's substrate (SSDP + WS-Discovery) is already evaluated separately. Including PnP-X separately would duplicate.

---

## 3. Recommendations for cascade rung inclusion

Prioritizing by hit-rate × ease-of-implementation × strategic value, the recommended additions to the cascade (beyond the already-named methods):

### Tier 1 — must include (high ROI):

1. **Hosts file scrape** — 30 LOC, decisive when hits, zero downside.
2. **Reverse-DNS PTR sweep of /24** — 80 LOC, broad coverage, fast.
3. **Hostname-pattern probe (DNS+mDNS chained via OS resolver)** — 50 LOC, free when miss, decisive when hit.
4. **Targeted reconnect probe of recent peer-IP history** — 60 LOC, augments the "cached LAN IP" rung that's already on the list.
5. **Network-change watchdog (background layer, not a rung)** — 120 LOC, eliminates the "moved laptop, JJF didn't notice" friction class entirely. This is the highest-impact addition by user-friction reduction.

### Tier 2 — include if budget allows:

6. **UPnP-IGD client+port-mapping scrape** — 150 LOC via Mono.Nat. Medium hit rate, free `GetExternalIPAddress` side benefit.
7. **Tailscale / WireGuard peer scrape** — 70-100 LOC. Currently low-hit but strategically aligns with remote-shack audience.
8. **Extended third-party app cache scrape** — 30-50 LOC per app × 8 apps. Implement as one provider per app, parallelize the work.
9. **SmartLink-as-metadata-provider** — 150-400 LOC. High hit rate for SmartLink users, respects user opt-in.

### Tier 3 — defensive / completeness:

10. **WS-Discovery / Function Discovery probe** — 150-200 LOC. Near-zero expected hit, but cheap defense if we want to claim exhaustiveness.
11. **Generic mDNS fall-back to `_services._dns-sd._udp.local`** — 30 LOC additional on top of the existing mDNS rung.

---

## 4. Methods explicitly ruled out (so future-team doesn't re-litigate)

### SNMP public-community sweep
**Rationale.** FlexRadio firmware does not run an SNMP agent. Even if it did, sweeping a /24 with SNMP requests is noisy, often triggers IDS/firewall alerts on corporate networks, and `public` community is a security smell in 2026. SharpSnmpLib works fine, but the underlying device support is missing. 200+ LOC for ~0% hit rate.

### SLP (Service Location Protocol, RFC 2608)
**Rationale.** SLP is essentially dead in the consumer/prosumer networking world. It was a pre-mDNS attempt that lost. No FlexRadio firmware advertises via SLP. OpenSLP is the only real implementation and it's lightly maintained. Same problem as SNMP: solution looking for a problem.

### LLDP / CDP receive
**Rationale.** LLDP and CDP are switch-to-switch protocols. End hosts (and certainly hams' workstations) rarely receive them — managed switches don't relay them upward. Even if they did, FlexRadio firmware almost certainly doesn't transmit LLDP. Requires Npcap + admin to listen. ~250 LOC for ~0% hit rate.

### ICMP broadcast (255.255.255.255 ping)
**Rationale.** RFC 1812 deprecates BSD-style broadcast ping responses. Modern Linux kernels (which FlexRadio firmware almost certainly is) default to NOT responding to broadcast pings. Windows itself never sent them by default. Even if Flex *did* respond, modern routers filter directed-broadcast at the gateway. Useless in 2026.

### ICMP Subnet Mask Request (ICMP type 17/18)
**Rationale.** Deprecated by RFC 6633 (2012). Windows still supports sending but no modern OS responds. Pure dead protocol.

### Passive DHCP snooping
**Rationale.** Listening for DHCP traffic from another machine requires raw socket access (admin) or Npcap. Even with that, you only see DHCP renewal traffic that happens to occur during the listen window — which for a long-lease device like a static-DHCP'd Flex is approximately never. Wrong tool.

### IPv6 NDP / Router Advertisement listening
**Rationale.** FlexRadio's network stack is IPv4-only as of firmware 4.x (no v6 documented). Until Flex publishes IPv6 support, NDP discovery has nothing to find. Revisit if/when v6 lands.

### IPv6 Neighbor Solicitation
**Rationale.** Same root cause — Flex doesn't speak IPv6.

### Bluetooth-LE / Wi-Fi-Direct service browse
**Rationale.** FLEX-6000/8000 series have no Bluetooth radio. Wi-Fi Direct requires both peers to support it; Flex doesn't. ~0% hit rate.

### Matter / Thread
**Rationale.** Smart-home protocols. FlexRadio has shown no signs of adopting them. Speculative-future, not present-day rung.

### HomePlug / Powerline networking
**Rationale.** A transport, not a discovery protocol. If a Flex is on powerline, normal IP discovery still works.

### Apple Bonjour Sleep Proxy
**Rationale.** Requires an Apple device acting as the proxy, AND for the Flex to have registered mDNS records with it. Compound dependency, near-zero hit rate.

### USB enumeration / Device Manager history
**Rationale.** FLEX-6000/8000 use Ethernet only (no USB-tethering for connection). Flex 1500/3000/5000 had USB but those are out of scope (JJF dropped legacy radio support in Phase 0). ~0% hit.

### Wireshark .pcap file scraping
**Rationale.** Reading pcap files in user's home is a privacy minefield (those captures may contain anything: passwords, private chats, medical data) and the cost of a pcap parser (SharpPcap or hand-rolled) plus the hit rate (only users who do their own packet captures, ~1% maybe) makes it a clear no.

### BJNP (Brother / Canon printer protocol)
**Rationale.** Vendor-specific to Brother/Canon. FlexRadio doesn't use it. ~0% hit.

### PnP-X as a separate rung
**Rationale.** Collapses to SSDP + WS-Discovery, already evaluated. Adding it separately would double-count.

### `GetIfTable2`, `GetIpForwardTable2`, etc. — interface stats
**Rationale.** These return stats about *our own* interfaces, not about peers. Useful for diagnostics, useless for finding a remote radio.

### WMI `Win32_TcpConnection` join with `Win32_Process`
**Rationale.** Functionally equivalent to `GetExtendedTcpTable` + `Process.GetProcessById`, which is already on the cascade. WMI just adds latency.

### Windows Mobile Hotspot scenario
**Rationale.** If radio is connected via the laptop's hotspot, the laptop is the DHCP server and `GetIpNetTable2` (ARP rung, already on list) sees it immediately. Not a separate rung.

### eAtmosphere / SDRplay open-source discovery
**Rationale.** Different vendor's protocol; FlexRadio doesn't speak it.

---

## 5. Open questions (empirical testing required on a real Flex)

1. **Does Flex 6000/8000 firmware advertise via mDNS at all?** Capture `tcpdump -i <iface> port 5353` while a 6300/6500 boots and stays running for 60 seconds. If we see *any* mDNS announce, hostname-pattern and meta-query rungs become much more valuable.

2. **Does Flex respond to ICMP broadcast (255.255.255.255 / directed broadcast)?** One ping test against Don's 6300 confirms or rules out.

3. **Does Flex auto-register UPnP port mappings when SmartLink is enabled?** Inspect router port-mapping table on Don's UniFi after pairing SmartLink. If yes, the IGD rung is much higher-yield than estimated.

4. **What hostnames do real users put in their hosts file?** Survey 5-10 user hosts files (anonymized) — Don, Justin, BHN net regulars. Tune the regex.

5. **What's the actual reverse-DNS hit rate on real consumer routers?** Test on Don's UniFi, Justin's setup, plus 3-4 BHN net members with diverse routers. Build a hit-rate matrix per router brand.

6. **Does the SmartLink coordinator return radio metadata (especially LAN-side public_ip) without establishing a relay tunnel?** Read the SmartLink-API-Reference.pdf (April 2026 draft) for the exact message flow; if it doesn't, talk to KE5DTO/Eric Wachsmann about an API extension.

7. **Tailscale MagicDNS and Flex:** when Don tailnets his shack, does the Flex appear in `tailscale status` automatically (because it shares an IP via subnet routing) or does it need a dedicated Tailscale node? What's the hostname pattern?

8. **WSDAPI on .NET 10:** Is the COM-interop path for `IWSDiscoveryProvider` viable, or do we hand-roll the SOAP-over-UDP/3702 path? Spike a 50-LOC test to see.

9. **Network-change watchdog debounce:** Empirically measure how many `NetworkAddressChanged` events fire during a typical Wi-Fi reconnect. Tune debounce window.

10. **Recent peer-IP history retention:** What's the right cap? 5 IPs probably covers home/portable/VPN; 20 covers contest stations with multiple sites; 100 is hoarder territory.

---

## Sources

- [WS-Discovery (Wikipedia)](https://en.wikipedia.org/wiki/WS-Discovery)
- [WSDAPI — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/wsdapi/about-web-services-for-devices)
- [PnP-X — Microsoft Learn](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/fundisc/pnp-x)
- [Function Discovery — Microsoft Learn](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/fundisc/fd-portal)
- [System.ServiceModel.Discovery (WCF)](https://learn.microsoft.com/en-us/dotnet/api/system.servicemodel.discovery)
- [GetExtendedTcpTable — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedtcptable)
- [GetTcpTable2 — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-gettcptable2)
- [GetIpNetTable2 — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-getipnettable2)
- [NotifyUnicastIpAddressChange — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-notifyunicastipaddresschange)
- [NetworkChange.NetworkAddressChanged — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.net.networkinformation.networkchange.networkaddresschanged)
- [Get-NetNeighbor — Microsoft Learn](https://learn.microsoft.com/en-us/powershell/module/nettcpip/get-netneighbor)
- [DnsClient.NET](https://dnsclient.michaco.net/)
- [RSSDP — .NET SSDP implementation](https://github.com/Yortw/RSSDP)
- [Mono.Nat — UPnP IGD + NAT-PMP for .NET](https://github.com/alanmcgovern/Mono.Nat)
- [SharpSnmpLib](https://github.com/lextudio/sharpsnmplib)
- [Tailscale MagicDNS](https://tailscale.com/docs/features/magicdns)
- [Tailscale CLI Reference](https://tailscale.com/kb/1080/cli/)
- [WireGuard wg(8) man page](https://www.wireguard.com/quickstart/)
- [Multicast DNS (Wikipedia)](https://en.wikipedia.org/wiki/Multicast_DNS)
- [Service Location Protocol (Wikipedia)](https://en.wikipedia.org/wiki/Service_Location_Protocol)
- [LLDP (Wikipedia)](https://en.wikipedia.org/wiki/Link_Layer_Discovery_Protocol)
- [LLMNR — RFC 4795](https://datatracker.ietf.org/doc/html/rfc4795)
- [NetBIOS over TCP/IP (Wikipedia)](https://en.wikipedia.org/wiki/NetBIOS_over_TCP/IP)
- [IPv6 Neighbor Discovery — RFC 4861](https://datatracker.ietf.org/doc/rfc4861/)
- [hosts file (Wikipedia)](https://en.wikipedia.org/wiki/Hosts_(file))
- [FlexRadio TCP/IP API wiki — Discovery protocol](http://wiki.flexradio.com/index.php?title=Discovery_protocol)
- [FlexRadio Community — UDP discovery protocol thread](https://community.flexradio.com/discussion/6983033/back-to-the-udp-discovering-protocol)
- [FlexRadio Community — New discovery protocol (VITA-49)](https://community.flexradio.com/discussion/5993575/new-discovery-protocol)
- [FlexRadio SmartLink API Reference (April 2026 DRAFT)](https://edge.flexradio.com/www/offload/20260331201908/SmartLink-API-Reference.pdf)
- [AetherSDR — Linux-native SmartSDR client](https://github.com/ten9876/AetherSDR)
- [WSJT-X User Guide](https://wsjt.sourceforge.io/wsjtx-doc/wsjtx-main-2.6.1.html)
- [UPnP IGD (Wikipedia)](https://en.wikipedia.org/wiki/Internet_Gateway_Device_Protocol)
- [UPnP WANIPConnection:2 Service spec](https://upnp.org/specs/gw/UPnP-gw-WANIPConnection-v2-Service.pdf)
- [Network Discovery and Name Resolution under Windows 10 (gary-nebbett)](http://gary-nebbett.blogspot.com/2021/05/network-discovery-and-name-resolution.html)
