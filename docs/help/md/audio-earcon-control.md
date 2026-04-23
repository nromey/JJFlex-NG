# Earcon Control

"Earcons" are the short alert sounds that JJ Flexible Radio Access plays for events such as dialog open, error, confirmation, CW notifications, and other background event cues. They are useful, but they are also audio — and sometimes you just want them quiet without digging into the Settings dialog.

## Quick Mute via the Leader Key

The fast way to mute every earcon at once is through the leader key. Press `Ctrl+J` to enter leader-key mode, then press `Shift+T`. Every earcon mutes (or unmutes again if you press the same sequence a second time).

- The quick mute only affects the earcon layer. It does not touch your meter tones, the radio audio, or the speech output — just the alert-sound layer.
- You will hear a short confirmation earcon as the quick mute toggles, indicating whether earcons are now on or off.
- The setting lasts for your current session. If you quick-mute, then close and reopen the application, earcons come back on by default. For a longer-term mute, use the Settings dialog (see below).

## When to Use the Quick Mute

- While contesting with headphones, where every extra sound competes for your attention.
- While recording a session or a demo, when you want a clean audio track.
- During a Zoom or Teams call, when you are sharing your audio and the earcons would leak out to the other participants.
- Any situation where you want to hear the radio without application chrome.

## Long-Term Disable

Under **Settings > Audio > Earcons** you will find a master enable/disable switch, plus finer controls for each earcon category (dialog, error, status, and so on). Use those controls if you want earcons off permanently, or off only for a specific category. The quick-mute leader-key sequence does not override these settings — the quick mute is a temporary layer on top.

## What Earcons Are Not

- **Not meter tones.** Meter tones are the continuous audio that represents SWR, ALC, and forward power during transmit. They have their own on/off controls — see the Meter Tones help page.
- **Not CW notifications.** Those live on a separate channel (see the CW Notifications help page) and have their own enable/disable in settings.
- **Not the radio audio.** You cannot mute earcons to silence the radio — you would be muting a tiny fraction of your audio while the big signal still plays through.

## Finding the Hotkey Again

If you forget the exact key sequence, open the Command Finder (press `Ctrl+/`) and search for "earcon" or "mute." The Command Finder lists every leader-key combination along with the regular hotkeys, and it will bring you straight to this one.
