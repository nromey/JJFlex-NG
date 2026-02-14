# Sprint 7 Test Matrix

**Sprint:** 7 — WPF Migration, Station Lookup, Status & Polish
**Build:** `bin\x64\Release\net8.0-windows\win-x64\JJFlexRadio.exe`
**Date:** 2026-02-13

---

## Track A: Bug Fixes & Polish

| # | Test | Steps | Expected | Pass? | Notes |
|---|------|-------|----------|-------|-------|
| A1 | BUG-015: F6 no double-announce | F6 in Logging Mode | Hear "Radio pane, Frequency..." exactly once, no extra "Radio pane" | kind of pass | No read of radio pane twice, in log panel though, you hear "pane" and you don't hear the first field (call) Press f6, radio pane does not repeat. f6 again reads "Entering Log mode pane, pane |
| A2 | BUG-013: Dup beep audible | Log a duplicate QSO | Hear both beep (SystemSounds.Exclamation) AND "worked x calls" speech | pass, kind of | When entering a duplicate, first entry, it dings (I wish it was the more shrill "beep" and not playing the windows alert sound. Anyway, first entry, it says "worked 3, last entry xxx, then press tab to enter in data, shift tab and it says "duplicate as well as an alert, agai nprefer a Beeeep, we may need to wait until we implement tone based earcons |
| A3 | CW hotkey feedback (no messages) | Press Ctrl+1 with no CW messages configured | Hear "No CW messages configured" or similar | pass | |
| A4 | CW hotkey feedback (F12) | Press F12 with no CW messages | Hear feedback message | pass | |
| A5 | Menu stubs wired | Open Modern menu, check previously "coming soon" items | Wired items work; remaining stubs still say "coming soon" | pass, kind of | Still am not getting "coming soon when arrowing around, for stubbed items, I don't see status for things like dsp, I don't see the actual status of the items. When I select rnn on Don's radio, a radio that doesn't support rnn, it just says rnn off. When selecting legacy nr, pressing enter turns it on and it says legacy noise reduction off, then do it again and it turns it off and says "legacy noise reduction on". Ultimately the new menus don't read tihngs like hotkeys, whether or not they are gray due to subscription gating, what state they are etc. Once all stubs are set, we should add hotkeys for these items. Big deal though is that no "coming soon"J is read by sr, and dsp current state is not read, and it seems to read the opposite state / the state of item previous to pressing enter on it. I notice that there are filter items now, I didn't try them. Sugest having some filter presets/filter presets by mode |
| A6 | Changelog updated | Open `docs/CHANGELOG.md` | Sprint 6 changes documented in first-person tone | pass | |

---

## Retest: Sprint 7 Bug Fixes (build 7:32 PM 2/13)

**Fixes applied in this build:**
- A1: "Entering Logging Mode" speech no longer includes "Call Sign" — lets SR naturally announce "Call Sign, edit"
- A1: F6 "Log entry pane" speech no longer includes "Call Sign" — same fix
- A5: DSP toggles use local variable — spoken state matches actual toggle (all 8 fixed)
- A5: "Coming soon" stubs are now enabled + speak on click
- A5: Filter menu items (Narrow/Widen/Shift) use local variable — spoken values are correct
- NEW: Ctrl+Shift+L / Ctrl+Shift+M now work inside Logging Mode (WPF key forwarding fix)

| # | Test | Steps | Expected | Pass? | Notes |
|---|------|-------|----------|-------|-------|
| R1 | Enter Logging Mode announcement | Press Ctrl+Shift+L from Modern/Classic | Hear "Entering Logging Mode" then SR announces "Call Sign, edit" — no "pane" | known issue | SR says "Entering logging mode, callsign, unknown" — the "unknown" is a WPF-in-WinForms interop artifact. Will be resolved when Form1 moves to WPF (Sprint 8). |
| R2 | F6 to log pane announcement | In Logging Mode, F6 from radio pane to log pane | Hear "Log entry pane" then SR announces "Call Sign, edit" — no "pane" | pass | |
| R3 | Ctrl+Shift+L exits Logging Mode | In Logging Mode, press Ctrl+Shift+L | Exits Logging Mode, hear "Returning to [Modern/Classic] mode" | pass | Fixed: reordered exit to clear WPF focus → move WinForms focus → then hide. |
| R4 | Ctrl+Shift+M exits Logging Mode | In Logging Mode, press Ctrl+Shift+M | Exits to previous mode, hear "Returning to [mode] mode" | pass | Wow, that actually works |
| R5 | DSP toggle speaks correct state | Toggle Legacy NR via Slice → DSP → Legacy NR | Hear "Legacy NR on" when turning ON, "Legacy NR off" when turning OFF — matches SmartSDR | pass | When toggled, the correct mode state is spoken. Menu item current state is not spoken though. Just says "legacy noise control" |
| R6 | Coming soon speaks on click | Click any stub item (e.g., Slice → Selection → Select Slice) | Hear "Select Slice, coming soon" | pass | |
| R7 | Coming soon items visible to SR | Arrow through stub items in Modern menu | SR announces each item name (not skipped/silent) | pass | |
| R8 | Filter Narrow speaks correct values | Filter → Narrow | Hear "Filter [low] to [high]" with values reflecting the actual change | pass | Is it supposed to speak the hotkey that narrows or widens etc. |
| R9 | Filter items not inverted | Narrow then Widen, check values make sense | Narrow increases low/decreases high; Widen reverses. Values consistent. | pass | works as expected. |

---

## Track A5 Retest: Modern Menu Items (individual)

**How to get there:** Switch to Modern Mode (Ctrl+Shift+M), then use the menu bar. All items below are in the Modern menus. Need a connected radio with an active slice for Slice/Filter items.

### Radio Menu

| # | Test | Steps | Expected | Pass? | Notes |
|---|------|-------|----------|-------|-------|
| M-R1 | Connect to Radio | Radio → Connect to Radio | Opens radio selection dialog | pass | If already connected, this disconnected, opened rig selector, clicked remote. System asked if I wanted to connect again with saved smart link account. It kind of locked up, and after 30 seconds or so, I the connection shows as invalid and I clicked yes to reconnect. I see rigs on Don's radioit says "session invalid" but keeps connecting. I clidk no because I'm listening to a connected radio, and it says again session invalid. I click no again and I'm fully connected. Check what happens in that logic, if already connected, might want to ask if yo uwant to disconnect from your connected radio, if yes, radio should disconnect, show rig selector. If connecting to an already stored smart link, see if we can fix how that all flows. First thing to do though is ask user if they really want to connect if they're connected. |
| M-R2 | Manage SmartLink | Radio → Manage SmartLink Accounts | Opens SmartLink account manager | pass | |
| M-R3 | Operators | Radio → Operators | Opens operator list | pass | |
| M-R4 | Profiles | Radio → Profiles | Opens profiles dialog | pass | |
| M-R5 | Connected Stations | Radio → Connected Stations | Opens connected stations dialog | pass | consider showing in the list for connected stations "no stations connected" in the listbox if none are connected. |
| M-R6 | Disconnect | Radio → Disconnect (with radio connected) | Hear "Disconnecting from [name]", radio disconnects | pass | |
| M-R7 | Exit | Radio → Exit | App closes (or prompts to save) | pass | |

### Slice → Audio Submenu

| # | Test | Steps | Expected | Pass? | Notes |
|---|------|-------|----------|-------|-------|
| M-A1 | Mute/Unmute | Slice → Audio → Mute/Unmute | Hear "Muted" (then "Unmuted" on 2nd press). Toggle works. | pass | Speeking of muted orunmuted works, perhaps say "unmuted slice x" or "unmuted slice x"? Once verbosity is implemented, you could just say unmuted muted, but slice x would be useful. |
| M-A2 | Volume Up | Slice → Audio → Volume Up | Hear "Volume [number]", value increases by 5 | | |
| M-A3 | Volume Down | Slice → Audio → Volume Down | Hear "Volume [number]", value decreases by 5 | pass | |
| M-A4 | Pan Left | Slice → Audio → Pan Left | Hear "Pan [number]", value decreases by 10 | pass | |
| M-A5 | Pan Center | Slice → Audio → Pan Center | Hear "Pan centered" | pass | |
| M-A6 | Pan Right | Slice → Audio → Pan Right | Hear "Pan [number]", value increases by 10 | pass | |

### Slice → DSP → Noise Reduction Submenu

| # | Test | Steps | Expected | Pass? | Notes |
|---|------|-------|----------|-------|-------|
| M-D1 | Neural NR (RNN) | Slice → DSP → Noise Reduction → Neural NR (RNN) | Hear "Neural NR on" (toggles). State matches actual radio. | fail | Don's radio is a 6300, though rnn actually toggles, you shouldn't even be able to do this, it should be grayed because the radio can't do it. I don't have a radio that is not subscribed at the moment, when I plug in my actual radio, we'll be able to test that, but the gating used to work for sub and radio capabilities. |
| M-D2 | Spectral NR (NRS) | Slice → DSP → Noise Reduction → Spectral NR (NRS) | Hear "Spectral NR on" (toggles). State matches actual radio. | maybe pass | I don't hear a difference, but it does toggle. Might want to check to see if it toggles the actual radio, Don's radio should be subscribed to do this |
| M-D3 | Legacy NR | Slice → DSP → Noise Reduction → Legacy NR | Hear "Legacy NR on" (toggles). State matches actual radio. | pass | |

### Slice → DSP → Auto Notch Submenu

| # | Test | Steps | Expected | Pass? | Notes |
|---|------|-------|----------|-------|-------|
| M-D4 | FFT Auto-Notch | Slice → DSP → Auto Notch → FFT Auto-Notch | Hear "FFT Auto-Notch on" (toggles). State matches actual radio. | pass | Doesn't show current  status in menu but I don't think this does it for any menu |
| M-D5 | Legacy Auto-Notch | Slice → DSP → Auto Notch → Legacy Auto-Notch | Hear "Legacy Auto-Notch on" (toggles). State matches actual radio. | pass | |

### Slice → DSP (top-level items)

| # | Test | Steps | Expected | Pass? | Notes |
|---|------|-------|----------|-------|-------|
| M-D6 | Noise Blanker (NB) | Slice → DSP → Noise Blanker (NB) | Hear "Noise Blanker on" (toggles). State matches actual radio. | pass | |
| M-D7 | Wideband NB (WNB) | Slice → DSP → Wideband NB (WNB) | Hear "Wideband NB on" (toggles). State matches actual radio. | pass | |
| M-D8 | Audio Peak Filter (APF) | Slice → DSP → Audio Peak Filter (APF) | Hear "Audio Peak Filter on" (toggles). State matches actual radio. | fail maybe | when activated, says audio peak filter on, when activated for the second time, still says audio peak filter on. Not sure if that's a subscriber function, or sometihng that is only available on certain radios, don's radio is on all the time. Even RNN which isn't possible toggles, so audio peak is odd. I am in LSB mode at the moment |

### Filter Menu

| # | Test | Steps | Expected | Pass? | Notes |
|---|------|-------|----------|-------|-------|
| M-F1 | Narrow | Filter → Narrow | Hear "Filter [low] to [high]", filter bandwidth decreases by 100 Hz | pass | |
| M-F2 | Widen | Filter → Widen | Hear "Filter [low] to [high]", filter bandwidth increases by 100 Hz | pass | |
| M-F3 | Shift Low Edge | Filter → Shift Low Edge | Hear "Low edge [value]", low edge increases by 50 Hz | pass | |
| M-F4 | Shift High Edge | Filter → Shift High Edge | Hear "High edge [value]", high edge increases by 50 Hz | pass | |

### Stub Items ("Coming Soon")

| # | Test | Steps | Expected | Pass? | Notes |
|---|------|-------|----------|-------|-------|
| M-S1 | Stub items announced | Arrow through any stub item (e.g., Slice → Selection → Select Slice) | Screen reader announces the item name (not skipped) | pass | |
| M-S2 | Stub click speaks | Click any stub item (e.g., Filter → Presets) | Hear "[item name], coming soon" | pass | |
| M-S3 | No radio guard | Try any wired item with no radio connected | Hear "No radio connected" | pass | |
| M-S4 | No slice guard | Try any wired item with radio but no active slice | Hear "No active slice" | unknown | I can't figure out how to disable all slices, when radio is disabled, it does not say no slice detected. |

### Tools Menu

| # | Test | Steps | Expected | Pass? | Notes |
|---|------|-------|----------|-------|-------|
| M-T1 | Command -Finder | Tools → Command Finder (or Ctrl+/) | Command Finder dialog opens | pass | |
| M-T2 | Station Lookup | Tools → Station Lookup (or Ctrl+L) | Station Lookup window opens | | |
| M-T3 | Enter Logging Mode | Tools → Enter Logging Mode | Switches to Logging Mode | pass | |
| M-T4 | Switch to Classic UI | Tools → Switch to Classic UI | Switches to Classic Mode | pass | |
| M-T5 | Hotkey Editor | Tools → Hotkey Editor | Opens hotkey editor dialog | pass | |
| M-T6 | Band Plans | Tools → Band Plans | Opens band plans dialog | pass | |
| M-T7 | Feature Availability | Tools → Feature Availability | Opens feature availability tab | pass | |

---

## Track B: WPF Migration

| # | Test | Steps | Expected | Pass? | Notes |
|---|------|-------|----------|-------|-------|
| B1 | WPF LogPanel loads | Enter Logging Mode | LogPanel displays with all fields (Call, RST S/R, Name, QTH, State, Grid, Comments) | pass | |
| B2 | Tab order correct | Tab through LogPanel fields | Order: Call → RST Sent → RST Rcvd → Name → QTH → State → Grid → Comments | pass | |
| B3 | QSO logging works | Enter a QSO and press Ctrl+Enter | QSO saved, appears in Recent QSOs grid, serial increments | pass | |
| B4 | Previous Contact lookup | Enter a known callsign, tab out of Call field | Previous contact info displayed | pass | |
| B5 | Dup checking | Enter a duplicate callsign | "Dup: N" label updates, beep plays, speech announces | fail kind of | dup entered, press tab and it says for wa2iwc in my log that I've worked him 6 times. when I shift tab, it says duplicate and beeps, sayd 3 contacts, very odd. beeps both times |
| B6 | Auto-fill from radio | Enter Logging Mode with radio connected | Freq, Mode, Band, UTC auto-filled | pass | |
| B7 | WPF Station Lookup opens | Press Ctrl+L | Station Lookup dialog opens | pass | |
| B8 | Station Lookup callbook | Enter a callsign, click Lookup | Name, QTH, State, Country, zones populated | pass | |
| B9 | F6 pane switching | Press F6 in Logging Mode | Switches between Radio pane and LogPanel | pass | If going back to logging pannel, says logging pannel unknown but I think this is known |
| B10 | Mode switching | Switch Classic → Modern → Logging → Classic | All modes render correctly, no crashes | pass | |
| B11 | Hotkeys after WPF | Test various hotkeys in Logging Mode | All hotkeys still work (ProcessCmdKey + ElementHost) | pass | |

---

## Track C: Station Lookup Enhancements

| # | Test | Steps | Expected | Pass? | Notes |
|---|------|-------|----------|-------|-------|
| C1 | Grid Square in Settings | Open PersonalInfo, enter grid (e.g., "FN31pr"), save, reopen | Grid square persisted and shown | pass | Not necessry, see below |
| C2 | Distance/bearing displayed | Set grid in Settings, Ctrl+L, look up a station with grid | Distance/bearing row shows (e.g., "245 mi (394 km), bearing 035 degrees") | NA  | No need to set your grid square if you code it right. It can be found if you search via hamqth or qrz. You could also do it with lat and lon, use personal callsign to look it up. If not set, have hamqth or qrz search it and save it, I suppose your address could change so maybe put a button in station lookup or in personal info or both to refresh grid location. Grid could be entered manually.  Hey, just had a thought. All flexes should have GPS, and flexlib is able to query it, if radio has gps, pull data from GPS, it'd work even if mobile! Add gps location into personal in case you want to know what it is, consider speaking gps in case you want it displayed, probably best for hotkey to display info in a dialog so it can be copied for ham radio applications, perhaps have speak GPS and view GPS items. Add speak and get GPS to back log. |
| C3 | Distance/bearing announced | Same as C2, listen to screen reader | Announcement includes distance and bearing | | I don't have grid typed into setting. If radio doesn't have gps, it can't search your call found from personal info, ask you to enter it via dialog when getting baring |
| C4 | No operator grid message | Clear grid in Settings, look up a station | Distance row shows "Set your grid square in Settings" | | Not sure how to test "no operator" or remove it, it should work. |
| C5 | Log Contact button | Look up a station, click "Log Contact" | Dialog closes, Logging Mode opens, fields pre-filled (Call, Name, QTH, State, Grid) | pass | |
| C6 | Log Contact Ctrl+Enter | Look up a station, press Ctrl+Enter | Same as C5 | pass | |
| C7 | Log Contact announcement | Click Log Contact | Screen reader announces "Entering Logging Mode with [call] pre-filled" | fail | Didn't announce. |
| C8 | Log Contact from non-Logging | Open Station Lookup from Classic/Modern mode, click Log Contact | Switches to Logging Mode, fields pre-filled | pass | I don't get that test. I tested this from a non logging mode and from logging mode, both worked. Both loked up call and when clicked logged contact. |
| C9 | Done button still works | Look up a station, click Done | Dialog closes, no Logging Mode change | pass | |
| C10 | Pre-fill triggers dup check | Log Contact with a previously worked station | Dup count and previous contact displayed after pre-fill | fail | Technically it passes, beeps but no extra speech as previously noted |

---

## Track D: Plain-English Status

| # | Test | Steps | Expected | Pass? | Notes |
|---|------|-------|----------|-------|-------|
| D1 | Speak Status (radio connected) | Press Speak Status hotkey with radio connected | Hear concise summary: freq, mode, band, slice | pass | |
| D2 | Speak Status (no radio) | Press Speak Status hotkey with no radio | Hear "No radio connected" or similar | | |
| D3 | Status Dialog opens | Press Status Dialog hotkey | WPF Status Dialog opens with full radio status | pass | Default hotkey to ctrl+alt+s I had to set it. This window comes up and is not accessible. I can't tab through results, I can ask NVDA to read the dialog and kind of read the thing. Should be an ok button or press enter to close it. Also, it seems to show outside of the whoe JJFlex application. |
| D4 | Status Dialog accessible | Tab through Status Dialog fields | All fields announced by screen reader | fail | This window comes up and is not accessible. I can't tab through results, I can ask NVDA to read the dialog and kind of read the thing. Should be an ok button or press enter to close it. |
| D5 | Status Dialog (no radio) | Open Status Dialog with no radio | Shows appropriate "no radio" state | unknown | inaccessibile, should test no radio whe is accessible |

---

## Track E: Configurable Recent QSOs

| # | Test | Steps | Expected | Pass? | Notes |
|---|------|-------|----------|-------|-------|
| E1 | Default value | New operator, enter Logging Mode | Grid shows 20 recent QSOs (or fewer if < 20 exist) | pass | |
| E2 | Change to 10 | Set Recent QSOs to 10 in Settings, enter Logging Mode | Grid shows max 10 QSOs | fail | I changed my operator to 10, it still has 20 litsed. Suggest adding a menu item in radio that is "update current operator", and change operator to "select operator". It's just operator, dumb. Also, as I arrow, the index is read as JJFlexWPF.recentqsorowdataitem  This should be a freaking english sound thingy like "qso number" or just not read anything since the thing says "1 of 20".  |
| E3 | Change to 50 | Set Recent QSOs to 50 in Settings | Grid shows up to 50 QSOs (if enough exist) | fail | |
| E4 | Persists | Change setting, close and reopen Settings | Value persisted | fail | unknown |

---

## Integration Tests (post-merge)

| # | Test | Steps | Expected | Pass? | Notes |
|---|------|-------|----------|-------|-------|
| I1 | Full QSO workflow | Station Lookup failg C ontact → fill RST → Ctrl+Enter → check grid | QSO lopass gged, appears in grid with correct data | pass | |
| I2 | Speak Status after QSO | Log a QSO, then Speak Status | Status reflects current frequency/mode | pass | |
| I3 | All modes work | Cycle through Classic → Modern → Logging → back | No crashes, all features available in each mode | pass | |
| I4 | Configurable grid + WPF | Set QSO count to 10, log cont acts, verify grid | WPF DataGrid respects the configurable count | fail | |

---

## Screen Reader Matrix

| Feature | JAWS | NVDA |
|---------|------|------|
| WPF LogPanel fields (AutomationProperties) | pass | pass |
| WPF LogPanel DataGrid (row/col headers) | pass | fail items repeat, w1aw w1aw call column 2, arrow up and down reads the wpf name as above, JAWS reads "row 1 column 1 for eveey item which is neat, not sure if NVDA is just configured differently but all I need really for NVDA is row and column as I up and down though saying both would be neat if we could figure out why NVDA works and JAWS doesn't. This isn't really important though but research perhaps. Arrow up and down to read row name should be fixed though. |
| WPF Station Lookup (all fields) | pass | pass |
| Grid Square field in Station Lookup | pass | pass |
| Distance/bearing announcement | pass | pass |
| Log Contact button + workflow | pass | pass |
| Speak Status hotkey | pass | pass |
| Status Dialog (all fields, tab order) | fail | fail |
| F6 pane switching (no double-announce) | fail (not worried until all converted to WPF | pass |
| CW feedback messages | pass | pass |
| Recent QSOs count setting (PersonalInfo) | pass | pass |
| Grid Square setting (PersonalInfo) | pass | pass |

---

## Sign-off

Noel Romey K5NER Tested full set 14 February 2026