using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using HamBands;

namespace Radios
{
    /// <summary>
    /// Per-operator band memory: remembers last frequency per band per mode.
    /// Stored as {OperatorName}_bandMemory.xml in the config directory.
    /// </summary>
    [XmlRoot("BandMemory")]
    public class BandMemory
    {
        public List<BandModeEntry> Entries { get; set; } = new();

        /// <summary>
        /// Get the remembered frequency for a band+mode combination.
        /// Falls back to band center, or Channel 1 for channelized bands (60m).
        /// </summary>
        public ulong GetFrequency(Bands.BandNames band, string mode)
        {
            var entry = Entries.FirstOrDefault(e => e.Band == band &&
                string.Equals(e.Mode, mode, StringComparison.OrdinalIgnoreCase));
            if (entry != null) return entry.Frequency;

            // Channelized bands: fall back to Channel 1 instead of band center
            if (band == Bands.BandNames.m60)
            {
                var alloc = SixtyMeterChannels.GetAllocation("US");
                if (alloc?.Channels.Length > 0)
                    return (ulong)(alloc.Value.Channels[0].FrequencyMHz * 1_000_000.0 + 0.5);
            }

            // Fall back to band center
            var bandInfo = Bands.Query(band);
            return bandInfo != null ? (bandInfo.Low + bandInfo.High) / 2 : 0;
        }

        /// <summary>
        /// Store the current frequency for a band+mode combination.
        /// </summary>
        public void SetFrequency(Bands.BandNames band, string mode, ulong freq)
        {
            var entry = Entries.FirstOrDefault(e => e.Band == band &&
                string.Equals(e.Mode, mode, StringComparison.OrdinalIgnoreCase));
            if (entry != null)
            {
                entry.Frequency = freq;
            }
            else
            {
                Entries.Add(new BandModeEntry { Band = band, Mode = mode, Frequency = freq });
            }
        }

        public static BandMemory Load(string configDirectory, string operatorName)
        {
            var filePath = GetFilePath(configDirectory, operatorName);

            if (!File.Exists(filePath))
                return new BandMemory();

            try
            {
                using var fs = File.OpenRead(filePath);
                var serializer = new XmlSerializer(typeof(BandMemory));
                var config = (BandMemory?)serializer.Deserialize(fs);
                return config ?? new BandMemory();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"BandMemory.Load failed: {ex.Message}");
                return new BandMemory();
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
                var serializer = new XmlSerializer(typeof(BandMemory));
                serializer.Serialize(fs, this);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"BandMemory.Save failed: {ex.Message}");
            }
        }

        private static string GetFilePath(string configDirectory, string operatorName)
        {
            return Path.Combine(configDirectory, $"{operatorName}_bandMemory.xml");
        }
    }

    public class BandModeEntry
    {
        [XmlAttribute] public Bands.BandNames Band { get; set; }
        [XmlAttribute] public string Mode { get; set; } = "";
        [XmlAttribute] public ulong Frequency { get; set; }
    }
}
