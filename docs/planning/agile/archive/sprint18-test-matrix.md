# Sprint 18 Test Matrix — v4.1.115 Don Release Punch List

**Tested by:** Noel Romey (K5NER)
**Date:** 2026-03-02
**Build:** v4.1.115 Debug x64
**Screen Reader:** NVDA
**Radio:** _(fill in)_

---

## Pre-Test Setup

1. Build Debug x64: `dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal`
2. Connect to radio via SmartLink or local
3. Verify you're on a known band (e.g., 20m USB) before starting

---

## 1. Menu Checkmarks on Toggle Items

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| 1.1 | Mute checkmark | Open Audio menu, check Mute item | Shows checkmark when muted, no checkmark when unmuted | | |
| 1.2 | Mute toggle updates checkmark | Toggle mute via menu, reopen menu | Checkmark state flips | | |
| 1.3 | ATU On/Off checkmark | Open Antenna menu, check ATU On/Off | Checkmark matches ATU state | | |
| 1.4 | VOX checkmark (Classic) | Classic mode > Operations > Transmission > VOX | Checkmark matches VOX state | | |
| 1.5 | VOX checkmark (Modern) | Modern mode > Slice > Transmission > VOX | Checkmark matches VOX state | | |
| 1.6 | Squelch checkmark | Open Receiver menu, check Squelch On/Off | Checkmark matches squelch state | | |
| 1.7 | Diversity checkmark | If 2-SCU radio: Antenna > Toggle Diversity | Checkmark matches diversity state | | |
| 1.8 | NVDA reads check state | Navigate to a checked toggle item | NVDA announces "checked" or "not checked" | | |

---

## 2. Speak Status / Status Dialog in Menu

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| 2.1 | Speak Status from menu | Tools > Speak Status | NVDA speaks full status summary (same as Ctrl+Shift+S) | | |
| 2.2 | Status Dialog from menu | Tools > Status Dialog | Speaks "coming in a future update" message (same as Ctrl+Alt+S) | | |
| 2.3 | Menu shows hotkeys | Open Tools menu | "Speak Status Ctrl+Shift+S" and "Status Dialog Ctrl+Alt+S" visible | | |

---

## 3. Wide-Open Filter Presets

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| 3.1 | SSB Wide Open | USB mode, Alt+] to cycle presets up past "Extra Wide" | Reaches "Wide Open, 4k" preset | | |
| 3.2 | CW Wide Open | CW mode, Alt+] to widest | Reaches "Wide Open, 2k" preset | | |
| 3.3 | DIGI Wide Open | DIGU mode, Alt+] to widest | Reaches "Wide Open, 4k" preset | | |
| 3.4 | AM Wide Open | AM mode, Alt+] to widest | Reaches "Wide Open, 12k" preset | | |
| 3.5 | FM Wide Open | FM mode, Alt+] to widest | Reaches "Wide Open, 16k" preset | | |
| 3.6 | Cycle wraps at boundary | At Wide Open, press Alt+] again | Says "Widest preset" | | |

---

## 4. Ctrl+F Frequency Parsing

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| 4.1 | "3.510" entry | Ctrl+F, type "3.510", OK | Tunes to 3.510.000 MHz | | |
| 4.2 | "14.225" entry | Ctrl+F, type "14.225", OK | Tunes to 14.225.000 MHz | | |
| 4.3 | "7000" entry (kHz) | Ctrl+F, type "7000", OK | Tunes to 7.000.000 MHz | | |
| 4.4 | Check trace log | After any Ctrl+F entry | Trace shows "FormatFreqForRadio: input='...' → '...'" | | |

---

## 5. Confirmation Feedback (Speech + Tones)

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| 5.1 | Ctrl+F confirmation speech | Ctrl+F, enter frequency, OK | NVDA says "Tuned to 14.225.000" (or similar) | | |
| 5.2 | Ctrl+F confirmation tone | Same as above | Short two-tone click plays | | |
| 5.3 | ValueFieldControl confirm tone | Focus a value field, Enter, type value, Enter | Confirmation tone plays on accept | | |
| 5.4 | Tone not intrusive | Repeat a few times | Tones are subtle, not annoying | | |

---

## 6. SmartLink Button Text

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| 6.1 | Button text | Open radio selector (no radio connected or via menu) | Button says "Connect to a remote radio" | | |
| 6.2 | NVDA reads button | Tab to the button | NVDA says "Connect to a remote radio" | | |

---

## 7. Context-Sensitive Help (F1)

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| 7.1 | F1 on Freq field | Focus frequency field, press F1 | NVDA speaks field help: "Frequency field. Up Down tune by position. Digits enter frequency. K round to kilohertz..." | | |
| 7.2 | F1 on Slice field | Focus Slice field, press F1 | NVDA speaks: "Slice field. Space next slice. Digits jump to slice. M mute. T set transmit..." | | |
| 7.3 | F1 on Mute field | Focus Mute field, press F1 | NVDA speaks: "Mute field. Space or M toggle mute." | | |
| 7.4 | F1 on RIT field | Focus RIT field, press F1 | NVDA speaks: "RIT field. Up Down adjust. Space toggle..." | | |
| 7.5 | F1 on value field | Focus a ScreenFieldsPanel value field, press F1 | NVDA speaks generic value field help | | |
| 7.6 | F1 elsewhere | Focus somewhere else (not FreqOut or fields panel), press F1 | Opens Command Finder dialog | | |

---

## 8. Mute Property Standardization

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| 8.1 | M key on Slice field mutes | Focus Slice field, press M | NVDA says "Muted", slice is muted | | |
| 8.2 | Menu checkmark matches M key | After pressing M to mute, open Audio menu | Mute item shows checkmark | | |
| 8.3 | Menu unmute matches M key | Unmute via menu, press M on Slice to check | States agree | | |
| 8.4 | Speech consistency | Toggle mute via M key and menu | Both say "Muted"/"Unmuted" (not "Mute on/off") | | |

---

## 9. Config Naming (Callsign)

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| 9.1 | New config files use callsign | Check AppData config directory after connect | Files named like "K5NER_Noel_Romey_pttConfig.xml" | | |
| 9.2 | Legacy files migrated | If old-format files existed, verify renamed | Old "Noel_Romey_K5NER_*.xml" files renamed to new format | | |
| 9.3 | Settings persist across restart | Change a setting, restart, verify it stuck | Setting persists (loaded from new filename) | | |

---

## 10. First-Run License Setup

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| 10.1 | First run prompt | Delete license config file, restart, connect | NVDA says "Welcome to JJFlexRadio. Your license class defaults to Extra..." | | |
| 10.2 | Subsequent runs silent | Connect again (config exists) | No welcome message | | |

---

## 11. TX Lockout Message

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| 11.1 | Lockout speech | Enable TX lockout in Settings, tune outside license, try to transmit | NVDA says "Unable to transmit here. Select license options in Settings to change." | | |

---

## 12. License Sub-Band Boundary Notifications

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| 12.1 | Sub-band crossing | Tune across a license boundary within a band (e.g., Extra→General CW/phone on 80m) | Beep + NVDA says "Entering [segment] segment" | | |
| 12.2 | Band crossing still works | Tune from 20m to 40m | Beep + NVDA says "Entering 40 meter band" | | |
| 12.3 | BoundaryNotifications off | Disable in Settings > License, tune across boundary | No beep, no speech | | |

---

## 13. Filter Edge Selection Mode (Double-Tap Bracket)

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| 13.1 | Double-tap [ enters lower mode | Quickly press [ twice | NVDA says "Adjust lower filter. Brackets move edge. Escape to exit." | | |
| 13.2 | In lower mode, [ moves low down | Press [ in lower mode | Low edge moves down, speaks filter values | | |
| 13.3 | In lower mode, ] moves low up | Press ] in lower mode | Low edge moves up (narrows from low side) | | |
| 13.4 | Double-tap ] enters upper mode | Quickly press ] twice | NVDA says "Adjust upper filter..." | | |
| 13.5 | Escape exits mode | Press Escape in edge mode | NVDA says "Filter edge mode cancelled" | | |
| 13.6 | 10s timeout exits mode | Wait 10 seconds in edge mode | NVDA says "Filter edge mode ended" | | |
| 13.7 | Single tap still works normally | Single [ (not double-tap) | Normal filter expand behavior | | |

---

## 14. Command Finder Additions

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| 14.1 | Search "filter" | Open Command Finder, type "filter" | Shows bracket filter keys, preset cycling | | |
| 14.2 | Search "mute" | Type "mute" | Shows M key on Slice field | | |
| 14.3 | Search "page" | Type "page" | Shows PgUp/PgDn for value fields | | |
| 14.4 | Search "enter" | Type "enter" | Shows Enter to type exact value | | |
| 14.5 | Info items not executable | Select an info item, press Enter | Nothing happens (no crash, no execution) | | |

---

## 15. Band Menu Checkmark

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| 15.1 | Current band checked | Open Band menu while on 20m | "20m F6" has checkmark | | |
| 15.2 | Switch band, checkmark moves | Jump to 40m (F5), reopen Band menu | "40m F5" now has checkmark, "20m" doesn't | | |
| 15.3 | WARC band checkmark | Jump to 17m (Shift+F5), open Band menu | "17m Shift+F5" has checkmark | | |

---

## 16. F2 Description Fix

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| 16.1 | Command Finder shows correct text | Open Command Finder, search "F2" | Shows "Focus frequency display" (not "Show frequency or pause scan") | | |

---

## 17. Frequency Units Preference

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| 17.1 | Default is Hz (dotted) | Tune up/down | NVDA speaks "14.225.000" format | | |
| 17.2 | Switch to kHz | Settings > Tuning > Frequency speech units > kHz | NVDA speaks "14,225 kilohertz" on tune | | |
| 17.3 | Switch to MHz | Settings > Tuning > MHz | NVDA speaks "14.225 megahertz" on tune | | |
| 17.4 | Persists across restart | Set to MHz, restart, connect | Still MHz format | | |

---

## 18. PTT Chirp — Two Discrete Tones

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| 18.1 | TX start tone | Ctrl+Space (hold) | Two distinct tones: low (400Hz) then high (800Hz) — NOT a sweep | | |
| 18.2 | TX stop tone | Release Ctrl+Space | Two distinct tones: high (800Hz) then low (400Hz) | | |
| 18.3 | Tones are subtle | Listen critically | Short, not intrusive | | |

---

## 19. Chirp On/Off Setting

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| 19.1 | Chirp checkbox in Settings | Settings > PTT tab | "Play chirp tones on transmit and receive" checkbox visible | | |
| 19.2 | Uncheck disables chirp | Uncheck, try TX | No chirp tones on TX start/stop | | |
| 19.3 | Safety warnings still play | With chirp off, let TX approach timeout | Warning beeps still fire | | |
| 19.4 | Re-enable chirp | Check again, try TX | Chirp tones restored | | |

---

## 20. Reverse Beacon

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| 20.1 | Open dialog | Ctrl+Alt+R | Reverse Beacon dialog opens with callsign pre-filled | | |
| 20.2 | Submit opens browser | Click OK | Browser opens to RBN results, NVDA says "Opening Reverse Beacon in your browser" | | |
| 20.3 | No crash on failure | Disconnect internet, try again | Graceful error, NVDA says "Could not open browser" | | |

---

## 21. Menu Accelerator Speech Cleanup

| # | Test | Steps | Expected | Result | Notes |
|---|------|-------|----------|--------|-------|
| 21.1 | Alt+R opens Radio menu | Press Alt+R | Radio menu opens, NVDA reads first item cleanly | | |
| 21.2 | No stutter/flutter | Press Alt+R | No repeated/garbled speech from NVDA | | |
| 21.3 | Alt+T opens Tools menu | Press Alt+T | Tools menu opens cleanly | | |
| 21.4 | Menu item speech works | Select a menu item | SpeakAfterMenuClose still works (not suppressed) | | |

---

## Screen Reader Matrix

| Area | NVDA | JAWS | Notes |
|------|------|------|-------|
| Menu checkmarks | | N/A | NVDA should say "checked"/"not checked" |
| F1 help speech | | N/A | |
| Confirmation tones | | N/A | Audio, not speech — should work everywhere |
| Filter edge mode speech | | N/A | |
| Frequency units speech | | N/A | |
| TX lockout message | | N/A | |
| Menu accelerator | | N/A | Primary fix target |

---

## Notes

- Items marked N/A for JAWS = test later if JAWS access available
- "Confirmation tone" tests are audio (speakers/headphones) not screen reader
- Filter edge mode has a 10s timeout — test that it actually fires
- Config naming migration: check %AppData%\JJFlexRadio for file renames
