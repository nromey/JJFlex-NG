using System;
using System.IO;
using System.Xml.Serialization;

namespace Radios
{
    /// <summary>
    /// Double-tap timing tolerance for input interactions that use double-press
    /// semantics (Escape-collapse-all, any future double-tap consumer). The
    /// filter-edge bracket double-tap was its other consumer until #748
    /// retired it for Alt+Shift with a bracket.
    ///
    /// Sprint 28 Phase 1 primitive. Enum values are the millisecond counts
    /// themselves — casting to int gives the tolerance directly, no translation
    /// table required. Adding a future step is a one-line enum change; nothing
    /// else needs to touch.
    /// </summary>
    public enum DoubleTapTolerance
    {
        /// <summary>250 ms — for fast typists; close to the pre-Sprint-28 hardcoded behavior.</summary>
        Quick = 250,

        /// <summary>500 ms — the recommended default. Balanced feel for most users.</summary>
        Normal = 500,

        /// <summary>750 ms — for deliberate input cadence.</summary>
        Relaxed = 750,

        /// <summary>1000 ms — slowest; suits users who verify speech between each press.</summary>
        Leisurely = 1000
    }

    /// <summary>
    /// Which way the Slice field's Up and Down arrows walk the slice letters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Noticed by Noel 2026-08-11 (#318):</b> the slice list navigated
    /// bottom-to-top — you arrowed UP to go A, B, C — so Down-arrow walked
    /// backwards through the alphabet. Reading order says Down should do that,
    /// and a list is the one control where a screen-reader operator has no
    /// visual layout to correct the mismatch against.
    /// </para>
    /// <para>
    /// <b>His ruling the same day was to make it a setting, not to flip it.</b>
    /// We pick the better default; the operator keeps the choice. Anyone who
    /// has spent months with the old direction in their fingers keeps it with
    /// one radio button, and nobody's muscle memory is confiscated by our
    /// opinion about reading order.
    /// </para>
    /// </remarks>
    public enum SliceArrowOrder
    {
        /// <summary>Down-arrow moves A to B to C. Reading order, and the
        /// default.</summary>
        TopToBottom = 0,

        /// <summary>Up-arrow moves A to B to C — how the field behaved before
        /// this setting existed.</summary>
        BottomToTop = 1
    }

    /// <summary>
    /// Per-operator accessibility preferences — input timing, UI pacing, and other
    /// accessibility-tuning knobs that may accumulate in future sprints (Sprint 28 ships
    /// this with just DoubleTapTolerance; future phases may add hold-to-repeat delay,
    /// announcement timing defaults, etc.).
    ///
    /// XML-serialized per-operator file at
    /// <c>{BaseConfigDir}\{opName}_accessibilityConfig.xml</c>, the settings root.
    /// Loaded at launch, at the first connect and when the operator changes
    /// (<see cref="Load"/>, <see cref="FollowOperator"/>), and saved to
    /// that same file when the Settings dialog commits (<see cref="SaveChoices"/>).
    /// Until the #517 repair the dialog saved it under <c>Radios</c> instead, where
    /// nothing read it; see <see cref="HoldsEverySetting"/>.
    ///
    /// A static <see cref="Current"/> reference exposes the active config to any
    /// consumer that doesn't want to thread the object through its call chain — useful
    /// for UI-layer key handlers that read DoubleTapToleranceMs at unpredictable times.
    /// </summary>
    [XmlRoot("AccessibilityConfig")]
    public class AccessibilityConfig
    {
        /// <summary>
        /// The active double-tap tolerance. Default <see cref="DoubleTapTolerance.Normal"/>
        /// (500 ms) — chosen as a reasonable midpoint for screen-reader users whose input
        /// cadence is often slower than sighted typists but who also don't want unwieldy
        /// delays.
        /// </summary>
        public DoubleTapTolerance DoubleTapTolerance { get; set; } = DoubleTapTolerance.Normal;

        /// <summary>
        /// The tolerance expressed as an integer millisecond count. Convenience accessor
        /// so callers that don't need the enum value (most of them) don't have to cast.
        /// Not serialized — derived from <see cref="DoubleTapTolerance"/>.
        /// </summary>
        [XmlIgnore]
        public int DoubleTapToleranceMs => (int)DoubleTapTolerance;

        /// <summary>
        /// Which way the Slice field's arrows walk the letters. Default
        /// <see cref="Radios.SliceArrowOrder.TopToBottom"/> — Down goes A, B, C.
        /// </summary>
        public SliceArrowOrder SliceArrowOrder { get; set; } = SliceArrowOrder.TopToBottom;

        /// <summary>
        /// The step Down-arrow should pass to the slice cycler: +1 toward the
        /// next letter, -1 toward the previous one. Up-arrow takes the
        /// negation.
        /// </summary>
        /// <remarks>
        /// Read by the Slice field's Up and Down while they switch slices
        /// (<see cref="SliceFieldArrowsTune"/> unchecked). The Slice operations
        /// field never read it: its arrows were the slice's volume before the
        /// 2026-11-09 batch and tune or change volume now, and never walked
        /// the slice list. Derived, so it is never serialised and can never
        /// disagree with <see cref="SliceArrowOrder"/>.
        /// </remarks>
        [XmlIgnore]
        public int SliceStepForDownArrow =>
            SliceArrowOrder == SliceArrowOrder.TopToBottom ? 1 : -1;

        /// <summary>
        /// Whether plain Up and Down on Home's Slice field tune by the coarse
        /// step. Default <c>false</c>: they switch slices, in the direction
        /// <see cref="SliceArrowOrder"/> gives (#517, ruled by Noel 2026-11-09
        /// 20:50 and 21:01).
        /// </summary>
        /// <remarks>
        /// The default lives HERE and nowhere else (Noel, 2026-11-09 20:59:
        /// flipping it later must stay a one-place change). A missing element
        /// in an older config file leaves this at the initialiser. Ctrl and
        /// Shift with the arrows tune from the field either way.
        /// </remarks>
        public bool SliceFieldArrowsTune { get; set; } = false;

        /// <summary>
        /// Whether plain Up and Down on Home's Slice operations field tune by
        /// the coarse step. Default <c>true</c>: they tune; unchecked they
        /// change the slice's volume, as before the 2026-11-09 batch (#517,
        /// ruled by Noel 2026-11-09 20:50 and 21:01).
        /// </summary>
        /// <remarks>
        /// The default lives HERE and nowhere else, as above. Page Up and Page
        /// Down pan on the field whatever this says.
        /// </remarks>
        public bool SliceOpsFieldArrowsTune { get; set; } = true;

        /// <summary>
        /// Whether the #529 focus watchdog may take the keyboard back when
        /// another program takes the foreground from an idle operator while a
        /// dialog of ours is up. Default <c>true</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why this exists (Sprint 45 Track D2).</b> The note that went to
        /// Don on 2026-09-05 says, in Noel's own words: <i>"if a JJ Flexible
        /// window ever comes to the front while you are doing something else,
        /// and you did not ask it to, that is this. Tell me when and what you
        /// were doing and I will switch it off."</i> That promise was made to a
        /// tester before there was anything to switch, so keeping it needed a
        /// rebuild. A promise a tester can act on has to be reachable from the
        /// tester's own keyboard.
        /// </para>
        /// <para>
        /// <b>Default ON, and that is not a formality.</b> The watchdog's
        /// negative case was measured at the radio on 2026-09-05 — the desktop
        /// held the foreground over one of our modals for 115 seconds with the
        /// operator genuinely absent, and nothing reclaimed. This is the switch
        /// for when it misbehaves, not a retreat from it.
        /// </para>
        /// <para>
        /// <b>It governs the RECLAIM only.</b> The two black-hole repairs — no
        /// window anywhere taking input, and a foreground of our own whose
        /// thread has no focus window — are untouched by it, and deliberately
        /// so. Neither of those can be "you were doing something else": in the
        /// first nothing on the desktop has the keyboard, and in the second we
        /// already hold it. Switching those off would take away the 2026-08-30
        /// rescue while answering a complaint that is not about them.
        /// </para>
        /// <para>
        /// <b>Off is not silent.</b> With this false the sentinel still ticks,
        /// still reaches the same verdict through the same six gates, and
        /// traces what it would have done — so a later diagnostic bundle can
        /// say "this would have rescued you and you had it switched off"
        /// rather than showing an unexplained outage with no watchdog line in
        /// it at all. See <see cref="StrandedFocusSentinel.WithOperatorPreference"/>.
        /// </para>
        /// <para>
        /// A missing element in an older config file leaves this at the
        /// property initialiser, so every operator who upgrades keeps the
        /// watchdog on without touching anything. Pinned by
        /// <c>Radios.Tests.FocusWatchdogSettingTests</c>.
        /// </para>
        /// </remarks>
        public bool ReclaimStolenForeground { get; set; } = true;

        /// <summary>
        /// The modes the mode cycle visits, in the operator's order (see
        /// <see cref="ModeCycle"/>). Null means never set, which is the cycle
        /// as shipped (<see cref="ModeCycle.DefaultCycle"/>); an empty array is
        /// a cycle the operator emptied.
        /// </summary>
        /// <remarks>
        /// Here rather than in the per-radio audio config because it describes
        /// the operator's keys, like <see cref="SliceArrowOrder"/>: this file
        /// is read from the settings root once a session (at the first connect)
        /// and no radio's copy replaces it. Settings writes the cycle into that
        /// same file through <see cref="ChooseModeCycle"/>, so it survives a
        /// restart and a connect. An array, not a list: the XML reader
        /// appends to a list that already holds the defaults.
        /// </remarks>
        [System.Xml.Serialization.XmlArray("ModeCycle")]
        [System.Xml.Serialization.XmlArrayItem("Mode")]
        public string[]? ModeCycleModes { get; set; }

        /// <summary>
        /// True in a settings-root file written by <see cref="SaveChoices"/>
        /// or brought home by <see cref="Load"/>: every field in it is the
        /// operator's. False in any older file, whose fields other than the
        /// mode cycle were never written by an operator's choice.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why it exists (#517 repair one, 2026-11-09).</b> From Sprint 28
        /// until this repair the Settings dialog saved this object into the
        /// per-radio folder it is handed (<c>Radios</c> under the settings
        /// root), while every load read the settings root. So the
        /// operator's double-tap tolerance, slice arrow order, focus watchdog
        /// switch and slice-field arrow boxes all lived in
        /// <c>Radios\&lt;operator&gt;_accessibilityConfig.xml</c>, and the
        /// root file held only the mode cycle (written there by
        /// <see cref="ChooseModeCycle"/> since #263) over defaults. An
        /// operator's live tree on 2026-11-09 held ONLY the Radios copy.
        /// </para>
        /// <para>
        /// A root file without this mark is therefore merged with that copy
        /// once (see <see cref="Load"/>) and written back with the mark, after
        /// which the Radios copy is never read again. An older build that
        /// rewrites the root file drops the element it does not know, so going
        /// back to one and forward again merges afresh, from whatever that
        /// build saved.
        /// </para>
        /// </remarks>
        public bool HoldsEverySetting { get; set; }

        /// <summary>
        /// The active (most recently loaded or saved) accessibility config. UI-layer
        /// consumers that don't take an AccessibilityConfig parameter (e.g., double-tap
        /// detectors in FreqOutHandlers or ScreenFieldsPanel) read this. Updated by
        /// <see cref="Load"/> and <see cref="Save"/>; starts at defaults until the first
        /// Load call.
        /// </summary>
        public static AccessibilityConfig Current { get; private set; } = new AccessibilityConfig();

        /// <summary>
        /// Read the operator's file from the settings root and make it
        /// <see cref="Current"/>. The application's ONE load path: at launch
        /// (globals.vb InitializeApplication) and at the first connect
        /// (ApplicationEvents.vb), both with the settings root and
        /// <c>PersonalData.UniqueOpName</c>; and, through
        /// <see cref="FollowOperator"/>, when the current operator changes.
        /// </summary>
        /// <remarks>
        /// The folder and name are remembered, and every later write goes to
        /// that same file: <see cref="SaveChoices"/> for the Settings dialog,
        /// <see cref="ChooseModeCycle"/> for the mode cycle. A root file
        /// written before the #517 repair is first merged with the copy the
        /// Settings dialog used to save under <c>Radios</c> (see
        /// <see cref="HoldsEverySetting"/> and <see cref="ReadHome"/>).
        /// </remarks>
        public static AccessibilityConfig Load(string configDirectory, string operatorName)
        {
            var config = ReadHome(configDirectory, operatorName);

            // Remember where the operator's file was read from, so the
            // Settings dialog's save and the mode cycle write back to THIS
            // file, the one this Load reads at the next start.
            _homeDirectory = configDirectory;
            _homeOperator = operatorName;

            if (_pendingChoices != null)
            {
                // Settings was committed before this file was first read this
                // session. The operator's choices win over the file (they were
                // made after it was last written), and are written into it now,
                // so they survive this load, which is the first connect's, and
                // the next start. They carry any mode cycle chosen in the same
                // commit, so the held cycle below is spent with them.
                config = _pendingChoices;
                _pendingChoices = null;
                _modeCyclePending = false;
                _pendingModeCycle = null;
                WriteHome(configDirectory, operatorName, config);
            }
            else if (_modeCyclePending)
            {
                // A mode cycle chosen before this file was first read (Settings
                // opened before the first connect of the session) wins over the
                // file, and is written into it now, so it is neither lost here
                // nor at the next start.
                config.ModeCycleModes = _pendingModeCycle;
                _modeCyclePending = false;
                _pendingModeCycle = null;
                WriteModeCycle(configDirectory, operatorName, config.ModeCycleModes);
            }

            Current = config;
            return config;
        }

        /// <summary>
        /// The current operator changed (Radio, Operators, or an edit or
        /// removal that changes who is current): from now on
        /// <see cref="Current"/> is that operator's, read from that
        /// operator's file, and the Settings dialog's save and the mode cycle
        /// write that file. Nothing happens when the operator is the one whose
        /// file is already in use.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>#517 repair two (Codex, 2026-11-10).</b> <see cref="Load"/>
        /// remembers the operator it read, and only the launch and each
        /// connect called it, so between switching operators and the next
        /// connect <see cref="Current"/> was still the previous operator's,
        /// Settings showed the previous operator's choices, and
        /// <see cref="SaveChoices"/> wrote the new operator's choice into the
        /// previous operator's file, leaving the new one's unwritten. The
        /// application wires this to the operator change event
        /// (ApplicationEvents.vb), with or without a radio.
        /// </para>
        /// <para>
        /// <b>Nothing held is lost or misdirected.</b> Once a file has been
        /// read, every commit is written at once (<see cref="SaveChoices"/>,
        /// <see cref="ChooseModeCycle"/>), so the previous operator's choices
        /// are already in the previous operator's file when this runs. Choices
        /// are held only while no file has been read this session, which means
        /// they belong to no operator yet; <see cref="Load"/> gives them to the
        /// operator it reads, exactly as the first connect would have.
        /// </para>
        /// </remarks>
        public static AccessibilityConfig FollowOperator(string configDirectory, string operatorName)
        {
            if (_homeDirectory != null && _homeOperator != null
                && string.Equals(_homeDirectory, configDirectory, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_homeOperator, operatorName, StringComparison.OrdinalIgnoreCase))
            {
                return Current;
            }
            System.Diagnostics.Trace.WriteLine(
                $"AccessibilityConfig: the operator changed to {operatorName}; reading that operator's settings");
            return Load(configDirectory, operatorName);
        }

        /// <summary>The per-radio folder the Settings dialog used to save into, under the settings root.</summary>
        internal const string FormerSettingsFolder = "Radios";

        /// <summary>
        /// The operator's settings, from the settings-root file, merged ONCE
        /// with the copy the Settings dialog used to save under
        /// <see cref="FormerSettingsFolder"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A root file marked <see cref="HoldsEverySetting"/> wins
        /// outright</b>: everything in it was written by this repair's save
        /// path, and the Radios copy is stale from then on.
        /// </para>
        /// <para>
        /// <b>An unmarked root file or none:</b> every field except the mode
        /// cycle comes from the Radios copy when there is one, because until
        /// this repair that copy was the ONLY place the Settings dialog saved
        /// them, and the root file's values for them were defaults no operator
        /// chose. The mode cycle comes from the root file when there is one,
        /// because that is where <see cref="ChooseModeCycle"/> has written it
        /// since #263, including from a Settings commit with no radio, which
        /// never reached the Radios copy. The merged result is written to the
        /// root, marked, at once, so the next load and every later write
        /// start from it. Nothing is deleted: the Radios copy stays where it
        /// is, unread.
        /// </para>
        /// <para>
        /// The Radios copy is looked for under the same operator name. The
        /// dialog saved it under the operator file's base name, which is
        /// <c>UniqueOpName</c> plus <c>.xml</c> for every operator added or
        /// edited since the callsign naming (<c>PersonalData.opFileName</c>),
        /// and <c>MigrateConfigFiles</c> renames the Radios folder's legacy
        /// names to it at launch.
        /// </para>
        /// </remarks>
        private static AccessibilityConfig ReadHome(string configDirectory, string operatorName)
        {
            string rootPath = GetFilePath(configDirectory, operatorName);
            var root = ReadFile(rootPath);
            if (root != null && root.HoldsEverySetting) return root;

            var former = ReadFile(GetFilePath(Path.Combine(configDirectory, FormerSettingsFolder), operatorName));
            if (root == null && former == null) return CreateDefault();

            var merged = former ?? root!;
            if (former != null && root != null) merged.ModeCycleModes = root.ModeCycleModes;
            if (former != null)
                System.Diagnostics.Trace.WriteLine(
                    $"AccessibilityConfig: {operatorName}'s settings brought home from the {FormerSettingsFolder} folder");
            WriteHome(configDirectory, operatorName, merged);
            return merged;
        }

        /// <summary>
        /// The Settings dialog's save (OK or Apply): write <see cref="Current"/>,
        /// whole, to the file <see cref="Load"/> reads.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>#517 repair one (Codex and the second check, 2026-11-09).</b>
        /// The dialog used to call <see cref="Save"/> with the per-radio
        /// folder it is handed, and the application loads from the settings
        /// root, so every choice on the Accessibility tab was live until the
        /// next start (or, made before the first connect, until that connect)
        /// and then gone. This writes where the load reads, with or without a
        /// radio.
        /// </para>
        /// <para>
        /// Before that file has been read this session there is nowhere to
        /// write: the choices are held, and the load applies them over the
        /// file and writes them, as <see cref="ChooseModeCycle"/> does for the
        /// cycle.
        /// </para>
        /// </remarks>
        public static void SaveChoices()
        {
            if (_homeDirectory != null && _homeOperator != null)
            {
                _pendingChoices = null;
                WriteHome(_homeDirectory, _homeOperator, Current);
            }
            else
            {
                _pendingChoices = Current;
            }
        }

        /// <summary>Write a whole config to the operator's settings-root file, marked as holding every setting.</summary>
        private static void WriteHome(string configDirectory, string operatorName, AccessibilityConfig config)
        {
            config.HoldsEverySetting = true;
            config.Save(configDirectory, operatorName);
        }

        /// <summary>Read one operator file, or null when it is missing or unreadable.</summary>
        private static AccessibilityConfig? ReadFile(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            try
            {
                using var fs = File.OpenRead(filePath);
                var serializer = new XmlSerializer(typeof(AccessibilityConfig));
                var config = (AccessibilityConfig?)serializer.Deserialize(fs);
                config?.Validate();
                return config;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"AccessibilityConfig.Load failed: {ex.Message}");
                return null;
            }
        }

        private static string? _homeDirectory;
        private static string? _homeOperator;
        private static bool _modeCyclePending;
        private static string[]? _pendingModeCycle;
        private static AccessibilityConfig? _pendingChoices;

        /// <summary>
        /// The operator chose a mode cycle (Settings, OK or Apply): make it
        /// live and keep it in the file <see cref="Load"/> reads at start-up.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Written for #263, when the Settings dialog saved this object under
        /// the per-radio folder it is handed (<c>Radios</c> under the settings
        /// root) while the application loaded it from the settings root, so a
        /// setting that relied on that save was live until the next start and
        /// then gone. This writes the one field into the file the load reads,
        /// leaving every other field there as it was. Since the #517 repair the
        /// dialog's whole save (<see cref="SaveChoices"/>) goes to that same
        /// file, just after this, so the two agree.
        /// </para>
        /// <para>
        /// Before that file has been read this session there is no file to
        /// write to: the choice is held, and the load applies it over the file
        /// and writes it, so it survives the connect that triggers the load.
        /// </para>
        /// </remarks>
        public static void ChooseModeCycle(string[]? modes)
        {
            Current.ModeCycleModes = modes;
            if (_homeDirectory != null && _homeOperator != null)
            {
                WriteModeCycle(_homeDirectory, _homeOperator, modes);
            }
            else
            {
                _modeCyclePending = true;
                _pendingModeCycle = modes;
            }
        }

        /// <summary>Put one field into an operator file without disturbing the rest.</summary>
        private static void WriteModeCycle(string configDirectory, string operatorName, string[]? modes)
        {
            string path = GetFilePath(configDirectory, operatorName);
            var onDisk = ReadFile(path) ?? CreateDefault();
            onDisk.ModeCycleModes = modes;
            try
            {
                Directory.CreateDirectory(configDirectory);
                AtomicFile.Write(path, fs => new XmlSerializer(typeof(AccessibilityConfig)).Serialize(fs, onDisk));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"AccessibilityConfig: mode cycle not saved: {ex.Message}");
            }
        }

        /// <summary>Tests only: forget where the operator file was read from and any held choice.</summary>
        internal static void ForgetHomeForTests()
        {
            _homeDirectory = null;
            _homeOperator = null;
            _modeCyclePending = false;
            _pendingModeCycle = null;
            _pendingChoices = null;
        }

        /// <summary>
        /// Write this object to <paramref name="configDirectory"/>. The
        /// application's own saves go through <see cref="SaveChoices"/> and
        /// <see cref="ChooseModeCycle"/>, which choose the folder the load
        /// reads; nothing in the application calls this with a folder of its
        /// own any more.
        /// </summary>
        public void Save(string configDirectory, string operatorName)
        {
            Validate();
            var filePath = GetFilePath(configDirectory, operatorName);

            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var serializer = new XmlSerializer(typeof(AccessibilityConfig));
                AtomicFile.Write(filePath, fs => serializer.Serialize(fs, this));

                Current = this;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"AccessibilityConfig.Save failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Validate and clamp out-of-range values. Called before serialization and after
        /// deserialization to defend against manually-edited config files.
        /// </summary>
        public void Validate()
        {
            if (!Enum.IsDefined(typeof(DoubleTapTolerance), DoubleTapTolerance))
                DoubleTapTolerance = DoubleTapTolerance.Normal;

            if (!Enum.IsDefined(typeof(SliceArrowOrder), SliceArrowOrder))
                SliceArrowOrder = SliceArrowOrder.TopToBottom;
        }

        private static AccessibilityConfig CreateDefault()
        {
            var config = new AccessibilityConfig();
            config.Validate();
            return config;
        }

        private static string GetFilePath(string configDirectory, string operatorName)
        {
            return Path.Combine(configDirectory, $"{operatorName}_accessibilityConfig.xml");
        }
    }
}
