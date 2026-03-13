# Sprint 21: Resonant Signal Sculpt

**Theme:** TX audio mastery, meter sonification, and the beginning of JJ Flexible Radio Access branding.

**Sprint Goal:** Give blind operators the same real-time TX audio feedback that sighted operators get from meters, plus keyboard-driven TX sculpting, a leader key system for efficient DSP control, F1 context-sensitive CHM help, and rebrand the app for its multi-radio future.

---

## Scope

### Feature Work

1. **Meter Sonification Engine + Peak Watcher** — Real-time audio tones that track S-meter, ALC, mic, SWR, and other meters. Multiple simultaneous tones with stereo panning. Per-meter on/off toggles. Mic unity reference tone (mid-pitch anchor — hear whether you're above or below unity). SWR tone for manual antenna tuning. Peak watcher alerts via earcons and speech. Global kill switch to silence all tones instantly. Speech readout on demand via hotkey from anywhere in the app.

2. **Audio Workshop Dialog** — A dedicated WPF dialog (titled "Audio Workshop") for sculpting transmit audio. Tabs: TX Audio (mic gain, compressor, compander, speech processor, TX monitor, TX filter width), Live Meters (real-time text values with per-meter tone toggles), and Earcon Explorer (audition all app sounds with labels). Real-time spoken feedback as you adjust. Save/load audio chain presets as shareable XML.

3. **Earcon Explorer** — A tab inside Audio Workshop AND accessible from Help menu + leader key shortcut (Ctrl+J, E). Opens Audio Workshop focused on the Earcon Explorer tab. Lets hams audition every sound the app makes: meter tones (unity, under, over, clipping), leader key tones (enter, on, off, invalid, cancel), peak watcher alerts, PTT chirps, filter edge tones. Each sound has a Play button and a spoken label.

4. **ScreenFields Transmission Expansion** — The Transmission category currently has only TX Power, VOX, and Tune Power. Expand with mic gain, compander, speech processor, TX filter low/high, TX monitor, mic boost/bias toggles.

5. **TX Bandwidth Sculpting** — Keyboard shortcuts to nudge TX filter edges, mirroring the RX filter bracket-key workflow.

6. **Leader Key System (Ctrl+J)** — Two-step keyboard dispatch: press Ctrl+J, then a second key to execute a command. No timeout — cancel with Escape (matches JAWS layered keystroke behavior). Full earcon vocabulary for every interaction (enter, on, off, invalid, cancel, help). Initial bindings: DSP filter toggles, meter tone toggles, audio shortcuts, earcon explorer.

7. **F1 Context-Sensitive CHM Help** — F1 opens compiled help (CHM) to the topic relevant to your current focus. ScreenFields DSP? Opens DSP help. Audio Workshop? Opens audio help. Generic context? Opens welcome page. **Note:** Command Finder (Ctrl+/) already exists and is fully functional — Sprint 21 just extends it to include leader key commands once they're built.

8. **Extend Command Finder with Leader Key Commands** — The existing Command Finder (`CommandFinderDialog.xaml.cs`, bound to Ctrl+/) already supports searchable commands with scope filtering and execute-on-Enter. Sprint 21 adds leader key commands to its registry so they appear in search results alongside regular hotkeys.

9. **App Rename: JJ Flexible Radio Access** — Display-name rebrand. All user-facing text changes. Filenames and internal identifiers stay short (JJFlex/JJFlexRadio). No AppData migration needed.

10. **Compiled Help File** — CHM help using Markdown source + hhc.exe (HTML Help Workshop). No HelpNDoc — GUI is inaccessible with screen readers. F1 context-sensitive wiring. Keyboard reference, getting started guide, feature documentation.

### Bug Fixes (sprinkled across tracks)

- **BUG-004**: System.IO.Ports v9.0.0 crash on shutdown — guard FlexKnob.Dispose()
- **BUG-016**: RNN/DSP toggles lack feature gating in menus — add license/capability checks
- **BUG-023**: "Connect while connected" needs confirmation dialog

---

## Research Summary

### Meter Data Flow (FlexLib -> App)

```
Flex Radio hardware
  -> VITA-49 UDP meter packets (VitaMeterPacket.cs)
  -> Radio.UpdateMeters() dispatches to individual Meter objects
  -> Meter.DataReady event fires with float data
  -> Radio-level events: ForwardPowerDataReady, MicDataReady, HWAlcDataReady, etc.
  -> FlexBase handlers store values: _SMeter, _PowerDBM, _MicData, _ALC, etc.
  -> UI can read properties or subscribe to events
```

**Available meters and current FlexBase integration:**

| Meter | FlexLib Event | FlexBase Property | Units | Notes |
|-------|--------------|-------------------|-------|-------|
| S-meter (LEVEL) | `Slice.SMeterDataReady` | `SMeter` (int) | S-units 0-9+dB | Per-slice, only fires for active slice |
| Forward Power | `Radio.ForwardPowerDataReady` | `_PowerDBM` (float) | dBm | TX only |
| Reflected Power | `Radio.ReflectedPowerDataReady` | -- | dBm | Not stored yet |
| SWR | `Radio.SWRDataReady` | `SWR` (float) | VSWR | TX only |
| Mic Level | `Radio.MicDataReady` | `MicData` (float) | Level | Continuous during TX |
| Mic Peak | `Radio.MicPeakDataReady` | `_MicPeakData` (float) | Peak | TX only |
| Compression Peak | `Radio.CompPeakDataReady` | -- | dB reduction | Handler is no-op |
| Hardware ALC | `Radio.HWAlcDataReady` | `ALC` (float) | Volts | TX only |
| PA Temperature | `Radio.PATempDataReady` | `PATemp` (float) | deg C | Always available |
| Supply Voltage | `Radio.VoltsDataReady` | `Volts` (float) | Volts | Always available |
| PA Efficiency | `Radio.PAEffDataReady` | -- | dBm | Not stored yet |

**Key insight:** S-meter data arrives via `Slice.SMeterDataReady` event on the active slice. FlexBase already wires this up via the `sMeter_t` inner class. All other meters arrive via `Radio.*DataReady` events and are already stored in FlexBase fields.

### TX Audio Properties (FlexBase Wrappers)

| Property | Type | Range | Increment | Notes |
|----------|------|-------|-----------|-------|
| `MicGain` | int | 0-100 | 1 | Maps to Radio.MicLevel |
| `MicBoost` | OffOnValues | on/off | -- | +20 dB boost |
| `MicBias` | OffOnValues | on/off | -- | Phantom power for condenser mics |
| `Compander` | OffOnValues | on/off | -- | Dynamic range processor |
| `CompanderLevel` | int | 0-100 | 5 | Compression intensity |
| `ProcessorOn` | OffOnValues | on/off | -- | Speech processor |
| `ProcessorSetting` | enum | NOR/DX/DXX | -- | Processor aggressiveness |
| `Monitor` | OffOnValues | on/off | -- | TX self-monitor |
| `SBMonitorLevel` | int | 0-100 | 5 | Monitor volume |
| `SBMonitorPan` | int | 0-100 | 5 | Monitor L/R pan |
| `TXFilterLow` | int | 0-9950 | 50 | TX low-cut frequency (Hz) |
| `TXFilterHigh` | int | 50-10000 | 50 | TX high-cut frequency (Hz) |
| `XmitPower` | int | 0-100 | 1 | RF output power |
| `TunePower` | int | 0-100 | 1 | Tune mode power |

### NAudio Tone Infrastructure (EarconPlayer.cs)

The existing EarconPlayer provides:
- Persistent `WaveOutEvent` + `MixingSampleProvider` (44100 Hz stereo) -- always running
- `PlayTone(freq, duration, volume)` -- single sine wave
- `PlayTonePanned(freq, duration, volume, pan)` -- stereo placement
- `PlayChirp(startHz, endHz, duration, volume)` -- frequency sweep
- `ChirpSampleProvider` -- custom ISampleProvider for linear frequency sweeps
- `AddToMixer()` / `AddToMixerPanned()` -- inject any ISampleProvider into the mix
- Embedded .wav support with `CachedSound` + `CachedSoundSampleProvider`

**What's missing for meter tones:** A *continuous* tone generator. Current infrastructure plays one-shot tones (fixed duration). Meter tones need a persistent, dynamically-updating pitch/volume. We'll need a new `ContinuousToneSampleProvider` that reads from a shared frequency/volume variable updated by meter events.

### Hotkey Dispatch (KeyCommands.vb)

- `DoCommand(Keys)` -- single-key lookup in `KeyDictionary`, invokes handler
- `KeyScope` enum: Global, Radio, Classic, Modern, Logging
- `KeyDefType` -- stores key binding + scope + command ID
- **No multi-step support** -- all commands are single keypress

### Filter Preset Infrastructure (FilterPresets.cs)

- XML serialized per operator: `{operatorName}_filterPresets.xml`
- `FilterPreset` -- Name, Low, High, Width (computed)
- `ModePresets` -- groups presets by mode (SSB, CW, DIGI, AM, FM)
- Built-in defaults per mode (Narrow, Normal, Wide, etc.)
- Pattern is directly reusable for TX filter presets and audio chain presets

### App Rename Scope

**Display-name only. Filenames stay short.**

| What Changes (display/brand) | What Stays (files/internal) |
|---|---|
| Window title -> "JJ Flexible Radio Access" | Assembly name: `JJFlexRadio` |
| Installer welcome text -> "JJ Flexible Radio Access" | Exe file: `JJFlexRadio.exe` |
| Installer filename -> `Setup JJFlex_4.x.x_x64.exe` | AppData folder: `JJFlexRadio` |
| Welcome speech -> "Welcome to JJ Flexible Radio Access" | Registry keys: `JJFlexRadio` |
| About dialog -> "JJ Flexible Radio Access" | Internal class/namespace names |
| NSIS display name -> "JJ Flexible Radio Access" | Solution/project file names |
| Changelog header, README title | .sln, .vbproj file names |
| Screen reader automation names | |
| QRZ User-Agent -> "JJFlexibleRadioAccess" | |
| Help file title | |

---

## Track Decomposition

### Track A: Meter Sonification Engine + Peak Watcher + Earcon Infrastructure
**Worktree:** Main repo (`C:\dev\JJFlex-NG`)
**Branch:** `sprint21/track-a`

**New files:**
- `JJFlexWpf/MeterToneEngine.cs` -- Core engine: subscribes to meter events, drives continuous tones
- `JJFlexWpf/AudioOutputConfig.cs` -- Persisted settings for NAudio device selection, master earcon volume (also serves future Say Again, Parrot, recording playback)
- `JJFlexWpf/ContinuousToneSampleProvider.cs` -- Persistent dynamically-updating tone for meter sonification

**Modified files:**
- `JJFlexWpf/EarconPlayer.cs` -- Add ContinuousToneSampleProvider integration, device selection, master volume control
- `Radios/FlexBase.cs` -- Expose meter events or callbacks for MeterToneEngine to subscribe to; store Reflected Power and CompPeak
- `JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs` -- Add meter tone toggle to DSP category (or new Meters category)
- `JJFlexWpf/NativeMenuBar.cs` -- Add meter tone toggle to menus
- `KeyCommands.vb` -- Add hotkeys for meter tone toggle, cycle meter source, speak meter values
- `JJFlexWpf/Dialogs/SettingsDialog.xaml.cs` -- Add Meter Tones tab to Settings
- `ApplicationEvents.vb` -- Load/wire AudioOutputConfig + MeterToneConfig at startup

**Design: Meter Tone Engine**

The engine supports **multiple simultaneous meter tones**, each independently panned in the stereo field.

```
MeterToneEngine (static or singleton)
+-- Initialize(FlexBase rig) -- subscribe to meter events
+-- MeterSlots: List<MeterSlot> -- up to 4 simultaneous tones
|   Each MeterSlot:
|   +-- Source: MeterSource enum (SMeter, ALC, Mic, Power, SWR, Compression, Voltage, PATemp)
|   +-- Enabled: bool
|   +-- Volume: float (0.0 - 1.0)
|   +-- Pan: float (-1.0 left to +1.0 right)
|   +-- PitchLow: int (Hz) -- pitch at meter minimum
|   +-- PitchHigh: int (Hz) -- pitch at meter maximum
|   +-- ToneProvider: ContinuousToneSampleProvider -- the audio source
|   +-- ThresholdMode: bool -- if true, silent below threshold, tone above
|
+-- Enabled: bool -- GLOBAL KILL SWITCH (mutes all slots instantly)
+-- MasterVolume: float (0.0 - 1.0) -- scales all slot volumes
+-- SpeechEnabled: bool -- toggle speech readouts independently from tones
|
+-- Default configurations:
|   "RX Monitor": S-meter centered, 200-1200 Hz
|   "TX Monitor": ALC left + Mic right, 300-1500 Hz / 200-1000 Hz
|   "Full Monitor": S-meter left + ALC right, both with defaults
|
+-- S-Meter tone:
|   Pitch: 200 Hz (S0/noise floor) -> 1200 Hz (S9+40)
|   Update rate: ~10 Hz (throttle meter events to avoid audio glitching)
|   Auto-mutes when TX (S-meter meaningless during transmit)
|
+-- ALC tone:
|   Pitch: 300 Hz (no ALC) -> 1500 Hz (ALC maxed)
|   Volume: increases with ALC level (urgency mapping)
|   Only active during TX
|   Threshold mode option: silent below threshold, tone above
|
+-- Mic Level tone (UNITY REFERENCE):
|   Mid-pitch (~550 Hz) = unity gain
|   Below unity: pitch drops proportionally (450 Hz = a bit under, 350 Hz = way under)
|   Above unity: pitch rises proportionally (650 Hz = a bit hot, 800 Hz = clipping)
|   Lets operator hear mic positioning/gain relative to the sweet spot
|   Only active during TX
|
+-- SWR tone (for manual antenna tuning):
|   Low SWR (1.0-1.5) = low calm tone (~200-300 Hz)
|   Rising SWR = rising pitch with increasing urgency
|   Above 2:1 = aggressive tone
|   Above 3:1 = alarming rapid warble (stop before you cook something)
|   Only active during TX/Tune
|
+-- Other meters (Power, Voltage, PA Temp):
|   Available as additional slots for advanced users
|
+-- Peak Watcher (sub-module):
    Monitors ALC/Mic continuously during TX
    Thresholds: Warning (ALC > 0.5V), Critical (ALC > 0.8V)
    Alerts: earcon beep at warning, rapid beep at critical
    Speech: "ALC high" after 3 seconds sustained
    Cooldown: don't nag more than once per 10 seconds
    Toggleable independently from meter tones
    Integrates with existing PTT health monitor code

+-- Speech Readout on Demand:
    Hotkey (from anywhere in app) speaks current meter values
    "S-meter S7. Forward power 50 watts. SWR 1.3."
    Works whether tones are on or off
```

**ContinuousToneSampleProvider design:**
```csharp
class ContinuousToneSampleProvider : ISampleProvider
{
    // Shared volatile fields updated by meter events
    public volatile float Frequency;  // Hz -- updated by meter callback
    public volatile float Volume;     // 0.0-1.0
    public volatile bool Active;      // master gate

    // Phase-continuous sine wave generation
    // Smooth frequency transitions (slew rate limiter to avoid clicks)
    // Fade in/out on Active toggle (10ms ramp to avoid pops)

    int ISampleProvider.Read(float[] buffer, int offset, int count)
    {
        // Generate sine wave at current Frequency
        // Apply volume envelope
        // Phase-continuous across buffer boundaries
        // Smooth frequency changes with linear interpolation
    }
}
```

**Phases:**
1. ContinuousToneSampleProvider + integration with EarconPlayer mixer
2. MeterToneEngine core -- subscribe to S-meter events, map to pitch
3. ALC and Mic meter tone modes (with mic unity reference tone)
4. SWR tone for manual antenna tuning
5. Peak Watcher -- threshold alerts with earcon beeps + speech
6. Global kill switch + per-meter toggles
7. Speech readout on demand (hotkey to speak current meter values from anywhere)
8. MeterToneConfig persistence + Settings UI tab
9. Menu items and hotkeys (toggle, cycle source, adjust volume)
10. ScreenFieldsPanel integration (toggle + source selector in DSP category)
11. Earcon sound card selector -- enumerate NAudio output devices, add dropdown to Settings -> Audio tab
12. Master earcon volume -- gain multiplier on EarconPlayer's MixingSampleProvider, slider in Settings -> Audio tab
13. AudioOutputConfig.cs persistence (serves earcons, meters, and future Say Again/Parrot/recording)
14. BUG-016: Add feature gating to DSP menu toggles (natural fit -- we're already in NativeMenuBar)

**Hotkeys:**
- `Ctrl+Shift+M` -- Toggle meter tones on/off (global kill switch)
- `Alt+Shift+M` -- Cycle active meter configuration ("RX Monitor" -> "TX Monitor" -> "Full Monitor" -> custom)
- Leader key: Ctrl+J -> M -- same as Ctrl+Shift+M toggle
- Leader key: Ctrl+J -> S -- Speak current meter values
- Speak current meter reading when tone is off (already exists for S-meter)

**Earcon Sound Card & Volume (in Settings Dialog -> Audio tab):**

Currently, rig audio device selection uses JJPortaudio and is accessed via "Radio Audio Device" in the menu. EarconPlayer (NAudio) has NO device selection -- it defaults to Windows default output. We need to add:

- **Earcon/Meter Sound Card selector** -- dropdown of available audio output devices. Uses NAudio's `WaveOut.DeviceCount` / `WaveOut.GetCapabilities()` to enumerate. Separate from the rig audio device. Persisted in AudioOutputConfig.
- **Master Earcon Volume** -- 0-100 slider that scales ALL EarconPlayer output. Stored as a gain multiplier on the MixingSampleProvider.
- Label as "Earcon and Meter Sound Card" in the UI.

---

### Track B: Audio Workshop + ScreenFields TX Expansion + Earcon Explorer
**Worktree:** `../jjflex-21b`
**Branch:** `sprint21/track-b`

**New files:**
- `JJFlexWpf/Dialogs/AudioWorkshopDialog.xaml` + `.cs` -- TX audio sculpting dialog with tabs
- `Radios/AudioChainPreset.cs` -- Save/load/share audio chain configurations

**Modified files:**
- `JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs` -- Expand Transmission category
- `JJFlexWpf/Controls/ScreenFieldsPanel.xaml` -- XAML for new Transmission controls
- `JJFlexWpf/NativeMenuBar.cs` -- Add "Audio Workshop" menu item
- `KeyCommands.vb` -- Add hotkey for Audio Workshop dialog
- `ApplicationEvents.vb` -- Load AudioChainPreset at startup

**Audio Workshop Dialog design (titled "Audio Workshop"):**

A standalone WPF dialog (not modal -- can stay open while operating) with three tabs:

```
Audio Workshop Dialog
+-- Tab 1: TX Audio
|   +-- Section: Microphone
|   |   +-- Mic Gain slider (0-100) with real-time level indicator (spoken)
|   |   +-- Mic Boost toggle (+20 dB)
|   |   +-- Mic Bias toggle (phantom power)
|   |
|   +-- Section: Processing
|   |   +-- Compander toggle + level (0-100)
|   |   +-- Speech Processor toggle + mode (NOR/DX/DXX)
|   |
|   +-- Section: TX Filter
|   |   +-- Low Cut (Hz) -- with live width readout
|   |   +-- High Cut (Hz)
|   |   +-- Width display (computed)
|   |   +-- TX Filter preset buttons (Narrow/Normal/Wide/Custom)
|   |
|   +-- Section: Monitor
|   |   +-- TX Monitor toggle
|   |   +-- Monitor Level (0-100)
|   |   +-- Monitor Pan (0-100)
|   |
|   +-- Toolbar:
|       +-- Load Preset / Save Preset / Export Preset / Reset to Defaults
|
+-- Tab 2: Live Meters
|   +-- Real-time text readout of ALL available meters, updating live:
|   |   S-meter, Forward Power, Reflected Power, SWR, Mic Level,
|   |   Mic Peak, Compression Peak, ALC, PA Temp, Voltage, PA Efficiency
|   +-- Per-meter toggle to enable/disable sonification tone
|   +-- Per-meter toggle to enable/disable speech readout
|   +-- Global kill switch for all tones
|   +-- Current meter values spoken on focus (screen reader friendly)
|
+-- Tab 3: Earcon Explorer
    +-- Categorized list of all app sounds with Play buttons:
    |   Meter Tones:
    |     "S-meter at S5" / "S-meter at S9" / "S-meter at S9+20"
    |     "Mic at unity" / "Mic a bit under" / "Mic a bit hot" / "Mic clipping"
    |     "SWR 1.2 (good)" / "SWR 2.0 (watch it)" / "SWR 3.0+ (danger)"
    |     "ALC clean" / "ALC warning" / "ALC critical"
    |   Leader Key Tones:
    |     "Enter leader mode" / "Feature on" / "Feature off"
    |     "Invalid key" / "Cancel" / "Help"
    |   Alerts:
    |     "Peak watcher warning" / "Peak watcher critical"
    |   PTT & Filters:
    |     "PTT chirp" / "Filter edge move" / "Tune tone"
    +-- Each button: plays the sound + speaks the label
    +-- Accessible from: Audio Workshop tab, Help menu, Ctrl+J -> E

Real-time feedback:
- Every adjustment speaks the new value
- Meter section auto-reads every 2 seconds during TX (configurable)
- Peak warnings same as peak watcher
```

**ScreenFieldsPanel Transmission expansion:**

Current controls: TX Power, VOX, Tune Power

Add:
- Mic Gain (0-100, step 1)
- Mic Boost (on/off toggle)
- Mic Bias (on/off toggle)
- Compander (on/off toggle)
- Compander Level (0-100, step 5) -- shown when Compander is on
- Speech Processor (on/off toggle)
- Processor Setting (cycle: NOR -> DX -> DXX) -- shown when Processor is on
- TX Filter Low (0-9950, step 50)
- TX Filter High (50-10000, step 50)
- TX Monitor (on/off toggle)
- Monitor Level (0-100, step 5) -- shown when Monitor is on

**AudioChainPreset.cs design:**
```csharp
[XmlRoot("AudioChainPreset")]
public class AudioChainPreset
{
    public string Name { get; set; }           // "Ragchew", "Contest SSB", "DX Pileup"
    public string Description { get; set; }    // Optional user notes
    public int MicGain { get; set; }
    public bool MicBoost { get; set; }
    public bool MicBias { get; set; }
    public bool CompanderOn { get; set; }
    public int CompanderLevel { get; set; }
    public bool SpeechProcessorOn { get; set; }
    public int SpeechProcessorLevel { get; set; } // 0=NOR, 1=DX, 2=DXX
    public int TxFilterLow { get; set; }
    public int TxFilterHigh { get; set; }
    public bool MonitorOn { get; set; }
    public int MonitorLevel { get; set; }
    public int MonitorPan { get; set; }

    public static AudioChainPreset Load(string filePath);
    public void Save(string filePath);
    public void ApplyTo(FlexBase rig);
    public static AudioChainPreset CaptureFrom(FlexBase rig, string name);
}
```

**Phases:**
1. ScreenFieldsPanel Transmission expansion (all new controls + polling)
2. AudioChainPreset data class + XML persistence
3. Audio Workshop Dialog -- layout with 3 tabs, TX Audio controls, basic wiring
4. Audio Workshop Live Meters tab -- real-time text values, per-meter tone/speech toggles
5. Audio Workshop Earcon Explorer tab -- categorized sound list with Play buttons
6. Audio Workshop spoken feedback (auto-announce adjustments + periodic meter reads)
7. Preset load/save/export UI in Audio Workshop
8. Menu items: Operations -> Audio Workshop, Help -> Earcon Explorer (opens AW focused on tab 3)
9. Hotkey for Audio Workshop
10. BUG-023: Add "Connect while connected" confirmation guard (natural fit -- touching menus)

**Hotkeys:**
- `Ctrl+Shift+W` -- Open Audio Workshop dialog
- Audio Workshop internal: Tab through sections, arrows adjust, Enter for presets

**Menu entry points for Earcon Explorer:**
- Help -> Earcon Explorer -- opens Audio Workshop, focuses Tab 3
- Leader key: Ctrl+J -> E -- same as above

---

### Track C: TX Sculpting Keyboard + Leader Key System + Command Finder Extension
**Worktree:** `../jjflex-21c`
**Branch:** `sprint21/track-c`

**EXISTING INFRASTRUCTURE (do NOT rebuild):**
- `JJFlexWpf/Dialogs/CommandFinderDialog.xaml` + `.cs` -- Already built! Searchable, scope-filtered, execute-on-Enter, screen reader friendly
- `KeyCommands.vb` line 659: `Ctrl+/` (`Keys.Oem2 Or Keys.Control`) already bound to `CommandValues.ContextHelp`
- `ApplicationEvents.vb` line 124+: `GetCommandFinderItemsCallback` already populates commands from KeyTable + manual entries
- **Sprint 21 task:** Add leader key commands to the Command Finder registry so they appear in search results

**New files:**
- None (Command Finder already exists)

**Modified files:**
- `KeyCommands.vb` -- Leader key state machine + TX filter shortcuts + command registry + new command entries
- `JJFlexWpf/MainWindow.xaml.cs` -- Leader key visual/audio feedback
- `JJFlexWpf/NativeMenuBar.cs` -- Show leader key bindings in menu text, add Command Finder to Help menu
- `JJFlexWpf/FreqOutHandlers.cs` -- TX filter adjustment handlers
- `Radios/FlexBase.cs` -- TX filter nudge methods (if not already present)
- `ApplicationEvents.vb` -- Wire leader key display callback

**Leader Key Implementation:**

The leader key (Ctrl+J) is a **layered keystroke** system. Press Ctrl+J first (the "leader"), then a simple second key to pick the action. **No timeout** -- cancel explicitly with Escape. Matches JAWS layered keystroke behavior where you press the modifier, then the next key at your own pace.

**Earcon vocabulary -- critical for responsive feel:**

| Event | Earcon | Description |
|-------|--------|-------------|
| Enter leader mode (Ctrl+J) | Rising "bink" (400->600 Hz, 80ms) | "I'm listening for your next key" |
| Feature toggled ON | "bonk-bink" (300->500->800 Hz, 50ms each) | Rising = activated |
| Feature toggled OFF | "bink-bonk" (800->500->300 Hz, 50ms each) | Falling = deactivated |
| Value adjusted | Soft click (reuse filter-edge-move) | "Setting changed" |
| Invalid second key | Dull buzz (200 Hz, 100ms, square wave) | "Not a valid command" |
| Escape (explicit cancel) | Soft descending (500->300 Hz, 150ms) | "Cancelled" |
| Help requested (?) | Distinct double-chime (800+1000 Hz) | "Here comes a list" |

These earcons are added to EarconPlayer as named methods: `LeaderEnterTone()`, `FeatureOnTone()`, `FeatureOffTone()`, `LeaderInvalidTone()`, `LeaderCancelTone()`, `LeaderHelpTone()`.

**State machine:**

```vb
' In KeyCommands.vb

Private leaderKeyActive As Boolean = False
' NO TIMEOUT -- cancel with Escape only (matches JAWS behavior)

' Modified DoCommand:
Function DoCommand(ByVal k As Keys) As Boolean
    ' If leader key is active, dispatch to leader dictionary
    If leaderKeyActive Then
        leaderKeyActive = False

        If k = Keys.Escape Then
            EarconPlayer.LeaderCancelTone()
            Return True
        End If

        Return DoLeaderCommand(k)
    End If

    ' Check for leader key trigger (Ctrl+J)
    If k = (Keys.J Or Keys.Control) Then
        leaderKeyActive = True
        EarconPlayer.LeaderEnterTone()
        Radios.ScreenReaderOutput.Speak("J", interrupt:=True)
        Return True
    End If

    ' Normal single-key dispatch (existing code)
    ...
End Function
```

**Leader key bindings (Ctrl+J -> ...):**

| Second Key | Action | Speech |
|------------|--------|--------|
| N | Toggle Noise Reduction | "Noise Reduction on/off" |
| B | Toggle Noise Blanker | "Noise Blanker on/off" |
| W | Toggle Wideband NB | "Wideband NB on/off" |
| R | Toggle Neural NR (RNN) | "Neural NR on/off" |
| S | Toggle Spectral NR | "Spectral NR on/off" |
| A | Toggle Auto Notch (ANF) | "Auto Notch on/off" |
| P | Toggle Audio Peak Filter | "APF on/off" |
| M | Toggle Meter Tone | "Meter tone on/off" |
| T | Open Audio Workshop | (opens dialog) |
| E | Open Earcon Explorer | (opens Audio Workshop, focuses Earcon Explorer tab) |
| F | Speak TX Filter width | "TX filter 100 to 2900, 2.8 kHz" |
| ? or H | Speak leader key help | Lists available commands |

**TX filter keyboard sculpting:**

| Shortcut | Action | Speech |
|----------|--------|--------|
| Ctrl+Shift+[ | Nudge TX filter low edge down 50 Hz | "TX low 100" |
| Ctrl+Shift+] | Nudge TX filter low edge up 50 Hz | "TX low 150" |
| Ctrl+Alt+[ | Nudge TX filter high edge down 50 Hz | "TX high 2850" |
| Ctrl+Alt+] | Nudge TX filter high edge up 50 Hz | "TX high 2900" |
| Ctrl+Shift+F | Speak TX filter width | "TX filter 100 to 2900, 2.8 kHz" |

**Extending Command Finder with Leader Key Commands:**

The Command Finder (Ctrl+/) already exists and works. Sprint 21 extends it:
- Add leader key commands to `GetCommandFinderItemsCallback` in `ApplicationEvents.vb`
- Each leader key entry shows "Ctrl+J, N" format in the KeyDisplay column
- Existing Command Finder infrastructure handles search, filtering, and execution automatically

**Key assignments (no conflicts):**
- `Ctrl+/` -- Command Finder (ALREADY EXISTS, keep as-is)
- `F1` -- Context-sensitive CHM help (NEW -- opens compiled help to relevant topic)

**Phases:**
1. Leader key earcons in EarconPlayer
2. Leader key state machine in DoCommand() -- Ctrl+J trigger, no timeout, Escape cancel
3. Leader key dictionary + initial DSP toggle bindings (each handler plays on/off earcon + speaks result)
4. Leader key help command (Ctrl+J -> ?)
5. Add leader key commands to existing Command Finder registry
6. TX filter nudge handlers in FreqOutHandlers
7. TX filter keyboard shortcuts (Ctrl+Shift/Alt + brackets)
8. TX filter earcon feedback (reuse filter edge tones from Sprint 19)
9. Menu text updates to show leader key bindings
10. Feature gating: leader key DSP toggles respect license/capability (partial BUG-016 fix)
11. BUG-004: Guard FlexKnob.Dispose() with try/catch

---

### Track D: App Rename -- JJ Flexible Radio Access
**Worktree:** `../jjflex-21d`
**Branch:** `sprint21/track-d`

**IMPORTANT:** This track should merge LAST to minimize conflicts with feature tracks.

**Scope: Display-name rename only. Filenames stay short.**

**Files to modify:**

1. `globals.vb` -- Change `ProgramName` constant (this propagates to many places)
2. `My Project/AssemblyInfo.vb` -- Change `AssemblyProduct` and `AssemblyTitle`
3. `JJFlexRadio.vbproj` -- Change `<Product>` element
4. `JJFlexWpf/MainWindow.xaml` -- AutomationProperties.Name
5. `JJFlexWpf/MainWindow.xaml.cs` -- Welcome speech, window title
6. `JJFlexWpf/Dialogs/WelcomeDialog.xaml` -- Title, automation name, button text
7. `BridgeForm.vb` -- Me.Text
8. `ApplicationEvents.vb` -- Welcome speech for first-run
9. `install template.nsi` -- Installer display name, welcome text, shortcuts
10. `install.bat` -- Installer output filename format
11. `build-installers.bat` -- Installer output filename format
12. `JJFlexWpf/NativeMenuBar.cs` -- About dialog ProductName
13. `QrzLookup/QrzCallbookLookup.cs` -- User-Agent string
14. `CrashReporter.vb` -- Error dialog title
15. `README.md` -- Title and description
16. `CLAUDE.md` -- Header and references
17. `docs/CHANGELOG.md` -- Header (add note about rename in current version)
18. `JJFlexRadioReadme.htm` -- All 55 occurrences of the app name

**Phases:**
1. Change `globals.vb:ProgramName` to "JJ Flexible Radio Access" and trace all usage
2. Update AssemblyInfo + .vbproj product name
3. Update all WPF/WinForms window titles and automation names
4. Update installer template, install.bat, build-installers.bat
5. Update documentation (README, CLAUDE.md, CHANGELOG header)
6. Update help file references (JJFlexRadioReadme.htm)
7. Build and verify -- installer runs, titles correct, speech correct
8. Build both x64 and x86 installers with new naming

---

### Track E: Compiled Help File
**Worktree:** `../jjflex-21e`
**Branch:** `sprint21/track-e`

**Toolchain: Markdown + hhc.exe (HTML Help Workshop)**

HelpNDoc GUI is inaccessible with NVDA and JAWS. Using pure CLI approach:
- Write content as Markdown in `docs/help/pages/`
- Convert to HTML (pandoc or simple script)
- Compile with `hhc.exe` (already installed at `C:\Program Files (x86)\HTML Help Workshop\hhc.exe`)

**New files:**
- `docs/help/` -- Help content source directory
- `docs/help/jjflex-help.hhp` -- HTML Help project file
- `docs/help/toc.hhc` -- Table of contents
- `docs/help/index.hhk` -- Index
- `docs/help/pages/` -- Individual help topic HTML files (generated from Markdown)
- `docs/help/md/` -- Markdown source files
- `JJFlexWpf/HelpLauncher.cs` -- F1 context-sensitive help dispatcher

**Modified files:**
- `KeyCommands.vb` -- F1 help hotkey wiring
- `JJFlexWpf/NativeMenuBar.cs` -- Help menu items
- `JJFlexRadio.vbproj` -- Include help file in build output
- `install template.nsi` -- Include help file in installer

**Help content structure:**
```
Welcome / Getting Started
+-- System Requirements
+-- Installation
+-- First Connection (local and SmartLink)
+-- Screen Reader Setup (JAWS / NVDA tips)

Operating Modes
+-- Modern Mode (default)
+-- Classic Mode
+-- Logging Mode

Keyboard Reference
+-- Global Hotkeys
+-- Modern Mode Hotkeys
+-- Classic Mode Hotkeys
+-- Logging Mode Hotkeys
+-- Leader Key Commands (Ctrl+J)
+-- ScreenFields Hotkeys (Ctrl+Shift+N/U/R/T/A)

Radio Control
+-- Tuning & Frequency
+-- Band Navigation (F-keys)
+-- Mode Switching
+-- Filters & DSP

Audio & Transmission
+-- PTT (Ctrl+Space / Shift+Space)
+-- Audio Workshop
+-- Meter Tones & Earcon Explorer
+-- Audio Chain Presets
+-- TX Bandwidth Sculpting
+-- TX Safety Features

Logging
+-- Quick Log (Ctrl+W)
+-- QSO Grid
+-- Callbook Lookup (QRZ / HamQTH)
+-- Station Lookup (Ctrl+L)

Settings & Profiles
+-- Global Profiles
+-- Operator Settings
+-- Earcon & Meter Sound Card
+-- Hotkey Customization

Troubleshooting
+-- Connection Issues
+-- SmartLink / Remote
+-- Audio Problems
+-- Known Issues
```

**F1 context-sensitive help + CHM integration:**
```csharp
public static class HelpLauncher
{
    private static readonly Dictionary<string, string> ContextMap = new()
    {
        { "FreqDisplay", "tuning.htm" },
        { "ScreenFieldsDSP", "filters-dsp.htm" },
        { "ScreenFieldsTX", "audio-transmission.htm" },
        { "AudioWorkshop", "audio-workshop.htm" },
        { "EarconExplorer", "earcon-explorer.htm" },
        { "LogPanel", "logging.htm" },
        { "SettingsDialog", "settings.htm" },
        // ... more mappings
    };

    public static void ShowHelp(string context = null)
    {
        string topic = context != null && ContextMap.TryGetValue(context, out var page)
            ? page : "welcome.htm";
        Help.ShowHelp(null, helpFilePath, topic);
    }
}
```

**Build integration:**
```batch
REM In build-installers.bat or post-build step:
IF EXIST "%ProgramFiles(x86)%\HTML Help Workshop\hhc.exe" (
    "%ProgramFiles(x86)%\HTML Help Workshop\hhc.exe" "docs\help\jjflex-help.hhp"
) ELSE (
    echo WARNING: HTML Help Workshop not found - skipping CHM build
)
```

**Phases:**
1. Create `docs/help/md/` directory with Markdown content files
2. Write keyboard reference page first (highest value for screen reader users)
3. Write getting started / first connection guide
4. Write feature documentation pages
5. Build script to convert Markdown -> HTML + compile with hhc.exe
6. HelpLauncher.cs -- F1 dispatcher with context-to-topic mapping
7. Wire F1 in KeyCommands.vb (Global scope) and Help menu in NativeMenuBar
8. Integrate CHM into build output and NSIS installer
9. Test with NVDA + JAWS -- navigate TOC, search, F1 from various app contexts
10. Write remaining content pages (new Sprint 21 features: audio workshop, meter tones, leader keys)

---

## Execution Plan

### Track Dependencies

```
Track A (Meter Tones + Earcon Infra) --+
Track B (Audio Workshop + Earcon Explorer) --+
Track C (Leader Key + Command Finder) --+-- All merge into Track A -- Merge to main
Track E (Help File) --+
                      |
Track D (App Rename) -+ <-- Merges LAST
```

**All tracks are independent** -- they work in different areas of the codebase:
- Track A: EarconPlayer, new MeterToneEngine, FlexBase meter wiring, AudioOutputConfig
- Track B: New AudioWorkshopDialog, ScreenFieldsPanel Transmission section, new AudioChainPreset
- Track C: KeyCommands.vb (leader key state machine), FreqOutHandlers (TX filter), extend existing Command Finder
- Track D: Display strings, installer scripts, documentation
- Track E: New help directory, new HelpLauncher, build scripts

**Potential merge conflicts (manageable):**
- A + B: Both add to NativeMenuBar.cs menus -- different submenus, easy merge
- A + B: Both may touch ScreenFieldsPanel.xaml.cs -- A adds to DSP category, B adds to Transmission category
- A + C: Both add commands to KeyCommands.vb -- different command IDs, easy merge
- B + C: Both touch TX filter properties -- B adds UI controls, C adds keyboard shortcuts. No overlap.
- D: Touches display strings in files others may edit. Merging last minimizes this.

### Execution Order

**Start all 5 tracks simultaneously.** No dependencies between them.

Track D (rename) should be the **last to merge** since it touches display strings across many files.

### Merge Plan

1. Tracks A, B, C, E can merge in any order as they complete
2. Track D merges last
3. Post-merge: clean build both x64 and x86, verify installers have new name
4. Final integration test

---

## Bug Fix Assignments

| Bug | Track | Rationale |
|-----|-------|-----------|
| BUG-004: System.IO.Ports crash | C | Quick try/catch in KeyCommands/FlexKnob |
| BUG-016: DSP feature gating | A + C | A adds gating to menu toggles, C adds gating to leader key toggles |
| BUG-023: Connect confirmation | B | Track B is touching NativeMenuBar for Audio Workshop menu |

---

## Version & Release

- **Version bump:** 4.1.16 (first version as "JJ Flexible Radio Access")
- **Installer names:** `Setup JJFlex_4.1.16_x64.exe` / `_x86.exe`
- **Tag:** `v4.1.16`
- **Changelog:** New section for 4.1.16 with rename announcement + all new features

---

## Estimated Track Sizes

| Track | New Files | Modified Files | Complexity |
|-------|-----------|---------------|------------|
| A: Meter Tones + Earcon Infra | 3 | 7 | High -- continuous audio generation, real-time meter subscription |
| B: Audio Workshop + Earcon Explorer | 3 | 5 | High -- 3-tab dialog, many controls, preset persistence, earcon explorer |
| C: Leader Key + TX Sculpt + CF Extension | 0 | 6 | Medium-High -- state machine, earcon vocabulary, extend existing Command Finder |
| D: App Rename | 0 | 18+ | Medium -- mechanical but thorough, installer testing |
| E: Help File | 15+ | 4 | Medium -- content writing + build integration |

---

## Verification

After all tracks merge:
1. Clean build both x64 and x86: `build-installers.bat`
2. Verify exe version: `powershell -Command "(Get-Item 'bin\x64\Release\net8.0-windows\win-x64\JJFlexRadio.exe').VersionInfo.ProductVersion"`
3. Run app -- verify window title says "JJ Flexible Radio Access"
4. Connect to radio -- verify meter tones work (S-meter, SWR)
5. Open Audio Workshop (Ctrl+Shift+W) -- verify all 3 tabs
6. Test Earcon Explorer -- play each sound category
7. Test leader key (Ctrl+J -> N, B, M, E, ?, Escape)
8. Test Command Finder (F1) -- search for commands, execute
9. Test TX filter sculpting (Ctrl+Shift+[/])
10. Test with NVDA and JAWS -- all speech, earcons, navigation
11. Open CHM help (F1 from various contexts)
12. Install both x64 and x86 -- verify installer names and display name
