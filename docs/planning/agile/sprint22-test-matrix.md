# Sprint 21 + 22 Test Matrix — v4.1.16 Pre-Release

**Tested by:** Noel Romey (K5NER)
**Date:** 2026-03-10
**Build:** Debug x64 (clean build from main)
**Screen Reader:** NVDA primary, JAWS secondary
**Radio:** _(fill in model and connection type)_

---

## Pre-Test Setup

1. Clean build: `dotnet clean JJFlexRadio.vbproj -c Debug -p:Platform=x64 && dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal`
2. Launch app, connect to radio
3. Start on a known band (e.g., 20m USB) before testing

---

## Sprint 21 Items (First time testing — Don's first look at these)

### 1. Meter Tones

- 1.1: Enable meter tones (Ctrl+M). Confirm NVDA says "Meter tones on."
- 1.2: Tune to a band with signals. Verify S-meter tone pitch rises with stronger signals.
- 1.3: Cycle preset (leader key M, or menu > Meter Tones > Cycle Preset). Confirm preset name is spoken: "TX Monitor", "Full Monitor", "RX Monitor."
- 1.4: Speak Meters (menu > Meter Tones > Speak Meters, or leader key R). Verify spoken summary like "S-meter S5."
- 1.5: Key up briefly (if safe). Verify TX meters become audible (ALC, mic, power, SWR tones).
- 1.6: Peak Watcher: if ALC goes high during TX, verify warning beep and "ALC high" or "ALC warning" speech.
- 1.7: Disable meter tones (Ctrl+M). Confirm silence and "Meter tones off."

### 2. Audio Workshop

- 2.1: Open Audio Workshop (Ctrl+Shift+W). Verify dialog opens and NVDA reads the title.
- 2.2: Tab through all 3 tabs. Confirm each tab is reachable and announced.
- 2.3: Modify a preset value (e.g., mic gain). Confirm control responds.
- 2.4: Save preset. Confirm save feedback.
- 2.5: Load a different preset. Confirm values change.
- 2.6: Close dialog with Escape. Confirm focus returns to main window.

### 3. Leader Key (Ctrl+J)

- 3.1: Press Ctrl+J. Verify leader tone plays (rising chirp).
- 3.2: Press N (toggle Neural NR). Verify "Neural NR on/off" speech.
- 3.3: Press B (toggle Noise Blanker). Verify "Noise Blanker on/off" speech.
- 3.4: Press W (open Audio Workshop). Verify dialog opens.
- 3.5: Press R (speak meters). Verify meter summary spoken.
- 3.6: Press S (speak status). Verify full status spoken.
- 3.7: Press A (toggle APF, CW mode only). In CW mode, verify "APF on/off."
- 3.8: Press P (cycle meter preset). Verify preset name spoken.
- 3.9: Press M (toggle meter tones). Verify on/off speech.
- 3.10: Press T (cycle tuning step). Verify step size spoken.
- 3.11: Press E (toggle meter tones — same as M). Verify behavior.
- 3.12: Press F (speak TX filter). Verify TX filter values spoken.
- 3.13: Press D (toggle tuning debounce). Verify "Tuning debounce on/off" with confirm tone.
- 3.14: Press L (log statistics). Verify confirm tone.
- 3.15: Press H or ? (leader key help). Verify help listing is spoken.
- 3.16: Press Escape after Ctrl+J. Verify leader mode cancelled (cancel tone).
- 3.17: Press an unbound key after Ctrl+J. Verify error tone and leader mode exits.

### 4. TX Filter Sculpting

- 4.1: In USB mode, press Ctrl+Shift+[ (TX filter low down). Verify value spoken.
- 4.2: Press Ctrl+Shift+] (TX filter low up). Verify value changes and is spoken.
- 4.3: Press Ctrl+Alt+[ (TX filter high down). Verify value spoken.
- 4.4: Press Ctrl+Alt+] (TX filter high up). Verify value spoken.
- 4.5: Press Ctrl+Shift+F. Verify full TX filter range is spoken ("TX filter 100 to 2800").

### 5. ScreenFields TX Expansion

- 5.1: Ctrl+Shift+4 to open Transmission category. Verify "Transmission expanded" speech.
- 5.2: Tab through controls. Verify TX Power, VOX, Tune Power, Mic Gain, Mic Boost, Mic Bias, Compander, Speech Processor, TX Filter Low, TX Filter High, TX Monitor are all present and announced.
- 5.3: Toggle Compander on. Verify Compander Level appears.
- 5.4: Toggle Speech Processor on. Verify Processor Mode appears.
- 5.5: Toggle TX Monitor on. Verify Monitor Level appears.
- 5.6: Adjust TX Power with Up/Down. Verify value changes.

### 6. CHM Help

- 6.1: Press F1. Verify compiled help file opens.
- 6.2: Close help and verify focus returns to app.

### 7. App Rename

- 7.1: Check window title bar. Should say "JJ Flexible Radio Access" (not JJFlexRadio).
- 7.2: Welcome speech on app launch. Should say "Welcome to JJ Flexible Radio Access."

### 8. Sprint 21 Bug Fixes

- 8.1: BUG-004 shutdown crash: Close app while connected to radio. Should exit cleanly without crash.
- 8.2: BUG-016 DSP gating: Toggle Neural NR on, then off. Verify speech says "on" then "off" correctly (not reversed).
- 8.3: BUG-023 connect confirmation: Connect to radio. Verify no duplicate confirmation dialog.

---

## Sprint 22 Items

### 9. Phase 1: RigSelector Auto-Connect

- 9.1: Open RigSelector (when no radio connected). Verify "Auto-Connect" button is present and announced.
- 9.2: With radios discovered, press Auto-Connect. Verify it connects to the first available radio.
- 9.3: With no radios discovered, press Auto-Connect. Verify spoken message about no radios found.
- 9.4: Empty radio list. Verify NVDA announces "No radios discovered" or similar.

### 10. Phase 2: About Dialog

- 10.1: Open About dialog (Help > About). Verify dialog opens with accessible title.
- 10.2: Tab through all 3 tabs (About, System Info, Credits). Verify each is announced.
- 10.3: About tab: verify version number, app name, Jim's tribute text.
- 10.4: System Info tab: verify radio model, FlexLib version, .NET version shown.
- 10.5: Copy to Clipboard button. Press it, verify confirmation speech, paste into Notepad to verify content.
- 10.6: Close with Escape. Verify focus returns.

### 11. Phase 3: Command Finder Categories

- 11.1: Open Command Finder (Ctrl+Shift+H or menu). Verify dialog opens.
- 11.2: Tab to category combo. Verify categories listed (All, Tuning, DSP, Audio, etc.).
- 11.3: Select a category (e.g., "Tuning"). Verify list filters to only tuning commands.
- 11.4: Select "All" category. Verify full list returns.
- 11.5: Type a search term while in a category. Verify search filters within that category.

### 12. Phase 4: Tuning Speech Debounce

- 12.1: Verify debounce is on by default. Tune rapidly with arrow keys. Verify only the final frequency is spoken (not every step).
- 12.2: Leader key D to toggle debounce off. Verify "Tuning debounce off" speech. Tune with arrows — every step should now speak immediately.
- 12.3: Leader key D to toggle back on. Verify "Tuning debounce on."
- 12.4: Open Settings > Tuning tab. Verify debounce checkbox and delay field are present.
- 12.5: Change delay to 500ms. Save. Tune rapidly. Verify longer pause before speaking.
- 12.6: Uncheck debounce in Settings. Save. Verify immediate speech on each step.

### 13. Phase 5: Tune Carrier + ATU Tune

- 13.1: Press Ctrl+Shift+T (Tune carrier toggle). Verify "Tune on" speech and rising chirp earcon. Verify radio enters tune mode.
- 13.2: Press Ctrl+Shift+T again. Verify "Tune off" speech and falling chirp earcon.
- 13.3: Verify Tune toggle button state updates in toolbar (if visible).
- 13.4: Press Ctrl+T (ATU Tune). Verify "ATU tuning" speech and pulsing earcon.
- 13.5: If ATU succeeds: verify progress earcon stops, success arpeggio plays, "SWR [value]" spoken.
- 13.6: If ATU fails: verify progress earcon stops, fail arpeggio plays, "Tune failed, SWR [value]" spoken.
- 13.7: Menu items: Classic > Operations > Transmission > "Tune Carrier Ctrl+Shift+T" — verify present and toggles.
- 13.8: Menu items: Antenna > "ATU Tune Ctrl+T" — verify present and starts cycle.

### 14. Phase 6: Antenna Switching

- 14.1: Menu > Antenna > RX Antenna submenu. Verify antenna list matches radio's actual antennas (ANT1, ANT2, etc.).
- 14.2: Select a different RX antenna from menu. Verify "RX antenna [name]" spoken and radio switches.
- 14.3: Menu > Antenna > TX Antenna submenu. Same verification.
- 14.4: Ctrl+Shift+5 to open Antenna ScreenFields category. Verify RX Antenna and TX Antenna cycle controls are present.
- 14.5: Cycle RX antenna with Up/Down arrows in ScreenFields. Verify "RX antenna [name]" spoken and radio switches.
- 14.6: Cycle TX antenna similarly. Verify.
- 14.7: Verify antenna checkmarks in menus reflect current selection.

### 15. Phase 7: Slice Management + Enhanced Status

- 15.1: Ctrl+Shift+2 to open Audio and Slice category. Verify "Audio and Slice expanded."
- 15.2: Tab past audio controls to Create Slice button. Verify "Create a new slice" announced.
- 15.3: Press Create Slice. Verify "Slice created, 2 slices active" spoken (if radio supports >1 slice).
- 15.4: If at max slices, verify "Maximum slices reached."
- 15.5: Tab to Release Slice button. Press it. Verify "Slice released, 1 slices active."
- 15.6: With only 1 slice, press Release. Verify "Cannot release the only slice."
- 15.7: Menu: verify Create Slice and Release Slice items appear in both Classic and Modern menus.
- 15.8: With 2+ slices active, press Ctrl+Shift+S. Verify multi-slice detail: "2 slices. Slice A selected, transmit, [freq], [mode], pan center. Slice B, [freq], [mode], muted, pan right." or similar.
- 15.9: With 1 slice, Ctrl+Shift+S. Verify normal single-slice status (same as before).

### 16. Phase 8: Startup Speech

- 16.1: Connect to radio (or restart app with auto-connect). Wait 1-2 seconds after connect.
- 16.2: Verify startup speech: "Connected to [model], [local/SmartLink]. Listening on [freq], [mode], [band], slice [letter]."
- 16.3: Verify the speech completes without being cut off by focus changes.
- 16.4: Start app with no radio. Verify only the welcome speech plays (no connect speech).

### 17. Phase 9: Meters Panel

- 17.1: Press Ctrl+M to toggle meters on. Verify "Meter tones on" and panel becomes visible.
- 17.2: Tab into MetersPanel. Verify Expander header "Meters" is announced.
- 17.3: Expand the Meters expander. Tab through slot controls. Verify each slot has: meter type combo, waveform combo, pan combo, base frequency field, enabled checkbox, Test button, Remove button.
- 17.4: Change waveform on slot 1 to Square. Verify the tone character changes (buzzier).
- 17.5: Change waveform to Sawtooth. Verify brighter tone.
- 17.6: Press Test button. Verify "Testing [source] tone" speech and 2-second tone preview.
- 17.7: Press Add Meter Slot. Verify "Meter slot [N] added."
- 17.8: Press Remove on a slot. Verify "Slot removed, [N] slots remaining."
- 17.9: With only 1 slot, verify Remove button is disabled.
- 17.10: Check "Auto-enable meters when tuning." Press Ctrl+Shift+T to start tune. Verify meters activate automatically.
- 17.11: Press Ctrl+Shift+T to stop tune. Verify meters return to previous state (off if they were off before).
- 17.12: Check "Speak meter values periodically." Verify meter values are spoken at the configured interval.
- 17.13: Change speech interval to 5 seconds. Verify speech timing changes.
- 17.14: Escape from MetersPanel. Verify focus returns to FreqOut.
- 17.15: Ctrl+M to toggle meters off. Verify "Meter tones off."

### 18. Phase 10: 60m Channelization

- 18.1: Tune to 60m band (Shift+F3 or manually tune near 5.3 MHz).
- 18.2: Press Alt+Shift+Up. Verify "Channel 1, 5.3320 megahertz, USB" spoken and radio tunes there.
- 18.3: Press Alt+Shift+Up repeatedly. Verify cycling through Channel 1-5 then "60 meter digital segment, 5.3515 megahertz."
- 18.4: Press Alt+Shift+Down. Verify reverse navigation through channels.
- 18.5: Verify mode is set to USB on channelized frequencies.
- 18.6: Menu: verify "60m Channel Up Alt+Shift+Up" and "60m Channel Down Alt+Shift+Down" appear in band menu.
- 18.7: Open Settings > License tab. Verify Country combo (US) and "Enforce transmit rules" checkbox are present.
- 18.8: Verify enforce rules checkbox is on by default.

---

## Screen Reader Matrix

### NVDA

- All speech announcements above verified with NVDA.
- Focus order: FreqOut > ScreenFields (when expanded) > MetersPanel (when expanded) > Panadapter > Received Text > Sent Text.
- Startup speech completes without interruption.
- Leader key tones audible and distinct from speech.
- Menu navigation: all new items reachable and announced correctly.

### JAWS

- Repeat key tests from sections 9, 13, 16, 17, 18 with JAWS.
- Verify startup speech is not too verbose (user can Ctrl to interrupt if needed).
- Verify menu checkmarks read as "checked" / "not checked."
- Verify ComboBox selections in MetersPanel and SettingsDialog announce correctly.

---

## Notes

_(Record any bugs, unexpected behavior, or observations here)_
