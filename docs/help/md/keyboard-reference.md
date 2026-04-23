# Keyboard Reference

Welcome to your one-stop reference for every keyboard shortcut in JJ Flexible Radio Access. You can also press `Ctrl+Shift+H` (or `Ctrl+/`) at any time to open the Command Finder, which lets you search for commands by name.

JJ Flexible Radio Access has three operating scopes: some keys work everywhere (Global), some only work when you're controlling the radio (Radio scope, which includes both Classic and Modern tuning modes), and some only work when you're in the logging pane (Logging scope).

## Global Hotkeys

These keys work no matter where you are in the application.

| Key | Action |
|-----|--------|
| F1 | Open this help file |
| Ctrl+/ | Open Command Finder (search all commands) |
| Ctrl+Shift+H | Open Command Finder (alternate shortcut) |
| Ctrl+J | Leader key (press, release, then press a second key) |
| Ctrl+Shift+1 | Jump to ScreenFields: Receiver category |
| Ctrl+Shift+2 | Jump to ScreenFields: DSP category |
| Ctrl+Shift+3 | Jump to ScreenFields: Audio category |
| Ctrl+Shift+4 | Jump to ScreenFields: Transmission category |
| Ctrl+Shift+5 | Jump to ScreenFields: Antenna category |
| Ctrl+M | Toggle meters panel on/off |
| Ctrl+Shift+W | Open the Audio Workshop |
| Ctrl+Shift+S | Speak full status (multi-slice aware) |
| Ctrl+Shift+T | Toggle the tune carrier on/off |
| Ctrl+T | Start an automatic Tuning Unit (ATU) tune cycle |
| Ctrl+Shift+F | Speak the current TX filter width |
| Ctrl+Shift+[ | move the TX filter low edge down |
| Ctrl+Shift+] | move the TX filter low edge up |
| Ctrl+Alt+[ | adjust the TX filter high edge down |
| Ctrl+Alt+] | adjust the TX filter high edge up |
| F12 | Stop CW transmission immediately |
| Ctrl+L | open the Callbook / station lookup utility |
| Ctrl+Alt+S | Open the status dialog |
| Alt+Shift+S | Speak your current transmit (TX) status |

## The JJ "Leader" Key Commands

This application's got a problem. We own it, it's true. There's a ton of keyboard shortcuts that you can use in this application. Problem is, we have too many keyboard shortcuts for the number of keys on your keyboard. Enter the JJ key.

The JJ Key, `Ctrl+J` in JJ Flexible Radio Access, can be used to activate various toggles, options, and other commands throughout the JJ Flexible Radio Access application. Similar to pressing layered commands in JAWS (JAWS Key+J then another key), the JJ key is our flavor of command-key layering you can use to access functions we couldn't fit onto single-keypress hotkeys. The JJ layer is our secret cheat code — it lets you reach commands we couldn't squeeze into a single keypress. We hope the JJ layer keeps you from needing to use three fingers on one hand, your right pinky finger, and your left big toe to activate neural noise mitigation or turn on the audio peak meter. Who wants to be a contortionist — both physically and mentally — when you're trying to rack up points in a busy contest or chase DX? I know I sure don't.


Press `Ctrl+J` and then release it to enter layered command mode. You'll hear a rising tone to let you know that you've activated the JJ key layer and it's ready for you to press one of these keys to perform an action in the app.

| Key | Action |
|-----|--------|
| N | Toggle Neural Noise Reduction (RNN) on/off |
| B | Toggle Noise Blanker on/off |
| W | Open Audio Workshop |
| R | Speak current meter readings |
| S | Speak full status |
| A | Toggle Audio Peak Filter (APF, CW only) |
| P | Cycle meter preset (RX Monitor, TX Monitor, Full Monitor) |
| M | Toggle meter tones on/off |
| E | Toggle meter tones on/off (alias for M) |
| T | Cycle tuning step size |
| F | Speak current TX filter width |
| D | Toggle tuning speech debounce |
| L | Speak log statistics |
| ? or H | List all leader key commands |
| Escape | Cancel leader mode |

Don't worry if you press the JJ key by accident or if you simply don't want to access the command layer. Press Escape to exit JJ command mode, or wait two seconds for the application to exit the mode automatically. When you drop out of the JJ layer without entering a command, you'll hear a cute little descending tone, and you can go back to whatever you were doing previously in the JJ Flexible Radio app.

## Band Jumping

These keys let you switch bands on your radio instantly. They work in both Classic and Modern tuning. Something that may help you remember which keys do what when it comes to band switches is that you can switch to standard amateur radio bands by pressing f-keys without any modifiers. Wark bands like thirty,  17, 12 and sixty meters are accessed by pressing f-keys with teh shift modifier

| Key | Band |
|-----|------|
| F3 | 160 meters |
| F4 | 80 meters |
| F5 | 40 meters |
| F6 | 20 meters |
| F7 | 15 meters |
| F8 | 10 meters |
| F9 | 6 meters |
| Shift+F3 | 60 meters |
| Shift+F4 | 30 meters |
| Shift+F5 | 17 meters |
| Shift+F6 | 12 meters |
| Alt+Up | Next band up |
| Alt+Down | Next band down |
| Alt+Shift+Up | 60m channel up (when on 60 meters) |
| Alt+Shift+Down | 60m channel down (when on 60 meters) |

## Radio Control (Classic and Modern Tuning Modes)

These keys work when you're in either Classic or Modern tuning mode:

| Key | Action |
|-----|--------|
| F2 | Go to Home (this is where you adjust frequencies or other radio options— see more information about the JJ Flexible home  below) |
| Ctrl+F | Set frequency (direct entry) |
| Ctrl+Shift+M | Start a memory scan |
| Ctrl+Shift+C | Clear RIT offset |
| Alt+Z | activate the CW zero beat option (Requires an SDR-Plus subscription to enable |
| Ctrl+P | Adjust audio panning |
| Escape | Collapse the field group you're in, or return to Home |
| Double tap escape (quickly) | Collapse all open field groups and return to Home |

## Notes on the JJ Flexible Home

These keys work from ANY field within your  JJ Flexible Home. First, remember to press f2 to access your radio interface home. Press the key and you will activate the following actions:

| Key | Action |
|-----|--------|
| M | Toggle mute on the active slice |
| V | Cycle to next slice |
| R | Toggle RIT on/off |
| X | Toggle XIT on/off |
| Q | Toggle squelch on/off |
| = | Transceive current slice (set both RX and TX to this slice) |

## JJ Flexible Home — Slice Field Keys

When focused on the Slice field specifically:

| Key | Action |
|-----|--------|
| Space or Up | Cycle to next slice |
| Down | Cycle to previous slice |
| 0-9, A-H | Jump directly to the desired slice by number or letter |
| T | Make this slice the TX slice |
| . (period) | Create a new slice |
| , (comma) | Release the current slice |
| Page Up | Pan right |
| Home | Pan center |
| Page Down | Pan left |

## JJ Flexible Home — Slice Operations Field Keys

When focused on the Slice Operations field (per-slice audio controls):

| Key | Action |
|-----|--------|
| Up / Down | Adjust volume up/down |
| Page Up / Page Down | Pan right / left |
| Space | Turn mute on or off |
| m | Mute |
| S | Unmute |
| A | Set the currently selected slice to active (RX) |
| T | set the currently selected  slice to transmit (TX)|

## JJ Flexible Home — Squelch Level Field Keys

When focused on the Squelch Level field:

| Key | Action |
|-----|--------|
| Up | Raise squelch level by 5 |
| Down | Lower squelch level by 5 |
| Q | Toggle squelch on/off |

## Mode Switching

| Key | Action |
|-----|--------|
| Alt+M | Next mode (cycles through available modes) |
| Alt+Shift+M | Previous mode |
| Alt+U | Switch to Upper Side Band (USB) |
| Alt+L | Switch to Lower Side Band (LSB) |
| Alt+C | Switch to the CW mode |

## Audio Controls

| Key | Action |
|-----|--------|
| Alt+Page Up | Audio gain up |
| Alt+Page Down | Audio gain down |
| Alt+Shift+Page Up | Headphones volume up |
| Alt+Shift+Page Down | Headphones volume down |
| Shift+Page Up | Line out volume up |
| Shift+Page Down | Line out volume down |

## Scanning

| Key | Action |
|-----|--------|
| Ctrl+Alt+S | Start scan |
| Ctrl+Shift+U | Start saved frequency scan |
| Ctrl+Z | Stop scan |
| Ctrl+Shift+F2 | Resume scan |

## DX and Spotting

| Key | Action |
|-----|--------|
| Alt+D | Open DX Cluster |
| Ctrl+Alt+R | Open Reverse Beacon Network |

## CW Messages

If you have CW messages configured, you can send them with `Ctrl+1` through `Ctrl+7`. Each number corresponds to a message slot. You can configure your CW messages in the Settings dialog.

## Logging Mode

When you're in the logging pane, these keys help you fill in QSO details quickly. Some keys that do something different in Radio mode (like Alt+C and Alt+D) switch to logging functions here.

| Key | Action |
|-----|--------|
| Alt+C | Jump to Call field |
| Alt+T | Jump to His RST field |
| Alt+R | Jump to My RST field |
| Alt+N | Jump to Name/Handle field |
| Alt+Q | Jump to QTH field |
| Alt+S | Jump to State field |
| Alt+G | Jump to Grid field |
| Alt+E | Jump to Comments field |
| Alt+D | Set date/time to now |
| Ctrl+W | Save/finalize QSO |
| Ctrl+N | New log entry |
| Ctrl+Shift+F | Search log |
| Ctrl+Shift+T | Log statistics |
| F6 | Switch between log panes |
| Ctrl+Shift+N | Log characteristics dialog |
| Ctrl+Alt+L | Open full log entry form |

## ScreenFields Quick Access

The ScreenFields panel has five expandable categories you can jump to instantly:

| Key | Category |
|-----|----------|
| Ctrl+Shift+1 | Receiver |
| Ctrl+Shift+2 | DSP |
| Ctrl+Shift+3 | Audio |
| Ctrl+Shift+4 | Transmission |
| Ctrl+Shift+5 | Antenna |

**Tip:** Each category expands to show its controls. Press the hotkey again to collapse it. Remember that you can also collapse any field by pressing escape. Press escape twice quickly to close all field categories.

## View Controls

| Key | Action |
|-----|--------|
| Ctrl+Shift+F3 | Move focus to the received text box |
| Ctrl+Shift+F4 | Move focus to the CW send text box |
| Ctrl+Shift+F5 | Move focus to the CW send text box (currently the same as Ctrl+Shift+F4 — a distinct direct-keying mode is planned for a future release) |
