# Tier 2 — UPnP (optional)

UPnP stands for Universal Plug-and-Play. It is a protocol that lets programs on your network ask your router to open ports automatically, without you having to log into the router's admin page. Convenient. Also widely disabled in security-conscious environments for reasons this page gets into.

JJ Flex's Tier 2 mode enables UPnP for SmartLink — you pick a port in Tier 1, and JJ Flex asks your router to open it via UPnP when you connect. No visit to the router's admin page required.

## When Tier 2 makes sense

- You are setting up SmartLink on a home network and don't want to hunt around in the router's admin UI.
- Your router supports UPnP and has it enabled. Most home routers do by default.
- You are okay with any program on your LAN being able to request port mappings from the router.

## When Tier 2 does NOT make sense

- Your router has UPnP disabled. Common for mesh systems, enterprise-style home routers like UniFi, and anything a network-aware admin has touched.
- You are on an enterprise or organizational network that follows DISA STIGs, PCI-DSS, HIPAA, or similar — UPnP is almost always explicitly disabled by those policies.
- You want to know exactly what's opened on your router. UPnP mappings happen without user confirmation; you can end up with old, stale mappings from programs that forgot to clean up after themselves.

## The security tradeoff

UPnP has no authentication. Any program running on your LAN can request a port mapping. Malware has historically abused UPnP to bypass firewall rules silently. This is why security policies disable it, and it is also why many router vendors are reluctant to enable it by default anymore.

For a home network where you run your own software and trust every program on it, the risk is usually acceptable. For anything else, prefer Tier 1.

## If UPnP fails

JJ Flex attempts the UPnP mapping silently on connect and falls back to Tier 1 behavior if any step fails. Failure modes:

- Your router does not speak UPnP: you will see "UPnP unavailable" in the diagnostic report.
- Your router has UPnP disabled: same symptom — we cannot distinguish "doesn't support" from "disabled at the router."
- Router refused the mapping: logged with the rejection reason in the trace file.
- You are on a different LAN than your radio (roaming laptop, hotel or coffee-shop network): JJ Flex detects this and skips UPnP entirely, because asking the hotel's router to forward a port at your home radio would be nonsense.

In any of these cases, your manual Tier 1 port forward still works. Tier 2 is a convenience layer on top of Tier 1, not a replacement for it.

## How to check if UPnP is enabled on your router

Log into your router's admin interface. Look for "UPnP," "NAT-PMP," or "Plug and Play" in the advanced or network settings. Some routers label it "Media Server" or "DLNA" — same family of features, different name.

If you don't want JJ Flex using UPnP, leaving Tier 1 selected in Settings > Network is enough. JJ Flex will not call UPnP unless you explicitly pick Tier 2 or Tier 3.
