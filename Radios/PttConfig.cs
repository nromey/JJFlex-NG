using System;
using System.IO;
using System.Xml.Serialization;

namespace Radios
{
    /// <summary>
    /// PTT safety configuration — timeout, warning thresholds, per-operator persistence.
    /// </summary>
    [XmlRoot("PttConfig")]
    public class PttConfig
    {
        /// <summary>
        /// User-configurable timeout in seconds (default 180 = 3 min, max 900 = 15 min).
        /// After this duration of locked TX, the radio returns to RX.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 180;

        /// <summary>
        /// Seconds before timeout to begin Warning1 (10-second beeps).
        /// </summary>
        public int Warning1SecondsBeforeTimeout { get; set; } = 30;

        /// <summary>
        /// Seconds before timeout to begin Warning2 (5-second beeps).
        /// </summary>
        public int Warning2SecondsBeforeTimeout { get; set; } = 15;

        /// <summary>
        /// Seconds before timeout to begin OhCrap warning (1-second beeps).
        /// </summary>
        public int OhCrapSecondsBeforeTimeout { get; set; } = 5;

        /// <summary>
        /// Absolute hard kill in seconds. Non-configurable, clamped to 900 (15 min).
        /// Nobody needs 15 minutes of PTT.
        /// </summary>
        [XmlIgnore]
        public static readonly int HardKillSeconds = 900;

        /// <summary>
        /// ALC zero-signal auto-release threshold in seconds.
        /// If ALC reads 0 for this many consecutive seconds while locked, auto-release TX.
        /// </summary>
        public int AlcAutoReleaseSeconds { get; set; } = 60;

        /// <summary>
        /// Whether to announce transmit/receive state changes via screen reader.
        /// When false, only safety warnings (timeout approaching) are spoken.
        /// </summary>
        public bool SpeechEnabled { get; set; } = true;

        /// <summary>
        /// Clamp TimeoutSeconds to valid range [10..900].
        /// </summary>
        public void Validate()
        {
            TimeoutSeconds = Math.Clamp(TimeoutSeconds, 10, HardKillSeconds);
            Warning1SecondsBeforeTimeout = Math.Clamp(Warning1SecondsBeforeTimeout, 5, TimeoutSeconds - 1);
            Warning2SecondsBeforeTimeout = Math.Clamp(Warning2SecondsBeforeTimeout, 3, Warning1SecondsBeforeTimeout - 1);
            OhCrapSecondsBeforeTimeout = Math.Clamp(OhCrapSecondsBeforeTimeout, 1, Warning2SecondsBeforeTimeout - 1);
            AlcAutoReleaseSeconds = Math.Clamp(AlcAutoReleaseSeconds, 10, 300);
        }

        public static PttConfig Load(string configDirectory, string operatorName)
        {
            var filePath = GetFilePath(configDirectory, operatorName);

            if (!File.Exists(filePath))
                return CreateDefault();

            try
            {
                using var fs = File.OpenRead(filePath);
                var serializer = new XmlSerializer(typeof(PttConfig));
                var config = (PttConfig?)serializer.Deserialize(fs);
                if (config == null) return CreateDefault();
                config.Validate();
                return config;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"PttConfig.Load failed: {ex.Message}");
                return CreateDefault();
            }
        }

        public void Save(string configDirectory, string operatorName)
        {
            Validate();
            var filePath = GetFilePath(configDirectory, operatorName);

            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                using var fs = File.Create(filePath);
                var serializer = new XmlSerializer(typeof(PttConfig));
                serializer.Serialize(fs, this);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"PttConfig.Save failed: {ex.Message}");
            }
        }

        private static PttConfig CreateDefault()
        {
            var config = new PttConfig();
            config.Validate();
            return config;
        }

        private static string GetFilePath(string configDirectory, string operatorName)
        {
            return Path.Combine(configDirectory, $"{operatorName}_pttConfig.xml");
        }
    }
}
