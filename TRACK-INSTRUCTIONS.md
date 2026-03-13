# Sprint 24 Track A: Slices + Status Dialog

**Branch:** `sprint24/track-a`
**Directory:** `C:\dev\JJFlex-NG`
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

- Commit after each phase: `Sprint 24 Phase 7A: description`
- Every commit must leave the solution building clean
- Push after each commit: `git push origin sprint24/track-a`

---

## Cross-Track Coordination

Check `C:\Users\nrome\.claude\sprint24-coordination.md` at the start of each phase. Append any notes about shared types or interfaces you change. Track B is running simultaneously in `C:\dev\jjflex-24b`.

---

## File Ownership (DO NOT touch Track B files)

**Track A owns:**
- `Radios/FlexBase.cs`
- `JJFlexWpf/FreqOutHandlers.cs`
- `JJFlexWpf/NativeMenuBar.cs`
- `Radios/RadioStatusBuilder.cs`
- `JJFlexWpf/Dialogs/StatusDialog.xaml` (new)
- `JJFlexWpf/Dialogs/StatusDialog.xaml.cs` (new)
- `JJFlexWpf/KeyCommands.cs` (slice hotkeys only — new entries)
- `Radios/KeyCommandTypes.cs` (new CommandValues only — append to enum)

**Track B owns (DO NOT modify):**
- `JJFlexWpf/EarconPlayer.cs`
- `JJFlexWpf/MeterToneEngine.cs`
- `JJFlexWpf/AudioOutputConfig.cs`
- `JJFlexWpf/Dialogs/SettingsDialog.xaml` and `.cs`
- `JJFlexWpf/Dialogs/AboutDialog.xaml` and `.cs`
- `JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs` (DSP minimums)
- All 42 dialog XAML files (access key announcements)

---

## Phase 7A: BUG-049 Fix + Slice Count Tracking

**Goal:** Fix RemoveSlice not updating MyNumSlices, causing false "Maximum slices reached."

**Current state (from research):**
- `FlexBase.cs` (~7,263 lines): `MyNumSlices` returns count of `mySlices` list (thread-locked). `RemoveSlice(int id)` removes a slice and its panadapter. `NewSlice()` uses async queue with SpinWait.
- `NativeMenuBar.cs` (~1,480 lines): Slice menu built in `BuildMenuBar()` lines 789-853. Shows "Slice (N of M)" with selection submenu. `RebuildCurrentMenu()` rebuilds via `ApplyUIMode()`.
- `FreqOutHandlers.cs` (~2,000+ lines): Comma key releases slice. `CycleVFO(direction)` cycles slices.

**What to build:**
1. In `FlexBase.cs`: Trace the async gap in RemoveSlice — the SliceRemoved event may fire after the next NewSlice capacity check. Add wait-for-confirmation after RemoveSlice (similar to AddSlice's existing SpinWait pattern). Verify `mySlices` list is properly synchronized.
2. In `NativeMenuBar.cs`: Trigger menu rebuild on slice add/remove events so slice count in menu label stays accurate. Wire `SliceAdded` and `SliceRemoved` FlexLib events to `RebuildCurrentMenu()`.
3. Improve creation/release speech: "Slice B created, 2 active" / "Slice B released, 1 active" — update speech in both FreqOutHandlers (comma/period keys) and NativeMenuBar (menu items).

**Verify:** Create/remove slices repeatedly. Menu updates correctly. Count is accurate. No false "Maximum slices reached."

---

## Phase 8A: Slice Selector + Operations + Frequency Field Improvements

**Goal:** New FreqOut fields for slice selection and per-slice operations, plus frequency field behavior fixes for both tuning modes.

**Current state (from research):**
- FreqOutHandlers has `AdjustSlice()` which handles Space (cycle), M (mute), T (transmit), digits 0-9 and letters A-H for direct selection, period/comma for create/release, arrows for gain/pan.
- `CycleVFO(direction)` cycles through valid slices, speaks letter and owner.
- KeyCommandTypes.cs has `CommandValues` enum (~126 values). No `MuteSlice` yet.

**What to build:**

### Slice work:
1. **Slice Selector field:** up/down cycles slices with speech "Slice B, 14.250 USB, yours"
2. **Slice Operations field:** per-slice volume, pan, mute controls
3. **Shift+M hotkey:** mute/unmute current slice with earcon + speech (Radio scope)
   - Add `MuteSlice` to `CommandValues` enum in `KeyCommandTypes.cs`
   - Add handler in `KeyCommands.cs` — toggle `slice.Mute`, play FeatureOnTone/FeatureOffTone, speak "Slice A muted"/"Slice A unmuted"
   - Wire in key table with `KeyScope.Radio`, default key `Keys.Shift | Keys.M`

### Modern mode frequency field — read-only navigation:
4. When arrowing to the frequency field in modern tuning mode, speak the frequency but DO NOT allow up/down arrows to change it
5. Prevents accidental frequency nudging — modern mode tunes using up/down arrow with coarse/fine tuning steps (not digit-position MHz/kHz)
6. Classic mode continues to work as before (up/down arrows change freq by digit position)

### Quick-type frequency entry (both tuning modes):
7. If digits are typed rapidly (each keystroke within ~1 second of the last), accumulate as a frequency entry
8. Accept formats: `3525` (kHz implied) or `3.525` (MHz implied — decimal distinguishes)
9. After typing pause (>1 second with no new digit), speak confirmation: "Change frequency to 3.525 MHz? Press Enter to confirm"
10. Enter commits the frequency change, Escape cancels and restores previous
11. Works in both classic and modern tuning modes when focus is on a frequency field

**Files:** `JJFlexWpf/FreqOutHandlers.cs`, `JJFlexWpf/KeyCommands.cs`, `Radios/KeyCommandTypes.cs`, `Radios/FlexBase.cs`

**Verify:** Slice selector cycles correctly. Shift+M toggles mute with earcon. Volume/pan adjust per-slice. Modern mode frequency field is read-only (arrows don't change freq by digit position). Classic mode still tunes by kHz/MHz fields as before. Quick-type entry works in both modes: type `7125`, hear confirmation, Enter commits. Escape cancels. Decimal format (`7.125`) also works.

---

## Phase 9A: Status Dialog Rebuild

**Goal:** Accessible WPF dialog showing full radio state snapshot.

**Current state (from research):**
- `RadioStatusBuilder.cs` (~202 lines): Has `BuildFullSliceStatus()`, `BuildSpokenStatus()`, `BuildDetailedStatus()` returning `RadioStatusSnapshot`. Formats frequency, mode, band, slice ownership.
- No StatusDialog exists yet — this is new.
- `MeterToneEngine.cs` has `GetMeterSpeechSummary()` — builds text summary of active meters.
- Proven accessible pattern: ListBox-based layout (AboutDialog uses this pattern successfully).

**What to build:**
1. **New `StatusDialog.xaml` + `.cs`** in `JJFlexWpf/Dialogs/`:
   - Inherits `JJFlexDialog` (base class handles Escape close, dialog earcon, modality)
   - ListBox-based layout (proven accessible pattern from AboutDialog)
   - Sections: Radio info, Active slices (freq/mode/volume each), Meters (S, ALC, SWR, Power), TX state, ATU, Antennas
   - Uses `RadioStatusBuilder.BuildDetailedStatus()` for radio/slice info
   - Uses `MeterToneEngine.GetMeterSpeechSummary()` for meter section
   - Respects verbosity (terse = fewer details, chatty = full details)
   - Copy to Clipboard button
   - Auto-refresh timer (2 seconds) — repopulate ListBox items
   - Escape closes (inherited from JJFlexDialog)
   - Shows "Not connected" gracefully when no radio

2. **Repurpose Ctrl+Alt+S:**
   - In `KeyCommands.cs`: Change `ShowStatusDialog` handler from disabled placeholder to launch StatusDialog
   - The key binding already exists in the key table as `ShowStatusDialog` (previously mapped to StartScan, repurposed)
   - Remove any "disabled" placeholder speech

3. **WPF dialog modality pattern:**
   - Use `WindowInteropHelper` with `Process.MainWindowHandle` for owner (Application.Current.MainWindow is null in ElementHost)
   - Same pattern used by all other dialogs in the app

**Files:** `JJFlexWpf/Dialogs/StatusDialog.xaml` (new), `JJFlexWpf/Dialogs/StatusDialog.xaml.cs` (new), `JJFlexWpf/KeyCommands.cs`, `Radios/RadioStatusBuilder.cs`

**Verify:** Ctrl+Alt+S opens dialog. Screen reader reads sections line-by-line. Copy works. Escape closes. Shows "Not connected" gracefully. Auto-refresh updates every 2 seconds.

---

## Phase 10A: Don's A/B Switching Investigation

**Goal:** Investigate and fix Don's intermittent A/B switching issues.

**Current state (from research):**
- VFO switching goes through `FlexBase.RXVFO` setter and `FreqOutHandlers.CycleVFO(direction)`
- `CycleVFO` uses `Rig.RXVFO`, `Rig.MyNumSlices`, `Rig.ValidVFO()`, speaks slice letter and owner on change
- `FlexBase.VFOToSlice(int vfo)` converts VFO index to Slice object (thread-locked, returns null if invalid)

**What to build:**
1. Add trace logging around VFO switching:
   - In `FlexBase.cs`: log RXVFO set (old → new), ValidVFO checks, VFOToSlice results
   - In `FreqOutHandlers.cs`: log CycleVFO direction, current state, target state
2. If reproducible: fix root cause
3. If not reproducible: leave trace hooks for Don's next session, document investigation
4. The Phase 7A slice count fix may resolve this indirectly — if slice indices become invalid after remove, A/B switching breaks

**Files:** `Radios/FlexBase.cs`, `JJFlexWpf/FreqOutHandlers.cs`

**Verify:** A/B switching works on single and multi-slice radios. No regression in VFO cycling.

---

## Architecture Notes

- **Speech calls:** Use `ScreenReaderOutput.Speak(text, VerbosityLevel, interrupt)` — the verbosity engine from Phase 6 filters by level. Use Critical for errors, Terse for state changes, Chatty for hints.
- **Earcon calls:** Call `EarconPlayer.FeatureOnTone()` / `FeatureOffTone()` for toggles. Track B may refactor EarconPlayer internally but the public API will remain the same.
- **Key table entries:** Follow the existing pattern in `KeyCommands.cs` `BuildKeyTable()`. Each entry needs: CommandValues id, default Keys binding, KeyScope, handler Action, help text, optional menu text, FunctionGroup, keywords.
- **ListBox accessible pattern:** See `AboutDialog.xaml.cs` `PopulateListBox()` for the proven pattern — split text into lines, add as ListBoxItems, screen reader reads each item with arrow keys.
- **Dialog base class:** `JJFlexDialog` provides Escape close, dialog earcon, and base behavior. Inherit from it for StatusDialog.
- **No tables in speech output.** Use plain prose or bulleted text. Noel uses a screen reader.
