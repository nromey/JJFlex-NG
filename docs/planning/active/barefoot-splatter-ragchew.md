# Barefoot Splatter Ragchew — the post-audio-arc track set

**Status:** planned 2026-08-16, not started. Walked track by track with Noel in
one sitting, one track at a time at his request
(`memory/feedback_review_one_item_at_a_time.md`) — so every decision below was
reviewed, not inferred.

**Track A is not in this file.** The roster and connect batch lives in
`docs/planning/active/qsy-pileup-handshake.md` and was specified separately.

---

## The principle that kept recurring — write it down once

Three times in one planning session, a value that looked simple turned out to
need to be a first-class object, and each time the fix was nearly free at the
point of first writing and expensive afterwards:

- **The connect preference as a `bool`** — cannot express a third path once JJ
  Flexible Connect exists. Wants an ordered chain.
- **A tone as an `enum WaveformType`** — cannot be authored, saved, shared, or
  reused by the waterfall. Wants a parameter set.
- **Version info as UI strings** — cannot be embedded in a crash report without
  duplicating the logic, and then the two disagree.

**So the standing rule for this track set: if a thing will be authored, shared,
persisted, or consumed by a second feature, model it as data from the first
line.** Not as an enum, not as display strings, not as a boolean. This is
cheaper than it sounds and it is the difference between the sketchpad and the
waterfall being possible later or being a rewrite.

---

## Track B — Telemetry honesty

**Theme:** every item is a readout that lies or is missing. None of it is a
feature; all of it is the instrument panel being wrong, which is what made the
2026-08-14 debugging session so expensive.

**The trace flood.** `startOpusInputChannel` (`FlexBase.cs`) is correctly
idempotent — it locks, checks `Started`, returns. But its `TraceLine` sits
**above** that guard, and `remoteAudioProc`'s main polling loop calls it every
iteration while transmitting. That is the 3.36M lines in four minutes and the
~20-second trace-part rotation. **`stopOpusInputChannel`, ten lines below, has
always had this right** — its trace is inside the lock and after the guard.
Making start match stop is the fix.

The open question underneath: whether that loop deserves pacing at all. It polls
for Opus RX data, so a blind `Thread.Sleep` buys quiet at the cost of receive
latency. Note the loop already carries a fix of this exact shape in its
`Disconnecting` branch — so the precedent exists and was applied deliberately
there.

**SC_MIC and SW ALC are not instrumented.** Both handlers store their value and
trace nothing. Two calls close a blind spot that cost a whole session.
**Instrument them throttled** — they fire at meter rate, and an unthrottled
`TraceLine` here would recreate exactly the flood being fixed above. Gate on
`Transmit` and emit a correlated snapshot of both at most once a second; the
correlation is what makes it useful.

**Forward power rounds sub-watt RF to zero.** `SMeter` returns `int`, and during
transmit it converts dBm to watts and truncates. Anything under half a watt reads
`0 W`, which is indistinguishable from not transmitting — the most misleading
readout in the app for low-power and transverter work, where sub-watt drive is
the normal operating point rather than a fault. (`_PowerDBM` is a `float`, so
there is no integer-division bug; the `(int)` return is the whole defect.)

`SMeter` is dual-purpose — watts during TX, S-units during RX — and its S-unit
callers legitimately want integers. **So add a separate `ForwardPowerWatts`
(float) rather than changing `SMeter`'s contract**, and switch the transmit
display path to it. Format with precision following magnitude: sub-watt gets
decimals because that is the entire point, a hundred watts does not.

**GPS status leads with the wrong fact.** Oscillator lock is load-bearing and can
disagree with the fix text during acquisition. Add the PPB figure. Read
`memory/project_gps_gnss_oscillator_facts.md` first — it corrects an earlier
wrong reading of the presence flags.

**Assert `mic_selection=PC` while PC TX audio runs**, and warn on divergence. The
one-shot set at opus-output start can be silently reverted by a later profile
load. This is the arc's thesis in one line: never stream TX audio into a closed
gate without saying so.

**Noel's addition:** verify that **all hardware inputs enumerate correctly per
model** — ACC on the 6300, BAL on the 8600. Same class of lie as a wattage that
reads zero. **May execute better in Track E**, which owns device enumeration;
Noel left the placement open. Natural bench work either way.

**Testable:** transmit and watch the trace stop exploding; key at low power and
see a real number; listen to the GPS announce.

---

## Track C — Settings that stick

**Theme:** settings you set and the app quietly did not keep. Every item treats a
stated intent as a command it may decline.

### The convention, settled 2026-08-16

Noel: *"If something's changed in settings and OK is pressed, the setting is
applied and the dialog closes. Apply applies and leaves the dialog in place.
There needs to be an apply on each screen for settings — no apply connection or
apply setting, just a standard OK and Apply."*

**OK applies and closes. Apply applies and stays. Cancel discards. Every settings
screen carries that same pair — no per-feature variants.**

This is the standard Windows convention, and the reason it became standard is the
reason to adopt it: the buttons mean the same thing in every dialog, so muscle
memory transfers. Bespoke per-screen buttons force relearning each dialog, which
is friction tax and worse through a screen reader.

**This reframes the radio-name bug.** Either there was an Apply that was missed
and OK discarded the edit, or there was no Apply and OK silently failed. **Both
are the same defect** — an OK that does not apply is broken either way. The
convention *is* the fix, not a workaround for it.

Two things the rule does not settle, decided here rather than left to emerge:

- **Cancel after Apply does not roll back.** Windows convention. The alternative
  needs an undo buffer per setting.
- **Apply stays present and enabled even when nothing has changed.** Convention
  says grey it out, and that is a useful "did it register my edit?" signal — but
  the house rule keeps disabled controls out of the tab order, which would make
  Apply *vanish* mid-dialog. Worse than useless when you are counting tab stops.
  Keep it present; let it be a no-op.

**Queued intents need their own voice.** When a setting cannot apply now — radio
disconnected — OK must say so plainly rather than implying it took effect.
"Saved; applies when you connect" is honest. Silence is what got us here.
(`memory/project_settings_are_intents_not_commands.md`)

### The items

- **REM ON reachable while disconnected.** Correction to the original task
  framing: it already exists — `TXControlsDialog` wires `RemoteOnCheck` to
  `Radio.RemoteOnEnabled`. This is about reaching it from per-radio settings with
  no live connection, and queueing to apply on connect. Don's radio being off is
  precisely the case it prevents, and it is unreachable exactly when needed.
- **The radio name that did not save.** Track A owns the display half
  (`PaintRoster` skipping the roster for discovered radios); C owns the save
  half. **Verify together or each looks fixed while the other still breaks it.**
- **Network settings discard unapplied port-forward edits on OK.**
- **Show the router mapping, and fix the comment that lies about it.** External
  TCP to radio port 4994, external UDP to 4993. The doc comment on
  `FlexBase.SetSmartLinkPortForwarding` claims the radio listens on the ports you
  type, which is wrong and misled a live debugging session.

### The "no physical access" flag — Noel, 2026-08-16

A per-radio setting meaning **"this radio is operated remotely; I cannot reach
its front panel."**

**Not redundant with Track A's path chain, and the distinction matters.** The
chain answers *how do I connect*. This answers *can a human reach the radio*. A
dual-homed radio at your own house might sensibly prefer SmartLink because you
are often out — and you can still walk over and press the button. Don's cannot be
walked over to. Geography, not networking.

**Make it explicit, not derived**, even though Track A will know enough to guess.
The failure modes are asymmetric: wrongly inferring "local" *suppresses* a
warning that would have saved you; wrongly inferring "remote" shows a prompt you
did not need. A safety gate should open because the operator said so.
Pre-populate the default from the path chain, show what it picked, allow
override.

**What it changes beyond the one warning:**

- The recovery ladder in `memory/project_flex_remote_power_facts.md` ends at "a
  human." For a flagged radio that rung does not exist, so the advice above it
  must be more careful and REM ON stops being optional.
- Firmware updates are LAN-only; a failed one on an unreachable radio is a
  different category of problem.
- Any advice amounting to "power cycle it" is useless and must not be offered.

**The cascade flow.** Checking the box presents a warning, then a yes/no on the
consequential settings it implies (REM ON active, update-without-hardware-check,
and so on). Yes sets them. **Unchecking asks again and reverses.**

- **Enumerate the bundle, do not just ask yes/no.** "Yes" to an unnamed set is
  not informed consent and teaches nothing. Name each setting and its new value —
  that is what makes the prompt educational rather than a speed bump.
- **Do not clobber on reverse.** If someone hand-tuned one of those settings
  afterwards, un-checking must not silently undo it. Listing what will change
  handles this: they can see it and decline that one.
- **REM ON has a hardware prerequisite.** Enabling it does nothing unless the RCA
  jack is wired to a relay. Say so, or it hands someone false confidence about a
  radio they cannot reach — the precise failure this flag exists to prevent.

**"Don't show this again," and why it is safe.** Noel raised the snowbird case —
a second home, the flag flipped twice a year, the explanation read fifteen times.
The dialog is two things with different rules: the enumerated prompt is
*teaching and consent*; the summary afterwards is a *receipt*. **Suppressing the
teaching is friction-tax compliance. Suppressing the receipt would be a silent
change, which we do not do.**

- The receipt is an **OK-only dialog, not a Tolk `Speak`.** A `Speak` is
  ephemeral, never reaches braille, and can be cut off mid-sentence — which is
  exactly Noel's 2026-08-14 complaint about missing a notification. A dialog is a
  real object in the tree: re-readable, braille-reachable, acknowledged.
- **Scope the suppression globally, not per radio.** The explanation is identical
  for every radio; learning it once should count once. The receipt still fires
  per radio, every time.
- **Version the bundle so it returns when its contents change.** If a setting is
  later added to the cascade, the explanation someone dismissed is no longer the
  one they read. Otherwise "don't show again" quietly becomes "never tell me
  about new things you do to my radio."

**Related task:** #70 — repeat-last-message holds exactly one message and should
be a short ring. That is the general safety net for "I missed the announcement";
the receipt dialog is the specific case for things too important to miss.

**Help page owed:** reaching your radio remotely for updates, since firmware is
LAN-only — VPN or Tailscale with subnet-router instructions. Written for Don
specifically.

**Future, scoped honestly:** a package that makes the radio reachable over a
tailnet. **You cannot install Tailscale on the radio** — it is a closed
appliance. The mechanism is a **subnet router on the radio's LAN**, which is
already the recommendation for Don's Pi. Same outcome, achievable version.

**Testable:** name a radio while disconnected, connect, confirm it survived; set
REM ON with no radio present; edit forwarding ports, press OK, reopen.

---

## Track D — The meters subsystem

**Scope changed during review.** It began as "the Live Meters tab has no tab
stops" and became a subsystem, because the investigation found there is no meter
management anywhere.

### What actually exists today

**Two separate systems that do not correspond to each other:**

- **The readout** — the Live Meters tab is **eight fixed labels** (S-Meter,
  Forward Power, SWR, Mic audio, TX drive/ALC, Amp ALC, PA Temperature, Supply
  Voltage), built by `MakeMeterLabel` as plain `TextBlock`s. Not focusable.
  Purely display.
- **The tones** — a different set of **four slots**, configurable only by named
  preset. `MeterToneEngine.ApplyPreset` hardcodes each:
  `ConfigureSlot(0, SMeter, true, 0.6f, 0f, 200, 1200)`. The only controls are a
  preset combo in the Settings dialog and `Ctrl+Alt+P` to cycle.

**So eight meters are readable, four are audible, and they are not the same
four.** There is no way to add, delete, or individually enable a meter.
`MeterToneEngine.AddSlot()` exists and nothing calls it. `MeterSlotConfig`
persists Source, Enabled, Volume, Pan, PitchLow, PitchHigh and Waveform — richer
than anything reachable, since only presets ever write those fields.

### D1 — Make the readout navigable

Read-only text boxes with real labels for the eight existing readings, same idiom
as the device and mic readings. Depends on nothing, touches one file, testable in
a minute. **Then reconsider the live region** — a polite live region on a value
changing twice a second is a lot of announcement for something now readable on
demand.

### D2 — The voice engine

**Do this first; it gates D3 and it is the part that can actually fail.** Whether
five voices stay distinguishable with three playing under speech is an empirical
question nobody has answered.

Voice palette, from Noel plus additions: sine, square, triangle, phone-ring
alternation, the rolling-R trill, raspy, filtered noise, and a 500 ms tone
alternating over an interval. Added: **additive harmonic voices** (hollow, reedy,
bell, organ) which are cheap and far more distinguishable than waveform swaps;
**pulse width** (10% duty reads thin and nasal, 50% full and hollow, from one
oscillator); and **attack character** (pluck versus swell), which maps naturally
onto "this meter is jumping" versus "this one is steady." Filtered noise has two
axes worth exposing separately — bandwidth and centre frequency.

**Hard requirement: voices are DATA, not an enum.** A voice is a small parameter
set — partial amplitudes, modulation rate and depth, attack, noise character. If
D2 ships `enum WaveformType` with a switch behind it, the sketchpad, the sharing
packs and the waterfall reuse all become rewrites. This is the standing rule at
the top of this file, and D2 is where it binds.

**Voices are first-class named objects**, not fields inside a meter slot. Meters
reference a voice; waterfall categories will later reference the same voices. A
voice defined inside a meter either cannot be reused or gets duplicated, and then
the two drift and the operator learns two vocabularies for one language.

**Governing grammar** (from `docs/planning/active/kerchunk-sidetone-pileup.md`):
**timbre identifies the meter, pitch carries its value, pan enhances but is never
load-bearing** — pan alone dies for mono listeners and asymmetric hearing loss,
which is Patrick's axis.

### D3 — Management UI and audition

Create, name, set pan and pitch range, assign a voice. A list of what exists.

**Keyboard, recommended rather than left open:** **Space** toggles enabled (the
universal list idiom, announced by every screen reader, no modifier). **Delete**
deletes, with a confirm. **Enter** opens properties. **Shift+F10 / Applications**
for the context menu — Right Ctrl for Noel
(`memory/user_applications_key_right_ctrl.md`). **Avoid Shift+Enter** — not a
standard list idiom, so nothing announces it and nobody discovers it.

**Rows speak state, not just name** — *"SWR, enabled, bell, centre"* — so
arrowing tells you everything without opening anything.

**The audition button must sweep, and must play in context.** Pitch carries the
*value*, so the tone is moving constantly in real use; a voice crisp at 200 Hz
can be mud at 1200. Auditioning at one pitch tells you almost nothing. Offer
**low / mid / high / sweep**, sweep as default. And offer **solo** versus
**against everything currently enabled** — a tone distinctive alone routinely
vanishes beside another, and the with-everything test is the one that predicts
real use.

### D3 phase two — the `Ctrl+J M` layer

Not a separate track: it cannot start earlier, the person who just built the
meter model is the right one to expose it, and keeping it here keeps the
**keyboard-audit obligation** in one place.

**One mode, and you are always standing on a meter.** Not select-mode versus
action-mode — that is what turns into a maze.

`Ctrl+J M` enters and announces where you are: *"Meter mode. SWR, enabled, bell,
centre."*

- **Up / Down** move the selection, announcing each — the discoverable path,
  needs no memorised numbers
- **A number** jumps straight to that meter — the fast path, same destination
- **Space** toggles enabled
- **Left / Right** pan
- **Shift+Up / Shift+Down** volume (plain arrows are navigation)
- **V** cycles the voice
- **O** solo, **M** mute-all and restore
- **P** cycles preset — **move `Ctrl+Alt+P` here** and free the flat chord
- **T** enters tone tweak (below)
- **Escape** leaves, and says so

**Why select-then-act rather than number-then-toggle**, and it only matters when
you cannot see the list: a mistyped digit under number-then-toggle silently
changes the wrong meter with nothing to tell you. Under select-then-act it lands
you somewhere unexpected and *announces it*, so you notice before anything
changes. One extra keystroke buys "I always hear what I am about to change."

Mode is sticky until Escape, and auto-exits after a stretch of no input rather
than holding the arrow keys hostage. Every chord speaks what it did
(`memory/project_no_silent_keystrokes_rule.md`).

**Live tone tweak — `T`.** Noel: tune it *in situ* rather than in the sterile
workshop. **The workshop is where you design a voice; the air is where you find
out whether it works** — the audition sweep runs in a quiet room against nothing,
while real use is band noise, your own speech, three other meters, and a value
moving in ways a sweep does not imitate. This is the same lesson as
`memory/project_earcon_audibility_rf_environment.md`.

Same model nested once: **Up/Down cycles which characteristic, Left/Right adjusts
it live, Escape returns.** Three characteristics, because more turns a leader
layer into a maze: **brightness** (upper harmonic content — the biggest "does it
cut through" lever), **modulation rate**, **modulation depth**. Announce as you
go: *"Brightness, forty percent."*

**Open problem this creates: tweaking a shared voice.** If you tune "bell" live
from the SWR meter, did you change bell everywhere, including for the waterfall
later? Almost certainly the live tweak should create a **per-meter override**,
with an explicit "save as a new voice" action. Not solved here, **but the data
model must leave room for it — which is a decision D2 makes whether or not it
means to.**

### Later, not now — the sound sketchpad

Noel: let people play with settings, add a harmonic, discover sounds — explicitly
to help invent the waterfall vocabulary.

**Bounded by making it a sketchpad, not a synthesizer. The value is the capture,
not the controls.** An editor you can fiddle with produces nothing durable —
you find something great, lose it, and describe it in words. What makes it worth
building is **"save this as a named voice,"** which turns a sound Noel discovered
into a thing that can ship.

**Build it when waterfall design starts** — that is when the vocabulary gets
invented and the tool pays for itself. Behind the workshop's advanced surface;
Margaret never meets a partial-amplitude slider.

**The division of labour is the real argument:** Noel has the ears and the
operating context, Claude has the build. Anything converting his ears into data
that can ship beats guessing at harmonic ratios.

### Sharing — one mechanism, not four

Noel: voices as JSON, exportable, shareable meter packs; same for waterfall
sounds (narrow-band, CW-like, wider-band).

**There are now at least four kinds of shareable user content** — noise profiles
(`memory/project_noise_profile_sharing.md`), DSP model packs
(`memory/project_dsp_model_pack_distribution.md`), curated Elmer material, and
voice/meter packs, with waterfall packs behind them. **They should share one
package mechanism and ride the Data Provider**
(`memory/project_jjflex_data_provider.md`), not grow four formats.

**Packs stay pure data — no code, no scripts, no URLs fetched at load.** A voice
is numbers, so this costs nothing, and it means a pack from a stranger on a
reflector is safe by construction rather than safe because someone reviewed it.
The moment a pack can execute or fetch, sharing becomes a trust problem and the
appeal dies.

**The waterfall vocabulary is achievable.** Noel's categories — narrow-band,
CW-like, wider-band, plus digital and perhaps carrier-only — are five or six,
comfortably inside the perceptual ceiling of five to seven per axis, and timbre
and modulation multiply.

**Open question carried forward: may two meters share a source?** Two SWR meters
with different pitch ranges, one coarse and one fine, is genuinely useful. One
word, but it changes the data model.

**Testable:** tab through Live Meters and land somewhere; arrow readings at your
own pace; run two meters together and see whether you can tell them apart.

---

## Track E — Device opening and rate policy

**Theme:** which device we open, at what rate, with how many channels. Four tasks
that look separate and are one decision.

- **Host API and sample rate are the same choice.** The default input device is
  whatever PortAudio nominates — usually MME — and nothing says so;
  `paWinWasapiAutoConvert` is what unblocks devices Windows holds at 44.1 kHz in
  shared mode. Decide them apart and you default to a host API that cannot reach
  the rates you need.
- **Mono capture.** Open at the device's native channel count, duplicate mono to
  stereo in the callback, walk half the buffer for mono. **`MicProbe` already
  does exactly this duplication** — copy the pattern, do not invent one.
- **Selectable Opus TX rate**, plus the low-resolution DAX IQ stream. Cheap now
  that rate is settled before the codec is built; every model already offers
  24 kHz. This is the fallback for Don whenever Tony's link is unhappy.
- **Re-measure before chasing.** "Decoded PC audio too quiet" and "tone monitor
  clicks" both predate the rate-negotiation fix, which may have moved both.
  Measuring is the first step, not a preliminary to it.
- **Input enumeration per model** — ACC on the 6300, BAL on the 8600. Raised
  under B; probably belongs here since this track owns enumeration.

**The rate fix is still unproven on hardware that needs it.** The headset and the
Evo both run at 48 kHz — where the old code already assumed it was — so every
test so far confirms only that nothing broke. **The onboard Realtek is the
44.1 kHz candidate.**

**The M50x rig makes this sharper than usual:** the same microphone into the Evo
versus into the radio removes the capsule as a variable, so a difference heard is
genuinely the path. First time this arc has had that
(`memory/project_audio_arc_test_microphones.md`).

### Confirmed at the bench 2026-08-16, before the track started

**There is no Realtek** on either machine — the ms-02 has generic HD Audio, the
EVO8, VAC and an NVIDIA device; the laptop has an Intel Smart Sound array.
"Realtek" was shorthand and it sent an earlier plan hunting for the wrong thing.
**The requirement is a device Windows holds at a non-Opus rate, not a brand.**

**Nothing on the ms-02 runs at 44.1 kHz** — every capture endpoint is 48 or 96
(read from the MMDevices registry). So that test case must be *created* rather
than found.

**96 kHz is the better trigger than 44.1, and the EVO8 already does it.** Opus
supports 8, 12, 16, 24 and 48 kHz — 96 is as unsupported as 44.1, exercises the
same negotiation path, and for anyone with a real interface is the *more likely*
real-world case. Setting it from the EVO8's own control panel beats fighting the
Windows default-format dropdown.

**The rate proof is radio-gated.** Negotiation runs on the Opus TX path, not on
the Microphone Check (which uses `MicProbe`, a separate opener). So this rides
with a bench session rather than being a standalone errand.

**Mono confirmed from the operator's seat.** The EVO8 exposes genuine 1-channel
endpoints — Mic | Line 2, 3, 4, Instrument 1, and both single Loop-backs. Noel
selected one: the row reads *"...: not usable yet"* and selection is refused with
*"it needs a stereo device."*

**Two consequences:**

- **Two refusal messages, in two different words, neither giving a reason.** The
  row tag in `Devices.cs` appends `" — mono, not usable yet"`; selection-time
  emits a separate "needs a stereo device." Noel heard the row without the word
  *mono* at all. One limitation, two vocabularies, no explanation — a case for
  #65.
- **Priority up.** Noel's available workaround is to gang two EVO inputs and pan
  both to centre — doing *in hardware, with the interface's own routing
  software*, exactly what the fix does in a few lines. That works because he owns
  a multi-channel interface. **Someone with a single mono USB headset mic has no
  workaround at all**; for them the app simply cannot use their microphone. Same
  shape as the note already in `Devices.cs` about hiding by channel count making
  a laptop's only real microphone unselectable — mono devices are frequently
  somebody's *only* microphone.

**Testable:** a genuinely mono device (EVO8 mono endpoints, already to hand), the
EVO8 at 96 kHz on the TX path, and a 24 kHz transmit.

---

## Track G — The honest About page

**Theme:** when Don reports a problem, we need to know exactly what he is
running, and there is currently no way for him to tell us.

**A WebView2 page** (Noel's call), with a plain fallback for machines without
WebView2.

**Why WebView2 is the right call and not merely cosmetic: it gives browse mode.**
This is the wall hit on the Audio Workshop — `HeadingLevel` did nothing there
because a WPF dialog runs in focus mode, where `H` types a letter. A web page
runs in browse mode: `H` jumps headings, arrows read continuously, selection and
copy behave normally. For a page whose whole job is "read this to your supporter
or paste it into a report," that is the difference between tabbing fields and
just reading.

**Query the libraries at runtime; never hardcode the numbers.** An About page
with `"Opus 1.5.2"` baked in is *worse* than none, because it lies with
confidence. **This project already made that mistake** — CLAUDE.md claimed Opus
1.5.2 until caught, when the shipped DLLs were 1.6.1 on both architectures. The
documentation drifted on the exact fact this page exists to report.
`opus_get_version_string()` and `Pa_GetVersionText()` cost nothing to call.

**PortAudio needs special handling.** It reports `"PortAudio V19.7.0-devel"`
whether built in 2021 or last week — upstream never bumped it. Only the revision
suffix distinguishes them. **Lead with the revision; never show a bare 19.7.0**,
which would tell a supporter the opposite of the truth.

**Usable in a support conversation:** selectable text, a copy-everything button
so nobody reads hex aloud, reachable with no radio connected. Include the
non-library facts support actually asks for — self-contained or not, the
executable path, and the trace file location, which turns "where are your logs"
into a glance.

**Entirely local.** No external fetch to render an About box
(`memory/project_no_silent_phone_home.md`).

**Build it as a data structure first, UI second** — the crash reporter and
feedback bundle need the same content embedded. If the page assembles display
strings, the reporter duplicates the logic and the two disagree about what
version you are running.

---

## Parked — Track F, presets and config truth

Mic profiles bound to the device, corrupt presets silently becoming defaults,
presets carrying no schema version, presets not recording which input they were
tuned for, and the `audioConfig.xml` two-directory migration.

**Nothing is broken for anyone today**, and it is the only track that would
collide with D over `AudioOutputConfig.cs`. Parking it is what makes the file
ownership below clean.

---

## File ownership — no two tracks own the same file

- **A** — `RigSelectorDialog.xaml.cs`, `KnownRadioRoster.cs`,
  `RadioConnectionCache.cs`, plus FlexBase's client-identity region
- **B** — FlexBase's meter, power, GPS and remote-audio-loop regions
- **C** — per-radio settings, Settings→Network, `TXControlsDialog.xaml.cs`
- **D1** — the Live Meters region of `AudioWorkshopDialog.xaml.cs`
- **D2** — `ContinuousToneSampleProvider.cs`, `MeterToneEngine.cs`
- **D3** — new management UI, plus `KeyCommands.cs` for the leader layer
- **E** — `JJPortaudio/Audio.cs`, `Devices.cs`, `AudioDevicesDialog.xaml.cs`
- **G** — new About page and its data provider

**`FlexBase.cs` is the exception**, touched by A, B and C in three disjoint
regions. Manage it explicitly: each track is told **"reuse the symbols you find;
if you conclude one should move or change signature, report it rather than doing
it"** — the 2026-08-12 lesson where two tracks merged with zero textual conflict
and the build failed. **Build after every merge**; a clean `git merge` is not
evidence the result compiles.

**D2 publishes the voice type before its synthesis is finished**, so D3 can build
against a known shape rather than waiting or guessing.

---

## Run order

**Start together:** B, C, D1, D2, E, G. All independent, all own distinct files.
**D2 early on purpose** — it gates D3 and answers the risky question.

**A runs alongside but produces a written state machine first**, not a diff. Its
Phase 1 deliverable is a map, reviewed before any roster code changes.

**D3 starts when D2 has published the voice type.** Its phase two — the leader
layer — follows D3's own numbering and naming decisions.

**Merge order:** D1, G, E, B, C as they land, building after each; then D3; then
A last, since it carries the largest FlexBase diff and should rebase onto the
others rather than the reverse.

**Three testable builds rather than one.** B and C give a settings-and-readouts
build with no connection risk. D and E give the audio and meters build. A gives
the connect build, which is also Don's.

---

## Test additions owed

Added to the existing matrices rather than replacing them:

- Transmit and confirm the trace no longer explodes; check part rotation timing
- Key at sub-watt and confirm a real number rather than zero
- GPS announce leads with lock and carries PPB
- Set a radio name disconnected, connect, confirm survival
- Set REM ON with no radio present; confirm the queued-intent wording
- Edit forwarding ports, OK, reopen, confirm the edit persisted
- The cascade dialog: enumerated, receipt fires, suppression is global, and a
  version bump un-suppresses it once
- Tab through Live Meters and land somewhere
- Two meters audible at once, told apart by ear, with speech over the top
- Audition swept, and audition against everything enabled
- Every new or changed chord in the `Ctrl+J M` layer **pressed on a real build**
- The Realtek at 44.1 kHz; a mono device; a 24 kHz transmit
- ACC on the 6300 and BAL on the 8600 enumerate
- About page read end to end with browse-mode navigation, and copied

**Keyboard audit owed** by A (Alt+R removal) and by D3 phase two (the whole
leader layer). Both need the full checklist including pressing the key.

---

## Open questions

- **May two meters share a source?** Changes D2's data model.
- **Does the readout list and the tone-slot list become one list?** Decides
  whether D is a fix or a subsystem.
- **Where does ACC/BAL enumeration execute** — B or E?
- **Does the live tone tweak fork a voice or edit it in place?** D2 must leave
  room either way.
