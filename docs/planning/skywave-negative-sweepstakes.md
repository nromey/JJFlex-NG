# Sprint 24: Skywave Negative Sweepstakes — The Leviathan Sprint

**Theme:** Modernization + Speech Control. Migrate the last major VB.NET component (KeyCommands) to C#, add user-controlled speech verbosity, rework slices, redesign audio architecture, rebuild the status dialog, upgrade About dialog to WebView2, and fix the Audio Workshop.

**Sprint Goal:** Complete the VB-to-C# migration of the hotkey system, give users control over how much the app talks, significantly improve the slice and audio experience, and fix persistent dialog issues.

**Version:** 4.1.16 (bump after sprint completion or after fix sprint completes)

---

## Structure: Serial Foundation + 2 Parallel Tracks + Serial Finishing

- Phases 1-6: Serial foundation (Key Migration + Verbosity Engine) — must be first, everything else builds on these
- Phases 7-10: Two parallel tracks (Slices+Status vs Audio+About+QuickWins)
- Phase 11: Merge + Integration + 60m advisory (serial)
- Phase 12: Audio Workshop — dedicated investigation-first phase (serial, after merge)
- Phase 13: Cleanup, builds, test matrix, changelog

**Build rule:** Full solution build after every phase (`dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64`). Verify exe timestamp.

**Commit strategy:** Commit early, commit often. Large phases (especially Phase 3 and Phase 6) break into multiple commits by category. Every commit must leave the solution in a building state.

- Phase 3 (126 handlers): commit per handler category
- Phase 6 (211 Speak() calls): commit per file or file group
- All other phases: commit per phase as usual

**Commit style:** `Sprint 24 Phase N: brief description`

---

## PHASE 1: Key Migration — Extract Shared Types to Radios Project

**Goal:** Move enums and data classes out of KeyCommands.vb into a shared C# file that both VB and C# projects can reference.

**What gets built:**
- New file `Radios/KeyCommandTypes.cs` containing:
  - `CommandValues` enum (126 values)
  - `KeyScope` enum (Global, Radio, Classic, Modern, Logging)
  - `KeyTypes` enum (flags)
  - `FunctionGroups` enum
  - `KeyDefType` class (key binding definition with XML serialization)
  - `KeyTableEntry` class (renamed from VB `keyTbl` — PascalCase for C#)
  - `KeyConfigType_V1` class (persistence format, version 5)
- Verify `Radios.csproj` has `<UseWindowsForms>true</UseWindowsForms>` (needed for System.Windows.Forms.Keys)

**Files modified:** `Radios/KeyCommandTypes.cs` (new), possibly `Radios/Radios.csproj`

**Verify:** Radios project builds alone; full solution builds (VB can see types via `Imports Radios`)

---

## PHASE 2: Key Migration — Create KeyCommands.cs Skeleton in JJFlexWpf

**Goal:** Create the C# KeyCommands class with all infrastructure (dispatch, lookup, leader key, config load/save) but NO handler methods yet.

**Why JJFlexWpf?** SDK-style projects can't mix VB and C# in one project. JJFlexWpf already has access to Radios, EarconPlayer, MeterToneEngine, MainWindow — everything handlers need.

**What gets built:**
- `JJFlexWpf/KeyCommands.cs` — the class shell:
  - Constructor with `setupData()`, config file load, key table initialization
  - `KeyDictionary` / `KeydefDictionary` (lookup structures)
  - `DoCommand(Keys)` — main dispatch entry point
  - `DoLeaderCommand(Keys)` — leader key second-press dispatch
  - `ScopeMatchesMode()` — scope-aware filtering (uses injected ActiveUIMode)
  - `lookup()` overloads, `AllKeyDictionaryEntries()`
  - Config persistence: `write()`, `SmartMergeDefaults()`, `murgeNewDefaults()`
  - Static helpers: `getKeyFromTypename()`, `KeyConfigVersion`
- `JJFlexWpf/KeyCommandContext.cs` (new) — context interface for VB globals:
  - `Func<FlexBase?>` for RigControl
  - `Func<bool>` for Power
  - `Func<UIMode>` for ActiveUIMode
  - `Func<MainWindow>` for WpfMainWindow
  - `Action<string>` for trace output
  - Other globals needed by handlers (scan, CurrentOp, PCAudio, etc.)

**Files modified:** `JJFlexWpf/KeyCommands.cs` (new), `JJFlexWpf/KeyCommandContext.cs` (new)

**Verify:** JJFlexWpf builds; full solution builds (old VB KeyCommands still exists — no namespace conflict since different assemblies)

---

## PHASE 3: Key Migration — Port All 126 Handler Methods

**Goal:** Convert all handler routines from VB to C#. This is the mechanical bulk — 2,000+ lines of VB-to-C# translation.

**Conversion patterns:**
- `AddressOf foo` becomes method group or lambda
- `RigControl` becomes `_context.GetRigControl()`
- `WpfMainWindow` becomes `_context.GetMainWindow()`
- `Power` / `ActiveUIMode` / etc. through context delegates
- `vbNullString` becomes `string.Empty`
- `OrElse`/`AndAlso` become `||`/`&&`
- `CType(x, Integer)` becomes `(int)x`
- `Is Nothing` / `IsNot Nothing` become `== null` / `!= null`

**Handler categories (all go into KeyCommands.cs) — commit per category:**
- Leader key DSP toggles (N, B, W, R, S, A, P)
- TX filter handlers
- Navigation (freq entry, scan, receive/send mode)
- Logging (20+ field handlers)
- Band jumps (160m through 6m, plus up/down)
- Mode handlers (next, prev, USB, LSB, CW)
- Audio handlers (gain, headphones, lineout)
- Feature toggles (meter tones, presets, meter speech)
- Dialog launchers
- Status handlers
- Screen fields expander toggles
- Cluster handlers
- CW message handlers

**Files modified:** `JJFlexWpf/KeyCommands.cs` (bulk of handlers added)

**Verify:** JJFlexWpf builds; full solution builds

---

## PHASE 4: Key Migration — Wire Up and Cut Over

**Goal:** Remove old VB KeyCommands.vb, point everything to the new C# version, wire context delegates.

**What gets built:**
- Update `ApplicationEvents.vb`: construct `JJFlexWpf.KeyCommands(context)` with delegates wired to VB globals
- Update `globals.vb`: change `Commands` type to `JJFlexWpf.KeyCommands`
- Update VB files that reference old types: `DefineCommands.vb`, `LogEntry.vb`, `ShowHelp.vb` — point to `Radios.KeyScope`, `Radios.KeyTableEntry`, etc.
- Update C# files: `DefineCommandsDialog.xaml.cs`, `CWMessageAddDialog.xaml.cs`, `LogEntryDialog.xaml.cs`, `LogField.cs` — namespace changes
- Delete `KeyCommands.vb`

**Key editor cleanup:**
- Delete `SetupKeysDialog.xaml` + `.cs` (superseded by DefineCommandsDialog — no scopes, no conflict detection)
- Rewire `ShowKeysDialog` Edit button to open `DefineCommandsDialog` instead of `SetupKeysDialog`
- Collapse three Help menu entries ("Key Assignments", "Key Assignments (Alphabetical)", "Key Assignments (By Function)") into one "Show Key Assignments" (ShowKeysDialog already has sort options internally)
- Add new Help menu entry "Edit Key Assignments" → opens `DefineCommandsDialog` directly
- Two paths to the editor: Help → Edit Key Assignments (direct), or Help → Show Key Assignments → Edit button (browse first, then edit)

**Files modified:** `ApplicationEvents.vb`, `globals.vb`, `DefineCommands.vb`, `LogEntry.vb`, `ShowHelp.vb`, 4 C# dialog/lib files, `NativeMenuBar.cs`, `ShowKeysDialog.xaml.cs`, delete `KeyCommands.vb`, delete `SetupKeysDialog.xaml` + `.cs`

**Verify:** Full solution builds 0 errors. Run debug build: F1 help works, Ctrl+J leader key works, band jumps work, mode switching works, KeyDefs.xml loads correctly (existing user config not lost). Help menu shows "Edit Key Assignments" and "Show Key Assignments". Both paths reach DefineCommandsDialog. ShowKeysDialog Edit button opens DefineCommandsDialog.

---

## PHASE 5: Key Migration — Conflict Audit and Scope Cleanup

**Goal:** Systematic audit of all 126 commands across 5 scopes.

**What gets built:**
- `ValidateKeyBindings()` method that checks for duplicate key+scope combos"
- Review all scope assignments: logging commands in Logging scope, radio-only in Radio, universal in Global
- Document and resolve any conflicts found
- Verify leader key commands don't shadow top-level keys
- Special care should be taken to determine during validation and auditing of 
	* keys, that we don't have repeat key assignments etc.
**Files modified:** `JJFlexWpf/KeyCommands.cs` 

**Verify:** Validation runs clean at startup (trace output). Full build.

---

## PHASE 6: Verbosity Engine

**Goal:** Add speech verbosity control (Off/Terse/Chatty), tones toggle, and tag all 211 existing Speak() calls.

### Step 6a — Core engine

Add to `ScreenReaderOutput.cs`:
- `VerbosityLevel` enum: Critical (always), Terse, Chatty
- `CurrentVerbosity` property (default: Chatty)
- `TonesEnabled` property (default: true)
- New overload: `Speak(string, VerbosityLevel, bool interrupt)` — filters by level
- Existing `Speak(string, bool)` defaults to Chatty (backward compatible)
- `CycleVerbosity()` / `ToggleTones()` methods

Persist in `AudioOutputConfig.cs`

### Step 6b — Tag all 211 Speak() calls (commit per file or file group)

- Critical (~30 calls): connection status, errors, PTT safety, "No radio connected"
- Terse (~120 calls): feature toggles ("NR on"), frequency readback, band/mode changes, meter values
- Chatty (~60 calls): hints ("arrows to change"), step-by-step context, supplementary info

Files to tag (by call count): FreqOutHandlers (61), KeyCommands (41), MainWindow (33), AudioWorkshop (18), ScreenFieldsPanel (16), ValueFieldControl (10), PttSafetyController (10), globals (10), FlexBase (8), plus 12 others

### Step 6c — Hotkeys

- Add `CycleVerbosity` and `ToggleTones` to `CommandValues` enum
- Verbosity cycle: Ctrl+Shift+V (verified unbound)
- Tones toggle: leader key T (Ctrl+J then T)

### Step 6d — Terse message variants where current messages are too verbose

- "Legacy Noise Reduction on" becomes "NR on" at Terse
- "Meter preset: RX Monitor" becomes "RX Monitor" at Terse
- etc.

**Files modified:** `Radios/ScreenReaderOutput.cs`, `JJFlexWpf/AudioOutputConfig.cs`, `JJFlexWpf/KeyCommands.cs`, `Radios/KeyCommandTypes.cs`, all 23 files with Speak() calls

**Verify:** Default Chatty = no behavior change (zero regression). Cycle through Off/Terse/Chatty. Tones toggle works. Settings persist across restart.

---

## PARALLEL TRACKS BEGIN AFTER PHASE 6

After Phase 6 committed to main, create branches and worktrees:
- Track A: `sprint24/track-a` in `C:\dev\JJFlex-NG` (main repo)
- Track B: `sprint24/track-b` in `C:\dev\jjflex-24b` (worktree)

**Worktree creation:**
```
git checkout -b sprint24/track-a
git worktree add ../jjflex-24b sprint24/track-a -b sprint24/track-b
```

**File ownership (zero overlap):**
- Track A owns: FlexBase.cs, FreqOutHandlers.cs, NativeMenuBar.cs, RadioStatusBuilder.cs, new StatusDialog, KeyCommands.cs (slice hotkeys), KeyCommandTypes.cs (new commands)
- Track B owns: EarconPlayer.cs, MeterToneEngine.cs, AudioOutputConfig.cs, SettingsDialog, AboutDialog, AudioWorkshopDialog, FilterPresets.cs

**Merge plan:** Tracks merge in any order as they complete (no overlap = no conflict risk). Track A is merge target; Track B merges into Track A. Then Track A merges into main.

**Cross-track coordination file:** `C:\Users\nrome\.claude\sprint24-coordination.md` — append-only, check at start of each phase.

---

## PHASE 7A (Track A): BUG-049 Fix + Slice Count Tracking

**Goal:** Fix RemoveSlice not updating MyNumSlices, causing false "Maximum slices reached."

**What gets built:**
- Trace the async gap: RemoveSlice calls FlexLib Close() but the SliceRemoved event may fire after the next NewSlice capacity check
- Add wait-for-confirmation after RemoveSlice (similar to AddSlice's existing SpinWait pattern)
- Fix stale slice menu in NativeMenuBar: trigger menu rebuild on slice add/remove events
- Improve creation/release speech: "Slice B created, 2 active" / "Slice B released, 1 active"

**Files:** `Radios/FlexBase.cs`, `JJFlexWpf/NativeMenuBar.cs`

**Verify:** Create/remove slices repeatedly. Menu updates correctly. Count is accurate.

---

## PHASE 8A (Track A): Slice Selector + Operations + Frequency Field Improvements

**Goal:** New FreqOut fields for slice selection and per-slice operations, plus frequency field behavior fixes for both tuning modes.

**What gets built:**

**Slice work:**
- Slice Selector field: up/down cycles slices, speech "Slice B, 14.250 USB, yours"
- Slice Operations field: per-slice volume, pan, mute
- JJ Shift+M hotkey: mute/unmute current slice with earcon + speech (Radio scope)
- Add `MuteSlice` to CommandValues, wire in KeyCommands.cs

**Modern mode frequency field — read-only navigation:**
- When arrowing to the frequency field in modern tuning mode, speak the frequency but do NOT allow up/down arrows to change it based on mHZ, kHZ, etc. 
- Prevents accidental frequency nudging when the user is just navigating between fields
- Frequency field becomes informational in modern mode (read-only display, thus reminding the user that modern mode tunes using the up or down arrow while using default or user provided coarse and fine tuning steps)
- Classic mode operates as it did in versions prior to version 4 or prior to WPF implementation.

**Quick-type frequency entry (both tuning modes):**
- If digits are typed rapidly (each keystroke within ~1 second of the last), accumulate as a frequency entry
- Accept formats: `3525` (kHz implied) or `3.525` (MHz implied — presence of decimal distinguishes)
- After typing pause (>1 second with no new digit), speak confirmation: "Change frequency to 3.525 MHz? Press Enter to confirm"
- Enter commits the frequency change, Escape cancels and restores previous
- Works in both classic and modern tuning modes when focus is on a frequency field

**Files:** `JJFlexWpf/FreqOutHandlers.cs`, `JJFlexWpf/KeyCommands.cs`, `Radios/KeyCommandTypes.cs`, `Radios/FlexBase.cs`

**Verify:** Slice selector cycles correctly. JJ Shift+M toggles mute with earcon. Volume/pan adjust per-slice. Modern mode frequency field is read-only (arrows don't change freq by digit position). Classic mode still tunes by kHz/MHz fields as before. Quick-type entry works in both modes: type `7125`, hear confirmation, Enter commits. Escape cancels. Decimal format (`7.125`) also works.

---

## PHASE 9A (Track A): Status Dialog Rebuild

**Goal:** Accessible WPF dialog showing full radio state snapshot.

**What gets built:**
- New `StatusDialog.xaml` + `.cs`:
  - ListBox-based layout (proven accessible pattern)
  - Sections: Radio info, Active slices (freq/mode/volume each), Meters (S, ALC, SWR, Power), TX state, ATU, Antennas
  - Uses `RadioStatusBuilder.BuildDetailedStatus()` + `MeterToneEngine.GetMeterSpeechSummary()`
  - Respects verbosity (terse/chatty display)
  - Copy to Clipboard button
  - Auto-refresh timer (2 seconds)
  - Escape closes
- Repurpose Ctrl+Alt+S from StartScan to Status Dialog (scan still accessible via menu)
- Remove "disabled" placeholder speech

**Files:** `JJFlexWpf/Dialogs/StatusDialog.xaml` (new), `.cs` (new), `JJFlexWpf/KeyCommands.cs`, `Radios/RadioStatusBuilder.cs`

**Verify:** Ctrl+Alt+S opens dialog. Screen reader reads sections line-by-line. Copy works. Escape closes. Shows "Not connected" gracefully.

---

## PHASE 10A (Track A): Don's A/B Switching Investigation

**Goal:** Investigate and fix Don's intermittent A/B switching issues.

**What gets built:**
- Add trace logging around VFO switching in FreqOutHandlers and FlexBase
- If reproducible: fix root cause
- If not: leave trace hooks for Don's next session, document investigation, trace can be removed if new slice layout fixes the problem.

**Files:** `Radios/FlexBase.cs`, `JJFlexWpf/FreqOutHandlers.cs`

**Verify:** A/B switching works on single and multi-slice radios. No regression in VFO cycling.

---

## PHASE 7B (Track B): Audio Channel Architecture

**Goal:** Split single NAudio WaveOutEvent into separate alert and meter channels.

**What gets built:**
- New `AudioChannel` class: WaveOutEvent + MixingSampleProvider + VolumeSampleProvider + device number
- Refactor EarconPlayer: replace single mixer with channel dictionary
  - Alert channel: earcons, beeps, PTT tones (everything current)
  - Meter channel: ContinuousToneSampleProvider instances from MeterToneEngine
  - Waterfall channel: placeholder (future)
- Master volume multiplier across all channels
- "Same as Alerts" default: other channels inherit alert device unless explicitly set
- Update MeterToneEngine to register with meter channel
- New `DingTone()` — confirmation ding with decay, cuts through radio audio better than the current click. Use for frequency entry confirmation (JJ Ctrl+F and quick-type). Model for future decay-style tones on enable/disable toggles.

**Files:** `JJFlexWpf/EarconPlayer.cs` (major refactor), `JJFlexWpf/MeterToneEngine.cs`, `JJFlexWpf/AudioOutputConfig.cs`

**Verify:** Earcons play on alert channel. Meter tones on meter channel. Volume controls independent. Device selection independent. Master volume scales both. Ding tone audible over radio audio.

---

## PHASE 8B (Track B): Audio Settings Tab + Radio Volume

**Goal:** Settings dialog Audio tab with all volume/device controls.

**What gets built:**
- New "Audio" tab in SettingsDialog:
  - Master Volume slider (0-100)
  - Alert Volume + Alert Device dropdown
  - Meter Volume + Meter Device dropdown (with "Same as Alerts" option)
  - Radio Volume (wrapping FlexLib audio gain)
  - All controls with AccessibleName and proper tab order
- "Audio Workshop..." button — launches AudioWorkshopDialog directly from the Audio tab (second path alongside menu)
- Persist in AudioOutputConfig.xml

**Files:** `JJFlexWpf/Dialogs/SettingsDialog.xaml`, `SettingsDialog.xaml.cs`, `JJFlexWpf/AudioOutputConfig.cs`

**Verify:** Audio tab reachable via Tab. Sliders announce values. Device dropdowns list devices. Changes persist.

---

## PHASE 9B (Track B): About Dialog WebView2 Upgrade

**Goal:** Replace ListBox content with WebView2 for heading-based screen reader navigation.

**What gets built:**
- Replace 4 ListBox controls with a single `Microsoft.Web.WebView2.Wpf.WebView2` control
- HTML templates as embedded resources: separate `.html` files in source tree, marked as embedded resources in `.csproj`, loaded via `Assembly.GetManifestResourceStream()` at runtime
- Templates use `{{placeholder}}` tokens for dynamic values (version, build date, etc.) — replaced at load time
- Tab selector loads the appropriate template and pushes to WebView2 via `NavigateToString()`
- HTML uses `<h2>` headings, `<p>` content — screen reader navigates with H key
- Per-tab Copy to Clipboard (plain text version)
- Copy All to Clipboard
- Homepage link opens default browser
- Check for Updates (already implemented, keep working)
- Async WebView2 init (`EnsureCoreWebView2Async()`) with loading state

**Files:** `JJFlexWpf/Dialogs/AboutDialog.xaml`, `AboutDialog.xaml.cs`, `JJFlexWpf/Resources/About*.html` (new, embedded), `JJFlexWpf/JJFlexWpf.csproj` (embed resources)

**Verify:** Dialog opens. WebView2 shows content. H key navigates headings in NVDA. Tab switching loads correct content. Copy buttons work. Update check works.

---

## PHASE 10B (Track B): Quick Wins

**Goal:** DSP level minimums, access key announcements.

**DSP level minimums 0 to 1:**
- NR Level: change minimum from 0 to 1 (level 0 kills all audio — confusing)
- NB Level: change minimum from 0 to 1 (level 0 means blanker on but doing nothing)
- WNB Level: change minimum from 0 to 1 (same — on but ineffective)

**Access key announcements for screen readers:**
- Buttons with access keys (from Mini Sprint 24a) don't announce their shortcut when tabbed to
- Set `AutomationProperties.AcceleratorKey` on all buttons with access keys across 42 dialogs
- Especially important for non-obvious labels (e.g., "Connect to Remote Radio" — is it Alt+C? Alt+R?)
- Screen reader should announce the access key so users know the shortcut exists

**Files:** `JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs`, 42 dialog XAML files

**Verify:** NR, NB, and WNB level sliders won't go below 1. Tabbing to buttons announces access key in NVDA. Build clean.

---

## PHASE 11: Merge + Integration + 60m Advisory

**Goal:** Merge tracks, integration test, implement 60m advisory.

**What gets built:**
- Merge `sprint24/track-b` into `sprint24/track-a`
- Clean build verification
- 60m mode advisory: warn if mode inappropriate for channel segment when jumping to 60m

**Files:** Merge conflicts (expected: none). `JJFlexWpf/FreqOutHandlers.cs` or `MainWindow.xaml.cs` (60m advisory)

**Verify:** Full clean build (x64 + x86). All features from both tracks work together. Verbosity affects all new speech. KeyDefs.xml round-trips correctly.

---

## PHASE 12: Audio Workshop — Investigation-First Fix

**Goal:** Diagnose WHY tab navigation is broken, THEN fix it. This is its own phase because we've tried to fix it twice with the same symptoms. No blind fixes — root cause first.

### Step 12a — Investigation (read-only, no code changes)

- Read AudioWorkshopDialog.xaml top to bottom — map the full visual tree
- Trace the keyboard event chain: PreviewKeyDown on Window then PreviewKeyDown on TabControl then KeyDown propagation
- Check KeyboardNavigation attached properties (TabNavigation, DirectionalNavigation, ControlTabNavigation)
- Check if JJFlexDialog base class PreviewKeyDown handler eats Tab before the TabControl sees it
- Check if the TabControl's ContentPresenter is properly hosting focusable controls
- Check if any IsTabStop=False or Focusable=False is blocking navigation
- Document the exact event chain and where Tab gets swallowed

### Step 12b — Fix based on findings

- Apply the fix that addresses the actual root cause (not a guess)
- Fix Escape close if not already handled by JJFlexDialog base

### Step 12c — If investigation reveals the dialog needs restructuring

- Document what's wrong architecturally
- Propose the restructuring approach
- Implement if straightforward, or defer with a clear diagnosis for next sprint

**Files:** `JJFlexWpf/Dialogs/AudioWorkshopDialog.xaml`, `.cs`, possibly `JJFlexWpf/JJFlexDialog.cs` (base class)

**Verify:** All tabs reachable with Tab key. Controls within each tab reachable. Escape closes. No regression in audio preset loading.

**Why this is separate:** Two previous fix attempts failed. The common pattern in WPF keyboard issues is that the symptom (Tab doesn't work) has a non-obvious cause (event handler higher in the tree consuming the keystroke, or focus scope misconfiguration). Guessing at the fix wastes time. Diagnosis first.

---

## PHASE 13: Cleanup, Build, Test Matrix

**Goal:** Final polish, version bumps, changelog, test matrix.

**What gets built:**
- Library version bumps: JJFlexWpf minor (major new features), Radios patch (new types, slice fix)
- Clean up dead VB code if any remains
- Update Agent.md
- Create `docs/planning/agile/sprint24-test-matrix.md`
- Update `docs/CHANGELOG.md`
- Clean build both architectures, verify installers

**Verify:** Clean build x64 + x86. Both installers generated. Exe version correct.

---

## Confirmed Decisions

- **Ctrl+Shift+V** — verified free. Used for verbosity cycling.
- **Ctrl+Alt+S** — repurpose from StartScan to Status Dialog. Scan remains accessible via Radio menu. StartScan gets unbound (Keys.None) or reassigned if needed.
- **Leader key T** (Ctrl+J then T) — tones toggle. Verify no leader conflict during Phase 5 audit.
- **Shift+M** — slice mute toggle. Verify no conflict during Phase 5 audit.

---

## Execution Summary

- **Phases 1-6:** Serial, in this repo on main branch. Start tomorrow.
- **Phases 7-10:** Two parallel tracks after Phase 6 commits. User spawns two CLI sessions.
  - Track A (main repo): Slices + Status Dialog — start simultaneously with Track B
  - Track B (worktree `../jjflex-24b`): Audio + About + Quick Wins — start simultaneously with Track A
- **Phases 11-13:** Serial finishing after both tracks complete.
- **Track instructions:** Written separately before parallel phase begins.
