# Sprint 23 Test Matrix — v4.1.16 Pre-Release

**Tested by:** Noel Romey (K5NER)
**Date:** ___
**Build:** Release x64 (clean build from main, Sprint 23 complete)
**Screen Reader:** NVDA primary, JAWS secondary
**Radio:** FLEX-6300 "6300 inshack" via SmartLink (Don's radio)

---

## Pre-Test Setup

1. Clean build: `dotnet clean JJFlexRadio.vbproj -c Release -p:Platform=x64 && dotnet build JJFlexRadio.vbproj -c Release -p:Platform=x64 --verbosity minimal`
2. Verify exe version: `powershell -Command "(Get-Item 'bin\x64\Release\net8.0-windows\win-x64\JJFlexRadio.exe').VersionInfo.ProductVersion"`
3. Launch app, connect to radio via SmartLink
4. Start on a known band (e.g., 20m USB) before testing

---

## Phase 1: Kill Classic Menus (FINDING-65)

- 1.1: Press Ctrl+Shift+M. Verify speech says "Classic tuning mode" or "Modern tuning mode" (not "Classic mode" / "Modern mode"). PASS
- 1.2: Verify menu structure does NOT change when toggling mode — same menu items in both modes. PASS
- 1.3: Verify Logging submenu exists in the unified menu. PASS
- 1.4: Verify CW Message management exists in the unified menu. PASS
- 1.5: Arrow through all top-level menus. Confirm nothing is missing compared to old Classic menu items. PASS

---

## Phase 2: Unified Hotkey Dispatch (FINDING-34, 15, 33, 20, 14, 3, 4, 5)

### Expander hotkeys (all should work from Radio scope)
- 2.1: Ctrl+Shift+N opens/closes DSP ScreenFields category. PASS
- 2.2: Ctrl+Shift+U opens/closes Audio ScreenFields category. PASS
- 2.3: Ctrl+Shift+R opens/closes Receiver ScreenFields category. PASS
- 2.4: Ctrl+Shift+A opens/closes Antenna ScreenFields category. PASS
- 2.5: Ctrl+Shift+X opens/closes Transmission ScreenFields category. PASS

### Deconflicted hotkeys
- 2.6: Ctrl+M triggers Flex memories (NOT meter tones). PASS
- 2.7: Ctrl+Shift+M toggles meter tones (freed from mode toggle). PASS
- 2.8: Meter tones menu entry exists in menu (Audio or similar section). PASS
- 2.9: MetersPanel is reachable by Tab when meter tones are enabled. PASS
- 2.10: Ctrl+Shift+F speaks frequency readout (NOT TX filter). PASS
- 2.11: Ctrl+Shift+T toggles Tune Carrier (NOT Transmission expander). PASS

### No remaining conflicts
- 2.12: Press each hotkey listed above. Verify only ONE action fires per key. PASS

---

## Phase 3: Earcon Redesign (FINDING-10, 12, 13)

### Double-beep toggle patterns
- 3.1: Toggle a feature ON (e.g., JJ N for NR). Verify double ascending beep plays. PASS
- 3.2: Toggle same feature OFF. Verify double descending beep plays. PASS
- 3.3: Confirm old single-tone patterns are gone — all on/off toggles use double-beep. PASS

### ScreenFields checkbox earcons (FINDING-12)
- 3.4: Tab to a DSP checkbox (e.g., NR or ANF). Toggle with Space. Verify earcon plays. PASS
- 3.5: Toggle off. Verify off-earcon plays. PASS

### Dialog spawn/dismiss earcons (FINDING-13)
- 3.6: Open a dialog (e.g., Settings, About, Audio Workshop). Verify NO earcon on open (removed per user preference). PASS
- 3.7: Close the dialog. Verify NO earcon on close (removed per user preference). PASS
- 3.8: Open Filter Calculator. Verify same behavior — no open earcon. PASS

---

## Phase 4: ScreenFields Controls Standardization (FINDING-24, 21, 22, 23, 17)

### Double/triple-speak fix (FINDING-22)
- 4.1: Tab to a cycle control (e.g., Processor Mode). Verify it speaks ONCE, not two or three times. PASS
- 4.2: Arrow up/down on a cycle control. Verify value speaks ONCE per change. PASS
- 4.3: Tab to a numeric value control (e.g., TX Power). Verify it speaks ONCE. PASS
- 4.4: Arrow up/down on a value control. Verify value speaks ONCE per change. PASS (fixed: LeafAutomationPeer + UpdateVisual split)

### "Arrows to change" hint (FINDING-21)
- 4.5: Tab to a cycle control. Verify speech includes "arrows to change" hint. PASS
- 4.6: Arrow to change value. Verify hint does NOT repeat on each arrow press. PASS

### Step sizes (FINDING-23)
- 4.7: On a value control, Up/Down changes by 1. PASS
- 4.8: Shift+Up/Down changes by 5. PASS
- 4.9: PageUp/PageDown changes by 10. PASS

### Manual numeric entry (FINDING-17)
- 4.10: On a value control, type digits (e.g., "75"). Verify number mode activates. PASS
- 4.11: Press Enter. Verify typed value is applied (clamped to valid range). PASS
- 4.12: Start typing again, press Escape. Verify previous value restored. PASS

---

## Phase 5: Comprehensive Feature Gating (FINDING-11, 42, 43, 44)

### Neural NR gating (FINDING-11 / BUG-016)
- 5.1: Press JJ R (Neural NR toggle) on 6300. Verify speech says "not available on this radio" (or similar). PASS
- 5.2: Press JJ S (Spectral NR toggle) on 6300. Same gating check. PASS
- 5.3: Tab to Neural NR checkbox in DSP ScreenFields. Verify it is NOT present (hidden, not grayed). PASS (fixed: gate on hardware model, not license)
- 5.4: Check menu — Neural NR menu item should NOT be present on 6300. PASS

### ATU gating (FINDING-42, 43, 44)
- 5.5: On Don's 6300 (no ATU): verify ATU On/Off is NOT in the menu. PASS
- 5.6: ATU Tune is NOT in the menu. PASS
- 5.7: Antenna ScreenFields expander does NOT show ATU controls. PASS
- 5.8: Ctrl+T (ATU Tune hotkey) speaks "no antenna tuner available" or similar. PASS

### Diversity gating
- 5.9: On 6300 (single-SCU): diversity menu items are NOT present. PASS
- 5.10: Diversity ScreenFields controls are NOT present. PASS

### Slice gating
- 5.11: Radio.MaxSlices is respected — can create up to max (2 on 6300), then properly says "Maximum slices reached." PASS

---

## Phase 6: Slice Management (FINDING-49, 50, 51, 52, 53, 54)

### Create Slice fix (FINDING-49)
- 6.1: With 1 active slice on 6300, press Create Slice. Verify slice B is created (not "Maximum slices reached"). KNOWN BUG — after removing a slice, count not updated, says "Maximum slices reached" even with 1 slice. Deferred to Sprint 24 slice rework.
- 6.2: With 2 active slices, try Create Slice. NOW it should say "Maximum slices reached." PASS

### Stale data fix (FINDING-50)
- 6.3-6.4: Deferred to Sprint 24 slice rework.

### Consistent messaging (FINDING-51)
- 6.5: Deferred to Sprint 24 slice rework.

### A/B direct select (FINDING-54)
- 6.6-6.8: Deferred to Sprint 24 slice rework (Don reports intermittent issues).

### Slice creation speech (FINDING-53)
- 6.9: Deferred to Sprint 24 slice rework.

### VFO Slice Selector (FINDING-52)
- 6.10-6.12: Not yet built — Sprint 24 deliverable.

---

## Phase 7: Audio Workshop Accessibility (FINDING-36, 37, 40)

- 7.1: Open Audio Workshop (Ctrl+Shift+W or menu). Verify NVDA reads "Audio Workshop" as window title. PASS
- 7.2: Press Escape. Verify dialog closes. FAIL — Escape does not close Audio Workshop. Deferred to Sprint 24.
- 7.3: Tab navigation broken — only Load Preset button reachable, can't tab to tab controls or content. FAIL — deferred to Sprint 24 for deep fix.
- 7.4-7.6: Blocked by 7.3. Deferred to Sprint 24.

---

## Phase 8: About Dialog Accessibility (FINDING-28, 29)

- 8.1: Open About dialog (Help > About). Verify NVDA reads dialog title. PASS
- 8.2: Navigate to About tab. Verify content is readable (version, tribute). PASS (ListBox fix applied)
- 8.3: Navigate to System Info tab. Verify FlexLib version shows actual version (not 0.0.0.0). PASS
- 8.4: Navigate to Credits tab. Verify content is readable. PASS
- 8.5: Press Copy to Clipboard. Verify info is copied. PASS
- 8.6: Close with Escape. Verify focus returns to main window. PASS

---

## Phase 9: JJ Key Fixes + Repeat Last Message (FINDING-7, 8, 9, 16, 59, 62)

### JJ key speech fixes
- 9.1: Press Ctrl+J. Verify speech says "JJ" (not just "J"). PASS
- 9.2: Press N after Ctrl+J. Verify speech says "Legacy Noise Reduction on/off" (not just "Noise Reduction"). PASS

### RX filter speak
- 9.3: Press Ctrl+J then Shift+F. Verify speech reads RX filter (low, high, width). PASS (fixed in earlier testing session)

### Frequency speak hotkey (FINDING-59)
- 9.4: Press the frequency speak hotkey (Alt+F or whatever was assigned). Verify current frequency is spoken. PASS (fixed in earlier testing session)
- 9.5: Change frequency. Press hotkey again. Verify updated frequency. PASS

### Repeat last message (FINDING-7, 62)
- 9.6: Have something spoken (e.g., speak frequency). Press Ctrl+F4. Verify last message repeats. PASS
- 9.7: Double-press Ctrl+F4. Verify clipboard contains the message and/or speech viewer dialog opens. PASS

---

## Phase 10: ATU and Antenna Fixes (FINDING-41, 45, 46, 47)

### ATU workflow (FINDING-45)
- 10.1: Toggle ATU On/Off (checkbox or menu). Verify NO progress tones play — just a quiet enable/disable. ___
- 10.2: Verify speech says "ATU on" or "ATU off" (not "tuning" or "tune started"). ___

### ATU timeout (FINDING-46)
- 10.3: Start ATU Tune on a radio with ATU. If no match within 15 seconds, verify tones stop and "ATU tune timed out" is spoken. ___

### ATU progress tone speed (FINDING-47)
- 10.4: Start ATU Tune. Verify progress beeps are noticeably faster than before (50ms on/50ms off, not 100ms/100ms). ___

### Antenna menu speech (FINDING-41)
- 10.5: Select a different antenna from the menu. Verify speech announces the antenna change WITHOUT re-speaking the menu title first. ___

---

## Phase 11: 60m, Welcome Text, Minor Fixes (FINDING-6, 55, 56, 57, 60, 61, 64)

### Welcome text (FINDING-6)
- 11.1: Launch app. Verify welcome dialog says "JJ Flexible Radio Access" (not "JJRadio"). PASS

### 60m band jump (FINDING-55)
- 11.2: Press Shift+F3 (60m band jump). Verify frequency lands on Channel 1 (5.3320 MHz), NOT 5.400 MHz. PASS (band memory recalled 5.400 as expected; Channel Up navigated to Ch1 correctly)

### 60m wording (FINDING-56, 57)
- 11.3: Navigate to 60m digital segment. Verify speech says "60 meter digital and CW segment" (not just "digital"). ___
- 11.4: Open Settings, check country combo. Verify it says "United States" (not "US"). ___

### Connect/Disconnect wording (FINDING-60, 61)
- 11.5: While connected, open Radio menu. Verify it says "Disconnect" (not "Connect to radio"). PASS (fixed: single toggle item)
- 11.6: Click Disconnect (or Connect while connected). Verify confirmation wording is natural: "You're already connected to a radio. Disconnect from this radio and connect to another radio?" N/A — single Disconnect item now, no confirmation needed

### Dialog modality (FINDING-64)
- 11.7: Open Settings dialog. Try Alt+Tab to main window. Verify main window is blocked (Settings stays on top). PASS

---

## Phase 12: Filter Enhancements (FINDING-18, 19, 35)

### RX filter width display (FINDING-35)
- 12.1: Open DSP ScreenFields (Ctrl+Shift+N). Tab to RX filter width field. Verify it shows "RX Filter: [low] to [high], [width]". ___
- 12.2: Adjust RX filter edge. Verify width display updates live. ___

### Width in edge speech (FINDING-18)
- 12.3: Nudge TX filter low edge (Ctrl+Shift+[). Verify speech includes width (e.g., "Filter 100 to 2900, 2.8 k"). ___
- 12.4: Nudge TX filter high edge (Ctrl+Alt+]). Verify speech includes width. ___
- 12.5: Same test for RX filter edges (KeyCommands TX filter handlers). Verify width included. ___

### Filter Calculator (FINDING-19)
- 12.6: Open Filter Calculator from menu. Verify dialog opens with accessible title. ___
- 12.7: Enter low=500 and width=2000. Verify high auto-computes to 2500. ___
- 12.8: Clear, enter high=3000 and width=2400. Verify low auto-computes to 600. ___
- 12.9: Press Apply. Verify filter is set on the radio. ___
- 12.10: Press Escape. Verify dialog closes without applying. ___

---

## Phase 13: MultiFlex Awareness (FINDING-63)

- 13.1: With another station connected (MultiFlex), press Ctrl+Shift+S. Verify status includes slice ownership (e.g., "Slice A, yours. Slice B, Don."). ___
- 13.2: Cycle VFO (Ctrl+E or however VFO cycling works). Verify ownership is spoken with each slice. ___
- 13.3: Single-station connection. Verify no ownership clutter — just slice info without "yours." ___

Note: Full MultiFlex testing requires two stations connected. If only one station, verify ownership shows "yours" or is omitted gracefully.

---

## Phase 14: Command Finder + CHM Help (FINDING-31, 26)

### Command Finder category filtering (FINDING-31)
- 14.1: Open Command Finder (Ctrl+Shift+H). ___
- 14.2: Tab to category combo. Select "Audio." Verify list filters to show only Audio commands. ___
- 14.3: Arrow keys on closed category combo (don't open dropdown). Verify list updates as category changes. ___
- 14.4: Select "All." Verify full list returns. ___
- 14.5: Type in search box with category selected. Verify search + category filter combine correctly. ___

### CHM Help (FINDING-26)
- 14.6: Press F1. Verify CHM help file opens. ___
- 14.7: Press Ctrl+F1. Verify context-sensitive help page opens. ___
- 14.8: Press Shift+F1. Verify table of contents opens. ___
- 14.9: Close help. Verify focus returns to main window. ___

---

## Phase 15: RigSelector Fixes (FINDING-1, 2)

- 15.1: Launch app. Before connecting (no radios discovered), tab to radio list. Verify it IS in tab order (not skipped). ___
- 15.2: Verify empty list announces "Radio list, 0 items" or similar. ___
- 15.3: After radios are discovered, tab to radio list. Verify it announces selected item and count (e.g., "6300 inshack, 1 of 1"). ___
- 15.4: Arrow through multiple radios (if available). Verify each selection is announced with position. ___

---

## Phase 16: Library Versions + Build Verification (FINDING-30)

- 16.1: In About dialog System Info, verify JJFlexWpf version shows 2.1.0 (not 2.0.0). ___
- 16.2: Verify Radios.dll version shows 3.2.6.0. ___
- 16.3: Both x64 and x86 installers were created by build-installers.bat. ___
- 16.4: x64 installer runs and installs correctly. ___
- 16.5: x86 installer runs and installs correctly. ___

---

## Screen Reader Matrix

### NVDA
- All speech tests above verified with NVDA. ___
- Double-beep earcons audible and distinct. ___
- Dialog open/close earcons audible. ___
- Focus order correct across all ScreenFields categories. ___
- No double/triple speak on any control. ___

### JAWS
- Repeat key tests above with JAWS active. ___
- JJ key tones play correctly under JAWS. ___
- Dialog modality works (Settings, About, Filter Calculator block main window). ___
- Slice selector announces correctly in JAWS. ___

---

## Guided Testing Session Results (2026-03-12)

### Bugs Found and Fixed During Testing
- 9.3: JJ Shift+F (speak RX filter) — was bonking instead of speaking. Fixed: bare modifier keypress consuming leader state. PASS after fix.
- 9.4/9.5: JJ Ctrl+F (enter frequency) — was bonking, wrong method name. Fixed: SetFrequency() → WriteFreq(). PASS after fix.
- Frequency readback was saying slice number instead of letter. Fixed: VFOToLetter(). PASS after fix.
- 2.11: Ctrl+Shift+T (tune carrier) — infinite repeating beeps on 6300 (no ATU). Fixed: progress earcons gated on auto ATU + HasATU. PASS after fix.
- Meter tones (FeatureOnTone/FeatureOffTone) missing from ToggleMeters and ToggleFreqReadout. Fixed. PASS.
- Filter "at limit" at -3500 in LSB — false positive because high edge at 0 matched highMax. Fixed: clamping-based detection. PASS.
- Adaptive filter step too slow (50 Hz max). Fixed: added 100 Hz and 200 Hz tiers. PASS.
- Meter tones silent when enabled — tone providers not proactively activated. Fixed: Enabled setter now activates slots. PASS.

### Items Tested and Passing
- Filter widening in LSB past -3500 — works to -12000. PASS.
- Filter widening in AM to 24 kHz — works. PASS.
- JJ Shift+F speaks RX filter width. PASS.
- Single-tap bracket widens filter. PASS.
- Edge grab mode (double-tap bracket) works. PASS.
- Ctrl+bracket (pull/squeeze) works. PASS.
- Alt+bracket preset cycling works. PASS.
- Meter tones activate and produce audible tone. PASS.
- Ctrl+Shift+T tune on/off with rising/falling chirp. PASS.

### Phases Fully Passed
- Phase 1 — PASS (all items)
- Phase 2 — PASS (all items, including Ctrl+Shift+S conflict fix)
- Phase 3 — PASS (all items: double-beep toggles, checkbox earcons, dialog earcons removed)
- Phase 4 — PASS (all items: double-speak fixed, step sizes, numeric entry, arrows-to-change hint)
- Phase 5 — PASS (all items: Neural NR gated on hardware, ATU hidden, diversity hidden, max slices)
- Phase 8 — PASS (all items: About dialog ListBox fix, all tabs readable)
- Phase 11 — mostly PASS (welcome text, band jump, disconnect wording, modality; 11.3/11.4 untested)
- Mini Sprint 24a — PASS (all 6 phases)

### Phases with Issues
- Phase 6 — slice count bug after remove, deferred to Sprint 24 slice rework
- Phase 7 — Audio Workshop tab nav broken + Escape doesn't close, deferred to Sprint 24

### Items Remaining for Pre-Release Testing
- Phase 9 — JJ key fixes, repeat last message (Ctrl+J confirmed saying "JJ")
- Phase 11.3/11.4 — 60m segment wording, country combo
- Phase 12 — filter width display, Filter Calculator (most filter work passed in earlier testing)
- Phase 14 — Command Finder (PASS per Don's feedback on help files), CHM Help (PASS per Don)
- Phase 15 — RigSelector announcements
- Phase 16 — library versions, installer verification (pre-release build)
- Phase 10 (ATU) — can't test on 6300 without ATU hardware
- Phase 13 (MultiFlex) — needs two stations connected
- Screen reader matrix (JAWS) — not yet done

---

## Mini Sprint 24a: Testing Backlog Cleanup

### Phase A1: Earcon Audit — All Toggles
- A1.1: Every toggle action has FeatureOnTone (ascending) for on, FeatureOffTone (descending) for off. ___
- A1.2: No toggle is missing earcon feedback. ___

### Phase A2: Dialog Earcons — Removed
- A2.1: Open any dialog (e.g., Settings, About). Verify NO earcon plays on open. ___
- A2.2: Close any dialog. Verify NO earcon plays on close. ___
- A2.3: Toggle a feature (e.g., Mute). Verify earcon still plays for toggles. ___

### Phase A3: Access Keys for All Dialogs
- A3.1: Open any dialog with OK/Cancel buttons. Alt+O activates OK, Alt+C activates Cancel. ___
- A3.2: Open About dialog. Alt+C activates Close button. ___
- A3.3: Open Filter Calculator. Alt+A activates Apply, Alt+C activates Close. ___
- A3.4: Open Settings. Alt+S activates Save, Alt+C activates Cancel. ___
- A3.5: Access keys are underlined in button text when Alt is held. ___

### Phase A4: Meter Hotkeys and Auto-SWR
- A4.1: Ctrl+Alt+P cycles meter presets (RX Monitor → TX Monitor → Full Monitor). Speech announces preset name. ___
- A4.2: Ctrl+Alt+V speaks current meter values on demand. ___
- A4.3: Ctrl+Alt+M toggles meter tones with earcon (ascending on, descending off). ___
- A4.4: Ctrl+Shift+T (tune carrier) auto-enables SWR tone — tune off restores previous meter state. ___

### Phase A5: Wider Filter Presets
- A5.1: SSB filter presets include wider options beyond 4 kHz. ___
- A5.2: CW filter presets include wider options beyond 2 kHz. ___
- A5.3: Alt+] cycles to wider presets. Verify new presets are reachable. ___

### Phase A6: Ctrl+Shift+S Status Audit
- A6.1: Status speech includes tuning mode (coarse/fine) when in Modern mode. ___
- A6.2: Status speech includes frequency readout state (on/off) if off. ___
- A6.3: Status speech includes meter tones state and active preset if enabled. ___
- A6.4: Status speech includes filter edge grab if active. ___

---

## Deferred Findings (NOT tested — intentionally skipped)

- FINDING-25: Multi-radio abstraction — deferred to radio abstraction sprint
- FINDING-48: Earcon fade-out/envelope — deferred to backlog, not critical for 4.1.16
- Connection error hang — needs deeper investigation, deferred
- Slice field up/down at leftmost position adjusts volume instead of switching slices — known, deferred

---

## Post-Test Actions

After all tests pass:
1. Version bump to 4.1.16 (both JJFlexRadio.vbproj AND AssemblyInfo.vb)
2. Update changelog (docs/CHANGELOG.md) — warm, user-facing tone
3. Clean build with build-installers.bat
4. Tag v4.1.16, push to origin, create GitHub release
5. Archive sprint plan to docs/planning/agile/archive/
