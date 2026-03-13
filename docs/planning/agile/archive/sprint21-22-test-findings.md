# Sprint 21 + 22 Detailed Test Findings

**Tester:** Noel Romey (K5NER)
**Date:** 2026-03-10 (two sessions)
**Build:** Debug x64, clean build from main (build fixes applied during testing)
**Radio:** FLEX-6300 "6300 inshack" via SmartLink (Don's radio)
**Screen Reader:** NVDA

---

## Build Issues Found Before Testing

Six build errors existed on main — Sprint 22 was never fully built against the VB project:

- 4 methods in MainWindow.xaml.cs marked `internal` instead of `public` (CurrentAudioConfig, ToggleTuneCarrier, StartATUTuneCycle, ToggleMetersPanel). VB project is a separate assembly and can't see `internal` members.
- `FunctionGroups.tuning` enum value referenced in KeyCommands.vb but never added to the enum.
- Root cause: Sprint 22 phases only built the C# project (JJFlexWpf), never the full solution. Cross-assembly errors went undetected.
- Lesson: Always build full solution after every phase, even in serial sprints.

---

## Hotkey Conflicts (Critical — Blocks Multiple Tests)

### FINDING-3: Ctrl+M conflicts with Flex memories
Ctrl+M is bound to ToggleMeters (Global scope) AND ShowMemory/Flex memories (Radio scope). Radio scope wins per the scope priority system (specific beats Global), so meter tones toggle never fires. Meter tones have NO working hotkey and NO menu entry (FINDING-5). MetersPanel is also not reachable by tab (FINDING-4). Meter tones are completely unreachable.

### FINDING-14: Ctrl+Shift+F conflicts with frequency readout
SpeakTXFilter is bound to Ctrl+Shift+F but that key already does frequency readout. Decision: remove global hotkey, keep TX filter speak on JJ F only.

### FINDING-20: Ctrl+Shift+T conflict
Tune Carrier toggle took over Ctrl+Shift+T which was the Transmission ScreenFields expander hotkey. Transmission expander now has no hotkey. The expander's menu entry already had the shortcut text removed but was never reassigned.

### FINDING-33: Full hotkey deconfliction needed
Additional potential conflicts identified in audit:
- Ctrl+Shift+N: DSP expander (NativeMenuBar) vs LogCharacteristicsDialog (Logging scope, KeyCommands). Different dispatch systems.
- Ctrl+Shift+U: Audio expander (NativeMenuBar) vs SavedScan (Radio scope, KeyCommands). Different dispatch systems.
- Ctrl+Shift+S: SpeakStatus (Global) vs ReadSMeter (Radio). Radio scope wins, so SpeakStatus only fires in Logging mode.

### FINDING-34: Architectural fix — unify hotkey dispatch
ScreenFields expander hotkeys are wired through NativeMenuBar. Everything else goes through KeyCommands.vb scope system. These two dispatch paths don't know about each other. All hotkeys should go through KeyCommands.vb so the scope system catches conflicts automatically.

### FINDING-15: Process — hotkey audit before new assignments
Before assigning any new shortcuts, dump all current bindings, check against Flex radio keys, and resolve all conflicts in one pass.

---

## JJ Key (Ctrl+J Leader Key) Findings

### FINDING-8: Entry speech says "J" not "JJ"
Should say "JJ" since we call it the JJ key.

### FINDING-9: Noise Reduction speech too vague
JJ N says "Noise Reduction on" without specifying which type (legacy NR). There are three types: legacy NR (N), Neural NR (R), Spectral NR (S). Speech should distinguish.

### FINDING-10: Earcon redesign for toggles
Current: single rising tone for ON, single falling tone for OFF. Proposed: double ascending beep for ON, double descending beep for OFF. More distinct and self-documenting. Also need different patterns for help entry/exit, dialog spawn/dismiss.

### FINDING-11: Feature gating broken (BUG-016)
JJ R toggles Neural NR on Don's 6300 which doesn't support it. The gating code exists but `NoiseReductionLicenseReported` is probably False on the 6300, so the check is bypassed. Same issue in ScreenFields checkboxes and likely menus. Was working pre-WPF, broken in migration.

### Test Results
- 3.1 (enter JJ key): PASS — tone plays, speech says "J" (should say "JJ")
- 3.2 (N = NR toggle): PASS — toggles legacy NR, speech works
- 3.5 (R = Neural NR): FAIL — toggles on unsupported radio
- 3.12 (F = speak TX filter): PASS
- 3.13 (D = debounce toggle): PASS
- 3.15 (H = help): PASS
- 3.16 (Escape = cancel): PASS
- 3.17 (unbound key = error): PASS

---

## Audio Workshop Findings

### FINDING-36: Doesn't announce dialog title
Says "audio workshop active, load audio preset" instead of clearly announcing "Audio Workshop" as the window title.

### FINDING-37: Can't tab past first button
Focus lands on "Load Audio Preset" button. Tab does nothing. Ctrl+Tab does nothing. Shift+Tab does nothing. The entire dialog is unreachable except that one button. Essentially non-functional.

### FINDING-38: Audio muting consideration
Should Audio Workshop mute RX audio while open (like CQ test dialog)? Edge case: if using slice B on a different antenna to monitor TX, you'd want that live. Make it an option, not automatic.

### FINDING-39: TX monitor via second antenna
Audio Workshop could offer a "Monitor TX via second antenna" option that sets up a second slice for self-monitoring. Requires antenna naming (let users label ports with friendly names like "20m Beam" instead of "ANT1").

### FINDING-40: Doesn't close with Escape
Escape does nothing. Only Alt+F4 closes the dialog. Basic accessibility requirement broken.

---

## ScreenFields Findings

### FINDING-12: No earcon on checkbox toggle
DSP checkboxes in ScreenFields toggle silently — no rising/falling tone. Only JJ key toggles play earcons. Should be consistent regardless of input method.

### FINDING-21: Cycle controls don't indicate interactivity
When you tab to a cycle control like Processor Mode, there's no cue that arrows change the value. Should say "Speech Processor DX, arrows to change" at normal verbosity.

### FINDING-22: Double/triple-speak on value change
- Arrow (value change): speaks value twice — control's value-changed event AND custom speech handler both fire.
- Tab (focus change): speaks three times — focus event, accessible name, and value echo all stack.
- Need one source of truth for speech.

### FINDING-23: Add step-of-5 increments
Arrows = 1, PageUp/Down = 10, but nothing for 5. Add Shift+Up/Down = 5.

### FINDING-24: Standardize value control behavior
All numeric value controls should share: arrows = 1, Shift+arrows = 5, PageUp/Down = 10, speak value on change, "arrows to change" hint. Define once in the base control.

### FINDING-25: Multi-radio abstraction
ScreenFields controls should be a platform feature. Each radio provides its own field map (what controls exist, ranges, steps) but control behavior, speech, and interaction are universal.

---

## Filter Findings

### FINDING-16: JJ Shift+F for RX filter width
JJ F speaks TX filter. Add JJ Shift+F to speak RX filter width. Consistent pair.

### FINDING-17: Manual numeric entry for filter values
ScreenFields controls use Up/Down in steps of 50. Need a way to type an exact value.

### FINDING-18: Speak width after adjusting edge
When nudging TX or RX filter edges, speak the new edge value AND the resulting bandwidth.

### FINDING-19: Filter calculator
Enter any two of low/high/width and auto-compute the third. "I want a 2.0 kHz filter starting at 500" — enter 500 and 2.0, it computes high = 2500.

### FINDING-35: Computed RX filter width in DSP ScreenFields
Show computed RX filter width alongside low/high values in the DSP expander.

---

## About Dialog Findings

### FINDING-28: Mostly inaccessible
Only reads "JJ Flexible Radio" — can't navigate the three tabs (About, System Info, Credits) or read any content. Copy to Clipboard works and captures everything, but the dialog itself is not screen-reader navigable.

### FINDING-29: FlexLib version shows 0.0.0.0
Should show actual FlexLib version (4.0.1). Assembly version attribute not set correctly on FlexLib DLL.

### FINDING-30: Internal library versions not maintained
JJFlexWpf and other sub-projects need semver bumps as they're touched. Patch (1.0.1) for small fixes, minor (1.1.0) for moderate changes, major (2.0.0) for architectural changes.

---

## RigSelector Findings

### FINDING-1: Empty listbox not reachable by tab
When no radios are discovered, the listbox is skipped in tab order. Should be tabbable and announce "Radio list, 0 items."

### FINDING-2: Listbox doesn't announce selected item on focus
Tab to listbox says "listbox" or "radios listbox" without reading the selected item. Should say "Radios listbox, 6300 inshack, 1 of 1."

---

## Command Finder Findings

### FINDING-31: Category filtering broken
Category combo is present and navigable, but selecting a category (e.g. "Audio") doesn't filter the command list. The filtering logic isn't connected.

---

## Antenna Switching Findings

### FINDING-41: Menu title re-speaks on antenna switch
When selecting an antenna from the menu, NVDA speaks the menu/dialog title before the antenna change speech. Might need speech delay or suppress the title re-announcement.

### FINDING-42: ATU on/off has no checkbox and not grayed without ATU
Should not appear at all if radio has no ATU hardware.

### FINDING-43: ATU menu items should be hidden when no ATU
Same as FINDING-42 but explicit — gray out or remove ATU items based on radio capability.

### FINDING-44: Antenna expander should hide ATU controls if no ATU
Same check needed in ScreenFields Antenna expander.

### FINDING-45: ATU workflow confusion
ATU on/off toggle appears to start a tune cycle (progress tones). Should be three separate actions:
1. ATU enable/disable — quiet toggle, just puts tuner inline
2. Tune Carrier — transmit carrier for manual tuning (any radio)
3. ATU Tune — automatic tune cycle (only if ATU present)

### FINDING-46: ATU progress tone beeps indefinitely
No timeout — beeps forever because no ATU hardware responds. Needs 10-15 second timeout with "ATU tune timed out" speech.

### FINDING-47: ATU progress tone timing too slow
Halve tone duration and delay between beeps.

---

## Earcon System Findings

### FINDING-13: Universal dialog spawn/dismiss earcons
Double beep pattern to signal when a dialog or listbox opens and when it closes. Consistent audio cue across the app.

### FINDING-48: Add fade-out/envelope support
EarconPlayer should support attack/decay envelopes on tones. Short fade-outs on dings and confirmation tones for a polished sound.

---

## Slice Management Findings

### FINDING-49: Create Slice button broken
With 1 active slice on a radio that supports 2, says "Maximum slices reached." Not reading current state from radio.

### FINDING-50: Slice menu shows stale data
"Slice 2/0" not reflecting actual state. Not subscribing to slice change events from FlexLib.

### FINDING-51: Inconsistent messaging
Create Slice says "Maximum slices reached" but the existing hotkey says "All slices in use." Pick one.

### FINDING-52: Slice selector in VFO area
First field at left of VFO: current slice and status. Arrow up/down to switch slices. Tab once to slice operations (volume, pan, mute). F1 shows context-sensitive operations list.

### FINDING-53: Slice creation behavior
Keep current slice selected after creating. Speak "Slice B created on [freq], [mode]. Slice A still selected."

### FINDING-54: A/B keys toggle instead of direct select
Pressing A should always go to slice A, pressing B always to slice B. Currently they just swap to the next slice regardless of which key is pressed.

---

## 60m Channelization Findings

### FINDING-55: Band jump should land on Channel 1
Shift+F3 lands at 5.400 MHz (band edge). Should go to Channel 1 (5.3320 MHz) by default for channelized bands. After first visit, remember last channel.

### FINDING-56: Digital segment wording
Says "60 meter digital segment" — should say "60 meter digital/CW segment."

### FINDING-57: Country name
Settings combo says "US" — should say "United States."

### FINDING-58: Additional testing needed
- CW/digital segment boundary enforcement
- Other countries' 60m allocations
- Enforce rules behavior at segment edges

---

## Startup, Connect, and App-Level Findings

### FINDING-6: Welcome.Designer.vb stale
Still says "Welcome to JJRadio" — missed in the rename.

### FINDING-7: Long speech needs reviewable dialog
Double-press hotkey to copy last speech to clipboard AND open a dialog with read-only edit box for reading at leisure. Same UX pattern as JAWS/NVDA speech viewers.

### FINDING-59: No hotkey to speak current frequency
SpeakFrequency() exists but isn't bound to any key. Needs a dedicated single-press hotkey (not just JJ key) — people will use this constantly.

### FINDING-60: Radio menu says "Connect" while connected
Should say "Disconnect" when a radio is connected. Regression.

### FINDING-61: Connect confirmation dialog wording
Current: "confirm, you're already connected, disconnect and connect to a different radio?"
Better: "You're already connected to a radio. Disconnect from this radio and connect to another radio?"

### FINDING-62: Startup speech drowned by radio audio
Radio audio starts before startup speech finishes. Either delay audio unmute until speech completes, or add "repeat last message" feature.

### FINDING-63: MultiFlex awareness
Startup speech and Ctrl+Shift+S should show slice ownership. "Slice A in use by Don." Arrow through slices should say who owns them.

### FINDING-64: Settings dialog not modal
Can Alt+Tab to main window while Settings is open. Should block main window interaction.

---

## Architectural Decisions

### FINDING-65: Kill Classic menus
Maintain one menu structure. Ctrl+Shift+M toggles between "Classic tuning mode" and "Modern tuning mode" — only changes tuning behavior, not menus. Reduces maintenance, reduces inconsistency bugs. Significant work but right long-term decision.

---

## Feature Requests (Backlog)

- Antenna naming/labeling per radio (FINDING-39)
- TX monitor via second antenna in Audio Workshop (FINDING-38)
- Filter calculator — enter two of low/high/width (FINDING-19)
- Slice selector in VFO area with context operations (FINDING-52)
- Repeat last spoken message / speech viewer (FINDING-7, FINDING-62)
- Speak frequency dedicated hotkey (FINDING-59)
- ScreenFields control abstraction for multi-radio (FINDING-25)
- Shift+Up/Down step-of-5 on value controls (FINDING-23)
- Earcon fade-out/envelope support (FINDING-48)
- Dialog spawn/dismiss earcons (FINDING-13)
- CW keyboard keyer (left/right shift for iambic, space for straight key) — backlog
- CAT serial keying for non-Flex radios — backlog

---

## Summary

### What's Working Well
- JJ key system (Ctrl+J leader key) — core interaction solid
- TX filter sculpting (bracket keys)
- 60m channelization (channel navigation, mode setting, menus)
- Antenna switching (menus, ScreenFields, checkmarks)
- ScreenFields Transmission expansion (all controls present and tabbable)
- Startup speech content (correct info, just drowned by audio)
- Shutdown fix (BUG-004 — clean exit)
- Tuning debounce (settings UI works)
- Connect confirmation (BUG-023 — dialog exists, wording needs cleanup)

### What Needs Major Work
- Audio Workshop (non-functional — can't tab, can't close)
- Meter tones (completely unreachable — no working hotkey, no menu, no tab)
- About dialog (inaccessible — can't navigate)
- Hotkey conflicts (6+ conflicts across two dispatch systems)
- Feature gating / BUG-016 (Neural NR toggles on unsupported radios)
- Slice management (stale data, create broken, A/B keys wrong)
- CHM help (file never compiled)

### Architecture Changes to Plan
- Unify hotkey dispatch into KeyCommands.vb
- Kill Classic menus (one menu structure)
- Standardize ScreenFields controls
- Add "repeat last message" feature
