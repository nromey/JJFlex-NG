# Mic Profile Ownership

Track B, Sprint 30 (2026-08-18), for task #94. Task #94's full evidence trail
lives in the task body; this doc is the design conclusion built on it.

**Status: RATIFIED 2026-08-19 by Noel, and built in Sprint 31 Track S.** The
sections below are kept as written, because the reasoning is the durable part.
What shipped, and the three places it deliberately differs, are recorded at the
bottom under "What shipped".

## The two axes, untangled

The confusion #94 reports comes from treating two different questions as one.

**Axis one — where a setting lives.** Three homes configure overlapping things:

- The **radio's own mic profile** (FlexLib `ProfileMICList` / `ProfileMICSelection`).
  Lives on the radio. Shared by every client that connects. Survives the
  operator leaving. Writing here changes the radio for everyone.
- The **operator's microphone profile** (`Radios\MicrophoneProfile.cs`,
  `{operator}_micProfiles.xml`). Lives on this PC. Travels with the person.
  Writing here changes nothing outside this machine.
- **Connect**, a third home once it exists. Not yet designed; the model below
  is written so it slots in rather than reshuffles everything.

**Axis two — whose radio it is.** Writing a mic profile to your own 8600 is
housekeeping. The same write to Margaret's radio changes her transmit chain
silently, from Memphis to Massachusetts.

## What is already built (verify before designing — the audit had drifted)

The operator/rig split the backlog still called "missing" shipped 2026-08-16
(`1b35ace3`). The current state, confirmed in code this sprint:

- A `MicrophoneProfile` is named for the MICROPHONE and carries the capture
  half once (device identity, Windows input level, boost, and — as of this
  sprint — the transmit cleanup chain: PC noise reduction and the gate).
- The radio half is a per-radio binding: on a Flex, a REFERENCE to a mic
  profile the radio itself owns (never a copy that could drift and fight other
  clients); for radios with no profile concept, actual stored values.
- **The guest rule already works:** a radio with no binding gets NOTHING
  applied to it. Your capture settings apply; their TX chain is left alone,
  and the app says so.
- **Writing to the radio is already explicit:** creating a mic profile ON the
  radio is one of the offered choices in the save dialog, never automatic.
- Mismatches are announced, not absorbed: a profile made with a different
  microphone leaves the level and cleanup settings alone and says why; a
  referenced profile absent on this radio is reported plainly, never created
  behind the operator's back.

So most of the safety the ownership question exists to protect is in place at
the profile layer. What is missing is the layer beneath it: the app has no
concept of *whose radio it is connected to*, and two real behaviours need one.

## Why ownership cannot be derived — the finding that shapes everything

Noel proposed deriving ownership from SmartLink registration: your radio if it
is registered to your account. It fails on the exact case that prompted the
question, tested the same day: he connected to Margaret's radio **using
Margaret's account**. The trace reads "Connecting to MargaretGaffney over
SmartLink as mmgaffney@comcast.net". A registration test would have called him
the owner, because to SmartLink he was.

Registration answers **who has access**, not **whose radio it is**. Those
coincide for a solo operator and diverge the moment anyone helps anyone else —
which is most of what the tester pool does. Two more cases no inference can
see:

- A LAN-only radio has no registration at all. No signal.
- Don's 6300 lives at Tony's house: local to Tony, remote to Don,
  unambiguously Don's. Physical location does not settle it either.

**Conclusion: ownership is a per-radio flag the operator SETS.** Registration
or local discovery may seed a first guess; neither can be the source of truth.

## The recommended design (not ratified — do not implement until Noel says so)

- **The flag.** `MineToWrite` (working name), a per-radio boolean on the
  serial-keyed per-radio config in `Radios\RadioConfig.cs`. Unset means
  "treat as not mine" — the safe default is guest behaviour. RadioConfig is
  Track A's file this sprint, which is the second reason the flag waits.
- **Seeding, never deciding.** On first sight of a radio the app may propose a
  default — registered to the operator's own SmartLink account suggests
  "mine"; anything else suggests nothing — but the flag is only ever set by
  the operator, once, and remembered. A one-time question at a natural moment
  beats a silent guess in both directions.
- **Two destinations, two verbs.** Do not overload "save preset" with "write
  to radio". The Workshop's microphone profile stays PC-side and portable —
  saving one is always safe, on anyone's radio. Writing to the radio is a
  separate explicit action with its own verb, surfaced only on a radio the
  operator has marked as theirs. Two destinations, two verbs, no ambiguity
  about what a Save just did. (The save dialog's current explicit
  create-on-radio option is the embryo of the second verb; under this design
  it appears only on owned radios.)
- **What ownership gates.** Every write to shared radio state that the
  operator did not individually ask for, starting with the
  `diag/don-audio-708` auto-select (below). Reads gate on nothing. Applying a
  stored VALUES binding is an interesting middle case: it writes radio state,
  but only state the operator explicitly bound to this radio earlier — the
  binding itself is the consent. Recommended: bindings keep working regardless
  of the flag; the flag gates CREATING new radio-side state and unrequested
  housekeeping writes.

## The auto-select, and why it stays fenced

Branch `diag/don-audio-708` (origin, commit `7b2c427e`) carries nineteen lines
that fix a real, pcap-verified silent-TX failure: a radio whose mic profile
selection is EMPTY has no transmit-audio DSP chain, so PC transmit audio
modulates nothing (SC_MIC pinned at -120). SmartSDR never hits this because it
keeps profile "Default" selected. The fix selects "Default" (or the first
available profile) whenever the selection is empty.

It is correct on your own radio and exactly the unauthorized write on someone
else's: `ProfileMICSelection` is shared radio state, and an empty selection on
a guest radio might be its owner's deliberate arrangement. Applying it
unconditionally writes to shared radio state on what may be a guest
connection — precisely what the ownership answer gates. So:

- On a radio marked mine: apply silently. It is housekeeping, and it makes PC
  transmit audio work.
- On any other radio: do not write. The failure is still real, so say it and
  offer it — "This radio has no mic profile selected, so computer transmit
  audio will not modulate. Load {name}?" — one keystroke for the operator who
  is allowed, zero silent writes to a radio that is not theirs.

Until the flag exists, the fence holds: the branch stays unapplied.

## The unknown worth testing separately

Can one radio be registered to two SmartLink accounts? Noel suspects yes.
Margaret's radio is already on her account, so registering it to his would
answer it. This matters beyond ownership: if registration IS exclusive, then
registering a friend's radio would silently EVICT them — a hazard in its own
right, and something the app should refuse to do without warning. Folded into
the #95 bench-day item.

## What shipped (Sprint 31 Track S, 2026-08-19)

**The flag.** `RadioConfig.RadioOwnership` — `Unset` / `Mine` /
`SomeoneElses`, serial-keyed, appended to the per-radio config beside
`SmartLinkIntent` and `RemOnOnConnect`. Absent from an older config.xml it
deserialises to `Unset`, so an upgrade never arms a write. Three states rather
than a boolean for the same reason `SmartLinkIntents` has three: "never asked"
and "asked, and the answer was no" are different, and only the first may raise
the question. `MayCreateRadioSideState` is the single accessor everything else
gates on; `SuggestOwnership(operatorAccount)` proposes a pre-selection and can
never propose `SomeoneElses`.

**Two surfaces, as ratified.** A standing field on Settings → Radios → "Whose
radio is this", with no cascade and no challenge; and `RadioOwnershipDialog`,
asked at the moment an action needs the answer. The dialog has three outcomes,
not two: Escape and "Not now" record nothing, because backing out of an
unexpected question is not a statement that the radio belongs to someone else.

**Bindings are not gated**, and the reasoning is now a `<remarks>` block on
`MicrophoneProfile.ApplyRadioHalf` saying so, since that is the one place a
later reader would "fix" by adding a check.

### Three deliberate differences from the text above

1. **The auto-select does not run at connect, on any radio — not even one
   marked `Mine`.** The design says "on a radio marked mine: apply silently",
   and Sprint 31 Track S was instructed instead to ship the announcement only
   and leave `diag/don-audio-708` unapplied. That instruction was followed, and
   it holds up on its own: the flag is `Unset` on every existing install, so a
   connect-time write would fire on no radio at all on day one, and the first
   operator to answer "mine" would be the first person ever to run an untested
   silent write to shared state during connect. The mechanism instead ships
   behind a press. **One decision is outstanding for Noel** — whether an owned
   radio should get the connect-time version. It is a small change, in
   `FlexBase.CheckMicProfileForSilentTx`, and it should be made on purpose
   rather than inherited.

2. **The repair is offered on every radio, not only on owned ones**, matching
   the design's own "say it and offer it… one keystroke for the operator who is
   allowed". Ownership changes how much is asked before the write, never
   whether the offer exists: `Mine` runs on the press with a receipt, `Unset`
   asks whose radio it is first, `SomeoneElses` confirms with the shared-state
   consequence named. That last case is deliberate — ownership is a declaration
   of intent, so it must not become a lock an operator cannot deliberately
   step over on their own equipment.

3. **The Settings field never pre-populates from a guess**, unlike the
   no-physical-access checkbox two groups below it, which pre-populates and
   says it did. The asymmetry that justifies pre-checking there does not hold
   here: guessing "reachable" wrong only costs a suppressed warning, while
   guessing "mine" wrong pre-arms writes to a radio that is not the operator's.
   The suggestion has a home — a sentence in the ask dialog that says out loud
   that it is a guess.
