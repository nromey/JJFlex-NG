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

- 1.1: Press Ctrl+Shift+M. Verify speech says "Classic tuning mode" or "Modern tuning mode" (not "Classic mode" / "Modern mode"). ___
- 1.2: Verify menu structure does NOT change when toggling mode — same menu items in both modes. ___
- 1.3: Verify Logging submenu exists in the unified menu. ___
- 1.4: Verify CW Message management exists in the unified menu. ___
- 1.5: Arrow through all top-level menus. Confirm nothing is missing compared to old Classic menu items. ___

---

## Phase 2: Unified Hotkey Dispatch (FINDING-34, 15, 33, 20, 14, 3, 4, 5)

### Expander hotkeys (all should work from Radio scope)
- 2.1: Ctrl+Shift+N opens/closes DSP ScreenFields category. ___
- 2.2: Ctrl+Shift+U opens/closes Audio ScreenFields category. ___
- 2.3: Ctrl+Shift+R opens/closes Receiver ScreenFields category. ___
- 2.4: Ctrl+Shift+A opens/closes Antenna ScreenFields category. ___
- 2.5: Ctrl+Shift+X opens/closes Transmission ScreenFields category. ___

### Deconflicted hotkeys
- 2.6: Ctrl+M triggers Flex memories (NOT meter tones). ___
- 2.7: Ctrl+Shift+M toggles meter tones (freed from mode toggle). ___
- 2.8: Meter tones menu entry exists in menu (Audio or similar section). ___
- 2.9: MetersPanel is reachable by Tab when meter tones are enabled. ___
- 2.10: Ctrl+Shift+F speaks frequency readout (NOT TX filter). ___
- 2.11: Ctrl+Shift+T toggles Tune Carrier (NOT Transmission expander). ___

### No remaining conflicts
- 2.12: Press each hotkey listed above. Verify only ONE action fires per key. ___

---

## Phase 3: Earcon Redesign (FINDING-10, 12, 13)

### Double-beep toggle patterns
- 3.1: Toggle a feature ON (e.g., JJ N for NR). Verify double ascending beep plays. ___
- 3.2: Toggle same feature OFF. Verify double descending beep plays. ___
- 3.3: Confirm old single-tone patterns are gone — all on/off toggles use double-beep. ___

### ScreenFields checkbox earcons (FINDING-12)
- 3.4: Tab to a DSP checkbox (e.g., NR or ANF). Toggle with Space. Verify earcon plays. ___
- 3.5: Toggle off. Verify off-earcon plays. ___

### Dialog spawn/dismiss earcons (FINDING-13)
- 3.6: Open a dialog (e.g., Settings, About, Audio Workshop). Verify ascending ding on open. ___
- 3.7: Close the dialog. Verify descending ding on close. ___
- 3.8: Open Filter Calculator. Verify same open earcon. ___

---

## Phase 4: ScreenFields Controls Standardization (FINDING-24, 21, 22, 23, 17)

### Double/triple-speak fix (FINDING-22)
- 4.1: Tab to a cycle control (e.g., Processor Mode). Verify it speaks ONCE, not two or three times. ___
- 4.2: Arrow up/down on a cycle control. Verify value speaks ONCE per change. ___
- 4.3: Tab to a numeric value control (e.g., TX Power). Verify it speaks ONCE. ___
- 4.4: Arrow up/down on a value control. Verify value speaks ONCE per change. ___

### "Arrows to change" hint (FINDING-21)
- 4.5: Tab to a cycle control. Verify speech includes "arrows to change" hint. ___
- 4.6: Arrow to change value. Verify hint does NOT repeat on each arrow press. ___

### Step sizes (FINDING-23)
- 4.7: On a value control, Up/Down changes by 1. ___
- 4.8: Shift+Up/Down changes by 5. ___
- 4.9: PageUp/PageDown changes by 10. ___

### Manual numeric entry (FINDING-17)
- 4.10: On a value control, type digits (e.g., "75"). Verify number mode activates. ___
- 4.11: Press Enter. Verify typed value is applied (clamped to valid range). ___
- 4.12: Start typing again, press Escape. Verify previous value restored. ___

---

## Phase 5: Comprehensive Feature Gating (FINDING-11, 42, 43, 44)

### Neural NR gating (FINDING-11 / BUG-016)
- 5.1: Press JJ R (Neural NR toggle) on 6300. Verify speech says "not available on this radio" (or similar). ___
- 5.2: Press JJ S (Spectral NR toggle) on 6300. Same gating check. ___
- 5.3: Tab to Neural NR checkbox in DSP ScreenFields. Verify it is NOT present (hidden, not grayed). ___
- 5.4: Check menu — Neural NR menu item should NOT be present on 6300. ___

### ATU gating (FINDING-42, 43, 44)
- 5.5: On Don's 6300 (no ATU): verify ATU On/Off is NOT in the menu. ___
- 5.6: ATU Tune is NOT in the menu. ___
- 5.7: Antenna ScreenFields expander does NOT show ATU controls. ___
- 5.8: Ctrl+T (ATU Tune hotkey) speaks "no antenna tuner available" or similar. ___

### Diversity gating
- 5.9: On 6300 (single-SCU): diversity menu items are NOT present. ___
- 5.10: Diversity ScreenFields controls are NOT present. ___

### Slice gating
- 5.11: Radio.MaxSlices is respected — can create up to max (2 on 6300), then properly says "Maximum slices reached." ___

---

## Phase 6: Slice Management (FINDING-49, 50, 51, 52, 53, 54)

### Create Slice fix (FINDING-49)
- 6.1: With 1 active slice on 6300, press Create Slice. Verify slice B is created (not "Maximum slices reached"). ___
- 6.2: With 2 active slices, try Create Slice. NOW it should say "Maximum slices reached." ___

### Stale data fix (FINDING-50)
- 6.3: Create a slice. Open slice menu. Verify it shows current slice info (not stale "Slice 2/0"). ___
- 6.4: Release a slice. Re-open slice menu. Verify it updates. ___

### Consistent messaging (FINDING-51)
- 6.5: All "max slices" messages use the same wording across menu, hotkey, and ScreenFields. ___

### A/B direct select (FINDING-54)
- 6.6: With 2 slices active, press A. Verify you're on Slice A. ___
- 6.7: Press B. Verify you're on Slice B. ___
- 6.8: Press A again. Verify you stay on Slice A (not toggle). ___

### Slice creation speech (FINDING-53)
- 6.9: Create a new slice. Verify speech says "Slice B created on [freq], [mode]. Slice A still selected." ___

### VFO Slice Selector (FINDING-52)
- 6.10: Tab to slice selector control (left of frequency display). Verify it announces current slice. ___
- 6.11: Up/Down arrows to switch slices. Verify speech announces new slice. ___
- 6.12: Tab once more to reach slice operations (volume, pan, mute). ___

---

## Phase 7: Audio Workshop Accessibility (FINDING-36, 37, 40)

- 7.1: Open Audio Workshop (Ctrl+Shift+W or menu). Verify NVDA reads "Audio Workshop" as window title. ___
- 7.2: Press Escape. Verify dialog closes. ___
- 7.3: Re-open Audio Workshop. Tab through all controls. Verify all 3 tabs reachable (Ctrl+Tab or arrow). ___
- 7.4: On TX Audio tab, verify controls are tabbable and announce correctly. ___
- 7.5: On Live Meters tab, verify controls are tabbable. ___
- 7.6: On Earcon Explorer tab, verify controls are tabbable. ___

---

## Phase 8: About Dialog Accessibility (FINDING-28, 29)

- 8.1: Open About dialog (Help > About). Verify NVDA reads dialog title. ___
- 8.2: Navigate to About tab. Verify content is readable (version, tribute). ___
- 8.3: Navigate to System Info tab. Verify FlexLib version shows actual version (not 0.0.0.0). ___
- 8.4: Navigate to Credits tab. Verify content is readable. ___
- 8.5: Press Copy to Clipboard. Verify info is copied. ___
- 8.6: Close with Escape. Verify focus returns to main window. ___

---

## Phase 9: JJ Key Fixes + Repeat Last Message (FINDING-7, 8, 9, 16, 59, 62)

### JJ key speech fixes
- 9.1: Press Ctrl+J. Verify speech says "JJ" (not just "J"). ___
- 9.2: Press N after Ctrl+J. Verify speech says "Legacy Noise Reduction on/off" (not just "Noise Reduction"). ___

### RX filter speak
- 9.3: Press Ctrl+J then Shift+F. Verify speech reads RX filter (low, high, width). ___

### Frequency speak hotkey (FINDING-59)
- 9.4: Press the frequency speak hotkey (Alt+F or whatever was assigned). Verify current frequency is spoken. ___
- 9.5: Change frequency. Press hotkey again. Verify updated frequency. ___

### Repeat last message (FINDING-7, 62)
- 9.6: Have something spoken (e.g., speak frequency). Press Ctrl+F4. Verify last message repeats. ___
- 9.7: Double-press Ctrl+F4. Verify clipboard contains the message and/or speech viewer dialog opens. ___

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
- 11.1: Launch app. Verify welcome dialog says "JJ Flexible Radio Access" (not "JJRadio"). ___

### 60m band jump (FINDING-55)
- 11.2: Press Shift+F3 (60m band jump). Verify frequency lands on Channel 1 (5.3320 MHz), NOT 5.400 MHz. ___

### 60m wording (FINDING-56, 57)
- 11.3: Navigate to 60m digital segment. Verify speech says "60 meter digital and CW segment" (not just "digital"). ___
- 11.4: Open Settings, check country combo. Verify it says "United States" (not "US"). ___

### Connect/Disconnect wording (FINDING-60, 61)
- 11.5: While connected, open Radio menu. Verify it says "Disconnect" (not "Connect to radio"). ___
- 11.6: Click Disconnect (or Connect while connected). Verify confirmation wording is natural: "You're already connected to a radio. Disconnect from this radio and connect to another radio?" ___

### Dialog modality (FINDING-64)
- 11.7: Open Settings dialog. Try Alt+Tab to main window. Verify main window is blocked (Settings stays on top). ___

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

## Deferred Findings (NOT tested — intentionally skipped)

- FINDING-25: Multi-radio abstraction — deferred to radio abstraction sprint
- FINDING-48: Earcon fade-out/envelope — deferred to backlog, not critical for 4.1.16

---

## Post-Test Actions

After all tests pass:
1. Version bump to 4.1.16 (both JJFlexRadio.vbproj AND AssemblyInfo.vb)
2. Update changelog (docs/CHANGELOG.md) — warm, user-facing tone
3. Clean build with build-installers.bat
4. Tag v4.1.16, push to origin, create GitHub release
5. Archive sprint plan to docs/planning/agile/archive/
