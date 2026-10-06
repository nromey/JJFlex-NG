# JJ Key Commands

The JJ key — `Ctrl+J`, J for JJ — gives you quick access to DSP toggles, audio controls, meter controls, status readouts, and other frequently used features of JJ Flexible Radio Access without having to memorise dozens of hotkeys. You press `Ctrl+J` and then a second key. The set of commands it opens up is the JJ key layer.

The second key follows a grammar, and the grammar is the point: you work out a chord instead of memorising it. Four rules cover nearly everything. A plain letter opens a layer — a set of keys about one thing, like the filter or your audio, with arrows to adjust and Escape to put things back. Shift plus a letter jumps to that slice, from anywhere, even from inside a layer: `Shift+C` is slice C and never anything else. Ctrl plus a letter switches on or off the thing whose initial it is — `Ctrl+N` is noise reduction, `Ctrl+B` the noise blanker, `Ctrl+P` PC audio, `Ctrl+T` meter tones. Alt plus a letter does something that is neither a layer nor a switch — `Alt+V` speaks the version, `Alt+K` is the mic check, `Alt+M` opens the memories. Inside any layer, `H` reads that layer's keys and the slash key opens the JJ key explorer. If you knew the old map: most of the plain-letter toggles moved one modifier over in this release, so `B` is `Ctrl+B`, `K` is `Alt+K`, and so on; press the old key and the layer tells you the new one. A handful of switches have not found their place yet, and they are listed last, each with the reason.

## How It Works

1. Press `Ctrl+J`. You will hear a rising "bink" tone — the JJ Command layer is now open.
2. Press one of the command keys listed below.
3. The action executes, and the layer closes automatically.

If you change your mind, press `Escape`. You will hear a soft falling tone letting you know that the JJ Command layer has closed. The layer waits patiently until you press a key or cancel — there is no timer sneaking you out of it.

The JJ key also works from another program. Press `Ctrl+Shift+J` while a contest logger or anything else has the keyboard, then the second key, and both are kept from that program — the same layer with a second door, so every command below works from outside except the ones that open a layer of their own, which need this window and say so. The key is a default you can change under Tools, System-wide keys; the keyboard reference has the details.

<!-- LEADER-KEY-TABLE: every key line from here to the END marker is checked against KeyInventory.LeaderCommands by Radios.Tests/LeaderDocCoverageTests. Add a chord to the layer and this list fails until it has a line; delete a chord and a leftover line fails too. The wording is yours — only the set of keys is checked. The groups are the TIERS of the grammar (#515), in the same order as KeyInventory.LeaderCommands and keyboard-reference.md: layers, slices, toggles, actions, the ones still finding a layer, then help. Keep the three in step — it is one map read three ways.

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

A plain letter after the JJ key opens a layer: a set of keys about one subject, where the arrows adjust, `H` lists the layer's own keys, Enter keeps what you changed and Escape puts it all back. Two layers exist today, and `M` is spoken for: the mode layer is ruled and not yet built, so pressing `M` tells you where the memories went instead.

- **A** — Enter the audio layer — pick a level with one letter (`V` slice volume, `Ctrl+H` headphone, `O` PC output, `M` mic, `L` line out, `C` compander, `S` processor, `X` VOX gain, `D` VOX delay, `P` pan), ride Up and Down and place it at once with Home, End or `0`; flip a switch with one press (`Ctrl+M` mute, `Ctrl+P` PC audio on or off, `Ctrl+B` binaural). Escape reverts changes - all of them, not just the last - and Enter accepts. Plain `H` lists the layer’s keys. The full walk is on the Keyboard Reference page
- **V** — The same audio layer, by the letter your fingers may already know from volume mode. A second door to one room, kept as a courtesy until `V` is wanted for a layer of its own
- **Alt+P** — Enter the audio layer with pan already picked — Left and Right, or Up and Down, place the slice you're on in the stereo field, Shift moves by one, 0 centers, Home and End are hard left and hard right, Escape reverts changes, Enter accepts. Speaks the number every time, on a 0-to-100 scale. The one chord on Alt that opens a layer rather than doing something: pan lives in the audio layer now (`A`, then `P`), and this door stays open for the fingers that learned pan mode
- **F** — Enter the filter layer — hold Left Shift for the low edge or Right Shift for the high edge, the arrows move it, Home and End send it straight to its limit, `T` and `R` switch between the transmit and receive filters, and `S` speaks it. Plain `H` lists the layer’s keys

## Slices — Shift plus the letter

- **Shift+A through Shift+H** — Jump straight to that slice from anywhere, even from inside a layer — the letter is the slice, all eight of them

## Toggles — Ctrl plus the initial

Ctrl plus a letter switches something on or off, and the letter is the thing's own initial. Each one says which way it went.

- **Ctrl+N** — Toggle Legacy Noise Reduction on or off
- **Ctrl+B** — Toggle Noise Blanker on or off
- **Ctrl+W** — Toggle Wideband Noise Blanker on or off
- **Ctrl+R** — Toggle On-Radio Neural Noise Reduction (RNN) on or off. R used to read you the recorded problems; that is `Alt+R` now
- **Ctrl+A** — Toggle Auto Notch Filter on or off. It was plain `A` until the grammar arrived; plain `A` is the audio layer's door now
- **Ctrl+P** — Turn PC audio on or off — whether radio audio plays through this computer at all. P for PC; it was `Ctrl+A` once. It reads the radio back before answering, so if turning it on could not find a sound device it says that rather than claiming success. `A` then `O` rides how loud it plays; this is the switch
- **Ctrl+G** — Arm or disarm the TX test tone (replaces your microphone while transmitting)
- **Ctrl+T** — Toggle meter tones on or off
- **Ctrl+E** — Toggle alert sounds (earcons) on or off — E for earcons. It was `Shift+T`
- **Ctrl+Q** — Start or stop the QSO signal analyzer — watch the S-meter, then hear what the signal did, QSB and all. Saved captures live under Tools, Signal captures
- **Ctrl+S** — Switch the S-meter between S-units and dBm. One S-unit is 6 dB, which is a factor of four in power hiding inside a single reading, so dBm is what you want for "did that antenna change help?" and S-units are the comfortable everyday scale. The chord says which unit you landed on, and the choice sticks for that radio — so `Ctrl+S` on its own reads the meter in whichever one you're in, and says the unit either way. The same switch lives on the Radios tab in Settings, where you can see where it stands without pressing anything
- **Ctrl+D** — Start or stop a detailed capture of what the app is doing. Works with no radio connected, and from inside any dialog. Stopping saves the capture as its own session in Saved Diagnostic Logs

## Actions — Alt plus the initial

Alt plus a letter does something that is neither a layer nor a switch: speaks a reading, opens a list, sends something, captures something.

- **Alt+K** — Mic check — speak your mic-audio verdict and level, nothing else
- **Alt+Q** — Capture a noise profile for PC Spectral NR — Q for "quiet." Press `Alt+Q` again while it runs to cancel. See the PC-Side Noise Reduction help page
- **Alt+E** — Re-send the CW notifications you just heard — E for echo. Press it again to step back to an earlier one
- **Alt+L** — Speak log statistics
- **Alt+M** — Open the Flex memory list — M for memories. Plain `M` is reserved for the mode layer
- **Alt+O** — Say what is still running and what it is costing — recording, captures, meter tones. O for "what's on"
- **Alt+R** — Read the problems recorded this session — everything that has gone wrong since you started, in case you missed an announcement
- **Alt+V** — Speak the version, the build type and the date this copy was built. The answer to "which build are you on?" without leaving what you are doing
- **Alt+W** — Open a list of every window on your screen: the one that has the keyboard first, then JJ Flexible Radio's own windows, then everything else. Each row names the program that owns the window, says whether that program is still running and answering, and whether the window is a dialog blocking the rest of its program. W for Windows. Works with no radio connected, and from inside any dialog
- **Alt+N** — Open the tracking notch filters. Auto Notch hunts a carrier for you; this is the one where you point at it yourself. Add drops a notch on your current receive frequency, and you set how wide and how deep it is. Set Keep to Yes and the radio holds that notch through band changes and power cycles, which is what you want for a birdie that lives in the same place forever. N for Notch, with Alt because it opens something rather than switching something

## Still finding their layer

These are the ones the grammar cannot place yet, and each has the same shape: a switch whose initial is already a Ctrl chord for something else. The spectral noise reduction wants `Ctrl+S`, which is the S-meter unit; the audio peak filter wants `Ctrl+P`, which is PC audio; the compander wants `Ctrl+C`, which is copy. They keep working where they are until the noise layer and the rest of the audio layer give each one a home, and this section shrinks as they do. Two Ctrl chords sit here on purpose, because they are not switches: `Ctrl+C` copies because `Ctrl+C` copies everywhere in Windows, and `Ctrl+F` is waiting on a decision about what the Alt tier should mean.

- **S** — Toggle On-Radio Spectral Noise Reduction (NRS) on or off
- **Shift+N** — Toggle NR Filter on or off (model-specific)
- **Shift+R** — Toggle PC Neural Noise Reduction on or off (runs on your computer, every radio)
- **Shift+S** — Toggle PC Spectral Noise Reduction on or off (runs on your computer, every radio)
- **P** — Toggle Audio Peak Filter on or off (CW mode only)
- **C** — Toggle Compander on or off
- **Shift+P** — Toggle Speech Processor on or off
- **D** — Toggle tuning speech debounce on or off
- **Ctrl+C** — Copy the message the history walk is sitting on to the clipboard. Walk to the one you want first with `Ctrl+F4` (back) and `Ctrl+F5` (forward), or press it straight away to copy the most recent thing said. Works with no radio connected — the moment you most want to paste an error is usually just after the radio has gone away
- **Ctrl+F** — Open the direct frequency-entry box

## Help

- **H** — Open the list of JJ key commands, one per row, to read at your own pace
- **/** — Open the JJ key explorer — every layer and its keys, at your own pace. The same key with Shift held, the question mark, does the same thing
- **Escape** — Close the JJ key layer

<!-- END LEADER-KEY-TABLE -->

## When the JJ layer doesn't know the key you pressed

Press `Ctrl+J` and then something the layer has no command for, and you'll hear a low thunk. What comes after it depends on your speech verbosity (`Ctrl+Shift+V` cycles it). On Chatty, the thunk is followed by the whole lesson: "Unknown key. Press H for the list of JJ key commands, or slash for the JJ key explorer. Escape to cancel." On Terse, and on Off, the thunk is the whole answer — after a while you know what it means, and you know `H` is where the list lives, so the app stops telling you. The same goes for a near miss, where the layer knows which key you probably meant: Chatty says "K is not a command. Alt+K: Mic check," and Terse just thunks. It names the chord; it does not run it for you.

Turning verbosity down never leaves you with nothing, though. If you've switched earcons off — all of them, or just the command tones — there's no thunk to hear, so the app speaks the short form instead at every level: "Unknown key. H for the list, Escape to cancel." A key you pressed always answers somehow. And the level only changes what the app *says*, never what it *does*: the three waiting keys below are the same on Terse as on Chatty.

Here's the part that matters, because it used to be a small lie. That sentence tells you to press `H` — and until now, by the time it finished saying so, the layer had already closed behind you. `H` did whatever `H` does wherever you happened to be standing. You were told to do the one thing that couldn't work.

Now the layer waits. After an unknown key it stays open for exactly three keys: `H`, which opens the command list, the slash key, which opens the JJ key explorer, and `Escape`, which closes it. Press any other key and the layer is already gone — that key does the ordinary thing it always does, nothing swallowed, nothing surprising.

## Reading the list at your own pace

`H` used to read you every command in the layer in one go — thirty-odd of them, most of a minute, and if the one you wanted went past, you started again from the top. Speech is the right medium for an answer you already know you want; it's the wrong one for looking for something. So `H` now opens a list instead. It says how many commands there are first, so you know what you're getting, then puts one command per row in a window you can arrow through: hear each one on its own, go back to the one you missed, type a letter to jump to that key, and press Escape when you're done. A short layer stays spoken — volume mode's six targets are one sentence, and a window would just be in the way.

From the list, **Explore the JJ key** (`Alt+X`) opens the explorer: the whole layer as a tree. The top branches are the kinds of key — plain letters, Shift letters, Control letters, Alt letters, and help — and under a chord that opens a mode of its own, such as `V` for volume mode, sit the keys that only work once you're in it. Right arrow opens a branch, Left closes it, Enter does either, and a letter jumps to the next key that starts with it. It's the same table the list reads from, so the two can't disagree.

Three keys and no more, on purpose. A layer that keeps hold of your keyboard without saying so is a trap, and you can't see whether you're still in one. These three all lead *out*: two to help, one to the door. There's no way to get stuck, and the layer only ever lingers right after it has told you it's doing so — in words on Chatty, and with the thunk those words taught you on Terse.

A note on how these keys get spoken. You'll notice the app says "slash" or "Shift slash" rather than "question mark" or a bare `?` character. That's deliberate. If your screen reader's punctuation level is set low, a lone `?` may not be spoken at all — so an instruction can quietly lose the very key it's naming. Naming the key also just describes what your hands do, which is the whole job of an instruction. You'll see the same wording anywhere the app asks you to press a punctuation key.

## Audio Feedback

Every JJ key action has its own audio feedback:

- **Feature toggled on** — a two-step rising tone (bonk-bink), then speech confirming the new state: for example, "On-Radio Neural NR on."
- **Feature toggled off** — a two-step falling tone (bink-bonk), then speech: "On-Radio Neural NR off."
- **Information spoken** — no earcon, just speech with the requested information.
- **Invalid or unavailable key** — a dull buzz (the thunk), then speech: for example, "Audio Peak Filter is CW only" or "On-Radio Neural NR not available on this radio." Those reasons are about your radio and are always spoken. A key the layer simply doesn't know, or a wrong arrow inside the audio or filter layer, is different: on Chatty the thunk is followed by the lesson, on Terse the thunk is the answer. See "When the JJ layer doesn't know the key you pressed" above.
- **Cancelled** — a soft descending tone.

## Why a JJ Key?

If you use JAWS or NVDA, you already know the idea as layered keystrokes — press one key, then a second key that does the work. Instead of needing a unique modifier combination for every feature (which would quickly exhaust the available Ctrl, Alt, and Shift combinations), you press the JJ key and then a letter you can work out. The modifier says what kind of thing you want — nothing for a layer, Ctrl to switch something, Alt to do something — and the letter is the thing's initial: `Ctrl+B` for the Blanker, `Ctrl+R` for RNN, `Alt+Q` for a quiet-band capture, `Alt+M` for Memories. You derive it rather than remember it.

**Tip:** Press `Ctrl+J`, then `H` for the list of JJ key commands, or the slash key for the explorer, at any time. You do not need to memorise this page.
