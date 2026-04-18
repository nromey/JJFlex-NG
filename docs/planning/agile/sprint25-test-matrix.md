# Sprint 25 Test Matrix

**Version:** 4.1.16
**Branch:** sprint25/track-a
**Radio:** Don's 6300 via SmartLink (primary), local if available

## Phase 1: Quick Fixes

- [x] **Hotkey conflict**: Press Ctrl+Alt+S. Should open Status Dialog, NOT start a scan.
- [x] **Ding on frequency entry**: Press a digit to start quick-type, then Enter. Should hear a ding before the frequency confirmation speech.
- [x] **Slice clamp**: Cycle slices with Up arrow until you reach the last slice. Should hear "Last slice" and stop (no wrap). Cycle Down to first — should hear "First slice."
- [x] **Status Dialog selection**: Open Status Dialog (Ctrl+Alt+S). Arrow down to select an item. Wait 5+ seconds for refresh. Selection should stay on the same item, not jump to top.

## Phase 2: Slice Ops Label + Modern Mode

- [x] **Slice Operations label**: Tab to Slice Operations field. NVDA should say "Slice A Operations: Volume 60" (or whatever volume is).
- [x] **Modern mode frequency**: Switch to Modern tuning mode (Ctrl+Shift+M). Tab to Frequency field. Left/Right arrows should jump between fields, NOT move digit-by-digit within frequency.

## Phase 3: Settings Sliders + DSP Refresh

- [x] **Volume controls accessible**: Open Settings, go to Audio tab. Tab to Master volume, Alert volume, Meter volume. Each should be a ValueFieldControl (Up/Down changes by 5, screen reader announces value), NOT a slider. *Fix applied during testing: ValueFieldControl now honors configured Step (was hardcoded to 1); surplus cold-Enter re-prompt path removed.*
- [x] **DSP refresh on mode change**: Connect to radio. Toggle NR on. Change mode (e.g., USB to CW via Alt+C). Check DSP panel (Ctrl+Shift+N) — NR state should update immediately. *Root-cause investigation found firmware stops applying Legacy NR after DemodMode round-trip though flag reads back as on. Client-side workaround: delayed off-then-on (150 ms pre-delay + 500 ms gap between off and on) — implemented in FlexBase.cs DemodMode handler. Upstream bug report appended to flexlib-discovery-nre-report.txt.*

## Phase 4: Earcon Mute + Connection Menu

- [x] **Earcon mute**: Press Ctrl+J then Shift+T. Should hear "Alert sounds off." Press again — "Alert sounds on." While muted, confirm earcons don't play but meter tones still work.
- [x] **Connection menu rebuild**: Connect to a radio. Open Radio menu — should show "Disconnect." Disconnect. Open Radio menu — should show "Connect to Radio" (not stuck on Disconnect). *Verified passing from prior testing.*

## Phase 5: Focus-Return Context

- [x] **Dialog close announcement**: Open any dialog (e.g., Settings). Close it. Should hear a compact status announcement: "Listening on [freq], [mode], [band], slice [letter]." Should NOT just say "pane."

## Phase 6a: Access Key Announcements

- [x] **NVDA access keys**: Open Radio Selector. Tab through buttons. Each should announce "Connect, Alt+N", "Low BW, Alt+L", "Remote, Alt+R", etc.
- [x] **JAWS access keys**: Same test with JAWS. Should announce the access key letter (e.g., "Connect, N").
- [x] **Settings dialog**: Open Settings. Tab to OK and Cancel. Both should announce access keys.
- [x] **Spot check 2-3 other dialogs**: Verify access keys are announced. *Noel spot-checked multiple dialogs; no dialog missed.*

## Phase 6b: Menu Checkbox State

- [x] **DSP toggle On**: Enable Legacy NR. Open DSP menu, arrow to Legacy NR. Should say "Legacy NR: On."
- [x] **DSP toggle Off**: Disable Legacy NR. Open DSP menu again. Should say "Legacy NR: Off."
- [x] **Other toggles**: Check NB, ANF, meter tones — all should show ": On" or ": Off."

## Phase 7: Easter Eggs + Typing Sounds

- [x] **Default typing sound**: Start quick-type (press a digit in frequency field). Should hear a click beep on each digit.
- [x] **Easter egg unlock — test 1**: In frequency field, type the first unlock word then Enter. Should hear verification tone and "Touch-tone mode unlocked!" Open Settings, Audio tab — Frequency Entry Sound dropdown should show "Touch-tone (DTMF)" option.
- [x] **Easter egg unlock — test 2**: Type the second unlock word then Enter. Should hear verification tone and "Mechanical keyboard mode unlocked!" Settings should now show "Mechanical keyboard" option.
- [x] **DTMF mode**: Select Touch-tone in Settings. Quick-type digits — should hear DTMF dual-tones.
- [x] **Mechanical mode**: Select Mechanical keyboard in Settings. Quick-type digits — should hear keyboard clack sounds.
- [x] **Off mode**: Select Off. Quick-type digits — no sound. *"Off" is pinned to bottom of dropdown regardless of which unlocks are active — fix applied during testing.*
- [x] **Reset for re-testing**: Type the reset keyword to re-lock easter eggs. *Reset is implemented (`HandleCalibrationUnlock` in FreqOutHandlers.cs). Plaintext word recovered via brute-force and now documented in private docs.*

## Phase 8: Braille Status Line

- [x] **Enable braille**: Open Settings, Audio tab. Check "Enable braille status line." Set cell count to match your display (40 for Focus 40). *Fix applied during testing: Settings OK now re-applies braille config to the engine at runtime (was only applied at PowerOn).*
- [x] **Home position**: Tab to frequency field (home position). Braille display should show compact status: frequency, mode, S-meter, etc. *Verified on Focus 40 via SmartLink to Don's 6300 — showed "3.730 lsb sm8" then updated live to sm9 as noise floor changed.*
- [x] **Leave home position**: Tab to another field. Braille display should revert to NVDA's normal output.
- [x] **Return to home**: Tab back to frequency field. Braille status should resume after a moment. *Fix applied during testing: removed _lastPushed change-detection and shortened timer interval from 1s to 500ms so NVDA can no longer reclaim the display between pushes during stable radio state.*

## Phase 9: Action Toolbar

- [x] **Open toolbar**: Press Ctrl+Tab. Should hear "Actions" dialog with a list of items.
- [x] **Navigate**: Arrow up/down through items (ATU Tune, Tune Carrier, Transmit, Speak Status, Cancel). *First item "Start Tune Carrier" is the Ctrl+Shift+T equivalent; label flips to "Stop Tune Carrier" when tuning is active — doubles as status readout.*
- [x] **Activate**: Select Speak Status, press Enter. Should hear the full radio status spoken.
- [x] **Cancel**: Press Escape. Should close the toolbar.

## Phase 10-11: NR Providers (not wired to audio yet)

- [x] **Build verification**: Confirm both NuGet packages (YellowDogMan.RRNoise.NET, FftSharp) are included and build succeeds. *Verified across every build today — 0 errors.*
- [x] **NR gating on 6300**: Neural NR (RNN), Spectral NR (NRS), and NR Filter (NRF) should NOT appear in DSP menu or ScreenFieldsPanel on a 6300. Legacy NR and ANF should appear. *Negative gating verified on Don's 6300; hotkey tests (Ctrl+J R/S/Shift+N) all announce "not available on this radio" correctly.* Positive half (features visible on >6300) deferred pending 8600 unbox or Justin's 8400 SmartLink access.

## SmartLink Multi-Account

- [ ] **Default account (Don)**: Launch app, hit Remote. Should connect to Don's 6300 silently (no login page after first time).
- [ ] **Switch to Justin**: Click Switch Account. In Manage SmartLink, select Justin, click Set Default. Should hear "Default account set to [Justin's name]."
- [ ] **Connect Justin**: Hit Remote. Should discover Justin's 8400 (login page only on first use for Justin's profile).
- [ ] **Switch back to Don**: Switch Account, select Don, Set Default. Hit Remote. Should connect to Don silently.
- [ ] **Persistence**: Close app, relaunch, hit Remote. Should use whichever account was last Set Default.

## NR Gating

- [x] **6300**: NR menu should show only Legacy NR. NRF, NRS, RNN should be hidden.
- [x] **Ctrl+J, R on 6300**: Should say "Neural NR not available on this radio."
- [x] **Ctrl+J, S on 6300**: Should say "Spectral NR not available on this radio."
- [x] **Ctrl+J, Shift+N on 6300**: Should say "NR Filter not available on this radio."
- [x] **ANF on 6300**: Should work normally (Ctrl+J, A toggles Auto Notch).

## Screen Reader Matrix

### NVDA
- [ ] Access keys on all dialog buttons (spot check 5 dialogs)
- [ ] Menu toggle states (On/Off)
- [ ] Focus-return context after dialog close
- [ ] Action toolbar navigation
- [ ] Slice Operations label reads correctly
- [ ] Status Dialog holds selection on refresh

### JAWS
- [ ] Access keys on dialog buttons (spot check 3 dialogs)
- [ ] Menu toggle states
- [ ] Focus-return context
- [ ] Action toolbar
