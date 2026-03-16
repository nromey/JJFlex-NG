# Agent Summary

This document captures the current state of JJ-Flex repository and active work.

**Repository root:** `C:\dev\JJFlex-NG`
**Branch:** `sprint24/track-a`

## 1) Overview
- JJFlexRadio: Windows desktop app for FlexRadio 6000/8000 series transceivers
- **Display name:** JJ Flexible Radio Access (internals stay JJFlexRadio)
- **Migration complete:** .NET 8, dual x64/x86 architecture, WebView2 for Auth0
- **Current version:** 4.1.15 (released 2026-03-07), version bump to 4.1.16 deferred to after Sprint 25

## Current State — Sprint 24 Complete, Pending Test + Merge

**Status:** All 13 phases coded, committed, and building clean on all four configurations (x64/x86 Debug/Release). Branch `sprint24/track-a` needs merge to main after testing.

**Sprint 24 plan:** `docs/planning/skywave-negative-sweepstakes.md`
**Test matrix:** `docs/planning/agile/sprint24-test-matrix.md`

### Sprint 24 Commit History (sprint24/track-a)
- Phase 1: Extract shared key types to Radios/KeyCommandTypes.cs
- Phase 2: KeyCommands.cs skeleton + KeyCommandContext
- Phase 3: Port all 126 handler methods to C# KeyCommands
- Phase 4: Wire up C# KeyCommands and cut over from VB
- Phase 5: Conflict audit and scope cleanup
- Phase 6a: Verbosity engine core + hotkeys
- Phase 6b: Tag all Speak() calls with verbosity levels
- Phase 7A: Fix slice count tracking and menu rebuild
- Phase 7B: Dual-channel audio architecture
- Phase 8A: Slice selector, operations, freq improvements
- Phase 8B: Merge Audio + Meter Tones tabs into unified Audio tab
- Phase 9A: Accessible Status Dialog with live refresh
- Phase 9B: About Dialog WebView2 upgrade
- Phase 10A: Fix VFO index drift after slice removal + trace logging
- Phase 10B: DSP level minimums and access key announcements
- Phase 11: 60m mode advisory on band jump
- Phase 12: Fix Audio Workshop Tab navigation
- Phase 13: Fix 32 build errors from key migration + x86 build, library version bumps

### Key Findings During Phase 13
- VB project was SKIPPED on x64 solution builds due to missing `Build.0` lines in JJFlexRadio.sln — Sprint 24 Phases 1-4 (key migration) were never build-verified against the VB project
- 32 VB compilation errors in globals.vb and ApplicationEvents.vb from Phase 4's delegate wirings referencing methods that were deleted during the migration
- Radios.csproj had RuntimeIdentifier that broke clean x86 builds (NETSDK1047)
- DX Cluster (ShowArCluster) is a placeholder — cluster UI needs reimplementation after key migration

### Smoke Test (2026-03-13 evening)
- **Tolk DLL fix**: Speech was completely broken — Tolk.dll and nvdaControllerClient64.dll missing from output. Radios.csproj conditioned DLL copy on Platform==x64 but solution maps Radios to AnyCPU. Fixed by adding AnyCPU to condition.
- **Key migration verified**: Mute (M on slice field), verbosity cycling (Ctrl+Shift+V), speak status (Ctrl+Shift+S) all working after Tolk fix
- **SmartLink connects OK** when Don's radio is on
- **Findings for Sprint 25 fixes**:
  - Slice Operations field accessible name says "audio 60" instead of "Slice A Operations"
  - Access key announcements missing in Rig Selector (only Cancel announces Alt+C)
  - SmartLink zero-radios loops back to Auth0 instead of saying "No remote radios available" (pre-existing)

### Directed Testing Session (2026-03-14)
**Build:** Debug x64 from sprint24/track-a with BUG-049 hotfix applied

#### BUG-049 Fix (slice create after release)
- **Root cause found and fixed**: Sprint 23 Phase 6 changed Jim's original `MyNumSlices == TotalNumSlices` check to `theRadio.SliceList.Count >= theRadio.MaxSlices`. FlexLib's `MaxSlices` reports *available remaining* (0 when profile fills all slots), not *total capacity*. So the check was permanently blocked.
- **Fix**: Added `TotalMaxSlices` property — model-based lookup (6300=2, 6600=4, 6700=8, etc.) that always returns correct hardware capacity. Also added `_pendingRemovals` counter for async queue timing safety.
- **Verified working**: release slice then create works correctly now. Refuses to release last slice (correct).
- **Code changed**: `Radios/FlexBase.cs` — `NewSlice()`, `RemoveSlice()`, new `TotalMaxSlices` property

#### Hotkey Sampler Results
- **Ctrl+J, N (NR toggle)**: PASS
- **F6 (20m band jump)**: PASS — announces band and frequency
- **Ctrl+Shift+V (verbosity cycle)**: PASS (confirmed from smoke test)
- **Ctrl+J, T (tones toggle)**: toggles meter tones, not general earcon mute — works for what it is
- **Period/Comma (create/release slice)**: PASS after BUG-049 fix
- **Ctrl+Alt+S (Status Dialog)**: hotkey does nothing — dialog opens from Tools menu though
- **Status Dialog content**: auto-refresh resets reading position every 2 seconds, unusable with screen reader
- **Escape from dialogs**: says "pane" instead of useful context

#### New Findings for Sprint 25
- **General earcon mute needed**: Ctrl+J, Shift+T suggested for global earcon toggle (distinct from Ctrl+J, T for meter tones)
- **Status Dialog refresh**: preserve listbox selection index across auto-refresh (suppress UIA events during update); fall back to manual Refresh button if screen reader still flashes
- **Ctrl+Alt+S hotkey broken**: Status Dialog opens from menu but not from keyboard shortcut — investigate key dispatch
- **Slice selector wraps**: up/down arrows wrap around instead of stopping at first/last slice
- **Focus-return context**: pressing Escape from any dialog just says "pane" — need a context announcement (e.g., "Slice A, 14.175, USB"). Investigate UIA LiveRegion Polite for post-close announcement.
- **Disconnection testing needed**: test with Don's radio off — Status Dialog "not connected" state, hotkey graceful failures, SmartLink zero-radio loop. Coordinate with Don.
- **SmartLink account**: Noel can create his own account via SmartLink login screen — useful for testing zero-radio scenario independently
- **Connection failure menu state**: after failed/slow connection, menu stuck on "Disconnect" with no way back to "Connect" — must restart app. Menu needs to track actual connection state.
- **Connection picker UX** (backlog): RigSelector dialog should show list of known radios (account+radio pairs) plus "New Connection" option, instead of always auto-connecting to last account
- **No ding tone on frequency entry confirmation**: test 7B.10 — ding should play when quick-type prompt appears

#### 60m Channel Fix (during testing session)
- **Root cause**: `BandMemory.GetFrequency` fell back to band center (5.400 MHz) for 60m — off-channel. The Channel 1 fallback in `BandJump` was dead code (only ran when band memory disabled).
- **Fix**: Two layers — `BandMemory.GetFrequency` now falls back to Channel 1 for 60m; `BandJump` validates any 60m frequency against channel table and snaps to nearest valid channel. Self-heals stale band memory on save.
- **Verified**: Shift+F3 now lands on Channel 1 (5.332 MHz). Mode advisory fires correctly (CW → USB auto-correction announced).
- **Code changed**: `Radios/BandMemory.cs`, `JJFlexWpf/FreqOutHandlers.cs`

#### Additional Test Results
- **About Dialog (Help → About)**: PASS — opens, WebView2 loads, H key heading nav works in NVDA, tabs switch, Escape closes
- **Quick-type frequency entry**: PASS — type digits on freq field, confirmation prompt speaks, Enter/Escape work. No ding tone though.
- **60m band jump (Shift+F3)**: PASS after fix — lands on Channel 1, mode advisory works
- **60m digi segment advisory**: PASS — CW→USB advisory fires on channels, USB→CW fires in digi segment
- **60m boundary notifications**: Fixed — digital segment enter/leave beep+speech on Chatty. Initial channel zone approach was too chatty (1.5kHz tolerance caused constant enter/leave), simplified to digi segment only.
- **Modern mode freq field**: FINDING — reads individual digits on left/right instead of whole frequency. Needs to be non-position-sensitive in modern mode.

#### Boundary Notification Improvements (during testing)
- **Verbosity gating**: beep always fires, speech only at Chatty verbosity (was Terse = always)
- **Human-readable labels**: "Extra Phone and CW", "General CW" etc. instead of raw enum values
- **Code changed**: `JJFlexWpf/FreqOutHandlers.cs` — `CheckBandBoundary`, `GetSubBandKey`, new `FormatLicense`/`FormatMode` helpers

#### More Test Results (continued session)
- **DSP level minimums**: PASS — NR Level stops at 1, won't go to 0
- **DSP state tracking edge case**: FINDING — change mode (Alt+M) while NR is on, radio turns off NR but ScreenFields panel still shows it on. Sprint 25 fix.
- **Audio Workshop tab navigation**: PASS after `EnableModelessKeyboardInterop` fix — Tab now moves through all controls
- **Audio Workshop Alt+Tab**: FINDING — can't Alt+Tab back to main window to operate radio. Owner relationship traps focus. Sprint 25: remove Owner for non-modal dialog.
- **Access key announcements**: PASS for dialogs using CreateButtonPanel (Settings OK/Cancel work). Rig Selector missing access keys (known from smoke test). Sprint 25: audit all dialogs for access keys.
- **VFO index drift**: PASS — tested during BUG-049 fix. Slice letters correct after create/remove.

#### More Test Results (late session)
- **Audio Settings sliders**: FINDING — can Tab to sliders, NVDA announces them, but arrow keys don't change values. WPF Slider + screen reader issue. Sprint 25: replace with ValueFieldControl.
- **Dual-channel audio**: Both earcon and meter tones play simultaneously without cutting each other off. Can't verify channel independence until volume controls work (slider fix needed).
- **Connection failure edge case**: FINDING — connection failed/slow, menu stuck on "Disconnect" with no way back. Must restart app. Sprint 25 fix.

#### Tests Not Yet Run
- Audio Settings tab persistence (save/reload)
- Full integration test suite (cross-feature interactions)
- Screen reader matrix (NVDA full pass, JAWS)

#### Sprint 25 Easter Egg Assets — READY
- All sounds hashed and stored in `JJFlexWpf/Resources/4f89f8bc7/` (hash of "TuningData")
- **autopatch2.wav** → `8abf5a4.0d3a3f5` (Patrick's autopatch welcome + ct1 chord + static crash)
- **56kfragment.wav** → `4c85663.f6cdb1f` (56k modem handshake, freesound CC BY 4.0)
- **Keyboard sounds** → `8b38e27/` subdirectory (hash of "keyboard-sounds"), 13 files: 0-9, enter, up, down
- Full manifest at `C:\Users\nrome\JJFlex-private\easter-egg-manifest.md` (local only, not in repo)
- Third-party attributions at `docs/THIRD-PARTY-NOTICES.md`
- Unlock storage: "TuningHash" field in config, salted SHA256
- Player function: `TCalibrationMasterSet()` in a misdirection class, feeds hashed files to NAudio
- DTMF tones: generate on the fly with NAudio (no stored files needed)
- Connection picker design: `docs/planning/connection-picker-design.md`

### Library Versions Bumped
- JJFlexWpf: 2.1.0 → 2.2.0 (minor: key migration, verbosity engine, status dialog, audio workshop)
- Radios: 3.2.6 → 3.2.7 (patch: KeyCommandTypes, SixtyMeterChannels, slice fix)

**Next steps:**
1. Merge sprint24/track-a to main (testing passed representative sample)
2. Sprint 25 planning — scope expanded from research/design session:

**Sprint 25 Fix Items:**
   - Slice Operations accessible name ("audio 60" → "Slice A Operations")
   - Access key audit — all dialogs need access keys on actionable buttons
   - SmartLink zero-radios UX (loops back to Auth0 instead of message)
   - Ctrl+Alt+S hotkey broken (Status Dialog opens from menu only)
   - Status Dialog auto-refresh resets screen reader position
   - Connection failure menu state (stuck on "Disconnect")
   - DSP state tracking on mode change (NR on but ScreenFields shows wrong)
   - Slice selector wraps instead of stopping at boundaries
   - Audio Settings sliders — replace with ValueFieldControl
   - No ding tone on frequency entry confirmation
   - Modern mode frequency field reads individual digits
   - Focus-return context after Escape ("pane" → useful context via UIA LiveRegion)
   - Audio Workshop Alt+Tab (remove Owner for non-modal)
   - General earcon mute (Ctrl+J, Shift+T)

**Sprint 25 Features:**
   - Easter eggs: "autopatch" + "qrm" unlock system (assets ready, TuningHash, TCalibrationMasterSet)
   - Typing sound options (beep / mechanical / touch tone / off, expands with easter egg unlock)
   - Braille display status line (design doc at docs/planning/braille-verbosity-design.md)
   - Meter speech configuration (which meters speak, when, verbosity level)
   - ATU/tune complete auto-speak SWR (Don's request)
   - Action toolbar (Ctrl+Tab, Tune/ATU/Transmit buttons)
   - Credits tab fleshed out (Jim, Patrick, freesound, libraries)

**Sprint 25 Research/Design Only:**
   - RNNoise integration prototype (NuGet package ready, ISampleProvider pattern known)
   - Braille verbosity design implementation (Phase 1 only)
   - NR/DSP research documented at docs/planning/nr-dsp-research.md
   - Connection picker UX design at docs/planning/connection-picker-design.md

**Backlog (not Sprint 25):**
   - Spectral subtraction (trainable NR, Don's idea)
   - Software AGC, auto-notch, expanded filters
   - CW decode (Goertzel + decoder, accessible alternative to SmartSDR's visual-only CW decode)
   - Accessible waterfall (FFT + noise floor + threshold + sonification/braille)
   - Digital mode decode (RTTY, fldigi subprocess)
   - VST3 plugin hosting
   - Connection picker implementation
   - Dot Pad tactile graphics (separate from braille display)

3. Bump version to 4.1.16 and release after Sprint 25

### Deferred to Backlog
- DX Cluster reimplementation (ShowArCluster placeholder — needs event handlers, ClusterForm UI)
- FINDING-25: Multi-radio abstraction — wait for second radio platform
- FINDING-48: Earcon fade-out/envelope — add selectively later
- Connection error hang: SSL error made app unresponsive — needs deeper investigation

## Open Bugs
- BUG-013: Duplicate QSO warning beep not playing
- BUG-015: F6 double-announces "Radio pane" in Logging Mode

## Completed Sprints
- Sprint 24: Key Migration + Verbosity + Slices + Audio + Status Dialog + About + Audio Workshop
- Sprint 23: Fix sprint — 63 of 65 findings from Sprint 21+22 testing
- Sprint 22: Auto-connect, About, Command Finder, debounce, tune carrier, antenna, slices, startup speech, meters, 60m
- Sprint 21: Meter sonification, Audio Workshop, leader key, TX sculpting, CHM help, app rename
- Sprint 17: Band nav, license tuning, PTT enhancements, settings dialog, controls
- Sprint 16: PTT safety (Ctrl+Space), Dummy Load, Connection Tester
- Sprint 15: WPF RigSelector, filter overhaul, PTT safety infra, menu redesign
- Sprint 14: ScreenFieldsPanel, speech debounce, slice menu, filter race fix
- Sprint 13: Tab chain, Modern tuning, menus, SmartLink auto-retry
- Sprint 12: Stabilize WPF — menus, SmartLink, FreqOut tuning, error dialogs
- Sprint 11: WPF adapters, Form1 kill, dead code deletion (~13,000 lines)
- Sprint 10: FlexBase.cs decoupling (interfaces + delegates)
- Sprint 9: All dialogs to WPF (3 parallel tracks, 122 files)
- Sprint 8: Form1 -> WPF MainWindow
- Sprint 7: Modern Menu, Logging Polish (v4.1.14)
- Sprint 6: Bug Fixes, QRZ Logbook & Hotkey System (v4.1.13)
- Sprint 5: QRZ/HamQTH Lookup & Full Log Access
- Sprint 4: Logging Mode (v4.1.12)
- Sprint 3: Classic/Modern Mode Foundation
- Sprint 2: Auto-Connect (v4.1.11)
- Sprint 1: SmartLink Saved Accounts (v4.1.10)

## Build Commands

```batch
# Debug build for testing
dotnet clean JJFlexRadio.vbproj -c Debug -p:Platform=x64 && dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal

# Clean + rebuild Release (triggers NSIS installer)
dotnet clean JJFlexRadio.vbproj -c Release -p:Platform=x64 && dotnet build JJFlexRadio.vbproj -c Release -p:Platform=x64 --verbosity minimal

# Both installers
build-installers.bat
```

---

*Updated: Mar 13, 2026 — Sprint 24 all 13 phases complete on branch sprint24/track-a. All four build configs clean (0 errors). Test matrix created. Ready for testing and merge to main.*
