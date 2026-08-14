# Elmer Mic Check-In — the audio setup wizard

**Status:** idea captured 2026-08-14 from Noel, not started.

**This is the first of a family.** Noel generalized the idea the same day —
*"We could have other elmers too that walk through steps to learn tuning, to
learn by ingesting pre-recorded training IQ etc."* The pattern, the other
candidates, and the requirements every Elmer shares are in
`docs/planning/vision/elmers.md`. Audio Elmer goes first because it has a named
user waiting and the machinery already exists; **build the shared framework out
of this one rather than designing it up front.**

> *"An audio setup wizard for those who are uncomfortable setting up audio, with
> ability to jump to the more advanced workshop pages at every step. Make it
> simple enough for a beginner to be advised as to the right microphone to pick,
> then be led to do a mic check, then led to transmit test, with offshoots into
> transverter stuff later."*

## Who this is for, and why that matters

**Margaret.** A real operator Don mentioned who is not comfortable changing
audio settings. Not a hypothetical persona — a person who will not touch the
Audio Workshop, and whose radio therefore sounds however it happens to sound.

That is worth holding onto, because it changes what "success" means. The wizard
is not there to be faster than the Workshop for someone who already knows what a
compander is. **It is there for the operator who would otherwise change
nothing.** Measure it against that.

It is also the flow-level version of a principle already ratified at the
sentence level. The verdict wording rewritten 2026-08-13 came from Noel's note
that *"audio adjustment can be a stressful process"* — the wizard is that same
observation applied to the whole journey instead of one utterance.

## The key design element is the escape hatch

Noel specified it up front: **the ability to jump to the more advanced workshop
pages at every step.**

That is the difference between a wizard that respects the operator and one that
patronises them. Every step should offer, plainly, "take me to the full controls
for this" — and coming back should not lose progress. An operator who starts
timid and gets curious mid-way is the *success* case, not a defection.

The corollary: **the wizard must never be the only route to a setting.**
Everything it configures stays reachable in the Workshop, always.

## It is mostly composition, not new machinery

Nearly every part already exists as of 2026-08-13:

- **Device picking** — the picker now filters to things you can actually talk
  into, with duplicates folded and rows leading with the device name.
- **Setting the level** — the Microphone Check carries the Windows input level
  and Microphone Boost, with a peak that zeroes when you move the slider, and
  loudness once the level is safe.
- **Coaching in plain language** — seven bands, a human voice, direction and
  stage in every verdict, rotating phrasings behind a fixed leading token.
- **Proving it without transmitting** — the Microphone Check involves no radio
  at all. Nothing is transmitted; the help text says so, deliberately, because
  that is the first thing a nervous operator wants to know.
- **The transmit test** — the Audio Check keys through the PTT safety controller.

So the wizard is a *sequence and a narrator* over machinery that is already
built and already tested. That is the cheap and honest version. Resist building
parallel implementations of any of it — a second device picker or a second
level control is how the two drift apart and the beginner gets the worse one.

## The sequence

Broadly the Workshop's own walk-through order, which was built 2026-08-12 to run
outward from the computer to the radio to the air — one step per screen, with
the decisions made for the operator instead of presented to them.

1. **Which microphone.** Advise, do not just list. The wizard should be willing
   to say "this one" — the headset over the array, a named interface over a
   virtual cable — and say why in one sentence. **This is the step that most
   needs opinion**, because a beginner facing eight device rows is exactly where
   Margaret stops.
2. **Is it working, and is the level right.** The Microphone Check, driven. No
   radio, nothing transmitted, and say so before they wonder. End when the
   verdict is in the good band, and let them hear it get there.
3. **What the radio does with it.** Only the settings that matter for a first
   setup. Compander and speech processor probably belong behind the escape
   hatch, not in the main line — see the DSP explanation work (#73), and note
   that a beginner cannot evaluate a compander by ear in a quiet room anyway.
4. **The transmit test.** Explicit, consented, and it says exactly what will
   happen before it happens.
5. **Later offshoots:** transverter setup, per Noel. Not in the first pass.

## Open questions, to settle before building

- **Does it offer itself on first run?** Tempting and dangerous. A wizard that
  appears uninvited is a modal in front of someone who wanted to use their
  radio. If it does, it is one clear offer, dismissible forever, never repeated
  — the friction-tax principle, not a nag.
- **Where does it live otherwise?** Help menu, the Workshop's front page, or
  both. It must be findable by someone who declined it once and changed their
  mind.
- **What does it save, and does it say so?** Settings are intents
  (`memory/project_settings_are_intents_not_commands.md`). If a step defers a
  change until connect, the wizard says when it will take effect.
- **Does it resume?** Margaret gets interrupted like anyone else. Losing her
  place is how she does not come back.
- **What if there is no radio?** Steps 1 and 2 need no radio at all — that is a
  strength worth using. The wizard should get her a working, correctly-levelled
  microphone before a radio is ever involved, and say that is what it is doing.

## Constraints

- **Every screen is a real accessible surface**, verified with NVDA by using it,
  not by inspection. This audience is the least able to route around a bad one.
- **No dead ends.** Every screen has forward, back, escape-to-advanced, and
  quit-without-breaking-anything.
- **Never transmit without saying so first.** Non-negotiable given who this is
  for.
- **Plain words.** No dBFS in the main line unless it is explained where it
  appears; the figures live behind the escape hatch where the Workshop already
  explains them (`docs/help/md/audio-two-numbers.md`).

## Related

- `memory/project_capture_then_sculpt.md` — the wizard's spine is exactly this
  order, and getting stage one right is most of the value for a beginner.
- `memory/project_friction_tax_principle.md` — the app does the work unless
  safety, ownership or privacy says otherwise. A wizard is that principle made
  visible.
- `memory/project_soft_launch_strategy.md` — first-run experience is a soft-launch
  concern, and this is the first-run experience for audio.
- `memory/project_jjflexible_connect.md` — a guest operating someone else's radio
  carries their own capture settings and leaves the host's TX chain alone. The
  wizard configures precisely the half that roams.
- `#73` DSP explanations, `#44` mic profiles, `docs/planning/active/kerchunk-sidetone-pileup.md`.
