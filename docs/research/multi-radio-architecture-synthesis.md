# Multi-Radio Architecture Synthesis

**Status:** Multi-radio Phase 9 deliverable — the synthesis Noel reads when
deciding whether to greenlight an implementation sprint
**Date:** 2026-04-29
**Author:** Claude Opus 4.7 (track/multi-radio)
**Audience:** Noel — the load-bearing review document for this whole track
**Builds on:** Phases 1-8 (every prior research doc on this branch)

---

## Strategic frame

JJ Radio (Jim Shaffer's pre-JJFlex application) is being folded into JJ
Flexible (`project_jj_radio_folding.md`). JJ Flexible inherits JJ Radio's
user base and the commitment to support the radios those users operate. At
the same time, JJ Flexible has signed up named testers operating Kenwood
radios (Mark's TS-590G, Doug's TM-V71A, Noel's TS-2000) and intends to honor
those commitments. The architectural response is a multi-radio backend layer
that lets JJ Flexible host both FlexLib (for FlexRadio SDR transceivers) and
Hamlib (for everything else) behind a single user-facing model — while
preserving the .NET / WPF accessibility moat
(`project_csharp_accessibility_moat.md`) and the universal-design product
thesis (`project_strategic_identity.md`).

The work is research and architecture only at this stage. This document is
the synthesis of Phases 1-8 into a coherent proposal Noel can review and
decide to greenlight (or refine) for an implementation sprint.

---

## 1. The architecture in prose

The proposal is a layered architecture with three new abstractions and a
preserved-and-extended legacy:

**`IRadioBackend`** is the C# interface JJF UI talks to for everything
radio-related. Its surface is large but bounded: lifecycle (connect /
disconnect / events), slices (a unified concept that subsumes Flex's
slices and Hamlib's VFOs), get/set methods for frequency / mode / levels
/ functions / memories, capability introspection, and optional streaming
surfaces (panadapter, waterfall, audio) that are non-null only on backends
that support them. Two concrete implementations exist initially —
`FlexLibBackend` (the existing JJF radio code refactored behind the
interface, no behavior change for Flex users) and `HamlibBackend` (new code
wrapping `libhamlib-5.dll` via P/Invoke).

**`IPerRadioConfig`** is the user-side companion. Per-radio config is keyed
by a stable radio-id string — `flex-<serial>` for Flex radios,
`hamlib-<modelid>-<serial-or-nickname>` for Hamlib radios — with each
radio's user-overridable settings (antenna labels, ATU memories, Customize
Home layout, audio routing) living in
`%AppData%\JJFlexRadio\radios\<radio-id>\`. The boundary with user-scope
config is intentional: things that depend on the radio's hardware (TX
controls, antenna labels) belong per-radio; things about the user's
preferences (verbosity, keyboard layout, log paths) stay user-scope.

**`IAudioRouter`** handles audio for non-Flex radios via Windows soundcard
APIs (NAudio + PortAudio + Windows Core Audio). The setup UX is fully
accessible: per-radio audio device picker with audible "Test Receive" /
"Test Transmit" diagnostics. Audio plumbs into JJF's existing
`RxAudioPipeline` (NR + spectral sub) — Mark's TS-590 gets the same PC-side
neural noise reduction Don's 6300 gets, regardless of whether the radio's
hardware NR is good or weak.

**Legacy preserved:** Jim's `AllRadios` + `RigCaps` + `RadioSelection.RigTable`
already established the multi-radio scaffold; the deletion in commit
`d8b67758` removed implementations but left the abstraction shape. The new
`IRadioBackend` extends rather than replaces — `RigCaps.Caps` flag names
carry forward, capability-driven UI rendering is the same pattern, the rig
registration table is the same shape (just larger to accommodate hundreds of
Hamlib-supported models). Refactoring is staged across multiple sprints with
backward compatibility throughout the transition.

---

## 2. The radio class taxonomy

Five classes anchor the design. Each is grounded in a radio a real tester
operates (or an inherited JJ Radio user is likely to operate):

- **SDR Transceiver** (FLEX-6300/6400/6500/6600/6700/8400/8600/Aurora) —
  Don, Justin, Noel. Multi-slice, network audio, panadapter streams,
  SmartLink remote, MultiFlex multi-client, feature licenses. FlexLibBackend
  serves this entire class.
- **HF Transceiver** (Kenwood TS-590S/SG, IC-7300, FT-991A, Elecraft K3) —
  Mark. Single-VFO-pair, USB audio CODEC, internal panadapter on some
  models. HamlibBackend.
- **All-Mode All-Band** (Kenwood TS-2000, Icom IC-9100, IC-7100) — Noel.
  HF + VHF + UHF + satellite + main/sub receiver. **Primary cross-class
  testbed.** HamlibBackend.
- **VHF/UHF FM Mobile** (Kenwood TM-V71A, TM-D710G, FTM-400) — Doug. Two
  bands always active, FM only, memory-primary, built-in TNC, hardware
  flow control on serial. HamlibBackend.
- **HT** and **RX-only** — provisional placeholders, no current tester.
  Capability flags exist; classes hold space in the taxonomy for future
  testers without architectural rework.

Class membership is **metadata for diagnostics and config defaults**, NOT
runtime UI dispatch. The UI branches on capability flags
(`HasCapability(RadioCapability.SatelliteMode)`), never on class. This keeps
the rendering logic uniform across Flex, Kenwood, Yaesu, Icom — every UI
section is gated by the same flag-check pattern.

---

## 3. Capability flags as the universal language

`RigCaps.Caps` (`Radios/RigCaps.cs:22-121`) is the historical capability
flag system Jim built. The new design preserves the names where applicable
and adds new flags for class-specific concerns (VHF/UHF mobile primitives,
streaming surfaces, programming-mode bulk operations, hardware flow control,
license gating). The 64-bit packed enum is at saturation; the new design
uses `HashSet<RadioCapability>` to allow open-ended growth.

For HamlibBackend, capability flags populate dynamically from
`rig_get_caps()` at connect time — Hamlib introspects the radio's supported
levels, functions, and modes; the backend translates them into JJF
capabilities. **This means adding a new Hamlib-supported radio model
typically requires zero JJF code changes** — just a registration entry in
the catalog, automatic from the Hamlib enumeration.

For FlexLibBackend, capability flags include FlexLib feature license state
(LicenseFeatDivEsc, etc.), so license activation/deactivation produces
`CapabilitiesChanged` events the UI can re-render against. This generalizes
existing Flex license-gating into the same pattern Hamlib uses.

---

## 4. Slice abstraction unifies Flex and Hamlib

Flex's slice concept (1-8 simultaneously decoded receivers per radio) is
richer than Hamlib's VFO concept (typically A/B with main/sub on dual-
receiver radios). The unified design exposes **slices** as the JJF concept,
mapping VFO-only radios as N=1-or-2 slices:

- Flex: slices map directly. SliceCount = radio's MaxSlices (2-8).
- TS-590-class: SliceCount = 1, single-VFO-pair operation. Split TX is a
  property of slice 0, not a second slice.
- TS-2000 / IC-9100: SliceCount = 2 — slice 0 = main, slice 1 = sub.
- TM-V71A: SliceCount = 2 — slice 0 = Band A, slice 1 = Band B (cross-band
  is a function toggle, not slice change).

The trade-off: single-VFO-with-split radios feel slightly stretched by the
slice model. Accept the stretch; the unified slice model preserves JJF's UI
mental model across the radio classes JJ Flexible supports.

Internally, HamlibBackend tracks "intended VFO" per slice and uses explicit
`vfo_t` values instead of `RIG_VFO_CURR` to keep behavior deterministic
regardless of the operator turning the radio's panel knob.

---

## 5. Per-radio config keyed by stable radio-id

Today's config (`%AppData%\JJFlexRadio\k5ner_Noel_Romey_*.xml`) is
user-scope. Some of it has radio-state mixed in (antenna labels, ATU
memories). The new design separates:

- **User-scope** (preferences regardless of radio): verbosity, keyboard
  customization, NVDA preferences, log paths, default audio output device,
  updater channel, crash-reporter consent.
- **Per-radio** (radio-state-dependent): antenna labels per port, ATU
  memory annotations, per-radio Customize Home layout, memory channel user-
  labels, TX control profiles, audio routing, per-radio default verbosity.

Per-radio config lives in `radios\<radio-id>\` with one JSON file per
concern (`connection.json`, `antennas.json`, `atu-memories.json`,
`customize-home.json`, `memory-channel-meta.json`, `audio.json`,
`notes.json`). Schema-versioned; user-driven import/export bundles per radio.

Migration from today's mixed-scope config is incremental: when a feature
that touches radio-state config is implemented, that data migrates per-radio
in the same sprint. No bulk refactor.

The radio-id format:

- `flex-<serial>` for Flex radios — serial is unique, embedded in CAT
- `hamlib-<rig_model_id>-<serial>` when Hamlib reports a serial via CAT
- `hamlib-<rig_model_id>-<user-nickname>` when serial isn't available

Including `rig_model_id` guards against radio-swap config inheritance bugs.
The user nickname is the disambiguator for indistinguishable-via-CAT
multi-radio cases.

---

## 6. Audio routing as a sibling concern

Hamlib does CAT only. Audio for non-Flex radios is JJF's job, via
`IAudioRouter`. The setup UX is per-radio: pick a Windows recording device
(radio's RX audio coming in), pick a playback device (PC audio going to
radio), confirm with audible "Test Receive" / "Test Transmit" buttons.

Three classes of non-Flex audio plumbing:

- **HF transceivers with USB audio CODEC** (TS-590SG, IC-7300) — radio
  enumerates as a USB audio device; clean.
- **HF transceivers with analog audio jacks** (TS-590S, older Yaesus) —
  external sound interface (SignaLink, RIGblaster, DigiRig).
- **VHF/UHF FM mobiles** (TM-V71A) — speaker audio + optional data port for
  packet/digital.

The audio pipeline middle-and-end (NR, spectral sub, output to listening
device) is reused from the existing Flex pipeline. Only the source side
changes — `SoundcardAudioSource` is the new implementation alongside
`FlexNetworkAudioSource`. Both implement `IAudioSource`; orchestration code
plugs the right one in.

This is a real win for non-Flex testers: **Mark's TS-590 gets PC-side
neural noise reduction (currently Flex-6000-only)** — a moat-grade benefit
extended to the broader user base.

---

## 7. TS-2000 conformance gates the architecture

Phase 7's ~80 conformance tests across 17 categories form the smoke test for
the multi-radio architecture. The TS-2000 covers HF + VHF + UHF +
satellite + dual-receiver in one device — passing the conformance means the
abstraction handles every code path the inherited JJ Radio user base might
hit.

**Definition of done for the architecture:** TS-2000 backend passes the
Phase 7 conformance list. If it does, TS-590 (HF subset) and TM-V71A
(VHF/UHF FM subset) implementations are incremental from there. If it
doesn't, the failures identify gaps in the abstraction that get folded
back into the design before broader implementation.

---

## 8. Tester onboarding — the human side

Each named tester has a documented end-to-end onboarding path (Phase 8):
install JJF, add their radio (model from Hamlib catalog or Flex discovery),
configure connection params (with class-specific defaults — flow control on
for TM-V71A, USB-audio for TS-590SG, network for Flex), configure audio
(skip for Flex; soundcard picker with audible test for non-Flex), connect,
operate.

Quality bar: **less than 10 minutes from "open JJF" to "operating on the
air with screen-reader feedback"** for each non-Flex tester's first
connection. Friction-tax violations are bugs.

Mark's TS-590 onboarding additionally accommodates **neuropathy** — adjacent-
key forgiveness, small-increment reversibility, destructive-action
confirmation. The same accessibility-first design that serves blind users
serves users with reduced fine-motor control.

---

## 9. Sprint scope recommendations

The full multi-radio arc is plausibly **5-6 sprints** of implementation
work. The sequencing:

### Sprint Multi-1 — Introduce IRadioBackend
- Define `IRadioBackend` interface and supporting types
- Refactor `FlexBase` / `AllRadios` to implement the interface
- UI code begins consuming `IRadioBackend` via casts (most callsites still
  use existing `theRadio` direct path)
- No new functionality; pure refactor
- Exit criteria: existing JJF works identically for Don, Justin, Noel-on-
  Flex; UI tests pass

### Sprint Multi-2 — HamlibBackend skeleton
- Bundle `libhamlib-5.dll` in installer
- Hand-roll P/Invoke layer for the ~50 calls JJF needs
- Implement `HamlibBackend` class
- Wire connection lifecycle + frequency/mode/PTT only
- Connection-test workflow in UI; rig picker + Hamlib catalog enumeration
- Exit criteria: Noel can connect to TS-2000, set frequency, transmit (no
  audio yet — silent operation)

### Sprint Multi-3 — Audio routing
- Introduce `IAudioRouter`
- Audio device picker dialog with accessible test buttons
- `SoundcardAudioSource` plumbing into the JJF audio pipeline
- Per-radio audio config persistence
- Exit criteria: Noel hears RX audio on TS-2000; can transmit voice; NR
  pipeline works on non-Flex audio

### Sprint Multi-4 — Capability surface broadening
- Wire levels (AF, RF, SQL, etc.) and functions (NB, NR, etc.) for
  HamlibBackend
- Memory channels read/write for HamlibBackend
- Tone (CTCSS / DCS) operations
- Per-radio config: antenna names, ATU memories
- Exit criteria: TS-2000 conformance test partial pass — most of the
  ~80 tests passing

### Sprint Multi-5 — Mark's TS-590 + Doug's TM-V71A
- Mark's TS-590G connects, operates fully
- Doug's TM-V71A connects with hardware flow control, two-band UI surfaces,
  memory channels work
- Quirks: TM-V71A's required RTS/CTS, programming-mode bulk operations
  flagged as not-yet-supported
- Per-radio config: TM-V71A group names, channel-edge-pair handling
- Exit criteria: full conformance pass for TS-2000; TS-590 and TM-V71A
  onboarding documented; testers using daily

### Sprint Multi-6 — Polish, documentation, hardening
- TS-2000 satellite mode end-to-end (real satellite pass)
- Cross-radio routing UX (use Flex 8600 as reference for IC-7300 — forward-
  looking)
- Multi-active-radio tabbed UI scaffolding (Sprint 30+ scope start)
- Per-class quick-start guides for testers
- Migration tools for incoming JJ Radio users
- Exit criteria: stable operation across all current testers, documented,
  ready for broader user-base announcement

This scopes to 5-6 sprints minimum. Could compress with parallel tracks
(audio routing parallel to capability broadening), at the cost of
coordination overhead. Default to serial.

### What this DOES NOT change in 4.1.x

- 4.1.17 release (foundation phase): no multi-radio. Foundation hardening
  for Flex.
- 4.2.0 release (FlexLib upgrade): no multi-radio. Flex-specific upgrade.
- Sprint 28-29 work: Flex-only.

Multi-radio enters as a Sprint 30+ track. Parallel to other Sprint 30 work
(Customize Home, verbosity architecture, NVDA app module, waterfall).

---

## 10. Risks and open questions

### 10.1 Risks

- **`bindings/csharp/` upstream is empty.** Hamlib's SWIG-to-C# binding
  generation is not configured. Path A (hand-rolled P/Invoke) is the
  pragmatic answer; Path B (SWIG) is a Phase 5+ refactor when surface area
  grows. Risk: hand-rolled marshalling bugs surface in production. Mitigation:
  bounded surface area (50 calls), extensive testing on TS-2000.
- **Hamlib version drift.** Hamlib evolves with active development; ABI is
  not strongly guaranteed. Pin to 4.7.4+ specifically; don't dynamic-resolve.
- **`channel_t` struct layout.** Memory channel record across Hamlib versions
  has changed. Pin Hamlib version; audit struct on each bump.
- **Native callback marshalling.** Transceive callbacks are the trickiest
  P/Invoke pattern. Test rigorously; fall back to polling on radios with
  buggy transceive.
- **Hardware flow control trap (TM-V71A).** Without it, radio is silent.
  JJF's connection-test must surface this clearly with cable-loopback
  workaround link.
- **The TS-2000 conformance test has not been verified end-to-end.** Until
  a HamlibBackend is implemented and Noel tests, every claim about TS-2000
  behavior is "according to Hamlib + research." Conformance testing is the
  truth-teller.

### 10.2 Open questions for Noel

(Repeating the must-decide questions from Phases 4-8.)

1. **Confirm the radio-id format.** `flex-<serial>` /
   `hamlib-<modelid>-<serial-or-nickname>` (recommended) vs UUID-only vs
   nickname-only. Recommendation: stable-source-keyed (the one proposed).
2. **`AllRadios` legacy lifetime.** Internal helper indefinitely vs
   public-API removal once UI migrates. Recommendation: indefinitely
   internal; remove from public API in Sprint 30+ window.
3. **Path A vs Path B for Hamlib bindings.** Recommendation: Path A first.
4. **Hamlib bundling vs system install.** Recommendation: bundle.
5. **Streaming surfaces — per-backend optional interfaces vs capability +
   method on backend.** Recommendation: separate interfaces.
6. **Tester acquisition for cohort expansion.** Once initial 3 non-Flex
   testers are working, who comes next from the JJ Radio user base?
7. **Cross-platform native UI plan.** This synthesis preserves the .NET
   accessibility moat. The cross-platform path (SwiftUI iOS, Compose Android)
   is alongside, not on top. Confirm direction.
8. **Funding / time horizon.** 5-6 sprints is real time. Decided here is
   the architecture; the implementation timeline is Noel's call given other
   priorities (4.1.17 foundation, 4.2.0 FlexLib upgrade, waterfall arc).

---

## 11. The decision Noel is being asked to make

**Should an implementation sprint be greenlit?**

If yes:
- First implementation sprint is "Sprint Multi-1" above (introduce
  IRadioBackend, refactor FlexBase). Small, focused, low-risk. Validates
  the abstraction without committing to Hamlib bringup.
- Sprint Multi-1 outcome is "JJF works identically for Flex users; the
  abstraction layer exists." If that doesn't ship cleanly, the broader
  arc is at risk and we revisit.
- Subsequent sprints commit incrementally based on Sprint Multi-1 outcome.

If no, or "later":
- The architecture lives in this branch as research. Branch can be merged
  as docs-only (no `Radios/` changes) or kept on track/multi-radio for
  reference.
- When the implementation green-light comes, Phase 10's handoff doc points
  to the right starting place.

If "yes, but different":
- Specific changes Noel wants (different sequencing, different scope,
  different sprint count) feed back into a revised plan. Phase 9 can be
  re-issued with revisions.

---

## 12. What this synthesis does NOT propose

To be honest about scope:

- **No specific Hamlib version pin yet.** "4.7.4+" is the baseline; final
  pin is implementation-time decision based on what's stable then.
- **No specific UI mockups for the multi-radio picker, audio dialog, etc.**
  The descriptions in Phases 6 and 8 are illustrative; the implementation
  sprint produces real UI specs.
- **No code.** This is research. Implementation code is post-greenlight.
- **No commitment to specific Hamlib radios beyond TS-2000, TS-590, TM-V71A.**
  Other Hamlib radios become incremental add-ons after the 5-6 sprint arc;
  prioritization based on JJ Radio user base when that list surfaces.
- **No commitment to JJ Radio's entire historical surface.** JJ Radio's
  Generic / GenericBinary escape hatches are not preserved (Hamlib's
  catalog supersedes). Per-radio support is for radios Hamlib supports;
  unsupported radios are out of scope until a Hamlib backend driver exists.
- **No multi-active-radio orchestration.** The interface allows it; the
  orchestration layer (which radio gets keystrokes, which gets audio) is a
  Sprint 30+ scope after the multi-radio backend is solid.
- **No firmware-update mechanics for non-Flex radios.** Hamlib doesn't
  generally do firmware. Out of scope.

---

## 13. Cross-references

This synthesis pulls from:

- `jj-radio-inventory.md` (Phase 1) — what we're inheriting
- `radio-class-taxonomy.md` (Phase 2) — what we're modeling
- `hamlib-api-survey.md` (Phase 3) — how we talk to non-Flex radios
- `iradio-backend-design.md` (Phase 4) — the central interface proposal
- `per-radio-config-strategy.md` (Phase 5) — user-side config
- `audio-routing-non-flex.md` (Phase 6) — audio plumbing
- `ts2000-conformance.md` (Phase 7) — the smoke test
- `tester-onboarding.md` (Phase 8) — the human side

It feeds:
- `handoff.md` (Phase 10) — the resume document for the implementation
  sprint
- `docs/planning/inbox/` — review-ready landing zone for Noel

Memory cross-references:
- `project_jj_radio_folding.md` — strategic frame
- `project_csharp_accessibility_moat.md` — hard architectural constraint
- `project_strategic_identity.md` — universal-design product thesis
- `project_flexibility_principle.md` — user choice as design commitment
- `project_per_radio_config_serial_keyed.md` — per-radio config principle
- `project_ts2000_cross_class_testbed.md` — testbed selection rationale
- `project_doug_tmv71a_tester.md` — Doug's TM-V71A research integration
- `project_kenwood_590g_commitment.md` — Mark's TS-590G commitment
- `feedback_accessibility_is_end_to_end.md` — onboarding accessibility rule
- `project_anti_patterns_from_blindcat.md` — design-review checklist

External research from prior Track B sessions:
- `at-scripting-research.md` — NVDA/JAWS scripting context
- `hamlib-integration-spike.md` — original Hamlib spike (April 28)
- `external-research/v71/TMV71A_CAT_PROTOCOL.md` — Doug's TM-V71A research
- `external-research/tmv71a-analysis-from-doug.md` — synthesis of Doug's research

---

## 14. The one-paragraph summary

JJ Flexible adds an `IRadioBackend` abstraction to host both `FlexLibBackend`
(for FlexRadio SDR transceivers, refactored from existing JJF radio code with
no behavior change for Flex users) and `HamlibBackend` (new code wrapping
`libhamlib-5.dll` for hundreds of CAT-controlled radios from Kenwood, Yaesu,
Icom, Elecraft, and others). The abstraction extends Jim's existing
`AllRadios` / `RigCaps` / `RadioSelection.RigTable` shape rather than replacing
it; capability flags drive UI rendering uniformly across all radio classes;
slices unify Flex's multi-receiver concept with Hamlib's VFOs; per-radio
config (keyed by `flex-<serial>` or `hamlib-<modelid>-<key>`) holds antenna
labels, ATU memories, and audio routing in
`%AppData%\JJFlexRadio\radios\<id>\`; an `IAudioRouter` plumbs Windows
soundcard devices into JJF's existing `RxAudioPipeline` so non-Flex radios
get the same NR + spectral sub treatment as Flex 6000-series radios; the
TS-2000 (Noel's primary cross-class testbed) gates conformance via ~80 tests
across 17 categories before we declare the architecture solid; tester
onboarding is documented end-to-end with accessibility callouts at every
step, including neuropathy considerations for Mark's TS-590G; the .NET WPF
accessibility moat is preserved (no UI-framework migration); and the full
implementation arc is 5-6 sprints, sequenced as IRadioBackend introduction →
HamlibBackend skeleton → audio routing → capability broadening → tester
onboarding → polish, with a small first sprint that validates the abstraction
on Flex alone before committing to Hamlib bringup.

---

## Document Status

**Phase 9 of 10 complete.** Synthesis ready for Noel's decision review.

**Estimated read time:** 25-30 minutes for full review; 12-15 minutes for
sections 1, 9, 10, and 11 alone (the load-bearing decisions and the
greenlight ask).

**Next deliverable:** Phase 10 handoff document — the standard
sprint-resume artifact that points implementation-time-Noel (or
implementation-time-Claude) to the right starting place.
