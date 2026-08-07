# Keyboard Reference

Welcome to your one-stop reference for every keyboard shortcut in JJ Flexible Radio Access. You can also press `Ctrl+/` at any time to open the Command Finder, which lets you search for commands by name. And here's the newest trick: while you're in the JJ Flexible Home, press `?` on any field and the app speaks the keys that work right there.

Almost every shortcut below can be changed. Open Tools, then Hotkey Editor, pick a command, and press the new key you want — the app tells you about any conflict and asks before taking a key away from another command. Help → Key Assignments opens the same list for browsing. Commands listed as having no key can be given one there too.

JJ Flexible Radio Access has three operating scopes: some keys work everywhere (Global), some only work when you're controlling the radio (Radio scope, which includes both Classic and Modern tuning modes), and some only work when you're in the logging pane (Logging scope).

## Global Hotkeys

These keys work no matter where you are in the application.

| Key | Action |
|-----|--------|
| F1 | Open this help file |
| Ctrl+/ | Open Command Finder (search all commands) |
| Ctrl+J | Leader key (press, release, then press a second key) |
| Ctrl+Shift+M | Switch between Classic and Modern tuning mode |
| Ctrl+Shift+L | Enter or exit Logging mode |
| Ctrl+M | Toggle the meters panel on/off |
| Ctrl+Shift+W | Open the Audio Workshop |
| Ctrl+Shift+S | Speak full status (multi-slice aware) |
| Ctrl+Alt+S | Open the status dialog |
| Alt+Shift+S | Speak your current transmit (TX) status |
| Ctrl+F4 | Repeat the last spoken message |
| Ctrl+Shift+V | Cycle speech verbosity (Chatty, Terse, Off) |
| Ctrl+Shift+B | Toggle the braille status line |
| F12 | Stop CW transmission immediately |
| Ctrl+L | Open the Callbook / station lookup utility |

## The JJ "Leader" Key Commands

This application's got a problem. We own it, it's true. There's a ton of keyboard shortcuts that you can use in this application. Problem is, we have too many keyboard shortcuts for the number of keys on your keyboard. Enter the JJ key.

The JJ Key, `Ctrl+J` in JJ Flexible Radio Access, can be used to activate various toggles, options, and other commands throughout the JJ Flexible Radio Access application. Similar to pressing layered commands in JAWS (JAWS Key+J then another key), the JJ key is our flavor of command-key layering you can use to access functions we couldn't fit onto single-keypress hotkeys. The JJ layer is our secret cheat code — it lets you reach commands we couldn't squeeze into a single keypress. We hope the JJ layer keeps you from needing to use three fingers on one hand, your right pinky finger, and your left big toe to activate neural noise mitigation or turn on the audio peak meter. Who wants to be a contortionist — both physically and mentally — when you're trying to rack up points in a busy contest or chase DX? I know I sure don't.

Press `Ctrl+J` and then release it to enter layered command mode. You'll hear a rising tone to let you know that you've activated the JJ key layer and it's ready for you to press one of these keys to perform an action in the app.

| Key | Action |
|-----|--------|
| N | Toggle legacy Noise Reduction |
| B | Toggle Noise Blanker |
| W | Toggle Wideband Noise Blanker |
| R | Toggle Neural Noise Reduction |
| S | Toggle Spectral Noise Reduction |
| Shift+N | Toggle NR Filter |
| Shift+R | Toggle PC Neural Noise Reduction (runs on your computer, works on every radio) |
| Shift+S | Toggle PC Spectral Noise Reduction (runs on your computer, works on every radio) |
| A | Toggle Auto Notch |
| P | Toggle Audio Peak Filter (APF, CW only) |
| F | Speak the TX filter width |
| Shift+F | Speak the RX filter width |
| Ctrl+F | Enter a frequency |
| D | Toggle tuning speech debounce |
| L | Speak log statistics |
| M | Open the memories dialog |
| T | Toggle meter tones on/off |
| Shift+T | Toggle alert sounds (earcons) on/off |
| Shift+A through Shift+H | Jump to that slice from anywhere (Shift+F is reserved for the RX filter readout) |
| ? or H | List all leader key commands |
| Escape | Cancel leader mode |

Don't worry if you press the JJ key by accident or if you simply don't want to access the command layer. Press Escape to exit JJ command mode — you'll hear a little descending tone, and you can go back to whatever you were doing previously in the JJ Flexible Radio app. The layer waits patiently until you press a key or cancel; there's no timer sneaking you out of it.

## Band Jumping

These keys let you switch bands on your radio instantly. They work in both Classic and Modern tuning. Something that may help you remember which keys do what when it comes to band switches is that you can switch to standard amateur radio bands by pressing F-keys without any modifiers. WARC bands like 30, 17, and 12 meters — plus 60 meters — are accessed by pressing F-keys with the Shift modifier.

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
| F2 | Go to Home (this is where you adjust frequencies or other radio options — see more information about the JJ Flexible Home below) |
| Ctrl+F | Set frequency (direct entry) |
| Ctrl+S | Read the S meter |
| Ctrl+Shift+F | Toggle the frequency speech readout on/off |
| Ctrl+Alt+F | Speak the RX filter values |
| Ctrl+Shift+C | Clear RIT offset |
| Alt+Z | Activate the CW zero beat option (requires an SDR-Plus subscription) |
| Ctrl+P | Adjust audio panning |
| Ctrl+Shift+T | Toggle the tune carrier on/off |
| Ctrl+T | Start an automatic tuning unit (ATU) tune cycle |
| Ctrl+Alt+M | Toggle meter sonification tones |
| Ctrl+Alt+P | Cycle the meter tone preset (RX, TX, Full Monitor) |
| Ctrl+Alt+V | Speak the current meter values |
| Shift+M | Mute or unmute every slice at once |
| Shift+Comma | Release every slice except the first |
| Escape | Collapse the field group you're in, or return to Home |
| Double tap Escape (quickly) | Collapse all open field groups and return to Home |

### TX Filter Sculpting

| Key | Action |
|-----|--------|
| Ctrl+Shift+[ | Move the TX filter low edge down |
| Ctrl+Shift+] | Move the TX filter low edge up |
| Ctrl+Alt+[ | Move the TX filter high edge down |
| Ctrl+Alt+] | Move the TX filter high edge up |

### Push to Talk

These work while your focus is in the JJ Flexible Home:

| Key | Action |
|-----|--------|
| Ctrl+Space | Push to talk — transmit while held |
| Shift+Space | Toggle a transmit lock on or off |
| Escape | Stop transmitting (while a transmit lock is on) |

### RX Filter Adjustment

The bracket keys shape your receive filter from anywhere in the radio modes:

| Key | Action |
|-----|--------|
| [ or ] | Widen the filter: [ moves the lower edge down, ] moves the upper edge up |
| Shift+[ or Shift+] | Slide the passband left or right |
| Ctrl+[ or Ctrl+] | Squeeze or pull both filter edges |
| Alt+[ or Alt+] | Cycle filter presets |
| [[ or ]] (double-tap) | Enter single-edge adjust mode — brackets then move just that edge, Escape exits |

## Notes on the JJ Flexible Home

These keys work from ANY field within your JJ Flexible Home. First, remember to press F2 to access your radio interface home. Press the key and you will activate the following actions:

| Key | Action |
|-----|--------|
| M | Toggle mute on the active slice |
| V | Cycle to the next slice |
| R | Toggle RIT on/off |
| X | Toggle XIT on/off |
| Q | Toggle squelch on/off |
| = | Transceive current slice (set both RX and TX to this slice) |
| Shift+M | Mute or unmute every slice at once |
| Shift+Comma | Release every slice except the first |
| ? | Speak the keys for the field you're on |

Navigation inside the Home: Left and Right arrows move one character at a time, Home jumps to the first field (except on the Slice field, where it pans center), End jumps to the last field, and Page Down jumps straight to the Frequency field from fields that don't use Page Down themselves.

## JJ Flexible Home — Slice Field Keys

When focused on the Slice field specifically:

| Key | Action |
|-----|--------|
| Space | Cycle to the next slice (wraps around) |
| Up / Down | Next or previous slice |
| 0-7 or A-H | Jump directly to the desired slice by number or letter (you'll hear "not created" if it doesn't exist yet) |
| T | Make this slice the TX slice |
| . (period) | Create a new slice |
| , (comma) | Release the current slice |
| Page Up | Pan hard right |
| Home | Pan center |
| Page Down | Pan hard left |

## JJ Flexible Home — Slice Operations Field Keys

When focused on the Slice Operations field (per-slice audio controls):

| Key | Action |
|-----|--------|
| Up / Down | Adjust volume up/down |
| Page Up / Page Down | Pan right / left |
| Space | Toggle mute |
| M | Mute |
| S | Sound (unmute) |
| A-H | Jump directly to that slice |
| T | Set the currently selected slice to transmit (TX) |
| = | Transceive the currently selected slice |

## JJ Flexible Home — Squelch and Squelch Level Field Keys

On the Squelch field, Space, Up, Down, or Q all toggle squelch on and off. On the Squelch Level field:

| Key | Action |
|-----|--------|
| Up / Down | Raise or lower the squelch level |
| Q | Toggle squelch on/off |

## JJ Flexible Home — Frequency Field Keys (Classic tuning mode)

When focused on the Frequency field in Classic tuning mode:

| Key | Action |
|-----|--------|
| Up / Down | Tune by the digit under the cursor |
| U / D | Same as Up and Down |
| Digits | Type a frequency, then Enter to apply |
| K | Round to the nearest kilohertz |
| + then digits | Set a step multiplier (for example, + then 25 at the 1 kHz position tunes by 25 kHz) |
| F | Speak the current frequency |
| S | Turn split on |
| T | Toggle showing the transmit frequency |

## JJ Flexible Home — Frequency Field Keys (Modern tuning mode)

When focused on the Frequency field in Modern tuning mode:

| Key | Action |
|-----|--------|
| Up | Tune up by your coarse step |
| Down | Tune down by your coarse step |
| Shift+Up | Tune up by your fine step |
| Shift+Down | Tune down by your fine step |
| Digits | Type a frequency, then Enter to apply |
| F | Speak the current frequency |
| Shift+S | Speak both your coarse and fine step sizes |

The coarse and fine step values are configured in Settings → Tuning. Coarse and fine each have a single step value, so there's no mode to switch and no list to cycle through.

## JJ Flexible Home — RIT and XIT Field Keys

When focused on the RIT or XIT field, the digits 1, 2, 3, and 4 enter a quick scale-adjust mode for offset tuning. This is the Don-driven workflow for chasing a drifting correspondent without having to navigate through decade fields:

| Key | Action |
|-----|--------|
| 1 | Enter scale-adjust mode at 1 Hz |
| 2 | Enter scale-adjust mode at 10 Hz |
| 3 | Enter scale-adjust mode at 100 Hz |
| 4 | Enter scale-adjust mode at 1 kHz |
| Up / Down | Apply the chosen scale to the offset — or, outside scale-adjust mode, adjust by the digit under the cursor |
| 5–9 | Type a digit at the cursor position (legacy field behaviour) |
| Space | Toggle RIT or XIT on/off |
| + / - | Make the offset positive or negative |
| = | On the RIT field only: copy RIT to XIT |
| 0 | Exit scale-adjust mode |
| Escape | Exit scale-adjust mode |
| R or X | Toggle RIT or XIT off — also exits scale-adjust mode |

You'll hear a rising mode-enter tone when scale-adjust starts and a descending mode-exit tone when it ends. The mode is also exited automatically when you navigate to a different field — there's no inactivity timeout to surprise you mid-QSO.

## JJ Flexible Home — Transmit Slice Field Keys

The Transmit slice field sits after VOX and shows which slice keys the radio ("-" when none does). These keys work while it has focus:

| Key | Action |
|-----|--------|
| Space | Set transmit to the active slice |
| Up / Down | Move transmit to another slice |
| A–H | Set the transmit slice by letter |
| Delete or Backspace | Clear the transmit slice (no slice keys the radio — a soft transmit lockout) |

On a receive-only connection these keys speak a refusal instead of acting silently. The same controls live in the Slice menu under Transmit Slice.

## JJ Flexible Home — Mute and Volume Fields (Classic tuning only)

On the Mute field, Space or M toggles mute. On the Volume field, Up and Down adjust the volume. (Modern tuning handles these through the universal M key and the Audio expander.)

## Mode Switching

| Key | Action |
|-----|--------|
| Alt+M | Next mode (cycles through available modes) |
| Alt+Shift+M | Previous mode |
| Alt+U | Switch to Upper Side Band (USB) |
| Alt+L | Switch to Lower Side Band (LSB) |
| Alt+C | Switch to CW mode |
| Alt+A | Switch to AM mode |
| Alt+F | Switch to FM mode |
| Alt+D | Switch to DIGU (digital upper) mode |
| Alt+Shift+D | Switch to DIGL (digital lower) mode |

## Audio Controls

Volume, headphone level, and line-out level live in the Audio expander. Press `Ctrl+Shift+U` to open the Audio expander, then arrow to the level you want and use Up / Down (or Page Up / Page Down for big jumps, Home / End for minimum / maximum, and Enter to type an exact value).

The previous `Alt+Page Up`, `Alt+Shift+Page Up`, and `Shift+Page Up` shortcuts (and their `Page Down` counterparts) no longer adjust audio. The slots are reserved on purpose so a future feature can claim them deliberately.

## Scanning

Scanning commands mostly live in the Command Finder these days — only stopping and resuming have keys out of the box (you can give the others keys in the Hotkey Editor):

| Key | Action |
|-----|--------|
| Ctrl+Z | Stop scan |
| Ctrl+Shift+F2 | Resume scan |

Start scan, saved scan, and memory scan have no default keys — find them in the Command Finder (`Ctrl+/`), or bind your own keys in Tools → Hotkey Editor.

## DX and Spotting

| Key | Action |
|-----|--------|
| Alt+Shift+X | Open DX Cluster |
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
| F6 | Switch between log panes |
| Ctrl+Shift+N | Log characteristics dialog |
| Ctrl+Alt+L | Open full log entry form |

Log statistics has no default key — press `Ctrl+J` then `L` to hear your stats, or bind a key in the Hotkey Editor.

The radio pane inside the logging view has its own tuning keys: Up and Down tune by one step, Shift+Up and Shift+Down tune by ten steps, Left and Right change the step size, and Ctrl+F enters a frequency directly.

## ScreenFields Quick Access

The ScreenFields panel has five expandable categories you can toggle open and closed instantly:

| Key | Category |
|-----|----------|
| Ctrl+Shift+R | Receiver |
| Ctrl+Shift+N | DSP |
| Ctrl+Shift+U | Audio |
| Ctrl+Shift+X | Transmission |
| Ctrl+Shift+A | Antenna |

**Tip:** Each category expands to show its controls. Press the hotkey again to collapse it. Remember that you can also collapse any field group by pressing Escape. Press Escape twice quickly to close all field categories. `Ctrl+Tab` and `Ctrl+Shift+Tab` move between categories.

## View Controls

| Key | Action |
|-----|--------|
| Ctrl+Shift+F3 | Move focus to the received text box |
| Ctrl+Shift+F4 | Move focus to the CW send text box |
| Ctrl+Shift+F5 | Move focus to the CW send text box (currently the same as Ctrl+Shift+F4 — a distinct direct-keying mode is planned for a future release) |

## Radio Selector

These keys are active in the Select Radio dialog — the window that opens at startup and whenever you connect to a radio. They are dialog accelerators, not global hotkeys: they work only while the selector is open.

| Key | Action |
|-----|--------|
| Enter | Connect to the selected radio (on an offline radio, starts looking for it instead) |
| Up / Down arrow | Move through the radio list. Arrows stay inside the list at both ends |
| Tab | Leave the radio list. Shift+Tab returns you to the row you left |
| Applications key, or Shift+F10 | Open the radio's context menu: Connect, Add or Remove Favorite, Auto-Connect Settings |
| Alt+N | Connect |
| Alt+L | Low bandwidth for the selected radio |
| Alt+P | Connection path — choose local network or SmartLink for a radio reachable both ways |
| Alt+R | Remote. Becomes Refresh Remote List once your remote radios are listed |
| Alt+S | SmartLink account. The label follows what you have saved: Sign in to SmartLink, SmartLink Account, or Switch Account |
| Alt+T | Test the connection to the selected radio |
| Alt+A | Auto-connect settings for the selected radio |
| Alt+C, or Escape | Cancel and close the selector |

## Commands With No Default Key

These commands exist and work — they just don't ship with a key. Run them from the Command Finder (`Ctrl+/`), or give them keys of your own in Tools → Hotkey Editor: start scan, saved scan, memory scan, speak frequency, speak TX filter width, show memories, switch S meter units, toggle continuous frequency display, PC audio on/off, select audio device, ATU memories, reboot radio, transmit controls, radio menus, collect debug info, mute slice, show keys help, and the remaining log field jumps (log file name, log mode, log rig, log antenna, log statistics).

## Trace Archive Browser

These keys are active when focus is on the row list inside the Archive Browser tab of the Tracing dialog (Operations → Tracing → Archive Browser).

| Key | Action |
|-----|--------|
| Enter | Open the selected trace in your default text viewer |
| Ctrl+C | Copy the selected trace's full file path to the clipboard |
| Delete | Delete the selected trace(s), with a confirmation prompt |
| Ctrl+A | Select every row in the current filter |
