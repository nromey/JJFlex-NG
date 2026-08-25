# Audio Workshop

The Audio Workshop is where you shape how you sound on the air and — new — where you *hear* it. One window holds your whole transmit audio chain, live meters, and the Audio Check session that keys the rig, lets you adjust while you talk, and plays you back to yourself. Adjust and hear, one surface.

It does not choose which sound device your audio goes to. That lives in Settings, on the Audio tab, under **Audio Devices** — or on the Audio menu under the same name. If you are chasing silence rather than shaping a signal, start at **Audio Troubleshooting** instead.

## Opening the Audio Workshop

Press `Ctrl+Shift+W` from anywhere in the application. You can also find it in the Command Finder (`Ctrl+/`) by searching for "workshop". The window is non-modal, so you can leave it open while you operate.

### No radio? Still useful

The Workshop opens happily with no radio connected — it is on the no-radio Home page's own short menu for exactly that reason. The microphone check and everything in the This Computer section work fully offline, so you can prove a headset before you ever connect. The radio-side controls — mic gain, boost, bias, the compander, processor, filters, and monitor — disable themselves and step out of the tab order until a radio arrives, so nothing in here will claim to have changed a rig that was not listening.

When the workshop opens, focus is already on **Start Audio Check** — if you're set up, or you've just loaded a preset, Enter starts a test with zero navigation. One `Tab` forward from that button is the live mic reading, and one more is Mic Gain — the three stops a running check actually uses, sitting together on purpose (more below).

### Workshop keys

These work anywhere inside the workshop window, and only there:

- `Ctrl+Enter` — start the Audio Check, or stop the one running, without hunting for the button.
- `Ctrl+S` — save a preset. `Ctrl+O` — load one. The universal document keys, doing the universal document things.
- `Alt+E` — export a preset to a file. `Alt+I` — import one from a file. `Alt+R` — reset the transmit chain to defaults.
- `Escape` — two-stage while a check is transmitting: the first press unkeys, the second closes the workshop. Escape never leaves you transmitting.

## Transmit audio: three categories, not one

Your transmit audio used to live on a single category called TX Audio, ten sections deep. It's now three, because it was really three subjects sharing one name:

**This Computer** is what your PC does to your voice before the radio hears any of it — which microphone you're using, everything that microphone needs (your mic profiles), and what gets cleaned out of the room before it goes anywhere.

**Transmit Settings** is what the radio does with it after that — which input it's listening to, how it shapes and band-limits you, and how you hear yourself.

**Hear Yourself** is putting a signal through all of it and listening to the result: a known tone, a known recording, or your own voice played back. Your mic gain lives here too, right next to the reading it changes, because setting a gain without hearing the result isn't really setting it.

If something is *broken* rather than needing adjustment, that's JJ Flexible Fix, not here. This is the bench; that's the diagnosis.

Everything in all three speaks as you change it, and the standard value keys work everywhere: Up / Down to nudge, Shift + Up / Down for fine steps, Page Up / Page Down for bigger jumps, Home / End for the ends of the range.

### Audio Check — hear yourself

At the top of the tab. Press **Start Audio Check** (or `Ctrl+Enter`) and the radio keys up through the same safety system as regular transmit — the timeout warnings, the license check, and the hard kill all stay on duty. The first thing you hear is the safety line: which kind of check this is, the frequency, where your transmit audio is coming from, and the reminder that Escape stops. Then focus lands on Mic Gain, with the live reading one `Shift+Tab` back and the Stop button one more — and the rest of the sculpting chain (boost, compander, processor, filter) further down the same tab while you talk.

**The live mic reading** sits just after the Start button — a read-only text field showing your level and the verdict, like "Mic audio now: just right, peak minus 11 dBFS." It updates continuously while you transmit (and holds your last transmission's peak after you unkey), but it never speaks on its own: sit on it and use your screen reader's read-current-control command whenever you want the number. That's the whole design — the reading is always fresh, and *you* decide when to hear it. During a check, the reading is one `Shift+Tab` back from Mic Gain, and the Stop button one more — start, reading, and gain are neighbors, so the adjust-and-listen loop never leaves three keys.

- **Listen method** picks how you hear yourself, remembered per radio:
  - **Monitor** — instant. The radio's TX monitor feeds your own voice back while you talk. Fine on a local connection; over remote the monitor arrives late enough to trip up your speech, which is why the session recommends the next one.
  - **Record and play back** — talk first, listen after. The radio records your transmission (it captures the full processing chain — compander, processor, filters, all of it) and plays it back to you automatically the moment you unkey. No talking and listening at once, ever. The buffer holds about two minutes and keeps the most recent material; two takes fit, so you can tweak a setting and compare.
- **Transmit power during checks** decides whether a check makes RF, remembered per radio:
  - **Dummy load, no RF** — the default. The check keys the radio with transmit and tune power at zero watts. Nothing goes on the air, and nothing needs to: every meter the check reads — mic audio, ALC, the verdict — measures your audio *before* the power amplifier, so the reading is identical at zero watts and at a hundred. The safety line says "Audio check, dummy load, no RF" so you always know this is that kind of check.
  - **Low power** — for the different question, "is RF actually leaving my radio?" Pick the wattage yourself, right down to 1 watt. It's a cap: if your power is already below the number, the check leaves it alone, and it never raises power. The safety line names the actual power, "Audio check, transmitting at 1 watt."
  - If you've engaged Dummy Load Mode yourself from the Transmit menu, the check respects it either way — it will never raise power behind a dummy load's back, and it leaves your dummy load exactly as you set it.
- **Play last take** replays the recording buffer whenever you like, or stops a playback in progress.
- **Escape is two-stage** while a check is transmitting: the first press unkeys ("Transmit off") and leaves you in the workshop with your settings; the second press closes the window. Escape never leaves you transmitting.
- The session ends itself — and puts back everything it changed — on every exit: the Stop button, Escape, the transmit timeout, closing the window, or the radio going away.

One honest warning worth knowing: if your hand mic keys the radio through a hardware line (the front-panel jack or the rear RCA connector), software cannot unkey it. If that ever happens the workshop tells you plainly, names the keying source, and keeps warning until the line releases.

### Test Tone — transmit without a microphone

Right below the Audio Check. Arm **Test tone instead of microphone** and a clean, steady tone takes your microphone's place in the PC audio transmit path. Your mic is fully muted while the tone runs — replaced, not mixed — so nothing from the room rides along. The tone travels the exact same path your voice does, which is the point: it is a known signal at a known level, perfect for checking that transmit audio works at all, for watching the meters react, or for hearing what your processing chain does to a pure input.

- **The pitch is yours.** The default is 440 hertz — the standard reference tone — but hearing varies, and a test tone you cannot hear tells you nothing. Pick **440 hertz reference**, **700 hertz CW tone**, or **1000 hertz standard test**, or choose **Custom frequency** and type any value in hertz. Your choice is remembered across sessions, and it follows you rather than the radio — it is about your ears, not your rig.
- **Tone level** is in dBFS, from minus 40 up to 0. The default of minus 10 lands in the "Good" range on the mic audio verdict. Turn it down to rehearse the "Quiet" and "Faint" coaching, or up to hear "Hot" and then "Clipping" — the tone is also how you learn what the coaching sounds like.
- **Hear the tone while it transmits** plays the tone through your alert sound device whenever it is actually going out, so you can confirm by ear that the check is running. Turn it off if you would rather work in silence — both are fine answers, so the switch is yours. The local tone is a presence indicator at a comfortable fixed volume; it does not change with the transmit level.
- **Arming is deliberate and temporary.** The tone only replaces your mic while the workshop is open, and it never survives closing the window, switching radios, or restarting the app. Whenever the tone is armed, every key-down — the Audio Check, push-to-talk, transmit lock, all of them — announces that the tone is going out instead of your microphone. You will never accidentally call CQ with a sine wave.

**The transmit filter warning, and why it will not stay quiet.** An SSB transmit filter typically passes roughly 100 to 2900 hertz. If you move the tone to a pitch you hear well and that pitch sits outside your filter, the radio transmits *nothing* — silently — while everything else looks like a working test. That is exactly the kind of quiet lie this workshop exists to end, so JJ Flexible Radio Access lets you set any frequency you like but warns you out loud the moment you set one outside the filter, again if you arm the tone there, at every key-down while it stays outside, and even if you move the filter out from under an armed tone later. The status line under the controls always says where the tone sits relative to your filter, and the TX filter controls are a few fields down in this same tab if you would rather widen the filter than move the tone.

The tone rides the PC audio path, so it needs PC audio on and the radio's transmit input set to PC — if either is off, arming tells you exactly what to change instead of pretending to work. And in CW mode the PC transmit audio path does not run at all, so the tone waits for a voice mode.

### Microphone Profiles — one name for everything a mic needs

Right after This Computer. A microphone profile is built around the microphone rather than the radio, because that is how the question actually arrives: "what does this headset need?" — and the answer travels with the mic, across every radio you use.

Each profile carries two halves:

- **The computer half** — which Windows device the mic is, its input level, its boost, and your PC Cleanup settings (the noise reduction and noise gate that tidy the room before the radio hears it). This half always belongs to JJ Flexible; a radio has nowhere to keep it.
- **The radio half, per radio** — and here is the part worth understanding. Flex radios keep their own mic profiles, on the radio itself, shared with SmartSDR and every other client. So on a Flex, your microphone profile simply **names which of the radio's own mic profiles to load** — nothing is copied, so there is nothing to drift out of date and nothing fighting other programs over the same settings. On a radio with no profile system of its own (a road that is being paved for other makes), the profile carries the actual values instead.

Pick a profile and press **Apply Profile**, and both halves go into effect — with plain words about anything that could not happen. If the radio does not have the referenced mic profile, JJ Flexible says so and leaves the radio alone; it never guesses at a substitute and never creates profiles on a radio behind your back. If your computer is using a different microphone than the profile was made with, it says that too, and leaves the Windows level where it is rather than moving it for the wrong mic.

**Save Profile** captures your computer settings under the mic's name and asks what to store for the radio you are on: reference the radio's current mic profile (the usual right answer on a Flex), snapshot the radio's TX settings into the file, or store the computer half only. Creating a mic profile *on the radio* is offered right there when the radio has none loaded — offered, and only ever done because you chose it.

One profile, three radios? Save it once on each. The bindings live side by side inside the same profile, and applying it on any of them uses that radio's own half. On a radio you have never set it up on — a club rig, a friend's station over remote — Apply sets up your computer half and touches nothing of theirs. That is by design.

### Transmit audio source

The Microphone section starts with **Transmit audio from**, showing the radio's own input list — the mic jack, line in, PC audio, and so on. Everything below it follows that choice, because the controls that matter are different depending on where your audio comes from.

**Choose the mic jack** and you get the radio's own controls: Mic Gain, Mic Boost, and Mic Bias. Mic Bias is a low-voltage supply for electret microphones on the front-panel jack — around 3 volts, depending on your model. It is **not** 48-volt phantom power, and a studio condenser that expects phantom will not run on it.

**Choose PC audio** and those three disappear, because they act on the radio's microphone jack and do nothing at all to audio arriving from your computer. In their place you get **Windows input level** — the same level Windows Sound settings shows for the microphone you picked in Audio Devices, adjustable right here. If your driver offers a Microphone Boost, that appears too; a boost left turned up is the most common reason a level pins no matter what else you do.

The section always shows the gain that actually applies to your current source, so there is never a control sitting there that cannot help you.

A line under the level names the exact Windows device being adjusted. If JJ Flexible cannot be certain which Windows device matches your chosen microphone, the control is switched off and that line says why rather than risk moving some other microphone's level.

### Moving around

The three transmit categories are a walk-through, running outward from your computer to the radio to the air. This Computer holds the audio devices, your microphone profiles, and PC cleanup. Transmit Settings holds the microphone the radio listens to, processing, the TX filter, and the TX monitor. Hear Yourself holds the test tone, the reference recording, the audio check, and your mic gain.

Press **F6** to jump to the next section and **Shift+F6** to go back. Each jump
names the section you arrived in, wraps around at the ends, and skips any
section that is currently hidden — so with PC audio selected it will not send
you to radio controls that are not on screen.

Tabbing works too, and your screen reader announces each section as you cross
into it. F6 is for when you know where you are going.

The Workshop is divided into categories, listed down the left-hand side of the window. **Ctrl+Tab** moves to the next category and **Ctrl+Shift+Tab** to the previous one, from anywhere in the window; either one lands you on the list, so you hear which category you arrived in and how many there are. F6 then moves between the sections inside it. Categories are the big divisions, sections are the groups within one.

### The sculpting chain

Mic gain, mic boost, mic bias, the compander and its level, the speech processor and its mode, and the TX filter edges with a live width readout. The TX Monitor section holds the monitor toggle, level, and pan; in phone modes its header names the mode you're in so you know which monitor family you're adjusting.

### Loopback Check — for radios with two receivers

On radios with two receive chains and transverter ports (the 6600, 6700, and 8600 families), the **Loopback Check** button runs your actual RF through the radio and back: it transmits at one watt into the transverter port while a second slice listens on the same port, then puts every setting back and removes the extra slice when you stop.

Be clear about what this is: your signal arrives at that receiver enormously strong, so what you hear is your audio — present, processed, recognizably you — through an overloaded front end. It proves your audio chain and your transmitter work end to end ("is my radio actually transmitting" and "check my audio" are the same button), but it is not a faithful recording-studio listen. For ground truth on how you sound on the air, nothing beats a real receiver on a real antenna — a WebSDR or KiwiSDR tuned to your frequency is the gold standard.

Also worth knowing: one watt into a transverter port with antennas connected can still leak a little RF. It should not put a meaningful signal on the air, but it is a transmission — identify if your regulations require it.

On radios that can't do this, the workshop says why instead of hiding the fact.

### Command Finder

"Check my transmit audio" in the Command Finder (`Ctrl+/`) opens the workshop and starts an Audio Check in one step. Search for "audio", "check", "hear", or "myself".

### While we're talking transmit

Outside the workshop, `Ctrl+Space` is push-to-talk (transmit while held) and `Shift+Space` toggles transmit lock, both from JJ Flexible Home. The Audio Check rides the same safety system those do.

## The Meters tab

S-Meter, forward power, SWR, mic audio, TX drive (ALC), amp ALC, PA temperature, and supply voltage, refreshed twice a second while the tab is open. Every reading is a read-only field now: Tab through them, or press F6 to hop between the Receiver, Transmit, and Hardware groups, and read any meter at your own pace with your screen reader's review commands. The meters no longer announce themselves as they change — when you want a value, go ask the field that holds it. During an Audio Check, mic audio and TX drive are the two worth watching — and the transmit health watcher still speaks up on its own if the mic looks silent or the ALC is pegging.

Forward power is shown two ways on purpose: in dBm, which is what the radio itself reports, and in watts alongside it. The watts figure carries decimals when it needs them, so a fraction of a watt reads as a fraction of a watt instead of rounding away to nothing — which matters if you drive a transverter or work QRP, where a fraction of a watt is the normal operating point rather than a fault.

Those eight are the ones worth watching, but they are not all your radio has. Down at the bottom of the page, Show All Meters lists every meter the radio publishes — around a hundred of them on a 6600 or an 8600 — with what each one currently reads. It starts collapsed, because the eight above are what you want almost every time, and a hundred lines you did not ask for is not help. Press it once and the full list appears with your cursor already in it; press it again to take a fresh reading. There is a Copy to clipboard button beside it, though the list is an ordinary text box, so Control A then Control C does the same job.

This used to be a separate category called Meter Inventory, sitting next to Live Meters in the list on the left, and there was no way to tell from the two names which one held the reading you were after. It is one page now.

## The Earcon Explorer tab

Every sound the application makes, each behind a button so you can learn them at your own pace — meter tones, transmit start and stop, the filter sounds, the warning ladder.

## Presets

The toolbar's Load, Save, Export, Import, and Reset buttons work on the whole TX audio chain, so you can keep one setup for ragchews and another for DX and switch between them. `Ctrl+S` saves and `Ctrl+O` loads from anywhere in the workshop; `Alt+E` exports a preset to a file, `Alt+I` imports one a friend sent you (into your list, not onto the radio), and deleting lives inside the Load picker where the list is. See the Audio Presets help page for the full story, including exactly what a preset does and does not capture.

## Everyday volume lives elsewhere

The moment-to-moment listening levels have three homes, none of them here: the Audio expander on JJ Flexible Home (press `Ctrl+Shift+U` and arrow to the level you want), volume mode (`Ctrl+J`, then `V`), and the two levels dialogs on the Audio menu — **PC Audio Levels** for this computer's side of the wire and **On-Radio Levels** for the radio's own jacks (see the Audio Levels help page). The workshop is for shaping your transmit audio, not for turning the speakers up a hair.

**Tip:** If you are running into audio issues — no sound, distorted audio, audio from the wrong device — see the Audio Troubleshooting help page for a step-by-step checklist.
