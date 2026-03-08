# Sprint 21: Resonant Signal Sculpt

**Theme:** TX audio mastery, meter sonification, and the beginning of JJ Flexible Radio Access.

**Sprint Goal:** Give blind operators the same real-time TX audio feedback that sighted operators get from meters, plus keyboard-driven TX sculpting, a leader key system for efficient DSP control, and rebrand the app for its multi-radio future.

---

## Scope

### Feature Work

1. **Meter Sonification Engine + Peak Watcher** — Real-time audio tones that track S-meter, ALC, and mic levels. Toggleable, unobtrusive, designed to coexist with received audio. Peak watcher monitors ALC/mic in background and alerts via earcons when levels are too hot or too cold.

2. **Audio Workshop Dialog** — A dedicated WPF dialog for sculpting transmit audio. Exposes mic gain, compressor, compander, speech processor, TX monitor, and TX filter width. Real-time spoken feedback as you adjust. Save/load audio chain presets as shareable XML.

3. **ScreenFields Transmission Expansion** — The Transmission category currently has only TX Power, VOX, and Tune Power. Expand it with mic gain, compander, speech processor, TX filter low/high, TX monitor, and mic boost/bias toggles.

4. **TX Bandwidth Sculpting** — Keyboard shortcuts to nudge TX filter edges, mirroring the RX filter bracket-key workflow. Lets operators narrow their TX bandwidth for contest conditions or widen for ragchew.

5. **Leader Key System (Ctrl+J)** — Two-step keyboard dispatch: press Ctrl+J, then a second key to execute a command. Initial bindings: DSP filter toggles (N=NR, B=NB, W=WNB, R=RNN, A=ANF, P=APF), meter tone toggles, and audio shortcuts.

6. **App Rename: JJ Flexible Radio Access** — Full rebrand from JJFlexRadio. All code, installer, registry, AppData paths, documentation. Migration code for existing user data.

7. **Compiled Help File** — CHM or HTML-based help with F1 context-sensitive wiring. Keyboard reference, getting started guide, feature documentation.

### Bug Fixes (sprinkled across tracks)

- **BUG-004**: System.IO.Ports v9.0.0 crash on shutdown — guard FlexKnob.Dispose()
- **BUG-016**: RNN/DSP toggles lack feature gating in menus — add license/capability checks
- **BUG-023**: "Connect while connected" needs confirmation dialog

---

## Research Summary

### Meter Data Flow (FlexLib → App)

```
Flex Radio hardware
  → VITA-49 UDP meter packets (VitaMeterPacket.cs)
  → Radio.UpdateMeters() dispatches to individual Meter objects
  → Meter.DataReady event fires with float data
  → Radio-level events: ForwardPowerDataReady, MicDataReady, HWAlcDataReady, etc.
  → FlexBase handlers store values: _SMeter, _PowerDBM, _MicData, _ALC, etc.
  → UI can read properties or subscribe to events
```

**Available meters and current FlexBase integration:**

| Meter | FlexLib Event | FlexBase Property | Units | Notes |
|-------|--------------|-------------------|-------|-------|
| S-meter (LEVEL) | `Slice.SMeterDataReady` | `SMeter` (int) | S-units 0-9+dB | Per-slice, only fires for active slice |
| Forward Power | `Radio.ForwardPowerDataReady` | `_PowerDBM` (float) | dBm | TX only |
| Reflected Power | `Radio.ReflectedPowerDataReady` | — | dBm | Not stored yet |
| SWR | `Radio.SWRDataReady` | `SWR` (float) | VSWR | TX only |
| Mic Level | `Radio.MicDataReady` | `MicData` (float) | Level | Continuous during TX |
| Mic Peak | `Radio.MicPeakDataReady` | `_MicPeakData` (float) | Peak | TX only |
| Compression Peak | `Radio.CompPeakDataReady` | — | dB reduction | Handler is no-op |
| Hardware ALC | `Radio.HWAlcDataReady` | `ALC` (float) | Volts | TX only |
| PA Temperature | `Radio.PATempDataReady` | `PATemp` (float) | °C | Always available |
| Supply Voltage | `Radio.VoltsDataReady` | `Volts` (float) | Volts | Always available |
| PA Efficiency | `Radio.PAEffDataReady` | — | dBm | Not stored yet |

**Key insight:** S-meter data arrives via `Slice.SMeterDataReady` event on the active slice. FlexBase already wires this up via the `sMeter_t` inner class. All other meters arrive via `Radio.*DataReady` events and are already stored in FlexBase fields.

### TX Audio Properties (FlexBase Wrappers)

| Property | Type | Range | Increment | Notes |
|----------|------|-------|-----------|-------|
| `MicGain` | int | 0-100 | 1 | Maps to Radio.MicLevel |
| `MicBoost` | OffOnValues | on/off | — | +20 dB boost |
| `MicBias` | OffOnValues | on/off | — | Phantom power for condenser mics |
| `Compander` | OffOnValues | on/off | — | Dynamic range processor |
| `CompanderLevel` | int | 0-100 | 5 | Compression intensity |
| `ProcessorOn` | OffOnValues | on/off | — | Speech processor |
| `ProcessorSetting` | enum | NOR/DX/DXX | — | Processor aggressiveness |
| `Monitor` | OffOnValues | on/off | — | TX self-monitor |
| `SBMonitorLevel` | int | 0-100 | 5 | Monitor volume |
| `SBMonitorPan` | int | 0-100 | 5 | Monitor L/R pan |
| `TXFilterLow` | int | 0-9950 | 50 | TX low-cut frequency (Hz) |
| `TXFilterHigh` | int | 50-10000 | 50 | TX high-cut frequency (Hz) |
| `XmitPower` | int | 0-100 | 1 | RF output power |
| `TunePower` | int | 0-100 | 1 | Tune mode power |

### NAudio Tone Infrastructure (EarconPlayer.cs)

The existing EarconPlayer provides:
- Persistent `WaveOutEvent` + `MixingSampleProvider` (44100 Hz stereo) — always running
- `PlayTone(freq, duration, volume)` — single sine wave
- `PlayTonePanned(freq, duration, volume, pan)` — stereo placement
- `PlayChirp(startHz, endHz, duration, volume)` — frequency sweep
- `ChirpSampleProvider` — custom ISampleProvider for linear frequency sweeps
- `AddToMixer()` / `AddToMixerPanned()` — inject any ISampleProvider into the mix
- Embedded .wav support with `CachedSound` + `CachedSoundSampleProvider`

**What's missing for meter tones:** A *continuous* tone generator. Current infrastructure plays one-shot tones (fixed duration). Meter tones need a persistent, dynamically-updating pitch/volume. We'll need a new `ContinuousToneSampleProvider` that reads from a shared frequency/volume variable updated by meter events.

### Hotkey Dispatch (KeyCommands.vb)

- `DoCommand(Keys)` — single-key lookup in `KeyDictionary`, invokes handler
- `KeyScope` enum: Global, Radio, Classic, Modern, Logging
- `KeyDefType` — stores key binding + scope + command ID
- **No multi-step support** — all commands are single keypress

**Leader key implementation approach:**
1. Add state to `DoCommand()`: `Private leaderKeyActive As Boolean = False`
2. When Ctrl+J is pressed: set `leaderKeyActive = True`, speak "J", start timeout timer (2 seconds)
3. On next keypress: if `leaderKeyActive`, look up in `LeaderKeyDictionary` instead of `KeyDictionary`
4. Execute leader command, reset state
5. On timeout or Escape: reset state, speak "cancelled"

### Filter Preset Infrastructure (FilterPresets.cs)

- XML serialized per operator: `{operatorName}_filterPresets.xml`
- `FilterPreset` — Name, Low, High, Width (computed)
- `ModePresets` — groups presets by mode (SSB, CW, DIGI, AM, FM)
- Built-in defaults per mode (Narrow, Normal, Wide, etc.)
- `CyclePreset()` cycles through presets
- `MirrorForMode()` handles LSB/DIGL sign inversion
- Pattern is directly reusable for TX filter presets and audio chain presets

### App Rename Scope

**31 files need updates, 294 total occurrences.** Critical categories:

| Category | Files | Key Items |
|----------|-------|-----------|
| Build/Project | 5 | .sln, .vbproj, AssemblyInfo.vb, Directory.Build.props |
| Installer | 4 | install template.nsi, install.bat, build-installers.bat, install.nsi |
| App Code | 12 | globals.vb (ProgramName const), BridgeForm.vb, MainWindow.xaml, WelcomeDialog.xaml, ApplicationEvents.vb |
| Data Paths | 6 | SmartLinkAccountManager.cs, ConnectionProfiler.cs, CrashReporter.vb, AuthDialog, AuthFormWebView2 |
| Documentation | 4 | README.md, CHANGELOG.md, CLAUDE.md, JJFlexRadioReadme.htm (55 occurrences) |

**Critical: AppData migration.** Existing users have data in `%AppData%\JJFlexRadio\`. Need startup code to detect old folder and migrate to new name. Also registry cleanup for uninstall entries.

---

## Track Decomposition

### Track A: Meter Sonification Engine + Peak Watcher
**Worktree:** Main repo (`C:\dev\JJFlex-NG`)
**Branch:** `sprint21/track-a`

**New files:**
- `JJFlexWpf/MeterToneEngine.cs` — Core engine: subscribes to meter events, drives continuous tones
- `Radios/MeterToneConfig.cs` — Persisted settings (which meters are active, volume, pitch mapping)

**Modified files:**
- `JJFlexWpf/EarconPlayer.cs` — Add `ContinuousToneSampleProvider` for persistent updating tones
- `Radios/FlexBase.cs` — Expose meter events or callbacks for MeterToneEngine to subscribe to
- `JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs` — Add meter tone toggle to DSP category (or new Meters category)
- `JJFlexWpf/NativeMenuBar.cs` — Add meter tone toggle to menus
- `KeyCommands.vb` — Add hotkeys for meter tone toggle, cycle meter source
- `JJFlexWpf/Dialogs/SettingsDialog.xaml.cs` — Add Meter Tones tab to Settings
- `ApplicationEvents.vb` — Load/wire MeterToneConfig at startup

**Design: Meter Tone Engine**

The engine supports **multiple simultaneous meter tones**, each independently panned in the stereo field. This is the killer feature: an operator can listen to S-meter on the left ear and ALC on the right ear simultaneously, getting spatial awareness of both RX signal and TX health at once. Sighted operators can't even do this — they have to look at meters one at a time.

```
MeterToneEngine (static or singleton)
├── Initialize(FlexBase rig) — subscribe to meter events
├── MeterSlots: List<MeterSlot> — up to 4 simultaneous tones
│   Each MeterSlot:
│   ├── Source: MeterSource enum (SMeter, ALC, Mic, Power, SWR, Compression, Voltage, PATemp)
│   ├── Enabled: bool
│   ├── Volume: float (0.0 - 1.0)
│   ├── Pan: float (-1.0 left to +1.0 right)
│   ├── PitchLow: int (Hz) — pitch at meter minimum
│   ├── PitchHigh: int (Hz) — pitch at meter maximum
│   ├── ToneProvider: ContinuousToneSampleProvider — the audio source
│   └── ThresholdMode: bool — if true, silent below threshold, tone above
│
├── Enabled: bool — master toggle (mutes all slots)
├── MasterVolume: float (0.0 - 1.0) — scales all slot volumes
│
├── Default configurations:
│   "RX Monitor": S-meter centered, 200-1200 Hz
│   "TX Monitor": ALC left + Mic right, 300-1500 Hz / 200-1000 Hz
│   "Full Monitor": S-meter left + ALC right, both with defaults
│
├── S-Meter tone:
│   Pitch: 200 Hz (S0/noise floor) → 1200 Hz (S9+40)
│   Update rate: ~10 Hz (throttle meter events to avoid audio glitching)
│   Auto-mutes when TX (S-meter meaningless during transmit)
│
├── ALC tone:
│   Pitch: 300 Hz (no ALC) → 1500 Hz (ALC maxed)
│   Volume: increases with ALC level (urgency mapping)
│   Only active during TX
│   Threshold mode option: silent below threshold, tone above
│
├── Mic Level tone:
│   Pitch: 200 Hz (silence) → 1000 Hz (full scale)
│   Useful for mic positioning/gain setting
│   Only active during TX
│
├── Other meters (SWR, Power, Voltage, PA Temp):
│   Available as additional slots for advanced users
│   SWR tone is useful during antenna tuning (pitch rises with SWR)
│
└── Peak Watcher (sub-module):
    Monitors ALC/Mic continuously during TX
    Thresholds: Warning (ALC > 0.5V), Critical (ALC > 0.8V)
    Alerts: earcon beep at warning, rapid beep at critical
    Speech: "ALC high" after 3 seconds sustained
    Cooldown: don't nag more than once per 10 seconds
    Integrates with existing PTT health monitor code
    Works independently of meter tones (can have peak watcher without tones)
```

**ContinuousToneSampleProvider design:**
```csharp
class ContinuousToneSampleProvider : ISampleProvider
{
    // Shared volatile fields updated by meter events
    public volatile float Frequency;  // Hz — updated by meter callback
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
2. MeterToneEngine core — subscribe to S-meter events, map to pitch
3. ALC and Mic meter tone modes
4. Peak Watcher — threshold alerts with earcon beeps + speech
5. MeterToneConfig persistence + Settings UI tab
6. Menu items and hotkeys (toggle, cycle source, adjust volume)
7. ScreenFieldsPanel integration (toggle + source selector in DSP category)
8. Earcon sound card selector — enumerate NAudio output devices, add dropdown to Settings → Audio tab
9. Master earcon volume — gain multiplier on EarconPlayer's MixingSampleProvider, add slider to Settings → Audio tab
10. Persist earcon device + volume in a new `AudioOutputConfig.cs` (not MeterToneConfig — this config will also serve future Say Again, Parrot, and recording playback)
11. BUG-016: Add feature gating to DSP menu toggles (natural fit — we're already in NativeMenuBar)

**Hotkeys:**
- `Ctrl+Shift+M` — Toggle meter tones on/off (master toggle)
- `Alt+Shift+M` — Cycle active meter configuration ("RX Monitor" → "TX Monitor" → "Full Monitor" → custom)
- Leader key: Ctrl+J → M — same as Ctrl+Shift+M toggle
- Speak current meter reading when tone is off (already exists for S-meter)

**Meter Tone Settings (in Settings Dialog → Meter Tones tab):**
- Slot 1-4: each with source dropdown, volume slider, pan slider, enabled toggle
- Preset configurations dropdown (RX Monitor, TX Monitor, Full Monitor)
- Master volume
- Peak watcher thresholds and cooldown

**Earcon Sound Card & Volume (in Settings Dialog → Audio tab):**

Currently, rig audio device selection uses JJPortaudio and is accessed via "Radio Audio Device" in the menu. EarconPlayer (NAudio) has NO device selection — it defaults to Windows default output. We need to add:

- **Earcon/Meter Sound Card selector** — dropdown of available audio output devices for all tones, beeps, chirps, and meter tones. Uses NAudio's `WaveOut.DeviceCount` / `WaveOut.GetCapabilities()` to enumerate. Separate from the rig audio device. Persisted in MeterToneConfig or a new EarconConfig.
- **Master Earcon Volume** — 0-100 slider that scales ALL EarconPlayer output (PTT chirps, filter edge tones, warning beeps, meter tones, everything). Stored as a gain multiplier on the MixingSampleProvider.
- Label as "Earcon and Meter Sound Card" in the UI — users know what earcons are from the changelog, and "sound card" is universally understood. Avoid "NAudio" in UI.

This enables the mixer workflow: rig audio on one fader (via PortAudio device selection), earcons/meter tones on another fader (via NAudio device selection), screen reader on a third. Full broadcast-style mixing with hardware control.

---

### Track B: Audio Workshop + ScreenFields TX Expansion
**Worktree:** `../jjflex-21b`
**Branch:** `sprint21/track-b`

**New files:**
- `JJFlexWpf/Dialogs/AudioWorkshopDialog.xaml` + `.cs` — TX audio sculpting dialog
- `Radios/AudioChainPreset.cs` — Save/load/share audio chain configurations

**Modified files:**
- `JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs` — Expand Transmission category
- `JJFlexWpf/Controls/ScreenFieldsPanel.xaml` — XAML for new Transmission controls
- `JJFlexWpf/NativeMenuBar.cs` — Add "Audio Workshop" menu item
- `KeyCommands.vb` — Add hotkey for Audio Workshop dialog
- `JJFlexWpf/Dialogs/SettingsDialog.xaml.cs` — Audio chain preset management in Settings
- `ApplicationEvents.vb` — Load AudioChainPreset at startup

**ScreenFieldsPanel Transmission expansion:**

Current controls: TX Power, VOX, Tune Power

Add:
- Mic Gain (0-100, step 1)
- Mic Boost (on/off toggle)
- Mic Bias (on/off toggle)
- Compander (on/off toggle)
- Compander Level (0-100, step 5) — shown when Compander is on
- Speech Processor (on/off toggle)
- Processor Setting (cycle: NOR → DX → DXX) — shown when Processor is on
- TX Filter Low (0-9950, step 50)
- TX Filter High (50-10000, step 50)
- TX Monitor (on/off toggle)
- Monitor Level (0-100, step 5) — shown when Monitor is on

**Audio Workshop Dialog design:**

A standalone WPF dialog (not modal — can stay open while operating) that provides a focused TX audio tuning experience. Think of it as a sound check booth.

```
Audio Workshop Dialog
├── Section: Microphone
│   ├── Mic Gain slider (0-100) with real-time level indicator (spoken)
│   ├── Mic Boost toggle (+20 dB)
│   ├── Mic Bias toggle (phantom power)
│   └── Mic Level meter (real-time spoken feedback: "Mic level: -20 dB")
│
├── Section: Processing
│   ├── Compander toggle + level (0-100)
│   ├── Speech Processor toggle + mode (NOR/DX/DXX)
│   └── Compression meter (real-time: "Compression: 6 dB")
│
├── Section: TX Filter
│   ├── Low Cut (Hz) — with live width readout
│   ├── High Cut (Hz)
│   ├── Width display (computed)
│   └── TX Filter preset buttons (Narrow/Normal/Wide/Custom)
│
├── Section: Monitor
│   ├── TX Monitor toggle
│   ├── Monitor Level (0-100)
│   └── Monitor Pan (0-100)
│
├── Section: Meters (read-only, real-time)
│   ├── ALC level (with warning threshold indicator)
│   ├── Mic peak level
│   ├── Compression amount
│   ├── Forward power
│   └── SWR
│
├── Toolbar:
│   ├── Load Preset (opens file picker)
│   ├── Save Preset (saves current chain)
│   ├── Export Preset (to file for sharing)
│   └── Reset to Defaults
│
└── Real-time feedback:
    - Every adjustment speaks the new value
    - Meter section auto-reads every 2 seconds during TX (configurable)
    - "Your mic gain is 45. ALC is clean. Compression 3 dB."
    - Peak warnings same as peak watcher
```

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

    // Persistence
    public static AudioChainPreset Load(string filePath);
    public void Save(string filePath);

    // Apply to radio
    public void ApplyTo(FlexBase rig);

    // Capture from radio
    public static AudioChainPreset CaptureFrom(FlexBase rig, string name);
}

[XmlRoot("AudioChainPresets")]
public class AudioChainPresets
{
    public List<AudioChainPreset> Presets { get; set; }

    public static AudioChainPresets Load(string configDir, string operatorName);
    public void Save(string configDir, string operatorName);
    // File: {operatorName}_audioPresets.xml
}
```

**Phases:**
1. ScreenFieldsPanel Transmission expansion (all new controls + polling)
2. AudioChainPreset data class + XML persistence
3. Audio Workshop Dialog — layout, controls, basic wiring
4. Audio Workshop real-time meter display (subscribe to FlexBase meter data)
5. Audio Workshop spoken feedback (auto-announce adjustments + periodic meter reads)
6. Preset load/save/export UI in Audio Workshop
7. Menu item + hotkey for Audio Workshop
8. BUG-023: Add "Connect while connected" confirmation guard (natural fit — touching menus)

**Hotkeys:**
- `Ctrl+Shift+W` — Open Audio Workshop dialog
- Audio Workshop internal: Tab through sections, arrows adjust, Enter for presets

---

### Track C: TX Sculpting Keyboard + Leader Key System
**Worktree:** `../jjflex-21c`
**Branch:** `sprint21/track-c`

**Modified files:**
- `KeyCommands.vb` — Leader key state machine + TX filter shortcuts + new command entries
- `JJFlexWpf/MainWindow.xaml.cs` — Leader key visual/audio feedback
- `JJFlexWpf/NativeMenuBar.cs` — Show leader key bindings in menu text
- `JJFlexWpf/FreqOutHandlers.cs` — TX filter adjustment handlers
- `Radios/FlexBase.cs` — TX filter nudge methods (if not already present)
- `ApplicationEvents.vb` — Wire leader key display callback

**Leader Key Implementation:**

The leader key (Ctrl+J) is a **layered keystroke** system. Instead of needing a unique hotkey combo for every command, you press Ctrl+J first (the "leader"), then a simple second key to pick the action. Think of it like opening an invisible audio menu — you navigate by ear, not by sight.

**Earcon vocabulary — critical for responsive feel:**

Every leader key event gets a distinct earcon so the operator knows what happened without waiting for speech. During a QSO, you should be able to toggle NB on with Ctrl+J → B and just hear the rising tone — no speech needed.

| Event | Earcon | Description |
|-------|--------|-------------|
| Enter leader mode (Ctrl+J) | Rising "bink" (400→600 Hz, 80ms) | "I'm listening for your next key" |
| Feature toggled ON | "bonk-bink" (300→500→800 Hz, 50ms each) | Rising = activated |
| Feature toggled OFF | "bink-bonk" (800→500→300 Hz, 50ms each) | Falling = deactivated |
| Value adjusted | Soft click (reuse filter-edge-move) | "Setting changed" |
| Invalid second key | Dull buzz (200 Hz, 100ms, square wave) | "Not a valid command" |
| Timeout (2 sec, no key) | Soft descending (500→300 Hz, 150ms) | "Cancelled, timed out" |
| Escape (explicit cancel) | Same as timeout | Consistent cancel sound |
| Help requested (?) | Distinct double-chime (800+1000 Hz) | "Here comes a list" |

These earcons are added to EarconPlayer as named methods: `LeaderEnterTone()`, `FeatureOnTone()`, `FeatureOffTone()`, `LeaderInvalidTone()`, `LeaderCancelTone()`, `LeaderHelpTone()`.

**State machine:**

```vb
' In KeyCommands.vb

Private leaderKeyActive As Boolean = False
Private leaderKeyTimer As System.Timers.Timer
Private Const LeaderKeyTimeoutMs As Integer = 2000

' Modified DoCommand:
Function DoCommand(ByVal k As Keys) As Boolean
    ' If leader key is active, dispatch to leader dictionary
    If leaderKeyActive Then
        leaderKeyActive = False
        leaderKeyTimer.Stop()

        If k = Keys.Escape Then
            EarconPlayer.LeaderCancelTone()
            Return True
        End If

        Return DoLeaderCommand(k)
    End If

    ' Check for leader key trigger (Ctrl+J)
    If k = (Keys.J Or Keys.Control) Then
        leaderKeyActive = True
        leaderKeyTimer.Start()
        EarconPlayer.LeaderEnterTone()
        Radios.ScreenReaderOutput.Speak("J", interrupt:=True)
        Return True
    End If

    ' Normal single-key dispatch (existing code)
    ...
End Function

Function DoLeaderCommand(ByVal k As Keys) As Boolean
    Dim entry = LeaderKeyLookup(k)
    If entry IsNot Nothing Then
        entry.rtn()
        ' Handler is responsible for playing FeatureOnTone/FeatureOffTone
        Return True
    Else
        EarconPlayer.LeaderInvalidTone()
        Radios.ScreenReaderOutput.Speak("Unknown command")
        Return True ' consumed the key even though invalid
    End If
End Function

' Timeout handler:
Private Sub LeaderKeyTimedOut(sender As Object, e As EventArgs)
    leaderKeyActive = False
    EarconPlayer.LeaderCancelTone()
End Sub
```

**Speech + earcon layering:** Each leader command handler plays the appropriate earcon AND speaks the result. The earcon fires immediately (< 5ms latency), speech follows. If the operator is in a QSO and wants silence, they can mute speech (PTT speech toggle) and rely on earcons alone — the rising/falling tones are unambiguous.

**Leader key bindings (Ctrl+J → ...):**

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

Note: These mirror the existing RX filter bracket-key pattern. The Ctrl+Shift and Ctrl+Alt modifiers distinguish TX from RX.

**Phases:**
1. Leader key earcons in EarconPlayer — LeaderEnterTone(), FeatureOnTone(), FeatureOffTone(), LeaderInvalidTone(), LeaderCancelTone(), LeaderHelpTone()
2. Leader key state machine in DoCommand() — Ctrl+J trigger, timeout, cancel, Escape
3. Leader key dictionary + initial DSP toggle bindings (each handler plays on/off earcon + speaks result)
4. Leader key help command (Ctrl+J → ?)
4. TX filter nudge handlers in FreqOutHandlers
5. TX filter keyboard shortcuts (Ctrl+Shift/Alt + brackets)
6. TX filter earcon feedback (reuse filter edge tones from Sprint 19)
7. Menu text updates to show leader key bindings
8. Feature gating: leader key DSP toggles respect license/capability (partial BUG-016 fix)
9. BUG-004: Guard FlexKnob.Dispose() with try/catch (quick fix, fits here since we're in KeyCommands)

---

### Track D: App Rename — JJ Flexible Radio Access
**Worktree:** `../jjflex-21d`
**Branch:** `sprint21/track-d`

**IMPORTANT:** This track should merge LAST to minimize conflicts with feature tracks.

**Rename strategy:**
- Executable stays `JJFlexRadio.exe` internally (assembly name) to avoid breaking user shortcuts, file associations, and registry entries
- **Display name** changes everywhere users see it: window titles, dialogs, installer, menus, about, speech
- **Product name** in AssemblyInfo changes to "JJ Flexible Radio Access"
- **AppData folder** stays `JJFlexRadio` for now (no migration needed if we keep internal name)
- Full assembly rename deferred to a future sprint when we're ready to break shortcuts

**Scope: Display-name rename only. Filenames stay short.**

The principle: "JJ Flexible Radio Access" is the **brand name** users see and hear. But file names, folder names, and internal identifiers stay as `JJFlex` or `JJFlexRadio` for brevity and to avoid breaking existing user shortcuts, AppData paths, and registry entries. No migration code needed.

| What Changes (display/brand) | What Stays (files/internal) |
|---|---|
| Window title → "JJ Flexible Radio Access" | Assembly name: `JJFlexRadio` |
| Installer welcome text → "JJ Flexible Radio Access" | Exe file: `JJFlexRadio.exe` |
| Installer filename → `Setup JJFlex_4.x.x_x64.exe` | AppData folder: `JJFlexRadio` |
| Welcome speech → "Welcome to JJ Flexible Radio Access" | Registry keys: `JJFlexRadio` |
| About dialog → "JJ Flexible Radio Access" | Internal class/namespace names |
| NSIS display name → "JJ Flexible Radio Access" | Solution/project file names |
| Changelog header | .sln, .vbproj file names |
| README title | |
| Screen reader automation names | |
| QRZ User-Agent → "JJFlexibleRadioAccess" | |
| Help file title | |

**Files to modify:**

1. `globals.vb` — Change `ProgramName` constant (this propagates to many places)
2. `My Project/AssemblyInfo.vb` — Change `AssemblyProduct` and `AssemblyTitle`
3. `JJFlexRadio.vbproj` — Change `<Product>` element
4. `JJFlexWpf/MainWindow.xaml` — AutomationProperties.Name
5. `JJFlexWpf/MainWindow.xaml.cs` — Welcome speech, window title
6. `JJFlexWpf/Dialogs/WelcomeDialog.xaml` — Title, automation name, button text
7. `BridgeForm.vb` — Me.Text
8. `ApplicationEvents.vb` — Welcome speech for first-run
9. `install template.nsi` — Installer display name, welcome text, shortcuts
10. `install.bat` — Installer output filename format
11. `build-installers.bat` — Installer output filename format
12. `JJFlexWpf/NativeMenuBar.cs` — About dialog ProductName
13. `QrzLookup/QrzCallbookLookup.cs` — User-Agent string
14. `CrashReporter.vb` — Error dialog title
15. `README.md` — Title and description
16. `CLAUDE.md` — Header and references
17. `docs/CHANGELOG.md` — Header (add note about rename in current version)
18. `JJFlexRadioReadme.htm` — All 55 occurrences of the app name

**Phases:**
1. Change `globals.vb:ProgramName` to "JJ Flexible Radio Access" and trace all usage
2. Update AssemblyInfo + .vbproj product name
3. Update all WPF/WinForms window titles and automation names
4. Update installer template, install.bat, build-installers.bat
5. Update documentation (README, CLAUDE.md, CHANGELOG header)
6. Update help file references (JJFlexRadioReadme.htm)
7. Build and verify — installer runs, titles correct, speech correct
8. Build both x64 and x86 installers with new naming

---

### Track E: Compiled Help File
**Worktree:** `../jjflex-21e`
**Branch:** `sprint21/track-e`

**New files:**
- `docs/help/` — Help content source directory
- `docs/help/jjflex-help.hhp` — HTML Help project file (or alternative format)
- `docs/help/toc.hhc` — Table of contents
- `docs/help/index.hhk` — Index
- `docs/help/pages/` — Individual help topic HTML files
- `JJFlexWpf/HelpLauncher.cs` — F1 context-sensitive help dispatcher

**Modified files:**
- `KeyCommands.vb` — F1 help hotkey wiring (may already exist partially)
- `JJFlexWpf/NativeMenuBar.cs` — Help menu items
- `JJFlexRadio.vbproj` — Include help file in build output
- `install template.nsi` — Include help file in installer

**Help content structure:**
```
Welcome / Getting Started
├── System Requirements
├── Installation
├── First Connection (local and SmartLink)
├── Screen Reader Setup (JAWS / NVDA tips)
│
Operating Modes
├── Modern Mode (default)
├── Classic Mode
├── Logging Mode
│
Keyboard Reference
├── Global Hotkeys
├── Modern Mode Hotkeys
├── Classic Mode Hotkeys
├── Logging Mode Hotkeys
├── Leader Key Commands (Ctrl+J)
├── ScreenFields Hotkeys (Ctrl+Shift+N/U/R/T/A)
│
Radio Control
├── Tuning & Frequency
├── Band Navigation (F-keys)
├── Mode Switching
├── Filters & DSP
├── TX Bandwidth Sculpting
│
Audio & Transmission
├── PTT (Ctrl+Space / Shift+Space)
├── Audio Workshop
├── Meter Tones
├── Audio Chain Presets
├── TX Safety Features
│
Logging
├── Quick Log (Ctrl+W)
├── QSO Grid
├── Callbook Lookup (QRZ / HamQTH)
├── Station Lookup (Ctrl+L)
│
Settings & Profiles
├── Global Profiles
├── Operator Settings
├── Hotkey Customization
│
Troubleshooting
├── Connection Issues
├── SmartLink / Remote
├── Audio Problems
├── Known Issues
```

**F1 context-sensitive help:**
```csharp
// HelpLauncher.cs
public static class HelpLauncher
{
    private static readonly Dictionary<string, string> ContextMap = new()
    {
        { "FreqDisplay", "tuning.htm" },
        { "ScreenFieldsDSP", "filters-dsp.htm" },
        { "ScreenFieldsTX", "audio-transmission.htm" },
        { "AudioWorkshop", "audio-workshop.htm" },
        { "LogPanel", "logging.htm" },
        { "SettingsDialog", "settings.htm" },
        // ... more mappings
    };

    public static void ShowHelp(string context = null)
    {
        string topic = context != null && ContextMap.TryGetValue(context, out var page)
            ? page : "welcome.htm";

        // Open CHM to specific topic, or open HTML in browser
        Help.ShowHelp(null, helpFilePath, topic);
    }
}
```

**Format: CHM (Compiled HTML Help)**

CHM is the right choice for our users:
- Built into Windows — `hh.exe` on every machine, no extra viewer
- NVDA and JAWS both navigate CHM (tree TOC, search, index)
- F1 context-sensitive help is one API call: `Help.ShowHelp(null, path, topic)`
- Single file to distribute, bundled in installer
- Searchable and indexed out of the box

**Toolchain: HelpNDoc + Markdown authoring**

- **HelpNDoc** (helpndoc.com) — GUI help authoring tool with Markdown import and CHM compilation
- Paid license may be needed for command-line build support (user researching)
- Fallback: HTML Help Workshop (`hhc.exe`) for free command-line CHM compilation

**Authoring workflow:**
1. Write/edit content as Markdown files in `docs/help/pages/`
2. Import into HelpNDoc project via GUI (organize TOC, set topic IDs)
3. HelpNDoc project file saved as `docs/help/jjflex-help.hnd`
4. Build: `hnd8.exe build "docs\help\jjflex-help.hnd" --verysilent` (or `hhc.exe` fallback)
5. Output: `docs/help/JJFlexRadio.chm` → copied to build output → bundled in installer

**Build integration:**
```batch
REM In build-installers.bat or post-build step:
IF EXIST "%ProgramFiles%\HelpNDoc\hnd8.exe" (
    "%ProgramFiles%\HelpNDoc\hnd8.exe" build "docs\help\jjflex-help.hnd" --verysilent
) ELSE IF EXIST "%ProgramFiles(x86)%\HTML Help Workshop\hhc.exe" (
    "%ProgramFiles(x86)%\HTML Help Workshop\hhc.exe" "docs\help\jjflex-help.hhp"
) ELSE (
    echo WARNING: No help compiler found - skipping CHM build
)
```

**Pre-sprint setup (user action required):**
- Install HelpNDoc from helpndoc.com
- Test GUI accessibility with NVDA (navigate, import a test .md, generate CHM)
- If command-line build needs paid license, purchase it

**Phases:**
1. Create `docs/help/pages/` directory with Markdown content files
2. Write keyboard reference page first (highest value for screen reader users)
3. Write getting started / first connection guide
4. Write feature documentation pages (ScreenFields, PTT, band nav, filters, etc.)
5. Set up HelpNDoc project — import Markdown, organize TOC, assign topic IDs for F1
6. HelpLauncher.cs — F1 dispatcher with context-to-topic-ID mapping
7. Wire F1 in KeyCommands.vb (Global scope) and Help menu in NativeMenuBar
8. Integrate CHM into build output and NSIS installer
9. Test with NVDA + JAWS — navigate TOC, search, F1 from various app contexts
10. Write remaining content pages (audio workshop, meter tones, leader keys — new Sprint 21 features)

---

## Execution Plan

### Track Dependencies

```
Track A (Meter Tones) ─────────────┐
Track B (Audio Workshop) ──────────┤
Track C (TX Sculpting + Leader) ───┼── All merge into Track A ── Merge to main
Track E (Help File) ───────────────┤
                                   │
Track D (App Rename) ──────────────┘ ← Merges LAST
```

**All tracks are independent** — they work in different areas of the codebase:
- Track A: EarconPlayer, new MeterToneEngine, FlexBase meter wiring
- Track B: New AudioWorkshopDialog, ScreenFieldsPanel Transmission section, new AudioChainPreset
- Track C: KeyCommands.vb (leader key), FreqOutHandlers (TX filter)
- Track D: Display strings, installer scripts, documentation
- Track E: New help directory, new HelpLauncher, build scripts

**Potential merge conflicts (manageable):**
- A + B: Both add to NativeMenuBar.cs menus — different submenus, easy merge
- A + B: Both may touch ScreenFieldsPanel.xaml.cs — A adds to DSP category, B adds to Transmission category
- A + C: Both add commands to KeyCommands.vb — different command IDs, easy merge
- B + C: Both touch TX filter properties — B adds UI controls, C adds keyboard shortcuts. No overlap.
- D: Touches display strings in files others may edit (NativeMenuBar, MainWindow, ApplicationEvents). Merging last minimizes this.

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
| BUG-004: System.IO.Ports crash | C | Quick try/catch in KeyCommands/FlexKnob — Track C is in that area |
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
| A: Meter Tones | 2 | 7 | High — continuous audio generation, real-time meter subscription |
| B: Audio Workshop | 3 | 6 | High — new dialog, many controls, preset persistence |
| C: Leader Key + TX Sculpt | 0 | 6 | Medium-High — state machine in hotkey dispatch, new shortcuts |
| D: App Rename | 0 | 18+ | Medium — mechanical but thorough, installer testing |
| E: Help File | 15+ | 4 | Medium — content writing + build integration |
