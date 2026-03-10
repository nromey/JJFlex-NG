# Sprint 22: Ragchew SWR Pileup

**Theme:** Don's daily-driver feedback — regressions, tuning workflow, meters panel, and 60m channelization.

**Sprint Goal:** Fix WPF migration regressions Don discovered on 4.1.15 (tune button, antenna switching, slice management, About dialog, auto-connect discoverability), add the meters UI panel that makes Sprint 21's engine user-configurable, deliver startup speech and tuning debounce, add Command Finder categories, and implement 60m channelization.

**Source:** Planning conversation between Don and Noel, March 9, 2026.

**Execution:** Single serial track. No worktrees. All work on a feature branch from main.

**Branch:** `sprint22/ragchew-swr-pileup`

**Important:** Sprint 21 code is merged to main but **untested**. Don has never seen meter tones, Audio Workshop, leader key, TX sculpting, help file, or the app rename. Sprint 22 builds on top of Sprint 21. The test matrix covers both sprints.

---

## What Sprint 21 Already Built (don't touch, just know it's there)

- **MeterToneEngine** (`JJFlexWpf/MeterToneEngine.cs`) — 4 slots, sine-only, 3 presets (RX/TX/Full Monitor), peak watcher, speech readout, global kill switch (Ctrl+Shift+M)
- **ContinuousToneSampleProvider** (`JJFlexWpf/ContinuousToneSampleProvider.cs`) — phase-continuous sine wave with 10ms fade, volatile frequency/volume/active controls
- **AudioOutputConfig** (`JJFlexWpf/AudioOutputConfig.cs`) — persists earcon device, volumes, meter tone state, per-slot configs to XML
- **Audio Workshop** (`JJFlexWpf/Dialogs/AudioWorkshopDialog.xaml.cs`) — 3 tabs (TX Audio, Live Meters, Earcon Explorer), preset save/load/export
- **ScreenFields TX expansion** — mic gain, compander, processor, TX filter, monitor already in Transmission category
- **Leader key** (Ctrl+J) — state machine in KeyCommands.vb, DSP toggles, earcon vocabulary
- **TX filter sculpting** — bracket-key shortcuts for nudging TX filter edges
- **CHM help file** — F1 context-sensitive, 22 Markdown pages
- **App rename** — display name is "JJ Flexible Radio Access" everywhere user-facing

---

## Scope Summary

Nine items from Don's feedback (Item 7 is a no-op — Sprint 21 already addressed it):

**Regressions (priority):**
- Item 1: RigSelector auto-connect button discoverability
- Item 2a: Tune button (Ctrl+Shift+T) — lost in WPF migration
- Item 2a-2: ATU Tune button (Ctrl+T) — simpler key for the more common action
- Item 3: Antenna switching (ANT1/ANT2 for RX and TX)
- Item 4: Slice management in menus and ScreenFields
- Item 5: About dialog screen reader fix

**New features:**
- Item 2b-2f: Meters panel (UI for Sprint 21 engine + new waveforms)
- Item 6: Startup speech on connect + enhanced Ctrl+Shift+S
- Item 8: 60m channelization
- Item 9: Command Finder categories
- Item 10: Adjustable tuning speech debounce

---

## Execution Order

Quick wins first (isolated dialogs), then interconnected regressions, then the two big features.

1. **Phase 1:** Item 1 — RigSelector auto-connect button
2. **Phase 2:** Item 5 — About dialog rewrite
3. **Phase 3:** Item 9 — Command Finder categories
4. **Phase 4:** Item 10 — Tuning speech debounce
5. **Phase 5:** Item 2a + 2a-2 — Tune button + ATU Tune
6. **Phase 6:** Item 3 — Antenna switching
7. **Phase 7:** Item 4 — Slice management + Ctrl+Shift+S enhancement
8. **Phase 8:** Item 6 — Startup speech on connect
9. **Phase 9:** Item 2b-2f — Meters panel + new waveforms
10. **Phase 10:** Item 8 — 60m channelization

Commit after each phase. Build verification after Phases 5, 8, and 10.

---

## Phase 1: RigSelector Auto-Connect Button (Item 1)

**Problem:** Auto-connect setup is hidden behind right-click. Don didn't know to right-click. Empty radio list gives no screen reader feedback.

**Files modified:**
- `JJFlexWpf/Dialogs/RigSelectorDialog.xaml` — add button to stack
- `JJFlexWpf/Dialogs/RigSelectorDialog.xaml.cs` — wire button, empty-list announce

**Implementation:**

1. Add "Setup Auto-Connect" button in the XAML button stack after TestButton, before CancelButton
2. Wire `AutoConnectButton_Click` — calls the existing `AutoConnectMenuItem_Click` handler (already opens `AutoConnectSettingsDialog` for selected radio)
3. Button disabled when `RadiosBox.SelectedItem == null`, enabled when a radio is selected (bind to `RadiosBox.SelectionChanged`)
4. Set `AutomationProperties.Name = "Setup Auto-Connect"` on the button
5. Empty list handling: In `RadiosBox_Loaded` or after discovery timeout, if `RadiosBox.Items.Count == 0`:
   - Set `RadiosBox.AutomationProperties.Name = "Radio list, empty, no radios found"`
   - Call `Radios.ScreenReaderOutput.Speak("No radios found")` after a short delay (500ms to let dialog settle)
6. Keep the existing right-click context menu as secondary path

**Screen reader behavior:**
- Tab to button stack: "Setup Auto-Connect button, disabled" (no radio selected)
- Select a radio, tab to button: "Setup Auto-Connect button"
- Empty list: "Radio list, empty, no radios found"

---

## Phase 2: About Dialog Rewrite (Item 5)

**Problem:** About dialog reads poorly — screen reader says generic labels instead of values.

**Files created:**
- `JJFlexWpf/Dialogs/AboutDialog.xaml` + `.cs` — new WPF tabbed dialog

**Files modified:**
- `JJFlexWpf/NativeMenuBar.cs` — wire Help > About to new dialog
- `AboutProgram.vb` — mark `[Obsolete]` or delete (keep until new dialog verified)

**Implementation:**

WPF dialog with 3 tabs, each containing a single read-only TextBox with `IsReadOnly=True`, `TextWrapping=Wrap`, `AcceptsReturn=True`. This gives screen readers clean linear text instead of label/value pairs.

**Tab 1 — About:**
```
JJ Flexible Radio Access
Version 4.1.16
Copyright (c) 2024-2026

Originally created by Jim Shaffer
Maintained by Noel Romey, K5NER

JJ Flexible Radio Access is an accessible alternative to SmartSDR
for controlling FlexRadio 6000 and 8000 series transceivers.
Designed for screen reader users.
```

**Tab 2 — Radio (populated after connect, "Not connected" otherwise):**
```
Radio: FLEX-6600
Serial: 1234-5678-9012-3456
Firmware: 3.4.39
Nickname: Don's 6600

Connection: Local (192.168.1.100)
Active slices: 2 of 4
SCU count: 2
Diversity: Available

Selected slice: A
Transmit slice: A
```

Data sources: `Rig.Model`, `Rig.Serial`, `theRadio.Version` (FlexLib), `Rig.Nickname`, connection type from `IsRemote`, `theRadio.IP`, `Rig.SliceCount`/`Rig.MaxSlices`, `theRadio.DiversityIsAllowed`

**Tab 3 — System:**
```
.NET Runtime: 8.0.x
Windows: Windows 11 Pro (10.0.26200)
Architecture: x64
FlexLib: 4.0.1.x
WebView2: (version)

Screen reader: NVDA detected
```

Detection: `Environment.Version`, `Environment.OSVersion`, `RuntimeInformation.ProcessArchitecture`, assembly version reflection, screen reader detection from existing `ScreenReaderOutput` class

**Tab 4 — Diagnostics:**
```
Update Status: You're up to date (4.1.16)
  — or —
Update Status: Version 4.1.17 available

[Check for Updates] button
```
- Hits `https://api.github.com/repos/nromey/JJFlex-NG/releases/latest` (JSON, parse `tag_name`)
- Compares against current version, shows result in read-only textbox
- No WebView2 — plain text, fully accessible
- Future: auto-check on startup with on/off setting (deferred)

Additional diagnostic controls:
- "Run Connection Test" button — launches existing `ConnectionTesterDialog`
- Network info when connected: radio IP, connection type
- "Copy Diagnostic Report" button — grabs all info from all 4 tabs for bug reports

**Bottom buttons:**
- "Copy to Clipboard" — concatenates all 4 tabs' text with section headers, copies to clipboard, speaks "Copied to clipboard"
- "Export Diagnostic Report" — SaveFileDialog, writes same concatenated text to .txt file, default filename `JJFlex-Diagnostic-2026-03-10.txt`, speaks "Diagnostic report saved"
- "Close"

**Menu wiring:** In `NativeMenuBar.cs`, change the Help > About handler to instantiate and show `AboutDialog` instead of `AboutProgram`

---

## Phase 3: Command Finder Categories (Item 9)

**Problem:** Command Finder is search-only. No way to browse by category.

**Files modified:**
- `JJFlexWpf/Dialogs/CommandFinderDialog.xaml` — add category ComboBox
- `JJFlexWpf/Dialogs/CommandFinderDialog.xaml.cs` — wire category filtering

**Implementation:**

1. Add `CategoryCombo` ComboBox **above** `SearchBox` in the XAML (top-down flow: category → search → results)
   - `AutomationProperties.Name = "Category"`
   - Default item: "All"
2. In `OnLoaded()`, populate categories from the distinct `Group` values in the command list:
   ```csharp
   var groups = _allCommands.Select(c => c.Group).Where(g => !string.IsNullOrEmpty(g)).Distinct().OrderBy(g => g).ToList();
   groups.Insert(0, "All");
   CategoryCombo.ItemsSource = groups;
   CategoryCombo.SelectedIndex = 0;
   ```
3. Wire `CategoryCombo.SelectionChanged` to call `FilterResults()` — but **not** while arrowing through (use `DropDownClosed` event or check `IsDropDownOpen` to avoid jabbering)
4. Extend `FilterResults()`:
   ```csharp
   var selectedCategory = CategoryCombo.SelectedItem as string;
   var filtered = _allCommands.Where(c =>
       (selectedCategory == "All" || c.Group == selectedCategory) &&
       (string.IsNullOrEmpty(query) || MatchesQuery(c, query)) &&
       IsInScope(c)
   ).ToList();
   ```
5. Arrow through results list should **not** execute commands — verify current behavior (it should already work this way since execution is on Enter)

**Screen reader behavior:**
- Tab order: Category combo, Search box, Results list
- "Category, All" → arrow to "DSP" → list filters to DSP commands only
- Search still works within selected category

---

## Phase 4: Tuning Speech Debounce (Item 10)

**Problem:** Speech debounce during tuning is hardcoded at 300ms. Some users want every step, others want longer delays.

**Files modified:**
- `JJFlexWpf/FreqOutHandlers.cs` — replace hardcoded 300 with config value
- `JJFlexWpf/Dialogs/SettingsDialog.xaml` — add debounce controls to Tuning tab
- `JJFlexWpf/Dialogs/SettingsDialog.xaml.cs` — wire debounce settings
- `JJFlexWpf/AudioOutputConfig.cs` (or new config) — persist debounce settings

**Implementation:**

1. Add two settings to persist (either in `AudioOutputConfig` or a new `TuningConfig`):
   - `TuneDebounceEnabled` (bool, default true)
   - `TuneDebounceMs` (int, range 50-1000, default 300)

2. In `FreqOutHandlers.SpeakTuningDebounced()`, replace `await Task.Delay(300, token)` with:
   ```csharp
   if (!_tuneDebounceEnabled)
   {
       // No debounce — speak every step
       Radios.ScreenReaderOutput.Speak(message, interrupt: true);
       return;
   }
   // ... existing debounce logic with configurable delay
   await Task.Delay(_tuneDebounceMs, token);
   ```

3. In Settings > Tuning tab, add:
   - "Speech debounce" checkbox (on/off toggle)
   - "Debounce delay" slider or value field (50-1000 ms, step 50, shown when checkbox is on)
   - Label: "Controls how long to wait after you stop tuning before speaking the frequency. Off means speak every step."

4. **Leader key toggle:** Ctrl+J → D toggles debounce on/off. Speaks "Debounce on, 300 milliseconds" or "Debounce off, speaking every step." Preserves the ms value when toggling off so toggling back on restores it.

---

## Phase 5: Tune Button + ATU Tune (Items 2a, 2a-2)

**Problem:** Tune button existed in Jim's original UI and was lost in WPF migration. Don uses a manual tuner daily.

**Files modified:**
- `Radios/FlexBase.cs` — add `TxTune` property wrapper, `ATUTuneStart()` method
- `JJFlexWpf/MainWindow.xaml` — add Tune button to main UI
- `JJFlexWpf/MainWindow.xaml.cs` — wire Tune button, ATU status handler
- `KeyCommands.vb` — add Ctrl+T and Ctrl+Shift+T hotkeys
- `JJFlexWpf/NativeMenuBar.cs` — add Tune and ATU Tune to menus
- `JJFlexWpf/Dialogs/SettingsDialog.xaml` — TunePower to Tuning tab (if not already there)
- `ApplicationEvents.vb` — wire hotkey handlers

**Implementation:**

### Step 5.0: Add WaveformType to ContinuousToneSampleProvider (pulled forward from Phase 9)

Needed now for ATU progress earcon. Add `WaveformType` enum and modify `Read()` method:

```csharp
public enum WaveformType
{
    Sine,          // Smooth pure tone (current behavior)
    Square,        // Buzzier, more distinct
    Sawtooth,      // Brighter, more harmonics
    SlowPulse,     // Sine on 300ms, off 300ms
    FastPulse,     // Sine on 100ms, off 100ms
    Alternating    // Alternates between two frequencies at 100ms
}
```

In `Read()`, replace hardcoded sine with a switch on `Waveform` property. All waveforms use phase-continuous approach and existing 10ms fade. This is Step 9.1 done early — Phase 9 just uses what's already built.

### Tune Button (2a)

1. Add `TxTune` property to FlexBase.cs:
   ```csharp
   public bool TxTune
   {
       get => theRadio.TXTune;
       set { theRadio.TXTune = value; }
   }
   ```

2. Add Tune toggle button in MainWindow.xaml, positioned in the control area (left of ScreenFields or near the status bar):
   - `AutomationProperties.Name = "Tune"` (updates to "Tune, on" / "Tune, off" on toggle)
   - Visual: toggle button that stays pressed when tune is active

3. Wire Ctrl+Shift+T in KeyCommands.vb:
   ```vb
   ' Toggle tune carrier
   Rig.TxTune = Not Rig.TxTune
   Dim state = If(Rig.TxTune, "Tune on", "Tune off")
   ScreenReaderOutput.Speak(state, interrupt:=True)
   EarconPlayer.PlayConfirmTone()
   ```

4. TunePower is already in ScreenFields TX category as a ValueFieldControl. Verify it's also in Settings > Tuning tab. If not, add it there.

5. **Power range fix for Aurora:** XmitPower and TunePower ValueFieldControls are currently capped at 100W. Aurora AU-510/AU-520 are 500W radios. Make the max power dynamic based on radio model — query `theRadio.MaxPowerLevel` or equivalent FlexLib property to set the ValueFieldControl upper bound. This affects both TX Power and Tune Power in ScreenFields and Settings. All 6000/8000 series stay at 100W max, Aurora gets 500W max.

5. Menu item: Slice > Transmission > "Tune (Ctrl+Shift+T)" with checkmark when active

### ATU Tune (2a-2)

1. Add `StartATUTune()` method to FlexBase.cs — calls `theRadio.ATUTuneStart()` (FlexLib method)

2. ATU status is already monitored via `ATUTuneStatus_Changed` callback in FlexBase. Extend with full audio narrative:
   - `ATUTuneStatus.InProgress` → speak "ATU tuning" + start **progress earcon**: soft two-tone alternating pulse (~400/500 Hz, 800ms cycle) that repeats until ATU reports done. Unobtrusive but audible — "I'm working on it."
   - `ATUTuneStatus.OK` → stop progress earcon + play **success tritone** (rising major arpeggio, e.g. C-E-G, ~150ms total) + speak "ATU successful, SWR [value]"
   - `ATUTuneStatus.Fail` → stop progress earcon + play **failure tone** (descending minor, e.g. E-C-A, ~200ms) + speak "ATU failed"
   - `ATUTuneStatus.Bypass` → stop progress earcon + speak "ATU bypassed"
   - Progress earcon uses `ContinuousToneSampleProvider` with `WaveformType.FastPulse` (100ms on/off from Phase 9's waveform enum) — no custom provider needed. Set frequency ~450 Hz, moderate volume, stop on status change.

3. **Free up Ctrl+T:** Remove `LogStats` binding from Ctrl+Shift+T in Logging scope. Move LogStats to Ctrl+J → L (leader key action). This frees the T key combos for Tune/ATU.

4. Wire Ctrl+T in KeyCommands.vb:
   ```vb
   ' Start ATU tune cycle
   If Not Rig.FlexTunerOn Then
       ScreenReaderOutput.Speak("ATU is off", interrupt:=True)
       Return True
   End If
   Rig.StartATUTune()
   ScreenReaderOutput.Speak("ATU tuning", interrupt:=True)
   ```

5. Gate on ATU presence: check `Rig.FlexTunerOn` or `Rig.FlexTunerType` before allowing ATU tune. If no ATU, speak "No antenna tuner available."

6. Menu item: Slice > Antenna > "ATU Tune (Ctrl+T)"

7. Add ATU Tune button to ScreenFields Antenna category (alongside existing ATU On/Off and ATU Mode)

**Future (not Sprint 22):** Settings > Tuning tab gets a "Tuner Source" combo: Internal ATU, Tuner Genius XL, External ATU, Manual, None. Ctrl+T behavior adapts based on source. For now, Ctrl+T always uses the Flex internal ATU.

---

## Phase 6: Antenna Switching (Item 3)

**Problem:** Cannot switch between ANT1/ANT2 for RX and TX. Antenna submenu only has ATU and Diversity.

**Files modified:**
- `Radios/FlexBase.cs` — expose RX/TX antenna list and selection properties cleanly
- `JJFlexWpf/NativeMenuBar.cs` — expand Antenna submenu with RX/TX antenna selection
- `JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs` — expand Antenna category with slice selector + antenna combos

**Implementation:**

### FlexBase Properties

1. Verify `RXAntenna` and `TXAntenna` properties work correctly with `Slice.RXAntList` / `Slice.TXAntList`. Current implementation maps to integer index — may need string-based properties for menu display:
   ```csharp
   public string RXAntennaName
   {
       get => activeSlice?.RXAnt ?? "ANT1";
       set { if (activeSlice != null) activeSlice.RXAnt = value; }
   }
   public string TXAntennaName
   {
       get => activeSlice?.TXAnt ?? "ANT1";
       set { if (activeSlice != null) activeSlice.TXAnt = value; }
   }
   public List<string> RXAntennaList => activeSlice?.RXAntList?.ToList() ?? new();
   public List<string> TXAntennaList => activeSlice?.TXAntList?.ToList() ?? new();
   ```

### Antenna Submenu Expansion

2. In `NativeMenuBar.cs`, modify the Antenna submenu build to add before ATU items. **All antenna items built dynamically at runtime** — iterate `Rig.RXAntennaList` and `Rig.TXAntennaList` (populated from FlexLib's `Slice.RXAntList` / `Slice.TXAntList`). No hardcoded antenna names. Structure:
   ```
   RX Antenna > (dynamically built from Rig.RXAntennaList, radio-checkmark on current)
   TX Antenna > (dynamically built from Rig.TXAntennaList, radio-checkmark on current)
   ─────────────
   ATU On/Off (existing)
   ATU Mode (existing)
   ATU Tune (new from Phase 5)
   ATU Memories (existing)
   ─────────────
   Diversity (existing, if hardware supports)
   ```
   A 6300 might show ANT1/ANT2, a 6700 might show ANT1/ANT2/RX_A/RX_B/XVTR — we don't care, we just render what the radio reports.

### ScreenFields Antenna Category Expansion

4. Add to the Antenna category in ScreenFieldsPanel:
   - **Slice selector combo** at the top (A, B, C, D — only showing active slices)
   - **RX Antenna combo** — populated from `Rig.RXAntennaList`, shows current selection
   - **TX Antenna combo** — populated from `Rig.TXAntennaList`, shows current selection
   - (Existing ATU On/Off and ATU Mode controls below)

5. Slice selector pattern: when user picks a different slice, the RX/TX antenna combos update to show that slice's antenna settings. Changes apply to the selected slice.

**Screen reader behavior:**
- Menu: "RX Antenna submenu" → "ANT1, checked" / "ANT2"
- ScreenFields: "Slice, combo box, A" → Tab → "RX Antenna, combo box, ANT1" → Tab → "TX Antenna, combo box, ANT1"

---

## Phase 7: Slice Management + Ctrl+Shift+S (Items 4, partial 6)

**Problem:** Can only create/release slices from frequency field. No menu/ScreenFields access. Ctrl+Shift+S too minimal.

**Files modified:**
- `JJFlexWpf/Controls/ScreenFieldsPanel.xaml` — add Slice category (new 6th expander)
- `JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs` — Slice category controls, shift hotkey assignments
- `JJFlexWpf/NativeMenuBar.cs` — add Create/Release Slice and Pan/Volume items to Slice menu
- `KeyCommands.vb` — wire Ctrl+Shift+S enhanced status
- `JJFlexWpf/MainWindow.xaml.cs` — build rich status string for Ctrl+Shift+S

**Implementation:**

### Expand Audio Category to "Audio and Slice"

1. Rename the existing **Audio** expander to **"Audio and Slice"**
   - No new category needed — keeps 5 categories, no navigation changes
   - Existing Audio controls stay at the top, Slice controls added below with a visual/logical separator

2. Add Slice controls below existing Audio controls (Volume, Pan, Mute, Headphone, Line Out):
   - **Slice selector combo** (A, B, C, D — same pattern as Antenna category)
   - **Create Slice** button (disabled if at max slices, speaks "Slice X created" or "Maximum slices reached")
   - **Release Slice** button (disabled if only 1 slice, speaks "Slice X released" and switches to remaining slice)
   - Note: Volume, Pan, and Mute are already in the Audio section — the slice selector combo changes which slice those controls apply to

### Slice Menu Additions

3. In NativeMenuBar, add to the Slice menu (Modern mode):
   - "Create Slice" menu item (with accelerator text if assigned)
   - "Release Slice" menu item
   - Separator
   - "Pan Left\tAlt+Left" / "Pan Right\tAlt+Right" (display existing hotkeys)
   - "Volume Up" / "Volume Down" (display existing hotkeys)

### Enhanced Ctrl+Shift+S

4. Build a `GetFullSliceStatus()` method in MainWindow.xaml.cs (or a helper class) that returns:
   ```
   "Slice A selected, transmit, 14.250 MHz, USB, mute off, pan center.
    Slice B, 7.150 MHz, LSB, muted, pan right."
   ```

5. Data sources per slice:
   - Letter: `VFOToLetter(sliceIndex)` (existing helper)
   - Selected: is this the active slice?
   - TX: is this the transmit slice?
   - Frequency: `Rig.VFOFreq` or equivalent per-slice
   - Mode: `Rig.Mode` per-slice
   - Mute: `slice.Mute`
   - Pan: map 0-100 to "left" / "center" / "right" (0-33 = left, 34-66 = center, 67-100 = right)

6. Wire Ctrl+Shift+S in KeyCommands.vb to call `GetFullSliceStatus()` and speak the result

---

## Phase 8: Startup Speech on Connect (Item 6)

**Problem:** After connecting to a radio, you don't know what state you're in.

**Files modified:**
- `JJFlexWpf/MainWindow.xaml.cs` — add post-connect speech
- `Radios/FlexBase.cs` — expose connect-complete event/callback if not already present

**Implementation:**

1. Identify the radio-connected callback. When the radio connects and slices are populated, fire the startup announcement. This is likely in the `RadioConnected` or equivalent handler in MainWindow/BridgeForm.

2. Build startup speech using the same `GetFullSliceStatus()` method from Phase 7:
   ```
   "Connected to FLEX-6600, local.
    Two slices.
    Slice A selected, transmit, 14.250 MHz, USB, mute off, pan center.
    Slice B, 7.150 MHz, LSB, muted, pan right."
   ```

3. Prepend radio info:
   - "Connected to [model], [local/SmartLink]."
   - "[N] slices." or "One slice."

4. Timing: delay 1-2 seconds after connect to let FlexLib populate slice data. Use the existing `SpeakWelcomeDelayed()` pattern (Task.Delay + marshal to UI thread).

5. This replaces the current `SpeakWelcome()` which only says "Welcome to JJ Flexible Radio Access, Modern mode." The new speech includes the welcome plus radio status.

6. If no radio connected (app starts without connecting), keep the existing welcome message.

7. Always-on for now. Verbosity settings (future sprint) will add a toggle.

**Screen reader testing concern:** NVDA tends to let new speech interrupt the queue — UI focus changes after connect could cut off the startup announcement. JAWS buffers and finishes, so the same announcement may feel too chatty. Test both:
   - **NVDA:** Verify full announcement completes without being cut off by focus events. May need to delay until after all focus settling is done, or queue the speech with a flag that prevents interruption.
   - **JAWS:** Verify announcement length feels appropriate, not too verbose. User can tap Ctrl to interrupt if they want.
   - If behavior is too different between the two, consider detecting which screen reader is active (existing `ScreenReaderOutput` detection) and adjusting speech strategy accordingly.

**Build and verify after this phase** — all regression items (1, 2a, 3, 4, 5) plus startup speech are done.

---

## Phase 9: Meters Panel + New Waveforms (Items 2b-2f)

**Problem:** Sprint 21's MeterToneEngine works but has no interactive UI for configuring slots, waveforms, or per-meter settings. Only controllable via presets and hotkeys.

This is the biggest phase. It extends the Sprint 21 engine with new waveforms and builds a user-facing meters panel in the main UI.

**Files created:**
- `JJFlexWpf/Controls/MetersPanel.xaml` + `.cs` — new collapsible meters panel for main UI

**Files modified:**
- `JJFlexWpf/ContinuousToneSampleProvider.cs` — add waveform type support (square, saw, pulse, alternating)
- `JJFlexWpf/MeterToneEngine.cs` — add WaveformType to MeterSlot, add/remove slot support, speech timer, auto-enable on tune
- `JJFlexWpf/MainWindow.xaml` — host MetersPanel
- `JJFlexWpf/MainWindow.xaml.cs` — wire Ctrl+M, auto-expand, tune integration
- `KeyCommands.vb` — Ctrl+M hotkey
- `JJFlexWpf/AudioOutputConfig.cs` — persist new per-slot waveform, speech interval, auto-enable setting

**Implementation:**

### Step 9.1: Waveform Types — ALREADY DONE in Phase 5

`WaveformType` enum and `ContinuousToneSampleProvider` waveform support was pulled forward to Phase 5 (Step 5.0) for ATU progress earcon. No work needed here — just verify all 6 waveform types work correctly with meter tones.

### Step 9.2: MeterSlot Extensions

Add to `MeterSlot`:
- `WaveformType Waveform` property (default Sine)
- Pass waveform to `ToneProvider.Waveform` when slot is configured

Add to `MeterToneEngine`:
- `AddSlot()` / `RemoveSlot(int index)` methods (currently fixed at 4, make dynamic)
- `AutoEnableOnTune` bool property — when true, enables default meters when TxTune activates
- `SpeechInterval` int property (1-10 seconds, default 3)
- `SpeechMuted` bool property (independent from tone mute)
- Speech timer: `DispatcherTimer` that fires every `SpeechInterval` seconds, calls `SpeakMeters()` with batched short labels ("SWR 1.5, fwd 80, ref 2")

### Step 9.3: Meters Panel UI

New WPF UserControl `MetersPanel` — a collapsible panel in the main UI tab order:

**Layout:**
- Expander header: "Meters" with Ctrl+M toggle
- When expanded, shows per-slot controls and global settings
- When Ctrl+M is pressed: if meters are off, turn them on AND expand panel. If on, turn them off (panel stays wherever it is).

**Per meter slot (repeating section):**
- Meter type combo: SWR, Forward Power, Reflected Power, ALC, Mic, Compression, Voltage, PA Temp, S-Meter
- Waveform combo: Sine, Square, Sawtooth, Slow Pulse, Fast Pulse, Alternating Tone
- Pan combo: Left, Center, Right (maps to -1.0, 0.0, +1.0)
- Base frequency value field (100-2000 Hz, step 50)
- Mute checkbox per slot
- Test button — plays 2 seconds of the configured tone at mid-range meter value
- Remove button (disabled if only 1 slot)

**Bottom controls:**
- Add Meter button (disabled if at max slots)
- "Auto-enable default meters when tuning" checkbox
- Master mute button (or Ctrl+M)
- "Speak meter values" checkbox (speech on/off, independent from tones)
- Speech interval value field (1-10 seconds, step 1)

**Panel placement:** After ScreenFieldsPanel in the tab order. Separate from ScreenFields — this is app config, not radio parameters.

**Accessibility:**
- Each slot group announced as "Meter slot 1: SWR, sine, center" etc.
- Tab navigates between controls within the panel
- Escape from panel returns focus to FreqOut
- Test button speaks "Testing SWR tone" then plays 2-second preview

### Step 9.4: Auto-Enable on Tune (Item 2e)

When `TxTune` is toggled on (via Ctrl+Shift+T from Phase 5):
1. Check `MeterToneEngine.AutoEnableOnTune`
2. If true and meters are currently off, enable meters with the current slot configuration
3. When tune is toggled off, meters return to their previous state (were they on before tune? restore that)

### Step 9.5: Speech/Braille Fallback (Item 2f)

Speech timer in MeterToneEngine:
- Fires every `SpeechInterval` seconds when `SpeechEnabled` is true
- Builds batched string: "SWR 1.5, fwd 80, ref 2, ALC 0.3"
- Only includes active (unmuted) meter slots
- Uses short labels: "SWR", "fwd", "ref", "ALC", "mic", "comp", "volts", "temp", "S"
- Speaks via `ScreenReaderOutput.Speak()` — braille displays see the same string
- Independent mute: can have tones only, speech only, or both

### Step 9.6: Persistence

Extend `AudioOutputConfig.xml` to persist:
- Per-slot: waveform type, base frequency (in addition to existing source, enabled, volume, pan, pitch range)
- Global: auto-enable on tune, speech enabled, speech interval, speech muted
- Slot count (dynamic, not fixed at 4)

---

## Phase 10: 60 Meter Channelization (Item 8)

**Problem:** No support for 60m channel navigation or TX enforcement.

**Files created:**
- `Radios/SixtyMeterChannels.cs` — data-driven channel table

**Files modified:**
- `Radios/FlexBase.cs` — 60m channel navigation methods
- `JJFlexWpf/FreqOutHandlers.cs` — channel up/down handlers, boundary enforcement
- `KeyCommands.vb` — 60m navigation hotkeys
- `Radios/LicenseConfig.cs` — add country setting
- `JJFlexWpf/Dialogs/SettingsDialog.xaml` — country setting in License tab (temporary home until Personal tab)
- `JJFlexWpf/NativeMenuBar.cs` — 60m channel menu items

**Implementation:**

### Channel Table Data Structure

```csharp
public static class SixtyMeterChannels
{
    public record Channel(double FrequencyMHz, string Mode, int MaxPowerW, string Label);
    public record DigiSegment(double StartMHz, double EndMHz, int MaxPowerW);

    // US allocation (FCC Part 97.303(h))
    public static readonly Channel[] USChannels = new[]
    {
        new Channel(5.332, "USB", 100, "Channel 1"),
        new Channel(5.348, "USB", 100, "Channel 2"),
        new Channel(5.358.5, "USB", 100, "Channel 3"),
        new Channel(5.373, "USB", 100, "Channel 4"),
        new Channel(5.405, "USB", 100, "Channel 5"),
    };

    public static readonly DigiSegment USDigiSegment = new(5.3515, 5.3665, 100);

    // Keyed by country code
    public static Dictionary<string, (Channel[] Channels, DigiSegment? Digi)> CountryAllocations;

    public static (Channel[] Channels, DigiSegment? Digi)? GetAllocation(string countryCode);
}
```

### Channel Navigation

- **Alt+Shift+Up/Down** for 60m channel navigation (parallels Alt+Up/Down for band nav — add Shift for channel-within-band):
  - Cycles through 6 stops: Channel 1-5 (channelized) + Digi Segment (5.3515 MHz)
  - Sets mode to USB automatically on channelized frequencies
  - Speaks "Channel 1, 5.332 MHz, USB" on each step; "60 meter digital segment, 5.351.5 MHz" on stop 6
  - Digi segment allows free tuning within 5.3515-5.3665 MHz with boundary tones at edges
  - Wraps around (Digi Segment → Channel 1)

- **60m Digi Segment** hotkey — jumps to 5.3515 MHz, allows free tuning within 5.3515-5.3665 MHz
  - Boundary tones when approaching edges (reuse band boundary earcon)
  - Speech: "Entering 60 meter digital segment" / "Leaving 60 meter digital segment"

### TX Enforcement (US rules)

- **Enforcement is opt-out, not mandatory.** Add a setting (default ON) in Settings > License tab: "Enforce transmit parameters based on published amateur radio rules." When on: USB mode enforcement on channels, 100W power cap, TX lockout outside channels/digi segment. When off: no restrictions, no boundary tones, no TX lockout — the operator takes full responsibility.
- On channelized frequencies (when enforcement on): enforce USB mode, cap TX power at 100W (FCC Part 97.303(h) says 100W ERP — we enforce at transmitter output like every other radio app; antenna gain accounting is the operator's responsibility)
- In digi segment (when enforcement on): allow CW/digital modes, same 100W cap
- Outside channels AND outside digi segment on 60m (when enforcement on): TX lockout (same pattern as license boundary lockout from Sprint 17)
- If country has no rules in table: no enforcement, free tuning, let Flex firmware handle it
- **Future (4O3A):** When amp integration exists, force barefoot on 60m if PGXL or other amp is detected in the chain. Document antenna gain / ERP responsibility in help docs.

### Country Setting

- Add `Country` string property to `LicenseConfig` (default "US")
- Temporary UI: combo box in Settings > License tab with common countries
- Future: proper Personal tab in Settings, GPS auto-detect
- When country changes, reload 60m channel table and enforcement rules

---

## Build and Verification

### After Phase 5 (Tune + ATU):
```batch
dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal
```
Verify Ctrl+T starts ATU cycle, Ctrl+Shift+T toggles tune carrier.

### After Phase 8 (All regressions + startup speech done):
```batch
dotnet clean JJFlexRadio.vbproj -c Release -p:Platform=x64 && dotnet build JJFlexRadio.vbproj -c Release -p:Platform=x64 --verbosity minimal
```
Full smoke test of all regression items.

### After Phase 10 (Sprint complete):
```batch
build-installers.bat
```
Full build, both architectures. Verify installer.

---

## Test Matrix Notes

The Sprint 22 test matrix (to be created at `docs/planning/agile/sprint22-test-matrix.md`) must cover:

**Sprint 21 items (untested, Don seeing for first time):**
- Meter tones: S-meter, ALC, SWR, mic level tones (3 presets, global kill, speech readout)
- Audio Workshop: all 3 tabs, preset save/load/export
- Leader key: Ctrl+J → all bindings (N, B, W, R, S, A, P, M, T, E, F, H/?)
- TX filter sculpting: Ctrl+Shift+[ ] and Ctrl+Alt+[ ]
- ScreenFields TX expansion: mic gain, compander, processor, TX filter, monitor
- CHM help: F1 from various contexts
- App rename: window title, installer, welcome speech all say "JJ Flexible Radio Access"
- Bug fixes: BUG-004 (shutdown crash), BUG-016 (DSP gating), BUG-023 (connect confirmation)

**Sprint 22 items:**
- Phase 1: RigSelector auto-connect button, empty list announce
- Phase 2: About dialog — all 3 tabs, Copy to Clipboard
- Phase 3: Command Finder categories — browse by category, search within category
- Phase 4: Debounce — toggle on/off, adjust ms, verify tuning speech behavior
- Phase 5: ATU Tune (Ctrl+T), Tune toggle (Ctrl+Shift+T), menu items
- Phase 6: Antenna switching — RX/TX antenna selection in menus and ScreenFields
- Phase 7: Slice management — create/release in ScreenFields, enhanced Ctrl+Shift+S
- Phase 8: Startup speech — full status after connect
- Phase 9: Meters panel — all waveforms, add/remove slots, speech fallback, auto-enable on tune, test buttons
- Phase 10: 60m — channel navigation, mode/power enforcement, boundary tones, country setting

**Screen reader matrix:** NVDA and JAWS for all items. Focus order, announce text, braille output for meters speech.

---

## Commit Strategy

Commit after each phase with message format:
```
Sprint 22: Phase N — [brief description]
```

Examples:
- `Sprint 22: Phase 1 — RigSelector auto-connect button`
- `Sprint 22: Phase 5 — Tune button and ATU Tune`
- `Sprint 22: Phase 9 — Meters panel and new waveforms`

Push after every 2-3 phases or whenever a natural milestone is reached.

---

## Deferred to Future Sprints

- **Verbosity settings system** — master sound on/off, master speech on/off, per-category earcon toggles (meter tones, ATU progress, PTT chirps, filter tones, leader key tones), per-event speech toggles (tune speech, status speech, connect speech). For Sprint 22 all sounds and speech are always on.
- **JAWS audit** — dedicated pass through the entire app with JAWS to tune speech timing, interruption behavior, and verbosity. Use Tolk's screen reader detection to adapt speech strategy per screen reader if needed (e.g., shorter announcements or different interrupt flags for JAWS vs NVDA).
- **Braille walkthrough** — full app walkthrough with a braille display. Verify all controls, status bar, ScreenFields, dialogs, menus, and meter speech strings render correctly on the braille line. Check truncation on shorter displays (e.g., 40-cell vs 80-cell).
- **Auto-update check on startup** — check GitHub releases on program load, notify if update available, configurable on/off in Settings
- **Tuner Source selector** — Settings > Tuning tab combo: Internal ATU, Tuner Genius XL, External ATU, Manual, None. Ctrl+T adapts based on source.

## Post-Sprint

After Sprint 22 is tested and verified:
1. Version bump to 4.1.16 (both .vbproj and AssemblyInfo.vb)
2. Write changelog entry for 4.1.16 covering Sprint 21 + Sprint 22
3. Clean build both architectures
4. Tag v4.1.16, push, create GitHub release
5. Archive sprint plan to `docs/planning/agile/archive/`
6. Update Agent.md
