# SmartLink Networking — The Three Tiers

JJ Flexible Radio Access offers three networking "tiers" for reaching your Flex radio over SmartLink from outside your home network. The tiers are cumulative — Tier 2 includes everything Tier 1 does, and Tier 3 includes everything Tier 1 and Tier 2 do. You pick the tier you want under **Settings > Network**, and each tier is designed for a different set of user needs.

## The Short Version

- **Tier 1 — Manual port forwarding.** You forward a port on your router to your radio. No UPnP, no automatic negotiation. This is the recommended default.
- **Tier 2 — Tier 1 plus UPnP.** JJ Flexible Radio Access asks your router to open the port for you automatically. Convenient, with some security tradeoffs.
- **Tier 3 — Tier 2 plus automatic hole-punching.** When direct port forwarding is not working, Flex's SmartLink server coordinates a UDP hole-punch to get through the router anyway. Designed for restrictive NAT situations.

Not sure which one you want? Tier 1. Seriously — it is the default for a reason and it works for almost everyone.

## When to Pick Each Tier

Pick **Tier 1 (Manual)** if any of these apply to you:

- You know how to forward a port on your router, or you are willing to learn.
- You work in an environment with a security policy that requires UPnP to be disabled (DISA STIGs, PCI-DSS, HIPAA, or most corporate networks).
- You want the simplest, most predictable setup.
- You run JJ Flexible Radio Access in a shack environment you control.

Tier 1 is sovereign — nothing happens at your router that you did not initiate. It is also the most reliable path through fussy NATs because you set up the path explicitly, end to end. See the Tier 1 Manual Port Forwarding help page for the step-by-step how-to.

Pick **Tier 2 (Tier 1 plus UPnP)** if any of these apply:

- You would rather not log into your router's admin page.
- Your router has UPnP enabled (most home routers do by default).
- You are comfortable with any program on your local network being able to open ports on your router.

UPnP is a convenience layer, and it has real security implications because UPnP has no authentication. See the Tier 2 UPnP help page for the full picture. If UPnP fails at connection time, JJ Flexible Radio Access silently falls back to the Tier 1 path — which means you still need your manual port forward configured for the fallback to have something to fall back to.

Pick **Tier 3 (Tier 1 plus UPnP plus hole-punch)** if any of these apply:

- Your network simply will not let you forward a port at all — for example, some mobile-carrier-backed NATs and certain corporate networks.
- You have tried Tier 1 and Tier 2, and neither one works for your situation.
- You need SmartLink working from a network that you do not control.

Hole-punching may still fail on symmetric NATs — these are network configurations that randomise outbound source ports per destination. When that happens, no tier works, and you will need a different approach (for example, a VPN back to your radio's network).

## What Happens on Failure

The tiers fall back gracefully: Tier 3 falls back to Tier 2, which falls back to Tier 1. The failure mode is surfaced in the Network Diagnostics section of **Settings > Network > Test network**. If you are not sure what is actually working on your network, run the Test network probe — it reports the reachability of each tier independently, from outside.

## The Three Tiers Are Not Three Different SmartLink Servers

All three tiers connect to Flex's SmartLink server on the same port (443). What differs between tiers is which kind of external reachability they set up for the radio itself — a manual port forward, a UPnP-mapped port forward, or a UDP hole-punch. The SmartLink server itself is the same in every case.
