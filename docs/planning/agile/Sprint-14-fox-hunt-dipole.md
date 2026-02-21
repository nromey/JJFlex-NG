# Sprint 14: Fox Hunt Dipole

**Status:** PLANNING
**Target Version:** 4.1.116
**Branch:** main
**Planned Dates:** Feb 2026

---

## Goal

Give screen reader users direct, keyboard-navigable access to radio parameters that are currently buried in menus. Build a **ScreenFieldsPanel** — expandable categories of focusable, UIA-native controls — so operators can Tab to "Noise Reduction," expand it, toggle Neural NR, Tab to "Audio," adjust volume with arrow keys, all without touching a menu.

The parameters stay in menus too (Sprint 13C/D wired those), but now they're also **on-screen, focusable, and keyboard-driven** — the way a screen reader user actually wants to work.

Companion items: speech debounce during tuning + enhanced slice menu.

---

## Track Summary

| Track | Focus | Worktree | Branch |
|-------|-------|----------|--------|
| A | ScreenFieldsPanel — framework + all categories | `C:\dev\JJFlex-NG` | `sprint14/track-a` |
| B | Speech debounce + Slice menu | `../jjflex-14b` | `sprint14/track-b` |

**Execution:** Both tracks start simultaneously. No dependencies between them.
**Merge:** Track B merges into Track A. Then Track A merges to main. Order doesn't matter.

---

## Track A: ScreenFieldsPanel

### Architecture

**New row in MainWindow.xaml Grid (between RadioControls and PanadapterPanel):**

```
MainWindow Grid
├── Row 0: RadioControlsPanel (FreqOut, Mode, TX/Tune, buttons)
├── Row 1: ScreenFieldsPanel  ← NEW
├── Row 2: PanadapterPanel (Waterfall braille, Collapsed by default)
├── Row 3: ContentArea (Received + Sent text, fills remaining space)
└── Row 4: LoggingPanel (Collapsed unless Logging mode)
```

**ScreenFieldsPanel** is a WPF UserControl containing a vertical StackPanel of WPF Expander controls. Each Expander represents a category and contains focusable field controls inside it.

**Tab chain update:** FreqOut(0) → ScreenFieldsPanel(1) → Waterfall(2) → Received(3) → Sent(4)

**Visibility:**
- Classic mode: Visible (this is where screen field expanders live)
- Modern mode: Collapsed (Modern uses menus from Sprint 13D)
- Logging mode: Collapsed

**Toggle:** ScreenFields menu gets a "Show/Hide Field Panel" item + a hotkey (TBD — maybe Ctrl+Shift+P or F3). Lets operators hide the panel if they prefer menu-only access.

### How UIA Navigation Works

```
Tab → lands on first Expander header: "Noise Reduction, collapsed"
  Enter → expands category
  Tab → first field: "Neural NR, checkbox, not checked"
  Space → toggles, NVDA says "checked"
  Tab → "Spectral NR, checkbox, not checked"
  Tab → next Expander header: "Audio, collapsed"
  Enter → expands
  Tab → "Volume: 50"
  Up → "Volume: 55" (speaks new value)
  Down → "Volume: 50" (speaks new value)
  Tab → next field or next Expander...
```

**Category jumping:** Ctrl+Tab jumps to the next Expander header. Ctrl+Shift+Tab jumps to the previous one. This lets operators skip past all fields in an expanded category without tabbing through each one.

Escape from any field → focus returns to FreqOut (consistent with menu Escape behavior).

### Field Control Types

| Type | WPF Implementation | Screen Reader Announcement | Keyboard |
|------|-------------------|---------------------------|----------|
| **Toggle** | CheckBox | "Neural NR, checkbox, checked" | Space toggles |
| **Value** | Focusable UserControl (label + value) | "Volume: 50" | Up/Down ±step |
| **Cycle** | Focusable UserControl (label + option) | "AGC Mode: Slow" | Up/Down cycles options |
| **Action** | Button | "Narrow Filter, button" | Enter/Space fires |

**Toggle controls** use standard WPF CheckBox — perfect UIA support, no custom automation peer needed. NVDA natively announces "checked"/"not checked" state.

**Value controls** are a custom `ValueFieldControl` UserControl. Focusable, shows "Label: Value" text, handles Up/Down arrow keys for adjustment. `AutomationProperties.Name` updates dynamically (e.g., "Volume: 55"). Calls `ScreenReaderOutput.Speak()` on each value change so NVDA hears the new value.

**Cycle controls** are similar to Value but cycle through discrete options (e.g., AGC Off → Slow → Medium → Fast → Off).

**Action controls** use standard WPF Button for one-shot operations (filter narrow/widen, edge shift).

### Distinguishing User vs Poll Updates

Same pattern as RadioComboBox: a `_polling` flag set during poll-timer updates. Field controls ignore programmatic changes during polling so they don't re-fire Rig commands or speech when the poll timer refreshes displayed values.

```csharp
private bool _polling;

private void PollUpdate()
{
    _polling = true;
    try
    {
        neuralNrCheckBox.IsChecked = Rig?.NeuralNoiseReduction == FlexBase.OffOnValues.on;
        // ... etc
    }
    finally { _polling = false; }
}

private void NeuralNr_Changed(object sender, RoutedEventArgs e)
{
    if (_polling) return;
    Rig.NeuralNoiseReduction = neuralNrCheckBox.IsChecked == true
        ? FlexBase.OffOnValues.on : FlexBase.OffOnValues.off;
    ScreenReaderOutput.Speak(neuralNrCheckBox.IsChecked == true ? "Neural NR on" : "Neural NR off");
}
```

### Categories and Fields

#### DSP (Noise & Filters)

| Field | Type | Rig Property | Notes |
|-------|------|-------------|-------|
| Neural NR (RNN) | Toggle | `NeuralNoiseReduction` | |
| Spectral NR (NRS) | Toggle | `SpectralNoiseReduction` | |
| Legacy NR | Toggle | `NoiseReductionLegacy` | |
| NR Level | Value | `NRLevel` | 0-15, step 1. Visible when Legacy NR is on |
| Noise Blanker | Toggle | `NoiseBlanker` | |
| NB Level | Value | `NBLevel` | 0-100, step 5. Visible when NB is on |
| Wideband NB | Toggle | `WidebandNoiseBlanker` | |
| WNB Level | Value | `WNBLevel` | 0-100, step 5. Visible when WNB is on |
| FFT Auto-Notch | Toggle | `AutoNotchFFT` | |
| Legacy Auto-Notch | Toggle | `AutoNotchLegacy` | |
| APF | Toggle | `APF` | CW modes only — hide in voice modes |

#### Audio

| Field | Type | Rig Property | Notes |
|-------|------|-------------|-------|
| Mute | Toggle | `SliceMute` | |
| Volume | Value | `AudioGain` | 0-100, step 5 |
| Pan | Value | `AudioPan` | 0-100 (50=center), step 5 |
| Headphone Level | Value | `HeadphoneGain` | 0-100, step 5 |
| Line Out Level | Value | `LineoutGain` | 0-100, step 5 |

#### Receiver

| Field | Type | Rig Property | Notes |
|-------|------|-------------|-------|
| AGC Mode | Cycle | `AGCSpeed` | Off / Slow / Medium / Fast |
| AGC Threshold | Value | `AGCThreshold` | 0-100, step 5 |
| Squelch | Toggle | `Squelch` | |
| Squelch Level | Value | `SquelchLevel` | Visible when Squelch is on |
| RF Gain | Value | `RFGain` | -10 to 30, step 10 |

#### Transmission

| Field | Type | Rig Property | Notes |
|-------|------|-------------|-------|
| TX Power | Value | (TBD — `_XmitPower` / radio property) | 0-100 |
| VOX | Toggle | `Vox` | |
| Tune Power | Value | (TBD — `_TunePower` / radio property) | 0-100 |

#### Antenna

| Field | Type | Rig Property | Notes |
|-------|------|-------------|-------|
| ATU | Toggle | `FlexTunerOn` | Only for radios with ATU |
| ATU Mode | Cycle | `FlexTunerType` | None / Manual / Auto |

#### Filter (stretch goal)

| Field | Type | Handler | Notes |
|-------|------|---------|-------|
| Narrow Filter | Action | `NarrowFilter()` | Button — one-shot |
| Widen Filter | Action | `WidenFilter()` | Button — one-shot |
| Shift Low Edge Up | Action | `ShiftLowEdge(+1)` | Button — one-shot |
| Shift Low Edge Down | Action | `ShiftLowEdge(-1)` | Button — one-shot |
| Shift High Edge Up | Action | `ShiftHighEdge(+1)` | Button — one-shot |
| Shift High Edge Down | Action | `ShiftHighEdge(-1)` | Button — one-shot |

### Conditional Visibility

Some fields and categories depend on radio state:

| Condition | Fields Affected |
|-----------|----------------|
| CW mode only | APF toggle |
| Legacy NR on | NR Level slider |
| NB on | NB Level slider |
| WNB on | WNB Level slider |
| Squelch on | Squelch Level slider |
| Radio has ATU | Antenna category |
| 2-SCU radio | Diversity category (future) |

Use `Visibility = Collapsed` for hidden fields, not `IsEnabled = false`. Collapsed fields are completely removed from the tab order and UIA tree — disabled fields still appear and confuse screen readers ("NR Level, disabled" when NR isn't even on).

### Phases

#### Phase 1: Framework (new files + layout)

1. Create `JJFlexWpf/Controls/ScreenFieldsPanel.xaml` + `.cs`
   - ScrollViewer → StackPanel of Expanders
   - `Initialize(FlexBase rig)` method to wire up after radio connection
   - `PollUpdate()` method called by MainWindow's existing poll timer
   - Public `PanelVisible` property for show/hide toggle
2. Create `JJFlexWpf/Controls/ValueFieldControl.xaml` + `.cs`
   - Focusable UserControl: label + value display
   - Properties: `Label`, `Value`, `Min`, `Max`, `Step`, `FormatString`
   - Events: `ValueChanged` (fires on user Up/Down)
   - Updates `AutomationProperties.Name` on value change
   - Calls `ScreenReaderOutput.Speak()` with new value
3. Create `JJFlexWpf/Controls/CycleFieldControl.xaml` + `.cs`
   - Focusable UserControl: label + current option
   - Properties: `Label`, `Options` (string array), `SelectedIndex`
   - Events: `SelectionChanged`
   - Up/Down cycles through options
4. Insert new Grid row in `MainWindow.xaml` between RadioControlsPanel (Row 0) and PanadapterPanel (current Row 1)
   - Shift PanadapterPanel to Row 2, ContentArea to Row 3, LoggingPanel to Row 4
   - ScreenFieldsPanel at Row 1, Height="Auto"
   - `TabIndex="1"` on the panel (bump Waterfall to 2, Received to 3, Sent to 4)
5. Extend MainWindow poll timer to call `ScreenFieldsPanel.PollUpdate()`
6. Wire visibility: Visible in Classic, Collapsed in Modern/Logging via `ApplyUIMode()`
7. **Build and test** — verify panel appears, expanders expand/collapse, NVDA reads category names

#### Phase 2: DSP Category

8. Add DSP Expander with all NR/NB/ANF/APF toggles as CheckBoxes
9. Wire each CheckBox.Checked/Unchecked → Rig property set + speech
10. Add conditional NR Level / NB Level / WNB Level ValueFieldControls (visible when parent toggle is on)
11. Add APF toggle with CW-only visibility
12. Wire poll updates for all DSP fields
13. **Build and test** — expand DSP, toggle each item, verify radio state changes + NVDA announces correctly

#### Phase 3: Audio Category

14. Add Audio Expander with Mute (CheckBox) + Volume/Pan/Headphone/Lineout (ValueFieldControls)
15. Wire all handlers + speech
16. Wire poll updates
17. **Build and test** — adjust each audio control, verify radio responds + speech feedback

#### Phase 4: Receiver Category

18. Add Receiver Expander with AGC Mode (CycleFieldControl) + AGC Threshold/Squelch Level/RF Gain (ValueFieldControls) + Squelch toggle
19. Wire all handlers + speech
20. Conditional visibility: Squelch Level visible only when Squelch is on
21. Wire poll updates
22. **Build and test**

#### Phase 5: TX + Antenna Categories

23. Add Transmission Expander with TX Power + VOX + Tune Power
24. Add Antenna Expander with ATU toggle + ATU Mode cycle (conditional: only if radio has ATU)
25. Wire all handlers + speech + poll
26. **Build and test**

#### Phase 6: Filter Category (stretch — can defer)

27. Add Filter Expander with action Buttons (Narrow, Widen, Shift edges)
28. Wire button clicks to existing filter handlers from NativeMenuBar
29. Speech feedback on each action
30. **Build and test**

#### Phase 7: Integration + Polish

31. Update tab chain: FreqOut(0) → ScreenFieldsPanel(1) → PanDisplay(2) → Received(3) → Sent(4)
32. Add "Show/Hide Field Panel" to ScreenFields menu + hotkey
33. Escape from panel → return focus to FreqOut
34. Verify all fields update correctly when switching slices (active slice change → poll picks up new values)
35. Verify mode switching: panel appears/disappears correctly in Classic/Modern/Logging
36. Clean build, full accessibility pass with NVDA

### New Files

| File | Purpose |
|------|---------|
| `JJFlexWpf/Controls/ScreenFieldsPanel.xaml` | Panel layout: ScrollViewer + Expanders |
| `JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs` | Poll updates, category setup, Rig wiring |
| `JJFlexWpf/Controls/ValueFieldControl.xaml` | Label + value display with Up/Down adjustment |
| `JJFlexWpf/Controls/ValueFieldControl.xaml.cs` | Keyboard handling, speech, AutomationProperties |
| `JJFlexWpf/Controls/CycleFieldControl.xaml` | Label + option display with Up/Down cycling |
| `JJFlexWpf/Controls/CycleFieldControl.xaml.cs` | Keyboard handling, speech, AutomationProperties |

### Modified Files

| File | Changes |
|------|---------|
| `JJFlexWpf/MainWindow.xaml` | Add Row 1 for ScreenFieldsPanel, shift rows 1-3 → 2-4 |
| `JJFlexWpf/MainWindow.xaml.cs` | Wire panel init, extend poll, mode visibility, tab chain |
| `JJFlexWpf/NativeMenuBar.cs` | Add "Show/Hide Field Panel" menu item |

### Toggle fields use standard CheckBox (no custom control needed)

WPF CheckBox has perfect UIA support out of the box. NVDA reads "Neural NR, checkbox, checked" — exactly what we want. No need for a custom ToggleFieldControl. Just use CheckBox directly in the ScreenFieldsPanel with appropriate `Content` and `AutomationProperties.Name`.

---

## Track B: Speech Debounce + Slice Menu

### B1: Speech Debounce (Rate-Limit Tuning Speech)

**Problem:** Rapid Up/Down tuning fires `ScreenReaderOutput.Speak()` for every step. At 10+ steps/second, the speech queue backs up and NVDA reads stale frequencies.

**Solution:** Tuning-specific debounce in FreqOutHandlers:

1. First tune step always speaks immediately (responsive feel)
2. Subsequent steps within 300ms reset a timer instead of speaking
3. When the 300ms timer fires, speak the CURRENT frequency (not the value from when the timer started)
4. Result: rapid tuning speaks first value, then silence during movement, then final value

```csharp
private CancellationTokenSource? _tuneDebounce;
private bool _firstTuneStep = true;

private void SpeakTuningDebounced(string message)
{
    if (_firstTuneStep)
    {
        ScreenReaderOutput.Speak(message, interrupt: true);
        _firstTuneStep = false;
    }

    _tuneDebounce?.Cancel();
    _tuneDebounce = new CancellationTokenSource();
    var token = _tuneDebounce.Token;

    _ = Task.Run(async () =>
    {
        await Task.Delay(300, token);
        if (!token.IsCancellationRequested)
        {
            // Speak current frequency, not the one from 300ms ago
            var currentFreq = FormatFreqForSpeech(Rig?.RXFrequency ?? 0);
            ScreenReaderOutput.Speak(currentFreq, interrupt: true);
        }
    }, token);
}

// Reset on key-up or focus leave
private void ResetTuneDebounce()
{
    _firstTuneStep = true;
    _tuneDebounce?.Cancel();
}
```

**Files modified:**
- `JJFlexWpf/FreqOutHandlers.cs` — add debounce to `SpeakFrequency()` and `SpeakStep()` calls in `AdjustFreq()` / `AdjustFreqModern()`

### B2: Slice Menu Enhancement

**Current state:** NativeMenuBar's Slice submenu lists available slices for selection. No create/release. No active indicator.

**Add these items to the Slice submenu:**

1. **Active slice indicator** — checkmark on the active slice in the list
   - Use existing `AddChecked()` pattern with state getter checking `Rig.ActiveSliceIndex == i`

2. **"New Slice"** menu item → `Rig.NewSlice()`
   - Grayed out when at max slices (`Rig.TotalNumSlices >= Rig.MaxSlices`)
   - Speech: "Slice N created" or "Cannot create slice, maximum reached"

3. **"Release Slice N"** menu item → `Rig.RemoveSlice(activeSliceIndex)`
   - Only appears when more than 1 slice exists
   - Speech: "Slice N released, now on slice M"

4. **Submenu label** shows count: "Slice (2 of 4)" instead of just "Slice"
   - Updated via `WM_INITMENUPOPUP` handler

**Files modified:**
- `JJFlexWpf/NativeMenuBar.cs` — modify `BuildSliceItems()` in both Classic and Modern menu builders

---

## Risks

| Risk | Impact | Mitigation |
|------|--------|------------|
| WPF Expander UIA inside ElementHost | Expanders might not announce correctly to NVDA/JAWS | Test in Phase 1 before building all categories. WPF Expanders legitimately use ExpandCollapsePattern (unlike WPF Menu which was the problem in Sprint 12). If broken, fall back to flat StackPanel with group labels. |
| Poll performance with 30+ field controls | 100ms poll updating many controls could cause lag | Only update fields in **expanded** categories. Collapsed expanders skip poll updates entirely. |
| Keyboard conflicts | Up/Down in ValueFieldControl vs other Up/Down handlers | Field controls only handle Up/Down when they have focus. PreviewKeyDown with `e.Handled = true` prevents bubbling. |
| Slice change invalidates all field values | Switching active slice → all DSP/Audio/Receiver values change | Poll timer naturally picks up new slice values. Force immediate poll on slice change event for responsiveness. |
| Feature gating | Some fields invalid for certain radios (ATU, Diversity) | Check radio capabilities in `Initialize()`. Use `Collapsed` visibility for unavailable categories — don't show disabled controls. |

---

## Test Matrix (Summary)

Detailed matrix will be created at `docs/planning/agile/sprint14-test-matrix.md` during testing.

### ScreenFieldsPanel

- [ ] Panel visible in Classic mode, hidden in Modern/Logging
- [ ] Each category expands/collapses with Enter/Space
- [ ] NVDA announces "expanded" / "collapsed" correctly on Expanders
- [ ] Tab navigates between categories and fields within expanded categories
- [ ] Shift+Tab reverses
- [ ] Escape returns focus to FreqOut
- [ ] Show/Hide toggle works (menu + hotkey)

### Toggle Fields (CheckBox)

- [ ] Each toggle changes radio state on Space
- [ ] NVDA announces "checked" / "not checked"
- [ ] Speech feedback: "Neural NR on" / "Neural NR off"
- [ ] Poll updates reflect external changes (SmartSDR adjusts same parameter)
- [ ] APF only visible in CW modes

### Value Fields

- [ ] Up/Down adjusts value by correct step
- [ ] Value clamps at min/max (no wrap)
- [ ] NVDA announces new value on each step
- [ ] Speech: "Volume 55" (label + value)
- [ ] Level fields (NR Level, NB Level) only visible when parent toggle is on

### Cycle Fields

- [ ] Up/Down cycles through options
- [ ] Wraps around (Fast → Off, Off → Slow)
- [ ] NVDA announces new option
- [ ] Speech: "AGC Mode: Fast"

### Speech Debounce

- [ ] First tune step speaks immediately
- [ ] Rapid steps (held arrow key) only speak first and last values
- [ ] Pause > 300ms between steps → both values spoken
- [ ] Debounce resets on focus change or key release

### Slice Menu

- [ ] Active slice has checkmark
- [ ] "New Slice" creates slice, speaks confirmation
- [ ] "New Slice" grayed out at max slices
- [ ] "Release Slice" removes current slice, speaks which slice is now active
- [ ] "Release Slice" hidden when only 1 slice exists
- [ ] Submenu label shows "Slice (N of M)"

---

## Deferred to Sprint 15+

- **Configurable tuning step lists** — user adds/removes from coarse/fine presets (needs a dialog + operator profile persistence)
- **Global tuning hotkeys** — system-wide hotkeys for tuning/transmit/lock from external apps (loggers, contest programs). Needs Win32 RegisterHotKey or similar, plus external control interface design. Major feature.
- **Logging mode F6 flip** — F6 to show FreqOut from logging mode (needs UX design for the overlay/split)
- **Alt+letter menu accelerators** — investigation into ElementHost → HMENU Alt routing (may be a dead end due to WPF interop)
- **Menu state display for non-toggle items** — show current values on menu items ("AGC Threshold: -70"). Less urgent now that ScreenFieldsPanel shows values directly.
- **Diversity category** — for 2-SCU radios (FLEX-6600/6700/8600), needs FlexBase.DiversityIsAllowed gating
- **Filter category** — action buttons for filter adjust (lower priority since filters are less frequently tweaked and already in menus)
- **Replace Tolk with UIA LiveRegion** for most speech
- **Earcons** (boundary bonk, field tick, tune tones)
- **ModeControl back in tab chain** after FreqOut
