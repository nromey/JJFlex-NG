using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace Radios;

// ── Transmit noise setups (Sprint 49 Track H, item F) ──
//
// THE RULED SHAPE (design part seven, "Storage, the ruled shape"; Noel
// 2026-10-31 08:47, and the 2026-11-07 07:52 ruling on a missing setup): a
// transmit noise setup is a NAMED record held PER RADIO, keyed by the radio's
// serial, beside the per-radio config. It carries both reducer slots (on,
// engine, each engine's strength and settings) and the gate's settings. A
// microphone setup points at one by name, per radio, through
// RadioTxSetup.NoiseSetupName; several microphones may point at one setup, so
// changing it changes it for all of them.
//
// Why per radio and not inside the microphone: the engines a computer runs
// for one radio's transmit chain are chosen against that radio's processing
// (its speech processor, its filters), and a setup tuned for the 8600 is not
// the 6300's. The microphone still decides WHICH setup, so a headset and a
// desk mic on one radio keep their own.
//
// Sharing between radios is deliberate and explicit (S10, 2026-11-18 08:28):
// CopyToRadio, and nothing automatic.

/// <summary>One reducer slot of a transmit noise setup: the receive slot's stored shape, for transmit.</summary>
public sealed class TxNoiseSlotSetup
{
    public bool Enabled { get; set; }

    /// <summary>The chosen engine's catalog id; empty means none chosen (the slot's own default applies).</summary>
    public string EngineId { get; set; } = "";

    /// <summary>Every engine's stored strength and settings, as the slot exports them. Direction is Transmit.</summary>
    public List<DenoiseEngineSettings> Engines { get; set; } = new();

    public TxNoiseSlotSetup Clone() => new()
    {
        Enabled = Enabled,
        EngineId = EngineId,
        Engines = Engines.Select(CloneSettings).ToList(),
    };

    internal static DenoiseEngineSettings CloneSettings(DenoiseEngineSettings s) => new()
    {
        EngineId = s.EngineId,
        Direction = s.Direction,
        Strength = s.Strength,
        Values = s.Values.Select(v => new DenoiseNamedValue { Name = v.Name, Number = v.Number, Choice = v.Choice }).ToList(),
    };
}

/// <summary>
/// The transmit noise gate's settings, as a setup carries them. The same
/// fields and defaults <see cref="TxConditioningSettings"/> has always carried
/// for the gate, so the migration moves them across one for one.
/// </summary>
public sealed class TxNoiseGateSetup
{
    public bool Enabled { get; set; }
    public bool AutoThreshold { get; set; } = true;
    public float ThresholdMarginDb { get; set; } = 8f;
    /// <summary>Only meaningful when AutoThreshold is false.</summary>
    public float ThresholdDb { get; set; } = JJPortaudio.TxNoiseGate.DefaultThresholdDb;
    public float AttackMs { get; set; } = 3f;
    public float HoldMs { get; set; } = 150f;
    public float ReleaseMs { get; set; } = 200f;
    public float RangeDb { get; set; } = 25f;

    // ── The downward expander (Sprint 49 Track I, #432) ──
    //
    // The gate became a downward expander; these fields carry what it gained.
    // The DEFAULTS here are a newly switched-on expander's (S7, ruled
    // 2026-11-23 11:29: 2:1), with the side-chain filter on and 3 ms of
    // look-ahead. A version 1 setup whose gate was ON keeps exactly its old
    // sound: it migrates to the gate setting with the filter off and no
    // look-ahead (TxNoiseSetup's Gate setter, the one place a loaded record
    // passes through). Turning the stage off and on never changes these.
    // Every field here is a scalar, so the member-wise Clone carries it.

    // Each of the expander's fields notes that it was assigned, so a record
    // read with no Version element can still tell a version 2 gate (which
    // carries them) from a version 1 gate (repair 1, N1). The initializers
    // write the fields, never the setters.
    private float _ratio = JJPortaudio.TxNoiseGate.DefaultRatioToOne;
    private bool _gateSetting, _sideChain = true;
    private float _scLow = JJPortaudio.TxNoiseGate.DefaultSideChainLowHz, _scHigh = JJPortaudio.TxNoiseGate.DefaultSideChainHighHz;
    private float _lookAhead = JJPortaudio.TxNoiseGate.DefaultLookAheadMs;

    /// <summary>Whether any of the expander's own fields was assigned (on a load: present in the file).</summary>
    [XmlIgnore]
    public bool ExpanderFieldsRead { get; private set; }

    /// <summary>The expander's ratio, n:1. Used when <see cref="GateSetting"/> is off.</summary>
    public float RatioToOne { get => _ratio; set { _ratio = value; ExpanderFieldsRead = true; } }

    /// <summary>The top setting, the gate: today's fixed-depth gate exactly. Explicit, never a large ratio.</summary>
    public bool GateSetting { get => _gateSetting; set { _gateSetting = value; ExpanderFieldsRead = true; } }

    /// <summary>The side-chain filter on what the detector hears (ruled 2026-11-23 11:35).</summary>
    public bool SideChainFilter { get => _sideChain; set { _sideChain = value; ExpanderFieldsRead = true; } }
    public float SideChainLowCutHz { get => _scLow; set { _scLow = value; ExpanderFieldsRead = true; } }
    public float SideChainHighCutHz { get => _scHigh; set { _scHigh = value; ExpanderFieldsRead = true; } }

    /// <summary>Look-ahead, ms (ruled 2026-11-23 20:56: adjustable, 3 ms to start).</summary>
    public float LookAheadMs { get => _lookAhead; set { _lookAhead = value; ExpanderFieldsRead = true; } }

    /// <summary>
    /// What a version 1 record's gate becomes (S7): one that was on keeps its
    /// sound exactly (the gate setting, no side-chain filter, no look-ahead);
    /// one that was off takes the new expander's defaults, which it already
    /// holds, so its first switch-on gets them.
    /// </summary>
    public void UpgradeFromVersion1()
    {
        if (!Enabled) return;
        GateSetting = true;
        SideChainFilter = false;
        LookAheadMs = 0f;
    }

    public TxNoiseGateSetup Clone() => (TxNoiseGateSetup)MemberwiseClone();
}

/// <summary>
/// One band of the parametric equaliser as a setup carries it (Sprint 49
/// Track I, #1240). The kind is stored as a word, not an enum, so a kind a
/// later build adds loads here as a band this build does not run, and is kept
/// on save, rather than failing the whole file.
/// </summary>
public sealed class ParametricEqBandSetup
{
    /// <summary>"peaking", "low_shelf", "high_shelf", "high_pass", "low_pass" or "notch".</summary>
    public string Kind { get; set; } = ParametricEqKinds.Peaking;
    public float FrequencyHz { get; set; } = 1000f;
    public float GainDb { get; set; }
    public float Q { get; set; } = 1f;

    public ParametricEqBandSetup Clone() => (ParametricEqBandSetup)MemberwiseClone();
}

/// <summary>The band kinds a setup may name, and their meaning to the equaliser.</summary>
public static class ParametricEqKinds
{
    public const string Peaking = "peaking", LowShelf = "low_shelf", HighShelf = "high_shelf",
        HighPass = "high_pass", LowPass = "low_pass", Notch = "notch";

    /// <summary>Every kind, in the order an editor offers them.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Peaking, LowShelf, HighShelf, HighPass, LowPass, Notch };

    /// <summary>The equaliser's kind for a stored word, or null for one this build does not know.</summary>
    public static JJPortaudio.BiquadKind? ToKind(string? kind) => (kind ?? "").Trim().ToLowerInvariant() switch
    {
        Peaking => JJPortaudio.BiquadKind.Peaking,
        LowShelf => JJPortaudio.BiquadKind.LowShelf,
        HighShelf => JJPortaudio.BiquadKind.HighShelf,
        HighPass => JJPortaudio.BiquadKind.HighPass,
        LowPass => JJPortaudio.BiquadKind.LowPass,
        Notch => JJPortaudio.BiquadKind.Notch,
        _ => null,
    };

    /// <summary>Whether a kind uses the band's gain (a pass or a notch does not).</summary>
    public static bool UsesGain(string? kind) => kind is Peaking or LowShelf or HighShelf;
}

/// <summary>
/// The parametric equaliser as a setup carries it (Sprint 49 Track I, #1240):
/// on or off, the bands in order, and an output gain. The same shape serves
/// the transmit setup and receive (ruled S2: one equaliser, both sides).
/// </summary>
public sealed class ParametricEqSetup
{
    public bool Enabled { get; set; }

    /// <summary>The preset the bands were last loaded from, by id, or empty. Informational: every field stays the operator's to change.</summary>
    public string PresetId { get; set; } = "";

    public float OutputGainDb { get; set; }

    public List<ParametricEqBandSetup> Bands { get; set; } = new();

    public ParametricEqSetup Clone() => new()
    {
        Enabled = Enabled,
        PresetId = PresetId,
        OutputGainDb = OutputGainDb,
        Bands = (Bands ?? new()).Where(b => b != null).Select(b => b.Clone()).ToList(),
    };

    /// <summary>
    /// The design the equaliser runs, or null when it is off. Bands of a kind
    /// this build does not know are left out of what runs (and kept in the
    /// setup); at most <see cref="JJPortaudio.ParametricEq.MaxBands"/> run.
    /// </summary>
    public JJPortaudio.ParametricEqDesign? ToDesign()
    {
        if (!Enabled) return null;
        var bands = new List<JJPortaudio.ParametricEqBand>();
        foreach (var b in Bands ?? new())
        {
            if (b == null || ParametricEqKinds.ToKind(b.Kind) is not JJPortaudio.BiquadKind kind) continue;
            bands.Add(new JJPortaudio.ParametricEqBand(kind, b.FrequencyHz,
                ParametricEqKinds.UsesGain(b.Kind) ? b.GainDb : 0, b.Q));
        }
        return new JJPortaudio.ParametricEqDesign(bands, OutputGainDb);
    }
}

/// <summary>The de-esser as a setup carries it (Sprint 49 Track I, item 4).</summary>
public sealed class TxDeEsserSetup
{
    public bool Enabled { get; set; }
    /// <summary>The band's centre the operator chose, Hz. Applied clamped below the transmit filter's edge.</summary>
    public float FrequencyHz { get; set; } = JJPortaudio.DeEsser.DefaultFrequencyHz;
    /// <summary>The most the band is turned down, dB.</summary>
    public float AmountDb { get; set; } = JJPortaudio.DeEsser.DefaultAmountDb;
    /// <summary>The band level it acts above, dB. Lower is more sensitive.</summary>
    public float SensitivityDb { get; set; } = JJPortaudio.DeEsser.DefaultThresholdDb;

    public TxDeEsserSetup Clone() => (TxDeEsserSetup)MemberwiseClone();
}

/// <summary>One band of the compressor's split-band mode as a setup carries it (Sprint 49 Track CL, phase 2).</summary>
public sealed class TxCompressorBandSetup
{
    /// <summary>dBFS of the band's own sample peak.</summary>
    public float ThresholdDb { get; set; } = JJPortaudio.TxCompressorSettings.DefaultThresholdDb;
    public float RatioToOne { get; set; } = 2f;
    public float AttackMs { get; set; } = JJPortaudio.TxCompressorSettings.DefaultAttackMs;
    public float ReleaseMs { get; set; } = JJPortaudio.TxCompressorSettings.DefaultReleaseMs;
    public float MakeupDb { get; set; }

    public TxCompressorBandSetup Clone() => (TxCompressorBandSetup)MemberwiseClone();

    public JJPortaudio.TxCompressorBand ToBand() => new()
    {
        ThresholdDb = ThresholdDb, RatioToOne = RatioToOne, AttackMs = AttackMs, ReleaseMs = ReleaseMs, MakeupDb = MakeupDb,
    };

    public static TxCompressorBandSetup From(JJPortaudio.TxCompressorBand b) => new()
    {
        ThresholdDb = b.ThresholdDb, RatioToOne = b.RatioToOne, AttackMs = b.AttackMs, ReleaseMs = b.ReleaseMs, MakeupDb = b.MakeupDb,
    };
}

/// <summary>The compressor's modes, stored as words so a mode a later build adds loads here, runs as the single band, and is kept on save.</summary>
public static class TxCompressorModes
{
    /// <summary>One band: the whole voice through one compressor.</summary>
    public const string Single = "single";
    /// <summary>Split band: three bands, each with its own compressor (Sprint 49 Track CL, phase 2).</summary>
    public const string Split = "split";

    /// <summary>Every mode this build runs, in the order a surface offers them.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Single, Split };

    /// <summary>The mode a stored word runs as: split when it says so, the single band for anything else.</summary>
    public static bool IsSplit(string? mode) => string.Equals((mode ?? "").Trim(), Split, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The transmit compressor stage as a setup carries it (Sprint 49 Track CL,
/// #1278): the compressor, its drive, its phase rotator and its split-band
/// mode. Off when absent from a file, so every setup saved before it existed
/// loads with it off. The defaults are a newly switched-on compressor's: plain
/// and moderate, no drive, the rotator off (the presets are where those come
/// on). Ranges are <see cref="JJPortaudio.TxCompressorSettings"/>'s; a value
/// out of range in a file is clamped where it is applied, and kept as written.
/// </summary>
public sealed class TxCompressorSetup
{
    public bool Enabled { get; set; }

    /// <summary>The preset the fields were last loaded from, by id, or empty. Informational: every field stays the operator's.</summary>
    public string PresetId { get; set; } = "";

    /// <summary>One of <see cref="TxCompressorModes"/>, as a word.</summary>
    public string CompressorMode { get; set; } = TxCompressorModes.Single;

    /// <summary>Where compression starts, dBFS of one channel's sample peak, read after the drive.</summary>
    public float ThresholdDb { get; set; } = JJPortaudio.TxCompressorSettings.DefaultThresholdDb;
    public float RatioToOne { get; set; } = JJPortaudio.TxCompressorSettings.DefaultRatio;
    public float AttackMs { get; set; } = JJPortaudio.TxCompressorSettings.DefaultAttackMs;
    public float ReleaseMs { get; set; } = JJPortaudio.TxCompressorSettings.DefaultReleaseMs;
    public float KneeDb { get; set; } = JJPortaudio.TxCompressorSettings.DefaultKneeDb;
    public float MakeupDb { get; set; } = JJPortaudio.TxCompressorSettings.DefaultMakeupDb;

    /// <summary>Gain before the compressor, dB (0 to 18).</summary>
    public float DriveDb { get; set; }

    /// <summary>The phase rotator's sections, 0 (off) to 6.</summary>
    public int RotatorStages { get; set; }

    public float CrossoverLowHz { get; set; } = JJPortaudio.TxCompressorSettings.DefaultCrossoverLowHz;
    public float CrossoverHighHz { get; set; } = JJPortaudio.TxCompressorSettings.DefaultCrossoverHighHz;

    public TxCompressorBandSetup LowBand { get; set; } = TxCompressorBandSetup.From(JJPortaudio.TxCompressorSettings.DefaultLow);
    public TxCompressorBandSetup MidBand { get; set; } = TxCompressorBandSetup.From(JJPortaudio.TxCompressorSettings.DefaultMid);
    public TxCompressorBandSetup HighBand { get; set; } = TxCompressorBandSetup.From(JJPortaudio.TxCompressorSettings.DefaultHigh);

    /// <summary>Band 0, 1 or 2: low, middle, high. Never null.</summary>
    public TxCompressorBandSetup Band(int index) => index <= 0 ? (LowBand ??= new()) : index == 1 ? (MidBand ??= new()) : (HighBand ??= new());

    public TxCompressorSetup Clone()
    {
        var copy = (TxCompressorSetup)MemberwiseClone();
        copy.LowBand = (LowBand ?? new()).Clone();
        copy.MidBand = (MidBand ?? new()).Clone();
        copy.HighBand = (HighBand ?? new()).Clone();
        return copy;
    }

    /// <summary>What the stage runs: these fields, each clamped to its range.</summary>
    public JJPortaudio.TxCompressorSettings ToSettings() => new()
    {
        Enabled = Enabled,
        ThresholdDb = ThresholdDb, RatioToOne = RatioToOne, AttackMs = AttackMs, ReleaseMs = ReleaseMs,
        KneeDb = KneeDb, MakeupDb = MakeupDb, DriveDb = DriveDb, RotatorStages = RotatorStages,
        SplitBand = TxCompressorModes.IsSplit(CompressorMode),
        CrossoverLowHz = CrossoverLowHz, CrossoverHighHz = CrossoverHighHz,
        Low = (LowBand ?? new()).ToBand(), Mid = (MidBand ?? new()).ToBand(), High = (HighBand ?? new()).ToBand(),
    };
}

/// <summary>
/// The transmit limiter as a setup carries it (Sprint 49 Track CL, #1278).
/// Off when absent from a file: a setup saved before the limiter existed
/// loads with it off, so nobody's transmit audio changes on upgrade (the
/// principle Noel ruled for saved gates, S7, applied by extension). A NEW
/// setup starts with it on, at full scale: <see cref="ForNewSetup"/>.
/// </summary>
public sealed class TxLimiterSetup
{
    /// <summary>
    /// Whether a new transmit setup starts with the limiter on (the brief's
    /// question 7 to Noel; its stated default, yes: at full scale it only
    /// catches samples that would have gone past full scale). One place to flip.
    /// </summary>
    public const bool NewSetupsStartOn = true;

    public bool Enabled { get; set; }

    /// <summary>The ceiling, dBFS of the sample peak (-12 to 0). 0 is full scale: audio within it is never touched.</summary>
    public float CeilingDb { get; set; } = JJPortaudio.TxLimiterSettings.DefaultCeilingDb;
    public float ReleaseMs { get; set; } = JJPortaudio.TxLimiterSettings.DefaultReleaseMs;

    /// <summary>Look-ahead, ms (0 to 5; Advanced). A switch: its length moves the stream in time.</summary>
    public float LookAheadMs { get; set; } = JJPortaudio.TxLimiterSettings.DefaultLookAheadMs;

    public TxLimiterSetup Clone() => (TxLimiterSetup)MemberwiseClone();

    /// <summary>The limiter a new setup starts with: at full scale, no look-ahead, on if <see cref="NewSetupsStartOn"/>.</summary>
    public static TxLimiterSetup ForNewSetup() => new() { Enabled = NewSetupsStartOn };

    public JJPortaudio.TxLimiterSettings ToSettings() => new()
    {
        Enabled = Enabled, CeilingDb = CeilingDb, ReleaseMs = ReleaseMs, LookAheadMs = LookAheadMs,
    };
}

/// <summary>
/// One named transmit noise setup: both reducer slots, spectral first then
/// neural, and the gate. Held per radio by <see cref="TxNoiseSetupStore"/>.
/// </summary>
public sealed class TxNoiseSetup
{
    /// <summary>
    /// 2 since Sprint 49 Track I: the gate became a downward expander and the
    /// equaliser, the de-esser and the stage order joined the record.
    /// </summary>
    public const int CurrentVersion = 2;

    /// <summary>Schema version of this record, for forward migration.</summary>
    public int Version
    {
        get => _version;
        set { _version = value; _versionRead = true; }
    }

    // Whether Version was assigned (by the serializer, on a load); the
    // initializer writes the field, so a record that never had the element
    // reads false. Only AfterLoad asks.
    private int _version = CurrentVersion;
    private bool _versionRead;

    public string Name { get; set; } = "";

    public TxNoiseSlotSetup Spectral { get; set; } = new();

    public TxNoiseSlotSetup Neural { get; set; } = new();

    /// <summary>
    /// The gate, a downward expander since version 2. A loaded version 1 gate
    /// is upgraded by <see cref="AfterLoad"/>, never by this setter.
    /// </summary>
    public TxNoiseGateSetup Gate { get; set; } = new();

    /// <summary>
    /// Bring a record just READ from a file up to this version (S7, Noel's
    /// ruling: saved gates keep exactly today's sound). Called by every home
    /// as it loads (the per-radio store, a preset file and the preset store,
    /// and <c>audioConfig.xml</c>), once the whole record has been read, so
    /// the decision does not depend on the order the serializer met the
    /// elements in (Sprint 49 Track I, repair 1, N1):
    /// <list type="bullet">
    /// <item>The version read is the <c>Version</c> element if the file had
    /// one; with none, it is 2 when the gate carries the expander's fields
    /// (only a version 2 build writes them) and 1 when it does not.</item>
    /// <item>Below 2: a gate that was on becomes the gate setting with no
    /// side-chain filter and no look-ahead, today's sound exactly; one that
    /// was off keeps the new defaults. The record is then version 2.</item>
    /// <item>2 or newer: untouched, a newer build's record included.</item>
    /// </list>
    /// Idempotent; a record built in code never needs it.
    /// </summary>
    public void AfterLoad()
    {
        if (_loaded) return;
        _loaded = true;
        Gate ??= new TxNoiseGateSetup();
        int read = _versionRead ? _version : (Gate.ExpanderFieldsRead ? CurrentVersion : 1);
        if (read < 2)
        {
            Gate.UpgradeFromVersion1();
            _version = CurrentVersion;
        }
        else if (!_versionRead) _version = CurrentVersion;
        // An older build's DEFAULT order (the default of its day: this build's
        // with the stages added since missing) is this build's default, said
        // in full (Sprint 49 Track CL). Nothing about how it runs changes; it
        // only stops two spellings of one order reading as different setups.
        // A newer build's record, and any order this build does not
        // understand, are left exactly as written.
        if (_version <= CurrentVersion && StageOrder != null
            && JJPortaudio.TxAudioConditioner.StageIds.RunsAsDefault(StageOrder))
            StageOrder = JJPortaudio.TxAudioConditioner.StageIds.DefaultOrder;
    }

    private bool _loaded;

    /// <summary>The parametric equaliser, after the expander (Sprint 49 Track I, #1240). Off in a version 1 record.</summary>
    public ParametricEqSetup Eq { get; set; } = new();

    /// <summary>The de-esser, after the equaliser (Sprint 49 Track I). Off in a version 1 record.</summary>
    public TxDeEsserSetup DeEsser { get; set; } = new();

    /// <summary>
    /// The stages' order, by stable id (<see cref="JJPortaudio.TxAudioConditioner.StageIds"/>),
    /// written with the default. This build runs only the default order: an
    /// order it does not understand is traced where it is applied, never
    /// refused, and kept exactly as loaded so a later build's order survives a
    /// save here. An array, not a list, because the serializer appends to an
    /// initialised list and would double it.
    /// </summary>
    public string[] StageOrder { get; set; } = JJPortaudio.TxAudioConditioner.StageIds.DefaultOrder;

    /// <summary>The compressor stage, after the de-esser (Sprint 49 Track CL, #1278). Off in every record saved before it existed.</summary>
    public TxCompressorSetup Compressor { get; set; } = new();

    /// <summary>The limiter, last in the chain (Sprint 49 Track CL, #1278). Off in every record saved before it existed.</summary>
    public TxLimiterSetup Limiter { get; set; } = new();

    /// <summary>A deep copy, so a caller holding a setup can never change the store's copy behind its back.</summary>
    public TxNoiseSetup Clone() => new()
    {
        Version = Version,
        Name = Name,
        Spectral = (Spectral ?? new()).Clone(),
        Neural = (Neural ?? new()).Clone(),
        Gate = (Gate ?? new()).Clone(),
        Eq = (Eq ?? new()).Clone(),
        DeEsser = (DeEsser ?? new()).Clone(),
        StageOrder = (string[])(StageOrder ?? JJPortaudio.TxAudioConditioner.StageIds.DefaultOrder).Clone(),
        Compressor = (Compressor ?? new()).Clone(),
        Limiter = (Limiter ?? new()).Clone(),
    };

    /// <summary>Whether <see cref="StageOrder"/> is the order this build runs: the default, or an older build's default (the same order with the stages added since missing).</summary>
    public bool RunsDefaultOrder =>
        JJPortaudio.TxAudioConditioner.StageIds.RunsAsDefault(StageOrder ?? JJPortaudio.TxAudioConditioner.StageIds.DefaultOrder);

    /// <summary>The same setup under another name.</summary>
    public TxNoiseSetup CloneAs(string name)
    {
        var copy = Clone();
        copy.Name = name;
        return copy;
    }

    /// <summary>
    /// True when the two setups would configure the chain identically, names
    /// aside. Compared through the serialized form, so a field added later is
    /// compared without anyone remembering to add it here.
    /// </summary>
    public bool SameContentAs(TxNoiseSetup other)
    {
        if (other == null) return false;
        return Serialize(CloneAs("")) == Serialize(other.CloneAs(""));
    }

    private static readonly XmlSerializer SetupSerializer = new(typeof(TxNoiseSetup));

    private static string Serialize(TxNoiseSetup s)
    {
        using var w = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        SetupSerializer.Serialize(w, s);
        return w.ToString();
    }
}

/// <summary>The file one radio's setups live in.</summary>
[XmlRoot("TxNoiseSetups")]
public sealed class TxNoiseSetupFile
{
    public const int CurrentSchemaVersion = 1;

    [XmlAttribute("schemaVersion")]
    public int SchemaVersion { get; set; }

    /// <summary>The radio these belong to, informational: the folder is the key.</summary>
    public string RadioId { get; set; } = "";

    public List<TxNoiseSetup> Setups { get; set; } = new();
}

/// <summary>How a store operation ended.</summary>
public enum TxNoiseSetupOutcome
{
    /// <summary>It happened and is on disk.</summary>
    Done,
    /// <summary>No setup of that name on that radio.</summary>
    NotFound,
    /// <summary>The target name is already used on that radio (rename, or a copy without replace).</summary>
    NameTaken,
    /// <summary>The name is empty or only spaces.</summary>
    BadName,
    /// <summary>The radio id is not a radio serial, or there is no settings root.</summary>
    BadRadio,
    /// <summary>The radio's setup file exists but could not be read; it was moved aside and nothing was changed.</summary>
    Unreadable,
    /// <summary>The change could not be written; nothing on disk changed.</summary>
    NotSaved,
}

/// <summary>The outcome of a store operation, and the name it ended up acting on.</summary>
public sealed record TxNoiseSetupResult(TxNoiseSetupOutcome Outcome, string Name = "", string? SidelinedPath = null)
{
    public bool Succeeded => Outcome == TxNoiseSetupOutcome.Done;
}

/// <summary>
/// One radio's transmit noise setups, in
/// <c>{settings root}\radios\{serial}\txNoiseSetups.xml</c>, beside that
/// radio's <c>config.xml</c>. Every operation reads the file, changes it and
/// writes it back under one lock, so two windows cannot interleave a rename
/// and a save. Names compare without regard to case, as the microphone
/// setups' names do. Every setup handed out is a copy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolved from <see cref="RadioConfig.AppDataRoot"/></b> (CLAUDE.md,
/// JJFLEX_CONFIG_DIR): a throwaway settings tree takes this store with it.
/// </para>
/// <para>
/// <b>An unreadable file is the operator's, so it is moved aside, never
/// overwritten</b>, the contract the microphone and preset stores keep: the
/// operation that found it changes nothing and reports
/// <see cref="TxNoiseSetupOutcome.Unreadable"/> with where the file went.
/// </para>
/// </remarks>
public sealed class TxNoiseSetupStore
{
    public const string FileName = "txNoiseSetups.xml";

    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly XmlSerializer FileSerializer = new(typeof(TxNoiseSetupFile));
    private readonly string _root;

    /// <param name="root">The settings root. The app passes <see cref="RadioConfig.AppDataRoot"/>; tests pass a scratch folder.</param>
    public TxNoiseSetupStore(string root)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
    }

    /// <summary>The store under the app's settings root, or null when that root cannot be resolved.</summary>
    public static TxNoiseSetupStore? Default()
    {
        string root = RadioConfig.AppDataRoot;
        return string.IsNullOrEmpty(root) ? null : new TxNoiseSetupStore(root);
    }

    /// <summary>Where a radio's setups live, or null when the id is not a radio serial.</summary>
    public string? PathFor(string radioId) =>
        RadioConfig.IsWellFormedRadioId(radioId)
            ? Path.Combine(_root, "radios", RadioConfig.SanitizeRadioId(radioId), FileName)
            : null;

    /// <summary>Every setup saved for this radio, copies, in the order they were first saved. Empty for an unknown radio or an unreadable file (which a read leaves in place).</summary>
    public IReadOnlyList<TxNoiseSetup> List(string radioId)
    {
        var path = PathFor(radioId);
        if (path == null) return Array.Empty<TxNoiseSetup>();
        lock (LockFor(path))
        {
            var file = Read(path, sidelineUnreadable: false, out _);
            return file?.Setups.Select(s => s.Clone()).ToList() ?? (IReadOnlyList<TxNoiseSetup>)Array.Empty<TxNoiseSetup>();
        }
    }

    /// <summary>The names of this radio's setups, in saved order.</summary>
    public IReadOnlyList<string> Names(string radioId) => List(radioId).Select(s => s.Name).ToList();

    /// <summary>A copy of the named setup, or null when this radio has none of that name.</summary>
    public TxNoiseSetup? Find(string radioId, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return List(radioId).FirstOrDefault(s => NameEquals(s.Name, name));
    }

    /// <summary>
    /// Save a setup under its own <see cref="TxNoiseSetup.Name"/>: replaces one
    /// of that name (keeping its place in the list), or adds it at the end. The
    /// stored copy is taken now; later changes to <paramref name="setup"/> do
    /// not reach the store.
    /// </summary>
    public TxNoiseSetupResult Save(string radioId, TxNoiseSetup setup)
    {
        if (setup == null) throw new ArgumentNullException(nameof(setup));
        string name = (setup.Name ?? "").Trim();
        if (name.Length == 0) return new(TxNoiseSetupOutcome.BadName);
        return Change(radioId, file =>
        {
            var copy = setup.CloneAs(name);
            int at = file.Setups.FindIndex(s => NameEquals(s.Name, name));
            if (at >= 0) file.Setups[at] = copy; else file.Setups.Add(copy);
            return new(TxNoiseSetupOutcome.Done, name);
        });
    }

    /// <summary>
    /// Rename a setup on this radio. Refuses a name already used by ANOTHER
    /// setup (a change of case alone is allowed). Microphone setups that point
    /// at the old name are NOT changed here: they live in the operator's
    /// microphone store, so the caller follows a rename with
    /// <see cref="MicrophoneProfileStore.RepointNoiseSetup"/> and saves that
    /// store, or they will read the setup as missing.
    /// </summary>
    public TxNoiseSetupResult Rename(string radioId, string from, string to)
    {
        string target = (to ?? "").Trim();
        if (target.Length == 0) return new(TxNoiseSetupOutcome.BadName);
        return Change(radioId, file =>
        {
            int at = file.Setups.FindIndex(s => NameEquals(s.Name, from));
            if (at < 0) return new(TxNoiseSetupOutcome.NotFound, from ?? "");
            int clash = file.Setups.FindIndex(s => NameEquals(s.Name, target));
            if (clash >= 0 && clash != at) return new(TxNoiseSetupOutcome.NameTaken, target);
            file.Setups[at] = file.Setups[at].CloneAs(target);
            return new(TxNoiseSetupOutcome.Done, target);
        });
    }

    /// <summary>Delete a setup from this radio. Microphones pointing at it then read it as missing, which applies nothing and is said once.</summary>
    public TxNoiseSetupResult Delete(string radioId, string name) =>
        Change(radioId, file =>
        {
            int at = file.Setups.FindIndex(s => NameEquals(s.Name, name));
            if (at < 0) return new(TxNoiseSetupOutcome.NotFound, name ?? "");
            file.Setups.RemoveAt(at);
            return new(TxNoiseSetupOutcome.Done, name ?? "");
        });

    /// <summary>
    /// "Copy this setup to another radio" (S10, ruled 2026-11-18 08:28): a
    /// copy under the same name in the other radio's store. Nothing automatic
    /// happens on either radio, and no microphone is re-pointed. Refuses with
    /// <see cref="TxNoiseSetupOutcome.NameTaken"/> when the other radio already
    /// has a setup of that name, unless <paramref name="replace"/>.
    /// </summary>
    public TxNoiseSetupResult CopyToRadio(string fromRadioId, string name, string toRadioId, bool replace = false)
    {
        if (PathFor(fromRadioId) == null || PathFor(toRadioId) == null) return new(TxNoiseSetupOutcome.BadRadio);
        var source = Find(fromRadioId, name);
        if (source == null) return new(TxNoiseSetupOutcome.NotFound, name ?? "");
        return Change(toRadioId, file =>
        {
            int at = file.Setups.FindIndex(s => NameEquals(s.Name, source.Name));
            if (at >= 0 && !replace) return new(TxNoiseSetupOutcome.NameTaken, source.Name);
            if (at >= 0) file.Setups[at] = source.Clone(); else file.Setups.Add(source.Clone());
            return new(TxNoiseSetupOutcome.Done, source.Name);
        });
    }

    /// <summary>The names compare as the microphone store's do: trimmed, without regard to case.</summary>
    public static bool NameEquals(string? a, string? b) =>
        string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private TxNoiseSetupResult Change(string radioId, Func<TxNoiseSetupFile, TxNoiseSetupResult> change)
    {
        var path = PathFor(radioId);
        if (path == null) return new(TxNoiseSetupOutcome.BadRadio);
        lock (LockFor(path))
        {
            var file = Read(path, sidelineUnreadable: true, out var sidelined);
            if (file == null) return new(TxNoiseSetupOutcome.Unreadable, "", sidelined);
            var result = change(file);
            if (!result.Succeeded) return result;
            file.RadioId = radioId;
            file.SchemaVersion = TxNoiseSetupFile.CurrentSchemaVersion;
            return Write(path, file) ? result : result with { Outcome = TxNoiseSetupOutcome.NotSaved };
        }
    }

    private static object LockFor(string path) => Locks.GetOrAdd(Path.GetFullPath(path), _ => new object());

    /// <summary>
    /// The file, a fresh one when there is none, or null when it exists and
    /// cannot be read. Only a CHANGE moves an unreadable file aside (it is
    /// about to write a new one there); a read leaves it where it is, so
    /// listing can never be what disturbs the operator's file.
    /// </summary>
    private static TxNoiseSetupFile? Read(string path, bool sidelineUnreadable, out string? sidelined)
    {
        sidelined = null;
        if (!File.Exists(path)) return new TxNoiseSetupFile();
        try
        {
            using var fs = File.OpenRead(path);
            if (FileSerializer.Deserialize(fs) is TxNoiseSetupFile file)
            {
                file.Setups ??= new();
                file.Setups.RemoveAll(s => s == null);
                foreach (var s in file.Setups)
                {
                    s.Name ??= "";
                    s.Spectral ??= new();
                    s.Neural ??= new();
                    s.Gate ??= new();
                    s.Eq ??= new();
                    s.Eq.Bands ??= new();
                    s.DeEsser ??= new();
                    s.StageOrder ??= JJPortaudio.TxAudioConditioner.StageIds.DefaultOrder;
                    s.Compressor ??= new();
                    s.Limiter ??= new();
                    s.AfterLoad(); // the version 1 upgrade, once the whole record is read (N1)
                }
                return file;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"TxNoiseSetupStore: {path} could not be read: {ex.Message}");
        }
        if (sidelineUnreadable) sidelined = Sideline(path);
        return null;
    }

    private static string Sideline(string path)
    {
        string aside = path + $".unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
        try { File.Move(path, aside); return aside; }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"TxNoiseSetupStore: could not move {path} aside: {ex.Message}");
            return path;
        }
    }

    /// <summary>Write through a temporary file and a replace, so a failed write leaves the previous file whole.</summary>
    private static bool Write(string path, TxNoiseSetupFile file)
    {
        string temp = path + ".saving";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var fs = File.Create(temp)) FileSerializer.Serialize(fs, file);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"TxNoiseSetupStore: {path} was not saved: {ex.Message}");
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            return false;
        }
    }
}

/// <summary>What moving a microphone's old cleanup settings into a named setup did.</summary>
public enum TxNoiseMigrationOutcome
{
    /// <summary>The microphone carries no cleanup settings; nothing to move.</summary>
    NothingToMove,
    /// <summary>The microphone already points at a setup on this radio; nothing moved again.</summary>
    AlreadyPointed,
    /// <summary>A setup was created from the microphone's settings and the microphone now points at it. Say so, once.</summary>
    Created,
    /// <summary>This radio already held a setup of that name with exactly these settings; the microphone now points at it.</summary>
    Reused,
    /// <summary>The radio id is not a serial; nothing was done.</summary>
    BadRadio,
    /// <summary>The setup could not be written (or the radio's setup file was unreadable); the microphone was left exactly as it was.</summary>
    NotSaved,
}

public sealed record TxNoiseMigrationResult(TxNoiseMigrationOutcome Outcome, string SetupName = "")
{
    /// <summary>True when the microphone profile was changed, so its store must be saved.</summary>
    public bool ProfileChanged => Outcome is TxNoiseMigrationOutcome.Created or TxNoiseMigrationOutcome.Reused;
}

/// <summary>
/// Moves a microphone setup's old per-microphone cleanup settings
/// (<see cref="MicCaptureSettings.Conditioning"/>) into a named transmit noise
/// setup for one radio (design part seven: "the app creates a setup named
/// after the microphone setup for the current radio, points at it, and says
/// so once").
/// </summary>
/// <remarks>
/// <para><b>It never loses a value.</b> Every field of
/// <see cref="TxConditioningSettings"/> lands in the setup (a test walks the
/// type by reflection, so a field added later fails it until it is carried),
/// and the microphone's <c>Conditioning</c> is LEFT IN PLACE, so an older build
/// reading the same file still finds it. The pointer, not the absence of
/// <c>Conditioning</c>, is what says a microphone has moved, so it moves once
/// per radio.</para>
/// <para><b>A name already used</b> by a different setup on that radio is not
/// overwritten: the new setup takes the first free "name 2", "name 3".</para>
/// </remarks>
public static class TxNoiseSetupMigration
{
    /// <summary>The engine the old settings always meant: transmit RNNoise was the only engine the chain ran.</summary>
    public const string LegacyEngineId = "rnnoise";

    /// <summary>The setup the old settings describe, under <paramref name="name"/>.</summary>
    public static TxNoiseSetup FromConditioning(string name, TxConditioningSettings c)
    {
        if (c == null) throw new ArgumentNullException(nameof(c));
        return new TxNoiseSetup
        {
            Name = name,
            Spectral = new TxNoiseSlotSetup(),
            Neural = new TxNoiseSlotSetup
            {
                Enabled = c.NrEnabled,
                EngineId = LegacyEngineId,
                Engines = new List<DenoiseEngineSettings>
                {
                    new() { EngineId = LegacyEngineId, Direction = DenoiseDirection.Transmit, Strength = c.NrStrength },
                },
            },
            Gate = new TxNoiseGateSetup
            {
                Enabled = c.GateEnabled,
                AutoThreshold = c.AutoThreshold,
                ThresholdMarginDb = c.ThresholdMarginDb,
                ThresholdDb = c.GateThresholdDb,
                AttackMs = c.GateAttackMs,
                HoldMs = c.GateHoldMs,
                ReleaseMs = c.GateReleaseMs,
                RangeDb = c.GateRangeDb,
            },
        }.WithOldGateSound();
    }

    /// <summary>
    /// The old per-microphone settings knew only the gate, so a gate that was
    /// on there keeps exactly its sound here too (S7): the same upgrade a
    /// version 1 setup gets.
    /// </summary>
    private static TxNoiseSetup WithOldGateSound(this TxNoiseSetup s)
    {
        s.Gate.UpgradeFromVersion1();
        return s;
    }

    /// <summary>
    /// The old settings shape for a setup: what the neural slot's RNNoise and
    /// the gate say. For the microphone store's dual-write and for older code
    /// that still reads <see cref="TxConditioningSettings"/>; a setup that runs
    /// any other engine reads as NR on with that engine's strength unknown, so
    /// RNNoise's stored strength is used.
    /// </summary>
    public static TxConditioningSettings ToConditioning(TxNoiseSetup s)
    {
        if (s == null) throw new ArgumentNullException(nameof(s));
        var neural = s.Neural ?? new();
        var gate = s.Gate ?? new();
        var rnn = neural.Engines.LastOrDefault(e => e.EngineId == LegacyEngineId);
        return new TxConditioningSettings
        {
            NrEnabled = neural.Enabled,
            NrStrength = rnn != null && float.IsFinite(rnn.Strength) ? rnn.Strength
                : NoiseFloor.DefaultStrength(LegacyEngineId, DenoiseDirection.Transmit),
            GateEnabled = gate.Enabled,
            AutoThreshold = gate.AutoThreshold,
            ThresholdMarginDb = gate.ThresholdMarginDb,
            GateThresholdDb = gate.ThresholdDb,
            GateAttackMs = gate.AttackMs,
            GateHoldMs = gate.HoldMs,
            GateReleaseMs = gate.ReleaseMs,
            GateRangeDb = gate.RangeDb,
        };
    }

    /// <summary>
    /// Move <paramref name="profile"/>'s cleanup settings into a setup on
    /// <paramref name="radioId"/> and point the microphone at it. Changes the
    /// profile in memory only; when <see cref="TxNoiseMigrationResult.ProfileChanged"/>
    /// the caller saves the microphone store. The setup is written first, so a
    /// failed write leaves the microphone pointing at nothing new.
    /// </summary>
    public static TxNoiseMigrationResult Migrate(MicrophoneProfile profile, string radioId, string radioModel,
        TxNoiseSetupStore store)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (store == null) throw new ArgumentNullException(nameof(store));
        if (store.PathFor(radioId) == null) return new(TxNoiseMigrationOutcome.BadRadio);
        var conditioning = profile.Capture?.Conditioning;
        if (conditioning == null) return new(TxNoiseMigrationOutcome.NothingToMove);
        var binding = profile.FindSetupFor(radioId);
        if (!string.IsNullOrWhiteSpace(binding?.NoiseSetupName))
            return new(TxNoiseMigrationOutcome.AlreadyPointed, binding!.NoiseSetupName);

        string baseName = string.IsNullOrWhiteSpace(profile.Name) ? "Microphone" : profile.Name.Trim();
        var wanted = FromConditioning(baseName, conditioning);
        var existing = store.List(radioId);
        string name = baseName;
        var outcome = TxNoiseMigrationOutcome.Created;
        for (int n = 2; ; n++)
        {
            var same = existing.FirstOrDefault(s => TxNoiseSetupStore.NameEquals(s.Name, name));
            if (same == null) break;
            if (same.SameContentAs(wanted)) { outcome = TxNoiseMigrationOutcome.Reused; name = same.Name; break; }
            name = baseName + " " + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (outcome == TxNoiseMigrationOutcome.Created)
        {
            var saved = store.Save(radioId, wanted.CloneAs(name));
            if (!saved.Succeeded) return new(TxNoiseMigrationOutcome.NotSaved);
        }

        if (binding == null)
        {
            // A microphone with no radio-side binding for this radio still
            // needs somewhere to hold the pointer. An empty reference means
            // "no radio mic profile": ApplyRadioHalf reads it as no binding
            // and leaves the radio alone, as it always did.
            binding = new RadioProfileReference { RadioId = radioId, RadioModel = radioModel ?? "" };
            profile.RadioSetups.Add(binding);
        }
        binding.NoiseSetupName = name;
        return new(outcome, name);
    }
}

/// <summary>A named starting shape for the parametric equaliser (Sprint 49 Track I; ruled 2026-11-23 11:38: "easy to use presets for sure").</summary>
/// <param name="Id">Stable id, saved in <see cref="ParametricEqSetup.PresetId"/>.</param>
/// <param name="NameKey">The lexicon key of its spoken name (DRAFT PROSE until Codex words it).</param>
/// <param name="ForTransmit">Offered on the transmit side.</param>
/// <param name="ForReceive">Offered on the receive side.</param>
public sealed record ParametricEqPreset(string Id, string NameKey, bool ForTransmit, bool ForReceive, float OutputGainDb,
    IReadOnlyList<ParametricEqBandSetup> Bands);

/// <summary>
/// The equaliser's presets: DATA, not code paths, so adding one later is a
/// line here. A preset is a list of bands loaded into the editor; choosing one
/// never hides a parameter, and every field is the operator's afterwards. The
/// set and the reason for each is Track I's proposal, in its report, for Noel.
/// </summary>
public static class ParametricEqPresets
{
    private static ParametricEqBandSetup B(string kind, float hz, float db, float q) =>
        new() { Kind = kind, FrequencyHz = hz, GainDb = db, Q = q };

    /// <summary>Every preset, transmit ones first.</summary>
    public static IReadOnlyList<ParametricEqPreset> All { get; } = new[]
    {
        // Flat: no bands; the honest starting point and the way back.
        new ParametricEqPreset("flat", "audio.eq.preset.flat", true, true, 0, Array.Empty<ParametricEqBandSetup>()),
        // A gentle presence lift for SSB: +3 dB around 2.5 kHz, where speech
        // intelligibility lives inside a 2.7 to 3 kHz passband, with the
        // sub-voice rumble rolled off below 100 Hz so the lift is not spent on it.
        new ParametricEqPreset("presence", "audio.eq.preset.presence", true, false, 0, new[]
        {
            B(ParametricEqKinds.HighPass, 100, 0, 0.7071f),
            B(ParametricEqKinds.Peaking, 2500, 3, 1.0f),
        }),
        // A boomy room or a close microphone: cut below 150 Hz and take 3 dB
        // out of the 250 Hz "mud", which a proximity effect piles up.
        new ParametricEqPreset("low_cut", "audio.eq.preset.low_cut", true, false, 0, new[]
        {
            B(ParametricEqKinds.HighPass, 150, 0, 0.7071f),
            B(ParametricEqKinds.Peaking, 250, -3, 1.2f),
        }),
        // A DX contour: lean lows, a dip in the 400 Hz chest tone and a firm
        // lift at 2.2 kHz, so the voice cuts through a pileup on the same power.
        new ParametricEqPreset("dx", "audio.eq.preset.dx", true, false, 0, new[]
        {
            B(ParametricEqKinds.HighPass, 200, 0, 0.7071f),
            B(ParametricEqKinds.Peaking, 400, -2, 1.0f),
            B(ParametricEqKinds.Peaking, 2200, 4, 0.9f),
        }),
        // Receive: soften band hiss above 2.5 kHz without dulling the voice below it.
        new ParametricEqPreset("hiss_cut", "audio.eq.preset.hiss_cut", false, true, 0, new[]
        {
            B(ParametricEqKinds.HighShelf, 2500, -6, 0.7071f),
        }),
        // Receive: take out hum and rumble below the voice band.
        new ParametricEqPreset("rumble_cut", "audio.eq.preset.rumble_cut", false, true, 0, new[]
        {
            B(ParametricEqKinds.HighPass, 180, 0, 0.7071f),
        }),
    };

    /// <summary>The preset with this id, or null.</summary>
    public static ParametricEqPreset? Find(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, (id ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The presets offered on one side.</summary>
    public static IReadOnlyList<ParametricEqPreset> For(bool transmit) =>
        All.Where(p => transmit ? p.ForTransmit : p.ForReceive).ToList();
}

/// <summary>A named starting point for the compressor stage (Sprint 49 Track CL; Noel 2026-11-23 20:48: "presets that one could start with").</summary>
/// <param name="Id">Stable id, saved in <see cref="TxCompressorSetup.PresetId"/>.</param>
/// <param name="NameKey">The lexicon key of its spoken name (DRAFT PROSE until Codex words it).</param>
/// <param name="Settings">Every field the preset fills. Its on or off is never taken: loading a preset leaves the switch as it was.</param>
public sealed record TxCompressorPreset(string Id, string NameKey, TxCompressorSetup Settings);

/// <summary>
/// The compressor's presets: DATA, so adding one is a line here. Loading one
/// fills every field; every field stays the operator's afterwards. The set and
/// the reason for each is Track CL's proposal, in its report, for Noel.
/// </summary>
/// <remarks>
/// <b>How the makeup in each was chosen:</b> so that a voice peaking at
/// -6 dBFS at the microphone leaves the compressor peaking near -6 dBFS once
/// it has settled. The peak stays where it was and the quieter parts come up,
/// which is "same peak, more average power". Attack transients pass the
/// compressor before it settles; the limiter after it is what holds those.
/// </remarks>
public static class TxCompressorPresets
{
    /// <summary>Every preset, in the order a surface offers them.</summary>
    public static IReadOnlyList<TxCompressorPreset> All { get; } = new[]
    {
        // Plain: the stage's own defaults, and the way back. 3 to 1 from
        // -18 dBFS, a 6 dB knee, 6 dB of makeup, no drive, no rotator.
        new TxCompressorPreset("plain", "audio.compressor.preset.plain", new TxCompressorSetup()),
        // Ragchew: gentle. 2.5 to 1 with a wide knee so it eases in, slower
        // times so it does not pump on pauses; about 7 dB of reduction on a
        // -6 dBFS peak, given back as makeup.
        new TxCompressorPreset("ragchew", "audio.compressor.preset.ragchew", new TxCompressorSetup
        {
            ThresholdDb = -18f, RatioToOne = 2.5f, AttackMs = 10f, ReleaseMs = 200f, KneeDb = 10f, MakeupDb = 7f,
        }),
        // DX and contest: the rotator at the broadcast four sections evens the
        // voice's lopsided peaks, 6 dB of drive pushes more of the voice over
        // the threshold, 5 to 1 with fast times holds it there. About 16 dB of
        // reduction on a -6 dBFS peak after the drive; 10 dB of makeup.
        new TxCompressorPreset("dx", "audio.compressor.preset.dx", new TxCompressorSetup
        {
            ThresholdDb = -20f, RatioToOne = 5f, AttackMs = 2f, ReleaseMs = 80f, KneeDb = 6f, MakeupDb = 10f,
            DriveDb = 6f, RotatorStages = JJPortaudio.PhaseRotator.BroadcastStages,
        }),
        // Level only: for consistency, not punch. A low threshold with a low
        // ratio and slow times rides the level as the operator leans in and
        // out, and leaves the voice's own dynamics alone inside each word.
        new TxCompressorPreset("level", "audio.compressor.preset.level", new TxCompressorSetup
        {
            ThresholdDb = -24f, RatioToOne = 2f, AttackMs = 30f, ReleaseMs = 800f, KneeDb = 12f, MakeupDb = 9f,
        }),
        // Bass control (split band, phase 2): the low band held firmly so a
        // boomy voice or a close microphone cannot swamp the rest, the middle
        // and high bands compressed gently. Each band's makeup gives back what
        // its own curve takes from a -6 dBFS peak in that band, less a little
        // in the low band, which is the point.
        new TxCompressorPreset("bass_control", "audio.compressor.preset.bass_control", new TxCompressorSetup
        {
            CompressorMode = TxCompressorModes.Split, KneeDb = 6f, MakeupDb = 0f,
            CrossoverLowHz = 300f, CrossoverHighHz = 1800f,
            LowBand = new TxCompressorBandSetup { ThresholdDb = -24f, RatioToOne = 4f, AttackMs = 10f, ReleaseMs = 200f, MakeupDb = 9f },
            MidBand = new TxCompressorBandSetup { ThresholdDb = -18f, RatioToOne = 2.5f, AttackMs = 5f, ReleaseMs = 150f, MakeupDb = 7f },
            HighBand = new TxCompressorBandSetup { ThresholdDb = -20f, RatioToOne = 2.5f, AttackMs = 2f, ReleaseMs = 100f, MakeupDb = 8f },
        }),
    };

    /// <summary>The preset with this id, or null.</summary>
    public static TxCompressorPreset? Find(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, (id ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>A preset's fields as a setup, with this on or off and the preset's id noted.</summary>
    public static TxCompressorSetup Load(TxCompressorPreset preset, bool enabled)
    {
        var copy = preset.Settings.Clone();
        copy.Enabled = enabled;
        copy.PresetId = preset.Id;
        return copy;
    }
}
