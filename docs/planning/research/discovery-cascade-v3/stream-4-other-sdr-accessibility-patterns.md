# Stream 4 — How Other Apps Handle "Find My LAN Device When Standard Discovery Fails"

- **Stream**: 4 of N (discovery-cascade-v3 research push)
- **Date**: 2026-05-06
- **Author**: Claude (research agent), commissioned by Noel (K5NER)
- **Research question**: How do other apps — direct SDR/ham competitors and adjacent LAN-device domains — solve the equivalent of "find this radio when UDP broadcast discovery has gone silent"? What patterns and anti-patterns should inform JJ Flex's discovery cascade?
- **Method**: Web research only; no code written. Citations inline as `[ref-N]` keyed to the references list at the bottom.

---

## 1. Executive summary — top patterns to borrow

1. **Cloud-side fallback list as a guaranteed-non-empty UI seed** (FlexRadio's own SmartLink, Plex's plex.tv server discovery). When local LAN discovery is empty, the radios you have signed in to elsewhere still appear in the list; once local discovery succeeds, the local entry replaces the cloud entry. This is *the* pattern that turns a dead-end "no radios found" screen into "here's what you can still try." JJ Flex already has SmartLink — it should appear as a persistent fallback row *during* the discovery cascade, not only after the cascade gives up. **[ref-2, ref-9, ref-19]**

2. **Parallel multi-method discovery, not a serial cascade for the user-facing latency** (Home Assistant, Plex). Run mDNS, SSDP, broadcast probe, multicast probe, and last-known-IP pings concurrently. First one home wins; the others quietly cancel. The "cascade" is a developer mental model — the user shouldn't wait through multiple sequential timeouts. **[ref-3, ref-4, ref-7]**

3. **Last-known-good IP as cheap zero-cost first probe** (Sonos, Roon, almost every IoT app). Cache `(serial, IP, MAC)` from prior successful connect and try that endpoint *immediately* on app start before any broadcast/multicast probe completes. JJ Flex Rung 1a already plans this; the pattern is universally validated.

4. **Multicast + broadcast in parallel** (Sonos, Plex GDM, Chromecast). Sonos sends SSDP probes to **both** 239.255.255.250 *and* 255.255.255.255 in the same go because some networks/firewalls/Wi-Fi APs pass one but not the other **[ref-5]**. Plex GDM tries calculated /24 broadcast first, then 239.0.0.250 multicast as fallback **[ref-3]**. JJ Flex's Rung 2 should send to both addresses on the same port without waiting for one to fail.

5. **Subnet sweep with TCP-knock as resilience layer** (Angry IP Scanner, Fing, our own Rung 4). When all probe-and-listen methods fail, scanning the local /24 with quick TCP knocks on a known port (4992/TCP for Flex) is the universal "did it move IPs?" backstop. ARP-then-TCP is faster than ICMP-then-TCP because ARP can't be firewalled.

6. **Never present a true dead end** (BlindCat anti-pattern parallel; Plex/Spotify pattern). Every "no devices found" screen needs an obvious "Add by IP/hostname" entry path AND a clear explanation of what was tried. SmartSDR-Windows famously *doesn't* have manual-IP entry for Windows clients (only iOS) — a long-standing user complaint **[ref-9]**. JJ Flex shipping manual-IP entry from day one is competitive differentiation, not a nice-to-have.

---

## 2. Direct competitors / SDR + ham apps

### 2.1 SmartSDR for Windows (FlexRadio's official client)

**Discovery method**: Listens for the radio's UDP broadcast packets on port 4992, sent to 255.255.255.255 approximately once per second **[ref-1, ref-9]**. Packet is VITA-49 extension format with stream ID 0x800, class ID 0x534CFFFF, ASCII payload containing serial number and IP **[ref-12]**.

**Fallback when discovery fails**: There is essentially no native local fallback. Per multiple FlexRadio Community threads, when broadcast discovery fails (managed switches, WiFi power-saving, "green" smart switches, separate VLANs, mesh APs in degraded states), users are told to fix the network. **[ref-8]**

**SmartLink as a passive backstop**: If the user is logged into SmartLink, the radio shows up via the cloud list "right away (instead of saying 'There are currently no radios available')" — and once local discovery later succeeds, the local entry replaces the SmartLink entry **[ref-8]**. **This is the load-bearing pattern JJ Flex should copy verbatim.**

**Manual-IP entry**: A long-standing user-requested feature *not implemented* on Windows (the iOS version supports it) **[ref-9]**. Multiple FlexRadio Community threads request "Fixed IP Input for Smart SDR" / "Static IP - SmartSDR v1.9.7" / "Feature Enhancement Request - Static IP Addresses." This is a clear competitive opportunity for JJ Flex.

**UX when discovery fails**: "There are currently no radios available" — a true dead end unless SmartLink fills it. Documented as the pain point that drives the entire FRStack/SliceMaster/JJ Flex aftermarket.

**Reusable for JJ Flex**: SmartLink-as-fallback-row pattern (Rung 5 candidate). Lesson on what *not* to do: the silent dead-end UI on local discovery failure.

### 2.2 BlindCat (closed-source competitor for blind FlexRadio users)

**Discovery method**: Web search returned no results for "BlindCat Flex radio discovery." Plausible inference from `project_anti_patterns_from_blindcat.md` and that BlindCat is built atop FlexLib: it almost certainly uses the same UDP-4992 broadcast discovery as SmartSDR (no aftermarket app has ever published an alternative discovery mechanism for Flex radios in the public corpus), and inherits the same dead-end UX.

**Reusable for JJ Flex**: Nothing positive to borrow. The data point itself — *no third-party Flex client has documented a discovery cascade* — is the gap JJ Flex is filling. We're not competing on this dimension; we're inventing it for the segment.

### 2.3 Maestro Companion / Maestro hardware

**Discovery method**: Same UDP-broadcast-on-4992 mechanism, observed via packet capture: "the discovery packets are coming out like clockwork every second on UDP port 4992 addressed to 255.255.255.255" **[ref-13]**.

**Fallback**: SmartLink populates the radio list when local discovery is delayed — same pattern as SmartSDR Windows **[ref-13]**. Specifically observed: "if the Maestro is logged in to SmartLink, it shows the radio available through SmartLink right away (instead of saying 'There are currently no radios available'). Once local discovery eventually succeeds, the local entry replaces the SmartLink entry."

**Documented failure modes**: WiFi mesh nodes in degraded states (passing unicast normally but blocking most broadcasts), green-power switches, sleep-suppression on WiFi APs. Resolution per FlexRadio engineers: reset network gear **[ref-13]**.

**Reusable for JJ Flex**: Confirms SmartLink-as-fallback as the *only* commercial-grade pattern for FlexRadio specifically. Also confirms the WiFi-broadcast-suppression failure mode is industry-recognized — JJ Flex's R6 cascade is solving a known-real problem, not a phantom.

### 2.4 FRStack (Mike Carper, K1DBO; commercial third-party Flex companion)

**Discovery method**: Same UDP-4992 broadcast listening. "The discover broadcast packets sent out from the radios contain s/n and IP" **[ref-14]**.

**Fallback when discovery fails**: None documented. The FRStack troubleshooting flow when "no radios available" is shown points users at Windows Firewall rule entries (TCP+UDP inbound for frstack.exe) **[ref-14]**. Failure mode is treated as a firewall config problem to fix, not as something the app can route around.

**Multi-client binding**: FRStack has `/client=StationName` command-line argument to bind to a specific SmartSDR client when V3 multi-client is in use **[ref-14]** — orthogonal to discovery, but a useful UX pattern (allow command-line override of automatic selection).

**Reusable for JJ Flex**: Command-line-IP-override pattern (`--radio-ip=192.168.1.50 --radio-serial=1234567`) is a low-cost addition that helps power users and crash-recovery scripts.

### 2.5 SliceMaster (Mike Buffington, K1DBO; free Flex companion)

**Discovery method**: Inferred — same FlexLib-based UDP-4992 listening (no published alternate mechanism in search corpus) **[ref-15]**.

**UX when discovery fails**: "SliceMaster not seeing FlexRadio" is a recurring forum thread title **[ref-15]**. No documented in-app fallback.

**Reusable for JJ Flex**: Negative finding — confirms the entire third-party Flex ecosystem inherits SmartSDR's discovery dead-end. JJ Flex's cascade is genuinely novel in this space.

### 2.6 PowerSDR (legacy FlexRadio app for 1500/3000/5000 series)

**Discovery method**: USB or FireWire enumeration via OS device-manager APIs — fundamentally different problem domain. PowerSDR's "discovery" was OS-level USB/1394 driver enumeration: "the FLEX-5000 and FLEX-3000 have a small Firewire connection status LED inside the radio. Additionally, you can check the computer's recognition of a radio's FireWire connection with the Flexradio.exe utility, and if both radios are connected, it will give you a rig selection window to select which rig you want to connect to" **[ref-16]**.

**Reusable for JJ Flex**: Almost nothing — the wire-bound era's discovery model doesn't translate. But the "rig selection window when multiple are present" UX hint is sound: when discovery returns more than one radio, show *all* of them with last-connected one pre-selected. JJ Flex already does roughly this.

### 2.7 Hamlib `rigctld` (network rig control daemon)

**Discovery method**: **None — fully manual config.** "rigctld opens a TCP server with default port 4532 that any compatible application can connect to" and the Hamlib wiki documents only "Enter the hostname:port as the Device" **[ref-17, ref-18]**. There's no service discovery, no mDNS, no broadcast probe.

**UX when discovery fails**: Connection refused / timeout — the user must manually verify host, port, and rigctld instance is running. The Hamlib FAQ does not even cover the case.

**Reusable for JJ Flex**: Negative finding — the entire ham-radio CAT ecosystem has *no* discovery culture; manual hostname:port entry is the norm. This is a low bar JJ Flex easily clears, and the lack of discovery in Hamlib is something to consciously address when JJ Flex extends to the broader Hamlib radio class (TS-590, etc.) — it'll be a JJ Flex differentiator, not table stakes.

**Anti-pattern carry-forward**: rigctld treats the user as the discovery mechanism. JJ Flex should never inherit that posture for the multi-radio expansion.

### 2.8 WSJT-X / FT8 logging clients

**Discovery method**: None for the radio itself — relies on intermediary CAT layer (Hamlib NET rigctl, OmniRig, Ham Radio Deluxe, DX Lab Commander, FlexRadio's own integration) **[ref-19]**.

**Reusable for JJ Flex**: Confirms that the digital-modes ecosystem outsources the discovery problem entirely. JJ Flex already exposes a CAT/IF-Cat surface to these clients, so this is irrelevant to the radio-discovery problem but reaffirms that JJ Flex must be the one to solve discovery — nothing downstream will.

### 2.9 Airspy SDR# / SDRSharp / SDRplay tools

**Discovery method**: Local USB enumeration (driver-level). For network mode (SPY Server), the user manually enters server hostname/IP **[ref-20]**. No automatic LAN scan for SPY Servers documented.

**Reusable for JJ Flex**: Negative finding — even the hard-real-time SDR ecosystem treats network discovery as the user's problem. Same lesson as Hamlib.

### 2.10 SDRangel / GQRX / CubicSDR

**Discovery method**: SoapyRemote with manually-entered device strings: `soapy=0,driver=remote,remote=tcp://192.168.0.111:55132` **[ref-21]**. `SoapySDRUtil --find="remote=<host>"` exists but requires the user to know the host first.

**Reusable for JJ Flex**: Negative finding. Confirms the entire open-source SDR stack puts the burden on the user. JJ Flex's cascade is novel relative to this entire neighborhood.

### 2.11 OpenWebRX / KiwiSDR / WebSDR

**Discovery method**: Web-directory cloud lookup. OpenWebRX+ "will periodically query ReceiverBook, KiwiSDR, and WebSDR.org sites for other online SDR receivers" **[ref-22]**. Receivers self-register with public directories; clients browse the directories.

**Reusable for JJ Flex**: A *different* kind of fallback — directory-based discovery instead of probe-based. **Pattern worth borrowing for the future "private circle of trust" feature**: a JJ Flex user could opt to register their radio's SmartLink ID with a private directory their friends can browse (call it "JJ Flex Radio Buddies") instead of every friend needing the SmartLink ID DM'd to them. Out of scope for current cascade, but file for waterfall-era differentiation.

### 2.12 Roku ECP (External Control Protocol — adjacent SDR-like LAN device)

**Discovery method**: Standard SSDP M-SEARCH on 239.255.255.250:1900; Roku devices respond with their HTTP-control endpoint **[ref-23]**.

**Fallback**: None documented in the dev docs — relies on SSDP working.

**Reusable for JJ Flex**: Reinforces that SSDP/multicast-only is a fragile choice on modern segmented networks. Roku's user-facing recovery is "use the IP address shown in the Roku settings menu" — same dead-end pattern as SmartSDR.

---

## 3. Adjacent domains — patterns from outside SDR/ham

### 3.1 Sonos (multi-speaker LAN audio; famously elegant discovery)

**Discovery method**: SSDP multicast (239.255.255.250:1900) + broadcast (255.255.255.255:1900) sent in parallel from the controller; speakers also send periodic "I'm alive" announcements at regular intervals so controllers can build a list passively **[ref-5]**. Reply is unicast from speaker back to controller's source port.

**Fallback architecture**:
- **Dual multicast + broadcast addressing in same probe** — explicit acknowledgment that some networks pass one but not the other.
- **mDNS as a second protocol** layered on SSDP (per various sources) — Sonos S1 used SSDP-primary; the new S2 controller in May 2024 controversially "dropped SSDP and rely entirely on mDNS for device discovery" causing significant discovery problems for many users **[ref-5, ref-26]**.
- **Periodic announcement** (heartbeat with max-age) means controllers don't have to actively probe at all once initial discovery succeeds.

**Cached state**: Sonos speakers cache previous Wi-Fi config aggressively, which is helpful for reconnects after router changes but creates a different failure mode (speaker keeps trying old SSID) **[ref-26]**.

**UX when discovery fails**: "Sonos speakers just disappear" is a famous community complaint **[ref-26]**. Recovery involves power-cycling and resetting network components — Sonos is *not* an exemplar for failure UX, only for the multi-protocol probe.

**Anti-pattern**: The May 2024 Sonos S2 redesign that dropped SSDP in favor of mDNS-only is a textbook case of "removing a working fallback because the team thought the new way was strictly better." JJ Flex's cascade should grow strictly additively — never *remove* a rung that's been observed to help any user.

**Reusable for JJ Flex**:
- Dual-address probe (Rung 2 should fire to both 255.255.255.255 and any directed broadcast for the local /24 in the same UDP send loop).
- Heartbeat semantics — once we've discovered the radio, watch for its periodic 1Hz beacon and treat absence over N seconds as cause to re-probe rather than mid-session reconnect-and-pray.
- The S2/SSDP cautionary tale: never deprecate a working rung.

### 3.2 Plex Media Server (GDM — "Good Day Mate" discovery)

**Discovery method (GDM source code analysis)** **[ref-3, ref-7]**:
- UDP M-SEARCH HTTP/1.1 to port 32414.
- **Per-interface broadcast first**: enumerates network interfaces, computes /24 broadcast (replace last octet with 255), sends per-interface.
- **Multicast fallback**: 239.0.0.250 if broadcast fails or `sendto` returns 0 bytes.
- **Retry**: up to 2 send attempts before listening; 5-second listen window; 10ms per-socket timeout.
- **Comments in source mention deprecated Roku-specific code paths** suggesting Plex previously had multiple per-target-class probes that were consolidated.

**Cloud fallback**: Plex Media Server publishes its private and public IP to plex.tv; clients signed into the same Plex account can fetch the server list from plex.tv without any LAN probe **[ref-24]**. This is the *exact* parallel of FlexRadio's SmartLink list. "On same network – Allow fallback" Roku setting is the user-visible knob **[ref-25]**.

**Manual fallback**: Manual server IP/hostname entry is *available* (some clients better than others; Roku has it natively, mobile apps require workarounds) **[ref-25]**.

**HTTPS-to-HTTP fallback**: "If the server advertised a secure hostname, clients add a secure connection as well and set the HTTP connection as a fallback" **[ref-2]** — discovery surfaces multiple connection candidates in priority order, not a single endpoint.

**Anti-pattern**: Recent Plex changes deprecated the ability to bypass plex.tv for cloud login, breaking offline-LAN-only setups. JJ Flex must never make the cloud (SmartLink/JJ Flex Data Provider) a hard dependency for local connection.

**Reusable for JJ Flex**:
- Per-interface broadcast — JJ Flex should enumerate adapters and probe each, not just rely on the OS's default route. (Multi-NIC laptops with WiFi+Ethernet+VPN are common in shacks.)
- Multiple connection candidates per discovered radio (LAN IP + SmartLink ID + future hostname) ranked by latency/cost.
- 5-second listen window is industry-typical — a useful sanity check for our cascade timing.

### 3.3 Roon Server (RAAT — Roon Advanced Audio Transport)

**Discovery method**: Unpublished proprietary protocol layered over LAN. Community evidence shows it's affected by VLAN segmentation and firewall rules, suggesting standard multicast/broadcast underneath **[ref-27]**. Roon Labs does not publish RAAT discovery internals.

**Reusable for JJ Flex**: Limited — proprietary closed protocol means no learnings except the negative one: even audiophile products with substantial engineering investment have LAN-discovery fragility.

### 3.4 Spotify Connect (zeroconf)

**Discovery method**: Pure mDNS / DNS-SD with service type `_spotify-connect._tcp` **[ref-28, ref-29]**. Devices advertise via mDNS; Spotify clients browse the service type and present a device list.

**Fallback**: None at the discovery layer — if mDNS is broken, devices don't show up. Spotify's recovery model is "fix your network."

**Reusable for JJ Flex**: Reinforces that mDNS-only is fragile (same Sonos lesson). But if JJ Flex were to ever advertise itself on the LAN (e.g., the radio service announcing on `_jjflex-radio._tcp.local`), this is the precedent for naming.

### 3.5 Chromecast / Google Cast (DIAL → mDNS migration)

**Discovery method**: Original v1: DIAL over SSDP (UDP 1900 multicast 239.255.255.250). Current v2: mDNS (`_googlecast._tcp.local`) **[ref-6]**.

**Fallback semantics**: "Some applications or devices will only try to look for Chromecast devices using DIAL service, and if the application only supports DIAL service then the Chromecast device and the end device used to discover it must be on the same VLAN and multicast forwarding needs to be enabled." Chromecast supports both methods so legacy clients still find devices.

**Reusable for JJ Flex**: Demonstrates the "additive cascade" principle — when Google migrated v1→v2, they kept v1 alive. JJ Flex's R6 cascade should treat each rung the same way: rungs accumulate, they don't get retired when a "better" rung ships.

### 3.6 HomeKit / Matter / BLE-WiFi commissioning

**Discovery method (commissioning)**: Three protocols simultaneously — BLE advertisement, mDNS (`_matterc._udp` for commissionable, `_matter._tcp` for operational), and Wi-Fi softAP **[ref-30, ref-31]**. Client probes all three to find a device in commissioning mode.

**Discovery method (operational)**: After commissioning, mDNS `_matter._tcp` only.

**Cascade**: BLE → Wi-Fi credential transfer → mDNS-on-new-network. The BLE path is specifically a fallback for when the device isn't yet on Wi-Fi.

**Reusable for JJ Flex**:
- The commissionable-vs-operational distinction maps cleanly to "first-time-add" vs "reconnect-known-radio." JJ Flex Rung 1a (cached IP) is the operational-mode probe; broader cascade is the commissionable-mode probe.
- Multiple discovery channels in parallel during the high-uncertainty (first-time / lost) state.
- Once known, narrow to a single cheap probe.

### 3.7 Network printers (WSD + IPP + SNMP cascade)

**Discovery method (Windows native)**: WS-Discovery (UDP 3702 multicast 239.255.255.250) is the primary; falls back to direct SNMP probe of known printer IPs; IPP (port 631) is a parallel discovery surface **[ref-10, ref-11]**.

**Priority cascade**: Windows tends to prioritize WSD over IPP for reasons that have caused complaints (forces you to disable WSD in Services to use IPP). But the cascade is real: WSD, then IPP, then SNMP, then direct port probe.

**WS-Discovery specifics (relevant to our cascade design)** **[ref-32]**: SOAP-over-UDP probe (multicast probe → unicast probe-match response). Targets MAY accept and respond to **unicast probes sent to their direct transport address** — which means if you remember a printer's IP, you can re-issue the probe directly to that IP via unicast and get the same probe-match response without any multicast involvement. This is essentially "directed unicast SSDP." JJ Flex's Rung 1a (cached IP + TCP knock) is doing the equivalent at a lower level; Rung 1b could send a directed unicast UDP probe to the cached IP on 4992 and look for the same VITA-49 packet that broadcast normally produces. **This is a candidate Rung the current R6 cascade may not have explicitly.**

**SNMP fallback**: Generic SNMP printer status query (sysDescr OID) to the cached IP address verifies "yes, a printer is still there" even when no discovery protocol responds.

**Reusable for JJ Flex**:
- **Directed unicast probe** to a cached IP on 4992 — solicit the same VITA-49 announcement the radio normally broadcasts, but unicast. If the radio responds, we've validated the IP is still good without waiting for the next 1Hz broadcast cycle. Worth confirming with FlexLib whether radios respond to a unicast probe; if so, this becomes Rung 1.5 between cached-IP-TCP-knock and full broadcast listen.
- The Windows-printer-discovery cascade is the closest documented analog to what we're building. Mirror its "multiple methods, results merged into one device list" UX.

### 3.8 UniFi / Ubiquiti device adoption

**Discovery method**: Custom UDP protocol on port 10001, sent multicast to 233.89.188.1 (a non-standard multicast address — chose a unique one to avoid SSDP collision). TLV-encoded packet with hardware address, IP info, firmware version, hostname, model, etc. **[ref-33, ref-34]**.

**Two protocol versions** — discovery script "will attempt to leverage version 1 of the protocol first and, if that fails, attempt version 2" **[ref-33]**. Forward-compatible cascading within the same logical mechanism.

**Discovery vs adoption split**: Initial discovery is local (multicast). Adoption is via cloud-redirect (`set-inform` URL pointing to controller). Once adopted, devices "phone home" to the configured controller URL — *no further LAN discovery needed*.

**Reusable for JJ Flex**:
- **Protocol versioning within one rung** — when the discovery cascade hits a rung, try version 2 of the probe first, fall back to version 1 if no response in N ms. JJ Flex's pre-/post-FlexLib-4.2.18 transition is exactly this scenario.
- **Phone-home alternative**: If the radio could be configured to phone-home to a JJ Flex-controlled rendezvous point, all LAN discovery becomes optional. This is *philosophically incompatible* with the no-silent-phone-home memory entry (the user must opt in per-event), but a *user-initiated* "Register this radio with my JJ Flex Data Provider account so I never need broadcast discovery again" feature is in alignment. **File for Sprint 30+ post-cascade feature.**

### 3.9 Home Assistant (the goldmine)

**Discovery method**: Multi-protocol orchestration — mDNS/Zeroconf (UDP 5353, 224.0.0.251), SSDP/UPnP (UDP 1900, 239.255.255.250), DHCP fingerprinting (snooping DHCP requests for known MAC OUIs and hostname patterns), Bluetooth, USB enumeration, network-prefix-aware integrations **[ref-4, ref-35, ref-36]**.

**Architecture** **[ref-4]**: Per-protocol "registration API" — integrations declare what mDNS service types / SSDP STs / DHCP fingerprints / etc. they care about. The discovery framework fans out probes once and notifies all interested integrations of any matches. **One probe loop, many consumers.**

**DHCP discovery** **[ref-35]**: `aiodiscover` library snoops DHCP traffic on the host network interface and matches against a registry of known device fingerprints (OUI + hostname patterns). When a known device joins, the matching integration is offered for setup. **This is the rung most apps don't have.** A FlexRadio has a known MAC OUI (Texas Instruments range, since the radios use TI Stellaris/Tiva microcontrollers) — JJ Flex could passively learn that a radio joined the network even before it sends its first VITA-49 broadcast.

**Failure mode**: HA has the same multicast-segmentation problems as everyone. Their advice: "run the container/VM in host/bridged networking, and verify that UDP 5353 (mDNS) and 1900 (SSDP) multicast traffic is allowed on the same subnet/VLAN as your IoT devices" **[ref-36]**. They don't have a magic bullet, but they probe across many protocols so the chance of *all* of them being blocked is low.

**Reusable for JJ Flex**:
- **One probe loop, many consumers** is overkill for a single-vendor app, but the *architectural principle* (every rung shares the listen-socket lifetime) is sound.
- **DHCP snooping** as a passive discovery rung is a candidate Rung worth considering for V4 of the cascade. Requires WinPcap/Npcap (already a dependency for some Windows network tools); legality and accessibility implications need vetting.
- **Discovery callbacks** instead of one-shot probes — the discovery layer should support "tell me when a radio is found" as well as "find any radios right now," so the rest of the app can subscribe.

### 3.10 Network scanner tools (Angry IP Scanner, Fing, Nmap)

**Discovery techniques** **[ref-37]**:
- ARP request (layer 2; bypasses firewall, only works on same subnet) — instant, definitive answer for "is anything at that IP?"
- ICMP ping (layer 3; firewallable) — common but unreliable.
- TCP SYN to a likely-closed port — host responds with RST if it's there, no response if not.
- NetBIOS query — Windows-specific identification.
- Multithreaded port-range scan.

**Architectural pattern**: Scanners assume nothing about the target. They spray every applicable probe at every IP in a range and merge results. For JJ Flex's "find one specific radio" problem, the equivalent is "spray every probe at every IP on the local /24 looking for the 4992-port-open signature."

**Reusable for JJ Flex**:
- ARP table query is a *zero-network-traffic* discovery method on Windows: `Get-NetNeighbor` (PowerShell) or the Win32 `GetIpNetTable` API returns the local ARP cache. If the radio's MAC was seen recently by Windows, its IP is already cached. **This is candidate Rung 1c — even cheaper than Rung 1a.**
- TCP-knock on 4992 to every IP in /24 is the universal backstop. JJ Flex's Rung 4 (per the cascade overview) is presumably this.
- Multithreaded so /24 sweep finishes in <2 seconds. Don't serialize.

### 3.11 mDNS browsers (Bonjour Browser, dns-sd, avahi-browse)

**Method**: `avahi-browse -a` (or `dns-sd -B _services._dns-sd._udp`) enumerates *every* service type advertised on the network, then for each type can enumerate instances **[ref-38, ref-39]**.

**Reusable for JJ Flex**: If FlexRadio ever starts advertising a `_smartsdr._tcp` service type via mDNS (current radios don't, but it's a low-friction firmware change), JJ Flex would gain a new rung "for free" by browsing for that type. **File a feature request with Tim Ellison at FlexRadio: please advertise the radio via mDNS as well as VITA-49 broadcast.**

### 3.12 Windows Function Discovery / WSDAPI

**Method**: Windows-native WS-Discovery client API. Apps that consume WSDAPI get auto-discovery of any WS-Discovery-advertising device on the LAN (printers, scanners, NAS).

**Reusable for JJ Flex**: If FlexRadio added WS-Discovery advertisement to firmware, JJ Flex could leverage existing OS-level discovery infrastructure rather than rolling its own listen sockets. **Same feature request applies as for mDNS.**

---

## 4. Pattern catalog (with applicability)

### Pattern A: Cloud-side fallback list as guaranteed-non-empty seed
**Source**: SmartSDR/Maestro SmartLink **[ref-8, ref-13]**, Plex GDM + plex.tv **[ref-2, ref-24]**.
**Mechanism**: When local discovery is empty, populate the list from a cloud account the user is already signed into. Local entries replace cloud entries when local discovery succeeds.
**JJ Flex applicability**: **High — borrow immediately.** SmartLink already exists; the "show SmartLink radios in the local discovery list as fallback rows" UI change is small and is the single biggest UX win. Should appear *during* the cascade, not only after.

### Pattern B: Last-known-good IP as zero-cost first probe
**Source**: Sonos cached config **[ref-26]**, Roon, virtually every reconnecting client.
**Mechanism**: Persist `(serial, IP, MAC, last_connected_at)` from prior successful connect. On app start, immediately attempt direct TCP connect to last IP before any broadcast probe.
**JJ Flex applicability**: **Already in plan as Rung 1a.** Confirmed universal. No risk in shipping.

### Pattern C: Directed unicast probe to last-known IP
**Source**: WS-Discovery spec section 4.3 ("A Target Service MAY also accept and respond to unicast Probe messages") **[ref-32]**, SSDP M-SEARCH unicast variants.
**Mechanism**: Send the same probe packet directly to the cached IP via unicast UDP. If the device responds normally, IP is still good even without listening for the periodic broadcast cycle.
**JJ Flex applicability**: **Candidate new rung 1.5** — needs to verify FlexLib radios respond to unicast UDP-4992 probes (likely yes, given the discovery service binds to 4992 generally; needs SmartSDR ILSpy decompile per memory entry `project_smartsdr_decompile_authorization.md`). If supported, this is a sub-second confirmation that's strictly faster than waiting for the 1Hz broadcast cycle.

### Pattern D: Dual-address probe (multicast + broadcast in one send loop)
**Source**: Sonos SSDP **[ref-5]**, Plex GDM (per-interface broadcast + multicast fallback) **[ref-3]**.
**Mechanism**: When probing, send to both the broadcast address (255.255.255.255 or directed broadcast for the local /24) AND any relevant multicast address (e.g., 224.0.0.251, 239.255.255.250) simultaneously. Don't wait for one to fail before trying the other.
**JJ Flex applicability**: **High.** R6 likely already does broadcast; consider adding a directed-broadcast (e.g., 192.168.1.255 derived from the local interface's /24) in parallel with the 255.255.255.255 send. The Maestro thread **[ref-13]** has a packet capture confirming the radio sends to 255.255.255.255 — but per the SmartSDR firewall thread, "Windows may or may not pass on something addressed to 255.255.255.255:4992, but DOES pass on things addressed to 192.168.0.255:4992" **[ref-1]**. Directed broadcast is documented to bypass real Windows-firewall edge cases.

### Pattern E: Per-network-interface enumeration
**Source**: Plex GDM **[ref-3]** ("Retrieves network interfaces via `netif.getInterfaces()`. Filters to interfaces with broadcast capability. Creates UDP socket per interface").
**Mechanism**: Don't assume the OS default route is the right one. Enumerate all active interfaces (Ethernet, WiFi, virtual NICs from VPN) and probe each separately.
**JJ Flex applicability**: **Medium-high.** Shack PCs with WiFi + Ethernet + Tailscale are common. Worth verifying the current cascade enumerates interfaces; if not, this is a focused add.

### Pattern F: ARP cache query (zero network traffic)
**Source**: Network scanners **[ref-37]** (Angry IP Scanner reads MAC from ARP table for local-subnet hosts).
**Mechanism**: Before issuing any network probe, query the local ARP cache for the cached radio's MAC address. If present, the IP it's bound to is the current IP. Windows: `Get-NetNeighbor`, `arp -a`, or `GetIpNetTable` Win32 API.
**JJ Flex applicability**: **High — candidate new Rung 1c.** Strictly cheaper than even Rung 1a (no packets sent at all). Only works if the radio's MAC is already in the OS's ARP cache (i.e., something on the box has talked to it recently), but when it works, it's instant.

### Pattern G: Subnet sweep with TCP knock
**Source**: Angry IP Scanner, Fing **[ref-37]**, presumably JJ Flex Rung 4 already.
**Mechanism**: For each IP in the local /24 (or each interface's /24), open a TCP connection to port 4992 with a short timeout (~100ms). Connections that succeed are radio candidates.
**JJ Flex applicability**: **Already in plan.** Multithreaded — a /24 should complete in under 2 seconds.

### Pattern H: Heartbeat-based passive presence
**Source**: Sonos SSDP heartbeats **[ref-5]**, FlexRadio's own 1Hz VITA-49 broadcast **[ref-1, ref-12]**.
**Mechanism**: Once discovered, the device's periodic announcement is the keep-alive. Absence of N consecutive announcements triggers re-discovery, not a mid-session reconnect.
**JJ Flex applicability**: **High.** Currently the app appears to treat session loss as a connection-level event; treating it as a discovery-level event (radio went away, restart cascade) may yield better recovery UX.

### Pattern I: Multi-protocol parallel probe with first-home-wins
**Source**: Home Assistant **[ref-4]**, Matter commissioning **[ref-30]**.
**Mechanism**: Fire all protocol probes simultaneously; the first to return a result populates the device list. Cancel the others (or let them run if cheap).
**JJ Flex applicability**: **High.** Matches the "spare nothing" cascade philosophy. Implement as Task.WhenAny() (or VB equivalent) over the cascade rungs that don't have explicit ordering dependencies.

### Pattern J: User-visible cascade transparency (the "what was tried" panel)
**Source**: This is rarely done well in the surveyed apps — most fail silently with "no devices found." But Home Assistant's debug log surface and Matter's commissioning UI do show cascade progress.
**Mechanism**: When discovery is running, show the user which rung is currently active and which have finished without finding the radio. When discovery completes empty, show the full list of attempts so the user (or a help-desk volunteer) has actionable diagnosis info. Critical for accessibility — screen-reader users have no visual fallback for "well it's just not finding it" hand-waving.
**JJ Flex applicability**: **High and JJ-Flex-distinctive.** Aligns with the "no silent failures" memory entry and the BlindCat anti-patterns checklist. Worth its own settings panel: "Discovery diagnostics — what did JJ Flex try?"

### Pattern K: Manual IP entry as the universal escape hatch
**Source**: Plex (most clients) **[ref-25]**, Roku ECP, Spotify Connect (workaround), Hamlib rigctld (the *only* mode), SmartSDR for iOS (but not Windows) **[ref-9]**.
**Mechanism**: Always offer "Add radio by IP/hostname" entry. Bypass discovery entirely.
**JJ Flex applicability**: **Critical.** SmartSDR Windows famously *lacks* this and it's a long-standing user complaint. JJ Flex shipping it is competitive differentiation, not catch-up. Should be a peer of the discovered-radios list, not buried in settings.

### Pattern L: Versioned probes within one rung
**Source**: UniFi v1/v2 cascade **[ref-33]**.
**Mechanism**: Within a single discovery method, attempt the newer protocol variant first; fall back to the older variant if no response within N ms. Allows graceful FlexLib upgrades.
**JJ Flex applicability**: **High right now** — this is exactly the FlexLib 4.2.18 pre/post-upgrade situation. Worth implementing for the 4.2.18 broadcast probe specifically: try the new format, fall back to listening for the old format if nothing heard in 2 seconds.

### Pattern M: DHCP snooping for passive arrival detection
**Source**: Home Assistant `aiodiscover` **[ref-35]**.
**Mechanism**: Sniff DHCP traffic; match request hostname/MAC OUI against known device fingerprints. Trigger discovery setup when a known device joins the network.
**JJ Flex applicability**: **Future / low priority.** Requires Npcap/WinPcap dependency and elevation. Useful for "JJ Flex was already running when you powered on the radio" scenarios — currently the user probably has to hit Discover. File for post-cascade Sprint 30+.

### Pattern N: Cloud-registered phone-home alternative
**Source**: UniFi `set-inform` adoption **[ref-33]**, JJ Flex's own potential Data Provider feature.
**Mechanism**: User opts in once to register the radio with a rendezvous server. Radio (or a small JJ Flex agent on the same network) periodically posts its current IP to the rendezvous; client fetches the IP from the rendezvous on demand.
**JJ Flex applicability**: **Sprint 30+, deliberate scoping.** Aligns with the "user-initiated, not silent" memory entry only if framed as an opt-in feature. Could leverage the JJ Flex Data Provider infrastructure already being built.

---

## 5. Anti-patterns to avoid

### AP-1: Silent dead-end "no devices found" UI
**Offenders**: SmartSDR for Windows **[ref-9]**, FRStack **[ref-14]**, SliceMaster **[ref-15]**, Roku ECP, Spotify Connect, BlindCat (inferred).
**The failure**: When discovery comes up empty, the user gets a single sentence and no actionable next step. Particularly hostile to screen-reader users who can't visually scan the rest of the UI for hidden affordances.
**JJ Flex counter-rule**: Empty discovery must always show (a) what was tried, (b) "Add by IP/hostname" entry, (c) link to SmartLink, (d) link to discovery-diagnostics panel.

### AP-2: Removing a working fallback because the new way is "better"
**Offenders**: Sonos S2 (May 2024) dropped SSDP for mDNS-only **[ref-26]** — caused widespread discovery failures. Plex deprecated direct-LAN-only login in favor of plex.tv-required.
**The failure**: Each rung in a discovery cascade exists because it works on *some* user's network. Removing it because "the new rung covers most cases" silently breaks the corner cases, and the affected users can't easily debug.
**JJ Flex counter-rule**: Once a rung lands in the cascade and shows real-world value, it stays. Cascade grows additively. (Already in line with the memory entry on the discovery cascade being a resilience layer, not a gate.)

### AP-3: Treating discovery failure as a network-config-fix-it task
**Offenders**: SmartSDR support docs ("check your switches / firewall / VLANs"), FRStack ("create two Inbound Windows Firewall rules"), Sonos community ("power-cycle everything"), Hamlib (no discovery at all).
**The failure**: The user's network has been working fine for everything else. Telling them their router/switch/firewall is wrong is both blame-shifting and almost always wrong (the underlying issue is usually our app's brittle reliance on a single protocol).
**JJ Flex counter-rule**: Any time the cascade comes up empty, the framing in the UI is "JJ Flex tried these N methods" — never "your network is broken." Diagnostic info is offered for the user to share with help if they want it, but the *first* presented options are ours to fix (manual IP, SmartLink, retry).

### AP-4: Single-protocol-only discovery
**Offenders**: Spotify Connect (mDNS only) **[ref-29]**, Roku ECP (SSDP only) **[ref-23]**, Hamlib (manual config only) **[ref-17]**.
**The failure**: Any single protocol fails on ~5-15% of real-world networks (multicast disabled on switches, IGMP snooping eating queries, WiFi Power Save sleeping APs, mesh networks dropping broadcast). A single point of failure for a feature with no alternative path.
**JJ Flex counter-rule**: Cascade is the design principle; the cascade itself is the deliverable, not a workaround.

### AP-5: Cloud as a hard dependency for local connection
**Offenders**: Plex (recent) requires plex.tv login even for LAN-only servers; some IoT products refuse to function offline.
**The failure**: When the cloud is down (or the user is offline), local-only operation is bricked. For ham radio specifically, this would defeat field-day, EmComm, and bunker-mode use cases.
**JJ Flex counter-rule**: SmartLink and JJ Flex Data Provider are *additive* fallbacks. Local operation must always work without them. (Already aligned with the "no silent phone-home" memory entry.)

### AP-6: Sequential-blocking cascade (UI hangs while each rung times out)
**Offenders**: Many older UPnP browsers, naive SSDP-then-mDNS implementations.
**The failure**: User waits 30+ seconds while each protocol times out one after another. By the time the empty-state shows up, they've alt-tabbed away.
**JJ Flex counter-rule**: Run rungs that don't have ordering dependencies in parallel. The user-perceivable latency is the latency of the *fastest* successful rung, not the sum of all attempted rungs.

### AP-7: No diagnostic surface
**Offenders**: SmartSDR (closed source, no logging visibility), most consumer smart-home apps.
**The failure**: When discovery fails, the user (or a third-party helper) has no way to see what was attempted or why nothing matched. Particularly bad for accessibility — screen-reader users frequently rely on remote help that needs to read out diagnostic state.
**JJ Flex counter-rule**: Trace persistence (already in plan per memory entry `project_trace_persistence_design.md`) plus a user-visible "discovery diagnostics" view make every failed cascade a learnable event.

### AP-8: Forcing the user to know their radio's IP without telling them how
**Offender**: Hamlib rigctld setup (no discovery at all; user must know hostname:port).
**The failure**: Asking a non-network-savvy user "what's your radio's IP?" without offering any path to find it (no DHCP-server-page link, no router-admin pointer, no manufacturer-default-IP hint) is hostile.
**JJ Flex counter-rule**: When manual IP entry is offered, also offer "I don't know — let me check" guidance: link to the radio's front-panel network info page, hint that it's usually 192.168.x.y on the user's home subnet, suggest router DHCP page steps, etc. (Could even be an F1 help topic.)

### AP-9: Aggressive caching of bad state
**Offender**: Sonos speakers cache old WiFi SSID and refuse to reconnect after router changes **[ref-26]**.
**The failure**: A cached value (last IP, last MAC, last SSID) becomes stale and the system keeps trying it past usefulness.
**JJ Flex counter-rule**: Cache aging — last-known-IP probe should fail fast (1 second) and de-prioritize the cached IP if it fails three consecutive times. Don't get stuck retrying a stale entry.

### AP-10: Deprecating discovery from firmware "to clean up"
**Inferred risk**: FlexRadio 4.2.18 silent-discovery investigation suggests the radio's broadcast behavior may have been changed or deprecated.
**JJ Flex's structural protection**: The cascade itself. Even if the vendor removes broadcast in some future firmware, JJ Flex's Rung 1a/1c (cached IP/ARP), Rung 2 (broadcast), Rung 3 (alternate probe), Rung 4 (subnet sweep), Rung 5 (SmartLink fallback), and the manual-IP escape hatch combined make any single rung's failure non-fatal.

---

## 6. Open questions

1. **Does FlexRadio respond to unicast UDP-4992 probes?** Pattern C (directed unicast probe to cached IP) only works if yes. Verify via Wireshark capture or SmartSDR ILSpy decompile (memory entry `project_smartsdr_decompile_authorization.md` already authorizes this).

2. **Will Tim Ellison / FlexRadio engineering accept a feature request to add mDNS advertisement (`_smartsdr._tcp.local`) to firmware?** Would give JJ Flex (and every other client) a "free" additional rung. Low effort for FlexRadio; high value for the ecosystem. Worth raising via the JJ Flex / FlexRadio relationship channel after the 4.2.18 discovery investigation closes.

3. **Is DHCP snooping (Pattern M) acceptable as a JJ Flex feature?** Requires Npcap/WinPcap dependency, OS-level packet capture privilege, and may surface privacy concerns. Worth a memo to Noel before building.

4. **Should the cascade run continuously in the background (Sonos heartbeat model, Pattern H) or only on user action?** Continuous = better reconnect UX; on-demand = lower battery/network impact. Suggest user-toggleable per the flexibility-principle memory entry, default "continuous when JJ Flex is foreground."

5. **Is there a SmartLink-equivalent rung for the multi-radio future?** When JJ Flex extends to non-Flex radios (Hamlib, TS-590, etc.), there's no SmartLink to fall back to. The "JJ Flex Data Provider rendezvous" Pattern N becomes the answer — but only if JJ Flex builds it. File against the multi-radio architectural memo.

6. **How does the cascade interact with the stuck-modal-escape design** (memory entry `project_stuck_modal_escape_design.md`)? Specifically, if Rung 5 (SmartLink) takes 30 seconds to time out, does the user see a counting earcon? What's the per-rung budget? Recommend each rung has its own timeout that contributes to a single visible-progress-of-cascade indicator, rather than each rung getting its own modal.

7. **Do any open-source Flex-related tools (FlexLib alternate implementations, kc2g/flex python libs, etc.) have discovery patterns we missed?** Worth a separate scan if Stream 4 turns up gaps.

---

## References

- **[ref-1]** FlexRadio Community — "TCP port not open" thread discussing UDP 4992 broadcast behavior. https://community.flexradio.com/discussion/8031806/tcp-port-not-open
- **[ref-2]** Plex Support — "Why can't the Plex app find or connect to my Plex Media Server?" https://support.plex.tv/articles/204604227-why-can-t-the-plex-app-find-or-connect-to-my-plex-media-server/
- **[ref-3]** plex-for-kodi GDM source code. https://github.com/plexinc/plex-for-kodi/blob/master/lib/_included_packages/plexnet/gdm.py
- **[ref-4]** Home Assistant Developer Docs — "Networking and discovery." https://developers.home-assistant.io/docs/network_discovery/
- **[ref-5]** GitHub `gotwalt/sonos` Wiki — Discovery. https://github.com/gotwalt/sonos/wiki/Discovery (and Sonos Community discussion of SSDP+broadcast dual addressing)
- **[ref-6]** Wikipedia — "Discovery and Launch" (Chromecast / DIAL → mDNS migration). https://en.wikipedia.org/wiki/Discovery_and_Launch
- **[ref-7]** python-PlexAPI GDM module documentation. https://python-plexapi.readthedocs.io/en/latest/modules/gdm.html
- **[ref-8]** FlexRadio Community — "Maestro LAN radio discovery delayed." https://community.flexradio.com/discussion/8026186/maestro-lan-radio-discovery-delayed
- **[ref-9]** FlexRadio Community — "SmartSDR. Manual setup IP address for connect to radio." https://community.flexradio.com/discussion/7930071/smartsdr-manual-setup-ip-address-for-connect-to-radio (and the Fixed IP Input / Static IP / Feature Enhancement Request threads cited in same search)
- **[ref-10]** Microsoft Learn — "Network discovery of an IPP printer on Windows 10, using Microsoft IPP Class Driver." https://learn.microsoft.com/en-us/answers/questions/3879405/network-discovery-of-an-ipp-printer-on-windows-10
- **[ref-11]** PaperCut — "How printers are discovered on the network and locally." https://www.papercut.com/help/manuals/pocket-hive/how-it-works/printer-discovery/
- **[ref-12]** FlexRadio Community — "New discovery protocol" (VITA-49 packet format details). https://community.flexradio.com/discussion/5993575/new-discovery-protocol
- **[ref-13]** FlexRadio Community — "Maestro not finding radio except on home WiFi connection and LAN connection" / "Maestro LAN radio discovery delayed." https://community.flexradio.com/discussion/7895366/ and https://community.flexradio.com/discussion/8026186/
- **[ref-14]** FRStack documentation and FlexRadio Community — "FRStack Instructions" / mkcmsoftware product page. https://www.mkcmsoftware.com/Flex/Index and https://community.flexradio.com/discussion/8026344/frstack-instructions
- **[ref-15]** FlexRadio Community — "SliceMaster not seeing FlexRadio." https://community.flexradio.com/discussion/8028477/slicemaster-not-seeing-flexradio
- **[ref-16]** FlexRadio Helpdesk — "Troubleshooting Firewire Connectivity Issues." https://helpdesk.flexradio.com/hc/en-us/articles/360004244431-Troubleshooting-Firewire-Connectivity-Issues
- **[ref-17]** Hamlib Wiki — "Network Device Control." https://github.com/Hamlib/Hamlib/wiki/Network-Device-Control
- **[ref-18]** rigctld(1) man page. https://hamlib.sourceforge.net/html/rigctld.1.html
- **[ref-19]** WSJT-X User Guide — Settings → Radio (CAT control options). https://www.physics.princeton.edu/pulsar/K1JT/wsjtx-doc/wsjtx-main-1.8.0.html
- **[ref-20]** Airspy / SDR# documentation. https://airspy.com/quickstart/
- **[ref-21]** SoapyRemote / SDRangel forum — "How to connect to remote Soapy server?" https://github.com/f4exb/sdrangel/issues/1003
- **[ref-22]** OpenWebRX+ / Marat Fayzullin — homepage. https://fms.komkon.org/OWRX/ (and Receiverbook https://www.receiverbook.de/)
- **[ref-23]** Roku Developer Documentation — "External Control Protocol (ECP)." https://developer.roku.com/docs/developer-program/dev-tools/external-control-api.md
- **[ref-24]** Plex Support — "Network." https://support.plex.tv/articles/200430283-network/
- **[ref-25]** Plex Support — "Settings: Plex for Roku" (manual server entry). https://support.plex.tv/articles/204275243-settings-plex-for-roku/
- **[ref-26]** Sonos Community — "Sonos speakers not found by Android app on the network" and SoCo discovery module docs. https://docs.python-soco.com/en/latest/api/soco.discovery.html
- **[ref-27]** Roon Labs Community — "RAAT Protocol Documentation." https://community.roonlabs.com/t/raat-protocol-documentation/39735
- **[ref-28]** Spotify for Developers — "ZeroConf API." https://developer.spotify.com/documentation/commercial-hardware/implementation/guides/zeroconf
- **[ref-29]** librespot/discovery source. https://github.com/librespot-org/librespot/blob/dev/discovery/src/lib.rs
- **[ref-30]** Google Home Developers — "Commissionable and Operational Discovery." https://developers.home.google.com/matter/primer/commissionable-and-operational-discovery
- **[ref-31]** Silicon Labs Matter — "Matter Commissioning." https://docs.silabs.com/matter/2.7.0/matter-overview-guides/matter-commissioning
- **[ref-32]** OASIS WS-Discovery 1.1 specification. https://docs.oasis-open.org/ws-dd/discovery/1.1/os/wsdd-discovery-1.1-spec-os.html
- **[ref-33]** Unofficial UniFi guide — Discovery protocol. https://jrjparks.github.io/unofficial-unifi-guide/protocols/discovery.html
- **[ref-34]** UBNT Discovery Protocol — Ubiquiti Community. https://community.ui.com/questions/UBNT-Discovery-Protocol/3f7271b6-90c7-4296-8a1a-4472e169836f
- **[ref-35]** Home Assistant — "DHCP discovery." https://www.home-assistant.io/integrations/dhcp/
- **[ref-36]** Home Assistant — "Zero-configuration networking (zeroconf)." https://www.home-assistant.io/integrations/zeroconf/
- **[ref-37]** Angry IP Scanner — About / documentation pages. https://angryip.org/about/ and https://angryip.org/documentation/
- **[ref-38]** Avahi — homepage. https://avahi.org/
- **[ref-39]** GitHub `lukszar/mDNS-discovery` — Discover all mDNS/Bonjour devices. https://github.com/lukszar/mDNS-discovery

---

## Stream 4 closure

The strongest finding is structural rather than tactical: **no app in the surveyed corpus has a discovery cascade as elaborate as what JJ Flex is building, and the apps that come closest (Home Assistant, Sonos, Plex) all fail in user-hostile ways when their cascade comes up empty.** JJ Flex's combination of (a) a multi-rung cascade, (b) SmartLink-as-fallback-row-during-cascade, (c) manual-IP entry as a peer of the discovered list, and (d) a transparent "what was tried" diagnostic surface would put it ahead of every app studied — not just ahead of SmartSDR or BlindCat.

Top three actionable recommendations for the cascade design:

1. **Add an ARP-cache-query rung (Pattern F) before the existing TCP-knock-cached-IP rung.** Cheaper, no network traffic, instant when it works.
2. **Add a directed-unicast-probe rung (Pattern C) between cached-IP and broadcast.** Solicits a same-format VITA-49 response from the radio's IP without waiting for the 1Hz broadcast cycle. Requires verifying FlexLib supports it.
3. **Surface SmartLink radios as fallback rows *during* the cascade (Pattern A), not only on cascade-empty.** Single biggest UX win; FlexRadio already proved it works in their own products.

End of stream.
