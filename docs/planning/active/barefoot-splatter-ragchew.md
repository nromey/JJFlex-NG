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

### Bench results 2026-08-16 — measured on the 8600, not inferred

**The radio reports 102 meters.** The first inventory dump caught it
mid-registration and logged 11; once the re-log-on-change fix was in, the settled
count was **102**. Our hardcoded eight is **thirteen times short**. This ends the
argument for asking the radio rather than maintaining a list.

**SC_MIC and ALC both exist and both work — an earlier claim in this file that
they might never fire was WRONG**, and Noel disproved it from the operator's seat
before the trace did. The trace shows `SC_MIC NOT FOUND, ALC NOT FOUND` twice and
then `found, found` at the same instant the count reached 102: the TX meters
simply register late, and the lazy-retry design handles that correctly.

**But that exposed a real latent bug next door.** `hookTxMeters` sets its "done"
flag only when it finds **both** meters — so it subscribes to whichever one it
did find, then runs again on the next mic-meter event and **subscribes to it
again**, indefinitely. Both arrived together here so it never bit. **On any radio
reporting one and not the other, that is an unbounded handler leak** with every
event firing N times and N growing forever. Fix: track the two subscriptions
independently.

**The forward-power defect, demonstrated with real numbers.** Three consecutive
keyed samples with the radio's power set to its default of **zero**:

- 17.0 dBm = **50 mW**
- 22.4 dBm = **174 mW**
- 18.7 dBm = **74 mW**

Real RF, leaving the radio, every time. Run through `SMeter`'s
dBm-to-watts-then-truncate conversion, all three display **0 watts** — identical
to not transmitting at all.

**This is no longer a theoretical worry about rounding.** Power set to zero still
makes 174 mW, and both the setting and the readout say zero while the radio
transmits. It is exactly the normal operating point for transverter work reading
as a fault, and it is the strongest single argument in this track.

Everything else measured healthy: SC_MIC tracked the test tone at about −11 dBFS,
SW ALC ran between −2.5 and −4.5, and the opening −150 sample is just the
pre-data idle state.

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

### THE FINDING THAT REFRAMES THIS TRACK — the radio instruments its whole chain

**Measured 2026-08-16.** The 8600 reports **102 meters** (37 distinct names across
four source types). They are not a grab-bag: **they are a signal-strength probe at
every stage of both signal chains.**

**Transmit, in signal-flow order** — `SC_MIC` at the microphone, `CODEC`,
`SC_FILT_1`, `SC_FILT_2`, `AFTEREQ` after the EQ, `TX_AGC`, `RM_TX_AGC`,
`COMPPEAK` just before the clipper, `ALC` after software ALC, `B4RAMP` and
`AFRAMP` either side of the ramp, `POST_P` after all processing but before power
attenuation, `ATTN_FPGA`, then `FWDPWR` / `REFPWR` / `SWR`.

**Receive, per slice** — `24kHz` broadband, `ESC`, `OSC`, `NB`, `TNF`, `ANF`,
`NR`, `AGC`, `SQUELCH`, `LEVEL` in the passband, plus `AFMDemod` /
`AFMDemodFilt` for FM.

**This is the honest-TX-audio question answered at nineteen points by the radio
itself**, and it has been available the whole time.

**It directly closes a complaint from 2026-08-13** — Noel: *"listening to TX
Monitor I can't really hear a difference with processing vs. no processing."* He
could not hear it. `COMPPEAK` against `POST_P` **measures** it.

**And the receive side makes DSP perceivable without sight**, which is the larger
claim: you can measure what the noise blanker actually removed, what the notch
filter took, what noise reduction is doing right now. A sighted operator infers
that from a waterfall changing shape. This is the same information and needs no
waterfall at all.

#### What a meter can and cannot do here

Noel asked the right question: *"we can't actually listen to what it takes out
but we can watch the needle jump?"*

**Correct — and it is not one needle, it is a subtraction.** A single "after NB"
reading tells you almost nothing, because it also rises when the band gets
busier. What isolates the stage is **the gap between the stage before and the
stage after**. Equal readings mean the stage is doing nothing, and neither meter
alone would ever tell you that.

**So the genuinely interesting meter is DERIVED** — value = stage A minus stage B
— which is the third meter category this plan already carries. "NB effectiveness"
becomes a nameable, tonable meter for very little code.

**Actually hearing the removed audio** would need the signal before and after the
stage as audio, time-aligned, and subtracted. The radio gives levels, not
per-stage audio. Reachable in principle via IQ minus slice audio; not worth an IQ
channel and the alignment work when the measurement answers the real question.

#### OUR DSP is better instrumented than the radio's — Noel, 2026-08-16

**An asymmetry worth building on.** For the radio's DSP we receive *levels* and
nothing else, so a stage's effect can only be measured. **For our own DSP — RNN
noise reduction, spectral subtraction, anything running on the PC — we own the
audio at both ends**, so the residual is a subtraction:

    removed = input − output

Play that to a monitor and **you can literally listen to what the noise reduction
took out.** Impossible for the radio's noise blanker; trivial for ours.

**This is the standard check in audio engineering, and it answers the question
that actually matters.** Noise reduction that is eating your voice sounds
"processed" but offers no way to tell what it took — and listening to the
*output* can never tell you, because the missing parts are not there to hear.
Listen to the **residual** and it is immediate: **hear speech in what was
removed, and it is over-reducing.**

Particularly valuable for a blind operator, who cannot glance at a spectrogram
showing the notch carved out of their voice — but can hear the carving.

**It also confirms the pathway is live.** Noel: add it to the transmit verbiage
*"to make sure that we know that that pathway's working."* Same lesson as never
streaming TX audio into a closed gate — **processing that is enabled but silently
bypassed sounds exactly like processing that is on and gentle.** Both give a
clean signal and no information.

**The residual distinguishes them instantly: bypassed produces silence in the
residual.** Not quiet — nothing. An unambiguous self-evident test needing no
reference and no calibration, usable on transmit while operating.

**The remedy must sit beside the diagnostic** (Noel): *"if it is eating your
voice you can change the strength of the noise reduction or DSP."* A diagnostic
without a remedy is just bad news — the same lesson as the level verdicts, which
were not useful until they named the control and the direction.

So the strength control must be **live while monitoring**: hear voice in the
residual, turn it down, hear it again, voice gone. Not apply-and-retest.

**This makes transmit-audio adjustment SELF-SERVICE, which it currently is not.**
Today the only way to learn you are over-processed is someone on the other end
saying so. Residual plus a live strength control means an operator can judge it
alone, at any hour, without asking a favour on the air.

**Monitor the OUTPUT as well as the residual.** Turning strength down until no
voice appears in the residual may leave far too much noise in the output — the
right setting is a trade-off and judging it needs both. Switch between output,
residual and both, with strength live throughout.

Relates to `memory/project_dsp_controls_design.md` (engine complete, UI is the
gap) and the model-pack work — a residual monitor is also how an operator would
judge one downloaded model against another.

#### Two instruments, two jobs

**Tones — continuous, while operating.** Two voices, one per stage, and **the
interval between them is the effect**: they spread as the stage works harder and
converge to a unison when it does nothing. Toggling a stage on and off also works
but stops you operating and compares against a second-old memory.

**Analysis — considered, on demand.** Noel's proposal: *"click a button, have it
sample readings and then analyze it."* Samples the chain over a window, then
explains it in words.

- **Needs a WINDOW, not an instant.** One reading of a noise blanker is
  meaningless — impulse noise is intermittent. Same shape as the microphone
  check's capture window.
- **Report the delta, not the level.** "NB took 15 dB", not "NB output is −75".
- **The most valuable output is "this stage is doing nothing."** *"Your notch
  filter is on and removed nothing"* is actionable; a number is not. Same at the
  other end — *"AGC is compressing 30 dB, which usually means RF gain is too
  high."*
- **The TX chain gets the identical button**, and that is the one answering the
  compander question directly.
- Plain words in the main line, figures behind the escape hatch — same rule as
  the level verdicts.
- **A natural Elmer step** — "let me explain your receive chain" is exactly what
  an Elmer does.

#### The dependency that gates the analysis

**The chain ORDER is not documented anywhere found so far.** The stage names are
known; that `NB` precedes `TNF` is not. Tones do not care — pick any two and
listen to the interval. **Analysis does care**, because pairing the wrong two
produces a confident report that the noise blanker *added* 12 dB, and confident
nonsense is worse than no feature.

**Bench protocol to settle it, and it is small:** enable one stage at a time and
observe which meters move. Or find it in FlexRadio's documentation.

#### The picker consequence

**102 meters cannot be a flat list.** Grouping by source (TX chain, slice N,
radio, codec) is mandatory, and **within the TX chain the order must be
signal-flow, not alphabetical** — presenting `AFRAMP` before `B4RAMP` because A
sorts first would actively mislead.

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

### Decided 2026-08-16 — the meter model

**One list, not two.** The eight readouts and the tone slots become a single list
where "readable" and "audible" are properties of the same meter, rather than two
unrelated systems with different membership. This also gives the `Ctrl+J M` layer
one set of numbers to address instead of an ambiguous two.

**The eight are a RECOMMENDED set, not built-ins.** Noel: *"A Claude hardcoded
those eight, they do not need to stay unless we have recommended meters or
something."* Correct — protecting them as undeletable would be protecting an
accident. Ship them as a sensible starting point, allow deletion, and provide a
**restore-recommended** action. Nothing is sacred; nobody starts from an empty
list. This also removes the two-classes-of-row problem an earlier draft created.

**The radio tells us what meters exist.** `Radio.GetMeterList()` sends a literal
`"meter list"` command and the reply populates `List<Meter>`; our eight are a
hardcoded subset in `MetersPanel.xaml.cs`. **The picker must offer whatever this
radio reports right now**, not a fixed list — a 6300 and an 8600 legitimately
differ, Amp ALC is only real with an amp fitted, and meters carry a `Source`
including `Meter.SOURCE_SLICE`, so per-slice meters appear and vanish with
slices. Hardcoding hides capability on the big radio and invents it on the small
one.

**A meter is a SOURCE plus a RANGE plus a VOICE.** Noel's example: an S-meter
scaled S5 to S9+60 rather than full scale, so the pitch range buys resolution
where it matters at the cost of range you do not care about. Same shape as the
coarse/fine SWR pair.

- **Two meters MAY share a source** (settled). The coarse/fine case is real:
  while tuning an antenna the interesting SWR band is tiny, while the band
  meaning "stop transmitting" is huge, and one mapping cannot serve both.
- **The range must be expressed in the source's own units** — S-units, watts, a
  ratio, degrees. A range stored as bare numbers cannot be validated, cannot be
  announced sensibly, and cannot tell you what "5 to 9" means.
- **Default: all meters off, at full range.** Nothing sounds until asked; nothing
  is hidden by a narrowed scale the operator did not choose.

**Three meter categories, and the data model must allow all three** — this is the
fourth instance this week of a value needing to be a first-class object:

1. **Radio-reported** — whatever the meter list returns.
2. **PC-derived** — mic LUFS and other measurements we compute locally.
3. **Frequency-domain** — a probe at a chosen frequency or span (below).

If D2 assumes every meter has a radio source, categories two and three need
surgery to add later.

### Priority watch — a distinct feature, not a meter variant

Noel, 2026-08-16: *"a priority watcher on say the maritime net frequency for
signal strength. Set that watcher to ping you when stuff happens on a frequency,
and optionally zoom you to it when things happen, similar to priority mode on
scanners."*

**The scanner framing is the right one to design from** — every ham already knows
what priority scan does, so the feature needs almost no explaining.

**The resource is the SPAN, not the watcher — settled 2026-08-16.**

Two corrections to earlier reasoning on this page. **DAX IQ is not 16 channels**
— `ModelInfo.cs` declares `MaxDaxIqChannels` as **2 on smaller models and 4 on
larger ones**. (DAX *audio* channels are plentiful; DAX *IQ* is scarce — easy to
conflate.) And **the panadapter limit is not artificial or hardcoded**:
`MaxPanadapters` arrives in the discovery packet as `max_panadapters`, so the
radio declares its own and we simply read it.

So IQ is *more* constrained than panadapters, not less. But Noel's underlying
point survives and sharpens the design:

**Spectrum is going to be flowing anyway for recording. A watcher inside an
existing span therefore costs nothing — it is reading bins we already have. The
scarce resource is consumed per SPAN, not per watcher.**

- Watch three frequencies inside the same 200 kHz: no additional cost.
- Watch one frequency outside every existing span: claims a panadapter or an IQ
  channel.

**Actionable consequence: the UI must say when a new watch frequency falls
outside what is already streaming**, because that is the moment it starts costing
a scarce resource. Silently claiming the last panadapter is the kind of thing an
operator discovers much later, in a worse mood.

**RESEARCHED 2026-08-16 — see `docs/planning/active/priority-watch-research.md`.**
The span hypothesis above is now **verified in code, not assumed**:
`Panadapter.DataReady` and `Waterfall.DataReady` are multicast events handing
every subscriber the whole frame, so N watchers inside one span cost what one
costs. Three findings change the design:

- **DAX IQ is NOT the escape hatch — it rides ON a panadapter.** `Radio.cs:5511`
  carries the vendor's own comment `stream create type=dax_iq pan=<panadapter>
  rate=<rate>`, and `DAXIQStream` holds a `Panadapter Pan` populated from it. So
  IQ costs a panadapter **plus** one of the 2–4 IQ channels, at 1.5 Mbit/s
  minimum. **Independently verified. Take IQ off the options list.**
- **The only span the app manages follows the operator's tuning.**
  `PanAdapterManager` re-centres `ActiveSlice.Panadapter` on QSY, so **a borrowed
  span silently stops watching the moment you tune away** — precisely the failure
  the feature exists to prevent. A watch must own a span until some other feature
  provides a stable one.
- **Probable standing bandwidth leak, worth fixing regardless of this feature:**
  `FlexBase.cs:6769` sets `pan.Width = 5000` on every panadapter we own, so extra
  slices' panadapters stream large frames to zero subscribers. **Independently
  verified.** Candidate for Track B.

**One honest unknown**, flagged in the research rather than papered over: what the
`ushort` FFT bin values actually mean. The design consequence that survives
either answer, and should be adopted regardless — **express thresholds in dB
above a tracked noise floor, never in absolute dBm**, because `RFGain`, `RXAnt`
and the wideband noise blanker all move the absolute level, and band noise moves
tens of dB between morning and a thunderstorm.

Also surfaced: `docs/help/md/panadapter-visibility.md` tells users the app is
"still receiving all of the IQ data." It is not — it receives radio-computed FFT
frames and waterfall tiles, and never opens an IQ stream. Description drift, and
a candidate for whichever track owns help corrections.

**Three things that separate a good watcher from an irritating one**, all long
since learned by scanner designers:

- **A threshold with hysteresis**, or it chatters endlessly at the boundary.
- **A dwell time**, so a single noise spike does not alert.
- **Auto-QSY that remembers where you were.** A radio that moves itself and
  cannot go back is worse than one that merely tells you.

**Auto-QSY is opt-in and announced.** It moves the operator's radio — exactly the
class of action that must never be a surprise.

**This is also the incremental path to the waterfall:** a watch-this-frequency
meter is a single-bin waterfall. Same voices, same pitch-carries-value grammar,
narrowed from a sweep to a point. Useful on its own terms before the waterfall
exists.

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

## Track H — the earcon audit (added 2026-08-16)

Noel, from the bench: he got a ding on toggling a checkbox in the Audio Workshop
and liked it — *"though it needs to be a high tone for on and a lower ding for
off. This should be everywhere."* And: *"we probably need a track that goes
through and finds / audits checkboxes."*

**One ding for both states carries no information.** The pair is the point:
rising for on, falling for off. Learn it once and the words stop being necessary,
which is faster than speech and survives speech being busy or cut off.

**Requirements, in order of how easily they get missed:**

- **The two tones must be obvious siblings** — same timbre, a clear interval
  apart — so they read as one vocabulary rather than two unrelated noises.
- **Quick and non-overlapping.** Noel's phrase is *"quick, poignant, gets out of
  the way,"* and the reason is rapid toggling: in Settings or the Workshop you
  check and uncheck several things in a row. **Five toggles must not stack into a
  chord or queue** — earcons cut each other off, or are short enough that they
  cannot overlap. This is what makes the difference between a pleasure and a
  thing people switch off on day two.
- **It leads the speech, it does not compete with it.** NVDA already announces
  "checked". The ding's job is to be *faster*; it fires immediately and the
  speech follows as the authoritative version.
- **One implementation, called from every toggle.** "This should be everywhere"
  is exactly the requirement that rots into six slightly different dings if each
  site rolls its own.
- **Audit every checkbox in the app** and wire it — that is the track's bulk.

**This closes #39 and #43 rather than adding a third sound system.** The help
already documents per-category earcon controls that were never built. H is
"build what the help already promises, and make every toggle use it."

**Toggle sounds are their own VERBOSITY CATEGORY, not a global on/off** (Noel,
2026-08-16): *"We can always add a verbosity setting to disable this category of
tones like we do with other tones."* So H needs no new settings machinery — it
needs to *be* one of the categories that machinery was designed for
(`memory/project_verbosity_architecture_proposal.md`).

The payoff is granularity someone will actually use: keep meter tones and the
connect earcon while silencing toggle dings during a long settings session, then
turn them back on. A global mute cannot express that — and a global mute is what
people reach for when the alternative is suffering.

**It is also a test of whether the earcon framework is real.** If registering
"toggle sounds" as a category is one line, the framework works. If it needs
plumbing, the framework was notional, and finding that out at the first category
is much cheaper than at the fourth.

**Current state to fix, per Noel at the bench:** the ding exists in places but is
**not applied system-wide** — Settings and the Audio Workshop are inconsistent —
and where it does fire it is one sound for both states, so it carries no
information at all.

**Dependency to settle: H needs tones, and D2 is building a tone engine.** Either
H waits and defines its ding pair as two voices in D2's model, or we ship two
synthesis paths that drift apart — the same mistake caught four times this week.
**Recommendation: H's earcons are voices in D2's vocabulary.** Costs H an
immediate start; saves a second sound system.

**Also found at the bench, same family:** toggling the test tone while PC audio
is OFF announces the raw string `testtone.armed` — a resource key leaking into
speech, with one branch holding a real string and the other the key. Fix the
wording at the same time: on/off or armed/disarmed, consistently, not mixed.

## Track F — presets and config truth (UNPARKED 2026-08-16)

Mic profiles bound to the device, corrupt presets silently becoming defaults,
presets carrying no schema version, presets not recording which input they were
tuned for, and the `audioConfig.xml` two-directory migration.

**Parked in an earlier draft, unparked when the ownership rule was narrowed** —
the sole reason for parking it was a file collision with D over
`AudioOutputConfig.cs`, and that rule no longer forbids sharing a file. Noel
caught that the document still said parked after the decision had changed.

**One item is stronger than "papercut" suggests: #49, a corrupt preset file
silently becoming the three defaults.** That is settings loss with no
notification — the operator's tuning disappears and nothing says so. Worth
treating as a real defect rather than polish.

**Coordination owed with D2**, and this is the one place the narrowed rule still
bites: F changes the config model *structurally* (a schema version, what a preset
contains, where the file lives), and D2 is adding the meter and voice model,
possibly to the same file. Additive edits to one file are fine; two tracks
restructuring one model is not. **Settle who owns the config model's shape before
both start** — most likely D2, with F reporting what it needs.

---

## File ownership — structural change is owned, additive change is shared

**Narrowed 2026-08-16.** An earlier draft said no two tracks may own the same
file. Noel pushed back: worktrees exist, so why not fix clashes at merge? The
answer is that worktrees solve the *mechanical* clash and not the *semantic* one
— the 2026-08-12 case where one track was told to reuse `MicAudioVerdict` while
another moved it to a new class, both merged with **zero textual conflict**, and
the build failed. Git cannot see that collision.

But strict ownership was over-tight, and it is what wrongly parked F and made the
papercuts awkward. So the rule is now narrower:

- **Single ownership applies to STRUCTURAL change** — moving a symbol, changing a
  signature, restructuring a shared model. That is where semantic collisions
  live.
- **Shared files are fine for ADDITIVE, LOCAL change** — a wording fix, a trace
  line, a papercut in a method nobody else is touching. Two tracks adding
  separate lines to `FlexBase.cs` is a trivial merge.
- **The test is one question:** *could another track be referencing what I am
  about to change?* If no, share freely.

Primary areas, for orientation rather than exclusion:

- **A** — `RigSelectorDialog.xaml.cs`, `KnownRadioRoster.cs`,
  `RadioConnectionCache.cs`, plus FlexBase's client-identity region
- **B** — FlexBase's meter, power, GPS and remote-audio-loop regions
- **C** — per-radio settings, Settings→Network, `TXControlsDialog.xaml.cs`
- **D1** — the Live Meters region of `AudioWorkshopDialog.xaml.cs`
- **D2** — `ContinuousToneSampleProvider.cs`, `MeterToneEngine.cs`, and the
  meter/voice model
- **D3** — new management UI, plus `KeyCommands.cs` for the leader layer
- **E** — `JJPortaudio/Audio.cs`, `Devices.cs`, `AudioDevicesDialog.xaml.cs`
- **F** — preset and config code; **coordinate the config model with D2**
- **G** — new About page and its data provider
- **H** — the earcon/verbosity category plus every checkbox site

**Standing instruction to every track:** *"reuse the symbols you find; if you
conclude one should move or change signature, report it rather than doing it."*
And **build after every merge** — a clean `git merge` is not evidence the result
compiles.

**D2 publishes the voice type before its synthesis is finished**, so D3 and H can
build against a known shape rather than waiting or guessing.

### Papercuts have owners, not a someday pile

**Each track carries the papercuts in files it already touches** — a wording fix
in the device dialog is E's, a settings-dialog papercut is C's. No conflict, and
the agent already in that code is best placed.

**Orphan papercuts** — help pages, string leaks, the installer file list, the
tracing dialog — go to the small-fixes sweep, which collides with nobody.

**And the rule that makes it stick: a track is not done until its papercuts are
done.** Small items get deferred because they are boring, not hard. Folding them
into the definition of done removes the choice — otherwise each one loses a
priority argument against real work, every time, forever.

---

## Run order

**Start together:** B, C, D1, D2, E, F, G. **D2 early on purpose** — it gates D3
and H, and it answers the question that can actually fail.

**F starts with them** now that it is unparked, with the config-model
coordination noted in its section settled first.

**H waits on D2's voice type**, so its ding pair is defined in the same
vocabulary rather than a second synthesis path.

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

## Sequencing — the transverter arc follows this tranche

Noel has the XLR extension as of 2026-08-16, so the transverter bench session
(#27) and its arc are unblocked on hardware. **Deliberately sequenced after this
track set**, with one exception worth watching: if transverter work turns out to
*depend* on something in here, that argues for pulling it forward rather than
duplicating.

The obvious candidate is the **forward-power fix in Track B**. Transverter
operation lives at sub-watt drive, which is precisely the range currently
displayed as zero — so a transverter session run before that fix would be reading
an instrument known to lie in exactly the band being used. Land B first, then
bench.

## Open questions — all four answered 2026-08-16

**1. May two meters share a source? — YES.** The coarse/fine case is real. See
the meter model section above.

**2. One list or two? — ONE**, with the eight as a recommended starter set rather
than undeletable built-ins. See the meter model section above.

**3. Where does ACC/BAL enumeration execute? — TRACK E.** Noel: *"I don't care
really, as long as it gets coded/audited."* **So it is written down as E's, on
E's test list, with a named owner** — an item that could sit in either track is
exactly the one that ends up in neither.

**4. Does the live tone tweak fork or replace? — NEITHER UP FRONT. Live preview,
decide on exit.**

Noel: *"I can see forking, but what if you want to delete the original and keep
the fork? When we're playing, why not just specify it — either create a copy and
edit, or replace?"* Right instinct, and there is a version with no friction at
all:

While in tone tweak the change is **audible immediately but uncommitted**. On
leaving, one prompt: **keep as a copy, replace the original, or discard.**

- Handles the delete-the-original case — that is simply "replace."
- Avoids putting the decision *before* you know whether you like the result, and
  at the worst possible moment, mid-adjustment on the air.
- **Adds the option neither fork-nor-replace had: discard** — which is the most
  likely outcome of any given fiddle, and under either of the other two you would
  have been left cleaning up a variant you never wanted.

---

## CLOSED — the meter list is now observable

`Radio.GetMeterListReply` parsed the reply and traced nothing, so the inventory
D2 and D3 are designed against could not be seen at all. **Fixed 2026-08-16**:
`FlexBase.traceMeterInventory` logs every meter's index, name, description,
source, range and units, and re-logs whenever the count changes.

The answer it produced — **102 meters, against our hardcoded eight** — is in the
Track B bench results above, and is the strongest single argument for the meter
model this plan adopts.

**Merge that diagnostic properly rather than leaving it as scaffolding.** It
reaches FlexLib's private meter list by reflection, which is right for a
diagnostic and wrong for a picker. Track D needs a real accessor, and that
probably means a documented FlexLib patch recorded in `MIGRATION.md` — some
things genuinely cannot be wrapped.
