# Sprint 11 Test Matrix — WPF Adapters + WinForms Removal

**Sprint:** 11 (Kill the Old Forms)
**Date:** Feb 15, 2026
**Plan:** `docs/planning/frolicking-forging-map.md`

## Track A — WPF Adapters (PanAdapterManager, WpfFilterAdapter, WpfMemoryManager)

### Pan Adapter / Braille Waterfall

| # | Test | Steps | JAWS | NVDA |
|---|------|-------|------|------|
| A1 | Pan adapter initializes | Connect to radio → power on → waterfall line appears | ☐ | ☐ |
| A2 | Braille waterfall output | Tab to pan field (Classic) → braille line displays signal levels | ☐ | ☐ |
| A3 | Frequency change updates pan | Change frequency → pan segment updates, new waterfall line | ☐ | ☐ |
| A4 | Mode change updates pan | Change mode (e.g., USB→CW) → pan reconfigures | ☐ | ☐ |
| A5 | Pan segment navigation | PageUp/PageDown in pan field → moves to prev/next segment | ☐ | ☐ |
| A6 | Pan range display | Low/high frequency text updates when segment changes | ☐ | ☐ |
| A7 | Zero beat (CW) | Switch to CW → tune near signal → zero beat returns valid freq | ☐ | ☐ |
| A8 | Pan copy (multi-slice) | Create 2nd slice → copy pan from 1st → 2nd slice has pan data | ☐ | ☐ |
| A9 | User-defined segment | Set custom low/high range → waterfall respects range | ☐ | ☐ |
| A10 | Operator change | Switch operators → pan config reloads (panRanges, configInfo) | ☐ | ☐ |

### Memory Manager

| # | Test | Steps | JAWS | NVDA |
|---|------|-------|------|------|
| A11 | Memory list populates | Power on → memories sorted by group then name | ☐ | ☐ |
| A12 | Select memory by index | Next/prev memory hotkey → radio tunes to memory freq | ☐ | ☐ |
| A13 | Select memory by name | Memory scan/select by name → radio tunes correctly | ☐ | ☐ |
| A14 | Memory count in status bar | Power on → status bar shows "Memories: N" | ☐ | ☐ |
| A15 | Memory names list | Command requests memory list → returns sorted names | ☐ | ☐ |
| A16 | Empty memory handling | Radio with 0 memories → no crash, channel = -1 | ☐ | ☐ |

### RigFields_t Refactor

| # | Test | Steps | JAWS | NVDA |
|---|------|-------|------|------|
| A17 | RigUpdate callback fires | Power on → poll timer calls RigUpdate every 100ms (no crash) | ☐ | ☐ |
| A18 | Memories accessible via RigFields | `RigControl.RigFields.Memories` returns IMemoryManager | ☐ | ☐ |

## Track B — MainWindow Wiring + Form1 Kill

### Radio Connection

| # | Test | Steps | JAWS | NVDA |
|---|------|-------|------|------|
| B1 | Manual connect (local) | Launch → RigSelector → pick local radio → connects | ☐ | ☐ |
| B2 | Manual connect (SmartLink) | Launch → RigSelector → SmartLink → auth → connects | ☐ | ☐ |
| B3 | Auto-connect (local) | Configure auto-connect → restart → auto-connects | ☐ | ☐ |
| B4 | Auto-connect (SmartLink) | Configure SmartLink auto → restart → auto-connects | ☐ | ☐ |
| B5 | Connection failure | Start with radio off → error message, can retry or cancel | ☐ | ☐ |

### Power On/Off

| # | Test | Steps | JAWS | NVDA |
|---|------|-------|------|------|
| B6 | Power on | Connect → turn on radio → controls enable, status says "On" | ☐ | ☐ |
| B7 | Power off | Turn off radio → controls disable, status says "Off" | ☐ | ☐ |
| B8 | Power cycle | On → Off → On → no crash, controls re-enable | ☐ | ☐ |

### Poll Timer / Status Updates

| # | Test | Steps | JAWS | NVDA |
|---|------|-------|------|------|
| B9 | Frequency display updates | Tune radio (VFO knob) → frequency display tracks in real-time | ☐ | ☐ |
| B10 | Mode display updates | Change mode on radio → mode combo updates | ☐ | ☐ |
| B11 | Status bar updates | Various operations → status bar fields update | ☐ | ☐ |

### Button Operations

| # | Test | Steps | JAWS | NVDA |
|---|------|-------|------|------|
| B12 | Transmit toggle | Press transmit button/hotkey → TX on, press again → TX off | ☐ | ☐ |
| B13 | ATU toggle | Press ATU button → tuner on/off toggles | ☐ | ☐ |
| B14 | ATU SWR display | During manual tune → SWR value shows on button | ☐ | ☐ |

### Memory Dialog (KeyCommands)

| # | Test | Steps | JAWS | NVDA |
|---|------|-------|------|------|
| B15 | Open memory dialog | Press memory hotkey → WPF MemoriesDialog opens | ☐ | ☐ |
| B16 | Select memory from dialog | Pick memory in dialog → radio tunes | ☐ | ☐ |
| B17 | Memory dialog accessibility | Tab through dialog → all controls announced | ☐ | ☐ |

### Form1 Replacement

| # | Test | Steps | JAWS | NVDA |
|---|------|-------|------|------|
| B18 | App launches | Start app → MainWindow appears (no visible Form1) | ☐ | ☐ |
| B19 | Window title | Window title shows app name + radio info | ☐ | ☐ |
| B20 | Clean shutdown | File→Exit → app closes cleanly, no crash dump | ☐ | ☐ |
| B21 | Shutdown while connected | Exit while radio connected → disconnects then closes | ☐ | ☐ |

## Track C — Dead Code Deletion

| # | Test | Steps | JAWS | NVDA |
|---|------|-------|------|------|
| C1 | No Flex6300Filters refs | `grep -r "Flex6300Filters"` → 0 hits in .cs/.vb | ☐ | ☐ |
| C2 | No RadioBoxes refs | `grep -r "RadioBoxes"` → 0 hits in .cs/.vb (except comments) | ☐ | ☐ |
| C3 | No FlexMemories refs | `grep -r "FlexMemories"` → 0 hits in .cs/.vb (except comments) | ☐ | ☐ |
| C4 | RadioBoxes.dll absent | Output folder has no RadioBoxes.dll | ☐ | ☐ |
| C5 | About dialog | Help→About → no crash (RadioBoxes version line removed) | ☐ | ☐ |

## Cross-Cutting Tests

| # | Area | Test Steps | JAWS | NVDA |
|---|------|------------|------|------|
| X1 | Mode switching | Switch Classic↔Modern↔Logging → menus update, no crash | ☐ | ☐ |
| X2 | Hotkeys fire | All scope hotkeys still work (CW, Transmit, Tune, etc.) | ☐ | ☐ |
| X3 | DSP toggles | Modern menu DSP items (NR, ANF, NB, etc.) still toggle | ☐ | ☐ |
| X4 | Scan operation | Start scan → frequency scans through memories → stop scan | ☐ | ☐ |
| X5 | CW send | Type CW message → radio transmits CW | ☐ | ☐ |
| X6 | Logging mode | Ctrl+Shift+L → logging panel opens, log a QSO | ☐ | ☐ |
| X7 | Station lookup | Ctrl+L → lookup dialog, enter call, results display | ☐ | ☐ |
| X8 | Command finder | Ctrl+/ → finder opens, search works | ☐ | ☐ |
| X9 | Cluster connect | Radio→Cluster → connects, spots appear | ☐ | ☐ |
| X10 | FlexControl knob | Knob turns → frequency changes (if knob connected) | ☐ | ☐ |

## Regression: WPF Dialogs (Sprint 9)

Spot-check that dialogs converted in Sprint 9 still work after adapter changes:

| # | Dialog | Test Steps | JAWS | NVDA |
|---|--------|------------|------|------|
| R1 | TXControls | Radio→TX Controls → adjust power, changes apply | ☐ | ☐ |
| R2 | TNF | Radio→TNF → add/remove TNF | ☐ | ☐ |
| R3 | Equalizer | Radio→Equalizer → adjust EQ, apply | ☐ | ☐ |
| R4 | ATU Memories | Radio→ATU Memories → view/select ATU memories | ☐ | ☐ |
| R5 | PersonalInfo | Operations→Personal Info → edit, save | ☐ | ☐ |
| R6 | DefineCommands | Operations→Define Commands → tabs, assign keys | ☐ | ☐ |

## Build Verification

| # | Check | x64 | x86 |
|---|-------|-----|-----|
| BV1 | Clean build (0 errors) | ☐ | ☐ |
| BV2 | Installer generates | ☐ | ☐ |
| BV3 | App launches from installer | ☐ | ☐ |
| BV4 | Connect to radio (local) | ☐ | ☐ |
| BV5 | Connect to radio (SmartLink) | ☐ | ☐ |
| BV6 | No RadioBoxes.dll in output | ☐ | ☐ |

## Screen Reader Matrix

| Screen Reader | Version | OS | Status |
|---------------|---------|-----|--------|
| JAWS | Latest | Win 11 | ☐ Not tested |
| NVDA | Latest | Win 11 | ☐ Not tested |

## Notes

- **High risk area:** Pan adapter tests (A1–A10). PanAdapterManager extraction is the most complex change — thread-safe waterfall processing, braille rendering, segment management. If anything regresses, it shows up here.
- **Medium risk:** Radio wiring (B1–B14). OpenParms, power handlers, poll timer moved from Form1 to MainWindow. Connection and power cycling are critical paths.
- **Low risk:** Dead code deletion (C1–C5). Straightforward file removal, mainly build verification.
- Test A8 (pan copy) requires a radio with 2+ available slices.
- Test B2/B4 (SmartLink) requires internet + SmartLink account.
- Test X10 (FlexControl knob) requires physical USB knob — skip if not available.
