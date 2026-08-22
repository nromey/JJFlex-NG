using System;
using System.IO;
using System.Xml.Serialization;

namespace Radios
{
    /// <summary>
    /// How much detail the diagnostic log records while it runs. Two choices,
    /// deliberately — see docs/planning/active/diagnostic-log-surface.md §6 for
    /// why the five-item TraceLevel list does not reach the user.
    ///
    /// "Off" is not here on purpose: it was index 0 of .NET's TraceLevel enum
    /// leaking into a user-facing list, and "the log is on at level Off" is
    /// incoherent. On/off is <see cref="DiagnosticsConfig.KeepDiagnosticLog"/>.
    /// Error-only and Warning-only are not offered either: a log filtered that
    /// hard cannot answer "what did the operator do just before this", which is
    /// the one question the reporting pipeline exists to answer.
    /// </summary>
    public enum DiagnosticDetail
    {
        /// <summary>Maps to TraceLevel.Info — today's boot default.</summary>
        Normal = 0,

        /// <summary>Maps to TraceLevel.Verbose.</summary>
        Detailed = 1
    }

    /// <summary>
    /// The diagnostic log's persisted settings.
    ///
    /// APP-LEVEL, not per-operator, and that is load-bearing: GetConfigInfo
    /// opens the log before any operator has been selected, so a per-operator
    /// setting could never govern boot. Stored as
    /// <c>&lt;BaseConfigDir&gt;\diagnosticsConfigV1.xml</c>, same serialization
    /// shape as <see cref="AutoConnectConfig"/>.
    ///
    /// An absent file yields the defaults, and the defaults are exactly what
    /// the app did before this type existed (log on, Info level). First run and
    /// upgrade are therefore no-ops — nobody's behaviour changes until they
    /// change it themselves.
    /// </summary>
    [XmlRoot("DiagnosticsConfig")]
    public class DiagnosticsConfig
    {
        /// <summary>File name under the base config directory.</summary>
        public const string FileName = "diagnosticsConfigV1.xml";

        /// <summary>
        /// Whether JJ Flex keeps a diagnostic log at all. Default true, which
        /// matches the pre-existing unconditional <c>BootTrace</c> behaviour —
        /// see the design's answered question 2. An off-by-default log defeats
        /// the reporting pipeline; the privacy posture is held by the
        /// local-only rule and the tab's verbatim note, not by this default.
        /// </summary>
        public bool KeepDiagnosticLog { get; set; } = true;

        /// <summary>
        /// The standing detail level. Serialized as the enum NAME ("Normal" /
        /// "Detailed") so a hand-read of the XML says something meaningful and
        /// an added level later does not renumber the existing ones.
        /// </summary>
        public DiagnosticDetail DetailLevel { get; set; } = DiagnosticDetail.Normal;

        /// <summary>
        /// Record the radio's continuous meter stream into the diagnostic log —
        /// mic level, SWR, forward and reflected power, ALC, S-meter — as one
        /// min/max/last summary line per meter per second (see
        /// MeterTraceStream). Off by default, deliberately: the raw stream was
        /// measured at 25.7 MB of one 52.4 MB capture on 2026-08-21 (task
        /// #170), and its volume was pushing every other line out of any
        /// tail-window a log reader used. Turn it on from Settings →
        /// Diagnostics for a bench session, where these values are exactly the
        /// evidence wanted and file size is nobody's problem.
        /// </summary>
        public bool RecordMeterStream { get; set; } = false;

        /// <summary>
        /// Record a transcript of everything the app SAYS and SOUNDS — every
        /// spoken message with its verbosity and origin, every earcon, every CW
        /// notification — as one JSON line each, alongside the ordinary
        /// diagnostic log. Off by default.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the same channel the test harness uses
        /// (<see cref="OutputChannelRecorder"/>), exposed to an operator for
        /// one reason: when somebody can reproduce a problem with what the app
        /// said, the transcript is the evidence, and nothing else in the bundle
        /// carries it. The diagnostic log records what the program DID; only
        /// this records what the operator HEARD.
        /// </para>
        /// <para>
        /// Cheap to include. A transcript is one short JSON line per utterance
        /// and compresses like the text it is, so a whole session adds little
        /// to a problem report — unlike the meter stream above, which is why
        /// that one carries a warning and this one does not.
        /// </para>
        /// <para>
        /// A <c>--record</c> switch or <c>JJFLEX_RECORD=1</c> still wins: a
        /// harness that asked for a transcript gets one whatever this says.
        /// This only turns recording ON, never off, so an automated run can
        /// never be silenced by an operator's saved preference.
        /// </para>
        /// </remarks>
        public bool RecordSpokenOutput { get; set; } = false;

        /// <summary>
        /// How many crash reports to keep in %AppData%\JJFlexRadio\Errors.
        ///
        /// The count matters more than the age here: a full-memory dump runs
        /// 200-700 MB compressed, so "30 days" alone let the folder reach
        /// gigabytes. Three covers the crash under discussion plus the two
        /// before it — enough for "does this reproduce" — at a bounded cost.
        /// A report that has never been submitted or dismissed is never
        /// deleted by this count; see CrashReporter.PruneCrashReports.
        /// </summary>
        public int KeepCrashReports { get; set; } = 3;

        /// <summary>
        /// Delete downloaded firmware images older than this many days. Zero or
        /// less disables the sweep. Firmware images are a pure cache — the
        /// radio's own update flow re-downloads them on demand — so ageing them
        /// out costs a download, never evidence.
        /// </summary>
        public int FirmwareCacheDays { get; set; } = 30;

        /// <summary>
        /// The TraceLevel this detail setting means. Kept here so the mapping
        /// lives in exactly one place; every TraceLine(..., lvl) call site and
        /// the TraceLevel enum itself stay untouched.
        /// </summary>
        [XmlIgnore]
        public System.Diagnostics.TraceLevel TraceLevel =>
            DetailLevel == DiagnosticDetail.Detailed
                ? System.Diagnostics.TraceLevel.Verbose
                : System.Diagnostics.TraceLevel.Info;

        /// <summary>
        /// The detail level's user-facing word, for speech and labels. One
        /// vocabulary, so the status line, the radio buttons and the spoken
        /// confirmation cannot drift apart.
        /// </summary>
        [XmlIgnore]
        public string DetailWord =>
            DetailLevel == DiagnosticDetail.Detailed ? "detailed" : "normal";

        /// <summary>
        /// Load from the base config directory. Never throws: an unreadable or
        /// malformed file yields defaults, because refusing to boot over a
        /// diagnostics preference would be a spectacular own goal.
        /// </summary>
        public static DiagnosticsConfig Load(string configDirectory)
        {
            var path = GetFilePath(configDirectory);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return new DiagnosticsConfig();

            try
            {
                using var fs = File.OpenRead(path);
                var serializer = new XmlSerializer(typeof(DiagnosticsConfig));
                var cfg = (DiagnosticsConfig?)serializer.Deserialize(fs);
                return Sanitize(cfg ?? new DiagnosticsConfig());
            }
            catch (Exception ex)
            {
                // Trace directly rather than through JJTrace: this can run
                // before the log this type governs has been opened.
                System.Diagnostics.Trace.WriteLine(
                    $"DiagnosticsConfig.Load failed ({path}): {ex.Message} — using defaults");
                return new DiagnosticsConfig();
            }
        }

        /// <summary>
        /// Save to the base config directory. Returns false rather than
        /// throwing — the caller decides what to tell the operator, and a
        /// failed preference write must never take the app down.
        /// </summary>
        public bool Save(string configDirectory)
        {
            var path = GetFilePath(configDirectory);
            if (string.IsNullOrEmpty(path)) return false;

            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                using var fs = File.Create(path);
                var serializer = new XmlSerializer(typeof(DiagnosticsConfig));
                serializer.Serialize(fs, this);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"DiagnosticsConfig.Save failed ({path}): {ex.Message}");
                return false;
            }
        }

        /// <summary>Full path of the config file for a base directory.</summary>
        public static string GetFilePath(string configDirectory)
        {
            if (string.IsNullOrEmpty(configDirectory)) return string.Empty;
            return Path.Combine(configDirectory, FileName);
        }

        /// <summary>
        /// Clamp values a hand-edited file could put out of range. A zero
        /// KeepCrashReports would delete the dump support is about to ask for,
        /// which is the exact failure this whole retention design exists to
        /// avoid, so the floor is 1.
        /// </summary>
        private static DiagnosticsConfig Sanitize(DiagnosticsConfig cfg)
        {
            if (cfg.KeepCrashReports < 1) cfg.KeepCrashReports = 1;
            if (cfg.KeepCrashReports > 50) cfg.KeepCrashReports = 50;
            if (cfg.FirmwareCacheDays < 0) cfg.FirmwareCacheDays = 0;
            return cfg;
        }
    }
}
