# Multi-Radio Track — Handoff Document

**Status:** Multi-radio Phase 10 deliverable — sprint-resume artifact
**Date:** 2026-04-29
**Author:** Claude Opus 4.7 (track/multi-radio)
**Audience:** Implementation-time-Noel and implementation-time-Claude (whichever
of "us" picks up after this research lands)

---

## What was done

This branch (`track/multi-radio`) contains **9 research deliverables** that
collectively design the multi-radio backend architecture for JJ Flexible. The
trigger was the strategic decision documented in `project_jj_radio_folding.md`
(2026-04-28) to fold JJ Radio's user base into JJ Flexible, inheriting the
commitment to support a long tail of non-Flex CAT-controlled radios. The work
extends the prior April-28 Track B Hamlib spike with the breadth and concrete
design proposals needed to greenlight an implementation sprint.

Phase-by-phase summary:

- **Phase 0** — Rebased onto current `main` (Sprint 27 + 28 work, FlexLib 4.2.18
  TODO). No conflicts.
- **Phase 1** — `jj-radio-inventory.md`. Inventory of what JJ Radio supported
  (Kenwood TS-2000 / TS-590S / TS-590SG, Icom IC-9100, Generic CAT) and the
  abstraction shape Jim left in our codebase (`AllRadios` + `RigCaps` +
  `RadioSelection.RigTable`).
- **Phase 2** — `radio-class-taxonomy.md`. Five-class taxonomy (SDR, HF
  transceiver, all-mode all-band, VHF/UHF FM mobile, plus provisional HT and
  RX-only) with concrete representatives, distinguishing capabilities, and
  tester mapping.
- **Phase 3** — `hamlib-api-survey.md`. The ~50 Hamlib calls JJF needs,
  P/Invoke pattern sketch, threading/async model, introspection-driven
  capability mapping.
- **Phase 4** — `iradio-backend-design.md`. The central interface proposal —
  capability+facts+state model, slice abstraction unifying Flex slices with
  Hamlib VFOs, connection lifecycle, threading model, registration mechanism,
  legacy bridge plan.
- **Phase 5** — `per-radio-config-strategy.md`. Radio-id keying scheme
  (`flex-<serial>` / `hamlib-<modelid>-<serial-or-nickname>`), directory
  layout, JSON file schemas, migration plan.
- **Phase 6** — `audio-routing-non-flex.md`. `IAudioRouter` interface,
  accessible setup dialog, integration with existing `RxAudioPipeline` (so
  Mark's TS-590 gets PC-side NR like Flex 6000-series radios).
- **Phase 7** — `ts2000-conformance.md`. ~80 conformance tests across 17
  categories on Noel's TS-2000 — the smoke test for the whole architecture.
- **Phase 8** — `tester-onboarding.md`. End-to-end onboarding paths for Don,
  Justin, Noel (TS-2000), Mark (TS-590G with neuropathy considerations), Doug
  (TM-V71A with hardware flow control trap).
- **Phase 9** — `multi-radio-architecture-synthesis.md`. The synthesis Noel
  reads when deciding to greenlight implementation. 5-6 sprint arc proposed,
  starting with a small first sprint that validates the abstraction on Flex
  alone.

All commits are local on `track/multi-radio`. Nothing has been pushed; nothing
has been merged. No production code (`Radios/`, `JJFlexWpf/`, `main_app/`)
was modified — read-only on the live tree per track instructions.

---

## Key findings

**The architecture extends rather than replaces.** Jim's `AllRadios` +
`RigCaps` + `RadioSelection.RigTable` is still in our codebase and still
sound — the deletion in commit `d8b67758` removed non-Flex implementations
but left the abstraction shape. The new `IRadioBackend` is an evolution of
this shape, not a green-field replacement. This means the implementation
arc is bounded; the unknown is the new code (HamlibBackend), not the
established code (FlexLibBackend / `AllRadios`).

**The TS-2000 is the architecture's truth-teller.** Noel owns one, Hamlib
supports it deeply, and it spans HF + VHF + UHF + satellite + dual-receiver.
The Phase 7 conformance list (~80 tests) is the architecture's smoke test —
if HamlibBackend operating a TS-2000 passes, broader Hamlib coverage is
incremental; if it fails, the failures identify gaps before we commit to
TS-590 / TM-V71A bringup.

**Hamlib's `bindings/csharp/` is empty.** The directory exists but only
contains a multicast helper unrelated to libhamlib. Hand-rolled P/Invoke for
~50 calls (Path A) is the right first move; SWIG-based bindings (Path B) come
later when surface area justifies the toolchain investment.

**Capability flags drive UI uniformly.** The UI never branches on radio
class — only on capability flags. Class is metadata for diagnostics and
config defaults. This pattern works across Flex (FlexLibBackend), Hamlib
(HamlibBackend), and any future backend, because the UI is one set of
rendering rules gated by one set of flag-checks.

**Audio routing is its own concern.** Hamlib does CAT only. JJF's
`IAudioRouter` plus the existing audio pipeline (NR + spectral sub) extends
to non-Flex radios, with Mark's TS-590G getting the same PC-side NR
treatment Don's 6300 gets — a moat-grade benefit broadened beyond Flex.

**Per-radio config is keyed by stable radio-id.** Flex radios use
`flex-<serial>`; Hamlib radios use `hamlib-<modelid>-<serial-or-nickname>`
with model-id guarding against radio-swap config bugs. JSON files in
`%AppData%\JJFlexRadio\radios\<radio-id>\`. Migration from today's user-
scope-with-radio-state-mixed-in is incremental, not bulk.

**Onboarding accessibility matters as much as runtime.** Per
`feedback_accessibility_is_end_to_end.md`, the connection-config dialog,
audio device picker, and "Test Receive / Test Transmit" diagnostics all
must be screen-reader-friendly. The dialogs are described in Phase 8 with
explicit accessibility callouts — these are not afterthoughts.

---

## Recommended sprint scope

The synthesis (Phase 9 §9) proposes 5-6 implementation sprints. The
**first** implementation sprint is the small one Noel should consider
greenlighting:

### Sprint Multi-1 — Introduce IRadioBackend (small, low-risk)

**Scope:**
- Define `IRadioBackend` interface and supporting types in
  `Radios/IRadioBackend.cs`, `Radios/RadioCapability.cs`, etc.
- Refactor `FlexBase` / `AllRadios` to implement the interface (existing
  behavior preserved exactly; new interface is a wrapper).
- Begin migrating UI code that consumes `theRadio` to consume
  `currentBackend` typed `IRadioBackend`. Initially via casts where needed;
  gradual migration over subsequent sprints.

**Out of scope:**
- HamlibBackend (Sprint Multi-2)
- Audio routing changes (Sprint Multi-3)
- New radio support (Sprint Multi-2+)

**Exit criteria:**
- JJF works identically for Don, Justin, Noel-on-Flex
- All existing UI tests pass
- The new abstraction layer exists and is consumable
- Sprint Multi-2 is unblocked

**Why this is the right first commit:** the refactor proves the abstraction
without committing to Hamlib bringup. If the abstraction has design issues,
they surface here at low cost. If it's clean, every subsequent sprint
builds on solid foundation.

**Estimated implementation effort:** medium. The `Radios/` codebase is large
(~70 files) but the refactor is mechanical (introduce interface, retrofit
existing classes). Not all callsites need to migrate in this sprint —
backward-compatible bridge during the transition.

### Sprint Multi-2 — HamlibBackend skeleton (next)

After Sprint Multi-1 confirms the abstraction. Bundles `libhamlib-5.dll`,
hand-rolls P/Invoke for ~50 calls, implements `HamlibBackend` for connection
lifecycle + frequency / mode / PTT only. Audio remains absent — Noel can
connect to TS-2000 and send/receive CW commands silently.

### Sprint Multi-3 through Multi-6

Audio routing → capability broadening → tester onboarding → polish.
Each sprint is bounded; per-sprint exit criteria in Phase 9 §9.

---

## Open questions for Noel

These are the must-decide items before implementation starts. The synthesis
proposes recommendations; Noel commits or counters.

1. **Greenlight Sprint Multi-1?** Y/N. If Y, what calendar slot? (After
   4.1.17 release? Parallel to waterfall arc? Specific sprint number?)
2. **Confirm radio-id format** (`flex-<serial>` /
   `hamlib-<modelid>-<key>`).
3. **Confirm Path A first for Hamlib bindings** (hand-rolled P/Invoke,
   migrate to SWIG later).
4. **Confirm Hamlib bundling** (ship `libhamlib-5.dll` in installer; LGPL
   compliant).
5. **Confirm streaming surfaces are separate optional interfaces**
   (`IPanadapterStream`, `IWaterfallStream`, etc.).
6. **Tester acquisition pacing.** When JJ Radio's user base starts
   migrating, who comes online when? (Probably after Sprint Multi-5
   stability, but worth a plan.)
7. **Naming.** `IRadioBackend` proposed. Counters? (`IRadioController`,
   `IRadioBridge`.)
8. **Cross-platform direction.** Synthesis preserves the .NET moat. The
   parallel-native-UI path (SwiftUI iOS, Compose Android) is alongside.
   Confirm.

---

## Suggested first implementation sprint scope

Concretely, what Sprint Multi-1 looks like as a sprint plan:

### Sprint Multi-1 phases

**Phase 1.1 — Define `IRadioBackend` and supporting types**

- `Radios/IRadioBackend.cs` — interface
- `Radios/IRadioFacts.cs` — facts struct
- `Radios/RadioCapability.cs` — capability enum (initial set; extend per Phase 4)
- `Radios/SliceState.cs` — slice state record
- `Radios/ConnectionConfig.cs`, `Radios/ConnectResult.cs` — connection types
- `Radios/RadioMode.cs`, `Radios/Band.cs`, etc. — supporting enums

Mostly type definitions; no behavior. Build cleanly; no callsites yet.

**Phase 1.2 — `FlexLibBackend` adapter wrapping existing `FlexBase`**

- New file `Radios/Backends/FlexLibBackend.cs` implementing `IRadioBackend`
- Internally holds a `FlexBase` instance and translates calls
- Capability flag population from FlexLib + FeatureLicense state
- Streaming-surface properties return `null` when not exposed (initially
  return wrapper types that delegate to existing FlexLib events)

**Phase 1.3 — Catalog and registration**

- `Radios/IRadioBackendCatalog.cs` interface
- `Radios/Backends/RadioBackendCatalog.cs` implementation
- `RadioSelection.RigTable` becomes the seed for FlexLib registrations
- Hamlib registrations slot in later (Sprint Multi-2)

**Phase 1.4 — UI migration begin**

Pick ONE key UI surface (e.g., Frequency Display or Slice Selector) and
migrate to consume `IRadioBackend`. Validates the interface shape against
real callsite needs. Other UI keeps consuming `theRadio` directly via
backward compat.

**Phase 1.5 — ConnectionProfiler generalization**

`Radios/ConnectionProfiler.cs` currently hooks FlexLib events. Generalize
to consume `IRadioBackend` events. This sets up Sprint Multi-2 to feed
non-Flex connection diagnostics into the same profiler.

**Phase 1.6 — Smoke test**

- All Flex testers (Don, Justin, Noel) confirm normal operation
- No behavior regressions
- Sprint Multi-2 is unblocked (the abstraction exists for HamlibBackend
  to plug into)

**Phase 1.7 — Documentation**

- Update CLAUDE.md mentioning the abstraction layer
- Architecture sketch in `docs/planning/agile/sprint-multi-1-plan.md`
- This handoff doc references Sprint Multi-1's plan as the next
  resume point

**Estimated sprint phases:** 7. Each phase is a commit; sprint runs
serially.

---

## Where the implementation track should live

This research branch is `track/multi-radio`. **It should be merged to
`main` as docs-only** before implementation starts, so:

- The research is durable in main's history
- Implementation track can branch off main fresh
- The branch hasn't accumulated Sprint 27/28 changes that complicate the
  merge

The implementation track itself lives in **a new worktree off updated main**
when Sprint Multi-1 starts. Suggested naming:

- New branch: `sprint-multi-1/iradio-backend` (or similar)
- New worktree: `../jjflex-multi-1` (matches existing convention `../jjflex-Nx`)
- TRACK-INSTRUCTIONS.md in the new worktree pointing to this handoff doc
  + Phase 4 (`iradio-backend-design.md`) + Phase 7 (`ts2000-conformance.md`)
  as the implementation references.

---

## Files in this branch

All under `docs/research/`:

```
docs/research/
├── at-scripting-research.md                         (pre-existing — Track B AT scripting)
├── hamlib-integration-spike.md                      (pre-existing — April 28 spike)
├── jj-radio-inventory.md                            (Phase 1 — new this track)
├── radio-class-taxonomy.md                          (Phase 2 — new)
├── hamlib-api-survey.md                             (Phase 3 — new)
├── iradio-backend-design.md                         (Phase 4 — new, central proposal)
├── per-radio-config-strategy.md                     (Phase 5 — new)
├── audio-routing-non-flex.md                        (Phase 6 — new)
├── ts2000-conformance.md                            (Phase 7 — new)
├── tester-onboarding.md                             (Phase 8 — new)
├── multi-radio-architecture-synthesis.md            (Phase 9 — new, synthesis)
├── handoff.md                                       (Phase 10 — this file)
└── external-research/
    ├── tmv71a-analysis-from-doug.md                 (pre-existing — Doug research synthesis)
    └── v71/
        ├── CLAUDE.md
        └── TMV71A_CAT_PROTOCOL.md                   (Doug's protocol research)
```

A copy of `handoff.md` will land at `docs/planning/inbox/multi-radio-handoff.md`
per the inbox convention (final handoff goes to inbox for Noel review).

---

## Reading order for someone picking up cold

If you're (re)starting from scratch and have time to read everything:

1. `multi-radio-architecture-synthesis.md` (Phase 9) — start here, 25-30 min
2. `iradio-backend-design.md` (Phase 4) — the central proposal, 30-40 min
3. `ts2000-conformance.md` (Phase 7) — what success looks like, 12-15 min
4. `tester-onboarding.md` (Phase 8) — the human side, 18-22 min
5. The remaining phase docs as needed for specific questions

If you have less time, just read:

- Phase 9 synthesis (the big picture)
- Phase 9 §11 (the decision being asked of Noel)

Total minimum reading: ~30 minutes for the synthesis alone.

---

## When this gets greenlit

Update `Agent.md` with the Sprint Multi-1 kickoff. Add memory entries:

- `project_multi_radio_implementation_kickoff.md` — captures the
  greenlight, date, scope, key decisions made on the open questions above
- Update existing memory `project_jj_radio_folding.md` and
  `project_csharp_accessibility_moat.md` to reference the implementation
  arc

Create new sprint plan at `docs/planning/agile/sprint-multi-1-<ham-words>.md`
(per project conventions).

---

## Document Status

**Phase 10 of 10 complete.** Track `track/multi-radio` is research-complete.
Awaiting Noel review and greenlight decision.

**This is the last commit on the track until further direction.**

If Noel greenlights: branch into a new implementation worktree per the
"Where the implementation track should live" section.

If Noel needs revisions: feedback against specific phases, re-issue
revised docs on this branch, re-handoff.

If Noel says "later": branch can stay as research; merge to main as docs-
only when convenient so the work is durably in history.

**Estimated read time of this handoff:** 8-10 minutes.

---

## Track totals

- Phases completed: 10 of 10 (counting Phase 0 rebase)
- Research documents authored: 9 (Phases 1-9)
- Plus this handoff: 10
- Lines of new documentation: roughly 5,500 lines across all phases
- Production code modified: zero
- Memory entries written: zero (memory updates happen at greenlight time)
- Commits on this branch: 11 (Phases 0-10 sealed; rebase didn't add a new
  commit)
- Branch state: clean, all commits local, ready for review or merge
