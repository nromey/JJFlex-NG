# Transverter completion plan

**Date:** 2026-08-11. **Branch:** `design/transverter-plan` (worktree `C:\dev\jjflex-xvtr`).
**Supersedes nothing; completes** `docs/planning/vision/moonbounce-mixer-handshake.md` (the
ratified design) by turning its open questions into an executable radio-session plan and a
phased build. All file and line citations below were re-verified against the vendored FlexLib
in this worktree (v4.2.20) on 2026-08-11 — none are taken on trust from earlier docs.

**Why this document closes a chapter.** Transverter support is the last new feature before
the deliberate freeze. Nothing here is deferred to "figure it out later": every unknown is
either resolved from source in section 2, scheduled as a numbered radio-seat experiment in
section 4, assigned to Don in section 7, or listed as a question only Noel can answer in
section 9. The build plan in section 6 is complete enough to start from the moment the
experiments report.

**The scarce resource is radio time.** The heart of this document is section 4: an ordered
list of experiments a session can be executed from, split by what each needs. Fifteen of the
seventeen run on the bench 8600 with no transverter, no antenna, and no transmission above
milliwatt level into an unconnected port. Two need a real transverter and belong to Don.

---

## 1. What we know, verified against the source

### 1a. The radio's transverter model, complete

`FlexLib_API/FlexLib/Xvtr.cs` (377 lines, the whole class):

- The status keys the client parses (`StatusUpdate`, Xvtr.cs:229-375): `if_freq` (247),
  `lo_error` (262), `max_power` (277), `name` (292), `order` (299), `rf_freq` (314),
  `rx_gain` (329), `rx_only` (344), `is_valid` (359). **Re-confirmed: no port field, no
  width field, anywhere in the class.** The design doc's central finding stands.
- **But this list is the CLIENT's vocabulary, not necessarily the radio's.** The wire
  provably carries at least one key the class never parses: `Radio.ParseStatus` removes an
  Xvtr when the raw status contains `in_use=0` (Radio.cs:3932, 3941-3945) — and `in_use`
  appears nowhere in `Xvtr.StatusUpdate`, which silently drops unknown keys (no default
  case). So "the complete status set is these nine fields" was only ever verified against
  the parser. Whether the radio sends more — including anything width- or extent-shaped —
  is experiment X1, and it costs one raw capture.
- A hint that the radio computes extents internally even though it never reports one: the
  doc comment on `Valid` says *"A high limit less than low limit is one example of an
  invalid XVTR definition"* (Xvtr.cs:211-214). High and low **limits** exist in the
  firmware's model; they are derived, not settable, and not streamed to us.
- `Name` truncates to 4 characters client-side before sending (Xvtr.cs:75-79). Our friendly
  profile names are ours alone.
- `MaxPower` is clamped **client-side** in the setter (Xvtr.cs:169-208): floor always
  −10.0 dBm; ceiling +15.0 dBm when IF is below 80 MHz, tightened to +10.0 on
  6400/6400M/6600/6600M, and +8.0 dBm when IF is at or above 80 MHz. Whether the *radio*
  enforces the same ceiling is untested; more importantly, the 2026-08-09 bench session
  found `max_power` **inert for actual drive** (swept −10 to +15 with no level change, and
  the SmartSDR decompile shows a bare pass-through). What it actually governs, if anything,
  is experiment X12.
- Vendor bug: the `order` status case updates `_order` but raises `PropertyChanged` for
  **"MaxPower"**, not "Order" (Xvtr.cs:309-310). Any UI we bind to `Order` will never hear
  radio-side changes until we patch this (one string) — note for `MIGRATION.md` alongside
  the rfgain-space patch below.
- Lifecycle: `xvtr create` via `Radio.RequestXvtr()` (Radio.cs:5515-5518) — fire and
  forget; the new band arrives as a status and surfaces through `XvtrAdded`. Removal is
  `xvtr remove <index>` (Xvtr.cs:35-41) or radio-initiated via `in_use=0`. **Trap:**
  `Radio.CreateXvtr()` (Radio.cs:13685-13688) merely news up an unregistered object with
  index −1; setting any property on it emits `xvtr set -1 ...`. Never use it; `RequestXvtr`
  then catch `XvtrAdded` is the real creation path.
- The radio pushes all definitions at connect (`sub xvtr all`, Radio.cs:2353);
  `Radio._xvtrs` is private (Radio.cs:326) with only `FindXvtrByIndex` public
  (Radio.cs:13695-13699), which is why FlexBase mirrors the list via events
  (FlexBase.cs:7396-7414). The list is cleared on disconnect (Radio.cs:2531-2537).

### 1b. Three code artifacts that bear directly on the fork

The single biggest unknown is whether the radio maps a tuned frequency to a transverter
band by itself. The source cannot settle it, but it leans hard in one direction:

- **The panadapter reports an `xvtr` status key.** `Panadapter.XVTR` (Panadapter.cs:665-677)
  is populated from the radio's `xvtr=` status (parse at Panadapter.cs:1301-1306) and its
  setter sends nothing back — it is a read-only radio verdict, per pan. The waterfall
  deliberately ignores the same key (Waterfall.cs:1127). A radio that did not resolve
  frequency to transverter would have nothing to report here.
- **The interlock has a transverter-specific refusal.** `InterlockReason.XVTR_RX_ONLY`
  (Radio.cs:102, parsed at Radio.cs:8027). The firmware refuses to transmit *because the
  relevant transverter definition is receive-only* — which requires the firmware to know
  which definition is relevant. That is frequency-to-band mapping, radio-side, enforced.
- **`order` is validated radio-side** (StatusUpdate rejects values above 16, Xvtr.cs:303)
  and exists at all — a precedence field only makes sense if the radio arbitrates between
  overlapping candidates itself.

Also relevant: `Panadapter.Band` is settable (`display pan set 0x… band=`,
Panadapter.cs:326-334; parse at 907-916; `Waterfall.Band` similar at Waterfall.cs:267-277).
This is how SmartSDR's band buttons work, and transverter bands presumably have band
identifiers reachable this way — the explicit-selection path exists in the protocol whether
or not auto-selection does.

### 1c. Per-band transmit settings — a possible enforcement gift

`TxBandSettings` objects (TxBandSettings.cs:23-29) arrive per `band_id` on `transmit band …`
and `interlock band …` status lines (routed at Radio.cs:8121-8125 and 10218-10222, parsed at
5262-5339). Each carries `rfpower`, `tunepower`, `hwalc_enabled`, and — the interesting one —
`inhibit` (`IsPttInhibit`, TxBandSettings.cs:88-99, wire command
`transmit bandset <id> inhibit=1`). If transverter bands get band ids (experiment X9), then:

- the radio itself remembers **per-band RF power** — tuning into the 2 m band would recall
  the drive you last used there, radio-side, which is most of the drive-safety story; and
- `inhibit` is a **radio-enforced transmit lock per band**, which is a stronger first-
  transmit gate than anything we can build app-side.

Neither is usable until X9 and X10 confirm the semantics.

### 1d. Tuning and antennas

- Tuning is `slice tune <index> <MHz> autopan=0` with **no client-side clamping**
  (Slice.cs:354-395); the radio replies with the frequency it actually accepted
  (`SetFreqReply`, Slice.cs:397-413). So "what happens when you tune to 144.1" is entirely
  the radio's decision, and the reply is our observable.
- Antenna lists are radio-reported per slice: `RXAntList` (Slice.cs:162-177), `TXAntList`
  (Slice.cs:219-234, defaulting to ANT1/ANT2/XVTR only when the radio has not reported).
  Setters at Slice.cs:185-198 (`rxant=`) and 241-265 (`txant=`). Verified live 2026-08-07:
  the 8600 reports XVT A and XVT B on both sides; the 6300 generation has a single "XVTR".
- The slice-level frequency translation applies to receive slices too: the 2026-08-09
  loopback recipe had the ears slice tuned to 144.100 on XVT B and it demodulated the
  signal — so the moonbounce doc's "does a defined band translate the ears-slice as well"
  question is **already answered yes, empirically.**
- Vendor bug, already known, restated because this feature will hit it: `Slice.RFGain`
  builds `"slice set" + _index` with no space (Slice.cs:213) and the radio discards the
  malformed command. In 4.2.20 the property is marked `[Obsolete]` in favour of
  `Panadapter.RFGain` (Slice.cs:204), **and the panadapter version is correctly formed**
  (Panadapter.cs:182-195). Recommendation: use `Panadapter.RFGain` and do not patch the
  vendored slice setter — the vendor already routed around their own bug.

### 1e. What JJFlex already has

- **The QB Track I transverter-power region** (FlexBase.cs:7390-7513): the Xvtr mirror,
  `TXAntennaIsTransverter` (7422), `ActiveXvtr` (7432-7452) — note this is **our
  heuristic** (highest `rf_freq` at or below the slice frequency wins, single-band
  fallback), i.e. exactly the guess that experiment X4 exists to validate or replace —
  plus `XvtrDrivePowerCentiDbm` and the clamp mirror (7460-7511).
- **The loopback arrangement** (FlexBase.cs:9683-9882): snapshot/apply/restore of FDX, TX
  antenna, power, monitor, ears slice; `LoopbackSupported` gating on `DiversityIsAllowed`
  plus an XVT port in the TX list (9712-9714); and the private band probe
  `findAnyValidXvtr()` (9869-9878) that Phase 2 dissolves into profiles.
- **Per-radio config keyed by serial already exists**: `RadioConfig`, XML at
  `{BaseConfigDir}\radios\{radioId}\config.xml` with `LoadForRadio` / `SaveForRadio`
  (RadioConfig.cs:70, 229-248, 364). Transverter profiles are a new section of this file,
  not a new persistence mechanism.
- **The IQ instrument is proven and self-owning.** `tools/rigbench/daxiq_probe.py` connects
  as its own GUI client, owns its own pan, never transmits, and prints per-second dBFS and
  the strongest spectral peak; `demod.py` turns a capture into listenable audio; `fdx.py`
  flips full duplex. Proven results that this plan builds on: a keyed tone produced a
  spectral spike 58 dB above the noise floor through internal coupling, and **the IQ stream
  carries the transmitted signal through the transmit mute at half duplex**
  (software-detune-proven 2026-08-09) — so PC-side demodulation gives ground truth on every
  Flex including 1-SCU radios (`audio-workshop-plan.md` §4e).
- As of 2026-08-11 `main` is on FlexLib 4.2.20, the audio arc is merged, and the Audio
  Check defaults to dummy-load mode.

### 1f. Session-hygiene facts from the bench, carried forward as rules

- The global profile load at connect resets `full_duplex_enabled` to 0 every time — set it
  after connecting, not before.
- `band_persistence_enabled` re-asserted itself to 1 during the 2026-08-09 session;
  unexplained, watch for it (folded into X7).
- `transmit freq` was observed reporting the IF (28.100) early in a session and the RF
  (144.100) later with the same valid band — unexplained; X14 resolves the domain question
  deliberately instead of leaving it a quirk.
- The operator hears a mix of every unmuted slice; every by-ear observation must state what
  else was audible, and every automated arrangement must mute-and-restore.
- "Looks constant" is not a measurement — vary a control and watch the response, or use the
  probe's numbers. Two false conclusions in one week came from eyeballing.

---

## 2. The fork, and the recommended posture

**Recommendation: design on the working hypothesis that the radio maps frequency to
transverter band itself, and we own port binding, drive policy, and speech.** Three
independent code artifacts support it (section 1b): the pan's `xvtr` status, the
`XVTR_RX_ONLY` interlock, and radio-side `order` validation. The firmware's internal
high/low limit concept (Xvtr.cs:211-214) says extents exist even though no width is
reported.

**The contingency, stated once:** if experiment X2 shows the radio does *not* auto-select —
tuning to 144.1 is refused or lands somewhere unexpected until a band is chosen explicitly —
then band entry becomes an explicit act (our profile activation drives
`Panadapter.Band` or retunes within the definition), and the existing `ActiveXvtr`
heuristic (FlexBase.cs:7432) graduates from labeling duty to being the mapper, with its
extent rule corrected by X4's findings.

**Either way, the JJFlex surface is identical**: profiles, port binding, announcements, the
first-transmit confirmation, and drive policy. The fork only decides what *triggers* a
profile becoming active — the radio's own band resolution, or our explicit activation. That
is why the build phases in section 6 do not block on the fork: only one small module (the
activation trigger) has two possible implementations, and X2 picks between them on night
one.

---

## 3. Ordered radio-seat experiments — the heart of this plan

### How to run these

- **Instrument, don't ear.** `tools/rigbench/` is the toolkit: `snapshot.py` for state,
  `raw.py` / `flexwire.py` for wire commands and raw status capture, `daxiq_probe.py` +
  `demod.py` for RF ground truth, `fdx.py` for full duplex. The probe never transmits;
  keying stays with the operator.
- **State discipline.** `xvtr create` mutates radio state that outlives the session. Step
  zero of every session: capture the existing transverter list; step last: remove every
  band the session created, by index, and re-capture to confirm the radio matches the
  entry snapshot. Never touch a band the session did not create.
- **Keying discipline.** All keyed experiments run with TX antenna on an XVT port
  (milliwatt-class output that bypasses the PA) or in the Audio Check's dummy-load mode,
  RF power at the minimum that produces measurable IQ energy. The bench 8600 has no
  antenna on any port; nothing here radiates meaningfully.
- Each experiment below states the unknown, the exact procedure, and what each result
  means. They are ordered so that state built early (the TEST band) is reused, and so the
  fork resolves first.

### Session One — the band model. Needs nothing but the radio; no keying at all.

**X1 — Wire census: what does the radio actually say about a transverter?**

- Unknown: the full key set of an `xvtr` status line. The client parses nine keys and
  silently drops the rest; `in_use` is proof at least one more exists (section 1a). If the
  wire carries an extent, a port hint, or anything else, everything downstream should know
  before we design around its absence.
- Do: with `raw.py` logging all status traffic, connect, and capture the full `sub xvtr
  all` response verbatim. Then `xvtr create` a band and capture every status line it
  generates, before and after setting each field once (name TEST, rf_freq 144.1, if_freq
  28.1 — the proven values).
- Result meanings: keys beyond the known ten (nine parsed plus `in_use`) → document each,
  and re-plan any that touch extent or ports before X4. Nothing new → the design doc's
  model is confirmed at the wire level, not just the parser level, and the no-port finding
  is finally airtight.

**X2 — THE FORK: does tuning into range select the band?**

- Unknown: whether entering 144.100 on a slice activates the TEST transverter definition
  with no explicit band selection.
- Do: with TEST defined and the slice at 28.1 on ANT 1, type 144.100 into the frequency
  field. Observe four things: the tune reply (does the radio accept 144.100 —
  `SetFreqReply` carries the accepted value), the slice's displayed frequency afterward,
  the pan's `xvtr=` status value (log it raw; we do not know if it reports the band's
  name, index, or something else — note the answer, our speech layer needs it), and any
  change in antenna or interlock status.
- Result meanings: accepted, and pan reports the band → **the radio maps; our job is port
  binding and speech.** Section 6 proceeds as written, activation triggered by the radio's
  own resolution. Refused or clamped → explicit selection is required; run X2a immediately:
  set `Panadapter.Band` to candidate values (the xvtr name, then its index, then any
  band_id X9 reveals) and find the value that enters the band. Our activation module drives
  that, and the contingency in section 2 is in force.

**X3 — Control: tuning to transverter frequencies with NO band defined.**

- Unknown: what the radio does with 144.1 when nothing maps it (the "no transverter
  defined" case a user will hit by typo).
- Do: remove TEST (or run this before X2's create). Tune to 144.100. Observe the reply and
  where the slice lands.
- Result meanings: refused with an error reply → we can give that refusal a spoken,
  friendly shape ("144.1 megahertz needs a transverter definition — you don't have one"),
  which becomes a Phase 1 speech item. Accepted → find out what the hardware is actually
  doing (pan center? IF?) before deciding what to announce; either way the announcement is
  ours to design and this tells us its content.

**X4 — Band extent: where does a definition stop?**

- Unknown: with `rf_freq` a single value and no width anywhere, how far above 144.1 the
  band reaches. This decides whether our `ActiveXvtr` heuristic (highest start at-or-below,
  FlexBase.cs:7444-7445) matches the radio's own arbitration.
- Do: with TEST at rf 144.1 / if 28.1, step the slice upward — 144.5, 146.0, 148.0, 150.0,
  160.0 — watching the pan's `xvtr=` at each step until it stops reporting TEST (or the
  tune is refused). Then note the boundary and probe just around it. If X2 went the
  explicit route, do the same by observing which frequencies the band accepts after
  explicit selection.
- Result meanings: a fixed span (say, rf_freq plus some constant) → encode it; extent ends
  where the IF would leave the radio's tunable range → the extent is derived from IF
  coverage, encode that arithmetic; extent runs until the next definition's start → order
  and adjacency govern, and X5 matters more. Whatever the rule, `ActiveXvtr` gets corrected
  to match the radio exactly, and our announcements can say "leaving the 2 meter
  transverter band" at the true edge.

**X5 — `order`: what does precedence actually arbitrate?**

- Unknown: what `order` does when two definitions could claim a frequency.
- Do: define a second band SEC at rf 144.2 / if 28.2 (overlapping TEST's plausible extent).
  Tune to 144.3. Note which band the pan reports. Swap the two bands' `order` values.
  Re-tune 144.3 (leave and return, in case arbitration happens at entry). Note again.
- Result meanings: winner follows `order` → precedence confirmed; our profile UI surfaces
  order only when definitions overlap, and otherwise hides it (it is operator-hostile
  noise when bands are disjoint). Winner ignores `order` → document what does win (lowest
  index? closest rf_freq?) and treat `order` as vestigial; hide it entirely. Remember the
  vendor bug from section 1a if any UI binds to Order.

**X6 — `is_valid`: what does the radio reject?**

- Unknown: which incoherent definitions flip `is_valid` false, so our validation can defer
  to the radio's verdict instead of reimplementing rules we cannot see.
- Do: on a scratch band, observe `is_valid` after creation with no fields set; then after
  setting only rf_freq; then rf below if (144.1 / 200.0); then an IF outside the radio's
  tunable range (if_freq 500.0); then a coherent definition. Capture the raw status each
  time (is_valid may change with keys we don't parse).
- Result meanings: each false case becomes a spoken validation message in the profile
  editor, phrased from what the radio rejected, not from guessed rules. If `is_valid` stays
  true through obvious nonsense, our editor needs its own sanity checks after all — but
  only the ones the radio provably lacks.

**X7 — Persistence: who owns a band across sessions?**

- Unknown: whether definitions survive disconnect, reconnect, and the global profile load —
  i.e. whether our profile system manages durable radio state or must re-create per
  session. Also whether `band_persistence_enabled` interacts (it re-asserted itself once,
  unexplained).
- Do: with TEST defined, disconnect and reconnect (the profile load will run — the same one
  that resets full duplex). Capture `sub xvtr all`. If convenient, also power-cycle the
  radio once and re-check. Note `band_persistence_enabled` before and after.
- Result meanings: bands survive everything → definitions are durable operator state; our
  profiles ADOPT radio bands rather than creating them per session, and the
  never-renumber/never-reuse rule from the design doc is enforced at adoption time. Bands
  vanish on profile load → our profiles must re-assert their bands at connect, and any
  hand-defined band the operator made in SmartSDR is fragile in ways we should warn about.
  Mixed behaviour → document exactly which action clears what; the profile system's
  create/adopt logic keys off this answer.

**X8 — Identity: are indices stable and are they reused?**

- Unknown: what a profile should store to re-find "its" radio band later.
- Do: note TEST's index. Remove it. Create a new band; note the index. Create two more,
  remove the middle one, create another; watch which indices get assigned.
- Result meanings: indices reused aggressively → index is a session handle, never a durable
  key; profiles match bands by rf_freq + name and treat index as a cache. Indices stable
  and monotonic → index is usable as a hint but the rf_freq + name match stays the
  authority (the operator can edit either in SmartSDR behind our back either way).

**X9 — Do transverter bands get TxBandSettings rows?**

- Unknown: whether defining TEST produces a `transmit band <id> ...` status with a new
  band_id (section 1c), giving us radio-side per-band power memory and the `inhibit` flag.
- Do: with raw status logging, create TEST and watch for `transmit band` / `interlock band`
  lines with an unfamiliar band_id. Tune into the band and set RF power to a distinctive
  value; tune out to 20 m, set a different power; tune back in and see whether the radio
  recalls the in-band value.
- Result meanings: rows appear and power is recalled per band → drive policy in Phase 1
  rides the radio's own per-band memory, and our job shrinks to setting it once per profile
  and announcing it. No rows → drive policy is entirely ours: the app stores drive in the
  profile and asserts RF power at band entry (the portable fallback, section 6 Phase 1).

### Session Two — keyed and instrumented. Still needs nothing but the radio; the IQ probe is the signal source and the meter.

Setup common to all of Session Two: TEST band defined, TX antenna XVT A, RF power 1, full
duplex OFF (the half-duplex IQ carry-through is the point), every other slice muted,
`daxiq_probe.py` bound to a pan on the slice frequency with a pre-flight signal check
before any conclusion is recorded (the mandatory lesson from the two near-false-negatives).

**X10 — Per-band `inhibit` as a radio-enforced transmit gate.** Only if X9 found rows.

- Unknown: whether `transmit bandset <id> inhibit=1` on a transverter band actually blocks
  keying, and what the refusal looks like on the wire.
- Do: set inhibit on TEST's band_id. Attempt to key (software MOX, dummy-load discipline).
  Capture the interlock status and reason. Clear inhibit; key again; confirm TX proceeds.
- Result meanings: blocks, with a readable reason → **the first-transmit confirmation gets
  radio-side enforcement**: the gate is armed at band entry and cleared only when the
  operator confirms the port — even a rogue hardware PTT line cannot transmit unconfirmed.
  Phase 1 wires it as belt-and-braces alongside the app-side gate. Doesn't block → app-side
  gate only, which was the baseline plan anyway.

**X11 — `rx_only` enforcement and its speech.**

- Unknown: confirmation that `rx_only=1` produces the `XVTR_RX_ONLY` interlock
  (Radio.cs:8027) in practice, and what the operator-facing status contains.
- Do: set rx_only on TEST, attempt to key, capture the interlock reason; unset, key,
  confirm normal.
- Result meanings: fires as expected → receive-converter profiles get radio-enforced TX
  protection for free, and our speech layer names it honestly ("this transverter is marked
  receive-only"). Doesn't fire → rx_only is advisory and Phase 1 must block TX app-side
  for RX-only profiles.

**X12 — `max_power`: clamp, scale, or label? Measured this time.**

- Unknown: the 2026-08-09 sweep heard no change, but by-ear through a receiver chain. The
  IQ probe turns this into arithmetic: a 25 dB max_power sweep is unmistakable in dBFS if
  it does anything at all.
- Do: keyed tone (the Track C tone generator), RF power fixed at 1, probe watching. Log
  mean dBFS at max_power −10.0, then +5.0, then −10.0 again. Then hold max_power at −10.0
  and step RF power 1 → 5 → 1, logging the same.
- Result meanings: dBFS tracks max_power → it IS a drive control after all (in-band, keyed,
  which the earlier sweep may have missed a precondition for) — per-profile drive maps onto
  it and the ratified dBm/mW slider (`audio-workshop-plan.md` §4a) becomes the profile's
  drive editor. dBFS ignores max_power but tracks RF power → max_power is a label; drive
  policy = RF power policy (X9's mechanism or our app-side assertion), the slider is
  deprioritized to cosmetic, and — important for Connect later — §14.5's granted drive
  ceiling must clip **RF power**, not max_power. Either answer ends the two-session-old
  ambiguity for good.

**X13 — `lo_error`: display arithmetic or hardware shift?**

- Unknown: whether lo_error moves the actual hardware IF (a real drift correction worth a
  profile field) or only the displayed frequency.
- Do: keyed tone at a fixed audio offset, probe watching the spectral peak. Set lo_error
  from 0 to +0.010 MHz. Watch whether the peak moves ~10 kHz in the IQ, whether the slice
  display changes, and what `transmit freq` reports.
- Result meanings: peak moves → lo_error is a hardware correction; the profile carries it
  and the editor explains it ("enter your transverter's measured error"). Only the display
  moves → it is cosmetic bookkeeping; keep the field (the radio has it) but rank it low in
  the editor and say what it does.

**X14 — DAX IQ through a transverter band: the domain question, and the audio-check tie-in.**

- Unknown: with the slice at 144.1 in a defined band (hardware IF 28.1), (a) what center
  frequency the probe's pan reports and accepts — RF or IF; (b) whether the keyed TX still
  rides the IQ through the transmit mute exactly as it does on a plain frequency; (c)
  whether a capture demodulates to clean audio unchanged. Also resolves the 2026-08-09
  `transmit freq` flip-flop (28.1 vs 144.1) deliberately.
- Do: standard Session Two arrangement. Bind the probe; log the pan's reported center and
  the `transmit freq` status. Key the tone; confirm the spectral spike at the expected
  offset. Record ~10 s of voice; run `demod.py`; listen. Repeat once with software detune
  ±1 kHz for the signature pitch shift.
- Result meanings: everything behaves as on a plain frequency → **the entire honest-TX-audio
  IQ tier is band-agnostic**: the Audio Check, live software full duplex (§4f), and the
  rolling replay buffer (§4g) all work while "on 2 meters" with zero transverter-specific
  code beyond knowing which domain (RF or IF) to label frequencies with — and whichever
  domain the pan reported is the one our demod bookkeeping adopts. Anything anomalous
  (wrong center, dead stream, offset arithmetic) → the delta becomes a documented
  translation step in the C# demodulator design before it is built.

**X15 — Meter census on the XVT path.**

- Unknown: `cookie-sked-keydown.md` §14.7 infers there is no directional coupler on the
  XVT output (so no FWDPWR/SWR presence signal) but flags it as strong inference, not
  fact. The meter list is radio-reported, so one enumeration settles it.
- Do: with TX antenna on XVT A, enumerate the meter list; key the tone briefly; log
  FWDPWR, SWR, and anything unfamiliar.
- Result meanings: no TX-side meters respond → confirmed: presence detection is RX-side
  only (Phase 4's design stands). Something responds → a TX-side presence/consistency
  signal exists; fold it into the Phase 4 detector as a second signature.

### Experiments that need a real transverter (Don's, or borrowed — see section 7)

**XD1 — Presence baseline: what does a powered box look like from the port?**

- Unknown: the actual RX-side signatures §14.7 predicts — converter noise floor rise, LO
  leakage birdie — and how many dB of separation a real box gives between powered and
  unpowered.
- Do (guided session at Don's or wherever a box lives): point a slice's RX antenna at the
  transverter port. Capture the noise floor and spectrum (the IQ probe is the right
  instrument; the S-meter is the fallback) with the box powered, then unpowered, then
  disconnected entirely. Three captures, a minute each.
- Result meanings: clean separation → the Phase 4 detector is real; the captures define the
  baseline format the profile stores. Murky separation → the detector ships as
  calibration-only (per-station learned baseline, exactly as §14.7 designed) with honest
  copy about what it can and cannot notice. Either way this is the first data anyone has.

**XD2 — Real conversion end to end.**

- Unknown: that the whole stack — profile, band definition, port binding, rx_gain, drive
  policy — produces a working receive (and, license and setup permitting, transmit) through
  actual hardware. Also validates that `rx_gain` makes the S-meter honest through the
  converter.
- Do: tune a known 2 m signal (local repeater output, beacon, or a handheld across the
  room) through the box with the profile active. Compare S-meter with and without the
  profile's rx_gain. If TX is safe at the station: key at the profile's drive into the box
  with a dummy load on its output, and confirm the far side (a handheld) hears 2 m.
- Result meanings: works → the feature is done and the chapter closes. Anomalies → each one
  is a specific, named bug against a specific layer, because every layer beneath it was
  verified independently on the bench.

---

## 4. How this ties to DAX IQ and hearing your own transmitted RF

The proven technique — a DAX IQ probe on its own pan, PC-side demodulation, TX energy
carried through the transmit mute even at half duplex, a keyed tone standing 58 dB above
the floor — is the measuring instrument for this plan (X12-X14) and a feature of it.

**What the IQ tier proves through a transverter port, stated honestly.** The radio's
hardware only ever sees the IF. The IQ stream taps the DDC, so what we capture and
demodulate is the exciter's IF signal: DSP chain, modulator, exciter, port routing — ground
truth **up to the jack**. The transverter's own mixer, amplifier, and antenna-side RF at
144 MHz exist only past the jack, where the radio cannot hear without help. So:

- **Audio checks on transverter bands need nothing new** (pending X14): the check verifies
  your audio and your exciter exactly as on 20 m, at milliwatt drive, and that claim is
  complete on every Flex including 1-SCU radios.
- **"Is the box actually converting" is a different question**, answerable only by the
  presence detector's RX-side signatures (XD1), an off-air receiver (the KiwiSDR tier), or
  a second receive path through the box (Don's demo ham "maybe used the separate receive
  antenna port" — XD2 territory). The help text must keep these claims distinct; an
  instrument that overclaims is the failure this whole arc exists to correct.
- **Frequency bookkeeping is the one transverter-specific wrinkle**: whether pan centers
  and captures are labeled in RF or IF is X14's answer, and the C# demodulator adopts it.

**Relation to Track F (`honest-tx-audio.md`).** Track F productizes the capture instrument
(permanent full/half-duplex IQ capture) and adds receiver simulation on playback. This plan
**consumes** that instrument and adds nothing parallel: Phase 3's transverter-band audio
check is a caller of Track F's capture; until Track F lands, the rigbench scripts remain
the bench instrument. The presence detector (Phase 4) likewise reuses the capture spine the
DSP track is building around `SpectralSubtractionProvider.StartSampling()` — three
consumers, one capture mechanism, per the research-queue's shared-capture note. This plan
builds no second or third capture UX.

---

## 5. The safety model

Mixer overdrive is the classic transverter killer, and silent failure is the classic
transverter frustration. The protections, in order of strength:

- **Drive never inherits across a band edge.** Entering a transverter profile's band
  asserts that profile's stored drive (through whichever mechanism X9/X12 proves real:
  radio-side per-band power memory, max_power if it turns out to clamp, or app-side RF
  power assertion as the portable fallback). Leaving the band restores what the operator
  had. The kill scenario — HF watts arriving on the transverter port because nobody
  changed the power — becomes structurally impossible, not merely warned about.
- **The first-transmit confirmation names the port**, because the port is precisely what
  software cannot verify and the operator can: "2 meter transverter, transmit antenna
  XVT A — connected?" Once per session by default, with the ratified remember-checkbox.
  If X10 confirms per-band inhibit, the gate is also enforced radio-side until confirmed.
- **Receive-only profiles block transmit** — radio-enforced via `rx_only` if X11 confirms
  the interlock, app-enforced regardless.
- **Tuning is friction-free** (receiving through an unconnected port is harmless and
  ratified as such); every protection lands on the transmit side, where the risk is.
- **The presence detector, when it exists, cross-checks staleness** rather than gating:
  "you marked XVT A as connected, but that port reads like an unpowered transverter." Per
  §14.7 it is pure receive, so it can run at band entry without asking anyone.
- **Announce every arrangement change.** Band entry speaks the profile, the port, and the
  asserted drive. Nothing about this feature is allowed to be discovered by its side
  effects — the failure mode that cost the 2026-08-09 session four hours.

---

## 6. The build, in phases, each independently shippable

**Phase 1 — Profiles, binding, speech, confirmation.** Shippable after Session One alone.

- Profile store: a transverter section in `RadioConfig` (per-serial XML, section 1e). Fields:
  friendly name; the band definition (rf_freq, if_freq, lo_error, rx_gain, rx_only — the
  radio-side mirror, linked by the identity rule X8 chooses); **port** (from the
  radio-reported TX antenna list); drive policy (value + mechanism per X9/X12); the
  "always connected, don't ask" checkbox; a sequencer flag (§14.7's known false-positive,
  designed in now, consumed by Phase 4); free-text notes. The schema also carries what
  Connect's §14.9 grant vocabulary will need (drive ceiling, rx_only, presence baseline
  slot) so that work later consumes rather than migrates — schema decided now, even though
  Connect itself is out of scope here.
- Band ownership: profiles adopt existing radio bands by identity match and create via
  `RequestXvtr` + `XvtrAdded` only when nothing matches; never renumber, never reuse, never
  delete a band the profile system did not create. Create-versus-adopt behaviour finalized
  by X7.
- Activation trigger per the fork (section 2): radio-resolved (announce on the pan's xvtr
  status changing) or explicit (profile activation drives the band and tunes). One module,
  two possible internals, decided by X2.
- Speech: band entry, band exit (at the true extent, per X4), the first-transmit
  confirmation naming the port, and the X3-shaped message for undefined transverter
  frequencies.
- UI placement, recommended (Noel confirms, section 9): profile management lives in
  **Settings, radio-scoped, as a Transverters section** — it is station wiring, i.e.
  configuration, and Settings already owns per-radio scope. Day-to-day operation needs no
  surface at all (tuning is the interface); add a Command Finder registration per profile
  ("switch to 2 meter transverter") and **Ctrl+J, X** in the leader layer to speak the
  active transverter state — the leader layer is the house pattern and is currently
  underused. No new flat global hotkey; the keyboard audit runs per CLAUDE.md.

**Phase 2 — The loopback becomes a profile.** Confirmed still true, reshaped by the
2026-08-09 findings: the synthetic band's job is *tunability*, not drive, and the operator-
chosen listening port (the ratified §4d combo box) is the same concept as the profile's
port field. So: a built-in "Audio Check" profile owns the synthetic band definition, the TX
port, the listening-port choice, and the gain/mute arrangement values — and
`findAnyValidXvtr()` plus `_lbDriveBand` (FlexBase.cs:9768, 9863-9878) dissolve into it.
The audio check then does nothing the operator cannot inspect and override in the same
editor as their real transverters, which was the design doc's promise. Whether the built-in
profile's band persists or is created per session follows X7's answer.

**Phase 3 — Audio-check and IQ integration on transverter bands.** Per X14: the audio
check, the live software-full-duplex monitor, and replay work while in a transverter band,
with frequency labels in the domain X14 established and per-profile drive respected during
checks. This phase is mostly verification plus labels; it consumes Track F's instrument and
adds no capture code.

**Phase 4 — The presence detector.** Built after XD1 provides real baselines: one-time
"learn this port powered" capture stored in the profile (reusing the DSP track's capture
UX), compared at band entry, speaking only on disagreement. Ships dormant — present in the
profile editor as "not yet calibrated" — until a real transverter has been measured.
Phases 1-3 do not depend on it.

**Phase 5 — Docs and audit.** Help page for transverter profiles (what the confirmation is
for, what the software can and cannot verify, the drive-safety story in operator language);
`keyboard-reference.md` for Ctrl+J, X; Command Finder keywords; Feature Availability
entries (single-XVTR-port radios and what that limits); changelog in the house voice; CHM
rebuild; the two vendor-bug patches (`Order` PropertyChanged string, and the note to keep
using `Panadapter.RFGain` rather than the obsolete slice setter) recorded in
`MIGRATION.md`.

---

## 7. What needs Don, and what to ask him

Don is the transverter-knowledgeable tester; his 6300 is remote at Tony's (nothing
LAN-local without Tony), and **whether he currently owns or can reach a physical
transverter is unknown — that is question one.** The ask list, ready to become a
`docs/planning/for-don/` questions doc when Noel says go:

- Do you own a transverter now, or can you borrow one? Which band, which model, and what
  drive does its manual ask for? (Calibrates the profile's drive-ceiling defaults against
  at least one real box.)
- From your experience: what does a safe first-connect procedure look like — attenuator
  in line first? Sequencer? What has actually destroyed transverters in stations you know?
  (Feeds the safety copy in Phase 5 with operator-true language.)
- Your 6300 reports a single "XVTR" port. When you eventually run one: does anything else
  in your station share that jack? (Shapes the single-port UX and the Feature Availability
  text for the 6300/6400/8400 class.)
- If a box materializes: the two guided sessions, XD1 (three one-minute captures, powered /
  unpowered / disconnected — pure receive, nothing transmitted) and XD2 (hear a real 2 m
  signal through it; optionally key into a dummy load). Both fit inside a single evening
  and both are scripted above.

If no box exists in Don's orbit, XD1/XD2 wait for whoever first attaches one — the plan
ships Phases 1-3 regardless (section 8), and Phase 4 waits calibrated-not-guessed.

---

## 8. The hardware reality, answered head-on

**Everything except real conversion and real presence baselines finishes on the bench 8600
alone.** Session One needs no keying at all; Session Two keys milliwatts into an
unconnected port with the IQ probe as both signal check and meter; neither needs an
antenna, a transverter, or a second operator. Phases 1, 2, 3, and 5 ship from those two
sessions. Phase 4 is the only work gated on hardware we may not have, it is deliberately
last, and it ships dormant rather than blocking the freeze. The chapter closes with two
bench sessions, one build arc, and — when a box appears — one guided evening.

---

## 9. Open questions only Noel can answer

- **UI placement:** confirm the Phase 1 recommendation — profile management under Settings
  (radio-scoped Transverters section), operation by tuning plus Command Finder, and
  Ctrl+J, X for spoken transverter status. Or name a different home.
- **Confirmation verbosity at tune time:** band entry currently announces profile, port,
  and drive. Is that the right amount every time, or should repeat entries within a session
  shorten to just the profile name?
- **Ship-before-hardware:** are you comfortable releasing Phases 1-3 verified only against
  the bench radio (no transverter ever attached), with Phase 4 dormant? The alternative is
  holding the whole feature for a hardware session that has no scheduled date.
- **Adoption etiquette:** when we find a radio band you defined by hand (in SmartSDR, or
  years ago), should the profile system offer to adopt it with a question, or adopt it
  automatically and announce it did? Friction-tax says automatic; ownership of
  operator-defined state says ask. Your call on which principle wins here.
- **Hardware path:** do you want to acquire a transverter for the shack (a 2 m box is the
  cheap, testable case), or is Don-or-whoever the plan of record for XD1/XD2?
- **The scheduling of the two bench sessions themselves** — Session One is entirely
  tune-and-observe and could run any evening; Session Two wants the tone generator and the
  probe, about an hour. Say when, and section 3 is the script.
