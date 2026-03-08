# Sprint 21 — Track B: Audio Workshop + ScreenFields TX Expansion + Earcon Explorer

**Branch:** `sprint21/track-b`
**Worktree:** `C:\dev\jjflex-21b`

---

## Overview

Build the Audio Workshop dialog (a 3-tab WPF dialog for TX audio sculpting, live meters, and earcon exploration), expand the ScreenFields Transmission category with all TX audio controls, and add audio chain presets. Also fix BUG-023 (connect while connected confirmation).

**Read the full sprint plan first:** `docs/planning/agile/sprint21-resonant-signal-sculpt.md`

---

## Build & Test

```batch
# Build (from this worktree directory)
dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64

# Verify timestamp
powershell -Command "(Get-Item 'bin\x64\Debug\net8.0-windows\win-x64\JJFlexRadio.exe').LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')"
```

**IMPORTANT:** Close any running JJFlexRadio before building (Radios.dll lock).

---

## Commit Strategy

Commit after each phase. Message format: `Sprint 21 Track B: <description>`

---

## New Files to Create

### 1. `Radios/AudioChainPreset.cs`

TX audio chain preset — save/load/share audio configurations.

```csharp
using System.Xml.Serialization;

namespace Radios
{
    [XmlRoot("AudioChainPreset")]
    public class AudioChainPreset
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public int MicGain { get; set; } = 50;
        public bool MicBoost { get; set; } = false;
        public bool MicBias { get; set; } = false;
        public bool CompanderOn { get; set; } = false;
        public int CompanderLevel { get; set; } = 50;
        public bool SpeechProcessorOn { get; set; } = false;
        public int SpeechProcessorLevel { get; set; } = 0;  // 0=NOR, 1=DX, 2=DXX
        public int TxFilterLow { get; set; } = 100;
        public int TxFilterHigh { get; set; } = 2900;
        public bool MonitorOn { get; set; } = false;
        public int MonitorLevel { get; set; } = 50;
        public int MonitorPan { get; set; } = 50;

        public static AudioChainPreset Load(string filePath);
        public void Save(string filePath);
        public void ApplyTo(FlexBase rig);
        public static AudioChainPreset CaptureFrom(FlexBase rig, string name);
    }

    [XmlRoot("AudioChainPresets")]
    public class AudioChainPresets
    {
        public List<AudioChainPreset> Presets { get; set; } = new();

        public static AudioChainPresets Load(string configDir, string operatorName);
        public void Save(string configDir, string operatorName);
        // File: {operatorName}_audioPresets.xml
    }
}
```

**Follow the FilterPresets.cs pattern exactly** for XML serialization, load/save, error handling with fallback to defaults.

**ApplyTo(FlexBase rig):** Set each property on the rig using existing FlexBase setters:
```csharp
public void ApplyTo(FlexBase rig)
{
    rig.MicGain = MicGain;
    rig.MicBoost = MicBoost ? FlexBase.OffOnValues.on : FlexBase.OffOnValues.off;
    rig.MicBias = MicBias ? FlexBase.OffOnValues.on : FlexBase.OffOnValues.off;
    rig.Compander = CompanderOn ? FlexBase.OffOnValues.on : FlexBase.OffOnValues.off;
    rig.CompanderLevel = CompanderLevel;
    rig.ProcessorOn = SpeechProcessorOn ? FlexBase.OffOnValues.on : FlexBase.OffOnValues.off;
    rig.ProcessorSetting = (FlexBase.ProcessorSettings)SpeechProcessorLevel;
    rig.TXFilterLow = TxFilterLow;
    rig.TXFilterHigh = TxFilterHigh;
    rig.Monitor = MonitorOn ? FlexBase.OffOnValues.on : FlexBase.OffOnValues.off;
    rig.SBMonitorLevel = MonitorLevel;
    rig.SBMonitorPan = MonitorPan;
}
```

**CaptureFrom(FlexBase rig):** Read each property from the rig and create a preset.

### 2. `JJFlexWpf/Dialogs/AudioWorkshopDialog.xaml` + `.cs`

A standalone, non-modal WPF dialog with 3 tabs. Title: "Audio Workshop".

**XAML structure:**
```xaml
<Window Title="Audio Workshop"
        AutomationProperties.Name="Audio Workshop"
        Width="500" Height="600"
        WindowStartupLocation="CenterOwner">
    <DockPanel>
        <!-- Toolbar at top -->
        <ToolBar DockPanel.Dock="Top">
            <Button Content="Load Preset" AutomationProperties.Name="Load audio preset" />
            <Button Content="Save Preset" AutomationProperties.Name="Save audio preset" />
            <Button Content="Export" AutomationProperties.Name="Export preset to file" />
            <Button Content="Reset" AutomationProperties.Name="Reset to defaults" />
        </ToolBar>

        <TabControl x:Name="MainTabs">
            <!-- Tab 1: TX Audio -->
            <TabItem Header="TX Audio" AutomationProperties.Name="TX Audio Settings">
                <ScrollViewer>
                    <StackPanel x:Name="TxAudioContent" Margin="8" />
                </ScrollViewer>
            </TabItem>

            <!-- Tab 2: Live Meters -->
            <TabItem Header="Live Meters" AutomationProperties.Name="Live Meter Readings">
                <ScrollViewer>
                    <StackPanel x:Name="LiveMetersContent" Margin="8" />
                </ScrollViewer>
            </TabItem>

            <!-- Tab 3: Earcon Explorer -->
            <TabItem Header="Earcon Explorer" AutomationProperties.Name="Earcon Sound Explorer">
                <ScrollViewer>
                    <StackPanel x:Name="EarconExplorerContent" Margin="8" />
                </ScrollViewer>
            </TabItem>
        </TabControl>
    </DockPanel>
</Window>
```

**Tab 1: TX Audio** — Build controls dynamically in code-behind (same pattern as ScreenFieldsPanel):

**Microphone section:**
- Mic Gain: slider 0-100, step 1 (`MakeValue` pattern or custom slider)
- Mic Boost: checkbox (+20 dB)
- Mic Bias: checkbox (phantom power)

**Processing section:**
- Compander: checkbox + level slider 0-100, step 5
- Speech Processor: checkbox + mode cycle (NOR/DX/DXX)

**TX Filter section:**
- Low Cut: value control, 0-9950, step 50
- High Cut: value control, 50-10000, step 50
- Width display (computed, read-only label)
- Preset buttons: Narrow / Normal / Wide / Custom

**Monitor section:**
- TX Monitor: checkbox
- Monitor Level: slider 0-100, step 5
- Monitor Pan: slider 0-100, step 5

**Real-time feedback:** Every control adjustment speaks the new value via `ScreenReaderOutput.Speak()`.

**Tab 2: Live Meters** — Real-time text readout of all available meters:
- Labels that update on a timer (~2 Hz): "S-meter: S7", "Forward Power: 50W", "SWR: 1.3", etc.
- Each meter row has a checkbox to enable/disable sonification tone for that meter
- Each meter row has a checkbox to enable/disable speech readout for that meter
- Global "Mute All Tones" button at top
- Use `LiveSetting="Polite"` on updating labels for screen reader announcements

**Tab 3: Earcon Explorer** — Categorized list of all app sounds:
- Each entry: a label + a Play button
- Categories: Meter Tones, Leader Key Tones, Alerts, PTT & Filters
- Play button calls the corresponding EarconPlayer method and speaks the label

**Opening with specific tab focus:**
```csharp
public void FocusTab(int tabIndex)
{
    MainTabs.SelectedIndex = tabIndex;
}
// For Earcon Explorer shortcut: dialog.FocusTab(2)
```

**Non-modal behavior:**
```csharp
// Don't use ShowDialog(). Use Show() and track the instance:
private static AudioWorkshopDialog _instance;
public static void ShowOrFocus(int tabIndex = 0)
{
    if (_instance == null || !_instance.IsLoaded)
    {
        _instance = new AudioWorkshopDialog();
        _instance.Show();
    }
    _instance.FocusTab(tabIndex);
    _instance.Activate();
}
```

**FlexBase TX audio property reference (exact signatures):**
```
MicGain: int get/set, 0-100, step 1
MicBoost: OffOnValues get/set (converts to bool internally)
MicBias: OffOnValues get/set
Compander: OffOnValues get/set
CompanderLevel: int get/set, 0-100, step 5
ProcessorOn: OffOnValues get/set
ProcessorSetting: ProcessorSettings enum (NOR=0, DX=1, DXX=2)
Monitor: OffOnValues get/set
SBMonitorLevel: int get/set, 0-100, step 5
SBMonitorPan: int get/set, 0-100, step 5
TXFilterLow: int get/set, 0-9950, step 50
TXFilterHigh: int get/set, 50-10000, step 50
```

All setters use the queue pattern: `q.Enqueue((FunctionDel)(() => { theRadio.Property = value; }))`.

---

## Files to Modify

### 3. `JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs` (717 lines)

**Expand the Transmission (TX) category.** Currently has only 3 controls: `_txPowerControl`, `_voxCheck`, `_tunePowerControl`.

**Add new field declarations (near lines 73-79):**
```csharp
private ValueFieldControl _micGainControl;
private CheckBox _micBoostCheck;
private CheckBox _micBiasCheck;
private CheckBox _companderCheck;
private ValueFieldControl _companderLevelControl;
private CheckBox _processorCheck;
private CycleFieldControl _processorSettingControl;
private ValueFieldControl _txFilterLowControl;
private ValueFieldControl _txFilterHighControl;
private CheckBox _monitorCheck;
private ValueFieldControl _monitorLevelControl;
```

**Expand `BuildTXControls()` method (currently lines ~298-311):**

Add after existing controls, following the exact patterns:

```csharp
// Mic Gain
_micGainControl = MakeValue("Mic Gain", 0, 100, 1);
_micGainControl.ValueChanged += (s, v) => { if (_rig != null && !_polling) _rig.MicGain = v; };
TxContent.Children.Add(_micGainControl);

// Mic Boost
_micBoostCheck = MakeToggle("Mic Boost (+20 dB)");
_micBoostCheck.Checked += (s, e) => ToggleRig("Mic Boost", v => { if (_rig != null) _rig.MicBoost = v; }, true);
_micBoostCheck.Unchecked += (s, e) => ToggleRig("Mic Boost", v => { if (_rig != null) _rig.MicBoost = v; }, false);
TxContent.Children.Add(_micBoostCheck);

// Mic Bias
_micBiasCheck = MakeToggle("Mic Bias (phantom power)");
_micBiasCheck.Checked += (s, e) => ToggleRig("Mic Bias", v => { if (_rig != null) _rig.MicBias = v; }, true);
_micBiasCheck.Unchecked += (s, e) => ToggleRig("Mic Bias", v => { if (_rig != null) _rig.MicBias = v; }, false);
TxContent.Children.Add(_micBiasCheck);

// Compander
_companderCheck = MakeToggle("Compander");
_companderCheck.Checked += (s, e) => ToggleRig("Compander", v => { if (_rig != null) _rig.Compander = v; }, true);
_companderCheck.Unchecked += (s, e) => ToggleRig("Compander", v => { if (_rig != null) _rig.Compander = v; }, false);
TxContent.Children.Add(_companderCheck);

// Compander Level (only visible when Compander is on)
_companderLevelControl = MakeValue("Compander Level", 0, 100, 5);
_companderLevelControl.ValueChanged += (s, v) => { if (_rig != null && !_polling) _rig.CompanderLevel = v; };
TxContent.Children.Add(_companderLevelControl);

// Speech Processor
_processorCheck = MakeToggle("Speech Processor");
_processorCheck.Checked += (s, e) => ToggleRig("Speech Processor", v => { if (_rig != null) _rig.ProcessorOn = v; }, true);
_processorCheck.Unchecked += (s, e) => ToggleRig("Speech Processor", v => { if (_rig != null) _rig.ProcessorOn = v; }, false);
TxContent.Children.Add(_processorCheck);

// Processor Setting (only visible when Processor is on)
_processorSettingControl = MakeCycle("Processor Mode", new[] { "Normal", "DX", "DX+" });
_processorSettingControl.SelectionChanged += (s, idx) =>
{
    if (_rig == null || _polling) return;
    _rig.ProcessorSetting = (FlexBase.ProcessorSettings)idx;
};
TxContent.Children.Add(_processorSettingControl);

// TX Filter Low
_txFilterLowControl = MakeValue("TX Filter Low", 0, 9950, 50);
_txFilterLowControl.ValueChanged += (s, v) => { if (_rig != null && !_polling) _rig.TXFilterLow = v; };
TxContent.Children.Add(_txFilterLowControl);

// TX Filter High
_txFilterHighControl = MakeValue("TX Filter High", 50, 10000, 50);
_txFilterHighControl.ValueChanged += (s, v) => { if (_rig != null && !_polling) _rig.TXFilterHigh = v; };
TxContent.Children.Add(_txFilterHighControl);

// TX Monitor
_monitorCheck = MakeToggle("TX Monitor");
_monitorCheck.Checked += (s, e) => ToggleRig("TX Monitor", v => { if (_rig != null) _rig.Monitor = v; }, true);
_monitorCheck.Unchecked += (s, e) => ToggleRig("TX Monitor", v => { if (_rig != null) _rig.Monitor = v; }, false);
TxContent.Children.Add(_monitorCheck);

// Monitor Level (only visible when Monitor is on)
_monitorLevelControl = MakeValue("Monitor Level", 0, 100, 5);
_monitorLevelControl.ValueChanged += (s, v) => { if (_rig != null && !_polling) _rig.SBMonitorLevel = v; };
TxContent.Children.Add(_monitorLevelControl);
```

**Expand `PollTX()` method (currently ~3 lines):**
```csharp
private void PollTX()
{
    if (_rig == null) return;
    _txPowerControl.Value = _rig.XmitPower;
    _voxCheck.IsChecked = _rig.Vox == FlexBase.OffOnValues.on;
    _tunePowerControl.Value = _rig.TunePower;

    // New fields
    _micGainControl.Value = _rig.MicGain;
    _micBoostCheck.IsChecked = _rig.MicBoost == FlexBase.OffOnValues.on;
    _micBiasCheck.IsChecked = _rig.MicBias == FlexBase.OffOnValues.on;
    _companderCheck.IsChecked = _rig.Compander == FlexBase.OffOnValues.on;
    _companderLevelControl.Value = _rig.CompanderLevel;
    _companderLevelControl.Visibility = (_rig.Compander == FlexBase.OffOnValues.on) ? Visibility.Visible : Visibility.Collapsed;
    _processorCheck.IsChecked = _rig.ProcessorOn == FlexBase.OffOnValues.on;
    _processorSettingControl.SelectedIndex = (int)_rig.ProcessorSetting;
    _processorSettingControl.Visibility = (_rig.ProcessorOn == FlexBase.OffOnValues.on) ? Visibility.Visible : Visibility.Collapsed;
    _txFilterLowControl.Value = _rig.TXFilterLow;
    _txFilterHighControl.Value = _rig.TXFilterHigh;
    _monitorCheck.IsChecked = _rig.Monitor == FlexBase.OffOnValues.on;
    _monitorLevelControl.Value = _rig.SBMonitorLevel;
    _monitorLevelControl.Visibility = (_rig.Monitor == FlexBase.OffOnValues.on) ? Visibility.Visible : Visibility.Collapsed;
}
```

**CRITICAL: The `_polling` flag.** Always check `if (_polling) return` in event handlers. The polling flag is set `true` during `PollUpdate()` to prevent rig updates from triggering circular events.

### 4. `JJFlexWpf/NativeMenuBar.cs` (1455 lines)

**Add menu items:**
- Operations menu → "Audio Workshop" (wired, opens dialog)
- Help menu → "Earcon Explorer" (wired, opens Audio Workshop focused on tab 3)

**Pattern:**
```csharp
AddWired(operationsPopup, "Audio Workshop\tCtrl+Shift+W", () =>
{
    AudioWorkshopDialog.ShowOrFocus(0);
});

AddWired(helpPopup, "Earcon Explorer\tCtrl+J, E", () =>
{
    AudioWorkshopDialog.ShowOrFocus(2);  // Tab 3 = Earcon Explorer
});
```

**BUG-023: Connect while connected confirmation.**
Find the connect menu handler. Before connecting, check if already connected and show a confirmation:
```csharp
if (Rig != null && Rig.IsConnected)
{
    var result = MessageBox.Show("You're already connected. Disconnect and connect to a different radio?",
        "Confirm", MessageBoxButton.YesNo);
    if (result != MessageBoxResult.Yes) return;
    // Proceed with disconnect + new connect
}
```

### 5. `KeyCommands.vb`

**Add new CommandValues:**
- `OpenAudioWorkshop`

**Add to KeyTable:**
- `Ctrl+Shift+W` → OpenAudioWorkshop

**Handler:**
```vb
Private Sub OpenAudioWorkshopHandler()
    ' Invoke on UI thread
    WpfMainWindow.Dispatcher.Invoke(Sub() AudioWorkshopDialog.ShowOrFocus(0))
End Sub
```

### 6. `ApplicationEvents.vb`

**Add to startup:**
- Load AudioChainPresets for current operator
- Wire Audio Workshop dialog to rig reference

---

## Phase Order

1. **ScreenFieldsPanel TX expansion** — all new controls + polling
2. **AudioChainPreset data class** — XML persistence, ApplyTo, CaptureFrom
3. **Audio Workshop Dialog** — layout with 3 tabs, TX Audio controls wired to rig
4. **Audio Workshop Live Meters tab** — real-time text values (read from FlexBase meter properties)
5. **Audio Workshop Earcon Explorer tab** — categorized sound list with Play buttons
6. **Audio Workshop spoken feedback** — auto-announce adjustments
7. **Preset load/save/export UI** in Audio Workshop toolbar
8. **Menu items** — Operations → Audio Workshop, Help → Earcon Explorer
9. **Hotkey** — Ctrl+Shift+W for Audio Workshop
10. **BUG-023** — Connect while connected confirmation guard

Build and commit after each phase or logical group.

---

## Accessibility Notes

- All controls need `AutomationProperties.Name`
- Non-modal dialog: use `Show()` not `ShowDialog()`
- Tab headers announced by screen reader on focus
- Live Meters: use `AutomationProperties.LiveSetting="Polite"` on updating labels
- Earcon Explorer: Play button speaks the sound label before playing
- Preset operations: speak confirmation ("Preset 'Ragchew' loaded")
- TX Filter width: auto-announce computed width when either edge changes
