# Agent Summary

This document captures the current state of JJ-Flex repository and active work.

**Repository root:** `C:\dev\JJFlex-NG`
**Branch:** `main`

## 1) Overview
- JJFlexRadio: Windows desktop app for FlexRadio 6000/8000 series transceivers
- **Migration complete:** .NET 8, dual x64/x86 architecture, WebView2 for Auth0
- **Current version:** 4.1.115 (pending release after Sprint 17 testing)

## Sprint 17: QRM Splatter Pileup — COMPLETE (merged 2026-03-01, pending testing)

**5 parallel tracks, all merged to main.**

- **Track A:** PTT/TX enhancements — ALC dummy load fix, TX chirp tones (400→800Hz start, 800→400Hz stop), PTT verbosity setting (SpeechEnabled in PttConfig), mic level monitoring (FlexLib MicDataReady), TX health monitor ("check microphone" / "mic too hot" warnings)
- **Track B:** Display bugs fixed — Filter presets now mirror values for LSB/DIGL (MirrorForMode helper), slice display changed from 0/1 to A/B/C/D everywhere (VFOToLetter helper)
- **Track C:** Band navigation — Ctrl+F1-F8 for main bands, Ctrl+Shift+F1-F4 for WARC, Alt+Up/Down for sequential band nav, mode-aware band memory (BandMemory[band][mode] per-operator XML), license-aware tuning with boundary notifications and TX lockout (LicenseConfig per-operator XML)
- **Track D:** ValueFieldControl enhancements — PgUp/PgDn for 10x step, direct number entry (Enter→type digits→Enter to confirm), Home/End verified working
- **Track E:** Tabbed Settings dialog (PTT/Tuning/License/Audio tabs), Command Finder keyword audit (all commands searchable), scope filtering in Command Finder

**New files:** `Radios/BandMemory.cs`, `Radios/LicenseConfig.cs`, `JJFlexWpf/Dialogs/SettingsDialog.xaml/.cs`

**Stats:** 17 files changed, +1,502 / -89 lines

## Sprint 16 — COMPLETE (tested 2026-03-01)

- PTT changed from bare Space to Ctrl+Space (prevent accidental transmit)
- Shift+Space lock works from any field (not just Freq)
- Alt+Shift+S TX status hotkey with time remaining
- Ctrl+Shift+S Speak Status enriched with PTT detail
- StatusTx field in status bar for braille displays
- Connection tester moved into RigSelector
- Dummy Load Mode toggle
- "No radios found" guidance in RigSelector

**Test results:** 10 PASS, 1 FAIL (filter presets LSB — fixed in Sprint 17), 1 NOT TESTED (timeout — ALC safety triggered first, fixed in Sprint 17)

## Sprint 16 Carried Items (now in Sprint 17)
- ~~Filter presets LSB/DIGL~~ FIXED in Sprint 17 Track B
- ~~Slice 0/1 → A/B~~ FIXED in Sprint 17 Track B
- ~~ALC auto-release in Dummy Load~~ FIXED in Sprint 17 Track A
- ~~ScreenFields step sizes / number entry~~ DONE in Sprint 17 Track D
- ~~Command Finder audit~~ DONE in Sprint 17 Track E

## Completed Sprints
- Sprint 17: Band nav, license tuning, PTT enhancements, settings dialog, controls
- Sprint 16: PTT safety (Ctrl+Space), Dummy Load, Connection Tester in RigSelector
- Sprint 15: WPF RigSelector, filter overhaul, PTT safety infra, menu redesign
- Sprint 14: ScreenFieldsPanel, speech debounce, slice menu, filter race fix
- Sprint 13: Tab chain, Modern tuning, menus, SmartLink auto-retry
- Sprint 12: Stabilize WPF — menus, SmartLink, FreqOut tuning, error dialogs
- Sprint 11: WPF adapters, Form1 kill, dead code deletion (~13,000 lines)
- Sprint 10: FlexBase.cs decoupling (interfaces + delegates)
- Sprint 9: All dialogs to WPF (3 parallel tracks, 122 files)
- Sprint 8: Form1 → WPF MainWindow
- Sprint 7: Modern Menu, Logging Polish (v4.1.114)
- Sprint 6: Bug Fixes, QRZ Logbook & Hotkey System (v4.1.13)
- Sprint 5: QRZ/HamQTH Lookup & Full Log Access
- Sprint 4: Logging Mode (v4.1.12)
- Sprint 3: Classic/Modern Mode Foundation
- Sprint 2: Auto-Connect (v4.1.11)
- Sprint 1: SmartLink Saved Accounts (v4.1.10)

## Backlog (Future Sprints)

### Band Navigation & Tuning
- 60m channel navigation (Shift+F7/F8 prev/next) — per-channel mode/power/tuning enforcement, US-only
- International band plans (ITU regions, country-aware license boundaries)
- F6 logging redesign: kill radio panel, use Ctrl+Tab/Ctrl+Shift+Tab

### Keyboard & Discoverability (design: `docs/planning/design/JJFlex-Keyboard-Proposal.md`)
- Keymap import/export (shareable community keymaps via Tools menu)
- Hotkey profiles: Default / Classic / Contest / Minimal
- Optional Ctrl+J leader key with 2-step max and timeout/cancel
- Conflict resolver UI for key assignment collisions
- Optional global hotkeys (opt-in, bring-JJFlex-to-front)

### Modern UI — Slice-Centric (design: `docs/planning/design/Slice-Menu-and-Status.md`, `JJFlex-Menu-Layout.md`)
- Slice menu: mode, TX, pan, AGC grouped under Slice
- Filter menu: narrow/widen, edge adjust, presets, reset (separated from mode/TX)
- Status Dialog: fully accessible WPF dialog with grouped sections
- Remove legacy status box from Modern mode
- Audio menu: Adjust Rig Audio entry point, Record/Playback, DAX

### Audio Pipeline (design: `docs/planning/design/Audio-Pipeline-Architecture.md`, `Audio-Mixer-and-Routing.md`)
- Rolling audio buffer with configurable length
- "Say Again" instant replay (last N seconds of RX audio)
- "Parrot" TX check: record/playback your own transmission
- Audio mixer: per-source level, ducking controls
- Audio device selection dialog (input/output)
- DAX integration research: what JJFlex owns vs. defers to Flex DAX manager

### DotPad Tactile Waterfall (design: 5 docs in `docs/planning/design/`)
- `TactileFrameBuffer` (60x40 logical grid, device-agnostic)
- `LayoutEngine` + 3 fixed layout modes: Waterfall, Instrument Panel, Hybrid
- `DotPadAdapter`: full-frame push at ~500ms cadence
- Frequency scale: `HzPerBin`, zoom, tick marks, center marker
- Cursor navigation: arrows, P snap-to-peak, Enter-to-tune
- Spectrum sonification: Peak Sprite engine + Freeze Frame Scan

### Product / Business
- Premium feature boundary definition (core stays free/open-source)
- Feature flag / license check mechanism for premium modules
- GitHub Sponsors setup
- Wider SDR support (RTL-SDR, Airspy, SDRplay — long-term, read-only)

### Open Bugs (from `docs/planning/vision/JJFlex-TODO.md`)
- BUG-004: System.IO.Ports crash on shutdown (downgrade or guard FlexKnob.Dispose)
- BUG-013: Duplicate QSO warning beep not playing
- BUG-015: F6 double-announces "Radio pane" in Logging Mode
- BUG-016: RNN / DSP toggles lack feature gating in Modern menu
- BUG-023: "Connect while connected" flow needs confirmation guard

### Other
- TX tones: user may provide custom .wav files (roger beep style)
- 4O3A ecosystem integration plan

## Build Commands

```batch
# Debug build for testing
dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal

# Clean + rebuild Release (triggers NSIS installer)
dotnet clean JJFlexRadio.vbproj -c Release -p:Platform=x64 && dotnet restore JJFlexRadio.vbproj -p:Platform=x64 && dotnet build JJFlexRadio.vbproj -c Release -p:Platform=x64 --verbosity minimal

# Both installers
build-installers.bat
```

---

## Sprint 21: Resonant Signal Sculpt — IN PROGRESS

**Status:** Setup complete. 5 tracks ready for parallel execution.

**Branches and worktrees:**
| Track | Branch | Worktree | Focus |
|-------|--------|----------|-------|
| A | `sprint21/track-a` | `C:\dev\JJFlex-NG` | Meter Sonification Engine + Peak Watcher + Earcon Infrastructure |
| B | `sprint21/track-b` | `C:\dev\jjflex-21b` | Audio Workshop + ScreenFields TX Expansion + Earcon Explorer |
| C | `sprint21/track-c` | `C:\dev\jjflex-21c` | TX Sculpting Keyboard + Leader Key System + Command Finder Extension |
| D | `sprint21/track-d` | `C:\dev\jjflex-21d` | App Rename — JJ Flexible Radio Access |
| E | `sprint21/track-e` | `C:\dev\jjflex-21e` | Compiled Help File (CHM) |

**Merge plan:** A, B, C, E merge in any order. D merges LAST (touches display strings).

---

*Updated: Mar 8, 2026 — Sprint 21 setup complete. 5 parallel tracks with worktrees and TRACK-INSTRUCTIONS.md in each. Version target: 4.1.16.*
