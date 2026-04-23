# SmartLink and Remote Operation

SmartLink lets you control your Flex radio over the internet from anywhere. JJ Flexible Radio Access supports SmartLink through a secure browser-based sign-in flow.

## Setting Up SmartLink

1. Create a SmartLink account at flexradio.com if you do not already have one.
2. Register your radio with your SmartLink account through SmartSDR on your home computer.
3. Make sure your radio has working internet access and that SmartLink is enabled on the radio itself.

## Connecting Remotely

1. Launch JJ Flexible Radio Access.
2. From the **Radio** menu, choose **Connect to Radio**. The Select Radio dialog opens and begins discovering radios on your local network.
3. Press the **Remote** button (`Alt+R`) inside the Select Radio dialog. A browser window opens (using Microsoft Edge WebView2) so you can sign in with your SmartLink credentials.
4. After you sign in, your remote radios appear in the same radio list you just saw.
5. Use the arrow keys to select the radio you want, then press **Connect** (`Alt+N`, or `Enter`).

## Audio Over SmartLink

Remote audio is streamed using the Opus codec for efficient, low-latency audio. The audio quality you hear depends on your internet connection — specifically on bandwidth and on how stable (jitter-free) the path is.

## Troubleshooting SmartLink

- **Cannot sign in.** Make sure you have working internet access. The authentication uses TLS 1.2 or later; older systems without TLS 1.2 support will fail.
- **Radio does not show up after sign-in.** The radio must be powered on, connected to the internet, and registered with your SmartLink account through SmartSDR.
- **Audio dropouts.** Check your internet bandwidth and connection stability. SmartLink audio needs a steady connection — Wi-Fi can introduce jitter, and a wired Ethernet connection is almost always more reliable.
- **The browser sign-in window never appears.** JJ Flexible Radio Access uses Microsoft Edge WebView2 for the sign-in flow. If WebView2 is not installed on your system, the sign-in window will not open. WebView2 normally ships with Windows 10 and Windows 11, but if it is missing you can reinstall it from Microsoft's website.

**Tip:** You do not need SmartLink at all for local operation. If your radio is on the same network as your computer, you can connect directly without any account or sign-in required.
