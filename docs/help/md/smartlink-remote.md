# SmartLink and Remote Operation

SmartLink lets you control your Flex radio over the internet from anywhere. JJFlexRadio supports SmartLink through a secure browser-based login.

## Setting Up SmartLink

1. Create a SmartLink account at flexradio.com if you don't have one.
2. Register your radio with your SmartLink account through SmartSDR.
3. Make sure your radio has internet access and SmartLink is enabled.

## Connecting Remotely

1. Launch JJFlexRadio.
2. Go to Connect and choose SmartLink.
3. A browser window opens for authentication (using Microsoft Edge WebView2).
4. Sign in with your SmartLink credentials.
5. Your remote radios appear in the list. Select one and press Enter.

## Audio Over SmartLink

Remote audio is streamed using the Opus codec for efficient, low-latency audio. Audio quality depends on your internet connection.

## Troubleshooting SmartLink

- **Can't sign in:** Make sure you have internet access. The authentication uses TLS 1.2 or later.
- **Radio not showing up:** The radio must be powered on, connected to the internet, and registered with your SmartLink account.
- **Audio dropouts:** Check your internet bandwidth. SmartLink audio needs a stable connection. WiFi can introduce jitter — a wired connection is more reliable.
- **Browser window doesn't appear:** JJFlexRadio uses WebView2 (based on Microsoft Edge). If WebView2 isn't installed, the sign-in window won't appear. WebView2 should be included with Windows 10/11, but can be reinstalled from Microsoft.

**Tip:** You don't need SmartLink for local operation. If your radio is on the same network, just connect directly — no account needed.
