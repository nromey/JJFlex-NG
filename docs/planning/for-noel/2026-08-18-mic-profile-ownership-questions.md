# Mic profile ownership — the questions only you can answer

> **Superseded as the place to answer.** Answer in `2026-08-19-sprint30-open-questions.md`, which consolidates every open
> Sprint 30 question in one file. This file is kept because it holds the full
> reasoning behind its questions, and the unified file points back here.

Track B, Sprint 30 (2026-08-18). The full design is in
`docs/planning/design/Mic-Profile-Ownership.md`; these are its decision
points, one at a time. Nothing below is implemented — the ownership flag
lives in RadioConfig.cs, which is Track A's file this sprint, and the design
is not ratified until you say so.

## 1. Ratify the ownership model?

Ownership is a per-radio flag you set — "this radio is mine to write to" —
stored in the serial-keyed per-radio config. Registration or local discovery
may suggest a first answer; neither ever decides it. The Margaret test killed
the derive-it idea: you connected to her radio using her account, so to
SmartLink you WERE the owner. Unset means guest behaviour, which is the safe
default.

Say yes, no, or amend.

## 2. Two destinations, two verbs?

Saving a microphone profile in the Workshop stays PC-side and always safe, on
anyone's radio. Writing anything to the radio itself becomes a separate,
explicitly named action that only appears on radios you have marked as yours.
The save dialog's current "create a mic profile ON THE RADIO" option would
move under that second verb.

The cost: on your own rig, doing both takes one more step than a combined
save would. The benefit: no Save ever needs a moment's thought about whose
radio you are on.

## 3. What should the ownership question feel like?

The flag has to be set once per radio. Options, roughly in order of my
preference:

- Ask once at a natural moment — the first time an action would need it (for
  example the first time you reach for the write-to-radio verb), never as a
  connect-time interruption.
- A field on the per-radio Settings panel you set proactively.
- Both: the panel field exists, and the first gated action offers to set it.

## 4. The silent-TX auto-select — apply, offer, or park?

The `diag/don-audio-708` fix is real: an empty mic-profile selection means PC
transmit audio modulates nothing, and SmartSDR users never see it because
SmartSDR keeps "Default" loaded. Proposed: on radios marked yours it applies
silently; on any other radio the app says "this radio has no mic profile
selected, so computer transmit audio will not modulate — load Default?" and
does nothing without a yes. Until the flag exists it stays parked entirely,
which means the silent-TX failure remains live on affected radios — worth
weighing if you want an interim announce-only version (no write, just the
warning) ahead of the flag.

## 5. One question the implemented code already half-answers

Task #44 asked whether picking a mic profile should offer the radio-side
chain too or stay strictly PC-side. What shipped: applying a profile applies
your PC half always, and the radio half only where a binding for THIS radio
exists — the binding you created is the consent. My recommendation is to keep
that (bindings keep working regardless of the ownership flag; the flag gates
creating NEW radio-side state), but it deserves your explicit blessing since
it is a write to radio state that a bare flag reading would forbid.

## 6. The registration-eviction unknown (bench day)

Can one radio be registered to two SmartLink accounts? If registration is
exclusive, registering a friend's radio would silently evict them — the app
should refuse that without a warning regardless of everything above.
Margaret's radio is the ready-made test. Folded into #95.
