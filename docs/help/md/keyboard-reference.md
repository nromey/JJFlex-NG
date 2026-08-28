# Keyboard Reference

Welcome to your one-stop reference for every keyboard shortcut in JJ Flexible Radio Access. You can also press `Ctrl+/` at any time to open the Command Finder, which lets you search for commands by name. And here's the newest trick: while you're in the JJ Flexible Home, press `?` on any field and the app speaks the keys that work right there.

Almost every shortcut below can be changed. Open Tools, then Hotkey Editor, pick a command, and press the new key you want — the app tells you about any conflict and asks before taking a key away from another command. Help → Key Assignments opens the same list for browsing. Commands listed as having no key can be given one there too.

JJ Flexible Radio Access has three operating scopes: some keys work everywhere (Global), some only work when you're controlling the radio (Radio scope, which includes both Classic and Modern tuning modes), and some only work when you're in the logging pane (Logging scope).

## Global Hotkeys

These keys work no matter where you are in the application — and "no matter where" now honestly includes dialogs. For a long while a Global key pressed inside a dialog quietly did nothing, or worse, landed on a dialog button that happened to share the keystroke (Alt+Shift+S inside the Audio Workshop used to save a preset instead of speaking your transmit status). That's fixed: press a Global key anywhere and its real command answers. A dialog's own keys still come first, so nothing a dialog needs is taken away from it.

- **F1** — Open this help file
- **Ctrl+F1** — Explain the control you are sitting on — the longer story its short name leaves out, plus whatever the note beneath it currently says. If there's nothing extra, it says so honestly
- **Ctrl+/** — Open Command Finder (search all commands)
- **Ctrl+J** — The JJ key (press, release, then press a second key)
- **Ctrl+Shift+M** — Switch between Classic and Modern tuning mode
- **Ctrl+Shift+L** — Enter or exit Logging mode
- **Ctrl+M** — Show or hide the meters panel. This opens and closes the panel and nothing else — it no longer starts or stops your meter tones. The tone switch is Ctrl+J then T
- **Ctrl+Shift+W** — Open the Audio Workshop
- **Ctrl+Shift+S** — Speak full status (multi-slice aware)
- **Ctrl+Alt+S** — Open the status dialog
- **Alt+Shift+S** — Speak your current transmit (TX) status
- **Ctrl+F4** — Repeat what was just spoken — and keep pressing it. Within about six seconds each further press steps back through the last ten messages, wrapping round at the oldest. Leave it longer and the next press starts again from the most recent
- **Ctrl+Shift+V** — Cycle speech verbosity (Chatty, Terse, Off)
- **Ctrl+Shift+B** — Toggle the braille status line
- **F12** — Stop CW transmission immediately
- **Ctrl+L** — Open the Callbook / station lookup utility

## The JJ Key Commands

This application's got a problem. We own it, it's true. There's a ton of keyboard shortcuts that you can use in this application. Problem is, we have too many keyboard shortcuts for the number of keys on your keyboard. Enter the JJ key.

The JJ Key, `Ctrl+J` in JJ Flexible Radio Access, can be used to activate various toggles, options, and other commands throughout the JJ Flexible Radio Access application. Similar to pressing layered commands in JAWS (JAWS Key+J then another key), the JJ key is our flavor of command-key layering you can use to access functions we couldn't fit onto single-keypress hotkeys. The JJ Command layer is our secret cheat code — it lets you reach commands we couldn't squeeze into a single keypress. We hope the JJ layer keeps you from needing to use three fingers on one hand, your right pinky finger, and your left big toe to activate neural noise mitigation or turn on the audio peak meter. Who wants to be a contortionist — both physically and mentally — when you're trying to rack up points in a busy contest or chase DX? I know I sure don't.

Press `Ctrl+J` and then release it to enter the JJ Command layer. You'll hear a rising tone to let you know that you've opened the layer and it's ready for you to press one of these keys to perform an action in the app.

<!-- LEADER-KEY-TABLE: every key line from here to the END marker is checked against KeyInventory.LeaderCommands by Radios.Tests/LeaderDocCoverageTests. Add a chord to the layer and this list fails until it has a line; delete a chord and a leftover line fails too. The wording is yours — only the set of keys is checked. The six groups below, and their order, match leader-key.md on purpose: it is the same list twice, so it should be the same walk twice.

     ONE LINE PER CHORD, and the shape is load-bearing, not a style: a hyphen, a
     space, the chord in bold, a space, an em dash, a space, then the meaning.

         - **Shift+N** — Toggle NR Filter

     The chord is the KeyDisplay string with the "Ctrl+J, " prefix dropped. Write
     it any other way — a hyphen instead of the em dash, no bold, a markdown
     table — and the reader will not see the line at all; the chord then reads as
     undocumented and the build goes red naming it. That is deliberate. A reader
     that quietly accepts several shapes is one that eventually accepts none and
     reports perfect agreement about an empty set. -->

### DSP toggles

- **N** — Toggle legacy Noise Reduction
- **Shift+N** — Toggle NR Filter
- **B** — Toggle Noise Blanker
- **W** — Toggle Wideband Noise Blanker
- **R** — Toggle On-Radio Neural Noise Reduction (the radio's own DSP)
- **S** — Toggle On-Radio Spectral Noise Reduction (the radio's own DSP)
- **Shift+R** — Toggle PC Neural Noise Reduction (runs on your computer, works on every radio)
- **Shift+S** — Toggle PC Spectral Noise Reduction (runs on your computer, works on every radio)
- **Q** — Capture a noise profile for PC Spectral NR — press Q again while it runs to cancel (see "Noise capture" below)
- **A** — Toggle Auto Notch
- **P** — Toggle Audio Peak Filter (APF, CW only)

### Audio and transmit

- **V** — Enter volume mode — pick what to adjust, ride the arrows (see "Volume mode" below)
- **Alt+P** — Enter pan mode — Left and Right place the slice you're on in the stereo field, Shift moves by one, Home or C centers, Enter keeps it, Escape puts it back. Speaks positions in words at chatty verbosity and numbers at terse
- **K** — Mic check — speak your mic-audio verdict and level, nothing else (see below)
- **G** — Arm or disarm the TX test tone (it replaces your microphone while transmitting)
- **C** — Toggle Compander
- **Ctrl+A** — Turn PC audio on or off — whether radio audio plays through this computer at all. (`Ctrl+J`, `V`, `P` rides how *loud* it plays; this is the switch, and it tells you which way it went)
- **Shift+P** — Toggle Speech Processor
- **E** — Echo the CW notifications you just heard — press again to step further back (see "CW echo" below)

### Filter information

- **F** — Speak the TX filter width
- **Shift+F** — Speak the RX filter width
- **Ctrl+F** — Enter a frequency

### Meter and tuning

- **T** — Toggle meter tones on/off
- **Shift+T** — Toggle alert sounds (earcons) on/off
- **D** — Toggle tuning speech debounce
- **Ctrl+Q** — Start or stop the QSO signal analyzer — watch the S-meter, then hear what the signal did, QSB and all (see "QSO signal analyzer" below)
- **Ctrl+S** — Switch the S-meter between S-units and dBm. It tells you which one you landed on, and it stays that way for this radio until you change it back — so `Ctrl+S` on its own reads the meter in whichever unit you chose. One S-unit is 6 dB, so "S7" covers a lot of ground; dBm is what you want when you're asking whether that antenna change actually helped. It's also on the Radios tab in Settings, if you'd rather see where it stands than press a key to find out

### Status, information, and slices

- **O** — Say what is still running and what it is costing — recording, captures, meter tones (see "What is still running" below)
- **Ctrl+D** — Start or stop a detailed capture of what the app is doing — works with no radio connected, and from inside any dialog. Stopping saves the capture as its own session in Saved Diagnostic Logs
- **Ctrl+R** — Read the problems recorded this session — everything that has gone wrong since you started, in case you missed an announcement
- **Alt+V** — Speak the version, the build type and the date this copy was built (see "Which build am I on" below)
- **L** — Speak log statistics
- **M** — Open the memories dialog
- **Shift+A through Shift+H** — Jump to that slice from anywhere (Shift+F is reserved for the RX filter readout)

### Help

- **? or H** — List all JJ Command layer commands
- **Escape** — Close the JJ Command layer

<!-- END LEADER-KEY-TABLE -->

Don't worry if you press the JJ key by accident or if you simply don't want to access the command layer. Press Escape to leave the JJ Command layer — you'll hear a little descending tone, and you can go back to whatever you were doing previously in JJ Flexible. The layer waits patiently until you press a key or cancel; there's no timer sneaking you out of it.

Press a key the layer doesn't know and it now says "Unknown key. H for the list, Escape to cancel." — and then it actually waits for you. After an unknown key the layer stays open for those three keys only: `H` and `Shift slash` read you the command list, `Escape` closes it. Any other key and the layer has already let go, so that key does its ordinary job. The full story is on the JJ Key Commands page.

One more thing worth knowing: the JJ key's built-in help (`Ctrl+J` then `H` or `Shift slash`) reads you every command in the JJ Command layer, top to bottom. If what you actually want is help for the control you're sitting on, that's Ctrl+F1 — F1 on its own opens this file. If you want to search every command in the app by name, that's `Ctrl+/` for the Command Finder. The JJ help announcement now ends by pointing you at both.

### Volume mode — Ctrl+J, then V

Every volume in the app, one gesture. Press `Ctrl+J`, then `V`, and you're in volume mode: pick a target with a single letter, then ride Up and Down. Every press speaks the new value. The mode stays put while you adjust and even while you switch targets — set your mic, hop to PC output, nudge it, hop back — and only Escape ends it. If you're a JAWS or NVDA user this layered-keystroke pattern will feel like home.

The targets:

- **H** — on-radio headphone volume. The headphone jack on the radio itself.
- **P** — PC output volume, in dB of boost. This is how loud radio audio plays *through your computer* — the one remote operators actually want. Ranges 0 to 24 dB; the long-time default is 12 dB.
- **M** — mic level, your transmit audio level. Applies to PC audio too.
- **L** — on-radio line out volume. The line out jacks on the radio itself.
- **C** — compander level. (Toggle the compander itself with `Ctrl+J`, `C`.)
- **S** — speech processor mode: Up and Down step through Normal, DX, and DX plus. (Toggle the processor with `Ctrl+J`, `Shift+P`.)

Press `?` inside volume mode to hear the target list again. Escape exits and announces it. A quick word on "on-radio": those two targets move the radio's own jacks — if you're listening over PC audio from across town, they won't change what you hear, and now they say so right in their names.

### Pan mode — Ctrl+J, then Alt+P

Stereo placement is how you keep two signals apart: slice A a little left, slice B a little right, and a pileup turns into two conversations instead of one mush. Hard left and hard right aren't separation, they're exile — what you usually want is *slightly* off center, and that's exactly what this mode is for.

Press `Ctrl+J`, then `Alt+P`, and you're in pan mode for the slice you're on. The keys:

- **Left and Right arrows** — nudge the slice through the stereo field. Hold an arrow and it sweeps.
- **Shift with an arrow** — move by one, for placing it exactly.
- **Home or C** — snap to center. It's the value you come back to, so it's one key away.
- **Enter** — keep the new pan and leave the mode.
- **Escape** — put the pan back where it was when you entered, out loud, and leave. Overshot? This is the way back.
- **?** — hear where you are, where Escape would take you, and the keys.
- Any other key keeps the pan, announces the mode closed, and then does its normal job — you can't get stuck in here.

What it speaks follows your speech verbosity. At chatty it talks in positions — "slightly left", "center", "hard right" — the words you want when you're placing a signal by ear. At terse it gives you the number — "Pan 40" on a 0-to-100 scale where 0 is hard left, 50 is center, and 100 is hard right — precise and repeatable, for jotting down an arrangement you like and dialing it in again tomorrow. Cycle verbosity with `Ctrl+Shift+V` right inside the mode and the very next nudge speaks in the other form.

The coarse keys you already know are untouched: on the Slice field, Page Up still slams hard right, Home centers, and Page Down slams hard left, and the Slice Operations field still nudges with Page Up and Page Down. Pan mode is the fine control beside them — reachable from anywhere, not just those two fields.

Pan is per slice, and it lives in the radio, not in this app. Like the rest of your slice layout, "Save Station Setup to Radio" on the Slice menu is what makes an arrangement survive.

### Mic check — Ctrl+J, then K

The binding you ride while setting mic gain. One chord, one answer: your mic-audio verdict and level — "Good. That's the sweet spot, right there. Peak minus 9 dBFS" — and nothing else in front of it. While you're transmitting it follows the last second and a half of audio, so each gain change is audible in the next check; while you're receiving it reports your last transmission's peak. You can change how the answer reads (plain English, decibels, or both) under Settings, Notifications, "Mic audio readout."

### Test tone — Ctrl+J, then G

Arms or disarms the Audio Workshop's TX test tone from anywhere, using your saved frequency and level — no need to open the workshop first. Same honesty rules as the workshop: if the tone can't reach the transmitter (PC audio off, transmit input not set to PC, CW mode), it refuses to arm and tells you why; if your tone frequency sits outside the transmit filter, it arms but warns you loudly that nothing will go out. While armed, every key-down announces that the tone is riding your transmission instead of your voice.

### Noise capture — Ctrl+J, then Q

Captures a noise profile for PC Spectral NR: three seconds (adjustable, 1 to 5) of what your band sounds like with nobody talking, so the spectral engine knows exactly what to subtract. Find a quiet spot on the band, press `Ctrl+J` then `Q`, and listen — it announces the start, counts the seconds out loud as they pass, and tells you when the profile is captured and whether Spectral NR is using it. Press `Q` again mid-capture to cancel. The capture listens to the radio audio playing through this computer, so PC audio has to be on — if it isn't, the capture says so instead of pretending. A finished capture saves itself and comes back on your next connect; naming and managing profiles lives in the Noise Profiles dialog (Slice menu, DSP, PC Noise Reduction). The full story is on the PC-Side Noise Reduction help page.

### QSO signal analyzer — Ctrl+J, then Ctrl+Q

"Is he fading? Is that QSB? Is he coming up?" You can't answer that by
listening to a stream of spoken meter readings — a pattern over time is
exactly what a human can't extract from numbers read one at a time, and the
speech would sit on top of the very signal you're trying to hear. So this one
works like a stopwatch instead: press `Ctrl+J` then `Ctrl+Q` when a station
starts talking, work the contact in silence, and press it again when you want
the answer. You'll hear the story of the signal: "Peaked S 9, fell to S 4,
averaged S 6. Deep fades about every 5 seconds." That's a real signal report
with evidence behind it — the kind of thing that goes straight in your log or
right back on the air.

It runs until you stop it — there's no timer, because a capture that quit
early mid-fade would hand you a confident answer built on half the story. The
safety net is the running-cost machinery: `Ctrl+J` then `O` will tell you a
capture is going, it speaks up on its own if you leave it running past
fifteen minutes, and closing the app asks about it on the way out.

Every capture saves itself when you stop it. The full report — peak, trough,
average, swing, fade rhythm and depth, trend, how long you transmitted, and
exactly how each number was measured — lives under Tools, then Signal
captures, where you can read it, rename it (a capture named "Don on 40
meters" beats "the one from 9:14" a week later), export it as a web page or
plain text to send to someone, or delete it. Anything the capture could NOT
determine says so in as many words: a ten-second capture doesn't guess at a
fade rhythm, it tells you it was too short to see one.

Your own transmissions are left out of the measurements automatically — while
you're keyed up, the S-meter isn't describing the other station. And if no
readings arrive at all, the report says exactly that, because "no data" and
"a quiet band" should never sound the same.

### CW echo — Ctrl+J, then E

Ctrl+F4 walks back through the last ten things the app *said*. This is the same idea for the last ten things it *sent in CW* — the slice census, "SL A USB", anything the app keyed at you. Press `Ctrl+J` then `E` and it re-sends the most recent one. Press it again and you step back another message, and another, wrapping round to the newest when you run off the end.

The two histories are kept apart on purpose. If you're running with speech off and CW notifications on, everything you've heard is in the CW list and nothing is in the speech list, so one key for both would spend most of its presses telling you about messages you never heard.

Two things worth knowing. The prosigns — AS, BT, SK — stay out of the history. They're punctuation, not information, and re-sending "closing" out of the blue tells you nothing you can act on. And "press again to step back" gives you the same generous window the speech version does, but measured from the moment the CW *finishes* rather than from when you pressed. That matters if you run slow code: at 10 words per minute "SL A USB" takes nearly nine seconds to send, and a window that started ticking at the keypress would have expired before you'd finished listening to it.

### Silencing CW — Ctrl, the key you already press

Ctrl has silenced your screen reader since forever — you press it without thinking. It now silences CW notifications too, in the same press. No new key to learn, no separate reflex: one tap of either Ctrl and both channels go quiet, whatever window you're in. Your screen reader still gets the key and still stops its own speech, exactly as before.

While we were at it, CW notifications stopped piling up. Arrow across four slices quickly and you hear the slice you *landed on*, not a recital of everywhere you passed through — a newer message replaces the one still waiting, finishing the character in flight first so you never hear a mangled half-letter. The connect and disconnect prosigns are the exception: BT and the 73 sign-off always play out in full. Some things you don't cut off.

### What is still running — Ctrl+J, then O

O for "what's on". Press `Ctrl+J` then `O` and JJ Flexible tells you every expensive thing it currently has switched on, and what each one has cost so far: "Meter stream recording, 218,000 meter lines into the log, and it will still be on the next time you start. The diagnostic log, 1.2 megabytes." If nothing is running, it says that too.

This one exists because sighted operators get it for free. They have a recording light in the corner of the screen, a meter they can watch moving, a panel that's obviously open. We don't, and a switch that stays on across restarts, quietly changes what the app writes to disk, and never says a word about itself is exactly the kind of thing this program is supposed to fix.

There's more to it than the key. If something you left on grows past a sensible size, JJ Flexible now says so on its own — once, when it actually crosses the line, not on a nagging timer. And if you close the app with recording still going, it tells you what's still on before it goes, and offers to turn it off for you on the way out.

### Which build am I on — Ctrl+J, then Alt+V

V for Version, with Alt on it because plain `V` has been volume mode for a while now and isn't moving.

Press `Ctrl+J` then `Alt+V` and JJ Flexible says the version number, whether it's a test build or a release build, and the date it was built: "Version 4.1.16.1024, Debug, built August 27, 2026." That's it — short enough to read straight back to me in an email.

This is the answer to a question that comes up in every single bug report, and until now the only place to find it was Help, About — a dialog you have to go and open, which is a nuisance when you're already in the middle of describing something that just went wrong. Now it's one chord from wherever you are, including from inside a dialog.

The build date matters more than you'd think. File dates lie: by the time a test build has travelled through Dropbox to you, the date on the file is the date it arrived, not the date I built it. The date this key speaks is stamped inside the build itself, so it's the real one no matter how the build reached you.

If you want the whole picture — every component version, the exact commit, where your trace file lives — that's still Help, About, and it's still the right place for it.

One more thing about the JJ layer: it works inside dialogs now. Press `Ctrl+J` in the Audio Workshop — or any other dialog — and the layer answers exactly as it does from Home. The mic check was built with that in mind, since the workshop is precisely where you sit while adjusting mic gain.

## Band Jumping

These keys let you switch bands on your radio instantly. They work in both Classic and Modern tuning. Something that may help you remember which keys do what when it comes to band switches is that you can switch to standard amateur radio bands by pressing F-keys without any modifiers. WARC bands like 30, 17, and 12 meters — plus 60 meters — are accessed by pressing F-keys with the Shift modifier.

- **F3** — 160 meters
- **F4** — 80 meters
- **F5** — 40 meters
- **F6** — 20 meters
- **F7** — 15 meters
- **F8** — 10 meters
- **F9** — 6 meters
- **Shift+F3** — 60 meters
- **Shift+F4** — 30 meters
- **Shift+F5** — 17 meters
- **Shift+F6** — 12 meters
- **Alt+Up** — Next band up
- **Alt+Down** — Next band down
- **Alt+Shift+Up** — 60m channel up (when on 60 meters)
- **Alt+Shift+Down** — 60m channel down (when on 60 meters)

## Radio Control (Classic and Modern Tuning Modes)

These keys work when you're in either Classic or Modern tuning mode:

- **F2** — Go to Home (this is where you adjust frequencies or other radio options — see more information about the JJ Flexible Home below)
- **Ctrl+F** — Set frequency (direct entry)
- **Ctrl+S** — Read the S meter, or forward power in watts while transmitting
- **Ctrl+Shift+F** — Toggle the frequency speech readout on/off
- **Ctrl+Alt+F** — Speak the RX filter values
- **Ctrl+Shift+C** — Clear RIT offset
- **Alt+Z** — Activate the CW zero beat option (requires an SDR-Plus subscription)
- **Ctrl+P** — Adjust audio panning
- **Ctrl+Shift+T** — Toggle the tune carrier on/off
- **Ctrl+T** — Start an automatic tuning unit (ATU) tune cycle
- **Ctrl+Alt+M** — Toggle meter tones on or off. The same switch as Ctrl+J then T, and it now says the same thing either way
- **Ctrl+Alt+P** — Cycle the meter tone preset (RX, TX, Full Monitor)
- **Ctrl+Alt+V** — Speak the current meter values
- **Shift+M** — Mute or unmute every slice at once
- **Shift+Comma** — Release every slice except the one you are on
- **Escape** — Collapse the field group you're in, or return to Home
- **Double tap Escape (quickly)** — Collapse all open field groups and return to Home

### TX Filter Sculpting

- **Ctrl+Shift+[** — Move the TX filter low edge down
- **Ctrl+Shift+]** — Move the TX filter low edge up
- **Ctrl+Alt+[** — Move the TX filter high edge down
- **Ctrl+Alt+]** — Move the TX filter high edge up

### Push to Talk

These work while your focus is in the JJ Flexible Home or in Home's field groups (the DSP, Audio, Receiver, Transmission, and Antenna expanders) — so you can key up while you're riding Mic Level without tabbing back to Home first:

- **Ctrl+Space** — Push to talk — transmit while held
- **Shift+Space** — Toggle a transmit lock on or off
- **Escape** — Stop transmitting (while a transmit lock is on)

### RX Filter Adjustment

The bracket keys shape your receive filter from anywhere in the radio modes:

- **[ or ]** — Widen the filter: [ moves the lower edge down, ] moves the upper edge up
- **Shift+[ or Shift+]** — Slide the passband left or right
- **Ctrl+[ or Ctrl+]** — Squeeze or pull both filter edges
- **Alt+[ or Alt+]** — Cycle filter presets
- **[[ or ]] (double-tap)** — Enter single-edge adjust mode — brackets then move just that edge, Escape exits

## Notes on the JJ Flexible Home

These keys work from ANY field within your JJ Flexible Home. First, remember to press F2 to access your radio interface home. Press the key and you will activate the following actions:

- **M** — Toggle mute on the active slice
- **V** — Cycle to the next slice
- **R** — Toggle RIT on/off
- **X** — Toggle XIT on/off
- **Q** — Toggle squelch on/off
- **=** — Transceive current slice (set both RX and TX to this slice)
- **Shift+M** — Mute or unmute every slice at once
- **Shift+Comma** — Release every slice except the one you are on
- **?** — Speak the keys for the field you're on

Navigation inside the Home: Left and Right arrows move one character at a time, Home jumps to the first field (except on the Slice field, where it pans center), End jumps to the last field, and Page Down jumps straight to the Frequency field from fields that don't use Page Down themselves.

No letter on the Home surface is ever silent. Press a letter that isn't bound on the field you're on and JJ Flexible says so — "S does nothing on the Volume field" — and points you at Shift slash, which speaks the keys that do work right there. With no radio connected, these keys answer "No radio connected" instead of pretending to be broken.

## JJ Flexible Home — Slice Field Keys

When focused on the Slice field specifically:

- **Space** — Cycle to the next slice (wraps around)
- **Up / Down** — Next or previous slice
- **0-7 or A-H** — Jump directly to the desired slice by number or letter (you'll hear "not created" if it doesn't exist yet)
- **T** — Make this slice the TX slice
- **. (period)** — Create a new slice
- **, (comma)** — Release the current slice
- **Page Up** — Pan hard right
- **Home** — Pan center
- **Page Down** — Pan hard left

## JJ Flexible Home — Slice Operations Field Keys

When focused on the Slice Operations field (per-slice audio controls):

- **Up / Down** — Adjust volume up/down
- **Page Up / Page Down** — Pan right / left
- **Space** — Toggle mute
- **M** — Mute
- **A-H** — Jump directly to that slice
- **T** — Set the currently selected slice to transmit (TX)
- **=** — Transceive the currently selected slice

There is deliberately no explicit "unmute" key here any more — the old S for "sound" mostly announced a state the slice was already in, so it read as a dead key and it's gone. M silences a slice fast without you having to know its current state, and Space toggles, which covers unmuting.

## JJ Flexible Home — Squelch and Squelch Level Field Keys

On the Squelch field, Space, Up, Down, or Q all toggle squelch on and off. On the Squelch Level field:

- **Up / Down** — Raise or lower the squelch level
- **Q** — Toggle squelch on/off

## JJ Flexible Home — Frequency Field Keys (Classic tuning mode)

When focused on the Frequency field in Classic tuning mode:

- **Up / Down** — Tune by the digit under the cursor
- **U / D** — Same as Up and Down
- **Digits** — Type a frequency, then Enter to apply
- **K** — Round to the nearest kilohertz
- **+ then digits** — Set a step multiplier (for example, + then 25 at the 1 kHz position tunes by 25 kHz)
- **F** — Speak the current frequency
- **P** — Toggle split on or off
- **T** — Toggle showing the transmit frequency

Heads-up for long-time users: split used to be S here, and S only turned it on. Split is P now, it toggles both ways, and it's the same P on this field in Modern tuning — S belongs to the step-size keys there, and one letter meaning two different things depending on your tuning mode was a trap.

Press Ctrl+F1 on the Frequency field for the live picture: it confirms Classic mode is active, names the digit your cursor is sitting on, reads this key map, and names the key that switches to Modern tuning.

## JJ Flexible Home — Frequency Field Keys (Modern tuning mode)

When focused on the Frequency field in Modern tuning mode:

- **Up** — Tune up by your coarse step
- **Down** — Tune down by your coarse step
- **Shift+Up** — Tune up by your fine step
- **Shift+Down** — Tune down by your fine step
- **Alt+Left** — Make your coarse step smaller
- **Alt+Right** — Make your coarse step larger
- **Shift+Left** — Make your fine step smaller
- **Shift+Right** — Make your fine step larger
- **Digits** — Type a frequency, then Enter to apply
- **F** — Speak the current frequency
- **S** — Choose both step sizes from a list
- **Shift+S** — Speak both your coarse and fine step sizes
- **P** — Toggle split on or off

There's one rule under all four arrow pairs, and it's worth learning once: **up and down tune, left and right size, and adding Shift makes it fine instead of coarse.** Up and Down move the frequency by your coarse step; Shift with them moves by your fine step. Left and Right change the step sizes themselves — Shift for the fine step, Alt for the coarse one. Left is always smaller, Right is always larger.

Alt is on the coarse pair rather than Shift because plain Left and Right already move your cursor across the Home fields, in both tuning modes, and that isn't going anywhere.

The sizes walk a short list of the values people actually use — 500 Hz, 1, 2, 5 and 10 kHz for coarse, and 1, 5, 10, 50 and 100 Hz for fine. The list doesn't wrap round: when you reach either end you'll hear the size followed by "smallest" or "largest", so you always know where you are rather than being dumped at the other end mid-QSO.

Press S to see the whole list and set both at once. Those are the same values Settings → Tuning offers, so wherever you set them you're choosing from the same list.

Press Ctrl+F1 on the Frequency field for the live picture: it confirms Modern mode is active, speaks your actual coarse and fine step values, reads this key map, and names the key that switches to Classic tuning.

## JJ Flexible Home — RIT and XIT Field Keys

When focused on the RIT or XIT field, the digits 1, 2, 3, and 4 enter a quick scale-adjust mode for offset tuning. This is the Don-driven workflow for chasing a drifting correspondent without having to navigate through decade fields:

- **1** — Enter scale-adjust mode at 1 Hz
- **2** — Enter scale-adjust mode at 10 Hz
- **3** — Enter scale-adjust mode at 100 Hz
- **4** — Enter scale-adjust mode at 1 kHz
- **Up / Down** — Apply the chosen scale to the offset — or, outside scale-adjust mode, adjust by the digit under the cursor
- **5–9** — Type a digit at the cursor position (legacy field behaviour)
- **Space** — Toggle RIT or XIT on/off
- **+ / -** — Make the offset positive or negative
- **=** — On the RIT field only: copy RIT to XIT
- **0** — Exit scale-adjust mode
- **Escape** — Exit scale-adjust mode
- **R or X** — Toggle RIT or XIT off — also exits scale-adjust mode

You'll hear a rising mode-enter tone when scale-adjust starts and a descending mode-exit tone when it ends. The mode is also exited automatically when you navigate to a different field — there's no inactivity timeout to surprise you mid-QSO.

## JJ Flexible Home — Transmit Slice Field Keys

The Transmit slice field sits after VOX and shows which slice keys the radio ("-" when none does). These keys work while it has focus:

- **Space** — Set transmit to the active slice
- **Up / Down** — Move transmit to another slice
- **A–H** — Set the transmit slice by letter
- **Delete or Backspace** — Clear the transmit slice (no slice keys the radio — a soft transmit lockout)

On a receive-only connection these keys speak a refusal instead of acting silently. The same controls live in the Slice menu under Transmit Slice.

## JJ Flexible Home — Mute and Volume Fields (Classic tuning only)

On the Mute field, Space or M toggles mute. On the Volume field, Up and Down adjust the volume. (Modern tuning handles these through the universal M key and the Audio expander.)

## Mode Switching

- **Alt+M** — Next mode (cycles through available modes)
- **Alt+Shift+M** — Previous mode
- **Alt+U** — Switch to Upper Side Band (USB)
- **Alt+L** — Switch to Lower Side Band (LSB)
- **Alt+C** — Switch to CW mode
- **Alt+A** — Switch to AM mode
- **Alt+F** — Switch to FM mode
- **Alt+D** — Switch to DIGU (digital upper) mode
- **Alt+Shift+D** — Switch to DIGL (digital lower) mode

## Audio Controls

Volume, headphone level, and line-out level live in the Audio expander. Press `Ctrl+Shift+U` to open the Audio expander, then arrow to the level you want and use Up / Down (or Page Up / Page Down for big jumps, Home / End for minimum / maximum, and Enter to type an exact value).

The Audio menu's **PC Audio Levels** and **On-Radio Levels** items each open a dialog with the same riding keys — Up / Down to nudge, Shift + Up / Down for steps of one, Page Up / Page Down for big jumps, Home / End for the ends, Escape to close. The dialog stays open while you adjust, which is the point.

The previous `Alt+Page Up`, `Alt+Shift+Page Up`, and `Shift+Page Up` shortcuts (and their `Page Down` counterparts) no longer adjust audio. The slots are reserved on purpose so a future feature can claim them deliberately.

Two rows on the Audio menu start with "Audio", which is one row too many for a single letter to sort out. So Audio Workshop now has a mnemonic of its own: with the Audio menu open, `A` goes to **Audio Devices** and `W` goes to **Audio Workshop**. Nothing is renamed, and `Ctrl+Shift+W` still opens the workshop from anywhere without visiting the menu at all.

## Audio Workshop

These keys are active anywhere inside the Audio Workshop window (`Ctrl+Shift+W` opens it). They are workshop-local accelerators, not global hotkeys.

- **Ctrl+Enter** — Start the Audio Check, or stop the one that's running
- **Ctrl+S** — Save an audio preset
- **Ctrl+O** — Load an audio preset
- **Alt+E** — Export a preset to a file you can share
- **Alt+I** — Import a preset from a file (it joins your saved presets — nothing changes on the radio until you load it)
- **Alt+R** — Reset the TX audio chain to defaults
- **Escape** — Two-stage while a check is transmitting: first press unkeys and stays in the workshop, second press closes it. Escape never leaves you transmitting

Inside the Load Preset picker, the `Delete` key (or the Delete button) deletes the preset you're on — it asks before doing anything, and deleting never touches the radio.

A note for anyone whose fingers learned the old way: Save Preset used to respond to its `Alt+S` button mnemonic, which also swallowed `Alt+Shift+S` — the global Speak Transmit Status key — while the workshop was focused. That mnemonic is gone; `Ctrl+S` is the Save key now, and the transmit status query is no longer blocked by the workshop.

## Transmit checks

The transmit checks (Tools, then Fix) key your radio on purpose, so the way out matters more here than anywhere else in the app.

**Escape** stops the check. While the carrier is up it drops it first and asks questions afterwards — no confirmation stands between you and stopping your own transmission. The Stop button on the page does the same thing.

Escape reaches the transmitter even while a check is mid-measurement and the window is busy, and it tells you what happened: an alert tone the moment you press, then either "Transmit stopped" or — if the radio didn't obey — that it still says it's transmitting and you should switch it off at the front panel. Those two are deliberately different sentences, because a stop that failed must never sound like one that worked.

## Scanning

Scanning commands mostly live in the Command Finder these days — only stopping and resuming have keys out of the box (you can give the others keys in the Hotkey Editor):

- **Ctrl+Z** — Stop scan
- **Ctrl+Shift+F2** — Resume scan

Start scan, saved scan, and memory scan have no default keys — find them in the Command Finder (`Ctrl+/`), or bind your own keys in Tools → Hotkey Editor.

## DX and Spotting

- **Alt+Shift+X** — Open DX Cluster
- **Ctrl+Alt+R** — Open Reverse Beacon Network

## CW Messages

If you have CW messages configured, you can send them with `Ctrl+1` through `Ctrl+7`. Each number corresponds to a message slot. You can configure your CW messages in the Settings dialog.

## Logging Mode

When you're in the logging pane, these keys help you fill in QSO details quickly. Some keys that do something different in Radio mode (like Alt+C and Alt+D) switch to logging functions here.

- **Alt+C** — Jump to Call field
- **Alt+T** — Jump to His RST field
- **Alt+R** — Jump to My RST field
- **Alt+N** — Jump to Name/Handle field
- **Alt+Q** — Jump to QTH field
- **Alt+S** — Jump to State field
- **Alt+G** — Jump to Grid field
- **Alt+E** — Jump to Comments field
- **Alt+D** — Set date/time to now
- **Ctrl+W** — Save/finalize QSO
- **Ctrl+N** — New log entry
- **Ctrl+Shift+F** — Search log
- **F6** — Switch between log panes
- **Ctrl+Shift+N** — Log characteristics dialog
- **Ctrl+Alt+L** — Open full log entry form

Log statistics has no default key — press `Ctrl+J` then `L` to hear your stats, or bind a key in the Hotkey Editor.

The radio pane inside the logging view has its own tuning keys: Up and Down tune by one step, Shift+Up and Shift+Down tune by ten steps, Left and Right change the step size, and Ctrl+F enters a frequency directly.

## ScreenFields Quick Access

The ScreenFields panel has five expandable categories you can toggle open and closed instantly:

- **Ctrl+Shift+R** — Receiver
- **Ctrl+Shift+N** — DSP
- **Ctrl+Shift+U** — Audio
- **Ctrl+Shift+X** — Transmission
- **Ctrl+Shift+A** — Antenna

**Tip:** Each category expands to show its controls. Press the hotkey again to collapse it. Remember that you can also collapse any field group by pressing Escape. Press Escape twice quickly to close all field categories. `Ctrl+Tab` and `Ctrl+Shift+Tab` move between categories.

## View Controls

- **Ctrl+Shift+F3** — Move focus to the received text box
- **Ctrl+Shift+F4** — Move focus to the CW send text box
- **Ctrl+Shift+F5** — Move focus to the CW send text box (currently the same as Ctrl+Shift+F4 — a distinct direct-keying mode is planned for a future release)

## Radio Selector

These keys are active in the Select Radio dialog — the window that opens at startup and whenever you connect to a radio. They are dialog accelerators, not global hotkeys: they work only while the selector is open.

- **Enter** — Connect to the selected radio along its preferred connection path, announcing the account for a SmartLink connect. If the path needs SmartLink and this session has not looked yet, one Enter signs in, finds the radio, and connects — no second press. On a radio that uses a different SmartLink account, it switches to that account and refreshes the list
- **F2** — Speak which halves of the list have loaded — local and remote — and how many radios are online right now
- **Up / Down arrow** — Move through the radio list. Arrows stay inside the list at both ends
- **Tab** — Leave the radio list. Shift+Tab returns you to the row you left
- **Applications key, or Shift+F10** — Open the radio's context menu: Connect, Connect Locally, Connect over SmartLink, Default Connection Path, Add or Remove Favorite, Auto-Connect Settings, Preferred Account, and Show Remote Radios (Refresh Remote List once they are listed)
- **Alt+N** — Connect
- **Alt+L** — Low bandwidth for the selected radio
- **Alt+P** — Connection path for the selected radio — Automatic, Local network first, or SmartLink first. Saved with the radio; Connect tries the chosen path first and falls back to the other, saying so
- **Alt+S** — SmartLink account. The label follows what you have saved: Sign in to SmartLink, SmartLink Account, or Switch Account
- **Alt+T** — Test the connection to the selected radio
- **Alt+A** — Auto-connect settings for the selected radio
- **Delete** — Take the selected radio off your list. It always asks first, and the choice it offers you starts on the one that deletes nothing — see below
- **Alt+C, or Escape** — Cancel and close the selector

**About the Delete key and what it asks you.** Delete opens a confirmation with
two choices: remove the radio from the list only, which keeps every setting you
have for it, or remove the radio and its settings, which cannot be undone. The
safe one is chosen for you before you touch anything, and that is deliberate —
it is what makes a bare Delete keypress safe in the first place.

Both choices are on the Tab ring. That is worth saying because until Sprint 32 they were not: the pair counted as a single stop, so Tab landed on the safe choice and the next Tab went straight to the Remove button. If you tabbed, you never met the second option at all. Now Tab visits each in turn — press Space to take the one you land on — and Up and Down arrow move between them and choose as they go.

**Heads-up: Alt+R is retired.** The Remote button is gone — Connect now opens SmartLink by itself whenever a radio's connection path asks for it, so the button had one job left, showing the remote list, and that lives in the context menu as Show Remote Radios / Refresh Remote List (Shift+F10 on the radio list). If Alt+R was in your fingers, Shift+F10 then R gets you the same list.

## Moving between categories in Settings and the Audio Workshop

Settings holds eleven categories and the Audio Workshop holds several, so both windows navigate the same way — the way NVDA's own settings dialog does. There is a list of categories down the left-hand side, and the category you pick fills the rest of the window.

- **Ctrl+Tab** moves to the next category and **Ctrl+Shift+Tab** to the previous one. Both work from anywhere in the window, including from inside a text box, so you never have to find your way back to the list first.
- Either one puts you **on the list**, so you hear the category's name and where it sits — "Network, 5 of 11". Tab from there moves into the category's own controls.
- Both wrap. Past the last category you are on the first; before the first you are on the last.
- **Up and Down arrow** move through the list in the ordinary way when you are sitting on it.
- Plain **Tab** is still plain Tab. It moves through the controls of the category you are on and never changes category by itself.
- Settings opens on the list rather than on a button, so the first thing you hear is where you are and what else there is.

Inside the Audio Workshop, **F6** and **Shift+F6** then move between the sections of whichever category you are on. Categories are the big divisions, sections are the groups within one.

If you knew the old tab strip: it is gone, and the list replaces it. Nothing you could reach before is out of reach — the same categories, in the same order, with a key that works from anywhere instead of only when the strip happened to have focus.

## Commands With No Default Key

Some commands ship without a key, and it is worth knowing that this is not one list but several. Every one of them runs from the Command Finder (`Ctrl+/`), and you can give any of them a key of your own in Tools → Hotkey Editor.

**Already have a key, just not their own one.** These answer to the JJ key, so
you may not need to bind anything: show memories (`Ctrl+J`, `M`), log
statistics (`Ctrl+J`, `L`), speak TX filter width (`Ctrl+J`, `F`), toggle meter
tones (`Ctrl+J`, `T`), PC audio on/off (`Ctrl+J`, `Ctrl+A`), echo recent CW
(`Ctrl+J`, `E`), and speak the version and build (`Ctrl+J`, `Alt+V`).

**Live somewhere better than a key.** Audio devices, ATU memories, reboot
radio, and transmit controls all open something, and the menu that opens them
is the place you already go. Reboot is deliberately kept off a chord: it
interrupts everyone on a MultiFlex radio, and the confirmation naming the other
stations connected is the point of the slower route.

**Waiting in the Command Finder on purpose.** Start scan, saved scan, switch S
meter units, collect debug info, and start audio check. Each is either rare, or
better reached where you already are — the Audio Check has `Ctrl+Enter` inside
the Audio Workshop, which is where you are when you want it.

**Deliberately empty.** The six audio-level slots (`Alt+Page Up` and friends)
are held open rather than reassigned, so a future feature has to make its case
before claiming them. Audio levels live in the Audio expander (`Ctrl+Shift+U`)
and volume mode (`Ctrl+J`, `V`).

**Honestly unbound.** Memory scan and speak frequency each once claimed a chord
that another command was quietly eating first. Rather than leave a key listed
that never worked, they have none — and the `F` key on the Frequency field
speaks the frequency, which was the working route all along.

The remaining log field jumps — log file name, log mode, log rig, log antenna —are filled in from the log form, where Tab reaches them.

## Audio Workshop

- **F6** moves to the next section — This Computer, Microphone, Processing, TX Filter, TX Monitor, Test Tone, Audio Check — and says which one you landed in. **Shift+F6** goes back. It wraps at both ends, and skips any section that is hidden, so on PC audio you will not be sent to controls that are not there.
- **Ctrl+Tab** moves to the next category, **Ctrl+Shift+Tab** to the previous one, from anywhere in the window. F6 then moves between the sections inside whichever category you are on. See "Moving between categories" below.
- **Ctrl+Enter** starts or stops the Audio Check from anywhere in the dialog.
  It is a toggle, so the same chord stops it — which matters because a running
  check parks focus on Mic Gain, two stops from the button.
- **Ctrl+S** saves a preset, **Ctrl+O** loads one.
- **Escape** stops a running Audio Check before it closes the window.

## Audio Devices dialog

These work only while the Audio Devices window is open.

- **Alt+M** starts or stops the microphone check.
- **Alt+L** speaks the current reading — level, verdict and loudness — from
  anywhere on the page. This is the one you want while setting your level: it
  means you can sit on the input-level slider, adjust, talk, and hear the
  result without ever tabbing away and losing your place.
- **Alt+S** shows every sound endpoint, every audio system at once, with the
  audio system named on each row. Off by default, which is when the lists hold
  only the audio system you chose at the top of the dialog and only devices you
  could actually talk into. It is also how you put your microphone on one audio
  system and your receive audio on another.
- **Alt+R** refreshes the device list.
- **Alt+U** unmutes the microphone in Windows, and appears only when Windows
  has it muted.
- **Alt+W** opens the Windows microphone privacy settings.

## Trace Archive Browser

These keys are active when focus is on the row list inside the Archive Browser tab of the Tracing dialog (Help → Tracing → Archive Browser).

- **Enter** — Open the selected trace in your default text viewer
- **Ctrl+C** — Copy the selected trace's full file path to the clipboard
- **Delete** — Delete the selected trace(s), with a confirmation prompt
- **Ctrl+A** — Select every row in the current filter
