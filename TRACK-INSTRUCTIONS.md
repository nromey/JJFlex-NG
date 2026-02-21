# Sprint 14 — Track A: ScreenFieldsPanel

**Branch:** `sprint14/track-a`
**Worktree:** `C:\dev\JJFlex-NG`
**Plan:** `docs/planning/agile/Sprint-14-fox-hunt-dipole.md`

---

## What You're Building

A new **ScreenFieldsPanel** — a WPF UserControl with expandable categories of focusable, UIA-native controls for radio parameters. Screen reader users Tab into the panel, expand categories (DSP, Audio, Receiver, TX, Antenna), and adjust values with keyboard. No menu diving needed.

This is the centerpiece of Sprint 14.

---

## Architecture

### Panel Placement (MainWindow.xaml)

Insert a new Grid row between RadioControlsPanel (Row 0) and PanadapterPanel (currently Row 1):

```
Row 0: RadioControlsPanel (FreqOut, Mode, TX/Tune, buttons) — EXISTING
Row 1: ScreenFieldsPanel  ← NEW (Height="Auto")
Row 2: PanadapterPanel    ← was Row 1 (Waterfall braille)
Row 3: ContentArea         ← was Row 2 (Received + Sent)
Row 4: LoggingPanel        ← was Row 3 (Logging mode)
```

Update Grid.Row attributes on PanadapterPanel, ContentArea, and LoggingPanel.

### Tab Chain Update

```
FreqOut        TabIndex="0"  (unchanged)
ScreenFieldsPanel  TabIndex="1"  (NEW)
PanDisplayBox  TabIndex="2"  (was 1)
ReceivedTextBox TabIndex="3" (was 2)
SentTextBox    TabIndex="4"  (was 3)
```

### Visibility Rules

- Classic mode: `Visible`
- Modern mode: `Collapsed`
- Logging mode: `Collapsed`

Wire in `ApplyUIMode()` in MainWindow.xaml.cs.

### Panel Structure

```
ScreenFieldsPanel (UserControl)
└── StackPanel
    ├── Expander Header="Noise Reduction and DSP"
    │   └── StackPanel
    │       ├── CheckBox "Neural NR (RNN)"
    │       ├── CheckBox "Spectral NR (NRS)"
    │       ├── CheckBox "Legacy NR"
    │       ├── ValueFieldControl "NR Level" (visible when Legacy NR on)
    │       ├── CheckBox "Noise Blanker"
    │       ├── ValueFieldControl "NB Level" (visible when NB on)
    │       ├── CheckBox "Wideband NB"
    │       ├── ValueFieldControl "WNB Level" (visible when WNB on)
    │       ├── CheckBox "FFT Auto-Notch"
    │       ├── CheckBox "Legacy Auto-Notch"
    │       └── CheckBox "APF" (CW modes only)
    │
    ├── Expander Header="Audio"
    │   └── StackPanel
    │       ├── CheckBox "Mute"
    │       ├── ValueFieldControl "Volume" (0-100, step 5)
    │       ├── ValueFieldControl "Pan" (0-100, step 5)
    │       ├── ValueFieldControl "Headphone Level" (0-100, step 5)
    │       └── ValueFieldControl "Line Out Level" (0-100, step 5)
    │
    ├── Expander Header="Receiver"
    │   └── StackPanel
    │       ├── CycleFieldControl "AGC Mode" (Off/Slow/Medium/Fast)
    │       ├── ValueFieldControl "AGC Threshold" (0-100, step 5)
    │       ├── CheckBox "Squelch"
    │       ├── ValueFieldControl "Squelch Level" (visible when Squelch on)
    │       └── ValueFieldControl "RF Gain" (-10 to 30, step 10)
    │
    ├── Expander Header="Transmission"
    │   └── StackPanel
    │       ├── ValueFieldControl "TX Power" (0-100)
    │       ├── CheckBox "VOX"
    │       └── ValueFieldControl "Tune Power" (0-100)
    │
    └── Expander Header="Antenna" (only if radio has ATU)
        └── StackPanel
            ├── CheckBox "ATU"
            └── CycleFieldControl "ATU Mode" (None/Manual/Auto)
```

---

## Custom Controls to Create

### ValueFieldControl (`JJFlexWpf/Controls/ValueFieldControl.xaml` + `.cs`)

A focusable UserControl showing "Label: Value" with Up/Down arrow key adjustment.

**Properties:**
- `Label` (string) — e.g., "Volume"
- `Value` (int) — current value
- `Min`, `Max`, `Step` (int) — bounds and increment
- `FormatString` (string, optional) — e.g., "{0}%" or "{0} dB"
- `Unit` (string, optional) — spoken unit, e.g., "percent"

**Behavior:**
- `Focusable="True"`, `IsTabStop="True"`
- `AutomationProperties.Name` = "Volume: 50" (updated dynamically)
- Up arrow: `Value = Math.Min(Value + Step, Max)` → speak new value
- Down arrow: `Value = Math.Max(Value - Step, Min)` → speak new value
- `ValueChanged` event for parent to wire to Rig property

**Visual:** Border with TextBlock showing "Volume: 50". Focus rectangle on focus.

### CycleFieldControl (`JJFlexWpf/Controls/CycleFieldControl.xaml` + `.cs`)

A focusable UserControl showing "Label: Option" with Up/Down cycling.

**Properties:**
- `Label` (string) — e.g., "AGC Mode"
- `Options` (string[]) — e.g., ["Off", "Slow", "Medium", "Fast"]
- `SelectedIndex` (int) — current option index

**Behavior:**
- Same focus/tab/accessibility pattern as ValueFieldControl
- `AutomationProperties.Name` = "AGC Mode: Slow"
- Up: next option (wraps from last to first)
- Down: previous option (wraps from first to last)
- `SelectionChanged` event

### Toggle Fields — Use Standard WPF CheckBox

No custom control needed. WPF CheckBox has perfect UIA support:
- NVDA reads: "Neural NR, checkbox, checked"
- Space toggles
- Set `Content` to the label, wire `Checked`/`Unchecked` events

---

## Polling Pattern (CRITICAL)

FlexBase does NOT implement INotifyPropertyChanged. Use polling with a `_polling` flag to distinguish user actions from poll updates:

```csharp
private bool _polling;

public void PollUpdate()
{
    if (Rig == null) return;
    _polling = true;
    try
    {
        // Only update expanded categories for performance
        if (dspExpander.IsExpanded)
        {
            neuralNrCheck.IsChecked = Rig.NeuralNoiseReduction == FlexBase.OffOnValues.on;
            // ... etc
        }
        if (audioExpander.IsExpanded)
        {
            volumeControl.Value = Rig.AudioGain;
            // ... etc
        }
    }
    finally { _polling = false; }
}

private void NeuralNr_Changed(object sender, RoutedEventArgs e)
{
    if (_polling || Rig == null) return;
    Rig.NeuralNoiseReduction = neuralNrCheck.IsChecked == true
        ? FlexBase.OffOnValues.on : FlexBase.OffOnValues.off;
    ScreenReaderOutput.Speak(neuralNrCheck.IsChecked == true ? "Neural NR on" : "Neural NR off");
}
```

**Performance rule:** Only poll fields inside **expanded** Expanders. Collapsed categories skip updates entirely.

---

## Category Jumping (Ctrl+Tab / Ctrl+Shift+Tab)

When focus is anywhere inside the ScreenFieldsPanel, Ctrl+Tab jumps to the next Expander header and Ctrl+Shift+Tab jumps to the previous one. This lets operators skip past all fields in an expanded category without tabbing through each field.

Handle in the panel's PreviewKeyDown:

```csharp
if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Control)
{
    FocusNextExpander(forward: true);
    e.Handled = true;
}
else if (e.Key == Key.Tab && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
{
    FocusNextExpander(forward: false);
    e.Handled = true;
}
```

`FocusNextExpander()` finds the current Expander (walk up visual tree from focused element), then moves focus to the next/previous Expander's header. Wrap around at boundaries.

---

## Escape Key Handling

Escape from anywhere in the ScreenFieldsPanel should return focus to FreqOut. Handle in the panel's PreviewKeyDown:

```csharp
private void Panel_PreviewKeyDown(object sender, KeyEventArgs e)
{
    if (e.Key == Key.Escape)
    {
        // Return focus to FreqOut (parent MainWindow handles this)
        EscapePressed?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}
```

MainWindow wires `EscapePressed` to `FreqOut.FocusDisplay()`.

---

## Conditional Visibility

Use `Visibility = Collapsed` (not `IsEnabled = false`) for unavailable fields:
- APF: Collapsed unless current mode is CW/CWL/CWU
- NR Level: Collapsed unless Legacy NR checkbox is checked
- NB Level: Collapsed unless NB checkbox is checked
- WNB Level: Collapsed unless WNB checkbox is checked
- Squelch Level: Collapsed unless Squelch checkbox is checked
- Antenna category: Collapsed if radio has no ATU

Collapsed removes the control from tab order and UIA tree entirely — disabled controls still appear and confuse screen readers.

---

## Show/Hide Toggle

Add to ScreenFields menu in NativeMenuBar.cs:
- "Show Field Panel" / "Hide Field Panel" (text changes based on state)
- Or use a checkmark item via `AddChecked()`
- Hotkey TBD (Ctrl+Shift+P suggested, discuss with user)

---

## Phases (commit after each)

1. **Framework** — ScreenFieldsPanel + ValueFieldControl + CycleFieldControl + MainWindow layout changes + poll wiring + visibility. Commit: `Sprint 14 Track A: ScreenFieldsPanel framework and layout`
2. **DSP category** — all NR/NB/ANF/APF toggles + conditional levels. Commit: `Sprint 14 Track A: DSP category with NR, NB, notch, APF controls`
3. **Audio category** — Mute + Volume/Pan/Headphone/Lineout. Commit: `Sprint 14 Track A: Audio category with volume, pan, mute controls`
4. **Receiver category** — AGC mode/threshold + Squelch + RF Gain. Commit: `Sprint 14 Track A: Receiver category with AGC, squelch, RF gain`
5. **TX + Antenna categories** — Power/VOX/Tune Power + ATU. Commit: `Sprint 14 Track A: TX and antenna categories`
6. **Filter category** (stretch) — action buttons. Commit: `Sprint 14 Track A: Filter category with action buttons`
7. **Integration + polish** — tab chain, Escape, show/hide toggle, mode switching, final NVDA pass. Commit: `Sprint 14 Track A: Integration, tab chain, and accessibility polish`

---

## Build Command

```batch
dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal
```

Build after each phase. Verify no errors before committing.

---

## Key Reference Files

| File | What's There |
|------|-------------|
| `JJFlexWpf/MainWindow.xaml` | Grid layout — you're adding Row 1 |
| `JJFlexWpf/MainWindow.xaml.cs` | Poll timer (~line 192), ApplyUIMode (~line 345), tab chain |
| `JJFlexWpf/NativeMenuBar.cs` | Menu builders — BuildDSPItems (240), BuildAudioItems (354), BuildReceiverItems (456) — use same Rig properties |
| `JJFlexWpf/Controls/FrequencyDisplay.xaml.cs` | DisplayField pattern, SilentTextBox — reference for focusable control patterns |
| `Radios/FlexBase.cs` | All Rig properties (NeuralNoiseReduction, AudioGain, AGCSpeed, etc.) |
| `JJFlexWpf/ScreenReaderOutput.cs` | Speak() method for speech feedback |
| `JJFlexWpf/Controls/RadioComboBox.cs` | `_userEntry` polling flag pattern — use same approach |

## Screen Reader Rules (from MEMORY.md)

- **Let NVDA read standard controls natively.** CheckBox, Expander, Button — NVDA knows these. Don't override with Speak() on focus.
- **Use Speak() only for value CHANGES** — when user presses Up/Down to adjust a value, speak the new value. Don't speak on focus enter (NVDA already reads the control).
- **Speak(interrupt=False)** for value changes — don't stomp on NVDA's focus announcement.
- **Use Collapsed, not Disabled** — disabled controls confuse screen readers. Hide what's not available.
