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

    public AudioChainPreset() { }

    public AudioChainPreset(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Apply this preset to the connected radio.
    /// </summary>
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

    /// <summary>
    /// Capture current radio TX settings into a new preset.
    /// </summary>
    public static AudioChainPreset CaptureFrom(FlexBase rig, string name)
    {
        return new AudioChainPreset
        {
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
            MonitorPan = rig.SBMonitorPan
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
    {
        preset = new AudioChainPreset();
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
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"AudioChainPreset.TryLoad failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Save this preset to file.
    /// </summary>
    public void Save(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using var fs = File.Create(filePath);
            var serializer = new XmlSerializer(typeof(AudioChainPreset));
            serializer.Serialize(fs, this);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"AudioChainPreset.Save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Format for speech output.
    /// </summary>
    public string FormatForSpeech()
    {
        int width = TxFilterHigh - TxFilterLow;
        string widthStr = width >= 1000 ? $"{width / 1000.0:0.#}k" : $"{width} hertz";
        return $"{Name}, mic gain {MicGain}, filter {widthStr}";
    }
}

/// <summary>
/// Collection of audio chain presets, persisted per operator.
/// File: {operatorName}_audioPresets.xml
/// </summary>
[XmlRoot("AudioChainPresets")]
public class AudioChainPresets
{
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
    {
        var filePath = GetFilePath(configDirectory, operatorName);

        if (!File.Exists(filePath))
            return CreateDefaults();

        try
        {
            using var fs = File.OpenRead(filePath);
            var serializer = new XmlSerializer(typeof(AudioChainPresets));
            var presets = (AudioChainPresets?)serializer.Deserialize(fs);
            return presets ?? CreateDefaults();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"AudioChainPresets.Load failed: {ex.Message}");
            return CreateDefaults();
        }
    }

    public void Save(string configDirectory, string operatorName)
    {
        var filePath = GetFilePath(configDirectory, operatorName);

        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using var fs = File.Create(filePath);
            var serializer = new XmlSerializer(typeof(AudioChainPresets));
            serializer.Serialize(fs, this);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"AudioChainPresets.Save failed: {ex.Message}");
        }
    }

    private static string GetFilePath(string configDirectory, string operatorName)
    {
        return Path.Combine(configDirectory, $"{operatorName}_audioPresets.xml");
    }
}
