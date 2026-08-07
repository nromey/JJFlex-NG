# Multi-radio track — Hamlib backend + radio abstraction architecture

**Worktree:** `C:/dev/jjflex-multi-radio`
**Branch:** `track/multi-radio` (currently at `40a1d6ee`, off old Sprint 26 main — Phase 0 rebases onto current main)
**Mode:** autonomous research + architecture design; no production code lands; resumes after Track B (FlexLib upgrade) closes

## The strategic frame (read this before anything else)

This is not a feature track. This is the architectural response to **JJ Radio's user base folding into JJ Flexible**. JJ Radio supports a long tail of non-Flex radios via Jim's hand-rolled abstraction layer; those users' commitments transfer to JJ Flexible when the merge happens. Hamlib is the path to honoring that commitment without re-implementing every CAT protocol from scratch.

The track designs the architecture that lets JJ Flexible host **multiple radio backends** (FlexLib + Hamlib + future direct-CAT backends if needed) behind a single user-facing model. The work is research and design only at this stage. Implementation is a later sprint with its own track instructions, after Noel reviews this synthesis.

**Constraint that must hold:** the C#/.NET accessibility moat must not be sacrificed. Cross-platform UI rewrites (Qt6, etc.) are off the table — that's documented in `project_csharp_accessibility_moat.md`. Hamlib integration happens *inside* the .NET surface via P/Invoke or a managed shim, not by switching UI frameworks.

## Background reading — do all of this before Phase 1

Memory entries (read top to bottom — strategic context first, then concrete constraints):

1. `~/.claude/projects/c--dev-JJFlex-NG/memory/project_jj_radio_folding.md` — the strategic frame for the merge
2. `~/.claude/projects/c--dev-JJFlex-NG/memory/project_csharp_accessibility_moat.md` — the hard architectural constraint
3. `~/.claude/projects/c--dev-JJFlex-NG/memory/project_strategic_identity.md` — universal-design product thesis
4. `~/.claude/projects/c--dev-JJFlex-NG/memory/project_flexibility_principle.md` — "flexible" means literal user choice
5. `~/.claude/projects/c--dev-JJFlex-NG/memory/project_anti_patterns_from_blindcat.md` — accessibility anti-patterns to avoid
6. `~/.claude/projects/c--dev-JJFlex-NG/memory/project_per_radio_config_serial_keyed.md` — config storage architecture
7. `~/.claude/projects/c--dev-JJFlex-NG/memory/project_ts2000_cross_class_testbed.md` — Noel's TS-2000 testbed
8. `~/.claude/projects/c--dev-JJFlex-NG/memory/project_doug_tmv71a_tester.md` — Doug's TM-V71A
9. `~/.claude/projects/c--dev-JJFlex-NG/memory/project_kenwood_590g_commitment.md` — Mark's TS-590G
10. `~/.claude/projects/c--dev-JJFlex-NG/memory/project_smartsdr_plus_tester_access.md` — Don/Justin Plus access
11. `~/.claude/projects/c--dev-JJFlex-NG/memory/feedback_accessibility_is_end_to_end.md` — install/configure must be accessible

In-repo research (already committed on this branch — read all of it):

- `docs/research/at-scripting-research.md` — NVDA + JAWS scripting (also relevant to Track C's braille work)
- `docs/research/hamlib-integration-spike.md` — Hamlib architecture proposal from yesterday's autonomous session
- `docs/research/external-research/tmv71a-analysis-from-doug.md` — synthesis of Doug's research
- `docs/research/external-research/v71/TMV71A_CAT_PROTOCOL.md` — Doug's protocol survey

Live code that informs the abstraction:

- `Radios/FlexBase.cs` — the existing radio surface. An IRadioBackend abstraction has to be derivable from what FlexBase already does.
- `Radios/AllRadios.cs` — discovery, RigTable, and how radios get plumbed in.
- `Radios/Flex6300Filters.cs` — example of a model-specific UI; shows what gets exposed per model.
- `Radios/ScreenReaderOutput.cs` — accessibility surface that any new backend must integrate with.

External tooling:

- `c:/dev/hamlib/README.developer` — Hamlib's API model
- `c:/dev/hamlib/VFOs.txt` — Hamlib's VFO concept (compare with Flex's slice concept)
- `c:/dev/hamlib/NEWS` — recent Hamlib changes that might affect API stability assumptions

## Phase plan

Each phase produces a doc under `docs/research/`. Commit per phase: `Multi-radio Phase N: <description>`.

### Phase 0 — Rebase onto current main

The branch is currently based on old Sprint 26 main. Current main has Sprint 27, Sprint 28, and (likely by the time this track runs) the FlexLib 4.2.18 upgrade. The IRadioBackend abstraction has to be designed against the *current* FlexBase, not the Sprint 26 version.

```
git fetch origin main
git rebase main
```

If the rebase is non-trivial (the multi-radio commit only touches `docs/research/`, so conflicts are unlikely), pause and report rather than force-resolving. If the FlexLib upgrade significantly changed `FlexBase.cs`, factor that into the abstraction design.

Commit nothing in this phase — rebase moves history, no new commit needed.

### Phase 1 — JJ Radio scope inventory

**Output:** `docs/research/jj-radio-inventory.md`

The original JJ Radio source is in two places:
- Dropbox archive: `C:/Users/nrome/Dropbox/JJFlexRadio/old/` (installer + zipped source)
- GitHub: the original fork (Jim's repo, predecessor to KevinSShaffer's JJFlexRadio)

Survey:

- What radios does the current JJ Radio explicitly support? (Look at the equivalent of RigTable.)
- What feature surface per radio? (Frequency, mode, VFO selection, memory channels, scan, satellite, etc.)
- Who in our tester roster is using JJ Radio today vs JJFlex? Are there radios in JJ Radio that no current tester owns?
- What's the implementation pattern for adding a new radio in JJ Radio? (Per-radio class, abstract base, etc.)

The goal is to know what we're inheriting before we design what's replacing it.

### Phase 2 — Radio class taxonomy

**Output:** `docs/research/radio-class-taxonomy.md`

Group radios by capability class, not vendor:

- HF transceiver (Flex 6000/8000, Kenwood TS-590G, Yaesu FT-991, etc.)
- VHF/UHF FM mobile (Kenwood TM-V71A, etc.)
- Satellite-capable (TS-2000, IC-9100, etc.)
- All-mode all-band (TS-2000, IC-7100, etc.)
- SDR-architected (FLEX-* family — separate class because of slice concept)

For each class, define:

- The minimum viable IRadioBackend surface
- The accessibility surface (what controls matter to the user; how does navigation work)
- Per-class testbed mapping (which tester's radio exercises this class)

Cross-reference Noel's TS-2000 (covers HF + VHF + UHF + sat in one box → primary IRadioBackend conformance testbed), Doug's TM-V71A (VHF/UHF FM mobile), Mark's TS-590G (HF), Don/Justin/Noel's Flex radios (SDR-architected).

### Phase 3 — Hamlib API survey for our needs

**Output:** `docs/research/hamlib-api-survey.md`

Build on `docs/research/hamlib-integration-spike.md`. Drill into the specific Hamlib calls we need:

- Connection: `rig_init`, `rig_open`, `rig_close`, `rig_cleanup`, error model
- Frequency: `rig_set_freq`, `rig_get_freq`, VFO targeting
- Mode: `rig_set_mode`, `rig_get_mode`, supported modes per rig
- VFO: Hamlib's VFO concept and how it maps to Flex's slice concept (they aren't the same)
- PTT: `rig_set_ptt`, `rig_get_ptt`, latency model
- Memory channels: `rig_set_mem`, `rig_get_mem`, channel list ops
- Capabilities introspection: `rig_get_caps`, `RIG_LEVEL_*`, `RIG_FUNC_*`
- Async/event model: does Hamlib have callbacks, or is it pure poll?
- Audio: NOT in Hamlib's scope (it's CAT only) — flag this for Phase 5

P/Invoke surface: how does .NET call into libhamlib? Existing C# bindings (HamlibSharp, CSharp-Hamlib, etc.) — survey what's out there and whether any are usable.

### Phase 4 — IRadioBackend abstraction design

**Output:** `docs/research/iradio-backend-design.md`

The deliverable. Propose IRadioBackend's surface based on:

- What FlexBase exposes today (existing internal API surface)
- What Hamlib exposes (Phase 3)
- What JJ Radio's abstraction did (Phase 1)
- What the radio class taxonomy demands (Phase 2)

Open design questions:

1. **Slice vs VFO** — Flex's slice concept is richer than Hamlib's VFO. Does IRadioBackend expose slices and let non-slice radios fake N=1 slice? Or expose VFOs and let Flex represent slices as VFO+ports? (My instinct: expose slices generically and let VFO-only radios collapse to slice 0.)
2. **Capability negotiation** — how does the UI know whether to show "diversity" or "ATU" controls when those don't apply to all radios?
3. **Async model** — FlexLib is event-driven; Hamlib is poll-driven. Does IRadioBackend expose events, or does the implementation translate poll-to-event?
4. **Backend lifecycle** — connect/disconnect/reconnect. Hamlib's `rig_open` failure modes vs FlexLib's. What's the unified error model?
5. **Audio responsibility** — does IRadioBackend handle audio, or does the audio pipeline (NAudio + PortAudio) plug in beside it?

Synthesis with `hamlib-integration-spike.md` — the existing spike proposed an abstraction; reconcile or supersede it.

### Phase 5 — Per-radio config strategy

**Output:** `docs/research/per-radio-config-strategy.md`

`project_per_radio_config_serial_keyed.md` says radio-state-dependent config (TX controls, ATU memories, antenna definitions, future per-radio Customize Home) lives in `radios\<serial>\config.xml`. For Flex radios serial number is unique and stable. For Hamlib radios:

- Many CAT radios don't expose a serial via CAT
- Connection profile (model + COM port + baud rate + civ-address etc.) is the natural identity
- Multiple radios of the same model on different ports are a real use case

Propose a keying strategy that handles both Flex (serial) and Hamlib (connection profile) without forcing one into the other's mold.

### Phase 6 — Audio routing strategy for non-Flex radios

**Output:** `docs/research/audio-routing-non-flex.md`

Hamlib is CAT only. Non-Flex radios route audio via PC sound card (line in/out, USB CODEC, virtual audio cables). JJFlex's existing audio pipeline (NAudio + PortAudio + Opus) was built for Flex's network audio model.

Survey:

- What sound card discovery / selection UX makes sense?
- How does the user assign "this sound device is RX from radio X" / "this device is TX to radio X"?
- Per-radio audio routing in `radios\<key>\audio.xml`?
- Accessibility: device list as labeled options, screen-reader friendly, no graphical mixers.

### Phase 7 — TS-2000 conformance scope

**Output:** `docs/research/ts2000-conformance.md`

Noel's TS-2000 covers HF + VHF + UHF + satellite in one box. It's the primary cross-class testbed. Define the IRadioBackend conformance surface that the TS-2000 backend needs to pass:

- Frequency: HF + VHF + UHF
- Modes: SSB, CW, AM, FM, satellite cross-band
- Dual VFO (TS-2000 has main + sub receivers)
- Memory channel ops
- Satellite-mode VFO linking
- PTT
- Audio routing (sound card)

This becomes the smoke test for "did we design the abstraction right" — if the TS-2000 backend implements IRadioBackend cleanly, the abstraction is probably sound.

### Phase 8 — Tester onboarding strategy

**Output:** `docs/research/tester-onboarding.md`

How does each tester install, configure, and start using their backend?

- Don (FLEX-6300, SmartLink): existing path, no change
- Justin (FLEX-8400, SmartLink): existing path, no change
- Noel (TS-2000): new path. Hamlib install, CAT cable, sound card setup, connection profile creation, accessibility throughout
- Doug (TM-V71A): new path. Same as TS-2000 but with VHF/UHF FM specifics
- Mark (TS-590G, neuropathy): new path. Critical accessibility — neuropathy is the second axis beyond blindness; controls must be reachable without fine motor coordination

Per `feedback_accessibility_is_end_to_end.md`: install + configure + use + maintain ALL must be accessible. No "sighted setup, accessible runtime" interim — that's documented as a failure mode.

### Phase 9 — Architecture synthesis

**Output:** `docs/research/multi-radio-architecture-synthesis.md`

The single doc that pulls Phases 1-8 into a coherent architecture proposal. This is what Noel reads when deciding "do we greenlight an implementation sprint?"

Sections:

- Strategic frame (one paragraph, sourced from `project_jj_radio_folding.md`)
- Architecture diagram (text/prose only — no ASCII art per Noel's preferences)
- IRadioBackend surface (final form)
- Per-radio config schema
- Audio routing model
- Tester onboarding plan
- Sprint scope recommendations (probably 2-3 sprints minimum: backend abstraction → first Hamlib backend (TS-2000) → broaden to TM-V71A and TS-590G)
- Risks and open questions

### Phase 10 — Handoff document

**Output:** `docs/research/handoff.md`

Standard handoff format:

- What was done
- Key findings
- Recommended sprint scope
- Open questions for Noel
- Suggested first implementation sprint scope (small, focused — probably "introduce IRadioBackend, port FlexBase to it, no Hamlib yet")
- Where the implementation track should live (new worktree off updated main)

Commit: `Multi-radio Phase 10: Handoff document`

## Coordination

- **Inbox/outbox** — review-needed docs go to `docs/planning/inbox/` per `docs/planning/inbox/README.md`. Final handoff doc lands there.
- **No merge to main** — research lands only on `track/multi-radio` until Noel reviews.
- **No production code** — this branch must NOT touch `Radios/`, `JJFlexWpf/`, `main_app/`, etc. Read them; do not edit.
- **Independence from other tracks** — Track B (FlexLib) and Track C (braille) operate in their own worktrees. Don't touch.
- **Cross-track artifacts** — `at-scripting-research.md` on this branch is also relevant to Track C; if Track C produces refinements, the multi-radio track can reference them via `git show track/braille-research:<path>` after Track C's research lands.

## What NOT to do

- Don't write production code. Read-only on `Radios/` etc.
- Don't propose UI framework changes (Qt6, Electron, Tauri, etc.) — explicitly out of scope per accessibility moat memo.
- Don't redesign FlexBase — the abstraction wraps it, doesn't rewrite it.
- Don't gold-plate the Hamlib integration. Initial scope: TS-2000 + TM-V71A + TS-590G. Other Hamlib radios come later.
- Don't open PRs or push to origin. Local commits only.
- Don't use `BlindCat` or competitor product names in any document — describe behaviors, not source product names (per `feedback_dont_name_competitor_in_repo.md`).
- Don't pace the work for human fatigue — this is autonomous research, run all phases contiguously.

## Resume notes

If the session ends mid-phase, leave a "RESUME HERE" marker in the most recent commit and update `docs/research/progress.md`. Standard sprint-resume protocol.

## Tools

WebFetch and WebSearch are available via the deferred-tool mechanism (use ToolSearch to load schemas). They will be useful for surveying GitHub for existing C#/.NET Hamlib bindings and reading Hamlib's online developer docs. Memory entries are accessed by absolute path through `Read`.

## Scoping reminder

This is research and architecture. The output is a stack of design documents and a synthesis proposal. It is **not** a working multi-radio backend. The greenlight for implementation comes from Noel after he reads `multi-radio-architecture-synthesis.md` and `handoff.md`. Don't try to build the thing here.
