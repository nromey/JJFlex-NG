# Sprint 6 Test Matrix

Refer to Sprint-06-qrp-sidetone-paddles.md for step-by-step instructions.

## A. Callbook Fallback

| # | Test | Pass/Fail | Notes |
|---|------|-----------|-------|
| A1 | QRZ valid creds → lookup works | PASS | |
| A2 | QRZ not-found → no failure counted | PASS | KZ4ABC came up blank but no error or crash. |
| A3 | QRZ auth fail x3 → HamQTH fallback | PASS | |
| A4 | HamQTH no creds → built-in account | PASS | |

## B. Modern Menu Accessibility

| # | Test | JAWS | NVDA | Notes |
|---|------|------|------|-------|
| B1 | Top-level menus speak | PASS | PASS | |
| B2 | Submenu items speak | PASS | PASS | Immediate submenus good; stubs do nothing on Enter. |
| B3 | "Coming soon" stubs speak | ~~FAIL~~ FIXED | ~~FAIL~~ FIXED | **BUG-011**: SR read `.Text` not `.AccessibleName`. Fixed: "coming soon" now in `.Text` directly. |

## C. QRZ Logbook Upload

| # | Test | Pass/Fail | Notes |
|---|------|-----------|-------|
| C1 | Validate button → shows stats | PASS | |
| C2 | Save QSO → "Logged to QRZ" | PASS | Feature idea: per-QSO "Upload to QRZ" checkbox with settings default + Ctrl+W to write. |
| C3 | Invalid key → local save OK, error announced | PASS | Verified via validation; full QSO save to QRZ not tested with real QSO. |
| C4 | QRZ disabled → no QRZ activity | PASS | |

## D. Hotkey Scoping

| # | Test | Pass/Fail | Notes |
|---|------|-----------|-------|
| D1 | Alt+C: CW Zero Beat (Radio) vs Call Sign (Logging) | ~~FAIL~~ FIXED | **BUG-010**: MenuStrip ate Alt+letter before DoCommand. Fixed in ProcessCmdKey. |
| D2 | Alt+S: Start Scan (Radio) vs State (Logging) | ~~FAIL~~ FIXED | **BUG-010**: Same root cause as D1. |
| D3 | F1 works in both modes | ~~FAIL~~ FIXED | **BUG-010**: F1 in logging mode — DoCommand not reached. Fixed in ProcessCmdKey. |
| D3b | Ctrl+/ works in both modes | ~~FAIL~~ FIXED | **BUG-010**: Intermittent — focus-dependent. Fixed: now routed through ProcessCmdKey always. |
| D4 | Ctrl+1 sends CW message (was F5) | UNTESTED | No CW messages configured. Suggest debug trace to verify. |

## E. Hotkey Settings UI

| # | Test | Pass/Fail | Notes |
|---|------|-----------|-------|
| E1 | Dialog opens with 3 tabs (Global/Radio/Logging) | PASS | Global = active in all modes (not system-wide outside app). |
| E2 | Rebind a key → works in app | PASS | |
| E3 | Same-scope conflict detected | ~~FAIL~~ FIXED | **BUG-012**: Now auto-clears conflicting binding (VS Code style). Save blocked if conflicts remain. |
| E3b | Cross-scope same key → no conflict | UNTESTED | Too few Radio/Logging keys to verify. Retest after more keys assigned. |
| E4 | Reset Selected reverts one key | PASS | |
| E4b | Reset All reverts whole tab | PASS | |

## F. Command Finder

| # | Test | Pass/Fail | Notes |
|---|------|-----------|-------|
| F1 | Ctrl+/ opens, search filters results | ~~INTERMITTENT~~ FIXED | **BUG-010**: Focus-dependent. Fixed in ProcessCmdKey — now always fires. 53 keys in Global correct. |
| F2 | Select + Enter executes command | PASS | |
| F3 | Radio mode: no Logging-only commands shown | ~~FAIL~~ FIXED | **BUG-010**: F6 regression — couldn't switch to Radio mode. Root cause was ProcessCmdKey. |
| F3b | Logging mode: no Radio-only commands shown | PASS | Only 3 logging keys mapped so limited test. |
| F4 | Screen reader announces dialog, results, count | PASS | "Truly awesome feature." |

## G. Regression

| # | Test | Pass/Fail | Notes |
|---|------|-----------|-------|
| G1 | Logging Mode end-to-end QSO | MOSTLY PASS | **BUG-013**: Dup beep not audible; speech "worked x calls" works. Logged for investigation. |
| G2 | Classic/Modern radio hotkeys work | NEEDS RETEST | Classic/modern switch works. Hotkey correctness needs retest after BUG-010 fix. |
| G3 | CW messages on Ctrl+1-7, F12 stops | UNTESTED | No CW messages configured. Suggest adding debug trace. |
| G4 | Legacy KeyDefs.xml imports as Global | UNKNOWN | 53 Global keys seen — may be correct if defaults were loaded fresh. |
