---
title: Discovery Cascade v3 — Stream 1: Windows OS-level passive-read primitives
date: 2026-05-06
stream: 1 of 6
research_question: "Catalog every Windows API and OS-level mechanism that, on a stock Windows 10/11 machine with no admin elevation, can yield IP addresses of devices the machine has seen on its LAN — without sending any new network traffic."
deliverable_type: research-only (markdown only, no code)
related:
  - docs/planning/design/discovery-fallback-chain.md (round 2 design)
  - memory/project_flexlib_4218_discovery_investigation.md (R6 chain ships 2026-05-05)
  - memory/project_friction_tax_principle.md (the user pays no friction; the dev pays maximal friction)
---

# Stream 1 — Windows OS-level passive-read primitives

## Executive summary

- **Windows maintains at least 12 distinct caches/tables that contain IP addresses of devices the machine has recently seen on its LAN.** Every one of them can be read without admin elevation. None of them require sending any new network traffic.
- **Three of them are load-bearing and cheap:** the IPv4/IPv6 neighbor cache (`GetIpNetTable2`), the routing table (`GetIpForwardTable2`) for default-gateway extraction, and the local adapter address list (`GetAdaptersAddresses`) for subnet derivation. These three are the foundation of every other rung in the cascade and should be built before any other passive-read source.
- **Three are high-value forward-compatibility plays:** the DNS resolver cache (`DnsGetCacheDataTable`, undocumented), the WSD/SSDP Function Discovery providers, and the NetBIOS name cache. Flex doesn't currently advertise via mDNS/SSDP/NetBIOS, but reading existing OS state for free and forward-watching for future Flex firmware updates is a cheap insurance policy.
- **Two are speculative-cheap:** the Network List Manager (`INetworkListManager`) for "have we been on this network before" gating, and `GetExtendedTcpTable` for "has any process on this machine ever talked to a Flex on TCP/4992 lately" — useful if the user just had SmartSDR open and we restart JJ Flex.
- **One is the surprise winner: the `autoConnectV2.xml` and FlexLib settings folder approach generalizes.** SmartSDR also caches the radio's IP in known disk locations. Reading SmartSDR's cache files (with user consent) is a passive disk-read that finds a Flex IP on every machine where SmartSDR has ever connected — even if Windows network state has been wiped. Already covered by Rung 1a in design but worth flagging that we can extend it to read SmartSDR's analogous file for users migrating from SmartSDR.
- **The methods that don't exist:** there's no public Windows API to enumerate the LLMNR cache. There's no public API to enumerate the mDNS resolver cache directly. WLAN BSSID neighbor data is gated behind location permission since Windows 10 1803.

## The reading frame: passive vs. active

Throughout this document, "passive read" means: the API reads state already in OS memory or on disk. It sends zero new network packets. Calling it does not cause Windows to issue any DNS query, ARP request, NDP solicitation, multicast, broadcast, or unicast packet. The information is whatever Windows has already accumulated through normal background operation.

This matters because the discovery cascade's whole point is to enumerate hints fast and silently. A passive-read rung that takes 1ms and returns "I have nothing" is strictly better than the same rung taking 100ms because it issued a probe — the silent fail is what makes the cascade composable with parallel rungs that DO probe.

A few methods listed below have ambiguity (e.g., `Dns.GetHostEntry` looks like a forward DNS lookup but the Windows resolver checks the cache first and only issues a packet on miss). Those are flagged explicitly.

---

## Method-by-method findings

### 1. ARP / IPv4 neighbor cache — `GetIpNetTable` / `GetIpNetTable2`

**Returns.** A table of `MIB_IPNET_ROW2` entries (one per known IPv4 or IPv6 neighbor on each interface), with these key fields:

- `Address` (`SOCKADDR_INET`) — the IP address of the neighbor
- `InterfaceLuid` / `InterfaceIndex` — which local adapter saw it
- `PhysicalAddress` (16 bytes) + `PhysicalAddressLength` — the MAC
- `State` — `NlnsUnreachable`, `NlnsIncomplete`, `NlnsProbe`, `NlnsDelay`, `NlnsStale`, `NlnsReachable`, `NlnsPermanent`
- `IsRouter`, `IsUnreachable`, `ReachabilityTime` — extra flags + recent reachability info

The older `GetIpNetTable` returns IPv4-only `MIB_IPNETROW` with `dwIndex`, `dwPhysAddrLen`, `bPhysAddr[8]`, `dwAddr`, `dwType` (`MIB_IPNET_TYPE_OTHER`/`DYNAMIC`/`STATIC`/`INVALID`). For a JJ Flex client, prefer the v2 API — it gives state, IPv6 coverage, and reachability hints in one call.

**.NET 10 access.** No managed wrapper in BCL. Two paths:

1. P/Invoke directly into `iphlpapi.dll` (`GetIpNetTable2(family, out PMIB_IPNET_TABLE2)` / `FreeMibTable(table)`). Vanara.PInvoke.IpHlpApi NuGet package has the bindings already if we want to skip writing them ourselves. Vanara is MIT-licensed and well-maintained.
2. WMI via `MSFT_NetNeighbor` in `ROOT\StandardCimv2` namespace (the underlying source for `Get-NetNeighbor` PowerShell cmdlet). Higher latency (CIM session setup) but no marshaling code.

**Latency.** Single-digit milliseconds for a P/Invoke call on a typical home subnet (10s-100s of entries). WMI version is 50-200ms because of CIM session setup.

**Admin required.** No. Read access to neighbor cache is unprivileged. Only `SetIpNetEntry2` (modifying the table) requires Administrators group membership.

**False positives.** Yes, three flavors:

- **Stale entries.** `NlnsStale` state means Windows hasn't confirmed reachability recently. A radio that was on `192.168.1.42` yesterday but is off today still appears.
- **Unsolicited entries.** Any device that has sent ANY packet to or through this machine in the recent past leaves an ARP entry. Phones, smart TVs, IoT devices, the router itself. Filtering by MAC OUI prefix (FlexRadio's IEEE-assigned prefix — Stream-2 should confirm exact bytes) reduces noise dramatically.
- **Address re-use.** An IP that ARP says is at MAC X may actually now be at MAC Y if DHCP rotated the lease since the ARP entry was cached.

**Cache lifetime.** Windows ages ARP entries based on the `BaseReachableTime` interface parameter, default ~30 seconds for `Reachable` state, ~30 seconds for `Stale` before deletion, but entries in `Permanent` or `Static` state persist indefinitely. In practice, on a home LAN where the radio chats with the gateway every few seconds, the radio's ARP entry stays present continuously.

**Conditions for Flex to appear.** The Flex radio's ARP entry exists if ANY device on the local segment (including the JJ Flex machine) has communicated with the radio's IP recently — which means: anytime the radio has been powered on and SmartSDR/JJ Flex has opened a socket, OR the gateway has periodically polled it, OR the radio has emitted its UDP discovery beacon (which our affected tester case still does at the wire — only the Windows-side receive path is broken). **In Don's specific case, the radio's IP almost certainly IS in his ARP table, because the radio is on, broadcasting, and his gateway is talking to it; the Windows-side discovery silence doesn't suppress ARP cache population.** This is a strong candidate for Rung 1.5.

### 2. IPv6 neighbor cache — same `GetIpNetTable2` with `AF_INET6`

Same API as above, just pass `AF_INET6` (or `AF_UNSPEC` to get both in one call). Returns NDP (Neighbor Discovery Protocol) entries — the IPv6 equivalent of ARP.

**Why this matters for Flex.** Flex 6000 firmware historically has been IPv4-focused but newer 4.2.x firmware reportedly enables IPv6 link-local at minimum. Even if our connection layer is IPv4-only (which it is in current FlexLib), the radio appearing in the IPv6 NDP cache is a confirmation it's alive on the segment. That's an additional signal we can correlate with the IPv4 ARP table to increase confidence.

**False positives.** IPv6 link-local addresses (`fe80::/10`) are nearly always present for any active network device. Filtering by MAC OUI is essential here.

### 3. TCP connection table — `GetExtendedTcpTable` / `GetTcpTable2`

**Returns.** All IPv4 (or IPv6) TCP endpoints known to the local TCP/IP stack. With `TCP_TABLE_OWNER_PID_ALL`, each `MIB_TCPROW_OWNER_PID` entry contains:

- `dwState` — `LISTEN`, `SYN_SENT`, `SYN_RCVD`, `ESTABLISHED`, `FIN_WAIT1`, `FIN_WAIT2`, `CLOSE_WAIT`, `CLOSING`, `LAST_ACK`, `TIME_WAIT`, `DELETE_TCB`, `CLOSED`
- `dwLocalAddr`, `dwLocalPort` — local endpoint
- `dwRemoteAddr`, `dwRemotePort` — remote endpoint
- `dwOwningPid` — the process that owns the socket

**.NET 10 access.** Either P/Invoke `iphlpapi.dll` directly, or use the BCL's `IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()` — but the BCL wrapper does NOT include PID information. For PID, P/Invoke is required. Vanara has bindings.

**Latency.** Single-digit milliseconds.

**Admin required.** No.

**False positives.** Almost none for our use case — if there's an `ESTABLISHED` or recent `TIME_WAIT` entry to remote port 4992 or 4993 on a local subnet IP, it almost certainly is or was a Flex.

**Conditions for Flex to appear.** Whenever ANY process on this machine has recently held an open socket to a Flex (port 4992 TCP), the connection appears in the table. `ESTABLISHED` while it's open, `TIME_WAIT` for ~2 minutes after close (Windows default `TcpTimedWaitDelay` is 120 seconds, configurable). **This is a powerful signal for the case where the user just closed SmartSDR and is opening JJ Flex** — SmartSDR's recent connection to the radio shows up here even though SmartSDR is no longer running.

**Surprise value.** This is one of the less-obvious rung candidates and arguably the most powerful one for the SmartSDR-migration scenario. If a user has SmartSDR running with their radio connected and they launch JJ Flex side-by-side, the radio's IP is sitting in `ESTABLISHED` state for SmartSDR's process. JJ Flex can read it instantly.

### 4. UDP listener/endpoint table — `GetExtendedUdpTable`

**Returns.** All UDP endpoints (which is to say, sockets bound to a UDP port — UDP has no "connection" so there's no remote endpoint in the table). With `UDP_TABLE_OWNER_PID`:

- `dwLocalAddr`, `dwLocalPort` — bound local endpoint
- `dwOwningPid` — owning process

**.NET 10 access.** P/Invoke `iphlpapi.dll`. Same Vanara bindings as TCP.

**Latency.** Single-digit milliseconds.

**Admin required.** No.

**Value for our use case.** **LIMITED.** UDP has no remote-endpoint info, so this table can tell us "some process is listening on UDP/4992" but not "we received UDP discovery beacons from `192.168.1.42`." Useful for diagnosing OUR OWN UDP discovery receiver state (is JJ Flex's UDP listener actually bound?) but not for finding the radio.

**Skip for cascade purposes.** Diagnostic-only utility, not a discovery rung.

### 5. DNS resolver cache — `DnsGetCacheDataTable` (undocumented)

**Returns.** All entries currently in the Windows DNS resolver cache. Each entry includes:

- The queried name (e.g., `myradio.local`)
- The record type (A, AAAA, PTR, etc.)
- TTL remaining
- The associated data (for A/AAAA: an IP address; for PTR: a name)

The function is an undocumented entry point in `dnsapi.dll`. It's the same function that `ipconfig /displaydns` uses. Microsoft has never documented it because it's an internal implementation detail of the DNS Client service, but it's been stable across Windows versions from XP through Windows 11.

**.NET 10 access.** Two paths:

1. `LoadLibrary` + `GetProcAddress` to dynamically resolve `DnsGetCacheDataTable` from `dnsapi.dll`, then unmarshal a singly-linked list of `DNS_CACHE_ENTRY` structures. Reference implementations: malcomvetter/DnsCache, FRex/muhdnscache (both on GitHub).
2. WMI `Win32_DNSClientCache` — official, documented, but slower (~50-200ms) and queries the same underlying source.
3. Shell out to `ipconfig /displaydns` and parse stdout — fragile, locale-sensitive, but zero P/Invoke.

For JJ Flex, the WMI path is preferred unless we need sub-50ms latency. The undocumented path is a fine fallback.

**Latency.** WMI: 50-200ms. P/Invoke: <10ms.

**Admin required.** No, for read access. (Flushing the cache via `DnsFlushResolverCache` requires admin.)

**False positives.** Stale entries within their TTL (typically 5-60 minutes for forward records). DNS poisoning would obviously corrupt this, but in a friendly LAN that's a non-issue.

**Conditions for Flex to appear.** This is the FORWARD-COMPATIBILITY rung. Today Flex doesn't register itself with mDNS/Bonjour, so its hostname doesn't go into the DNS cache from the local network. BUT:

- Some users put their radio in their `hosts` file or their router's local DNS. If they ever resolved `myradio.local` or `flex6300.lan`, the entry is cached.
- If Flex EVER adds mDNS announcement in a future firmware release (and they should — it's the standard), Windows 10/11's built-in mDNS resolver will populate the DNS cache transparently. Our cascade would catch this for free without code changes.
- SmartSDR may have made a hostname-based connection at some point (checking SmartSDR's logic is Stream 2's job).

**Recommendation.** Build it. Cheap, future-proof, free upside.

### 6. Routing table — `GetIpForwardTable` / `GetIpForwardTable2`

**Returns.** All IPv4 (or IPv6) routes on the system. `MIB_IPFORWARD_ROW2`:

- `DestinationPrefix` — destination network + prefix length
- `NextHop` — next-hop IP
- `InterfaceLuid` / `InterfaceIndex` — outgoing interface
- `Metric`, `Protocol`, `Origin`, `Age`, `ValidLifetime`, `PreferredLifetime`

**.NET 10 access.** P/Invoke. Vanara has bindings.

**Latency.** <10ms.

**Admin required.** No.

**Value for our use case.** **LOAD-BEARING for Rung 2 (subnet probe).** We need to know the user's local subnet to walk it. The routing table gives us the default gateway IP, from which we derive the subnet (combined with the adapter's address+mask from `GetAdaptersAddresses`). Without this, Rung 2 has to make assumptions about subnet layout.

**Conditions for Flex to appear.** Doesn't, directly. The radio doesn't have routing-table presence on its own. But the routing table tells us WHERE to look in the other tables — it scopes the subnet probe and validates which IPs from ARP are "on a segment we can reach directly."

### 7. Network adapter list — `GetAdaptersAddresses` / `NetworkInterface.GetAllNetworkInterfaces()`

**Returns.** Per-adapter info: name, friendly name, MAC, MTU, flags, list of bound unicast/multicast/anycast addresses, list of DNS servers, list of gateway addresses, list of DHCP servers. `IP_ADAPTER_ADDRESSES` chain.

**.NET 10 access.** **Managed BCL covers this fully.** `System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()` returns `NetworkInterface[]`, and each adapter's `GetIPProperties()` exposes everything we need (`UnicastAddresses`, `GatewayAddresses`, `DhcpServerAddresses`, `DnsAddresses`).

**Latency.** <10ms.

**Admin required.** No.

**Value for our use case.** **LOAD-BEARING.** This tells us:

- What subnets the local machine is on (so we know what subnets the radio could be on)
- The default gateway IP per interface (which is itself in the ARP table, so we can validate ARP-read freshness)
- The DHCP server IP (a candidate to query later if we ever want to ask the DHCP server "what's on the LAN")

**Conditions for Flex to appear.** Never directly. This is scope-derivation, not discovery.

### 8. Network List Manager / Windows Connect Manager

**Returns.** `INetworkListManager` COM interface exposes:

- `GetNetworks(NLM_ENUM_NETWORK_ALL)` — all networks the machine has ever been on
- For each `INetwork`: `GetName()`, `GetDescription()`, `GetNetworkId()` (a GUID), `GetCategory()` (Public/Private/Domain), `GetConnectivity()` (`NLM_CONNECTIVITY` flags including `IPV4_LOCALNETWORK`, `IPV4_INTERNET`, `IPV6_LOCALNETWORK`, `IPV6_INTERNET`), `GetTimeCreatedAndConnected()` (when did we first see and last connect to this network)
- `GetNetworkConnections()` — currently active connections
- For each `INetworkConnection`: associated `INetwork` and `INetworkAdapter`

**.NET 10 access.** COM interop. Either:

1. Add a COM reference to `NetworkListManager 1.0 Type Library` and use generated interop assemblies
2. Use the `Windows API Code Pack` style or community wrappers (e.g., `Joppe-T/NetworkListManager` on GitHub)
3. Roll our own `[ComImport, Guid(...)]` interfaces

**Latency.** ~100ms first call (COM activation), <20ms subsequent.

**Admin required.** No.

**Value for our use case.** **GATING / CONFIDENCE BOOSTER.** This API tells us: "are we currently on the same network where we cached this radio's IP?" — by GUID match. If the user moved their laptop to a coffee shop, the cached `192.168.1.42` is meaningless; NLM tells us we're on a different network (different GUID) and we should skip Rung 1a. If we're on the SAME network as last time (same GUID), Rung 1a's success probability is much higher.

**Surprise value.** This is the right way to scope cached-IP rungs. Without NLM gating, Rung 1a wastes time probing `192.168.1.42` when the user is on a coffee-shop hotspot in the `10.0.0.0/24` range. With NLM gating, Rung 1a is a no-op in mismatched-network cases.

**Recommendation.** Build it as a guard for Rungs 1a and 1b. Doesn't replace any rung; it makes the existing rungs smarter.

### 9. NetBIOS name cache — `nbtstat -c` equivalent / NetBIOS Helper API

**Returns.** Cached NetBIOS-name-to-IP mappings the local machine has resolved recently. Each entry:

- NetBIOS name (16 bytes, last byte is the type — `<00>` = workstation, `<20>` = file server, etc.)
- Type
- Cached IP address
- TTL remaining

**.NET 10 access.** No clean managed API. Three options:

1. Shell out to `nbtstat -c` and parse stdout (locale-stable, always works on Windows)
2. P/Invoke `Netbios()` from `netapi32.dll` — old, awkward NCB-block-based API
3. WMI does not expose NetBIOS name cache directly

**Latency.** Shell-out: ~100-300ms (process start cost). P/Invoke: <10ms.

**Admin required.** No.

**Value for our use case.** **MARGINAL.** Flex doesn't register a NetBIOS name. Wouldn't be in the cache from any source other than someone manually configured. Build only as a "complete the survey" data point in the diagnostic capture rung; not worth a dedicated discovery rung.

### 10. LLMNR resolver cache

**Returns.** Per official documentation (RFC 4795), LLMNR maintains a per-interface isolated cache.

**.NET 10 access.** **NONE.** Microsoft has not exposed any public API to enumerate LLMNR cache entries. `ipconfig /displaydns` does not show LLMNR cache. There's no WMI class. There's no event log. The cache is private to the DNS Client service.

**Recommendation.** **Skip.** No accessible passive read path exists. If LLMNR resolution ever happens for a Flex-related name, the resolved IP would surface in the regular DNS resolver cache (Method 5) anyway, so we get partial coverage there.

### 11. mDNS resolver cache (Windows 10 1703+)

**Returns.** Same DNS resolver cache infrastructure as Method 5. Windows 10 1703 and later integrated mDNS resolution into the DNS Client service. mDNS responses populate the same cache that `DnsGetCacheDataTable` returns.

**.NET 10 access.** Same as Method 5.

**Value for our use case.** **Already covered by Method 5.** No separate API to call.

**Active mDNS browse** (which DOES send packets) — `DnsServiceBrowse` from `windns.h`, with `DNS_SERVICE_BROWSE_REQUEST` and a callback. This is OUT OF SCOPE for Stream 1 because it sends new traffic, but Stream 3 or 4 should evaluate it as an active rung.

### 12. Function Discovery providers — `IFunctionDiscovery` (UPnP/SSDP/WSD/PnP)

**Returns.** Unified enumeration over multiple device discovery providers:

- **PnP provider** — devices on the local PCI/USB/etc. bus
- **SSDP provider** — UPnP devices that have announced via SSDP (passive: reads internal SSDP cache; active: triggers SSDP M-SEARCH)
- **WSD provider** — Web Services on Devices (DPWS), used for printers, cameras, NAS
- **Registry provider** — manually registered devices

For each device: a property store with `PKEY_*` keys (model, manufacturer, network address, serial number, etc.).

**.NET 10 access.** COM interop via `IFunctionDiscovery` and `IFunctionInstance`. No managed wrapper in BCL. Examples: Microsoft's "Function Discovery Provider Sample" (older Win32 sample), Vanara may have partial bindings.

**Latency.** Initial enumeration: 100-500ms (depends on which providers are queried). PnP-only: <50ms.

**Admin required.** No.

**Passive vs. active.** The SSDP and WSD providers internally maintain caches of devices that have announced themselves via multicast in the recent past. Querying the providers reads those caches AND optionally triggers a fresh search (the `IFunctionDiscovery::CreateInstanceCollectionQuery` with `fSubcategoryOfPnPClass = false` and `fIncludeSubcategoryClasses` flags control this). For Stream 1 (passive only), we want the cache-read mode without triggering a search.

**Value for our use case.** **FORWARD-COMPATIBILITY.** Flex doesn't currently advertise via SSDP, WSD, or DPWS. But:

- Windows' "Network and Sharing" UI uses Function Discovery to populate "Other Devices on this network" — if Flex EVER adds WSD or SSDP support, the radio becomes visible to Windows immediately and Function Discovery is how we'd see it.
- Reading existing Function Discovery state is free (we get printers, NAS, smart TVs, etc.) — we filter to the categories we care about.

**Recommendation.** Build the read with the SSDP and WSD providers in cache-only mode. Free, future-proof, surfaces the radio on day-one of any future Flex firmware that adds standards-based discovery.

### 13. UPnP `IUPnPDeviceFinder` (COM)

**Returns.** Direct UPnP device discovery interface. `FindByType(deviceType, flags)` returns an `IUPnPDevices` collection. Each `IUPnPDevice` has `UniqueDeviceName`, `FriendlyName`, `ModelName`, `ManufacturerName`, `Children`, `Services`, etc.

**.NET 10 access.** COM interop. `Type.GetTypeFromProgID("UPnP.UPnPDeviceFinder")`, `Activator.CreateInstance(t)`, then `dynamic` or direct dispatch.

**Latency.** Synchronous `FindByType` triggers an SSDP M-SEARCH and waits for responses (~3 seconds). Asynchronous variant via `IUPnPDeviceFinderCallback` is preferable.

**Passive vs. active.** Synchronous mode is ACTIVE (sends M-SEARCH multicasts). Asynchronous mode initially returns cached responses then continues to receive new ones — the first callback batch is effectively passive.

**Recommendation.** Skip for Stream 1. Function Discovery (Method 12) is the higher-level wrapper that includes UPnP via SSDP and gives us cache-read mode more cleanly. Keep `IUPnPDeviceFinder` in mind for an active rung in Stream 3/4.

### 14. `hosts` file

**Returns.** Static name-to-IP mappings. Plain text at `%SystemRoot%\System32\drivers\etc\hosts`.

**.NET 10 access.** `File.ReadAllLines(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts"))`. Parse each non-comment line as `<IP> <hostname>`.

**Latency.** <5ms.

**Admin required.** **READ: no.** Write requires admin.

**Conditions for Flex to appear.** The user manually added an entry like `192.168.1.42 myflex` or similar. Some power users do this. Free signal worth grabbing.

**Recommendation.** Build it. Trivially cheap. We scan for any line whose IP is in a private RFC1918 range and on a subnet matching our local interfaces.

### 15. WinHTTP / WinINET proxy bypass list

**Returns.** Two registry locations:

- `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\Connections\WinHttpSettings` (machine-level, WinHTTP)
- `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\ProxyOverride` (user-level, WinINET/Edge/Chrome) — semicolon-separated string

**.NET 10 access.** `Microsoft.Win32.Registry.LocalMachine.OpenSubKey(...).GetValue(...)` and same for `CurrentUser`. WinHTTP value is a binary blob with `WINHTTP_PROXY_INFO` layout; ProxyOverride is a simple string.

**Latency.** <5ms.

**Admin required.** No (read).

**Value for our use case.** **LOW.** Bypass lists rarely contain LAN device IPs. They typically contain `*.corp.local`, `127.0.0.1`, `<local>` macro. A Flex IP would only show up if the user explicitly added it for browser traffic, which makes no sense for a radio.

**Recommendation.** Skip. Diagnostic-only.

### 16. Windows registry: `NetworkList`

**Returns.** Profile metadata for every network the machine has ever been on. Located at `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList`:

- `Profiles\<GUID>` — `ProfileName` (SSID for WiFi, otherwise descriptive name), `Description`, `DateCreated`, `DateLastConnected`, `Category`, `Managed`, `NameType`
- `Signatures\Managed\<hash>` — joined-network metadata: `ProfileGuid`, `Description`, `DefaultGatewayMac`, `DnsSuffix`, `FirstNetwork`, `Source`
- `Signatures\Unmanaged\<hash>` — same shape, for unjoined ones

**.NET 10 access.** `Microsoft.Win32.Registry`.

**Latency.** <10ms.

**Admin required.** No (read of HKLM keys is unprivileged).

**Value for our use case.** **CONFIDENCE / GATING.** Cross-references with NLM (Method 8). The `DefaultGatewayMac` field is interesting — combined with the NLM network GUID, we can identify "is this the same router we connected through last time we talked to the radio?" For cached-IP rungs, this answers "is the network topology the same?" with high confidence.

**Recommendation.** Build it. Adds a confidence dimension to cached-IP rungs without doing any network work.

### 17. DHCP client lease info

**Returns.** Per-adapter DHCP lease state stored at `HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\<AdapterGUID>`:

- `DhcpIPAddress`, `DhcpSubnetMask`, `DhcpServer` (the DHCP server IP), `DhcpDefaultGateway`, `DhcpNameServer`, `DhcpDomain`
- `Lease`, `LeaseObtainedTime`, `LeaseTerminatesTime`, `T1`, `T2`

**.NET 10 access.** `Microsoft.Win32.Registry` for the cached values, OR `DhcpRequestParams` from `dhcpcsvc.dll` for live values (which also reads from the local DHCP client cache without contacting the server). Vanara has `Vanara.PInvoke.Dhcp` bindings.

**Latency.** <10ms.

**Admin required.** No (read).

**Value for our use case.** **MEDIUM.** The DHCP server IP itself is a candidate for active probing later (Stream 3+) — a properly configured DHCP server might have a way to query its lease table. But more usefully: combined with the routing table, this confirms the local subnet and the DHCP server identity, both useful for NLM-gated Rung 1a confidence.

**Recommendation.** Build minimally — just read `DhcpServer` and `DhcpIPAddress` for context. Don't attempt to query the DHCP server in Stream 1 (that would be active).

### 18. Windows event logs — DNS Client / TCP-IP / NCSI

**Returns.** Per-event records. Most relevant logs:

- `Microsoft-Windows-DNS-Client/Operational` — every DNS query the system made (event IDs 3006, 3008, 3010, 3018). DISABLED BY DEFAULT.
- `Microsoft-Windows-NetworkProfile/Operational` — network connection events including IP details
- `Microsoft-Windows-NCSI/Operational` — Network Connectivity Status Indicator events
- `Microsoft-Windows-Dhcp-Client/Admin` and `/Operational` — DHCP lease events including the server IP

**.NET 10 access.** `System.Diagnostics.Eventing.Reader.EventLogReader` with an `EventLogQuery`. Or `Get-WinEvent` PowerShell.

**Latency.** Highly variable. Tens of ms to seconds depending on query, log size, filters.

**Admin required.** Reading the Operational logs typically requires Local Service or higher; some Operational logs are readable as a regular user on Windows 10/11 home, but DNS-Client/Operational specifically requires admin to read AND admin to enable. Mixed bag.

**Value for our use case.** **LOW for default config; valuable in diagnostic capture.** DNS-Client/Operational is disabled by default, so we won't have a history of DNS queries for the radio's hostname. NetworkProfile/Operational may show "you joined network X with IP Y on date Z" — useful for forensic-style "when did this user last see this radio's subnet" but not for live discovery.

**Recommendation.** Skip for Stream 1's discovery rungs. Mention as a candidate for the diagnostic-capture rung (Phase 6 of the design doc) where we report "we tried to find the radio and here's what we know about your network history."

### 19. SmartSDR / FlexLib settings caches on disk (cross-app cache reading)

**Returns.** SmartSDR (the vendor's own client) caches the radio's IP in its settings folder. Path approximately `%LOCALAPPDATA%\FlexRadio Systems\SmartSDR-Win\` — the exact filename and format are vendor-private. JJ Flex's own `autoConnectV2.xml` file is the equivalent.

**.NET 10 access.** Plain file read. No API.

**Latency.** <5ms.

**Admin required.** No (current user's local AppData).

**Value for our use case.** **HIGH for migration scenarios.** Users coming from SmartSDR have the radio's IP cached in SmartSDR's files. Reading them on first launch (with one-time user consent for the cross-app read) gives JJ Flex an instant working IP without Rung 2's subnet probe.

**Privacy/consent note.** Reading another app's settings folder without user consent is a friction-tax-principle gray area. Recommend: add a one-time prompt during JJ Flex first-run: "We noticed SmartSDR is installed. May we read your radio's saved IP from SmartSDR's settings to skip discovery?" Default: yes-please. The user can decline and we fall back to other rungs.

**Recommendation.** Build it. Stream 2 (FlexLib internals) should locate the exact SmartSDR cache file format — that's their job, not Stream 1's.

### 20. Network share / SMB connection cache — `WNetEnumResource` / `NetUseEnum`

**Returns.** Cached SMB / network drive connections.

**.NET 10 access.** P/Invoke `mpr.dll` or `netapi32.dll`.

**Value for our use case.** **NONE.** Flex doesn't expose SMB shares.

**Recommendation.** Skip.

### 21. WLAN-related APIs (`WlanGetAvailableNetworkList`, `WlanQueryInterface` BSSID neighbor)

**Returns.** Available WiFi networks and their BSSIDs.

**.NET 10 access.** Native WiFi API (`wlanapi.dll`).

**Admin required.** Since Windows 10 1803, **access to BSSIDs is gated behind location permission.** Without the user granting Location access to JJ Flex, we get back results without BSSIDs.

**Value for our use case.** **NONE for IP discovery.** WiFi APIs deal with WiFi link-layer info, not IPs of connected devices. If the radio is on Ethernet, this API knows nothing about it. If the radio is on WiFi, it still tells us about WiFi networks, not about the radio's IP.

**Recommendation.** Skip. Adds a permission prompt for zero discovery value.

### 22. `NetServerEnum` — Computer Browser cached server list

**Returns.** Cached list of "servers" (Windows machines advertising via the Computer Browser service) the local machine has seen on the network. Filterable by `SV_TYPE_*` flags.

**.NET 10 access.** P/Invoke `netapi32.dll`. Or just shell out to `net view`.

**Value for our use case.** **NONE.** Computer Browser is for Windows machines advertising file/print sharing. Flex doesn't participate. Also, the Computer Browser service is essentially deprecated and disabled by default on Windows 10/11.

**Recommendation.** Skip.

### 23. Reverse DNS via `Dns.GetHostEntry(IPAddress)`

**Note: NOT a passive read in the strict sense.** `Dns.GetHostEntry` issues a PTR query if the address isn't in the cache. Cache hit is passive, cache miss is active.

**Use case for cascade:** Given an IP from ARP or another source, asking the resolver "what name maps to this?" can confirm a Flex if the user has added a PTR record on their LAN — extremely uncommon, low value.

**Recommendation.** Skip as a passive-read source. Stream 3+ may want to consider it for active confirmation.

---

## Recommendations for cascade rung inclusion

### Build now (Phase 1.5 — passive-read rung set)

These are the load-bearing passive reads. They form the input to all subsequent active rungs (Rung 2 subnet probe, Rung 3 UDP discovery). Build them as a single composite "OS state harvester" that produces a candidate-IP set with confidence scores:

1. **`GetIpNetTable2` (ARP / IPv4+IPv6 neighbor cache)** — primary signal source. Filter by FlexRadio MAC OUI prefix (Stream 2 confirms exact bytes); also include any IP in the local subnet that's in `Reachable` or `Stale` state as lower-confidence candidates.
2. **`GetIpForwardTable2` (routing table)** — derives default gateway, validates subnet scope.
3. **`GetAdaptersAddresses`** — local subnet derivation, available via BCL `NetworkInterface`.
4. **`GetExtendedTcpTable` with `TCP_TABLE_OWNER_PID_ALL`** — looks for any open or recently-closed TCP connection to remote port 4992. Catches the SmartSDR-running-side-by-side case at zero cost.
5. **`hosts` file read** — trivial, free, catches power-user manual entries.
6. **`autoConnectV2.xml` (already in Rung 1a)** — keep.
7. **SmartSDR settings folder read (with user consent)** — Stream 2 confirms the file path.

### Build with confidence-boosters (Phase 1.5b)

Wrap the rung-set above with these gates so we don't waste time probing IPs from a different network:

8. **`INetworkListManager.GetCurrentConnectivity()` and `GetNetworks()`** — gate cached-IP rungs by current network GUID match.
9. **Registry `NetworkList\Profiles` and `Signatures`** — cross-reference with NLM.

### Build for forward-compatibility (Phase 1.5c)

These return nothing today but cost almost nothing to add and turn on automatic future-proofing:

10. **`DnsGetCacheDataTable`** (or WMI `Win32_DNSClientCache`) — DNS resolver cache. Catches future Flex mDNS announcement automatically.
11. **`IFunctionDiscovery` SSDP and WSD provider read in cache-only mode** — catches future Flex SSDP/WSD announcement automatically.

### Speculative-cheap diagnostic capture (Phase 6 of design)

When all other rungs fail and we're producing a diagnostic bundle for support:

12. NetBIOS name cache (`nbtstat -c` shell-out)
13. DHCP lease registry read
14. NetworkProfile/Operational event log read (if user-readable)
15. UDP listener table (`GetExtendedUdpTable`) — confirms our own UDP listener is bound

### Skip and document why

- **LLMNR cache** — no public API exists.
- **Wireless BSSID neighbor** — gated behind location permission for zero discovery value.
- **WinHTTP/WinINET proxy bypass** — never contains LAN device IPs in practice.
- **`NetServerEnum`** — Computer Browser is dead, Flex doesn't participate.
- **`WNetEnumResource`** — SMB-only, Flex doesn't expose shares.
- **Reverse DNS (`Dns.GetHostEntry`)** — not strictly passive (cache miss = active query); if added, must be flagged as active.
- **Synchronous `IUPnPDeviceFinder`** — issues SSDP M-SEARCH; Function Discovery cache mode covers the passive-read case.
- **Full `IFunctionDiscovery` query (non-cache mode)** — issues protocol-specific probes; not passive.

---

## Open questions / follow-up

1. **FlexRadio's actual IEEE-registered MAC OUI prefix(es).** Stream 2 (FlexLib internals) is best positioned to confirm exact byte values. Without this, MAC-OUI-based filtering of ARP results is impossible. **Action:** Stream 2 should verify by reading a known Flex's MAC from a working install's `autoConnectV2.xml` or similar source, then cross-check against IEEE OUI registry. Alternatively, empirically read MAC from Don's, Justin's, Noel's radios via active discovery on a known-good network and document the prefix(es).
2. **SmartSDR settings file path and format on Windows 10/11.** Stream 2's territory; without it we can't build the SmartSDR-cache-read rung. Likely path: `%LOCALAPPDATA%\FlexRadio Systems\SmartSDR-Win\`. Need someone with SmartSDR installed to confirm.
3. **`DnsGetCacheDataTable` stability across Windows 11 24H2 and beyond.** The function is undocumented; Microsoft is under no obligation to keep it stable. Mitigate by always wrapping it in a try/catch + fallback to WMI `Win32_DNSClientCache`. Empirical: it has been stable since XP; risk is low but real.
4. **`IFunctionDiscovery` cache-only mode flag values.** The Win32 docs are imprecise on which combination of flags causes a cache-read vs. a fresh-search. Empirical testing is needed. Worst case: every call triggers a fresh SSDP search, and we have to use a different approach (e.g., direct registry read of cached SSDP devices, if they're cached at all).
5. **Network List Manager profile-GUID stability across `netsh wlan delete profile` and `Forget Network` actions.** Need to verify that the GUID is stable across the kinds of actions a normal user takes. If it churns easily, NLM-gating becomes unreliable.
6. **Should the passive-read pass run on a background thread at app startup, before the user even initiates a connect?** Pre-warming the candidate-IP set means Rung 1.5 has zero latency at connect time. Recommendation: yes, as a low-priority background task during the first 2-3 seconds of app launch. Discuss with Noel.
7. **What's the right per-source confidence weighting?** ARP `Reachable` + matching MAC OUI = very high. Hosts file entry on local subnet = medium. DNS cache hit for a name we don't know is a Flex hostname = low. Some kind of confidence-score aggregator is the natural shape. Defer the scoring math to implementation time but flag it now so the architecture supports it.
8. **Cross-stream interaction: which streams own which sources?** Stream 1 handles all of these passive-read APIs. Stream 2 should own SmartSDR cache file location and FlexLib MAC OUI confirmation. Stream 3+ owns active probes (subnet scan, mDNS browse, SSDP M-SEARCH, etc.).

---

## Citations / references

### Primary Microsoft documentation

- [GetIpNetTable2 function (netioapi.h)](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-getipnettable2)
- [GetIpNetTable function (iphlpapi.h)](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getipnettable)
- [MIB_IPNET_ROW2 (netioapi.h)](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/ns-netioapi-mib_ipnet_row2)
- [GetExtendedTcpTable function (iphlpapi.h)](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedtcptable)
- [MIB_TCPROW_OWNER_PID (tcpmib.h)](https://learn.microsoft.com/en-us/windows/win32/api/tcpmib/ns-tcpmib-mib_tcprow_owner_pid)
- [GetExtendedUdpTable function (iphlpapi.h)](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedudptable)
- [GetIpForwardTable function (iphlpapi.h)](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getipforwardtable)
- [GetIpForwardTable2 function (netioapi.h)](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-getipforwardtable2)
- [GetAdaptersAddresses function (iphlpapi.h)](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getadaptersaddresses)
- [GetIfTable function (iphlpapi.h)](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getiftable)
- [GetIfTable2 function (netioapi.h)](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-getiftable2)
- [NotifyIpInterfaceChange function (netioapi.h)](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-notifyipinterfacechange)
- [NotifyAddrChange function (iphlpapi.h)](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-notifyaddrchange)
- [NotifyUnicastIpAddressChange function (netioapi.h)](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-notifyunicastipaddresschange)
- [About the Network List Manager API](https://learn.microsoft.com/en-us/windows/win32/nla/about-the-network-list-manager-api)
- [NLM_ENUM_NETWORK (netlistmgr.h)](https://learn.microsoft.com/en-us/windows/win32/api/netlistmgr/ne-netlistmgr-nlm_enum_network)
- [DnsServiceBrowse function (windns.h)](https://learn.microsoft.com/en-us/windows/win32/api/windns/nf-windns-dnsservicebrowse)
- [DNS_SERVICE_BROWSE_REQUEST structure](https://learn.microsoft.com/en-us/windows/win32/api/windns/ns-windns-dns_service_browse_request)
- [About Web Services on Devices (WSDAPI)](https://learn.microsoft.com/en-us/windows/win32/wsdapi/about-web-services-for-devices)
- [Function Discovery overview](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/fundisc/fd-portal)
- [SSDP Provider documentation](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/fundisc/ssdp-provider)
- [IFunctionDiscovery (functiondiscoveryapi.h)](https://learn.microsoft.com/en-us/windows/win32/api/functiondiscoveryapi/nn-functiondiscoveryapi-ifunctiondiscovery)
- [IUPnPDeviceFinder (upnp.h)](https://learn.microsoft.com/en-us/windows/win32/api/upnp/nn-upnp-iupnpdevicefinder)
- [WlanGetProfileList function (wlanapi.h)](https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/nf-wlanapi-wlangetprofilelist)
- [WlanQueryInterface function (wlanapi.h)](https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/nf-wlanapi-wlanqueryinterface)
- [Changes to API behavior for Wi-Fi access and location](https://learn.microsoft.com/en-us/windows/win32/nativewifi/wi-fi-access-location-changes)
- [DhcpRequestParams function (dhcpcsdk.h)](https://learn.microsoft.com/en-us/windows/win32/api/dhcpcsdk/nf-dhcpcsdk-dhcprequestparams)
- [DHCP Client API Examples](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/dhcp/dhcp-client-api-examples)
- [NetServerEnum function (lmserver.h)](https://learn.microsoft.com/en-us/windows/win32/api/lmserver/nf-lmserver-netserverenum)
- [Get-NetTCPConnection (NetTCPIP)](https://learn.microsoft.com/en-us/powershell/module/nettcpip/get-nettcpconnection)
- [Get-NetNeighbor (NetTCPIP)](https://learn.microsoft.com/en-us/powershell/module/nettcpip/get-netneighbor)
- [MSFT_NetNeighbor class](https://learn.microsoft.com/en-us/windows/win32/fwp/wmi/nettcpipprov/msft-netneighbor)
- [DNS reverse lookup zones](https://learn.microsoft.com/en-us/windows-server/networking/dns/reverse-lookup)
- [Enable DNS logging and diagnostics](https://learn.microsoft.com/en-us/windows-server/networking/dns/dns-logging-and-diagnostics)

### Community references and code samples

- [Vanara.PInvoke.IpHlpApi (NuGet)](https://www.nuget.org/packages/Vanara.PInvoke.IpHlpApi) — comprehensive managed P/Invoke bindings for IP Helper APIs
- [Vanara.PInvoke.DnsApi (NuGet)](https://www.nuget.org/packages/Vanara.PInvoke.DnsApi/) — DNS API bindings including DnsServiceBrowse
- [Vanara.PInvoke.Dhcp readme](https://github.com/dahall/Vanara/blob/master/PInvoke/Dhcp/readme.md) — DHCP client API bindings
- [pinvoke.net: GetIpNetTable](https://www.pinvoke.net/default.aspx/iphlpapi.getipnettable)
- [pinvoke.net: MIB_TCPROW_OWNER_PID](http://pinvoke.net/default.aspx/Structures/MIB_TCPROW_OWNER_PID.html)
- [pinvoke.net: GetExtendedUdpTable](http://www.pinvoke.net/default.aspx/user32/GetExtendedUdpTable.html)
- [Microsoft Windows Classic Samples — IPArp.cpp (ARP table read example)](https://github.com/microsoft/Windows-classic-samples/blob/main/Samples/Win7Samples/netds/iphelp/iparp/IPArp.Cpp)
- [Microsoft Windows Classic Samples — DHCP persistent client](https://github.com/microsoft/Windows-classic-samples/tree/main/Samples/Win7Samples/netds/dhcp/dhcppersist)
- [malcomvetter/DnsCache (DnsGetCacheDataTable usage)](https://github.com/malcomvetter/DnsCache/blob/master/DnsCache/DnsCache.cpp)
- [FRex/muhdnscache (alternative DnsGetCacheDataTable example)](https://github.com/FRex/muhdnscache)
- [osquery dns_cache table PR (Windows DNS cache implementation)](https://github.com/osquery/osquery/pull/6505)
- [Sam's Code: Function Discovery Intro](https://samscode.blogspot.com/2009/09/function-discovery-intro.html)
- [The Road to Delphi: Network List Manager API](https://theroadtodelphi.com/2015/10/28/using-the-network-list-manager-nlm-api-from-delphi/)
- [Yortw/RSSDP — pure .NET SSDP implementation](https://github.com/Yortw/RSSDP)
- [The Windows Forensic Journey — NetworkList registry artifact](https://medium.com/@boutnaru/the-windows-forensic-journey-networklist-wireless-network-profiles-list-17b41a80903f)
- [Cyber Triage: How to Find Evidence of Network in Windows Registry](https://www.cybertriage.com/blog/how-to-find-evidence-of-network-windows-registry/)
- [DNS Client Logging on Windows (gist)](https://gist.github.com/randomvariable/be90107fd57a4f9502af2eba62978fb6)

### FlexRadio / domain context

- [FlexRadio Community: UDP discovery protocol on port 4992](https://community.flexradio.com/discussion/comment/14299827)
- [FlexRadio: How to Connect your FLEX-6000/FLEX-8000 to a LAN](https://helpdesk.flexradio.com/hc/en-us/articles/202118558-How-to-Connect-your-FLEX-6000-FLEX-8000-to-a-LAN)
- [maclookup.app: FlexRadio Systems vendor page](https://maclookup.app/vendors/flexradio-systems)

### Standards / RFCs

- [RFC 4795 — Link-local Multicast Name Resolution (LLMNR)](https://www.rfc-editor.org/rfc/rfc4795.html)
- [WS-Discovery (Wikipedia)](https://en.wikipedia.org/wiki/WS-Discovery)
- [Devices Profile for Web Services / DPWS (Wikipedia)](https://en.wikipedia.org/wiki/Devices_Profile_for_Web_Services)
- [Multicast DNS / mDNS (Wikipedia)](https://en.wikipedia.org/wiki/Multicast_DNS)
- [Zero-configuration networking (Wikipedia)](https://en.wikipedia.org/wiki/Zero-configuration_networking)
