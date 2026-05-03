---
type: discovery / pointer doc — substantial existing multi-radio research found
review needed: be aware before reading the multi-radio + braille read pack
priority: high — reframes the multi-radio reading from "design from scratch" to "synthesize existing work"
---

# Multi-radio research already exists — pointer doc (2026-05-02)

While processing the Hamlib spike copy you requested, I discovered a substantial body of multi-radio research in `C:\dev\jjflex-multi-radio\docs\research\`. This is the worktree from earlier multi-radio work (Track B from a prior sprint). It contains:

- `hamlib-integration-spike.md` (32 KB) — copied to `for-noel/2026-05-02-hamlib-integration-spike-FROM-WORKTREE.md` for direct reading per your request
- `hamlib-api-survey.md` — broader API survey beyond just integration
- `iradio-backend-design.md` — direct design for the IRadioBackend interface
- `multi-radio-architecture-synthesis.md` — the synthesis doc
- `radio-class-taxonomy.md` — the radio class taxonomy I flagged as "future doc" in the multi-radio read pack — **already exists**
- `per-radio-config-strategy.md` — per-radio config approach
- `jj-radio-inventory.md` — inventory of JJ Radio's existing surface
- `at-scripting-research.md` — assistive-technology scripting research
- `audio-routing-non-flex.md` — audio routing for non-Flex radios
- `tester-onboarding.md` — tester ramp-up doc
- `ts2000-conformance.md` — TS-2000 conformance test work
- `external-research/` subdirectory — likely Doug's TM-V71A research per memory

## What this means for the multi-radio + braille read pack

The read pack I wrote this morning (`for-noel/2026-05-02-multi-radio-braille-read-pack.md`) framed the architectural questions as "form a position before the working session" — which IS still the right frame, but the read pack didn't know about the worktree's existing research. The reality is:

**Most of the Track A (multi-radio) reading is in this worktree, not in memory.** The memos in memory (`project_jj_radio_folding.md`, `project_kenwood_590g_commitment.md`, etc.) are STRATEGIC frame; the worktree docs are the IMPLEMENTATION-LEVEL research that flows from that frame.

When you read multi-radio tomorrow, the suggested order is:

1. **Read the strategic memos first** (per the read pack): `project_jj_radio_folding.md`, `project_kenwood_590g_commitment.md`, `project_ts2000_cross_class_testbed.md`, `project_doug_tmv71a_tester.md`, `project_csharp_accessibility_moat.md`, `project_per_radio_config_serial_keyed.md`. ~30 min.
2. **Then the worktree research** in this order:
   - `multi-radio-architecture-synthesis.md` — the synthesis (start here for the rolled-up view)
   - `iradio-backend-design.md` — the proposed interface design
   - `radio-class-taxonomy.md` — the class taxonomy
   - `hamlib-integration-spike.md` — Hamlib-specific integration approach (NOW in for-noel as a copy)
   - `per-radio-config-strategy.md` — config approach
   - Skim others as time/interest allows
3. **Form positions** on the read pack's questions WITH the worktree research as context — most of the questions probably already have proposed answers in the worktree docs.

## Implications for the working session

Once you've read both layers, the working session is "review what's been proposed in the worktree research, override anything you disagree with, and ACK the synthesized position." That's much faster than designing from scratch.

I should have surfaced this worktree existence in the read pack from the start. Catching it now means tomorrow's reading is shorter and more concrete — you're reviewing existing work, not greenfield-designing.

## After the reading + working session

Implementation track (`track/multi-radio-foundation`) starts from the worktree's existing skeleton plus your reviewed-and-blessed architecture. Build-now-ship-later authorization is already in place per your ACK on the JJ Radio folding pull doc.
