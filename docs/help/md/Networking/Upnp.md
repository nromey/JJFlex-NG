# Tier 2 — UPnP (Optional)

UPnP stands for Universal Plug-and-Play. It is a protocol that lets programs on your network ask your router to open ports automatically, without you having to log into the router's admin page. Convenient — and also widely disabled in security-conscious environments, for reasons this page gets into.

JJ Flexible Radio Access's Tier 2 mode enables UPnP for SmartLink: you still pick a port in Tier 1, and then at connection time JJ Flexible Radio Access asks your router to open that port via UPnP. No visit to the router's admin page is required.

## When Tier 2 Makes Sense

- You are setting up SmartLink on a home network, and you do not want to hunt around in your router's admin interface.
- Your router supports UPnP and has it enabled. Most home routers do by default.
- You are comfortable with any program on your local network being able to request port mappings from the router.

## When Tier 2 Does Not Make Sense

- Your router has UPnP disabled. This is common on mesh systems, on enterprise-style home routers like UniFi, and on anything a network-aware administrator has touched.
- You are on an organisational network that follows DISA STIGs, PCI-DSS, HIPAA, or similar policies — UPnP is almost always explicitly disabled by those frameworks.
- You want to know exactly what is opened on your router. UPnP port mappings happen without any user confirmation, and you can end up with old, stale mappings left behind by programs that forgot to clean up after themselves.

## The Security Tradeoff

UPnP has no authentication. Any program running on your local network can request a port mapping from the router. Malware has historically abused UPnP to bypass firewall rules silently. This is why security policies disable UPnP by default, and it is also why many router vendors have become reluctant to enable it out of the box.

For a home network where you run only your own software and trust every program on it, the risk is usually acceptable. For anything else, prefer Tier 1.

## If UPnP Fails

JJ Flexible Radio Access attempts the UPnP mapping silently at connection time and falls back to Tier 1 behaviour if any step fails. Typical failure modes include:

- **Your router does not speak UPnP.** You will see "UPnP unavailable" in the diagnostic report.
- **Your router has UPnP disabled.** Same symptom as above — the application cannot distinguish between "the router does not support UPnP" and "UPnP is disabled at the router."
- **The router refused the mapping.** The rejection reason is logged in the trace file for you to review.
- **You are on a different local network than your radio** (a roaming laptop, a hotel network, or a coffee-shop network). JJ Flexible Radio Access detects this situation and skips UPnP entirely, because asking the hotel's router to forward a port at your home radio would be nonsense.

In any of these cases, your manual Tier 1 port forward still works, if you have one configured. Tier 2 is a convenience layer on top of Tier 1, not a replacement for it.

## How to Check Whether UPnP Is Enabled on Your Router

Log into your router's admin interface and look for "UPnP," "NAT-PMP," or "Plug and Play" in the advanced or network settings. Some routers label the same feature "Media Server" or "DLNA" — same family of features, different marketing name.

If you do not want JJ Flexible Radio Access to use UPnP at all, just leave Tier 1 selected under **Settings > Network**. JJ Flexible Radio Access will not call UPnP unless you explicitly pick Tier 2 or Tier 3.
