---
title: Stream 2 — Empirical Flex-Radio LAN-Probe Survey (Runbook for Noel)
status: awaiting-noel
needs-hardware: yes
estimated-time: 30-90 minutes (split over multiple sessions if you want)
---

# Stream 2 runbook — empirical Flex LAN-probe survey

## What this stream answers

The other six research streams are theoretical / desk-research. This one is empirical — only your hardware can answer it. The question:

**When a real Flex radio is sitting on a LAN, what discovery probes does it actually answer?**

The round-2 design memo speculated the radio might respond to mDNS, SSDP, NetBIOS, etc. — but speculation isn't evidence. We need to know which probes get a response so v3 doesn't ship rungs that probe protocols Flex radios don't speak.

You have two radios available (the 6300 in the shack, plus whatever else; previously you mentioned an 8600 still boxed). You also have Tailscale, multiple NICs, and the production-style network Don's debugging has been bouncing off of.

Ideal conditions:
- Both radios powered on (or at least one)
- Quiet RF day so you're not in the middle of a QSO
- ~30–90 minutes of focused tinkering time
- Optionally: Don available for a 5-minute call to repeat a couple of probes from his end

## Tools you'll want

Install if you don't have them (all free, all benign):

- **Wireshark** — packet capture, the canonical tool. https://www.wireshark.org/
- **Nmap** (with Zenmap GUI if you prefer) — active probing. https://nmap.org/
- **Bonjour Browser** for Windows — mDNS/Bonjour service browser. https://hobbyistsoftware.com/bonjourbrowser (or `dns-sd -B _services._dns-sd._udp.local.` from cmd if you have Apple's Bonjour installed)
- **SimpleSSDP** or `gssdp-discover` — SSDP/UPnP probe. (Or use nmap's `broadcast-upnp-info` script.)
- **Advanced IP Scanner** (free GUI) — for sanity-checking what shows up on your subnet. https://www.advanced-ip-scanner.com/

You probably have Wireshark already from prior FlexLib debugging. The rest are quick installs.

## Tasks

### Task A — Wireshark passive listen for the radio's announcements (15 min)

Goal: see every packet the radio voluntarily sends out when no client is talking to it.

1. Disconnect any clients (close SmartSDR, JJ Flex, Maestro). Power-cycle the radio if you want a clean baseline.
2. Start Wireshark capturing on your Ethernet/Wi-Fi interface (whichever the radio is on).
3. Filter: `host <radio-IP> or arp` (substitute Don's `192.168.1.76` or your own radio's IP).
4. Let it capture for **5 minutes** with the radio idle.
5. Note in `stream-2-empirical-flex-probes.md`:
   - Every packet type the radio sends without prompting (UDP discovery beacons? mDNS announcements? gratuitous ARP? IGMP membership? other?)
   - Every multicast group the radio joins (look for IGMP messages)
   - Source ports, destination ports, payload size patterns

### Task B — Active probes from your laptop (20 min)

Goal: see what the radio answers when each known LAN-discovery protocol pokes it.

For each probe, note: did the radio respond? What did it say? How fast?

1. **mDNS browse**
   ```
   dns-sd -B _services._dns-sd._udp.local.
   dns-sd -B _flex._tcp.local.
   dns-sd -B _http._tcp.local.
   dns-sd -B _workstation._tcp.local.
   ```
   Or use Bonjour Browser GUI. Let it sit 30 seconds. Note all entries that include the radio.

2. **SSDP M-SEARCH** (UPnP discovery)
   ```
   nmap --script broadcast-upnp-info
   ```
   Or use SimpleSSDP. Did the radio respond?

3. **NetBIOS name query**
   ```
   nbtstat -A <radio-IP>
   ```
   Did it return a name table?

4. **ICMP echo**
   ```
   ping <radio-IP>
   ```
   (Sanity check — should always work.)

5. **ICMP echo broadcast**
   ```
   ping 192.168.1.255
   ```
   Or whatever your subnet broadcast is. Note who responds — does the radio?

6. **WS-Discovery** (Microsoft's discovery protocol)
   ```
   nmap --script broadcast-wsdd
   ```
   Or use a WS-Discovery probe tool. Did the radio respond?

7. **SNMP**
   ```
   snmpwalk -v1 -c public <radio-IP>
   snmpwalk -v2c -c public <radio-IP>
   ```
   Most likely no — but cheap to check.

8. **TCP probe of port 4992**
   ```
   nmap -p 4992 192.168.1.0/24
   ```
   Should find the radio. Note the response time and what nmap reports about the service banner.

9. **TCP probe of all open ports on the radio**
   ```
   nmap -p- <radio-IP>
   ```
   This is the big "what does the radio listen for at all" question. Document every open port. (Surprising results may include HTTP, Telnet, SSH, TFTP for firmware updates, etc.)

10. **LLMNR**
    ```
    nslookup flex
    nslookup flexradio
    nslookup flex-<your-callsign>
    ```
    Try common hostname patterns the radio might respond to.

### Task C — Check Tailscale visibility (10 min)

Goal: when you're connected to Tailscale, can JJ Flex still find your radio? Are subnet probes useful or pointless on a tailnet?

1. Connect Tailscale on your laptop.
2. From the laptop, repeat **Task B step 8** (TCP probe of `192.168.1.0/24`). Does it complete? How long does it take? Does it find the radio?
3. From the laptop, with Tailscale connected, can you `ping <radio's LAN IP>` directly?
4. Does Tailscale's "subnet routes" feature surface the radio? (If yes, that's a path other Tailscale users could use.)

### Task D — Read SmartSDR's saved-radios state (5 min)

Goal: confirm SmartSDR's config files contain the radio's IP, validate the format for stream 3.

1. Open `%AppData%\FlexRadio Systems\` and `%LocalAppData%\FlexRadio Systems\` in Explorer.
2. List every file. Note structure.
3. For any XML/JSON file: open it, search for an IP pattern (`192.168.` or whatever your radio's subnet is). Document where the IP lives.
4. If it's a SQLite DB, open with DB Browser for SQLite. Find the radios table.
5. Optional: zip and attach the (anonymized — strip your callsign and email) SmartSDR config tree to your output for the codebase.

### Task E — Repeat from Don's end (5 min on a call with Don)

This is the gold-standard data: confirm the same probe set against his (UDP-broken-discovery) box.

Phone Don. Walk him through Task B steps 1, 2, 3, 6, 8, 9 (mDNS, SSDP, NetBIOS, WS-Discovery, TCP probe, full nmap). Note any divergences from your results.

Particular interest: does Don's machine see the radio via mDNS / SSDP / WS-Discovery even though UDP discovery is silent? If yes — those are immediate-shippable rungs that work where UDP doesn't on his box.

## What to write up

Write to `stream-2-empirical-flex-probes.md` in this same folder. Structure:

1. Frontmatter: name, date, equipment used (which radios, network setup)
2. Executive summary (which probes work, which don't)
3. Task A findings — packets the radio sends unsolicited
4. Task B findings — table of probe → response (yes/no, latency, payload)
5. Task C findings — Tailscale story
6. Task D findings — SmartSDR config inventory
7. Task E findings — Don's box (if/when you do that call)
8. Recommendations for which active rungs to include in v3 (only those you saw responses for)
9. Open questions

## Notes

- This is the only research stream that needs your active time. The other six are running as background agents right now and will write their findings without your involvement.
- Don't sweat completeness. Even Task A + Task B step 1 + step 6 is a huge data point. You can do C/D/E in a second session if you want.
- If you do Task E with Don, that's also a good chance to check on his cache-writer build status — kill two birds with one phone call.
