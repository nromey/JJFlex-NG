# Sprint 28 Bug Bundle — Open Questions

**Type:** decision pull (3 questions, one already auto-resolved during the same session — net 2 active)
**Urgency:** non-blocking — bug-bundle CLI session is working the seven scoped bugs; these questions only affect *optional scope additions* and one re-verify. Answer whenever (post-recovery is fine).
**Full context:** `docs/planning/active/sprint28-bug-bundle-triage.md` (working dashboard with the full 14-entry triage from the 2026-04-27/28 testing session, status of every bug, recommended schedule, two-merge-event plan).

---

## Background, in 60 seconds

The 2026-04-28 morning briefing flagged "12 bugs" from the late-night iterative testing session. Triaged 2026-05-04:

- **3 already FIXED** (KEY CONFLICT Ctrl+F, F2 Home prefix, Help dialog Escape) — shipped between 2026-04-28 morning sweep and the evening seal.
- **2 IN FLIGHT** (modal escape + slice-acquisition state) — collapsed into `track/stuck-modal-escape`, **already merged to sprint28/home-key-qsk earlier today** (commit `de239328`, build clean, pushed to origin).
- **2 connect-time investigations** (AS-retry janky reconnect, post-overhaul connect time) — Sprint 29 scope, gated on trace persistence.
- **7 polish bugs** in `track/sprint28-bug-bundle` — **CLI session COMPLETE 2026-05-04**, all 7 bugs shipped on `track/sprint28-bug-bundle`. Awaiting orchestrator merge to sprint28/home-key-qsk.
- **2 DESIGN entries from same briefing — DEFERRED on Q2 below.** RunsWithoutRadio per-command opt-out + action-aware no-radio announcement. NOT yet implemented; will spin up as code work after your Q2 answer.

Both `track/stuck-modal-escape` and `track/sprint28-bug-bundle` branched from `sprint28/home-key-qsk` (pre-4.2.0 baseline) per the testability-gating principle in `memory/project_independent_merge_events.md`. They merge to main as a foundation drop *before* the 4.2.0 work — main stays the 4.1 working branch until the three 4.2 ship gates clear, per `memory/project_main_branch_41_posture.md`.

---

## Questions

### 1. (RESOLVED — answered 2026-05-04 in chat)

////Spin up `track/sprint28-bug-bundle` now?//// → **YES, and you ran it.** Worktree at `C:\dev\jjflex-bug-bundle` with TRACK-INSTRUCTIONS.md is live; CLI session is working the seven bugs as of this writing.

---

### 2. The two DESIGN entries from the same 2026-04-28 briefing — fold into the bundle?

**Context:** in the 2026-04-28 morning briefing's response to the no-radio guard work, you raised two design points that ARE actionable but were never spec'd:

**(a) No-radio guard per-command opt-out — `RunsWithoutRadio` flag on `KeyTableEntry`.**

Lets `Ctrl+F` (SetFreq) open the freq-input dialog with no radio so easter-egg input (`cqtest`, etc.) and just-typing-a-frequency-to-remember-it still work. The dialog handler then decides what to do with the input — apply if radio is connected, easter-egg-process if matches, otherwise speak "no radio, can't tune" at apply time.

Estimated effort: ~30-50 LOC. Files: `Radios/KeyCommandTypes.cs`, `JJFlexWpf/KeyCommands.cs:107` and `:1677-`, `globals.vb` `WriteFreq` handler.

**Sub-decision needed if you say yes:** which OTHER commands opt in to `RunsWithoutRadio = true`? My suggested candidates:
- `SetFreq` — yes (the original ask)
- `ShowMemory` — view saved memories without a connected radio
- Anything else? Most other Radio-scope commands actually need a radio (band keys, tune, RIT/XIT toggle, etc.).



**(b) Action-aware no-radio announcement.**

Replace the generic `"JJ Flexible Home, no radio connected"` with `"Unable to [change band], JJ Flexible Home no radio connected"` — pulls the action label from the `KeyTableEntry.ShortActionLabel` field already populated for ~80 commands per `memory/project_short_action_labels_vocabulary.md`.

Estimated effort: ~50 LOC. Files: `Radios/ScreenReaderOutput.cs` (extend `SpeakNoRadioConnected` to take optional action label), `JJFlexWpf/KeyCommands.cs:1677-` (pass the label through).

This pairs naturally with the Short Action Labels vocabulary work that's already designed but not yet shipped — labels exist; just route them to the no-radio announcement.



**Three answer shapes:**

- **YES to both** — fold (a) and (b) into the current bug-bundle CLI session. Adds ~80-100 LOC; bundle grows from ~195 to ~295 LOC. Bundle CLI session would surface this scope expansion next time it checks in.
- **YES to one, NO to the other** — pick whichever feels right. (a) is more user-visible (lets workflows continue without radio); (b) is more polish-tier (better announcement).
- **NO to both** — keep the bundle scoped to the original seven bugs. Both DESIGN entries slip to a separate post-bundle track or to 4.2.0.x.

**My recommendation:** YES to both. (a) is correcting a regression you introduced in the no-radio guard work; (b) is small, valuable, and pairs with existing vocabulary infrastructure. Together ~80-100 LOC fits comfortably with the bundle.



---

### 3. (AUTO-RESOLVED 2026-05-04 by the bug-bundle CLI session)

////PlayCwSK `ee` close drop with no radio — should we just delete the TODO entry?//// → **The CLI session ran the re-verify and the bug WAS still reproducible.** Commit `564e9333` ("Bug bundle: PlayCwSK ee close fires on all exit paths") fixed it. Your morning-of-2026-04-28 partial retraction was based on the with-radio path which DID work; the no-radio and connect-then-disconnect paths were dropping the EE. All three paths now produce the EE consistently. No further decision needed; entry will be removed from JJFlex-TODO.md as part of merge bookkeeping.



---

## What happens after you answer

- **Q2 yes-shape** → I spin up `track/sprint28-bug-bundle-design-followup` from `sprint28/home-key-qsk` (the bug-bundle CLI session has already finished and committed; for the deferred DESIGN items I'll restart in the same worktree at `C:\dev\jjflex-bug-bundle` rather than make a new one — the bundle branch is already shaped for this kind of polish work). New TRACK-INSTRUCTIONS.md will scope just (a) and/or (b) per your answer. Estimated ~80-100 LOC if both, ~30-50 if one.
- **Q3** → already auto-resolved by the CLI session per Q3 above.
- **Either decision** → I move this doc to `for-claude/` for processing per the for-noel/for-claude protocol (annotated copy goes to for-claude/, then Claude files it to its permanent home and deletes).

**If Q2 is "no to both":** the two DESIGN entries slip to a 4.2.0.x patch or 4.2.0 (carried forward via main-line merge); the JJFlex-TODO.md entries stay as-is, marked with the DEFERRED-pending-decision status. They are NOT lost.

If you want to skip a question, write `**** SKIP` on its line — it'll move on without it.

---

## Cross-references

- `docs/planning/active/sprint28-bug-bundle-triage.md` — full triage of all 14 entries
- `docs/planning/vision/JJFlex-TODO.md` — the source bug entries with root-cause analysis (lines 34-380 for the 2026-04-28 batch)
- `memory/project_stuck_modal_escape_design.md` — sibling track design (already merged today)
- `memory/project_independent_merge_events.md` — the two-merge-events principle
- `memory/project_main_branch_41_posture.md` — main = 4.1 line until 4.2 ship gates clear
- `memory/project_short_action_labels_vocabulary.md` — the labels infrastructure question (b) builds on
