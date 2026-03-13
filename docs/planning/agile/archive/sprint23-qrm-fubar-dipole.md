# Sprint 23: QRM Fubar Dipole

**Theme:** Fix sprint. Address 63 of 65 findings from Sprint 21+22 guided testing.

**Sprint Goal:** Make every Sprint 21+22 feature actually work correctly with a screen reader. Fix hotkey conflicts, broken gating, inaccessible dialogs, stale data, and UI inconsistencies discovered during the 2026-03-10 testing sessions. Kill Classic menus. Unify hotkey dispatch. Standardize ScreenFields controls.

---

## Architecture: Serial Execution

This sprint runs serially (not parallel tracks) because the same 5-6 files get touched by nearly every phase. Parallel tracks would cause merge conflict storms.

**Most-touched files across all phases:**
- `JJFlexWpf/NativeMenuBar.cs` — Phases 1, 2, 5, 6, 10, 11, 12 (7 phases)
- `JJFlexWpf/MainWindow.xaml.cs` — Phases 1, 2, 6, 9, 10, 11, 13 (7 phases)
- `KeyCommands.vb` — Phases 1, 2, 3, 5, 6, 9 (6 phases)
- `Radios/FlexBase.cs` — Phases 5, 6, 7, 10, 13 (5 phases)
- `JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs` — Phases 4, 5, 10, 12 (4 phases)
- `JJFlexWpf/EarconPlayer.cs` — Phases 3, 10 (2 phases)

**Build rule:** Full solution build after every phase (`dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64`). Verify exe timestamp.

**Commit style:** `Sprint 23 Phase N: brief description`. Sub-phases: `Sprint 23 Phase N.M: sub-phase description`.

---

## Findings Coverage

**63 of 65 findings addressed. 2 deferred to backlog:**
- FINDING-25: Multi-radio abstraction — wait for second radio platform to test against
- FINDING-48: Earcon fade-out/envelope — add selectively later, not critical for 4.1.16

---

## Phase 1: Kill Classic Menus (FINDING-65)

**Goal:** One menu structure. Ctrl+Shift+M toggles tuning mode behavior only — does NOT rebuild menus.

**Why this must be first:** Every subsequent phase that touches menus benefits from having one code path instead of two. Reduces surface area for bugs across the entire sprint.

**Work:**
- Delete `BuildClassicMenuBar()` (~170 lines, approximately lines 758-927 in NativeMenuBar.cs)
- Rename `BuildModernMenuBar()` to `BuildMenuBar()`
- Update `RebuildMenu()` to always call the single builder
- Simplify `ApplyUIMode()` in MainWindow.xaml.cs — mode toggle only changes tuning behavior
- Update KeyCommands speech to say "Classic tuning mode" / "Modern tuning mode" (not "Classic mode" / "Modern mode")
- Verify no Classic-only menu items are lost (specifically: Logging submenu, CW Message management — these must exist in the unified menu)

**Files:** NativeMenuBar.cs, MainWindow.xaml.cs, KeyCommands.vb, globals.vb

**Risk:** Must audit both menu builders line-by-line to ensure the unified menu has every item from both. Diff Classic vs Modern menu items before deleting.

---

## Phase 2: Unify Hotkey Dispatch (FINDING-34, 15, 33, 20, 14, 3)

**Goal:** All hotkeys go through KeyCommands.vb scope system. No more dual dispatch with NativeMenuBar hardcoded shortcuts.

### Sub-phase 2.1: Move Expander Hotkeys to KeyCommands.vb
- Add new CommandValues: ToggleDspExpander, ToggleAudioExpander, ToggleReceiverExpander, ToggleTransmissionExpander, ToggleAntennaExpander
- Register in KeyCommands with appropriate scope (Radio)
- Remove hardcoded `\tCtrl+Shift+N` etc. shortcut text from NativeMenuBar menu items
- Wire menu items to call KeyCommands dispatch instead of direct `ToggleCategory()`
- Keep Ctrl+Shift+N (DSP), Ctrl+Shift+U (Audio), Ctrl+Shift+R (Receiver), Ctrl+Shift+A (Antenna)

### Sub-phase 2.2: Assign Transmission Expander New Hotkey
- Ctrl+Shift+T stays with Tune Carrier (Sprint 22 assignment)
- Assign Ctrl+Shift+X for Transmission expander ("TX" mnemonic)
- Register in KeyCommands.vb

### Sub-phase 2.3: Fix Ctrl+M Conflict (FINDING-3, 4, 5)
- Ctrl+M stays with Flex memories (Radio scope — existing FlexLib binding)
- Remove global ToggleMeters from Ctrl+M
- Assign new key for meter tones toggle (check availability first — suggest Ctrl+Shift+M since it's freed from mode toggle in Phase 1)
- Add menu entry for meter tones in Audio section of unified menu
- Make MetersPanel reachable by Tab (set IsTabStop=true, add to tab order)

### Sub-phase 2.4: Fix Ctrl+Shift+F Conflict (FINDING-14)
- Remove Ctrl+Shift+F binding for SpeakTXFilter
- TX filter speak stays as JJ F only (leader key)
- Ctrl+Shift+F remains frequency readout

### Sub-phase 2.5: Full Hotkey Audit
- Dump ALL bindings from both KeyCommands.vb and NativeMenuBar.cs
- Cross-reference with Flex radio built-in keys
- Resolve all remaining conflicts
- Document final hotkey map

**Files:** KeyCommands.vb, NativeMenuBar.cs, MainWindow.xaml.cs, MetersPanel.xaml.cs

---

## Phase 3: Earcon Redesign (FINDING-10, 12, 13)

**Goal:** New double-beep patterns for on/off toggles. Earcons on ScreenFields checkbox toggles. Dialog spawn/dismiss earcons.

### Sub-phase 3.1: Double-Beep Toggle Patterns
- Replace single rising tone (FeatureOnTone) with double ascending beep for ON
- Replace single falling tone (FeatureOffTone) with double descending beep for OFF
- Update EarconPlayer with new methods: `PlayToggleOn()`, `PlayToggleOff()`
- Update all callers (KeyCommands JJ key toggles, menu toggles, etc.)

### Sub-phase 3.2: ScreenFields Checkbox Earcons (FINDING-12)
- When a RadioCheckBox is toggled in ScreenFields, play the same on/off earcon
- Wire into RadioCheckBox.xaml.cs checked/unchecked events

### Sub-phase 3.3: Dialog Spawn/Dismiss Earcons (FINDING-13)
- Add ascending ding pattern for dialog open
- Add descending ding pattern for dialog close
- Wire into JJFlexDialog base class (OnLoaded / OnClosing)
- Same pattern for listbox/combo popups if feasible

**Files:** EarconPlayer.cs, JJFlexDialog.cs (or equivalent base), RadioCheckBox.xaml.cs, KeyCommands.vb, NativeMenuBar.cs

---

## Phase 4: ScreenFields Controls Standardization (FINDING-24, 21, 22, 23, 17)

**Goal:** Consistent behavior across all value and cycle controls. One speech event per action. Universal interaction hints.

### Sub-phase 4.1: Fix Double/Triple-Speak (FINDING-22)
- Arrow (value change): speaks value twice — control's value-changed event AND custom speech handler both fire. Kill one.
- Tab (focus change): speaks three times — focus event, accessible name, and value echo all stack. Kill duplicates.
- One source of truth for speech on each event type.

### Sub-phase 4.2: "Arrows to Change" Hint (FINDING-21)
- When tabbing to a cycle control (like Processor Mode), speak: "[value], arrows to change"
- Only on focus entry, not on every arrow press
- Implement in CycleFieldControl base, not per-control

### Sub-phase 4.3: Shift+Up/Down Step-of-5 (FINDING-23)
- Arrows = 1, Shift+Up/Down = 5, PageUp/Down = 10
- Implement in ValueFieldControl.xaml.cs PreviewKeyDown handler
- Apply universally to all numeric value controls

### Sub-phase 4.4: Manual Numeric Entry (FINDING-17)
- On a ValueFieldControl, pressing digit keys enters number mode
- Display accumulates digits
- Enter commits the typed value (clamped to valid range)
- Escape cancels and restores previous value
- Note: some of this infrastructure may already exist (`_numberEntryMode`, `_numberBuffer` fields) — check and extend

**Files:** ValueFieldControl.xaml.cs, CycleFieldControl.xaml.cs, ScreenFieldsPanel.xaml.cs

---

## Phase 5: Comprehensive Feature Gating (FINDING-11, 42, 43, 44 + audit)

**Goal:** If the radio doesn't have a feature, the UI element doesn't exist. Hide, don't gray out. Comprehensive audit of all hardware/license-gated features.

**Philosophy:** If it's not in the radio, don't show the menu item, don't show the ScreenFields control, don't allow the JJ key binding to fire. The user should never encounter a feature their radio can't use.

### Sub-phase 5.1: Fix Neural NR Gating (FINDING-11, BUG-016)
- Root cause: `NoiseReductionLicenseReported` returns False on FLEX-6300 because the license object is null (never reports). The gating check short-circuits on False and allows the toggle.
- Fix: Invert logic to "if feature NOT positively confirmed as licensed, block it"
- Apply to: JJ key R (Neural NR), JJ key S (Spectral NR), ScreenFields checkboxes, menu items
- Speech: "Neural Noise Reduction is not available on this radio"

### Sub-phase 5.2: ATU Gating (FINDING-42, 43, 44)
- Check `Radio.HasATU` (or equivalent FlexLib property — research actual API)
- If no ATU hardware: hide ATU On/Off menu item, hide ATU Tune menu item, hide ATU controls in Antenna ScreenFields expander
- FLEX-6300 has optional ATU — must detect actual presence, not just model
- Don's 6300 has no ATU — this is our test case

### Sub-phase 5.3: Diversity Gating
- `Radio.DiversityIsAllowed` — only 2-SCU radios (6600, 6700, 8600, AU-520)
- Hide diversity menu items and ScreenFields controls on single-SCU radios

### Sub-phase 5.4: Max Slices Gating
- Use `Radio.MaxSlices` for slice creation limits (not hardcoded counts)
- Connects to Phase 6 slice management fixes

### Sub-phase 5.5: Full Gating Audit
- Walk every menu item, every ScreenFields control, every JJ key binding
- For each: is there a hardware or license prerequisite?
- If yes: is it checked? Does the check work?
- Document any gaps found, fix them in this phase

**Files:** FlexBase.cs, KeyCommands.vb, NativeMenuBar.cs, ScreenFieldsPanel.xaml.cs, MainWindow.xaml.cs

---

## Phase 6: Slice Management + VFO Selector (FINDING-49, 50, 51, 52, 53, 54)

**Goal:** Fix broken slice create/release, fix stale data, add VFO slice selector, fix A/B keys.

### Sub-phase 6.1: Fix Create Slice (FINDING-49)
- With 1 active slice on a radio that supports 2, says "Maximum slices reached"
- Root cause: likely reading `MyNumSlices` vs `TotalNumSlices` — wrong count property
- Fix: use correct FlexLib property for available vs used slices

### Sub-phase 6.2: Fix Stale Slice Data (FINDING-50)
- Slice menu shows "Slice 2/0" — not reflecting actual state
- Subscribe to slice add/remove events from FlexLib
- Rebuild slice menu data on `Radio.SliceAdded` / `Radio.SliceRemoved` events
- Or: rebuild on `WM_INITMENUPOPUP` (query fresh state when menu opens)

### Sub-phase 6.3: Unify Slice Messaging (FINDING-51)
- "Maximum slices reached" vs "All slices in use" — pick one
- Standardize to "Maximum slices reached" (clearer)

### Sub-phase 6.4: A/B Keys Direct Select (FINDING-54)
- Pressing A should ALWAYS go to slice A
- Pressing B should ALWAYS go to slice B
- Currently they just swap to the next slice regardless of which key
- Fix in KeyCommands.vb — direct slice selection by index

### Sub-phase 6.5: Slice Creation Speech (FINDING-53)
- After creating a new slice, speak: "Slice B created on [freq], [mode]. Slice A still selected."
- Keep current slice selected (don't auto-switch to new slice)

### Sub-phase 6.6: VFO Slice Selector (FINDING-52)
- NEW control: focusable field at left of VFO/frequency display area
- Shows current slice and status
- Up/Down arrows to switch slices
- Tab once to reach slice operations (volume, pan, mute)
- F1 shows context-sensitive operations list
- Must NOT interfere with frequency tuning arrow keys (different focus context)

**Files:** NativeMenuBar.cs, MainWindow.xaml.cs, MainWindow.xaml, KeyCommands.vb, FlexBase.cs, RadioStatusBuilder.cs, NEW: JJFlexWpf/Controls/SliceSelector.xaml + SliceSelector.xaml.cs

---

## Phase 7: Audio Workshop Accessibility (FINDING-36, 37, 38, 40)

**Goal:** Make Audio Workshop actually functional — tab, close, title, mute option.

### Sub-phase 7.1: Fix Escape to Close (FINDING-40)
- Escape does nothing. Only Alt+F4 closes.
- If using JJFlexDialog base: wire Escape to close
- If standalone Window: add KeyDown handler for Escape, or set `IsCancel=true` on a close button

### Sub-phase 7.2: Fix Title Announcement (FINDING-36)
- Currently says "audio workshop active, load audio preset" instead of window title
- Set `Title="Audio Workshop"` and `AutomationProperties.Name="Audio Workshop"`
- Ensure screen reader announces title on dialog open

### Sub-phase 7.3: Fix Tab Navigation (FINDING-37)
- Focus lands on "Load Audio Preset" button, Tab does nothing
- Set `IsTabStop=true` on all interactive controls
- Set `TabIndex` on programmatically-created controls
- Set `KeyboardNavigation.TabNavigation="Cycle"` on the container
- Verify all three tabs (TX Audio, Live Meters, Earcon Explorer) are reachable via Ctrl+Tab

### Sub-phase 7.4: RX Audio Mute Option (FINDING-38)
- Add checkbox: "Mute RX audio while workshop is open"
- Default: checked (mute)
- Edge case: user monitoring TX via second antenna needs RX live — hence option not forced

### Sub-phase 7.5: Antenna Naming (FINDING-39)
- Settings UI for friendly names per antenna port per radio
- E.g., "ANT1" -> "20m Beam", "ANT2" -> "40m Dipole"
- Display friendly names everywhere antennas are shown (menus, ScreenFields, speech)
- Store in app settings keyed by radio serial number

**Files:** AudioWorkshopDialog.xaml.cs, AudioWorkshopDialog.xaml, JJFlexDialog.cs, SettingsDialog.xaml.cs, SettingsDialog.xaml, FlexBase.cs

---

## Phase 8: About Dialog Accessibility (FINDING-28, 29, 30)

**Goal:** Fully navigable About dialog with correct version numbers.

### Sub-phase 8.1: Fix Tab Navigation (FINDING-28)
- Only reads "JJ Flexible Radio" — can't navigate tabs or content
- Replace TextBlock content with focusable read-only TextBox or focusable ScrollViewer
- Ensure all three tabs (About, System Info, Credits) reachable via Ctrl+Tab or arrow keys
- Each tab's content must be screen-reader navigable

### Sub-phase 8.2: Fix FlexLib Version (FINDING-29)
- Currently shows 0.0.0.0
- Use `FileVersionInfo.GetVersionInfo()` on the FlexLib DLL instead of assembly attribute
- Should show 4.0.1

### Sub-phase 8.3: Bump Internal Library Versions (FINDING-30)
- JJFlexWpf: minor bump (significant changes in Sprint 21-23)
- Other libraries: bump as appropriate based on changes made this sprint
- Follow three-tier semver: patch for small fixes, minor for moderate, major for architectural

**Files:** AboutDialog.xaml, AboutDialog.xaml.cs, JJFlexWpf.csproj, other .csproj files as needed

---

## Phase 9: JJ Key Fixes + Repeat Last Message (FINDING-7, 8, 9, 16, 59, 62)

**Goal:** Fix JJ key speech issues, add frequency speak hotkey, add repeat-last-message feature.

### Sub-phase 9.1: Fix "J" to "JJ" (FINDING-8)
- KeyCommands.vb around line 1251 — entry speech says "J" not "JJ"
- Change to "JJ" since we call it the JJ key

### Sub-phase 9.2: Fix NR Speech (FINDING-9)
- JJ N says "Noise Reduction on" — too vague
- Three types: legacy NR (N), Neural NR (R), Spectral NR (S)
- Change to: "Legacy Noise Reduction on/off" for N
- Neural and Spectral already have distinct names if gating is fixed (Phase 5)

### Sub-phase 9.3: JJ Shift+F for RX Filter (FINDING-16)
- JJ F speaks TX filter — add JJ Shift+F to speak RX filter width
- Speak: "RX filter low [value], high [value], width [computed]"
- One new case in DoLeaderCommand

### Sub-phase 9.4: Frequency Speak Hotkey (FINDING-59)
- `SpeakFrequency()` exists but isn't bound to any key
- Needs a dedicated single-press hotkey (not just JJ key) — people will use this constantly
- Suggest Alt+F — check for conflicts first
- Register in KeyCommands.vb

### Sub-phase 9.5: Repeat Last Message + Speech Viewer (FINDING-7, 62)
- Store `LastMessage` in ScreenReaderOutput (or equivalent speech output class)
- Single press of hotkey: re-speak last message
- Double press: copy to clipboard AND open read-only dialog (MessageViewerDialog)
- This also solves FINDING-62 (startup speech drowned by radio audio) — user can replay it
- NEW: MessageViewerDialog.xaml / MessageViewerDialog.xaml.cs

**Files:** KeyCommands.vb, ScreenReaderOutput.cs (or equivalent), MainWindow.xaml.cs, NEW: JJFlexWpf/MessageViewerDialog.xaml + MessageViewerDialog.xaml.cs

---

## Phase 10: ATU and Antenna Fixes (FINDING-41, 45, 46, 47)

**Goal:** Fix ATU workflow, timeout, tone speed, antenna speech. (ATU visibility gating handled in Phase 5.)

### Sub-phase 10.1: ATU Workflow Fix (FINDING-45)
- Three separate actions, clearly distinguished:
  1. **ATU Enable/Disable** — quiet toggle, just puts tuner inline. No progress tones.
  2. **Tune Carrier** — transmit carrier for manual tuning (any radio, no ATU required)
  3. **ATU Tune** — automatic tune cycle (only if ATU present and enabled)
- Fix: ATU On/Off toggle must NOT start a tune cycle or play progress tones

### Sub-phase 10.2: ATU Timeout (FINDING-46)
- Currently beeps forever with no timeout
- Add 15-second timeout
- On timeout: stop progress tones, speak "ATU tune timed out"
- Escape should also cancel tune in progress

### Sub-phase 10.3: ATU Progress Tone Speed (FINDING-47)
- Halve tone duration
- Halve delay between beeps
- More responsive feel during tune cycle

### Sub-phase 10.4: Antenna Menu Speech (FINDING-41)
- When selecting antenna from menu, NVDA re-speaks the menu/dialog title before antenna change speech
- Fix: increase SpeakAfterMenuClose delay, or suppress title re-announcement
- Investigate whether posting speech via async delay avoids the title echo

**Files:** NativeMenuBar.cs, ScreenFieldsPanel.xaml.cs, MainWindow.xaml.cs, EarconPlayer.cs, ContinuousToneSampleProvider.cs, FlexBase.cs

---

## Phase 11: 60m, Welcome Text, and Minor Fixes (FINDING-6, 55, 56, 57, 58, 60, 61, 64)

**Goal:** 60m polish, welcome text fix, connect/disconnect wording, settings modality.

### Sub-phase 11.1: Welcome Text (FINDING-6)
- Welcome.Designer.vb still says "Welcome to JJRadio"
- Change to "Welcome to JJ Flexible Radio Access"

### Sub-phase 11.2: 60m Band Jump to Channel 1 (FINDING-55)
- Shift+F3 currently lands at 5.400 MHz (band edge)
- Should land on Channel 1 (5.3320 MHz) for channelized bands
- After first visit, remember last channel used

### Sub-phase 11.3: 60m Wording Fixes (FINDING-56, 57)
- "60 meter digital segment" -> "60 meter digital and CW segment"
- Settings combo: "US" -> "United States"

### Sub-phase 11.4: 60m Boundary Testing (FINDING-58)
- Test CW/digital segment boundary enforcement
- Test at least one other country's 60m allocation
- Fix any edge cases found

### Sub-phase 11.5: Connect/Disconnect Wording (FINDING-60, 61)
- Radio menu: show "Disconnect" when connected (currently always says "Connect")
- Confirmation dialog: "You're already connected to a radio. Disconnect from this radio and connect to another radio?" (replace current wording)

### Sub-phase 11.6: Settings Dialog Modality (FINDING-64)
- Can Alt+Tab to main window while Settings is open — should block
- Set Owner via WindowInteropHelper (MainWindow is a WinForms-hosted WPF UserControl, not a Window)
- ShowDialog() should prevent interaction with main window

### Sub-phase 11.7: Debounce Audibility
- Investigate whether default debounce delay is audible enough
- Increase if needed — user confirmed toggle and settings work, but practical effect may be too subtle

**Files:** Welcome.Designer.vb, MainWindow.xaml.cs, NativeMenuBar.cs, SixtyMeterChannels.cs (or equivalent), SettingsDialog.xaml, LicenseConfig.cs (or equivalent)

---

## Phase 12: Filter Enhancements (FINDING-18, 19, 35)

**Goal:** RX filter width display, speak width after edge adjustment, filter calculator dialog.

### Sub-phase 12.1: Computed RX Filter Width (FINDING-35)
- Add read-only computed RX filter width field in DSP ScreenFields expander
- Shows difference between RX filter high and low
- Updates live as filter edges change

### Sub-phase 12.2: Speak Width After Edge Adjustment (FINDING-18)
- When nudging TX or RX filter edges with bracket keys, speak:
  - "Low [value], width [computed]" or "High [value], width [computed]"
- Gives immediate feedback on resulting bandwidth

### Sub-phase 12.3: Filter Calculator Dialog (FINDING-19)
- NEW dialog: enter any two of low/high/width, third auto-computes
- E.g., "I want a 2.0 kHz filter starting at 500" — enter low=500, width=2000, computes high=2500
- Apply button sets the filter on the current slice
- Accessible: all fields labeled, tab order logical, Escape closes

Note: FINDING-16 (JJ Shift+F for RX filter) handled in Phase 9.3.

**Files:** ScreenFieldsPanel.xaml.cs, FreqOutHandlers.cs (or equivalent filter handler), NativeMenuBar.cs, NEW: JJFlexWpf/FilterCalculatorDialog.xaml + FilterCalculatorDialog.xaml.cs

---

## Phase 13: MultiFlex Awareness (FINDING-63)

**Goal:** Show slice ownership in startup speech, status readout, and slice navigation.

**Work:**
- Startup speech: iterate all slices, annotate ownership
  - "Slice A, yours. Slice B in use by Don."
- Ctrl+Shift+S status readout: include ownership per slice
- VFO slice selector (Phase 6.6): show ownership when arrowing through slices
- Depends on FlexLib exposing ClientHandle/Owner on Slice object — research actual API

**Files:** RadioStatusBuilder.cs, MainWindow.xaml.cs, FlexBase.cs

---

## Phase 14: Command Finder + CHM Help (FINDING-31)

**Goal:** Fix category filtering in Command Finder. Compile CHM help file.

### Sub-phase 14.1: Fix Command Finder Category Filtering (FINDING-31)
- Category combo is present and navigable but selecting a category doesn't filter
- Root cause likely: event wiring — combo `SelectionChanged` may not be connected, or keyboard nav doesn't fire the expected event
- Fix: wire filtering logic to the correct event

### Sub-phase 14.2: Compile CHM Help
- CHM was never compiled — F1 silently fails because the .chm file doesn't exist
- Requires HTML Help Workshop (hhc.exe) — verify it's installed or install it
- Run build-help.bat or equivalent to compile Markdown sources to CHM
- Add .chm to build output (copy to output directory)

### Sub-phase 14.3: Verify Help Key Wiring
- F1: context-sensitive help (opens CHM to relevant topic)
- Ctrl+F1: CHM page for current context
- Shift+F1: table of contents
- Verify all three paths work

**Files:** CommandFinderDialog.xaml.cs, CommandFinderDialog.xaml, docs/help/build-help.bat (or equivalent), JJFlexRadio.vbproj, HelpLauncher.cs (or equivalent)

---

## Phase 15: RigSelector Fixes (FINDING-1, 2)

**Goal:** Empty listbox tabbable and announces item count. Selected item announced on focus.

### Sub-phase 15.1: Empty Listbox Accessibility (FINDING-1)
- When no radios are discovered, listbox is skipped in tab order
- Fix: keep ListBox in tab order when empty
- Announce: "Radio list, 0 items"

### Sub-phase 15.2: Selected Item Announcement (FINDING-2)
- Tab to listbox says "listbox" or "radios listbox" without reading selected item
- Fix: set AutomationProperties.Name to include selected item and count
- Should say: "Radios listbox, 6300 inshack, 1 of 1"

**Files:** RigSelectorDialog.xaml, RigSelectorDialog.xaml.cs

---

## Phase 16: Library Versions + Build Verification (FINDING-30)

**Goal:** Bump library versions, clean build, verify everything.

**Work:**
- Bump JJFlexWpf version (minor bump — significant changes this sprint)
- Bump Radios library version if touched
- Bump other libraries as appropriate based on Sprint 23 changes
- Full clean build: `build-installers.bat` (x64 + x86)
- Verify exe timestamps match build time
- Verify exe version numbers
- Run both installers to confirm they work
- Note: main app version bump to 4.1.16 happens AFTER testing, not in this phase

**Files:** JJFlexWpf.csproj, Radios.csproj, other .csproj files as needed

---

## New Files Created This Sprint

- `JJFlexWpf/Controls/SliceSelector.xaml` + `SliceSelector.xaml.cs` — VFO slice selector (Phase 6)
- `JJFlexWpf/MessageViewerDialog.xaml` + `MessageViewerDialog.xaml.cs` — Speech viewer (Phase 9)
- `JJFlexWpf/FilterCalculatorDialog.xaml` + `FilterCalculatorDialog.xaml.cs` — Filter calculator (Phase 12)

---

## Post-Sprint

- Run full test matrix against all 63 findings
- Version bump to 4.1.16 after testing passes
- Update changelog (user-facing, warm tone per CLAUDE.md conventions)
- Update Agent.md with sprint completion
- Sprint 24 is pledged as VB-to-C# migration sprint

---

*Sprint 23: QRM Fubar Dipole — 16 serial phases, 63 findings, one menu to rule them all.*
