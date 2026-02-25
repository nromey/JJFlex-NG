# Sprint 9 Test Matrix — WPF Dialog Conversion

**Sprint:** 9 (All Remaining Dialogs to WPF)
**Date:** Feb 15, 2026
**Build:** sprint9/track-a branch, clean x64+x86 Release (0 errors)

## Track A — High-Priority Dialogs

| # | Dialog | Trigger | Test Steps | JAWS | NVDA |
|---|--------|---------|------------|------|------|
| 1 | RigSelector | Startup (no saved radio) | Launch app → selector appears → pick radio → OK | ☐ | ☐ |
| 2 | Welcome | First launch / Help→Welcome | Verify all text reads, OK closes | ☐ | ☐ |
| 3 | PersonalInfo | Operations→Personal Info | Edit call/name/grid, OK saves, Cancel discards | ☐ | ☐ |
| 4 | Profile | Operations→Profile | View/edit profile fields, save | ☐ | ☐ |
| 5 | AuthDialog | SmartLink→Login (WebView2) | Auth0 login flow completes, token saved | ☐ | ☐ |
| 6 | RadioInfo | Help→Radio Info | Read-only radio details, ESC closes | ☐ | ☐ |
| 7 | AutoConnect Settings | Operations→Auto Connect | Toggle auto-connect, set radio, OK saves | ☐ | ☐ |
| 8 | AutoConnect Failed | Auto-connect failure | Error message reads, retry/cancel buttons work | ☐ | ☐ |
| 9 | SmartLinkAccount | SmartLink→Account | Account details display, buttons work | ☐ | ☐ |
| 10 | LoginName | Multi-operator login | Enter operator name, OK proceeds | ☐ | ☐ |
| 11 | ProfileWorker | Background profile ops | Progress feedback, completes or errors cleanly | ☐ | ☐ |

## Track B — Radio Operation Dialogs

| # | Dialog | Trigger | Test Steps | JAWS | NVDA |
|---|--------|---------|------------|------|------|
| 12 | FlexMemories | Radio→Memories | List memories, select, apply, add/delete | ☐ | ☐ |
| 13 | TXControls | Radio→TX Controls | Adjust power, mic gain, etc., changes apply | ☐ | ☐ |
| 14 | DefineCommands | Operations→Define Commands | All 5 scope tabs visible, assign/clear keys | ☐ | ☐ |
| 15 | LogEntry | Ctrl+L / Logging mode | Enter QSO fields, log contact, clear | ☐ | ☐ |
| 16 | FindLogEntry | Ctrl+F in log | Search by call/date, results navigate | ☐ | ☐ |
| 17 | ExportForm | File→Export | Select format, destination, export completes | ☐ | ☐ |
| 18 | ImportForm | File→Import | Select file, import completes, count shown | ☐ | ☐ |
| 19 | Scan | Radio→Scan | Start/stop scan, frequency range settings | ☐ | ☐ |
| 20 | EscDialog | Various ESC handlers | Confirmation prompt, Yes/No work | ☐ | ☐ |
| 21 | FlexEq | Radio→Equalizer | RX/TX EQ sliders, presets, apply/reset | ☐ | ☐ |
| 22 | FlexTNF | Radio→TNF | Add/remove/adjust TNFs | ☐ | ☐ |
| 23 | ComInfo | Help→COM Info | Serial port details display | ☐ | ☐ |
| 24 | Menus | Operations→Menus | Menu configuration, save/load | ☐ | ☐ |

## Track C — Low-Priority + Library Dialogs

| # | Dialog | Trigger | Test Steps | JAWS | NVDA |
|---|--------|---------|------------|------|------|
| 25 | About | Help→About | Version, credits display, OK closes | ☐ | ☐ |
| 26 | FreqInput | Ctrl+F (radio) | Enter frequency, OK tunes radio | ☐ | ☐ |
| 27 | ShowBands | Radio→Bands | Band list, select tunes radio | ☐ | ☐ |
| 28 | CW Macros (various) | CW→Macros | Edit/send macros, function keys work | ☐ | ☐ |
| 29 | MessageForm | Various error/info msgs | Message reads, buttons work | ☐ | ☐ |
| 30 | ClusterForm | Radio→Cluster | Connect, spots display, click tunes | ☐ | ☐ |
| 31 | SetupKeys | Operations→Keys | Key assignments display and edit | ☐ | ☐ |
| 32 | LogTemplate | Logging→Template | Template fields edit, save/load | ☐ | ☐ |
| 33 | WattMeterConfig | Operations→Watt Meter | W2 config fields, save | ☐ | ☐ |
| 34+ | Remaining C dialogs | Various | Open, interact, close — no crashes | ☐ | ☐ |

## Cross-Cutting Tests

| # | Area | Test Steps | JAWS | NVDA |
|---|------|------------|------|------|
| 35 | ESC-close (all dialogs) | Open any dialog, press ESC → closes | ☐ | ☐ |
| 36 | Tab order (all dialogs) | Tab through controls — logical order, no traps | ☐ | ☐ |
| 37 | Focus on open | Each dialog announces its title on open | ☐ | ☐ |
| 38 | DialogStyles consistency | Buttons, labels use shared styles (visual check) | ☐ | ☐ |
| 39 | Keyboard-only operation | All dialogs usable without mouse | ☐ | ☐ |
| 40 | Mode switching | Switch Classic↔Modern↔Logging, menus update | ☐ | ☐ |
| 41 | 5-scope hotkeys | DefineCommands tabs match 5 scopes, keys fire in correct mode | ☐ | ☐ |

## Build Verification

| # | Check | x64 | x86 |
|---|-------|-----|-----|
| 42 | Clean build (0 errors) | ✅ | ✅ |
| 43 | Installer generates | ☐ | ☐ |
| 44 | App launches from installer | ☐ | ☐ |
| 45 | Connect to radio (local) | ☐ | ☐ |
| 46 | Connect to radio (SmartLink) | ☐ | ☐ |

## Screen Reader Matrix

| Screen Reader | Version | OS | Status |
|---------------|---------|-----|--------|
| JAWS | Latest | Win 11 | ☐ Not tested |
| NVDA | Latest | Win 11 | ☐ Not tested |

## Notes
- Form1.vb, LogPanel.vb, RadioPane.vb still compile as dead code (blocked by type dependencies)
- RadioBoxes/ still present (blocked by FlexBase.cs coupling)
- Sprint 10 will handle the decoupling to allow final deletion
- Focus testing is critical — WPF dialogs should announce title, first control should be focused
