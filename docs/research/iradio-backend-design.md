# IRadioBackend Abstraction Design

**Status:** Multi-radio Phase 4 deliverable — the central architectural proposal
**Date:** 2026-04-29
**Author:** Claude Opus 4.7 (track/multi-radio)
**Audience:** Noel — the doc to read carefully before greenlighting an implementation sprint
**Builds on:** Phases 1-3 (JJ Radio inventory, radio class taxonomy, Hamlib API survey)

---

## What this document is

This is the proposal for `IRadioBackend` — the C# interface JJ Flexible's UI
layer talks to, behind which sits either the existing FlexLib code (refactored)
or a new Hamlib-based backend (or future backends). It is the deliverable Phase
9 (architecture synthesis) consumes and Noel reviews before deciding whether to
greenlight an implementation sprint.

The design is constrained by:

- **The accessibility moat** — must remain in .NET, must not push JJF toward a
  cross-platform UI framework that compromises UIA accessibility
  (`project_csharp_accessibility_moat.md`).
- **JJ Radio inheritance** — must extend Jim's `AllRadios`/`RigCaps` shape
  rather than replace it from zero (Phase 1).
- **Radio class breadth** — must accommodate SDR, HF, all-mode, FM mobile,
  and provisional HT/RX classes from day one (Phase 2).
- **Hamlib's API and version constraints** — must be implementable atop the
  ~50 calls in section 2 of the Hamlib API survey (Phase 3).
- **Flexibility principle** — every behavioral decision the backend takes must
  flow from user-overridable config, not developer assumption
  (`project_flexibility_principle.md`).
- **Friction-tax principle** — backend choices must minimize the user's setup
  burden (`project_friction_tax_principle.md`).
- **End-to-end accessibility** — connection profile setup, model picking, port
  config, error recovery all accessible (`feedback_accessibility_is_end_to_end.md`).

---

## 1. Architectural shape

```
+--------------------------------------------------------------------+
|                       JJF UI layer (WinForms + WPF)                |
|   FlexScreen, NativeMenuBar, dialogs, ScreenReaderOutput, ...      |
+----------------------------+---------------------------------------+
                             |
                             |  IRadioBackend (this document)
                             |  + IRadioBackendCatalog (registration / discovery)
                             |  + IPerRadioConfig    (user-side overrides — Phase 5)
                             |  + IAudioRouter       (audio plumbing — Phase 6)
                             |
       +---------------------+---------------------+
       |                     |                     |
+---------------+   +-------------------+   +---------------+
| FlexLibBackend|   | HamlibBackend     |   | (FutureXBackend)
|  (existing    |   |  (new — wraps     |   |  (provisional;
|   FlexBase    |   |   libhamlib via   |   |   for radios where
|   refactored) |   |   P/Invoke)       |   |   neither fits)
+---------------+   +-------------------+   +---------------+
       |                     |                     |
+---------------+   +-------------------+   +---------------+
|  FlexLib +    |   |  libhamlib-5.dll  |   |  (TBD)        |
|  VITA-49      |   |  (LGPL bundled)   |   |               |
+---------------+   +-------------------+   +---------------+
```

The shape preserves what JJ Radio's `AllRadios` + `RigTable` already
established:

- An abstract surface JJF UI consumes, with capability introspection deciding
  what to render.
- A registration table of supported radios mapping integer IDs to backend
  implementation classes plus per-radio defaults.
- Concrete backends per protocol family, NOT per radio model (the Hamlib
  backend handles every Hamlib-supported radio with no per-radio JJF code).

The shape extends what Jim left:

- `IRadioBackend` is an interface (consumable polymorphically) rather than an
  abstract base class — easier to mock for testing, easier to compose
  (potential decorator backends for tracing, retry, etc.).
- Capability introspection works from a richer flag/data superset (Phase 2).
- Streaming surfaces are first-class but optional (Flex-only initially).
- Per-radio config has a separate interface so the user-overridable bits
  aren't mixed into the radio-driver bits.
- Audio routing has its own interface so non-Flex radios can plug into the
  audio pipeline without polluting `IRadioBackend`.

---

## 2. The capability-and-data model

The interface separates **capabilities** (booleans about what the radio can do)
from **radio facts** (numbers, lists, ranges) from **runtime state** (current
frequency, current mode, current S-meter). This separation matters because the
capability flags drive UI rendering at startup, the facts inform UI bounds and
options, and the state is read/written via methods and events.

### 2.1 Capability flags — replacement for `RigCaps.Caps`

Today's `RigCaps.Caps` is a 64-bit packed enum that's already at 53/64 bits
used. The new design uses a `HashSet<RadioCapability>` with an open enum:

```csharp
public enum RadioCapability
{
    // Frequency / VFO / mode
    FrequencyGet,
    FrequencySet,
    ModeGet,
    ModeSet,
    VfoGet,
    VfoSet,
    SplitGet,
    SplitSet,
    SplitFreqGet,
    SplitFreqSet,
    Tune,                        // tuning-step support

    // RIT / XIT / repeater offset
    RitGet, RitSet,
    XitGet, XitSet,
    RepeaterShiftGet, RepeaterShiftSet,
    RepeaterOffsetGet, RepeaterOffsetSet,

    // PTT / DCD
    PttSoftware,                 // PTT via CAT
    PttHardware,                 // PTT via dedicated PTT line (Flex, some Hamlib)
    DcdGet,                      // squelch-open status

    // Tones (CTCSS / DCS)
    CtcssEncodeGet, CtcssEncodeSet,
    CtcssSquelchGet, CtcssSquelchSet,
    DcsCodeGet, DcsCodeSet,
    DcsSquelchGet, DcsSquelchSet,

    // Levels (analog 0..1 or specific units; one flag per level)
    AfGainGet, AfGainSet,
    RfGainGet, RfGainSet,
    SquelchGet, SquelchSet,
    MicGainGet, MicGainSet,
    TxPowerGet, TxPowerSet,
    KeySpeedGet, KeySpeedSet,
    NotchFreqGet, NotchFreqSet,
    NoiseReductionLevelGet, NoiseReductionLevelSet,
    NoiseBlankerLevelGet, NoiseBlankerLevelSet,
    PreampGet, PreampSet,
    AttenuatorGet, AttenuatorSet,
    AgcGet, AgcSet,
    AgcTimeGet, AgcTimeSet,
    SignalStrengthGet,           // S-meter
    SwrGet,
    AlcGet,
    CompressorGet, CompressorSet,
    VoxGainGet, VoxGainSet,
    VoxDelayGet, VoxDelaySet,
    AntiVoxGet, AntiVoxSet,
    SlopeLowGet, SlopeLowSet,
    SlopeHighGet, SlopeHighSet,
    UsbAfInputGet, UsbAfInputSet,
    UsbAfOutputGet, UsbAfOutputSet,

    // Functions (boolean toggles; one flag per function)
    NoiseBlanker,
    NoiseReduction,
    Compressor,
    Vox,
    AutomaticNotchFilter,
    ManualNotchFilter,
    SemiBreakIn,
    FullBreakIn,
    AutoTunerStart,              // RIG_FUNC_TUNER
    Lock,
    Mute,
    SatelliteMode,
    DualWatch,
    Diversity,
    BeatCanceller,
    AudioPeakFilter,
    Tone1750Burst,
    Reverse,
    AutoFrequencyControl,
    Spectrum,
    SyncVfos,

    // Memory channels
    MemoryGet, MemorySet,
    MemoryChannelGet, MemoryChannelSet,
    MemoryNamesUserEditable,     // JJF stores per-channel labels

    // Antenna / ATU
    AntennaSelectGet, AntennaSelectSet,
    AtuMemoriesUserEditable,     // JJF stores ATU memory annotations
    ManualAtuStart,
    ManualTransmit,

    // Hardware-keyed CW
    HardwareCwKeyer,             // rig_send_morse / Flex equivalent

    // SDR-specific (only FlexLibBackend exposes these)
    MultiSliceCapable,
    MultiFlexClients,
    SmartLinkRemote,
    PanadapterStream,
    WaterfallStream,
    MeterStream,
    AudioStream,
    IqStream,
    DiversityCapable,            // Flex 2-SCU radios
    CwxTextKeyer,                // Flex CWX
    FirmwareUpdateInBand,        // Flex firmware push

    // VHF/UHF FM mobile-specific
    BandPair,                    // explicit Band A / Band B (TM-V71A)
    CrossbandRepeat,
    ToneSelectionByIndex,
    MemoryChannelEdgePairs,
    PowerPresetEnum,             // High / Mid / Low rather than continuous
    ProgrammingModeBulk,         // bulk memory ops via separate protocol
    BuiltInTNC,
    AprsBuiltIn,
    GpsBuiltIn,

    // HT-specific (provisional)
    DigitalVoiceModes,           // D-STAR / DMR / C4FM
    BatteryStatus,
    ProgrammingOnly,             // radio doesn't support live CAT meaningfully

    // RX-only / scanner
    WideRxScannerMode,
    NoTransmit,                  // radio is RX-only

    // Connection / config
    PowerStateGet,               // turn radio on/off via CAT
    PowerStateSet,
    Reset,                       // dangerous; soft/master reset
    RequiresHardwareFlowControl, // surfaces to serial config UI
    RequiresLicense,             // Flex feature license relevant

    // Diagnostics
    GetCaps,                     // can introspect capabilities (true on Hamlib, true on Flex via FlexLib)
}
```

This list is illustrative and not exhaustive — Phase 4 implementation will add
or rename flags as the wiring surfaces specifics. The principle: **one flag
per "is this UI section worth rendering" decision.**

For backwards compatibility with code currently checking `RigCaps.Caps`, a
helper layer maps existing names to the new enum (or the new enum is named to
match existing names where possible). Don't bulk-rename callsites; introduce
the new enum and migrate over time.

### 2.2 Radio facts — per-radio data the backend reports

Static per-radio data, not boolean. Read once at connect time, surfaced as
properties:

```csharp
public interface IRadioFacts
{
    string ModelName { get; }                       // "Kenwood TS-2000"
    string Manufacturer { get; }                    // "Kenwood"
    string FirmwareVersion { get; }                 // queried via rig_get_caps_cptr / FlexLib
    string SerialNumber { get; }                    // Flex direct; Hamlib radios may not have it
    RadioClass Class { get; }                       // taxonomy hint
    int MaxSlices { get; }                          // 1 for non-multislice; 8 for FLEX-6700
    int MaxMemoryChannels { get; }                  // 200 for HF, 1000 for mobiles
    int MemoryNameLength { get; }                   // 6 for TM-V71A, 8 for TM-D710
    IReadOnlyList<Band> SupportedBands { get; }     // amateur band coverage
    IReadOnlyList<RadioMode> SupportedModes { get; }
    IReadOnlyList<int> SupportedTuningSteps { get; }
    IReadOnlyList<PowerPreset> PowerPresets { get; } // null when continuous; non-null for mobiles
    int MaxTxPowerWatts { get; }                    // approximate; per-band table for accuracy
    FrequencyRange RxFrequencyRange { get; }
    FrequencyRange TxFrequencyRange { get; }        // possibly multi-range for multi-band radios
    IReadOnlyList<AntennaPort> AntennaPorts { get; }
}

public enum RadioClass
{
    SdrTransceiver,              // FLEX
    HfTransceiver,               // TS-590-like
    AllModeAllBand,              // TS-2000-like
    VhfUhfFmMobile,              // TM-V71A-like
    Ht,                          // provisional
    RxOnly,                      // provisional
    Other,                       // unclassified
}
```

`SerialNumber` is interesting: Flex always has it (radio's MAC/serial); Hamlib
radios sometimes don't expose it via CAT. For radios that don't, the
per-radio config keys by user-assigned nickname or by connection profile
(Phase 5 handles this).

### 2.3 Runtime state — the get/set surface

The behavioral interface — what JJF actually calls to operate the radio.

```csharp
public interface IRadioBackend : IDisposable
{
    // ---- Identity & introspection ----
    IRadioFacts Facts { get; }
    HashSet<RadioCapability> Capabilities { get; }
    bool HasCapability(RadioCapability cap);

    // ---- Lifecycle ----
    Task<ConnectResult> ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    bool IsConnected { get; }
    event EventHandler<ConnectionStateChangedEventArgs> ConnectionStateChanged;

    // ---- Slice / VFO ----
    int SliceCount { get; }                              // 1 for VFO-only radios; up to 8 for Flex
    int ActiveSlice { get; set; }                        // 0-based
    SliceState GetSlice(int sliceIndex);                 // composite state read
    Task SetSliceFrequencyAsync(int sliceIndex, double hz, CancellationToken ct = default);
    Task SetSliceModeAsync(int sliceIndex, RadioMode mode, int passbandHz, CancellationToken ct = default);
    Task<bool> CreateSliceAsync(double hz, RadioMode mode, CancellationToken ct = default);  // Flex; returns false on non-multislice
    Task<bool> RemoveSliceAsync(int sliceIndex, CancellationToken ct = default);
    event EventHandler<SliceChangedEventArgs> SliceChanged;

    // ---- PTT and split ----
    Task SetPttAsync(bool ptt, CancellationToken ct = default);
    bool Ptt { get; }
    event EventHandler<bool> PttChanged;
    Task SetSplitAsync(bool enabled, double txHz, CancellationToken ct = default);
    bool Split { get; }
    double SplitTxFrequency { get; }
    event EventHandler<bool> SplitChanged;

    // ---- Levels (analog) ----
    Task<float> GetLevelFloatAsync(RadioLevel level, int sliceIndex = -1, CancellationToken ct = default);
    Task SetLevelFloatAsync(RadioLevel level, float value, int sliceIndex = -1, CancellationToken ct = default);
    Task<int> GetLevelIntAsync(RadioLevel level, int sliceIndex = -1, CancellationToken ct = default);
    Task SetLevelIntAsync(RadioLevel level, int value, int sliceIndex = -1, CancellationToken ct = default);
    event EventHandler<LevelChangedEventArgs> LevelChanged;

    // ---- Functions (toggles) ----
    Task<bool> GetFunctionAsync(RadioFunction func, int sliceIndex = -1, CancellationToken ct = default);
    Task SetFunctionAsync(RadioFunction func, bool enabled, int sliceIndex = -1, CancellationToken ct = default);
    event EventHandler<FunctionChangedEventArgs> FunctionChanged;

    // ---- Tones (CTCSS / DCS) ----
    Task SetCtcssEncodeAsync(int sliceIndex, double freqHz, CancellationToken ct = default);
    Task<double> GetCtcssEncodeAsync(int sliceIndex, CancellationToken ct = default);
    // ... and CTCSS_SQL, DCS variants

    // ---- Memory channels ----
    Task<MemoryChannel> GetMemoryChannelAsync(int channelNumber, CancellationToken ct = default);
    Task SetMemoryChannelAsync(int channelNumber, MemoryChannel ch, CancellationToken ct = default);
    Task SetCurrentMemoryChannelAsync(int channelNumber, CancellationToken ct = default);
    Task DeleteMemoryChannelAsync(int channelNumber, CancellationToken ct = default);
    Task<MemoryChannelBulkResult> ReadAllMemoryChannelsAsync(IProgress<int> progress = null, CancellationToken ct = default);

    // ---- Antenna ----
    Task SetAntennaAsync(int sliceIndex, int antennaPortIndex, CancellationToken ct = default);
    Task<int> GetAntennaAsync(int sliceIndex, CancellationToken ct = default);

    // ---- ATU ----
    Task StartAutoAtuTuneAsync(CancellationToken ct = default);   // gated by AutoTunerStart capability
    Task StartManualAtuTuneAsync(CancellationToken ct = default); // gated by ManualAtuStart capability
    event EventHandler<AtuStateEventArgs> AtuStateChanged;

    // ---- CW ----
    Task SendMorseAsync(string message, CancellationToken ct = default);
    Task StopMorseAsync(CancellationToken ct = default);

    // ---- Power / reset ----
    Task SetPowerStateAsync(bool on, CancellationToken ct = default);  // gated by PowerStateSet
    Task<bool> GetPowerStateAsync(CancellationToken ct = default);

    // ---- Streaming surfaces (optional — null on backends that don't support) ----
    IPanadapterStream Panadapter { get; }       // null when capability absent
    IWaterfallStream Waterfall { get; }
    IMeterStream Meters { get; }
    IAudioStream Audio { get; }
    IIqStream Iq { get; }

    // ---- Diagnostic ----
    string LastError { get; }
    event EventHandler<RadioErrorEventArgs> ErrorOccurred;
}
```

This is large but tractable. Most methods are async because both FlexLib and
Hamlib operate over network or serial — every operation can fail or take time.
`CancellationToken` accepted for every async method per .NET conventions.

The `int sliceIndex` defaulting to `-1` (meaning "active slice" / VFO_CURR)
gives both convenient calls (`SetLevelFloat(RadioLevel.AfGain, 0.5f)`) and
explicit-slice calls (`SetLevelFloat(RadioLevel.AfGain, 0.5f, sliceIndex: 2)`).

Events are conventional .NET events, fired on the UI thread (the backend
marshalls). Polling-vs-transceive at the Hamlib layer is hidden.

Optional streaming surfaces are read-only properties of typed interfaces; null
means the capability is absent. The UI checks the capability flag (or the
property for null) and renders accordingly.

### 2.4 The composite SliceState

```csharp
public sealed class SliceState
{
    public int SliceIndex { get; init; }
    public double Frequency { get; init; }
    public RadioMode Mode { get; init; }
    public int PassbandHz { get; init; }
    public bool RxAudioOn { get; init; }
    public int FilterShiftHz { get; init; }
    public int RitOffsetHz { get; init; }
    public int XitOffsetHz { get; init; }
    public bool RitEnabled { get; init; }
    public bool XitEnabled { get; init; }
    public bool MutedByOperator { get; init; }
    public bool IsTransmitOn { get; init; }
    // ... more as needed; add fields, don't break the type
}
```

`record` or `sealed class` with init-only properties — immutable snapshot.
Each `GetSlice(idx)` call returns a fresh snapshot. For composite read paths
(Hamlib's `rig_get_vfo_info`), the backend can populate this in one CAT
round-trip.

For Flex's slice-rich state (slice's audio routing, slice owner client name,
license-gated state) the backend extends `SliceState` with derived classes or
optional fields. The UI checks for non-null on optional fields.

---

## 3. The slice abstraction — unifying Flex and Hamlib

This is the biggest design call in the document. The recommendation:

**Expose slices generically. VFO-only radios collapse to slice 0 (and slice 1
when split is on, mapping to `RIG_VFO_B`).**

### 3.1 Why slices, not VFOs

JJF's existing UI is built around the slice concept (Customize Home, slice
hotkeys, MultiFlex client awareness, slice cycling). Pushing the UI to "VFO A"
/ "VFO B" terminology for non-Flex radios would either:

- Force JJF into Hamlib's VFO model and force Flex slices to fake A/B/C/D
  semantics (loses Flex's slice richness)
- Or split the UI into "slice mode" vs "VFO mode" depending on the radio (UI
  forking — bad)

Generalizing slice as the unit and mapping VFO-only radios as N=1-or-2 slices
preserves the JJF UI model.

### 3.2 The mapping rules

For FlexLibBackend:
- `SliceCount = backend.MaxSlices` (queried from FlexLib at connect)
- Each slice is a `Slice` from FlexLib. Direct one-to-one.
- `ActiveSlice` is the slice with focus.

For HamlibBackend on a single-VFO-pair radio (TS-590, IC-7300):
- `SliceCount = 1`
- Slice 0 maps to Hamlib's `RIG_VFO_CURR` for state reads/writes.
- Split TX (different TX frequency) is a property of slice 0, NOT a second
  slice. This matters because users expect "split is a toggle on a slice,"
  not "split creates a new slice."

For HamlibBackend on a dual-receiver radio (TS-2000, IC-9100):
- `SliceCount = 2` when in normal mode
- Slice 0 = `RIG_VFO_MAIN`, slice 1 = `RIG_VFO_SUB`
- When `SatelliteMode` is on, slice 0 = uplink (main), slice 1 = downlink
  (sub) — the backend tracks the satellite-mode toggle and surfaces it as a
  function, not as a slice-count change.

For HamlibBackend on a VHF/UHF FM mobile (TM-V71A):
- `SliceCount = 2` (both bands always active)
- Slice 0 = Band A = `RIG_VFO_A`, slice 1 = Band B = `RIG_VFO_B`
- Cross-band repeat is exposed as `CrossbandRepeat` capability + a function
  toggle, not as a slice-count change.

For HamlibBackend on an HT or simple mobile (TH-D74):
- Identical to dual-receiver: 2 slices for two bands, one slice for single-
  band radios.

### 3.3 Why slice 0 vs RIG_VFO_CURR for single-VFO radios

Two options:

(a) HamlibBackend always passes `RIG_VFO_CURR` to Hamlib for slice 0
operations. Whichever VFO is currently active on the radio gets affected.

(b) HamlibBackend tracks "I'm operating on VFO A" or "VFO B" per the user's
slice selection and passes the explicit VFO.

**Recommendation: (b) with `RIG_VFO_CURR` as a fallback.** Tracking explicit
VFO matters because:

- Users with split TX active sometimes flip the "active VFO" on the radio's
  panel (creating a confusing state)
- Memory recall ops expect a specific VFO context
- It makes JJF's slice-switching deterministic regardless of radio panel
  state

Track "intended VFO" on the JJF side; resync on radio panel events.

### 3.4 Multi-VFO radios with sub-receiver in non-sat mode

TS-2000 has main + sub-receiver. Sub-receiver can monitor a different band
while main is on HF. JJF's slice 1 represents the sub-receiver. PTT applies
to main only (TS-2000 doesn't TX from sub). Capability flag
`SubReceiverTransmits = false` for slices that can only receive.

For Flex MultiFlex (multi-client) the slices are owned by different clients —
the FlexLibBackend exposes this via a per-slice owner property in its
extended `SliceState`. UI uses this for "this slice belongs to remote operator
Don" displays.

### 3.5 The honest trade-off

Single-VFO radios with split TX have an awkward fit in this model. Today's
Flex JJF UI rarely uses split (multi-slice supersedes it). For Mark's TS-590
where split-tx is the norm for DX work, the UX needs to surface "this slice
has a split TX" clearly. Capability flag `SplitSet` plus a `SplitTxFrequency`
on the slice state is the answer; UI surfaces it.

This is a real cost — the slice abstraction works cleanly for SDR and dual-
receiver but feels stretched on single-VFO radios with split. Alternatives
(separate `Vfos` collection vs `Slices` collection) were considered and
rejected because they fork the UI. Accept the stretch.

---

## 4. Capability negotiation — the UI's contract with the backend

### 4.1 The rule

**The UI never branches on `RadioClass`.** It branches on capability flags.

Why: class is too coarse. A Yaesu FT-991A is in HF Transceiver class but has
VHF/UHF on it; a "shows VHF panel" decision should be `radio.Facts.SupportedBands.Contains(Band.Vhf2m)`,
not `radio.Facts.Class == RadioClass.AllModeAllBand`.

Class metadata is for diagnostics, logs, picker grouping, default config —
not runtime branching.

### 4.2 The pattern

```csharp
// In a UI section (e.g., the satellite panel)
if (radio.HasCapability(RadioCapability.SatelliteMode))
{
    EnableSatelliteUi();
}
else
{
    HideSatelliteUi();
}
```

For UI sections that depend on data, not just capability:

```csharp
if (radio.Facts.SupportedBands.Any(b => b.IsVhfOrAbove))
{
    EnableVhfUi();
}
```

### 4.3 Dynamic capability changes

Some capabilities change at runtime:

- TS-2000 entering satellite mode changes which controls are meaningful
- Flex license activation enables a previously-disabled capability
- Connection downgrade (slow-mode) disables streaming surfaces

The interface emits a `CapabilitiesChanged` event when the set changes:

```csharp
event EventHandler<CapabilitiesChangedEventArgs> CapabilitiesChanged;
```

UI re-runs its capability checks on the event. This is the same pattern
`AllRadios.CapsChangeEvent` uses today, generalized.

### 4.4 Per-feature license gating (Flex)

Flex feature licenses are a special case: capability flags say "the radio CAN
do this physically," but a license check is needed before showing UI. The
clean way is to fold license into the capability:

- `RadioCapability.Diversity` is in the set if AND ONLY IF the radio is 2-SCU
  AND the diversity feature license is active.
- License changes (rare but possible) raise `CapabilitiesChanged`.

This lets all UI sections branch on capability flags uniformly without each
one needing to query license state.

### 4.5 Granularity decisions to make in Phase 4 implementation

The current Phase 2 capability list has ~150 flags. Some are debatable:

- Should "Spectrum" be one flag or split into "SpectrumScope" / "SpectrumHold"?
- Should every level/function become its own flag, or should we group some
  together (e.g., "DSP" capability covers NR / NB / Notch as a group)?

Design preference: **finer granularity initially**, collapse as duplicate
checks surface. It's easier to merge flags than to split them later.

---

## 5. Connection lifecycle and error model

### 5.1 The connect path

```csharp
public class ConnectionConfig
{
    public string DisplayName { get; init; }       // user-friendly name
    public BackendKind Kind { get; init; }         // FlexLib | Hamlib | Future
    public int RigModelId { get; init; }           // RadioSelection.RIGID* OR Hamlib model_t
    public string PortPath { get; init; }          // "COM3" or "192.168.1.50"
    public int? PortBaudRate { get; init; }
    public Parity? PortParity { get; init; }
    public int? PortDataBits { get; init; }
    public int? PortStopBits { get; init; }
    public Handshake? PortHandshake { get; init; }
    public string CivAddress { get; init; }        // Icom CI-V
    public bool? UseHamlibTransceive { get; init; } // null = backend default
    public Dictionary<string, string> ExtraConfig { get; init; }   // backend-specific overflow
}

public enum ConnectResult
{
    Success,
    PortNotFound,
    PortInUse,
    AuthenticationFailed,        // SmartLink Auth0
    AuthenticationCancelled,
    RadioModelMismatch,          // user picked TM-V71A but radio answers TS-590
    ProtocolError,
    Timeout,
    Cancelled,
    UnsupportedFirmware,
    OperatorPresenceDenied,      // for sensitive operations
    LicenseRequired,
    Unknown,                     // catch-all with LastError populated
}
```

`ConnectionConfig` is a flat record. Per-radio-config (Phase 5) layers on top —
the user picks a saved config from a list, JJF instantiates the right backend
with that config.

`ConnectResult` is rich enough to drive accessible error messages. Each result
maps to a specific `ScreenReaderOutput.Speak()` payload.

### 5.2 The disconnect path

`DisconnectAsync` is always idempotent and never throws. After disconnect,
properties returning state behave like "no radio" (frequency = 0, slice count
= 0, etc.). Re-connecting requires `ConnectAsync` again with the same or
different config.

### 5.3 Reconnection

Networks lose connection. Serial ports get yanked. The contract:

- The backend does NOT auto-reconnect on its own. JJF code (orchestration
  layer) drives reconnection per its retry policy (matches existing behavior
  for Flex SmartLink).
- `ConnectionStateChanged` fires when state changes. JJF's reconnect logic
  hooks this event.
- `RadioErrorEventArgs` carries error details for diagnostics.

This matches the existing Flex backend behavior; no change for FlexLibBackend
users.

### 5.4 Error event vs return code

Most async methods return `Task<TResult>` for results. Errors that happen on
the call path throw or surface in the result. Errors that happen async (the
radio sends an unsolicited error event, or the connection drops) fire
`ErrorOccurred`. The two paths are NOT redundant — call-path errors must be
on the result; ambient errors must be on the event.

For `ConnectAsync` specifically, the result is the rich `ConnectResult` enum
(not a thrown exception). Connection failures are expected and should not look
like exceptions to the calling code. This is consistent with friction-tax
principle: a user mis-typing a COM port number is not an exception, it's a
failure-to-connect condition the UI surfaces gracefully.

---

## 6. Threading model

### 6.1 The contract

- All public methods are safe to call from the UI thread.
- All events are raised on the UI thread.
- Long-running operations (connect, ATU tune) honor `CancellationToken` and
  return promptly when cancelled.
- The backend internally does NOT block the UI thread; it dispatches CAT
  operations to its own worker(s).

### 6.2 Implementation guidance for HamlibBackend

Hamlib is synchronous and per-rig single-threaded. Run all `rig_*` calls on a
dedicated worker thread (or thread pool with serialization per rig). Marshal
events back via `SynchronizationContext.Post` to the UI thread.

For polling (radios without transceive), a background timer task on the
backend thread polls and emits events when values change.

For transceive-enabled radios, the Hamlib reader thread fires native callbacks;
the backend's callback handlers marshal to UI thread.

### 6.3 Implementation guidance for FlexLibBackend

Existing JJF code already uses the `RigControl` / FlexLib event model. The
refactor wraps the existing patterns behind `IRadioBackend`; threading
behavior unchanged.

---

## 7. Extension points — the "ext" surface

Hamlib has an "ext" parameter system for radio-specific controls outside the
standard surface (CW message memories, microphone profiles, custom DSP modes).
FlexLib has its own equivalent (custom feature license enums).

`IRadioBackend` doesn't model these as first-class capabilities. Instead, it
exposes them via a generic key-value surface:

```csharp
public interface IRadioExtensionSurface
{
    IReadOnlyList<ExtensionParameter> EnumerateParameters();
    Task<ExtensionValue> GetExtensionAsync(string parameterName, CancellationToken ct = default);
    Task SetExtensionAsync(string parameterName, ExtensionValue value, CancellationToken ct = default);
}

public sealed class ExtensionParameter
{
    public string Name { get; init; }
    public ExtensionParameterKind Kind { get; init; }   // Boolean / Integer / Float / String / Enum
    public string AccessibleLabel { get; init; }        // for UI announcement
    public IReadOnlyList<string> EnumOptions { get; init; }  // for Kind=Enum
    public RadioCapability? GatingCapability { get; init; }  // only show if cap set
}
```

UI can iterate the extension parameters and render a generic settings panel
for each. Power users get the full surface; casual users don't see it unless
they go looking. This matches Hamlib's design philosophy ("everything not in
the core API can still be reached").

---

## 8. Backend registration — the new RigTable

```csharp
public sealed record BackendRegistration(
    int RigId,
    string DisplayName,
    string ManufacturerHint,
    BackendKind BackendKind,
    Type BackendType,
    RadioClass DefaultClass,
    Func<ConnectionConfig> DefaultConnectionConfig);

public interface IRadioBackendCatalog
{
    IReadOnlyList<BackendRegistration> AllRegistrations { get; }
    BackendRegistration GetById(int rigId);
    IRadioBackend Instantiate(int rigId, ConnectionConfig config);
}
```

For Flex radios: 9 entries, all pointing to FlexLibBackend, same default
config (network).

For Hamlib radios: hundreds of entries, all pointing to HamlibBackend, with
per-model `DefaultConnectionConfig` derived from `rig_get_caps()` at install
time (the Hamlib model list is enumerated at first run, cached).

The registration table is the source of truth for the picker UI ("Add a
Radio" → list of all registrations grouped by manufacturer). User selects an
entry; JJF creates a `ConnectionConfig` from defaults, lets the user edit
(port, baud, etc.), saves to per-radio config (Phase 5), and connects.

### 8.1 Registration enumeration: when

Two options:

(a) Enumerate Hamlib's model list at install time, generate a static list
file shipped with JJF.

(b) Enumerate at first run via P/Invoke, cache locally.

**Recommendation: (b).** First-run cost is one-time and transparent (a
hundred-millisecond enumeration on a modern machine). Avoids shipping a stale
list when Hamlib bumps. The enumeration is `for each rig_model in 1..N: caps
= rig_get_caps(model); if caps != null: register`.

### 8.2 Hidden / deprecated radios

Hamlib's rig list includes deprecated radios and dummy entries. The catalog
should filter out:

- `RIG_MODEL_DUMMY` — Hamlib's mock for testing
- Radios marked `RIG_STATUS_BUGGY` in their caps
- `RIG_MODEL_NETRIGCTL` — proxy for talking to a remote rigctld
- Manufacturer-specific oddities (debug rigs, etc.)

Provide a "show all" toggle for power users who explicitly want a buggy or
testing radio in the picker.

---

## 9. The legacy bridge — `AllRadios` compatibility

Existing JJF code references `AllRadios` extensively. The refactor:

### 9.1 Phase 1 of refactor (Sprint 30 candidate)

`AllRadios` becomes the partial implementation of `IRadioBackend` for Flex
radios. `FlexBase : AllRadios` continues to work. New code consumes
`IRadioBackend` via casts where needed. No callsite changes initially.

### 9.2 Phase 2 of refactor

Extract `IRadioBackend` interface. Implement on `FlexBase` (FlexLibBackend
becomes the new interface implementation; old `AllRadios` becomes the
backing class for the implementation). Existing UI code keeps working
against `AllRadios` for the transition period.

### 9.3 Phase 3 of refactor

Introduce `HamlibBackend` implementing `IRadioBackend`. UI code that's been
migrated to consume `IRadioBackend` works with both. Code still consuming
`AllRadios` directly (Flex-only) keeps working — just the UI sections that
need to support non-Flex radios are migrated first.

### 9.4 Phase 4+

Migrate remaining `AllRadios`-direct callsites to `IRadioBackend`. Eventually
`AllRadios` becomes an internal implementation helper, not a UI-facing type.

This is a multi-sprint refactor by design. The first implementation sprint is
"introduce IRadioBackend, port FlexBase to it, no Hamlib backend yet" —
small, focused, validates the abstraction without committing to Hamlib.

---

## 10. What `IRadioBackend` does NOT include

Explicitly out of scope for this interface:

### 10.1 Audio routing

Audio is its own concern. `IAudioRouter` is a separate interface (Phase 6
deliverable). For Flex radios, the FlexLibBackend exposes `IAudioStream` for
network audio access; the audio router can plug it in. For non-Flex radios,
the audio router talks to sound cards directly.

### 10.2 Per-radio user-overridable config

Antenna labels, ATU memory annotations, custom button bindings — these are
user-overridable and live in `IPerRadioConfig` (Phase 5). The backend reports
"the radio has 3 antenna ports"; the per-radio config holds "port 1 = hex
beam, port 2 = wire dipole, port 3 = dummy load."

### 10.3 Discovery beacons

Flex's discovery beacon is a Flex-specific protocol. It lives in
`FlexLibBackend.Catalog.Discovery` or a separate `IRadioDiscovery` for Flex.
Hamlib has no equivalent (you pick a model from a list). The discovery
mechanism is per-backend, not part of the abstraction.

### 10.4 SmartLink Auth0 flow

Flex-specific. Lives in FlexLibBackend's authentication helper. Hamlib radios
don't have this concept.

### 10.5 The accessibility layer

`ScreenReaderOutput`, `KeyCommands`, `NativeMenuBar` consume `IRadioBackend`
but are themselves not part of it. The backend reports radio state; the
accessibility layer decides when and how to announce.

### 10.6 Rotators and amplifiers

Hamlib supports rotators and amplifiers. JJF doesn't currently. If JJF
eventually does, separate interfaces (`IRotatorBackend`, `IAmplifierBackend`)
are the right shape — not folded into `IRadioBackend`.

---

## 11. Open design questions

These remain for Noel review. Phase 4 *implementation* commits to specific
answers; this document collects the questions.

1. **Interface vs abstract base class.** Recommendation: interface for
   testability + composition. Concrete implementations may share a base class
   for plumbing (`RadioBackendBase`).

2. **Async-only vs sync-where-fast.** Recommendation: async everywhere. The
   cost of a `Task<T>` for a fast property read is negligible; the consistency
   payoff is large. Where ergonomics matter, expose snapshot properties (e.g.,
   `IsConnected`) cached from event handlers.

3. **Naming: `IRadioBackend` vs `IRadioController` vs `IRadioBridge`.**
   Recommendation: `IRadioBackend`. "Backend" already used in the spike and
   research docs; aligns with how Hamlib refers to its drivers.

4. **`int sliceIndex = -1` for "active slice" vs an explicit `Slice.Active`
   sentinel.** Recommendation: int with -1 sentinel. Symmetrical with how
   Hamlib uses `RIG_VFO_CURR`. Document clearly.

5. **Capability set as `HashSet<RadioCapability>` vs bit-flags.**
   Recommendation: `HashSet`. The 64-bit limit is already a problem;
   serializing as int array for config persistence is fine.

6. **Streaming surfaces as separate interfaces vs. capability+method on the
   backend.** Recommendation: separate interfaces (`IPanadapterStream`,
   etc.). Lets the UI bind to the stream interface directly without going
   through the backend; lets the backend not implement them at all when
   capability is absent.

7. **Multi-radio-active concurrent operation.** The strategic identity memo
   mentions "up to four concurrent connections in tabbed UI." `IRadioBackend`
   per radio handles this naturally; orchestration (which one is the
   foreground / receives keystrokes / emits to the audio output) is a JJF UI
   concern. Phase 4 doesn't model multi-radio-active explicitly; Phase 5+ may
   add it as a per-radio config flag for "is this radio currently routed to
   speakers."

8. **Backwards-compatibility horizon.** How long do we keep `AllRadios` as a
   public type? Recommendation: indefinitely as an internal helper; remove it
   from the public API once UI has fully migrated to `IRadioBackend` (Sprint
   30+ scope).

9. **ConnectionProfiler integration.** JJF's existing connection profiler
   (`ConnectionProfiler.cs`) tracks events for the diagnostic stuck-connecting
   work. It currently knows about FlexLib connection events specifically. The
   refactor should generalize it to consume `IRadioBackend` events
   (`ConnectionStateChanged`, `ErrorOccurred`) so non-Flex connection
   diagnostics get the same treatment.

10. **Hamlib version requirement.** First implementation pins Hamlib 4.7.x.
    Per `tmv71a-analysis-from-doug.md`, minimum is 4.6.4 due to the `\r`
    terminator fix. Pin 4.7.4+ for safety margin. Document this in the
    HamlibBackend manifest.

---

## 12. Summary — what changes in the codebase

**New files (eventual implementation):**

- `Radios/IRadioBackend.cs` — the interface
- `Radios/IRadioFacts.cs`, `Radios/RadioCapability.cs`, `Radios/SliceState.cs`, etc.
  — supporting types
- `Radios/IRadioBackendCatalog.cs` — registration / discovery surface
- `Radios/Backends/FlexLibBackend.cs` — wraps existing FlexBase
- `Radios/Backends/Hamlib/HamlibBackend.cs` — new
- `Radios/Backends/Hamlib/Hamlib.cs` — P/Invoke layer
- `Radios/Backends/Hamlib/HamlibCapsAdapter.cs` — `rig_get_caps` → capability set
- `Radios/Streams/IPanadapterStream.cs`, etc. — streaming surface interfaces
- `runtimes/win-x64/native/libhamlib-5.dll`, `runtimes/win-x86/native/libhamlib-5.dll`

**Modified files:**

- `Radios/AllRadios.cs` — implements `IRadioBackend`, retains existing API
  for backward compat (transition period)
- `Radios/FlexBase.cs` — minor tweaks to support the interface (mostly
  property naming / event signatures)
- `Radios/RadioSelection.cs` — partial migration to `IRadioBackendCatalog`
- `Radios/ConnectionProfiler.cs` — generalize to consume `IRadioBackend`
  events instead of FlexLib-specific events
- `JJFlexRadio.vbproj` — Hamlib DLL native asset registration
- `JJFlexWpf/...` — UI code that references `theRadio` migrates progressively
  to `currentBackend` typed `IRadioBackend`

**Estimated implementation scope:** 3-4 sprints minimum for the full
multi-backend transition. First implementation sprint is just "introduce
IRadioBackend, port FlexBase to it, no Hamlib yet" — scoped to validate the
abstraction without committing to Hamlib bringup.

---

## 13. Forward references

This document feeds:

- **Phase 5 (Per-radio config strategy)** — what's queryable from
  `IRadioBackend` vs what lives in `IPerRadioConfig`
- **Phase 6 (Audio routing)** — `IAudioRouter` is the sibling interface
- **Phase 7 (TS-2000 conformance scope)** — the conformance test list
  exercises `IRadioBackend` against a HamlibBackend connected to TS-2000
- **Phase 8 (Tester onboarding)** — connection-profile setup UX is built on
  the `ConnectionConfig` shape proposed here
- **Phase 9 (Architecture synthesis)** — this is the central proposal of the
  synthesis

---

## Document Status

**Phase 4 of 10 complete.** `IRadioBackend` shape, capability model,
slice abstraction, connection lifecycle, threading model, registration
mechanism, legacy bridge plan. Open design questions explicit; recommendations
present but not committed (commits happen at implementation time).

**Estimated read time:** 30-40 minutes for full review; 15 minutes for
sections 1, 3, 4, and 11 alone (the load-bearing decisions).

**Next deliverables:** Phase 5 (per-radio config strategy), Phase 6 (audio
routing), Phase 7 (TS-2000 conformance) — all consume the design here.
