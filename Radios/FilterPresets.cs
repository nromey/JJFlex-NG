using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace Radios;

/// <summary>
/// A single filter preset: a named low/high pair for a specific mode.
/// </summary>
public class FilterPreset
{
    public string Name { get; set; } = "";
    public int Low { get; set; }
    public int High { get; set; }

    /// <summary>
    /// Bandwidth in Hz (computed).
    /// </summary>
    [XmlIgnore]
    public int Width => High - Low;

    public FilterPreset() { }

    public FilterPreset(string name, int low, int high)
    {
        Name = name;
        Low = low;
        High = high;
    }

    /// <summary>
    /// What every route that announces this preset says: its name once, then
    /// the filter it sets for <paramref name="mode"/>, in the readout form
    /// (#747; Noel, 2026-11-06, J15: "every Chatty filter readout"). At
    /// Chatty "Wide, filter 100 to 2800, width 2700 hertz"; at Terse "Wide,
    /// 2.7 k". DRAFT. The edges are the ones the preset puts on the radio,
    /// mirrored for a lower-sideband mode as <see cref="FilterPresets.MirrorForMode"/>
    /// does.
    /// </summary>
    /// <remarks>
    /// Until round four of the 2026-11-06 junk box each route wrapped a
    /// fragment that already carried the name, so the name was said twice
    /// and no edges were said at all: "Wide, 2700 hertz Wide" (Codex's
    /// re-verify, F1). Alt with a bracket, the Filter menu's preset items
    /// and the active-preset status all say this now.
    /// </remarks>
    public string Announcement(string mode)
        => Announcement(mode, ScreenReaderOutput.CurrentVerbosity);

    /// <summary>The announcement at the given verbosity; see <see cref="Announcement(string)"/>.</summary>
    public string Announcement(string mode, VerbosityLevel level)
    {
        var (low, high) = FilterPresets.MirrorForMode(mode ?? "USB", Low, High);
        return Said(low, high, atLimit: false, level);
    }

    /// <summary>The name once, then a filter readout of these edges; ", at limit" when the mode held it back.</summary>
    private string Said(int low, int high, bool atLimit, VerbosityLevel level)
        => Lexicon.Get("settings.filter.preset_said", level, ("name", Name), ("low", low), ("high", high),
            ("width", HertzSpeech.ReadoutWidth(high - low, HertzUnitStyle.KiloLetter, level)),
            ("limit", atLimit ? Lexicon.Get("settings.filter.suffix_at_limit") : ""));

    /// <summary>
    /// What selecting this preset says, from what the filter BECAME (round
    /// five of the 2026-11-06 junk box, Codex B2). <paramref name="applied"/>
    /// is read on the command loop right after FlexLib's
    /// <c>Slice.UpdateFilter</c> has run, so its edges are FlexLib's own
    /// result: FM and NFM refuse a filter change outright, and every other
    /// mode clamps to its own range, by rules this code never copies.
    /// <list type="bullet">
    /// <item>Taken as asked: the announcement, "Wide, filter 100 to 2800,
    /// width 2700 hertz".</item>
    /// <item>Held back by the mode: the filter it became, marked ", at
    /// limit" as the bracket keys mark it.</item>
    /// <item>Not changed at all: "Narrow, filter unchanged at -6000 to 6000,
    /// width 12000 hertz", the filter as it stands.</item>
    /// </list>
    /// DRAFT wording.
    /// </summary>
    public string SayApplied(FilterApplied applied, VerbosityLevel level)
    {
        if (applied.Exact) return Said(applied.Low, applied.High, atLimit: false, level);
        if (applied.Unchanged)
            return Lexicon.Get("settings.filter.preset_unchanged", level, ("name", Name), ("low", applied.Low), ("high", applied.High),
                ("width", HertzSpeech.ReadoutWidth(applied.High - applied.Low, HertzUnitStyle.KiloLetter, level)));
        return Said(applied.Low, applied.High, atLimit: true, level);
    }

    /// <summary>
    /// The active preset as Speak Status and the Status window give it:
    /// the preset's NAME, then the filter that IS on the radio,
    /// <paramref name="liveLow"/> to <paramref name="liveHigh"/>: "preset
    /// Wide, filter 110 to 2790, width 2680 hertz" at Chatty, "preset Wide,
    /// 2.68 k" at Terse. DRAFT.
    /// </summary>
    /// <remarks>
    /// A preset is recognised within 20 Hz an edge
    /// (<see cref="FilterPresets.FindActivePreset"/>), so the saved edges
    /// may not be the current ones. This said the saved edges as if they
    /// were current until round five (Codex B1): "filter 100 to 2850" for a
    /// filter at 110 to 2840.
    /// </remarks>
    public string StatusSentence(int liveLow, int liveHigh, VerbosityLevel level)
        => Lexicon.Get("settings.filter.active_preset_status",
            ("description", Said(liveLow, liveHigh, atLimit: false, level)));

    /// <summary>
    /// The preset as a menu item shows it, which is not on the verbosity
    /// dial: "Wide (2.7 k)", the never-rounding kilo form at and above 1 kHz.
    /// It was "Wide (2.7 k Wide)", the name twice, until round five (Codex F1).
    /// </summary>
    public string FormatForDisplay()
        => $"{Name} ({HertzSpeech.DisplayedWidth(Width, HertzUnitStyle.KiloLetter)})";
}

/// <summary>
/// A receive filter request, as the slice holds it right after FlexLib's
/// <c>Slice.UpdateFilter</c> ran on the command loop: the mode, the edges
/// asked for, the edges before, and the edges now (round five of the
/// 2026-11-06 junk box, Codex B2).
/// </summary>
public readonly record struct FilterApplied(string Mode, int RequestedLow, int RequestedHigh, int BeforeLow, int BeforeHigh, int Low, int High)
{
    /// <summary>The filter is what was asked for.</summary>
    public bool Exact => Low == RequestedLow && High == RequestedHigh;

    /// <summary>
    /// The filter is not what was asked for and is what it was before:
    /// refused, as FM and NFM refuse, or held back to exactly where it
    /// already was. Either way the filter did not change, which is the one
    /// thing to say.
    /// </summary>
    public bool Unchanged => !Exact && Low == BeforeLow && High == BeforeHigh;
}

/// <summary>
/// Per-mode filter preset collection with defaults for SSB, CW, and DIGI modes.
/// XML serialized per-operator as {OperatorName}_filterPresets.xml.
/// </summary>
[XmlRoot("FilterPresets")]
public class FilterPresets
{
    /// <summary>
    /// All presets keyed by normalized mode name (USB, LSB, CW, DIGU, DIGL, etc.).
    /// </summary>
    public List<ModePresets> Modes { get; set; } = new();

    /// <summary>
    /// Get presets for a mode, falling back to defaults if none saved.
    /// Maps mode variants to base presets (e.g. LSB → SSB defaults, DIGL → DIGI defaults).
    /// </summary>
    public List<FilterPreset> GetPresetsForMode(string mode)
    {
        string key = NormalizeMode(mode);
        var match = Modes.FirstOrDefault(m =>
            string.Equals(m.Mode, key, StringComparison.OrdinalIgnoreCase));
        if (match != null && match.Presets.Count > 0)
            return match.Presets;
        return GetDefaults(key);
    }

    /// <summary>
    /// Mirror filter values for lower-sideband modes.
    /// SSB presets store USB-positive values (100, 1900).
    /// LSB/DIGL need negated+swapped values (-1900, -100).
    /// </summary>
    public static (int low, int high) MirrorForMode(string mode, int low, int high)
    {
        string norm = mode?.ToUpperInvariant() ?? "USB";
        if (norm == "LSB" || norm == "DIGL")
            return (-high, -low);
        return (low, high);
    }

    /// <summary>
    /// Find which preset index (0-based) best matches the current filter, or -1 if none.
    /// Match tolerance: within 20 Hz on each edge.
    /// Mirrors current radio values to match stored preset convention (USB-positive).
    /// </summary>
    /// <summary>
    /// The active-preset status for a live filter: the recognised preset's
    /// name with the LIVE edges and width (Codex B1), or null when no preset
    /// is within 20 Hz an edge. One reading of the filter feeds both the
    /// recognition and the numbers.
    /// </summary>
    public string? ActiveStatus(string mode, int liveLow, int liveHigh, VerbosityLevel level)
    {
        int idx = FindActivePreset(mode, liveLow, liveHigh);
        return idx < 0 ? null : GetPresetsForMode(mode)[idx].StatusSentence(liveLow, liveHigh, level);
    }

    public int FindActivePreset(string mode, int low, int high)
    {
        // Mirror current radio values so LSB (-1900, -100) compares as (100, 1900)
        var (compLow, compHigh) = MirrorForMode(mode, low, high);
        var presets = GetPresetsForMode(mode);
        for (int i = 0; i < presets.Count; i++)
        {
            if (Math.Abs(presets[i].Low - compLow) <= 20 &&
                Math.Abs(presets[i].High - compHigh) <= 20)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Cycle to the next or previous preset for the current mode.
    /// Returns the preset, or null if no presets are available.
    /// </summary>
    public FilterPreset? CyclePreset(string mode, int currentLow, int currentHigh, int direction)
    {
        var presets = GetPresetsForMode(mode);
        if (presets.Count == 0) return null;

        int active = FindActivePreset(mode, currentLow, currentHigh);
        int next;
        if (active < 0)
        {
            // Not on a preset — find nearest by bandwidth
            int width = currentHigh - currentLow;
            next = 0;
            int closest = int.MaxValue;
            for (int i = 0; i < presets.Count; i++)
            {
                int diff = Math.Abs(presets[i].Width - width);
                if (diff < closest) { closest = diff; next = i; }
            }
            // Move one step in the requested direction from nearest
            if (direction > 0 && next < presets.Count - 1) next++;
            else if (direction < 0 && next > 0) next--;
        }
        else
        {
            next = active + direction;
            if (next < 0) next = 0;
            if (next >= presets.Count) next = presets.Count - 1;
            if (next == active) return null; // already at boundary
        }

        return presets[next];
    }

    /// <summary>
    /// Normalize mode name to a base key for preset lookup.
    /// </summary>
    private static string NormalizeMode(string mode)
    {
        return (mode?.ToUpperInvariant()) switch
        {
            "LSB" or "USB" => "SSB",
            "DIGL" or "DIGU" or "FDV" => "DIGI",
            "CW" or "CWL" => "CW",
            "AM" or "SAM" or "DSB" => "AM",
            "FM" or "NFM" or "DFM" => "FM",
            _ => mode?.ToUpperInvariant() ?? "SSB"
        };
    }

    /// <summary>
    /// Default presets for each mode category. Sorted narrow to wide.
    /// </summary>
    private static List<FilterPreset> GetDefaults(string modeKey)
    {
        return modeKey switch
        {
            "SSB" => new List<FilterPreset>
            {
                new("Narrow", 100, 1900),      // 1.8k
                new("Normal", 100, 2500),       // 2.4k
                new("Wide", 100, 2800),         // 2.7k
                new("Extra Wide", 100, 3100),   // 3.0k
                new("Wide Open", 100, 4100),    // 4.0k
                new("Monitor", 100, 6100),      // 6.0k — ragchew / relaxed listening
                new("Full Band", 100, 10100),   // 10.0k — wideband monitoring
            },
            "CW" => new List<FilterPreset>
            {
                new("Tight", -50, 50),         // 100 Hz
                new("Narrow", -125, 125),      // 250 Hz
                new("Normal", -250, 250),      // 500 Hz
                new("Wide", -500, 500),        // 1k
                new("Wide Open", -1000, 1000), // 2k
                new("Monitor", -2000, 2000),   // 4k — hear nearby CW signals
            },
            "DIGI" => new List<FilterPreset>
            {
                new("Narrow", 100, 600),       // 500 Hz
                new("Normal", 100, 2800),      // 2.7k
                new("Wide", 100, 3100),        // 3.0k
                new("Wide Open", 100, 4100),   // 4.0k
            },
            "AM" => new List<FilterPreset>
            {
                new("Narrow", -3000, 3000),    // 6k
                new("Normal", -4000, 4000),    // 8k
                new("Wide", -5000, 5000),      // 10k
                new("Wide Open", -6000, 6000), // 12k
            },
            "FM" => new List<FilterPreset>
            {
                new("Narrow", -4000, 4000),    // 8k
                new("Normal", -6000, 6000),    // 12k
                new("Wide Open", -8000, 8000), // 16k
            },
            _ => new List<FilterPreset>
            {
                new("Normal", 100, 2500),
            }
        };
    }

    #region Persistence

    public static FilterPresets Load(string configDirectory, string operatorName)
        => Load(configDirectory, operatorName, out _);

    /// <summary>
    /// Load the operator's filter presets, with the same honesty contract the
    /// audio preset store adopted (#49): a missing file is a fresh start; a
    /// file that exists but cannot be read is the operator's tuning, so it is
    /// moved aside — never left for the next save to overwrite — and its new
    /// path comes back in <paramref name="corruptSidelinedPath"/> (null when
    /// nothing was wrong) for the caller to speak.
    /// </summary>
    public static FilterPresets Load(string configDirectory, string operatorName,
        out string? corruptSidelinedPath)
    {
        corruptSidelinedPath = null;
        var filePath = GetFilePath(configDirectory, operatorName);

        if (!File.Exists(filePath))
            return new FilterPresets();

        try
        {
            using (var fs = File.OpenRead(filePath))
            {
                var serializer = new XmlSerializer(typeof(FilterPresets));
                var presets = (FilterPresets?)serializer.Deserialize(fs);
                if (presets != null)
                    return presets;
            }
            corruptSidelinedPath = SidelineCorruptFile(filePath);
            return new FilterPresets();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"FilterPresets.Load failed: {ex.Message}");
            corruptSidelinedPath = SidelineCorruptFile(filePath);
            return new FilterPresets();
        }
    }

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
                $"FilterPresets: could not sideline corrupt file: {ex.Message}");
            return filePath;
        }
    }

    /// <summary>
    /// Save the presets. Returns whether the file actually landed — callers
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

            using var fs = File.Create(filePath);
            var serializer = new XmlSerializer(typeof(FilterPresets));
            serializer.Serialize(fs, this);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"FilterPresets.Save failed: {ex.Message}");
            return false;
        }
    }

    private static string GetFilePath(string configDirectory, string operatorName)
    {
        return Path.Combine(configDirectory, $"{operatorName}_filterPresets.xml");
    }

    #endregion
}

/// <summary>
/// Presets for a single mode (serialization helper).
/// </summary>
public class ModePresets
{
    [XmlAttribute]
    public string Mode { get; set; } = "";

    public List<FilterPreset> Presets { get; set; } = new();
}
