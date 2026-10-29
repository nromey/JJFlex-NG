---
keywords:
  - audio workshop
---

# Audio Workshop

The Audio Workshop is where you shape how you sound on the air. Hearing how you sound has a window of its own, **Check your audio**, described further down this page. We're not saying that an old fashioned radio check won't ever be useful, but Check your audio lets you hear yourself as you adjust settings, and the button at the end of the workshop's toolbar opens it.

Audio workshop can help you adjust and monitor the radio's transmit audio chain, edit, monitor, and adjust live meters, and functions which allow you to check your audio settings and diagnose any problems, and hear how you sound to others.

## Opening the Audio Workshop

Press `Ctrl+Shift+W` from anywhere in the application to open the audio workshop module. You can also find it in the Command Finder (`Ctrl+/`) by searching for "workshop". The window is non-modal, so you can leave it open while you operate.

### No radio? Still useful

The Workshop opens happily with no radio connected — it is on the no-radio Home page's own short menu for exactly that reason. The microphone check and everything in the This Computer section work fully offline, so you can prove a headset before you ever connect. The radio-side controls — mic gain, boost, bias, the compander, processor, filters, and monitor — disable themselves and step out of the tab order until a radio arrives, so nothing in here will claim to have changed a rig that was not listening.

When the workshop opens, you land on the list of categories down the left, which tells you where you are.

### Workshop keys

These work anywhere inside the workshop window, and only there:

- `Ctrl+S` — save a preset. `Ctrl+O` — load one. The universal document keys, doing the universal document things.
- `Alt+E` — export a preset to a file. `Alt+I` — import one from a file. `Alt+R` — reset the transmit chain to defaults.

The check's own keys, `Ctrl+Enter` and the two-press Escape, live in Check your audio now.

## Transmit audio: two categories, and a window of its own

Your transmit audio used to live on a single category called TX Audio, ten sections deep. It was really three subjects sharing one name, so it became three categories, and now the third has moved out into its own window:

**This Computer** is what your PC does to your voice before the radio hears any of it — which microphone you're using, everything that microphone needs (your mic profiles), and what gets cleaned out of the room before it goes anywhere.

**Transmit Settings** is what the radio does with it after that — which input it's listening to, how it shapes and band-limits you, and how you hear yourself.

**Check your audio**, which was the Hear Yourself category, is putting a signal through all of it and listening to the result: a known tone, a known recording, or your own voice played back. It is its own window because checking is a different moment from adjusting. Your mic gain is in both places, because setting a gain without hearing the result isn't really setting it; it is the same setting in each, so a change in one shows in the other.

If something is *broken* rather than needing adjustment, that's JJ Flexible Fix, not here. This is the bench; that's the diagnosis.

Everything in both categories speaks as you change it, and the standard value keys work everywhere: Up / Down to nudge, Shift + Up / Down for fine steps, Page Up / Page Down for bigger jumps, Home / End for the ends of the range.

### Microphone Setups — one name for everything a mic needs

Right after This Computer. A microphone setup is built around the microphone rather than the radio, because that is how the question actually arrives: "what does this headset need?" — and the answer travels with the mic, across every radio you use.

**Two things are easy to confuse here, so this section is careful about which word it uses.** A **microphone setup** is yours and lives on this computer. A **mic profile** is the radio's own, lives on the radio, and is shared with SmartSDR and every other program connected to it. A Flex often holds a couple of dozen mic profiles while you have no microphone setups saved at all — both statements can be true at once, and neither is a fault.

Each setup carries two halves:

- **The computer half** — which Windows device the mic is, its input level, its boost, and your PC Cleanup settings (the noise reduction and noise gate that tidy the room before the radio hears it). This half always belongs to JJ Flexible Radio; a radio has nowhere to keep it.
- **The radio half, per radio** — and here is the part worth understanding. Flex radios keep their own mic profiles, on the radio itself, shared with SmartSDR and every other client. So on a Flex, your microphone setup simply **names which of the radio's own mic profiles to load** — nothing is copied, so there is nothing to drift out of date and nothing fighting other programs over the same settings. On a radio with no mic profiles of its own (a road that is being paved for other makes), the setup carries the actual values instead.

Pick a setup and press **Apply Setup**, and both halves go into effect — with plain words about anything that could not happen. If the radio does not have the referenced mic profile, JJ Flexible Radio says so and leaves the radio alone; it never guesses at a substitute and never creates mic profiles on a radio behind your back. If your computer is using a different microphone than the setup was made with, it says that too, and leaves the Windows level where it is rather than moving it for the wrong mic.

**Save Setup** captures your computer settings under the mic's name and asks what to store for the radio you are on: reference the radio's current mic profile (the usual right answer on a Flex), snapshot the radio's TX settings into the file, or store the computer half only. Creating a mic profile *on the radio* is offered right there when the radio has none loaded — offered, and only ever done because you chose it.

One setup, three radios? Save it once on each. The bindings live side by side inside the same setup, and applying it on any of them uses that radio's own half. On a radio you have never set it up on — a club rig, a friend's station over remote — Apply sets up your computer half and touches nothing of theirs. That is by design.

### Transmit audio source

The Microphone section starts with **Transmit audio from**, showing the radio's own input list — the mic jack, line in, PC audio, and so on. Everything below it follows that choice, because the controls that matter are different depending on where your audio comes from.

**Choose the mic jack** and you get the radio's own controls: Mic Gain, Mic Boost, and Mic Bias. Mic Bias is a low-voltage supply for electret microphones on the front-panel jack — around 3 volts, depending on your model. It is **not** 48-volt phantom power, and a studio condenser that expects phantom will not run on it.

**Choose PC audio** and those three disappear, because they act on the radio's microphone jack and do nothing at all to audio arriving from your computer. In their place you get **Windows input level** — the same level Windows Sound settings shows for the microphone you picked in Audio Devices, adjustable right here. If your driver offers a Microphone Boost, that appears too; a boost left turned up is the most common reason a level pins no matter what else you do.

The section always shows the gain that actually applies to your current source, so there is never a control sitting there that cannot help you.

A line under the level names the exact Windows device being adjusted. If JJ Flexible Radio cannot be certain which Windows device matches your chosen microphone, the control is switched off and that line says why rather than risk moving some other microphone's level.

### Moving around

The two transmit categories are a walk-through, running outward from your computer to the radio. This Computer holds the audio devices, your microphone setups, and PC cleanup. Transmit Settings holds the microphone the radio listens to and its gain, processing, the TX filter, and the TX monitor. Check your audio takes it from there to the air.

Press **F6** to jump to the next section and **Shift+F6** to go back. Each jump
names the section you arrived in, wraps around at the ends, and skips any
section that is currently hidden — so with PC audio selected it will not send
you to radio controls that are not on screen.

Tabbing works too, and your screen reader announces each section as you cross
into it. F6 is for when you know where you are going.

The Workshop is divided into categories, listed down the left-hand side of the window. **Ctrl+Tab** moves to the next category and **Ctrl+Shift+Tab** to the previous one, from anywhere in the window; either one lands you on the list, so you hear which category you arrived in and how many there are. F6 then moves between the sections inside it. Categories are the big divisions, sections are the groups within one.

### The sculpting chain

Mic gain, mic boost, mic bias, the compander and its level, the speech processor and its mode, and the TX filter edges with a live width readout. The TX Monitor section holds the monitor toggle, level, and pan; in phone modes its header names the mode you're in so you know which monitor family you're adjusting.

## Check your audio

Check your audio is every way of checking how you sound, in one window. Open it with `Ctrl+J`, then `Alt+C`, from anywhere; from the Audio menu; or with the **Check your audio** button in the Audio Workshop. Opening it never keys the radio. Focus lands on the method, `Shift+Tab` from there reaches **Start Audio Check**, and `Ctrl+Enter` starts or stops a check from anywhere in the window. `F6` and `Shift+F6` move between its sections.

Every check keys the radio through the same safety system as ordinary transmit: the timeout warnings, the license check, and the hard kill all stay on duty. When a check starts, focus moves to your gain, and the live reading is the very next `Tab`.

### The countdown before it keys

Before a check keys the radio, it counts you in: a pip a second, then a long tone an octave higher on the last beat, and then the usual transmit tone. While it counts, the check is setting itself up, so the radio keys as soon as both are done; if the setup takes longer than the count, the last beat waits for it. Escape during the count stops the check before anything keys.

How you hear the count follows your sound and speech settings. With the transmit alert sounds off, the count is spoken: "5, 4, 3, 2, 1, transmitting." With them on and verbosity at Chatty, each number is spoken on its pip. With them on at Terse, you hear the pips alone. The count can open by naming the check, for example "Starting the microphone test in", then the numbers. The count keeps pace with your screen reader. When your screen reader reports when it begins and finishes each number, as NVDA does, each pip waits until the screen reader has begun its number, each number waits for the one before it to finish, and the radio never keys before the last number has been spoken. Other screen readers, such as JAWS, don't report that, so JJ Flexible Radio estimates how long each number takes, and it can't hear anything the screen reader says on its own. With the transmit alert sounds off, each number is allowed that estimate plus a margin before the next one, and before the radio keys. With them on, the pips are the count, and the numbers simply follow the estimate, so with another screen reader the pips are the count to trust. If the numbers can't be spoken at all, the check stops without transmitting and tells you so.

All of it lives in Settings, on the Audio tab, in the **Countdown** group: turn it off, set its length from 2 to 10 seconds (5 to start with), pick the pip's pitch from the list or a custom one from 200 to 2000 hertz, set how long each pip lasts, and choose whether it names the check. **Play sample** plays it the way you've set it before you keep it. The Fix tool counts down before its tests at the same length, pitch and pip length.

### The methods

The method decides how you hear yourself. Each name says what you hear and whether anything goes out. Only the methods your radio and your connection can do are offered, and the line under the method says what Start will do and, if a method is missing, why. `Ctrl+F1` on the method says the same about the method shown, and `F1` anywhere in the window opens this page at that method.

Your method is remembered per radio. The same radio reached over SmartLink starts on Record yourself if the method you chose locally is not offered there, without forgetting your local choice.

#### Test your microphone, nothing goes on the air {#check-audio-mic-test}

Start keys the radio at zero power, whatever the check power says, so nothing goes on the air. Your microphone's verdict appears in the reading box, and nothing is played back.

It keys on purpose: the level that matters is the one going into the transmit chain.

#### Hear yourself live while you talk {#check-audio-monitor}

Start keys the radio at your check power and turns on the transmit monitor, so you hear yourself as you talk. Your microphone's verdict appears in the reading box.

Fine on a local connection; over SmartLink the monitor arrives late enough to trip up your speech.

#### Record yourself, then hear it played back {#check-audio-record}

Start keys the radio at your check power, and the radio records you. When you stop, it plays the recording back. It keeps the last two minutes. Your microphone's verdict appears in the reading box.

Talk first, listen after. The recording carries the full processing chain. This is the one you start on over SmartLink.

#### Hear what you transmitted, received off the air {#check-audio-off-air}

Start makes an off-air take at one watt: the radio listens to its own signal through its second transverter port and plays it back after you stop. Your microphone's verdict and the take's measurements appear in the reading box. It needs a local or VPN connection, two receivers and a transverter port.

Its options, the IQ rate and whether to hear yourself during the take, appear only while it is chosen. Its measurements are loudness, peak, punch, brightness, splatter, and how what you sent compares with what came back.

#### Hear your real signal, looped back through the transverter port {#check-audio-loopback}

Start sends one watt through the transverter port to a listening receiver, records what it hears, and plays it back after you stop. Your microphone's verdict appears in the reading box. It needs a local connection, two receivers and a transverter port.

The listening receiver sits on another transverter port. Your signal arrives there enormously strong, so what you hear is your audio, present and processed, through an overloaded front end: it proves your audio chain and your transmitter work end to end, but it is not a faithful on-air listen. A WebSDR or KiwiSDR tuned to your frequency is still the gold standard. One watt into a transverter port with antennas connected can still leak a little RF, so identify if your regulations require it.

### Test tone and reference recording

These replace your microphone with a known signal while you transmit, and only one of them can be armed at a time.

**Test tone instead of microphone** sends a clean, steady tone in place of your mic, muted rather than mixed, along the exact path your voice takes. The pitch is yours: 440, 700 or 1000 hertz, or a custom frequency, remembered across sessions because it is about your ears rather than your rig. Tone level is in dBFS, from minus 40 up to 0; the default of minus 10 lands in the "Good" range. **Hear the tone while it transmits** plays it through your alert sound device whenever it is actually going out. The tone's controls appear only while it is armed. Arming never transmits by itself and never survives closing the window, switching radios or restarting, and while it is armed every key-down announces that the tone is going out instead of your microphone.

An SSB transmit filter typically passes roughly 100 to 2900 hertz. A tone outside it transmits nothing, silently, so JJ Flexible Radio warns you out loud the moment you set one there, when you arm it, at every key-down, and if the filter moves out from under an armed tone.

**Reference audio** sends a known recording instead: the shipped JJ Flexible reference voice, or a take you record yourself with **Record a reference take**. It is the honest way to answer "did that change help?", because a tone does not respond to a compressor and you never say it the same way twice. It arms the same way the tone does and transmits only when you key.

Both ride the PC audio path, so they need PC audio on and the radio's transmit input set to PC. If the input is the only thing in the way, arming switches it for you and puts it back when you disarm.

### Check power

**Transmit power during checks** is shown for the two methods that obey it, Hear yourself live and Record yourself, and is remembered per radio.

- **Dummy load, no RF** — the default. Every meter the check reads measures your audio before the power amplifier, so the reading is the same at zero watts as at a hundred.
- **Low power** — for "is RF actually leaving my radio?" Pick the wattage, down to 1 watt. It is a cap: it only ever lowers your power, and it never overrides a dummy load you set yourself.

### Levels and the reading

**Mic Gain**, or the **Windows input level** when your computer is the transmit source, sits right before the reading, and it is the same setting as in the Audio Workshop. The **Mic audio reading** is a read-only field with your level and the verdict. It updates continuously while you transmit and holds your last transmission's peak after you unkey, but it never speaks on its own: use your screen reader's read-current-control command whenever you want it.

### Results

Results appear only once there is a take. **Play last take** plays the slice recording with Record yourself, or the off-air take with Hear what you transmitted, and stops a playback in progress. With an off-air take you also get **Play what I sent** and **Tell me how my sound changed compared to my last take**, which compares each new take with the one before it this session. **Review unrestored take settings** appears only when a take left radio settings the radio never confirmed putting back.

### Escape, and what a check puts back

While a check is transmitting, the first Escape unkeys and stays; the second closes the window. Escape never leaves you transmitting. Every check puts back everything it changed, power, monitor, the listening slice and full duplex, on every way out: Stop, Escape, the transmit timeout, closing the window, or the radio going away.

One honest warning worth knowing: if your hand mic keys the radio through a hardware line, software cannot unkey it. If that happens the window tells you plainly and keeps warning until the line releases.

### Bench tools

The receive-only diagnostic take, the ten-second IQ capacity check and the two "Prove it is real" detune buttons prove that the take machinery works, rather than check how you sound, so they live on the Audio Workshop's **Diagnostics** category, under **Bench tools**. They are not offered over SmartLink, which does not carry IQ.

### Command Finder

"Check my transmit audio" in the Command Finder (`Ctrl+/`) opens Check your audio and starts a check with the method shown, in one step. Search for "audio", "check", "hear", or "myself".

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


The Record and play back Audio Check records the transmit slice even when a different slice is active. For local playback, JJ Flexible pauses VOX or CW break-in and restores it after the take. The slice Play control shows the recording length when known; pressing it during an existing transmission sends that recording through the transmit slice until you unkey. Play never keys the radio itself.
