# Sprint 32 Track E — The audio bench

**Worktree:** `C:\dev\jjflex-32e` · **Branch:** `sprint32/track-e`
**Full design:** `docs/planning/active/elmer-meter-pileup.md`, section "Track E".
**Read that first.** It carries the reasoning; this file carries the contract.

## Order matters: build the instrument before the work

E1 gives one synthesis vocabulary. E2 makes every sound reachable. E3 and E4 make
them auditionable against real band noise. Only then are E6 and E7 judgeable at
all. **Do not start with the tuning work** — you cannot evaluate a candidate tone
without a bench to play it on.

## ONE BLOCKING DEPENDENCY, and it is narrow

Track A is splitting `AudioWorkshopDialog.xaml.cs` (4,866 lines, one file) into
per-tab partial files. **Do not touch `AudioWorkshopDialog` until the orchestrator
tells you A4 has landed.** Editing it before then guarantees a merge conflict.

Nothing else of yours is blocked. **Start with E1 and E2** — `EarconPlayer` and the
registry — which do not touch that dialog at all. By the time you need the
Workshop, A4 will be in.

## E1. #112 — one synthesis vocabulary

Three additive synthesisers exist in `JJFlexWpf` and do not know about each other:

1. **`VoicedToneSampleProvider` + `MeterVoice` + `MeterVoiceLibrary`** — the real
   engine. Fifteen named voices (Pure, Hollow, Reedy, Organ, Bell, Trill, Raspy,
   Thin, Square, Breath, Ring, Two-Tone, Swell, Pulsing, Urgent), arbitrary
   `Partials[]`, Brightness, Inharmonicity, ADSR, Tremolo, Vibrato, Gating,
   tracked band noise, equal-power normalisation. XML-persisted. Already
   documented as intended for reuse. Renders to `MeterMixer`.
2. **`DecayingGavelSynthesizer`** (EarconPlayer internal) — hand-rolled, unwired
   since 2026-04-21, kept as a reference nobody read.
3. **`PlayAdditiveTone`** (added 2026-08-19 for the warning alarm) — crudest:
   fundamental plus integer partials, symmetric linear fades, no envelope, no
   modulation, no noise. Renders to `AlertMixer`.

**Make the alert path render through `VoicedToneSampleProvider`** so there is one
vocabulary, one place to author a tone, one set of parameters to learn. Alert
earcons then inherit ADSR, inharmonicity and tracked noise for free.

Noel asked specifically for decay: *"for some tones I'd also consider adding more
of a fade out (decay)... you might use it for a button press."*

Note the two mixers are separate on purpose (`AlertMixer` vs `MeterMixer`).
Understand why before collapsing them; if you conclude they should merge, **report
it, do not do it.**

## E2. #113 — an earcon registry

`EarconPlayer` exposes 45 no-argument public methods. The Earcon Explorer reaches
**18**. Unreachable today: the whole connect series including `ConnectSuccessTone`
(the app's most recognisable sound, and you cannot play it on demand), all four
JJ-key leader tones, tune and ATU, mute, dialog open/close, expand/collapse.

**Drive the explorer from a registry** — an attribute, or a static table
`EarconPlayer` owns — so a new earcon appears automatically and adding a sound
never again requires remembering to edit a dialog.

Sections must mirror the **six `EarconCategory` values** so the explorer and the
Settings on/off switches speak one vocabulary. Today "Meter Tones" heads a group
of alert beeps that are not meter tones.

Continuous earcons (ATU progress: `StartATUProgressEarcon` /
`StopATUProgressEarcon`) need a Start/Stop pair, not a fire-and-forget Play.

## E3. #119 — the explorer becomes a live bench

Start and stop, pan, volume, play a series. The judging environment is real band
noise; Noel can produce plenty at S2 with no antenna.

## E4. #120 — extend the Earcon Scratchpad

**It already exists** (`EarconScratchpadDialog`) and is already the most
interactive audio surface in the app — frequency, end-frequency, duration, volume,
pan, tone/sweep/slide. Extend it: sustained tone, voice selection, scale walk,
harmonic sweep.

**UserVoices is IMPORT-ONLY.** Noel's ruling: *"We'd probably want to add it in
code, I'm not sure how we'd 'author' a tone in the actual interface, that might be
too complex for a radio application."* Import yes; in-app authoring no.

## E5. #128 — the toggle-tone sweep

Every operator-facing toggle plays the on/off tone **whichever way it is reached**,
application-wide: on gives a higher tone, off a lower one. PC Audio on/off plays
nothing at all today, which is what surfaced this.

## E6. #118 — differentiate the warning family

`Beep(int frequencyHz = 800, int durationMs = 150)` and `Warning1Beep()` are
byte-identical, and the whole PTT warning family is one sine getting higher. Fix
once E1 gives you the vocabulary.

## E7. #114 and the tier half of #115

Direction reads fine — Noel: *"I can definitely tell rising from falling on feature
on and feature off, that's never been an issue."* They are simply bland beside the
warning alarm. Separately, the modern earcon tier sits at 0.2–0.3 volume against
the legacy tier's 0.5–0.7, a 6 dB gap with no reason behind it. **Normalise the
tiers.**

## EXPLICITLY NOT YOURS

**#115's camouflage problem and #116's ducking.** Sounds under ~50 ms are clicks by
physics and spectrally indistinguishable from QRN; raising gain just makes a louder
static crash. That needs the tonal redesign your track enables, judged on the bench
your track builds — it is sequenced AFTER you. Ducking is separate plumbing in a
different audio stack (`PostDecodeProcessor`, PortAudio side — earcons are NAudio)
and only helps one listening topology.

## You own these files

`EarconPlayer`, the earcon registry, `EarconScratchpadDialog`, and
`AudioWorkshopDialog.Earcons.cs` once A4 creates it. **You do not own
`AudioWorkshopDialog`'s navigation shell — Track G does.**

## Rules that apply to every track this sprint

- **Reuse the symbols you are told to reuse. If you conclude one should MOVE or
  CHANGE SIGNATURE, report it — do not do it.** A clean `git merge` with zero
  textual conflict still broke the build in Sprint 30 for exactly this reason.
- **NO tables, diagrams or ASCII art** in anything you write. Prose or bullets.
  The primary user is blind and uses NVDA.
- **Verify builds by the `N Error(s)` summary line**, never by grepping for the
  word "error" — that matches warning prose.
- Commit per logical chunk with `Sprint 32 Track E: <description>`.
- Do not merge anything into your branch. The orchestrator runs the merge train.

## Build

```
dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal
```

Close any running JJFlexRadio first — `Radios.dll` locks.

## Definition of done

One synthesis vocabulary in use by the alert path; the explorer driven by a
registry with all real sounds reachable and sections matching the six categories;
bench controls working; scratchpad extended; clean x64 build verified by the
error-count line. **Report which sounds you could not reach and why**, if any.
