using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace Radios;

/// <summary>
/// TX audio chain preset — save/load/share audio configurations.
/// Follows the FilterPresets.cs pattern for XML serialization and error handling.
/// </summary>
[XmlRoot("AudioChainPreset")]
public class AudioChainPreset
{
    /// <summary>
    /// Schema version this build writes (#50). 0 (the value an attribute-less
    /// pre-versioning file deserializes to) means "made before versioning" —
    /// which also means before the TX EQ and tuned-input fields existed, so
    /// <see cref="TxEqCaptured"/> is false on those and ApplyTo leaves the
    /// radio's EQ alone rather than zeroing it with defaults the file never
    /// held. Version 1 (2026-08-16): adds the version itself, the TX EQ
    /// block, and the tuned-for input fields. Version 2 (2026-09-01, #431):
    /// adds <see cref="TxEq32"/>, the ninth and lowest band. A version 1 file
    /// has no 32 Hz value at all, which deserialises to 0 — and 0 is a
    /// perfectly legal band level, so the absence is not visible in the
    /// number. <see cref="ApplyTo"/> therefore reads the radio's own 32 Hz
    /// and writes it back unchanged on a version 1 file, rather than flatten
    /// a band the file never had an opinion about.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>Written-by schema version. 0 = a pre-versioning file.
    /// Deliberately NOT defaulted to current: absence must stay detectable.</summary>
    [XmlAttribute("schemaVersion")]
    public int SchemaVersion { get; set; }

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

    // ── The tuned-for input (#51) ──
    // Mic gain, boost and bias act on the SELECTED input; a preset tuned on
    // the balanced input is simply a different animal on the hand mic. These
    // record what the preset was tuned against so loading can say so.

    /// <summary>Radio mic input this preset was tuned for (MIC, BAL, LINE,
    /// ACC, PC…). "" on files from before this was recorded.</summary>
    public string RadioMicInput { get; set; } = "";

    /// <summary>When the input was PC, the Windows capture device that was
    /// feeding it. Informational — a device name from another computer is a
    /// clue for the operator, not something to act on.</summary>
    public string PcInputDevice { get; set; } = "";

    // ── TX equalizer (#50 — the piece exports were missing) ──

    /// <summary>True when the TX EQ block below holds a real capture. False
    /// on pre-versioning files and on captures made before the radio had
    /// reported its EQ — ApplyTo then leaves the radio's EQ untouched,
    /// because "apply all zeros" and "the file never knew" must not read the
    /// same.</summary>
    public bool TxEqCaptured { get; set; }

    public bool TxEqEnabled { get; set; }

    /// <summary>The lowest band, added at schema version 2. Absent from
    /// version 1 files — see <see cref="CurrentSchemaVersion"/> for why that
    /// absence cannot be read off the value.</summary>
    public int TxEq32 { get; set; }

    public int TxEq63 { get; set; }
    public int TxEq125 { get; set; }
    public int TxEq250 { get; set; }
    public int TxEq500 { get; set; }
    public int TxEq1000 { get; set; }
    public int TxEq2000 { get; set; }
    public int TxEq4000 { get; set; }
    public int TxEq8000 { get; set; }

    public AudioChainPreset() { }

    public AudioChainPreset(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Apply this preset to the connected radio. Returns a spoken-ready note
    /// about anything that could NOT be applied faithfully ("" when the
    /// apply was complete): an EQ the file never captured, or an input
    /// mismatch the operator should hear about. Callers append it to their
    /// own announcement.
    /// </summary>
    public string ApplyTo(FlexBase rig) => ApplyTo(rig, applyEq: true);

    /// <summary>
    /// The same apply with the equaliser part optional. The guest-radio live
    /// path (#499) passes <paramref name="applyEq"/> false when the snapshot it
    /// took of the radio's own settings could not read the radio's EQ — a
    /// setting that cannot be put back must not be changed in the first place.
    /// </summary>
    public string ApplyTo(FlexBase rig, bool applyEq)
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

        var notes = new List<string>();

        if (TxEqCaptured && applyEq)
        {
            // A version 1 file carries eight bands. Its 32 Hz reads 0 because
            // the element was not there, not because anyone chose 0 — so send
            // the radio back its own current 32 Hz and leave that band alone.
            // Same doctrine as TxEqCaptured itself: "the file never knew" and
            // "the file said zero" must not act the same.
            int hz32 = SchemaVersion >= 2 ? TxEq32 : (rig.GetTxEq()?.Hz32 ?? 0);

            if (!rig.ApplyTxEq(new FlexBase.TxEqSettings
            {
                Enabled = TxEqEnabled,
                Hz32 = hz32,
                Hz63 = TxEq63,
                Hz125 = TxEq125,
                Hz250 = TxEq250,
                Hz500 = TxEq500,
                Hz1000 = TxEq1000,
                Hz2000 = TxEq2000,
                Hz4000 = TxEq4000,
                Hz8000 = TxEq8000,
            }))
            {
                notes.Add("The radio has not reported its TX equalizer yet, so the EQ part was not applied.");
            }
        }
        // No note for !TxEqCaptured on a load from the operator's own store —
        // every pre-existing preset lacks it and saying so on every load
        // would be noise. Import announces it once, at import time.

        if (!string.IsNullOrEmpty(RadioMicInput))
        {
            string current = rig.MicSource;
            if (!string.IsNullOrEmpty(current) &&
                !string.Equals(current, RadioMicInput, StringComparison.OrdinalIgnoreCase))
            {
                notes.Add($"It was tuned for the {RadioMicInput} input; the radio is on {current}.");
            }
        }

        return string.Join(" ", notes);
    }

    /// <summary>
    /// Capture current radio TX settings into a new preset.
    /// </summary>
    /// <param name="rig">the connected radio</param>
    /// <param name="name">preset name</param>
    /// <param name="pcInputDevice">the Windows capture device feeding the PC
    /// input, when the caller knows it; recorded only if the radio's mic
    /// input is PC-family</param>
    public static AudioChainPreset CaptureFrom(FlexBase rig, string name, string pcInputDevice = "")
    {
        var txEq = rig.GetTxEq();
        string micInput = rig.MicSource ?? "";
        bool pcSourced = micInput.StartsWith("PC", StringComparison.OrdinalIgnoreCase);

        return new AudioChainPreset
        {
            SchemaVersion = CurrentSchemaVersion,
            Name = name,
            MicGain = rig.MicGain,
            MicBoost = rig.MicBoost == FlexBase.OffOnValues.on,
            MicBias = rig.MicBias == FlexBase.OffOnValues.on,
            CompanderOn = rig.Compander == FlexBase.OffOnValues.on,
            CompanderLevel = rig.CompanderLevel,
            SpeechProcessorOn = rig.ProcessorOn == FlexBase.OffOnValues.on,
            SpeechProcessorLevel = (int)rig.ProcessorSetting,
            TxFilterLow = rig.TXFilterLow,
            TxFilterHigh = rig.TXFilterHigh,
            MonitorOn = rig.Monitor == FlexBase.OffOnValues.on,
            MonitorLevel = rig.SBMonitorLevel,
            MonitorPan = rig.SBMonitorPan,
            RadioMicInput = micInput,
            PcInputDevice = pcSourced ? (pcInputDevice ?? "") : "",
            TxEqCaptured = txEq != null,
            TxEqEnabled = txEq?.Enabled ?? false,
            TxEq32 = txEq?.Hz32 ?? 0,
            TxEq63 = txEq?.Hz63 ?? 0,
            TxEq125 = txEq?.Hz125 ?? 0,
            TxEq250 = txEq?.Hz250 ?? 0,
            TxEq500 = txEq?.Hz500 ?? 0,
            TxEq1000 = txEq?.Hz1000 ?? 0,
            TxEq2000 = txEq?.Hz2000 ?? 0,
            TxEq4000 = txEq?.Hz4000 ?? 0,
            TxEq8000 = txEq?.Hz8000 ?? 0,
        };
    }

    /// <summary>
    /// Load a single preset from file, reporting failure instead of masking it.
    /// This was Load(filePath), which answered a missing or corrupt file with a
    /// default-valued preset — a contract that sat unused until Import became
    /// its first caller, and Import is exactly where that contract is wrong: an
    /// operator handed a bad file must hear so, not receive a silently blank
    /// preset that then gets "imported" as if the file had been read. No caller
    /// ever depended on the forgiving shape, so it was changed rather than
    /// worked around. (The collection-level AudioChainPresets.Load keeps its
    /// fall-back-to-defaults contract on purpose — that one reads the app's own
    /// per-operator store, where defaults ARE the right answer for a fresh
    /// install.)
    /// </summary>
    public static bool TryLoad(string filePath, out AudioChainPreset preset)
        => TryLoad(filePath, out preset, out _);

    /// <summary>
    /// The three-argument shape adds what the file can tell us about itself
    /// (#50): <paramref name="fileNote"/> is a spoken-ready sentence when the
    /// file deserves comment — written by a newer schema than this build
    /// knows, or predating the TX EQ capture — and "" otherwise. Import
    /// speaks it once, at import time.
    /// </summary>
    public static bool TryLoad(string filePath, out AudioChainPreset preset, out string fileNote)
    {
        preset = new AudioChainPreset();
        fileNote = "";
        if (!File.Exists(filePath))
            return false;

        try
        {
            using var fs = File.OpenRead(filePath);
            var serializer = new XmlSerializer(typeof(AudioChainPreset));
            var loaded = (AudioChainPreset?)serializer.Deserialize(fs);
            if (loaded == null)
                return false;
            preset = loaded;

            if (loaded.SchemaVersion > CurrentSchemaVersion)
            {
                fileNote = "The file was made by a newer version of the app; "
                    + "anything this version does not understand was ignored.";
            }
            else if (!loaded.TxEqCaptured)
            {
                fileNote = "The file predates TX equalizer capture, so loading "
                    + "it will leave the radio's EQ as it is.";
            }
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"AudioChainPreset.TryLoad failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Save this preset to file. Returns whether the file actually landed —
    /// a false return must never be announced as an export.
    /// </summary>
    public bool Save(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using var fs = File.Create(filePath);
            var serializer = new XmlSerializer(typeof(AudioChainPreset));
            serializer.Serialize(fs, this);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"AudioChainPreset.Save failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Format for speech output.
    /// </summary>
    public string FormatForSpeech()
    {
        int width = TxFilterHigh - TxFilterLow;
        string widthStr = width >= 1000 ? $"{width / 1000.0:0.#}k" : $"{width} hertz";
        string forInput = string.IsNullOrEmpty(RadioMicInput) ? "" : $", tuned for {RadioMicInput}";
        return $"{Name}, mic gain {MicGain}, filter {widthStr}{forInput}";
    }
}

/// <summary>
/// Collection of audio chain presets, persisted per operator.
/// File: {operatorName}_audioPresets.xml
/// </summary>
[XmlRoot("AudioChainPresets")]
public class AudioChainPresets
{
    /// <summary>Store schema version (#50). Stamped on every save; 0 on
    /// files from before versioning existed.</summary>
    [XmlAttribute("schemaVersion")]
    public int SchemaVersion { get; set; }

    public List<AudioChainPreset> Presets { get; set; } = new();

    /// <summary>
    /// Built-in default presets.
    /// </summary>
    public static AudioChainPresets CreateDefaults()
    {
        return new AudioChainPresets
        {
            Presets = new List<AudioChainPreset>
            {
                new("Ragchew")
                {
                    MicGain = 50, TxFilterLow = 100, TxFilterHigh = 3100,
                    Description = "Wide, natural voice for casual contacts"
                },
                new("Contest SSB")
                {
                    MicGain = 60, CompanderOn = true, CompanderLevel = 70,
                    SpeechProcessorOn = true, SpeechProcessorLevel = 1,
                    TxFilterLow = 200, TxFilterHigh = 2900,
                    Description = "Punchy and narrow for pileups"
                },
                new("DX Pileup")
                {
                    MicGain = 65, CompanderOn = true, CompanderLevel = 80,
                    SpeechProcessorOn = true, SpeechProcessorLevel = 2,
                    TxFilterLow = 300, TxFilterHigh = 2700,
                    Description = "Maximum punch for DX work"
                }
            }
        };
    }

    public static AudioChainPresets Load(string configDirectory, string operatorName)
        => Load(configDirectory, operatorName, out _);

    /// <summary>
    /// Load the operator's preset store (#49). A missing file is a fresh
    /// install and the built-in defaults are the honest answer. A file that
    /// EXISTS but cannot be read is different: it is the operator's tuning,
    /// and silently answering with the three defaults is settings loss with
    /// no notification — the defect this overload exists to end. The
    /// unreadable file is moved aside (never overwritten by the next save),
    /// its new path comes back in <paramref name="corruptSidelinedPath"/>,
    /// and the caller owns telling the operator. Null when nothing was
    /// wrong.
    /// </summary>
    public static AudioChainPresets Load(string configDirectory, string operatorName,
        out string? corruptSidelinedPath)
    {
        corruptSidelinedPath = null;
        var filePath = GetFilePath(configDirectory, operatorName);

        if (!File.Exists(filePath))
            return CreateDefaults();

        try
        {
            using (var fs = File.OpenRead(filePath))
            {
                var serializer = new XmlSerializer(typeof(AudioChainPresets));
                var presets = (AudioChainPresets?)serializer.Deserialize(fs);
                if (presets != null)
                    return presets;
            }
            // Deserialized to nothing — same recovery as an exception.
            corruptSidelinedPath = SidelineCorruptFile(filePath);
            return CreateDefaults();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"AudioChainPresets.Load failed: {ex.Message}");
            corruptSidelinedPath = SidelineCorruptFile(filePath);
            return CreateDefaults();
        }
    }

    /// <summary>
    /// Move an unreadable preset file out of the store's path so the next
    /// save cannot destroy the evidence — the contents are the operator's
    /// tuning and may be hand-recoverable. Returns the file's new path, or
    /// its original path if even the move failed (still reported; the
    /// operator deserves the truth either way).
    /// </summary>
    private static string SidelineCorruptFile(string filePath)
    {
        string sidelined = filePath + $".unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Move(filePath, sidelined);
            return sidelined;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"AudioChainPresets: could not sideline corrupt file: {ex.Message}");
            return filePath;
        }
    }

    /// <summary>
    /// Save the store. Returns whether the file actually landed — callers
    /// announce saves, and a save that did not happen must never be
    /// announced as one.
    /// </summary>
    public bool Save(string configDirectory, string operatorName)
    {
        var filePath = GetFilePath(configDirectory, operatorName);

        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            SchemaVersion = AudioChainPreset.CurrentSchemaVersion;
            using var fs = File.Create(filePath);
            var serializer = new XmlSerializer(typeof(AudioChainPresets));
            serializer.Serialize(fs, this);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"AudioChainPresets.Save failed: {ex.Message}");
            return false;
        }
    }

    private static string GetFilePath(string configDirectory, string operatorName)
    {
        return Path.Combine(configDirectory, $"{operatorName}_audioPresets.xml");
    }
}
