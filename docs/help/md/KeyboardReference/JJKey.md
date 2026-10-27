---
keywords:
  - JJ key
---

# JJ Key Commands

Press `Ctrl+J`, then a second key, to reach frequently used commands in JJ Flexible Radio Access. This JJ key sequence gives you access to DSP switches, audio and meter controls, status readouts, and other features without a separate shortcut to memorise for each one. The commands available after `Ctrl+J` form the JJ key layer. The J stands for JJ.

You can work out most JJ key chords from four patterns. A plain letter opens a layer of related controls, such as the filter or audio layer. Inside a layer, arrow keys make adjustments, and `Escape` puts changes back. `Shift` plus a letter jumps to that slice from anywhere, including another layer: `Shift+C` always selects slice C. `Ctrl` plus a letter usually switches something on or off, such as the test tone with `Ctrl+G`, meter tones with `Ctrl+T`, or alert sounds with `Ctrl+E`; `Ctrl+D` starts or stops a detailed capture. `Alt` plus a letter performs an action, such as speaking the version with `Alt+V`, checking your microphone with `Alt+K`, or opening memories with `Alt+M`. Within any layer, press `H` to read that layer's keys or slash to open the JJ key explorer.

If you learned an earlier JJ key map, use `N`, then `B` for the noise blanker; the noise switches are now in the noise layer. The mic check is `Alt+K` rather than `K`, and other one-step actions have likewise moved to modified chords. An old one-step key announces its new chord. An old noise key may announce the nearest available chord, which might lead to a different control; press `N`, then `H` to hear the noise layer's keys. One switch has not yet moved into a layer and appears under "Still finding their layer" below.

## How It Works

1. Press `Ctrl+J`. You will hear a rising "bink" tone when the JJ Command layer opens.
2. Press one of the command keys listed below.
3. A one-step command runs, then the JJ key layer closes. If you open the audio, filter, or noise layer, it remains open until you press `Enter` to keep changes, `Escape` to cancel them, or the JJ key to leave the layer.

If you change your mind, press `Escape` to close the JJ Command layer. You will hear a soft falling tone. While you remain in JJ Flexible, the layer waits until you choose a key or cancel it; it does not time out.

You can also use JJ key commands while another program, such as a contest logger, has the keyboard. Press `Ctrl+Shift+J`, then the command key. JJ Flexible receives both keys, so the other program does not. Commands that open a layer of their own need the JJ Flexible window; they will tell you when they cannot open from another program. You can change the default system-wide key under **Settings > System-wide keys**, which you can also reach from the Tools menu. The keyboard reference explains that setting.

When you start the JJ key from another program, it closes after about ten seconds without a command, so it will not keep a key intended for that program. Inside JJ Flexible, it waits until you choose a command or cancel. If you switch to another program, the layer lets go quietly and your next key has its ordinary effect.

<!-- LEADER-KEY-TABLE: every key line from here to the END marker is checked against KeyInventory.LeaderCommands by Radios.Tests/LeaderDocCoverageTests. Add a chord to the layer and this list fails until it has a line; delete a chord and a leftover line fails too. The wording is yours — only the set of keys is checked. The groups are the TIERS of the grammar, in the same order as KeyInventory.LeaderCommands and KeyboardReference.md: layers, slices, toggles, actions, the ones still finding a layer, then help. Keep the three in step — it is one map read three ways.

     ONE LINE PER CHORD, and the shape is load-bearing, not a style: a hyphen, a
     space, the chord in bold, a space, an em dash, a space, then the meaning.

         - **Shift+N** — Toggle NR Filter on or off (model-specific)

     The chord is the KeyDisplay string with the "Ctrl+J, " prefix dropped. Write
     it any other way — a hyphen instead of the em dash, no bold, a markdown
     table — and the reader will not see the line at all; the chord then reads as
     undocumented and the build goes red naming it. That is deliberate. A reader
     that quietly accepts several shapes is one that eventually accepts none and
     reports perfect agreement about an empty set. -->

## Layers — a plain letter

After the JJ key, press a plain letter to open a layer of related controls. The arrow keys make adjustments, `H` lists that layer's keys, `Enter` keeps your changes, and `Escape` puts them back. The audio, filter, and noise layers are available. A mode layer is planned for `M`; until it exists, pressing `M` tells you how to reach memories.

- **A** — Open the audio layer. Choose a level with `V` for slice volume, `Ctrl+H` for headphones, `O` for PC output, `M` for microphone, `L` for line out, `C` for compander, `S` for processor, `X` for VOX gain, `D` for VOX delay, or `P` for pan. Use Up and Down to adjust it, or Home, End, or `0` to place it at once. Press `Ctrl+M` for mute, `Ctrl+P` for PC audio, `Ctrl+B` for binaural, `Ctrl+C` for compander, or `Ctrl+S` for processor to switch that feature. `Escape` reverts every change made in the layer; `Enter` keeps them. Press `H` to hear its keys, or read the full walk on the Keyboard Reference page.
- **V** — Open the same audio layer. This chord remains available to anyone who learned `V` for volume mode, until `V` is needed for its own layer.
- **Alt+P** — Open the audio layer with pan selected. Left and Right, or Up and Down, position the current slice in the stereo field; `Shift` changes the position by one, `0` centers it, and Home and End move it to the far left or right. You will hear the position on a scale from 0 to 100 after each move. `Escape` reverts changes and `Enter` keeps them. Pan also lives at `A`, then `P`; `Alt+P` remains for people who learned pan mode. It is the Alt chord that opens a layer instead of performing a one-step action.
- **F** — Open the filter layer. Hold Left Shift to choose the low edge or Right Shift to choose the high edge, then use the arrows to move it. Home and End send the chosen edge to its limit. Press `T` or `R` to select the transmit or receive filter, `S` to hear its settings, or `H` to hear the layer's keys.
- **N** — Open the noise layer. Each key switches a feature and announces its new state: `A` for auto notch, `B` for noise blanker, `W` for wideband blanker, `N` for noise reduction, `R` for the radio's neural NR, `S` for the radio's spectral NR, `Ctrl+N` for the NR filter, `P` for the audio peak filter, and `Ctrl+R` and `Ctrl+S` for the neural and spectral NR that run on your computer. `Escape` puts back every switch you changed, `Enter` keeps them, and `H` lists the layer's keys. These letters are provisional; the Keyboard Reference page has the full walk.

## Slices — Shift plus the letter

- **Shift+A through Shift+H** — Jump directly to the slice named by the letter, even when you are inside another layer. All eight slices have a chord.

## Toggles — Ctrl plus the initial

Press `Ctrl` plus a letter to switch the feature named by that letter on or off. Each command announces the new state.

- **Ctrl+X** — Switch full duplex for the whole radio on or off. The X stands for dupleX. You will hear the new state after the radio reports it. This control is also on Home, in Transmit, and on the Transmit menu.
- **Ctrl+G** — Arm or disarm the TX test tone, which replaces your microphone while you transmit.
- **Ctrl+R** — Start or stop recording the active slice with the radio's recorder. It keeps one recording per slice, up to the last two minutes.
- **Ctrl+P** — Start or stop playing the active slice's recording. If you are already transmitting, playback goes over the air until you unkey; otherwise, it plays locally. For local playback, JJ Flexible pauses VOX or CW break-in and restores it when playback stops. Playing a recording never keys the radio.
- **Ctrl+Shift+R** — Start or stop recording to a file what the radio plays through this computer. This Shift form of `Ctrl+R` reaches the other recorder.
- **Ctrl+Shift+P** — Start or stop playing the last recording made on this computer. This is the Shift form of `Ctrl+P`.
- **Ctrl+T** — Switch meter tones on or off.
- **Ctrl+E** — Switch alert sounds, also called earcons, on or off. The E stands for earcons; the earlier chord was `Shift+T`.
- **Ctrl+Q** — Start or stop the QSO signal analyzer. It watches the S-meter so you can later hear what the signal did, including QSB. Find saved captures under **Tools > Signal captures**.
- **Ctrl+S** — Switch the S-meter between S-units and dBm. One S-unit is 6 dB, a factor of four in power, so dBm can help you judge whether an antenna change helped while S-units offer the familiar everyday scale. You will hear the selected unit. The choice remains with that radio; pressing `Ctrl+S` by itself reads the meter in the current unit and names it. You can also see and change the unit on the Radios tab in Settings.
- **Ctrl+D** — Start or stop a detailed capture of what JJ Flexible is doing, even without a connected radio or while you are in a dialog. When you stop, the capture becomes a separate session in Saved Diagnostic Logs.

## Actions — Alt plus the initial

Press `Alt` plus a letter for an action such as hearing a reading, opening a list, sending something, or making a capture. These commands do not open a layer or simply switch a feature.

- **Alt+K** — Hear your microphone audio verdict and level, without another report.
- **Alt+Q** — Capture a noise profile for PC Spectral NR. The Q stands for "quiet." Press `Alt+Q` again to cancel a capture in progress. See the PC-Side Noise Reduction help page.
- **Alt+E** — Hear a CW notification again. The E stands for echo; press the chord again to step back to an earlier notification.
- **Alt+L** — Hear your log statistics.
- **Alt+M** — Open the Flex memory list. The M stands for memories; plain `M` is reserved for the mode layer.
- **Alt+O** — Hear what is still running and what it is costing, including recording, captures, and meter tones. The O stands for "what's on."
- **Alt+R** — Hear the problems recorded since you started this session, including any announcement you missed.
- **Alt+V** — Hear the version, build type, and build date without leaving your current task.
- **Alt+W** — Open a list of every window on your screen. The window with the keyboard comes first, followed by JJ Flexible Radio's windows and then other windows. Each row names the program that owns it, reports whether that program is running and responding, and says whether a dialog is blocking the rest of its program. The W stands for Windows. This command works without a radio and from inside any dialog.
- **Alt+N** — Open the tracking notch filters. Auto Notch hunts for a carrier, while this control lets you place a notch yourself. Add puts it at your current receive frequency; you can set its width and depth. Set Keep to Yes to retain the notch through band changes and power cycles, as you might for a birdie that stays at one frequency. The N stands for Notch. Alt fits because this chord opens a control instead of switching a feature.
- **Alt+F** — Open Recorded QSOs, which holds recordings you made with Record to file. Choose a recording to play from the computer, play it here, trim it, save it as MP3 or another format, delete it, or prune old recordings. The F stands for files. Alt fits because this chord opens a list; it works without a connected radio.

## Still finding their layer

Tuning speech debounce remains outside a layer. Its likely chord, `Ctrl+D`, is already used for detailed capture, and a tuning layer does not yet exist. The noise controls are in the noise layer (`N`); the compander and speech processor switches sit beside their levels in the audio layer (`A`, then `Ctrl+C` or `Ctrl+S`), along with PC audio (`A`, then `Ctrl+P`). Two other Ctrl chords remain here even though they are not switches. `Ctrl+C` copies, as it does elsewhere in Windows, and `Ctrl+F` awaits a decision about the Alt tier.

- **D** — Switch tuning speech debounce on or off.
- **Ctrl+C** — Copy the selected message in the history walk to the clipboard. Use `Ctrl+F4` to move back or `Ctrl+F5` to move forward first, or copy the most recent message right away. This works without a connected radio, including when you need to paste an error after the radio disconnects.
- **Ctrl+F** — Open the direct frequency-entry box.

## Help

- **H** — Open the list of JJ key commands, with one command per row so you can read them at your own pace.
- **/** — Open the JJ key explorer to browse every layer and its keys. Pressing the same key with Shift, the question mark, opens it too.
- **Escape** — Close the JJ key layer.

<!-- END LEADER-KEY-TABLE -->

## When the JJ layer doesn't know the key you pressed

If you press `Ctrl+J` followed by a key with no JJ command, you will hear a low thunk. What follows depends on your speech verbosity, which `Ctrl+Shift+V` cycles. On Chatty, you will also hear: "Unknown key. Press H for the list of JJ key commands, or slash for the JJ key explorer. Escape to cancel." On Terse and Off, the thunk is the entire response. Once you know the sound and where to find help with `H`, the extra words are unnecessary.

A near miss gives you a more useful answer because the layer can suggest the chord you may have meant. For example, `K` has no command, but `Alt+K` runs the mic check. On Chatty, pressing `K` gives you this suggestion: "You pressed K which isn't assigned. Pressing Alt+K launches Mic check if that was what you were looking for, otherwise use JJ H to hear keys you can use with the JJ key." On Terse, you will hear: "Alt+K launches Mic check, not K." On Off, you will hear only the thunk. The suggestion names a chord but does not run it for you.

You will still get a response if you have turned off all earcons or just command tones. Without a thunk to hear, JJ Flexible speaks the short form at every verbosity level: "Unknown key. H for the list, Escape to cancel." Verbosity changes what you hear, not which keys work while the layer waits for you.

After an unknown key, the layer stays open for three possible keys: `H` opens the command list, slash opens the JJ key explorer, and `Escape` closes the layer. You can press one of them immediately after hearing the response. After a near miss, it also waits for the suggested chord. For example, press `Alt+K` after a suggested mic check without pressing `Ctrl+J` again.

This works both inside JJ Flexible and when you entered through the system-wide JJ key in another program. A pending suggestion made inside JJ Flexible does not follow you into another program; switching away releases it. If you press any other key after the suggestion, the layer has already closed, so that key performs its ordinary action.

## Reading the list at your own pace

Press `H` to open a list you can read at your own pace. JJ Flexible first announces the number of commands, then presents one command per row. Use the arrow keys to hear each one, move back to a command you missed, or type a letter to jump to a key that starts with it. Press `Escape` to close the list. In the audio layer, `H` presents its ten targets: V, Ctrl+H, O, M, L, C, S, X, D, and P.

From the list, choose **Explore the JJ key** with `Alt+X` to open the explorer. It presents the layer as a tree. Its top branches group plain letters, Shift letters, Control letters, Alt letters, and help. Under a chord that opens its own mode, such as `V` for volume mode, you will find the keys available in that mode. Right arrow opens a branch, Left arrow closes it, `Enter` does either, and typing a letter jumps to the next matching key. The explorer and list read the same command data, so their entries agree.

The layer waits for only those three keys, or four after a near miss, so it will not keep control of your keyboard unexpectedly. Two keys open help, one closes the layer, and the fourth runs the suggested chord. It waits only after giving you a response: words on Chatty, words after a near miss on Terse, or the thunk on the quieter settings.

JJ Flexible says "slash" or "Shift slash" rather than "question mark" or a bare `?` character. If your screen reader speaks little punctuation, it may omit a lone `?` and leave you without the key the instruction meant to name. The spoken name tells you what to press. JJ Flexible uses this wording for other punctuation keys too.

## Audio Feedback

You will hear different feedback for these JJ key results:

- **When a feature turns on,** you will hear a two-step rising tone (bonk-bink), then a spoken confirmation such as "On-Radio Neural NR on."
- **When a feature turns off,** you will hear a two-step falling tone (bink-bonk), then a spoken confirmation such as "On-Radio Neural NR off."
- **When you request information,** you will hear the answer without an earcon.
- **When a key is invalid or unavailable,** you will hear a dull buzz, or thunk, followed by a reason such as "Audio Peak Filter is CW only" or "On-Radio Neural NR not available on this radio." Reasons concerning your radio are always spoken. For a key the layer does not know, or a wrong arrow in the audio or filter layer, Chatty adds an explanation after the thunk; on Terse the thunk is the answer. See "When the JJ layer doesn't know the key you pressed" above.
- **When you cancel,** you will hear a soft descending tone.

## Why a JJ Key?

If you use JAWS or NVDA, the JJ key may feel familiar: you press one key to enter a layer, then another to act. A separate `Ctrl`, `Alt`, or `Shift` shortcut for every feature would use up many combinations. With the JJ key, the modifier usually tells you the kind of command and the letter suggests its name. A plain letter opens a layer, `Ctrl` usually switches a feature, and `Alt` performs an action. For example, press `N`, then `B` for the Blanker in the Noise layer, `Ctrl+G` for the test-tone Generator, `Alt+Q` for a quiet-band capture, or `Alt+M` for Memories. The pattern helps you work out a chord instead of memorising every one.

**Tip:** Press `Ctrl+J`, then `H` for the command list, or press slash for the explorer. You can look up a chord whenever you need it.
