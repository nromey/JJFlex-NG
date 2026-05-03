---
type: reading list — read these memos and decide
review needed: form a position on each, drop reactions in for-claude
priority: high — your reading unblocks two parallel dev tracks today
---

# Multi-radio + braille read pack

This is your reading queue for the two parallel dev tracks you flagged this morning. **You said: "we can probably proceed on multi-radio and braille once I read the info."** Reading these memos in the suggested order should give you enough context to weigh in.

You don't need to read everything in one sitting. Read at your pace, drop reactions in `../for-claude/` (could be a single file: `2026-05-02-multi-radio-braille-feedback.md` with sections per memo).

## Track A — Multi-radio architecture

### Required reading (in order)

1. **`memory/project_jj_radio_folding.md`** — strategic frame. JJ Radio's user base migrates to JJ Flexible; multi-radio commitment is inherited, not optional. "As many Hamlib rigs as possible." This is the WHY for everything else in this track.

2. **`memory/project_kenwood_590g_commitment.md`** — first concrete non-Flex commitment. Mark, neuropathy, suffering through BlindCat. Individual user, individual radio.

3. **`memory/project_ts2000_cross_class_testbed.md`** — your TS-2000 as the cross-class testbed. All-mode all-band; exercises every Hamlib code path. Primary IRadioBackend conformance testbed.

4. **`memory/project_doug_tmv71a_tester.md`** — Doug operates a TM-V71A; emailed his Claude-driven Hamlib TM-V71A research on 2026-04-28 (filed under `docs/planning/track-b/external-research/v71/`). Primary VHF/UHF FM mobile tester.

5. **`memory/project_csharp_accessibility_moat.md`** — architectural constraint. .NET / WinForms / WPF / UIAutomation gives accessibility for free. Multi-radio cannot trade this away for "cross-platform" if it means a Qt-style accessibility ceiling.

6. **`memory/project_per_radio_config_serial_keyed.md`** — config layer principle. Per-radio state lives in `radios\<serial>\config.xml`. Multi-radio architecture inherits this.

### Position-forming questions

While reading, work toward a position on:

- **What's the IRadioBackend interface look like?** Minimum viable surface that supports Flex 6000/8000 (today) + Kenwood TS-2000 (your testbed) + TS-590G (Mark) + TM-V71A (Doug). Hamlib-shaped or JJ-Flex-shaped?
- **First non-Flex target.** TS-2000 (your testbed, broadest coverage) or TS-590G (Mark, urgent user)? Build-order decision.
- **Capability-discovery model.** Today's `theRadio.MaxSlices` / `theRadio.DiversityIsAllowed` pattern works for Flex but doesn't generalize. What's the multi-radio version?
- **Radio class taxonomy.** Per `project_jj_radio_folding.md`, "architecture must accommodate full radio class taxonomy from day one, not bolt-on per radio." What ARE the classes? HF transceiver / VHF-UHF mobile / handheld / SDR / scanner / receiver-only?
- **Hamlib integration depth.** Direct C-API binding via P/Invoke? rigctld TCP socket? Or a JJ-Flex-native abstraction with Hamlib only as one backend?

### Track A working session shape

Once you've read the above, reactions in for-claude. Then we have a working session (chat or full Claude-driven) where I draft a multi-radio architecture memo capturing your decisions. That memo lands in for-noel for ACK. Once ACK'd, I spin up `track/multi-radio-foundation` worktree and start the IRadioBackend interface + first non-Flex backend skeleton. **Build-now-ship-later** — branch lives independently of 4.2.0 release scope per `memory/project_build_now_ship_later.md`.

## Track B — Braille primitive (BrailleElement v1)

### Required reading (in order)

1. **`memory/project_jamie_teh_braille_commitment.md`** — context. Jamie Teh (NVDA core dev + OSARA maintainer) reached out 2026-04-29; you committed to deliver a cross-AT braille primitive. Track C is no longer speculative — it's an in-flight commitment. Email outreach when ready, not GitHub-public.

2. **`memory/project_braille_primitive_v1_decisions.md`** — decisions already locked: name `brailleElement`, standalone repo from day one, license deferred, opt-in cursor indicator pulled into v1, polling watchdog alongside event-driven focus dismissal.

3. **`memory/project_multi_braille_output_vision.md`** — multi-channel braille output (Dot Pad + Focus 40 in parallel with linear status line). Three-channel tactile + audio output is unprecedented in ham radio software. Shapes Sprint 26+ waterfall sink design from day one.

4. **`docs/planning/braille-verbosity-design.md`** — earlier braille design memo, predates the verbosity-channel split. Read for context but expect inconsistency with the newer thinking.

5. **`memory/project_verbosity_architecture_proposal.md`** (the version we updated this morning) — verbosity-channel framing. Braille is a parallel channel; brailleElement v1 needs to interoperate with this framing.

### Position-forming questions

While reading, work toward a position on:

- **API surface for `brailleElement` v1.** What's the minimum API any text-rendering app would call to send content to braille? `Set(string)` / `SetWithCursor(string, int)` / `Clear()` is the obvious starting point. What else?
- **NVDA add-on shape.** Standalone NVDA add-on or extension to OSARA? Jamie Teh would have opinions; what's your initial position?
- **Hardware coverage.** Focus 40 / Brailliant / Dot Pad / others? What's the v1 supported list, what's deferred?
- **Repo + license.** Standalone repo per the v1 decisions memo — name it `brailleElement` under `nromey/` or under a new `jjflexible/` GitHub org? License deferred per memo, but is there a placeholder we ship while undecided?
- **First sender app.** JJFlex sends to brailleElement? OSARA sends? Both? Who's the validator that proves the API works?

### Track B working session shape

Once you've read the above, reactions in for-claude. Working session refines the v1 API spec, drops it in for-noel for ACK. Once ACK'd, I spin up the standalone `brailleElement` repo (or worktree) and start v1 implementation. Same **build-now-ship-later** posture — Jamie's design feedback can come during or after v1 lands.

## Suggested reading order

Track A (multi-radio) first if you want to get into engineering mode; Track B (braille) first if you want to engage with the cross-AT contribution angle. Both roughly 30-45 minutes of reading each. They can be read in parallel days if today is too packed.

## After reading

Drop your reactions in `../for-claude/` as a single file (suggest `2026-05-02-multi-radio-braille-feedback.md`) with two top-level sections (Track A / Track B). Use `**** ` for inline answers in the position-forming questions above.

If a question doesn't have a clear answer yet — `**** SKIP` is fine; we'll work it out in the working session.
