# Getting Started

This guide walks you through installing JJFlexRadio and connecting to your FlexRadio for the first time.

## System Requirements

- Windows 10 or later (64-bit recommended, 32-bit supported)
- .NET 8 runtime (the installer will prompt you if it's not installed)
- A FlexRadio FLEX-6000 or FLEX-8000 series transceiver
- Your radio and computer on the same local network, or a SmartLink account for remote operation

## Installation

1. Run the installer (`Setup JJFlexRadio_x64.exe` for 64-bit, or `_x86.exe` for 32-bit systems).
2. Follow the prompts. The default install location is fine for most people.
3. Launch JJFlexRadio from the Start menu or desktop shortcut.

## First Connection (Local Network)

When your Flex radio is on the same network as your computer:

1. Turn on your radio and wait for it to boot up.
2. Launch JJFlexRadio. The app automatically discovers Flex radios on your local network.
3. You should hear your radio announced. If multiple radios are found, use the arrow keys to select the one you want.
4. Press Enter to connect.

That's it. You don't need a SmartLink account for local connections.

## First Connection (Remote / SmartLink)

For remote operation through the internet:

1. You need a SmartLink account from FlexRadio. Set this up at flexradio.com if you haven't already.
2. In JJFlexRadio, go to the Connect menu and choose SmartLink.
3. A browser window opens for you to sign in with your SmartLink credentials.
4. After signing in, your remote radios appear in the list.
5. Select your radio and press Enter to connect.

**Tip:** If you have trouble with SmartLink, check the SmartLink & Remote troubleshooting page.

## What Happens After You Connect

Once connected, JJFlexRadio announces the radio model and current frequency. You're in Modern mode by default, which gives you a streamlined keyboard-driven interface.

Here are the first things to try:

- Press `F2` to hear the current frequency spoken.
- Use the arrow keys to tune.
- Press `Alt+M` to cycle through modes (USB, LSB, CW, etc.).
- Press `F3` through `F9` to jump to different bands.
- Press `Ctrl+/` to open the Command Finder and search for any command.

## Next Steps

- **Screen Reader Setup** — Fine-tune NVDA or JAWS for JJFlexRadio
- **Keyboard Reference** — Learn all the hotkeys
- **Operating Modes** — Understand Classic vs. Modern mode
