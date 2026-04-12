# Sprint 25 — Foundational Features Plan

**Branch:** sprint25/track-a
**Version:** 4.1.16
**Goal:** Ship foundational radio operating features so the waterfall sprint can build on a solid base.

This plan covers the remaining foundational work after all 12 original phases and interactive fixes are complete.

---

## Phase 13: Quick Fixes (RigSelector label + default indicator)

Two small UX fixes found during testing.

**13a: RigSelector label fix**
- File: `JJFlexWpf/Dialogs/RigSelectorDialog.xaml.cs` line 158
- Current text: "Press Connect to SmartLink for remote radios."
- Fix: Change to "Press Remote for remote radios."
- One line change.

**13b: Default account indicator in Manage SmartLink**
- The SmartLinkAccountDialog shows accounts as "FriendlyName (Email) - Last used: date" but doesn't indicate which is the default.
- Add `IsDefault` bool property to `SmartLinkAccountInfo` class
- Update `ToString()` to append " (Default)" when true
- In `MainWindow.ShowSmartLinkAccountManager()` where accounts are built (line ~2540), compare each account email against the saved default from `AutoConnectConfig.SmartLinkAccountEmail`
- Screen reader will naturally read "(Default)" as part of the list item text

**Build + commit after Phase 13.**

---

## Phase 14: Expanded Typing Sound Modes

Add three always-available (unlocked) typing sound options: single fixed tone, random tones, and musical notes. Current "Click beep" becomes "Musical notes" and the two new modes are added.

**14a: Expand TypingSoundMode enum**
- File: `JJFlexWpf/AudioOutputConfig.cs`
- Rename `Beep` to `MusicalNotes` (or add new values and deprecate)
- Add: `SingleTone` — fixed pitch beep every keystroke (e.g. 800Hz, 30ms)
- Add: `RandomTones` — random frequency between 300-2000Hz, not snapped to musical notes
- Keep: `Off`, `Mechanical`, `TouchTone` unchanged

**14b: Update EarconPlayer.PlayTypingSound()**
- File: `JJFlexWpf/EarconPlayer.cs`
- `SingleTone` case: `PlayTone(800, 30, 0.25f)` — same pitch every time
- `RandomTones` case: `PlayTone(_keyRandom.Next(300, 2001), 30, 0.25f)` — random frequency, no musical snapping
- `MusicalNotes` case: keep existing MIDI note logic (C4-C8 random notes)

**14c: Update Settings dropdown**
- File: `JJFlexWpf/Dialogs/SettingsDialog.xaml.cs` — `PopulateTypingSoundCombo()`
- Always show: "Single tone", "Random tones", "Musical notes", "Off"
- Conditionally show (when unlocked): "Mechanical keyboard", "Touch-tone (DTMF)"
- Map dropdown indices to enum values carefully

**14d: Handle enum rename in persistence**
- `AudioOutputConfig.xml` stores the enum name as string. If renaming `Beep` to `MusicalNotes`, add XML deserialization fallback so existing configs that say "Beep" map to `MusicalNotes`. Alternatively, keep `Beep` as the internal enum name and only change the display string — simpler and avoids migration.

**Build + commit after Phase 14.**

---

## Phase 15: CW Prosign Status Notifications

Play Morse code prosigns for connection events and mode changes. Uses the operator's configured sidetone frequency and speed. Architected with an output provider abstraction for future haptic/vibration support.

**15a: Create ICwNotificationOutput interface**
- File: new `JJFlexWpf/ICwNotificationOutput.cs`
- Interface:
  - `void PlayDit(int frequencyHz, int durationMs, float volume)`
  - `void PlayDah(int frequencyHz, int durationMs, float volume)`
  - `void PlayElementGap(int durationMs)` — intra-character silence
  - `void PlayCharGap(int durationMs)` — inter-character silence
  - `void PlayWordGap(int durationMs)` — inter-word silence
- This abstraction lets us add gamepad vibration, iPhone haptic, or braille-device outputs later without touching the Morse logic.

**15b: Create EarconCwOutput (speaker implementation)**
- File: new `JJFlexWpf/EarconCwOutput.cs`
- Implements `ICwNotificationOutput` using `EarconPlayer.PlayTone()` for dits/dahs and `Task.Delay()` for gaps
- Plays on the alert channel (same as earcons)

**15c: Create MorseNotifier**
- File: new `JJFlexWpf/MorseNotifier.cs`
- Reuses the character-to-element mapping from `JJPortaudio/Morse.cs` codeTable (copy the static data, don't take a dependency on JJPortaudio)
- Properties: `int SidetoneFrequency` (default 700Hz), `int SpeedWpm` (default 20), `float Volume` (0.25)
- Key method: `async Task PlayProsign(string prosign)` — plays each element with correct PARIS timing
- Convenience methods: `PlayAS()`, `PlayBT()`, `PlaySK()`
- Mode announcement: `PlayModeChange(string modeName)` — plays mode abbreviation in CW (e.g. "USB", "CW", "AM")

**15d: Add CW notification settings to AudioOutputConfig**
- `bool CwNotificationsEnabled` (default false — opt-in)
- `int CwSidetoneHz` (default 700)
- `int CwSpeedWpm` (default 20)
- `bool CwModeAnnounce` (default false — play mode changes in CW when speech is off)

**15e: Wire into connection events**
- In `FlexBase.cs` connection flow:
  - On "Connection slow, retrying" (line 827): play AS prosign
  - On successful connect (line 417): play BT prosign
- In app shutdown: play SK prosign before closing
- In mode change handler: if `CwModeAnnounce` is true AND speech verbosity is Off, play mode name in CW

**15f: New "Verbosity & Notifications" tab in Settings**
- Create a new Settings tab that consolidates all notification/feedback controls:
  - **Speech section:** Speech verbosity level (moved from Audio tab — Off/Terse/Chatty)
  - **Earcons section:** Earcon sounds enable/disable (moved from being hotkey-only)
  - **CW Notifications section:** Enable checkbox, Sidetone frequency spinner (400-1200 Hz), Speed spinner (10-30 WPM), Announce mode changes in CW checkbox (when speech off)
  - **Meter Tones section:** Meter tones enable/disable (moved from Audio tab)
  - **Future placeholders:** Haptic/vibration output settings will go here
- Audio tab keeps: device selection, typing sounds, braille display settings
- All moved settings keep their existing AudioOutputConfig properties — just a UI reorganization

**Build + commit after Phase 15.**

---

## Phase 16: Editable Filter Presets

Add a dialog to create, edit, delete, and reorder filter presets per mode. Backend persistence already exists in FilterPresets.cs.

**16a: Filter Preset Editor dialog**
- File: new `JJFlexWpf/Dialogs/FilterPresetEditorDialog.xaml` + `.cs`
- WPF dialog (follows JJFlexDialog pattern)
- Content: mode selector (combo or tabs), preset list for selected mode, Add/Edit/Delete/Move Up/Move Down buttons
- Each preset shows: name + bandwidth (e.g., "Normal — 2.4 kHz")
- Add/Edit opens a sub-dialog with: Name (text), Low Hz (number), High Hz (number), with validation per mode bounds
- Accessible: all buttons have AutomationProperties.Name, list items read name + bandwidth

**16b: Wire to menu**
- Add "Edit Filter Presets" to the Filter menu (or DSP menu, wherever filter presets are currently cycled)
- Hotkey: TBD (Ctrl+J then F? or just a menu item for now)

**16c: Persistence**
- Use existing `FilterPresets.Save(configDir, operatorName)` and `Load()` — already implemented
- On dialog OK: save immediately, update the in-memory preset list
- Filter cycling (Alt+[, Alt+]) automatically uses updated presets

**16d: Reset to defaults**
- "Reset to Defaults" button that restores the hardcoded presets for the selected mode

**Build + commit after Phase 16.**

---

## Phase 17: Editable Tuning Step Presets

Make the coarse and fine tuning step lists configurable per operator.

**17a: Expose step arrays**
- File: `JJFlexWpf/FreqOutHandlers.cs`
- Change `_coarseSteps` and `_fineSteps` from private hardcoded arrays to `List<int>` properties
- Add `SetCoarseSteps(List<int> steps)` and `SetFineSteps(List<int> steps)` methods
- Validate: at least one step, all positive, sorted ascending

**17b: Tuning Step Editor dialog**
- File: new `JJFlexWpf/Dialogs/TuningStepEditorDialog.xaml` + `.cs`
- Two lists: Coarse Steps and Fine Steps
- Each list: Add/Edit/Delete/Move Up/Move Down
- Add/Edit: simple number entry with validation (positive integer, Hz units)
- Shows human-readable labels: "1 kHz", "500 Hz", "10 Hz", etc.
- Reset to Defaults button (restores {1000, 2000, 5000} and {5, 10, 100})

**17c: Persistence**
- Save/load as XML per operator (similar to filter presets pattern)
- File: `{AppData}/JJFlexRadio/{OperatorName}_tuningSteps.xml`
- Load at radio connect, save on dialog OK

**17d: Wire to Settings or menu**
- Add to Settings dialog (Tuning tab or Audio tab) or as a menu item
- Accessible from Ctrl+J leader key: TBD

**Build + commit after Phase 17.**

---

## Phase 18: Braille Status Line Display-Size-Aware Formatting

Replace naive truncation with per-width content profiles in BrailleStatusEngine.

**18a: Define display profiles**
- File: `JJFlexWpf/BrailleStatusEngine.cs`
- 20-cell profile: Frequency + Mode only (critical minimum). Example: `14.250 USB          `
- 32-cell profile: Frequency + Mode + S-meter + Slice. Example: `14.250 USB SM7 A              `
- 40-cell profile: All current fields (frequency, mode, s-meter/SWR/power/ALC, DSP flags, slice). This is the current behavior.
- 80-cell profile: All fields with extra spacing and full labels. Example: `14.250.00 USB  S7+10  NR NB  Slice A  SWR 1.2  PWR 75W       `

**18b: Implement profile selection**
- In `BuildStatus()` method, select profile based on `CellCount`
- Each profile defines which fields to include and their format (abbreviated vs full)
- Extract formatting into helper methods per profile tier

**18c: Test with different cell counts**
- Verify 20-cell shows only critical info (freq + mode)
- Verify 40-cell matches current behavior (no regression)
- Verify 80-cell shows expanded info with readable spacing

**Build + commit after Phase 18.**

---

## Phase 19: MultiFlex Management

Full MultiFlex client awareness: who's connected, notifications on connect/disconnect, ability to kick clients.

**19a: Client tracking in FlexBase**
- File: `Radios/FlexBase.cs`
- Expand existing `guiClientAdded()` handler (line 2430) to:
  - Play earcon on other-client connect (not our own): short ascending chirp
  - Speak: "Client connected: {Program} on {Station}" (VerbosityLevel.Terse)
  - Optionally play CW notification (BT) if CW notifications enabled
- Expand `guiClientRemoved()` handler (line 2528) to:
  - Play earcon on other-client disconnect: short descending chirp
  - Speak: "Client disconnected: {Program}" (VerbosityLevel.Terse)
- Track client list changes for UI refresh

**19b: MultiFlex Status dialog**
- File: new `JJFlexWpf/Dialogs/MultiFlexDialog.xaml` + `.cs`
- Accessible WPF dialog showing connected clients as a ListBox
- Each item shows: Program name, Station name, Client handle, Slices owned (by letter)
- "Our" client marked with "(This client)" suffix
- Buttons: Disconnect Selected, Disconnect All Others, Close
- `DisconnectClientByHandle()` API already exists in FlexLib
- Refresh on GUIClientAdded/Updated/Removed events
- Hotkey: Ctrl+Alt+M or add to Radio menu

**19c: MultiFlex toggle (if supported)**
- Research: FlexLib may not expose a MultiFlex on/off toggle — this might be a radio-level setting only changeable in SmartSDR settings
- If API exists: add toggle to Radio menu with speech confirmation
- If not: skip this sub-item, document limitation

**19d: Slice ownership display**
- In the MultiFlex dialog, show which slices each client owns
- Use `Slice.ClientHandle` to map slices to clients
- When kicking a client, announce which slices will be freed: "Disconnecting {Program} — slices B and C will be released"

**Build + commit after Phase 19.**

---

## Phase 20: Wire NR into Audio Pipeline

Connect the existing NoiseReductionProvider (RNNoise) and SpectralSubtractionProvider into the live RX audio chain.

**20a: Find the audio chain insertion point**
- The RX audio from FlexLib arrives via `RXAudioStream` as raw PCM
- Need to trace where this is consumed in FlexBase/MainWindow and converted to speaker output
- Insert NR providers between the radio audio source and the speaker output
- Chain order: RXAudioStream source → SpectralSubtraction → RNNoise → Speaker

**20b: Wire NoiseReductionProvider**
- Create provider instance during PowerOn, configure from AudioOutputConfig (RNNoiseEnabled, RNNoiseStrength)
- Feed the RX audio ISampleProvider through NoiseReductionProvider
- Mode tracking: set `CurrentMode` property on mode changes for auto-disable in CW/DIG

**20c: Wire SpectralSubtractionProvider**
- Create provider instance, configure from AudioOutputConfig (SpectralSubEnabled, SpectralSubStrength)
- Wire before RNNoise in chain (spectral sub first to remove broadband noise, then RNNoise for residual)

**20d: Settings integration**
- AudioOutputConfig already has: RNNoiseEnabled, RNNoiseStrength, SpectralSubEnabled, SpectralSubStrength, SpectralSubSampleDuration
- Settings UI already has fields for these (verify)
- Noise profile capture: add "Sample Noise" button or hotkey that calls SpectralSubtractionProvider.StartSampling()
- Speech feedback: "Sampling noise... done. Profile captured."

**20e: Hotkey integration**
- Ctrl+J, R for RNNoise toggle (8000/Aurora only — already gated)
- Ctrl+J, S for Spectral NR toggle
- Both should announce on/off state and "not available" on unsupported radios

**Note:** This phase needs additional exploration of the RX audio path before coding. The exact insertion point depends on how FlexBase routes RXAudioStream data to the speaker — may involve PortAudio callbacks, NAudio chain, or both. Plan to explore first, then implement.

**Build + commit after Phase 20.**

---

## Phase 21: Trace Cleanup

Audit and trim verbose tracing that inflates log files.

**21a: Audit connection traces**
- FlexBase.cs has 30+ Tracing.TraceLine calls in ReconnectRemote and TryAutoConnectRemote
- These are valuable for debugging connection issues but verbose for normal operation
- Consider gating behind a "verbose connection tracing" flag or reducing to key milestones only (begin, auth, connected, failed)

**21b: Audit SmartLinkAccountDialog traces**
- Lines 121, 123: trace on every "Set Default" click with full type info — reduce to single line

**21c: Remove any remaining debug traces**
- Search for "debug", "TODO", "test" in trace messages
- Remove or gate behind trace level

**Build + commit after Phase 21.**

---

## Phase 22: Changelog Finalization

Write the 4.1.16 changelog entry covering all Sprint 25 work (original phases + interactive fixes + foundational features).

- Follow existing tone: warm, personal, first-person, ham-radio audience
- No internal jargon (no track labels, sprint numbers, framework names)
- Screen reader details OK (users are screen reader users)
- Cover the user-facing highlights: typing sounds, easter eggs, braille status, action toolbar, CW notifications, MultiFlex management, filter/step editing, NR pipeline, accessibility improvements

**Commit changelog + final version verification.**

---

## Phase Order and Dependencies

Phases are numbered 13-22 continuing from the original Sprint 25 phases (1-12).

- Phases 13-14: No dependencies, quick wins first
- Phase 15 (CW): Independent, but informs Phase 19 notifications
- Phases 16-17 (filter/step presets): Independent of each other, can run in any order
- Phase 18 (braille): Independent
- Phase 19 (MultiFlex): Can use CW notifications from Phase 15 if available
- Phase 20 (NR pipeline): Independent, needs exploration spike first
- Phase 21 (trace): After all code is written
- Phase 22 (changelog): Last

All phases are serial (single track, single branch sprint25/track-a).

---

## Build Verification

After every phase: full solution build + verify exe timestamp per CLAUDE.md rules.

```batch
dotnet build JJFlexRadio.sln -c Release -p:Platform=x64 --verbosity minimal
powershell -Command "(Get-Item 'bin\x64\Release\net8.0-windows\win-x64\JJFlexRadio.exe').LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')"
```
