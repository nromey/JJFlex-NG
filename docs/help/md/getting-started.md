# Getting Started

This guide walks you through installing JJ Flexible Radio Access and connecting to your FlexRadio for the first time.

## System Requirements

- Windows 10 or later (64-bit recommended, 32-bit supported)
- The .NET 10 runtime (the installer will prompt you to install it if you don't already have it)
- A FlexRadio FLEX-6000, FLEX-8000 series, or Flex Aurora  transceiver
- Your radio and computer on the same local network, or a SmartLink account which is attached to a physical radio for remote operation

## Installation

1. Run the installer (`Setup JJFlexRadio_x64.exe` for 64-bit Windows, or `Setup JJFlexRadio_x86.exe` for 32-bit systems).
2. Follow the prompts. The default install location is fine for most people.
3. Launch JJ Flexible Radio Access from the Start menu or the desktop shortcut. When the application starts up, you'll hear "Welcome to JJ Flexible Radio Access" spoken.

## First Connection (Local Network)

When your Flex radio is on the same network as your computer:

1. Turn on your radio and wait for it to finish booting up. This can take a while.
2. Launch JJ Flexible Radio Access. The application automatically discovers Flex radios on your local network.
3. You should hear your radio announced. If more than one radio is found, use the arrow keys to select the one you want.
4. Press Enter to connect — or press the **Auto-Connect** button, which connects to the first available radio automatically. This is handy if you only have one radio on the network.

That's it. If you've got a local radio on the network, remember that you don't need a SmartLink account for local connections, but feel free to sign up for one should you wish to be able to connect to your radio remotely..

## First Connection (Remote / SmartLink)

For remote operation through the internet:

1. You need a SmartLink account from FlexRadio Systems. If you don't already have one, set it up at flexradio.com first, or use the sign-up link that appears on the SmartLink sign-in page within JJ Flexible Radio Access.
2. From the **Radio** menu, choose **Connect to Radio**. The Select Radio dialog opens, and JJ Flexible Radio Access starts announcing any radios it discovers on your local network.
3. Press the **Remote** button (or `Alt+R`) to switch the dialog to remote mode. A browser window opens so you can sign in with your SmartLink credentials.
4. After you sign in, your remote radios appear in the same radio list alongside (or in place of) the local ones.
5. Use the arrow keys to select the radio you want, then press the **Connect** button (or `Alt+N`, or `Enter`) to start the connection.

**Tip:** If you run into trouble with SmartLink, check the SmartLink and Remote Troubleshooting page for help.

## What Happens After You Connect

Once you're connected, the application automatically speaks a full status summary which comes from your radio's connection. You'll hear something like: "Connected to FLEX-6600, local network. Listening on 14.225 megahertz, Upper Side Band, 20 meters, slice A." That tells you everything you need to know to start operating right away.

You're in Modern tuning mode by default, which gives you a streamlined keyboard-driven interface and the ability to tun e in very finie steps or more coarse ones.

Here are the first things you might want to try:

- Press `F2` to hear the current frequency spoken and to focus the JJ Flexible Radio Home.
- Use the up and down arrow keys to tune.
- Press `Alt+M` to cycle through modes (Upper Side Band, Lower Side Band, CW, AM, and so on).
- Press `F3` through `F9` to jump to different bands.
- Press `Ctrl+/` to open the Command Finder, where you can search for any keyboard command by name.

## Next Steps

- **Screen Reader Setup** — Fine-tune NVDA or JAWS for JJ Flexible Radio Access.
- **Keyboard Reference** — Learn all the hotkeys.
- **Operating Modes** — Understand Classic and Modern modes.
