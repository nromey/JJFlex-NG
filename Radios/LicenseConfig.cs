using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using HamBands;

namespace Radios
{
    /// <summary>
    /// Per-operator license configuration for band boundary notifications and TX lockout.
    /// Stored as {OperatorName}_licenseConfig.xml in the config directory.
    /// </summary>
    [XmlRoot("LicenseConfig")]
    public class LicenseConfig
    {
        /// <summary>
        /// Operator's license class. Defaults to Extra (no restrictions).
        /// </summary>
        public Bands.Licenses LicenseClass { get; set; } = Bands.Licenses.extra;

        /// <summary>
        /// When true, speak announcements when crossing band or license boundaries.
        /// </summary>
        public bool BoundaryNotifications { get; set; } = true;

        /// <summary>
        /// When true, block PTT when signal extends outside licensed band segment.
        /// </summary>
        public bool TxLockout { get; set; } = false;

        /// <summary>
        /// Operator's country code for 60m channelization and regulatory enforcement.
        /// Default "US" for FCC Part 97.303(h) rules.
        /// </summary>
        public string Country { get; set; } = "US";

        /// <summary>
        /// When true (the default), the 60 m rules are enforced at transmit time
        /// (#1161, ruled by Noel 2026-11-26: on by default for a US operator, "or for
        /// any other 60 m countries that we find require limitations"): on 60 m a
        /// transmission must be on a channel at the exact dial for its mode, or in
        /// the segment, with the whole emission inside and no wider than 2.8 kHz
        /// (<see cref="SixtyMeterChannels.JudgeTransmission"/>). Independent of
        /// <see cref="TxLockout"/>. A country with no 60 m table is never judged.
        /// The power limits are NOT enforced yet (#1311). When false, no 60 m rule
        /// is applied at transmit time.
        /// </summary>
        public bool EnforceTxRules { get; set; } = true;

        /// <summary>
        /// The operator's license, from the ONE file every reader and writer
        /// uses (#1072 repair, Codex's B2): <c>{operator}_licenseConfig.xml</c>
        /// in <paramref name="configDirectory"/>, which is the per-radio
        /// settings folder (<c>OpenParms.ConfigDirectory</c>, the settings root's
        /// <c>Radios</c> folder) that Settings has always saved to.
        /// </summary>
        /// <remarks>
        /// Until this repair Home loaded, tested for and wrote first-run
        /// defaults to a copy at the settings ROOT, while Settings saved to the
        /// Radios folder, so a license class or transmit lockout chosen in
        /// Settings was read back as the root copy's first-run defaults after
        /// an operator switch or a restart, and the next Settings save wrote
        /// those over the choice. The root copy was only ever written as
        /// first-run defaults (Settings never wrote there), so where both exist
        /// the Radios copy is the operator's choice. A root copy alone is
        /// carried: read, never moved or deleted, until Settings first saves.
        /// <paramref name="firstRun"/> is true only when neither file exists,
        /// so first-run defaults can never be written over a saved choice.
        /// </remarks>
        public static LicenseConfig LoadOrCarry(string configDirectory, string legacyDirectory,
            string operatorName, out bool firstRun)
        {
            firstRun = false;
            if (Exists(configDirectory, operatorName)) return Load(configDirectory, operatorName);
            if (!string.IsNullOrEmpty(legacyDirectory) && Exists(legacyDirectory, operatorName))
            {
                System.Diagnostics.Trace.WriteLine(
                    $"LicenseConfig: {operatorName}'s license read from the settings root's older copy; Settings' next save puts it in {configDirectory}");
                return Load(legacyDirectory, operatorName);
            }
            firstRun = true;
            return new LicenseConfig();
        }

        // ---------------- one licence class per operator (#1239) ----------------

        /// <summary>
        /// The five classes the operator form and Settings offer, in the order
        /// both show them: the three still issued (Extra, General, Technician),
        /// then the two held but no longer issued (Advanced, Novice).
        /// </summary>
        /// <remarks>
        /// <para><b>Ruled by Noel 2026-11-26</b> (#1309, question 5 of the
        /// 41-question sheet, option C): all five classes Part 97 recognises
        /// (97.9(a)) are offered, Novice and Advanced marked held but no longer
        /// issued (97.17(a)). The ORDER is the orchestrator's suggestion, not a
        /// ruling.</para>
        /// <para>Display order only: the licence file stores a class by its
        /// enum NAME and the operator record by its NUMBER, so
        /// <see cref="Bands.Licenses"/> itself is never reordered or renamed
        /// (the misspelt <c>technition</c> stays). <c>none</c> is not a class
        /// anyone holds and is not offered.</para>
        /// </remarks>
        public static readonly IReadOnlyList<Bands.Licenses> OfferedClasses = new[]
        {
            Bands.Licenses.extra, Bands.Licenses.general, Bands.Licenses.technition,
            Bands.Licenses.advanced, Bands.Licenses.novice,
        };

        /// <summary>
        /// The class an operator the form creates starts on (Noel,
        /// 2026-11-23: "I'd start on General"). Only a NEW operator: an
        /// existing one with no licence file still reads Extra, the
        /// deserialization default, so nothing on disk changes meaning.
        /// </summary>
        public const Bands.Licenses NewOperatorClass = Bands.Licenses.general;

        /// <summary>
        /// A stored class as one of the five offered: each of the five reads as
        /// itself. A stored <c>none</c> is not a class anyone holds and reads as
        /// Extra, which is what Settings has always shown for it; nothing stored
        /// is changed by reading it.
        /// </summary>
        /// <remarks>
        /// Until 2026-11-26 a held Novice read as Technician here, on the premise
        /// (#1239) that the band table gives the two the same privileges. Part 97
        /// does not: above 30 MHz a Novice has only 222 to 225 MHz and 1270 to
        /// 1295 MHz (97.301(e)), while a Technician has every 97.301(a) band from
        /// 6 m up. On HF the two are identical. So a Novice is shown, kept and
        /// judged as Novice (#1309).
        /// </remarks>
        public static Bands.Licenses ShownClass(Bands.Licenses stored) => stored switch
        {
            Bands.Licenses.novice or Bands.Licenses.technition or Bands.Licenses.general
                or Bands.Licenses.advanced or Bands.Licenses.extra => stored,
            _ => Bands.Licenses.extra,
        };

        /// <summary>
        /// The class the TX band lockout and the band notices judge for a stored
        /// class: the class Settings shows (<see cref="ShownClass"/>), so a stored
        /// <c>none</c>, which no division lists and so was refused on every band,
        /// is judged as the Extra that Settings shows for it.
        /// </summary>
        /// <remarks>The orchestrator's decision for junk box batch 31 (#1309's
        /// "shown, kept and judged"), now that an untouched Settings OK no longer
        /// rewrites the stored class.</remarks>
        public static Bands.Licenses JudgedClass(Bands.Licenses stored) => ShownClass(stored);

        /// <summary>The spoken label for one of the offered classes, Settings' own words.</summary>
        public static string ClassLabel(Bands.Licenses offered) => ShownClass(offered) switch
        {
            Bands.Licenses.advanced => Lexicon.Get("settings.license.class_advanced_held"),
            Bands.Licenses.novice => Lexicon.Get("settings.license.class_novice_held"),
            Bands.Licenses.general => Lexicon.Get("settings.license.class_general"),
            Bands.Licenses.technition => Lexicon.Get("settings.license.class_technician"),
            _ => Lexicon.Get("settings.license.class_extra"),
        };

        /// <summary>
        /// The class a list of <see cref="OfferedClasses"/> was moved to, or null
        /// when it was not moved: still on <paramref name="positionAtOpen"/>, or
        /// on no valid position. Settings' OK writes the class only when this is
        /// not null (#1309, folding in #1281), so an untouched License tab never
        /// turns a stored class into another.
        /// </summary>
        public static Bands.Licenses? ClassChosenInList(int position, int positionAtOpen)
        {
            if (position == positionAtOpen) return null;
            if (position < 0 || position >= OfferedClasses.Count) return null;
            return OfferedClasses[position];
        }

        /// <summary>
        /// One commit of a list of <see cref="OfferedClasses"/> into
        /// <paramref name="license"/> (Settings' OK or Apply): writes the class
        /// only when the list was moved off <paramref name="committedPosition"/>
        /// (<see cref="ClassChosenInList"/>), and returns the position the NEXT
        /// commit is judged against: this one when it wrote, else the old one.
        /// </summary>
        /// <remarks>
        /// Repair round 1 of junk box batch 31 (Codex's #1309 blocker): the
        /// baseline was the position at open, forever, so after an Apply a move
        /// back to the opening class wrote nothing and the class Apply wrote
        /// stayed, under a list showing the other one. An untouched stored
        /// <c>none</c> still stays untouched through any number of commits.
        /// </remarks>
        public static int CommitClassFromList(LicenseConfig license, int position, int committedPosition)
        {
            if (license == null) throw new ArgumentNullException(nameof(license));
            if (ClassChosenInList(position, committedPosition) is not { } chosen) return committedPosition;
            license.LicenseClass = chosen;
            return position;
        }

        /// <summary>What <see cref="SaveClass"/> did.</summary>
        public enum ClassSave
        {
            /// <summary>The licence file was written.</summary>
            Saved,
            /// <summary>Nothing changed, so nothing was written.</summary>
            Unchanged,
            /// <summary>No licence file to carry, and none was to be made.</summary>
            NothingToCarry,
            /// <summary>The licence file does not read; it is left exactly as it is.</summary>
            CorruptLeftInPlace,
            /// <summary>The write failed (traced).</summary>
            NotSaved,
        }

        /// <summary>
        /// Save an operator's licence class from the operator form (#1239):
        /// read the licence file the operator has now (as
        /// <see cref="LoadOrCarry"/> finds it, under
        /// <paramref name="fromOperator"/>), change ONLY the class, and save the
        /// rest as it was, under <paramref name="toOperator"/> in
        /// <paramref name="configDirectory"/>. The file also holds the
        /// transmit lockout, boundary notices, country and the 60 m rules, so
        /// a fresh file here would switch the lockout off.
        /// </summary>
        /// <param name="licenseClass">The class to save; null keeps the stored one (a rename carries the file as it is).</param>
        /// <param name="createIfMissing">With no file to read, make one from the defaults; otherwise write nothing.</param>
        /// <remarks>A file that does not read, at either name, is left in place and never overwritten with defaults.</remarks>
        public static ClassSave SaveClass(string configDirectory, string legacyDirectory,
            string fromOperator, string toOperator, Bands.Licenses? licenseClass, bool createIfMissing)
        {
            string? source = Exists(configDirectory, fromOperator) ? GetFilePath(configDirectory, fromOperator)
                : !string.IsNullOrEmpty(legacyDirectory) && Exists(legacyDirectory, fromOperator) ? GetFilePath(legacyDirectory, fromOperator)
                : null;
            LicenseConfig config;
            if (source == null)
            {
                if (!createIfMissing) return ClassSave.NothingToCarry;
                config = new LicenseConfig();
            }
            else if (!TryRead(source, out config))
            {
                System.Diagnostics.Trace.WriteLine($"LicenseConfig: {source} does not read, so the operator form's class was not saved and the file is left as it is");
                return ClassSave.CorruptLeftInPlace;
            }
            string target = GetFilePath(configDirectory, toOperator);
            if (!string.Equals(target, source, StringComparison.OrdinalIgnoreCase) && File.Exists(target)
                && !TryRead(target, out _))
            {
                System.Diagnostics.Trace.WriteLine($"LicenseConfig: {target} does not read, so it is left as it is");
                return ClassSave.CorruptLeftInPlace;
            }
            bool sameFile = string.Equals(target, source, StringComparison.OrdinalIgnoreCase);
            if (sameFile && (!licenseClass.HasValue || config.LicenseClass == licenseClass.Value))
                return ClassSave.Unchanged;
            if (licenseClass.HasValue) config.LicenseClass = licenseClass.Value;
            return config.TrySave(configDirectory, toOperator) ? ClassSave.Saved : ClassSave.NotSaved;
        }

        /// <summary>Read one licence file; false when it does not read.</summary>
        private static bool TryRead(string filePath, out LicenseConfig config)
        {
            try
            {
                using var fs = File.OpenRead(filePath);
                var read = (LicenseConfig?)new XmlSerializer(typeof(LicenseConfig)).Deserialize(fs);
                config = read ?? new LicenseConfig();
                return read != null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"LicenseConfig: {filePath} could not be read: {ex.Message}");
                config = new LicenseConfig();
                return false;
            }
        }

        public static LicenseConfig Load(string configDirectory, string operatorName)
        {
            var filePath = GetFilePath(configDirectory, operatorName);

            if (!File.Exists(filePath))
                return new LicenseConfig();

            try
            {
                using var fs = File.OpenRead(filePath);
                var serializer = new XmlSerializer(typeof(LicenseConfig));
                var config = (LicenseConfig?)serializer.Deserialize(fs);
                return config ?? new LicenseConfig();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"LicenseConfig.Load failed: {ex.Message}");
                return new LicenseConfig();
            }
        }

        public void Save(string configDirectory, string operatorName) => TrySave(configDirectory, operatorName);

        /// <summary><see cref="Save"/>, saying whether the file was written.</summary>
        public bool TrySave(string configDirectory, string operatorName)
        {
            var filePath = GetFilePath(configDirectory, operatorName);

            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var serializer = new XmlSerializer(typeof(LicenseConfig));
                AtomicFile.Write(filePath, fs => serializer.Serialize(fs, this));
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"LicenseConfig.Save failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Check if a license config file exists for this operator (i.e., not a first run).
        /// </summary>
        public static bool Exists(string configDirectory, string operatorName)
        {
            return File.Exists(GetFilePath(configDirectory, operatorName));
        }

        private static string GetFilePath(string configDirectory, string operatorName)
        {
            return Path.Combine(configDirectory, $"{operatorName}_licenseConfig.xml");
        }
    }
}
