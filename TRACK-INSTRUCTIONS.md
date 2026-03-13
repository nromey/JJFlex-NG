# Sprint 24 Track B: Audio Architecture + About Dialog + Quick Wins

**Branch:** `sprint24/track-b`
**Directory:** `C:\dev\jjflex-24b`
**Sprint plan:** `docs/planning/skywave-negative-sweepstakes.md`

---

## Build Command

After every phase, build the full solution and verify:

```batch
dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64 --verbosity minimal
```

Verify exe timestamp:
```batch
powershell -Command "(Get-Item 'bin\x64\Debug\net8.0-windows\win-x64\JJFlexRadio.exe').LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')"
```

**IMPORTANT:** Close running JJFlexRadio before building (Radios.dll lock).

---

## Commit Strategy

- Commit after each phase: `Sprint 24 Phase 7B: description`
- Every commit must leave the solution building clean
- Push after each commit: `git push origin sprint24/track-b`

---

## Cross-Track Coordination

Check `C:\Users\nrome\.claude\sprint24-coordination.md` at the start of each phase. Append any notes about shared types or interfaces you change. Track A is running simultaneously in `C:\dev\JJFlex-NG`.

---

## File Ownership (DO NOT touch Track A files)

**Track B owns:**
- `JJFlexWpf/EarconPlayer.cs`
- `JJFlexWpf/MeterToneEngine.cs`
- `JJFlexWpf/AudioOutputConfig.cs`
- `JJFlexWpf/Dialogs/SettingsDialog.xaml` and `.cs`
- `JJFlexWpf/Dialogs/AboutDialog.xaml` and `.cs`
- `JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs` (DSP minimums only)
- All 42 dialog XAML files (access key announcements)
- `JJFlexWpf/Resources/About*.html` (new, embedded resources)
- `JJFlexWpf/JJFlexWpf.csproj` (embed resources only)

**Track A owns (DO NOT modify):**
- `Radios/FlexBase.cs`
- `JJFlexWpf/FreqOutHandlers.cs`
- `JJFlexWpf/NativeMenuBar.cs`
- `Radios/RadioStatusBuilder.cs`
- `JJFlexWpf/Dialogs/StatusDialog.xaml` and `.cs` (new, Track A creates)
- `JJFlexWpf/KeyCommands.cs`
- `Radios/KeyCommandTypes.cs`

---

## Phase 7B: Audio Channel Architecture

**Goal:** Split single NAudio WaveOutEvent into separate alert and meter channels with independent volume and device control.

**Current state (from research):**
- `EarconPlayer.cs` (~996 lines): Static class with single persistent `WaveOutEvent` + `MixingSampleProvider` (stereo, 44.1 kHz). Master volume via `VolumeSampleProvider`. Device selection via `SetOutputDevice(int deviceNumber)` where -1 = Windows default. Has `RegisterContinuousTone()` / `UnregisterContinuousTone()` for meter tones.
- `MeterToneEngine.cs` (~563 lines): Static class. Up to 4 slots (expandable to 8). Each slot has a `ContinuousToneSampleProvider` registered with EarconPlayer. `MasterVolume` property (0.0-1.0). Presets: RX Monitor, TX Monitor, Full Monitor.
- `AudioOutputConfig.cs` (~144 lines): XML-serializable. Has `EarconDeviceNumber` (int, -1 default), `MasterEarconVolume` (int 0-100), `MeterMasterVolume` (float 0.0-1.0), plus meter settings. Persisted as `audioConfig.xml`.

**What to build:**

1. **New `AudioChannel` class** (can be inner class in EarconPlayer or separate file):
   - `WaveOutEvent` + `MixingSampleProvider` + `VolumeSampleProvider`
   - `int DeviceNumber` property (-1 = Windows default)
   - `float Volume` property (0.0-1.0)
   - `void Initialize(int deviceNumber)` / `void Dispose()`
   - `void AddMixerInput(ISampleProvider)` / `void RemoveMixerInput(ISampleProvider)`

2. **Refactor EarconPlayer** to use channel dictionary:
   - **Alert channel:** earcons, beeps, PTT tones (all current sounds)
   - **Meter channel:** ContinuousToneSampleProvider instances from MeterToneEngine
   - **Waterfall channel:** placeholder for future (don't build infrastructure, just reserve the name)
   - Master volume multiplier across all channels
   - "Same as Alerts" default: meter channel inherits alert device unless explicitly set
   - Keep the existing public API surface working — `Beep()`, `FeatureOnTone()`, etc. all still work, they just route to the alert channel now
   - `RegisterContinuousTone()` routes to meter channel instead of alert mixer
   - `GetOutputDevices()` still works (shared across channels)

3. **Update MeterToneEngine:**
   - No major changes needed — it already registers with EarconPlayer via `RegisterContinuousTone()`
   - The refactored EarconPlayer will route those registrations to the meter channel automatically
   - Verify `MasterVolume` property still works (may need to set volume on meter channel instead of individual providers)

4. **New `DingTone()` method** in EarconPlayer:
   - Confirmation ding with decay — a clear, pleasant tone that cuts through radio audio
   - Use for frequency entry confirmation (Track A will call this)
   - Model for future decay-style tones
   - Play on alert channel

5. **Update AudioOutputConfig:**
   - Add `MeterDeviceNumber` (int, -1 = "Same as Alerts")
   - Add `AlertVolume` (float 0.0-1.0, replaces the int-based `MasterEarconVolume`)
   - Keep `MasterEarconVolume` for backward compatibility during deserialization but map to new float
   - Add `MasterVolume` (float 0.0-1.0, default 1.0) — scales all channels

**Files:** `JJFlexWpf/EarconPlayer.cs` (major refactor), `JJFlexWpf/MeterToneEngine.cs`, `JJFlexWpf/AudioOutputConfig.cs`

**Verify:** Earcons play on alert channel. Meter tones on meter channel. Volume controls independent. Device selection independent. Master volume scales both. DingTone is audible and pleasant. All existing earcon calls still work (no regression). Build clean.

---

## Phase 8B: Audio Settings Tab + Radio Volume

**Goal:** Consolidate audio settings into a proper Audio tab in SettingsDialog, replacing the current split across Audio and Meter Tones tabs.

**Current state (from research):**
- `SettingsDialog.xaml` (262 lines) / `.cs` (297 lines): 5 tabs — PTT, Tuning, License, Audio, Meter Tones.
- Current Audio tab: earcon device dropdown + master earcon volume slider (0-100).
- Current Meter Tones tab: enable checkbox, preset combo, meter volume slider, Peak Watcher, meter speech.

**What to build:**

1. **Merge and expand the Audio tab** — combine current Audio and Meter Tones tabs into one comprehensive Audio tab:
   - **Master Volume** slider (0-100) — scales all audio channels
   - **Alert section:**
     - Alert Volume slider (0-100)
     - Alert Device dropdown (list of audio output devices)
   - **Meter section:**
     - Meter Volume slider (0-100)
     - Meter Device dropdown (with "Same as Alerts" as first option)
     - Enable Meter Tones checkbox
     - Meter Preset combo (RX Monitor / TX Monitor / Full Monitor)
     - Peak Watcher checkbox
     - Meter Speech checkbox
   - **Radio Volume** — wrapping FlexLib audio gain (the radio's DAX/speaker output volume)
   - All controls with `AutomationProperties.Name` and proper tab order

2. **Remove the old "Meter Tones" tab** — all its content moves into Audio tab

3. **"Audio Workshop..." button** — launches AudioWorkshopDialog directly from the Audio tab (second path alongside menu)

4. **Persist via AudioOutputConfig** — use the new properties added in Phase 7B

**XAML layout pattern:** Follow existing SettingsDialog tab structure. Use `StackPanel` with labeled sections. Each slider gets a value label TextBlock that updates on ValueChanged. Dropdowns use `ComboBox` with `AutomationProperties.Name`.

**Files:** `JJFlexWpf/Dialogs/SettingsDialog.xaml`, `SettingsDialog.xaml.cs`, `JJFlexWpf/AudioOutputConfig.cs`

**Verify:** Audio tab reachable via Tab key. All sliders announce values to screen reader. Device dropdowns list available devices. "Same as Alerts" works for meter device. Changes persist across restart. Build clean.

---

## Phase 9B: About Dialog WebView2 Upgrade

**Goal:** Replace ListBox content with WebView2 for heading-based screen reader navigation.

**Current state (from research):**
- `AboutDialog.xaml` (80 lines) / `.cs` (348 lines): Tab-based dialog inheriting JJFlexDialog. 4 tabs: About, Radio, System, Diagnostics. Uses ListBox for content — each line is a ListBoxItem. Has `PopulateListBox()` helper and `ListBoxToText()` for clipboard.
- WebView2 is already a dependency: `Microsoft.Web.WebView2` version 1.0.2478.35 in `JJFlexWpf.csproj`.
- Buttons: Copy to Clipboard, Export Diagnostic Report, Close, Check for Updates.

**What to build:**

1. **Replace 4 ListBox controls with a single WebView2 control:**
   - `Microsoft.Web.WebView2.Wpf.WebView2` in XAML
   - Tab selector loads the appropriate HTML template and pushes via `NavigateToString()`
   - Async init: `EnsureCoreWebView2Async()` with loading indicator

2. **HTML templates as embedded resources:**
   - Create `.html` files in `JJFlexWpf/Resources/`: `AboutGeneral.html`, `AboutRadio.html`, `AboutSystem.html`, `AboutDiagnostics.html`
   - Mark as embedded resources in `JJFlexWpf.csproj` (add `<EmbeddedResource Include="Resources\About*.html" />`)
   - Load via `Assembly.GetManifestResourceStream()` at runtime
   - Use `{{placeholder}}` tokens: `{{Version}}`, `{{BuildDate}}`, `{{RadioModel}}`, `{{DotNetVersion}}`, etc.
   - Replace tokens at load time before pushing to WebView2

3. **HTML structure for accessibility:**
   - Use `<h2>` headings for section headers — screen reader navigates with H key
   - Use `<p>` for content paragraphs
   - Simple, clean styling (dark background, light text to match app theme, or let WebView2 handle system theme)
   - No complex CSS — keep it minimal and readable

4. **Keep existing functionality:**
   - Per-tab Copy to Clipboard (plain text version — extract text from HTML or maintain parallel text)
   - Copy All to Clipboard
   - Homepage link opens default browser (`CoreWebView2.NewWindowRequested` to intercept and use `Process.Start()`)
   - Check for Updates (already implemented, keep working — uses GitHub API)
   - Export Diagnostic Report

5. **WebView2 security:**
   - Disable JavaScript if not needed: `CoreWebView2Settings.IsScriptEnabled = false`
   - Disable dev tools: `CoreWebView2Settings.AreDevToolsEnabled = false`
   - Intercept navigation to prevent browsing away: `NavigationStarting` event

**Files:** `JJFlexWpf/Dialogs/AboutDialog.xaml`, `AboutDialog.xaml.cs`, `JJFlexWpf/Resources/AboutGeneral.html` (new), `AboutRadio.html` (new), `AboutSystem.html` (new), `AboutDiagnostics.html` (new), `JJFlexWpf/JJFlexWpf.csproj`

**Verify:** Dialog opens. WebView2 shows content. H key navigates headings in NVDA. Tab switching loads correct content. Copy buttons work. Update check works. Links open in browser. Build clean.

---

## Phase 10B: Quick Wins

**Goal:** DSP level minimums and access key announcements.

**Current state (from research):**
- `ScreenFieldsPanel.xaml.cs` (~996 lines): DSP controls created with `MakeValue()`. Current minimums:
  - NR Level: `MakeValue("NR Level", 0, 15, 1)` — min is 0
  - NB Level: `MakeValue("NB Level", 0, 100, 5)` — min is 0
  - WNB Level: `MakeValue("WNB Level", 0, 100, 5)` — min is 0

### DSP level minimums 0 to 1:
1. Change NR Level minimum from 0 to 1: `MakeValue("NR Level", 1, 15, 1)`
2. Change NB Level minimum from 0 to 1: `MakeValue("NB Level", 1, 100, 5)`
3. Change WNB Level minimum from 0 to 1: `MakeValue("WNB Level", 1, 100, 5)`
   - Level 0 kills all audio (NR) or means blanker is on but doing nothing (NB/WNB) — confusing for users
   - When the feature is toggled on, level should always be at least 1

### Access key announcements for screen readers:
4. Set `AutomationProperties.AcceleratorKey` on all buttons with access keys across 42 dialogs
   - Mini Sprint 24a added access keys (underlined `_` prefix in Content) to all 42 dialogs
   - But screen readers don't automatically announce the shortcut when tabbing to the button
   - `AutomationProperties.AcceleratorKey="Alt+C"` makes NVDA/JAWS announce the shortcut
   - Example: `<Button Content="_Connect" AutomationProperties.AcceleratorKey="Alt+C" />`
   - For each button with an access key (`_X` prefix), add the matching `AutomationProperties.AcceleratorKey="Alt+X"`

**Finding all dialog files with access keys:**
- Search all `.xaml` files in `JJFlexWpf/Dialogs/` for `Content="_` pattern
- Each match needs a corresponding `AutomationProperties.AcceleratorKey` attribute
- Also check `JJFlexWpf/Controls/` for any user controls with access-keyed buttons

**Files:** `JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs`, 42 dialog XAML files in `JJFlexWpf/Dialogs/`

**Verify:** NR, NB, and WNB level sliders won't go below 1 (arrow down from 1 stays at 1). Tabbing to buttons with access keys announces the shortcut in NVDA (e.g., "Connect button, Alt+C"). Build clean.

---

## Architecture Notes

- **Speech calls:** Use `ScreenReaderOutput.Speak(text, VerbosityLevel, interrupt)` — the verbosity engine from Phase 6 filters by level. Use Critical for errors, Terse for state changes, Chatty for hints.
- **Earcon public API:** After refactoring, keep the same public method signatures: `Beep()`, `FeatureOnTone()`, `FeatureOffTone()`, `ConfirmTone()`, etc. Internal routing changes to channels, but callers don't need to know.
- **AudioOutputConfig persistence:** XML serialization. Add new properties with defaults so existing `audioConfig.xml` files deserialize without error (missing elements get defaults).
- **WebView2 init pattern:** See `AuthFormWebView2.cs` for the existing WebView2 initialization pattern used in the app. Use `EnsureCoreWebView2Async()` in `Loaded` event handler.
- **Embedded resources:** In `.csproj`, use `<EmbeddedResource Include="Resources\About*.html" />`. Load with `typeof(AboutDialog).Assembly.GetManifestResourceStream("JJFlexWpf.Resources.AboutGeneral.html")` — note the namespace-dot-path convention.
- **JJFlexDialog base class:** Provides Escape close, dialog earcon, modality. All dialogs inherit from it.
- **SettingsDialog tab pattern:** Each tab is a `TabItem` with a `StackPanel` inside. Sliders use `Slider` + `TextBlock` for value display. Combos use `ComboBox`. All need `AutomationProperties.Name`.
- **No tables in speech output.** Noel uses a screen reader — plain prose or bulleted text only.
