# Sprint 15: pileup-dx-ragchew

## Context

Sprint 14 shipped the ScreenFieldsPanel (5 expander categories, 25+ focusable controls) and speech debounce. During pre-Sprint 15 testing of new SmartLink account management menu items, we discovered:

1. **Dual auto-connect system** — legacy `_autoConnect.xml` and V2 `_autoConnectV2.xml` are out of sync. RigSelector reads legacy, startup reads V2. Must unify to V2 only.
2. **WinForms RigSelector needs WPF replacement** — the WPF `RigSelectorDialog` already exists (352 lines, callback-based) but was never wired. Do it right once.
3. **No SmartLink login from account manager** — "New Login" button closes dialog without triggering Auth0.
4. **Classic/Modern speech mismatch at startup** — says "classic mode" but shows Modern (Radio) menu.
5. **SpeakAfterMenuClose is a 150ms timing hack** — should use UIA LiveRegion.

Sprint 15 also delivers the planned filter overhaul, PTT safety, and menu redesign from the Agent.md scope.

**Target version:** 4.1.115

---

## Track Decomposition (5 Tracks)

| Track | Name | Starts | Depends On |
|-------|------|--------|------------|
| A | WPF RigSelector + Auto-Connect Unification | Immediately | None |
| B | Filter Overhaul | Immediately | None |
| C | PTT Safety System | Immediately | None |
| D | ScreenFields Menu Redesign + Menu Audit | After Track A merges | Track A |
| E | UIA LiveRegion Pilot | Immediately | None |

**Execution:** Start A, B, C, E in parallel. When A completes, merge A, then start D.

---

## Track A: WPF RigSelector + Auto-Connect Unification

**Goal:** Replace WinForms RigSelector with WPF RigSelectorDialog. Delete legacy auto-connect. Wire SmartLink login. Fix startup bugs.

**Worktree:** `C:\dev\JJFlex-NG` (main)

### Files Owned
- `globals.vb` — replace `selectorProc`/`openTheRadio`/`TryAutoConnectOnStartup`
- `ApplicationEvents.vb` — fix auto-connect callbacks, remove legacy references
- `RigSelector.vb` + `.Designer.vb` + `.resx` — **DELETE**
- `JJFlexWpf/Dialogs/RigSelectorDialog.xaml.cs` — wire callbacks
- `JJFlexWpf/Dialogs/SmartLinkAccountDialog.xaml.cs` — wire "New Login" to Auth0
- `JJFlexWpf/MainWindow.xaml.cs` — SmartLink region only (lines 1693-1768)
- `Radios/AutoConnectConfig.cs` — minor cleanup
- `Radios/AutoConnectSettingsDialog.cs` — update for WPF parent

### Phases
1. Create `wpfSelectorProc` in globals.vb — instantiate `RigSelectorDialog` with `RigSelectorCallbacks` populated from FlexBase/globals state
2. Wire all callbacks: `StartLocalDiscovery`, `StartRemoteDiscovery`, `Connect`, `RegisterRadioFound`, `SaveAutoConnectSettings`, `SaveGlobalAutoConnect`, `CheckOtherAutoConnect`
3. Replace `selectorProc` call in `openTheRadio` with `wpfSelectorProc`
4. Update `TryAutoConnectOnStartup` — use only V2 `AutoConnectConfig`, remove all legacy `_autoConnect.xml` references
5. Fix `ApplicationEvents.vb` `ClearAutoConnectRadio` lambda — remove `RigSelector.AutoConnectData` reference
6. Wire "New Login" in `SmartLinkAccountDialog` to trigger WPF `AuthDialog` (WebView2 PKCE flow), save resulting account to `SmartLinkAccountManager`
7. Fix Classic/Modern speech mismatch — ensure `ApplyUIMode` runs before `SpeakWelcome` at startup
8. Delete `RigSelector.vb`, `RigSelector.Designer.vb`, `RigSelector.resx`
9. Remove all legacy `_autoConnect.xml` file handling throughout codebase
10. Clean build x64 + x86, verify both platforms

### Key patterns to preserve
- `RigControl.ShowAccountSelector` lambda (globals.vb:1468) — auto-selects most recent SmartLink account
- `RemoteButton` spawns STA thread for WebView2 auth — WPF `AuthDialog` runs on UI thread via `ShowDialog()`, should work without separate thread
- `AutoConnectTimer` (1-second interval) — WPF dialog already has `DispatcherTimer` equivalent

---

## Track B: Filter Overhaul

**Goal:** Filter presets, adaptive steps, independent edge keys, speech hotkeys, Classic mode bracket keys.

**Worktree:** `C:\dev\jjflex-15b`

### Files Owned
- `JJFlexWpf/FreqOutHandlers.cs` — filter hotkey logic
- `Radios/FilterPresets.cs` — **NEW** (data model + persistence)

### Files Shared (isolated regions)
- `JJFlexWpf/MainWindow.xaml.cs` — line 567 only (Classic bracket key gate fix)
- `JJFlexWpf/NativeMenuBar.cs` — `BuildFilterItems` only (lines 318-426)

### Revised Filter Key Scheme (replaces Sprint 14 mapping)

| Keys | Action | Description |
|------|--------|-------------|
| `[` | Low edge down | Independent — expand low side |
| `]` | High edge up | Independent — expand high side |
| `Ctrl+[` | Squeeze | Narrow both edges equally |
| `Ctrl+]` | Pull | Widen both edges equally |
| `Shift+[` | Shift down | Slide entire passband down (unchanged from Sprint 14) |
| `Shift+]` | Shift up | Slide entire passband up (unchanged from Sprint 14) |
| `Alt+[` | Previous preset | Cycle to narrower preset for current mode |
| `Alt+]` | Next preset | Cycle to wider preset for current mode |
| `Alt+Ctrl+F` | Read filter | Speak current filter low/high values |

**Frequency field hotkey remapping (bare keys = reads, modifiers = actions):**

| Keys | Context | Action | Description |
|------|---------|--------|-------------|
| `F` | Freq field (Classic + Modern) | Read frequency | One-shot speak current frequency (was toggle readout) |
| `Ctrl+Shift+F` | Global | Toggle freq readout | Enable/disable auto-speak during tuning (was one-shot read) |
| `Shift+S` | Modern freq field | Announce step | Speak current coarse/fine mode + step size (was bare S) |
| `S` | Modern freq field | *(freed)* | Reserved for S-meter read (Sprint 16) |

Logic: unmodified = independent edges, Ctrl = both edges symmetric, Shift = slide whole passband, Alt = presets.
Frequency field: bare letter = non-destructive read, modifier combo = toggle/action.

### Phases
1. Rewrite `HandleFilterHotkey` in `FreqOutHandlers.cs` with new key scheme — replace Sprint 14's narrow/widen (unmodified) with independent edge (unmodified) and squeeze/pull (Ctrl). Keep Shift passband shift unchanged.
2. Create `FilterPresets.cs` — per-mode defaults (SSB: 1.8k/2.4k/2.7k/3.0k, CW: 100/250/500/1k, DIGI: 500/2.7k/3.0k), user-customizable, XML serialized per-operator
3. Add `Alt+[` / `Alt+]` preset cycling — cycle through presets for current mode, speak preset name and width
4. Implement adaptive step sizes — step=10 below 200Hz bandwidth, step=25 below 500Hz, step=50 default
5. Add filter speech hotkey — `Alt+Ctrl+F` = read current filter values aloud
6. Remap frequency field hotkeys:
   - `S` → `Shift+S` in Modern freq field (announce step)
   - `F` → one-shot read frequency in Classic + Modern freq field (calls `SpeakFrequency()`)
   - `Ctrl+Shift+F` global → toggle freq readout on/off (add `ToggleFreqReadout()` on FreqOutHandlers)
   - Free bare `S` in Modern mode for future S-meter read
   - **Partially done:** FreqOutHandlers.cs edits for S→Shift+S and F→read already applied. Still needed: MainWindow.xaml.cs Ctrl+Shift+F handler change.
7. Fix Classic mode bracket key gate — change line 567 condition from `ActiveUIMode == UIMode.Modern` to `ActiveUIMode != UIMode.Logging`
8. Add filter preset menu items to `BuildFilterItems` — list presets for current mode, checkmark active preset
9. Clean build x64

---

## Track C: PTT Safety System

**Goal:** Space/Shift+Space PTT, transmit lock, timeout warnings with earcon beeps, hard kill.

**Worktree:** `C:\dev\jjflex-15c`

### Files Owned
- `JJFlexWpf/PttSafetyController.cs` — **NEW** (state machine, timers, ALC monitoring)
- `JJFlexWpf/EarconPlayer.cs` — **NEW** (synthesized beep tones)
- `Radios/PttConfig.cs` — **NEW** (timeout settings, per-operator)

### Files Shared (isolated regions)
- `JJFlexWpf/MainWindow.xaml.cs` — `PreviewKeyDown` (new Space/Shift+Space block)
- `JJFlexWpf/MainWindow.xaml` — Transmit button visibility wiring

### Phases
1. Create `PttConfig.cs` — timeout (default 3 min, max 15 min), warning thresholds, hard 15-min kill (non-configurable), persisted per-operator
2. Create `PttSafetyController.cs` state machine — states: Idle, PttHold, Locked, Warning1 (10s beeps), Warning2 (5s beeps), OhCrap (1s beeps), HardKill. All transitions check `_radioPowerOn && RigControl != null`
3. Create `EarconPlayer.cs` — synthesized beeps via `System.Media.SoundPlayer` or `Console.Beep`. Avoid PortAudio conflicts with remote audio stream
4. Wire `Space` = PTT hold (TX on KeyDown, RX on KeyUp via `PreviewKeyUp`), `Shift+Space` = lock TX (speak "Transmitting, locked"), `Escape` or `Shift+Space` again = unlock (speak "Transmit off")
5. Wire Transmit button Classic visibility — already exists in MainWindow.xaml RadioControlsPanel, needs visibility toggle for Classic mode
6. Add ALC=0 auto-release — poll ALC while locked, 60 consecutive seconds at 0 = auto-release with speech "No signal detected, transmit off"
7. Clean build x64

---

## Track D: ScreenFields Menu Redesign + Menu Audit

**Goal:** Replace flat DSP dump with 5 expander nav items, Ctrl+Shift+1-5 hotkeys, menu audit.

**Worktree:** `C:\dev\jjflex-15d` (created AFTER Track A merges)

### Files Owned
- `JJFlexWpf/NativeMenuBar.cs` — full menu restructure

### Files Shared
- `JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs` — add Expand/Collapse/Focus methods
- `JJFlexWpf/MainWindow.xaml.cs` — `PreviewKeyDown` Ctrl+Shift+1-5 block

### Phases
1. Redesign Classic ScreenFields menu:
   ```
   Show Field Panel [check]
   ──────────────────
   Noise Reduction and DSP    Ctrl+Shift+1
   Audio                      Ctrl+Shift+2
   Receiver                   Ctrl+Shift+3
   Transmission               Ctrl+Shift+4
   Antenna                    Ctrl+Shift+5
   ```
2. Wire expander navigation: if panel visible + expander expanded → collapse. If collapsed → expand + focus header. If panel hidden → show panel, expand, focus.
3. Move DSP toggle items, filter controls, diversity to Operations menu
4. Wire `Ctrl+Shift+1-5` in `PreviewKeyDown`
5. Audit Modern menus — wire all `AddNotImplemented` items that have working Classic counterparts
6. Fix FieldsPanel visibility persistence — save to operator profile
7. Fix FieldsPanel tab order (Sprint 14 remnant — panel ends up last instead of second)
8. Clean build x64

---

## Track E: UIA LiveRegion Pilot

**Goal:** Replace SpeakAfterMenuClose 150ms hack with UIA LiveRegion. Pilot for broader Tolk reduction.

**Worktree:** `C:\dev\jjflex-15e`

### Files Owned
- `Radios/ScreenReaderOutput.cs` — add LiveRegion mode

### Files Shared
- `JJFlexWpf/MainWindow.xaml` — add hidden LiveRegion TextBlock
- `JJFlexWpf/MainWindow.xaml.cs` — add `SpeakViaLiveRegion` helper
- `JJFlexWpf/NativeMenuBar.cs` — replace `SpeakAfterMenuClose` implementation

### Phases
1. Add hidden `TextBlock` with `AutomationProperties.LiveSetting="Assertive"` to MainWindow.xaml
2. Create `SpeakViaLiveRegion(string message)` — sets TextBlock text, UIA fires LiveRegionChanged
3. Replace `SpeakAfterMenuClose` to use LiveRegion instead of 150ms `Task.Delay` + Tolk
4. Test with NVDA — verify LiveRegion works through ElementHost interop
5. Test with JAWS — verify cross-reader compatibility
6. If LiveRegion doesn't work through ElementHost (possible interop issue), document and keep Tolk fallback
7. Document which `Speak()` calls are candidates for LiveRegion migration vs which need Tolk (Braille, interrupt)

### Risk
UIA LiveRegion events may not propagate through WinForms↔WPF ElementHost interop. Track E is a pilot — if it fails, existing Tolk approach is preserved.

---

## Merge Plan

| Order | Track | Into | Notes |
|-------|-------|------|-------|
| 1 | A | main | Foundation — must land first |
| 2 | B | main | Filter changes isolated to FreqOutHandlers |
| 3 | C | main | PTT additions to PreviewKeyDown are additive |
| 4 | E | main | LiveRegion is additive (new controls + helper) |
| 5 | D | main | Menu overhaul last — benefits from B/C/E in place |

Post-merge clean build after each merge. Track D merges last because it restructures NativeMenuBar and can properly wire any items added by B and C.

---

## Conflict-Prone Areas

- **`MainWindow.xaml.cs` PreviewKeyDown** (lines 513-588): B adds Classic bracket keys (line 567 condition change), C adds Space/Shift+Space (new block), D adds Ctrl+Shift+1-5 (new block). All additive in different sections — simple merges.
- **`NativeMenuBar.cs`**: B touches `BuildFilterItems` (lines 318-426), E touches `SpeakAfterMenuClose` (lines 1021-1029), D does full restructure. D merges last to absorb all changes.

---

## File Ownership Matrix

| File | Track A | Track B | Track C | Track D | Track E |
|------|---------|---------|---------|---------|---------|
| `globals.vb` | **OWN** | - | - | - | - |
| `ApplicationEvents.vb` | **OWN** | - | - | - | - |
| `RigSelector.vb` (delete) | **OWN** | - | - | - | - |
| `MainWindow.xaml.cs` | SmartLink region | PreviewKeyDown L567 | PreviewKeyDown (new block) | Ctrl+Shift+1-5 | SpeakViaLiveRegion |
| `MainWindow.xaml` | - | - | TransmitButton vis | - | LiveRegion TextBlock |
| `NativeMenuBar.cs` | - | BuildFilterItems | - | **OWN** (full rebuild) | SpeakAfterMenuClose |
| `FreqOutHandlers.cs` | - | **OWN** | - | - | - |
| `ScreenFieldsPanel.xaml.cs` | - | - | - | Expand/Focus methods | - |
| `RigSelectorDialog.xaml.cs` | **OWN** | - | - | - | - |
| `SmartLinkAccountDialog.xaml.cs` | **OWN** | - | - | - | - |
| `AutoConnectConfig.cs` | **OWN** | - | - | - | - |
| `FlexBase.cs` | READ | READ | READ | - | - |
| `ScreenReaderOutput.cs` | - | - | - | - | **OWN** |
| `FilterPresets.cs` (new) | - | **OWN** | - | - | - |
| `PttSafetyController.cs` (new) | - | - | **OWN** | - | - |
| `EarconPlayer.cs` (new) | - | - | **OWN** | - | - |
| `PttConfig.cs` (new) | - | - | **OWN** | - | - |

---

## Worktree Setup

```
C:\dev\JJFlex-NG          -- Track A (main worktree)
C:\dev\jjflex-15b         -- Track B (Filter)
C:\dev\jjflex-15c         -- Track C (PTT Safety)
C:\dev\jjflex-15d         -- Track D (Menu Redesign) -- created AFTER Track A merges
C:\dev\jjflex-15e         -- Track E (LiveRegion)
```

Branch naming:
- `sprint15/track-a` (merge target)
- `sprint15/track-b`
- `sprint15/track-c`
- `sprint15/track-d`
- `sprint15/track-e`

---

## Execution Instructions

**Phase 1 — Start 4 tracks simultaneously:**
1. "Start Sprint 15 Track A from TRACK-INSTRUCTIONS.md" in `C:\dev\JJFlex-NG`
2. "Start Sprint 15 Track B from TRACK-INSTRUCTIONS.md" in `C:\dev\jjflex-15b`
3. "Start Sprint 15 Track C from TRACK-INSTRUCTIONS.md" in `C:\dev\jjflex-15c`
4. "Start Sprint 15 Track E from TRACK-INSTRUCTIONS.md" in `C:\dev\jjflex-15e`

**Phase 2 — When Track A reports done:**
- Claude merges Track A to main
- Claude creates Track D worktree from post-merge main
- User starts: "Start Sprint 15 Track D from TRACK-INSTRUCTIONS.md" in `C:\dev\jjflex-15d`

**Merge plan:**
- Track A merges to main first (foundation)
- Remaining tracks merge in order: B, C, E, D (D last because it restructures menus)
- Post-merge clean build after each merge

---

## Verification

After all tracks merge:
1. Clean build x64 + x86 (`build-installers.bat`)
2. Verify exe timestamps match build time
3. Test matrix (separate file: `docs/planning/agile/sprint15-test-matrix.md`):
   - RigSelector: local discovery, remote SmartLink login, auto-connect toggle/clear, New Login flow
   - Filters: bracket keys in Classic AND Modern, Ctrl+bracket squeeze/pull, independent edges, adaptive steps, speech toggle, Alt+bracket presets
   - PTT: Space hold, Shift+Space lock, timeout warnings, hard kill, ALC auto-release
   - Menu: Ctrl+Shift+1-5 expander nav, Operations reorganization, audit completeness
   - LiveRegion: NVDA + JAWS speech from menu actions
   - Accessibility: NVDA full walkthrough, JAWS spot checks
