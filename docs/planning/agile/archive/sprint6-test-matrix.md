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
| D4 | Ctrl+1 sends CW message (was F5) | PASS | No CW messages configured — no crash, silent as expected. Sprint 7: add spoken feedback when no messages configured. |

## E. Hotkey Settings UI

| # | Test | Pass/Fail | Notes |
|---|------|-----------|-------|
| E1 | Dialog opens with 3 tabs (Global/Radio/Logging) | PASS | Global = active in all modes (not system-wide outside app). |
| E2 | Rebind a key → works in app | PASS | |
| E3 | Same-scope conflict detected | ~~FAIL~~ FIXED | **BUG-012**: Now auto-clears conflicting binding (VS Code style). Save blocked if conflicts remain. |
| E3b | Cross-scope same key → no conflict | PASS | Alt+Z assigned to Radio (Show Memories) and Logging (Write Log Entry) — no conflict, both fire correctly in their scope. |
| E4 | Reset Selected reverts one key | PASS | |
| E4b | Reset All reverts whole tab | PASS | SR announced reset, Ctrl+W still worked after reset. BUG-014 contamination guard verified. |

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
| G2 | Classic/Modern radio hotkeys work | PASS | Ctrl+Shift+M toggles correctly. From Logging Mode, exits to previous base mode first (new behavior). |
| G3 | CW messages on Ctrl+1-7, F12 stops | PASS | No CW messages configured — no crash, silent. Sprint 7: add spoken feedback. |
| G4 | Legacy KeyDefs.xml imports as Global | N/A | No legacy file available — fresh defaults loaded. BUG-014 fix verified via Reset All test. |

## H. Additional Hotkeys (post-matrix)

| # | Test | Pass/Fail | Notes |
|---|------|-----------|-------|
| H1 | Ctrl+Shift+L toggles Logging Mode | PASS | "Entering Logging Mode" / "Returning to Modern mode" — correct announcements. |
| H2 | Ctrl+Shift+M toggles Classic/Modern | PASS | Works from all modes including Logging (exits Logging first). |
| H3 | Alt+Z fires per-scope (Radio vs Logging) | PASS | Show Memories in Radio, Write Log Entry in Logging — no conflict. |
| H4 | F6 pane toggle in Logging Mode | PASS | Focus switches between RadioPane and LogPanel. **BUG-015**: Double-announces "Radio pane" — logged for Sprint 7. |

## New Bugs Found During Testing

| Bug | Description | Priority | Sprint |
|-----|-------------|----------|--------|
| BUG-015 | F6 in Logging Mode double-announces "Radio pane" (FreqBox Enter handler + AccessibleName both speak) | Low | 7 |

## Enhancement Requests

| Item | Description | Sprint |
|------|-------------|--------|
| CW feedback | Ctrl+1-7 and F12 should speak short message when no CW messages configured | 7 |
