# Pre-4.2.0 Foundation Drop — Test Pull

**Type:** test execution doc (you read each test, do the action, write your result on the `**** ` line below it)
**Build:** Debug x64, version `4.1.16.0`, branch `sprint28/home-key-qsk`, commit `ee8faebb`. Already built; exe at `bin\x64\Debug\net10.0-windows\win-x64\JJFlexRadio.exe`.
**When done:** move this doc to `for-claude/`. I'll translate `**** ` results into the matrix at `docs/planning/agile/pre-4.2-foundation-drop-test-matrix.md` and propose the foundation drop merge to main if everything looks clean.
**Skip-friendly:** any test you can't run, write `**** SKIP <reason>` and move on. Tests are independent — skipping one doesn't invalidate the others.
**Want to talk through one instead?** Just stop and ask in chat — happy to walk through any specific test interactively rather than have you fight through this doc.

---

## Session 1 — Quick solo wins

### 1. CW prosign pause between SK and EE

Exit JJFlex (any state, no radio needed). Listen to the CW close. Should be `73 SK <pause> EE` — audible gap between SK and EE, AND the EE itself plays.

**** 

### 2. PlayCwSK ee on three exit paths

Three sub-tests: (a) launch with no radio + exit, (b) launch + connect + exit, (c) launch + connect + disconnect + exit. All three should produce the EE in the close. Pre-fix dropped EE on (a) and (c).

**** 

### 3. 73 Morse plays exactly once on disconnect

Connect to your radio, then disconnect (Radio menu → Disconnect). 73 Morse should play exactly once. Pre-fix played twice.

**** 

### 4. Dialog Escape produces "MainContent" not "pane"

Press `Ctrl+F` to open Set Frequency dialog. Press `Escape`. NVDA should announce "MainContent" (or whatever named focus target) — NOT generic "pane."

**** 

### 5. Slice and Slice Operations purpose-naming

Focus the Slice field — should say "Slice selector: slice A active." Focus Slice Operations — should say "Slice operations: slice A controls." Test in BOTH Classic and Modern tuning modes.

**** 

### 6. Squelch Level skip when Squelch is off

Connected, focus on Home. Toggle Squelch OFF. Arrow-nav through Home — should skip the Squelch Level field. Toggle Squelch ON — Squelch Level back in the order.

**** 

### 7. Classic and Modern field-order parity

Switch to Classic, arrow-nav through Home, note the order. Switch to Modern, arrow-nav. Shared fields should appear in the same relative order. Mute/Volume are intentionally Classic-only — that's by design.

**** 

---

## Session 2 — No-radio guards and stuck-modal core

### 8. Universal Home keys speak when pressed outside Home with no radio

Disconnect any radio. Move focus OUT of Home (into a menu, settings dialog, etc.). Press `R`, `M`, `X`, `Q`, `V`, `=` in turn. Each should produce no-radio guidance speech. Pre-fix: silent.

**** 

### 9. Universal Home keys still work when radio IS connected (regression check)

Reconnect to your radio, focus on a Home field. Press each of `R`, `M`, `X`, `Q`, `V`, `=`. Each should fire its NORMAL field-handler behavior (Mute toggle, Slice cycle, etc.). The new no-radio guard must not block normal operation.

**** 

### 10. Cancel during normal connect (Escape on the modal)

Disconnect. Then connect to your radio. While JJFlex shows the Connecting dialog, press `Escape`. Should hear "Connection attempt cancelled" at Critical verbosity, modal closes, focus returns to radio selector. (If your local connect is too fast to catch, do this against Don's radio in Session 3.)

**** 

### 11. Verbosity opt-out

Settings → Notifications → "Speak connection progress" toggle to OFF. Connect — should hear NO phase progress speech, NO counting earcons. Toggle back ON, reconnect — phase speech and earcons return.

**** 

### 12. Successful fast connect must remain unobtrusive

Local connect with verbosity defaults and "Speak connection progress" ON. Modal should open and close within ~500ms. Phase announcements should NOT fire for the fast common case (gated on phase-takes-longer-than-500ms).

**** 

### 13. Both modal entry points work

Test (a): Auto-Connect enabled, launch JJFlex — modal behaves correctly, closes on success. Test (b): manually disconnect + manually reconnect via Radio menu — modal behaves correctly, closes on success.

**** 

---

## Session 3 — With Don over SmartLink

### 14. Cancel during slice-acquisition wait

Don connects to his 6300 first (slices held). You attempt to connect to the same radio. Modal text should transition past "Connecting…" to slice-unavailability text. Press `Escape`. Should cancel cleanly + radio side cleans up your GUIClient (next attempt should not find stale state).

**** 

### 15. Phase transitions on a slow network

Connect to Don's 6300 over SmartLink. Listen for phase progression: phase 1 earcon (1 tone) → phase 2 earcon (1+1 tones) → phase 3 earcon (1+1+1 tones). Earcons should be auditorially distinct from existing pitch-melody earcons (these are simple count-based).

**** 

---

## Session 4 — Forced stuck conditions (optional)

These need a deliberately-broken connect (slice held forever, radio killed mid-connect, network cable yanked). Skip-friendly if you don't want to engineer the failure mode.

### 16. 60-second escalation dialog

Force a stuck condition (Don holds slice indefinitely, OR power off your radio mid-connect, OR pull network cable). Wait 60 seconds. Escalation dialog should appear with **diagnostic-rich text** from `ConnectionProfiler` events — radio model, stuck phase, etc. Choose "Keep waiting" — another 60s elapses before next escalation.

**** 

### 17. 5-minute hard auto-cancel

Continuation of test 16. Keep "Keep waiting"-ing through escalations until 5 minutes total wall-clock. JJFlex should unilaterally Disconnect, speak "Connection attempt timed out — cancelled" at Critical, return focus to radio selector. **DEFER-friendly**: if test 10 worked, this most likely works (same mechanic + timer). Mark `**** SKIP code-confidence` if you'd rather not sit through 5 minutes.

**** 

---

## Session 5 — Cross-track integration

### 18. Stuck-modal escape PLUS no-radio guards together

No radio connected. Press `Escape` from various contexts (Home, dialogs, menus). Should NOT spuriously speak "Connection attempt cancelled" (no connection to cancel). Either no-radio guidance OR normal context-specific Escape behavior.

**** 

### 19. 73-Morse + ee fix + pause all compose cleanly

Connect, then disconnect. The disconnect should produce: 73 plays exactly once (test 3), `73 SK <pause> EE` cadence (test 1), AND the EE plays (test 2). All three CW changes touched adjacent code; verify they compose.

**** 

---

## After you're done

Total: 19 tests across 5 sessions. Skip Session 4 if you're not in the mood for forced-stuck-condition setup; skip Session 3 if Don isn't available. Sessions 1, 2, 5 are pure solo and cover most of the ground.

Move this doc to `for-claude/` when done. I'll:
1. Translate `**** ` answers into the matrix's PASS/FAIL/DEFER convention.
2. Update `docs/planning/agile/pre-4.2-foundation-drop-test-matrix.md` with verified state.
3. Promote any FAIL results into TODO entries in `JJFlex-TODO.md`.
4. If everything's clean (or only minor FAILs that can ship-and-fix-forward), propose the `sprint28/home-key-qsk → main` merge.

---

## Cross-references

- `docs/planning/agile/pre-4.2-foundation-drop-test-matrix.md` — same tests, dense matrix format, for storage / accumulation
- `docs/planning/agile/pre-4.2-foundation-drop-guided-walkthrough.md` — alternate format (long-form prose walkthrough) for Don/Justin if they prefer that shape
- `memory/feedback_test_matrix_vs_guided_paper.md` — the convention this pull doc enacts
- `memory/project_stuck_modal_escape_design.md` — design behind tests 10-17
- `memory/project_no_silent_keystrokes_rule.md` — design behind tests 8-9
