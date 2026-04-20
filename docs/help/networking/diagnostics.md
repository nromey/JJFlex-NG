# Network diagnostics

Settings > Network > Test network runs a probe against Flex's SmartLink servers to tell you what your network looks like from outside. Useful when things aren't working and you need to figure out why, or when you want to confirm things are working before you hit the road.

The probe reports five pieces of information, each answering a specific question about your network's reachability from the outside.

## UPnP TCP port reachable

Yes: your router's UPnP mapping (if active) is working for TCP traffic.

No: either you don't have a UPnP mapping, or the router-external path to that mapping is blocked. If you are on Tier 2, this is the signal that UPnP is not delivering what it promised.

## UPnP UDP port reachable

Same as above but for UDP. Some routers mishandle UDP mappings specifically, so you can see UPnP TCP yes and UPnP UDP no on the same network.

## Manual TCP port reachable

Yes: the port you configured in Tier 1 is actually forwarded on your router and reachable from the internet. This is the signal Tier 1 is working end-to-end.

No: either you have not forwarded the port yet, or the forward is not working. Common reasons include pointing at the wrong LAN IP, a router reboot that cleared the rule, or a double-NAT situation where your ISP's modem is NATing in front of your router.

## Manual UDP port reachable

Same as above for UDP. You need both TCP and UDP working for SmartLink. If TCP shows yes and UDP shows no, check your router's port-forward rule — it may be TCP-only.

## Hole-punch support

Yes: your NAT preserves source ports, which means Tier 3 hole-punch coordinated by Flex's SmartLink server can work for you.

No: your NAT is symmetric, or doesn't preserve ports in a way Flex's probe can exploit. Tier 3 will fail on this network. Tier 1 or Tier 2 are the reliable options.

## Common scenarios

Everything shows no. Your network probably can't reach Flex's SmartLink servers at all. Check your internet connection. Check if your router blocks outbound traffic to smartlink.flexradio.com on port 443.

Manual yes, UPnP no. Totally normal if you are on Tier 1 and don't use UPnP. Not an error — your radio just doesn't have a UPnP mapping because you didn't ask for one.

Manual no, UPnP yes. Your UPnP mapping is working, but your manual forward isn't. Either the manual forward isn't configured, or it points at the wrong IP. Re-check your router's port-forward rule.

Manual yes, UPnP yes, hole-punch no. All your reachable paths work; Tier 3 hole-punch just won't help you. Stay on Tier 1 or Tier 2.

## When the probe doesn't complete

Two common reasons the probe returns "did not complete" instead of actual yes/no answers:

- Your SmartLink session isn't authenticated yet. Log in to SmartLink first, then try again.
- Your SmartLink session was active but Flex's backend didn't respond within 30 seconds. Usually means a transient issue on Flex's side or between you and them. Retry in a minute.

If the probe never completes across multiple attempts, that's the signal to check https://status.flexradio.com or the FlexRadio forums for outage reports.

## Copying diagnostic data for bug reports

The Network Diagnostic section shows a short summary. The copy-to-clipboard and save-to-file buttons in that section produce a readable markdown snapshot of the full probe result — handy when reporting issues on GitHub or asking for help on a forum. Paste the markdown into the issue body; it renders cleanly in GitHub and other markdown-aware places, and it is readable as plain text everywhere else.
