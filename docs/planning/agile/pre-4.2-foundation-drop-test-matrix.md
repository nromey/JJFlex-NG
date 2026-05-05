# Pre-4.2.0 Foundation Drop Test Matrix — 2026-05-04

**Tested by:** Noel Romey (K5NER)
**Date:** ___
**Build:** Debug x64, version `4.1.16.0`, branch `sprint28/home-key-qsk`, commit `ee8faebb`
**Screen Reader:** NVDA primary, JAWS secondary
**Radio(s):** Noel's own rig primary; Don's FLEX-6300 "6300 inshack" via SmartLink where noted

---

## What's in this build

Two tracks merged into `sprint28/home-key-qsk` 2026-05-04:

- **Track 1: Stuck-modal escape** (commit `de239328`) — state-aware connecting modal with always-honored Escape, 60-second escalation, 5-minute hard auto-cancel, 73-Morse-twice fix. ~275 LOC across 5 files.
- **Track 2: Bug bundle** (commit `ee8faebb`) — 7 polish fixes from the 2026-04-27/28 testing session (CW prosign pause, Squelch Level skip, universal Home keys no-radio guard, Slice/Slice Operations naming, Classic/Modern field-order parity, MainContent dialog-close focus, PlayCwSK ee on all exit paths). ~187 LOC across 4 files.

Both tracks branched from `sprint28/home-key-qsk` (pre-4.2.0 baseline, FlexLib 4.1.5) per `memory/project_main_branch_41_posture.md`.

---

## How to Use This Matrix

Same convention as the 4.1.17 matrix — user-flow organised, tester-availability tagged, PASS/FAIL/DEFER recorded inline.

### Tester Availability Tags

- `[S]` — Solo. Noel at his own radio, local or SmartLink, no other humans needed.
- `[D]` — Needs Don. Don connected to his 6300, or Noel testing against Don's radio over SmartLink.
- `[J]` — Needs Justin. Justin's FLEX-8400 over SmartLink.
- `[M]` — Multi-client. At least two active clients on the same radio (MultiFlex testing).
- `[W]` — Waits for conditions. RF conditions that can't be scheduled.
- `[F]` — Forced stuck condition. Test requires triggering a stuck connect (slice held by another op, radio killed mid-connect, or similar). Often combinable with `[D]`.

### Pass / Fail / Defer Convention

- `PASS` — test executed, expected result observed.
- `FAIL: <short note>` — ran but wrong; note captures the wrong-ness.
- `DEFER: <reason>` — not attempted this pass, with reason.

### Setup Before Starting

- Pull latest `sprint28/home-key-qsk`, confirm clean working tree.
- `dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal` — already done by Claude post-merge; exe at `bin\x64\Debug\net10.0-windows\win-x64\JJFlexRadio.exe` is fresh as of 2026-05-04.
- Verify exe version: `powershell -Command "(Get-Item 'bin\x64\Debug\net10.0-windows\win-x64\JJFlexRadio.exe').VersionInfo.FileVersion"` → expect `4.1.16.0`.
- Launch JJFlex; confirm welcome speech.
- NVDA (or JAWS) running at a verbosity you're used to. Several tests are verbosity-dependent — note your verbosity at start so DEFER/FAIL notes are interpretable.

---

## A — Stuck-Modal Escape (track 1)

Covers the cancel mechanism, state-aware modal text, counting earcons, escalation paths, verbosity opt-out, and the 73-Morse-twice fix. Everything that a user encounters when trying to connect (or trying to escape from a stuck connect).

### A.1 Cancel mechanism — Escape during normal connect

- A.1 `[S]` Connect to your own radio. While the connecting modal shows "Connecting…" (you'll need a moment of latency to catch it — SmartLink connections give more time than local), press **Escape**. Confirm: speech "Connection attempt cancelled" speaks at Critical-level verbosity (heard regardless of verbosity setting), modal closes, focus returns to the radio selector. Result: ___

### A.2 Cancel during slice-acquisition wait

- A.2 `[D] [F]` Have Don connect to his 6300 first (so all slices are held). You then attempt to connect to the same radio. The modal should transition past "Connecting…" to a text describing slice unavailability ("Slice in use by [Don's station]" or similar). Press **Escape**. Confirm: same cancel speech + modal close + focus return as A.1, AND that the radio side cleans up your GUIClient registration (next connect attempt should not find stale state). Result: ___

### A.3 Phase transitions on a slow network

- A.3 `[D]` Connect to Don's 6300 over SmartLink (naturally slow phase transitions vs local). Listen for the phase progression: phase 1 earcon (1 tone) → text update → phase 2 earcon (1+1 tones) → text update → phase 3 earcon (1+1+1 tones) → connected. Confirm earcons are auditorially distinct from existing pitch-melody earcons. Result: ___

### A.4 60-second escalation dialog

- A.4 `[D] [F]` Force a stuck condition (slice held by Don, OR pull the radio's network cable mid-connect, OR similar). Wait 60 seconds wall-clock from connect start. Confirm: an escalation dialog appears with diagnostic-rich text (NOT just "Connection slow") populated from `ConnectionProfiler` events — should reference the radio model, the specific phase that's stuck, possibly the other op holding the slice. Choose **Keep waiting**. Confirm: another 60s elapses before the next escalation. Result: ___

### A.5 5-minute hard auto-cancel

- A.5 `[D] [F]` Continuation of A.4 — keep choosing "Keep waiting" through escalations until you reach the 5-minute total wall-clock mark. Confirm: JJ Flex unilaterally calls Disconnect, speaks "Connection attempt timed out — cancelled" at Critical verbosity, returns focus to the radio selector. **This test is `DEFER`-friendly** if you're not in the mood to sit through 5 minutes — the mechanic's identical to A.1 plus a timer, so a code-confidence DEFER is fine. Result: ___

### A.6 Verbosity opt-out

- A.6 `[S]` Settings → Notifications → "Speak connection progress" — toggle to OFF. Connect normally. Confirm: NO phase progress speech, NO counting earcons. Only Critical events should speak (errors, "Disconnected", success). Toggle back ON; verify default-on behavior restored. Result: ___

### A.7 73-Morse fix on disconnect

- A.7 `[S]` Connect to your radio. Disconnect (Radio menu → Disconnect, or however you typically disconnect). Listen carefully for the 73 Morse code earcon. Confirm: 73 plays **exactly once** (not twice as it did pre-fix). Result: ___

### A.8 Successful fast connect must remain unobtrusive

- A.8 `[S]` Connect to your local radio (fast, ~3s). Confirm: the modal opens and closes within ~500ms of connect completion. Phase announcements should NOT fire for the fast common case (the implementation gates phase speech on phases-taking-longer-than-500ms). Result: ___

### A.9 Both connecting-modal entry points work

- A.9 `[S]` Test 1: auto-connect on startup (Auto-Connect enabled, launch app). Confirm modal behaves as expected and closes on success. Test 2: manual radio-selector connect. Confirm same behavior. Result: ___

---

## B — Bug Bundle: CW Prosigns (track 2 — bugs #1 and #7)

### B.1 CW prosign pause between SK and EE

- B.1 `[S]` Trigger the SK close (typically by exiting the app, OR via the explicit close-CW path if you have one bound). Listen carefully to the cadence. Pre-fix: `73 SKEE` runs together. Post-fix: audible gap between SK and EE — `73 SK <pause> EE` — pause should match end-of-sentence cadence (5-7 dot-lengths). Confirm by ear that the gap is present AND that the EE itself still plays (regression check). Result: ___

### B.2 PlayCwSK `ee` on all three exit paths

- B.2 `[S]` Three states to test in order:
   - **B.2a** Launch JJFlex with NO radio connected. Exit. Confirm CW close = `73 SK <pause> EE` (both pause from B.1 AND the EE present).
   - **B.2b** Launch JJFlex, connect to a radio, exit. Confirm same.
   - **B.2c** Launch JJFlex, connect, disconnect (without exiting), then exit. Confirm same.
   - Pre-fix: B.2a and B.2c dropped the EE entirely. Post-fix: all three states produce the EE. Result: ___

---

## C — Bug Bundle: Home Field Navigation (track 2 — bugs #2, #4, #5)

### C.1 Squelch Level skip when Squelch is off

- C.1 `[S]` Connect to a radio. Toggle Squelch OFF (typically `Q` from Home, or via Slice menu). Arrow-nav through Home fields. Confirm: arrow nav SKIPS the Squelch Level field when Squelch is off (mirrors RIT/XIT pattern from `f6c00aa2`). Toggle Squelch ON; arrow-nav again — Squelch Level should be in the order normally. Result: ___

### C.2 Slice and Slice Operations purpose-naming

- C.2 `[S]` Focus the **Slice** field. Confirm announcement: "Slice selector: slice A active" (or whatever active slice). Focus the **Slice Operations** field. Confirm announcement: "Slice operations: slice A controls" (or similar — the field is now labeled by purpose, not just by value). Test in BOTH Classic and Modern tuning modes. Result: ___

### C.3 Classic and Modern field-order parity

- C.3 `[S]` Switch to **Classic** tuning mode. Arrow-nav through every Home field, noting the order. Switch to **Modern** tuning mode. Arrow-nav again. Confirm: shared fields appear in the SAME relative order (Classic's per-digit Frequency sub-positions live within the Frequency field as logical sub-positions and shouldn't shift the overall field order). Mute and Volume are intentionally Classic-only — that's by design, not a parity violation. Result: ___

---

## D — Bug Bundle: No-Radio Guards & Dialog Focus (track 2 — bugs #3, #6)

### D.1 Universal Home keys speak when pressed outside Home with no radio

- D.1 `[S]` Disconnect any radio (or launch with no radio). Move focus OUT of Home (e.g., into a menu, a settings dialog, anywhere where Home doesn't have focus). Press each of `R`, `M`, `X`, `Q`, `V`, `=` in turn. Pre-fix: silent (the bug). Post-fix: each press should produce no-radio guidance speech — likely "JJ Flexible Home, no radio connected" or similar (reusing `SpeakNoRadioConnected()` from the 2026-04-28 dispatcher work). Result: ___

### D.2 Universal Home keys still work when radio IS connected (no regression)

- D.2 `[S]` Reconnect to a radio. Focus on a Home field. Press each of `R`, `M`, `X`, `Q`, `V`, `=`. Confirm: each key fires its normal field-handler behavior (Mute toggle, Slice cycle, etc.) — the new no-radio guard should NOT block normal operation when a radio is connected. This is the regression check for D.1. Result: ___

### D.3 Dialog Escape produces "MainContent" (or similar named focus), not "pane"

- D.3 `[S]` Press `Ctrl+F` to open the Set Frequency dialog. Press **Escape** to close it. NVDA should announce a meaningful name (probably "MainContent" or whatever the named focus target is — the implementation chose `MainContent` per commit `9d425c5a`) rather than the generic "pane". Test for at least one other dialog you commonly use (Settings, Help, etc.) — same fix pattern should apply if those landed in the same commit. Result: ___

---

## E — Cross-Track Integration

These are the "did the two tracks land cleanly together" tests — anything where stuck-modal and bug-bundle touch adjacent surfaces.

### E.1 Stuck-modal escape PLUS no-radio guards

- E.1 `[S]` Disconnect any radio. Press `Escape` from various contexts (Home, dialogs, menus). Confirm no-radio guard for Universal Home keys (D.1) and stuck-modal Escape (A.1) coexist without surprising interactions. Specifically: with no radio, pressing Escape from Home should NOT speak "Connection attempt cancelled" (there's no connection to cancel) — it should speak no-radio guidance OR do nothing, depending on context. Result: ___

### E.2 73-Morse fix PLUS PlayCwSK ee fix together

- E.2 `[S]` Connect to a radio, then disconnect. Listen for: 73 Morse plays exactly once (A.7 fix), AND the SK close has the audible gap between SK and EE (B.1 fix), AND the EE actually plays (B.2 fix). All three fixes touch CW assembly; they should compose cleanly. Result: ___

### E.3 Build verification

- E.3 `[S]` (Already done by Claude): `dotnet clean` + `dotnet build` produced 0 errors, 1474 warnings (all pre-existing per CLAUDE.md "safe to ignore" list). No new warnings from either track. Build time ~58s post-merge. **PASS** by default; mark `FAIL` if you observe a build break locally. Result: PASS unless you see otherwise

---

## Deferred to Post-Release Testing

Tests that couldn't be run this pass should accumulate here with the unblocking condition:

- ___

---

## Test session summary

After running through the matrix:

- **Total tests:** 17 (A.1-A.9, B.1-B.2, C.1-C.3, D.1-D.3, E.1-E.3)
- **PASS count:** ___
- **FAIL count:** ___ (each FAIL needs a TODO entry in `JJFlex-TODO.md` if not already there)
- **DEFER count:** ___ (each DEFER needs an unblocking condition recorded above)

If FAIL count is zero (or all FAILs are minor enough to ship + fix-forward), the foundation drop is verified and ready for `sprint28/home-key-qsk → main` merge per `memory/project_main_branch_41_posture.md`.

---

## What this matrix does NOT cover (deliberately)

- The **2 deferred DESIGN entries** from the 2026-04-28 briefing (RunsWithoutRadio + action-aware no-radio announcement) — those are pending Q2 in `for-noel/2026-05-04-sprint28-bug-bundle-questions-pull.md`. Not in this build; not in this test pass.
- **Connect-time investigation bugs** (AS-retry janky reconnect, post-overhaul connect time) — Sprint 29 scope, gated on trace persistence.
- **FlexLib 4.2.18 work** — separate base (`track/flexlib-42`), separate testing surface, gated on R5 trace + Phase D firmware update + crash reporter + updater per `memory/project_main_branch_41_posture.md`.
- **Pre-existing Sprint 28 functionality** — this matrix covers ONLY today's two tracks. The 4.1.17 test matrix covered everything else; this is incremental verification on top.

---

## Cross-references

- `memory/project_stuck_modal_escape_design.md` — track 1 design (canonical)
- `memory/project_dialog_escape_rule.md` — design rule track 1 enforces
- `memory/project_no_silent_keystrokes_rule.md` — design rule bugs #3 and D.1 enforce
- `docs/planning/active/sprint28-bug-bundle-triage.md` — track 2 triage with bug-by-bug detail
- `docs/planning/agile/4.1.17-test-matrix.md` — format model for this matrix
- `docs/planning/for-noel/2026-05-04-sprint28-bug-bundle-questions-pull.md` — the still-open Q2 (deferred DESIGN entries)
- `docs/planning/for-noel/2026-05-04-42-release-execution-plan-pull.md` — the broader 4.2.0 release plan this foundation drop is the first step toward
