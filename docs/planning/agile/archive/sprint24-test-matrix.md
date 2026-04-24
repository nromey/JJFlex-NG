# Sprint 24 Test Matrix — v4.1.16 Pre-Release

**Tested by:** Noel Romey (K5NER)
**Date:** ___
**Build:** Debug x64 (clean build from sprint24/track-a, Sprint 24 complete)
**Screen Reader:** NVDA primary, JAWS secondary
**Radio:** FLEX-6300 "6300 inshack" via SmartLink (Don's radio)

---

## Pre-Test Setup

1. Clean build: `dotnet clean JJFlexRadio.sln -c Debug -p:Platform=x64 && dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64 --verbosity minimal`
2. Verify exe timestamp: `powershell -Command "(Get-Item 'bin\x64\Debug\net8.0-windows\win-x64\JJFlexRadio.exe').LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')"`
3. Launch app, connect to radio via SmartLink
4. Start on a known band (e.g., 20m USB) before testing

---

## Phase 6: Verbosity Engine

### Verbosity cycling (Ctrl+Shift+V)

- 6.1: Press Ctrl+Shift+V. Verify speech announces current verbosity level (e.g., "Terse"). ___
- 6.2: Press Ctrl+Shift+V again. Verify it cycles to the next level (Terse, Normal, Verbose, or Off depending on cycle order). ___
- 6.3: Cycle through all levels. Verify each level name is announced clearly. ___

### Verbosity filtering

- 6.4: Set verbosity to Verbose/Chatty. Toggle a DSP feature (e.g., JJ N for NR). Verify full speech including hints. ___
- 6.5: Set verbosity to Terse. Toggle the same DSP feature. Verify shorter speech (e.g., "NR on" instead of "Legacy Noise Reduction on"). ___
- 6.6: Set verbosity to Terse. Perform a band jump. Verify terse frequency readback (no extra context). ___
- 6.7: Critical messages (connection errors, PTT safety, "No radio connected") are always spoken regardless of verbosity level. ___

### Tones toggle (Ctrl+J then T)

- 6.8: Press Ctrl+J then T. Verify speech says "Tones off" (or similar) and earcons stop playing for toggles. ___
- 6.9: Press Ctrl+J then T again. Verify speech says "Tones on" and earcons resume. ___

### Persistence

- 6.10: Set verbosity to Terse and tones off. Close and relaunch the app. Verify settings are restored on startup. ___
- 6.11: Change back to Verbose/Chatty and tones on. Restart again. Verify restored. ___

---

## Phase 7A: Slice Count Tracking Fix (BUG-049)

- 7A.1: With 1 active slice on 6300, press Create Slice. Verify slice B is created and speech confirms (e.g., "Slice B created, 2 active"). ___
- 7A.2: Remove slice B. Verify speech confirms removal (e.g., "Slice B released, 1 active"). ___
- 7A.3: After removing slice B, press Create Slice again. Verify a new slice is created (NOT "Maximum slices reached"). This was the BUG-049 regression from Sprint 23. ___
- 7A.4: With 2 active slices on 6300, try Create Slice. Verify "Maximum slices reached" is spoken. ___
- 7A.5: Rapidly create and remove slices several times. Verify count stays accurate and menus update correctly. ___
- 7A.6: After slice add/remove, check the Radio menu or slice submenu. Verify it shows the correct list of active slices (no stale entries). ___

---

## Phase 8A: Slice Selector, Operations, and Frequency Field Improvements

### Slice selector

- 8A.1: Tab to the slice selector field. Verify current slice is announced (e.g., "Slice A, 14.250 USB, yours"). ___
- 8A.2: Arrow up/down on the slice selector. Verify it cycles through active slices with speech (freq, mode, ownership). ___
- 8A.3: With only 1 slice active, verify up/down on slice selector does not crash or produce confusing speech. ___

### Slice operations

- 8A.4: On the slice operations field, adjust volume for the current slice. Verify speech announces the change. ___
- 8A.5: Adjust pan for the current slice. Verify speech announces the change. ___

### Slice mute (Shift+M)

- 8A.6: Press Shift+M. Verify current slice is muted with earcon and speech (e.g., "Slice A muted"). ___
- 8A.7: Press Shift+M again. Verify unmute with earcon and speech (e.g., "Slice A unmuted"). ___

### Modern mode frequency field (read-only)

- 8A.8: Switch to modern tuning mode (Ctrl+Shift+M if needed). Tab to the frequency field. Verify frequency is spoken. ___
- 8A.9: Press Up/Down arrows on the frequency field in modern mode. Verify the frequency does NOT change by digit position. The field should be read-only (informational). ___
- 8A.10: Verify that modern mode tuning still works via the normal up/down arrow coarse/fine step mechanism (outside the frequency field). ___

### Classic mode frequency field (unchanged behavior)

- 8A.11: Switch to classic tuning mode. Tab to frequency field. Verify arrows change frequency by kHz/MHz digit positions as before. ___

### Quick-type frequency entry

- 8A.12: In either tuning mode, with focus on a frequency field, type digits rapidly (e.g., "7125"). After a pause, verify speech confirms "Change frequency to 7.125 MHz? Press Enter to confirm." ___
- 8A.13: Press Enter. Verify frequency changes to 7.125 MHz. ___
- 8A.14: Type another frequency (e.g., "3.525" with decimal). After pause, verify confirmation. Press Escape. Verify previous frequency is restored. ___
- 8A.15: Type an out-of-band frequency. Verify graceful handling (error message or clamping). ___

### DSP level minimums

- 8A.16: Open DSP ScreenFields (Ctrl+Shift+N). Tab to NR Level. Try to set it below 1. Verify minimum is 1 (not 0). ___
- 8A.17: Tab to NB Level. Try to set it below 1. Verify minimum is 1. ___
- 8A.18: Tab to WNB Level. Try to set it below 1. Verify minimum is 1. ___

---

## Phase 9A: Accessible Status Dialog

### Opening and content

- 9A.1: Press Ctrl+Alt+S. Verify Status Dialog opens and screen reader reads the dialog title. ___
- 9A.2: Arrow down through the list. Verify Radio info section is present (model, name, connection). ___
- 9A.3: Verify Active slices section shows each slice with frequency, mode, and volume. ___
- 9A.4: Verify Meters section shows S-meter, ALC, SWR, Power values. ___
- 9A.5: Verify TX state and ATU status are shown. ___
- 9A.6: Verify Antenna info is shown. ___

### Verbosity interaction

- 9A.7: Set verbosity to Terse. Open Status Dialog. Verify terse display format is used (less detail). ___
- 9A.8: Set verbosity to Verbose/Chatty. Open Status Dialog. Verify full detail format. ___

### Auto-refresh

- 9A.9: Leave Status Dialog open. Change frequency on another slice (if available) or wait for meter values to change. Verify the dialog updates automatically (2-second refresh). ___

### Copy and close

- 9A.10: Press the Copy to Clipboard button. Paste into a text editor. Verify complete status info was copied. ___
- 9A.11: Press Escape. Verify dialog closes and focus returns to main window. ___

### Not connected state

- 9A.12: Disconnect from radio. Press Ctrl+Alt+S. Verify dialog shows "Not connected" gracefully (no crash). ___

---

## Phase 10A: VFO Index Drift Fix

- 10A.1: Create a second slice (Slice B). Switch between A and B using the slice selector. Verify A/B letters are correct and don't drift. ___
- 10A.2: Remove Slice B. Verify remaining slice is still labeled correctly. ___
- 10A.3: Create a new slice after removal. Verify it gets the correct letter (not a stale index). ___
- 10A.4: Rapidly switch between slices. Verify no "phantom" slices or incorrect labels appear. ___

---

## Phase 7B: Dual-Channel Audio Architecture

### Channel separation

- 7B.1: Enable a toggle (e.g., NR). Verify earcon plays on the alert channel. ___
- 7B.2: Enable meter tones (Ctrl+Alt+M). Verify meter tones play on the meter channel. ___
- 7B.3: Play earcon and meter tone simultaneously. Verify both are audible and on their respective channels. ___

### Volume independence

- 7B.4: Open Settings, Audio tab. Adjust Alert Volume. Verify only earcon/alert volume changes (meter tones unaffected). ___
- 7B.5: Adjust Meter Volume. Verify only meter tone volume changes (alerts unaffected). ___
- 7B.6: Adjust Master Volume. Verify both alert and meter volumes scale together. ___

### Device selection

- 7B.7: In Audio tab, change Alert Device to a different output. Verify alerts play on the new device. ___
- 7B.8: Verify Meter Device shows "Same as Alerts" by default. ___
- 7B.9: Change Meter Device to a different output. Verify meter tones play on that device. ___

### Ding tone

- 7B.10: Trigger a ding tone (e.g., frequency entry confirmation). Verify the ding is audible and has a noticeable decay. ___

---

## Phase 8B: Audio Settings Tab

### Tab navigation

- 8B.1: Open Settings dialog. Tab through the tabs. Verify "Audio" tab is reachable. ___
- 8B.2: On the Audio tab, Tab through controls. Verify all controls are reachable in logical order. ___

### Controls and accessibility

- 8B.3: Tab to Master Volume slider. Verify screen reader announces the slider name and current value. ___
- 8B.4: Adjust Master Volume with arrow keys. Verify value change is announced. ___
- 8B.5: Tab to Alert Volume slider. Verify accessible name is announced. ___
- 8B.6: Tab to Alert Device dropdown. Verify it lists available audio output devices. ___
- 8B.7: Tab to Meter Volume slider. Verify accessible name is announced. ___
- 8B.8: Tab to Meter Device dropdown. Verify "Same as Alerts" is the default option. ___

### Audio Workshop button

- 8B.9: Tab to "Audio Workshop..." button on the Audio tab. Verify pressing it opens the Audio Workshop dialog. ___

### Persistence

- 8B.10: Change audio settings (volume levels, device). Close Settings with Save. Reopen Settings. Verify changes persisted. ___
- 8B.11: Close and relaunch the app. Open Settings, Audio tab. Verify settings survived restart. ___

---

## Phase 9B: About Dialog WebView2 Upgrade

### Dialog basics

- 9B.1: Open About dialog (Help > About). Verify dialog opens and screen reader reads the title. ___
- 9B.2: Verify WebView2 content loads (not blank, no error state). ___

### Screen reader heading navigation

- 9B.3: On the About tab, press H in NVDA browse mode. Verify screen reader navigates to headings within the HTML content. ___
- 9B.4: Navigate to System Info tab. Press H. Verify headings are navigable. ___
- 9B.5: Navigate to Credits tab. Press H. Verify headings are navigable. ___

### Tab switching

- 9B.6: Switch between About, System Info, and Credits tabs. Verify each loads the correct content. ___
- 9B.7: Verify version number on About tab matches the current build version. ___

### Copy functionality

- 9B.8: Press Copy to Clipboard (per-tab). Paste into a text editor. Verify plain text version of current tab's content. ___
- 9B.9: Press Copy All to Clipboard. Paste. Verify all tabs' content is included. ___

### Other features

- 9B.10: Click/activate the homepage link. Verify it opens in the default browser. ___
- 9B.11: Press Check for Updates. Verify the update check runs (online or offline response). ___
- 9B.12: Press Escape. Verify dialog closes. ___

---

## Phase 10B: DSP Level Minimums and Access Key Announcements

### DSP level minimums (may overlap with 8A.16-8A.18 — test here if not already covered)

- 10B.1: NR Level slider minimum is 1, not 0. ___
- 10B.2: NB Level slider minimum is 1, not 0. ___
- 10B.3: WNB Level slider minimum is 1, not 0. ___

### Access key announcements

- 10B.4: Open Settings dialog. Tab to the Save button. Verify NVDA announces the access key (e.g., "Save, Alt+S"). ___
- 10B.5: Tab to the Cancel button. Verify NVDA announces its access key (e.g., "Cancel, Alt+C"). ___
- 10B.6: Open About dialog. Tab to the Close button. Verify access key is announced. ___
- 10B.7: Open Filter Calculator. Tab to Apply and Close buttons. Verify access keys are announced for each. ___
- 10B.8: Open Status Dialog. Verify any buttons announce their access keys. ___
- 10B.9: Spot-check 3-4 other dialogs. Verify buttons with access keys announce them when tabbed to. ___

---

## Phase 11: 60m Mode Advisory on Band Jump

- 11.1: Set radio to a non-USB mode (e.g., CW or LSB). Press the 60m band jump (Shift+F3). Verify speech advises that the mode was auto-corrected to USB for 60m channels. ___
- 11.2: Verify the radio is now actually in USB mode after the band jump. ___
- 11.3: Already in USB mode. Press 60m band jump. Verify no advisory is spoken (mode was already correct). ___
- 11.4: Jump away from 60m (e.g., to 40m). Verify mode is not forced (user's original mode is not disturbed on other bands). ___

---

## Phase 12: Audio Workshop Tab Navigation Fix

- 12.1: Open Audio Workshop (Ctrl+Shift+W or menu). Verify dialog opens and title is announced. ___
- 12.2: Press Tab. Verify focus moves through controls within the dialog (not stuck on one button). ___
- 12.3: Verify all tabs in the Audio Workshop are reachable with Ctrl+Tab or arrow keys on the tab strip. ___
- 12.4: Switch to each tab. Verify controls within each tab are reachable by Tab. ___
- 12.5: Press Escape. Verify dialog closes. ___
- 12.6: Audio preset loading still works (select a preset, click Load, verify settings applied). ___

---

## Integration Tests (Post-Merge Verification)

These tests verify that features from both parallel tracks work correctly together after merge.

### Verbosity + new dialogs

- INT.1: Set verbosity to Terse. Open Status Dialog (Ctrl+Alt+S). Verify terse format applies. ___
- INT.2: Set verbosity to Terse. Open About Dialog. Verify content still loads fully (verbosity should not affect static HTML content). ___
- INT.3: Toggle verbosity while Status Dialog is open. Verify next refresh reflects the new level. ___

### Audio channels + earcons across features

- INT.4: Mute a slice (Shift+M). Verify earcon plays on alert channel. ___
- INT.5: Enable meter tones. Verify meter tones play on meter channel while slice mute earcon plays on alert channel. ___
- INT.6: Ding tone (from frequency entry) plays on alert channel while meter tones continue on meter channel. ___

### Key migration integrity

- INT.7: Verify Ctrl+J leader key works (press Ctrl+J, then N for NR toggle). ___
- INT.8: Verify band jump hotkeys work (e.g., Shift+F5 for 20m). ___
- INT.9: Verify all KeyDefs.xml loads correctly on startup (no "key config corrupt" warnings). ___
- INT.10: Open Help > Show Key Assignments. Verify keys are listed. Press Edit. Verify DefineCommandsDialog opens. ___
- INT.11: Open Help > Edit Key Assignments. Verify DefineCommandsDialog opens directly. ___

### Slice + audio interaction

- INT.12: Create a second slice. Adjust its volume in slice operations. Verify audio actually changes. ___
- INT.13: Mute slice B. Verify slice A audio is unaffected. ___
- INT.14: Remove slice B while it's muted. Verify no crash or orphaned audio state. ___

---

## Screen Reader Matrix

### NVDA (Primary)

- NVDA.1: All speech tests above verified with NVDA. ___
- NVDA.2: Verbosity cycling (Ctrl+Shift+V) announces levels clearly. ___
- NVDA.3: Status Dialog (Ctrl+Alt+S) content readable line-by-line with arrows. ___
- NVDA.4: About Dialog WebView2 heading navigation works with H key. ___
- NVDA.5: Slice selector announces slice letter, frequency, mode, ownership. ___
- NVDA.6: Audio tab sliders announce name and value. ___
- NVDA.7: Access key announcements work (buttons announce Alt+key shortcut). ___
- NVDA.8: Double-beep earcons (on/off toggles) audible and distinct. ___
- NVDA.9: Ding tone audible over radio audio. ___
- NVDA.10: 60m mode advisory announced when mode auto-corrected. ___
- NVDA.11: Audio Workshop tab navigation works — all tabs and controls reachable. ___
- NVDA.12: Quick-type frequency entry confirmation and commit/cancel announced. ___
- NVDA.13: Focus order correct across all new and modified dialogs. ___
- NVDA.14: No double/triple speak on any new or modified control. ___

### JAWS (Secondary)

- JAWS.1: Repeat key speech tests with JAWS active (verbosity cycling, status dialog, slice selector). ___
- JAWS.2: Status Dialog content readable in JAWS. ___
- JAWS.3: About Dialog WebView2 heading navigation works in JAWS. ___
- JAWS.4: Access key announcements work in JAWS. ___
- JAWS.5: Earcon tones and ding tone play correctly under JAWS. ___
- JAWS.6: Dialog modality works (Status Dialog, About Dialog, Settings, Audio Workshop block main window). ___
- JAWS.7: Slice selector announces correctly in JAWS. ___
- JAWS.8: Audio Workshop tab navigation works in JAWS. ___

---

## Known Limitations for This Test Cycle

- ATU tests: 6300 under test has no ATU. ATU-specific behavior (tune progress, timeout) cannot be tested. Verify ATU controls remain hidden on this radio.
- MultiFlex slice ownership: Needs two stations connected simultaneously. If unavailable, verify single-station shows no ownership clutter.
- JAWS availability: If JAWS is not installed, mark JAWS section as N/T (not tested) rather than FAIL.
- Diversity: 6300 is single-SCU. Diversity controls should remain hidden.

---

## Post-Test Actions

After all tests pass:
1. Version bump to 4.1.16 (both JJFlexRadio.vbproj AND AssemblyInfo.vb)
2. Update changelog (docs/CHANGELOG.md) — warm, user-facing tone
3. Clean Release build with build-installers.bat
4. Verify exe version matches 4.1.16
5. Tag v4.1.16, push to origin, create GitHub release with both x64 and x86 installers
6. Archive sprint plan to docs/planning/agile/archive/
