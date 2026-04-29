# JJ Flex — TODO / Rolling Backlog

Last updated: 2026-04-28

## Upcoming Major Work

### FlexLib upgrade to 4.2.18 — branch ready, merge gated on 4.1.17 + firmware-update UI (2026-04-29 status)

**Trigger:** Flex released SmartSDR 4.2.18 with new FlexLib. Per `project_versioning_scheme.md`, our versioning mirrors FlexLib's first three components, so our next version becomes **4.2.0.xxx** when this lands.

**Status (2026-04-29):** Upgrade work complete on `track/flexlib-42` branch, all eight phases done, parked at commit `971d5425`. Live audio confirmed on Don's 6300 over SmartLink. Branch does NOT merge yet — see sequencing below.

**What landed on the branch:**
- v4.2.18 vendor source dropped in over the prior FlexLib, four MIGRATION.md patches reapplied (TLS wrapper refresh to mirror new vendor `SslClient` async API, Discovery race fix layered on vendor's partial absorption, Radio.cs `IsWan || PublicTlsPort > 0` selector, `TlsCommandCommunication` wrapper swap)
- **Phase 5 audio fix:** four new sub commands (`display_marker`, `navtex`, `filt_preset`, `waveform`) gated behind `FlexVersion.Parse("4.2.18.0")`. Older firmware halts opus audio after ~2 packets when those subs are sent — gating them off restores audio. Modern firmware (4.2.18+) gets the full sub surface; older firmware loses those four notification streams but keeps audio + general radio function.
- All four configurations (x64/x86 × Debug/Release) build clean
- Updated `MIGRATION.md` with v4.2.18-specific notes (vendor partial Discovery fix already absorbed; SslClient API reshuffle, etc.)

**Noel's commentary on the release:** *"I'm super disappointed with Flex, we've had features that they're calling new for ages. They have improved Dax and Cat, but radio features ... only cool thing is that they allow loading waves packages using the API now which to me says we can more easily implement things like FreeDV. This is a severely anemic release but we go with the times I guess."*

**Silver lining identified:** The new "load Waves packages via API" capability **opens the door to in-app FreeDV implementation** without depending on Flex shipping native FreeDV. This is a real opportunity worth a separate Sprint 30+ exploration — JJ Flex could become the first SDR client to make FreeDV accessible to blind hams via Waves package loading.

**Sequencing (decided 2026-04-29):** Merge gated on TWO prerequisites:
1. **4.1.17 ships first.** Merging this branch forces the 4.2.0.x version cut — would conflict with the in-flight 4.1.17 release on FlexLib 4.1.5.
2. **Firmware-update UI is implemented.** The Phase 5 gate means users on older firmware lose those four subscriptions until they update firmware. The 4.2.0.x release should bundle "FlexLib upgrade + firmware update mechanism" so users can self-unlock the gated features. Firmware-update UI is queued as part of Sprint 29's updater scope (per `project_sprint29_updater_vision.md`).

**Merge plan when both prerequisites are met:**
```
git checkout main
git merge track/flexlib-42 --no-ff -m "Sprint FlexLib-42: Upgrade FlexLib to v4.2.18"
# verify clean build both archs
# bump JJFlexRadio.vbproj <Version>4.2.0</Version>
# release per standard process
```

**Optional follow-up (not blocking merge):** Bisect the four gated subs to identify which specific one(s) actually break older firmware. Right now all four gate together; some may be safe even on older firmware. Cheap re-test if Don's connection is stable.

**Branch contents:**
- See `docs/planning/track-b/4.2.18-handoff.md` for the full Track B writeup
- See `docs/planning/track-b/4.2.18-inventory.md` for the v4.x → v4.2.18 API drift map
- See `docs/planning/track-b/4.2.18-build-errors-phase4.md` for the build evidence
- See `docs/planning/track-b/SmartSDR-v4.2.18-Release-Notes.pdf` for vendor's release notes

## Open Bugs

### BUG: CW prosign concatenation has no pause primitive — `73 SK ee` runs together as `73 SKEE` (2026-04-27 Noel observation)

**Symptom:** When PlayCwSK appends `ee` after the SK prosign (the 2026-04-26 friendly hand-wave close), the resulting CW reads as `73 SKEE` rather than `73 SK <pause> EE`. The desired cadence is the same as a sentence-end pause — a clear gap separating SK from the closing dits, the way a real op would key it. Same shape would affect any future prosign+character concatenation (BT, AS, KN, etc. followed by single-character status dits).

**Root cause (suspected):** The CW notification engine concatenates characters without any pause primitive. Prosigns and following characters land back-to-back at the configured WPM, with only the inter-character gap defined by Morse timing — which is shorter than what a human op would key between an end-of-message prosign and a friendly tail.

**Design fix (proposed):** Add a pause primitive to the CW assembly layer. Could be `--` (double-hyphen) at the prosign-source level, or a dedicated `CwPause(milliseconds)` API call inside `PlayCwSK` and friends. The pause should match the cadence of an end-of-sentence pause in the rest of the CW notification engine — likely 5-7 dot-lengths at the current WPM, matching standard `<BT>` cadence or natural human spacing.

**Where to fix:**
- `ScreenReaderOutput.PlayCwSK` and the underlying CW notification engine that owns prosign assembly. Likely `JJTrace` or `Radios` — needs to be located.
- Wherever `ee` gets appended after `SK` (commit `1f7a8e65` 2026-04-26).

**Priority:** Low. The current behavior is functional; it's a polish item. But Noel's a CW op and his ear catches this — worth fixing for credibility. Good Sprint 29 polish-pass slip-in or a one-off "audio polish" mini-sprint.

**Also covers:** any future prosign+character chain like `<AS>` for "stand by, will call" with following content; `<KN>` for "go ahead, named station only"; `<BT>` already implemented as a separator, but its pause length deserves audit while the fix is in flight.

**What MUST NOT regress when fixing this:** The `ee` (two single dits) at the close of `PlayCwSK` is a confirmed-working friendly-hand-wave touch that Noel verified by ear ("I hear a dit dit at close, that works"). The fix adds a pause BEFORE the `ee`, it does not remove or replace the `ee`. Test plan after the fix: send `73 SK ee` and confirm both (a) the pause between SK and EE is audible, AND (b) the EE itself is still there. Easy to accidentally drop the EE while restructuring the prosign assembly — the regression check matters.

**Status:** Logged 2026-04-27 from Noel's runtime observation while testing 4.1.16.208 SK close. Updated 2026-04-27 with the "preserve the EE" regression note after Noel reminded mid-session.

### NEEDS RE-VERIFY: Did PlayCwSK actually drop the `ee` close with no radio? (2026-04-28 Noel observation, partially retracted)

**2026-04-28 morning update:** Noel confirmed during morning testing that the dit-dit close DOES fire on app close. Original observation may have been a misreading or a different scenario. Keeping the entry as DEFERRED-pending-re-verification rather than deleting it — when next investigating CW prosign assembly, also test the "exit JJ Flex with no radio connected" path explicitly to confirm the `ee` is or isn't there. If it always fires, this entry can be deleted; if it ever fails, keep open.

### BUG (original, pending re-verify above): PlayCwSK drops the `ee` close entirely when no radio is connected (2026-04-28 Noel observation)

**Symptom:** With NO radio connected, the CW close at app exit (or wherever PlayCwSK fires) sends `73 SK` only — the friendly `ee` (dit-dit) hand-wave from commit `1f7a8e65` is missing. With a radio connected, the EE is present (just runs together with SK — see the related pause-prosign bug above). So the bug is *connection-state-dependent EE delivery*, not just the pause-cadence issue.

**Root cause (suspected):** Either (a) the EE-append code in PlayCwSK is gated on a radio-connected check that has no business being there — CW notifications play through local PC audio, not through the radio, so they should fire regardless of connection state — OR (b) there are two paths through the close handler (with-radio / without-radio) and only the with-radio path got the EE addition in `1f7a8e65`. (b) is the more likely shape since the original commit message frames the EE as a "friendly close" added on top of an existing prosign chain that may not have been the only chain.

**Design fix (proposed):** Locate the EE-append site, confirm whether it's behind a connection check OR whether there are two close paths. If gated: remove the gate (CW notifications are independent of radio state). If two paths: add the EE to the no-radio path so both close paths produce the same friendly tail.

**Where to fix:**
- `ScreenReaderOutput.PlayCwSK` and the close-path callers — find every place CW prosigns are emitted on app exit / SK announcement.
- Diff against commit `1f7a8e65` to see exactly where the EE was added and whether the addition was complete across both paths.

**Priority:** Low. Same priority class as the pause-prosign bug — a CW polish item. But it's a regression-from-intent (the EE was added to be heard always, not conditionally), so worth fixing in the same pass as the pause-prosign work.

**What MUST NOT regress when fixing this:** Both connect/disconnect close paths must produce the EE. Test plan: (1) launch JJFlex with no radio, exit — confirm EE present. (2) launch JJFlex, connect to a radio, exit — confirm EE present. (3) launch JJFlex, connect-then-disconnect, exit — confirm EE present. Three states, same outcome.

**Status:** Logged 2026-04-28 from Noel's runtime observation during 4.1.17 matrix C.4 testing.

### BUG: Modal connecting state has no escape path; user can't quit app while "connecting" (2026-04-28 Noel discovered during stuck slice-acquisition)

**Symptom:** When the app reaches a "stuck on connecting" state — actually a successful transport-level connection that's blocked on slice acquisition (slice in use by another op) — the connecting modal stays up indefinitely with no Escape, OK, Cancel, or window-close path that works. User had to force-kill via taskkill. Alt+F4 didn't work either.

**Severity:** **Critical accessibility bug.** A blind user with no visual cue that a dialog is modal cannot easily distinguish "the app is busy" from "the app is hung." When there's also no Escape-out path, the only options are kill-via-Task-Manager (assumes user knows that path) or force shutdown — both are violations of the friction-tax principle. **The app must ALWAYS be closable, especially for screen reader users.**

**Root cause (suspected):** The connecting modal probably absorbs Escape and Alt+F4 to prevent accidental dismissal during legitimate connection attempts, but doesn't transition to a "connected, waiting for slice" or "connecting timed out" state when the underlying connect succeeds but slice acquisition fails. The trace from tonight's session (`Trace-current-73sk-session.txt`) shows transport `Connected:True` at T+8.6s, but the UI remained in connecting state until force-kill at T+215s — 200+ seconds of unresponsive modal.

**Design fix (proposed):**
1. **Time-bounded modal:** the connecting dialog has a max wait (e.g., 60s); after that, transition to "Connecting timeout — Cancel / Wait / Quit" with explicit user choice.
2. **State-aware messaging:** when transport connects but slice acquisition fails, transition the modal text from "Connecting..." to "Connected, waiting for slice (currently in use by [station])." Different state, different UX.
3. **Always-honored Escape:** Escape on the connecting modal should ALWAYS cancel the in-flight connect attempt, return user to the radio selector. No silent ignore.
4. **Always-honored Alt+F4 / window close:** if user wants to quit, let them. Cancel any in-flight operation, dispose, exit cleanly.

**Where to fix:**
- `globals.vb` `_connectingForm` and surrounding logic (~line 2170 in `openTheRadio`)
- The connecting form itself — likely a WinForms `Form` with custom KeyDown handler
- Coordination with `RigControl.Start()` to allow cancellation from the UI

**Priority:** **HIGH.** Crosses the line from "annoying UX" to "user is trapped in a dialog they can't close." Promotes to a 4.1.17 candidate, not a Sprint 29 deferral.

**What MUST NOT regress when fixing this:** Successful connections must still close the modal cleanly; the modal must remain robust against accidental dismiss during a legitimate fast connect (don't introduce a "hit Escape too early and lose your progress" regression).

**Status:** Logged 2026-04-28 from Noel's runtime observation during stuck-slice-acquisition session at 1:32–1:36 AM. Trace preserved at `.investigation/2026-04-28-marks-radio-stuck/Trace-current-73sk-session.txt`. Promotes the design rule "every dialog must be Escape-closable, every state must be exit-able" to memory.

### BUG: Slice-acquisition failure presented as "stuck on connecting" instead of distinct state (2026-04-28 Noel discovery)

**Symptom:** When connecting to a remote radio whose slices are all in use (MultiFlex collision with another operator), the connect path *succeeds* at the transport layer but fails at slice acquisition. The UI remains in "connecting" state, the user has no idea a slice was the problem, and there's no path forward. Tonight's session: connected to Don's `6300inshack` at T+8.6s, slice acquisition failed at T+9.35s with `freeSlices=0, in use by k5ner1`, app sat for 204 seconds before user force-killed at T+215s. **k5ner1 disconnected at T+214s — Noel was 4 seconds away from the slice freeing up when he killed the app.** Sad timing.

**Root cause:** The state machine conflates "transport connecting" with "claiming a usable slice." Both are part of `Start()`, but they're different failure modes that deserve different UX. The current model: any `Start()` failure → user sees "still connecting" → user has no guidance.

**Design fix (proposed):**
1. **Distinct state name:** "Connecting" → "Connected, waiting for slice (in use by [other station])."
2. **Wait-or-give-up choice:** offer "Wait" (continue polling slice availability), "Try lower-power slice" (if available), or "Cancel" (disconnect and return to selector).
3. **Slice-freed-up notification:** when slice becomes available, speak it: "Slice now available — connecting" so the user knows progress is happening.
4. **Distinct earcon:** so the audio signal for "slice in use" is different from "still connecting." Same logic as distinct earcons for FeatureOn vs FeatureOff.

**Where to fix:**
- `Radios/FlexBase.cs` `Start()` method around lines 1099–1108 (where `couldn't get a slice` is logged)
- `globals.vb` openTheRadio retry path
- `JJFlexWpf` connecting form text + state transitions

**Priority:** Medium-high. Coexists with the "modal escape" bug above — both fixes likely land together since they touch the same dialog and state machine. Sprint 29 networking-fix candidate.

**What MUST NOT regress when fixing this:** The fast-success path (slice immediately available) must remain fast and not gain extra modal transitions for the common case.

**Status:** Logged 2026-04-28 with full trace evidence. Tonight's "k5ner1" was likely Don himself using his own radio at the moment Noel tried to join — a legitimate MultiFlex collision, not a bug-driven ghost.

### BUG: AS-retry-then-janky-reconnect pattern — secondary retry leaves the app in an unhealthy state (2026-04-28 Noel report, recurring)

**Symptom:** During a remote connect, user hears the AS prosign (CW) and "Connection slow, retrying" speech (often three times in succession). After retries, the connection completes, but the app is in a "janky" state: sometimes announces "K5NER disconnected" mid-connect, sometimes lands in a state with confused slice / station name handling, sometimes ends up needing another full disconnect-reconnect to function. Noel's specific Q1 from tonight: "*why does the secondary retry connection's causing problems?*"

**Root cause (preliminary investigation):**
- The AS prosign + "Connection slow, retrying" speech fires from `Radios/FlexBase.cs:1216–1226` when `Start()` times out waiting for the GUIClient station name to be set.
- Station-name wait timeout is 45 seconds (line 1135), with an early-abort path at 1 second if the client is removed during start without prior add (line 1137).
- On timeout, `Start()` returns false. Caller `openTheRadio` (in `globals.vb:2109–2161`) catches the false and retries via `RigControl.RetryConnect()` — up to 3 attempts.
- `RetryConnect` is the lightweight reconnect path (no Auth0 re-auth, reuses WAN session) introduced in Sprint 26 networking overhaul.
- **Suspected jank source:** the radio side may not have fully cleaned up from the prior `Disconnect()` call (line 1227) before the retry begins. The new attempt sees stale state, leading to confused station name / GUIClient state machines on the second pass.

**Investigation targets (Sprint 29 networking-fix sprint):**
- Trace a fresh AS-retry session end-to-end (need persistent trace archive — see Sprint 29 trace polish item).
- Compare time spent in each phase: pre-Sprint-26 vs post-Sprint-26 connect path. The "ragchew-keepalive-kerchunk" sprint added the station-name-wait, the early-abort heuristics, and the SmartLink re-add detection.
- Verify that `Disconnect()` at line 1227 actually cleans up radio-side state before the retry's `Connect()` begins. If not, the retry rebuilds against stale state.
- Specifically check: does the radio retain the prior GUIClient registration when the client is force-disconnected mid-station-name-wait? If yes, that's a leak that the retry then trips over.

**Design fix (proposed, pending investigation):**
1. **Wait for radio-side cleanup before retry.** After `Disconnect()` returns, wait for radio's GUIClient list to NOT contain our prior client ID before re-issuing Connect. Bounded by a sanity timeout.
2. **Consider longer station-name wait over WAN.** 45s might not be enough for slow-network conditions. But **don't just bump the timeout** — that masks the bug. Investigate why station name takes >45s on some sessions and not others.
3. **Make retry idempotent.** Each retry should fully reset state, not just re-issue commands against potentially-tainted shared state.

**Priority:** **HIGH.** Recurring symptom that wasn't this bad pre-Sprint-26+27. Confirmed regression-from-overhaul per Noel's historical observation.

**Status:** Logged 2026-04-28 from Noel's recurring observation + tonight's specific Q1. Pre-Sprint-26 historical context: "I'd only heard AS once (maybe). It was another type of unreliable. We had to do the network rewrite to support full UPNP, port forward, and hole punch." That's a clear regression marker.

### BUG: Connect path takes longer post-Sprint-26+27 networking overhaul (2026-04-28 Noel Q2)

**Symptom:** Connections take longer than they used to. Pre-overhaul, AS-retry was rare. Post-overhaul, AS-retry is frequent and connects routinely take >10 seconds end-to-end. Noel's specific Q2: "*why does it take so long to connect? What's it doing?*"

**Root cause (preliminary breakdown of Sprint 26+27 connect path latency budget):**
- TLS 1.2 handshake: ~200–500ms (was always there)
- SmartLink WAN session connect: ~300ms (line 173 of trace = T+4.825s; from T+4.621s connect attempt = 204ms — fast)
- Radio Connect message round-trip: ~480ms (T+6.062 to T+6.540, line 211–212)
- **`Start()` station-name wait: up to 45 seconds** (the new defensive wait added in Sprint 26)
- **`Start()` antenna list wait: up to 20 seconds** (line 1115 — also added in Sprint 26 era; comment says "5–15s typical over WAN")
- **`Start()` slice acquisition: variable** depending on radio state
- **NetworkTestRunner probe** issued at line 8783 of trace — runs in parallel, ~80ms — minor.

**The dominant cost is the station-name-wait** when SmartLink is slow to re-add the GUIClient on the radio side. Over WAN, this can take 30+ seconds (per the comment at line 1135: "*SmartLink re-add can take 30s+ over WAN*"). Tonight's session didn't show this because slice acquisition failed first (~T+9.35s).

**Investigation targets (Sprint 29 networking-fix sprint):**
- Profile pre-Sprint-26 connect path. Compare timing breakdowns.
- Verify whether the station-name-wait is necessary for ALL connections or only some. If it's a defensive wait only needed for SmartLink-re-add scenarios, can it be skipped for direct local connections?
- Investigate WHY SmartLink takes 30s+ to re-add GUIClient sometimes. Is the SmartLink server slow? Is JJFlex's auth dance triggering excessive re-validation? Is the new UPnP / hole-punch path adding round-trips?
- Quantify: median connect time pre-overhaul vs post-overhaul, with timing histograms.

**Design improvement candidates:**
1. **Adaptive timeout:** start with optimistic timeout (5–10s), extend on first failure rather than always defaulting to 45s. Fast networks pay only fast cost.
2. **Parallelize phases:** can antenna-list-wait and station-name-wait run concurrently? Today they're sequential.
3. **Skip phases that aren't needed for the use case:** SmartLink re-add detection only matters for a subset of connect scenarios.

**Priority:** **HIGH.** Foundation phase concern — slow + unreliable connects are worse than slow + reliable connects, which are worse than fast + reliable. We're at 1 today; we need 3.

**Status:** Logged 2026-04-28 from Noel's recurring observation. Tied to Sprint 26+27 networking overhaul ("ragchew-keepalive-kerchunk"). Pairs with the AS-retry-jank bug above — both will be investigated in the same Sprint 29 networking-fix work.

### BUG: KEY CONFLICT — Ctrl+F bound to both SetFreq and SpeakFrequency (2026-04-28 trace inspection)

**Symptom:** App startup trace logs `KEY CONFLICT: F, Control bound to SetFreq (Radio) AND SpeakFrequency (Radio)`. Two commands compete for the same key. Whichever wins lookup-order arbitration is the one that actually fires; the other is unreachable via that binding.

**Root cause:** `JJFlexWpf/KeyCommands.cs` has two binding entries:
- Line 940: `new(Keys.F | Keys.Control, CommandValues.SetFreq, KeyScope.Radio)` — entered Sprint 23 unified-dispatch era; opens freq-input dialog.
- Line 1057: `new(Keys.F | Keys.Control, CommandValues.SpeakFrequency, KeyScope.Radio)` — added after Sprint 15 with the comment "Speak frequency, Repeat last message."

Sprint 15 plan documents (`docs/planning/agile/archive/Sprint-15-pileup-dx-ragchew.md` line 116) document the original intent: `F` → speak frequency on freq fields (already implemented in `FreqOutHandlers.cs:425`/`2092`). The line-1057 binding was likely meant to be `Ctrl+Shift+F` (per `docs/planning/agile/archive/sprint21-22-test-findings.md:252`) but was typo'd as `Ctrl+F`, colliding with the existing Sprint 23 SetFreq binding.

**Fix (small, cleanly evidenced):** Change line 1057 from `Keys.F | Keys.Control` to `Keys.F | Keys.Control | Keys.Shift` to match Sprint 15+21 design intent. After the fix:
- `Ctrl+F` = Set Frequency (dialog) — restored unambiguous binding
- `Ctrl+Shift+F` = Speak Frequency (one-shot announcement from anywhere) — as originally designed
- `F` (on Home freq field) = Speak Frequency — unchanged (FreqOutHandlers handles it)

**Verification before applying:** Confirm `Ctrl+Shift+F` is currently unbound in the KeyTable (no other consumer). Sprint 23+ heavily uses `Ctrl+Shift+<letter>` for expander toggles (`N` DSP, `U` Audio, `R` Receiver, `X` Transmission, `A` Antenna, `B` Braille). `F` for Frequency-Speak fits the same naming pattern.

**Priority:** Low (cosmetic — startup warning) but **easy** (~1-line change). Good Sprint 29 slip-in candidate, or fix during Sprint 28's wrap. The startup-trace warning system is worth its weight in gold for surfacing this kind of thing — credit to Sprint 24 Phase 5 `ValidateKeyBindings()`.

**Status:** Logged 2026-04-28 after trace investigation found `KEY CONFLICT` line at startup of every session. The conflict has been silently in the codebase since Sprint 15+ era; the validator has been logging it; nobody read the trace until tonight's diagnostic exercise.

### BUG: Help dialog can't be exited with Escape; Alt+F4 is the only close path (2026-04-28 Noel during C.4h)

**Symptom:** Pressed F1 to open Help dialog. Help opens correctly. But Escape doesn't close it — the Help dialog absorbs Escape (or doesn't bind it) and stays open. Alt+F4 successfully closes the dialog.

**Severity:** Accessibility regression — every dialog should honor Escape per the design rule "every dialog must be Escape-closable, every state must be exit-able." Help is the most common dialog a user might want to dismiss quickly; making them remember Alt+F4 is friction-tax.

**Root cause (suspected):** The Help dialog (likely opened via `HelpLauncher.cs` or similar) probably uses `ShowDialog()` without an Escape-key handler, OR the Escape key is being absorbed by a focused control inside Help (a TextBox?) before reaching the form-level handler.

**Fix:** Add a window-level KeyDown handler on the Help dialog that catches Escape and calls `Close()`. If a child control absorbs Escape (e.g. text-search box), make it conditional — Escape clears the search first, then closes on second press.

**Where to fix:**
- `JJFlexWpf/HelpLauncher.cs` and the actual Help window XAML/code-behind.

**Priority:** Medium. Easy fix, common case, accessibility regression.

**Status:** Logged 2026-04-28 from Noel during C.4h. Surfaced during no-radio-state testing but is a general bug independent of radio state. Verified: F1 opens Help correctly even with no radio (Global scope, correctly bypasses no-radio guard).

### BUG: Dialog Escape produces "pane" announcement instead of restoring named focus (2026-04-28 Noel during C.4d)

**Symptom:** When pressing Escape to close a dialog (the freq-input dialog in C.4d), NVDA announced "pane" — meaning focus returned to a generic UI container rather than a specifically-named control.

**Root cause:** Common WPF focus-restoration issue. When a modal dialog closes, focus returns to the prior FocusScope owner. If that owner doesn't have an `AccessibleName` / `AutomationProperties.Name`, the screen reader reads the control type ("pane") instead of an identifiable name.

**Fix:** Identify which control receives focus after the freq-input dialog closes. Add `AutomationProperties.Name` to that control with a meaningful name (probably "Home" or the previously-focused field).

**Where to fix:**
- The freq-input dialog (`FreqInput`) close handler
- The control that receives post-close focus (likely `MainWindow` or a panel within it)

**Priority:** Low — cosmetic accessibility polish, doesn't block functionality. But it's the kind of small polish that cumulatively makes the app feel professional. Sprint 29 accessibility-polish sprint slot or a one-off tweak.

**Status:** Logged 2026-04-28 from Noel during C.4d. Same fix pattern would apply anywhere else in the app where dialog close → "pane" / "frame" / "group" generic announcement. May warrant a global accessibility audit for unnamed focus targets.

### BUG: Squelch Level should skip during arrow-nav when Squelch is off (matches RIT/XIT pattern) (2026-04-28 Noel during C.5/C.6)

**Symptom:** Arrow-navigating through Home fields, when Squelch is OFF, the arrow keys still force the user through the Squelch Level field (which displays a numeric level even when squelch is off, per the 2026-04-24 fix). Noel's request: "*when rit and xit it doesn't make you arrow through tuning, squelch should do the same, when off move on to next field.*"

**Design fix:** Mirror the RIT/XIT off-state behavior. When Squelch is off, arrow nav should skip Squelch Level (treat it as collapsed). When Squelch is on, the field is in the nav order normally.

**Where to fix:**
- `JJFlexWpf/Controls/FrequencyDisplay.xaml.cs` field navigation logic
- The same `IsPositionSensitive` helper (or sibling) that gained RIT/XIT awareness in commit `f6c00aa2` 2026-04-26.

**Priority:** Medium. UX consistency — pattern that's already established for RIT/XIT should extend to Squelch.

**Status:** Logged 2026-04-28 from Noel during C.5/C.6 arrow-nav testing.

### BUG: Slice and Slice Operations field announcements lack purpose-naming (2026-04-28 Noel during C.5/C.6/C.7/C.8)

**Symptom:** When focus lands on the Slice or Slice Operations field, the announcement is just "slice A" or "slice A operations volume 90" — runs together oddly and doesn't tell the user what kind of field they're on. Noel's suggestion: "*Slice A 'slice selector'? I know we say what value we're moving across, but slice and slice ops have multiple functions you can change. If anything we could say 'slice selector: slice A active.'*"

**Design proposal:**
- **Slice field:** announce as "Slice selector: slice A active" — labels the field as a *selector* (implying "use V to cycle, or letters to jump"), then the current value.
- **Slice Operations field:** announce as something that names its function. Slice Operations has multiple functions (mute toggle, pan, volume, A/T to set RX/TX, slice letter jump). Hard to summarize in one phrase. Possible: "Slice operations: slice A controls" — labels what the field controls. Then context help (F1) tells the user the available actions.

**Why this matters:** The current announcements assume the user already knows what each field does. New users get a confusing string ("slice A operations volume 90") that doesn't decompose into "this is the slice operations field, currently showing slice A's volume at 90." Renaming the field announcements to lead with purpose, then state, makes discovery easier.

**Where to fix:**
- `JJFlexWpf/Controls/FrequencyDisplay.xaml.cs` `NavigateToField` and field-key resolution — the speech for Slice and Slice Operations specifically.

**Priority:** Medium — UX clarity improvement, especially for new users. Pairs naturally with F1 context help refinement (also Sprint 29-ish).

**Status:** Logged 2026-04-28 from Noel during C.5/C.6/C.7/C.8 arrow-nav testing.

### BUG: Classic and Modern tuning modes have different field orders, no parity (2026-04-28 Noel observation)

**Symptom:** Arrow-nav through Home in Classic vs Modern produces different sequences. Classic has per-digit frequency sub-positions (1 MHz, 100 kHz, 10 kHz, 100 Hz, 10 Hz, 1 Hz) embedded between Frequency and Mute. Modern has just Frequency and Volume at that position. Field SET differences are by design (Classic exposes per-digit; Modern doesn't), but the **field ORDER** of the shared fields should be consistent.

**Noel's request:** "*Until we get 'customize home' we should mirror order on both classic and modern tuning modes.*"

**Design fix:** Align the field sequence so the shared fields appear in the same relative order in both modes. The per-digit Classic fields live within the Frequency field (logical sub-positions); they shouldn't shift the overall field order, only expand within Frequency.

**Where to fix:**
- `JJFlexWpf/FreqOutHandlers.cs` — `SetupFreqOut` (Classic) vs `SetupFreqoutModern` (Modern) sequences.

**Priority:** Medium — consistency is foundational; user shouldn't have to relearn the spatial model when switching modes. Sprint 29 Home-polish slip-in candidate, or hold for Customize Home (Sprint 30+).

**Status:** Logged 2026-04-28 from Noel during C.5/C.6/C.7/C.8 testing. Customize Home (Sprint 30+) will eventually let user pick the order, but until then, parity by default reduces cognitive load.

### BUG: Universal Home keys (R/M/X/Q/V/=) silent when pressed outside Home with no radio (2026-04-28 Noel during C.4f)

**Symptom:** From outside the Home (no Home field has focus), with no radio connected, pressing `R`, `M`, `X`, `Q`, `V`, or `=` produces no audible feedback. These keys are field-handler-bound (in `FreqOutHandlers.cs`), so they only fire when focus is on a Home field. With no radio, focus can't be on a Home field (the FrequencyDisplay's `_fieldDict` is empty). So the keystrokes hit no handler and silently do nothing.

**Design challenge:** A simple "add a global binding for R/M/X/Q/V/=" would conflict with field-handler routing when a radio IS connected and focus IS on a Home field. The field handler runs first because focus claims the keystroke before the global dispatcher sees it. So the no-radio-fallback binding wouldn't fire when on a field.

**Design fix (proposed):**
1. **Add a window-level `PreviewKeyDown` check** for these specific letter keys: if `RigControl == null` (no radio), speak no-radio guidance and consume the key.
2. **Trigger order:** the check fires BEFORE the field-handler routing, so it wins by virtue of being at the WPF Window scope. With a radio connected, skip — let normal routing happen.
3. **Helper:** reuse `ScreenReaderOutput.SpeakNoRadioConnected()` from the no-radio guard work.

**Alternative fix:** Add these keys to the global KeyTable with `KeyScope.Radio` and let the no-radio dispatcher guard handle them. Drawback: when the radio IS connected, the global handler would fire instead of the field-specific handler unless the field-specific handler explicitly consumes the keystroke first. Need to verify ordering before choosing this path.

**Where to fix:**
- `JJFlexWpf/MainWindow.xaml.cs` `MainWindow_PreviewKeyDown` — add a no-radio + universal-Home-key check between the existing meta-commands and the registry dispatch.
- OR: `JJFlexWpf/KeyCommands.cs` global table additions, depending on chosen approach.

**Priority:** Medium. Per the "no silent keystrokes" rule (`memory/project_no_silent_keystrokes_rule.md`), every bound key must produce audible feedback in every reachable state. This fix completes the no-radio coverage that the dispatcher guard already provides for KeyTable-routed keys.

**Status:** Logged 2026-04-28 from Noel during C.4f. The no-radio guard work this evening covered KeyTable-routed Radio-scope commands; this entry is the field-handler-routed sibling that the audit's Tier 3 declared "naturally protected" but isn't fully protected when the user presses these keys outside Home.

### BUG: F2 announcement may have lost "Home" prefix in connected state (2026-04-28 Noel observation, NEEDS VERIFICATION)

**Symptom:** Noel reports that with the radio connected, pressing F2 produces "frequency 3.961.000 coarse 1 kilohertz" or similar — without the "JJ Flexible Home" or "Home" destination prefix that C.1 confirmed working on 2026-04-26. This may be a regression from tonight's `FocusFrequencyField` patch, OR it may be a test-setup difference (focus was already on Home so no transition triggered the prefix).

**Verification needed:** Re-test with the C.1 technique — focus a non-Home control (open menu bar, then Esc), then press F2. If the prefix returns, this is not a regression — the prefix is gated on focus transition, which is by design. If the prefix does NOT return, this IS a regression and the patch must be revisited.

**Suspected cause if regression:** `FocusFrequencyField` patch added a `return` after `NavigateToField(freqField)` in the connected-radio path. If `NavigateToField` was previously falling through to additional speech logic that included the Home-destination prefix, the early return short-circuited it.

**Where to investigate:**
- `JJFlexWpf/Controls/FrequencyDisplay.xaml.cs` `FocusFrequencyField` (current, post-patch) and `NavigateToField` (the destination-prefix code path).

**Priority:** Medium-high if confirmed as regression — would want to fix before any further releases. Verify first thing tomorrow.

**Status:** Logged 2026-04-28 from Noel as a possible-regression-or-test-setup-issue. Top priority for tomorrow's first action: re-test with menu-bar-bounce technique to disambiguate.

### DESIGN: No-radio guard should opt-out per-command — let SetFreq dialog open even with no radio (2026-04-28 Noel feedback)

**Symptom / proposal:** With the 2026-04-28 no-radio guard, pressing `Ctrl+F` produces "JJ Flexible Home, no radio connected" and the freq-input dialog does NOT open. Noel's pushback: *"you should be able to enter in text into a frequency, the JJ Flexible home no radio connected should complain at you if you try to tune. If you enter an easter egg / cqtest, it should let that through."*

**Design challenge:** The current guard is a uniform "all Radio-scope commands blocked when no radio is connected." But some commands have legitimate uses without a radio:
- **SetFreq** — opens a text-input dialog. User might enter an easter egg string (`cqtest`, etc.) that should be processed independent of radio state. Or user might just want to type a frequency to remember it. Or test-typing.
- **Memory dialog** — viewing saved memories doesn't require a connected radio.
- **Log dialogs** — already Logging-scope, not Radio-scope. Not affected.
- **Verbosity / help / status** — already Global-scope. Not affected.

**Design fix (proposed):** Add a per-command opt-out flag on `KeyTableEntry` — `RunsWithoutRadio` boolean (default false for Radio-scope commands). For commands that opt-in (SetFreq, ShowMemory, etc.):
- The dispatcher guard at `KeyCommands.DoCommand:1677` skips the no-radio block.
- The handler runs.
- The HANDLER itself decides what to do with no radio — open the dialog, accept input, decide what to do with that input. For SetFreq: dialog opens, user enters text, on OK/apply the dialog handler announces "no radio, can't tune" UNLESS the input matches an easter-egg / special command pattern.

**Where to fix:**
- `Radios/KeyCommandTypes.cs` — add `RunsWithoutRadio` field on `KeyTableEntry`
- `JJFlexWpf/KeyCommands.cs:107` — mark `SetFreq` (and others to be decided with Noel) as `RunsWithoutRadio = true`
- `JJFlexWpf/KeyCommands.cs:1677-` — extend the no-radio block to skip if `kt.RunsWithoutRadio`
- `globals.vb WriteFreq` (the SetFreq handler) — handle the no-radio case at apply time: speak "no radio, can't tune" OR process easter eggs as appropriate

**Priority:** Medium. The current guard is a defensible default but Noel's framing is correct — uniform blocking is too aggressive for input-accepting commands. Worth fixing during 4.1.17 polish since it's a UX regression I introduced.

**Decision needed from Noel:** which other commands should opt-in to `RunsWithoutRadio = true`? Suggested candidates: ShowMemory (view memories without radio), CycleVerbosity (already Global, fine), Help (already Global), but the call belongs to Noel.

**Status:** Logged 2026-04-28 morning from Noel's response to the C.4d-with-DoCommand-fix test.

### DESIGN: No-radio announcement should be action-aware — say what couldn't happen (2026-04-28 Noel feedback)

**Symptom / proposal:** The current no-radio announcement is generic: "JJ Flexible Home, no radio connected." Noel's feedback during band-key test: *"probably be best to be more verbose for verbosity and say 'unable to change band, JJ Flexible Home no radio connected.'"*

**Design fix (proposed):** Replace the single `SpeakNoRadioConnected()` helper with a per-action variant that prepends what couldn't happen. Options:
1. **Per-command label.** Each command has a "what would have happened" label ("change band", "set frequency", "tune", "toggle RIT"). The helper takes that label and produces "Unable to [label], JJ Flexible Home no radio connected."
2. **Verbosity-scaled.** At Terse, just "[label], no radio." At Chatty, the full "Unable to [label], JJ Flexible Home no radio connected." At Off (Critical), "[label], no radio" or even just "no radio."
3. **Reuse existing description fields.** `KeyTableEntry.Description` already exists ("Jump to 60 meter band", "Toggle tune carrier on or off", etc.). The no-radio helper can pull from there.

**Where to fix:**
- `Radios/ScreenReaderOutput.cs` — extend `SpeakNoRadioConnected` to take an optional action label
- `JJFlexWpf/KeyCommands.cs:1677-` — pass `kt.Description` (or a derived "what this would have done" label) to the helper

**Priority:** Medium-low. UX polish on top of the working guard. Improves discoverability ("oh, that key would have changed bands") but the basic no-radio feedback is already correct.

**Status:** Logged 2026-04-28 morning from Noel's response after band-key test passed but felt under-informative.

### FEATURE: Disconnect notification — speech + CW prosign when radio disconnects (2026-04-28 Noel proposal)

**Proposal:** When the radio disconnects (user-initiated, network drop, or radio side disconnect), JJ Flex should announce it. Today there's no clear notification. Noel's framing: *"radio disconnect should probably give a CW disconnect / speech should give some kind of notification it has disconnected. Maybe 'jjf DC k' in CW, 'JJ Flexible disconnected from radio' in speech."*

**Design proposal:**
1. **Speech:** "JJ Flexible disconnected from radio" (Critical-level — connection state). Verbosity-aware variants:
   - **Chatty:** "JJ Flexible disconnected from radio. Press Ctrl+R to reconnect." (or whatever the reconnect key becomes)
   - **Terse:** "Disconnected from radio."
   - **Off:** "Disconnected." (still spoken — Critical-level)
2. **CW prosign:** A brief "DC" or "DC K" (or "BK" for "back to you") sequence. Noel suggested `JJF DC K`. Worth confirming the desired CW with Noel — there's a tradition of using `73 SK` for end-of-conversation; `BK` is "back to you"; `DC` isn't standard but could be a JJ-Flex-specific shorthand. Or use the AS prosign (already in the engine) since AS = "wait" / "standby" semantically aligns with disconnect. Or invent a JJ-specific pattern.
3. **Earcon:** A descending tone (matches the "client disconnected" earcon already in MultiFlex code at `ScreenReaderOutput.cs:330`). Possibly reuse `PlayClientDisconnectedEarcon`.

**Where to fix:**
- `Radios/FlexBase.cs` `Disconnect()` method (around line 1245) — fire the speech + CW + earcon when disconnect is initiated
- May need to distinguish user-initiated disconnect vs unexpected disconnect (different announcements). User-initiated = "disconnected from radio." Unexpected = "connection lost" + perhaps a more urgent earcon.

**Priority:** Medium. Currently an audible feedback gap — disconnects happen silently in some paths. Pairs well with the connection-event-speech race fixes already in TODO (line 62).

**Decision needed from Noel:** desired CW prosign for disconnect. Options listed above; Noel's call.

**Status:** Logged 2026-04-28 morning from Noel's proposal during morning testing.

### BUG: build-installers.bat release mode silently skips NAS installer publish (2026-04-19 discovered during 4.1.16 release)

**Symptom:** `build-installers.bat both release` reports "Publish complete (RELEASE)" but NAS `historical\<ver>\installers\` and `stable\` do not receive the release installer .exe files. A subtle warning "`Installer not found at C:\dev\JJFlex-NG\.\Setup JJFlex_<ver>_x64,x86.exe -- skipping.`" is printed but easy to miss in the hundreds of compile warnings.

**Root cause:** the .bat passes architecture as `"x64,x86"` (comma-joined string) to `publish-to-nas.ps1 -Archs`. PowerShell's parameter binder interprets that as a single-element `[string[]]` containing the literal string "x64,x86" rather than a two-element array `@('x64','x86')`. Filename interpolation then produces non-existent names like `Setup JJFlex_4.1.16.44_x64,x86.exe`, which don't match on disk, and the script skips without erroring.

**Workaround until fixed:** after `build-installers.bat both release` completes, manually run:
```powershell
.\publish-to-nas.ps1 -ProjectRoot 'C:\dev\JJFlex-NG' -Version '<FULL.4.PART.VERSION>' -Archs @('x64','x86') -Release
```

**Fix:** change the .bat's `publish-to-nas.ps1` invocation to either (a) call twice with one arch each, (b) pass each arch as a separate positional arg, or (c) pass `-Archs x64,x86` without quoting so PowerShell parses them as array elements. Needs local test with both `x64`, `x86`, and `both` modes to confirm all still work.

**Priority:** Medium. Release publishes succeed locally and can be cleaned up manually post-hoc, but every release currently requires remembering to run the publish step by hand. Good Sprint 27 slip-in.

**Status:** Logged 2026-04-19 after 4.1.16.44 release surfaced it.

### BUG: Connection-event speech announces "no active slice" prematurely (2026-04-26 fartsnoodle, Noel)

**Symptom:** When connecting to a Flex (Noel observed on 6300), screen reader announces "Connected to Flex 6300, no active slice." The "no active slice" portion is a transient truth — slice data hasn't propagated yet at the moment of the connection event. Once slices populate (a beat later), the active slice does exist. The announcement is misleading and confusing.

**Root cause (suspected):** the connection-event handler (likely `MainWindow.ConnectedEventHandler` or similar; needs verification) speaks slice status synchronously on the connect event, before FlexLib has finished pushing slice creation events for the radio. Same shape as the Command Finder pre-deferral bug fixed in `3893082b` — "speak too early, the state isn't ready yet" pattern.

**Fix:** defer the slice-portion of the connection announcement until first slice update arrives, OR check `theRadio.AvailableSlices > 0` (or equivalent) before speaking; OR speak only the radio identity in the connect event and let the slice arrival event own the slice announcement.

**Priority:** Medium-low. Cosmetic confusion, not a functional break. Worth bundling with the next round of slice/connection event-ordering work.

**Status:** Logged 2026-04-26 from Noel's runtime fartsnoodle while testing universal-keys field audit.

### BUG: RIT/XIT toggle from sub-position orphans cursor (2026-04-26 fartsnoodle, Noel)

**Symptom:** With RIT (or XIT) on, user arrows into one of the field's expanded sub-positions (e.g., the 10 Hz adjustment digit), then toggles RIT off via either Space (existing field-specific behaviour) or R (universal helper added 2026-04-26). The field collapses — sub-positions disappear because there's no value to display when RIT is off. Arrow-left and arrow-right then do not navigate (no speech, possibly no visual cursor movement either).

**Root cause:** when RIT toggles off, the field's effective length shrinks. The cursor's `posInField` is now out of range relative to the new field structure. Arrow-key handlers in the underlying `FrequencyDisplay` control silently do nothing for out-of-range positions instead of clamping or wrapping.

**Pre-existing, NOT a regression:** the universal R (added in commit `4cf73823`) takes the same toggle path as Space (which has been there for many sprints). Confirmed by Noel: Space exhibits identical behaviour. The universal R just makes the bug more discoverable because users who learned Space-from-field-start now have a second key (R) that they might press from deep inside the expanded field.

**Fix options:**
- (A) When RIT/XIT toggles off via any path (Space or R or universal), reset the cursor to position 0 of the field (i.e. land on the on/off indicator). Simple, predictable.
- (B) When the field shrinks, clamp `SelectionStart` to the new max position. More general fix; covers any future field-shrink scenario.
- (C) Always show RIT/XIT field with at least the on/off indicator and a placeholder value position (e.g., "OFF" + "+0000"), so the structure never shrinks. UX impact — would change the read-aloud verbosity.

Option (A) is smallest-blast-radius and matches existing keep-cursor-sane patterns elsewhere in the codebase. Option (B) is the right architectural fix if FrequencyDisplay's selection model is shared across multiple variable-length fields.

**Priority:** Medium. Workaround is "press Space/R from outside the expanded field," which most users already do. But the universal-keys promise means more users will hit this once they discover R works from deep positions.

**Status:** Logged 2026-04-26 from Noel's runtime fartsnoodle while testing universal-keys field audit (Test 3 follow-up).

**Related sub-symptom (same root cause, observed Test 4 setup 2026-04-26):** When RIT (or XIT) is OFF, arrowing RIGHT from RIT through the field's 5-char width announces 3-5 "nothing" sub-positions before reaching XIT. Arrowing LEFT from XIT skips those sub-positions and lands directly on RIT. Same fix as above (clamp navigable positions to populated ones in both directions, OR collapse the field width to 1 when RIT/XIT is off) resolves this asymmetry too.

### BUG: SetupFreqoutModern doc comment is stale (2026-04-26 spotted during fartsnoodle)

**Symptom:** The summary comment for `SetupFreqoutModern` at `JJFlexWpf/MainWindow.xaml.cs:1383-1387` claims:
> Modern mode: simplified field set — Freq + Slice + SMeter only. ... Other controls (Mute, Volume, Split, VOX, RIT, XIT) accessible via Slice menu.

Actual Modern field list (lines 1406-1436) is: Slice, SliceOps, Freq, SMeter, Squelch, SquelchLevel, Split, VOX, Offset, RIT, XIT. The comment was written before Sprint 26 Phase 8 added the checkbox-field mirroring; it never got updated.

**Fix:** rewrite the doc comment to match current reality. Note that Mute and Volume are still legitimately omitted from Modern (universal `M` covers mute, Slice menu covers volume), but the rest of the comment is wrong.

**Priority:** Low (cosmetic — incorrect comment misleads future contributors but doesn't affect runtime). Good drive-by cleanup next time anyone touches `SetupFreqoutModern`.

**Status:** Logged 2026-04-26 from Noel's runtime fartsnoodle.

### BUG: Step-entry mode — `+` and `-` are identically-behaved (2026-04-26 Noel during C.7i regression test)

**Symptom:** In Frequency-field step-entry mode (engaged via `+` = Shift+OemPlus), `-` (Shift+OemMinus or just OemMinus → '-' from KeyToChar) is treated identically to `+`. Both re-enter step-entry, reset `_stepMultiplier = 1` and clear `_stepBuffer`. Concrete example: press `+5` to set step to 5 kHz, then press `-1` — multiplier announces as "Step 1 kHz" instead of "Step 4 kHz" (Noel's expected decrement) or an error/notice.

**Root cause:** `JJFlexWpf/FreqOutHandlers.cs:295` AdjustFreq default block:
```csharp
if (ch == '+' || ch == '-')
{
    _inStepEntry = true;
    _stepBuffer = "";
    _stepMultiplier = 1;
    Radios.ScreenReaderOutput.Speak("Step entry");
    e.Handled = true;
}
```
The `||` was probably intended to give `-` a separate behavior that never got implemented, or the author conflated "user starting step entry" with both signs. There's no semantic concept of a negative step multiplier in tuning — Down arrow already tunes negatively. So `-` arguably shouldn't enter step-entry at all.

**Fix options:**
- (A) Remove `-` from the `if`, so only `+` enters step-entry. `-` becomes an error / notice ("Step entry uses + only" or similar). Cleanest.
- (B) Make `-` decrement an existing multiplier (Noel's first guess: `+5` then `-1` → step 4). Adds state-machine complexity. Negative results (step ≤ 0) would need a clamp + announce ("step 0, exited step entry" or reset to 1).
- (C) Make `-` a no-op when in step-entry (silent ignore). Less disruptive than error but loses discoverability.

**Recommendation:** Option (A) — error/notice. Aligns with the principle that `-` has no tuning-step semantics and surfaces the correct affordance to the user.

**Priority:** Low (cosmetic/UX — not a correctness bug, the radio still tunes correctly). Sprint 28 wrap polish or Sprint 29 candidate.

**Status:** Logged 2026-04-26 — surfaced during 4.1.17 matrix test C.7i.

### BUG: RIT/XIT `+`/`-` press is silent — no announcement of the abs-value sign change (2026-04-26 Noel during C.7j)

**Symptom:** From the RIT (or XIT) field, pressing `+` calls `Math.Abs(value)` (force positive); pressing `-` calls `-Math.Abs(value)` (force negative). Both DO modify the value as appropriate but emit ZERO speech announcement, so the user has no audible feedback that anything happened. Discoverability is also broken — Noel didn't even know these keys did anything until C.7j directed him to test it.

**Location:** `JJFlexWpf/FreqOutHandlers.cs:1467` (the `+` / `-` handlers in AdjustRITXIT default block).

**Action terminology:** strictly, `+` is "make positive" (abs) and `-` is "make negative" (-abs). NOT "invert" — pressing `+` from already-positive is a no-op (still positive). Announcement text should reflect the actual action so screen-reader users build a correct mental model.

**Two candidate announcement patterns** (Noel's preferences captured 2026-04-26 — pick one at fix time):

**Option A — "made positive/negative" (action-verb explicit):**
- Terse: `"RIT made positive, 30 hertz"` / `"RIT made negative, -30 hertz"`
- Chatty: `"RIT offset made positive: 30 hertz, was -30"` (delta only when value changed)

**Option B — "sign positive/negative" (closer to Noel's original phrasing):**
- Terse: `"RIT sign positive, 30 hertz"` / `"RIT sign negative, -30 hertz"`
- Chatty: `"RIT offset sign positive: 30 hertz, was -30"`

Either pattern handles the no-change case gracefully — just announce the result, omit the "was X" delta when the value didn't change. Same pattern applies to XIT field (`isRIT = false` branch).

**Priority:** Medium — discoverability bug for a feature already shipped in JJFlex. Users currently can't find this functionality without reading source. Cheap fix (~6 lines including chatty/terse split).

**Status:** Logged 2026-04-26 — surfaced during 4.1.17 matrix test C.7j.

### BUG: `+` and `-` keys have three different meanings across Home fields (2026-04-26 Noel observation, not yet a customer complaint)

**Symptom:** Across the Home fields, `+` (and `-`) have inconsistent semantics:
1. **Frequency field:** `+` enters step-entry mode (announces "Step entry"). `-` does the same (per the separate "Step-entry +/- symmetry" bug above).
2. **RIT/XIT field:** `+` is `Math.Abs(value)` (force positive). `-` is `-Math.Abs(value)` (force negative). Currently silent (per the separate announcement bug above).
3. **Offset field:** `+` sets offset direction to plus (FM repeater offset). `-` sets it to minus.

None of them mean "add 1 kHz" or "increment by N." Up/Down arrow is the universal "increment" semantic. `+`/`-` is field-specific and the meanings don't share an axis.

**Why it might be fine:** field-specific behavior is a JJ Radio heritage feature. Each field has its own affordance set. Users learn per-field. Help docs can document the semantics. The inconsistency is "honest" in that each field's `+` does something semantically appropriate to that field (step in Freq, sign in RIT/XIT, direction in Offset).

**Why it might NOT be fine:** screen-reader users especially benefit from key-meaning being predictable across contexts. "What does `+` do here?" shouldn't be a per-field question. Could rationalize: e.g., reserve `+`/`-` for one universal meaning + use other keys for field-specific actions.

**Decision needed:** rationalize or accept. No correctness bug; pure UX-consistency question. Noel surfaced this 2026-04-26 during C.7j testing as an observation, not as a customer complaint. Defer to a deliberate Sprint 30+ UX-review pass.

**Priority:** Low (pure UX question, not a bug). Accumulate user feedback over time before deciding.

**Status:** Logged 2026-04-26 — surfaced during 4.1.17 matrix test C.7j observation.

### BUG: Universal `V` cycle key does not wrap from last slice back to first (2026-04-26 Noel during C.7g)

**Symptom:** Pressing `V` cycles to the next slice but **stops at the last slice** instead of wrapping around to the first. Noel: *"Pressing V does go to the next slice, shouldn't a press of it again cycle you back to the first slice?"* Same behavior in both Classic and Modern tuning modes.

**Root cause:** `JJFlexWpf/FreqOutHandlers.cs:859` — `CycleVFO(int direction, bool wrap = false)` defaults `wrap = false`. The universal `V` handler in TryHandleUniversalHomeKey calls `CycleVFO(1)` with the default no-wrap behavior. Same in AdjustFreq's V handler at line 339.

**Why wrap is correct here:** the function is literally named `CycleVFO` — "cycle" implies round-trip. Industry convention: Tab cycles controls (wraps), Alt+Tab cycles windows (wraps), Ctrl+Tab cycles tabs (wraps). Cycle keys wrap; navigation arrows clamp. The current no-wrap behavior makes V behave like an arrow key, defeating the purpose of having a separate cycle affordance. With 3+ slices, clamping at the end forces the user to use a different key (or arrow back) to return to the first slice — needless detour.

**Fix:** change V handlers to call `CycleVFO(1, wrap: true)`:
- `JJFlexWpf/FreqOutHandlers.cs` line ~339 (AdjustFreq's V handler)
- `JJFlexWpf/FreqOutHandlers.cs` line in `TryHandleUniversalHomeKey` (universal V handler added 2026-04-26 in commit `4cf73823`)
- Verify any other V call sites use the same pattern

**Future polish:** consider `Shift+V` for reverse cycle (B→A→last). Not blocking; defer to a UX-pass that decides the full cycle-key family.

**Priority:** Medium-low. Functionally V works (forward direction); wrap is the polish that completes the cycle semantic. Cheap fix (~2 lines).

**Status:** Logged 2026-04-26 — surfaced during 4.1.17 matrix test C.7g.

### FEATURE: Universal `=` transceive should be a TOGGLE with memory of prior split TX (2026-04-26 Noel during C.7h)

**Symptom:** Pressing `=` from any Home field sets `Rig.TXVFO = Rig.RXVFO` — RX and TX both point to the same slice (transceive mode, exiting split). Works correctly. **But there's no inverse:** pressing `=` again does nothing meaningful (TX is already equal to RX, so re-equalizing is a no-op). To "turn off transceive" the user must manually navigate to Slice Operations and set TX back to a different slice — high friction, especially mid-QSO.

Noel: *"What if I wanted to set it to receive i.e. turn off transceive? Shouldn't that toggle?"*

**Why this matters for ham operators:** split-mode operation (TX on different slice/freq from RX) is common during DX pileups, contests, and any contact where the other station is listening on a different frequency. Operators flip between split (TX on slice B) and transceive (TX on slice A = RX) frequently within a single contact. A symmetric toggle key would match the workflow exactly.

**Three design options:**

**(A) `=` becomes a true toggle WITH memory of prior split TX:**
- First press: save current `TXVFO` → `_priorSplitTxVfo`. Set `TXVFO = RXVFO`. Announce "Slice A transceive" (current).
- Second press: if `_priorSplitTxVfo` is valid + still exists, restore `TXVFO = _priorSplitTxVfo`. Announce "Split, RX slice A, TX slice B" (or similar). If prior TX is no longer valid (slice was released), fall back to "Cannot restore split, TX stays on slice A" or similar.
- State: one int field on `FreqOutHandlers` or stored on `Rig`. ~10 lines of code.
- **This is the right answer for ham workflow** — matches how operators actually use split.

**(B) Separate `Shift+=` for "exit transceive":**
- `=` still does set-transceive (no change).
- `Shift+=` (which is `+`) — but `+` already means step-entry from Freq, abs-value from RIT. Conflict.
- Could pick a different modifier: `Ctrl+=`. Less ergonomic than (A).

**(C) Accept as-is:**
- Document that `=` is one-way set, "exit transceive" requires manual TX navigation.
- Lower work, worse UX. Not recommended for shipping ham product.

**Recommendation:** Option (A). Memory-toggle is the natural ham workflow.

**Edge cases for (A):**
- First-ever press of `=` (no prior split TX saved): no special handling, just set transceive (matches today).
- Prior split TX slice was released between two `=` presses: announce "Cannot restore split — slice X no longer exists" and stay on transceive.
- Multiple slices in split: do we save the entire prior config or just the TX slice? Just TX slice is simpler and covers the 95% case.
- The `_priorSplitTxVfo` should clear when user manually changes TX (so we don't restore a stale value after they've gone elsewhere).

**Priority:** Medium — quality-of-life improvement for split-mode operators. Not blocking 4.1.17. Good Sprint 28 polish or Sprint 29 candidate.

**Status:** Logged 2026-04-26 — surfaced during 4.1.17 matrix test C.7h.

### BUG: Letter slice-jump (a-h) inconsistent between Classic and Modern from Slice Operations field (2026-04-26 Noel)

**Symptom:** From the **Slice field**, letters a-h and digits 0-7 both jump to the named slice — works correctly in both Classic and Modern. From the **Slice Operations field**, behavior diverges by mode:
- **Classic tuning mode:** pressing `a` does NOTHING (silent no-op)
- **Modern tuning mode:** pressing `a` switches to slice A (jump works)

**Inconsistency is the bug** — same field, same key, different behavior across modes. Either both should jump, or both should be silent.

**Recommendation:** make BOTH modes jump (Modern's behavior is the more useful default — letter-jump is a discoverable affordance worth preserving). Investigation: find the Slice Operations key handler in `JJFlexWpf/FreqOutHandlers.cs` (search for `AdjustSliceOps`); compare Classic vs Modern code paths for the letter case.

**Priority:** Medium — UX consistency bug. Cheap fix once located. Sprint 28 polish or Sprint 29.

**Status:** Logged 2026-04-26 — surfaced during 4.1.17 matrix test C.7-series exploration.

### FEATURE: Universal slice-jump from any Home field (a-h letter jump or modifier-letter combo) (2026-04-26 Noel design discussion)

**Workflow Noel described:** "Go to slice operations, go to VOX, set VOX, press 'the key' or `a`, set VOX on in slice A, but since you're near it, set RIT on slice A to something cool, then you could switch to B." The pattern is **rapid per-slice multi-field configuration** — operator wants to set VOX, RIT, mute, etc. for slice A, then hop to slice B and do the same, without traversing back to the Slice field every time.

**Today's slice-navigation tools:**
- `V` cycles forward through slices (no wrap — see separate TODO entry)
- Numbers 0-7 + letters a-h jump to slice from the **Slice field only** (not universal)
- No keyboard equivalent of "jump to slice X while staying on current field"

**Recommended design (refined 2026-04-26 with Noel):**

**`Ctrl+J Shift+A` through `Ctrl+J Shift+H` for universal slice jump.** The leader-key prefix `Ctrl+J` should be reframed cognitively as **"jump to"** — the principle: Ctrl+J + an uppercase letter targets something; Ctrl+J + a lowercase letter is an action toggle. Two semantic spaces in the same leader, no new prefix needed.

**Subsystems:**

1. **Radio-aware filtering** (using `theRadio.MaxSlices`): help-list "what's available here?" dynamically filters to slice letters the radio supports. 6300 user sees A and B; 6700 user sees A-H. Pressing an unavailable slice (e.g., `Ctrl+J Shift+E` on a 4-slice 6500): announce **"Slice E not available on this radio — max 4 slices"** and don't fail silently.

2. **Auto-create on jump-to-uncreated-slice** (default behavior, not prompt-to-confirm):
    - Speech: **"Created slice C, now active"** (announces the create-side-effect)
    - **Distinct earcon** for "created new slice" vs plain "switched to existing" (e.g., ascending three-tone vs. plain switch tone) — gives audible signal of what just happened
    - Setting toggle: **"Confirm before creating slices on jump"** — default OFF (matches expert ham workflow); cautious users can opt in to a Y/N prompt
    - Rationale: ham workflow is fast — interrupting with confirmation every jump = friction. Distinct earcon + speech is enough discoverability without prompts.

3. **Other "jump to" candidates that fit the same Ctrl+J frame** (future expansions):
    - `Ctrl+J Shift+F` → jump to Frequency field from anywhere
    - `Ctrl+J Shift+M` → jump to Mute field
    - `Ctrl+J Shift+L` → jump to log entry
    - Specifically NOT `Ctrl+J Shift+A` etc. for those — those are reserved for slice jump. Field-jump targets pick distinct letters from slice letters.

**Why this refinement supersedes the earlier (A)/(B)/(C) options:** the `Ctrl+J Shift+letter` design combines the namespace-clean property of (B) with the speed-of-direct-jump property of (A), while reframing the leader-key meaning to support future jump-target families. (C)'s quasi-modal pattern stays as a fallback if collision audits show the leader-key space can't be expanded.

**Concerns to verify before implementing:**
- `Ctrl+J Shift+S` might collide with `Ctrl+J S` (Spectral NR) if the keymap doesn't case-distinguish modifier states. Check `KeyCommands.vb` for whether leader-followup is case-sensitive.
- All 8 of `Ctrl+J Shift+A` through `Ctrl+J Shift+H` must be unbound currently. Quick grep would confirm.
- Existing `Ctrl+J Shift+R` may be PC-side NR (per memory `project_sprint29_updater_vision.md` references). If yes, that conflict shows the case-distinction works AND that slice R needs a different binding (or PC-side NR moves).

**Multi-channel feedback for layer entry (refined 2026-04-26 with Noel):**

Aligns with `project_multi_braille_output_vision.md` and `project_friction_tax_principle.md`: never assume a user has audio, speech, OR braille only — fan out to all configured channels.

- **Speech (default + chatty):** terse `"JJ"`, moderate/chatty `"JJ Jump"` — speaks layer name on entry
- **Earcon:** distinctive ascending tone (different from existing leader-key earcons — this is a *layer entry*, not just a key press)
- **Braille:** comes for free via NVDA/JAWS pushing speech text to attached braille displays. Future enhancement (per `project_multi_braille_output_vision.md`): dedicated "status braille line" updates on layer entry too.
- **Discoverability speech (after timeout or on `?`):** `"JJ Jump layer. Press a letter for jump target. Question mark to list options."` — radio-filtered list when user explicitly asks.

**Layer-help affordances:**

- **`?` (Shift+/) within JJ Jump layer = list available targets dynamically.** Speech: `"Slices A through [N]. F for Frequency. M for Mute."` Filtered by radio's MaxSlices.
- **`F1` within JJ Jump layer = open CHM page about JJ Jump** for full reference reading.
- Two distinct affordances by design: `?` for fast in-context reminder, `F1` for slower thorough reading.

**Inactivity-triggered entry announcement (Vim leader-key idiom):**

- Press `Ctrl+J Shift+A` as a single-thought chord (fast operators): announcement skipped, jump fires immediately with "Slice A active"
- Press `Ctrl+J` then pause without followup (e.g., 500ms inactivity): "JJ Jump" announces, user can then explore
- This pattern punishes nobody — fast users don't hear the layer-entry, exploring users do. Standard vim modal-editor convention.
- Inactivity threshold should be configurable per the existing Double-Tap Tolerance settings family (Sprint 28 Phase 1 added the four-option group: Quick / Normal / Relaxed / Leisurely).

**This design generalizes:** the "Ctrl+J = jump to" framing + multi-channel layer-entry feedback + `?`/F1 help affordances + inactivity announcement is a **reusable modal-layer pattern**. Future modal layers (RIT/XIT scale-adjust per Sprint 29, waterfall navigation, etc.) should use the same shape so users learn one pattern.

**Numbers can't be universal slice-jump** because they're already used for tuning (Frequency field digit entry, RIT/XIT digit entry, Sprint 29's planned scale-adjust 1-4). Letters are the right axis.

**Related:** would partially be unblocked by fixing V wrap (separate TODO) + adding `Shift+V` reverse cycle. Combined with V wrap, you can reach any slice in at most floor(N/2) presses. But for arbitrary "jump to slice E" without counting, direct letter-jump wins.

**Priority:** Medium — quality-of-life feature for multi-slice operators. Not blocking 4.1.17. Good Sprint 29 candidate, especially since Sprint 29 already plans the RIT/XIT scale-adjust quasi-modal — could share infrastructure.

**Status:** Logged 2026-04-26 — surfaced during 4.1.17 matrix testing as Noel explored slice navigation across fields.

### POLISH: Mode-change coach text — add chatty version + improve wording (2026-04-26 Noel)

**Location:** `JJFlexWpf/MainWindow.xaml.cs:809-812`

**Current (single Terse string each):**
- Classic: `"Classic tuning mode. Cursor on a digit, up down to tune. Space on RIT or XIT to activate."`
- Modern: `"Modern tuning mode. Up down tune, C toggle coarse and fine, page up down change step size."`

**Noel's preferred chatty versions:**
- Classic: `"Classic tuning mode, with your cursor pointed to frequency parts such as MHz or kHz, press the up or down arrows to tune. Press space to toggle fields such as mute or XIT."`
- Modern: `"Modern tuning mode. Press up or down to tune in coarse steps. Press C to switch to fine tuning. Press space to toggle on/off items in JJ Flexible Home."`

**Implementation pattern:** add a chatty/terse pair. Send the chatty version at `VerbosityLevel.Chatty`, send a properly-trimmed terse version at `VerbosityLevel.Terse`. Two `Speak` calls back-to-back works if `ScreenReaderOutput` filters by user verbosity (chatty user hears the chatty one, terse user hears the terse one — only one fires per user). If duplication is an issue, refactor `Speak` to take both forms and pick based on the user's setting (cleaner and reusable across the codebase).

**Priority:** Low (Sprint 28/29 polish). Cosmetic improvement to discoverability hint.

**Status:** Logged 2026-04-26 — Noel said he'd take this himself.

### POLISH: Add "e e" (dit-dit) close to PlayCwSK (2026-04-26 Noel)

**Location:** `JJFlexWpf/MainWindow.xaml.cs:2073-2078` (`Radios.ScreenReaderOutput.PlayCwSK = async () => { ... }`)

**Current:** `73` (or `73 de JJF` at ≥25 WPM) followed by prosign SK. Feels abrupt.

**Proposed:** append `e e` (two dits, a friendly hand-wave close) after the SK. Updated body would end with `await _morseNotifier.PlayString("ee");` after the existing `await _morseNotifier.PlaySK();`. Applies to both slower and faster CW speed messages.

**Why "e e":** in CW prosign convention, two single dits = quick "bye" / "73 friend" feel. Removes the abruptness of a bare SK close.

**Priority:** Low (Sprint 28/29 polish). User-experience nicety for the CW notification system.

**Status:** Logged 2026-04-26 — Noel said he'd take this himself.

### FEATURE: On-demand tuning-key announcement hotkey (2026-04-20 design, Don via Noel)

**Context:** Sprint 26 Phase 8c added a mode-change announcement that speaks the tuning-key summary on Ctrl+Shift+M mode toggle. Good for the moment of switching, but operators in a long session may forget the vocabulary halfway through and want a mid-session refresher without toggling modes just to hear it.

**Proposed fix:** an explicit "speak tuning keys for current mode" hotkey. Candidate bindings: Ctrl+Shift+/ (universally-usable) or ? / Shift+/ when focused on the Freq field specifically. Action speaks the same hint string Phase 8c emits on mode change, keyed off `ActiveUIMode`.

**Sizing:** small — ~10 lines to wire in KeyCommands.cs + expose a `SpeakTuningKeyHint()` on MainWindow. Could land in Sprint 28 polish or defer to a dedicated accessibility-improvement pass.

**Priority:** Low-medium. Accessibility polish; discoverability help. Mode-change announcement already covers the common case.

### FEATURE: First-focus frequency-home orientation announcement (2026-04-20 design, Don via Noel)

**Context:** Don asked for the frequency home position to announce the tuning keys whenever focus lands there. Phase 8 scoped that out because announcing on every focus is too verbose (operators Tab through fields constantly). A once-per-session "orientation" announcement on first focus after app launch might be the right compromise.

**Proposed fix:** track per-session "has freq-home been focused yet?" flag. On first focus after app launch, speak the mode-appropriate tuning-key hint. Subsequent focuses only speak the current frequency + mode status like today.

**Sizing:** small — a bool flag + focus-handler guard + existing hint-string reuse from Phase 8c.

**Priority:** Low. Nice-to-have; the mode-change announcement + F1 help + the new menu items together cover the discoverability need. Revisit if real-world tester feedback indicates it's still a gap.

### FEATURE: Ctrl+F commit speaks band-segment context (2026-04-19 design, Noel — Sprint 27 target)

**Gap:** when the Ctrl+F frequency entry dialog commits, the app announces the new frequency but does not speak which portion of the band the operator just landed in (e.g., "20-meter CW segment," "40-meter phone," "80-meter DX window"). A sighted operator can glance at the band plan; a screen-reader or braille operator has no cue.

**Fix:** after Ctrl+F commits the frequency, the tuning assistant (or equivalent speech path) announces the band-segment context the same way it does for band-edge warnings. Existing band-segment lookup infrastructure used for the band-edge-warning path is the source of truth — no new band-plan data needed.

**Scope:** small. One announce call inserted into the Ctrl+F commit path. Segment text format should match whatever the app already uses for band-edge announcements to keep voice consistent across features.

**Related:** the "Smarter Band Edge Speech" work already landed in 4.1.16 establishes the lookup pathway; this feature extends it to a second entry point.

**Priority:** Medium. User-facing accessibility gap, small fix.

**Target:** Sprint 27 (polish bucket alongside PC NR fleshout, slice-UI completeness, MultiFlex Channels 2/3).

**Status:** Logged 2026-04-19.

### FEATURE: PC-side noise reduction fleshout (2026-04-19 design, Noel — Sprint 27 target)

**Context:** Sprint 25 Phase 20 wired the PC-side NR engines (RNNoise + spectral subtraction) into the live RX audio pipeline on 2026-04-13. That landed the *capability* but not the *control surface* — engines turn on and off, but users can't tune them, compare them, or capture reference noise profiles. Sprint 27 flesh-out makes them genuinely useful to operators, not just technologically present.

**Scope for Sprint 27:**

1. **RNN modes.** RNNoise has configurable aggressiveness and can target speech vs. broadband noise differently. Expose selectable modes via DSP menu or Settings:
   - Voice (default — tuned for SSB speech)
   - CW/Narrow (tuned for narrow-band signals, preserves weak-signal fidelity)
   - Broadband (more aggressive, general noise floor reduction)
   - Off (disables RNN while keeping the feature discoverable)
   Selection persists per operator; default is Voice.

2. **NR strength (wet/dry blend).** Value control — how much NR is mixed into the output. 0% = bypassed, 100% = full NR. Lets operators dial in the right balance between noise reduction and audio naturalness. Default: 70-80%. Applies independently to RNN and spectral subtraction. Adjustable via Ctrl+J leader + up/down, with spoken percentage announcement.

3. **Spectral subtraction options + presets.** Spectral subtraction has many parameters (window size, over-subtraction alpha, noise floor beta, smoothing constant). Rather than expose all of them, ship named presets tuned to common conditions:
   - **Voice (SSB/AM/FM)** — default for phone modes, preserves speech articulation
   - **CW/Narrow** — tighter filtering, preserves weak-signal transients
   - **Atmospheric** — for 160/80/40 m noise floors dominated by static crashes
   - **Urban** — for mains hum, switching-power-supply buzz, other periodic urban noise
   - **OTH Radar** — aggressive sweep-rejection for over-the-horizon radar bursts common on HF
   - **Custom** — power-user access to raw parameters via an expandable dialog
   Preset selection persists per operator per band (if feasible) or per mode.

4. **Noise capture UI.** Button in DSP panel / ScreenFields: "Capture noise profile from current band." Captures ~2 seconds of receive audio when the band is quiet (or when operator judges it to be), stores as a named noise profile. Spectral subtraction uses the profile as the reference to subtract. Profiles are named, saveable, loadable. Per operator, per band.

5. **Bundled noise profiles.** Ship 3-5 canned profiles covering common scenarios so operators can evaluate Spectral NR without waiting for capture-UI work to be their first introduction:
   - "Typical 40m evening" — atmospheric noise profile
   - "Urban mains" — 60 Hz hum and harmonics
   - "Switching supplies" — broadband buzz common in apartment/condo setups
   - "OTH sweep" — captured OTH radar burst reference
   - "Quiet rural" — minimal reference for generally clean bands
   Operators can load bundled profiles, modify, save as their own.

6. **A/B comparison.** Quick hotkey (candidate: Ctrl+J, Shift+B) toggles NR off momentarily so operator can hear the before/after difference. State is temporary — releases in ~5 seconds or on next NR setting change. Speech announces "bypassed" and "enabled" so it's clear which state is active.

7. **Profile export / import (future hook, not this sprint).** Per `project_noise_profile_sharing.md` memory, eventually profiles should be shareable with antenna metadata, band, location, etc. Sprint 27 scope is local capture/save/load only; sharing plumbing defers to later work.

8. **Per-slice vs. per-session scope.** NR settings should bind per-slice, not globally — operator may want aggressive NR on slice A (noisy DX listening) and light NR on slice B (local ragchew). Each slice remembers its own NR state. Ties into the new session/slice model from Sprint 26.

**Related Sprint 27 track:** this becomes Track F (PC NR fleshout) in the Sprint 27 plan. Runs parallel with Tracks B/C/E (the other post-A parallels). No dependency on networking tracks; only dependency is Sprint 26 slice/session architecture being stable.

**Dependencies:**
- Sprint 26 Phase 2.5 (MultiFlex/session refactor) — for per-slice binding.
- Nothing else.

**Priority:** High. Completes the PC NR story that Phase 20 opened. Without the control surface, the engines are hidden value — "yes we have NR" is technically true but operators can't actually use it.

**Status:** Logged 2026-04-19. Targets Sprint 27 as new Track F. Update `sprint27-barefoot-openport-hotel.md` with Track F definition when Sprint 27 planning is finalized.

### FEATURE: Screen reader auto-detect + sighted-user support (2026-04-19 design, Noel)

**Context:** JJ Flex was built accessibility-first, but the UI itself (menus, dialogs, field labels, visual layout) is a reasonably complete radio control surface that probably works for sighted operators too. The current gap: JJ Flex auto-speaks many events via its self-voiced output path (SAPI, earcon engine, mode announcements, status chatter). For a sighted user who isn't running a screen reader, this would be annoying-to-unusable. For a blind user who doesn't realize a screen reader is needed, they'd get worse output than NVDA would give them.

**Design:**

1. **Screen reader detection at startup.** Call Windows `SystemParametersInfo(SPI_GETSCREENREADER)` and/or UIA `IsScreenReaderPresent` to determine whether a screen reader is running. Re-check when focus returns from Settings (user may have started one mid-session).
2. **Three operating modes:**
   - **Mode A — Screen reader detected (default when SR present):** current behavior. JJ Flex fires its auto-speech events; screen reader handles UI navigation announcements. Combined output is the intended experience.
   - **Mode B — No screen reader detected, self-voiced OFF (default when no SR):** auto-speech muted by default. Visual UI still works normally. Earcons still fire (they're non-speech audio cues and help all users). A hotkey (candidate: **Ctrl+Shift+Space** or a Settings toggle) enables self-voicing for sighted users who want spoken feedback without installing a screen reader.
   - **Mode C — No screen reader detected, self-voiced ON (user opt-in):** sighted user has enabled self-voicing. Auto-speech fires via SAPI. Acts like a self-voicing radio software experience (similar to how Accessible Morse or some ham logging software work).
3. **First-launch welcome prompt** (when no SR detected on first run):
   - Accessible dialog that presents **peer options**, not a recommended-plus-fallback hierarchy. Both paths are legitimate; the user picks based on their comfort level and situation.
   - Suggested copy: "No screen reader detected. JJ Flex works with a few different accessibility setups — pick what fits you:
     - **Turn on Windows Narrator now** — built into Windows, no download needed. Good if you want speech right away or prefer to stick with tools already on your computer.
     - **Install NVDA** — free screen reader from nvaccess.org. More customizable and widely used in the ham community, but requires a download and a few minutes to set up.
     - **Continue without speech** — JJ Flex's visual UI works fine on its own. You can turn on built-in self-voicing anytime with [hotkey]."
   - Dismissible with "don't show again." Each option is a button that actually does the thing (not just a link): Narrator button launches `Narrator.exe` via `Process.Start` (no elevation needed); NVDA button opens the download page in the default browser; "continue" dismisses.
   - **Target persona that makes this worth doing:** long-time Flex owner who's transitioning to low/no vision (age-related macular degeneration, retinitis pigmentosa, diabetic retinopathy, etc.). They've loved their radio for years and know it inside-out; they're now learning accessibility tools for the first time. For this user, "install a screen reader" is intimidating, but "turn on the one already on your computer" is immediately doable. JJ Flex becomes the bridge that keeps their beloved radio usable during the transition, without forcing a steep learning curve at the worst moment.
   - **NEVER auto-fires** any of these actions. All three are user-initiated buttons in the dialog. Auto-enabling a screen reader without consent violates the flexibility principle (see `project_flexibility_principle.md`).
   - Narrator launch caveats communicated clearly: "Narrator stays running system-wide after JJ Flex closes. Toggle it off with Win+Ctrl+Enter." No gotchas, no surprises.
4. **Settings UI:**
   - Settings → Notifications → "Self-voiced speech (when no screen reader is running)" toggle.
   - Shows current detected state: "Screen reader detected: NVDA" or "No screen reader detected."
   - User can force Mode B or Mode C regardless of detection (in case detection is wrong or user wants to override).
5. **Critical-safety speech exception:** some speech MUST fire regardless of mode — out-of-band TX attempts, TX safety timeout warnings, dummy-load-mode reminders, serious errors. These are safety-critical announcements that speak even if auto-speech is globally off (they also have visual/earcon companions).

**Phases:**

1. **Test current sighted experience.** Walk through the UI without a screen reader (or with one minimized/suspended). Inventory every place auto-speech fires. Identify which speech is self-voiced (via `ScreenReaderOutput.Speak` or SAPI paths) vs. which is driven by the screen reader reading the UIA tree.
2. **Implement detection + gate.** Add the detection API call + a central `SpeechRouter` (or similar) that all self-voiced speech flows through. Route by mode.
3. **First-launch prompt.** Dialog + "don't show again" persistence + NVDA link.
4. **Settings UI.** Toggle + detection display.
5. **Critical-safety review.** Identify which speech sites are safety-critical and bypass the gate.

**Market angle:**

This is a significant positioning win. JJ Flex becomes credibly "the radio app for everyone — works without a screen reader, works great with one." Universal design stops being a marketing word and becomes literally demonstrable. Sighted hams who would never have tried "accessibility software" can just use JJ Flex as a radio app; accessibility features are there but not required. Expands addressable market significantly.

**Connects to memories:** `project_flexibility_principle.md` (user choice over developer assumption — sighted user gets to decide whether to self-voice), `project_strategic_identity.md` (universal design is literal, not marketing).

**Priority:** Medium-High. Not Sprint 26 or 27 (foundation phase scope lock). Post-foundation candidate. Detection + gate is low effort; polish (first-launch prompt, Settings UI) is moderate.

**Status:** Logged 2026-04-19. Pending sighted-user experience test before design finalization.

### FEATURE: Per-slice toggles in the VFO row (Jim feature parity, 2026-04-19 design, Noel)

**Context:** Jim's original UI exposed per-slice toggles (VOX, squelch, etc.) as a long single-line cell display in the main VFO row. To activate one, you had to count cells visually then press a letter key (V for VOX, S for squelch, etc.). That model worked on a screen but was hard for screen-reader and braille operators — no announcement on focus, no spoken state, no clear navigation. The WPF rebuild dropped the cell display rather than carry forward an inaccessible pattern. This feature restores the toggle set with a proper accessible model.

**Design:**

1. **Location:** main VFO row, immediately to the left of the frequency field. Order: `[slice letter] [toggles] [frequency] [other fields]`.
2. **Navigation:** Right/Left arrow continues the existing VFO-row navigation pattern. Tab into the row, arrow across to the toggle of interest.
3. **Activation:** Spacebar toggles the focused control. Enter does the same (alias for muscle memory consistency).
4. **Speech:** on focus, screen reader announces "[Toggle name]: On" or "[Toggle name]: Off" (e.g. "VOX: On"). On state change after activation, brief speech confirms the new state ("VOX off"). Works on both NVDA and JAWS via the same accessibility tree the rest of the row uses.
5. **Visual indication:** each toggle is its own labeled control with clear on/off visual state. Not just "V" — the label should be readable for sighted helpers. Paired icon optional.
6. **Braille indication:** integrates with the Sprint 25 braille status line. Compact representation needed since cells are scarce: candidate notation is one letter per toggle, uppercase = on, lowercase = off (e.g. `V s m T` = VOX on, squelch off, mute off, TX-enable on). Settings option for which toggles to render in the braille status line (full set may not fit on a 14-cell display).
7. **Hotkey alternative preserved:** the underlying letter-key shortcuts (V for VOX, S for squelch, etc.) stay wired up as global hotkeys in Radio scope so muscle-memory operators can still hit them without arrowing. Difference vs Jim: hotkeys speak the new state rather than relying on visual cell update.

**Candidate toggle set (final list to confirm during implementation):**

- **TX** — which slice transmits (high value, frequently changed in MultiFlex contexts)
- **Mute** — per-slice audio mute (high value, common during multi-slice listening)
- **Squelch** — per-slice squelch on/off (medium-high value)
- **VOX** — voice-activated transmit on/off (medium value, mainly when transmitting)
- **Lock** — slice lock (medium value, prevents accidental tuning)

DSP-level toggles (NB, NR, ANF, etc.) are NOT proposed here — those already live in the DSP menu and ScreenFields panel and don't need duplication in the VFO row.

**Why this design over Jim's:**

- Speech-first: state is announced on focus, not inferred from visual cell position.
- Braille-first: encoded into the existing status line rather than requiring a separate display read.
- Discoverable: tab to row, arrow across → operator finds the toggles without prior knowledge. Jim's model required knowing the letter shortcuts up front.
- Hotkeys preserved: muscle-memory operators don't lose anything; new operators get an accessible discovery path.

**Sprint landing target:** **Sprint 27.** Pure UI work, no session-layer dependency. Coherent with the MultiFlex Channels 2/3 also deferred to Sprint 27 (both are slice-UI completeness work). Sprint 27 theme becomes "networking depth + slice/MultiFlex UI completeness."

**Priority:** Medium-High. Noel-flagged as needing to land in 26 or 27 (Sprint 27 chosen for scope discipline and theme coherence). User-visible accessibility gap relative to Jim's UI; restoring closes a feature-parity hole.

**Status:** Logged 2026-04-19. Awaiting Sprint 27 plan finalization.

### FEATURE: MultiFlex three-channel awareness (2026-04-19 design, Noel)

**Context:** The 4.1.16 changelog entry "slice selector now shows you only the slices you actually own" is locally correct (clean slice list) but globally creates a "where is that audio coming from?" mystery — when another client (e.g. Don) has an unmuted slice, the audio reaches your speakers but no UI tells you the source. Noel hit this himself during 2026-04-19 BUG-062 testing.

**Design (mix of all three options for full awareness):**

1. **Status Dialog enhancement = discovery channel.** Add a "Other clients on this radio" section to the existing Status Dialog (Ctrl+Alt+S). Lists each connected client with: callsign / station name, program name, owned slices (letter + frequency + mode + mute state). Updates live. Always on, no config needed. The "look here when curious" answer. Fits the established Status-Dialog-as-one-stop-shop pattern.
2. **Slice selector visibility toggle = persistent ambient awareness channel.** New Settings option: "Show other clients' slices in slice selector." Default OFF (preserves current cleanup). When ON, slice selector shows ALL slices on the radio, with non-owned ones clearly screen-reader-labeled (e.g. "Slice C, owned by WA2IWC"). Sub-setting: "Skip non-owned slices when cycling" (default ON; Up/Down arrow stops on owned slices only, but you can navigate to non-owned ones with shifted arrows or another mechanism).
3. **Audio source announcement = just-in-time alert channel.** New Settings option: "Announce audio from other clients' slices." Default OFF (could be annoying for high-traffic radios). When ON, brief speech triggers when audio from a non-owned slice is detected: "Receiving WA2IWC slice on 7.225 USB." Throttled with a configurable cooldown (e.g. once per slice per 60 seconds) to avoid spam.

**Why all three vs picking one:** each serves a different cognitive moment. Status Dialog = "I want to know" (intentional query). Slice selector visibility = "I want to be reminded" (ambient). Audio announcement = "I want to be told" (event-driven). Operators choose their preferred awareness mode by toggling the settings; the always-on Status Dialog is the safety net.

**Critical dependency: BUG-062 must land first.** All three channels require trustworthy MultiFlex client-list data. Sprint 26 Phase 2.5 fixes BUG-062 — this feature work is post-Sprint 26.

**Implementation order:** Status Dialog enhancement first (no Settings UI, no slice selector behavior changes). Then slice selector visibility toggle. Then audio source announcement.

**Priority:** Medium-High. User-visible quality-of-life for any MultiFlex operator. Lands cleanly post-Sprint 26.

**Status:** Logged 2026-04-19. Awaiting Sprint 26 Phase 2.5 (BUG-062) completion.

### BUG-062: MultiFlex stack has multiple sync/event issues (2026-04-19 testing with Don)
Testing MultiFlex with Don (station WA2IWC) on his FLEX-6300 via SmartLink surfaced a cluster of related bugs in the multi-client synchronization and event pipeline. Captured as one bug because they all point to the same underlying subsystem (session / client-sync / event routing).

**Test setup:** Don primary on local 6300 with 2 slices (A and B). Noel connecting remotely via SmartLink.

**Observed issues:**
1. **Slice visibility broken:** When Noel connected, his app only showed slice A (not Don's slice B). Don's app then also dropped to showing only 1 slice. Expected: both clients see both slices (ownership annotated per client).
2. **New Slice refused despite capacity:** With only 1 slice visible on Noel's side (of a 2-slice-max 6300), creating a new slice was refused. If the visible-slice count is wrong, the capacity check may be reading the wrong state.
3. **Connected-client list not propagating to Don:** With Noel connected, Don's MultiFlex Clients dialog did NOT show Noel as a connected client. Consequence: Don cannot kick Noel — the primary-client kick path is effectively broken when the client list doesn't populate.
4. **Flaky connection:** One connection attempt timed out rather than succeeding. Not reproducible on every attempt but indicates connection-initiation robustness issue.
5. **Connect/disconnect event announcements unreliable on Don's side:** When remote clients (Noel) connect or disconnect, Don's speech isn't firing the "connecting" / "disconnecting" events consistently. Sometimes silent, sometimes delayed, sometimes wrong.
6. **Wrong-event-on-disconnect:** When Noel disconnected, Don's app spoke "wa2iwc connected" — i.e. announced a CONNECT event using Don's own callsign at the moment Noel DISCONNECTED. Event source (who) and event type (what) both appear mis-wired.

**Likely subsystem:** the multi-client state synchronization layer — possibly in `WanSession` / `FlexLib` discovery / the station-event dispatcher. Needs investigation into:
- How remote clients are registered into the client list broadcast
- When/how slice ownership is synced across clients
- Connection event emission pathway (who speaks, with whose station ID, for what action)
- Possible race between client join/leave and slice-inventory refresh

**Why these belong together:** all six symptoms are consistent with a single broken state-replication or event-emission pathway. A fix in one area may resolve multiple symptoms.

**Priority:** HIGH. MultiFlex is a core Flex feature; current state makes it essentially unusable for genuine two-client operation. But this is explicitly in Sprint 26+27 "network fixing" scope per foundation-phase memory — natural landing target, not a Sprint 25 emergency.

**4.1.16 ship impact:** MultiFlex was not new to Sprint 25 — these bugs were likely present before. Unlikely to be a ship blocker for 4.1.16, but warrants a "MultiFlex improvements coming" note in the changelog so users know we're aware.

**Status:** Logged 2026-04-19. Deferred to Sprint 26 scope.

### FEATURE: Mode-key deconfliction + expanded mode hotkeys (2026-04-19 design, Noel)
- **Problem:** Main menu mnemonics own Alt+A (Audio) and Alt+F (Filter), which blocks adding Alt+A = AM and Alt+F = FM as mode-change hotkeys. Windows menu bar mnemonics swallow the Alt+letter before a global hotkey can fire.
- **Design direction (Path A — expand Alt+ mode hotkeys, preserve existing muscle memory):**
  - **Menu mnemonic moves:**
    - Audio menu: `&Audio` (Alt+A) → `Audi&o` (Alt+O)
    - Filter menu: `&Filter` (Alt+F) → `Filt&er` (Alt+E)
  - **Existing hotkey move:**
    - DX Cluster: Alt+D → **Alt+Shift+X** (KeyCommands.cs line 884, `CommandValues.ArCluster`)
  - **New mode hotkeys (all Radio scope):**
    - Alt+A = AM
    - Alt+F = FM
    - Alt+D = DIGU
    - Alt+Shift+D = DIGL
  - **Unchanged:** Alt+U = USB, Alt+L = LSB, Alt+C = CW, Alt+M = ModeNext, Alt+Shift+M = ModePrev
  - **Not hotkeyed:** SAM, NFM, DFM — reached via Alt+M cycle from AM (Noel uses SAM occasionally but fine with cycle access)
- **Why Path A over wholesale Alt+Shift mode scheme:** preserves USB/LSB/CW muscle memory (the three most-typed modes for a ham). Cost is moving two menu mnemonics + one hotkey (Cluster).
- **Why Alt+O and Alt+E for the menu moves:** Alt+O is the only free distinctive letter in "Audio" after A/U/D/I are ruled out (U = USB, D = Cluster/DIGU, I = weak). Alt+E in "Filter" is free and phonetically clearer than Alt+I.
- **Why Alt+Shift+X for DX Cluster:** mnemonic for "DX" works better than stashing under Ctrl+Alt+D; X is free across all scopes.
- **Searchable command palette:** all new mode-change commands and the moved Cluster command will appear in the Ctrl+Tab Actions palette / keyboard search, so users who forget the binding can still find them by name.
- **Files to touch:**
  - `JJFlexWpf/NativeMenuBar.cs` lines 1052, 1018 (Audio + Filter menu labels)
  - `JJFlexWpf/KeyCommands.cs` line 884 (Cluster rebind), mode section around lines 320-340 (new ModeAM, ModeFM, ModeDIGU, ModeDIGL CommandValues + bindings)
  - Mode-menu accelerator hints in `NativeMenuBar.cs` line 913-919 (extend switch statement to show Alt+A, Alt+F, Alt+D, Alt+Shift+D next to the mode names in the Slice → Mode submenu)
- **Priority:** Medium. Quality-of-life + accessibility; not blocking a ship. Good Sprint 26 or late-Sprint-25-slip-in candidate.
- **Status:** Design locked 2026-04-19. Ready for implementation.


### BUG-061: CW word/prosign spacing timing not standard (2026-04-17 testing)
- **Symptom:** "73 SK" and other multi-element CW output runs together — inter-word and prosign-boundary spacing feels tighter than standard Morse timing. Noel's ear against W1AW practice streams and electronic keyers flagged it. Not fatal for notification-level CW (BT/SK/mode names still readable) but noticeable.
- **Standard:** PARIS word timing — 50 dit-durations total including 7-unit inter-word gap. Inter-character gap is 3 units. Our generator may be using shorter gaps.
- **Leading theory (Noel 2026-04-17):** the running-together effect may be because `PlayCwSK` calls `PlayString("73")` + `PlaySK()` back-to-back — two separate rendering passes through the FIFO queue. Each pass has its own envelope and inter-element timing context; the gap between them is the queue/buffer gap, not a true 7-unit word space. Fix may be to build a single rendering pass that takes a list of elements (plain chars + prosigns) and emits one continuous waveform with PARIS-standard gaps throughout. Candidate API: `_morseNotifier.PlaySequence(List<CwElement>)` or extend `PlayString` to understand prosign syntax (e.g. `"73 <SK>"` where `<SK>` renders as the joined prosign).
- **Why it matters now vs later:** For notification-level CW this is cosmetic. For the future CW practice mode (virtual keyer + decoder), incorrect timing would teach operators bad habits and fail real-decoder testing. Whatever fix we land must hit PARIS-compliant timing precisely.
- **Related:** BUG-055 (CW prosign envelope + timing quality) — partially addressed in Sprint 25 with CwToneSampleProvider rewrite. Timing math may need a second pass. Also see FEATURE below for dedicated CW processor.
- **Scope:** Audit `MorseNotifier` + `EarconCwOutput` + `CwToneSampleProvider` — element durations, inter-element space, inter-character space, inter-word space, prosign no-gap semantics. Compare to PARIS standard. Instrument with a test harness that feeds sample patterns and verifies gap lengths.
- **Priority:** Medium. Address as a dedicated CW-quality pass before CW practice mode ships.
- **Status:** Logged.

### FEATURE: Dedicated CW processor/engine (2026-04-17 design direction, Noel)
- **Context:** As JJFlex grows into CW practice mode + on-air CW keying + iambic/bug/straight-key support, the CW rendering logic is becoming a first-class subsystem, not a notification helper. Currently spread across `MorseNotifier`, `EarconCwOutput`, `CwToneSampleProvider` with notification-level assumptions baked in.
- **Scope for the engine:**
  - Timing standards: PARIS (default) and the alternative word-length standards (CODEX, etc.) — configurable.
  - Speed: adjustable WPM with NO upper clamp (current: 30 WPM max). CW expert operators and contest regulars routinely run 35-45+ WPM; the engine should support whatever's plausibly decodable.
  - Farnsworth timing: slow char rate with normal inter-char spacing for learners.
  - Single-utterance rendering: accept a sequence of elements (chars + prosigns + explicit word gaps) and emit one continuous waveform with precise PARIS-spec gaps throughout. No back-to-back-utterance artifact.
  - Prosign syntax in string input: bracket notation (`<SK>`, `<BT>`, `<AR>`) that the engine resolves to joined prosigns with no inter-character gap.
  - Envelope shaping: proper attack/release for click-free signals (already partially addressed in BUG-055 fix).
  - Weight / rhythm variance controls for future sending-grade work (CW practice tutor mode).
  - Separate from output: engine produces elements/waveforms, output layer routes to earcon channel (notification) or TX pipeline (on-air) or practice sidetone. Same engine, different destinations.
- **Why this vs piecemeal fixes:** BUG-055, BUG-061, and the eventual CW practice mode all point at the same underlying engine. Doing the engine properly once makes all three land naturally. A piecemeal fix just to tighten "73 SK" spacing wouldn't unlock the future work.
- **Priority:** Medium-High. Foundation for CW practice mode + on-air CW + any future CW feature. Not urgent today but high leverage when scheduled.
- **Status:** Logged. Likely a dedicated sprint post-foundation phase, paired with CW practice mode planning.

### FEATURE: Hide CW message management UI outside CW mode (2026-04-17 design direction, Noel)
- **Context:** Jim-era code carries CW message add/edit/send UI — `CWMessageAddDialog.xaml`, `CWMessageUpdateDialog.xaml`, plus the VB files `CWMessageAdd.vb`, `CWMessageUpdate.vb`, `CWMessages.vb`. These let operators compose and store memorized CW messages for on-air transmission (CQ / call-contact / contest-exchange templates).
- **Noel's design:** these fields are CW-only by nature. When the active slice is in SSB / FM / digital mode, exposing them is clutter for a feature that doesn't apply. Only surface the management UI when the active mode is CW / CWL / CWU. Hide menu entries, access shortcuts, and dialog triggers otherwise.
- **Investigation needed:** confirm no non-CW code path references these dialogs. Grep for call sites, verify they're all mode-gated or gateable. If any are unconditional, either gate them or remove unused code paths.
- **Implementation:** mode-aware visibility on menu items / toolbar entries / hotkeys referencing CW message management. Listen to DemodMode changes and update visibility. Default: hidden; enable when mode is CW variant.
- **Priority:** Low-Medium. Cleanup, not functional. But reduces screen-reader tab-order noise for non-CW operators (most users) who never touch these fields.
- **Status:** Logged.

### FEATURE: Ctrl+Tab action palette expansion (2026-04-17 testing insight)
- **Context:** Sprint 25 Phase 9 added a Ctrl+Tab "Actions" dialog (command palette) at `MainWindow.xaml.cs:2280`. Current items are context-gated: ATU Tune (if hasATU), Start/Stop Tune Carrier (if canTx), Start/Stop Transmit, Speak Status, Cancel. Labels flip based on state ("Start" vs "Stop") which doubles as status readout.
- **Architecture:** `ExecuteActionToolbarItem` is a switch-on-string at `:2317`. Adding a new action is one `list.Items.Add()` + one case in the switch. Context-gating pattern (`hasATU`, `canTx`) is already established for conditional items.
- **Direction:** grow this into JJFlex's primary *command discovery* surface — keyboard-native, state-aware, discoverable via a single hotkey. Obvious candidates for additional entries:
  - DSP: Toggle Legacy NR, RNN, NRS, NRF (each with license/feature gating), Toggle NB, Toggle ANF
  - Audio: Toggle meter tones, Toggle earcon mute, Open Audio Workshop
  - Tuning: Jump to band (sub-palette or quick-pick), Toggle tune debounce
  - Radio: Disconnect, Switch slice, Open RadioInfo / Feature Availability
  - Modes: Cycle mode forward/back, Jump to CW/USB/LSB/digital
  - Braille: Toggle status line, Cycle cell-count profile
  - Settings shortcut, Speak full status (vs current compact Speak Status)
- **UX enhancements to consider** (not required for basic expansion):
  - Recent/favorited items at top
  - Type-to-filter text search (like VS Code Ctrl+Shift+P) for discoverability at scale
  - Grouped sections with spoken group headers for screen-reader navigation
  - Visual polish: icons, grouped separators — accessibility-first, visual second
- **Priority:** Medium. The base is working; growth is pure additive feature work. Good candidate for a dedicated "action palette expansion" mini-sprint after Foundation phase completes. Could also be incremental — add items as adjacent features land.
- **Status:** Logged during Sprint 25 Phase 9 testing.

### BUG-059: Earcon audibility under loud radio audio (2026-04-17 testing, reported by Don + Noel agrees)
- **Symptom:** Earcons (alert tones, toggle feedback) are sometimes unhearable when radio audio is loud. AlertVolume at 100 is not enough headroom to cut through a loud signal on the rig audio channel. Users can miss alerts they should hear.
- **Why simple volume boost isn't enough:** at AlertVolume = 100 we're already at software unity gain. Going higher either distorts or requires raising the digital amplification ceiling. Even max earcon volume can't beat radio audio that's actively loud.
- **Design — three tiers:**
  1. **Tier 1 (cheap, near-term):** raise the AlertVolume ValueFieldControl ceiling above 100 (e.g. to 200) for software amplification. Document distortion tradeoff in the UI label or a tooltip. Users who need raw dB can push past unity.
  2. **Tier 2 (proper fix, sprint-sized):** audio ducking. When an earcon fires, momentarily drop radio audio output by a configurable dB (default ~12) for the ~150-300 ms the earcon plays, then restore. This is what every GPS nav system and car infotainment does for the same reason. Needs an earcon start/stop event hook into the radio-audio channel's gain.
  3. **Tier 3 (orthogonal):** better discoverability of the already-existing `EarconDeviceNumber` separate-device routing. Users can already route earcons to PC speakers while radio audio goes to a different output — but most don't know. Add a "Use separate device for alerts" helper toggle in Settings Audio tab that surfaces this option prominently.
- **Priority:** Medium — real usability gap for the people who care about hearing alerts over loud audio. Tier 1 is a few lines; Tier 2 is a real design pass.
- **Status:** Logged during Sprint 25 Phase 3 testing; deferred to a post-foundation sprint.

### BUG-060: AudioOutputConfig mixes user-scope and per-radio fields (2026-04-17 testing)
- **Symptom (surface):** When no radio is connected, the Frequency Entry Sound dropdown in Settings → Audio tab shows only the three always-on modes — Mechanical and DTMF unlocks are hidden even though TuningHash is persisted at BaseConfigDir. Connecting a radio makes the unlocks reappear. Found during Sprint 25 Phase 7 testing; minimum-viable fix already applied (Settings now loads from BaseConfigDir when disconnected and always saves user-global fields to root on OK).
- **Root cause (architectural):** `AudioOutputConfig` conflates truly user-global preferences (TuningHash, TypingSound, SpeechVerbosity, EarconsEnabled, AlertVolume, MasterVolume, CwNotificationsEnabled, CwSidetoneHz, CwSpeedWpm, TuneDebounceEnabled, TuneDebounceMs, BrailleEnabled, BrailleCellCount, AnnounceSwrAfterTune) with truly per-radio settings (EarconDeviceNumber — device enumeration is machine-local but may legitimately differ between a headset-connected session vs a shack-speakers session on a different radio). One XML file serializes everything; both root and per-radio locations have copies; the load path depends on connection state.
- **Proper fix (later sprint):** split serialization into two files. `userPrefs.xml` at BaseConfigDir holds user-global fields (loaded regardless of connection). `audioConfig.xml` per-radio holds only truly per-radio fields. Migration on first load reads the old single-file format from both locations and splits it. No user-visible regression — just a cleaner separation.
- **Priority:** Medium — today's fix works for the Phase 7 case. But as the config grows, the scope question will keep reappearing (e.g. Sprint 26+ network settings, multi-radio session prefs). Splitting now prevents per-feature special-casing later.
- **Status:** Logged. Minimum fix in commit bundle for today's session. Full split deferred.

### BUG-054: Ctrl+F frequency entry doesn't announce license sub-band crossings (2026-04-15 testing)
- **Symptom:** Type a frequency via Ctrl+F that crosses into a different license class sub-band (e.g. into the extra-only portion of 20 m). Boundary announcement does not fire on Enter. The same crossing via arrow-key tuning DOES announce.
- **Hypothesis:** The boundary-check code compares old vs new frequency (delta-based). Arrow-tuning updates `_lastFreq` before the check; Ctrl+F's set-frequency path likely bypasses or runs after `_lastFreq` has been updated to the new value, making the delta zero.
- **Fix sketch:** centralize the boundary check in a set-frequency helper that both tuning paths call, or invoke the check explicitly from the Ctrl+F Enter handler before `_lastFreq` rolls forward.
- **Priority:** Low — license-safety redundancy (arrow keys still announce), but noisy gap in feedback
- **Status:** Logged — caught during Sprint 25 testing

### BUG-055: CW prosign envelope and timing quality (2026-04-15 testing)
- **Symptom:** Dits sound weak/wrong, dahs don't sound long enough. Root cause was `FadeInOutSampleProvider` misuse that turned every tone into a ~90%-fading envelope. Tones should be click-free sine with proper attack/release.
- **Status:** FIXED in the commit bundle that lands this TODO update. New engine (CwToneSampleProvider + element-batch ICwNotificationOutput + rewritten MorseNotifier) replaces the tone-by-tone + Task.Delay pattern. See `docs/planning/design/cw-keying-design.md`.

### BUG-057: CW prosign cancellation race — only SK fires reliably (2026-04-15 testing of 4.1.16.10)
- **Symptom:** In 4.1.16.10, the CW engine rewrite works (prosign tones sound clean), but on a real connect sequence the user only hears SK (app-close). BT (connected) and AS (slow connect) don't fire. Mode-change CW does work.
- **Hypothesis:** The new engine submits the whole element sequence to the mixer in one call; there's a ~50 ms buffer window before actual audio starts playing. If a subsequent prosign or PlayString fires during that window, its `Cancel()` disposes the CancellableCwProvider before any audio has reached output — the earlier prosign never becomes audible. SK is the only one that works because it fires at app-close with nothing firing after it. The old engine's Task.Delay-driven element-by-element approach didn't have a buffer window, so a second prosign cancelling the first before it played was rarer.
- **Fix options:**
  - (a) Queue prosigns instead of cancel-and-replace — multiple events in quick succession play in sequence (preferred UX).
  - (b) Add a short "minimum-audible" grace period before a sequence is cancellable.
  - (c) Scope `Cancel()` only to `PlayString` (long messages), not to prosign helpers (short, always run to completion).
- **Priority:** High — defeats the whole CW-notification feature for connection events even though the engine itself is fine.
- **Status:** Logged. Sample-accurate engine rewrite shipped 2026-04-15 (`02bc948f`) introduced this race; wasn't caught because single-prosign tests worked.

### BUG-056: CW "speech off" silences navigation along with status (2026-04-15 testing)
- **Symptom:** Setting speech verbosity to Off suppresses everything — including navigation feedback the user needs to operate the app. Separately, "Off" didn't actually silence the initial connect speech, so the dial is incoherent.
- **Priority:** Medium — accessibility regression for operators who want to rely on CW/braille for status while keeping navigation feedback.
- **Fix sketch:** Replace single-dial verbosity with categorized channels (Status / Navigation / Data readout / Hints), each with its own on/off. Three profiles + custom. See "Speech verbosity redesign" in Future Work below.
- **Status:** Logged — design sketched, implementation deferred to Sprint 26 or 27.

### BUG-002: Brief rig audio leak on remote connect (reported by Don, 2026-02-09)
- **Symptom:** Don hears 2-3 seconds of rig audio through local speakers when a remote user connects via SmartLink, before local audio goes silent.
- **Priority:** Low — cosmetic annoyance
- **Status:** Logged for future investigation

### BUG-003: Modern mode tab stops need design (reported by Don, 2026-02-09)
- **Description:** Tab in Modern mode has no intentional design for tab order. Needs purpose-built focus flow.
- **Priority:** Low — Modern mode still under construction
- **Status:** Logged for future sprint

### BUG-004: Crash on shutdown — System.IO.Ports v9.0.0 missing (2026-02-09)
- **Symptom:** App crashes on exit — `FileNotFoundException` for `System.IO.Ports v9.0.0`. Knob thread tries to dispose serial port even when no knob is connected.
- **Fix options:** (a) Downgrade System.IO.Ports to v8.x, (b) Add try/catch in FlexKnob.Dispose(), (c) Skip dispose if no knob was initialized
- **Priority:** Medium — crash dump on every shutdown
- **Status:** Pre-existing, logged for fix

### BUG-013: Duplicate QSO beep not audible (Sprint 6 testing, 2026-02-12)
- **Symptom:** Duplicate warning beep not heard when logging a duplicate QSO. Speech announcement works.
- **Priority:** Low
- **Status:** Logged for investigation

### BUG-016: RNN toggles on radios that don't support it (Sprint 7 testing, 2026-02-14)
- **Symptom:** Neural NR can be toggled on FLEX-6300 which doesn't support it. Needs feature gating.
- **Priority:** Medium
- **Status:** Open

### BUG-023: Connect to Radio while already connected has messy flow (Sprint 7 testing, 2026-02-14)
- **Symptom:** Connecting while already connected causes confusing disconnect/reconnect sequence with "session invalid" errors.
- **Fix:** Add confirmation dialog before disconnecting.
- **Priority:** Medium
- **Status:** Open

## Near-term (next 1–3 sprints)

### High priority — ship ASAP

- [ ] **Announce final SWR after tune** (Don's ask, 2026-04-15). Priority: **HIGH** — Don explicitly asked for this; small bounded scope.
  - After Ctrl+T manual tune releases OR ATU auto-tune completes, speak the settled SWR reading ("SWR 1.3 to 1").
  - Wait ~200 ms after tuner-off before reading so mid-sweep transients don't get announced.
  - Track last SWR via `FlexBase.SWRDataReady` event; detect tune-end via `FlexTunerOn: true→false` transition.
  - Gated by a Notifications-tab checkbox, defaults **on** (low noise — one line per tune operation).
  - In the future verbosity-category redesign, this is a **Status**-class announcement (survives the "speech off, status on" profile).

### Upcoming features
- [x] **Migrate to .NET 10 LTS**: Completed 2026-04-13. All 25 active projects updated from `net8.0-windows` to `net10.0-windows`. Breaking change in .NET 10: new WFO1000 WinForms analyzer requires explicit `DesignerSerializationVisibility` on runtime-only public properties on Form classes (fixed 11 occurrences). Both x64 and x86 Release builds clean, installers generated successfully. ComPortPTT (still on .NET Framework 4.0) not in sln, left alone for now.
- [ ] **TX bandwidth sculpting**: Adjust transmit filter edges from keyboard, mirroring RX filter bracket-key workflow
- [ ] **Editable filter & step presets**: Create, edit, save, load, and share filter/step presets as XML
- [ ] **Compiled help file**: CHM workflow, build integration, context-sensitive F1

### Sprint 8 — Form1 WPF Conversion
- [ ] Convert Form1 from WinForms to WPF Window (kills all interop issues)
- [ ] Eliminate ElementHost — all WPF controls native in WPF Window

### Sprint 9 — Remaining Forms WPF Conversion
- [ ] WPF migration: Command Finder + DefineCommands + PersonalInfo + LogCharacteristics + remaining dialogs

### Sprint 10
- [ ] Slice Menu + Filter Menu (slice-centric operating model)
- [ ] QSO Grid: full filtering + paging

### Sprint 11
- [ ] QRZ per-QSO upload, confirmation status, data import

### Sprint 12
- [ ] Modern menus → WPF Menu control
- [ ] StationLookup full redesign

### Backlog — Open Items
- [ ] "Update Current Operator" menu item — opens PersonalInfo directly for current operator
- [ ] Rename "Operators" to "Edit or Change Operators" in Modern and Logging menus
- [ ] GPS grid from FlexLib — query radio GPS for operator grid square
- [ ] Include slice number in mute/unmute speech ("Muted slice A")
- [ ] Filter presets / filter presets by mode
- [ ] DSP current state display — show on/off state when arrowing through DSP menu items
- [ ] Hotkeys for Modern menu DSP/filter items
- [ ] BUG-015: Fix F6 double-announce "Radio pane" in Logging Mode
- [ ] Focus-on-launch: App doesn't grab focus when launched from Explorer
- [ ] WPF DataGrid cell announcements: NVDA double-reads cell values (fixable with custom AutomationPeer)

## Slice UI & Status
- [ ] Define ActiveSlice rules and announce/earcon behaviors
- [ ] Implement Slice List (next/prev, select)
- [ ] Implement Slice Menu entry point
- [ ] Status Dialog rebuild (BUG-020) — proper accessible WPF dialog with live-updating view

## Keyboard & Discoverability
- [ ] Contextual help: group help via H/?
- [ ] Optional leader key design (Ctrl+J) + 2-step max

## Audio
- [ ] Audio menu / Adjust Rig Audio workflow (TX/RX levels, ALC guidance)
- [ ] **Peak watcher (background audio monitor)**: Runs silently in the background anywhere in JJFlexRadio. Monitors audio levels and alerts via earcon tones or screen reader speech when your audio is too hot, compressor is slamming, or ALC is peaking — no visual UI, just audio/speech feedback. Toggle on/off from Settings or hotkey. Configurable thresholds and announcement frequency so it doesn't nag you during a QSO but catches problems before someone on frequency tells you your audio sounds like a garbage disposal.
- [ ] **Audio test dialog**: The workshop for sculpting your transmit audio. Test and adjust mic gain, compressor, compander, EQ, TX filter width — all with real-time spoken feedback and peak monitoring. Standalone mode (no TX required) so you can dial everything in before you go on the air. This is where operators spend quality time getting their signal right. Needs to expose every audio parameter the Flex offers (and eventually the DSP abstraction layer for other radios).
- [ ] **Off-air self-monitoring**: Use a second receiver (MultiFlex second slice, or a cheap SDR like RTL-SDR) to receive your own transmitted signal off a nearby antenna and analyze it in real-time. Hear exactly what you sound like over the air — not what your mic sounds like, what your *signal* sounds like. On a dual-SCU Flex (6600/6700), TX on Slice A at low power, receive on Slice B via a separate antenna. With future cheap receiver support, anyone could do this with an RTL-SDR dongle and a piece of wire. Feed the received audio into the peak watcher for objective analysis.
- [ ] **Audio chain presets (save/load/share)**: Save the entire TX/RX audio configuration as a shareable preset file — mic gain, compression, EQ, TX filter width, RX volume, AGC settings. Import/export so operators can share “my ragchew voice” or “contest audio” profiles with friends. Same XML-based approach as filter presets.
- [ ] Recording/playback and “parrot” concept (design + feasibility)
- [ ] DAX integration: superseded by JJ Audio interfaces (see SmartLink Independence section)

## CW Audio Engine (Sprint 25 landed; extensions planned)

See `docs/planning/design/cw-keying-design.md` for full architecture.

- [x] **Prosign engine fix** (landed 2026-04-15): replaced tone-by-tone + Task.Delay with element-batch `ICwNotificationOutput` and raised-cosine-envelope `CwToneSampleProvider`. Sample-accurate PARIS timing, click-free sine waves. AS / BT / SK + arbitrary strings.
- [ ] **Farnsworth timing for code-practice use**: element speed at character-speed WPM, gaps stretched to overall-speed WPM. Drops in via an overridable `InterCharMs` / `InterWordMs` on MorseNotifier.
- [ ] **Weighting** (adjustable dit/dah ratio, 2.8:1 to 3.3:1). Operator-preference feature for learning mode.
- [ ] **Code-practice / learning mode**: same engine, no radio involvement. Random letter groups, common-word groups, imported text. Valuable accessible learning tool since there's very little software in this space for blind hams.
- [ ] **On-air CW via PC synthesis** (future sprint, 28+): PC generates audio, streams to radio TX via DAX in SSB mode (CW mode doesn't accept audio — confirmed). Operator hears sidetone from PC in real time; receiver hears it with SmartLink+internet latency.
- [ ] **Iambic keyer** (future, requires on-air path): Mode A and Mode B, pluggable via `IIambicKeyer`. Input adapters for keyboard, gamepad thumbsticks, touchscreen.
- [ ] **CW keying bandwidth configurable**: riseFallMs exposed in Settings. Default 5 ms (ARRL recommendation for ≤30 WPM); power users can tighten for crispness or loosen for cleaner on-air spectrum.

## Session Latency Service (designed; implements in Sprint 26 or 27)

See `docs/planning/design/session-latency.md`.

- [ ] **Per-session RTT and jitter probe**, median + MAD over rolling 20-sample window. Piggybacks on keepalive traffic where possible; falls back to a cheap dedicated probe over the TLS control channel.
- [ ] **Exposed on `IWanSessionOwner.Latency`** (fits the Sprint 26 Phase 1 refactor).
- [ ] **CW keyer consumes** to extend PTT-hold / VOX-tail appropriately.
- [ ] **Multi-radio mixer consumes** (Sprint 28+) to align per-stream PlayoutDelay so concurrent radios are time-coherent at the operator's ears.
- [ ] **Session health watches** RTT spikes and jitter bursts to fire preemptive reconnect and "connection shaky" status.
- [ ] **Status UI displays** live RTT + jitter per session ("round trip 52 ms, jitter 3 ms — connection stable").
- [ ] **Auto-quality tuning** uses initial probe to pick low-bandwidth mode on slow connections.

## Speech Verbosity Redesign (BUG-056 fix path)

Replace the single VerbosityLevel dial with categorized channels:

- [ ] **Categories**: Status (connect/error), Navigation (focus/field feedback), Data readout (freq/mode/meters), Hints/chatty (tooltips/help).
- [ ] **Three predefined profiles** plus Custom:
  - **Full speech** (default) — all categories on, Chatty.
  - **Status and navigation** (CW-assisted mode) — status + navigation on; data readout on but Terse; hints off.
  - **Status only** (true minimal — requires braille for detail) — status on, everything else off.
- [ ] **Per-call categorization**: every `ScreenReaderOutput.Speak` site tagged with `SpeechCategory`. Large touch surface but bounded.
- [ ] **Integrates with CW notifications**: "Speech off + CW on" becomes a coherent operating profile — fluent CW operators hear Morse for events, silence otherwise.

## PC-side Noise Reduction UX

- [ ] **Bundled noise profiles**: ship 2–4 canned Spectral NR profiles (typical 40 m atmospheric noise, urban mains hum, OTH radar sweep) so users can evaluate Spectral NR without waiting for capture-UI work.
- [ ] **Noise profile capture UI**: button in DSP section / Settings to capture the current band's noise floor into a profile that Spectral NR can subtract. Per memory `project_noise_profile_sharing.md`, eventually shareable with metadata (band, antenna, location).
- [ ] **PC NR menu entries**: today PC Neural NR and PC Spectral NR only appear as ScreenFieldsPanel checkboxes and hotkeys. Add menu items under DSP → Noise Reduction so the feature is discoverable via menu, not only via hotkey memorization.
- [ ] **PC NR strength / mode selection**: `RnnEnabled` is binary; `Strength` and `AutoDisableNonVoice` are not exposed. Either as a sub-menu (choose strength levels) or as ValueFieldControls in ScreenFieldsPanel.

## Operator Profile & Band Plans (future sprint)
- [ ] QRZ self-lookup: if operator has a QRZ subscription and is logged in, auto-populate operator data (name, QTH, grid, etc.) from QRZ. Trigger from settings or on first callbook login.
- [ ] License class selection: add license class field to operator profile. Provide a dropdown of license classes based on operator's country (US: Technician/General/Amateur Extra; UK: Foundation/Intermediate/Full; etc.). For countries without a built-in list, allow free-text entry.
- [ ] Band plan enforcement/guidance: look up band plans by ITU region + license class. Show operator which bands/modes they're authorized for. Could highlight out-of-privilege operation. Research: ARRL band plan data, IARU region plans.
- [ ] Internationalization of license classes for popular countries (US, UK, Canada, Germany, Japan, Australia, etc.)

## Ecosystem
- [ ] 4O3A device integration plan (SDK/protocol + device matrix)
- [ ] Tuner Genius support planning

## QSO Grid — Full-Featured Log Viewer (Sprint 8)

Rename "Recent QSOs grid" → "QSO Grid". Default view: filter to recent (preserves current behavior).

- [ ] Filter bar: band, mode, country, date range, callsign, free-text search
- [ ] Apply filter → paged results with accessible announcement: "Loaded N more, showing X of Y"
- [ ] Down arrow at bottom of grid → load next page (10-20 rows)
- [ ] Row-0→1 indexing (WPF DataGrid gives this for free)
- [ ] Configurable default page size (from Sprint 7 Track E setting)
- [ ] Screen reader: announce filter results count, page loads, selected row details
- [ ] Clear filter → return to "recent" default view

## QRZ Deep Integration (Sprint 9)

Per-QSO upload with operator control, plus data import and confirmation features.

### Per-QSO Upload Checkbox
- Add "Upload to QRZ" checkbox as **last field in LogPanel tab order** (after Comments)
- Hotkey: **Alt+Q** jumps directly to checkbox (quick toggle for ragchew QSOs)
- Default state driven by operator settings: "Upload QSOs to QRZ by default" checkbox in PersonalInfo
- If settings checked → checkbox pre-checked on every new QSO
- Operator can uncheck per-QSO (e.g., another QSO with Don — he'll never confirm anyway)
- On Ctrl+W (save): if checked → save locally AND push to QRZ API; if unchecked → save locally only
- Screen reader: "Upload to QRZ, checked" / "Upload to QRZ, unchecked"
- Tab order: Call(Alt+C) → RST Sent → RST Rcvd → Name → QTH → State → Grid → Comments → Upload to QRZ(Alt+Q) → Ctrl+W saves

### QRZ Confirmation
- Check confirmation status from QSO Grid (hotkey on selected QSO)
- Request confirmation if QRZ API supports it
- Bulk confirmation status check (filter unconfirmed QSOs)

### Data Import
- Bulk upload existing log to QRZ Logbook
- Import QSOs from QRZ into local log (sync)
- Settings-driven: enable/disable auto-upload globally

## Multi-Slice Decode & Auto Snap (future — multiple sprints)

Full design docs in `docs/planning/design/multislice-decode/`. Key features:
- Per-slice CW + digital decoding with structured DecodeEvent pipeline
- DecodeAggregator: unified stacked feed (CW / Digital / All tabs)
- Auto Slice Snap policy engine: Trigger → Eligibility → ActionSet → Confirmation
- Five snap levels (0: highlight → 4: contest mode) — never auto-TX
- Keyboard-first navigation: Up/Down signals, Enter to snap, F6 toggle panes, J/Shift+J jump CQs
- Accessibility-first: Tolk for confirmations, UIA live regions for passive, speech modes (off/active/background/events-only)
- Safety: no auto-TX, no focus change during TX, pin override, undo last snap

## Logging Mode Customization (Sprint 9+)

### Customizable Split Logger Field Layout
JJ's vision: logging should be **blazing fast** with keyboard shortcuts. The split logger (Logging Mode) is the speed machine — minimal fields, every one reachable by hotkey. JJ's big logger (Ctrl+Alt+L) has everything.

**Design:**
- Operator selects which fields appear in the split logger from a curated list
- **No "select all"** — the split logger must stay minimal and fast. Cap the max field count
- Each field in JJ's big logger already has a default hotkey (Alt+letter). When a field is pulled into the split logger, **its hotkey comes with it** — inherited, not reassigned
- Hotkeys are Logging-scope (already supported by Sprint 6 KeyScope system)
- Tab order follows selected fields in display order
- Operators can Tab sequentially OR jump directly with hotkeys — whichever is faster

**Speed workflow example:**
Alt+C → W1ABC → Alt+N → Bob → Alt+Q → Space (uncheck QRZ) → Ctrl+W → logged in 5 seconds

**Field picker UI:**
- Accessible list with add/remove/reorder (screen-reader-friendly)
- Shows field name + its hotkey for each available field
- Preview of tab order after changes
- Reset to defaults button

**Implementation notes:**
- Research JJ's existing field hotkey assignments in the big logger (LogEntry form)
- Map each LogEntry field to a Logging-scope hotkey
- Split logger dynamically builds its UI from the operator's field selection
- Store field selection in PersonalData (per-operator)

### Contest Mode
- [ ] Contest designer / contest mode: review JJ's existing contest C# files in JJLogLib (FieldDay.cs, NASprint.cs, SKCCWESLog.cs), assess whether they work and are useful. Consider building a contest designer that lets operators define required exchange fields and contest rules. May be complex — research scope before committing. At minimum, validate existing contest exchange definitions work with LogPanel.

## LogPanel Graduation & Logging Enhancements
- [ ] Graduate LogPanel to full-featured: add record navigation (PageUp/Down browse)
- [ ] Graduate LogPanel: edit existing records (load, modify, update)
- [ ] Graduate LogPanel: search log (find QSOs by criteria)
- [ ] Graduate LogPanel: load more records in Recent QSOs grid (batch/paging)
- [ ] Modern Mode gets embedded LogPanel (split view like Logging Mode)
- [ ] Classic Mode keeps full-screen LogEntry form (JJ's original design)
- [ ] Settings tab for managing Logging Mode preferences (suppress confirmations, etc.)
- [ ] Screen reader verbosity levels (concise vs. verbose announcements for pileup vs. ragchew)
- [ ] Contest-specific exchange validation (Field Day, NA Sprint, etc. — each has different required fields)

## WPF Migration (incremental, ongoing)

**Goal:** Replace WinForms controls with WPF for better native UI Automation, screen reader support, and modern UI capabilities. Migrate incrementally — 2 forms per sprint, new forms always WPF. WinForms shell (Form1) stays until most content is WPF.

**Why:** WinForms uses MSAA accessibility bridge which causes quirks (DataGridView row-0 indexing, menu items needing manual `AccessibleName`, etc.). WPF has native UIA support — controls expose automation properties automatically. JAWS/NVDA work more reliably with WPF.

**Rules going forward:**
- All NEW forms/dialogs → WPF from day one
- Existing forms → convert 2 per sprint alongside feature work
- WPF controls hosted in WinForms via `ElementHost` during transition
- Test each conversion with JAWS + NVDA before marking done

**Migration roadmap (REVISED — Form1 first to kill interop issues):**

| Priority | Form | Why | Sprint |
|----------|------|-----|--------|
| **1** | **Form1 shell → WPF Window** | **Kills all WPF-in-WinForms interop issues** | **8** |
| 2 | Command Finder | Already new, natural WPF fit | 9 |
| 3 | DefineCommands (hotkey settings) | Tabbed UI, benefits from WPF TabControl | 9 |
| 4 | PersonalInfo (settings) | Straightforward form conversion | 9 |
| 5 | LogCharacteristics | Small dialog | 9 |
| 6 | All remaining dialogs | Finish the job | 9 |
| 7 | Modern menus → WPF Menu control | Eliminates AccessibleName workarounds | 12 |

**Acceptance criteria per conversion:**
- [ ] WPF control/window created with proper AutomationProperties
- [ ] Hosted via ElementHost (or standalone WPF Window for dialogs)
- [ ] JAWS announces all controls, grids show 1-based rows
- [ ] NVDA announces all controls, same behavior
- [ ] Tab order logical, focus management correct across WinForms↔WPF boundary
- [ ] No regression in existing functionality

### Backlog — Mini Sprint 24a Remaining Items

**Meter presets:**
- [ ] User-saveable presets — save current slot config as a named preset (e.g., "Don's Tuner")
- [ ] Ability to create new presets from scratch, not just modify existing ones

**Filter help:**
- [ ] Update help text to explain filter edge grab mode (double-tap bracket, then both brackets move that edge)

**Connectivity / stability:**
- [ ] Connection error hang: SSL/SmartLink error makes app unresponsive, requires taskkill — observed twice in one session

### SmartLink Independence — Eliminate SmartSDR Dependency

**Goal:** JJFlexRadio becomes a fully standalone client — no SmartSDR required for any SmartLink operation.

- [ ] **Port forwarding settings (NEXT TO DEVELOP)**: Add TCP/UDP external port configuration to SmartLink settings. FlexLib has `Radio.WanSetForwardedPorts()` — we just need UI. Includes connection test results display (`WanTestConnectionResults`). Don needs this for his 6300.
- [ ] **SmartLink radio registration**: Register/unregister radios with SmartLink directly from JJFlex. FlexLib has `Radio.WanRegisterRadio(owner_token)` and `WanUnregisterRadio()` — uses our existing Auth0 JWT. Eliminates the last reason to open SmartSDR for setup.
- [ ] **JJ Audio interfaces**: Replace DAX dependency with direct FlexLib audio streams. FlexLib has DAXRXAudioStream, DAXTXAudioStream, RXRemoteAudioStream (with OPUS), TXRemoteAudioStream. Create JJFlex-managed audio routing for digital mode programs (fldigi, WSJT-X) — virtual audio devices or direct pipe.
- [ ] **JJ CAT ports**: Replace SDR-CAT dependency with FlexLib-native CAT control. FlexLib has full Slice API (freq, mode, filter) and UsbCatCable virtual COM port support. Create JJFlex-managed virtual serial ports for external programs. Potentially expose Hamlib-compatible interface.
- [ ] **Connection test UI**: Surface WanTestConnectionResults (UPnP TCP/UDP, forwarded TCP/UDP, hole punch capability) as a "Test SmartLink Connection" feature so operators can diagnose connectivity issues without SmartSDR.
- [ ] **Firmware update from JJFlex**: FlexLib has `Radio.SendUpdateFile(filename)` — uploads firmware via TCP port 4995, tracks progress. **Decompile revealed (2026-04-12):** SmartSDR bundles firmware in installer, stores at `C:\ProgramData\FlexRadio Systems\SmartSDR\Updates\`. Files named `FLEX-6x00_v{version}.ssdr` (6000 series) or `FLEX-9600_v{version}.ssdr` (8000/Aurora "BigBend" platform). No download API exists — files ship with the installer. **NOTE: Firmware updates are LAN-only (SmartSDR explicitly blocks over SmartLink).** Implementation phases: (1) Auto-detect firmware files from SmartSDR's ProgramData folder if installed, (2) Manual file browse as fallback, (3) Accessible progress dialog monitoring UpdateStatus/ConnectedState/UpdateFailed properties, (4) Host firmware files on web server (blindhams.network/Netlify initially, or dedicated domain like jjflexible.radio long-term). JJFlex builds filename from `radio.IsBigBend` + `radio.ReqVersion`, downloads only the file needed for that radio platform (~61 MB for 6000 series, ~372 MB for 8000/Aurora). Noel uploads new .ssdr files when Flex releases firmware. (5) Future: investigate Mac client or Flex download page for standalone firmware file source.
- [ ] **License refresh**: Refresh radio feature licenses (SmartLink subscription, Multi-Flex, etc.) directly from JJFlex without SmartSDR. FlexLib exposes `Radio.FeatureLicense` — research how license activation/refresh works on the server side. Eliminates another "go open SmartSDR" moment.
- [ ] **"Box to QSO" setup wizard**: Guided first-run experience — create SmartLink account, discover radio on LAN, register with SmartLink, update firmware if needed, configure network (UPnP or port forwarding), test connection, connect. Zero sighted assistance, zero SmartSDR. The pitch: from opening the Flex box to your first QSO, all in one accessible app.

### Backlog — Don's Feedback Items (2026-03-12)

**HIGH PRIORITY — Slice interaction:**
- [ ] Slice selector field in FreqOut: up/down arrows switch active slice, speaks short slice status on change (letter, freq, mode)
- [ ] Slice operations field in FreqOut: adjacent to slice selector, allows pan/volume/release operations on current slice
- [ ] Slice A/B switching unreliable for Don — needs trace session to diagnose (may be timing, scope, or MultiFlex ownership issue)
- [ ] Slice status speech: when switching slices, speak a concise summary (letter, freq, mode, muted/unmuted)

**HIGH PRIORITY — Status Dialog rebuild (BUG-020):**
- [ ] Rebuild Status Dialog as proper accessible WPF dialog — tab stops, close button, window ownership
- [ ] Display: radio model, connection type, all slice info, meter readings, active preset, tuning mode
- [ ] Should be a live-updating view, not just a snapshot

**HIGH PRIORITY — Key migration:**
- [ ] Move KeyCommands.vb to C# — key conflicts keep accumulating, VB dispatch is hard to audit
- [ ] Key conflict audit: systematic check for duplicate bindings across scopes (Sprint 24 scope)

**MEDIUM PRIORITY — Modern mode tuning UX:**
- [ ] Modern mode frequency: arrow keys move through frequency as a single unit (no char-by-char review), speaks frequency on change
- [ ] Classic vs Modern confusion: consider better onboarding, mode-switch announcement improvements, or unifying the modes

**MEDIUM PRIORITY — Auth Config Externalization (Sprint 25):**
- [ ] Move Auth0 endpoint, client ID, redirect URI out of hardcoded AuthFormWebView2.cs into a config file
- [ ] Allows rotation without rebuild if Flex changes their Auth0 settings (has happened during beta firmware)
- [ ] Sets the pattern for future auth providers (Icom, etc.)

**MEDIUM PRIORITY — Action Toolbar (Sprint 25):**
- [ ] WPF ToolBar with action buttons: Manual Tune, ATU Tune (ATU radios only), Transmit (PTT toggle)
- [ ] Toolbar navigation: Ctrl+Tab from frequency fields lands on toolbar (first item), Ctrl+Tab again moves to next pane. Arrow keys move between buttons within toolbar.
- [ ] Screen reader announces "toolbar, Manual Tune button, 1 of 3"
- [ ] Buttons duplicate existing hotkeys (Ctrl+Shift+T, Ctrl+T, Ctrl+Space) — second path for sighted users and discovery
- [ ] Consider JJ key (Ctrl+J then B?) as direct jump to toolbar
- [ ] Design note: F6/F8 are traditional toolbar jump keys but are taken for band navigation — Ctrl+Tab is the chosen alternative

### Backlog — Frequency Entry Audio Feedback (Sprint 25)

- [ ] Per-keystroke audio feedback during frequency entry (quick-type accumulator and JJ Ctrl+F) — multiple selectable sound modes
- [ ] Confirmation ding on frequency commit (DingTone from Sprint 24 Phase 8A)
- [ ] DTMF tone generator utility — reusable for future repeater control on non-Flex radios
- [ ] Unlockable bonus sound modes with discovery mechanics (details in private planning docs)
- [ ] Visual feedback sequences for sighted users alongside audio feedback
- [ ] Earcon rename: `ConfirmTone()`/`confirm.wav` → `ClunkTone()`/`clunk.wav` (do during Sprint 24 Phase 8A)
- [ ] Settings UI for sound mode selection in Audio tab

### Backlog — FM / Repeater Support

- [ ] PL/CTCSS tone decode — FFT on sub-audible 67-254 Hz range in RX audio, auto-detect and speak the tone. Blind operators can't see it on a spectrum display.
- [ ] PL/CTCSS tone encode for TX — needed for 10m and 6m FM repeaters. Check if FlexLib already exposes tone properties in slice settings.
- [ ] Pairs well with waterfall sprint — FFT infrastructure overlaps
- [ ] SmartSDR already handles repeater offsets but may not expose PL tone settings — investigate

### Backlog — Virtual Keyer / CW Practice Mode

- [ ] Built-in virtual keyer: straight key (spacebar), iambic paddles (shift keys), and bug simulator with realistic mechanical spring character
- [ ] Practice mode: local sidetone + CW decoder for real-time feedback, no radio required — practice anywhere
- [ ] Live TX mode: local and remote (SmartLink) — keyer logic runs locally, sends element timing to radio
- [ ] Real-time CW keying over remote — first-to-market on any radio platform. No other software allows paddle/bug feel over a network connection.
- [ ] Full break-in (QSK) and semi break-in with configurable hang time (TW-2000 style)
- [ ] No VOX required for CW — keyer state IS the PTT (improvement over Jim's design which required VOX)
- [ ] Semi break-in hides network latency for smooth remote operation
- [ ] Configurable WPM, weight, Farnsworth spacing for learners
- [ ] CW decoder reused from multi-slice decode feature

## Accessible Receive Path (Zero to Low Cost Entry)

The cheapest path into accessible ham radio — no transmitter required.

- [ ] **Web SDR integration**: Connect to KiwiSDR, WebSDR, OpenWebRX receivers with full accessible tuning, braille waterfall, and JJFlex's keyboard-first UX. Zero cost, zero hardware — just an internet connection. Research APIs/protocols for each platform (KiwiSDR has a documented WebSocket API). An iOS TestFlight app already proves accessible Web SDR control is viable.
- [ ] **RTL-SDR receive station**: $30 dongle + JJFlex = complete accessible receive station. PC-side DSP (from DSP abstraction layer) provides filtering, NR, AGC. Braille waterfall from IQ data. Full tuning experience. SoapySDR library for device abstraction.
- [ ] **Upgrade funnel**: Free (Web SDR) → $30 (RTL-SDR) → $50-200 (QRP TX) → $1,100 (IC-7300) → $3,000+ (Flex). Same app at every tier, same interface, same muscle memory. Each step adds capability (TX, hardware DSP, more bands) but the core experience is consistent.

## Digital Voice — FreeDV2 via Pi Proxy

- [ ] **FreeDV2 / RADEV2 integration**: Full planning session completed — see `docs/planning/future items from chatgpt brainstorming session.md` EPIC 1. Architecture: SSH-managed Pi node as external vocoder, with future native in-radio support. Includes Node Manager UI (Tools → FreeDV2 Node Manager), runtime backend selection (prefer node vs native), mode status states. Determined to be more than doable.

## Radio Memory Programming (RT Systems Alternative)

- [ ] **Radio memory/channel snapshots**: Read, write, and manage radio memories and channel programming — accessible alternative to RT Systems. RT Systems charges per-radio, is not accessible. Research memory formats for popular rigs (IC-7300, Yaesu FT-991A, etc.). Import/export, backup/restore, share channel plans between operators. Way down the line but fills a real gap.

## Website & Distribution Infrastructure

- [ ] **jjflexible.radio domain**: Register dedicated domain for the project. Host on Netlify (already paying $9/month). Ruby-based site (same stack as blindhams.network). Landing page, download links, release notes, setup guide.
- [ ] **Firmware hosting**: Serve .ssdr firmware files as Netlify assets from jjflexible.radio. Two files per Flex firmware release (~61 MB for 6000 series, ~372 MB for 8000/Aurora). Noel uploads new files when Flex releases firmware. Flex contacted for permission (2026-04-12).
- [ ] **Auto-update check for JJ Flexible (multi-channel + silent download + optional relaunch)** — design expanded 2026-04-20.
  - **Channels:** user picks Stable / Debug / Daily in Settings. Stable is default. Debug and Daily surface a one-line warning that pre-release builds may be unstable. Channel change is an explicit opt-in, not auto-promotion.
  - **Source:** GitHub Releases API for the Stable path (today's existing "Check for Updates" button already does this). Debug and Daily need a channel model GitHub can express — candidates: (a) tag-prefix scheme (`v4.1.17` stable, `v4.1.17-debug.N` debug, `v4.1.17-daily-YYYYMMDD` daily, filter `releases/` by pattern), (b) a maintained `updates.json` manifest that decouples updater logic from GitHub's release model and ports cleanly to jjflexible.radio hosting when that lands, (c) parallel repos per channel (heavy — probably skip). Leaning (b) for migration flexibility.
  - **Timing:** update check runs pre-radio-connection, pre-SmartLink-auth. No in-flight state to preserve. Happy path is invisible (no "no updates available" chatter unless user explicitly asks). Update-available path: quick screen-reader + earcon notification, background download starts while user proceeds to connect their radio.
  - **Silent download + relaunch:** installer runs with NSIS `/SILENT` flag. Once download completes, user picks relaunch-now / apply-on-exit / defer-to-next-launch. Never force a silent relaunch — a user mid-QSO must not get their app pulled out from under them.
  - **Shared plumbing with firmware update feature:** the Flex firmware check (line above) wants the same check-download-apply pipeline. Whichever lands first should design the shared layer; the second inherits it.
  - **Open design questions:** channel-downgrade policy (Daily → Stable when Daily is newer — force-reinstall blessed build, wait, or prompt?); delta vs full installer (full is simpler, delta is faster — probably ship full v1, evaluate delta later); update-check opt-out toggle placement (Settings tab most natural).
  - **No hosting gate — buildable on GitHub alone.** All three channels can live on GitHub without waiting for jjflexible.radio: the manifest JSON is just a file served by `raw.githubusercontent.com/nromey/JJFlex-NG/<branch-or-tag>/updates.json`, editable via normal commits. If migrating off GitHub later, repoint the updater's manifest URL once; the rest of the logic is unchanged. Tag-prefix convention (`v4.1.17` / `v4.1.17-debug.N` / `v4.1.17-daily-YYYYMMDD`) works today with the existing `gh release create` flow — daily cadence just means more tags, which GitHub handles trivially.
  - **Status:** Unblocked as of 2026-04-20. Candidate work for a side-branch or slip-in; does not require coordination with Sprint 26's connection-fix track.
- [ ] **Firmware version check**: When connected to a Flex radio, check `radio.Version` vs latest available firmware on jjflexible.radio. Notify user if firmware update available. Offer to download and install (LAN-only).
- [ ] **Donate link**: Add donation link to website — users have been requesting this. Prominent but not pushy.
- [ ] **App distribution**: Serve installers from jjflexible.radio, link to GitHub releases, or both. Consider which is better for discoverability vs reliability.
- [ ] **Fix blindhams.network**: Site currently broken — fix alongside jjflexible.radio spinup (same Ruby/Netlify stack, work with Codex).
- [ ] **Crash report upload**: Crash zip capture already exists (saves locally). Add a "Send Report" button that uploads the crash zip to jjflexible.radio endpoint (Netlify function or simple POST). Ping ntfy.sh to push notification to Noel's phone when a crash report arrives. User-initiated (click to send), not automatic — respects privacy. Add before next release once web endpoint exists.
- [ ] **Premium features infrastructure**: Future — iOS app, Android app, premium features (multi-radio, etc.) with revenue to cover hosting costs.

## Long-term
- [ ] Wideband SDR support: RTL-SDR (RX-only, ~$30), HackRF (TX/RX, ~$350), ADALM-Pluto (TX/RX, ~$200), Airspy, SDRplay. Needs a radio abstraction layer that can talk to FlexLib, SoapySDR, and potentially Hamlib/CAT. This is the accessibility play — getting the entry cost for an accessible SDR station from $3,000+ down to $200-400.
- [ ] **Radio abstraction — clean up `OpenParms` / `Callouts` structure**: When the radio abstraction layer sprint lands, dismantle the current two-class parallel structure (`AllRadios.OpenParms` + `FlexBase.OpenParms`, one shadowing the other). It was the root cause of BUG-058 (Don's 2026-04-16 NRE) — a C#/VB accessibility + shadowing trap that silently routed external callers to a blank stub. Replacement design: single `RadioCallouts` base class with the common delegates (FormatFreq, GotoHome, ConfigDirectory, OperatorName, CWTextReceiver); per-radio subclasses add their own bits (`FlexCallouts` → StationName, License, GetSWRText; `IcomCallouts` → CI-V address; `HamlibCallouts` → whatever). One `Callouts` field on the radio base, typed as the base, always public — no shadowing, ever. Concrete radios downcast when they need their own extensions. Also delete Kenwood-era dead weight from the current `AllRadios.OpenParms` (SendRoutine, SendBytesRoutine, NextValue1Description, RawIO, DirectDataReceiver — unused after the Flex-only cut). See `Radios/FlexBase.cs:6625` for the load-bearing comment explaining why the current field must stay public until this refactor happens.
- [ ] QRP rig support: Research affordable QRP transceivers (QDX, QMX, (tr)uSDX, etc.) as low-cost accessible entry points. These are kit/assembled rigs in the $50-200 range with CAT control.
- [ ] **Icom IC-7300/7300 Mark II support (HIGH DEMAND — 5 user requests)**: The 7300 is a hybrid SDR that outputs spectrum/waterfall data over CI-V protocol. At ~$1,100 it's the most popular HF rig of the last decade and less than half the cheapest Flex. Research CI-V spectrum scope data commands — if we can pull FFT data over USB, we can drive the braille panadapter from it. This could be the first non-Flex radio with accessible waterfall support. Research spike: grab CI-V spectrum data spec, determine resolution and refresh rate, assess feasibility. **Note:** Open source IC-7300 controllers exist — study their CI-V implementation rather than reverse-engineering from scratch.
- [ ] Xiegu radio support: Research Xiegu SDR rigs (G90, G106, X6100). SDR-based internally but unclear how much data is accessible via CAT/serial. X6100 runs Linux which is promising. Need to investigate CAT protocol depth and whether panadapter/waterfall data can be extracted.
- [ ] DSP abstraction layer: Build a JJFlexRadio-side DSP engine (noise reduction, filtering, FFT, AGC) that runs on the PC regardless of radio backend. For Flex: stack on top of hardware DSP for deeper signal extraction. For cheap radios (HackRF, QRP rigs): provide Flex-grade signal processing they don't have natively. Research neural noise reduction models (RNNoise, etc.), FFT libraries, and real-time audio pipeline architecture. Same ScreenFields controls drive both hardware DSP (Flex) and software DSP (everything else) — operator doesn't need to know where the processing runs.
- [ ] Weak signal / Dig Mode presets: One-keystroke DSP profiles optimized for FT8 and other weak signal digital modes — tight filter bandwidth, neural NR tuned for digital, optimized AGC/NB settings. Auto-load when switching to digital modes. Works with both hardware DSP (Flex) and software DSP (abstraction layer).
- [ ] Braille + sonified waterfall "famous interface" implementation
- [ ] Braille meter display: render active meter values on braille line, width-adaptive (20/40/80 char displays). Three hardware configs: (a) standard braille display only — meters share line, cursor routing keys for frequency digit tuning, (b) Dot Pad only — 20-char braille line for text meters plus tactile graphic meter panel, (c) both — standard display for tuning with cursor routing, Dot Pad for meter visualization
- [ ] Multi-radio simultaneous operation (possible premium)
- [ ] **Mac port**: FlexLib is .NET 10 LTS, runs on macOS natively. UI layer needs platform-specific approach (Avalonia, MAUI, or native). Dogpark and SmartSDR for Mac are competitors — Justin AI5OS reports Dogpark is usable with VoiceOver, SmartSDR Mac is "not fun." Key advantage: JJFlex would be the only Mac client with full setup wizard (registration, firmware, port forwarding). Keep business logic separated from WinForms/WPF to enable this.
- [ ] **iOS remote client**: Remote radio control from iPhone/iPad. Three connection models: (a) bridge through Mac/Windows JJFlex instance, (b) direct SmartLink connection (SmartLink for iOS proves this works), (c) limited standalone operation. Ties into CW notification / haptic vision work for deaf-blind users. VoiceOver on iOS is mature — accessibility comes almost free if built with standard UIKit/SwiftUI.
- [ ] AllStar (ASL) remote base: Connect JJFlexRadio to AllStar Link nodes, turning a supported radio (Flex for now) into a remote base accessible from the AllStar network. Research ASL protocol, audio routing (DAX/Opus to ASL), PTT integration, and node registration. Big project — needs feasibility study first.
- [ ] Transmit signal sculpting: Adjust TX filter width from the keyboard, similar to how RX filter edges can be adjusted with bracket keys. Let operators narrow or widen their transmitted audio bandwidth for tight band conditions or signal shaping. Research FlexLib TX filter properties (`Slice.TxFilterLow`, `Slice.TxFilterHigh`) and determine safe limits per mode.
- [ ] Braille art on the waterfall: Render callsigns, CQ markers, or ASCII art as braille characters on the panadapter braille display. If sighted hams get to paint their callsigns in the waterfall, blind hams deserve the same fun. Arr. 🏴‍☠️
