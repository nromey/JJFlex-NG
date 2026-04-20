# Tier 1 — Manual port forwarding

This is the recommended default for SmartLink access to your radio from outside your home network. You pick a port, forward it on your router to your radio's LAN IP, and JJ Flex tells the radio to listen on that port. That's all there is to it on the JJ Flex side — the rest is one trip to your router's admin page.

## Why Tier 1 is the recommended default

It's sovereign. Nothing asks your router for permission except you. No UPnP, no third-party coordination, no hole-punch. If your router is set up right once, it stays set up.

It's the only tier compatible with strict security policies. If you work for an organization that follows DISA STIGs, PCI-DSS, HIPAA, or similar, UPnP is almost certainly disabled on your home router by policy. Tier 1 is the path that stays compatible with those requirements.

It's the most reliable path through picky NATs. Some carrier-grade NATs or small-business routers balk at UPnP requests or hole-punch probes but happily forward a port you configured yourself.

## Picking a port

Use a port number between 1024 and 65535 — the range reserved for normal applications, not system services. Avoid very well-known numbers like 3389 (Windows Remote Desktop), 5900 (VNC), or 8080 (HTTP proxies) in case you run those services on your network. JJ Flex's default is 4992, which matches FlexRadio's default and is a safe starting point.

The "Test port" button in Settings > Network will warn you if you pick something that commonly conflicts.

## Forwarding the port on your router

Every router brand has a different admin UI, but the steps are essentially the same everywhere:

- Find your radio's LAN IP address. It's the 192.168.x.x or 10.x.x.x address your radio shows on its front-panel network display, or in the radio's web UI.
- Log into your router's admin interface. Usually a browser address like 192.168.1.1 or 192.168.0.1.
- Find the port-forwarding or virtual-server section.
- Add a rule: external port 4992 (or whatever you chose), TCP and UDP, forwarded to your radio's LAN IP address, same port inside.
- Save.

Your router's manufacturer documentation will have exact menu names. Most brands — ASUS, Netgear, Linksys, TP-Link, UniFi, Mikrotik — all call this feature "Port Forwarding," "Virtual Server," or "NAT Forwarding" depending on brand.

## Once the port is forwarded

JJ Flex remembers the port you chose per SmartLink account. Next time you connect, it automatically tells the radio to listen on that port — you don't need to re-Apply from Settings every session.

## Troubleshooting

If SmartLink isn't connecting after you've forwarded the port:

- Run "Test network" in Settings > Network. It will tell you whether your forwarded port is visible from the internet.
- Double-check you forwarded both TCP and UDP. Some routers default to TCP-only.
- Confirm your radio's LAN IP hasn't changed. If your router hands out DHCP leases, the radio may have a different IP than when you set up the forward. Consider reserving a static DHCP lease for the radio.
- If you have multiple routers chained (modem-router plus mesh, carrier-grade NAT on top of your ISP modem, etc.), port forwarding may need to be set on the outer one too. Or you may be stuck behind something you can't forward through — in which case look at Tier 3 (automatic hole-punch).
