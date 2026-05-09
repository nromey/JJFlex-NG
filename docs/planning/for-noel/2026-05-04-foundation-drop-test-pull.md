# Pre-4.2.0 Foundation Drop — Test Pull

**Type:** test execution doc (you read each test, do the action, write your result on the `**** ` line below it)
**Build (post-cherry-pick to main, 2026-05-08 evening):** All 28 tests run against **main**, commit `28e2eaec`. Code-only cherry-pick from `sprint28/home-key-qsk` landed on main: 10 commits, all the foundation-drop fixes + stuck-modal escape + 7 bug-bundle fixes + design followup. Docs/memory/seals from sprint28 stay parked there until the 4.2-fold (per the keep-main-clean conversation 2026-05-08).

To test:
1. From `C:\dev\JJFlex-NG`: `git checkout main && git pull` (handle your local working-tree changes first if any — stash, commit, or checkout-discard as appropriate).
2. `dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal` for a Debug build, OR `-c Release` if you want Release.
3. Exe lands at `bin\x64\Debug\net10.0-windows\win-x64\JJFlexRadio.exe` (or Release path).
4. Run through all 6 sessions of tests below.

**Test 23 has a known spec-vs-implementation difference** — see Test 23's note for what was actually shipped.
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

## Session 6 — Sprint 28 design-followup (RunsWithoutRadio + action-aware no-radio)

**Requires post-design-followup-merge build.** Track is running on auto in `C:\dev\jjflex-bug-bundle` on branch `sprint28/bug-bundle-design-followup`. When done, it merges into `sprint28/home-key-qsk` and the new commit becomes the build target. If you're testing before that merge, mark every test 20–28 as `**** SKIP build-not-ready-yet` and continue.

**What this session validates:**
- (a) `RunsWithoutRadio` flag — `SetFreq` and `ShowMemory` now run with no radio connected so easter eggs and just-typing-a-frequency-to-remember-it still work.
- (b) Action-aware no-radio announcement — generic "JJ Flexible Home, no radio connected" is replaced by "Unable to [change band], JJ Flexible Home no radio connected" sourced from each command's `ShortActionLabel`.

### 20. SetFreq with no radio opens dialog silently

Disconnect any radio. Press `Ctrl+F`. Set Frequency dialog opens. **No "no radio" announcement at the open** — the dialog just appears and your screen reader announces the dialog's own focus target normally. Pre-fix: would have spoken "JJ Flexible Home, no radio connected" and never opened the dialog.

**** 

### 21. SetFreq easter-egg input still works with no radio

Continuation of test 20. With the freq dialog open and no radio connected, type your easter-egg trigger (e.g., `cqtest`) and confirm. The easter egg fires normally — same behavior as if a radio were connected.

**** 

### 22. SetFreq apply-time announcement when typing a real frequency with no radio

Continuation of test 20. With no radio and the freq dialog open, type a real frequency (e.g., `14250.00`) and confirm. At apply time the announcement should be something like "no radio, can't tune" — the failure message is moved from open-time to apply-time. Verify the announcement doesn't try to interpret the typed frequency as something it isn't (no "tuning to 14.25" before the failure speech).

**** 

### 23. ShowMemory with no radio (REVISED — implementation chose action-aware speech, not silent dialog)

Disconnect any radio. Trigger ShowMemory (your bound key for it). **Implementation shipped with action-aware speech, not silent dialog**: the keystroke produces the action-aware announcement *"Unable to show memories, JJ Flexible Home no radio connected"* (or the appropriate verbosity-tier form) and the dialog does NOT open. Reasoning: memory data lives radio-side, so opening an empty viewer is more confusing than a clear failure announcement.

What to verify: the announcement names "show memories" (not the generic "JJ Flexible Home, no radio connected"), and the keystroke does not silently fail (no-silent-keystrokes rule). The dialog NOT opening is the intended behavior, not a bug.

If you'd prefer the original spec behavior (dialog opens silently, allows browsing locally-cached memory data when there is some), that's a separate small change — flag it on the `**** ` line and we'll plan a follow-up. Today's behavior matches the no-silent-keystrokes principle and the data-lives-radio-side reality.

**** 

### 24. Action-aware no-radio announcement at Verbose verbosity

Settings → Notifications → verbosity = Verbose. Disconnect. Press `Ctrl+B` (ChangeBand). Should hear: **"Unable to change band, JJ Flexible Home no radio connected."** The action label ("change band") leads, the venue ("JJ Flexible Home") and the failure ("no radio connected") follow. Pre-fix: just "JJ Flexible Home, no radio connected" with no action context.

**** 

### 25. Action-aware announcement at Terse verbosity

Same setup as test 24, switch verbosity to Terse. Press `Ctrl+B`. Should hear something like **"change band, no radio"** — action label still leads, venue + failure abbreviated. Exact phrasing is at the build session's discretion (whatever the helper produces at Terse).

**** 

### 26. Action-aware announcement at Critical-only verbosity

Same setup as test 24, switch verbosity to Critical (the lowest). Press `Ctrl+B`. Should still hear an action-led phrase — not silence. The exact form may be even shorter (e.g., "change band, no radio") but the action label MUST appear; this is the most-information-with-least-words case.

**** 

### 27. Multiple action labels — sample a few

With no radio and at Verbose verbosity, sample these and confirm each speaks its action label:
- Tune key — should say "Unable to [tune action label], …"
- RIT toggle key — should say "Unable to [RIT toggle label], …"
- One band-other-than-Ctrl+B (e.g., 40m direct key) — should say "Unable to [its action label], …"

You don't need to memorize the labels; just confirm each one's announcement names what you tried to do, not just "no radio connected."

**** 

### 28. Regression — command without ShortActionLabel falls back cleanly

Some commands may not have a populated `ShortActionLabel` yet (vocabulary work continues into Sprint 29). Find one such command (or the build session will list a couple in its commit messages). Press it with no radio. The announcement should fall back to **the plain "JJ Flexible Home, no radio connected"** (today's behavior) — NOT speak "Unable to , JJ Flexible Home…" with an empty label, NOT crash, NOT go silent.

**** 

---

## After you're done

Total: 28 tests across 6 sessions. Skip Session 4 if you're not in the mood for forced-stuck-condition setup; skip Session 3 if Don isn't available; skip Session 6 if the design-followup track hasn't merged yet (mark each `**** SKIP build-not-ready-yet`). Sessions 1, 2, 5, 6 are pure solo and cover most of the ground.

Move this doc to `for-claude/` when done. I'll:
1. Translate `**** ` answers into the matrix's PASS/FAIL/DEFER convention.
2. Update `docs/planning/agile/pre-4.2-foundation-drop-test-matrix.md` with verified state (Session 6 results extend the matrix with new test rows).
3. Promote any FAIL results into TODO entries in `JJFlex-TODO.md`.
4. If everything's clean (or only minor FAILs that can ship-and-fix-forward), propose the `sprint28/home-key-qsk → main` merge.

---

## Cross-references

- `docs/planning/agile/pre-4.2-foundation-drop-test-matrix.md` — same tests, dense matrix format, for storage / accumulation
- `docs/planning/agile/pre-4.2-foundation-drop-guided-walkthrough.md` — alternate format (long-form prose walkthrough) for Don/Justin if they prefer that shape
- `memory/feedback_test_matrix_vs_guided_paper.md` — the convention this pull doc enacts
- `memory/project_stuck_modal_escape_design.md` — design behind tests 10-17
- `memory/project_no_silent_keystrokes_rule.md` — design behind tests 8-9
- `docs/planning/active/sprint28-design-followup-track-instructions.md` — spec behind Session 6 (tests 20-28)
- `memory/project_short_action_labels_vocabulary.md` — the `ShortActionLabel` infrastructure tests 24-28 exercise
