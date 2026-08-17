# Track F — Presets and config truth

**Worktree:** `C:\dev\jjflex-f` · **Branch:** `bsr/track-f` · **Model:** Sonnet

**Read first:** `docs/planning/active/barefoot-splatter-ragchew.md`, "Track F",
including "The radio ALREADY has mic profiles, and we use none of them".

## THE FINDING THAT CHANGES THIS TRACK

**The radio has a complete mic-profile system and we use none of it.** FlexLib
carries `profile mic create / save / load / delete / reset / info` plus a profile
list, and **there is not one reference to it outside FlexLib.** We were about to
build a parallel system beside one that already exists and is shared with every
other client.

**The split falls out of capture-then-sculpt:**

- **Stage one, PC capture** — which device, Windows input level, boost, the gate,
  our noise reduction. **The radio cannot store any of this**; a USB device
  identifier is meaningless to it. **Ours.**
- **Stage two, the radio's TX chain** — mic gain, EQ, compander, processor, bias.
  **The radio already stores these in its own profiles. Not ours to duplicate.**

**So our profile REFERENCES the radio's rather than copying it:** PC-side
settings plus the *name* of a radio mic profile. Selecting "headset" applies our
capture half and sends `profile mic load "headset"`. No duplication, no drift,
nothing fighting other clients over the same state.

**Three questions to answer:**

- **Referenced profile absent on this radio?** Apply the PC half, say so plainly,
  **do not guess at a substitute.**
- **Do we ever CREATE radio profiles?** Offer, never automatically — that is
  writing to someone's equipment.
- **The binding is per-radio**, since the profile list is. Fits
  `memory/project_per_radio_config_serial_keyed.md`.

**Design the model MICROPHONE-FIRST.** Flex's profile is *per-radio* state — "what
settings do I use on this radio". The operator's actual question is "what does
this microphone need", and that **travels**. One mic across a Flex, a Kenwood and
a borrowed rig is a case Flex's model cannot express at all.

**And stage two must be a DISCRIMINATED shape:** a *reference* on Flex (do not
duplicate what the radio manages), **actual values everywhere else** — because a
TS-590 or IC-7300 has no named-profile concept and nothing else will hold them.
Design it as reference-only and every non-Flex radio needs surgery later.

## The rest of the work

- **#49 — a corrupt preset file silently becomes the three defaults.** **Treat
  this as a real defect, not polish:** it is settings loss with no notification.
  The operator's tuning disappears and nothing says so.
- **#50 — exported presets carry no schema version**, and may be missing the TX
  EQ. Add a version; verify completeness.
- **#51 — a preset does not record which input it was tuned for.** Compounds #44.
- **#68 — `audioConfig.xml` lives in two directories.** Made *safe* on 2026-08-13
  (Load takes the newer, Save writes both); **not yet made correct.** Needs a
  real migration, and **must keep reading the old location for one release.**

## Coordination — read this before touching the config model

**`AudioOutputConfig.cs` is shared with Track D2.** You restructure the preset and
config model; D2 adds the meter and voice model, possibly to the same file.

**Additive edits to one file are fine. Two tracks restructuring one model is
not** — that is exactly the semantic collision git cannot see. **Settle who owns
the config model's shape before you start**, most likely D2, with you reporting
what you need.

**Track I's noise gate settings belong in your profile structure**, not the app —
a gate tuned for a headset in a quiet room is wrong for a desk mic in a noisy
one, and actively wrong when operating someone else's radio. Leave room.

## Papercuts you own

Wording papercuts in the preset and profile dialogs.

## Rules

- **Reuse the symbols you find. If you conclude one should move or change
  signature, REPORT it rather than doing it.**
- Build: `dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal`
- Commit style: `Track F: <description>`.
- **Do not merge, do not push to main, do not touch other worktrees.**

## Done means

Builds clean. Profiles are microphone-first with a per-radio binding, referencing
the radio's own profiles on Flex and carrying values where no profile system
exists. A corrupt preset says so instead of silently reverting. Exports carry a
schema version and the full contents. `audioConfig.xml` has a real migration that
still reads the old location. You have reported the config-model ownership
agreement reached with D2.
