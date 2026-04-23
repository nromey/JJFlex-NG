# Connection Troubleshooting

If JJ Flexible Radio Access cannot find or connect to your radio, here are some things to check.

## Radio Not Found on the Local Network

1. **Is the radio powered on?** The radio takes a minute or so to boot up after it is powered on. Give it time to finish before you expect it to appear.
2. **Are you on the same network?** Your computer and your radio must be on the same local network (the same subnet). If you are on Wi-Fi and the radio is plugged into Ethernet, make sure both devices are on the same network segment — not just the same physical room.
3. **Is Windows Firewall blocking the discovery?** Windows Firewall can block JJ Flexible Radio Access's discovery traffic. The first time you run the application, Windows should prompt you to allow it. If you accidentally blocked it, go into Windows Firewall settings and add an exception for JJ Flexible Radio Access.
4. **Do you have multiple network adapters?** If your computer has multiple network connections active at the same time (Wi-Fi and Ethernet, or a VPN plus your normal adapter), the discovery traffic may go out the wrong interface. Try disabling the other network adapters temporarily to see if that helps.

## The Connection Drops After You Connect

If you connect successfully but then lose the connection partway through a session:

1. **Check your network stability.** Wi-Fi can be unreliable for sustained radio control — it introduces jitter and occasional dropouts. A wired Ethernet connection is almost always more reliable.
2. **Check the radio's firmware.** Make sure your radio is running the latest firmware release from FlexRadio.
3. **Check for other clients.** If another instance of SmartSDR or JJ Flexible Radio Access is already connected to the same radio, you may be hitting the radio's MultiFlex client limit.

## SmartLink Issues

For SmartLink (remote) connection problems specifically, see the SmartLink and Remote Operation help page for targeted troubleshooting steps.

## General Tips

- JJ Flexible Radio Access does not require a SmartLink account for local network connections. If you are on the same network as the radio, it should just work — no account needed.
- Press `Ctrl+Shift+S` at any time to hear the current connection status spoken.
- The Tracing option under the **Help** menu lets you capture detailed logs for debugging, which is useful when you are reporting a connection issue.
