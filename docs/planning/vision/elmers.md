# Elmers — guided learning as a product pattern

**Status:** concept captured 2026-08-14 from Noel, generalized from the audio
setup wizard.

> *"Audio elmer, like it. We could have other elmers too that walk through steps
> to learn tuning, to learn by ingesting pre-recorded training IQ etc."*

---

## The word is doing real work

In amateur radio an **Elmer** is a mentor — the experienced operator who sits
with you, answers the daft question you were embarrassed to ask, and stays until
you have made your first contact. It is one of the warmest words in the hobby
and every ham knows it.

**Blind operators are the least likely to have one.** Not because nobody is
willing, but because mentoring happens over a screen someone else is pointing
at. "Click the thing in the top right" is not mentorship you can receive. So the
people who most need an Elmer are the ones least able to be Elmered by
conventional means.

**An application that can Elmer you is therefore not a nice-to-have feature; it
is the accessibility argument extended from operating into learning.** It also
fits the brand: warm, ham-native, not a "wizard" or an "onboarding flow".

## The first one: Audio Elmer

Designed at `docs/planning/active/elmer-mic-checkin.md`. Prompted by Margaret, a
real operator Don mentioned who is not comfortable changing audio settings.
Advises which microphone, leads a mic check, leads a transmit test, with the
full Workshop reachable at every step.

Mostly composition over machinery that already exists as of 2026-08-13.

## Candidates, and why each one earns its place

**Tuning Elmer.** Classic versus Modern tuning modes, VFOs, RIT and XIT, band
edges, what a step size is for. The app already has strong tuning
infrastructure and two tuning philosophies; nothing teaches either.

**IQ Elmer — and this is the one with the sharpest pedagogical case.** Learn by
listening to pre-recorded band activity: find a signal, tune onto it, tell SSB
from CW from digital by ear, hear what "too wide" sounds like.

**Because it is recorded IQ, nothing can go wrong.** No transmitting, no
antenna, no embarrassment on the air, no way to break a setting that matters. A
nervous operator can practise the single most anxiety-producing skill in the
hobby with the stakes removed entirely. That is not a lesser version of real
operating — for a beginner it is *better*, because the same thirty seconds of
band can be replayed until the skill sticks.

**The engine for this is already a planned task.** Task #10, "Audio Track F —
receiver simulation on IQ playback", is exactly the substrate; it is gated on
the bench session (#56). See also `memory/project_daxiq_iq_findings.md`. So IQ
Elmer is a pedagogy layer over a receiver simulator that is already on the
roadmap — the same "composition, not new machinery" shape as Audio Elmer.

It also creates a genuinely new artifact class: **teaching material as recorded
IQ.** A curated set of clips — a clean SSB signal, a pileup, a CW ragchew, a
signal buried in QRM — is content that could ship with the app, be contributed
by operators, or be distributed through the Data Provider
(`memory/project_jjflex_data_provider.md`).

**Waterfall Elmer.** The waterfall is the signature feature
(`memory/project_waterfall_signature_feature.md`) and it will be an unfamiliar
instrument presented through sound. It will need teaching more than anything
else in the app. Its vocabulary should be the same sonification grammar the
meters establish — see `docs/planning/active/kerchunk-sidetone-pileup.md`.
Invent that language once and teach it once.

**First QSO Elmer.** The genuinely frightening one, and the one no software
currently helps with: what to say, how an exchange goes, what to do when you
lose the thread. Pairs naturally with IQ Elmer for the listening half.

**CW Elmer.** Large, and the CW arc is already ratified
(`memory/project_cw_notification_system.md`). Note the adjacency; do not fold it
in.

**Connect Elmer.** Eventually — a guest operating somebody else's radio for the
first time has a specific set of things to understand about grants, limits and
what they are responsible for.

## What all Elmers must share

These come straight out of the Audio Elmer design and should be settled once,
in a shared framework, rather than re-litigated per Elmer:

- **An escape hatch at every step**, to the real controls, with the ability to
  come back without losing progress. This is what separates mentoring from
  patronising, and an operator who starts timid and gets curious halfway is the
  success case.
- **Never the only route.** Everything an Elmer configures or teaches stays
  reachable by the ordinary path.
- **Resumable.** People get interrupted. Losing their place is how they do not
  come back.
- **No dead ends** — forward, back, out to the advanced surface, and quit
  without breaking anything, on every screen.
- **Real accessible surfaces**, verified with NVDA by using them. This audience
  is the least able to route around a bad one.
- **Never transmit without saying so first**, and prefer the steps that need no
  radio at all — Audio Elmer's first two steps and all of IQ Elmer qualify.
- **Plain words in the main line**, with the figures and the jargon behind the
  escape hatch where the help pages already explain them.
- **Offered, never nagged.** If an Elmer proposes itself, it is one clear offer,
  dismissible for good, and findable again afterwards by someone who changed
  their mind.

## Why this is strategically interesting

The project's differentiation is that a blind operator can do the whole job
without sighted help (`memory/feedback_accessibility_is_end_to_end.md`). Elmers
extend that from *doing* to *learning* — the part currently outsourced to a
sighted friend who is willing but cannot help through a screen reader.

It is also the answer to a soft-launch problem
(`memory/project_soft_launch_strategy.md`): every new user arrives without a
mentor, and the ones who most need the software are the ones most likely to
bounce off it in the first hour.

## Sequence

Audio Elmer first — it has a named user waiting, the machinery already exists,
and it is the smallest complete instance to learn the framework on. **Build the
shared framework from that one rather than designing it up front**, then judge
the second Elmer against how well it reuses it.

IQ Elmer is the natural second, gated on Track F (#10) and the bench session
(#56).
