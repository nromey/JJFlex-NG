# Sprint 21 — Track A: Meter Sonification Engine + Peak Watcher + Earcon Infrastructure

**Branch:** `sprint21/track-a`
**Worktree:** `C:\dev\JJFlex-NG` (main repo)

---

## Overview

Build a real-time meter sonification engine that converts radio meter data into continuous audio tones. Blind operators will hear S-meter, ALC, mic level, and SWR as pitched tones — something sighted operators can't even do (they look at meters one at a time). Also add a peak watcher for TX safety, earcon sound card selection, and master earcon volume.

**Read the full sprint plan first:** `docs/planning/agile/sprint21-resonant-signal-sculpt.md`

---

## Build & Test

```batch
# Build (from this directory)
dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64

# Verify timestamp
powershell -Command "(Get-Item 'bin\x64\Debug\net8.0-windows\win-x64\JJFlexRadio.exe').LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')"
```

**IMPORTANT:** Close any running JJFlexRadio before building (Radios.dll lock).

---

## Commit Strategy

Commit after each phase. Message format: `Sprint 21 Track A: <description>`

---

## New Files to Create

### 1. `JJFlexWpf/ContinuousToneSampleProvider.cs`

A persistent ISampleProvider that generates a phase-continuous sine wave with dynamically updating frequency and volume. This is the audio backbone for meter tones.

**Key requirements:**
- Implements `NAudio.Wave.ISampleProvider`
- WaveFormat: 44100 Hz, 1 channel (mono — panning handled by PanningSampleProvider wrapper)
- `public volatile float Frequency` — updated by meter callbacks (thread-safe)
- `public volatile float Volume` — 0.0 to 1.0
- `public volatile bool Active` — master gate
- Phase-continuous across buffer boundaries (track `double _phase`)
- **Smooth frequency transitions:** Linear interpolation over each buffer to avoid clicks. Don't jump from 200 Hz to 800 Hz instantly — ramp over the buffer duration.
- **Fade in/out on Active toggle:** 10ms ramp (441 samples at 44100 Hz) to avoid pops. Track fade state.
- `Read()` generates sine wave: `sample = (float)(Math.Sin(2 * Math.PI * _phase) * currentVolume)`
- Advance phase: `_phase += currentFreq / SampleRate`, wrap at 1.0

**Pattern reference:** Look at `ChirpSampleProvider` in `EarconPlayer.cs` (it's an inner class) for how custom ISampleProviders integrate with the mixer.

### 2. `JJFlexWpf/MeterToneEngine.cs`

The core engine that subscribes to FlexBase meter events and drives ContinuousToneSampleProvider instances.

**Architecture:**
```csharp
public static class MeterToneEngine
{
    // Up to 4 simultaneous meter tone slots
    public static List<MeterSlot> Slots { get; }
    public static bool Enabled { get; set; }  // Global kill switch
    public static float MasterVolume { get; set; }
    public static bool SpeechEnabled { get; set; }

    public static void Initialize(/* rig reference or event source */)
    public static void Shutdown()

    // Preset configurations
    public static void ApplyPreset(string presetName)  // "RX Monitor", "TX Monitor", "Full Monitor"
    public static string CurrentPreset { get; }
    public static void CyclePreset()  // Cycle through presets

    // Speech readout
    public static string GetMeterSpeechSummary()  // "S-meter S7. Forward power 50 watts. SWR 1.3."

    // Peak Watcher sub-module
    public static bool PeakWatcherEnabled { get; set; }
}

public class MeterSlot
{
    public MeterSource Source { get; set; }
    public bool Enabled { get; set; }
    public float Volume { get; set; }
    public float Pan { get; set; }  // -1.0 left to +1.0 right
    public int PitchLow { get; set; }
    public int PitchHigh { get; set; }
    public bool ThresholdMode { get; set; }
    public ContinuousToneSampleProvider ToneProvider { get; }
}

public enum MeterSource
{
    SMeter, ALC, Mic, Power, SWR, Compression, Voltage, PATemp
}
```

**Meter-to-pitch mapping:**
- S-Meter: 200 Hz (S0) → 1200 Hz (S9+40). Auto-mute during TX.
- ALC: 300 Hz (no ALC) → 1500 Hz (ALC maxed). TX only. Volume increases with level.
- Mic Level (unity reference): 550 Hz = unity. Below: drops to 350 Hz. Above: rises to 800 Hz. TX only.
- SWR: 200 Hz (1.0) → aggressive warble above 3:1. TX/Tune only.

**Throttling:** Meter events fire frequently. Throttle tone updates to ~10 Hz (100ms intervals) to avoid audio glitching. Use a simple timestamp check.

**Subscribing to FlexBase meter data:**
FlexBase stores meter values in fields updated by event handlers:
- `_SMeter` (int) — via `sMeter_t.sMeterData()` on active slice
- `_ALC` (float) — via `hwALCData()`
- `_MicData` (float) — via `micData()`
- `_SWR` (float) — via `sWRData()`
- `_PowerDBM` (float) — via `forwardPowerData()`
- `_PATempData` (float) — via `PATempDataHandler()`
- `_VoltsData` (float) — via `VoltsDataHandler()`

**Option A (preferred):** Add public events to FlexBase that fire when meter values change, so MeterToneEngine can subscribe.
**Option B:** Poll FlexBase properties on a timer (~10 Hz). Simpler but less responsive.

Choose the approach that best fits. Option A is cleaner but requires modifying FlexBase to add events. Option B is self-contained.

**Peak Watcher:**
- Monitors ALC during TX continuously
- Warning threshold: ALC > 0.5V → earcon beep + "ALC warning" speech after 3s sustained
- Critical threshold: ALC > 0.8V → rapid beep + "ALC high" speech
- Cooldown: Don't nag more than once per 10 seconds
- Toggleable independently from meter tones
- Uses `EarconPlayer.Warning1Beep()` and `EarconPlayer.Warning2Beep()` for alerts

**Speech Readout on Demand:**
- Hotkey from anywhere speaks current meter values
- Format: "S-meter S7. Forward power 50 watts. SWR 1.3."
- Works whether tones are on or off
- Reads whatever meters are currently available (RX: S-meter. TX: power, ALC, SWR, mic)

### 3. `JJFlexWpf/AudioOutputConfig.cs`

Persisted settings for NAudio output device and master earcon volume. This config will also serve future features (Say Again, Parrot, recording playback).

```csharp
[XmlRoot("AudioOutputConfig")]
public class AudioOutputConfig
{
    public int EarconDeviceNumber { get; set; } = -1;  // -1 = Windows default
    public int MasterEarconVolume { get; set; } = 80;  // 0-100

    // Meter tone settings (could be a sub-object)
    public bool MeterTonesEnabled { get; set; } = false;
    public string MeterPreset { get; set; } = "RX Monitor";
    public float MeterMasterVolume { get; set; } = 0.5f;
    public bool PeakWatcherEnabled { get; set; } = true;
    public bool MeterSpeechEnabled { get; set; } = true;

    // Per-slot settings (serialize as list)
    public List<MeterSlotConfig> MeterSlots { get; set; }

    public static AudioOutputConfig Load(string configDir);
    public void Save(string configDir);
}
```

Persistence pattern: Follow `FilterPresets.cs` — XML serialization, load with fallback to defaults, save to config directory.

---

## Files to Modify

### 4. `JJFlexWpf/EarconPlayer.cs` (678 lines)

**Changes needed:**
- Add method to register a ContinuousToneSampleProvider with the mixer (panned)
- Add method to remove/mute a continuous tone
- Add device selection support: `SetOutputDevice(int deviceNumber)` — recreate WaveOutEvent with specific device
- Add master volume: Apply gain multiplier to `_mixer` output (wrap mixer in a `VolumeSampleProvider`)
- Add `GetOutputDevices()` method to enumerate available audio devices

**Existing architecture you're building on:**
- `_waveOut` (WaveOutEvent) — persistent, always running
- `_mixer` (MixingSampleProvider) — stereo, 44100 Hz
- `AddToMixer(ISampleProvider)` — adds mono source
- `AddToMixerPanned(ISampleProvider, float pan)` — adds with stereo panning
- All public methods check `if (_mixer == null)` and have try/catch with `FallbackBeep()`
- `Initialize()` creates the mixer and waveOut
- `Dispose()` tears them down

**For continuous tones:** The ContinuousToneSampleProvider is added to the mixer once and stays there permanently. It generates silence when `Active = false` (volume faded to 0). The MeterToneEngine controls when it's active and what frequency/volume it produces.

### 5. `Radios/FlexBase.cs` (7115 lines)

**Changes needed:**
- Store Reflected Power: Add `_ReflectedPower` field, wire `ReflectedPowerDataReady` handler
- Store CompPeak: Add `_CompPeakData` field, wire `CompPeakDataReady` handler (currently a no-op)
- Store PAEfficiency: Add `_PAEffData` field, wire `PAEffDataReady` handler
- Add public meter value properties (if not already public) for MeterToneEngine to read
- **Option A:** Add meter change events (e.g., `public event Action<float> SMeterChanged`)
- Add TX state check: Expose `Transmit` property (already exists as bool get/set via `_Transmit`)

**Existing meter handler pattern (follow exactly):**
```csharp
private void forwardPowerData(float data)
{
    Tracing.TraceLine("forwardPower:" + data.ToString(), TraceLevel.Verbose);
    if (_PowerDBM != data)
        _PowerDBM = data;
}
```

**Meter event subscription (lines ~234-241):**
```csharp
theRadio.ForwardPowerDataReady += forwardPowerData;
theRadio.SWRDataReady += sWRData;
theRadio.MicDataReady += micData;
// etc.
```

### 6. `JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs` (717 lines)

**Add to DSP category (or create new Meters sub-section in DSP):**
- Meter tone toggle (on/off)
- Meter preset cycle (RX Monitor / TX Monitor / Full Monitor)

**Pattern to follow (from existing DSP controls):**
```csharp
// In field declarations (top of class)
private CheckBox _meterToneCheck;

// In BuildDSPControls()
_meterToneCheck = MakeToggle("Meter Tones");
_meterToneCheck.Checked += (s, e) => { if (!_polling) MeterToneEngine.Enabled = true; ScreenReaderOutput.Speak("Meter tones on"); };
_meterToneCheck.Unchecked += (s, e) => { if (!_polling) MeterToneEngine.Enabled = false; ScreenReaderOutput.Speak("Meter tones off"); };
DspContent.Children.Add(_meterToneCheck);

// In PollDSP()
_meterToneCheck.IsChecked = MeterToneEngine.Enabled;
```

### 7. `JJFlexWpf/NativeMenuBar.cs` (1455 lines)

**Add meter tone items to the Operations or DSP submenu:**
- "Toggle Meter Tones" (checkable via `AddChecked`)
- "Cycle Meter Preset" (wired via `AddWired`)
- "Speak Meters" (wired via `AddWired`)

**Pattern:**
```csharp
AddChecked(dspPopup, "Meter Tones",
    () => { MeterToneEngine.Enabled = !MeterToneEngine.Enabled; SpeakAfterMenuClose($"Meter tones {(MeterToneEngine.Enabled ? "on" : "off")}"); },
    () => MeterToneEngine.Enabled);
```

**BUG-016 (feature gating):** While adding menu items, also add license/capability checks to existing DSP toggles:
```csharp
// Before adding RNN toggle:
if (Rig?.NeuralNRAvailable == true) { /* add menu item */ }
```
Check what gating properties exist in FlexBase. Look for `NeuralNoiseReductionLicensed`, `NoiseReductionLicensed`, etc.

### 8. `KeyCommands.vb`

**Add new CommandValues:**
- `ToggleMeterTones`
- `CycleMeterPreset`
- `SpeakMeters`

**Add to KeyTable with hotkeys:**
- `Ctrl+Shift+M` → ToggleMeterTones
- `Alt+Shift+M` → CycleMeterPreset
- Leader key bindings (Ctrl+J → M and Ctrl+J → S) will be added by Track C

**Pattern (follow existing entries):**
```vb
lookup(CommandValues.ToggleMeterTones, Keys.M Or Keys.Control Or Keys.Shift, KeyTypes.Command,
    AddressOf ToggleMeterTonesHandler, "Toggle meter sonification tones",
    FunctionGroups.audio, KeyScope.Radio)
```

### 9. `JJFlexWpf/Dialogs/SettingsDialog.xaml.cs` (202 lines)

**Add a "Meter Tones" tab** with:
- Enable meter tones checkbox
- Preset selector (dropdown: RX Monitor, TX Monitor, Full Monitor)
- Master meter volume slider (0-100)
- Peak watcher enable checkbox
- Peak watcher thresholds (ALC warning/critical)
- Speech readout enable checkbox

**Also add to the existing "Audio" tab** (currently placeholder):
- Earcon sound card dropdown (enumerate NAudio devices)
- Master earcon volume slider (0-100)

**Tab pattern (follow PTT tab):**
```xaml
<TabItem Header="Meter Tones" AutomationProperties.Name="Meter Tones Settings">
    <StackPanel Margin="8">
        <CheckBox x:Name="MeterTonesEnabledCheck" Content="Enable meter tones" />
        <!-- etc. -->
    </StackPanel>
</TabItem>
```

### 10. `ApplicationEvents.vb`

**Add to startup sequence (after `EarconPlayer.Initialize()`):**
- Load `AudioOutputConfig` from config directory
- Apply earcon device/volume settings to `EarconPlayer`
- Initialize `MeterToneEngine` (but don't start tones until radio connects)

**Add to radio connect callback:**
- Wire MeterToneEngine to the connected radio's meter data

---

## Phase Order

1. **ContinuousToneSampleProvider** — the audio primitive
2. **EarconPlayer integration** — register continuous tones with mixer, device selection, master volume
3. **MeterToneEngine core** — S-meter tone with pitch mapping
4. **FlexBase meter exposure** — add events or properties for missing meters
5. **ALC + Mic meter tones** (with mic unity reference)
6. **SWR tone** for antenna tuning
7. **Peak Watcher** — threshold alerts
8. **Global kill switch + per-meter toggles**
9. **Speech readout on demand**
10. **AudioOutputConfig persistence**
11. **SettingsDialog tabs** (Meter Tones + Audio device/volume)
12. **Menu items + hotkeys**
13. **ScreenFieldsPanel integration**
14. **BUG-016: Feature gating** on DSP menu toggles

Build and commit after each phase or logical group.

---

## Accessibility Notes

- All Settings dialog controls need `AutomationProperties.Name`
- Speak toggle state changes: "Meter tones on" / "Meter tones off"
- Speak preset changes: "Meter preset: TX Monitor"
- Speak meter readout format: "S-meter S7. Forward power 50 watts. SWR 1.3."
- Peak watcher alerts use both earcon AND speech (earcon first for speed, speech follows)
