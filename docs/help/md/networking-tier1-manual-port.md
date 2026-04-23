# Tier 1 — Manual Port Forwarding

This is the recommended default for SmartLink access to your radio from outside your home network. You pick a port, forward it on your router to your radio's local IP address, and JJ Flexible Radio Access tells the radio to listen on that port. That is all there is to it on the JJ Flexible Radio Access side — the rest is one trip to your router's admin page.

## Why Tier 1 Is the Recommended Default

**It is sovereign.** Nothing asks your router for permission except you. No UPnP, no third-party coordination, no hole-punching. If your router is set up correctly once, it stays set up.

**It is the only tier compatible with strict security policies.** If you work for an organisation that follows DISA STIGs, PCI-DSS, HIPAA, or similar, UPnP is almost certainly disabled on your home router by policy. Tier 1 is the path that stays compatible with those requirements.

**It is the most reliable path through picky NATs.** Some carrier-grade NATs or small-business routers balk at UPnP requests or hole-punch probes, but happily forward a port that you configured yourself.

## Picking a Port

Use a port number between 1024 and 65535 — that is the range reserved for normal applications, not for system services. Avoid well-known numbers like 3389 (Windows Remote Desktop), 5900 (VNC), or 8080 (HTTP proxies), in case you run those services elsewhere on your network. The default in JJ Flexible Radio Access is 4992, which matches FlexRadio's own default and is a safe starting point.

The **Test port** button under **Settings > Network** will warn you if you have picked a port number that commonly conflicts with something else.

## Forwarding the Port on Your Router

Every router brand has a different admin interface, but the steps are essentially the same everywhere:

- Find your radio's local network IP address. You will find it on your radio's front-panel network display, or inside the radio's built-in web interface (the one you reach by typing the radio's IP address into a browser).
- Log into your router's admin interface. The address is usually something like `192.168.1.1` or `192.168.0.1`, typed into a web browser.
- Find the port-forwarding section of the router's settings. It may also be labelled "Virtual Server" or "NAT Forwarding" depending on the brand.
- Add a rule: external port 4992 (or whichever port you chose), both TCP and UDP, forwarded to your radio's local IP address on the same port inside.
- Save the rule.

Your router's manufacturer documentation will have the exact menu names. Most brands — ASUS, Netgear, Linksys, TP-Link, UniFi, and Mikrotik — all call this feature "Port Forwarding," "Virtual Server," or "NAT Forwarding" depending on how the vendor markets it.

## Once the Port Is Forwarded

JJ Flexible Radio Access remembers the port you chose per SmartLink account. The next time you connect, the application automatically tells the radio to listen on that port — you do not have to re-Apply from Settings every session.

## Troubleshooting

If SmartLink is not connecting after you have forwarded the port, work through this checklist:

- Run **Test network** under **Settings > Network**. It will tell you whether your forwarded port is actually visible from the internet.
- Double-check that you forwarded both TCP and UDP. Some routers default to TCP only, and SmartLink needs both.
- Confirm your radio's local IP address has not changed. If your router hands out IP addresses via DHCP, the radio may have received a different address since you set up the forward. Consider reserving a static DHCP lease for the radio.
- If you have multiple routers chained (for example, a modem-router plus a mesh system, or carrier-grade NAT sitting on top of your ISP's modem), the port forwarding may need to be set on the outer router as well. Alternatively, you may be stuck behind something you cannot forward through at all — in which case, look at Tier 3 (automatic hole-punching).
