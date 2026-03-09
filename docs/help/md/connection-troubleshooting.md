# Connection Troubleshooting

If JJFlexRadio can't find or connect to your radio, here are some things to check.

## Radio Not Found on Local Network

1. **Is the radio on?** It takes a minute or so to boot up.
2. **Same network?** Your computer and radio must be on the same local network (same subnet). If you're on WiFi and the radio is wired, make sure they're on the same network segment.
3. **Firewall?** Windows Firewall may be blocking JJFlexRadio's discovery traffic. The first time you run JJFlexRadio, Windows should ask you to allow it. If you accidentally blocked it, go to Windows Firewall settings and add an exception.
4. **Multiple network adapters?** If your computer has multiple network connections (WiFi + Ethernet, VPN, etc.), discovery traffic may go out the wrong interface. Try disabling other network adapters temporarily.

## Connection Drops

If you connect but then lose the connection:

1. **Network stability** — Check your network connection. WiFi can be unreliable for sustained radio control.
2. **Radio firmware** — Make sure your radio is running the latest firmware.
3. **Other clients** — If another instance of SmartSDR or JJFlexRadio is connected, the radio may be at its client limit (MultiFlex).

## SmartLink Issues

For SmartLink (remote) connection problems, see the SmartLink & Remote page.

## General Tips

- JJFlexRadio doesn't require a SmartLink account for local connections. If you're on the same network, it should just work.
- Press `Ctrl+Shift+S` to hear the current connection status.
- Check the Tracing option in the Help menu to capture detailed logs for debugging.
