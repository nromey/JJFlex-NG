using System;
using System.IO;
using System.Xml.Serialization;

namespace Radios
{
    /// <summary>
    /// Preferred units for frequency speech announcements.
    /// </summary>
    public enum FrequencyUnits
    {
        /// <summary>Full Hz display: "14.225.000"</summary>
        Hz,
        /// <summary>kHz display: "14,225 kilohertz"</summary>
        kHz,
        /// <summary>MHz display: "14.225 megahertz"</summary>
        MHz
    }

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
        /// Set to 0 to disable ALC auto-release.
        /// </summary>
        public int AlcAutoReleaseSeconds { get; set; } = 60;

        /// <summary>
        /// When true, the reflected-power alarm CUTS the transmission when it
        /// fires with forward power above the cut floor (#224, ruled by Noel
        /// 2026-08-25). When false — the default — the alarm only warns.
        /// </summary>
        /// <remarks>
        /// A setting, not a behaviour, on purpose: an app that unilaterally
        /// unkeys a transmitter has taken the operator's station away
        /// mid-transmission, and an operator running a reactive load or an
        /// experimental antenna must be able to switch it off.
        ///
        /// DEFAULT ON — ruled by Noel 2026-08-26, reversing the initial OFF.
        /// Two reasons, and the second is the one that decides it.
        ///
        /// The errors are asymmetric. A wrong cut is startling and entirely
        /// recoverable: the operator turns the setting off, annoyed. A cut
        /// that never comes is damaged finals, which is neither. When one
        /// error is recoverable and the other is not, the default belongs on
        /// the recoverable side.
        ///
        /// And the asymmetry is sharper for the operators this app exists
        /// for. A sighted operator glances at an SWR meter and reaches for
        /// the power knob; a blind one cannot. This is not a convenience
        /// layered over their own vigilance — it is the only version of that
        /// protection they have. Defaulting OFF would have hidden it from
        /// exactly the people least able to do without it.
        ///
        /// The decision itself lives in
        /// <c>TransmitSafety.ShouldCutReflected</c>, pure and tested; the ATU
        /// stand-down, the &gt;10 W floor, the NaN refusal and the two-sample
        /// rule live there too.
        ///
        /// STILL OWED, and it is a release gate rather than a nicety: NOBODY
        /// HAS WATCHED THIS FIRE. A guard nobody has seen work is a guess
        /// wearing a safety label. The bench recipe is known and was run by
        /// accident twice on 2026-08-22 (see #189): select an EMPTY antenna
        /// port and key at roughly 20 W. Measured then, into that open port —
        /// forward 17.5 W, reflected 13.4 W, seventy-six percent coming
        /// straight back, comfortably over this guard's 10 W floor. Note the
        /// SWR meter reported 1.008 throughout and was lying; REFLECTED POWER
        /// told the truth, which is why this guard reads watts and not SWR.
        /// A dummy load cannot be used for this test — it is a good match, so
        /// there is nothing to reflect.
        /// </remarks>
        public bool CutTransmitOnReflectedAlarm { get; set; } = true;

        /// <summary>
        /// When true, TX/RX transition speech is announced ("Transmitting", "Receiving").
        /// When false, speech is muted but tones and safety warnings always play.
        /// </summary>
        public bool SpeechEnabled { get; set; } = true;

        /// <summary>
        /// When true, band jumps save/recall frequency per band+mode.
        /// When false, band jumps always go to band center.
        /// </summary>
        public bool BandMemoryEnabled { get; set; } = true;

        /// <summary>
        /// When true, TX start/stop chirp tones play on PTT transitions.
        /// When false, chirp tones are muted (safety warnings always play).
        /// </summary>
        public bool ChirpEnabled { get; set; } = true;

        /// <summary>
        /// Preferred units for frequency speech announcements (Hz, kHz, or MHz).
        /// </summary>
        public FrequencyUnits FrequencyDisplayUnits { get; set; } = FrequencyUnits.Hz;

        /// <summary>
        /// Clamp TimeoutSeconds to valid range [10..900].
        /// </summary>
        public void Validate()
        {
            TimeoutSeconds = Math.Clamp(TimeoutSeconds, 10, HardKillSeconds);
            Warning1SecondsBeforeTimeout = Math.Clamp(Warning1SecondsBeforeTimeout, 5, TimeoutSeconds - 1);
            Warning2SecondsBeforeTimeout = Math.Clamp(Warning2SecondsBeforeTimeout, 3, Warning1SecondsBeforeTimeout - 1);
            OhCrapSecondsBeforeTimeout = Math.Clamp(OhCrapSecondsBeforeTimeout, 1, Warning2SecondsBeforeTimeout - 1);
            // 0 = disabled, otherwise clamp to valid range
            if (AlcAutoReleaseSeconds != 0)
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
