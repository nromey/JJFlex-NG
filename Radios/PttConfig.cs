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
        /// A HELD push to talk's warnings, in seconds before
        /// <see cref="HardKillSeconds"/> counted from its first press (#1020;
        /// ruled by Noel 2026-11-11 11:41: "a held key warns 30, 15 and 5
        /// seconds before the 15-minute kill"). Fixed on purpose, not the
        /// operator's configurable offsets above: those are clamped against
        /// the lock's timeout, which the ruling says applies to the lock only.
        /// </summary>
        public const int HeldKeyWarning1SecondsBeforeKill = 30;

        /// <summary>The held key's second warning; see <see cref="HeldKeyWarning1SecondsBeforeKill"/>.</summary>
        public const int HeldKeyWarning2SecondsBeforeKill = 15;

        /// <summary>The held key's last warning; see <see cref="HeldKeyWarning1SecondsBeforeKill"/>.</summary>
        public const int HeldKeyLastWarningSecondsBeforeKill = 5;

        /// <summary>
        /// ALC zero-signal auto-release threshold in seconds.
        /// If ALC reads 0 for this many consecutive seconds while locked, auto-release TX.
        /// Set to 0 to disable ALC auto-release.
        /// </summary>
        public int AlcAutoReleaseSeconds { get; set; } = 60;

        /// <summary>The shortest tune reminder: a quick tune must never hear it.</summary>
        public const int TuneReminderMinSeconds = 10;

        /// <summary>
        /// The longest tune reminder, half an hour: room for any tube amplifier's
        /// tune-up, while a forgotten carrier still always gets told.
        /// </summary>
        public const int TuneReminderMaxSeconds = 1800;

        /// <summary>
        /// How long a tune carrier stays up before the operator is told it is
        /// still on, and how often after that (#860, ruled by Noel 2026-11-01
        /// 07:52: default 25, changeable because people tune tube amplifiers
        /// for long periods). The carrier itself is never touched.
        /// </summary>
        public int TuneReminderSeconds { get; set; } = 25;

        /// <summary>
        /// When true — the default, ruled ON below — the reflected-power alarm
        /// CUTS the transmission when it fires with forward power above the
        /// cut floor (#224, ruled by Noel 2026-08-25). When false, the alarm
        /// only warns.
        /// </summary>
        /// <remarks>
        /// A setting, not a behaviour, on purpose: an app that unilaterally
        /// unkeys a transmitter has taken the operator's station away
        /// mid-transmission, and an operator running a reactive load or an
        /// experimental antenna must be able to switch it off.
        ///
        /// AND THEY CAN, since 2026-08-30: one checkbox on the Settings
        /// dialog's PTT tab, committed on OK like its neighbours — arming or
        /// disarming a safety belongs in the deliberate pipeline, not the
        /// live-apply one. Until then this property was reachable only by
        /// hand-editing XML, which meant the 2026-08-26 ON ruling was
        /// shipping on a premise ("the escape hatch is intact") that was
        /// false. Ruled defeatable by Noel 2026-08-30: the radio already
        /// folds power back on its own and already alarms, so this cut is a
        /// THIRD layer over two that exist in hardware — that is why
        /// defeating it is tolerable. Two mitigations are part of the deal:
        /// while this is false the reflected warning says out loud that no
        /// cut is coming (<c>TransmitSafety.ReflectedWarningText</c>,
        /// cutDisarmed), and unchecking the box is never a silent flip — the
        /// dialog speaks what is being given up.
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
        /// <c>TransmitSafety.JudgeReflectedCut</c>, pure and tested, and it
        /// has TWO rungs since Sprint 47 (#571 tier 1). The share rung,
        /// <c>ShouldCutReflected</c>: the warning has fired and a further
        /// sample above 10 W forward still has the warning's share or more
        /// coming back (36 percent, SWR 4, since 2026-11-02; it was forty). The watts rung, <c>ShouldCutReflectedWatts</c>: ten watts or
        /// more coming back on two coherent samples in a row, whatever the
        /// share — reflected watts is what heats the finals, and a voice
        /// trough cannot fake it the way it faked the ratio (#453). The ATU
        /// stand-down, the NaN refusal and the two-sample rule apply to both.
        /// Each rung speaks in its own unit and never the other's (#237).
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
        /// When true, the transmit and receive announcements name their slice:
        /// "Transmitting on slice A", "Receiving on slice B", "Transmitting on
        /// slice A, locked" (#496; Noel, 2026-09-01: "with a toggle on or in
        /// verbose 'transmitting on slice a' 'receiving on slice b' ... bingo
        /// done"). Every time when on, never only when the slices differ, so
        /// silence is never ambiguous.
        /// </summary>
        /// <remarks>
        /// Its own switch rather than the Chatty level, because verbosity is one
        /// dial (#322) and a declaration is not a hint; on by default, because
        /// it is the fact that would have stopped a check keying a slice nobody
        /// was listening to. Both are the orchestrator's decisions, 2026-11-02,
        /// not rulings.
        /// </remarks>
        public bool AnnounceTransmitSlice { get; set; } = true;

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
            TuneReminderSeconds = Math.Clamp(TuneReminderSeconds, TuneReminderMinSeconds, TuneReminderMaxSeconds);
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

        /// <summary>
        /// This configuration as its saved XML, to tell whether the Settings
        /// dialog changed it (the 8600 bench's Fail 3, #496).
        /// </summary>
        public string Snapshot()
        {
            try
            {
                using var sw = new StringWriter();
                new XmlSerializer(typeof(PttConfig)).Serialize(sw, this);
                return sw.ToString();
            }
            catch { return ""; }
        }

        /// <summary>
        /// True when Settings changed the PTT tab with nowhere to save it: no
        /// radio connected, so <paramref name="now"/> is a throwaway and its
        /// changes are lost (the 8600 bench's Fail 3, #496). The dialog must
        /// then say so rather than "Settings saved."
        /// </summary>
        public static bool ChangesNotKept(string atOpen, PttConfig now, bool hasHome) =>
            !hasHome && now != null && !string.Equals(atOpen ?? "", now.Snapshot(), StringComparison.Ordinal);

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
