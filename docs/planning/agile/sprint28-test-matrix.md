# 4.1.17 Release Test Matrix — Sprints 26 + 27 + 28

**Tested by:** Noel Romey (K5NER)
**Date:** ___
**Build:** Debug x64 (version ___, commit ___)
**Screen Reader:** NVDA primary, JAWS secondary
**Radio(s):** Noel's own rig primary; Don's FLEX-6300 "6300 inshack" via SmartLink where noted

---

## How to Use This Matrix

This matrix is user-flow organised rather than sprint-organised — each section corresponds to a thing a user actually does with the application, and the tests within it exercise whichever sprint work contributes to that flow. Pass/Fail/Defer markers live at the end of each test line.

### Tester Availability Tags

Every test is tagged with who needs to be present to run it. If you do not have the right tester available at test time, skip the test and mark it `DEFER` — it should not block the rest of the matrix.

- `[S]` — Solo. Noel at his own radio, local or SmartLink, no other humans needed.
- `[D]` — Needs Don. Either Don is connected via SmartLink to his 6300, or Noel is testing against Don's radio over SmartLink.
- `[J]` — Needs Justin. Either Justin is connected from his Mac-side setup, or Noel is testing against Justin's FLEX-8400 over SmartLink.
- `[M]` — Multi-client. At least two active clients connected to the same radio at the same time (MultiFlex testing).
- `[W]` — Waits for conditions. Needs natural RF conditions that cannot be scheduled — a crowded 40m pileup, an evening 80m thunderstorm, a contest weekend, and so on.

### Pass / Fail / Defer Convention

Mark each test with one of:

- `PASS` — the test executed and produced the expected result.
- `FAIL: <short note>` — the test ran but the result was wrong; a short note captures what was wrong.
- `DEFER: <reason>` — the test was not attempted this pass, with the reason recorded (missing tester, conditions, time-boxed, and so on).

A `DEFER` is not a blocker for the release. Deferred tests accumulate in the "Deferred to Post-Release Testing" section at the bottom, along with the condition that would unblock each one.

### Setup Before Starting

- Pull the latest commit on `sprint28/home-key-qsk`, confirm clean working tree.
- Run a fresh Debug build: `build-debug.bat` (no flags). Confirm exit code 0 and matching version stamp.
- Verify exe version: `powershell -Command "(Get-Item 'bin\x64\Debug\net10.0-windows\win-x64\JJFlexRadio.exe').VersionInfo.FileVersion"`.
- Launch JJ Flexible Radio Access. Confirm "Welcome to JJ Flexible Radio Access" speaks on launch.
- Connect to a radio on a known starting band (20m USB is a common choice).
- NVDA (or JAWS) running at a verbosity you are used to.

---

## A — First-Time Setup and First Connection

Covers local discovery, Auto-Connect, SmartLink remote connect via the Remote button on the RigSelector dialog, and the Radio-menu connect path.

- A.1 `[S]` Launch the application with no radios on the network. Confirm "Welcome to JJ Flexible Radio Access" speaks, followed by the empty-list announcement within ~500ms. Result: ___
- A.2 `[S]` Launch with your own radio powered on and reachable on the local network. Confirm the radio is discovered and announced by nickname. Press Enter on its row to connect. Result: ___
- A.3 `[S]` From the **Radio** menu, choose **Connect to Radio**. The Select Radio dialog opens. Press **Cancel** (or Escape). Confirm focus returns to the main window cleanly with no disconnect side-effects. Result: ___
- A.4 `[S]` Connect normally, then disconnect (Radio menu → Disconnect). Confirm disconnect speech ("Disconnecting from …") and that the status bar updates to "Ready — no radio connected." Result: ___
- A.5 `[D]` From the Select Radio dialog with only local radios listed, press **Remote** (Alt+R). Confirm the browser window opens for SmartLink sign-in. Sign in; confirm remote radios appear. Result: ___
- A.6 `[D]` After signing in via Remote, select Don's 6300 from the list and press **Connect** (Alt+N or Enter). Confirm full status summary speaks on successful connect. Result: ___
- A.7 `[S]` Enable Auto-Connect on your own radio. Disconnect, relaunch. Confirm the radio auto-reconnects without prompting. Result: ___
- A.8 `[S]` With Auto-Connect enabled, launch while the radio is powered off. Confirm the auto-connect attempt times out cleanly and the Select Radio dialog becomes available. Result: ___
- A.9 `[D]` **Connection-state announcement regressions check:** connect to Don's 6300 via SmartLink, then disconnect. Confirm the Sprint 26 session-owner transitions speak sensibly ("Disconnecting from…", reconnect attempts if any). Result: ___

---

## B — Day-to-Day Operating: Tuning, Modes, Bands, Filters

Covers the full operating surface: arrow-key tuning, direct frequency entry, band jumps, mode switching, filter/DSP toggles via the leader key, audio-level adjustments, and RIT/XIT.

### Tuning and Frequency Entry

- B.1 `[S]` Press `F2`. Confirm current frequency speaks with band (e.g., "14.225 megahertz, 20 meters"). Result: ___
- B.2 `[S]` Press `Ctrl+F`, type `7040`, press Enter. Confirm radio tunes to 7.040 MHz and speaks confirmation. Result: ___
- B.3 `[S]` Arrow-tune Up and Down on the Frequency field. Confirm each step announces the new frequency (or respects debounce if enabled). Result: ___
- B.4 `[S]` Press `Ctrl+J` then `D` to toggle tuning debounce. Confirm "Tuning debounce on" / "Tuning debounce off" announcement. Tune rapidly with debounce on; confirm only the final frequency speaks. Result: ___
- B.5 `[S]` In CW mode, press `Alt+Z` to zero-beat a received CW signal. Confirm zero-beat adjustment happens. Result: ___

### Band Navigation

- B.6 `[S]` Press each of `F3` through `F9`. Confirm each jumps to the expected band (160m, 80m, 40m, 20m, 15m, 10m, 6m) and speaks band name. Result: ___
- B.7 `[S]` Press `Shift+F3`, `Shift+F4`, `Shift+F5`, `Shift+F6`. Confirm 60m, 30m, 17m, 12m respectively. Result: ___
- B.8 `[S]` Press `Alt+Up` and `Alt+Down` to cycle bands sequentially. Confirm each step speaks the new band. Result: ___
- B.9 `[S]` On 60m (via `Shift+F3`), press `Alt+Shift+Up` and `Alt+Shift+Down`. Confirm channel navigation speaks each of Channels 1-5 plus the digital segment, wrapping at the ends. Result: ___
- B.10 `[S]` On 60m, confirm automatic mode enforcement: voice channels speak "Upper Side Band," the digital segment switches to CW by default. Result: ___

### Mode Switching

- B.11 `[S]` Press `Alt+U`, `Alt+L`, `Alt+C`, `Alt+A`, `Alt+F`, `Alt+D`, `Alt+Shift+D` in turn. Confirm each switches to the expected mode and speaks it. Result: ___
- B.12 `[S]` Press `Alt+M` five times. Confirm cycling through available modes, each announced. Result: ___
- B.13 `[S]` Press `Alt+Shift+M` several times. Confirm reverse-direction cycling. Result: ___

### Filter and DSP

- B.14 `[S]` Press `Ctrl+J, N`. Confirm Legacy Noise Reduction toggles on/off with earcon + speech ("Noise Reduction on" / "off"). Result: ___
- B.15 `[S]` Press `Ctrl+J, B`, `Ctrl+J, W`. Confirm Noise Blanker and Wideband NB toggles. Result: ___
- B.16 `[S]` In CW mode, press `Ctrl+J, P`. Confirm Audio Peak Filter toggles on/off. In USB mode, press `Ctrl+J, P`. Confirm "Audio Peak Filter is CW only" speaks and the feature does not toggle. Result: ___
- B.17 `[S]` Press `Ctrl+J, R` (Neural NR) and `Ctrl+J, Shift+R` (PC-side NR). Confirm each toggles and speaks correctly. If your radio does not support Neural NR, confirm "Neural NR not available on this radio" speaks for `Ctrl+J, R`. Result: ___
- B.18 `[S]` Press `Ctrl+J, S` (Spectral NR) and `Ctrl+J, A` (Auto Notch). Confirm toggles. Result: ___
- B.19 `[S]` Press `Ctrl+J, F` (TX filter width) and `Ctrl+J, Shift+F` (RX filter width). Confirm each speaks the current filter edges and width. Result: ___
- B.20 `[S]` Press `Ctrl+Shift+[`, `Ctrl+Shift+]`, `Ctrl+Alt+[`, `Ctrl+Alt+]` on the Frequency field. Confirm TX low-edge and high-edge adjust and speak the new value. Result: ___

### Audio Levels

- B.21 `[S]` Press `Alt+PageUp` / `Alt+PageDown` several times. Confirm audio gain adjusts and speaks new value. Result: ___
- B.22 `[S]` Press `Alt+Shift+PageUp` / `Alt+Shift+PageDown`. Confirm headphone volume adjusts and speaks. Result: ___
- B.23 `[S]` Press `Shift+PageUp` / `Shift+PageDown`. Confirm line-out volume adjusts and speaks. Result: ___
- B.24 `[S]` Press `Ctrl+Shift+W` to open Audio Workshop. Confirm dialog opens, is tab-navigable, and closes on Escape. Result: ___
- B.25 `[S]` Press `Ctrl+P` to check panning. Confirm current pan setting speaks. Result: ___

### RIT / XIT

- B.26 `[S]` From any Home field, press `R`. Confirm RIT toggles on/off with speech. Press `X`. Confirm XIT toggles. Result: ___
- B.27 `[S]` On the RIT field, adjust offset with arrows. Confirm offset value speaks. Press `Ctrl+Shift+C` to clear RIT offset to zero. Confirm clear speaks. Result: ___

---

## C — Home Navigation and the JJ Flexible Home (Sprint 28)

Covers the Sprint 28 Home rename, focus-landing announcements with verbosity levels, arrow navigation through Home fields, universal keys, and field-specific behaviours. Includes today's two code fixes (plural-slice speech, always-numeric Squelch Level).

### Focus Landing and Verbosity

- C.1 `[S]` Press `F2` from a non-Home focus position. Confirm the speech begins with "Home" or "JJ Flexible Home" (depending on verbosity) and names the sub-field you landed on. Result: ___
- C.2 `[S]` Under **Tools > Settings > Notifications**, set Speech verbosity to **Terse**. Press `F2`. Expect: "Home, slice" (or current field). Result: ___
- C.3 `[S]` Set verbosity to **Chatty**. Press `F2`. Expect: "JJ Flexible Home, slice, 14.225.000" (destination + field + current frequency). Result: ___
- C.4 `[S]` Return verbosity to **Moderate** for the rest of the matrix (the default). Result: ___

### Arrow Navigation Through Fields

- C.5 `[S]` From Home, press the right arrow repeatedly. Confirm each field announces in order: Slice, Slice Operations, Frequency, Mute, Volume, S-Meter, Squelch, Squelch Level, Split, VOX, Offset, RIT, XIT. Result: ___
- C.6 `[S]` Press the left arrow from XIT. Confirm reverse traversal announces each field correctly. Result: ___

### Universal Home Keys

- C.7 `[S]` From the Slice field, press `M`. Confirm mute toggles with speech. Press from the Frequency field — still toggles. Result: ___
- C.8 `[S]` From any Home field, press `R`. Confirm RIT toggles. Press `X`. Confirm XIT toggles. Result: ___
- C.9 `[S]` From any Home field, press `Q`. Confirm squelch toggles on/off with two-tone earcon and speech. Result: ___
- C.10 `[S]` From any Home field, press `V`. Confirm cycles to next slice with announcement. Result: ___
- C.11 `[S]` On Slice Operations, press `=`. Confirm "Slice A receive" and "Slice A transmit" (or equivalent) — receive and transmit both set to current slice. Result: ___

### Slice Operations Field — Three-Way Mute

- C.12 `[S]` Focus the Slice Operations field. Press `Space`. Confirm mute toggles (current state flips). Result: ___
- C.13 `[S]` On Slice Operations, press `m`. Confirm "Slice A muted" (forces muted, re-announces even if already muted). Press `m` again — confirm re-announce of the same muted state. Result: ___
- C.14 `[S]` On Slice Operations, press `S`. Confirm "Slice A sounding" (forces unmuted, re-announces if already unmuted). Press `S` again — confirm re-announce. Result: ___
- C.15 `[S]` On Slice Operations, press `PageUp` / `PageDown` / `Home`. Confirm pan right / left / center with speech. Result: ___
- C.16 `[S]` On Slice Operations, press `Up` / `Down` several times. Confirm slice volume adjusts and speaks. Result: ___
- C.17 `[S]` On Slice Operations, press `A` then `T`. Confirm "active receive" (RX slice set) and "transmit" (TX slice set). Result: ___

### Squelch and Squelch Level (Sprint 28 Phase 3.9 plus today's fix)

- C.18 `[S]` With squelch OFF, arrow to the Squelch Level field. Confirm the field shows a numeric level (e.g., "20"), not "---". Result: ___
- C.19 `[S]` With squelch OFF, press `Up` on Squelch Level. Confirm announcement "Squelch level 25" (or whatever) AND the field display shows the new number (not "---"). This is the 2026-04-24 fix. Result: ___
- C.20 `[S]` Ask your screen reader to re-read the Squelch Level field after adjustment (e.g., Insert+Tab in NVDA). Confirm it reads the numeric value, not "off". Result: ___
- C.21 `[S]` Press `Q` to turn squelch on. Confirm the Squelch field shows "Q" (speaks "on") and Squelch Level continues to show the same number. Press `Q` again — squelch off, number stays visible on Squelch Level. Result: ___
- C.22 `[S]` On the Squelch field directly, press `Space`. Confirm it toggles squelch on/off (same as `Q` universal key). Result: ___

### Escape and Collapse

- C.23 `[S]` Expand the DSP ScreenFields category (`Ctrl+Shift+N`). Focus inside the expanded group. Press `Escape` once. Confirm the group collapses with a descending-chirp earcon and focus lands on the DSP header. Result: ___
- C.24 `[S]` With focus on the DSP header after collapse, press `Space`. Confirm re-expand with ascending chirp. Result: ___
- C.25 `[S]` Expand two or more categories. Press `Escape` twice quickly (within your Double-Tap Tolerance). Confirm gavel two-tone earcon, all categories collapse, focus returns to the JJ Flexible Home, and the "All panels collapsed, home" speech fires. Result: ___
- C.26 `[S]` From the JJ Flexible Home (no expanded groups), press `Escape` once. Confirm it is a no-op or returns you to Home — no errors or confusing speech. Result: ___

### Slice Create / Release — Plural Speech Fix (Today)

- C.27 `[S]` Starting with 1 slice active, open Audio & Slice ScreenFields (`Ctrl+Shift+2`) and use **Create Slice**. Confirm speech "Slice created, 2 slices active" (plural). Result: ___
- C.28 `[S]` With 2 slices active, use **Release Slice**. Confirm speech "Slice released, 1 slice active" (singular — this is the 2026-04-24 plural-slice fix). Result: ___
- C.29 `[S]` Attempt to release the only remaining slice. Confirm "Cannot release the only slice" speaks and no action is taken. Result: ___

---

## D — Logging a QSO

Covers the logging pane, logging-mode scope, callbook lookup, and log management.

- D.1 `[S]` Tab into the logging pane. Confirm Logging mode announces itself (focus change from Radio scope). Result: ___
- D.2 `[S]` In logging pane, press `Alt+C`. Confirm focus jumps to the Call field (not CW mode — this is the Logging-scope vs Radio-scope disambiguation). Result: ___
- D.3 `[S]` Fill Call with `K4ABC`, press `Alt+T` (His RST), type `59`, press `Alt+R` (My RST), type `58`. Confirm each field jump and data entry. Result: ___
- D.4 `[S]` Press `Alt+N` (Name), `Alt+Q` (QTH), `Alt+S` (State), `Alt+G` (Grid), `Alt+E` (Comments). Confirm each jumps to the correct field. Result: ___
- D.5 `[S]` Press `Alt+D`. Confirm date and time update to current. Result: ___
- D.6 `[S]` Press `Ctrl+W` to save the QSO. Confirm save speech and form clears for next entry. Result: ___
- D.7 `[S]` Press `Ctrl+N` to start a new entry. Confirm empty-form state. Result: ___
- D.8 `[S]` Press `Ctrl+Shift+F` to search the log. Confirm search dialog opens. Result: ___
- D.9 `[S]` Press `Ctrl+J, L` (leader-key log stats). Confirm log statistics speak. Result: ___
- D.10 `[S]` Press `Ctrl+L` to open the Station Lookup dialog. Type a callsign you know is in QRZ or HamQTH. Confirm lookup populates Name, QTH, and Grid. Result: ___
- D.11 `[S]` Confirm that Radio-scope hotkeys do NOT fire while in Logging mode (e.g., `Alt+C` in log pane should NOT switch the radio to CW mode). Result: ___
- D.12 `[S]` Tab out of the logging pane back to Radio scope. Confirm transition back to Radio hotkeys. Result: ___

---

## E — Remote (SmartLink) Operation

Covers SmartLink account management, Sprint 27 tiered networking (Tier 1 manual port, Tier 2 UPnP, Tier 3 hole-punch), and the Sprint 28 port-forward ownership gate.

### SmartLink Account Manager

- E.1 `[D]` Radio menu → Manage SmartLink Accounts. Confirm dialog opens and lists your saved accounts. Result: ___
- E.2 `[D]` Rename an account to a friendly name ("Home shack" or similar). Confirm rename persists after closing and reopening the dialog. Result: ___
- E.3 `[D]` Switch between two saved accounts. Confirm switch succeeds without password re-entry (refresh token still valid). Confirm remote radio list refreshes. Result: ___
- E.4 `[D]` Delete an account (pick a test one you can re-add). Confirm the stored credentials are gone. Re-add the account by signing in again. Result: ___

### Sprint 27 Tier 1 — Manual Port Forwarding

- E.5 `[S]` Open **Tools > Settings > Network**. Confirm Tier 1 is the default selection. Result: ___
- E.6 `[S]` Enter a port number (e.g., 4992). Press the **Test port** button. Confirm a basic-reachability test runs and announces a result. Result: ___
- E.7 `[S]` Set the port to a common-conflict number (e.g., 3389 for RDP). Confirm the Test port button warns about the conflict. Result: ___
- E.8 `[S]` Save the port setting. Confirm it persists per account — the same port loads when you connect to the same SmartLink account next time. Result: ___
- E.9 `[D]` On initial connect to Don's 6300, confirm the configured port is auto-applied silently (no extra prompt). Verify in the trace log that the auto-apply fired. Result: ___
- E.10 `[D]` Disconnect and reconnect. Confirm the port is not re-applied redundantly (auto-apply is a no-op if the radio already has the right port). Result: ___

### Sprint 27 Tier 2 — UPnP

- E.11 `[S]` In Settings > Network, pick **Tier 2 (Tier 1 plus UPnP)**. Confirm the radio-button group updates and the "JJ Flex sets up your router" explanation appears. Result: ___
- E.12 `[S]` Connect to a radio with Tier 2 active. Confirm UPnP mapping is attempted silently on connect. Result: ___
- E.13 `[S]` On disconnect, confirm the UPnP mapping is released (check your router's admin UI if accessible). Result: ___
- E.14 `[S]` Force UPnP to fail (disable UPnP on the router temporarily). Confirm the application silently falls back to Tier 1 behaviour and the diagnostic report shows UPnP unavailable. Result: ___

### Sprint 27 Tier 3 — Hole-Punch

- E.15 `[D]` In Settings > Network, pick **Tier 3 (Tier 1 plus UPnP plus hole-punch)**. Confirm the selection. Result: ___
- E.16 `[D]` Connect to Don's 6300 from a network where direct port forwarding does NOT work (if such a network is available). Confirm Tier 3 hole-punch kicks in. Result: ___

### Sprint 28 — Port-Forward Ownership Gate

- E.17 `[S]` At your own radio, open Settings > Network and press **Apply port forwarding**. Confirm the confirmation dialog appears with default focus on **No**. Press Enter (should be No by default). Confirm no change is committed. Result: ___
- E.18 `[S]` Press Apply again; this time Tab to Yes and press Enter. Confirm the change commits successfully. Result: ___
- E.19 `[D]` From a remote SmartLink connection to Don's 6300 (not primary operator), press Apply port forwarding. Confirm the application refuses with speech: "Cannot change SmartLink port settings. You must be the primary operator at the radio." Confirm no dialog, no action. Result: ___

---

## F — Networking Diagnostics (Sprint 27 Tracks C and D)

Covers the Network Test probe, the diagnostic report output, Copy/Save buttons, Verbose toggle, and the post-disconnect auto-test behaviour.

- F.1 `[S]` Connect to a radio. Open **Tools > Settings > Network**. Press **Test network**. Confirm the probe runs and announces a summary (pass/fail for each of the five checks: UPnP TCP, UPnP UDP, Manual TCP, Manual UDP, hole-punch support). Result: ___
- F.2 `[S]` Press **Copy report**. Confirm a "Report copied" announcement. Paste the clipboard content into a plain-text editor. Confirm it is readable Markdown with the full verbose report. Result: ___
- F.3 `[S]` Press **Save report**. Confirm a Save File dialog with a timestamped default filename. Save to disk and confirm the file contains the same Markdown as the Copy output. Result: ___
- F.4 `[S]` Toggle the **Verbose** checkbox. Press Test network again. Confirm the in-app summary is more detailed with Verbose on. Confirm that Copy/Save still produce the full verbose report regardless of this toggle. Result: ___
- F.5 `[S]` Disconnect while connected. Confirm the post-disconnect auto-test runs on the transition into Reconnecting state, and its result appears in Settings > Network. Result: ___
- F.6 `[S]` Disconnect without a SmartLink session authenticated (sign out of SmartLink first). Press Test network. Confirm "did not complete" result with a clear reason. Result: ___

---

## G — Settings and Preferences

Covers the Tools > Settings dialog, the Accessibility tab (DoubleTapTolerance), Speech verbosity cycling, Filter and Tuning preset editors, Profile management, and the License tab's country and enforcement settings.

### Double-Tap Tolerance (Sprint 28 Phase 1)

- G.1 `[S]` Open **Tools > Settings > Accessibility**. Confirm the Double-Tap Tolerance radio group has four options: Quick (250), Normal (500), Relaxed (750), Leisurely (1000). Result: ___
- G.2 `[S]` Confirm each option announces both name and milliseconds ("Normal, 500 milliseconds, selected"). Result: ___
- G.3 `[S]` Select **Relaxed**. OK the dialog. Reopen Settings. Confirm Relaxed is still selected (persists across dialog open/close). Restart the app. Confirm Relaxed is still selected (persists across restart). Result: ___
- G.4 `[S]` With Relaxed (750 ms) selected, press `[` twice with ~600 ms between presses. Confirm filter-edge adjustment mode engages (the double-tap is recognised because it is within 750 ms). Result: ___
- G.5 `[S]` Still with Relaxed, expand two ScreenFields groups. Press Escape twice with ~600 ms between presses. Confirm the gavel two-tone fires (collapse-all). Result: ___
- G.6 `[S]` Switch to **Quick (250 ms)**. Repeat the `[[` filter-edge test with ~400 ms between presses. Confirm the double-tap is NOT recognised (interval too long for Quick). Result: ___

### Speech Verbosity

- G.7 `[S]` Press `Ctrl+Shift+V`. Confirm the next verbosity level speaks ("Terse" / "Moderate" / "Chatty"). Cycle through all three. Result: ___
- G.8 `[S]` Set verbosity, restart, confirm persistence. Result: ___

### Filter Preset Editor (per operator)

- G.9 `[S]` Open **Tools > Settings > Tuning > Edit Filter Presets**. Confirm the dialog lists presets for each mode. Result: ___
- G.10 `[S]` Modify a CW preset (change a width, add a new preset, remove one). OK out and confirm changes persist. Result: ___
- G.11 `[S]` Reset to defaults. Confirm the baseline preset lists return, scoped to the current operator. Result: ___

### Tuning Step Editor (per operator)

- G.12 `[S]` Open **Tools > Settings > Tuning > Edit Tuning Steps**. Confirm two lists: Coarse and Fine. Result: ___
- G.13 `[S]` Add a custom Fine step (e.g., 1 Hz). Confirm it appears in the step-cycle rotation during tuning. Result: ___

### Profiles

- G.14 `[S]` Save the current settings as a new profile. Confirm it persists. Result: ___
- G.15 `[S]` Switch to a different profile. Confirm the settings change. Switch back. Confirm round-trip fidelity. Result: ___

### License Tab

- G.16 `[S]` Open **Tools > Settings > License**. Confirm Country defaults to US. Result: ___
- G.17 `[S]` Toggle "Enforce transmit rules." Confirm the setting persists. Result: ___
- G.18 `[S]` On 60m with enforcement enabled, attempt to TX in LSB on Channel 1 (which should be USB-only). Confirm the application blocks or corrects the mode. Result: ___

---

## H — Help System

Covers F1 context-sensitive help, Command Finder, the Help menu items (including the Sprint 28 Phase 8c-ii What's New wiring), and the About dialog.

- H.1 `[S]` Press `F1` from the main window. Confirm the general help page opens in the CHM viewer. Result: ___
- H.2 `[S]` Focus the Frequency field, press `F1`. Confirm context-sensitive help opens on the Tuning and Frequency page. Result: ___
- H.3 `[S]` Focus a ScreenFields category (e.g., DSP) and press `F1`. Confirm context-sensitive help opens on Filters and DSP. Result: ___
- H.4 `[S]` Press `Ctrl+/`. Confirm Command Finder opens. Type "noise" in the search box. Confirm results filter down to noise-related commands. Close with Escape. Result: ___
- H.5 `[S]` Help menu → **Help Topics**. Confirm CHM opens at the top. Result: ___
- H.6 `[S]` Help menu → **Keyboard Reference**. Confirm CHM opens directly on the Keyboard Reference page. Result: ___
- H.7 `[S]` Help menu → **What's New** (Sprint 28 Phase 8c-ii). Confirm CHM opens on the What's New topic (the imported CHANGELOG.md). Result: ___
- H.8 `[S]` Help menu → **Earcon Explorer**. Confirm the Earcon Explorer dialog opens. Browse and preview a few earcons. Result: ___
- H.9 `[S]` Help menu → **About**. Confirm the About dialog opens. Confirm four tabs: About, Radio, System, Diagnostics. Result: ___
- H.10 `[S]` On the About tab, find the version number text. Activate the version link (click it visually, or use the WebView's keyboard navigation to reach the link and press Enter). Confirm it opens What's New via the jjflex:// URI scheme. Result: ___
- H.11 `[S]` Press `Alt+N` to activate the **View What's New** button (bottom-row, always visible). Confirm What's New opens the same way. Result: ___
- H.12 `[S]` Press `Alt+C` to Copy to Clipboard. Paste into a text editor. Confirm the current tab's content is copied as readable plain text. Result: ___
- H.13 `[S]` Press `Alt+E` to Export Diagnostic Report. Save to disk. Confirm the file contains all four tabs combined. Result: ___
- H.14 `[S]` On the Diagnostics tab, press **Check for Updates** (Alt+U). Confirm an update-check runs against GitHub and announces a result. Result: ___
- H.15 `[S]` On the Diagnostics tab, press **Run Connection Test** (Alt+T) while connected. Confirm the connection tester launches. Result: ___
- H.16 `[S]` Press `Alt+L` (or Escape) to close the About dialog. Confirm focus returns to the main window. Result: ___

---

## I — MultiFlex Coordination (Sprint 26)

Covers the MultiFlex Clients dialog and multi-client coordination behaviours.

- I.1 `[M]` Connect two clients to the same MultiFlex-capable radio (e.g., JJ Flexible Radio Access on your machine + SmartSDR on another). Radio menu → **MultiFlex Clients**. Confirm the dialog lists both clients. Result: ___
- I.2 `[M]` Confirm your own client is marked distinctly from the other. Confirm each entry shows station name, program + version, and open slices. Result: ___
- I.3 `[M]` When the other client joins, confirm a short announcement fires ("Client K4ABC connected" or similar). Result: ___
- I.4 `[M]` When the other client disconnects, confirm a short announcement fires. Result: ___
- I.5 `[M]` As primary operator, select the other client in the dialog and choose **Disconnect**. Confirm the other client's session ends. Result: ___
- I.6 `[M]` Client A has Slice A open. From Client B (you), attempt to tune Slice A. Confirm Slice A is not tunable from your side (slice ownership respected). Result: ___

---

## J — Accessibility Cross-Cutting

Covers NVDA and JAWS coverage, native Win32 menu navigation, speech verbosity at all levels, toggle-state speech in menus, and end-to-end keyboard-only workflow.

### NVDA Coverage

- J.1 `[S]` With NVDA running, execute a short smoke flow: F2 to Home, arrow to Mute and back, Ctrl+J N to toggle NR, Alt+U to switch to USB, Ctrl+F to enter a frequency, Ctrl+/ to open Command Finder, Ctrl+Shift+W to open Audio Workshop and close it. Confirm every step speaks sensibly. Result: ___
- J.2 `[S]` With NVDA running, tab through the About dialog (all four tabs, all four bottom-row buttons). Confirm each control announces with its name and, where applicable, its accelerator key (e.g., "View What's New, Alt+N"). Result: ___
- J.3 `[S]` With NVDA running, open and navigate the Tools > Settings dialog. Tab through each tab and every control inside each tab. Confirm every control announces meaningfully. Result: ___

### JAWS Coverage

- J.4 `[S]` Switch to JAWS. Repeat the smoke flow from J.1. Confirm equivalent behaviour. Result: ___
- J.5 `[S]` With JAWS, verify dynamic content changes are read (e.g., Noise Reduction toggle announcement, frequency change announcement). Adjust JAWS settings if needed. Result: ___

### Menu Navigation

- J.6 `[S]` Press Alt. Navigate the menu bar with arrow keys. Confirm every top-level menu announces its name, every submenu announces its name, and every item announces its name plus state (checkmark for toggles). Result: ___
- J.7 `[S]` Open a menu with a toggle item (e.g., Classic > Receiver > NR, or the DSP toggle wherever it lives in the current menu structure). Confirm checkmark state speaks ("NR on" / "NR off" or "checked" / "unchecked" depending on screen reader). Result: ___

### Dialog Close Behaviour (Sprint 25+ announcements)

- J.8 `[S]` Open the Status Dialog (`Ctrl+Alt+S`), then close it with Escape. Confirm the application announces the current listening state on close ("Listening on 14.225, Upper Side Band, 20 meter band, slice A"). Result: ___
- J.9 `[S]` Open Settings, change something trivial, click OK. Confirm the dialog closes and the application re-announces current state. Result: ___

### End-to-End Keyboard-Only Workflow

- J.10 `[S]` Complete a simulated QSO end-to-end without touching the mouse: connect, tune, set mode, pick a slice, adjust filters, open log, fill log entry, save QSO, disconnect. Confirm every step is reachable with the keyboard and announces sensibly. Result: ___

---

## Deferred to Post-Release Testing

*(Items marked DEFER during the pass accumulate here, each with the condition that would unblock it.)*

- ___

---

## Known Pre-Existing Gaps (Not Regressions, Do Not Block)

These are documented issues from earlier work that 4.1.17 inherits but does not regress. Flagged here so they do not get re-logged as new problems during testing.

- CW direct-send (character-at-a-time transmit) is un-wired in the WPF port. `Ctrl+Shift+F4` and `Ctrl+Shift+F5` both just focus the send text box; the distinction between buffered and direct is stubbed pending a future sprint. (From help-audit batch 1 findings.)
- The Ctrl+Tab Quick Actions popup is disabled pending redesign (Sprint 28 Phase 3.5-3.6). Ctrl+Tab now cycles ScreenFields expanders; the former-popup actions (ATU Tune, Tune Carrier, Transmit, Speak Status) are still available via their direct hotkeys. (From memory/project_action_toolbar_redesign.md.)
- Audio device mid-session hot-swap (plug in or unplug headphones while the application is running) may require a restart to pick up the new device. (From docs/help/md/known-issues.md.)
- Pandoc-produced CHM pages carry a filename-derived `<h1 class="title">` in addition to the markdown's own `# Title`, so the compiled help pages have two visible H1s. Cosmetic; flagged for build-pipeline cleanup in a future sprint.

---

## Sign-off

- Tester: ___
- Date completed: ___
- Overall verdict: ___
- Defects found that block release: ___
- Defects found that are flagged for follow-up sprints (not blocking): ___
