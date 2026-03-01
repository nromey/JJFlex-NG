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
    /// Format preset for speech: e.g. "2.4k SSB" or "250 CW".
    /// </summary>
    public string FormatForSpeech()
    {
        int w = Width;
        if (w >= 1000)
            return $"{w / 1000.0:0.#}k {Name}".Trim();
        return $"{w} hertz {Name}".Trim();
    }
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
                new("Narrow", 100, 1900),   // 1.8k
                new("Normal", 100, 2500),    // 2.4k
                new("Wide", 100, 2800),      // 2.7k
                new("Extra Wide", 100, 3100), // 3.0k
            },
            "CW" => new List<FilterPreset>
            {
                new("Tight", -50, 50),     // 100 Hz
                new("Narrow", -125, 125),  // 250 Hz
                new("Normal", -250, 250),  // 500 Hz
                new("Wide", -500, 500),    // 1k Hz
            },
            "DIGI" => new List<FilterPreset>
            {
                new("Narrow", 100, 600),    // 500 Hz
                new("Normal", 100, 2800),   // 2.7k
                new("Wide", 100, 3100),     // 3.0k
            },
            "AM" => new List<FilterPreset>
            {
                new("Narrow", -3000, 3000),  // 6k
                new("Normal", -4000, 4000),  // 8k
                new("Wide", -5000, 5000),    // 10k
            },
            "FM" => new List<FilterPreset>
            {
                new("Narrow", -4000, 4000),  // 8k
                new("Normal", -6000, 6000),  // 12k
            },
            _ => new List<FilterPreset>
            {
                new("Normal", 100, 2500),
            }
        };
    }

    #region Persistence

    public static FilterPresets Load(string configDirectory, string operatorName)
    {
        var filePath = GetFilePath(configDirectory, operatorName);

        if (!File.Exists(filePath))
            return new FilterPresets();

        try
        {
            using var fs = File.OpenRead(filePath);
            var serializer = new XmlSerializer(typeof(FilterPresets));
            var presets = (FilterPresets?)serializer.Deserialize(fs);
            return presets ?? new FilterPresets();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"FilterPresets.Load failed: {ex.Message}");
            return new FilterPresets();
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
            var serializer = new XmlSerializer(typeof(FilterPresets));
            serializer.Serialize(fs, this);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"FilterPresets.Save failed: {ex.Message}");
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
