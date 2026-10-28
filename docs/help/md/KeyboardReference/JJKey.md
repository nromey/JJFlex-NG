---
keywords:
  - JJ key
---

# JJ Key Commands

Use the JJ key, `Ctrl+J`, to reach frequently used commands within JJ Flexible Radio Access. Press `Ctrl+J`, then a second key to control digital signal processing (DSP), audio, meters, status readouts, and other features. J stands for JJ, and it's a tip of the hat to the original author, Jim Shaffer, now a silent key, who went by JJ. JJ Flexible's authors thought it would be neat to honor JJ by opening up the endless possibilities of command entry that become available with JJ's JJ key.

## How It Works

1. Press `Ctrl+J`. A rising "bink" tone confirms that the JJ key layer is open.
2. Press one of the command keys listed on this page.
3. A one-step command runs and closes the JJ key layer. A key that opens a layer, such as the audio layer, leaves that layer open until you close it. See "What Is a Layer?" below.

If you change your mind, press `Escape`. A soft falling tone confirms that the JJ key layer has closed. Within JJ Flexible's own windows, the layer waits for a key or for `Escape` without a time limit. Switching to another program closes it quietly, so your next key works normally when you return.

The JJ key uses layered keystrokes, like those in JAWS and NVDA: one key combination, then a second key to perform the command. This gives access to many features without a separate combination of `Ctrl`, `Alt`, and `Shift` for each one.

## The Four Kinds of JJ Key Command

Most commands follow four rules, allowing hams to figure out a command from its general purpose. The modifier says what kind of command it is, and the letter names the subject:

- A plain letter opens a layer, a set of keys for one subject, such as audio or filters.
- `Shift` plus a letter selects a slice, even from inside another active JJ layer. For example, the JJ key, then `Shift+C`, always selects slice C.
- `Ctrl` plus a letter switches a feature on or off. The letter is usually the beginning of the feature's name or a portion of it. For example, `Ctrl+G` controls the test-tone generator, `Ctrl+T` controls meter tones, `Ctrl+E` controls alert sounds, and `Ctrl+D` controls a detailed capture.
- `Alt` plus a letter performs an action other than opening a layer or switching a feature on or off. In essence, `Alt` covers the miscellaneous functions. For example, `Alt+V` speaks the full version of the application, `Alt+K` runs the microphone check, and `Alt+M` opens the memories stored in JJ Flexible.

A few commands are exceptions to these rules. They are listed under "Still finding their layer" below.

## What Is a Layer?

A layer groups the controls for one subject and stays open until you close it. There are three:

- The audio layer, `A`, adjusts levels such as slice volume, headphones, and the microphone. Select a level with its letter, then use the arrows to change it.
- The filter layer, `F`, moves the receive and transmit filter edges with the arrows.
- The noise layer, `N`, holds the noise switches. Each one changes state with a single press.

Inside any layer:

- `H` lists that layer's keys.
- `Enter` keeps your changes and closes the layer.
- `Escape` undoes every change made in the layer and closes it.
- The JJ key also leaves the layer.
- `Shift` plus a letter still selects a slice.

`M` is reserved for a planned mode layer. That layer is not available yet, so pressing `M` tells you how to open the memories instead.

## Finding a Key: the Command List and the Explorer

Press `H` after the JJ key, or inside any layer, to hear what keys are available. A short list, eight commands or fewer, is spoken. A longer list opens in a window: the title gives the number of commands, followed by one command per row. Press up and down arrow to read each command, type a letter to jump to that key, and press `Escape` or `Enter` to close the list. The list describes commands but does not run them; close it and press the key.

Inside the audio layer, `H` lists its 10 targets: `V`, `Ctrl+H`, `O`, `M`, `L`, `C`, `S`, `X`, `D`, and `P`.

Press the slash key to open the JJ key explorer, or choose **Explore the JJ key** (`Alt+X`) from the command list. The explorer shows every JJ command in a tree, grouped into plain letters, Shift letters, Control letters, Alt letters, and help. A chord that opens its own layer, such as `A` for the audio layer, has that layer's keys beneath it: select it and press `Right` to expand it, then arrow down through the functions it holds. Press `Left` to close a branch, `Enter` to open or close it, and type a letter to move to the next key beginning with that letter. Like the command list, the explorer describes commands rather than running them. Both read from the same command table, so they always agree.

Use `H` when you have forgotten a key or a function you know is in a layer. Use the explorer to get to know the JJ key layers and what each one can do.

The application names punctuation keys as "slash" or "Shift slash." A screen reader may omit a lone question mark when its punctuation level is low, making the instruction incomplete. Naming the key tells you what to press at any punctuation level. The same wording is used wherever the application asks you to press a punctuation key.

## Audio Feedback

Each JJ key action gives audio feedback:

- When you turn a feature on, you will hear two rising tones, "bonk-bink," followed by speech confirming the state. For example: "On-Radio Neural NR on."
- When you turn a feature off, you will hear two falling tones, "bink-bonk," followed by speech. For example: "On-Radio Neural NR off."
- When you request information, the information is spoken without an earcon.
- If a key is unavailable on your radio, you will hear a dull buzz, called the thunk, followed by the reason. For example: "Audio Peak Filter is CW only" or "On-Radio Neural NR not available on this radio." These radio-specific reasons are always spoken. A key with no command at all is handled differently; see "When the JJ layer doesn't know the key you pressed" below.
- When you cancel, you will hear a soft descending tone.

## Using the JJ Key from Another Program

The JJ key also works while another program, such as a contest logger, has the keyboard. From another program, press `Ctrl+Shift+J`, the system-wide JJ key, then the command key. You will hear the same rising tone as inside JJ Flexible. Feedback, for now, is given by your screen reader or text-to-speech.

- Both keystrokes are kept from the other program, so it never sees them.
- One-step commands, the command list, and the explorer all work this way.
- A key that opens a layer does not. A layer needs the JJ Flexible window, and the application will say so. Switch to JJ Flexible and press the JJ key there.
- If you press the system-wide JJ key and then nothing, the layer closes silently after about ten seconds, so a forgotten layer cannot take a key meant for the other program. Each key you press restarts the ten seconds, and the clock stops while the command list or the explorer is open. Press `Escape` to close it sooner.

You can change the system-wide key in **Settings**, under **System-wide keys**, if it clashes with a global hotkey you use in another application. The same settings can turn the system-wide JJ key off, leaving `Ctrl+J` working only inside JJ Flexible. The Tools menu's **System-wide keys** item opens these settings. See [Keyboard Reference](KeyboardReference.md) for details.

<!-- LEADER-KEY-TABLE: Each chord line in this region is checked against KeyInventory.LeaderCommands by Radios.Tests/LeaderDocCoverageTests. Keep one line per chord, using a hyphen, a space, the chord in bold, a space, an em dash, a space, and its description. Keep these groups in the same order as the inventory and KeyboardReference.md: layers, slices, toggles, actions, exceptions, and help. The wording may change; the keys and this line shape must stay in agreement. -->

## Layers — a plain letter

These keys open the layers described under "What Is a Layer?" above.

- **A** — Opens the audio layer. Select a level with `V` for slice volume, `Ctrl+H` for headphones, `O` for personal computer (PC) output, `M` for the microphone, `L` for line out, `C` for the compander, `S` for the processor, `X` for voice-operated transmit (VOX) gain, `D` for VOX delay, or `P` for pan. Use `Up` and `Down` to adjust the level, or `Home`, `End`, and `0` to set it directly. Use `Ctrl+M` to toggle mute, `Ctrl+P` for PC audio, `Ctrl+B` for binaural audio, `Ctrl+C` for the compander, and `Ctrl+S` for the processor. `Escape` undoes all changes made in the layer, and `Enter` accepts them. Press `H` to list the keys. The Keyboard Reference page gives the full instructions.
- **V** — Opens the same audio layer as `A`. This alternative preserves the familiar volume-mode key until `V` is needed for a separate layer.
- **Alt+P** — Opens the audio layer with pan selected. Use `Left` and `Right`, or `Up` and `Down`, to place the active slice in the stereo field. Hold `Shift` to move by one, press `0` to centre the slice, or use `Home` and `End` for hard left and hard right. `Escape` undoes changes, and `Enter` accepts them. Each adjustment speaks the value on a scale from 0 to 100. Pan is also available with `A`, then `P`. `Alt+P` preserves the earlier pan-mode key and is the one `Alt` chord that opens a layer.
- **F** — Opens the filter layer. Hold `Left Shift` to select the low edge or `Right Shift` to select the high edge, then use the arrows to move it. `Home` and `End` move the edge to its limit. Use `T` and `R` to switch between the transmit and receive filters, and `S` to hear the settings. Press `H` to list the layer's keys.
- **N** — Opens the noise layer. Each switch changes state with one press and announces the result. Use `A` for auto notch, `B` for the noise blanker, `W` for the wideband blanker, `N` for noise reduction (NR), `R` for the radio's neural NR, `S` for the radio's spectral NR, `Ctrl+N` for the NR filter, and `P` for the audio peak filter. `Ctrl+R` and `Ctrl+S` control neural and spectral NR on your computer. `Escape` restores every switch you changed, and `Enter` keeps the changes. Press `H` to list the keys. These letters are provisional. See the Keyboard Reference page for the full instructions.

## Slices — Shift plus the letter

- **Shift+A through Shift+H** — Selects the slice named by the letter, from A through H. These keys work from anywhere, including inside a layer.

## Toggles — Ctrl plus the initial

These keys switch features on or off and announce the resulting state. The letter matches the feature's initial.

- **Ctrl+X** — Toggles full duplex for the whole radio. X stands for the X in duplex. The new state is announced after the radio reports it. The same control is available in Home's Transmit group and in the Transmit menu.
- **Ctrl+G** — Arms or disarms the transmit (TX) test tone, which replaces your microphone while transmitting.
- **Ctrl+R** — Starts or stops recording the active slice with the radio's own recorder. The radio keeps one recording per slice, containing up to the last 2 minutes.
- **Ctrl+P** — Starts or stops playback of the active slice's recording. If you are already transmitting, playback goes over the air until you unkey. Otherwise, it plays locally. JJ Flexible pauses VOX or CW break-in for local playback and restores it when playback stops. Playback never keys the radio.
- **Ctrl+Shift+R** — Starts or stops Record to file, which records what the radio plays through this computer. Adding `Shift` to `Ctrl+R` selects this recorder rather than the radio's recorder.
- **Ctrl+Shift+P** — Starts or stops Play from computer, which plays your last recording on this computer. This is the `Shift` form of `Ctrl+P`.
- **Ctrl+T** — Toggles meter tones on or off.
- **Ctrl+E** — Toggles alert sounds, also called earcons, on or off. E stands for earcons. If you used `Shift+T` for alert sounds, use this key instead.
- **Ctrl+Q** — Starts or stops the contact (QSO) signal analyzer. It watches the S-meter and reports how the signal changed, including fading (QSB). Saved captures are available under **Tools > Signal captures**.
- **Ctrl+S** — Switches the S-meter between S-units and decibels relative to 1 milliwatt (dBm). One S-unit is 6 decibels (dB), equivalent to a factor of four in power. Use dBm to compare the effect of an antenna change, or S-units for everyday readings. This chord announces the selected unit and saves the choice for that radio. Pressing `Ctrl+S` without the JJ key reads the meter in the selected unit and names that unit. The same setting is on the Radios tab in Settings, where you can check its state without toggling it.
- **Ctrl+D** — Starts or stops a detailed capture of what the application is doing. This works without a connected radio and from inside any dialog. Stopping the capture saves it as a separate session in Saved Diagnostic Logs.

## Actions — Alt plus the initial

These keys speak information, open lists, send notifications, or capture audio. They perform actions rather than opening layers or toggling features.

- **Alt+K** — Runs the microphone check, which speaks only the microphone-audio verdict and level.
- **Alt+Q** — Captures a noise profile for PC Spectral NR. Q stands for "quiet." Press `Alt+Q` again during capture to cancel it. See the PC-Side Noise Reduction help page.
- **Alt+E** — Repeats the CW notifications you just heard. E stands for echo. Press it again to hear an earlier notification.
- **Alt+L** — Speaks log statistics.
- **Alt+M** — Opens the Flex memory list. M stands for memories. Plain `M` is reserved for the mode layer.
- **Alt+O** — Reports what is still running and what resources it is using, including recordings, captures, and meter tones. O stands for "what's on."
- **Alt+R** — Reads all problems recorded since the session began, so you can check for an announcement you missed.
- **Alt+V** — Speaks the version, build type, and build date of this copy. You can check which build you are using without leaving your current task.
- **Alt+W** — Opens a list of every window on your screen. The window with keyboard focus comes first, followed by JJ Flexible Radio's windows, then all other windows. Each row names the program that owns the window, reports whether it is still running and responding, and says whether the window is a dialog blocking the rest of that program. W stands for Windows. This works without a connected radio and from inside any dialog.
- **Alt+N** — Opens the tracking notch filters. Auto Notch finds a carrier automatically; a tracking notch lets you choose one yourself. **Add** places a notch at the current receive frequency. You can set its width and depth. Set **Keep** to **Yes** to retain the notch through band changes and power cycles, for example to suppress a birdie that always occurs at the same frequency. N stands for notch. It uses `Alt` because it opens a control rather than switching a feature on or off.
- **Alt+F** — Opens Recorded QSOs, which lists the recordings made with Record to file. You can choose a recording for Play from computer, play it in Recorded QSOs, trim it, save it as MPEG Audio Layer III (MP3) or another format, delete it, or prune old recordings. F stands for files. It uses `Alt` because it opens a window. This works without a connected radio.

## Still finding their layer

Tuning speech debounce uses `D` because `Ctrl+D` is assigned to detailed capture. Its intended tuning layer is not available yet. The noise switches are in the noise layer (`N`). The compander, speech processor, and PC audio switches are in the audio layer: press `A`, then `Ctrl+C`, `Ctrl+S`, or `Ctrl+P`, respectively. The compander and processor levels are also in that layer.

Two `Ctrl` chords are exceptions to the toggle rule. `Ctrl+C` copies text, following the Windows convention. `Ctrl+F` opens frequency entry; its placement awaits a decision about the `Alt` group.

- **D** — Toggles tuning speech debounce on or off.
- **Ctrl+C** — Copies the selected speech-history message to the clipboard. Use `Ctrl+F4` to move back through the history or `Ctrl+F5` to move forward, then copy the message you want. If you have not moved through the history, this copies the most recent announcement. It works without a connected radio, so you can copy an error after a disconnect.
- **Ctrl+F** — Opens the direct frequency-entry box.

## Help

- **H** — Opens the list of JJ key commands, with one command per row, for you to read at your own pace.
- **/** — Opens the JJ key explorer to browse every layer and its keys. Holding `Shift` with the slash key does the same thing.
- **Escape** — Closes the JJ key layer.

<!-- END LEADER-KEY-TABLE -->

## When the JJ layer doesn't know the key you pressed

If you press `Ctrl+J`, then a key with no assigned command, you will hear a low thunk. Your speech verbosity setting controls the explanation that follows. Press `Ctrl+Shift+V` to cycle through the verbosity levels.

On Chatty, you will hear: "Unknown key. Press H for the list of JJ key commands, or slash for the JJ key explorer. Escape to cancel." On Terse and Off, the thunk is the only feedback. Once you recognise it, you can press `H` for help without hearing the explanation each time.

For some unassigned keys, the layer suggests a nearby command. For example, `K` is unassigned, but `Alt+K` runs the microphone check. On Chatty, pressing `K` gives this explanation: "You pressed K which isn't assigned. Pressing Alt+K launches Mic check if that was what you were looking for, otherwise use JJ H to hear keys you can use with the JJ key." On Terse, you will hear: "Alt+K launches Mic check, not K." On Off, you will hear only the thunk. A suggestion names the command; it does not run it.

If all earcons or just the command tones are off, there is no thunk. The application will speak the short explanation at every verbosity level instead: "Unknown key. H for the list, Escape to cancel." Reducing verbosity therefore never leaves you without feedback. It changes what the application says, not how the keys behave.

After an unknown key, the layer stays open for three keys only: `H` to open the command list, slash to open the explorer, and `Escape` to close the layer. After a near miss, it also accepts the chord it just named. For example, you can press `Alt+K` immediately after the suggestion to run the microphone check without pressing `Ctrl+J` again. Any other key closes the layer and performs its ordinary action, so the layer never holds on to keys meant for something else.

This recovery works both within JJ Flexible and in another program when you used the system-wide JJ key. Switching away from JJ Flexible cancels recovery from a key pressed there.

## Related Topics

- [Keyboard Reference](KeyboardReference.md)
- [Command Finder](CommandFinder.md)
