# Sprint 9 Test Checklist — Prioritized

**How to use:** Work through Priority 1 first (these are the dialogs you use most). If P1 passes, move to P2, then P3. Mark each cell with ✅ (pass), ❌ (fail + note), or ⏭️ (skipped). JAWS/NVDA columns: test that the dialog title is announced on open, tab order is logical, and controls are labeled.

**Quick test for each dialog:**
1. Open it (note how — menu path or hotkey listed below)
2. Tab through all controls — logical order? No tab traps?
3. Press ESC — does it close?
4. With screen reader: title announced? Controls labeled?

---

## Priority 1 — Daily Use (test these first!)

| # | Dialog | How to Open | What to Check | Pass? | JAWS | NVDA | Notes |
|---|--------|-------------|---------------|-------|------|------|-------|
| 1 | RigSelector | Launch app (or Radio→Select Radio) | Pick radio, OK connects. Cancel works. | ☐ | ☐ | ☐ | |
| 2 | FreqInput | Ctrl+F (in radio mode) | Type freq, OK tunes radio, ESC cancels | ☐ | ☐ | ☐ | |
| 3 | MemoriesDialog | Radio→Memories | List shows, select works, add/delete | ☐ | ☐ | ☐ | |
| 4 | TXControls | Radio→TX Controls | Power/mic sliders work, changes apply to radio | ☐ | ☐ | ☐ | |
| 5 | ScanDialog | Radio→Scan | Start/stop, freq range, step size | ☐ | ☐ | ☐ | |
| 6 | LogEntryDialog | Menu→Log Entry (if not in logging mode) | Fields fill, log contact works, clear | ☐ | ☐ | ☐ | |
| 7 | FindLogEntry | Ctrl+F (in log) | Search call/date, results navigate log | ☐ | ☐ | ☐ | |
| 8 | ShowBands | Radio→Bands | Band list, click tunes radio | ☐ | ☐ | ☐ | |
| 9 | DefineCommands | Operations→Define Commands | All 5 scope tabs show, assign/clear keys work | ☐ | ☐ | ☐ | |
| 10 | EscDialog | Various (confirms quit, etc.) | Yes/No/Cancel buttons work, ESC picks Cancel | ☐ | ☐ | ☐ | |

## Priority 2 — Regular Use

| # | Dialog | How to Open | What to Check | Pass? | JAWS | NVDA | Notes |
|---|--------|-------------|---------------|-------|------|------|-------|
| 11 | PersonalInfo | Operations→Personal Info | Edit call/name/grid, OK saves, Cancel discards | ☐ | ☐ | ☐ | |
| 12 | TNFDialog | Radio→TNF | Add/remove/adjust TNFs on panadapter | ☐ | ☐ | ☐ | |
| 13 | EqualizerDialog | Radio→Equalizer | RX/TX sliders move, presets load, apply/reset | ☐ | ☐ | ☐ | |
| 14 | ClusterDialog | Radio→Cluster | Connect, spots appear, click tunes radio | ☐ | ☐ | ☐ | |
| 15 | ExportDialog | File→Export | Pick format + destination, export runs | ☐ | ☐ | ☐ | |
| 16 | ImportDialog | File→Import | Pick file, import runs, count shown | ☐ | ☐ | ☐ | |
| 17 | CWMessageAdd | CW→Add Macro | Type message, assign F-key, save | ☐ | ☐ | ☐ | |
| 18 | CWMessageUpdate | CW→Edit Macro | Edit existing macro text, save | ☐ | ☐ | ☐ | |
| 19 | MenusDialog | Operations→Menus | Menu config loads, changes save | ☐ | ☐ | ☐ | |
| 20 | Profile | Operations→Profile | View/edit fields, save works | ☐ | ☐ | ☐ | |

## Priority 3 — Setup/Infrequent Use

| # | Dialog | How to Open | What to Check | Pass? | JAWS | NVDA | Notes |
|---|--------|-------------|---------------|-------|------|------|-------|
| 21 | AuthDialog | SmartLink→Login | WebView2 Auth0 flow completes, token saved | ☐ | ☐ | ☐ | |
| 22 | SmartLinkAccount | SmartLink→Account | Account info displays, buttons work | ☐ | ☐ | ☐ | |
| 23 | AutoConnectSettings | Operations→Auto Connect | Toggle on/off, pick radio, OK saves | ☐ | ☐ | ☐ | |
| 24 | AutoConnectFailed | (auto — when connect fails) | Error reads clearly, retry/cancel work | ☐ | ☐ | ☐ | |
| 25 | RadioInfo | Help→Radio Info | Read-only radio details display | ☐ | ☐ | ☐ | |
| 26 | Welcome | Help→Welcome | Text reads, OK closes | ☐ | ☐ | ☐ | |
| 27 | About | Help→About | Version/credits show, OK closes | ☐ | ☐ | ☐ | |
| 28 | LoginName | (multi-op login) | Enter name, OK proceeds | ☐ | ☐ | ☐ | |
| 29 | SetupKeys | Operations→Keys | Key list shows, edit works | ☐ | ☐ | ☐ | |
| 30 | ShowKeys | Help→Show Keys | Key assignments display | ☐ | ☐ | ☐ | |
| 31 | ComInfo | Help→COM Info | Serial port details display | ☐ | ☐ | ☐ | |
| 32 | TraceAdmin | Operations→Tracing | Trace toggle works | ☐ | ☐ | ☐ | |
| 33 | LogTemplate | Logging→Template | Fields edit, save/load | ☐ | ☐ | ☐ | |
| 34 | LogCharacteristics | File→Log Characteristics | Stats display correctly | ☐ | ☐ | ☐ | |
| 35 | LOTWMerge | File→LOTW Merge | Import LOTW file, merge runs | ☐ | ☐ | ☐ | |
| 36 | WattMeterConfig | Operations→Watt Meter | W2 config saves | ☐ | ☐ | ☐ | |
| 37 | ManageGroups | Radio→Memory Groups | Add/edit/delete groups | ☐ | ☐ | ☐ | |
| 38 | ATUMemories | Radio→ATU Memories | ATU memory list (6300/6400 only) | ☐ | ☐ | ☐ | |
| 39 | Lister | Various (list picker) | List shows, selection returns | ☐ | ☐ | ☐ | |
| 40 | MessageDialog | Various (error/info) | Message reads, button works | ☐ | ☐ | ☐ | |
| 41 | PanList | Radio→Panadapters | Pan list shows | ☐ | ☐ | ☐ | |
| 42 | ShowHelp | Help→Help | Help text shows | ☐ | ☐ | ☐ | |
| 43 | ShowStationNames | Help→Station Names | Station list shows | ☐ | ☐ | ☐ | |
| 44 | ReverseBeacon | Radio→Reverse Beacon | RBN connection/display | ☐ | ☐ | ☐ | |
| 45 | CommandFinder | Operations→Find Command | Search commands, shows key binding | ☐ | ☐ | ☐ | |
| 46 | CMDLine | (debug) | Command line input works | ☐ | ☐ | ☐ | |
| 47 | ProfileWorker | (background profile ops) | Progress shown, completes | ☐ | ☐ | ☐ | |

## Cross-Cutting (test after P1 passes)

| # | Test | Steps | Pass? | Notes |
|---|------|-------|-------|-------|
| 48 | Mode switching | Classic↔Modern↔Logging via Ctrl+Shift+M/L — menus change correctly | ☐ | |
| 49 | 5-scope hotkeys | Set a key in each scope tab, verify it fires only in that mode | ☐ | |
| 50 | ESC-close all | Every dialog closes on ESC (no exceptions) | ☐ | |
| 51 | Tab order all | No dialog traps keyboard focus in a loop | ☐ | |
| 52 | x86 smoke test | Install x86, launch, connect, open 3-4 dialogs | ☐ | |

---

**Bug reporting:** If something fails, write the dialog name + what happened in the Notes column. We'll fix in a patch before Sprint 10.
