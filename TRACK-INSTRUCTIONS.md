# Track D2 — The voice engine and the meter model

**Worktree:** `C:\dev\jjflex-d2` · **Branch:** `bsr/track-d2` · **Model:** Fable

**Read first:** `docs/planning/active/barefoot-splatter-ragchew.md`, the whole of
"Track D", especially "THE FINDING THAT REFRAMES THIS TRACK" and "Decided
2026-08-16 — the meter model". Also
`docs/planning/active/kerchunk-sidetone-pileup.md` for the sonification grammar.

## Why this track is first and why it can fail

**It gates D3, H, and eventually the waterfall.** And it answers a question
nobody has answered: *are five voices still distinguishable when three play at
once under speech?* That is empirical, and finding out late is expensive.

## THE HARD REQUIREMENT

**Voices are DATA, not an enum.**

A voice is a small parameter set — partial amplitudes, modulation rate and depth,
attack, noise character. **If this ships as `enum WaveformType` with a switch
behind it, the sound sketchpad, the sharing packs and the waterfall's reuse all
become rewrites.** This is the standing rule of the tranche, and D2 is where it
binds.

**Voices are first-class NAMED objects**, not fields inside a meter slot. Meters
*reference* a voice; waterfall categories will later reference the same voices. A
voice defined inside a meter either cannot be reused or gets duplicated, and then
the two drift and the operator learns two vocabularies for one language.

**Publish the voice type EARLY**, before the synthesis is finished, so D3 and H
can build against a known shape instead of waiting or guessing.

## The voice palette

From Noel: sine, square, triangle, phone-ring alternation, the rolling-R trill,
raspy, filtered noise, and a 500 ms tone alternating over an interval.

Added, because they buy far more separation per unit of effort than waveform
swaps: **additive harmonic voices** (hollow, reedy, bell, organ), **pulse width**
(10% duty reads thin and nasal, 50% full and hollow, from one oscillator), and
**attack character** (pluck versus swell, which maps naturally onto "this meter
is jumping" versus "this one is steady"). Filtered noise has two axes worth
exposing separately — bandwidth and centre frequency.

## The governing grammar — do not violate it

**Timbre identifies the meter. Pitch carries its value. Pan enhances but is never
load-bearing.**

Pitch is already spoken for: `PitchLow`/`PitchHigh` map the value, so the tone
moves constantly in real use and cannot also be what tells you *which* meter you
are hearing. **Modulation is a second identity axis and is perceptually
orthogonal to pitch** — a 6 Hz tremolo is recognisably the same on a low tone and
a high one.

Pan must never be the only thing distinguishing two meters: mono listeners and
operators with asymmetric hearing loss lose it entirely. Patrick is a tester on
exactly that axis.

**Honest ceiling:** people reliably tell apart roughly five to seven timbre
families and a similar number of modulation rates. **Design an alphabet, not a
continuum.**

## The meter model

**A meter is a SOURCE plus a RANGE plus a VOICE.**

- **Two meters MAY share a source** (settled with Noel). Coarse and fine SWR is
  the real case: the band that matters while tuning an antenna is tiny, the band
  meaning "stop transmitting" is huge, and one mapping cannot serve both.
- **The range is expressed in the SOURCE'S OWN UNITS** — S-units, watts, a ratio,
  degrees. A range stored as bare numbers cannot be validated or announced
  sensibly. **The radio supplies units and range**; see below.
- **Default: all meters off, at full range.**

**Three meter categories, and the model must allow all three:**

1. **Radio-reported** — whatever the meter list returns.
2. **PC-derived** — mic LUFS and anything we compute locally.
3. **Frequency-domain** — a probe at a chosen frequency or span (priority watch,
   next tranche). **If you assume every meter has a radio source, categories two
   and three need surgery to add later.**

**Derived meters matter more than they look:** a stage delta (stage A minus
stage B) *is* a derived meter, and it is what makes the signal-chain analysis
possible. Leave room for it.

## What the radio actually gives you — measured, not assumed

A FLEX-8600 reports **102 meters** (37 distinct names, four source types), each
carrying **index, name, description, source and source-index, low/high range and
a real units type** (Dbfs, Volts, Amps, Dbm, SWR, DegreesC, None).

**So range-and-units are free — the radio states them.** Do not invent a table.

`FlexBase.traceMeterInventory` logs the whole inventory; run a Debug build
against a radio and read the trace if you want the live list. It reaches
FlexLib's private list by **reflection** — fine for a diagnostic, **not** the
basis for a picker. A real accessor is likely a documented FlexLib patch; Track B
owns reporting that.

## Also yours: the live tweak model

Track D3 will add live tone tweaking. **Decide the data model now:** a live tweak
creates a **per-meter override** over a referenced voice, with an explicit "save
as a new voice" action — never an in-place edit of the shared voice, or a
two-second adjustment on the air silently rewrites a vocabulary other features
share. Live preview, uncommitted; **keep as copy / replace / discard on exit.**

## Rules

- **Reuse the symbols you find. If you conclude one should move or change
  signature, REPORT it rather than doing it.**
- **`AudioOutputConfig.cs` is shared with Track F.** F restructures the preset and
  config model; you add the meter and voice model. **Additive edits are fine; if
  you conclude the config model's SHAPE must change, report it — do not
  restructure unilaterally.**
- Build: `dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal`
- Commit style: `Track D2: <description>`.
- **Do not merge, do not push to main, do not touch other worktrees.**

## Done means

Builds clean. A voice is a named, serialisable parameter set — not an enum. The
voice type is published early and its shape is documented for D3 and H. The meter
model expresses source-plus-range-plus-voice, allows two meters per source,
carries units, and leaves room for derived and frequency-domain meters. **And you
have reported an honest opinion on whether the voices are actually
distinguishable with several playing at once** — that question is the reason this
track went first.
