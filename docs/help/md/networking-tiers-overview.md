# SmartLink networking: the three tiers

JJ Flex offers three networking "tiers" for reaching your Flex radio over SmartLink from outside your home network. They're cumulative — Tier 2 includes Tier 1, Tier 3 includes Tiers 1 and 2. Pick one in Settings > Network; each covers different user needs.

## The short version

- Tier 1 — Manual port forwarding. You forward a port on your router to the radio. No UPnP, no automatic negotiation. The recommended default.
- Tier 2 — Tier 1 plus UPnP. JJ Flex asks your router to open the port for you automatically. Convenient, with security tradeoffs.
- Tier 3 — Tier 2 plus automatic hole-punch. If direct port forwarding isn't working, Flex's SmartLink coordinates a UDP hole-punch to get through. For restrictive NAT situations.

Not sure which one you want? Tier 1. Seriously. It's the default for a reason and it works for almost everyone.

## When to pick each tier

Pick **Tier 1 (manual)** if any of these are true:

- You know how to forward a port on your router, or are willing to learn.
- You work in an environment with a security policy that requires UPnP disabled (DISA STIGs, PCI-DSS, HIPAA, most corporate networks).
- You want the simplest, most predictable setup.
- You run JJ Flex in a shack environment you control.

Tier 1 is sovereign — nothing happens at your router that you didn't initiate. It's also the most reliable through fussy NATs because you set up the path explicitly. See `tier1-manual-port` for the how-to.

Pick **Tier 2 (Tier 1 + UPnP)** if:

- You don't want to log into the router admin page.
- Your router has UPnP enabled (most home routers do by default).
- You're okay with any program on your LAN being able to open router ports.

UPnP is a convenience; it has real security implications because it has no authentication. See `tier2-upnp` for the full picture. If UPnP fails, JJ Flex falls back to Tier 1 silently — you still need your manual port forward configured for the fallback to help.

Pick **Tier 3 (Tier 1 + UPnP + hole-punch)** if:

- Your network won't let you forward a port at all — some mobile-carrier-backed NATs and certain corporate networks.
- You've tried Tier 1 and Tier 2 and neither works.
- You need SmartLink working from a network you don't control.

Hole-punch may still fail on symmetric NATs (network configurations that randomize outbound source ports per destination). In that case no tier works and a different approach is needed (e.g., a VPN back to your radio's network).

## What happens on failure

Tiers fall back gracefully: Tier 3 falls back to Tier 2 falls back to Tier 1. The failure mode gets surfaced in the Network Diagnostic section of Settings (Settings > Network > Test network). If you're not sure what's working on your network, run the Test network probe — it reports each tier's actual reachability from outside.

## The three tiers aren't three different SmartLink servers

All three tiers connect to Flex's SmartLink server on the same port (443). What differs is which kind of external reachability they set up for the radio itself — manual port forward, UPnP-mapped port forward, or UDP hole-punch. SmartLink itself is the same in all cases.
