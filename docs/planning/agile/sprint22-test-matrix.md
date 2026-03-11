# Sprint 21 + 22 Test Matrix — v4.1.16 Pre-Release

**Tested by:** Noel Romey (K5NER)
**Date:** 2026-03-10
**Build:** Debug x64 (clean build from main, build fix applied — internal→public, FunctionGroups.tuning added)
**Screen Reader:** NVDA primary, JAWS secondary
**Radio:** FLEX-6300 "6300 inshack" via SmartLink (Don's radio)

---

## Pre-Test Setup

1. Clean build: `dotnet clean JJFlexRadio.vbproj -c Debug -p:Platform=x64 && dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal`
2. Launch app, connect to radio
3. Start on a known band (e.g., 20m USB) before testing

---

## Sprint 21 Items

### 1. Meter Tones

- 1.1: BLOCKED — Ctrl+M conflicts with Flex memories (Radio scope wins). No menu entry exists. Meter tones unreachable. [FINDING-3, FINDING-5]
- 1.2: BLOCKED — can't enable meter tones
- 1.3: BLOCKED
- 1.4: BLOCKED
- 1.5: BLOCKED
- 1.6: BLOCKED
- 1.7: BLOCKED

### 2. Audio Workshop

- 2.1: Open Audio Workshop (Ctrl+Shift+W). Verify dialog opens and NVDA reads the title. — NOT YET TESTED
- 2.2: Tab through all 3 tabs. Confirm each tab is reachable and announced. — NOT YET TESTED
- 2.3: Modify a preset value (e.g., mic gain). Confirm control responds. — NOT YET TESTED
- 2.4: Save preset. Confirm save feedback. — NOT YET TESTED
- 2.5: Load a different preset. Confirm values change. — NOT YET TESTED
- 2.6: Close dialog with Escape. Confirm focus returns to main window. — NOT YET TESTED

### 3. JJ Key (Ctrl+J)

- 3.1: Press Ctrl+J. Verify JJ key tone plays (rising chirp). — PASS (tone plays, but speech says "J" not "JJ") [FINDING-8]
- 3.2: Press N (toggle Noise Reduction). — PASS (toggles legacy NR, says "Noise Reduction on/off"). Note: test matrix originally said Neural NR but N=legacy NR, R=Neural NR.
- 3.3: Press B (toggle Noise Blanker). — NOT YET TESTED
- 3.4: Press W (open Audio Workshop). — NOT YET TESTED
- 3.5: Press R (Neural NR). — FAIL: Toggles Neural NR on 6300 even though radio doesn't support it. Feature gating broken. [FINDING-11]
- 3.6: Press S (speak status). — NOT YET TESTED
- 3.7: Press A (toggle APF, CW mode only). — NOT YET TESTED
- 3.8: Press P (cycle meter preset). — NOT YET TESTED
- 3.9: Press M (toggle meter tones). — NOT YET TESTED (meters unreachable, may have same issue)
- 3.10: Press T (cycle tuning step). — NOT YET TESTED (T not in DoLeaderCommand — test matrix wrong?)
- 3.11: Press E (toggle meter tones — same as M). — NOT YET TESTED (E not in DoLeaderCommand — test matrix wrong?)
- 3.12: Press F (speak TX filter). — PASS
- 3.13: Press D (toggle tuning debounce). — PASS (speaks on/off correctly)
- 3.14: Press L (log statistics). — NOT YET TESTED
- 3.15: Press H or ? (JJ key help). — PASS
- 3.16: Press Escape after Ctrl+J. — PASS (cancel tone plays)
- 3.17: Press an unbound key after Ctrl+J. — PASS (error tone plays)

### 4. TX Filter Sculpting

- 4.1: Ctrl+Shift+[ (TX filter low down). — PASS
- 4.2: Ctrl+Shift+] (TX filter low up). — NOT YET TESTED
- 4.3: Ctrl+Alt+[ (TX filter high down). — NOT YET TESTED
- 4.4: Ctrl+Alt+] (TX filter high up). — NOT YET TESTED
- 4.5: Ctrl+Shift+F (speak TX filter). — FAIL: conflicts with frequency readout. Use JJ F instead. [FINDING-14]

### 5. ScreenFields TX Expansion

- 5.1: Open Transmission category. — PASS (note: Ctrl+Shift+T now opens Tune Carrier, not Transmission. Use menu or tab/arrow to reach Transmission.) [FINDING-20]
- 5.2: Tab through controls. All present and announced. — PASS
- 5.3: Toggle Compander on. Compander Level appears. — PASS
- 5.4: Toggle Speech Processor on. Processor Mode appears. — PASS (double-speak issue: value announced twice) [FINDING-22]
- 5.5: Toggle TX Monitor on. Monitor Level appears. — PASS
- 5.6: Adjust TX Power with Up/Down. Value changes. — PASS

### 6. CHM Help

- 6.1: Press F1. — FAIL: CHM file was never compiled. JJFlexRadio.chm doesn't exist in build output. Launcher silently fails. [FINDING-26]
- 6.2: Close help and verify focus returns. — BLOCKED

### 7. App Rename

- 7.1: Window title. — NOT YET VERIFIED (need to check title bar text)
- 7.2: Welcome speech. — PASS (says "Welcome to JJ Flexible Radio Access"). Note: Welcome.Designer.vb still says "Welcome to JJRadio" — stale. [FINDING-6]

### 8. Sprint 21 Bug Fixes

- 8.1: BUG-004 shutdown crash. — NOT YET TESTED
- 8.2: BUG-016 DSP gating. — FAIL: Neural NR toggles on 6300 which doesn't support it. Feature gating broken across JJ key, ScreenFields, and likely menus. [FINDING-11]
- 8.3: BUG-023 connect confirmation. — NOT YET TESTED

---

## Sprint 22 Items

### 9. Phase 1: RigSelector Auto-Connect

- 9.1: Auto-Connect button present. — NOT YET TESTED (connected via SmartLink before checking)
- 9.2: Auto-Connect connects to first radio. — NOT YET TESTED
- 9.3: No radios discovered, Auto-Connect feedback. — NOT YET TESTED
- 9.4: Empty radio list announcement. — PARTIAL PASS: Startup speech says "no radios connected" but empty listbox not reachable by tab, no "0 items" announcement. [FINDING-1, FINDING-2]

### 10. Phase 2: About Dialog

- 10.1: Opens from Help > About. — PASS (dialog opens)
- 10.2: Tab through 3 tabs. — FAIL: Can't navigate tabs or content. Only reads "JJ Flexible Radio." [FINDING-28]
- 10.3: About tab content. — FAIL: Can't read version, tribute, etc. [FINDING-28]
- 10.4: System Info tab. — FAIL: Can't reach tab. FlexLib shows 0.0.0.0 in clipboard paste. [FINDING-29]
- 10.5: Copy to Clipboard. — PASS (copies all info correctly)
- 10.6: Close with Escape. — NOT YET TESTED

### 11. Phase 3: Command Finder Categories

- 11.1: Open Command Finder (Ctrl+Shift+H). — PASS
- 11.2: Tab to category combo. Categories listed. — PASS
- 11.3: Select category, list filters. — FAIL: Selecting "Audio" doesn't filter the list. [FINDING-31]
- 11.4: Select "All", full list returns. — NOT YET TESTED (filtering broken)
- 11.5: Search within category. — NOT YET TESTED

### 12. Phase 4: Tuning Speech Debounce

- 12.1: Debounce on by default, only final freq spoken. — PASS
- 12.2: JJ key D toggle off. — PARTIAL: Speech says "Tuning debounce off" but behavior sounds the same. [FINDING-32]
- 12.3: JJ key D toggle back on. — NOT YET TESTED
- 12.4: Settings > Tuning tab. — NOT YET TESTED
- 12.5: Change delay to 500ms. — NOT YET TESTED
- 12.6: Uncheck debounce in Settings. — NOT YET TESTED

### 13. Phase 5: Tune Carrier + ATU Tune

- 13.1: Ctrl+Shift+T (Tune carrier toggle). — BLOCKED: Ctrl+Shift+T conflicts with Transmission expander hotkey (different dispatch systems). Need deconfliction first. [FINDING-20]
- 13.2: Ctrl+Shift+T again to turn off. — BLOCKED
- 13.3: Tune toggle button state. — BLOCKED
- 13.4: Ctrl+T (ATU Tune). — NOT YET TESTED (separate key, may work)
- 13.5: ATU success feedback. — NOT YET TESTED
- 13.6: ATU fail feedback. — NOT YET TESTED
- 13.7: Menu items for Tune Carrier. — NOT YET TESTED
- 13.8: Menu items for ATU Tune. — NOT YET TESTED

### 14. Phase 6: Antenna Switching

- 14.1: Menu > Antenna > RX Antenna submenu. — NOT YET TESTED
- 14.2: Select different RX antenna. — NOT YET TESTED
- 14.3: TX Antenna submenu. — NOT YET TESTED
- 14.4: Ctrl+Shift+A to open Antenna ScreenFields. — NOT YET TESTED
- 14.5: Cycle RX antenna in ScreenFields. — NOT YET TESTED
- 14.6: Cycle TX antenna. — NOT YET TESTED
- 14.7: Menu checkmarks. — NOT YET TESTED

### 15. Phase 7: Slice Management + Enhanced Status

- 15.1: Ctrl+Shift+U to open Audio and Slice category. — NOT YET TESTED (note: test matrix said Ctrl+Shift+2, corrected to Ctrl+Shift+U)
- 15.2: Tab to Create Slice button. — NOT YET TESTED
- 15.3: Create Slice. — NOT YET TESTED (6300 supports max 2 slices)
- 15.4: Max slices reached. — NOT YET TESTED
- 15.5: Release Slice. — NOT YET TESTED
- 15.6: Release only slice. — NOT YET TESTED
- 15.7: Menu items. — NOT YET TESTED
- 15.8: Multi-slice Ctrl+Shift+S. — NOT YET TESTED
- 15.9: Single-slice Ctrl+Shift+S. — NOT YET TESTED

### 16. Phase 8: Startup Speech

- 16.1: Connect to radio, wait. — TESTED but speech was interrupted before fully heard
- 16.2: Verify speech content. — INCONCLUSIVE: Speech fired but Noel cut it short. Likely working but needs re-test. [FINDING-7: need "speak again" or reviewable dialog for long speech]
- 16.3: Speech not cut off by focus changes. — NOT YET TESTED
- 16.4: No radio, welcome speech only. — PASS (welcome speech played on startup without radio)

### 17. Phase 9: Meters Panel

- 17.1: BLOCKED — Ctrl+M conflicts with Flex memories, no menu entry. MetersPanel not reachable by tab. [FINDING-3, FINDING-4, FINDING-5]
- 17.2-17.15: BLOCKED

### 18. Phase 10: 60m Channelization

- 18.1: Tune to 60m (Shift+F3). — NOT YET TESTED
- 18.2: Alt+Shift+Up. — NOT YET TESTED
- 18.3: Cycle through channels. — NOT YET TESTED
- 18.4: Alt+Shift+Down. — NOT YET TESTED
- 18.5: Mode set to USB. — NOT YET TESTED
- 18.6: Menu items. — NOT YET TESTED
- 18.7: Settings > License tab. — NOT YET TESTED
- 18.8: Enforce rules default. — NOT YET TESTED

---

## Screen Reader Matrix

### NVDA

- All speech announcements above verified with NVDA where tested.
- Focus order: NOT FULLY TESTED — MetersPanel not reachable by tab.
- Startup speech: fired but needs re-test for completeness.
- JJ key tones audible and distinct from speech. — PASS
- Menu navigation: new items partially tested.
- ScreenFields double-speak issue on cycle controls. [FINDING-22]
- No earcon on ScreenFields checkbox toggle. [FINDING-12]

### JAWS

- NOT YET TESTED with JAWS.

---

## Findings Log

- FINDING-1: RigSelector empty listbox not reachable by tab
- FINDING-2: RigSelector listbox doesn't announce selected item on focus
- FINDING-3: Ctrl+M conflicts with Flex memories — meter tones toggle unreachable
- FINDING-4: MetersPanel not reachable by tab navigation
- FINDING-5: No menu entry for meter tones toggle
- FINDING-6: Welcome.Designer.vb still says "Welcome to JJRadio"
- FINDING-7: Long speech needs reviewable dialog (double-press to copy/show)
- FINDING-8: JJ key entry speech says "J" instead of "JJ"
- FINDING-9: JJ key N says "Noise Reduction" without specifying type (legacy vs Neural)
- FINDING-10: Earcon redesign — double-beep patterns for on/off/help/dialog
- FINDING-11: Feature gating broken — Neural NR toggles on 6300 which doesn't support it (JJ key, ScreenFields, menus)
- FINDING-12: No earcon feedback on ScreenFields checkbox toggle
- FINDING-13: Universal dialog spawn/dismiss earcons (feature request)
- FINDING-14: Ctrl+Shift+F conflicts with frequency readout — move TX filter speak to JJ F only
- FINDING-15: Need full hotkey conflict audit before any new key assignments
- FINDING-16: Add JJ Shift+F to speak RX filter width
- FINDING-17: Manual numeric entry for filter values (feature request)
- FINDING-18: Filter controls should speak width after adjusting edge
- FINDING-19: Filter calculator — enter any two of low/high/width, compute the third
- FINDING-20: Ctrl+Shift+T conflict — Tune Carrier took Transmission expander's key
- FINDING-21: Cycle controls need "arrows to change" hint in accessible name
- FINDING-22: Double-speak on cycle control value change
- FINDING-23: Add Shift+Up/Down for step-of-5 on value controls (feature request)
- FINDING-24: Standardize all ScreenFields value control behavior
- FINDING-25: ScreenFields control abstraction for multi-radio future
- FINDING-26: CHM help file never compiled — F1 silently fails
- FINDING-27: Three help paths: F1 (context keys, existing), Ctrl+F1 (CHM context page), Shift+F1 (CHM TOC)
- FINDING-28: About dialog mostly inaccessible — can't navigate tabs or read content
- FINDING-29: FlexLib version shows 0.0.0.0 in About dialog
- FINDING-30: Internal library versions not maintained — semver as touched
- FINDING-31: Command Finder category filtering broken
- FINDING-32: Tuning debounce toggle may not have noticeable effect
- FINDING-33: Hotkey deconfliction master list (Ctrl+M, Ctrl+Shift+T, Ctrl+Shift+F, Ctrl+Shift+N, Ctrl+Shift+U, Ctrl+Shift+S)
- FINDING-34: Unify all hotkeys into KeyCommands.vb scope system
- FINDING-35: Add computed RX filter width display in DSP ScreenFields
- FINDING-36: Audio Workshop doesn't announce dialog title on open
- FINDING-37: Audio Workshop can't tab past "Load Audio Preset" button — dialog non-functional
- FINDING-38: Audio Workshop should mute RX audio while open (but smart — option for TX monitor via second antenna)
- FINDING-39: Antenna naming — let users label antenna ports with friendly names, show everywhere
- FINDING-40: Audio Workshop doesn't close with Escape
- FINDING-41: RX antenna switch speaks menu title before announcing change
- FINDING-42: ATU on/off has no checkbox, not grayed when no ATU present — should not appear at all if no ATU
- FINDING-43: ATU menu items should be grayed/hidden when radio has no ATU
- FINDING-44: Antenna expander should hide/disable ATU controls if no ATU
- FINDING-45: ATU workflow: enable/disable (quiet toggle), Tune Carrier (manual), ATU Tune (auto) — three separate actions
- FINDING-46: ATU progress tone beeps indefinitely — needs timeout (10-15 sec)
- FINDING-47: ATU progress tone timing too slow — halve tone duration and delay
- FINDING-48: Add fade-out/envelope support to earcon system
- FINDING-49: Create Slice button doesn't work — says "maximum slices reached" with 1 active slice
- FINDING-50: Slice menu shows stale data — not refreshing from radio events
- FINDING-51: Inconsistent slice messaging — "Maximum slices reached" vs "All slices in use"
- FINDING-52: Slice selector and status in VFO area — arrow to switch slices, F1 for context operations
- FINDING-53: Slice creation should keep current slice selected, speak new slice info
- FINDING-54: Slice selection keys A/B toggle instead of selecting directly — A should go to A
- FINDING-55: 60m band jump (Shift+F3) should land on Channel 1 by default
- FINDING-56: 60m digital segment should say "60 meter digital/CW segment"
- FINDING-57: Country combo should say "United States" not "US"
- FINDING-58: Additional 60m testing needed — segment boundaries, other countries
- FINDING-59: No hotkey to speak current frequency on demand — needs dedicated single key, not just JJ key
- FINDING-60: Radio menu says "Connect to radio" while already connected — should say "Disconnect"
- FINDING-61: Connect confirmation dialog wording cleanup
- FINDING-62: Startup speech gets drowned out by radio audio — delay audio unmute or add "repeat last message"
- FINDING-63: MultiFlex awareness — startup speech and Ctrl+Shift+S should show slice ownership ("Slice A in use by Don")
- FINDING-64: Settings dialog is not modal — can Alt+Tab to main window
- FINDING-65: Kill Classic menus — one menu structure, Ctrl+Shift+M just toggles tuning mode

## Session 2 Test Results (Late Night)

### Tests Passed
- 2.1: Audio Workshop opens (partial — announces wrong thing)
- 2.6: Audio Workshop close — FAIL with Escape [FINDING-40], Alt+F4 works
- 8.1: BUG-004 shutdown crash — PASS, clean exit
- 12.4: Settings > Tuning tab, debounce checkbox and delay — PASS
- 13.4: Ctrl+T ATU Tune — PASS ("no antenna tuner available" on 6300)
- 13.7: Tune Carrier in Classic menu — PASS
- 13.8: ATU Tune in Antenna menu — PASS (but ATU on/off not grayed)
- 14.1: Antenna submenus present in both menus — PASS
- 14.2: RX antenna switch and speech — PASS (menu title re-speaks first)
- 14.3: TX antenna switch and speech — PASS
- 14.4: Ctrl+Shift+A Antenna expander — PASS
- 14.7: Antenna menu checkmarks — PASS
- 15.4: Max slices message — PASS (but triggered incorrectly from Create)
- 15.7: Create/Release Slice in menus — PASS
- 16.2: Startup speech — PASS (content correct but drowned by radio audio)
- 18.1-18.6: 60m channelization — PASS (minor wording fixes needed)
- 18.7: Settings License tab — PASS
- 18.8: Enforce rules default on — PASS

### Tests Failed
- 2.2-2.5: Audio Workshop navigation — FAIL (can't tab, can't close with Escape)
- 14.5-14.6: Antenna cycling in ScreenFields — double/triple speak issue
- 15.3: Create Slice — FAIL (says max reached with 1 slice)
- 15.8: Multi-slice status — tested but slice management has stale data issues

## Build Fixes Applied During Testing

- Changed `internal` to `public` on: CurrentAudioConfig, ToggleTuneCarrier, StartATUTuneCycle, ToggleMetersPanel in MainWindow.xaml.cs
- Added `tuning` to FunctionGroups enum in KeyCommands.vb
- These fixes were required to get a successful build — Sprint 22 shipped with 6 build errors
