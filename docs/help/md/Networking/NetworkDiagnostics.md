# Network Diagnostics

Under **Settings > Network > Test network**, JJ Flexible Radio Access runs a probe against Flex's SmartLink servers to tell you what your network actually looks like from outside. This is useful when something is not working and you need to figure out why, and it is also useful when you want to confirm things are working before you hit the road.

The probe reports five pieces of information, each answering a specific question about your network's reachability from the internet side.

## UPnP TCP Port Reachable

**Yes** — your router's UPnP mapping (if one is active) is working for TCP traffic.

**No** — either you do not have a UPnP mapping in place, or the router-external path to that mapping is blocked. If you are on Tier 2, a "No" here is the signal that UPnP is not delivering what it promised.

## UPnP UDP Port Reachable

Same as above but for UDP traffic. Some routers mishandle UDP mappings specifically, so it is possible to see UPnP TCP yes and UPnP UDP no on the same network.

## Manual TCP Port Reachable

**Yes** — the port you configured for Tier 1 is actually forwarded on your router and reachable from the internet. A "Yes" here is the signal that Tier 1 is working end to end.

**No** — either you have not forwarded the port yet, or the forward is not working. Common reasons include the forward pointing at the wrong local IP address, a router reboot that cleared the rule, or a double-NAT situation where your ISP's modem is doing NAT in front of your own router.

## Manual UDP Port Reachable

Same as above but for UDP. You need both TCP and UDP working end to end for SmartLink to work properly. If TCP shows yes and UDP shows no, check your router's port-forwarding rule — it may have been configured as TCP-only.

## Hole-punch Support

**Yes** — your NAT preserves source ports, which means the Tier 3 hole-punch coordinated by Flex's SmartLink server can work for you.

**No** — your NAT is symmetric, or it does not preserve ports in a way that Flex's probe can exploit. Tier 3 will fail on this network. Tier 1 and Tier 2 are the reliable options to fall back on.

## Common Scenarios

- **Everything shows No.** Your network probably cannot reach Flex's SmartLink servers at all. Check your internet connection first. Check whether your router blocks outbound traffic to `smartlink.flexradio.com` on port 443.
- **Manual yes, UPnP no.** Totally normal if you are on Tier 1 and do not use UPnP — this is not an error. Your radio simply does not have a UPnP mapping, because you never asked for one.
- **Manual no, UPnP yes.** Your UPnP mapping is working, but your manual forward is not. Either the manual forward was never configured, or it points at the wrong IP address. Re-check your router's port-forwarding rule.
- **Manual yes, UPnP yes, hole-punch no.** All of your direct reachable paths are working, and Tier 3 hole-punch simply is not available on your network. Stay on Tier 1 or Tier 2 — there is nothing to fix.

## When the Probe Does Not Complete

There are two common reasons the probe returns "did not complete" instead of actual yes/no answers:

- Your SmartLink session is not authenticated yet. Log into SmartLink first, then try the probe again.
- Your SmartLink session was active, but Flex's backend did not respond within 30 seconds. Usually this means a transient issue on Flex's side, or somewhere between you and them. Retry in a minute or two.

If the probe never completes across multiple attempts, that is the signal to check `https://status.flexradio.com` or the FlexRadio forums for outage reports.

## Copying Diagnostic Data for Bug Reports

The Network Diagnostics section of Settings shows a short summary of the probe result. The **Copy report** and **Save report** buttons in that section produce a readable Markdown snapshot of the full probe result — handy when you are reporting an issue on GitHub, or asking for help on a forum. Paste the Markdown into the issue body; it renders cleanly on GitHub and other Markdown-aware platforms, and it remains readable as plain text everywhere else.
