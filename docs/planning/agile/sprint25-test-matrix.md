# Sprint 25 Test Matrix

**Version:** 4.1.16
**Branch:** sprint25/track-a
**Radio:** Don's 6300 via SmartLink (primary), local if available

## Phase 1: Quick Fixes

- [ ] **Hotkey conflict**: Press Ctrl+Alt+S. Should open Status Dialog, NOT start a scan.
- [ ] **Ding on frequency entry**: Press a digit to start quick-type, then Enter. Should hear a ding before the frequency confirmation speech.
- [ ] **Slice clamp**: Cycle slices with Up arrow until you reach the last slice. Should hear "Last slice" and stop (no wrap). Cycle Down to first — should hear "First slice."
- [ ] **Status Dialog selection**: Open Status Dialog (Ctrl+Alt+S). Arrow down to select an item. Wait 5+ seconds for refresh. Selection should stay on the same item, not jump to top.

## Phase 2: Slice Ops Label + Modern Mode

- [ ] **Slice Operations label**: Tab to Slice Operations field. NVDA should say "Slice A Operations: Volume 60" (or whatever volume is).
- [ ] **Modern mode frequency**: Switch to Modern tuning mode (Ctrl+Shift+M). Tab to Frequency field. Left/Right arrows should jump between fields, NOT move digit-by-digit within frequency.

## Phase 3: Settings Sliders + DSP Refresh

- [ ] **Volume controls accessible**: Open Settings, go to Audio tab. Tab to Master volume, Alert volume, Meter volume. Each should be a ValueFieldControl (Up/Down changes by 5, screen reader announces value), NOT a slider.
- [ ] **DSP refresh on mode change**: Connect to radio. Toggle NR on. Change mode (e.g., USB to CW via Alt+C). Check DSP panel (Ctrl+Shift+N) — NR state should update immediately.

## Phase 4: Earcon Mute + Connection Menu

- [ ] **Earcon mute**: Press Ctrl+J then Shift+T. Should hear "Alert sounds off." Press again — "Alert sounds on." While muted, confirm earcons don't play but meter tones still work.
- [ ] **Connection menu rebuild**: Connect to a radio. Open Radio menu — should show "Disconnect." Disconnect. Open Radio menu — should show "Connect to Radio" (not stuck on Disconnect).

## Phase 5: Focus-Return Context

- [ ] **Dialog close announcement**: Open any dialog (e.g., Settings). Close it. Should hear a compact status announcement: "Listening on [freq], [mode], [band], slice [letter]." Should NOT just say "pane."

## Phase 6a: Access Key Announcements

- [ ] **NVDA access keys**: Open Radio Selector. Tab through buttons. Each should announce "Connect, Alt+N", "Low BW, Alt+L", "Remote, Alt+R", etc.
- [ ] **JAWS access keys**: Same test with JAWS. Should announce the access key letter (e.g., "Connect, N").
- [ ] **Settings dialog**: Open Settings. Tab to OK and Cancel. Both should announce access keys.
- [ ] **Spot check 2-3 other dialogs**: Verify access keys are announced.

## Phase 6b: Menu Checkbox State

- [ ] **DSP toggle On**: Enable Legacy NR. Open DSP menu, arrow to Legacy NR. Should say "Legacy NR: On."
- [ ] **DSP toggle Off**: Disable Legacy NR. Open DSP menu again. Should say "Legacy NR: Off."
- [ ] **Other toggles**: Check NB, ANF, meter tones — all should show ": On" or ": Off."

## Phase 7: Easter Eggs + Typing Sounds

- [ ] **Default typing sound**: Start quick-type (press a digit in frequency field). Should hear a click beep on each digit.
- [ ] **Easter egg unlock — test 1**: In frequency field, type the first unlock word then Enter. Should hear verification tone and "Touch-tone mode unlocked!" Open Settings, Audio tab — Frequency Entry Sound dropdown should show "Touch-tone (DTMF)" option.
- [ ] **Easter egg unlock — test 2**: Type the second unlock word then Enter. Should hear verification tone and "Mechanical keyboard mode unlocked!" Settings should now show "Mechanical keyboard" option.
- [ ] **DTMF mode**: Select Touch-tone in Settings. Quick-type digits — should hear DTMF dual-tones.
- [ ] **Mechanical mode**: Select Mechanical keyboard in Settings. Quick-type digits — should hear keyboard clack sounds.
- [ ] **Off mode**: Select Off. Quick-type digits — no sound.
- [ ] **Reset for re-testing**: Type the reset keyword (TBD — needs implementation) to re-lock easter eggs.

## Phase 8: Braille Status Line

- [ ] **Enable braille**: Open Settings, Audio tab. Check "Enable braille status line." Set cell count to match your display (40 for Focus 40).
- [ ] **Home position**: Tab to frequency field (home position). Braille display should show compact status: frequency, mode, S-meter, etc.
- [ ] **Leave home position**: Tab to another field. Braille display should revert to NVDA's normal output.
- [ ] **Return to home**: Tab back to frequency field. Braille status should resume after a moment.

## Phase 9: Action Toolbar

- [ ] **Open toolbar**: Press Ctrl+Tab. Should hear "Actions" dialog with a list of items.
- [ ] **Navigate**: Arrow up/down through items (ATU Tune, Tune Carrier, Transmit, Speak Status, Cancel).
- [ ] **Activate**: Select Speak Status, press Enter. Should hear the full radio status spoken.
- [ ] **Cancel**: Press Escape. Should close the toolbar.

## Phase 10-11: NR Providers (not wired to audio yet)

- [ ] **Build verification**: Confirm both NuGet packages (YellowDogMan.RRNoise.NET, FftSharp) are included and build succeeds.
- [ ] **NR gating on 6300**: Neural NR (RNN), Spectral NR (NRS), and NR Filter (NRF) should NOT appear in DSP menu or ScreenFieldsPanel on a 6300. Legacy NR and ANF should appear.

## SmartLink Multi-Account

- [ ] **Default account (Don)**: Launch app, hit Remote. Should connect to Don's 6300 silently (no login page after first time).
- [ ] **Switch to Justin**: Click Switch Account. In Manage SmartLink, select Justin, click Set Default. Should hear "Default account set to [Justin's name]."
- [ ] **Connect Justin**: Hit Remote. Should discover Justin's 8400 (login page only on first use for Justin's profile).
- [ ] **Switch back to Don**: Switch Account, select Don, Set Default. Hit Remote. Should connect to Don silently.
- [ ] **Persistence**: Close app, relaunch, hit Remote. Should use whichever account was last Set Default.

## NR Gating

- [ ] **6300**: NR menu should show only Legacy NR. NRF, NRS, RNN should be hidden.
- [ ] **Ctrl+J, R on 6300**: Should say "Neural NR not available on this radio."
- [ ] **Ctrl+J, S on 6300**: Should say "Spectral NR not available on this radio."
- [ ] **Ctrl+J, Shift+N on 6300**: Should say "NR Filter not available on this radio."
- [ ] **ANF on 6300**: Should work normally (Ctrl+J, A toggles Auto Notch).

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
