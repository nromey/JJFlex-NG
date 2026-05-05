# Pre-4.2.0 Foundation Drop — Guided Test Walkthrough

**For:** alternate format (Excel-tabular-friendly walkthrough). Noel prefers the for-noel test pull format at `docs/planning/for-noel/2026-05-04-foundation-drop-test-pull.md` for his own use. This walkthrough exists for other testers (Don, Justin) who may prefer step-by-step prose with `Result: ___` slots over a `**** ` annotation pattern.
**Build:** Debug x64, version `4.1.16.0`, branch `sprint28/home-key-qsk`, commit `ee8faebb`
**Companion to:** `pre-4.2-foundation-drop-test-matrix.md` (storage format), `for-noel/2026-05-04-foundation-drop-test-pull.md` (Noel's preferred format)

## How to use this paper

This is a **guided walkthrough**, not a reference matrix. Read top-to-bottom, do each step in order, fill in the `Result: ___` slot, move on. Heading-jump (NVDA `H` / `Shift+H` between H3 headings) to skip around if you want.

The tests are grouped into **5 sessions** of ~5–15 minutes each. You don't have to do them all in one go — stop at any session boundary and pick up later. Each session ends with a checkpoint summary so you know you finished it.

If a test is fiddly and you'd rather just talk it through with me in chat, **say so** — I can walk you through any specific test interactively instead of you fighting through this doc. That offer holds for the whole walkthrough.

If a test doesn't work as expected, write what actually happened in the Result slot. Don't worry about "PASS/FAIL/DEFER" formality — plain prose is fine ("the speech didn't fire," "the dialog froze," "I heard 'pane' instead of MainContent"). I'll translate to matrix conventions later.

## Prerequisites — before you start any session

- The build at `bin\x64\Debug\net10.0-windows\win-x64\JJFlexRadio.exe` is fresh (Claude verified post-merge today). If you've rebuilt since, you're fine — anything from `sprint28/home-key-qsk` after commit `ee8faebb` includes both tracks.
- Launch JJFlex. Confirm the welcome speech "Welcome to JJ Flexible Radio Access" plays.
- Note your current verbosity setting (some tests reference verbosity behavior).
- For tests that need Don connected: coordinate with him via your usual channel. Don't block on it — those tests are clustered into Session 3 and can be deferred.

---

## Session 1 — Quick solo wins (~15 min, no special setup)

These are the easiest tests. Do them first to build momentum.

### Step 1 — CW prosign pause between SK and EE

**What you should have set up:**
- JFlex running, any radio state (this test runs at app exit, doesn't need a connection).

**What to do:**
1. Exit JJFlex (File menu → Exit, or close window).
2. Listen carefully to the CW close.

**What you should hear:**
- Pre-fix cadence was `73 SK EE` running together as `73 SKEE` (no audible gap).
- Post-fix cadence: `73 SK` then an audible pause, then `EE` — the pause should match end-of-sentence cadence, similar to a `<BT>` separator in CW.
- Both the pause AND the EE should be present.

**Result:** ___

**If this didn't work:**
- If you don't hear the pause: the CW prosign pause primitive didn't take.
- If you don't hear the EE: regression — that fix should still be in place.

---

### Step 2 — PlayCwSK ee close on all three exit paths

**What you should have set up:**
- JJFlex closed.

**What to do (three sub-tests in order):**
1. Launch JJFlex with NO radio connected. Exit. Listen to CW close.
2. Launch JJFlex, connect to a radio, then exit. Listen to CW close.
3. Launch JJFlex, connect to a radio, disconnect (Radio menu → Disconnect, don't exit yet), THEN exit. Listen to CW close.

**What you should hear (all three sub-tests):**
- The full `73 SK <pause> EE` cadence — same as Step 1 but for all three exit paths.
- Pre-fix: sub-tests 1 and 3 dropped the EE entirely. Post-fix: all three should produce it.

**Result:** ___ (note any sub-test that differed)

---

### Step 3 — 73 Morse plays exactly once on disconnect

**What you should have set up:**
- JJFlex running, connected to your radio.

**What to do:**
1. Disconnect from the radio (Radio menu → Disconnect).
2. Listen carefully to the 73 Morse code earcon.

**What you should hear:**
- 73 plays **exactly once**.
- Pre-fix: it played twice (the bug we fixed in this track).

**Result:** ___

---

### Step 4 — Dialog Escape produces "MainContent" not "pane"

**What you should have set up:**
- JJFlex running, connected to your radio.

**What to do:**
1. Press `Ctrl+F` to open the Set Frequency dialog.
2. Press `Escape` to close it without entering a frequency.
3. Listen to what NVDA announces as focus returns.

**What you should hear:**
- Pre-fix: NVDA announced "pane" — generic UI container with no name.
- Post-fix: NVDA should announce "MainContent" (or whatever the named focus target ended up being — the implementation chose `MainContent` per commit `9d425c5a`).

**Result:** ___

**Bonus:** if you commonly use other dialogs (Settings, Help, etc.), close them with Escape too and note whether they still report "pane" or also got renamed. The fix may apply to other dialogs that share the same close path.

---

### Step 5 — Slice and Slice Operations purpose-naming

**What you should have set up:**
- JJFlex running, connected to your radio, focus on Home.

**What to do:**
1. Arrow-nav to the Slice field. Listen to the announcement.
2. Arrow-nav to the Slice Operations field. Listen to the announcement.
3. Switch to the OTHER tuning mode (Classic ↔ Modern) and repeat both.

**What you should hear:**
- Slice field: "Slice selector: slice A active" (or whatever active slice) — leads with PURPOSE (selector), then VALUE.
- Slice Operations field: "Slice operations: slice A controls" (or similar) — leads with PURPOSE (operations on slice), then VALUE.
- Pre-fix: just "slice A" or "slice A operations volume 90" — values without purpose label.

**Result:** ___

---

### Step 6 — Squelch Level skip when Squelch is off

**What you should have set up:**
- JJFlex running, connected. Focus on Home.

**What to do:**
1. Toggle Squelch ON if it isn't already (`Q` from Home, or via Slice menu).
2. Arrow-nav through Home fields. Note that Squelch Level appears in the field order normally.
3. Toggle Squelch OFF.
4. Arrow-nav through Home fields again.

**What you should hear:**
- With Squelch OFF: arrow-nav SKIPS the Squelch Level field — same pattern as RIT/XIT off-state from commit `f6c00aa2`.
- With Squelch ON: Squelch Level is back in the order normally.

**Result:** ___

---

### Step 7 — Classic and Modern field-order parity

**What you should have set up:**
- JJFlex running, connected. Focus on Home.

**What to do:**
1. Switch to Classic tuning mode.
2. Arrow-nav through every Home field, noting the order out loud or on paper.
3. Switch to Modern tuning mode.
4. Arrow-nav again.

**What you should hear:**
- The shared fields (Slice, SliceOps, Frequency, SMeter, Squelch, Squelch Level, Split, VOX, Offset, RIT, XIT) should appear in the SAME relative order in both modes.
- Per-digit Classic frequency sub-positions (1 MHz, 100 kHz, etc.) live within the Frequency field — they shouldn't shift the overall field order.
- Mute and Volume are intentionally Classic-only — that's by design, not a parity violation.

**Result:** ___

---

**Session 1 checkpoint:** 7 tests done. Take a break if you need one — Session 2 is a different surface.

---

## Session 2 — No-radio guards and stuck-modal core (~15 min, no special setup)

These tests cover the always-honored Escape on the connecting modal and the no-radio guards on universal Home keys.

### Step 8 — Universal Home keys speak when pressed outside Home with no radio

**What you should have set up:**
- JJFlex running with NO radio connected. Disconnect first if needed.
- Focus moved OUT of Home — into a menu, a settings dialog, anywhere where Home doesn't have focus.

**What to do:**
1. Press each of these keys in turn: `R`, `M`, `X`, `Q`, `V`, `=`.

**What you should hear:**
- Each key press should produce no-radio guidance speech — likely "JJ Flexible Home, no radio connected" (reusing `SpeakNoRadioConnected()` from the 2026-04-28 dispatcher work).
- Pre-fix: silent — the keystroke hit no handler because Home had no focus, and there was no window-level guard.

**Result:** ___

---

### Step 9 — Universal Home keys still work when radio IS connected (no regression)

**What you should have set up:**
- JJFlex running, connected to your radio. Focus on a Home field.

**What to do:**
1. Press each of `R`, `M`, `X`, `Q`, `V`, `=` from various Home fields.

**What you should hear:**
- Each key fires its NORMAL field-handler behavior — Mute toggle, Slice cycle, Tune toggle, etc. — same as before.
- The new no-radio guard should NOT block normal operation when a radio is connected.
- This is the regression check for Step 8.

**Result:** ___

---

### Step 10 — Cancel during normal connect (Escape works on the modal)

**What you should have set up:**
- JJFlex running, no radio currently connected.

**What to do:**
1. Open Radio menu → Connect to Radio.
2. Select your radio and press Enter.
3. While JJFlex shows the Connecting dialog, press `Escape`.

**What you should hear:**
- "Connection attempt cancelled" — speaks at Critical-level verbosity, so it speaks even if your verbosity is set to Off.
- The Connecting dialog closes.
- Focus returns to the radio selector dialog.

**Result:** ___

**If this didn't work:**
- The window for catching the modal mid-connect is small for local connects (sub-second). If your radio connects too fast to press Escape, do this test against Don's radio over SmartLink later (Session 3).

---

### Step 11 — Verbosity opt-out

**What you should have set up:**
- JJFlex running.

**What to do:**
1. Open Settings → Notifications.
2. Find "Speak connection progress" and toggle it OFF. Save / close.
3. Connect to your radio.
4. Listen during the connect.
5. Reopen Settings → Notifications, toggle "Speak connection progress" back ON. Save.
6. Disconnect, reconnect, listen again.

**What you should hear:**
- With the toggle OFF: NO phase progress speech, NO counting earcons during connect. Only Critical events should speak (errors, "Disconnected", success acknowledgement).
- With the toggle ON: phase progress speech and counting earcons return (default behavior).

**Result:** ___

---

### Step 12 — Successful fast connect must remain unobtrusive

**What you should have set up:**
- JJFlex running. Verbosity at default. "Speak connection progress" set to ON.

**What to do:**
1. Disconnect any radio.
2. Connect to your local radio (fast, ~3 seconds).
3. Time how long the modal is visible.

**What you should hear:**
- The modal opens and closes within ~500ms of connect completion.
- Phase announcements should NOT fire for the fast common case — the implementation gates phase speech on phases-taking-longer-than-500ms.
- This is the regression check that fast connects haven't gained a "wait through all the announcements" UX.

**Result:** ___

---

### Step 13 — Both modal entry points work (auto-connect AND manual)

**What you should have set up:**
- JJFlex closed.
- Auto-Connect enabled on your radio.

**What to do (two sub-tests):**
1. Launch JJFlex. Confirm the auto-connect modal behaves correctly and closes on success.
2. Disconnect (Radio menu → Disconnect). Connect manually (Radio menu → Connect to Radio, select, Enter). Confirm modal behaves correctly and closes on success.

**What you should hear:**
- Both entry points produce a working connect modal that opens and closes cleanly.
- No surprises (no phase mismatch, no stuck states).

**Result:** ___

---

**Session 2 checkpoint:** 6 more tests done (13 total). Take a break.

---

## Session 3 — With Don over SmartLink (~15 min, needs Don)

These tests cover the slower SmartLink path and the slice-acquisition state. Need Don connected to his 6300 first; coordinate with him.

### Step 14 — Cancel during slice-acquisition wait

**What you should have set up:**
- Don connected to his FLEX-6300 (so all slices are held).
- You at JJFlex, NOT yet connected to Don's radio.

**What to do:**
1. Open Radio menu → Connect to Radio. Press Remote (Alt+R) if needed to get to SmartLink radios.
2. Select Don's "6300 inshack" and press Connect.
3. The modal should transition past "Connecting…" to a text describing slice unavailability — something like "Slice in use by [Don's station]" or "Connected, waiting for slice."
4. Press `Escape`.

**What you should hear:**
- "Connection attempt cancelled" + modal close + focus return — same as Step 10.
- AND: when you re-attempt the connect right after, JJFlex should NOT find stale GUIClient state on the radio side. Should behave like a fresh attempt.

**Result:** ___

---

### Step 15 — Phase transitions on a slow network

**What you should have set up:**
- Don available, his radio reachable, his slices freed up if you want a successful connect (or kept held if you want to test the slice-wait phase).

**What to do:**
1. Connect to Don's 6300 over SmartLink.
2. Listen carefully to the phase progression as the connect proceeds.

**What you should hear:**
- Phase 1 earcon (1 tone) → text update "Connecting to 6300 inshack…"
- Phase 2 earcon (1+1 tones) → text update "Connected, waiting for slice…" (or similar)
- Phase 3 earcon (1+1+1 tones) → text update "Slice acquired, setting up…"
- Then connected, normal radio operation begins.
- Earcons should be auditorially distinct from existing pitch-melody earcons (which use harmony/pitch); these are simple count-based.

**Result:** ___

---

**Session 3 checkpoint:** 2 more tests done (15 total). If you don't have Don available, DEFER both with reason "Don not available this pass" — they'll surface again next time.

---

## Session 4 — Forced stuck conditions (~10 min, optional)

These tests need a deliberately-broken connect (slice held forever, radio killed mid-connect, network cable yanked). They're the load-bearing tests for the escalation paths but are skip-friendly if you don't want to engineer the failure mode.

### Step 16 — 60-second escalation dialog

**What you should have set up:**
- A way to force a stuck condition. Options:
  - Don connected and holding the slice indefinitely (coordinate).
  - Power off your own radio mid-connect.
  - Pull the radio's network cable mid-connect.

**What to do:**
1. Initiate the stuck connect.
2. Wait 60 seconds wall-clock from connect start.
3. When the escalation dialog appears, listen carefully to what it says.
4. Choose "Keep waiting." Wait another 60 seconds for the next escalation.

**What you should hear:**
- An escalation dialog at 60 seconds with **diagnostic-rich text** populated from `ConnectionProfiler` events — should reference the radio model, the specific phase that's stuck, possibly the other op holding the slice or "no response from radio" depending on which failure you forced.
- NOT a generic "Connection slow, retrying" — that would be the pre-fix behavior.
- After "Keep waiting," another full 60 seconds elapses before the next escalation.

**Result:** ___

---

### Step 17 — 5-minute hard auto-cancel

**What you should have set up:**
- Continuation of Step 16 — keep "Keep waiting"-ing through escalations.

**What to do:**
1. Keep "Keep waiting"-ing until 5 minutes total wall-clock has elapsed since connect start.
2. Listen for what JJFlex does at the 5-minute mark.

**What you should hear:**
- JJFlex unilaterally calls Disconnect.
- Speaks "Connection attempt timed out — cancelled" at Critical verbosity.
- Returns focus to the radio selector.
- No fourth or fifth "Keep waiting" prompt offered.

**Result:** ___

**This test is DEFER-friendly.** Sitting through 5 minutes of stuck connection is not fun. The mechanic is identical to Step 10's cancel mechanism plus a timer; if Step 10 worked, Step 17 most likely works too. Mark `DEFER: validated by code-review confidence` if you'd rather skip.

---

**Session 4 checkpoint:** 2 more tests done (17 total). If you skipped both, that's 15 tests — still solid coverage.

---

## Session 5 — Cross-track integration sweep (~5 min)

These are the "did the two tracks land cleanly together" tests. Quick sanity checks.

### Step 18 — Stuck-modal escape PLUS no-radio guards together

**What you should have set up:**
- JJFlex running with NO radio connected.

**What to do:**
1. Press `Escape` from various contexts (Home, dialogs, menus).

**What you should hear:**
- With no radio, pressing Escape should NOT speak "Connection attempt cancelled" (there's no connection to cancel).
- It should either speak no-radio guidance OR do the normal Escape behavior for that context (close menu, close dialog, etc.) — depending on where you pressed it.
- The point: the stuck-modal Escape handler shouldn't fire spuriously when there's no connect-in-progress.

**Result:** ___

---

### Step 19 — 73-Morse + ee fix + pause all compose cleanly

**What you should have set up:**
- JJFlex running, connected to your radio.

**What to do:**
1. Disconnect from the radio (Radio menu → Disconnect).
2. Listen to the FULL CW close.

**What you should hear (all three should be present in one disconnect):**
- 73 Morse plays exactly ONCE (Step 3 fix).
- The cadence is `73 SK <pause> EE` — audible pause between SK and EE (Step 1 fix).
- The EE itself plays (Step 2 fix).
- All three CW changes touched adjacent code; they should compose cleanly.

**Result:** ___

---

**Session 5 checkpoint:** 19 tests total (or 17 if you skipped Session 4). Walkthrough complete.

---

## After the walkthrough

Once you've filled in the Result slots, tell me and I'll:
1. Translate your prose results into the matrix's PASS/FAIL/DEFER convention.
2. Update the matrix at `pre-4.2-foundation-drop-test-matrix.md` with the verified state.
3. Promote any FAIL results into TODO entries in `JJFlex-TODO.md`.
4. If everything looks clean, propose the `sprint28/home-key-qsk → main` merge as the actual foundation drop.

If a test was unclear or you'd rather walk it through with me interactively, just say which one and we'll handle it in chat. The walkthrough doc is meant to ENABLE solo testing, not require it.

---

## Cross-references

- `pre-4.2-foundation-drop-test-matrix.md` — same tests, dense reference shape, for storage / accumulation
- `memory/feedback_test_matrix_vs_guided_paper.md` — the convention this paper enacts
- `memory/feedback_directed_testing_delivery_format.md` — file-vs-chat threshold
- `memory/project_stuck_modal_escape_design.md` — design behind tests in Sessions 2-4
- `memory/project_no_silent_keystrokes_rule.md` — design behind tests in Session 2 (no-radio guards)
