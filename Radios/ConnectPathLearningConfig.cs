using System;
using System.IO;
using System.Xml.Serialization;

namespace Radios
{
    /// <summary>
    /// Whether JJ Flexible is allowed to learn a radio's preferred connection
    /// path from its own history, and how much evidence that takes (task #102).
    ///
    /// <para>Task #79 shipped the learning with a hardwired threshold of three
    /// and no way to turn it off or take it back. Three is a judgement about how
    /// much evidence outweighs inertia, and the right answer is not the same for
    /// everyone: an operator who is often on the road wants the app slower to
    /// conclude anything at all, because half their connects happen from a
    /// network that says nothing about where they usually are.</para>
    ///
    /// <para>APP-LEVEL, not per-operator, matching
    /// <see cref="DiagnosticsConfig"/> — the store this governs
    /// (<see cref="ConnectionHistory"/>) is keyed by radio serial and shared by
    /// every operator profile on the machine, so a per-operator switch could
    /// only ever half-apply. Stored as
    /// <c>&lt;BaseConfigDir&gt;\connectPathLearningV1.xml</c>.</para>
    ///
    /// <para>An absent file yields the defaults, and the defaults are exactly
    /// what the app did before this type existed: learning on, threshold three.
    /// Upgrading changes nobody's behaviour until they change it themselves.</para>
    /// </summary>
    [XmlRoot("ConnectPathLearningConfig")]
    public class ConnectPathLearningConfig
    {
        /// <summary>File name under the base config directory.</summary>
        public const string FileName = "connectPathLearningV1.xml";

        /// <summary>
        /// The smallest run of successful connects this setting will accept.
        /// Below three, a single unlucky evening becomes a habit.
        /// </summary>
        public const int MinThreshold = 3;

        /// <summary>
        /// The largest run this setting will offer, and it is a CEILING OF THE
        /// STORE rather than a matter of taste.
        ///
        /// <para><see cref="ConnectionHistory.MaxEntries"/> is ten ATTEMPTS, and
        /// a chain-walking connect writes two of them — the leg that failed,
        /// then the leg that worked. A radio that habitually falls back
        /// therefore has room for exactly five successes in its ring, so five is
        /// the largest number that radio could ever reach. Six would be a
        /// setting the store cannot honour: learning would simply never fire
        /// again, silently, for exactly the radios whose habit is strongest.
        /// Offering a number that can never be reached is worse than not
        /// offering it.</para>
        /// </summary>
        public const int MaxThreshold = 5;

        /// <summary>
        /// Whether the trend is consulted at all. False means JJ Flexible never
        /// prefills a connection path from history — the stored choice and the
        /// plain availability default are the only inputs left, which is exactly
        /// how the app behaved before task #79.
        ///
        /// <para>Turning this off does NOT stop the history being recorded.
        /// The ring is diagnostic data in its own right ("how long is this
        /// connect actually taking") and answers support questions nothing else
        /// can. Off means "do not act on it", not "do not keep it".</para>
        /// </summary>
        public bool LearnFromHistory { get; set; } = true;

        /// <summary>
        /// How many successful connects in a row on one path count as a habit.
        /// Clamped to <see cref="MinThreshold"/>..<see cref="MaxThreshold"/> on
        /// load, so a hand-edited file cannot ask for a number the ring can
        /// never produce.
        /// </summary>
        public int TrendThreshold { get; set; } = ConnectPathPolicy.TrendThreshold;

        /// <summary>
        /// The current setting, in the words the UI and the speech both use, so
        /// the two cannot drift. Honest about the OFF state, which is the state
        /// most easily left unsaid.
        /// </summary>
        [XmlIgnore]
        public string Description =>
            LearnFromHistory
                ? $"Learning the connection path is on, after {TrendThreshold} connects in a row the same way."
                : "Learning the connection path is off. Nothing is prefilled from history.";

        // ------------------------------------------------------------------
        // The cached current value
        // ------------------------------------------------------------------

        private static readonly object _sync = new();
        private static ConnectPathLearningConfig? _cached;
        private static string _cachedDir = "";

        /// <summary>
        /// The setting in force, loaded once per config directory.
        ///
        /// <para>Cached because the reader is per RADIO: the selector asks the
        /// policy about every row it paints, and a LAN radio re-announces about
        /// once a second. Keyed on the directory so a test (or a config-root
        /// change) gets a fresh load rather than a stale answer; invalidated
        /// explicitly by <see cref="Invalidate"/> when the settings screen
        /// writes a new value.</para>
        /// </summary>
        public static ConnectPathLearningConfig Current
        {
            get
            {
                var dir = RadioConfig.ResolvedBaseDirectory ?? "";
                lock (_sync)
                {
                    if (_cached != null && string.Equals(_cachedDir, dir, StringComparison.OrdinalIgnoreCase))
                        return _cached;
                    _cached = Load(dir);
                    _cachedDir = dir;
                    return _cached;
                }
            }
        }

        /// <summary>Drop the cache so the next read comes off disk. Call after
        /// saving.</summary>
        public static void Invalidate()
        {
            lock (_sync) { _cached = null; _cachedDir = ""; }
        }

        /// <summary>
        /// Load from the base config directory. Never throws: an unreadable or
        /// malformed file yields the defaults, because refusing to open the
        /// radio picker over a learning preference would be absurd.
        /// </summary>
        public static ConnectPathLearningConfig Load(string configDirectory)
        {
            var path = GetFilePath(configDirectory);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return new ConnectPathLearningConfig();

            try
            {
                using var fs = File.OpenRead(path);
                var serializer = new XmlSerializer(typeof(ConnectPathLearningConfig));
                var cfg = (ConnectPathLearningConfig?)serializer.Deserialize(fs);
                return Sanitize(cfg ?? new ConnectPathLearningConfig());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"ConnectPathLearningConfig.Load failed ({path}): {ex.Message} — using defaults");
                return new ConnectPathLearningConfig();
            }
        }

        /// <summary>
        /// Save to the base config directory and invalidate the cache. Returns
        /// false rather than throwing — the caller decides what to tell the
        /// operator, and must not claim success on a false.
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

                Sanitize(this);
                using (var fs = File.Create(path))
                {
                    var serializer = new XmlSerializer(typeof(ConnectPathLearningConfig));
                    serializer.Serialize(fs, this);
                }
                Invalidate();
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"ConnectPathLearningConfig.Save failed ({path}): {ex.Message}");
                return false;
            }
        }

        /// <summary>Full path of the config file for a base directory.</summary>
        public static string GetFilePath(string configDirectory)
        {
            if (string.IsNullOrEmpty(configDirectory)) return string.Empty;
            return Path.Combine(configDirectory, FileName);
        }

        private static ConnectPathLearningConfig Sanitize(ConnectPathLearningConfig cfg)
        {
            if (cfg.TrendThreshold < MinThreshold) cfg.TrendThreshold = MinThreshold;
            if (cfg.TrendThreshold > MaxThreshold) cfg.TrendThreshold = MaxThreshold;
            return cfg;
        }
    }
}
